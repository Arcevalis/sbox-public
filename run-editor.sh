#!/bin/bash
# Launch the s&box editor with the engine's own HarfBuzz preloaded.
#
# The engine ships libHarfBuzzSharp.so (SkiaSharp's statically-linked HarfBuzz) in
# game/bin/linuxsteamrt64, but the system's libharfbuzz.so.0 also ends up in the
# process - pulled in through Qt's xcb platform plugin, fontconfig and the GTK
# portal used for file dialogs. Both export the same unversioned hb_* names, so
# calls get spread across two copies: an hb_buffer allocated by one is handed to
# the other's free(), and glibc aborts with "free(): invalid pointer".
#
# LD_PRELOAD puts the engine's copy first in the global symbol scope, so every
# hb_* reference in the process - the engine's and the system libraries' - binds
# to that single implementation.
#
# Env overrides:
#   SBOX_EXE=sbox            launch a different binary from game/ (default sbox-dev)
#   SBOX_HARFBUZZ=/path.so   preload a different HarfBuzz (e.g. the system one, to
#                            force everything onto that copy instead)
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
GAME_DIR="$SCRIPT_DIR/game"
NATIVE_DIR="$GAME_DIR/bin/linuxsteamrt64"

EXE_NAME="${SBOX_EXE:-sbox-dev}"
EXE="$GAME_DIR/$EXE_NAME"
HARFBUZZ="${SBOX_HARFBUZZ:-$NATIVE_DIR/libHarfBuzzSharp.so}"

if [ ! -f "$HARFBUZZ" ]; then
	echo "error: HarfBuzz not found at $HARFBUZZ" >&2
	echo "       run ./bootstrap.sh first, or set SBOX_HARFBUZZ to the library to preload" >&2
	exit 1
fi

if [ ! -x "$EXE" ]; then
	echo "error: $EXE not found or not executable" >&2
	echo "       run ./bootstrap.sh first, or set SBOX_EXE to a binary in $GAME_DIR" >&2
	exit 1
fi

# Prepend rather than overwrite - keep anything the caller (or Steam) already set.
export LD_PRELOAD="$HARFBUZZ${LD_PRELOAD:+:$LD_PRELOAD}"
export LD_LIBRARY_PATH="$NATIVE_DIR${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}"

if [ "${1:-}" = "--print-env" ]; then
	echo "LD_PRELOAD=$LD_PRELOAD"
	echo "LD_LIBRARY_PATH=$LD_LIBRARY_PATH"
	echo "exec $EXE"
	exit 0
fi

# The engine resolves content paths relative to the working directory.
cd "$GAME_DIR"
exec "$EXE" "$@"
