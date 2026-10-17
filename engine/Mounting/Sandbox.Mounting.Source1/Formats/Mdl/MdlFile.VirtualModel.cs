using System;
using System.Collections.Generic;
using System.IO;

namespace Sandbox.Mounting.Source1;

internal sealed partial class MdlFile
{
	// studio.h: a forward-declared sequence (not STUDIO_WORLD).
	internal const int StudioOverride = 0x0800;
	internal readonly record struct VirtualEntry( int Group, int LocalIndex );

	internal sealed class IncludeNode
	{
		public string Path { get; }
		public MdlFile Mdl { get; }
		public int Depth { get; }
		public int[] MasterSeq { get; internal set; } = Array.Empty<int>();
		public int[] MasterAnim { get; internal set; } = Array.Empty<int>();

		internal IncludeNode( string path, MdlFile mdl, int depth )
		{
			Path = path;
			Mdl = mdl;
			Depth = depth;
		}
	}

	internal enum IncludeStatus
	{
		Resolved, Missing, Unreadable, InvalidPath, Cycle, Repeated,
		DepthLimit, NodeLimit, ChildLimit, AttemptLimit
	}

	internal enum IncludeResolution { None, Verbatim, ParentRelative }
	internal readonly record struct IncludeAttempt( string Path, IncludeStatus Status, string Error );

	internal sealed class IncludeEdge
	{
		public int ParentGroup { get; internal set; }
		public int DeclarationIndex { get; internal set; }
		public string Label { get; internal set; }
		public string DeclaredPath { get; internal set; }
		public string ResolvedPath { get; internal set; }
		public int TargetGroup { get; internal set; } = -1;
		public IncludeStatus Status { get; internal set; }
		public IncludeResolution Resolution { get; internal set; }
		public string Reason { get; internal set; }
		public List<IncludeAttempt> Attempts { get; } = new();
	}

	/// <summary>
	/// Root-first DFS dependencies and SDK virtual tables. No streams are retained;
	/// decoding reopens an owner's mount-absolute Path as needed.
	/// </summary>
	internal sealed class IncludeGraph
	{
		internal const int MaxDepth = 32;
		internal const int MaxNodes = 256;
		internal const int MaxDirectChildren = 64;
		// Separate from the SDK valid-child cap: bound hostile missing declarations.
		internal const int MaxDeclarationsPerNode = 1024;
		private readonly List<IncludeNode> _nodes = new();
		private readonly List<IncludeEdge> _edges = new();
		private readonly List<VirtualEntry> _sequences = new();
		private readonly List<VirtualEntry> _animations = new();
		public IReadOnlyList<IncludeNode> Nodes => _nodes;
		public IReadOnlyList<IncludeEdge> Edges => _edges;
		public IReadOnlyList<VirtualEntry> Sequences => _sequences;
		public IReadOnlyList<VirtualEntry> Animations => _animations;

		internal IncludeGraph( string filename, MdlFile root, Func<string, Stream> openFile )
		{
			var path = NormalizeIncludePath( filename );
			_nodes.Add( new IncludeNode( path, root, 0 ) );
			var visited = new Dictionary<string, int>( StringComparer.OrdinalIgnoreCase ) { [path] = 0 };
			Visit( 0, openFile, visited, new HashSet<string>( StringComparer.OrdinalIgnoreCase ) );
			BuildVirtualTables();
			var resolved = 0;
			var cycles = 0;
			var repeated = 0;
			var limited = 0;
			foreach ( var edge in _edges )
			{
				if ( edge.Status == IncludeStatus.Resolved ) resolved++;
				else if ( edge.Status == IncludeStatus.Cycle ) cycles++;
				else if ( edge.Status == IncludeStatus.Repeated ) repeated++;
				else if ( edge.Status is IncludeStatus.DepthLimit or IncludeStatus.NodeLimit or IncludeStatus.ChildLimit or IncludeStatus.AttemptLimit ) limited++;
			}
			Log.Trace( $"{path}: include graph: {_nodes.Count} nodes, {_edges.Count} edges, {resolved} resolved, {cycles} cycles, {repeated} repeated, {limited} limited, {_edges.Count - resolved - cycles - repeated - limited} unavailable; {_sequences.Count} sequences, {_animations.Count} animations." );
		}

		private void Visit( int group, Func<string, Stream> openFile, Dictionary<string, int> visited, HashSet<string> active )
		{
			var parent = _nodes[group];
			active.Add( parent.Path );
			var validChildren = 0;
			for ( var i = 0; i < parent.Mdl.IncludeCount; i++ )
			{
				var edge = new IncludeEdge
				{
					ParentGroup = group,
					DeclarationIndex = i,
					Label = parent.Mdl.GetIncludeLabel( i ),
					DeclaredPath = parent.Mdl.GetIncludeFile( i )
				};
				_edges.Add( edge );
				if ( validChildren >= MaxDirectChildren )
				{
					edge.Status = IncludeStatus.ChildLimit;
					edge.Reason = $"Stopped after {MaxDirectChildren} successfully resolved direct includes (not declarations).";
				}
				else if ( i >= MaxDeclarationsPerNode )
				{
					edge.Status = IncludeStatus.AttemptLimit;
					edge.Reason = $"Stopped after {MaxDeclarationsPerNode} declarations to bound missing/unreadable work independently of the SDK valid-child cap.";
				}
				else
				{
					ResolveEdge( parent, edge, openFile, visited, active );
				}
				Log.Trace( $"{parent.Path}: include[{i}] '{edge.Label}' '{edge.DeclaredPath}' => {edge.Status} {edge.Resolution} '{edge.ResolvedPath}': {edge.Reason}" );
				if ( edge.Status is IncludeStatus.ChildLimit or IncludeStatus.AttemptLimit ) break;
				if ( edge.Status is IncludeStatus.Resolved or IncludeStatus.Cycle or IncludeStatus.Repeated ) validChildren++;
				if ( edge.Status == IncludeStatus.Resolved ) Visit( edge.TargetGroup, openFile, visited, active );
			}
			active.Remove( parent.Path );
		}

		private void ResolveEdge( IncludeNode parent, IncludeEdge edge, Func<string, Stream> openFile, Dictionary<string, int> visited, HashSet<string> active )
		{
			if ( string.IsNullOrWhiteSpace( edge.DeclaredPath ) )
			{
				edge.Status = IncludeStatus.InvalidPath;
				edge.Reason = "Empty include filename.";
				return;
			}
			var paths = new List<(string Path, IncludeResolution Resolution)>();
			AddPath( edge.DeclaredPath, IncludeResolution.Verbatim );
			var slash = parent.Path.LastIndexOf( '/' );
			if ( slash >= 0 ) AddPath( parent.Path[..(slash + 1)] + edge.DeclaredPath, IncludeResolution.ParentRelative );
			foreach ( var candidate in paths )
			{
				var path = candidate.Path;
				if ( visited.TryGetValue( path, out var existing ) )
				{
					edge.TargetGroup = existing;
					edge.ResolvedPath = path;
					edge.Resolution = candidate.Resolution;
					edge.Status = active.Contains( path ) ? IncludeStatus.Cycle : IncludeStatus.Repeated;
					edge.Reason = edge.Status == IncludeStatus.Cycle ? "Ancestor cycle; not traversed again." : "Already visited dependency; not traversed again.";
					return;
				}
				// Limits are not missing candidates: do not fall through to another path.
				if ( parent.Depth >= MaxDepth || _nodes.Count >= MaxNodes )
				{
					edge.Status = parent.Depth >= MaxDepth ? IncludeStatus.DepthLimit : IncludeStatus.NodeLimit;
					edge.Reason = $"Include graph bounded to depth {MaxDepth} and {MaxNodes} nodes.";
					return;
				}
				try
				{
					MdlFile mdl;
					using ( var stream = openFile?.Invoke( path ) )
					{
						if ( stream == null )
						{
							edge.Attempts.Add( new IncludeAttempt( path, IncludeStatus.Missing, "Mount returned no stream." ) );
							continue;
						}
						mdl = new MdlFile( stream );
					}
					// Register only after parsing AND disposal have succeeded.
					edge.TargetGroup = _nodes.Count;
					_nodes.Add( new IncludeNode( path, mdl, parent.Depth + 1 ) );
					visited.Add( path, edge.TargetGroup );
					edge.Status = IncludeStatus.Resolved;
					edge.ResolvedPath = path;
					edge.Resolution = candidate.Resolution;
					edge.Attempts.Add( new IncludeAttempt( path, IncludeStatus.Resolved, null ) );
					return;
				}
				catch ( Exception e ) when ( e is InvalidDataException or IOException or NotSupportedException or ArgumentException or UnauthorizedAccessException )
				{
					var status = e is FileNotFoundException or DirectoryNotFoundException ? IncludeStatus.Missing : IncludeStatus.Unreadable;
					edge.Attempts.Add( new IncludeAttempt( path, status, e.Message ) );
					Log.Trace( $"{parent.Path}: include '{path}' was not imported: {e.Message}" );
				}
			}
			edge.Status = paths.Count == 0 ? IncludeStatus.InvalidPath : IncludeStatus.Missing;
			foreach ( var attempt in edge.Attempts )
				if ( attempt.Status == IncludeStatus.Unreadable ) edge.Status = IncludeStatus.Unreadable;
			edge.Reason = "No readable include at the declared path or parent-directory-relative path.";

			void AddPath( string value, IncludeResolution resolution )
			{
				try
				{
					var normalized = NormalizeIncludePath( value );
					foreach ( var existing in paths )
						if ( string.Equals( existing.Path, normalized, StringComparison.OrdinalIgnoreCase ) ) return;
					paths.Add( (normalized, resolution) );
				}
				catch ( ArgumentException e )
				{
					edge.Attempts.Add( new IncludeAttempt( value, IncludeStatus.InvalidPath, e.Message ) );
				}
			}
		}

		/// <summary>Rebuild tables in root-first DFS order; local maps remain separate.</summary>
		public void BuildVirtualTables()
		{
			_sequences.Clear();
			_animations.Clear();
			for ( var group = 0; group < _nodes.Count; group++ )
			{
				_nodes[group].MasterSeq = Append( group, _sequences, true );
				_nodes[group].MasterAnim = Append( group, _animations, false );
			}
		}

		private int[] Append( int group, List<VirtualEntry> table, bool sequences )
		{
			// numCheck snapshot: newly appended same-group names are NOT deduped.
			var numCheck = table.Count;
			var previous = new Dictionary<string, int>( StringComparer.OrdinalIgnoreCase );
			for ( var i = 0; i < numCheck; i++ )
			{
				var entry = table[i];
				var name = TableName( _nodes[entry.Group].Mdl, entry.LocalIndex, sequences );
				if ( name != null ) previous.TryAdd( name, i );
			}
			var mdl = _nodes[group].Mdl;
			var master = new int[sequences ? mdl.SequenceCount : mdl.AnimDescCount];
			for ( var i = 0; i < master.Length; i++ )
			{
				var name = TableName( mdl, i, sequences );
				if ( name == null || !previous.TryGetValue( name, out var slot ) )
				{
					slot = table.Count;
					table.Add( new VirtualEntry( group, i ) );
				}
				else if ( sequences )
				{
					var owner = table[slot];
					if ( (_nodes[owner.Group].Mdl.GetSequence( owner.LocalIndex ).Flags & StudioOverride) != 0 )
						table[slot] = new VirtualEntry( group, i );
				}
				master[i] = slot;
			}
			return master;
		}

		private static string TableName( MdlFile mdl, int index, bool sequences )
		{
			var name = sequences ? mdl.GetSequenceName( index ) : mdl.GetAnimDescName( index );
			if ( string.IsNullOrWhiteSpace( name ) ) return null;
			if ( !sequences && !mdl.HasAnimDescName( index ) ) return null;
			return name;
		}
	}

	internal IncludeGraph ResolveIncludes( string filename, Func<string, Stream> openFile ) => new( filename, this, openFile );

	private static string NormalizeIncludePath( string path )
	{
		if ( string.IsNullOrWhiteSpace( path ) || path.IndexOf( '\0' ) >= 0 || path.IndexOf( ':' ) >= 0 )
			throw new ArgumentException( "Invalid mount-relative include path." );
		var parts = new List<string>();
		foreach ( var part in path.Replace( '\\', '/' ).Split( '/', StringSplitOptions.RemoveEmptyEntries ) )
		{
			if ( part == "." ) continue;
			if ( part == ".." )
			{
				if ( parts.Count == 0 ) throw new ArgumentException( "Include path escapes the mount root." );
				parts.RemoveAt( parts.Count - 1 );
			}
			else parts.Add( part );
		}
		if ( parts.Count == 0 ) throw new ArgumentException( "Empty mount-relative include path." );
		return string.Join( '/', parts );
	}
}
