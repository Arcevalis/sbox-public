using Sandbox.Mounting.Source1;
using System;
using System.IO;
using System.Text;

[TestClass]
public partial class SourceMountAnimationTests
{
	[TestMethod]
	public void DecodesMovingSequenceAndSkipsStatic()
	{
		using var stream = new MemoryStream( BuildTestMdl() );
		var mdl = new MdlFile( stream );

		Assert.AreEqual( 2, mdl.SequenceCount );
		Assert.AreEqual( 2, mdl.AnimDescCount );
		Assert.AreEqual( "idle", mdl.GetSequenceName( 0 ) );
		Assert.AreEqual( "still", mdl.GetSequenceName( 1 ) );

		var animations = mdl.ReadAnimations( stream, null );

		Assert.AreEqual( 1, animations.Count );
		var idle = animations[0];
		Assert.AreEqual( "idle", idle.Name );
		Assert.AreEqual( 30f, idle.Fps );
		Assert.IsTrue( idle.Looping );
		Assert.AreEqual( 2, idle.Frames );
		Assert.AreEqual( 2, idle.LocalFrames.Length );

		var first = idle.LocalFrames[0];
		Assert.AreEqual( 10f, first.Position.x, 1e-4 );
		Assert.AreEqual( 20f, first.Position.y, 1e-4 );
		Assert.AreEqual( 30f, first.Position.z, 1e-4 );
		Assert.AreEqual( 0f, first.Rotation.x, 1e-6 );
		Assert.AreEqual( 0f, first.Rotation.y, 1e-6 );
		Assert.AreEqual( 0f, first.Rotation.z, 1e-6 );
		Assert.AreEqual( 1f, first.Rotation.w, 1e-6 );

		var second = idle.LocalFrames[1];
		Assert.AreEqual( 10f, second.Position.x, 1e-4 );
		Assert.AreEqual( 20f, second.Position.y, 1e-4 );
		Assert.AreEqual( 30f, second.Position.z, 1e-4 );
		Assert.AreEqual( 0.049979169, second.Rotation.x, 1e-6 );
		Assert.AreEqual( 0f, second.Rotation.y, 1e-6 );
		Assert.AreEqual( 0f, second.Rotation.z, 1e-6 );
		Assert.AreEqual( 0.99875026, second.Rotation.w, 1e-6 );
	}

	[TestMethod]
	[DataRow( 0 )]
	[DataRow( 0x40 )]
	public void ImportsLocomotionInPlaceWithoutApplyingExtractedMovement( int flags )
	{
		Action<BinaryWriter> writeTake = flags == 0 ? null : w => WriteFrameTake( w, false );
		using var baselineStream = new MemoryStream( BuildTestMdl( flags, writeTake: writeTake ) );
		var baseline = new MdlFile( baselineStream ).ReadAnimations( baselineStream, null )[0];
		using var stream = new MemoryStream( BuildTestMdl( flags, writeTake: writeTake, patchDesc: WriteExtractedMovement ) );
		var mdl = new MdlFile( stream );
		Assert.AreEqual( 1, mdl.GetAnimDesc( 0 ).NumMovements );
		var animations = mdl.ReadAnimations( stream, null );
		Assert.AreEqual( 1, animations.Count );
		var animation = animations[0];
		Assert.AreEqual( baseline.Name, animation.Name );
		Assert.AreEqual( baseline.Fps, animation.Fps );
		Assert.AreEqual( baseline.Looping, animation.Looping );
		Assert.AreEqual( baseline.Frames, animation.Frames );
		CollectionAssert.AreEqual( baseline.LocalFrames, animation.LocalFrames );
		// Preserve authored bone motion, including root bob, but never add the 500-unit travel.
		Assert.AreEqual( flags == 0 ? 10f : 11f, animation.LocalFrames[1].Position.x );
	}

	[TestMethod]
	[DataRow( 60 )] // NumIkRules
	[DataRow( 72 )] // NumLocalHierarchy
	public void LocomotionFallbackStillSkipsIkAndReparenting( int countOffset )
	{
		using var stream = new MemoryStream( BuildTestMdl( patchDesc: ( w, ms, desc ) =>
		{
			WriteExtractedMovement( w, ms, desc );
			Patch( w, ms, desc + countOffset, 1 );
		} ) );
		Assert.AreEqual( 0, new MdlFile( stream ).ReadAnimations( stream, null ).Count );
	}

	[TestMethod]
	public void RejectsNegativeMovementCount()
	{
		using var stream = new MemoryStream( BuildTestMdl( patchDesc: ( w, ms, desc ) => Patch( w, ms, desc + 20, -1 ) ) );
		Assert.AreEqual( 0, new MdlFile( stream ).ReadAnimations( stream, null ).Count );
	}

	[TestMethod]
	public void ExtractedMovementAloneDoesNotCreateBoneAnimation()
	{
		using var stream = new MemoryStream( BuildTestMdl( writeTake: w => WriteBoneChain( w, 0, 0 ), patchDesc: WriteExtractedMovement ) );
		Assert.AreEqual( 0, new MdlFile( stream ).ReadAnimations( stream, null ).Count );
	}

	static void WriteExtractedMovement( BinaryWriter w, MemoryStream ms, long desc )
	{
		Patch( w, ms, desc + 20, 1 );
		Patch( w, ms, desc + 24, (int)(ms.Position - desc) );
		w.Write( 1 ); // End frame.
		w.Write( 1 ); // X movement flag.
		w.Write( 500f ); w.Write( 500f ); w.Write( 0f ); // Speeds and yaw.
		w.Write( 1f ); w.Write( 0f ); w.Write( 0f ); // Movement direction.
		w.Write( 500f ); w.Write( 0f ); w.Write( 0f ); // Extracted end position.
	}

	[TestMethod]
	public void ReadsPositionOnlyFramesWithPaddingAndBindRotation()
	{
		using var stream = new MemoryStream( BuildTestMdl( 0x40, writeTake: w => WriteFrameTake( w, false ) ) );
		var animations = new MdlFile( stream ).ReadAnimations( stream, null );
		Assert.AreEqual( 1, animations.Count );
		Assert.AreEqual( 10f, animations[0].LocalFrames[0].Position.x );
		Assert.AreEqual( 11f, animations[0].LocalFrames[1].Position.x );
		Assert.AreEqual( 1f, animations[0].LocalFrames[1].Rotation.w );
	}

	[TestMethod]
	public void CombinesConstantRotationAndAnimatedPosition()
	{
		using var stream = new MemoryStream( BuildTestMdl( 0x40, writeTake: w => WriteFrameTake( w, true ) ) );
		var animations = new MdlFile( stream ).ReadAnimations( stream, null );
		Assert.AreEqual( 1, animations.Count );
		Assert.AreEqual( 11f, animations[0].LocalFrames[1].Position.x );
		// Quaternion48 must decode negative X without unsigned underflow.
		Assert.AreEqual( -0.5f, animations[0].LocalFrames[1].Rotation.x, 1e-6 );
		Assert.AreEqual( MathF.Sqrt( 0.75f ), animations[0].LocalFrames[1].Rotation.w, 1e-6 );
	}

	[TestMethod]
	public void ReadsDescriptorRelativeSectionsAndDedicatedFinalFrame()
	{
		using var stream = new MemoryStream( BuildTestMdl( frames: 4, patchDesc: ( w, ms, desc ) =>
		{
			Patch( w, ms, desc + 56, 0 ); // Sectioned descriptors need no AnimIndex.
			Patch( w, ms, desc + 80, (int)(ms.Position - desc) );
			Patch( w, ms, desc + 84, 2 );
			var table = ms.Position;
			for ( var i = 0; i < 4; i++ ) { w.Write( 0 ); w.Write( 0 ); }
			Patch( w, ms, table + 4, (int)(ms.Position - desc) );
			WriteBoneChain( w, 0, 100 );
			Patch( w, ms, table + 12, (int)(ms.Position - desc) );
			WriteBoneChain( w, 200, 250 );
			Patch( w, ms, table + 28, (int)(ms.Position - desc) );
			WriteBoneChain( w, 300, 300 );
		} ) );
		var animations = new MdlFile( stream ).ReadAnimations( stream, null );
		Assert.AreEqual( 1, animations.Count );
		for ( var frame = 0; frame < 4; frame++ )
			Assert.AreEqual( MathF.Sin( frame * 0.05f ), animations[0].LocalFrames[frame].Rotation.x, 1e-6 );
	}

	[TestMethod]
	[DataRow( 0x04 )]
	[DataRow( 0x20 )]
	public void SkipsUnsupportedDescriptionFlags( int flags )
	{
		using var stream = new MemoryStream( BuildTestMdl( flags ) );
		Assert.AreEqual( 0, new MdlFile( stream ).ReadAnimations( stream, null ).Count );
	}

	[TestMethod]
	[DataRow( 0, 0 )]
	[DataRow( 2, 1 )]
	public void RejectsMalformedRleWithoutFailingTheModel( int valid, int total )
	{
		using var stream = new MemoryStream( BuildTestMdl( writeTake: w =>
		{
			w.Write( (byte)0 ); w.Write( (byte)8 ); w.Write( (short)0 );
			w.Write( (short)6 ); w.Write( (short)0 ); w.Write( (short)0 );
			w.Write( (byte)valid ); w.Write( (byte)total );
		} ) );
		var mdl = new MdlFile( stream );
		Assert.AreEqual( 1, mdl.BoneCount );
		Assert.AreEqual( 0, mdl.ReadAnimations( stream, null ).Count );
	}

	[TestMethod]
	public void KeepsValidSequencesWhenAnotherExternalFileIsMissing()
	{
		using var stream = new MemoryStream( BuildTestMdl( patchDesc: ( w, ms, desc ) =>
		{
			Patch( w, ms, desc + 52, 1 );
			// Header animation block name string offset.
			Patch( w, ms, 348, (int)ms.Position );
			w.Write( Encoding.UTF8.GetBytes( "missing.ani\0" ) );
		}, moveStill: true ) );
		var animations = new MdlFile( stream ).ReadAnimations( stream, _ => throw new FileNotFoundException() );
		Assert.AreEqual( 1, animations.Count );
		Assert.AreEqual( "still", animations[0].Name );
	}

	[TestMethod]
	public void ReadsExternalTakeWithinItsBlockExtent()
	{
		using var external = new MemoryStream();
		using var writer = new BinaryWriter( external, Encoding.UTF8, leaveOpen: true );
		writer.Write( 0 ); // The block begins after an unrelated prefix.
		writer.Write( 0 ); // The take begins four bytes into that block.
		WriteBoneChain( writer, 0, 100 );
		using var stream = new MemoryStream( BuildTestMdl( patchDesc: ( w, ms, desc ) =>
		{
			Patch( w, ms, desc + 52, 1 );
			Patch( w, ms, desc + 56, 4 );
			Patch( w, ms, 348, (int)ms.Position );
			w.Write( Encoding.UTF8.GetBytes( "take.ani\0" ) );
			Patch( w, ms, 352, 2 );
			Patch( w, ms, 356, (int)ms.Position );
			w.Write( 0 ); w.Write( 0 ); // Block zero is the MDL.
			w.Write( 4 ); w.Write( (int)external.Length );
		} ) );
		var animations = new MdlFile( stream ).ReadAnimations( stream, path =>
		{
			Assert.AreEqual( "take.ani", path );
			return new MemoryStream( external.ToArray() );
		} );
		Assert.AreEqual( 1, animations.Count );
		Assert.AreEqual( 0.049979169, animations[0].LocalFrames[1].Rotation.x, 1e-6 );
	}

	[TestMethod]
	public void SkipsExcessiveFrameCountsBeforeAllocatingTracks()
	{
		using var stream = new MemoryStream( BuildTestMdl( frames: (1 << 20) + 1 ) );
		Assert.AreEqual( 0, new MdlFile( stream ).ReadAnimations( stream, null ).Count );
	}

	[TestMethod]
	public void ReadsRleRepeatsWithoutRequiringAnotherRunHeader()
	{
		using var stream = new MemoryStream( BuildTestMdl( frames: 4, writeTake: w =>
		{
			w.Write( (byte)0 ); w.Write( (byte)8 ); w.Write( (short)0 );
			w.Write( (short)6 ); w.Write( (short)0 ); w.Write( (short)0 );
			w.Write( (byte)2 ); w.Write( (byte)4 );
			w.Write( (short)0 ); w.Write( (short)100 );
		} ) );
		var animations = new MdlFile( stream ).ReadAnimations( stream, null );
		Assert.AreEqual( 1, animations.Count );
		for ( var frame = 1; frame < 4; frame++ )
			Assert.AreEqual( 0.049979169, animations[0].LocalFrames[frame].Rotation.x, 1e-6 );
	}

	[TestMethod]
	public void RejectsTruncatedPayloadAndKeepsOtherSequences()
	{
		using var stream = new MemoryStream( BuildTestMdl( patchDesc: ( w, ms, desc ) =>
		{
			Patch( w, ms, desc + 56, int.MaxValue );
		}, moveStill: true ) );
		var animations = new MdlFile( stream ).ReadAnimations( stream, null );
		Assert.AreEqual( 1, animations.Count );
		Assert.AreEqual( "still", animations[0].Name );
	}

	[TestMethod]
	public void DecodesHierarchyWithoutComposingUntrackedBones()
	{
		using var stream = new MemoryStream( BuildHierarchyMdl() );
		var mdl = new MdlFile( stream );
		var animations = mdl.ReadAnimations( stream, null );
		Assert.AreEqual( 3, mdl.BoneCount );
		Assert.AreEqual( 1, animations.Count );
		var animation = animations[0];
		for ( var frame = 0; frame < animation.Frames; frame++ )
		{
			for ( var bone = 1; bone < 3; bone++ )
			{
				var local = animation.LocalFrames[frame * 3 + bone];
				Assert.AreEqual( mdl.Bones[bone].Position, local.Position );
				Assert.AreEqual( mdl.Bones[bone].Quaternion, local.Rotation );
			}
		}
		Assert.AreEqual( 0.049979169, animation.LocalFrames[3].Rotation.x, 1e-6 );
	}

	[TestMethod]
	public void SubmitsParentLocalFramesDespiteModelSpaceBindSkeleton()
	{
		using var stream = new MemoryStream( BuildHierarchyMdl() );
		var mdl = new MdlFile( stream );
		var animations = mdl.ReadAnimations( stream, null );
		var builder = Sandbox.Model.Builder;
		var world = new Transform[mdl.BoneCount];
		for ( var bone = 0; bone < mdl.BoneCount; bone++ )
		{
			var bind = mdl.Bones[bone];
			var local = new Transform( bind.Position, bind.Quaternion );
			world[bone] = bind.Parent < 0 ? local : world[bind.Parent].ToWorld( local );
			builder.AddBone( $"bone{bone}", world[bone].Position, world[bone].Rotation,
				bind.Parent < 0 ? null : $"bone{bind.Parent}" );
		}
		SourceModelBuilder.AddAnimations( builder, mdl, animations );
		// Inspect the exact frame payload handed to native, without creating a native model.
		var field = typeof( Sandbox.ModelBuilder ).GetField( "_animations", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic );
		Assert.IsNotNull( field );
		var built = (System.Collections.Generic.List<Sandbox.AnimationBuilder>)field.GetValue( builder );
		Assert.AreEqual( 1, built.Count );
		Assert.AreEqual( "idle", built[0].Name );
		Assert.AreEqual( 30f, built[0].FrameRate );
		Assert.AreEqual( 2, built[0].FrameCount );
		Assert.IsTrue( built[0].Looping );
		Assert.AreNotEqual( world[2].Position, mdl.Bones[2].Position );
		for ( var frame = 0; frame < built[0].FrameCount; frame++ )
		{
			var submitted = built[0].GetFrame( frame );
			Assert.AreEqual( 3, submitted.Length );
			for ( var bone = 0; bone < 3; bone++ )
				Assert.AreEqual( animations[0].LocalFrames[frame * 3 + bone], submitted[bone] );
		}
	}

	static byte[] BuildHierarchyMdl()
	{
		var original = BuildTestMdl();
		var originalBone = BitConverter.ToInt32( original, 160 );
		using var ms = new MemoryStream();
		using var w = new BinaryWriter( ms );
		w.Write( original );
		Patch( w, ms, 156, 3 );
		Patch( w, ms, 160, (int)ms.Position );
		for ( var bone = 0; bone < 3; bone++ )
		{
			var start = ms.Position;
			w.Write( original, originalBone, 216 );
			Patch( w, ms, start, 0 ); // Optional name; this fixture tests transforms only.
			Patch( w, ms, start + 4, bone - 1 );
			if ( bone > 0 )
			{
				var end = ms.Position;
				ms.Position = start + 32;
				w.Write( 2f ); w.Write( 3f ); w.Write( 4f );
				w.Write( 0f ); w.Write( 0f ); w.Write( MathF.Sqrt( 0.5f ) ); w.Write( MathF.Sqrt( 0.5f ) );
				w.Write( 0f ); w.Write( 0f ); w.Write( MathF.PI / 2 );
				ms.Position = end;
			}
		}
		Patch( w, ms, 76, (int)ms.Length );
		return ms.ToArray();
	}

	static void WriteFrameTake( BinaryWriter w, bool constantRotation )
	{
		w.Write( constantRotation ? 25 : 0 );
		w.Write( constantRotation ? 31 : 25 );
		w.Write( 16 ); // Twelve bytes of positions plus padding.
		for ( var i = 0; i < 3; i++ ) w.Write( 0 );
		w.Write( (byte)(constantRotation ? 0x12 : 0x10) );
		if ( constantRotation )
		{
			w.Write( (ushort)16384 ); w.Write( (ushort)32768 ); w.Write( (ushort)16384 );
		}
		for ( var frame = 0; frame < 2; frame++ )
		{
			w.Write( 10f + frame ); w.Write( 20f ); w.Write( 30f ); w.Write( 0 );
		}
	}

	/// <summary>
	/// Minimal 48-version MDL: one bone, an "idle" sequence with a two-frame
	/// animated X channel and a "still" sequence whose frames never move.
	/// </summary>
	static byte[] BuildTestMdl( int flags = 0, int frames = 2, Action<BinaryWriter> writeTake = null, Action<BinaryWriter, MemoryStream, long> patchDesc = null, bool moveStill = false )
	{
		using var ms = new MemoryStream();
		using var w = new BinaryWriter( ms );

		w.Write( 0x54534449 );
		w.Write( 48 );
		w.Write( 1234 );
		w.Write( new byte[64] );
		var lengthPosition = ms.Position;
		w.Write( 0 );
		for ( var i = 0; i < 18; i++ ) w.Write( 0f );
		w.Write( 0 );
		w.Write( 1 );
		var boneIndexPosition = ms.Position;
		w.Write( 0 );
		w.Write( 0 );
		w.Write( 0 );
		w.Write( 0 );
		w.Write( 0 );
		w.Write( 2 );
		var animIndexPosition = ms.Position;
		w.Write( 0 );
		w.Write( 2 );
		var seqIndexPosition = ms.Position;
		w.Write( 0 );
		for ( var i = 0; i < 33; i++ ) w.Write( 0 );
		w.Write( 0f );
		w.Write( 0 );
		for ( var i = 0; i < 9; i++ ) w.Write( 0 );
		w.Write( (byte)0 );
		w.Write( (byte)0 );
		w.Write( (byte)0 );
		w.Write( (byte)0 );
		w.Write( 0 );
		w.Write( 0 );
		w.Write( 0 );
		w.Write( 1f / 4096f );
		w.Write( 0 );
		w.Write( 0 );
		w.Write( 0 );

		Patch( w, ms, boneIndexPosition, (int)ms.Position );
		var boneStart = ms.Position;
		w.Write( 216 );
		w.Write( -1 );
		for ( var i = 0; i < 6; i++ ) w.Write( 0 );
		w.Write( 10f );
		w.Write( 20f );
		w.Write( 30f );
		w.Write( 0f );
		w.Write( 0f );
		w.Write( 0f );
		w.Write( 1f );
		w.Write( 0f );
		w.Write( 0f );
		w.Write( 0f );
		w.Write( 1f );
		w.Write( 1f );
		w.Write( 1f );
		w.Write( 0.001f );
		w.Write( 0.001f );
		w.Write( 0.001f );
		for ( var i = 0; i < 12; i++ ) w.Write( i % 5 == 0 ? 1f : 0f );
		w.Write( 0f );
		w.Write( 0f );
		w.Write( 0f );
		w.Write( 1f );
		for ( var i = 0; i < 7; i++ ) w.Write( 0 );
		for ( var i = 0; i < 7; i++ ) w.Write( 0 );
		w.Write( Encoding.UTF8.GetBytes( "root\0" ) );
		Assert.AreEqual( 216, (int)(ms.Position - boneStart - 5) );

		Patch( w, ms, seqIndexPosition, (int)ms.Position );
		WriteSequenceEntry( w, ms, out var idleEntry, out var idleLabel, out var idleTable );
		WriteSequenceEntry( w, ms, out var stillEntry, out var stillLabel, out var stillTable );

		var namePosition = (int)ms.Position;
		w.Write( Encoding.UTF8.GetBytes( "idle\0" ) );
		Patch( w, ms, idleLabel, namePosition - (int)idleEntry );
		var tablePosition = (int)ms.Position;
		Patch( w, ms, idleTable, tablePosition - (int)idleEntry );
		w.Write( (short)0 );

		namePosition = (int)ms.Position;
		w.Write( Encoding.UTF8.GetBytes( "still\0" ) );
		Patch( w, ms, stillLabel, namePosition - (int)stillEntry );
		tablePosition = (int)ms.Position;
		Patch( w, ms, stillTable, tablePosition - (int)stillEntry );
		w.Write( (short)1 );

		Patch( w, ms, animIndexPosition, (int)ms.Position );
		WriteAnimDescEntry( w, ms, out var idleDescEntry, out var idleName, out var idleData, flags, frames );
		WriteAnimDescEntry( w, ms, out var stillDescEntry, out var stillName, out var stillData );

		Patch( w, ms, idleName, (int)ms.Position - (int)idleDescEntry );
		w.Write( Encoding.UTF8.GetBytes( "anim_idle\0" ) );
		Patch( w, ms, idleData, (int)ms.Position - (int)idleDescEntry );
		if ( writeTake == null ) WriteBoneChain( w, 0, 100 );
		else writeTake( w );
		patchDesc?.Invoke( w, ms, idleDescEntry );

		Patch( w, ms, stillName, (int)ms.Position - (int)stillDescEntry );
		w.Write( Encoding.UTF8.GetBytes( "anim_still\0" ) );
		Patch( w, ms, stillData, (int)ms.Position - (int)stillDescEntry );
		WriteBoneChain( w, 0, moveStill ? (short)100 : (short)0 );

		Patch( w, ms, lengthPosition, (int)ms.Length );
		return ms.ToArray();
	}

	static void Patch( BinaryWriter w, MemoryStream ms, long position, int value )
	{
		var here = ms.Position;
		ms.Position = position;
		w.Write( value );
		ms.Position = here;
	}

	static void WriteSequenceEntry( BinaryWriter w, MemoryStream ms, out long entry, out long labelPatch, out long tablePatch )
	{
		entry = ms.Position;
		var start = entry;
		w.Write( 0 );
		labelPatch = ms.Position;
		w.Write( 0 );
		w.Write( 0 );
		w.Write( 1 );
		w.Write( 0 );
		w.Write( 0 );
		w.Write( 0 );
		w.Write( 0 );
		for ( var i = 0; i < 6; i++ ) w.Write( 0f );
		w.Write( 1 );
		tablePatch = ms.Position;
		w.Write( 0 );
		w.Write( 0 );
		w.Write( 1 );
		w.Write( 1 );
		w.Write( 0 );
		w.Write( 0 );
		w.Write( 0f );
		w.Write( 0f );
		w.Write( 0f );
		w.Write( 0f );
		w.Write( 0 );
		w.Write( 0f );
		w.Write( 0f );
		w.Write( 0 );
		w.Write( 0 );
		w.Write( 0 );
		w.Write( 0f );
		w.Write( 0f );
		w.Write( 1f );
		for ( var i = 0; i < 14; i++ ) w.Write( 0 );
		for ( var i = 0; i < 5; i++ ) w.Write( 0 );
		Assert.AreEqual( 212, (int)(ms.Position - start) );
	}

	static void WriteAnimDescEntry( BinaryWriter w, MemoryStream ms, out long entry, out long namePatch, out long dataPatch, int flags = 0, int frames = 2 )
	{
		entry = ms.Position;
		var start = entry;
		w.Write( 0 );
		namePatch = ms.Position;
		w.Write( 0 );
		w.Write( 30f );
		w.Write( flags );
		w.Write( frames );
		w.Write( 0 );
		w.Write( 0 );
		w.Write( 0 );
		for ( var i = 0; i < 5; i++ ) w.Write( 0 );
		w.Write( 0 );
		dataPatch = ms.Position;
		w.Write( 0 );
		w.Write( 0 );
		w.Write( 0 );
		w.Write( 0 );
		w.Write( 0 );
		w.Write( 0 );
		w.Write( 0 );
		w.Write( 0 );
		w.Write( (short)0 );
		w.Write( (short)0 );
		w.Write( 0 );
		w.Write( 0f );
		Assert.AreEqual( 100, (int)(ms.Position - start) );
	}

	static void WriteBoneChain( BinaryWriter w, short first, short second )
	{
		w.Write( (byte)0 );
		w.Write( (byte)0x08 );
		w.Write( (short)0 );
		w.Write( (short)6 );
		w.Write( (short)0 );
		w.Write( (short)0 );
		w.Write( (byte)2 );
		w.Write( (byte)2 );
		w.Write( first );
		w.Write( second );
	}
}
