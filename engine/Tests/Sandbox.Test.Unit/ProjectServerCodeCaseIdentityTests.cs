using Sandbox.DataModel;
using System;
using System.IO;
using System.Xml.Linq;
using ProjectSolutionGenerator = Sandbox.SolutionGenerator.Generator;

namespace Sandbox.Test;

[TestClass]
public class ProjectServerCodeCaseIdentityTests
{
	[TestMethod]
	[DataRow( "Feature.Server.cs" )]
	[DataRow( "Feature.server.cs" )]
	[DataRow( "Feature.SERVER.cs" )]
	[DataRow( "Feature.Server.CS" )]
	[DataRow( "Feature.sErVeR.cS" )]
	[DataRow( "Nested/Feature.SERVER.CS" )]
	public async Task GenerateProject_IncludesServerProjectForCaseInsensitiveSuffix( string sourcePath )
	{
		await WithProject( true, async ( project, root ) =>
		{
			var source = Path.Combine( project.GetCodePath(), sourcePath );
			Directory.CreateDirectory( Path.GetDirectoryName( source ) );
			File.WriteAllText( source, "public class ServerFeature {}" );

			GenerateSolution( project, root, await CreateGenerator( project ) );

			var serverPath = Path.Combine( project.GetCodePath(), "server_case.server.csproj" );
			Assert.IsTrue( File.Exists( serverPath ), $"No server project generated for {sourcePath}" );
			var server = XDocument.Load( serverPath );
			Assert.IsTrue( server.Descendants( "DefineConstants" ).Single().Value.Split( ';' ).Contains( "SERVER" ) );

			var client = XDocument.Load( Path.Combine( project.GetCodePath(), "server_case.csproj" ) );
			Assert.IsFalse( client.Descendants( "DefineConstants" ).Single().Value.Split( ';' ).Contains( "SERVER" ) );
			StringAssert.Contains( client.Descendants( "DefaultItemExcludes" ).Single().Value, "*.Server.cs" );

			var solution = XDocument.Load( Path.Combine( root, "project.slnx" ) );
			Assert.AreEqual( 2, solution.Descendants( "Project" ).Count() );
			Assert.IsTrue( solution.Descendants( "Project" ).Any( x => (string)x.Attribute( "Path" ) == "Code/server_case.server.csproj" ) );
		} );
	}

	[TestMethod]
	[DataRow( "Feature.cs" )]
	[DataRow( "Feature.CS" )]
	[DataRow( "Feature.NotServer.cs" )]
	[DataRow( "Feature.Server.cs.txt" )]
	[DataRow( "Feature.Server.cs/Readme.txt" )]
	[DataRow( "Nested/Feature.Server.razor" )]
	public async Task GenerateProject_DoesNotCreateServerProjectForOtherFiles( string sourcePath )
	{
		await WithProject( true, async ( project, root ) =>
		{
			var source = Path.Combine( project.GetCodePath(), sourcePath );
			Directory.CreateDirectory( Path.GetDirectoryName( source ) );
			File.WriteAllText( source, "// Not a server C# file" );

			GenerateSolution( project, root, await CreateGenerator( project ) );

			Assert.IsFalse( File.Exists( Path.Combine( project.GetCodePath(), "server_case.server.csproj" ) ) );
			var client = XDocument.Load( Path.Combine( project.GetCodePath(), "server_case.csproj" ) );
			Assert.IsFalse( client.Descendants( "DefaultItemExcludes" ).Any() );
			var solution = XDocument.Load( Path.Combine( root, "project.slnx" ) );
			Assert.AreEqual( 1, solution.Descendants( "Project" ).Count() );
		} );
	}

	[TestMethod]
	public async Task GenerateProject_WithoutCodeDirectoryDoesNotCreateProjects()
	{
		await WithProject( false, async ( project, root ) =>
		{
			GenerateSolution( project, root, await CreateGenerator( project ) );

			var solution = XDocument.Load( Path.Combine( root, "project.slnx" ) );
			Assert.IsFalse( solution.Descendants( "Project" ).Any() );
			Assert.IsFalse( Directory.Exists( Path.Combine( root, "Code" ) ) );
		} );
	}

	static async Task<ProjectSolutionGenerator> CreateGenerator( Project project )
	{
		var generator = new ProjectSolutionGenerator();
		await project.GenerateProject( generator );
		return generator;
	}

	static void GenerateSolution( Project project, string root, ProjectSolutionGenerator generator )
	{
		generator.Run( "sbox-dev", "bin/managed", "project.slnx", root, project.GetRootPath() );
	}

	static async Task WithProject( bool hasCode, Func<Project, string, Task> action )
	{
		var root = Path.Combine( Path.GetTempPath(), $"project-server-case-{Guid.NewGuid():N}" );
		Directory.CreateDirectory( root );
		if ( hasCode ) Directory.CreateDirectory( Path.Combine( root, "Code" ) );

		Project project = null;
		try
		{
			project = new Project( root )
			{
				Config = new ProjectConfig { Type = "library", Org = "local", Ident = "server_case" }
			};
			project.Config.Init( project.ConfigFilePath );
			await action( project, root );
		}
		finally
		{
			project?.Dispose();
			Directory.Delete( root, true );
		}
	}
}
