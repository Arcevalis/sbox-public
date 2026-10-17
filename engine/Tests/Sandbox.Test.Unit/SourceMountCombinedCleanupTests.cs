using Sandbox.Mounting.Source1;
using System;
using System.Collections.Generic;
using System.IO;

public partial class SourceMountAnimationTests
{
	[TestMethod]
	public void VirtualDecodeDisposesEveryLibraryAndKeepsClipsAfterCleanupFailure()
	{
		var files = TwoLibraryFiles();
		var counts = new Dictionary<string, int>();
		var reopened = new List<CleanupTestStream>();
		Stream Open( string path )
		{
			counts.TryGetValue( path, out var count );
			counts[path] = ++count;
			if ( count == 1 || path == "models/root.mdl" ) return new MemoryStream( files[path] );
			var stream = new CleanupTestStream( files[path], path == "models/a.mdl" );
			reopened.Add( stream );
			return stream;
		}
		using var root = Open( "models/root.mdl" );
		var mdl = new MdlFile( root );
		var animations = mdl.ReadAnimations( mdl.ResolveIncludes( "models/root.mdl", Open ), root, Open );
		Assert.AreEqual( 2, animations.Count );
		Assert.AreEqual( 2, reopened.Count );
		Assert.IsTrue( reopened[0].WasDisposed );
		Assert.IsTrue( reopened[1].WasDisposed );
		Assert.IsTrue( root.CanRead );
	}

	[TestMethod]
	public void VirtualDecodePermissionFailureReopeningOneLibraryKeepsLaterClips()
	{
		var files = TwoLibraryFiles();
		var counts = new Dictionary<string, int>();
		Stream Open( string path )
		{
			counts.TryGetValue( path, out var count );
			counts[path] = ++count;
			if ( count > 1 && path == "models/a.mdl" ) throw new UnauthorizedAccessException( "Optional library inaccessible." );
			return new MemoryStream( files[path] );
		}
		using var root = Open( "models/root.mdl" );
		var mdl = new MdlFile( root );
		var animations = mdl.ReadAnimations( mdl.ResolveIncludes( "models/root.mdl", Open ), root, Open );
		Assert.AreEqual( 1, animations.Count );
		Assert.AreEqual( "b", animations[0].Name );
	}

	private static Dictionary<string, byte[]> TwoLibraryFiles() => new()
	{
		["models/root.mdl"] = VirtualFixture( new[] { "root" }, new[] { 10f }, includes: new[] { "models/a.mdl", "models/b.mdl" } ),
		["models/a.mdl"] = VirtualFixture( new[] { "root" }, new[] { 10f }, "a", "anim_a" ),
		["models/b.mdl"] = VirtualFixture( new[] { "root" }, new[] { 10f }, "b", "anim_b" ),
	};

	private sealed class CleanupTestStream( byte[] bytes, bool throwOnDispose ) : MemoryStream( bytes )
	{
		public bool WasDisposed { get; private set; }

		protected override void Dispose( bool disposing )
		{
			WasDisposed = true;
			base.Dispose( disposing );
			if ( throwOnDispose ) throw new IOException( "Synthetic cleanup failure." );
		}
	}
}
