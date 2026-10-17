using Sandbox.Mounting.Source1;
using System;
using System.Collections.Generic;
using System.IO;

public partial class SourceMountAnimationTests
{
	[TestMethod]
	public void VirtualDecodeSectionRecordPresenceUsesSequenceBindInsteadOfAnimationBind()
	{
		var source = BuildTestMdl( frames: 4, patchDesc: ( writer, stream, desc ) =>
		{
			Patch( writer, stream, desc + 56, 0 );
			Patch( writer, stream, desc + 80, (int)(stream.Position - desc) );
			Patch( writer, stream, desc + 84, 2 );
			var table = stream.Position;
			for ( var index = 0; index < 4; ++index )
			{
				writer.Write( 0 );
				writer.Write( 0 );
			}
			Patch( writer, stream, table + 4, (int)(stream.Position - desc) );
			WriteAbsentEdgeRecord( writer ); // Frames 0 and 1: no animation record.
			Patch( writer, stream, table + 12, (int)(stream.Position - desc) );
			writer.Write( (byte)0 ); // Frame 2: record exists, both channels absent.
			writer.Write( (byte)0 );
			writer.Write( (short)0 );
			Patch( writer, stream, table + 28, (int)(stream.Position - desc) );
			WriteAbsentEdgeRecord( writer ); // Dedicated final frame: no record.
		} );
		var files = new Dictionary<string, byte[]>
		{
			["models/root.mdl"] = VirtualFixture( new[] { "root" }, new[] { 10f }, includes: new[] { "models/a.mdl", "models/b.mdl" } ),
			["models/a.mdl"] = VirtualFixture( new[] { "root" }, new[] { 100f }, "static_a", "shared", original: source ),
			["models/b.mdl"] = VirtualFixture( new[] { "root" }, new[] { 200f }, "moving_b", "shared" ),
		};
		var animations = DecodeVirtual( files );
		Assert.AreEqual( 1, animations.Count );
		Assert.AreEqual( "moving_b", animations[0].Name );
		Assert.AreEqual( 4, animations[0].Frames );
		Assert.AreEqual( 200f, animations[0].LocalFrames[0].Position.x );
		Assert.AreEqual( 200f, animations[0].LocalFrames[1].Position.x );
		Assert.AreEqual( 100f, animations[0].LocalFrames[2].Position.x );
		Assert.AreEqual( 200f, animations[0].LocalFrames[3].Position.x );
	}

	[TestMethod]
	public void VirtualDecodeSkipsTargetStaticWhenOnlyUnmappedLibraryBoneMoves()
	{
		var source = BuildTestMdl( writeTake: writer => WriteEdgeRotationChain( writer, 1, 0, 100 ) );
		var library = VirtualFixture( new[] { "root", "unmapped" }, new[] { 10f, 20f }, "unmapped_motion", "unmapped_anim", original: source );
		Assert.AreEqual( 0, DecodeVirtual( VirtualFiles( library ) ).Count );
	}

	[TestMethod]
	public void VirtualDecodeRawFramePositionsRemainAbsoluteAndTargetOnlyBoneKeepsBind()
	{
		var source = BuildTestMdl( 0x40, writeTake: writer => WriteFrameTake( writer, false ) );
		var files = new Dictionary<string, byte[]>
		{
			["models/root.mdl"] = VirtualFixture( new[] { "root", "target_only" }, new[] { 300f, 700f }, includes: new[] { "models/lib.mdl" } ),
			["models/lib.mdl"] = VirtualFixture( new[] { "root" }, new[] { 100f }, "raw_absolute", "raw_anim", original: source ),
		};
		var animations = DecodeVirtual( files );
		Assert.AreEqual( 1, animations.Count );
		var frames = animations[0].LocalFrames;
		Assert.AreEqual( 10f, frames[0].Position.x ); // RAW data is not added to 100 or 300.
		Assert.AreEqual( 11f, frames[2].Position.x );
		Assert.AreEqual( 20f, frames[0].Position.y );
		Assert.AreEqual( 30f, frames[0].Position.z );
		Assert.AreEqual( 1f, frames[0].Rotation.w ); // Missing channel uses animation bind.
		Assert.AreEqual( 700f, frames[1].Position.x );
		Assert.AreEqual( 700f, frames[3].Position.x );
	}

	[TestMethod]
	public void VirtualDecodeAnimationBoneMissingFromSequenceCannotOverrideTargetBind()
	{
		var source = BuildTestMdl( writeTake: writer => WriteEdgeRotationChain( writer, 1, 0, 100 ) );
		var files = new Dictionary<string, byte[]>
		{
			["models/root.mdl"] = VirtualFixture( new[] { "root", "target_only" }, new[] { 10f, 700f }, includes: new[] { "models/a.mdl", "models/b.mdl" } ),
			["models/a.mdl"] = VirtualFixture( new[] { "root", "target_only" }, new[] { 100f, 200f }, "moving_a", "shared", original: source ),
			["models/b.mdl"] = VirtualFixture( new[] { "root" }, new[] { 300f }, "static_b", "shared" ),
		};
		var animations = DecodeVirtual( files );
		Assert.AreEqual( 1, animations.Count );
		Assert.AreEqual( "moving_a", animations[0].Name );
		Assert.AreEqual( 0.049979169, animations[0].LocalFrames[3].Rotation.x, 1e-6 );
	}

	[TestMethod]
	[DataRow( 72 )] // Descriptor NumLocalHierarchy.
	[DataRow( 148 )] // Sequence NumAutoLayers, encoded below separately.
	public void VirtualDecodeRejectsReparentingAndAutolayers( int kind )
	{
		var source = BuildTestMdl( patchDesc: ( writer, stream, desc ) =>
		{
			if ( kind == 72 )
				Patch( writer, stream, desc + 72, 1 );
		} );
		if ( kind != 72 )
		{
			var sequence = BitConverter.ToInt32( source, 192 );
			// NumAutoLayers is the int immediately before AutoLayerIndex and WeightListIndex.
			BitConverter.GetBytes( 1 ).CopyTo( source, sequence + 148 );
		}
		var library = VirtualFixture( new[] { "root" }, new[] { 10f }, "unsupported", "unsupported_anim", original: source );
		Assert.AreEqual( 0, DecodeVirtual( VirtualFiles( library ) ).Count );
	}

	[TestMethod]
	[DataRow( true )]
	[DataRow( false )]
	public void VirtualDecodeUsesAnimationOwnerScalesAndBindRotation( bool rotationChannel )
	{
		var source = BuildTestMdl( writeTake: writer => WriteEdgeScaledChannels( writer, rotationChannel ) );
		var target = VirtualFixture( new[] { "root" }, new[] { 300f }, includes: new[] { "models/a.mdl", "models/b.mdl" } );
		var animation = VirtualFixture( new[] { "root" }, new[] { 100f }, "clip_a", "shared", original: source );
		var sequence = VirtualFixture( new[] { "root" }, new[] { 200f }, "clip_b", "shared" );
		PatchEdgeBonePose( target, 2f, 12f, 0.1f );
		PatchEdgeBonePose( animation, 0.4f, 2.5f, 0.002f );
		PatchEdgeBonePose( sequence, 1.2f, 9f, 0.05f );
		var animations = DecodeVirtual( new Dictionary<string, byte[]>
		{
			["models/root.mdl"] = target,
			["models/a.mdl"] = animation,
			["models/b.mdl"] = sequence,
		} );
		Assert.AreEqual( 2, animations.Count );
		var frames = animations[1].LocalFrames;
		Assert.AreEqual( "clip_b", animations[1].Name );
		Assert.AreEqual( 100f, frames[0].Position.x );
		Assert.AreEqual( 110f, frames[1].Position.x ); // 100 + 4 * animation-owner scale 2.5.
		Assert.AreEqual( MathF.Sin( 0.2f ), frames[0].Rotation.x, 1e-6 );
		Assert.AreEqual( MathF.Cos( 0.2f ), frames[0].Rotation.w, 1e-6 );
		var halfAngle = rotationChannel ? 0.3f : 0.2f;
		Assert.AreEqual( MathF.Sin( halfAngle ), frames[1].Rotation.x, 1e-6 );
		Assert.AreEqual( MathF.Cos( halfAngle ), frames[1].Rotation.w, 1e-6 );
	}

	[TestMethod]
	public void VirtualDecodeUsesAnimationOwnerFixedAlignmentQuaternionSign()
	{
		var source = BuildTestMdl( 0x40, writeTake: writer => WriteFrameTake( writer, true ) );
		var animation = VirtualFixture( new[] { "root" }, new[] { 100f }, "clip_a", "shared", original: source );
		var bone = BitConverter.ToInt32( animation, 160 );
		PatchEdgeInt( animation, bone + 160, 0x100000 );
		PatchEdgeFloat( animation, bone + 144, 0.5f );
		PatchEdgeFloat( animation, bone + 148, 0f );
		PatchEdgeFloat( animation, bone + 152, 0f );
		PatchEdgeFloat( animation, bone + 156, -MathF.Sqrt( 0.75f ) );
		var animations = DecodeVirtual( new Dictionary<string, byte[]>
		{
			["models/root.mdl"] = VirtualFixture( new[] { "root" }, new[] { 300f }, includes: new[] { "models/a.mdl", "models/b.mdl" } ),
			["models/a.mdl"] = animation,
			["models/b.mdl"] = VirtualFixture( new[] { "root" }, new[] { 200f }, "clip_b", "shared" ),
		} );
		Assert.AreEqual( 2, animations.Count );
		Assert.AreEqual( "clip_b", animations[1].Name );
		foreach ( var frame in animations[1].LocalFrames )
		{
			Assert.AreEqual( 0.5f, frame.Rotation.x, 1e-6 );
			Assert.AreEqual( -MathF.Sqrt( 0.75f ), frame.Rotation.w, 1e-6 );
		}
	}

	[TestMethod]
	[DataRow( 0 )] // Missing weightlist.
	[DataRow( 1 )] // Out-of-range weightlist.
	[DataRow( 2 )] // Truncated float.
	[DataRow( 3 )] // Infinite weight.
	[DataRow( 4 )] // Negative weightlist offset.
	public void VirtualDecodeMalformedWeightlistSkipsOnlyAffectedSequence( int kind )
	{
		var bad = VirtualFixture( new[] { "root" }, new[] { 10f }, "bad", "bad_anim", weight: kind == 3 ? float.PositiveInfinity : 1f );
		var sequence = BitConverter.ToInt32( bad, 192 );
		if ( kind != 3 )
		{
			var offset = kind switch
			{
				0 => 0,
				1 => int.MaxValue,
				2 => bad.Length - 2 - sequence,
				_ => -1,
			};
			PatchEdgeInt( bad, sequence + 156, offset );
		}
		var animations = DecodeVirtual( new Dictionary<string, byte[]>
		{
			["models/root.mdl"] = VirtualFixture( new[] { "root" }, new[] { 10f }, includes: new[] { "models/bad.mdl", "models/good.mdl" } ),
			["models/bad.mdl"] = bad,
			["models/good.mdl"] = VirtualFixture( new[] { "root" }, new[] { 10f }, "good", "good_anim" ),
		} );
		Assert.AreEqual( 1, animations.Count );
		Assert.AreEqual( "good", animations[0].Name );
	}

	[TestMethod]
	public void VirtualDecodeSkipsSectionedExternalTake()
	{
		var source = BuildTestMdl( frames: 4, patchDesc: ( writer, stream, desc ) =>
		{
			Patch( writer, stream, desc + 56, 0 );
			Patch( writer, stream, desc + 80, (int)(stream.Position - desc) );
			Patch( writer, stream, desc + 84, 2 );
			for ( var index = 0; index < 4; ++index )
			{
				writer.Write( 1 ); // External block sections are deliberately unsupported.
				writer.Write( 4 );
			}
		} );
		var library = VirtualFixture( new[] { "root" }, new[] { 10f }, "sectioned_external", "sectioned_anim", original: source );
		Assert.AreEqual( 0, DecodeVirtual( VirtualFiles( library ) ).Count );
	}

	[TestMethod]
	public void VirtualDecodeExternalBlockTableCannotReadPastDeclaredLength()
	{
		var (library, external) = BuildEdgeExternalLibrary();
		var declaredLength = library.Length;
		Array.Resize( ref library, declaredLength + 16 );
		PatchEdgeInt( library, 356, declaredLength );
		PatchEdgeInt( library, declaredLength + 8, 4 );
		PatchEdgeInt( library, declaredLength + 12, external.Length );
		// Keep Header.Length unchanged: the copied block table is physically present only.
		var files = VirtualFiles( library );
		files["models/take.ani"] = external;
		Assert.AreEqual( 0, DecodeVirtual( files ).Count );
	}

	[TestMethod]
	public void VirtualDecodeExternalOpenerReturningNullSkipsTake()
	{
		var (library, _) = BuildEdgeExternalLibrary();
		var files = VirtualFiles( library );
		Stream Open( string path ) => files.TryGetValue( path, out var bytes ) ? new MemoryStream( bytes ) : null;
		using var root = Open( "models/root.mdl" );
		var mdl = new MdlFile( root );
		var graph = mdl.ResolveIncludes( "models/root.mdl", Open );
		Assert.AreEqual( 0, mdl.ReadAnimations( graph, root, Open ).Count );
	}

	private static (byte[] Library, byte[] External) BuildEdgeExternalLibrary()
	{
		using var external = new MemoryStream();
		using var writer = new BinaryWriter( external );
		writer.Write( 0 );
		writer.Write( 0 );
		WriteBoneChain( writer, 0, 100 );
		var source = BuildTestMdl( patchDesc: ( output, stream, desc ) =>
		{
			Patch( output, stream, desc + 52, 1 );
			Patch( output, stream, desc + 56, 4 );
			Patch( output, stream, 348, (int)stream.Position );
			output.Write( System.Text.Encoding.UTF8.GetBytes( "take.ani\0" ) );
			Patch( output, stream, 352, 2 );
			Patch( output, stream, 356, (int)stream.Position );
			output.Write( 0 );
			output.Write( 0 );
			output.Write( 4 );
			output.Write( (int)external.Length );
		} );
		return (VirtualFixture( new[] { "root" }, new[] { 10f }, "external", "external_anim", original: source ), external.ToArray());
	}

	private static void PatchEdgeBonePose( byte[] bytes, float angle, float positionScale, float rotationScale )
	{
		var bone = BitConverter.ToInt32( bytes, 160 );
		PatchEdgeFloat( bytes, bone + 44, MathF.Sin( angle * 0.5f ) );
		PatchEdgeFloat( bytes, bone + 56, MathF.Cos( angle * 0.5f ) );
		PatchEdgeFloat( bytes, bone + 60, angle );
		PatchEdgeFloat( bytes, bone + 72, positionScale );
		PatchEdgeFloat( bytes, bone + 84, rotationScale );
	}

	private static void PatchEdgeFloat( byte[] bytes, int offset, float value )
	{
		PatchEdgeInt( bytes, offset, BitConverter.SingleToInt32Bits( value ) );
	}

	private static void PatchEdgeInt( byte[] bytes, int offset, int value )
	{
		BitConverter.GetBytes( value ).CopyTo( bytes, offset );
	}

	private static void WriteEdgeScaledChannels( BinaryWriter writer, bool rotationChannel )
	{
		writer.Write( (byte)0 );
		writer.Write( (byte)(rotationChannel ? 12 : 4) );
		writer.Write( (short)0 );
		if ( rotationChannel )
		{
			writer.Write( (short)12 ); // Rotation RLE follows both channel pointer triplets.
			writer.Write( (short)0 );
			writer.Write( (short)0 );
		}
		writer.Write( (short)(rotationChannel ? 12 : 6) );
		writer.Write( (short)0 );
		writer.Write( (short)0 );
		if ( rotationChannel )
		{
			writer.Write( (byte)2 );
			writer.Write( (byte)2 );
			writer.Write( (short)0 );
			writer.Write( (short)100 );
		}
		writer.Write( (byte)2 );
		writer.Write( (byte)2 );
		writer.Write( (short)0 );
		writer.Write( (short)4 );
	}

	private static void WriteAbsentEdgeRecord( BinaryWriter writer )
	{
		writer.Write( (byte)255 );
		writer.Write( (byte)0 );
		writer.Write( (short)0 );
	}

	private static void WriteEdgeRotationChain( BinaryWriter writer, byte bone, short first, short second )
	{
		writer.Write( bone );
		writer.Write( (byte)8 );
		writer.Write( (short)0 );
		writer.Write( (short)6 );
		writer.Write( (short)0 );
		writer.Write( (short)0 );
		writer.Write( (byte)2 );
		writer.Write( (byte)2 );
		writer.Write( first );
		writer.Write( second );
	}
}
