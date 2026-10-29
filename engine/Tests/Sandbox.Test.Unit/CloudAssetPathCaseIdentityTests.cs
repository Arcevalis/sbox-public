using Editor;
using System;
using System.IO;

[TestClass]
public class CloudAssetPathCaseIdentityTests
{
	[DataTestMethod]
	[DataRow( false )]
	[DataRow( true )]
	public void OrphanDeletionUsesPhysicalSpellingOrResolvesLegacyIdentity( bool lowercaseIdentity )
	{
		var root = Path.Combine( Path.GetTempPath(), $"sbox-cloud-case-{Guid.NewGuid():N}" );
		var directory = Path.Combine( root, "MiXeDProject", ".sbox", "cloud", "Models" );
		Directory.CreateDirectory( directory );
		try
		{
			var source = Path.Combine( directory, "Chair.VMDL" );
			File.WriteAllText( source, "orphan" );

			CloudAssetDirectory.DeleteOrphanedFile( lowercaseIdentity ? source.ToLowerInvariant() : source );

			Assert.IsFalse( File.Exists( source ) );
			Assert.IsTrue( Directory.Exists( directory ) );
		}
		finally
		{
			Directory.Delete( root, true );
		}
	}

	[TestMethod]
	public void OrphanDeletionDoesNotDeleteLowercaseSibling()
	{
		if ( !OperatingSystem.IsLinux() ) return;
		var root = Path.Combine( Path.GetTempPath(), $"sbox-cloud-sibling-{Guid.NewGuid():N}" );
		var directory = Path.Combine( root, "MiXeDProject", ".sbox", "cloud", "Models" );
		Directory.CreateDirectory( directory );
		try
		{
			var source = Path.Combine( directory, "Chair.VMDL" );
			var sibling = source.ToLowerInvariant();
			Directory.CreateDirectory( Path.GetDirectoryName( sibling ) );
			File.WriteAllText( source, "orphan" );
			File.WriteAllText( sibling, "unrelated lowercase tree" );

			CloudAssetDirectory.DeleteOrphanedFile( source );

			Assert.IsFalse( File.Exists( source ) );
			Assert.AreEqual( "unrelated lowercase tree", File.ReadAllText( sibling ) );
		}
		finally
		{
			Directory.Delete( root, true );
		}
	}
}
