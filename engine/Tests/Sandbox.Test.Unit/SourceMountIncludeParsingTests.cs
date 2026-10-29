using Sandbox.Mounting.Source1;
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

[TestClass]
public class SourceMountIncludeParsingTests
{
	[TestMethod]
	public void ModelGroupMatchesSdkLayout()
	{
		Assert.AreEqual( 8, Marshal.SizeOf<MdlFile.StudioModelGroup>() );
		Assert.AreEqual( 0, Marshal.OffsetOf<MdlFile.StudioModelGroup>( nameof( MdlFile.StudioModelGroup.LabelIndex ) ).ToInt32() );
		Assert.AreEqual( 4, Marshal.OffsetOf<MdlFile.StudioModelGroup>( nameof( MdlFile.StudioModelGroup.NameIndex ) ).ToInt32() );
	}

	[TestMethod]
	public void ReadsEntryRelativeIncludesAndNormalizesFileSlashes()
	{
		using var stream = new MemoryStream( BuildMdl( [("first", "models\\shared\\one.mdl"), ("second", "models/shared/two.mdl")] ) );
		var mdl = new MdlFile( stream );
		Assert.AreEqual( 2, mdl.IncludeCount );
		Assert.AreEqual( "first", mdl.GetIncludeLabel( 0 ) );
		Assert.AreEqual( "second", mdl.GetIncludeLabel( 1 ) );
		Assert.AreEqual( "models/shared/one.mdl", mdl.GetIncludeFile( 0 ) );
		Assert.AreEqual( "models/shared/two.mdl", mdl.GetIncludeFile( 1 ) );
	}

	[TestMethod]
	public void ZeroIncludesAreSupported()
	{
		using var stream = new MemoryStream( BuildMdl( [] ) );
		Assert.AreEqual( 0, new MdlFile( stream ).IncludeCount );
	}

	[TestMethod]
	[DataRow( -1, 0 )]
	[DataRow( int.MaxValue, 0 )]
	[DataRow( 1, -1 )]
	[DataRow( 1, int.MaxValue )]
	public void InvalidIncludeTableIsNonfatal( int count, int offset )
	{
		var bytes = BuildMdl( [] );
		PatchHeader( bytes, nameof( MdlFile.Header.NumIncludeModels ), count );
		PatchHeader( bytes, nameof( MdlFile.Header.IncludeModelOffset ), offset );
		using var stream = new MemoryStream( bytes );
		var mdl = new MdlFile( stream );
		Assert.AreEqual( 0, mdl.IncludeCount );
		Assert.AreEqual( 0, mdl.BoneCount );
	}

	[TestMethod]
	[DataRow( int.MaxValue )]
	[DataRow( int.MinValue )]
	public void MalformedIncludeStringsDoNotDiscardOtherFieldsOrEntries( int offset )
	{
		var bytes = BuildMdl( [("first", "models/one.mdl"), ("second", "models/two.mdl")] );
		var table = Marshal.SizeOf<MdlFile.Header>();
		Patch( bytes, table, offset );
		Patch( bytes, table + 12, offset );
		using var stream = new MemoryStream( bytes );
		var mdl = new MdlFile( stream );
		Assert.AreEqual( 2, mdl.IncludeCount );
		Assert.AreEqual( string.Empty, mdl.GetIncludeLabel( 0 ) );
		Assert.AreEqual( "models/one.mdl", mdl.GetIncludeFile( 0 ) );
		Assert.AreEqual( "second", mdl.GetIncludeLabel( 1 ) );
		Assert.AreEqual( string.Empty, mdl.GetIncludeFile( 1 ) );
	}

	[TestMethod]
	public void ZeroStringOffsetsAreOptional()
	{
		using var stream = new MemoryStream( BuildMdl( [(null, null), ("valid", "models/valid.mdl")] ) );
		var mdl = new MdlFile( stream );
		Assert.AreEqual( 2, mdl.IncludeCount );
		Assert.AreEqual( string.Empty, mdl.GetIncludeLabel( 0 ) );
		Assert.AreEqual( string.Empty, mdl.GetIncludeFile( 0 ) );
		Assert.AreEqual( "models/valid.mdl", mdl.GetIncludeFile( 1 ) );
	}

	[TestMethod]
	public void IncludeStringCannotReadPastDeclaredLength()
	{
		var bytes = BuildMdl( [("valid", "models/unterminated.mdl")] );
		// A physical terminator exists, but not within the declared MDL extent.
		PatchHeader( bytes, nameof( MdlFile.Header.Length ), bytes.Length - 1 );
		using var stream = new MemoryStream( bytes );
		var mdl = new MdlFile( stream );
		Assert.AreEqual( "valid", mdl.GetIncludeLabel( 0 ) );
		Assert.AreEqual( string.Empty, mdl.GetIncludeFile( 0 ) );
	}

	[TestMethod]
	public void IncludeTableCannotReadPastDeclaredLength()
	{
		var bytes = BuildMdl( [("valid", "models/valid.mdl")] );
		PatchHeader( bytes, nameof( MdlFile.Header.Length ), Marshal.SizeOf<MdlFile.Header>() + 7 );
		using var stream = new MemoryStream( bytes );
		Assert.AreEqual( 0, new MdlFile( stream ).IncludeCount );
	}

	[TestMethod]
	public void ReadsRawEntryRelativeAnimationDescriptorNames()
	{
		using var stream = new MemoryStream( BuildMdl( [], ["raw\\take", "Different Take"] ) );
		var mdl = new MdlFile( stream );
		Assert.AreEqual( 2, mdl.AnimDescCount );
		Assert.AreEqual( "raw\\take", mdl.GetAnimDescName( 0 ) );
		Assert.AreEqual( "Different Take", mdl.GetAnimDescName( 1 ) );
		Assert.IsTrue( mdl.HasAnimDescName( 0 ) );
		Assert.IsTrue( mdl.HasAnimDescName( 1 ) );
	}

	[TestMethod]
	public void MalformedDescriptorNamesAreMarkedInvalidWithoutDiscardingDescriptors()
	{
		var bytes = BuildMdl( [], ["bad", "good", null, " ", ""] );
		var table = Marshal.SizeOf<MdlFile.Header>();
		Patch( bytes, table + 4, int.MaxValue );
		using var stream = new MemoryStream( bytes );
		var mdl = new MdlFile( stream );
		Assert.AreEqual( 5, mdl.AnimDescCount );
		Assert.AreEqual( "good", mdl.GetAnimDescName( 1 ) );
		Assert.IsTrue( mdl.HasAnimDescName( 1 ) );
		foreach ( var index in new[] { 0, 2, 3, 4 } )
		{
			Assert.AreEqual( string.Empty, mdl.GetAnimDescName( index ) );
			Assert.IsFalse( mdl.HasAnimDescName( index ) );
		}
	}

	[TestMethod]
	public void UnterminatedDescriptorNameIsNonfatal()
	{
		var bytes = BuildMdl( [], ["good", "unterminated"] );
		PatchHeader( bytes, nameof( MdlFile.Header.Length ), bytes.Length - 1 );
		using var stream = new MemoryStream( bytes );
		var mdl = new MdlFile( stream );
		Assert.AreEqual( 2, mdl.AnimDescCount );
		Assert.AreEqual( "good", mdl.GetAnimDescName( 0 ) );
		Assert.IsFalse( mdl.HasAnimDescName( 1 ) );
	}

	[TestMethod]
	public void LocalAnimationTableStillRequiresValidBounds()
	{
		var bytes = BuildMdl( [] );
		PatchHeader( bytes, nameof( MdlFile.Header.NumLocalAnims ), 1 );
		PatchHeader( bytes, nameof( MdlFile.Header.LocalAnimIndex ), bytes.Length - 1 );
		using var stream = new MemoryStream( bytes );
		Assert.AreEqual( 0, new MdlFile( stream ).AnimDescCount );
	}

	private static byte[] BuildMdl( (string Label, string File)[] includes, string[] descriptorNames = null )
	{
		using var stream = new MemoryStream();
		using var writer = new BinaryWriter( stream, Encoding.UTF8, leaveOpen: true );
		writer.Write( new byte[Marshal.SizeOf<MdlFile.Header>()] );
		var includeTable = (int)stream.Position;
		writer.Write( new byte[includes.Length * Marshal.SizeOf<MdlFile.StudioModelGroup>()] );
		for ( var index = 0; index < includes.Length; ++index )
		{
			var entry = includeTable + index * Marshal.SizeOf<MdlFile.StudioModelGroup>();
			WriteString( writer, entry, entry, includes[index].Label );
			WriteString( writer, entry, entry + 4, includes[index].File );
		}
		descriptorNames ??= [];
		var descriptorTable = (int)stream.Position;
		writer.Write( new byte[descriptorNames.Length * Marshal.SizeOf<MdlFile.StudioAnimDesc>()] );
		for ( var index = 0; index < descriptorNames.Length; ++index )
		{
			var entry = descriptorTable + index * Marshal.SizeOf<MdlFile.StudioAnimDesc>();
			WriteString( writer, entry, entry + 4, descriptorNames[index] );
		}
		var bytes = stream.ToArray();
		PatchHeader( bytes, nameof( MdlFile.Header.Id ), 0x54534449 );
		PatchHeader( bytes, nameof( MdlFile.Header.Version ), 48 );
		PatchHeader( bytes, nameof( MdlFile.Header.Length ), bytes.Length );
		PatchHeader( bytes, nameof( MdlFile.Header.VertAnimFixedPointScale ), BitConverter.SingleToInt32Bits( 1f / 4096f ) );
		PatchHeader( bytes, nameof( MdlFile.Header.NumIncludeModels ), includes.Length );
		PatchHeader( bytes, nameof( MdlFile.Header.IncludeModelOffset ), includeTable );
		PatchHeader( bytes, nameof( MdlFile.Header.NumLocalAnims ), descriptorNames.Length );
		PatchHeader( bytes, nameof( MdlFile.Header.LocalAnimIndex ), descriptorTable );
		return bytes;
	}

	private static void WriteString( BinaryWriter writer, int entry, int field, string value )
	{
		if ( value is null )
			return;
		var position = writer.BaseStream.Position;
		writer.BaseStream.Position = field;
		writer.Write( (int)position - entry );
		writer.BaseStream.Position = position;
		writer.Write( Encoding.UTF8.GetBytes( value + "\0" ) );
	}

	private static void PatchHeader( byte[] bytes, string field, int value )
	{
		Patch( bytes, Marshal.OffsetOf<MdlFile.Header>( field ).ToInt32(), value );
	}

	private static void Patch( byte[] bytes, int offset, int value )
	{
		BitConverter.GetBytes( value ).CopyTo( bytes, offset );
	}
}
