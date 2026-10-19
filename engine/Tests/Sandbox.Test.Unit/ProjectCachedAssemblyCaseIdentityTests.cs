using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Sandbox.DataModel;
using System;
using System.Globalization;
using System.IO;
using System.Reflection;

namespace Sandbox.Test;

[TestClass]
[DoNotParallelize]
public class ProjectCachedAssemblyCaseIdentityTests
{
	[TestMethod]
	[DataRow( "Feature.cs", true, false )]
	[DataRow( "Feature.CS", true, false )]
	[DataRow( "Feature.Cs", true, false )]
	[DataRow( "Feature.SERVER.CS", true, false )]
	[DataRow( "Feature.razor", true, false )]
	[DataRow( "Feature.RAZOR", true, false )]
	[DataRow( "Nested/Feature.RaZoR", true, false )]
	[DataRow( "Feature.CS", false, true )]
	[DataRow( "Feature.RAZOR", false, true )]
	[DataRow( "Feature.cs.txt", true, true )]
	[DataRow( "Feature.razor.txt", true, true )]
	public void LoadCachedAssembly_ChecksSourceFreshnessIndependentlyOfExtensionCase( string sourcePath, bool newer, bool expectedCached )
	{
		var root = Path.Combine( Path.GetTempPath(), $"project-cache-case-{Guid.NewGuid():N}" );
		var code = Path.Combine( root, "Code" );
		Directory.CreateDirectory( code );

		Project project = null;
		try
		{
			project = new Project( root )
			{
				Config = new ProjectConfig { Type = "library", Org = "local", Ident = "cache_case" }
			};
			project.Config.Init( project.ConfigFilePath );

			// A future compile time keeps creation time older, isolating last-write freshness.
			var compileTime = DateTime.UtcNow.AddHours( 1 );
			var source = Path.Combine( code, sourcePath );
			Directory.CreateDirectory( Path.GetDirectoryName( source ) );
			File.WriteAllText( source, "// Freshness fixture" );
			File.SetLastWriteTimeUtc( source, compileTime.AddMinutes( newer ? 1 : -1 ) );

			using var group = new CompileGroup( "cache-case" );
			var settings = new Compiler.Configuration();
			settings.Clean();
			using var compiler = group.CreateCompiler( project.Config.FullIdent, code, settings );

			var bin = Path.Combine( root, ".sbox", "bin" );
			Directory.CreateDirectory( bin );
			File.WriteAllBytes( Path.Combine( bin, $"{compiler.AssemblyName}.dll" ), CreateCachedAssembly( project, compileTime ) );

			var load = typeof( Project ).GetMethod( "LoadCachedAssembly", BindingFlags.Instance | BindingFlags.NonPublic );
			Assert.IsNotNull( load );
			var cached = (bool)load.Invoke( project, new object[] { compiler } );

			Assert.AreEqual( expectedCached, cached, $"Incorrect cache freshness for {sourcePath}" );
			Assert.AreEqual( expectedCached, compiler.BuildSuccess );
			Assert.AreEqual( expectedCached, project.AssemblyFileSystem.FileExists( $"/.bin/{compiler.AssemblyName}.dll" ) );
		}
		finally
		{
			project?.Dispose();
			Directory.Delete( root, true );
		}
	}

	static byte[] CreateCachedAssembly( Project project, DateTime compileTime )
	{
		var source = $$"""
			using System.Reflection;
			[assembly: AssemblyMetadata( "Ident", "{{project.Config.FullIdent}}" )]
			[assembly: AssemblyMetadata( "EngineVersion", "{{Sandbox.Engine.Protocol.Api}}" )]
			[assembly: AssemblyMetadata( "CompileTime", "{{compileTime.ToString( "O", CultureInfo.InvariantCulture )}}" )]
			""";
		var core = Path.GetDirectoryName( typeof( object ).Assembly.Location );
		var references = new[]
		{
			MetadataReference.CreateFromFile( typeof( object ).Assembly.Location ),
			MetadataReference.CreateFromFile( Path.Combine( core, "System.Runtime.dll" ) )
		};
		var compilation = CSharpCompilation.Create( "package.local.cache_case",
			new[] { CSharpSyntaxTree.ParseText( source ) }, references,
			new CSharpCompilationOptions( OutputKind.DynamicallyLinkedLibrary ) );
		using var stream = new MemoryStream();
		var emitted = compilation.Emit( stream );
		Assert.IsTrue( emitted.Success, string.Join( "\n", emitted.Diagnostics ) );
		return stream.ToArray();
	}
}
