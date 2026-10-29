using System;

[TestClass]
public class ResourceIdCaseIdentityTests
{
	[DataTestMethod]
	[DataRow( 0, null, true )]
	[DataRow( 0, "", true )]
	[DataRow( 0, "Prefabs/Source.PREFAB_C", false )]
	[DataRow( 1, null, true )]
	[DataRow( 1, "", true )]
	[DataRow( 1, "Prefabs/Source.PREFAB_C", false )]
	[DataRow( 2, null, false )]
	[DataRow( 2, "", false )]
	[DataRow( 2, "Prefabs/Source.PREFAB_C", false )]
	public void EmptyIdentityRequiresNeitherAUsableGuidNorAPath( int guidKind, string path, bool expectedEmpty )
	{
		Guid? guid = guidKind switch
		{
			0 => null,
			1 => Guid.Empty,
			_ => Guid.NewGuid()
		};
		var id = new ResourceId { Guid = guid, Path = path };

		Assert.AreEqual( expectedEmpty, id.IsEmpty );
		Assert.AreEqual( guid, id.Guid );
		Assert.AreEqual( path, id.Path );
	}
}
