using Sandbox;
using Sandbox.Engine.Shaders;
using System;
using System.IO;

[TestClass]
public class ShaderCaseIdentityTests
{
	sealed class TestFiles : IDisposable
	{
		readonly string root = Path.Combine( Path.GetTempPath(), $"sbox-shader-case-{Guid.NewGuid():N}" );
		public string Source => Path.Combine( root, "Shaders", "MiXeD.SHADER" );
		public string Canonical => Source.LowercaseFileName() + "_c";
		public string Legacy => Source + "_c";

		public TestFiles()
		{
			Directory.CreateDirectory( Path.GetDirectoryName( Source ) );
			File.WriteAllText( Source, "physical source" );
		}

		public void Dispose() => Directory.Delete( root, true );
	}

	[TestMethod]
	public void SourceAndCompiledPaths_SeparatePhysicalSpellingFromIdentity()
	{
		using var files = new TestFiles();
		var shader = new ShaderSource { AbsolutePath = files.Source };

		Assert.AreEqual( files.Source, shader.AbsolutePath );
		Assert.AreEqual( Path.Combine( Path.GetDirectoryName( files.Source ), "mixed.shader" ), shader.IdentityPath );
		Assert.AreEqual( files.Canonical, shader.CompiledPath );
		Assert.AreEqual( "physical source", File.ReadAllText( shader.AbsolutePath ) );
	}

	[TestMethod]
	public void SourcePath_LowercaseNativePathResolvesPhysicalSpelling()
	{
		if ( !OperatingSystem.IsLinux() ) return;

		using var files = new TestFiles();
		var shader = new ShaderSource { AbsolutePath = files.Source.ToLowerInvariant() };

		Assert.AreEqual( files.Source, shader.AbsolutePath );
		Assert.AreEqual( files.Canonical, shader.CompiledPath );
	}

	[TestMethod]
	public void ExistingCompiledPath_PrefersNewCanonicalOutputAfterLegacyLookup()
	{
		if ( !OperatingSystem.IsLinux() ) return;

		using var files = new TestFiles();
		File.WriteAllText( files.Legacy, "old" );
		var shader = new ShaderSource { AbsolutePath = files.Source };
		Assert.AreEqual( files.Legacy, shader.ExistingCompiledPath );

		File.WriteAllText( files.Canonical, "new" );
		Assert.AreEqual( files.Canonical, shader.ExistingCompiledPath );
		Assert.AreEqual( "new", File.ReadAllText( shader.ExistingCompiledPath ) );
		Assert.AreEqual( "physical source", File.ReadAllText( shader.AbsolutePath ) );
	}
}
