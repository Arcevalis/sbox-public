using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ResourceTests;

/// <summary>
/// Pins when a block-less compiled resource may skip the "_d" sidecar search: blob data is only
/// ever read through a "$blob" reference, so JSON without one loads from an empty table.
/// </summary>
[TestClass]
public class BlobReferenceTests
{
	[TestMethod]
	public void JsonWithoutBlobReferenceSkipsSidecarSearch()
	{
		Assert.IsFalse( BlobDataSerializer.ReferencesBlobs( """{"__version":1,"Scalar":42}""" ) );
	}

	[TestMethod]
	public void JsonWithBlobReferenceSearchesSidecar()
	{
		Assert.IsTrue( BlobDataSerializer.ReferencesBlobs( """{"Data":{"$blob":"3fa85f64-5717-4562-b3fc-2c963f66afa6"}}""" ) );
	}

	[TestMethod]
	public void BlobLookalikeInStringValueTakesSlowPath()
	{
		// A false positive just searches like before - never a wrong load.
		Assert.IsTrue( BlobDataSerializer.ReferencesBlobs( """{"Name":"my $blob collection"}""" ) );
	}
}
