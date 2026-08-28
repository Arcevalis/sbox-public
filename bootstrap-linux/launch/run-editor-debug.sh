#!/bin/bash
# Launch the editor with the Linux input diagnostics turned on.
#
# Wraps run-editor.sh (which handles the HarfBuzz preload and LD_LIBRARY_PATH) and adds:
#
#   SBOX_INPUT_DEBUG=1   the managed diagnostics - [inputdbg] from the scene viewport,
#                        [routerdbg] from InputRouter, [gamemode] at the play handover.
#                        All land in game/logs/sbox-dev.log.
#
# SPY=1 additionally preloads libsdlspy.so, an LD_PRELOAD shim counting the SDL event
# plumbing (SDL_PushEvent / PollEvent, the relative-mouse + grab calls, window ids).
#
# The spy is OPT-IN because it is not free. It interposes SDL_PushEvent and
# SDL_SetWindowRelativeMouseMode, and interposing on the render path has produced
# "The selected graphics queue does not support presenting a swapchain image" plus a fatal
# Wayland disconnect - see the gotchas in bootstrap-linux/linux-input.md. Always re-verify a
# finding against a no-spy run before believing it.
#
# Reach for SPY=1 only for the things managed code structurally cannot see, all of them below
# the interop boundary:
#   - is the Qt->SDL bridge emitting at all (SDL_PushEvent), as opposed to events not being
#     delivered - InputRouter's own counters only see what survived the trip
#   - the actual X grab (SDL_SetWindowMouseGrab), as distinct from our request for relative mode
#   - which SDL window id events are stamped with
# For capture state, focus, delivery rates and the Qt mouse-move rate, the managed [routerdbg]
# and [gamemode] lines already cover it.
#
# It opens a project directly rather than going through the launcher: sbox-dev without
# -project re-execs sbox-launcher as a SEPARATE process and returns, so the editor you
# end up looking at is not the one you launched, and the env vars above never reach it.
#
# Usage:
#   bootstrap-linux/launch/run-editor-debug.sh                       open the sweeper sample
#   bootstrap-linux/launch/run-editor-debug.sh /path/to/.sbproj      open some other project
#   bootstrap-linux/launch/run-editor-debug.sh -- -someflag          pass extra flags through to sbox-dev
#   bootstrap-linux/launch/run-editor-debug.sh --dry-run             print the setup and exit, launching nothing
#   SPY=1 bootstrap-linux/launch/run-editor-debug.sh                 also preload the SDL spy (see above)
#
# Env overrides:
#   SPY=1              preload libsdlspy.so as well (default: off)
#   SPY_LOG=/path      where the SDL spy writes      (default: game/logs/sdlspy.log)
set -euo pipefail

# This lives in bootstrap-linux/launch/, so the repo root is two levels up.
LAUNCH_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$LAUNCH_DIR/../.." && pwd)"
PATCH_DIR="$ROOT/bootstrap-linux/patches"
SPY_SRC="$PATCH_DIR/sdlspy.c"
SPY_SO="$PATCH_DIR/libsdlspy.so"
SPY_LOG="${SPY_LOG:-$ROOT/game/logs/sdlspy.log}"
EDITOR_LOG="$ROOT/game/logs/sbox-dev.log"

PROJECT="$ROOT/game/samples/sweeper/.sbproj"
EXTRA=()
DRY=0
if [ "${1:-}" = "--dry-run" ]; then DRY=1; shift; fi
if [ $# -gt 0 ]; then
	if [ "$1" = "--" ]; then shift; EXTRA=("$@")
	else PROJECT="$1"; shift; EXTRA=("$@")
	fi
fi

# -project takes the .sbproj file. Accept a directory too and find it.
if [ -d "$PROJECT" ]; then PROJECT="$PROJECT/.sbproj"; fi
if [ ! -f "$PROJECT" ]; then
	echo "error: project file not found: $PROJECT" >&2
	exit 1
fi
PROJECT="$(cd "$(dirname "$PROJECT")" && pwd)/$(basename "$PROJECT")"

# Build the spy on demand - it is a single file and costs nothing.
if [ "${SPY:-0}" = "1" ]; then
	if [ ! -f "$SPY_SO" ] || [ "$SPY_SRC" -nt "$SPY_SO" ]; then
		echo "building $(basename "$SPY_SO")"
		gcc -shared -fPIC -O1 -o "$SPY_SO" "$SPY_SRC" -ldl
	fi
	# run-editor.sh PREPENDS the engine's HarfBuzz to LD_PRELOAD, so exporting the spy
	# here yields "libHarfBuzzSharp.so:libsdlspy.so" - both loaded, HarfBuzz still first,
	# which is the ordering that keeps the hb_* symbols on one implementation.
	export LD_PRELOAD="$SPY_SO${LD_PRELOAD:+:$LD_PRELOAD}"
	export SDLSPY_LOG="$SPY_LOG"
	: > "$SPY_LOG"
fi

export SBOX_INPUT_DEBUG=1

echo "project     $PROJECT"
echo "editor log  $EDITOR_LOG"
# Plain [ ... ] && echo would be the last command in the list, so a false test returns
# non-zero and set -e kills the script. Now that the spy is off by default, that matters.
if [ "${SPY:-0}" = "1" ]; then echo "sdl spy log $SPY_LOG"; fi
echo
echo "  tail -f '$EDITOR_LOG' | grep -E 'inputdbg|routerdbg|gamemode'"
if [ "${SPY:-0}" = "1" ]; then echo "  tail -f '$SPY_LOG'"; fi
echo

if [ "$DRY" = "1" ]; then
	echo "LD_PRELOAD=${LD_PRELOAD:-<unset>}"
	echo "SBOX_INPUT_DEBUG=$SBOX_INPUT_DEBUG"
	echo "would exec: $LAUNCH_DIR/run-editor.sh -project $PROJECT ${EXTRA[*]-}"
	exit 0
fi

exec "$LAUNCH_DIR/run-editor.sh" -project "$PROJECT" ${EXTRA+"${EXTRA[@]}"}
