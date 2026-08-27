#!/usr/bin/env python3
"""Split a present-trace log into one file per distinct stall, grouped by stack signature.

A trace run records every occurrence, but occurrences are not bugs: eleven hits with
byte-identical stacks are one bug seen eleven times, and the thing worth knowing is
whether a run contains one signature or several.

Frames are normalised to "module+0xoffset" before grouping. That matters twice over:
raw runtime addresses differ between runs (ASLR) so they cannot group across logs, and
a build-relative offset is what objdump - and the .dbg files the build produced -
actually consume. Offsets need the ELF load base, which present-trace.py writes as
"module_base" lines; without them the grouping falls back to raw addresses and is only
valid within a single run.

Idle threads are excluded from the signature by default. They are the large majority
(parked in futex/poll/nanosleep), their thread numbers churn between dumps, and
including them would make every hit look unique. They are still written to the output.

Usage:
  split-trace.py <trace.log> [-o OUTDIR] [--all-threads] [--full]

  -o OUTDIR      where to write (default: <log dir>/split/<log stem>/)
  --all-threads  include idle threads in the signature too
  --full         write every occurrence's dump, not just a representative
"""

import argparse
import hashlib
import os
import re
import sys

HIT_RE = re.compile( r"^\[present-trace\] hit #(\d+)\s+(\S+)\s+via (\S+)" )
THREAD_RE = re.compile( r"^--- thread (\d+) \[([^\]]*)\] lwp (\d+)(.*)$" )
FRAME_RE = re.compile( r"^#(\d+)\s+(?:(0x[0-9a-f]+) in )?(.+?)(?: from (\S+))?$" )
MODULE_RE = re.compile( r"^module_base (0x[0-9a-f]+) (\S+)$" )
BAR = "=" * 78

# A thread sitting in one of these as frame #0 is parked, not working.
IDLE_MARKERS = (
	"futex", "pthread_cond", "nanosleep", "__poll", "epoll_wait", "syscall_cancel",
	"__GI___clock_nanosleep", "select", "accept", "recvmsg", "sigtimedwait", "sem_wait",
	"syscall ()",
)


class Frame:
	def __init__( self, index, addr, text, module ):
		self.index = index
		self.addr = addr
		self.text = text
		self.module = module
		self.offset = None

	def base_for( self, bases ):
		"""Match a backtrace's module path against the recorded load bases.

		These do not always agree textually. gdb prints the path the library was
		opened with, which for the engine's own libraries contains a doubled slash
		("game//bin/linuxsteamrt64/libengine2.so" - 129 such lines in one real log),
		while "info proc mappings" reports the kernel's normalised path. Exact match
		first, then normalised, then basename.
		"""
		if self.module in bases:
			return bases[ self.module ]
		normalised = os.path.normpath( self.module )
		if normalised in bases:
			return bases[ normalised ]
		return bases.get( os.path.basename( self.module ) )

	def normalised( self, bases ):
		"""module+0xoffset when resolvable, else the symbol name, else the raw address."""
		base = self.base_for( bases ) if self.module else None
		if base is not None and self.addr:
			self.offset = int( self.addr, 16 ) - base
			return "%s+0x%x" % ( os.path.basename( self.module ), self.offset )
		if self.text and not self.text.startswith( "?? " ):
			name = self.text.split( "(" )[ 0 ].strip()
			if name:
				return name
		if self.module:
			return "%s@%s" % ( os.path.basename( self.module ), self.addr or "?" )
		return self.addr or self.text


class Thread:
	def __init__( self, num, name, lwp, note ):
		self.num = num
		self.name = name
		self.lwp = lwp
		self.note = note.strip()
		self.frames = []
		self.lines = []

	@property
	def is_origin( self ):
		return "reported the warning" in self.note

	@property
	def is_idle( self ):
		if not self.frames:
			return True
		head = self.frames[ 0 ].text
		return any( marker in head for marker in IDLE_MARKERS )

	def useful_frames( self ):
		"""Frames up to the point the unwind stops being trustworthy.

		Native frames carry a module ("?? () from libfoo.so") or a real symbol. Once
		gdb walks off the end of native code - into JIT'd managed frames, which have
		neither CFI nor symbols here - it starts reporting stack slots as return
		addresses: "0x000a09e84283add1 in ?? ()" with no module. Those values differ
		on every hit, so including them makes each occurrence look unique and defeats
		the grouping entirely. Everything past the first such frame is discarded.
		"""
		kept = []
		for frame in self.frames:
			if not frame.module and frame.text.startswith( "??" ):
				break
			kept.append( frame )
		return kept

	def signature( self, bases, depth ):
		frames = self.useful_frames()[ :depth ]
		return ( self.name, tuple( f.normalised( bases ) for f in frames ) )


class Hit:
	def __init__( self, number, stamp, source ):
		self.number = number
		self.stamp = stamp
		self.source = source
		self.message = ""
		self.threads = []
		self.bases = {}
		self.skipped = ""
		self.lines = []

	@property
	def capped( self ):
		"""A hit past --max-dumps: counted and timestamped, but with no backtrace."""
		return not self.threads

	def signature( self, all_threads, depth ):
		parts = []
		for thread in self.threads:
			if not all_threads and thread.is_idle and not thread.is_origin:
				continue
			parts.append( thread.signature( self.bases, depth ) )
		# Sort so thread enumeration order cannot split an otherwise identical hit.
		parts.sort()
		blob = repr( ( self.message, parts ) ).encode()
		return hashlib.sha1( blob ).hexdigest()[ :10 ], parts

	@property
	def resolvable( self ):
		return bool( self.bases )


def parse( path ):
	hits = []
	current = None
	thread = None

	with open( path, errors = "replace" ) as handle:
		for line in handle:
			line = line.rstrip( "\n" )

			match = HIT_RE.match( line )
			if match:
				current = Hit( int( match.group( 1 ) ), match.group( 2 ), match.group( 3 ) )
				hits.append( current )
				thread = None
				current.lines.append( line )
				continue

			if current is None:
				continue

			current.lines.append( line )

			if line.startswith( "[present-trace] " ) and not current.message:
				current.message = line[ len( "[present-trace] " ): ]
				continue

			match = MODULE_RE.match( line )
			if match:
				path, base = match.group( 2 ), int( match.group( 1 ), 16 )
				current.bases[ path ] = base
				current.bases.setdefault( os.path.normpath( path ), base )
				current.bases.setdefault( os.path.basename( path ), base )
				continue

			match = THREAD_RE.match( line )
			if match:
				thread = Thread( int( match.group( 1 ) ), match.group( 2 ), match.group( 3 ), match.group( 4 ) )
				current.threads.append( thread )
				thread.lines.append( line )
				continue

			if line.startswith( "--- skipped" ):
				current.skipped = line
				thread = None
				continue

			if thread is not None:
				thread.lines.append( line )
				match = FRAME_RE.match( line )
				if match:
					thread.frames.append( Frame(
						int( match.group( 1 ) ), match.group( 2 ),
						( match.group( 3 ) or "" ).strip(), match.group( 4 ) ) )

	return hits


def write_group( out_dir, rank, sig, members, parts, full ):
	name = "group-%02d-%s.log" % ( rank, sig )
	path = os.path.join( out_dir, name )
	first = members[ 0 ]

	with open( path, "w" ) as handle:
		handle.write( "%s\n" % BAR )
		handle.write( "signature   %s\n" % sig )
		handle.write( "occurrences %d\n" % len( members ) )
		handle.write( "hits        %s\n" % ", ".join( "#%d" % m.number for m in members ) )
		handle.write( "timestamps  %s -> %s\n" % ( members[ 0 ].stamp, members[ -1 ].stamp ) )
		handle.write( "message     %s\n" % first.message )
		handle.write( "offsets     %s\n" % ( "resolved from module_base" if first.resolvable
			else "UNRESOLVED - no module_base lines, raw addresses, valid within this run only" ) )
		handle.write( "%s\n\n" % BAR )

		handle.write( "active threads in the signature:\n" )
		for tname, frames in parts:
			handle.write( "\n  [%s]\n" % tname )
			for depth, frame in enumerate( frames ):
				handle.write( "    #%-2d %s\n" % ( depth, frame ) )

		handle.write( "\n%s\n" % BAR )
		handle.write( "representative dump (hit #%d at %s)\n" % ( first.number, first.stamp ) )
		handle.write( "%s\n" % BAR )
		handle.write( "\n".join( first.lines ) )
		handle.write( "\n" )

		if full and len( members ) > 1:
			for member in members[ 1: ]:
				handle.write( "\n%s\nhit #%d at %s\n%s\n" % ( BAR, member.number, member.stamp, BAR ) )
				handle.write( "\n".join( member.lines ) )
				handle.write( "\n" )

	return path, name


def main():
	parser = argparse.ArgumentParser( description = __doc__,
		formatter_class = argparse.RawDescriptionHelpFormatter )
	parser.add_argument( "log" )
	parser.add_argument( "-o", "--out-dir" )
	parser.add_argument( "--all-threads", action = "store_true" )
	parser.add_argument( "--full", action = "store_true" )
	parser.add_argument( "--depth", type = int, default = 24,
		help = "max frames per thread in the signature (default 24)" )
	args = parser.parse_args()

	if not os.path.isfile( args.log ):
		sys.exit( "no such file: %s" % args.log )

	hits = parse( args.log )
	if not hits:
		sys.exit( "no [present-trace] hits found in %s" % args.log )

	stem = os.path.splitext( os.path.basename( args.log ) )[ 0 ]
	out_dir = args.out_dir or os.path.join( os.path.dirname( os.path.abspath( args.log ) ), "split", stem )
	os.makedirs( out_dir, exist_ok = True )

	capped = [ h for h in hits if h.capped ]
	dumped = [ h for h in hits if not h.capped ]

	groups = {}
	order = []
	for hit in dumped:
		sig, parts = hit.signature( args.all_threads, args.depth )
		if sig not in groups:
			groups[ sig ] = ( [], parts )
			order.append( sig )
		groups[ sig ][ 0 ].append( hit )

	ranked = sorted( order, key = lambda s: -len( groups[ s ][ 0 ] ) )

	index_path = os.path.join( out_dir, "index.txt" )
	with open( index_path, "w" ) as index:
		index.write( "source     %s\n" % os.path.abspath( args.log ) )
		index.write( "hits       %d (%d with backtraces, %d past the dump cap)\n"
			% ( len( hits ), len( dumped ), len( capped ) ) )
		index.write( "signatures %d\n" % len( ranked ) )
		index.write( "offsets    %s\n\n" % ( "resolved" if dumped and dumped[ 0 ].resolvable else
			"UNRESOLVED (no module_base lines - re-run with the current present-trace.py)" ) )

		print( "%d hit(s), %d with backtraces -> %d distinct signature(s)"
			% ( len( hits ), len( dumped ), len( ranked ) ) )
		if capped:
			note = ( "%d further hit(s) past the dump cap, no backtrace: %s -> %s"
				% ( len( capped ), capped[ 0 ].stamp, capped[ -1 ].stamp ) )
			index.write( note + "\n" )
			print( "  " + note )
		for rank, sig in enumerate( ranked, 1 ):
			members, parts = groups[ sig ]
			path, name = write_group( out_dir, rank, sig, members, parts, args.full )
			active = ", ".join( tname for tname, _ in parts ) or "<none>"
			line = "%-28s %3d occurrence(s)  %s -> %s  [%s]" % (
				name, len( members ), members[ 0 ].stamp, members[ -1 ].stamp, active )
			index.write( line + "\n" )
			print( "  " + line )

	print( "\nindex: %s" % index_path )


if __name__ == "__main__":
	main()
