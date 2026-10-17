using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Sandbox.Mounting.Source1;

/// <summary>
/// One decoded Source sequence: bone-local transforms for every frame, ready to emit.
/// </summary>
internal sealed class SourceAnimation
{
	public string Name;
	public float Fps;
	public bool Looping;
	public int Frames;
	/// <summary>Bone-local transforms, <c>Frames * boneCount</c>, in file space (inches).</summary>
	public Transform[] LocalFrames;
}

internal sealed partial class MdlFile
{
	private const int MaxFramesPerDesc = 1 << 22;
	/// <summary>Budget of bone-frames decoded per model; further sequences are skipped.</summary>
	private const long MaxTotalBoneFrames = 1 << 20;

	/// <summary>
	/// Decodes supported sequence samples, exposing blend-grid cells as individual clips.
	/// A sample is skipped (never fatal) when it is static, delta/additive, external and unavailable, uses
	/// reparenting or IK, or would exceed the decode budget. Extracted root motion
	/// is ignored so otherwise supported locomotion clips play in place.
	/// </summary>
	internal IReadOnlyList<SourceAnimation> ReadAnimations( Stream stream, Func<string, Stream> openFile )
	{
		var animations = new List<SourceAnimation>();
		if ( IsStaticProp || BoneCount == 0 || SequenceCount == 0 )
			return animations;

		long totalBoneFrames = 0;
		var names = new HashSet<string>( _sequenceNames, StringComparer.OrdinalIgnoreCase );
		using var data = new AnimationData( stream, 0, stream.Length );
		for ( var i = 0; i < SequenceCount; i++ )
		{
			foreach ( var sample in GetSequenceAnimationSamples( i, names ) )
			{
				try
				{
					var animation = ReadSequenceAnimation( data, stream, openFile, i, sample.DescIndex, sample.Name, ref totalBoneFrames );
					if ( animation != null )
						animations.Add( animation );
				}
				catch ( Exception e ) when ( e is InvalidDataException or ArgumentOutOfRangeException or FileNotFoundException )
				{
					Log.Trace( $"{sample.Name}: animation was not imported: {e.Message}" );
				}
			}
		}
		return animations;
	}

	private SourceAnimation ReadSequenceAnimation( AnimationData data, Stream stream, Func<string, Stream> openFile, int sequenceIndex, int descIndex, string name, ref long totalBoneFrames )
	{
		var sequence = GetSequence( sequenceIndex );
		if ( (sequence.Flags & 0x04) != 0 )
			return null;
		var desc = GetAnimDesc( descIndex );
		if ( desc.NumFrames <= 1 || desc.NumFrames > MaxFramesPerDesc )
			return null;
		if ( (desc.Flags & 0x04) != 0 || (desc.Flags & 0x20) != 0 )
			return null;
		if ( desc.NumLocalHierarchy != 0 || desc.NumIkRules != 0 || desc.NumMovements < 0 )
			return null;

		// Source stores extracted travel separately from the bone animation. Leave
		// MovementIndex unread for an in-place fallback; keep authored bone motion
		// (including root bob) rather than freezing the root transform.

		var frames = desc.NumFrames;
		if ( totalBoneFrames + (long)frames * BoneCount > MaxTotalBoneFrames )
		{
			Log.Trace( $"{GetSequenceName( sequenceIndex )}: animation skipped: decode budget exceeded." );
			return null;
		}

		// Charge attempted decodes too, including malformed and motionless takes.
		totalBoneFrames += (long)frames * BoneCount;
		var tracks = ReadTake( data, stream, openFile, descIndex, desc, frames );
		if ( tracks == null || !TakeMoves( tracks ) )
			return null;

		var locals = new Transform[(long)frames * BoneCount];
		for ( var frame = 0; frame < frames; frame++ )
		{
			for ( var bone = 0; bone < BoneCount; bone++ )
			{
				var bind = Bones[bone];
				var position = bind.Position;
				var rotation = bind.Quaternion;
				if ( tracks[bone] != null )
				{
					var track = tracks[bone];
					var values = track.Frames == 1 ? 0 : frame;
					if ( values < track.Frames )
					{
						var qx = track.Rotation[values * 4];
						var qy = track.Rotation[values * 4 + 1];
						var qz = track.Rotation[values * 4 + 2];
						var qw = track.Rotation[values * 4 + 3];
						if ( qx != 0f || qy != 0f || qz != 0f || qw != 0f )
						{
							rotation = new Rotation( qx, qy, qz, qw );
						}
						for ( var axis = 0; axis < 3; axis++ )
						{
							var value = track.Position[values * 3 + axis];
							if ( !float.IsNaN( value ) )
							{
								if ( axis == 0 ) position.x = value;
								else if ( axis == 1 ) position.y = value;
								else position.z = value;
							}
						}
					}
				}
				if ( !float.IsFinite( position.x ) || !float.IsFinite( position.y ) || !float.IsFinite( position.z )
					|| !float.IsFinite( rotation.x ) || !float.IsFinite( rotation.y ) || !float.IsFinite( rotation.z ) || !float.IsFinite( rotation.w ) )
					throw new InvalidDataException( "Non-finite animation frame." );
				locals[(long)frame * BoneCount + bone] = new Transform( position, rotation );
			}
		}

		return new SourceAnimation
		{
			Name = string.IsNullOrWhiteSpace( name ) ? $"sequence_{sequenceIndex}" : name,
			Fps = float.IsFinite( desc.Fps ) && desc.Fps > 0f ? desc.Fps : 30f,
			Looping = (GetSequence( sequenceIndex ).Flags & 0x1) != 0 || (desc.Flags & 0x1) != 0,
			Frames = frames,
			LocalFrames = locals,
		};
	}

	/// <summary>Bone tracks of one animation description, indexed by bone, or null when unavailable.</summary>
	private BoneTrack[] ReadTake( AnimationData data, Stream stream, Func<string, Stream> openFile, int descIndex, StudioAnimDesc desc, int frames )
	{
		var sections = ReadAnimSections( data, descIndex, desc );
		if ( sections.Count > 0 )
		{
			// Mixed external sections are not supported: never emit a partial take.
			if ( sections.Any( section => section.Block != 0 ) )
				return null;
			var merged = new BoneTrack[BoneCount];
			var frameOffset = 0;
			while ( frameOffset < frames )
			{
				// Source stores the final frame of a long take in its own section.
				var last = frames > desc.SectionFrames && frameOffset == frames - 1;
				var id = last ? frames / desc.SectionFrames + 1 : frameOffset / desc.SectionFrames;
				var count = last ? 1 : Math.Min( desc.SectionFrames, frames - frameOffset );
				if ( frames > desc.SectionFrames && !last )
					count = Math.Min( count, frames - 1 - frameOffset );
				var section = sections[id];
				if ( section.Offset <= 0 )
					throw new InvalidDataException( "Missing animation section data." );
				data.Seek( AnimDescEntry( descIndex ) + section.Offset );
				var chunk = ReadChunk( data, desc, count );
				if ( chunk == null )
					return null;
				MergeTake( merged, chunk, frameOffset, count, frames );
				frameOffset += count;
			}
			return merged;
		}

		if ( desc.AnimBlock != 0 )
			return ReadExternalTake( stream, openFile, desc, frames );
		if ( desc.AnimIndex <= 0 )
			return null;
		data.Seek( AnimDescEntry( descIndex ) + desc.AnimIndex );
		return ReadChunk( data, desc, frames );
	}

	private long AnimDescEntry( int descIndex ) => (long)_header.LocalAnimIndex + (long)descIndex * StudioAnimDescSize;

	private static readonly int StudioAnimDescSize = Marshal.SizeOf<StudioAnimDesc>();

	private readonly struct AnimSection( int block, long offset )
	{
		public readonly int Block = block;
		public readonly long Offset = offset;
	}

	private List<AnimSection> ReadAnimSections( AnimationData data, int descIndex, StudioAnimDesc desc )
	{
		var sections = new List<AnimSection>();
		if ( desc.SectionFrames < 0 || (desc.SectionFrames > 0 && desc.SectionIndex <= 0) )
			throw new InvalidDataException( "Invalid animation section table." );
		if ( desc.SectionFrames > 0 )
		{
			var count = desc.NumFrames / desc.SectionFrames + 2;
			if ( count < 0 || count > 256 )
				throw new InvalidDataException( "Invalid animation section count." );
			data.Seek( AnimDescEntry( descIndex ) + desc.SectionIndex );
			for ( var i = 0; i < count; i++ )
				sections.Add( new AnimSection( data.I32(), data.I32() ) );
		}
		return sections;
	}

	/// <summary>Whole-description take stored in an external .ani file.</summary>
	private BoneTrack[] ReadExternalTake( Stream stream, Func<string, Stream> openFile, StudioAnimDesc desc, int frames )
	{
		if ( desc.SectionIndex != 0 || openFile == null )
			return null;
		var path = ReadOptionalRelativeString( stream, 0, _header.AnimBlockNameOffset ).Replace( '\\', '/' );
		if ( string.IsNullOrWhiteSpace( path ) || path.StartsWith( '/' ) || path.Contains( ':' )
			|| path.Split( '/' ).Any( part => part == ".." ) )
			throw new InvalidDataException( "Invalid animation file path." );
		using var external = openFile( path );
		if ( !external.CanSeek )
			throw new InvalidDataException( "External animation file is not seekable." );
		var start = ReadBlockExtent( stream, desc.AnimBlock, true );
		var end = ReadBlockExtent( stream, desc.AnimBlock, false );
		using var data = new AnimationData( external, start, end );
		data.Seek( desc.AnimIndex );
		return ReadChunk( data, desc, frames );
	}

	private long ReadBlockExtent( Stream stream, int block, bool first )
	{
		if ( block < 1 || block >= _header.NumAnimBlocks )
			throw new InvalidDataException( "Invalid animation block." );
		ValidateRange<StudioAnimBlock>( stream, _header.AnimBlockOffset, _header.NumAnimBlocks );
		using var data = new AnimationData( stream, 0, _header.Length );
		return data.IntAt( (long)_header.AnimBlockOffset + block * 8L + (first ? 0 : 4) );
	}

	private BoneTrack[] ReadChunk( AnimationData data, StudioAnimDesc desc, int frames )
	{
		if ( (desc.Flags & 0x40) != 0 )
			return ReadFrameAnimationFull( data, frames );
		return ReadBoneChainFull( data, frames );
	}

	private BoneTrack[] ReadBoneChainFull( AnimationData data, int frames )
	{
		var tracks = new BoneTrack[BoneCount];
		var previous = -1;
		for ( var n = 0; n < BoneCount; n++ )
		{
			var boneEntry = data.Position;
			var boneIndex = data.U8();
			if ( boneIndex == 255 )
			{
				data.Skip( 3 );
				break;
			}
			if ( boneIndex >= BoneCount || boneIndex <= previous )
				throw new InvalidDataException( "Animation bone index out of range." );
			var bone = Bones[boneIndex];
			var flags = data.U8();
			var next = data.I16();
			if ( (flags & ~0x3f) != 0 || (flags & 0x10) != 0 || (flags & 5) == 5
				|| ((flags & 2) != 0 && (flags & 0x28) != 0) || (flags & 0x28) == 0x28
				|| next < 0 || (next != 0 && next < 4) )
				throw new InvalidDataException( "Unsupported animation bone flags or link." );

			var track = BoneTrack.New( frames );
			var payload = boneEntry + 4;
			ReadRotationFull( data, flags, frames, bone, track );
			var positionOffset = (flags & 1) != 0
				? ((flags & 2) != 0 ? 6 : (flags & 0x20) != 0 ? 8 : 0)
				: ((flags & 8) != 0 ? 6 : 0);
			data.Seek( payload + positionOffset );
			ReadPositionFull( data, flags, frames, bone, track );
			tracks[boneIndex] = track;

			if ( next > 0 )
			{
				previous = boneIndex;
				data.Seek( boneEntry + next );
				continue;
			}
			break;
		}
		return tracks;
	}

	private void ReadRotationFull( AnimationData data, int flags, int frames, StudioBone bone, BoneTrack track )
	{
		float x, y, z, w;
		if ( (flags & 0x02) != 0 )
		{
			data.Quaternion48( out x, out y, out z, out w );
		}
		else if ( (flags & 0x20) != 0 )
		{
			data.Quaternion64( out x, out y, out z, out w );
		}
		else if ( (flags & 0x08) == 0 )
		{
			if ( (flags & 0x10) != 0 ) { x = 0f; y = 0f; z = 0f; w = 1f; }
			else { x = bone.Quaternion.x; y = bone.Quaternion.y; z = bone.Quaternion.z; w = bone.Quaternion.w; }
		}
		else
		{
			var entry = data.Position;
			var xo = data.I16();
			var yo = data.I16();
			var zo = data.I16();
			var after = data.Position;
			if ( (xo != 0 && xo < 6) || (yo != 0 && yo < 6) || (zo != 0 && zo < 6) )
				throw new InvalidDataException( "Invalid animation RLE pointer." );
			var ax = new short[frames];
			var ay = new short[frames];
			var az = new short[frames];
			if ( xo > 0 ) { data.Seek( entry + xo ); ax = data.ReadRleAll( frames ); }
			if ( yo > 0 ) { data.Seek( entry + yo ); ay = data.ReadRleAll( frames ); }
			if ( zo > 0 ) { data.Seek( entry + zo ); az = data.ReadRleAll( frames ); }
			data.Seek( after );

			for ( var f = 0; f < frames; f++ )
			{
				var roll = (ax[f] * bone.RotationScale.x + bone.Rotation.x) * 0.5;
				var pitch = (ay[f] * bone.RotationScale.y + bone.Rotation.y) * 0.5;
				var yaw = (az[f] * bone.RotationScale.z + bone.Rotation.z) * 0.5;
				var sr = Math.Sin( roll );
				var cr = Math.Cos( roll );
				var sp = Math.Sin( pitch );
				var cp = Math.Cos( pitch );
				var sy = Math.Sin( yaw );
				var cy = Math.Cos( yaw );
				track.SetQuaternion( f,
					(float)(sr * cp * cy - cr * sp * sy),
					(float)(cr * sp * cy + sr * cp * sy),
					(float)(cr * cp * sy - sr * sp * cy),
					(float)(cr * cp * cy + sr * sp * sy) );
			}
			return;
		}
		for ( var f = 0; f < frames; f++ ) track.SetQuaternion( f, x, y, z, w );
	}

	private void ReadPositionFull( AnimationData data, int flags, int frames, StudioBone bone, BoneTrack track )
	{
		if ( (flags & 0x04) != 0 && (flags & 0x01) == 0 )
		{
			var entry = data.Position;
			var xo = data.I16();
			var yo = data.I16();
			var zo = data.I16();
			var after = data.Position;
			if ( (xo != 0 && xo < 6) || (yo != 0 && yo < 6) || (zo != 0 && zo < 6) )
				throw new InvalidDataException( "Invalid animation RLE pointer." );
			var ax = new short[frames];
			var ay = new short[frames];
			var az = new short[frames];
			if ( xo > 0 ) { data.Seek( entry + xo ); ax = data.ReadRleAll( frames ); }
			if ( yo > 0 ) { data.Seek( entry + yo ); ay = data.ReadRleAll( frames ); }
			if ( zo > 0 ) { data.Seek( entry + zo ); az = data.ReadRleAll( frames ); }
			data.Seek( after );
			for ( var f = 0; f < frames; f++ )
			{
				track.Position[f * 3] = ax[f] * bone.PositionScale.x + bone.Position.x;
				track.Position[f * 3 + 1] = ay[f] * bone.PositionScale.y + bone.Position.y;
				track.Position[f * 3 + 2] = az[f] * bone.PositionScale.z + bone.Position.z;
			}
			return;
		}

		float x, y, z;
		if ( (flags & 0x01) != 0 )
		{
			x = data.Half(); y = data.Half(); z = data.Half();
		}
		else if ( (flags & 0x10) != 0 )
		{
			x = 0f; y = 0f; z = 0f;
		}
		else
		{
			x = bone.Position.x; y = bone.Position.y; z = bone.Position.z;
		}
		for ( var f = 0; f < frames; f++ )
		{
			track.Position[f * 3] = x;
			track.Position[f * 3 + 1] = y;
			track.Position[f * 3 + 2] = z;
		}
	}

	private BoneTrack[] ReadFrameAnimationFull( AnimationData data, int frames )
	{
		var entry = data.Position;
		var constantOffset = data.I32();
		var frameOffset = data.I32();
		var frameLength = data.I32();
		data.Skip( 12 );
		var boneFlags = new int[BoneCount];
		var constantSize = 0;
		var frameSize = 0;
		for ( var i = 0; i < boneFlags.Length; i++ )
		{
			var flags = boneFlags[i] = data.U8();
			var positions = flags & 0x35;
			var rotations = flags & 0xca;
			if ( (positions & (positions - 1)) != 0 || (rotations & (rotations - 1)) != 0 )
				throw new InvalidDataException( "Conflicting frame animation channels." );
			if ( (flags & 0x88) != 0 ) frameSize += 6;
			if ( (flags & 4) != 0 ) frameSize += 6;
			if ( (flags & 0x10) != 0 ) frameSize += 12;
			if ( (flags & 0x42) != 0 ) constantSize += 6;
			if ( (flags & 1) != 0 ) constantSize += 6;
			if ( (flags & 0x20) != 0 ) constantSize += 12;
		}
		if ( frameLength < frameSize || constantOffset < 0 || frameOffset < 0
			|| (constantSize > 0 && constantOffset < 24 + BoneCount)
			|| (frameSize > 0 && frameOffset < 24 + BoneCount) )
			throw new InvalidDataException( "Invalid frame animation offsets or stride." );

		var tracks = new BoneTrack[BoneCount];
		if ( constantSize > 0 )
		{
			data.Seek( entry + constantOffset );
			for ( var i = 0; i < BoneCount; i++ )
			{
				var flag = boneFlags[i];
				if ( flag == 0 ) continue;
				var track = tracks[i] = BoneTrack.New( frames );
				float x = float.NaN, y = float.NaN, z = float.NaN;
				float qx = 0, qy = 0, qz = 0, qw = 0;
				if ( (flag & 0x40) != 0 ) data.Quaternion48S( out qx, out qy, out qz, out qw );
				if ( (flag & 0x20) != 0 ) { x = data.Float(); y = data.Float(); z = data.Float(); }
				if ( (flag & 2) != 0 ) data.Quaternion48( out qx, out qy, out qz, out qw );
				if ( (flag & 1) != 0 ) { x = data.Half(); y = data.Half(); z = data.Half(); }
				for ( var frame = 0; frame < frames; frame++ )
				{
					track.SetQuaternion( frame, qx, qy, qz, qw );
					track.Position[frame * 3] = x;
					track.Position[frame * 3 + 1] = y;
					track.Position[frame * 3 + 2] = z;
				}
			}
		}
		if ( frameSize > 0 )
		{
			for ( var frame = 0; frame < frames; frame++ )
			{
				data.Seek( entry + frameOffset + (long)frame * frameLength );
				for ( var i = 0; i < BoneCount; i++ )
				{
					var flag = boneFlags[i];
					if ( (flag & 0x9c) == 0 ) continue;
					var track = tracks[i] ??= BoneTrack.New( frames );
					float x, y, z, w;
					if ( (flag & 0x80) != 0 ) { data.Quaternion48S( out x, out y, out z, out w ); track.SetQuaternion( frame, x, y, z, w ); }
					if ( (flag & 8) != 0 ) { data.Quaternion48( out x, out y, out z, out w ); track.SetQuaternion( frame, x, y, z, w ); }
					if ( (flag & 4) != 0 ) { track.Position[frame * 3] = data.Half(); track.Position[frame * 3 + 1] = data.Half(); track.Position[frame * 3 + 2] = data.Half(); }
					if ( (flag & 0x10) != 0 ) { track.Position[frame * 3] = data.Float(); track.Position[frame * 3 + 1] = data.Float(); track.Position[frame * 3 + 2] = data.Float(); }
				}
			}
		}
		return tracks;
	}

	private static void MergeTake( BoneTrack[] into, BoneTrack[] chunk, int frameOffset, int count, int frameCount )
	{
		for ( var bone = 0; bone < into.Length; bone++ )
		{
			var source = chunk[bone];
			if ( source == null )
				continue;
			var target = into[bone] ??= BoneTrack.New( frameCount, present: false );
			for ( var k = 0; k < count && frameOffset + k < frameCount; k++ )
			{
				var s = source.Frames == 1 ? 0 : k;
				if ( s >= source.Frames )
					break;
				var d = frameOffset + k;
				target.RecordPresent[d] = source.RecordPresent[s];
				for ( var i = 0; i < 3; i++ ) target.Position[d * 3 + i] = source.Position[s * 3 + i];
				for ( var i = 0; i < 4; i++ ) target.Rotation[d * 4 + i] = source.Rotation[s * 4 + i];
			}
		}
	}

	/// <summary>True when any tracked bone moves relative to its first frame.</summary>
	private bool TakeMoves( BoneTrack[] tracks )
	{
		for ( var bone = 0; bone < BoneCount; bone++ )
		{
			var track = tracks[bone];
			if ( track == null || track.Frames <= 1 )
				continue;
			var bind = Bones[bone];
			var first = new double[3];
			var firstQuat = new double[4];
			TrackFrame( bind, track, 0, first, firstQuat );
			var position = new double[3];
			var quat = new double[4];
			for ( var frame = 1; frame < track.Frames; frame++ )
			{
				TrackFrame( bind, track, frame, position, quat );
				if ( !SameTransform( first, firstQuat, position, quat ) )
					return true;
			}
		}
		return false;
	}

	/// <summary>File-space transform of one bone at one frame; uncovered data falls back to the bind pose.</summary>
	private static void TrackFrame( StudioBone bind, BoneTrack track, int frame, double[] position, double[] quat )
	{
		frame = Math.Min( frame, track.Frames - 1 );
		if ( track.Rotation[frame * 4] == 0f && track.Rotation[frame * 4 + 1] == 0f
			&& track.Rotation[frame * 4 + 2] == 0f && track.Rotation[frame * 4 + 3] == 0f )
		{
			quat[0] = bind.Quaternion.x; quat[1] = bind.Quaternion.y; quat[2] = bind.Quaternion.z; quat[3] = bind.Quaternion.w;
		}
		else
		{
			for ( var i = 0; i < 4; i++ ) quat[i] = track.Rotation[frame * 4 + i];
		}
		for ( var i = 0; i < 3; i++ )
		{
			var value = track.Position[frame * 3 + i];
			position[i] = float.IsNaN( value )
				? (i == 0 ? bind.Position.x : i == 1 ? bind.Position.y : bind.Position.z)
				: value;
		}
	}

	private static bool SameTransform( double[] positionA, double[] quatA, double[] positionB, double[] quatB )
	{
		for ( var i = 0; i < 3; i++ )
			if ( Math.Abs( positionA[i] - positionB[i] ) > 1e-4 )
				return false;
		var lengthA = Math.Sqrt( quatA[0] * quatA[0] + quatA[1] * quatA[1] + quatA[2] * quatA[2] + quatA[3] * quatA[3] );
		var lengthB = Math.Sqrt( quatB[0] * quatB[0] + quatB[1] * quatB[1] + quatB[2] * quatB[2] + quatB[3] * quatB[3] );
		if ( lengthA == 0.0 || lengthB == 0.0 )
			return lengthA == lengthB;
		var dot = 0.0;
		for ( var i = 0; i < 4; i++ ) dot += quatA[i] / lengthA * (quatB[i] / lengthB);
		return 1.0 - Math.Abs( dot ) <= 1e-5 * 1e-5;
	}

	/// <summary>Per-bone decoded frames: positions (NaN where uncovered) and quaternions (zero where uncovered).</summary>
	private sealed class BoneTrack
	{
		public int Frames;
		public float[] Position;
		public float[] Rotation;
		public bool[] RecordPresent;

		public static BoneTrack New( int frames, bool present = true )
		{
			var track = new BoneTrack { Frames = frames, Position = new float[frames * 3], Rotation = new float[frames * 4], RecordPresent = new bool[frames] };
			Array.Fill( track.Position, float.NaN );
			Array.Fill( track.RecordPresent, present );
			return track;
		}

		public void SetQuaternion( int frame, float x, float y, float z, float w )
		{
			Rotation[frame * 4] = x;
			Rotation[frame * 4 + 1] = y;
			Rotation[frame * 4 + 2] = z;
			Rotation[frame * 4 + 3] = w;
		}
	}

	/// <summary>Bounded absolute + sequential reader over an MDL or .ani stream.</summary>
	private sealed class AnimationData : IDisposable
	{
		private readonly BinaryReader reader;
		private readonly long start;
		private readonly long length;
		private long position;

		public AnimationData( Stream stream, long start, long end )
		{
			if ( !stream.CanSeek || start < 0 || end < start || end > stream.Length )
				throw new InvalidDataException( "Invalid animation block extent." );
			reader = new BinaryReader( stream, Encoding.UTF8, leaveOpen: true );
			this.start = start;
			length = end - start;
		}

		public void Dispose() => reader.Dispose();

		public long Position => position;

		public void Seek( long offset )
		{
			if ( offset < 0 || offset > length )
				throw new InvalidDataException( "Animation read exceeds its block." );
			position = offset;
		}

		public void Skip( long count ) => Seek( position + count );

		private void Require( int size )
		{
			if ( size < 0 || position > length - size )
				throw new InvalidDataException( "Animation read exceeds its block." );
			reader.BaseStream.Position = start + position;
			position += size;
		}

		public byte U8() { Require( 1 ); return reader.ReadByte(); }
		public short I16() { Require( 2 ); return reader.ReadInt16(); }
		public int I32() { Require( 4 ); return reader.ReadInt32(); }
		public float Float() => BitConverter.Int32BitsToSingle( I32() );
		public float Half() => (float)BitConverter.UInt16BitsToHalf( (ushort)I16() );

		public int IntAt( long offset )
		{
			Seek( offset );
			return I32();
		}

		public void Quaternion48( out float x, out float y, out float z, out float w )
		{
			var rawX = (int)(ushort)I16();
			var rawY = (int)(ushort)I16();
			var rawZ = (int)(ushort)I16();
			x = (float)((rawX - 32768) * (1.0 / 32768.0));
			y = (float)((rawY - 32768) * (1.0 / 32768.0));
			z = (float)(((rawZ & 0x7fff) - 16384) * (1.0 / 16384.0));
			var ww = 1.0 - (double)x * x - (double)y * y - (double)z * z;
			if ( ww < -0.0002 )
				throw new InvalidDataException( "Invalid packed animation quaternion." );
			w = (float)Math.Sqrt( Math.Max( 0, ww ) );
			if ( ((rawZ >> 15) & 1) != 0 ) w = -w;
		}

		public void Quaternion64( out float x, out float y, out float z, out float w )
		{
			Require( 8 );
			var b0 = reader.ReadUInt32();
			var b1 = reader.ReadUInt32();
			var xs = (long)(b0 & 0x1fffff);
			var ys = (long)(((b1 & 0x03ff) << 11) | (b0 >> 21));
			var zs = (long)((b1 >> 10) & 0x1fffff);
			var negative = (b1 & 0x80000000u) != 0;
			x = (float)((xs - 1048576) * (1.0 / 1048576.5));
			y = (float)((ys - 1048576) * (1.0 / 1048576.5));
			z = (float)((zs - 1048576) * (1.0 / 1048576.5));
			var ww = 1.0 - (double)x * x - (double)y * y - (double)z * z;
			if ( ww < -0.000003 )
				throw new InvalidDataException( "Invalid packed animation quaternion." );
			w = (float)Math.Sqrt( Math.Max( 0, ww ) );
			if ( negative ) w = -w;
		}

		public void Quaternion48S( out float x, out float y, out float z, out float w )
		{
			var d0 = (uint)I16() & 0xffff;
			var d1 = (uint)I16() & 0xffff;
			var d2 = (uint)I16() & 0xffff;
			var a = (int)(d0 & 0x7fff);
			var offsetHigh = (int)(d0 >> 15);
			var b = (int)(d1 & 0x7fff);
			var offsetLow = (int)(d1 >> 15);
			var c = (int)(d2 & 0x7fff);
			var negative = (d2 >> 15) != 0;

			var ia = offsetLow + offsetHigh * 2;
			var ib = (ia + 1) % 4;
			var ic = (ia + 2) % 4;
			var id = (ia + 3) % 4;

			var quat = new double[4];
			quat[ia] = (a - 16384) * (1.0 / 23168.0);
			quat[ib] = (b - 16384) * (1.0 / 23168.0);
			quat[ic] = (c - 16384) * (1.0 / 23168.0);
			var rest = 1.0 - quat[ia] * quat[ia] - quat[ib] * quat[ib] - quat[ic] * quat[ic];
			if ( rest < -0.0002 )
				throw new InvalidDataException( "Invalid packed animation quaternion." );
			quat[id] = Math.Sqrt( Math.Max( 0, rest ) );
			if ( negative ) quat[id] = -quat[id];
			x = (float)quat[0]; y = (float)quat[1]; z = (float)quat[2]; w = (float)quat[3];
		}

		/// <summary>Reads a run-length encoded channel and returns all values.</summary>
		public short[] ReadRleAll( int frameCount )
		{
			var result = new short[frameCount];
			var offset = 0;
			while ( offset < frameCount )
			{
				var valid = U8();
				var total = U8();
				if ( valid == 0 || total == 0 || valid > total )
					throw new InvalidDataException( "Invalid animation RLE run." );
				short value = 0;
				for ( var i = 0; i < valid; i++ )
				{
					value = I16();
					if ( offset + i < frameCount ) result[offset + i] = value;
				}
				for ( var i = valid; i < total && offset + i < frameCount; i++ )
					result[offset + i] = value;
				offset += total;
			}
			return result;
		}
	}
}
