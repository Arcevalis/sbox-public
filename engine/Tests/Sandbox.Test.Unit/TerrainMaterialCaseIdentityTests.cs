using Sandbox;
using Sandbox.Resources;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;

[TestClass]
public class TerrainMaterialCaseIdentityTests
{
	sealed class TestCompiler : TerrainMaterialCompiler
	{
		public Task<bool> Run() => Compile();
	}

	sealed class RecordingStream : ResourceCompileContext.DataStream
	{
		public List<byte> Bytes { get; } = new();
		public override void Write( byte[] bytes ) => Bytes.AddRange( bytes );
	}

	sealed class RecordingChild : ResourceCompileContext.Child
	{
		public string Input { get; private set; }
		public bool WasCompiled { get; private set; }
		public override bool Compile() => WasCompiled = true;
		public override void SetInputData( string data ) => Input = data;
	}

	sealed class StubContext : ResourceCompileContext
	{
		readonly string input;
		public List<(string Path, RecordingChild Child)> Children { get; } = new();
		public RecordingStream Output { get; } = new();
		public int SourceReads { get; private set; }
		public override string AbsolutePath { get; }
		public override string RelativePath => Path.GetFileName( AbsolutePath );
		public override int ResourceVersion { get; set; }

		public StubContext( string absolutePath, string input )
		{
			AbsolutePath = absolutePath;
			this.input = input;
			Data = Output;
		}

		internal override int WriteBlock( string blockName, IntPtr data, int count ) => throw new NotSupportedException();
		public override void AddRuntimeReference( string path ) => throw new NotSupportedException();
		public override void AddCompileReference( string path, bool optional = false ) => throw new NotSupportedException();
		public override void AddGameFileReference( string path ) => throw new NotSupportedException();
		public override Child CreateChild( string absolutePath )
		{
			var child = new RecordingChild();
			Children.Add( (absolutePath, child) );
			return child;
		}

		public override string ScanJson( string json ) => throw new NotSupportedException();
		public override byte[] ReadSource()
		{
			SourceReads++;
			return Encoding.UTF8.GetBytes( input );
		}
	}

	[DataTestMethod]
	[DataRow( false )]
	[DataRow( true )]
	public async Task Compile_UsesContextInputAndLowercaseChildIdentity( bool lowercaseContextIdentity )
	{
		var root = Path.Combine( Path.GetTempPath(), $"sbox-terrain-case-{Guid.NewGuid():N}" );
		var directory = Path.Combine( root, "Materials" );
		Directory.CreateDirectory( directory );

		try
		{
			var source = Path.Combine( directory, "RoCk.TMAT" );
			File.WriteAllText( source, "invalid disk input must not override context input" );
			var json = "{\"AlbedoImage\":\"Textures/MiXeD.PNG\"}";
			var context = new StubContext( lowercaseContextIdentity ? source.LowercaseFileName() : source, json );
			var compiler = new TestCompiler();
			compiler.SetContext( context );

			Assert.IsTrue( await compiler.Run() );
			Assert.AreEqual( 1, context.SourceReads );
			Assert.AreEqual( 2, context.Children.Count );
			Assert.AreEqual( Path.Combine( directory, "rock_tmat_bcr.generated.vtex" ), Path.GetFullPath( context.Children[0].Path ) );
			Assert.AreEqual( Path.Combine( directory, "rock_tmat_nho.generated.vtex" ), Path.GetFullPath( context.Children[1].Path ) );
			Assert.IsTrue( context.Children[0].Child.WasCompiled );
			Assert.IsTrue( context.Children[1].Child.WasCompiled );
			StringAssert.Contains( context.Children[0].Child.Input, "Textures/MiXeD.PNG" );
			Assert.AreEqual( json, Encoding.UTF8.GetString( context.Output.Bytes.ToArray() ).TrimEnd( '\0' ) );
			Assert.AreEqual( "invalid disk input must not override context input", File.ReadAllText( source ) );
		}
		finally
		{
			Directory.Delete( root, true );
		}
	}
}
