using Sandbox;
using Sandbox.Resources;
using System;
using System.Collections.Generic;
using System.IO;

[TestClass]
public class ResourceCompileCaseIdentityTests
{
	sealed class StubContext( string absolutePath ) : ResourceCompileContext
	{
		public List<(string Path, bool Optional)> References { get; } = new();
		public override string AbsolutePath => absolutePath;
		public override string RelativePath => Path.GetFileName( absolutePath );
		public override int ResourceVersion { get; set; }
		internal override int WriteBlock( string blockName, IntPtr data, int count ) => throw new NotSupportedException();
		public override void AddRuntimeReference( string path ) => throw new NotSupportedException();
		public override void AddCompileReference( string path, bool optional = false ) => References.Add( (path, optional) );
		public override void AddGameFileReference( string path ) => throw new NotSupportedException();
		public override void AddCompiledReference( string path ) => throw new NotSupportedException();
		public override bool Describing => false;
		internal override void Fail( string error ) => throw new NotSupportedException();
		public override Child CreateChild( string absolutePath ) => throw new NotSupportedException();
		public override string ScanJson( string json ) => throw new NotSupportedException();
		public override byte[] ReadSource() => throw new NotSupportedException();
	}

	sealed class TestFiles : IDisposable
	{
		readonly string root = Path.Combine( Path.GetTempPath(), $"sbox-compile-case-{Guid.NewGuid():N}" );
		public string Source => Path.Combine( root, "Sounds", "Voice.WAV" );
		public string Canonical => Path.Combine( root, "Sounds", "voice.wav.meta" );
		public string Legacy => Source + ".meta";

		public TestFiles()
		{
			Directory.CreateDirectory( Path.GetDirectoryName( Source ) );
			File.WriteAllText( Source, "source" );
		}

		public void Dispose() => Directory.Delete( root, true );
	}

	[TestMethod]
	public void ReadMeta_MixedCaseContextReadsCanonicalMetadata()
	{
		using var files = new TestFiles();
		File.WriteAllText( files.Canonical, "{\"value\":42}" );
		var context = new StubContext( files.Source );

		Assert.AreEqual( 42, context.ReadMeta<int>( "value" ) );
		Assert.AreEqual( 1, context.References.Count );
		Assert.AreEqual( (files.Canonical, true), context.References[0] );
	}

	[TestMethod]
	public void ReadMeta_MissingMetadataStillTracksCanonicalOptionalDependency()
	{
		using var files = new TestFiles();
		var context = new StubContext( files.Source );

		Assert.IsNull( context.ReadMeta() );
		Assert.AreEqual( 1, context.References.Count );
		Assert.AreEqual( (files.Canonical, true), context.References[0] );

		Assert.IsNull( context.ReadMeta() );
		Assert.AreEqual( 1, context.References.Count );
	}

	[TestMethod]
	public void ReadMeta_LegacyFallbackTracksCanonicalAndActualDependencies()
	{
		if ( !OperatingSystem.IsLinux() )
			return;

		using var files = new TestFiles();
		File.WriteAllText( files.Legacy, "{\"value\":7}" );
		var context = new StubContext( files.Source.LowercaseFileName() );

		Assert.AreEqual( 7, context.ReadMeta<int>( "value" ) );
		Assert.AreEqual( 2, context.References.Count );
		Assert.AreEqual( (files.Canonical, true), context.References[0] );
		Assert.AreEqual( (files.Legacy, false), context.References[1] );
	}

	[TestMethod]
	public void ReadMeta_CanonicalWinsAfterLegacyResolutionWasWarmed()
	{
		if ( !OperatingSystem.IsLinux() )
			return;

		using var files = new TestFiles();
		File.WriteAllText( files.Legacy, "{\"value\":7}" );
		var legacyContext = new StubContext( files.Source.LowercaseFileName() );
		Assert.AreEqual( 7, legacyContext.ReadMeta<int>( "value" ) );

		File.WriteAllText( files.Canonical, "{\"value\":42}" );
		var context = new StubContext( files.Source );

		Assert.AreEqual( 42, context.ReadMeta<int>( "value" ) );
		Assert.AreEqual( 1, context.References.Count );
		Assert.AreEqual( (files.Canonical, true), context.References[0] );
		Assert.AreEqual( "{\"value\":7}", File.ReadAllText( files.Legacy ) );
	}
}
