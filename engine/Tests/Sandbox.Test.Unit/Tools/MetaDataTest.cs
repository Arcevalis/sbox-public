using System;
using System.IO;
using Editor;

namespace ToolsTests;

[TestClass]
public class MetaDataTest
{
	/// <summary>
	/// Reads are cached against the file's write time and length, but never hide a write: a value
	/// set through the same instance is what the next lookup returns, and a file changed on disk
	/// is picked up once its state no longer matches the cached parse.
	/// </summary>
	[TestMethod]
	public void CachedParseSeesWritesAndExternalEdits()
	{
		var path = Path.Combine( Path.GetTempPath(), $"{Guid.NewGuid()}.meta" );
		var meta = new MetaData( path );

		try
		{
			meta.Set( "value", 1 );
			Assert.AreEqual( 1, meta.GetInt( "value" ) );

			// Same instance: the write must invalidate the parse it cached above
			meta.Set( "value", 2 );
			Assert.AreEqual( 2, meta.GetInt( "value" ), "A write through the same instance must not be hidden by the cached read" );

			// Someone else rewrites the file; the cached parse must not outlive it
			File.WriteAllText( path, """{ "value": 4321 }""" );
			File.SetLastWriteTimeUtc( path, DateTime.UtcNow.AddSeconds( 1 ) );

			Assert.AreEqual( 4321, meta.GetInt( "value" ), "An edit made outside this instance must be picked up" );

			// A file that goes away reads as absent, even though a parse is still cached
			File.Delete( path );
			Assert.AreEqual( 0, meta.GetInt( "value" ), "A deleted file must not serve the cached parse" );
		}
		finally
		{
			if ( File.Exists( path ) )
				File.Delete( path );
		}
	}
}
