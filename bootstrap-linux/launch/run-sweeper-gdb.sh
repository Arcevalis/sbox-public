#!/bin/bash
# run-sbox-gdb.sh, but pointed at a project instead of the launcher menu.
#
# sbox-dev with no -project doesn't run the editor at all: it re-execs sbox-launcher
# as a SEPARATE process and returns immediately (engine/Launcher/SboxDev/Launcher.cs).
# gdb then traces a process that has already exited, and the env it set never reaches
# the editor you end up looking at. Passing -project keeps everything in the one
# process gdb owns.
#
# Modes (SBOX_MODE, or --mode):
#
#   editor      sbox-dev -project <sbproj>          (default)
#               The full editor on the project. Play mode runs in-process, so the
#               game itself is still under this gdb.
#
#   server      sbox-server +game <sbproj>          headless, no window
#               The only non-editor path that can load a LOCAL project: the "game"
#               concommand compiles a .sbproj only when Application.IsDedicatedServer
#               (engine/Sandbox.GameInstance/GameInstanceDll.cs, StartGame).
#
#   client      sbox -rungame <ident>               needs a PUBLISHED package
#               The client resolves the ident through the backend - "local.sweeper"
#               is not a thing it can fetch. Here for completeness.
#
#   standalone  sbox-standalone                     needs an exported build
#               Reads game/assets/standalone.manifest.json, which only exists after
#               Editor -> Publish -> Standalone. Errors out if it isn't there.
#
# Everything else - HarfBuzz preload, LD_LIBRARY_PATH, the present-stall breakpoints
# and their env knobs (SBOX_GDB_BT, SBOX_GDB_MAX_DUMPS, SBOX_GDB_TRACE_MSG, ...) - is
# exactly run-sbox-gdb.sh's; see the header there.
#
# Usage:
#   bootstrap-linux/launch/run-sweeper-gdb.sh                          editor on game/samples/sweeper
#   bootstrap-linux/launch/run-sweeper-gdb.sh /path/to/.sbproj         some other project (dir works too)
#   bootstrap-linux/launch/run-sweeper-gdb.sh --mode server            headless dedicated server on sweeper
#   bootstrap-linux/launch/run-sweeper-gdb.sh -- -someflag             pass extra args to the exe
#   bootstrap-linux/launch/run-sweeper-gdb.sh --dry-run                print the setup and launch nothing
#
# Env overrides:
#   SBOX_MODE=server            same as --mode
#   SBOX_PROJECT=/path/.sbproj  same as the positional argument
#   SBOX_GAME_IDENT=org.ident   package ident for client mode (default local.sweeper)
#   SBOX_INPUT_DEBUG=1          managed input diagnostics - just inherited by the
#                               inferior, same as any other var you export first
set -euo pipefail

# This lives in bootstrap-linux/launch/, so the repo root is two levels up.
LAUNCH_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$LAUNCH_DIR/../.." && pwd)"
GAME_DIR="$ROOT/game"
GDB_DIR="$ROOT/bootstrap-linux/gdb"
LOG_DIR="$ROOT/bootstrap-linux/logs"

MODE="${SBOX_MODE:-editor}"
PROJECT="${SBOX_PROJECT:-$ROOT/game/samples/sweeper/.sbproj}"
IDENT="${SBOX_GAME_IDENT:-local.sweeper}"
EXTRA=()
DRY=0

while [ $# -gt 0 ]; do
	case "$1" in
		--dry-run) DRY=1; shift ;;
		--mode) MODE="${2:?--mode needs a value}"; shift 2 ;;
		--mode=*) MODE="${1#--mode=}"; shift ;;
		--) shift; EXTRA=("$@"); break ;;
		*) PROJECT="$1"; shift ;;
	esac
done

# -project takes the .sbproj file. Accept a directory too and find it.
if [ -d "$PROJECT" ]; then PROJECT="$PROJECT/.sbproj"; fi

case "$MODE" in
	editor)
		EXE_NAME="sbox-dev"
		if [ ! -f "$PROJECT" ]; then
			echo "error: project file not found: $PROJECT" >&2
			exit 1
		fi
		PROJECT="$(cd "$(dirname "$PROJECT")" && pwd)/$(basename "$PROJECT")"
		ARGS=( -project "$PROJECT" )
		TARGET="$PROJECT"
		;;
	server)
		EXE_NAME="sbox-server"
		if [ ! -f "$PROJECT" ]; then
			echo "error: project file not found: $PROJECT" >&2
			exit 1
		fi
		PROJECT="$(cd "$(dirname "$PROJECT")" && pwd)/$(basename "$PROJECT")"
		ARGS=( +game "$PROJECT" )
		TARGET="$PROJECT"
		;;
	client)
		EXE_NAME="sbox"
		ARGS=( -rungame "$IDENT" )
		TARGET="$IDENT"
		echo "note: the client fetches packages from the backend - a local project"
		echo "      won't resolve here. Use --mode editor or --mode server for that."
		;;
	standalone)
		EXE_NAME="sbox-standalone"
		MANIFEST="$GAME_DIR/assets/standalone.manifest.json"
		if [ ! -f "$MANIFEST" ]; then
			echo "error: no standalone build at $MANIFEST" >&2
			echo "       export one from the editor first (Publish -> Standalone)," >&2
			echo "       or use --mode editor / --mode server" >&2
			exit 1
		fi
		ARGS=()
		TARGET="$MANIFEST"
		;;
	*)
		echo "error: unknown mode '$MODE' (editor, server, client, standalone)" >&2
		exit 1
		;;
esac

if ! command -v gdb >/dev/null 2>&1; then
	echo "error: gdb not found on PATH" >&2
	echo "       sudo apt install gdb" >&2
	exit 1
fi

for f in "$GDB_DIR/present-trace.gdb" "$GDB_DIR/present-trace.py"; do
	if [ ! -f "$f" ]; then
		echo "error: missing $f" >&2
		exit 1
	fi
done

# Take LD_PRELOAD / LD_LIBRARY_PATH from run-editor.sh rather than duplicating the
# HarfBuzz reasoning here - it prints exactly the env it would have exec'd with.
ENV_DUMP="$( SBOX_EXE="$EXE_NAME" "$LAUNCH_DIR/run-editor.sh" --print-env )"
export LD_PRELOAD="$( sed -n 's/^LD_PRELOAD=//p' <<<"$ENV_DUMP" )"
export LD_LIBRARY_PATH="$( sed -n 's/^LD_LIBRARY_PATH=//p' <<<"$ENV_DUMP" )"
EXE="$( sed -n 's/^exec //p' <<<"$ENV_DUMP" )"

mkdir -p "$LOG_DIR"
STAMP="$( date +%Y%m%d-%H%M%S )"
LOG="$LOG_DIR/present-trace-$EXE_NAME-$STAMP.log"

echo "mode         $MODE"
echo "exe          $EXE"
echo "target       $TARGET"
echo "trace log    $LOG"
echo "backtrace    ${SBOX_GDB_BT:-bt full}"
echo "dump cap     ${SBOX_GDB_MAX_DUMPS:-20}"
echo
echo "  tail -f '$LOG'"
echo "  grep -n 'present-trace' '$LOG'"
echo

if [ "$DRY" = "1" ]; then
	echo "LD_PRELOAD=$LD_PRELOAD"
	echo "LD_LIBRARY_PATH=$LD_LIBRARY_PATH"
	echo "would run: gdb -q -x present-trace.gdb -x present-trace.py --args $EXE ${ARGS[*]-} ${EXTRA[*]-}"
	exit 0
fi

# The engine resolves content paths relative to the working directory.
cd "$GAME_DIR"

# -batch would detach at the first error; -q plus an explicit "run"/"quit" keeps the
# session alive across the whole game run and still exits cleanly when it closes.
exec gdb -q \
	-iex "set logging file $LOG" \
	-iex "set logging overwrite on" \
	-iex "set logging enabled on" \
	-x "$GDB_DIR/present-trace.gdb" \
	-x "$GDB_DIR/present-trace.py" \
	-ex "run" \
	-ex "quit" \
	--args "$EXE" ${ARGS+"${ARGS[@]}"} ${EXTRA+"${EXTRA[@]}"}
