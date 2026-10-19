using Editor;
using Sandbox;
using Sandbox.Engine;
using System;
using System.Collections.Generic;
using System.IO;

[TestClass]
[DoNotParallelize]
public class SceneGenerationCleanupTests
{
	static void WithMountedProject( Action<string> action )
	{
		var root = Path.Combine( Path.GetTempPath(), $"scene-cleanup-{Guid.NewGuid():N}" );
		Directory.CreateDirectory( root );

		var previous = GlobalContext.Current.FileMount;
		var mount = new AggregateFileSystem();
		var local = new LocalFileSystem( root );
		mount.Mount( local );
		GlobalContext.Current.FileMount = mount;

		try
		{
			action( root );
		}
		finally
		{
			GlobalContext.Current.FileMount = previous;
			mount.Dispose();
			local.Dispose();
			Directory.Delete( root, true );
		}
	}

	[TestMethod]
	public void CleanupPreviousGenerations_DeletesOldGenerationsAndStraysThroughReadOnlyMount()
	{
		WithMountedProject( root =>
		{
			var compiled = Path.Combine( root, "scenes", "menu_scene_data", "compiled" );
			var oldGen = Path.Combine( compiled, "f1e54c1f53ee45719b9ef219e443373f" );
			var currentGen = Path.Combine( compiled, "9b8a7c6d5e4f3a2b1c0d9e8f7a6b5c4d" );
			var activeGen = Path.Combine( compiled, "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" );
			Directory.CreateDirectory( oldGen );
			Directory.CreateDirectory( currentGen );
			Directory.CreateDirectory( activeGen );
			File.WriteAllText( Path.Combine( oldGen, "aggregate_0.vmdl_c" ), "old" );
			File.WriteAllText( Path.Combine( currentGen, "aggregate_0.vmdl_c" ), "current" );
			File.WriteAllText( Path.Combine( activeGen, "aggregate_0.vmdl_c" ), "active" );
			File.WriteAllText( Path.Combine( compiled, "stray.tmp" ), "stray" );

			// The mount is a read-only aggregate view, so mutating it always throws.
			Assert.ThrowsException<IOException>( () => Sandbox.FileSystem.Mounted.DeleteDirectory( "scenes/menu_scene_data/compiled/f1e54c1f53ee45719b9ef219e443373f", true ) );

			var current = "scenes/menu_scene_data/compiled/9b8a7c6d5e4f3a2b1c0d9e8f7a6b5c4d";
			var active = new HashSet<string>( new[] { "scenes/menu_scene_data/compiled/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" }, StringComparer.OrdinalIgnoreCase );
			SceneCompiler.CleanupPreviousGenerations( current, new object(), active );

			Assert.IsFalse( Directory.Exists( oldGen ) );
			Assert.IsTrue( Directory.Exists( currentGen ) );
			Assert.AreEqual( "current", File.ReadAllText( Path.Combine( currentGen, "aggregate_0.vmdl_c" ) ) );
			Assert.IsTrue( Directory.Exists( activeGen ) );
			Assert.IsFalse( File.Exists( Path.Combine( compiled, "stray.tmp" ) ) );
		} );
	}

	[TestMethod]
	public void CleanupPreviousGenerations_ResolvesMixedCaseParentsOnDisk()
	{
		WithMountedProject( root =>
		{
			var compiled = Path.Combine( root, "Scenes", "MeNu_ScEnE_Data", "compiled" );
			var oldGen = Path.Combine( compiled, "f1e54c1f53ee45719b9ef219e443373f" );
			var currentGen = Path.Combine( compiled, "9b8a7c6d5e4f3a2b1c0d9e8f7a6b5c4d" );
			Directory.CreateDirectory( oldGen );
			Directory.CreateDirectory( currentGen );
			File.WriteAllText( Path.Combine( oldGen, "aggregate_0.vmdl_c" ), "old" );
			File.WriteAllText( Path.Combine( currentGen, "aggregate_0.vmdl_c" ), "current" );

			var current = "scenes/menu_scene_data/compiled/9b8a7c6d5e4f3a2b1c0d9e8f7a6b5c4d";
			SceneCompiler.CleanupPreviousGenerations( current, new object(), new HashSet<string>( StringComparer.OrdinalIgnoreCase ) );

			Assert.IsFalse( Directory.Exists( oldGen ) );
			Assert.AreEqual( "current", File.ReadAllText( Path.Combine( currentGen, "aggregate_0.vmdl_c" ) ) );
		} );
	}

	[TestMethod]
	public void CleanupGeneration_DeletesExistingGeneration()
	{
		WithMountedProject( root =>
		{
			var gen = Path.Combine( root, "scenes", "menu_scene_data", "compiled", "f1e54c1f53ee45719b9ef219e443373f" );
			Directory.CreateDirectory( gen );
			File.WriteAllText( Path.Combine( gen, "aggregate_0.vmdl_c" ), "old" );

			SceneCompiler.CleanupGeneration( "scenes/menu_scene_data/compiled/f1e54c1f53ee45719b9ef219e443373f" );

			Assert.IsFalse( Directory.Exists( gen ) );
		} );
	}

	[TestMethod]
	public void CleanupGeneration_MissingPath_IsNoOp()
	{
		WithMountedProject( _ =>
		{
			SceneCompiler.CleanupGeneration( "scenes/menu_scene_data/compiled/doesnotexist" );
		} );
	}
}
