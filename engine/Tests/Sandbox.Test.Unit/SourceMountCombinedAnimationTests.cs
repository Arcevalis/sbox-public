using Sandbox.Mounting.Source1;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

public partial class SourceMountAnimationTests
{
	[TestMethod]
	public void VirtualDecodePreservesThreeSkeletonProvenancesAndBoneOrder()
	{
		var files = new Dictionary<string, byte[]>
		{
			["models/root.mdl"] = VirtualFixture( new[] { "extra", "ROOT", "target_only" }, new[] { 20f, 10f, 30f }, includes: new[] { "models/a.mdl", "models/b.mdl" } ),
			["models/a.mdl"] = VirtualFixture( new[] { "root", "extra" }, new[] { 100f, 110f }, "clip_a", "shared" ),
			["models/b.mdl"] = VirtualFixture( new[] { "root", "extra" }, new[] { 200f, 210f }, "clip_b", "SHARED" ),
		};
		var animations = DecodeVirtual( files );
		Assert.AreEqual( 2, animations.Count );
		var clip = animations[1];
		Assert.AreEqual( "clip_b", clip.Name );
		Assert.AreEqual( 6, clip.LocalFrames.Length );
		for ( var frame = 0; frame < 2; frame++ )
		{
			Assert.AreEqual( 210f, clip.LocalFrames[frame * 3].Position.x ); // Absent record: sequence B bind.
			Assert.AreEqual( 100f, clip.LocalFrames[frame * 3 + 1].Position.x ); // Absent channel: animation A bind.
			Assert.AreEqual( 30f, clip.LocalFrames[frame * 3 + 2].Position.x ); // Target-only bind.
		}
		Assert.AreEqual( 0.049979169, clip.LocalFrames[4].Rotation.x, 1e-6 );
	}

	[TestMethod]
	public void VirtualDecodeIgnoresIkAndKeepsExtractedTravelInPlace()
	{
		var library = BuildTestMdl( patchDesc: ( w, ms, desc ) =>
		{
			WriteExtractedMovement( w, ms, desc );
			Patch( w, ms, desc + 60, 1 );
		} );
		var files = VirtualFiles( VirtualFixture( new[] { "root" }, new[] { 10f }, "walk", "walk_anim", original: library ) );
		var animations = DecodeVirtual( files );
		Assert.AreEqual( 1, animations.Count );
		Assert.AreEqual( "walk", animations[0].Name );
		Assert.AreEqual( 10f, animations[0].LocalFrames[1].Position.x );
		Assert.AreEqual( 0.049979169, animations[0].LocalFrames[1].Rotation.x, 1e-6 );
	}

	[TestMethod]
	[DataRow( 0f )]
	[DataRow( -1f )]
	[DataRow( float.NaN )]
	public void VirtualDecodeRejectsUnsafeWeightlists( float weight )
	{
		var library = VirtualFixture( new[] { "root" }, new[] { 10f }, "masked", "masked_anim", weight: weight );
		Assert.AreEqual( 0, DecodeVirtual( VirtualFiles( library ) ).Count );
	}

	[TestMethod]
	public void VirtualDecodeResolvesExternalTakeFromAnimationLibraryDirectory()
	{
		using var external = new MemoryStream();
		using var writer = new BinaryWriter( external, Encoding.UTF8, leaveOpen: true );
		writer.Write( 0 ); writer.Write( 0 );
		WriteBoneChain( writer, 0, 100 );
		var source = BuildTestMdl( patchDesc: ( w, ms, desc ) =>
		{
			Patch( w, ms, desc + 52, 1 );
			Patch( w, ms, desc + 56, 4 );
			Patch( w, ms, 348, (int)ms.Position );
			w.Write( Encoding.UTF8.GetBytes( "take.ani\0" ) );
			Patch( w, ms, 352, 2 );
			Patch( w, ms, 356, (int)ms.Position );
			w.Write( 0 ); w.Write( 0 );
			w.Write( 4 ); w.Write( (int)external.Length );
		} );
		var files = new Dictionary<string, byte[]>
		{
			["models/root.mdl"] = VirtualFixture( new[] { "root" }, new[] { 10f }, includes: new[] { "models/libraries/anim.mdl" } ),
			["models/libraries/anim.mdl"] = VirtualFixture( new[] { "root" }, new[] { 10f }, "external", "external_anim", original: source ),
			["models/libraries/take.ani"] = external.ToArray(),
		};
		var animations = DecodeVirtual( files );
		Assert.AreEqual( 1, animations.Count );
		Assert.AreEqual( 0.049979169, animations[0].LocalFrames[1].Rotation.x, 1e-6 );
	}

	[TestMethod]
	public void VirtualDecodeChargesFailedAttemptsAcrossLibraries()
	{
		var missing = BuildTestMdl( frames: (1 << 20) - 2, patchDesc: ( w, ms, desc ) =>
		{
			Patch( w, ms, desc + 52, 1 );
			Patch( w, ms, 348, (int)ms.Position );
			w.Write( Encoding.UTF8.GetBytes( "missing.ani\0" ) );
		} );
		var files = new Dictionary<string, byte[]>
		{
			["models/root.mdl"] = VirtualFixture( new[] { "root" }, new[] { 10f }, includes: new[] { "models/fail.mdl", "models/pass.mdl", "models/over.mdl" } ),
			["models/fail.mdl"] = VirtualFixture( new[] { "root" }, new[] { 10f }, "fail", "fail_anim", original: missing ),
			["models/pass.mdl"] = VirtualFixture( new[] { "root" }, new[] { 10f }, "pass", "pass_anim" ),
			["models/over.mdl"] = VirtualFixture( new[] { "root" }, new[] { 10f }, "over", "over_anim" ),
		};
		var animations = DecodeVirtual( files );
		Assert.AreEqual( 1, animations.Count );
		Assert.AreEqual( "pass", animations[0].Name );
	}

	[TestMethod]
	[DataRow( false )]
	[DataRow( true )]
	public void MultiBlendGridEmitsEveryDistinctSampleAndKeepsSingleSlotSequenceName( bool virtualModel )
	{
		var animations = DecodeBlendFixture( BuildBlendGridMdl(), virtualModel );
		var samples = IndexBlendSamplesByFps( animations );
		Assert.AreEqual( 9, animations.Count ); // Eight distinct grid descriptors, plus the single-slot sequence.
		var expectedNames = new[] { "walkS", "walkSW", "walkW", "walkNW", "walkN", "walkNE", "walkE", "walkSE", "actual_idle" };
		var names = new HashSet<string>( StringComparer.OrdinalIgnoreCase );
		for ( var sample = 0; sample < expectedNames.Length; sample++ )
		{
			var animation = samples[12f + sample];
			Assert.AreEqual( expectedNames[sample], animation.Name );
			Assert.IsTrue( names.Add( animation.Name ) );
			Assert.AreEqual( 2, animation.Frames );
			Assert.AreEqual( sample * 100f, animation.LocalFrames[0].Position.x );
			Assert.AreEqual( sample * 100f + 1f, animation.LocalFrames[1].Position.x );
			Assert.AreEqual( 20f + sample, animation.LocalFrames[0].Position.y );
		}
		Assert.IsFalse( names.Contains( "walk_all" ) );
		Assert.IsFalse( names.Contains( "authored_idle_anim" ) );
	}

	[TestMethod]
	[DataRow( false, false )]
	[DataRow( false, true )]
	[DataRow( true, false )]
	[DataRow( true, true )]
	public void MultiBlendBadFirstSampleDoesNotSuppressLaterValidSamples( bool virtualModel, bool unsupported )
	{
		var animations = DecodeBlendFixture( BuildBlendGridMdl( badFirstSample: unsupported ? 2 : 1 ), virtualModel );
		var samples = IndexBlendSamplesByFps( animations );
		Assert.AreEqual( 8, animations.Count );
		Assert.IsFalse( samples.ContainsKey( 12f ) );
		for ( var sample = 1; sample < 9; sample++ )
		{
			Assert.IsTrue( samples.TryGetValue( 12f + sample, out var animation ) );
			Assert.AreEqual( sample * 100f + 1f, animation.LocalFrames[1].Position.x );
		}
		Assert.AreEqual( "walkNE", samples[17f].Name );
		Assert.AreEqual( "actual_idle", samples[20f].Name );
	}

	[TestMethod]
	[DataRow( false )]
	[DataRow( true )]
	public void MultiBlendUnnamedDescriptorUsesFirstSlotCoordinates( bool virtualModel )
	{
		var animations = DecodeBlendFixture( BuildBlendGridMdl( unnamedSample: 6 ), virtualModel );
		var samples = IndexBlendSamplesByFps( animations );
		Assert.AreEqual( 9, animations.Count );
		// The grid is x + y * 3. Descriptor six first appears at (0, 2).
		Assert.AreEqual( "walk_all_blend_0_2", samples[18f].Name );
		Assert.AreEqual( 601f, samples[18f].LocalFrames[1].Position.x );
		Assert.AreEqual( "actual_idle", samples[20f].Name );
	}

	[TestMethod]
	[DataRow( false )]
	[DataRow( true )]
	public void MultiBlendNamesCannotShadowAuthoredSequencesOrEachOther( bool virtualModel )
	{
		var animations = DecodeBlendFixture( BuildBlendGridMdl( collideNames: true ), virtualModel );
		var samples = IndexBlendSamplesByFps( animations );
		Assert.AreEqual( 9, animations.Count );
		Assert.AreEqual( "walkN", samples[20f].Name ); // Authored sequence is declared after the blend sequence.
		var first = samples[12f].Name;
		var duplicate = samples[16f].Name;
		StringAssert.StartsWith( first, "walk_all_" );
		StringAssert.StartsWith( duplicate, "walk_all_" );
		Assert.IsFalse( string.Equals( first, duplicate, StringComparison.OrdinalIgnoreCase ) );
		Assert.IsTrue( HasNumericBlendSuffix( first ) || HasNumericBlendSuffix( duplicate ) );
		var names = new HashSet<string>( StringComparer.OrdinalIgnoreCase );
		foreach ( var animation in animations )
			Assert.IsTrue( names.Add( animation.Name ), $"Duplicate imported clip name: {animation.Name}" );
	}

	[TestMethod]
	public void MultiBlendVirtualDecodeResolvesEachSampleThroughSequenceOwnerMasterAnim()
	{
		var animations = DecodeVirtual( new Dictionary<string, byte[]>
		{
			["models/root.mdl"] = VirtualFixture( new[] { "root" }, new[] { 10f }, includes: new[] { "models/a.mdl", "models/b.mdl" } ),
			["models/a.mdl"] = BuildBlendGridMdl( sequenceName: "first_walk_all", singleSequenceName: "first_idle" ),
			["models/b.mdl"] = BuildBlendGridMdl( sequenceName: "second_walk_all", singleSequenceName: "second_idle", sampleOffset: 1000f, fpsOffset: 30f ),
		} );
		Assert.AreEqual( 18, animations.Count );
		SourceAnimation secondSample = null;
		foreach ( var animation in animations )
			if ( animation.Name == "second_walk_all_walkSW" ) secondSample = animation;
		Assert.IsNotNull( secondSample );
		// Library B's local descriptor one contains x=1100/1101 at 43 fps. Its
		// masterAnim entry instead resolves the authored name to A's descriptor one.
		Assert.AreEqual( 13f, secondSample.Fps );
		Assert.AreEqual( 100f, secondSample.LocalFrames[0].Position.x );
		Assert.AreEqual( 101f, secondSample.LocalFrames[1].Position.x );
	}

	private static bool HasNumericBlendSuffix( string name ) =>
		int.TryParse( name.AsSpan( name.LastIndexOf( '_' ) + 1 ), out var suffix ) && suffix > 0;

	private static Dictionary<float, SourceAnimation> IndexBlendSamplesByFps( IReadOnlyList<SourceAnimation> animations )
	{
		var samples = new Dictionary<float, SourceAnimation>();
		foreach ( var animation in animations )
			Assert.IsTrue( samples.TryAdd( animation.Fps, animation ), $"Descriptor at {animation.Fps} fps was emitted more than once." );
		return samples;
	}

	private static IReadOnlyList<SourceAnimation> DecodeBlendFixture( byte[] bytes, bool virtualModel )
	{
		if ( virtualModel )
			return DecodeVirtual( VirtualFiles( bytes ) );
		using var stream = new MemoryStream( bytes );
		return new MdlFile( stream ).ReadAnimations( stream, null );
	}

	/// <summary>
	/// One 3x3 blend grid with a repeated descriptor and one single-slot sequence.
	/// The descriptors deliberately use different authored names, positions and fps.
	/// </summary>
	private static byte[] BuildBlendGridMdl( int badFirstSample = 0, int unnamedSample = -1, bool collideNames = false,
		string sequenceName = "walk_all", string singleSequenceName = "actual_idle", float sampleOffset = 0f, float fpsOffset = 0f )
	{
		using var stream = new MemoryStream();
		using var writer = new BinaryWriter( stream );
		writer.Write( BuildTestMdl() );
		Patch( writer, stream, 188, 2 );
		Patch( writer, stream, 192, (int)stream.Position );
		WriteSequenceEntry( writer, stream, out var grid, out var gridName, out var gridTable );
		WriteSequenceEntry( writer, stream, out var single, out var singleName, out var singleTable );
		Patch( writer, stream, grid + 56, 9 );
		Patch( writer, stream, grid + 68, 3 );
		Patch( writer, stream, grid + 72, 3 );
		Patch( writer, stream, gridName, (int)(stream.Position - grid) );
		writer.Write( Encoding.UTF8.GetBytes( sequenceName + "\0" ) );
		Patch( writer, stream, gridTable, (int)(stream.Position - grid) );
		foreach ( var descriptor in new short[] { 0, 1, 2, 3, 4, 5, 6, 7, 2 } ) writer.Write( descriptor );
		Patch( writer, stream, singleName, (int)(stream.Position - single) );
		writer.Write( Encoding.UTF8.GetBytes( (collideNames ? "walkN" : singleSequenceName) + "\0" ) );
		Patch( writer, stream, singleTable, (int)(stream.Position - single) );
		writer.Write( (short)8 );
		Patch( writer, stream, grid + 156, (int)(stream.Position - grid) );
		writer.Write( 1f );
		Patch( writer, stream, single + 156, (int)(stream.Position - single) );
		writer.Write( 1f );

		Patch( writer, stream, 180, 9 );
		Patch( writer, stream, 184, (int)stream.Position );
		var descriptors = new long[9];
		var namePatches = new long[9];
		var dataPatches = new long[9];
		for ( var sample = 0; sample < descriptors.Length; sample++ )
		{
			WriteAnimDescEntry( writer, stream, out descriptors[sample], out namePatches[sample], out dataPatches[sample],
				flags: sample == 0 && badFirstSample == 2 ? 0x44 : 0x40 );
			Patch( writer, stream, descriptors[sample] + 8, BitConverter.SingleToInt32Bits( 12f + sample + fpsOffset ) );
		}
		var names = new[] { "walkS", "walkSW", "walkW", "walkNW", "walkN", "walkNE", "walkE", "walkSE", "authored_idle_anim" };
		if ( collideNames ) names[0] = "walkN";
		for ( var sample = 0; sample < descriptors.Length; sample++ )
		{
			if ( sample != unnamedSample )
			{
				Patch( writer, stream, namePatches[sample], (int)(stream.Position - descriptors[sample]) );
				writer.Write( Encoding.UTF8.GetBytes( names[sample] + "\0" ) );
			}
			Patch( writer, stream, dataPatches[sample], sample == 0 && badFirstSample == 1 ? int.MaxValue : (int)(stream.Position - descriptors[sample]) );
			WriteBlendGridTake( writer, sample, sampleOffset );
		}
		Patch( writer, stream, 76, (int)stream.Length );
		return stream.ToArray();
	}

	private static void WriteBlendGridTake( BinaryWriter writer, int sample, float sampleOffset )
	{
		writer.Write( 0 ); // No constant channels.
		writer.Write( 25 ); // Animated positions follow the 24-byte header and one bone flag.
		writer.Write( 12 );
		for ( var unused = 0; unused < 3; unused++ ) writer.Write( 0 );
		writer.Write( (byte)0x10 );
		for ( var frame = 0; frame < 2; frame++ )
		{
			writer.Write( sampleOffset + sample * 100f + frame );
			writer.Write( 20f + sample );
			writer.Write( 30f );
		}
	}

	private static Dictionary<string, byte[]> VirtualFiles( byte[] library ) => new()
	{
		["models/root.mdl"] = VirtualFixture( new[] { "root" }, new[] { 10f }, includes: new[] { "models/lib.mdl" } ),
		["models/lib.mdl"] = library,
	};

	private static IReadOnlyList<SourceAnimation> DecodeVirtual( Dictionary<string, byte[]> files )
	{
		Stream Open( string path ) => files.TryGetValue( path, out var bytes ) ? new MemoryStream( bytes ) : throw new FileNotFoundException( path );
		using var root = Open( "models/root.mdl" );
		var mdl = new MdlFile( root );
		return mdl.ReadAnimations( mdl.ResolveIncludes( "models/root.mdl", Open ), root, Open );
	}

	private static byte[] VirtualFixture( string[] names, float[] positions, string sequence = null, string animation = null,
		string[] includes = null, float weight = 1f, byte[] original = null )
	{
		original ??= BuildTestMdl();
		using var ms = new MemoryStream();
		using var w = new BinaryWriter( ms );
		w.Write( original );
		var originalBone = BitConverter.ToInt32( original, 160 );
		var boneTable = ms.Position;
		Patch( w, ms, 156, names.Length );
		Patch( w, ms, 160, (int)boneTable );
		for ( var i = 0; i < names.Length; i++ )
		{
			w.Write( original, originalBone, 216 );
			Patch( w, ms, boneTable + i * 216 + 4, i == 0 ? -1 : 0 );
			Patch( w, ms, boneTable + i * 216 + 32, BitConverter.SingleToInt32Bits( positions[i] ) );
		}
		for ( var i = 0; i < names.Length; i++ )
		{
			Patch( w, ms, boneTable + i * 216, (int)(ms.Position - boneTable - i * 216) );
			w.Write( Encoding.UTF8.GetBytes( names[i] + "\0" ) );
		}
		Patch( w, ms, 188, sequence == null ? 0 : 1 );
		Patch( w, ms, 180, sequence == null ? 0 : 1 );
		if ( sequence != null )
		{
			var seq = BitConverter.ToInt32( original, 192 );
			var desc = BitConverter.ToInt32( original, 184 );
			Patch( w, ms, seq + 4, (int)ms.Position - seq );
			w.Write( Encoding.UTF8.GetBytes( sequence + "\0" ) );
			Patch( w, ms, desc + 4, (int)ms.Position - desc );
			w.Write( Encoding.UTF8.GetBytes( animation + "\0" ) );
			Patch( w, ms, seq + 156, (int)ms.Position - seq );
			for ( var i = 0; i < names.Length; i++ ) w.Write( weight );
		}
		includes ??= Array.Empty<string>();
		Patch( w, ms, 336, includes.Length );
		Patch( w, ms, 340, (int)ms.Position );
		var table = ms.Position;
		w.Write( new byte[includes.Length * 8] );
		for ( var i = 0; i < includes.Length; i++ )
		{
			Patch( w, ms, table + i * 8 + 4, (int)(ms.Position - table - i * 8) );
			w.Write( Encoding.UTF8.GetBytes( includes[i] + "\0" ) );
		}
		Patch( w, ms, 76, (int)ms.Length );
		return ms.ToArray();
	}
}
