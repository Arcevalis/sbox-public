#!/bin/bash
# Run ./sbox under gdb and auto-capture a full backtrace whenever the Vulkan
# swapchain reports a present stall.
#
# The target is this, from librendersystemvulkan.so:
#
#   CSwapChainBase::QueuePresentAndWait() looped for %d iterations without a
#   present event.
#
# The swapchain allows one outstanding present (m_nMaxOutstanding = 1 from the
# ctor) and waits on the device's present-completed event in 10 ms slices, up to
# (outstanding + 20) times - so "21 iterations" is one un-retired present and
# ~210 ms of waiting. It then returns false and the frame is dropped. What we want
# to know is which thread failed to retire it, which means a snapshot of every
# thread's stack at the moment the warning is emitted.
#
# gdb breaks on tier0's Warning(), matches the format string, dumps all threads
# and resumes on its own - the game is never left halted, so one run collects
# every occurrence.
#
# Usage:
#   bootstrap-linux/launch/run-sbox-gdb.sh                    launch game/sbox
#   bootstrap-linux/launch/run-sbox-gdb.sh -somearg           pass args through to sbox
#   bootstrap-linux/launch/run-sbox-gdb.sh --dry-run          print the setup and exit
#
# Env overrides (see bootstrap-linux/gdb/present-trace.py for the full set):
#   SBOX_EXE=sbox-dev        trace the editor instead of the client
#   SBOX_GDB_BT="bt"         shallower per-thread backtrace than the default bt full
#   SBOX_GDB_MAX_DUMPS=0     no cap on the number of dumps (default 20)
#   SBOX_GDB_TRACE_MSG=1     also catch the "Hitch alert" / "frames ahead" lines,
#                            which go through Msg() rather than Warning()
#   SBOX_GDB_SKIP_THREADS=   regex of thread names to omit (default ^\.NET)
#   SBOX_GDB_STOP_SEGV=1     halt on SIGSEGV/SIGBUS instead of passing them through
set -euo pipefail

# This lives in bootstrap-linux/launch/, so the repo root is two levels up.
LAUNCH_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$LAUNCH_DIR/../.." && pwd)"
GAME_DIR="$ROOT/game"
GDB_DIR="$ROOT/bootstrap-linux/gdb"
LOG_DIR="$ROOT/bootstrap-linux/logs"

EXE_NAME="${SBOX_EXE:-sbox}"

DRY=0
if [ "${1:-}" = "--dry-run" ]; then DRY=1; shift; fi

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

echo "exe          $EXE"
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
	echo "would run: gdb -q -x present-trace.gdb -x present-trace.py --args $EXE ${*-}"
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
	--args "$EXE" ${@+"$@"}
