using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ContentBuild;

/// <summary>
/// Editor startup builds every mounted root with the content builder before the project opens, so nothing it
/// built or kept compiles on demand when loaded afterwards (an asset that failed still can, as before).
/// Opt-in, because it opens real editors through sbox-cli on a throwaway project: set SBOX_EDITOR_TESTS=1.
/// Engine roots are built too, so run SboxBuild build-content first or the first open compiles what engine
/// content needs.
/// </summary>
[TestClass]
public class EditorStartupContentBuild
{
	const string Blood = "core/decals/blood/blood_splatter_01.decal";
	const string BloodColor = "decals/blood/decal_blood_splatter_01_color.png";

	[TestMethod, TestCategory( "Slow" )]
	public void StartupBuildsWhatTheProjectLoads()
	{
		if ( Environment.GetEnvironmentVariable( "SBOX_EDITOR_TESTS" ) is null )
			Assert.Inconclusive( "Opens an editor: set SBOX_EDITOR_TESTS=1 to run" );

		using var project = new EditorProject();
		project.Copy( Sources.ImageA, "materials/shared.tga" );
		project.Write( "materials/a.vmat", Sources.Material( "TextureColor", "materials/shared.tga" ) );
		project.Write( "models/box.vmdl", Sources.Box( "materials/a.vmat" ) );
		project.Write( "textures/broken.vtex", Sources.Broken );
		project.Copy( Sources.ImageB, "textures/t.tga" );
		project.Write( "textures/t.vtex", Sources.Texture( "textures/t.tga" ) );

		// A library, mounted before the project, whose material a project model uses
		project.Copy( Sources.ImageA, "materials/libm_color.tga", EditorProject.Library );
		project.Write( "materials/libm.vmat", Sources.Material( "TextureColor", "materials/libm_color.tga" ), EditorProject.Library );
		project.Write( "models/libbox.vmdl", Sources.Box( "materials/libm.vmat" ) );

		// Generated textures: a standalone generator copying another texture's compiled pixels, a copy of a
		// core decal (so its generated textures are shared with core's), a decal whose color embeds the same
		// texture, and one whose image doesn't exist yet
		project.Write( "textures/copy.vtex", """{ "$compiler": "texture", "$source": "imagefile", "data": { "FilePath": "textures/t.vtex", "CacheToDisk": true } }""" );
		foreach ( var image in new[] { "color", "normal", "RMO", "height" } )
			project.Copy( $"core/decals/blood/decal_blood_splatter_01_{image}.png", $"decals/blood/decal_blood_splatter_01_{image}.png" );
		project.Copy( Blood, "decals/blood.decal" );
		var decal = File.ReadAllText( Path.Combine( ContentFixture.Engine, Blood ) );
		project.Write( "decals/copy.decal", decal.Replace( BloodColor, "textures/t.vtex" ) );
		project.Write( "decals/later.decal", decal.Replace( BloodColor, "decals/later.png" ) );

		var cold = project.Open();
		cold.Assert( compiles: 8, failed: 1, library: 1 );
		cold.AssertCopiedPixels();

		// Unchanged: only the broken texture is tried again, and fails describing
		project.Open().Assert( compiles: 0, failed: 1 );

		// The copied texture's source: the texture, then both copies of its pixels
		project.Copy( Sources.ImageA, "textures/t.tga" );
		var edited = project.Open();
		edited.Assert( compiles: 3, failed: 1 );
		edited.AssertCopiedPixels();

		// The library material's shader changes its signature: the project model follows in the same startup
		project.Write( "materials/libm.vmat", Sources.Material( "shaders/unlit.shader", ["TextureColor", "materials/libm_color.tga"] ), EditorProject.Library );
		project.Open().Assert( compiles: 1, failed: 1, library: 1 );

		// The missing image turns up
		project.Copy( $"core/{BloodColor}", "decals/later.png" );
		project.Open().Assert( compiles: 1, failed: 1 );

		// The project's copy goes; core's decal still has the generated textures they shared
		File.Delete( Path.Combine( project.Assets, "decals/blood.decal" ) );
		var removed = project.Open();
		removed.Assert( compiles: 0, failed: 1, removed: 1 );
		StringAssert.Contains( removed.Loads, "core decal True", removed.ToString() );
		project.Open().Assert( compiles: 0, failed: 1 );

		// Another build holds core while this editor starts: it waits for it rather than skip the build
		var lockFile = Path.Combine( ContentFixture.Engine, ".source2", "contentbuild", "core", "lock" );
		using ( var held = new FileStream( lockFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None ) )
		{
			var release = Task.Delay( TimeSpan.FromSeconds( 70 ) ).ContinueWith( _ => held.Dispose() );
			var waited = project.Open();
			release.Wait();
			waited.Assert( compiles: 0, failed: 1 );
		}

		// A sound's .meta is an input of its compile even before it exists: writing one while the editor is open
		// rebuilds the sound, after a startup that left the .meta files registration writes current
		project.Copy( "core/sounds/ambience/stings/sting-frog-1.wav", "sounds/frog.wav" );
		var meta = Path.Combine( project.Assets, "sounds", "frog.wav.meta" );
		var live = project.Open( $$"""
			System.IO.File.WriteAllText( @"{{meta}}", "{ \"visemes\": [] }" );
			await Task.Delay( 5000 );
			return "written";
			""" );
		live.Assert( compiles: 1, failed: 1 );
		StringAssert.Contains( live.Live, "Content build: 1 compiles", live.ToString() );
	}

	/// <summary>
	/// A texture copying another's compiled pixels, both loaded in an open editor: editing the source rebuilds the
	/// copy from the new pixels, not from the old ones still loaded.
	/// </summary>
	[TestMethod, TestCategory( "Slow" )]
	public void LiveEditRebuildsACopyFromTheNewPixels()
	{
		if ( Environment.GetEnvironmentVariable( "SBOX_EDITOR_TESTS" ) is null )
			Assert.Inconclusive( "Opens an editor: set SBOX_EDITOR_TESTS=1 to run" );

		using var project = new EditorProject();
		// Images of the same size: a loaded texture that changes size reloads as a fallback, which isn't this
		project.Copy( Sources.ImageA, "textures/t.tga" );
		project.Write( "textures/t.vtex", Sources.Texture( "textures/t.tga" ) );
		project.Write( "textures/copy.vtex", """{ "$compiler": "texture", "$source": "imagefile", "data": { "FilePath": "textures/t.vtex", "CacheToDisk": true } }""" );
		// Edited while the project's root is locked, so the asset system can't compile the loaded texture by itself
		// first: one build compiles both, the copy right after the texture it reads
		var image = Path.Combine( ContentFixture.Engine, Sources.ImageC );
		var source = Path.Combine( project.Assets, "textures", "t.tga" );
		var lockFile = Path.Combine( project.Dir, ".sbox", "build", "lock" );
		var live = project.Open( $$"""
			// Both loaded, as a scene using them would have them
			var copy = Texture.Load( "textures/copy.vtex" );
			var old = Texture.Load( "textures/t.vtex" ).GetPixels();
			using ( var held = new System.IO.FileStream( @"{{lockFile}}", System.IO.FileMode.OpenOrCreate, System.IO.FileAccess.ReadWrite, System.IO.FileShare.None ) )
			{
				System.IO.File.Copy( @"{{image}}", @"{{source}}", true );
				await Task.Delay( 3000 );
			}
			// The copy reloads some frames after it's built; one built from the old pixels never matches
			Texture now = null, copied = null;
			bool Done()
			{
				now = Texture.Load( "textures/t.vtex" );
				copied = Texture.Load( "textures/copy.vtex" );
				return !now.GetPixels().SequenceEqual( old ) && copied.Width == now.Width && copied.GetPixels().SequenceEqual( now.GetPixels() );
			}
			for ( var waited = 0; waited < 20000 && !Done(); waited += 500 )
				await Task.Delay( 500 );
			return $"source changed {!now.GetPixels().SequenceEqual( old )} copy current {copied.Width == now.Width && copied.GetPixels().SequenceEqual( now.GetPixels() )} ({copied.Width}x{copied.Height} copy, {now.Width}x{now.Height} source)";
			""" );

		StringAssert.Contains( live.Live, "Content build: 2 compiles", live.ToString() );
		StringAssert.Contains( live.Live, "source changed True", live.ToString() );
		StringAssert.Contains( live.Live, "copy current True", live.ToString() );
	}

	sealed class EditorProject : IDisposable
	{
		public readonly string Dir = Directory.CreateTempSubdirectory( "sbox-editor-startup" ).FullName;
		public const string Ident = "cb_editor_startup";

		public const string Library = "cb_editor_startup_lib";

		public string Assets => Path.Combine( Dir, "Assets" );

		public EditorProject()
		{
			Directory.CreateDirectory( Assets );
			File.WriteAllText( Path.Combine( Dir, $"{Ident}.sbproj" ), $$"""
				{ "Title": "Editor Startup Test", "Type": "game", "Org": "local", "Ident": "{{Ident}}", "Schema": 1, "AI": { "EnableMcpCommunication": true } }
				""" );

			var library = Path.Combine( Dir, "Libraries", Library );
			Directory.CreateDirectory( Path.Combine( library, "Assets" ) );
			File.WriteAllText( Path.Combine( library, $"{Library}.sbproj" ), $$"""
				{ "Title": "Editor Startup Test Library", "Type": "library", "Org": "local", "Ident": "{{Library}}", "Schema": 1 }
				""" );
		}

		string Root( string library ) => library is null ? Assets : Path.Combine( Dir, "Libraries", library, "Assets" );

		public void Dispose()
		{
			try { Directory.Delete( Dir, true ); } catch ( IOException ) { } catch ( UnauthorizedAccessException ) { }
		}

		public void Write( string rel, string text, string library = null )
		{
			Directory.CreateDirectory( Path.GetDirectoryName( Path.Combine( Root( library ), rel ) ) );
			File.WriteAllText( Path.Combine( Root( library ), rel ), text.ReplaceLineEndings( "\r\n" ) );
		}

		public void Copy( string engineRel, string rel, string library = null )
		{
			Directory.CreateDirectory( Path.GetDirectoryName( Path.Combine( Root( library ), rel ) ) );
			File.Copy( Path.Combine( ContentFixture.Engine, engineRel ), Path.Combine( Root( library ), rel ), true );
		}

		/// <summary>
		/// Opens the project in an editor of its own, waits for startup, reads what happened and closes it.
		/// </summary>
		public Startup Open( string live = null )
		{
			using var editor = new McpSession( Path.Combine( ContentFixture.Engine, "bin", "managed", "sbox-cli.exe" ), Path.Combine( Dir, $"{Ident}.sbproj" ) );

			var summary = editor.Tool( "read_console", new { limit = 50, minimumLevel = "Trace", filter = "Content " } );

			// Load what was built, the way a session would, then see what the asset system still thinks is stale
			var loads = editor.Tool( "run_csharp", new
			{
				code = """
					var model = Model.Load( "models/box.vmdl" );
					var material = Material.Load( "materials/a.vmat" );
					var libbox = Model.Load( "models/libbox.vmdl" );
					var copy = ResourceLibrary.Get<DecalDefinition>( "decals/copy.decal" );
					var core = ResourceLibrary.Get<DecalDefinition>( "decals/blood/blood_splatter_01.decal" );
					var source = Texture.Load( "textures/t.vtex" );
					var standalone = Texture.Load( "textures/copy.vtex" );
					await Task.Delay( 1000 );
					bool Same( Texture t ) => t is not null && t.Width == source.Width && t.GetPixels().SequenceEqual( source.GetPixels() );
					var copied = Same( copy?.ColorTexture ) && Same( standalone );
					var stale = Editor.AssetSystem.All.Where( x => x.Path.StartsWith( "models/" ) || x.Path.StartsWith( "materials/" ) || x.Path.StartsWith( "decals/" ) )
						.Where( x => !x.IsTrivialChild && x.CanRecompile && !x.IsCompiledAndUpToDate ).Select( x => x.Path );
					return $"model error {model?.IsError} material valid {material?.IsValid()} library model error {libbox?.IsError} copied pixels {copied} core decal {core?.ColorTexture?.IsValid() == true} stale [{string.Join( ", ", stale )}]";
					"""
			} );
			var onDemand = editor.Tool( "read_console", new { limit = 50, minimumLevel = "Trace", filter = "On-demand recompile" } );

			if ( live is null ) return new Startup( summary, loads, onDemand, null );

			var ran = editor.Tool( "run_csharp", new { code = live } );
			return new Startup( summary, loads, onDemand, $"{ran}\n{editor.Tool( "read_console", new { limit = 50, minimumLevel = "Trace", filter = "Content build: " } )}" );
		}
	}

	sealed record Startup( string Summary, string Loads, string OnDemand, string Live )
	{
		/// <summary>
		/// What the project's and the library's roots compiled, failed and removed at startup, with no describe
		/// misses, nothing compiled on demand afterwards and nothing left stale.
		/// </summary>
		public void Assert( int compiles, int failed, int removed = 0, int library = 0 )
		{
			var project = Regex.Match( Summary, $@"Content local\.{EditorProject.Ident}: (\d+) compiles, (\d+) failed, \d+ blocked, \d+ adopted, \d+ described, (\d+) removed" );
			var lib = Regex.Match( Summary, $@"Content local\.{EditorProject.Library}: (\d+) compiles" );
			var misses = Regex.Match( Summary, @"Content build: (\d+) describe misses" );
			Microsoft.VisualStudio.TestTools.UnitTesting.Assert.IsTrue( project.Success && lib.Success && misses.Success, $"No content build at startup:\n{this}" );
			Microsoft.VisualStudio.TestTools.UnitTesting.Assert.AreEqual( (compiles, failed, removed, library, 0), (Count( project, 1 ), Count( project, 2 ), Count( project, 3 ), Count( lib, 1 ), Count( misses, 1 )), $"compiles, failed, removed, library compiles, misses:\n{this}" );

			StringAssert.Contains( Loads, "model error False material valid True library model error False", ToString() );
			StringAssert.Contains( Loads, "stale []", ToString() );
			StringAssert.Contains( OnDemand, "No matching console output", ToString() );
		}

		public void AssertCopiedPixels() => StringAssert.Contains( Loads, "copied pixels True", ToString() );

		static int Count( Match match, int group ) => int.Parse( match.Groups[group].Value );

		public override string ToString() => $"{Summary}\n{Loads}\n{OnDemand}\n{Live}";
	}

	/// <summary>
	/// sbox-cli --project as an MCP server over stdio: it launches an editor it owns and closes it with stdin.
	/// </summary>
	sealed class McpSession : IDisposable
	{
		readonly Process process;
		int id;

		public McpSession( string cli, string project )
		{
			var info = new ProcessStartInfo( cli ) { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
			foreach ( var arg in new[] { "--project", project, "--startup-timeout", "3000", "--request-timeout", "600" } ) info.ArgumentList.Add( arg );
			process = Process.Start( info );

			Call( "initialize", new { protocolVersion = "2025-06-18", capabilities = new { }, clientInfo = new { name = "contentbuild-editor-startup", version = "1" } } );
			Send( new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "notifications/initialized" } );
		}

		public string Tool( string name, object arguments )
		{
			var result = Call( "tools/call", new { name, arguments } );
			var text = new List<string>();
			foreach ( var item in result["result"]?["content"]?.AsArray() ?? [] ) text.Add( item?["text"]?.GetValue<string>() ?? "" );
			return string.Join( "\n", text );
		}

		JsonNode Call( string method, object parameters )
		{
			var request = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = ++id, ["method"] = method, ["params"] = JsonSerializer.SerializeToNode( parameters ) };
			Send( request );
			while ( true )
			{
				var line = process.StandardOutput.ReadLine() ?? throw new InvalidOperationException( $"sbox-cli closed: {process.StandardError.ReadToEnd()}" );
				var reply = JsonNode.Parse( line );
				if ( reply?["id"]?.GetValue<int>() == id ) return reply;
			}
		}

		void Send( JsonNode message )
		{
			process.StandardInput.WriteLine( message.ToJsonString() );
			process.StandardInput.Flush();
		}

		public void Dispose()
		{
			process.StandardInput.Close();
			if ( !process.WaitForExit( TimeSpan.FromMinutes( 2 ) ) ) process.Kill( true );
			process.Dispose();
		}
	}
}
