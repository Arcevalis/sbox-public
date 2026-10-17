using Sandbox.Mounting.Source1;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

[TestClass]
public class SourceMountVirtualModelTests
{
	[TestMethod]
	public void IncludesAreRootFirstDepthFirstAndDisposeParsedStreams()
	{
		var files = new Dictionary<string, byte[]>( StringComparer.OrdinalIgnoreCase )
		{
			["models/a.mdl"] = BuildMdl( ["models/shared.mdl", "MODELS/./ROOT.MDL"] ),
			["models/shared.mdl"] = BuildMdl(),
			["models/b.mdl"] = BuildMdl( ["MODELS\\shared.mdl"] )
		};
		using var root = new MemoryStream( BuildMdl( ["models/a.mdl", "models/b.mdl"] ) );
		var opened = new List<TrackedStream>();
		var graph = new MdlFile( root ).ResolveIncludes( "models/root.mdl", path =>
		{
			var stream = new TrackedStream( files[path] );
			opened.Add( stream );
			return stream;
		} );
		CollectionAssert.AreEqual( new[] { "models/root.mdl", "models/a.mdl", "models/shared.mdl", "models/b.mdl" }, graph.Nodes.Select( x => x.Path ).ToArray() );
		CollectionAssert.AreEqual( new[] { 0, 1, 2, 1 }, graph.Nodes.Select( x => x.Depth ).ToArray() );
		Assert.AreEqual( 3, opened.Count );
		Assert.IsTrue( opened.All( x => x.Disposed ) );
		Assert.IsTrue( root.CanRead );
		Assert.AreEqual( 1, graph.Edges.Count( x => x.Status == MdlFile.IncludeStatus.Cycle ) );
		Assert.AreEqual( 1, graph.Edges.Count( x => x.Status == MdlFile.IncludeStatus.Repeated ) );
	}

	[TestMethod]
	public void TriesVerbatimThenParentRelativeWithoutInventingModelsPrefix()
	{
		using var root = new MemoryStream( BuildMdl( ["shared.mdl", "local.mdl", "missing.mdl", "../sibling.mdl"] ) );
		var calls = new List<string>();
		var graph = new MdlFile( root ).ResolveIncludes( "models/sub/root.mdl", path =>
		{
			calls.Add( path );
			return path is "shared.mdl" or "models/sub/local.mdl" or "models/sibling.mdl" ? new MemoryStream( BuildMdl() ) : null;
		} );
		CollectionAssert.AreEqual( new[] { "shared.mdl", "local.mdl", "models/sub/local.mdl", "missing.mdl", "models/sub/missing.mdl", "models/sibling.mdl" }, calls );
		Assert.AreEqual( MdlFile.IncludeResolution.Verbatim, graph.Edges[0].Resolution );
		Assert.AreEqual( MdlFile.IncludeResolution.ParentRelative, graph.Edges[1].Resolution );
		Assert.AreEqual( MdlFile.IncludeStatus.Missing, graph.Edges[2].Status );
		Assert.AreEqual( MdlFile.IncludeResolution.ParentRelative, graph.Edges[3].Resolution );
	}

	[TestMethod]
	public void UnreadableLibrariesDoNotHideLaterValidIncludes()
	{
		using var root = new MemoryStream( BuildMdl( ["bad.mdl", "io.mdl", "unsupported.mdl", "argument.mdl", "valid.mdl"] ) );
		using var invalid = new TrackedStream( new byte[1] );
		var graph = new MdlFile( root ).ResolveIncludes( "root.mdl", path => path switch
		{
			"bad.mdl" => invalid,
			"io.mdl" => throw new IOException( "unreadable" ),
			"unsupported.mdl" => throw new NotSupportedException(),
			"argument.mdl" => throw new ArgumentException(),
			_ => new MemoryStream( BuildMdl() )
		} );
		Assert.IsTrue( invalid.Disposed );
		Assert.AreEqual( 2, graph.Nodes.Count );
		Assert.AreEqual( "valid.mdl", graph.Nodes[1].Path );
		Assert.IsTrue( graph.Edges.Take( 4 ).All( x => x.Status == MdlFile.IncludeStatus.Unreadable ) );
	}

	[TestMethod]
	public void DirectChildLimitCountsSuccessfulIncludesNotDeclarations()
	{
		var includes = Enumerable.Range( 0, 70 ).Select( i => $"missing{i}.mdl" )
			.Concat( Enumerable.Range( 0, 65 ).Select( i => $"valid{i}.mdl" ) ).ToArray();
		using var root = new MemoryStream( BuildMdl( includes ) );
		var calls = 0;
		var graph = new MdlFile( root ).ResolveIncludes( "root.mdl", path =>
		{
			calls++;
			return path.StartsWith( "valid", StringComparison.Ordinal ) ? new MemoryStream( BuildMdl() ) : null;
		} );
		Assert.AreEqual( 65, graph.Nodes.Count );
		Assert.AreEqual( 134, calls );
		Assert.AreEqual( MdlFile.IncludeStatus.ChildLimit, graph.Edges[^1].Status );
		Assert.AreEqual( 134, graph.Edges[^1].DeclarationIndex );
	}

	[TestMethod]
	public void BoundsMissingDeclarationAttemptsSeparatelyFromValidChildCap()
	{
		using var root = new MemoryStream( BuildMdl( Enumerable.Range( 0, 1030 ).Select( i => $"missing{i}.mdl" ).ToArray() ) );
		var calls = 0;
		var graph = new MdlFile( root ).ResolveIncludes( "root.mdl", _ => { calls++; return null; } );
		Assert.AreEqual( MdlFile.IncludeGraph.MaxDeclarationsPerNode, calls );
		Assert.AreEqual( MdlFile.IncludeStatus.AttemptLimit, graph.Edges[^1].Status );
		Assert.IsTrue( graph.Edges[^1].Reason.Contains( "missing/unreadable", StringComparison.Ordinal ) );
	}

	[TestMethod]
	public void BoundsDepthAndTotalNodeCount()
	{
		using var root = new MemoryStream( BuildMdl( ["node1.mdl"] ) );
		var graph = new MdlFile( root ).ResolveIncludes( "node0.mdl", path =>
		{
			var index = int.Parse( path.AsSpan( 4, path.Length - 8 ) );
			return new MemoryStream( BuildMdl( [$"node{index + 1}.mdl"] ) );
		} );
		Assert.AreEqual( 33, graph.Nodes.Count );
		Assert.AreEqual( 32, graph.Nodes[^1].Depth );
		Assert.AreEqual( MdlFile.IncludeStatus.DepthLimit, graph.Edges[^1].Status );

		using var wideRoot = new MemoryStream( BuildMdl( Enumerable.Range( 0, 64 ).Select( i => $"branch{i}.mdl" ).ToArray() ) );
		graph = new MdlFile( wideRoot ).ResolveIncludes( "root.mdl", path => new MemoryStream( BuildMdl(
			path.StartsWith( "branch", StringComparison.Ordinal ) && !path.Contains( '_' ) ? Enumerable.Range( 0, 4 ).Select( i => $"{path}_{i}.mdl" ).ToArray() : null ) ) );
		Assert.AreEqual( 256, graph.Nodes.Count );
		Assert.IsTrue( graph.Edges.Any( x => x.Status == MdlFile.IncludeStatus.NodeLimit ) );
	}

	[TestMethod]
	public void SameGroupDuplicatesAppendButLaterGroupsMatchPreviousSlots()
	{
		using var root = new MemoryStream( BuildMdl( ["child.mdl"], ["idle", "IDLE"], ["take", "TAKE"] ) );
		var graph = new MdlFile( root ).ResolveIncludes( "root.mdl", _ => new MemoryStream( BuildMdl(
			sequences: ["Idle", "new", "NEW"], animations: ["Take", "other", "OTHER"] ) ) );
		Assert.AreEqual( 4, graph.Sequences.Count );
		Assert.AreEqual( 4, graph.Animations.Count );
		CollectionAssert.AreEqual( new[] { 0, 1 }, graph.Nodes[0].MasterSeq );
		CollectionAssert.AreEqual( new[] { 0, 2, 3 }, graph.Nodes[1].MasterSeq );
		CollectionAssert.AreEqual( new[] { 0, 1 }, graph.Nodes[0].MasterAnim );
		CollectionAssert.AreEqual( new[] { 0, 2, 3 }, graph.Nodes[1].MasterAnim );
		Assert.AreEqual( new MdlFile.VirtualEntry( 0, 0 ), graph.Sequences[0] );
		Assert.AreEqual( new MdlFile.VirtualEntry( 0, 0 ), graph.Animations[0] );
		graph.BuildVirtualTables();
		Assert.AreEqual( 4, graph.Sequences.Count );
		CollectionAssert.AreEqual( new[] { 0, 2, 3 }, graph.Nodes[1].MasterAnim );
	}

	[TestMethod]
	public void OnlySdkForwardDeclarationFlagAllowsReplacingSequenceOwner()
	{
		using var root = new MemoryStream( BuildMdl( ["child.mdl"], ["forward", "not_forward"], ["take"], [0x0800, 0x8000] ) );
		var graph = new MdlFile( root ).ResolveIncludes( "root.mdl", _ => new MemoryStream( BuildMdl(
			sequences: ["FORWARD", "NOT_FORWARD"], animations: ["TAKE"] ) ) );
		Assert.AreEqual( 0x0800, MdlFile.StudioOverride );
		Assert.AreEqual( new MdlFile.VirtualEntry( 1, 0 ), graph.Sequences[0] );
		Assert.AreEqual( new MdlFile.VirtualEntry( 0, 1 ), graph.Sequences[1] );
		Assert.AreEqual( new MdlFile.VirtualEntry( 0, 0 ), graph.Animations[0] );
		CollectionAssert.AreEqual( new[] { 0, 1 }, graph.Nodes[1].MasterSeq );
		CollectionAssert.AreEqual( new[] { 0 }, graph.Nodes[1].MasterAnim );
	}

	[TestMethod]
	public void UnnamedAnimationDescriptorsRemainUniqueAcrossGroups()
	{
		using var root = new MemoryStream( BuildMdl( ["child.mdl"], animations: [null, "animation_0"] ) );
		var graph = new MdlFile( root ).ResolveIncludes( "root.mdl", _ => new MemoryStream( BuildMdl( animations: [null, "ANIMATION_0"] ) ) );
		Assert.AreEqual( 3, graph.Animations.Count );
		CollectionAssert.AreEqual( new[] { 0, 1 }, graph.Nodes[0].MasterAnim );
		CollectionAssert.AreEqual( new[] { 2, 1 }, graph.Nodes[1].MasterAnim );
	}

	static byte[] BuildMdl( string[] includes = null, string[] sequences = null, string[] animations = null, int[] flags = null )
	{
		includes ??= Array.Empty<string>();
		sequences ??= Array.Empty<string>();
		animations ??= Array.Empty<string>();
		using var stream = new MemoryStream();
		using var writer = new BinaryWriter( stream, Encoding.UTF8, leaveOpen: true );
		writer.Write( new byte[Marshal.SizeOf<MdlFile.Header>()] );
		Header( nameof( MdlFile.Header.Id ), 0x54534449 );
		Header( nameof( MdlFile.Header.Version ), 48 );
		Header( nameof( MdlFile.Header.NumIncludeModels ), includes.Length );
		Header( nameof( MdlFile.Header.IncludeModelOffset ), (int)stream.Position );
		Entries( includes.Length, 8, ( entry, i ) =>
		{
			RelativeString( entry, 0, $"include_{i}" );
			RelativeString( entry, 4, includes[i] );
		} );
		Header( nameof( MdlFile.Header.NumLocalSeq ), sequences.Length );
		Header( nameof( MdlFile.Header.LocalSeqIndex ), (int)stream.Position );
		Entries( sequences.Length, Marshal.SizeOf<MdlFile.StudioSeqDesc>(), ( entry, i ) =>
		{
			RelativeString( entry, 4, sequences[i] );
			Patch( entry + 12, flags?[i] ?? 0 );
		} );
		Header( nameof( MdlFile.Header.NumLocalAnims ), animations.Length );
		Header( nameof( MdlFile.Header.LocalAnimIndex ), (int)stream.Position );
		Entries( animations.Length, Marshal.SizeOf<MdlFile.StudioAnimDesc>(), ( entry, i ) => RelativeString( entry, 4, animations[i] ) );
		Header( nameof( MdlFile.Header.Length ), (int)stream.Length );
		return stream.ToArray();

		void Header( string field, int value ) => Patch( Marshal.OffsetOf<MdlFile.Header>( field ).ToInt64(), value );
		void Patch( long offset, int value )
		{
			var position = stream.Position;
			stream.Position = offset;
			writer.Write( value );
			stream.Position = position;
		}
		void Entries( int count, int size, Action<long, int> write )
		{
			var offset = stream.Position;
			writer.Write( new byte[count * size] );
			for ( var i = 0; i < count; i++ ) write( offset + i * size, i );
		}
		void RelativeString( long entry, int field, string text )
		{
			if ( text == null ) return;
			Patch( entry + field, checked((int)(stream.Position - entry)) );
			writer.Write( Encoding.UTF8.GetBytes( text + '\0' ) );
		}
	}

	sealed class TrackedStream( byte[] bytes ) : MemoryStream( bytes )
	{
		public bool Disposed { get; private set; }
		protected override void Dispose( bool disposing )
		{
			Disposed = true;
			base.Dispose( disposing );
		}
	}
}
