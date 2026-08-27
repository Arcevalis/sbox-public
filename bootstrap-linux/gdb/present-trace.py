# Auto-backtrace on the Vulkan present stall, for use under gdb.
#
# CSwapChainBase::QueuePresentAndWait() waits on the device's present-completed
# event in 10 ms slices, up to (outstanding + 20) times. When it gives up it calls
# tier0's Warning() with:
#
#   "CSwapChainBase::QueuePresentAndWait() looped for %d iterations without a
#    present event."
#
# and returns false, which makes Present() report the frame as not presented. The
# question that matters is which thread is wedged and where, so this hooks Warning
# itself and dumps every thread's stack, then resumes the process on its own.
#
# The trigger is a plain gdb breakpoint with a condition and a command list, NOT a
# Python Breakpoint.stop() override. That distinction is load-bearing: stop() is
# evaluated before gdb has finished stopping the other threads, so a backtrace
# taken from inside it fails with "Selected thread is running". By the time
# breakpoint *commands* run, every thread is stopped and readable.
#
# Env vars:
#   SBOX_GDB_PATTERNS      ;-separated substrings to match against the format
#                          string (default: the present/swapchain set below)
#   SBOX_GDB_SKIP_THREADS  regex of thread names to omit (default: ^\.NET)
#   SBOX_GDB_BT            backtrace command per thread (default: "bt full")
#   SBOX_GDB_MAX_DUMPS     stop dumping after this many hits, keep counting
#                          (default 20, 0 = unlimited)
#   SBOX_GDB_TRACE_MSG     also hook Msg(), which carries the "Hitch alert" and
#                          "main thread is more than %d frames ahead" lines. Off by
#                          default: Msg is every console line in the engine, and a
#                          Python condition per log line is a real slowdown.

import os
import re
import time

import gdb

DEFAULT_PATTERNS = ";".join( [
	"QueuePresentAndWait",
	"Hitch alert",
	"out-of-date, suboptimal, or surface lost",
	"main thread is more than",
	"no swap queue images",
] )

PATTERNS = [ p for p in os.environ.get( "SBOX_GDB_PATTERNS", DEFAULT_PATTERNS ).split( ";" ) if p ]

# CoreCLR names its pool threads ".NET TP Worker", ".NET Finalizer", ".NET BGC",
# ".NET Server GC", ... - all truncated to 15 chars by the kernel, all prefixed
# ".NET". The engine's own threads are named after what they do, so the prefix is
# a clean split. The thread that actually hit the breakpoint is never skipped.
SKIP_RE = re.compile( os.environ.get( "SBOX_GDB_SKIP_THREADS", r"^\.NET" ) )

BT_CMD = os.environ.get( "SBOX_GDB_BT", "bt full" )
MAX_DUMPS = int( os.environ.get( "SBOX_GDB_MAX_DUMPS", "20" ) )
TRACE_MSG = os.environ.get( "SBOX_GDB_TRACE_MSG", "0" ) == "1"

_hits = 0
_dumps = 0
_by_pattern = {}


def out( text ):
	# gdb.write rather than print so "set logging" tees it into the log file.
	gdb.write( text + "\n" )


def read_fmt():
	"""Warning/Msg are varargs - the format string is the first integer arg, %rdi."""
	try:
		ptr = gdb.parse_and_eval( "$rdi" )
		return ptr.cast( gdb.lookup_type( "char" ).pointer() ).string( errors = "replace" )
	except gdb.error:
		return None


class MatchFunction( gdb.Function ):
	"""$_sbox_present_match($rdi) - breakpoint condition. Substring match rather
	than $_regex so the patterns need no escaping, and unreadable memory is a
	non-match instead of an error that would halt the session."""

	def __init__( self ):
		super( MatchFunction, self ).__init__( "_sbox_present_match" )

	def invoke( self, ptr ):
		try:
			text = ptr.cast( gdb.lookup_type( "char" ).pointer() ).string( errors = "replace" )
		except gdb.error:
			return 0
		return 1 if any( p in text for p in PATTERNS ) else 0


def dump_modules():
	"""Emit the ELF load base of every mapped file.

	The shipped natives are stripped (no .symtab, no .debug_info - only .dynsym),
	so most frames print as "?? () from libfoo.so". The raw 0x7ff8... addresses in
	them are meaningless outside this one process, but address minus load base is a
	build-relative offset that maps straight onto objdump, and onto the .dbg files
	the build produced (.gnu_debuglink names them, they are just not shipped).

	"info proc mappings" is the right source: the LOWEST mapping of a file is where
	vaddr 0 lands, which is what the offset arithmetic needs. "info sharedlibrary"
	reports the start of .text instead, which is not the same thing.
	"""
	try:
		raw = gdb.execute( "info proc mappings", to_string = True )
	except gdb.error as e:
		out( "--- modules unavailable: %s" % e )
		return

	bases = {}
	for line in raw.splitlines():
		parts = line.split()
		if len( parts ) < 5 or not parts[ 0 ].startswith( "0x" ):
			continue
		path = parts[ -1 ]
		if not path.startswith( "/" ):
			continue
		try:
			start = int( parts[ 0 ], 16 )
		except ValueError:
			continue
		if path not in bases or start < bases[ path ]:
			bases[ path ] = start

	out( "" )
	out( "--- modules (%d)" % len( bases ) )
	for path, start in sorted( bases.items(), key = lambda kv: kv[ 1 ] ):
		out( "module_base 0x%016x %s" % ( start, path ) )


def dump_threads( origin_tid ):
	inferior = gdb.selected_inferior()
	saved = gdb.selected_thread()

	skipped = []
	for thread in inferior.threads():
		if not thread.is_valid():
			continue

		name = thread.name or "<unnamed>"
		if thread.num != origin_tid and SKIP_RE.search( name ):
			skipped.append( "%d:%s" % ( thread.num, name ) )
			continue

		try:
			thread.switch()
		except gdb.error as e:
			out( "  <cannot switch to thread %d: %s>" % ( thread.num, e ) )
			continue

		marker = "  <-- reported the warning" if thread.num == origin_tid else ""
		out( "" )
		out( "--- thread %d [%s] lwp %s%s" % ( thread.num, name, thread.ptid[ 1 ], marker ) )
		try:
			out( gdb.execute( BT_CMD, to_string = True ).rstrip() )
		except gdb.error as e:
			out( "  <backtrace failed: %s>" % e )

	if skipped:
		out( "" )
		out( "--- skipped %d thread(s): %s" % ( len( skipped ), ", ".join( skipped ) ) )

	if saved is not None and saved.is_valid():
		saved.switch()


class DumpCommand( gdb.Command ):
	"""present-trace-dump <source> - run from the breakpoint's command list."""

	def __init__( self ):
		super( DumpCommand, self ).__init__( "present-trace-dump", gdb.COMMAND_USER )

	def invoke( self, arg, from_tty ):
		global _hits, _dumps

		fmt = read_fmt() or "<unreadable format string>"
		source = arg.strip() or "Warning"

		_hits += 1
		key = next( ( p for p in PATTERNS if p in fmt ), fmt[ :40 ] )
		_by_pattern[ key ] = _by_pattern.get( key, 0 ) + 1

		one_line = fmt.strip().replace( "\n", " " )

		stamp = time.strftime( "%H:%M:%S" )

		if MAX_DUMPS and _dumps >= MAX_DUMPS:
			out( "[present-trace] hit #%d  %s  via %s - dump cap %d reached, not backtracing: %s"
				% ( _hits, stamp, source, MAX_DUMPS, one_line ) )
			return

		_dumps += 1
		thread = gdb.selected_thread()
		origin_tid = thread.num if thread is not None else -1

		out( "" )
		out( "=" * 78 )
		out( "[present-trace] hit #%d  %s  via %s" % ( _hits, stamp, source ) )
		out( "[present-trace] %s" % one_line )
		out( "=" * 78 )
		dump_modules()
		dump_threads( origin_tid )
		out( "=" * 78 )
		out( "" )


# sbox is a .NET single-file host: it links only libc/libstdc++, and libtier0.so
# arrives later through dlopen from the interop chain. Setting "break Warning" up
# front therefore resolves against nothing and gdb reports
#
#   Error in re-setting breakpoint 1: Function "Warning" not defined.
#
# So instead of relying on pending-breakpoint resolution, wait for the objfile to
# appear and arm at that moment, when the symbol is genuinely there.

SPEW_LIB = os.environ.get( "SBOX_GDB_SPEW_LIB", "libtier0" )

_symbols = [ "Warning" ] + ( [ "Msg" ] if TRACE_MSG else [] )
_armed = False


def arm( symbol ):
	try:
		gdb.execute( "break %s" % symbol, to_string = True )
	except gdb.error as e:
		out( "[present-trace] could not break on %s: %s" % ( symbol, e ) )
		return

	breakpoint = gdb.breakpoints()[ -1 ]
	gdb.execute( "condition %d $_sbox_present_match($rdi)" % breakpoint.number, to_string = True )
	# "silent" suppresses the stop announcement, "continue" resumes - so the game
	# runs to completion and one session collects every occurrence.
	gdb.execute( "commands %d\nsilent\npresent-trace-dump %s\ncontinue\nend\n" % ( breakpoint.number, symbol ),
		to_string = True )
	out( "[present-trace] armed on %s -> breakpoint %d, %d location(s)"
		% ( symbol, breakpoint.number, breakpoint.locations and len( breakpoint.locations ) or 1 ) )


def spew_lib_loaded():
	for objfile in gdb.objfiles():
		if SPEW_LIB in os.path.basename( objfile.filename or "" ):
			return True
	return False


def try_arm( event = None ):
	"""Runs on every new objfile until the spew library shows up, then once more."""
	global _armed
	if _armed or not spew_lib_loaded():
		return
	_armed = True
	out( "[present-trace] %s loaded, arming" % SPEW_LIB )
	for symbol in _symbols:
		arm( symbol )


def on_exit( event ):
	out( "" )
	out( "[present-trace] %d matching message(s), %d backtrace dump(s)" % ( _hits, _dumps ) )
	for key, count in sorted( _by_pattern.items(), key = lambda kv: -kv[ 1 ] ):
		out( "[present-trace]   %5d  %s" % ( count, key ) )


MatchFunction()
DumpCommand()

gdb.events.new_objfile.connect( try_arm )
gdb.events.exited.connect( on_exit )

# Covers the already-attached case; otherwise the objfile event does it.
try_arm()

out( "[present-trace] waiting for %s to load, then hooking Warning%s" % ( SPEW_LIB, " + Msg" if TRACE_MSG else "" ) )
out( "[present-trace] patterns: %s" % " | ".join( PATTERNS ) )
out( "[present-trace] skipping threads matching %s, backtrace = '%s'" % ( SKIP_RE.pattern, BT_CMD ) )
