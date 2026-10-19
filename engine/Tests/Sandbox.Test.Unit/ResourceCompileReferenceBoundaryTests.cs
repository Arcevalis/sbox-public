using Editor;
using System;
using System.IO;

[TestClass]
public class ResourceCompileReferenceBoundaryTests
{
	sealed class TestFiles : IDisposable
	{
		readonly string root = Path.Combine( Path.GetTempPath(), $"sbox-compile-reference-{Guid.NewGuid():N}" );
		public string Source => Path.Combine( root, "Textures", "Diffuse.PNG" );

		public TestFiles()
		{
			Directory.CreateDirectory( Path.GetDirectoryName( Source ) );
			File.WriteAllText( Source, "source" );
		}

		public void Dispose() => Directory.Delete( root, true );
	}

	[DataTestMethod]
	[DataRow( "Textures/Diffuse.PNG", "textures/diffuse.png" )]
	[DataRow( "/Textures\\Diffuse.PNG_C", "textures/diffuse.png" )]
	[DataRow( "mount://GAME/Maps/Example.SCENE_C", "mount://game/maps/example.scene" )]
	public void RuntimeReferencesUseCanonicalIdentity( string path, string expected )
	{
		Assert.AreEqual( expected, ResourceCompileContextImp.GetRuntimeReferencePath( path ) );
	}

	[TestMethod]
	public void LogicalCompileReferenceUsesPhysicalSourceSpelling()
	{
		using var files = new TestFiles();

		var paths = ResourceCompileContextImp.ResolveCompileReferencePaths( "textures/diffuse.png", files.Source, false );

		CollectionAssert.AreEqual( new[] { files.Source }, paths );
	}

	[TestMethod]
	public void LogicalCompileReferenceUsesSourceExtensionRatherThanRuntimeExtension()
	{
		using var files = new TestFiles();
		var source = Path.ChangeExtension( files.Source, "WAV" );
		File.WriteAllText( source, "sound source" );

		var paths = ResourceCompileContextImp.ResolveCompileReferencePaths( "sounds/voice.vsnd", source, false );

		CollectionAssert.AreEqual( new[] { source }, paths );
	}

	[TestMethod]
	public void AbsoluteCompileReferenceDoesNotRedirectToAnotherSource()
	{
		using var files = new TestFiles();
		var sidecar = files.Source + "_d";
		File.WriteAllText( sidecar, "blob" );

		var paths = ResourceCompileContextImp.ResolveCompileReferencePaths( sidecar, files.Source, false );

		CollectionAssert.AreEqual( new[] { sidecar }, paths );
	}

	[TestMethod]
	public void RequiredAbsoluteCompileReferenceResolvesActualDiskCasing()
	{
		if ( !OperatingSystem.IsLinux() )
			return;

		using var files = new TestFiles();

		var paths = ResourceCompileContextImp.ResolveCompileReferencePaths( files.Source.ToLowerInvariant(), null, false );

		CollectionAssert.AreEqual( new[] { files.Source }, paths );
	}

	[TestMethod]
	public void OptionalCompileReferenceKeepsCanonicalWatchAlongsideLegacyFile()
	{
		if ( !OperatingSystem.IsLinux() )
			return;

		using var files = new TestFiles();
		var legacy = files.Source + ".meta";
		var canonical = Path.Combine( Path.GetDirectoryName( files.Source ), "diffuse.png.meta" );
		File.WriteAllText( legacy, "metadata" );

		var paths = ResourceCompileContextImp.ResolveCompileReferencePaths( canonical, null, true );

		CollectionAssert.AreEqual( new[] { canonical, legacy }, paths );
	}

	[TestMethod]
	public void MissingOptionalCompileReferenceRetainsRequestedFilename()
	{
		using var files = new TestFiles();
		var missing = files.Source + ".not-created";

		var paths = ResourceCompileContextImp.ResolveCompileReferencePaths( missing, null, true );

		CollectionAssert.AreEqual( new[] { missing }, paths );
	}

	[TestMethod]
	public void ExactCanonicalOptionalFileWinsAfterLegacyLookup()
	{
		if ( !OperatingSystem.IsLinux() )
			return;

		using var files = new TestFiles();
		var legacy = files.Source + ".meta";
		var canonical = Path.Combine( Path.GetDirectoryName( files.Source ), "diffuse.png.meta" );
		File.WriteAllText( legacy, "legacy" );
		ResourceCompileContextImp.ResolveCompileReferencePaths( canonical, null, true );
		File.WriteAllText( canonical, "canonical" );

		var paths = ResourceCompileContextImp.ResolveCompileReferencePaths( canonical, null, true );

		CollectionAssert.AreEqual( new[] { canonical }, paths );
	}

	[DataTestMethod]
	[DataRow( "Textures/Missing.PNG" )]
	[DataRow( "Textures/Missing.PNG_C" )]
	[DataRow( "Textures\\Missing.PNG" )]
	public void UnresolvedRelativeCompileReferenceIsNotRewrittenAsResourceIdentity( string path )
	{
		var paths = ResourceCompileContextImp.ResolveCompileReferencePaths( path, null, false );

		CollectionAssert.AreEqual( new[] { path }, paths );
	}
}
