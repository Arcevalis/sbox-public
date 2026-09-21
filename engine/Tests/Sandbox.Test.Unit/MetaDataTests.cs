using Editor;
using System;
using System.IO;

[TestClass]
public class MetaDataTests
{
	static string NewTempPath() => Path.Combine( Path.GetTempPath(), $"metadata_test_{Guid.NewGuid():N}.meta" );

	static readonly DateTime PinnedTime = new DateTime( 2020, 1, 1, 0, 0, 0, DateTimeKind.Utc );

	[TestMethod]
	public void SetSameValueTwiceSkipsSecondWrite()
	{
		var path = NewTempPath();
		try
		{
			var meta = new MetaData( path );
			meta.Set( "guid", Guid.NewGuid() );

			// Pin mtime, then Set the identical value again: no write expected.
			File.SetLastWriteTimeUtc( path, PinnedTime );
			var before = File.ReadAllBytes( path );

			meta.Set( "guid", meta.Get<Guid>( "guid" ) );

			Assert.AreEqual( PinnedTime, File.GetLastWriteTimeUtc( path ) );
			CollectionAssert.AreEqual( before, File.ReadAllBytes( path ) );
		}
		finally
		{
			File.Delete( path );
		}
	}

	[TestMethod]
	public void SetNewValueStillWrites()
	{
		var path = NewTempPath();
		try
		{
			var meta = new MetaData( path );
			meta.Set( "guid", Guid.NewGuid() );

			File.SetLastWriteTimeUtc( path, PinnedTime );
			var before = File.ReadAllBytes( path );

			meta.Set( "guid", Guid.NewGuid() );
			var after = File.ReadAllBytes( path );

			CollectionAssert.AreNotEqual( before, after );
			Assert.IsTrue( File.GetLastWriteTimeUtc( path ) > PinnedTime );
		}
		finally
		{
			File.Delete( path );
		}
	}

	[TestMethod]
	public void SetReorderedKeyConvergesAfterOneWrite()
	{
		var path = NewTempPath();
		try
		{
			var guid = Guid.NewGuid();
			// "guid" is not the last key, so the first Set moves it (one write).
			File.WriteAllText( path, $"{{\n  \"guid\": \"{guid:D}\",\n  \"other\": 1\n}}" );

			var meta = new MetaData( path );
			meta.Set( "guid", guid );

			// The reorder is done: a second identical Set must be a no-op.
			File.SetLastWriteTimeUtc( path, PinnedTime );
			var before = File.ReadAllBytes( path );

			meta.Set( "guid", guid );

			Assert.AreEqual( PinnedTime, File.GetLastWriteTimeUtc( path ) );
			CollectionAssert.AreEqual( before, File.ReadAllBytes( path ) );
		}
		finally
		{
			File.Delete( path );
		}
	}
}
