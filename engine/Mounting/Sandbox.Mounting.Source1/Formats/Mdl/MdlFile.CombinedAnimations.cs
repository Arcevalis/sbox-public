using System;
using System.Collections.Generic;
using System.IO;

namespace Sandbox.Mounting.Source1;

internal sealed partial class MdlFile
{
	/// <summary>
	/// Target, sequence skeleton and animation skeleton are independent. Low-level
	/// readers run on the animation owner's instance, including bind and scales.
	/// </summary>
	internal IReadOnlyList<SourceAnimation> ReadAnimations( IncludeGraph graph, Stream rootStream, Func<string, Stream> openFile )
	{
		var result = new List<SourceAnimation>();
		if ( IsStaticProp || BoneCount == 0 )
			return result;

		var boneMaps = new int[graph.Nodes.Count][];
		for ( var group = 0; group < graph.Nodes.Count; group++ )
			boneMaps[group] = MapBones( graph.Nodes[group].Mdl );
		var streams = new Dictionary<int, Stream> { [0] = rootStream };
		var unavailable = new HashSet<int>();
		var names = new HashSet<string>( StringComparer.OrdinalIgnoreCase );
		foreach ( var slot in graph.Sequences )
			names.Add( graph.Nodes[slot.Group].Mdl.GetSequenceName( slot.LocalIndex ) );
		long attemptedBoneFrames = 0;
		Stream GetStream( int group )
		{
			if ( unavailable.Contains( group ) )
				throw new InvalidDataException( "Library stream is unavailable." );
			if ( streams.TryGetValue( group, out var stream ) )
				return stream;
			try
			{
				stream = openFile( graph.Nodes[group].Path );
				if ( stream == null )
					throw new FileNotFoundException( "Library stream is unavailable." );
				streams.Add( group, stream );
				return stream;
			}
			catch
			{
				unavailable.Add( group );
				throw;
			}
		}

		try
		{
			foreach ( var slot in graph.Sequences )
			{
				var node = graph.Nodes[slot.Group];
				var sequenceOwner = node.Mdl;
				if ( sequenceOwner.GetSequenceAnimDescs( slot.LocalIndex ).Length == 0 )
					Log.Trace( $"{node.Path}:{sequenceOwner.GetSequenceName( slot.LocalIndex )}: animation skipped: no valid blend." );
				foreach ( var sample in sequenceOwner.GetSequenceAnimationSamples( slot.LocalIndex, names ) )
				{
					try
					{
						var animationSlot = graph.Animations[node.MasterAnim[sample.DescIndex]];
						var animationNode = graph.Nodes[animationSlot.Group];
						var animation = ReadCombinedSequence( sequenceOwner, GetStream( slot.Group ), slot.LocalIndex, sample.Name,
							animationNode.Mdl, GetStream( animationSlot.Group ), animationSlot.LocalIndex,
							path => OpenAnimationDependency( animationNode.Path, path, openFile ),
							boneMaps[slot.Group], boneMaps[animationSlot.Group], ref attemptedBoneFrames );
						if ( animation != null )
							result.Add( animation );
					}
					catch ( Exception e ) when ( e is IOException or ArgumentException or NotSupportedException or InvalidDataException or UnauthorizedAccessException )
					{
						Log.Trace( $"{node.Path}:{sample.Name}: animation skipped: {e.Message}" );
					}
				}
			}
		}
		finally
		{
			foreach ( var pair in streams )
			{
				if ( pair.Key == 0 ) continue; // The caller owns the root stream.
				try
				{
					pair.Value.Dispose();
				}
				catch ( Exception e ) when ( e is IOException or NotSupportedException or UnauthorizedAccessException )
				{
					Log.Trace( $"{graph.Nodes[pair.Key].Path}: library stream cleanup failed: {e.Message}" );
				}
			}
		}
		Log.Trace( $"{graph.Nodes[0].Path}: imported {result.Count} animation samples from {graph.Sequences.Count} virtual sequences; attempted {attemptedBoneFrames}/{MaxTotalBoneFrames} bone-frames." );
		return result;
	}

	private static Stream OpenAnimationDependency( string ownerPath, string path, Func<string, Stream> openFile )
	{
		path = path.Replace( '\\', '/' );
		var resolved = path.StartsWith( "models/", StringComparison.OrdinalIgnoreCase )
			? path : ownerPath[..(ownerPath.LastIndexOf( '/' ) + 1)] + path;
		return openFile( resolved ) ?? throw new FileNotFoundException( "Animation dependency is unavailable.", resolved );
	}

	private SourceAnimation ReadCombinedSequence( MdlFile sequenceOwner, Stream sequenceStream, int sequenceIndex, string name,
		MdlFile animationOwner, Stream animationStream, int descIndex, Func<string, Stream> openFile,
		int[] sequenceMap, int[] animationMap, ref long attemptedBoneFrames )
	{
		var sequence = sequenceOwner.GetSequence( sequenceIndex );
		var desc = animationOwner.GetAnimDesc( descIndex );
		SourceAnimation Skip( string reason )
		{
			Log.Trace( $"{name}: animation skipped: {reason}." );
			return null;
		}
		if ( (sequence.Flags & 0x04) != 0 || (desc.Flags & 0x24) != 0 ) return Skip( "delta/additive" );
		if ( sequence.NumAutoLayers != 0 ) return Skip( "autolayers" );
		if ( desc.NumLocalHierarchy != 0 ) return Skip( "reparenting" );
		if ( desc.NumMovements < 0 || desc.NumIkRules < 0 ) return Skip( "invalid descriptor counts" );
		if ( desc.NumFrames <= 1 || desc.NumFrames > MaxFramesPerDesc ) return Skip( "static or invalid frame count" );
		if ( desc.NumIkRules != 0 || sequence.NumIkRules != 0 || sequence.NumIkLocks != 0 )
			Log.Trace( $"{name}: ik-ignored; skeletal channels only." );

		// Weight indices belong to the sequence skeleton, not the animation or target.
		if ( sequence.WeightListIndex <= 0 ) return Skip( "missing weightlist" );
		var sequenceEntry = (long)sequenceOwner._header.LocalSeqIndex + (long)sequenceIndex * System.Runtime.InteropServices.Marshal.SizeOf<StudioSeqDesc>();
		sequenceOwner.ValidateRange<float>( sequenceStream, sequenceEntry + sequence.WeightListIndex, sequenceOwner.BoneCount );
		using ( var weights = new AnimationData( sequenceStream, 0, sequenceOwner._header.Length ) )
		{
			weights.Seek( sequenceEntry + sequence.WeightListIndex );
			for ( var bone = 0; bone < sequenceOwner.BoneCount; bone++ )
			{
				var weight = weights.Float();
				if ( !float.IsFinite( weight ) ) return Skip( "invalid weightlist" );
				if ( weight <= 0f ) return Skip( "partial-body weightlist" );
			}
		}

		var frames = desc.NumFrames;
		var cost = (long)frames * Math.Max( BoneCount, animationOwner.BoneCount );
		if ( cost > MaxTotalBoneFrames - attemptedBoneFrames ) return Skip( "decode budget exceeded" );
		attemptedBoneFrames += cost;
		using var data = new AnimationData( animationStream, 0, animationOwner._header.Length );
		var tracks = animationOwner.ReadTake( data, animationStream, openFile, descIndex, desc, frames );
		if ( tracks == null ) return Skip( "unavailable or sectioned external take" );

		var locals = new Transform[(long)frames * BoneCount];
		var position = new double[3];
		var quat = new double[4];
		for ( var frame = 0; frame < frames; frame++ )
		{
			for ( var bone = 0; bone < BoneCount; bone++ )
			{
				var sequenceBone = sequenceMap[bone];
				var animationBone = animationMap[bone];
				var bind = sequenceBone < 0 ? Bones[bone] : sequenceOwner.Bones[sequenceBone];
				var transform = new Transform( bind.Position, bind.Quaternion );
				var track = animationBone < 0 ? null : tracks[animationBone];
				if ( sequenceBone >= 0 && track != null && track.RecordPresent[Math.Min( frame, track.Frames - 1 )] )
				{
					var animationBind = animationOwner.Bones[animationBone];
					TrackFrame( animationBind, track, frame, position, quat );
					if ( (animationBind.Flags & 0x100000) != 0 )
					{
						var alignment = animationBind.Alignment;
						if ( quat[0] * alignment.x + quat[1] * alignment.y + quat[2] * alignment.z + quat[3] * alignment.w < 0 )
							for ( var axis = 0; axis < 4; axis++ ) quat[axis] = -quat[axis];
					}
					transform = new Transform( new Vector3( (float)position[0], (float)position[1], (float)position[2] ),
						new Rotation( (float)quat[0], (float)quat[1], (float)quat[2], (float)quat[3] ) );
				}
				if ( !float.IsFinite( transform.Position.x ) || !float.IsFinite( transform.Position.y ) || !float.IsFinite( transform.Position.z )
					|| !float.IsFinite( transform.Rotation.x ) || !float.IsFinite( transform.Rotation.y ) || !float.IsFinite( transform.Rotation.z ) || !float.IsFinite( transform.Rotation.w ) )
					throw new InvalidDataException( "Non-finite animation frame." );
				locals[(long)frame * BoneCount + bone] = transform;
			}
		}
		if ( !MappedTakeMoves( locals, frames ) ) return Skip( "motionless target take" );
		return new SourceAnimation
		{
			Name = name,
			Fps = float.IsFinite( desc.Fps ) && desc.Fps > 0 ? desc.Fps : 30f,
			Looping = (sequence.Flags & 1) != 0 || (desc.Flags & 1) != 0,
			Frames = frames,
			LocalFrames = locals,
		};
	}

	private bool MappedTakeMoves( Transform[] frames, int frameCount )
	{
		var firstPosition = new double[3];
		var firstRotation = new double[4];
		var position = new double[3];
		var rotation = new double[4];
		for ( var bone = 0; bone < BoneCount; bone++ )
		{
			Components( frames[bone], firstPosition, firstRotation );
			for ( var frame = 1; frame < frameCount; frame++ )
			{
				Components( frames[(long)frame * BoneCount + bone], position, rotation );
				if ( !SameTransform( firstPosition, firstRotation, position, rotation ) )
					return true;
			}
		}
		return false;

		static void Components( Transform transform, double[] position, double[] rotation )
		{
			position[0] = transform.Position.x;
			position[1] = transform.Position.y;
			position[2] = transform.Position.z;
			rotation[0] = transform.Rotation.x;
			rotation[1] = transform.Rotation.y;
			rotation[2] = transform.Rotation.z;
			rotation[3] = transform.Rotation.w;
		}
	}

	/// <summary>Target-indexed name mapping; never applies bind-space retargeting.</summary>
	private int[] MapBones( MdlFile owner )
	{
		var map = new int[BoneCount];
		Array.Fill( map, -1 );
		if ( ReferenceEquals( owner, this ) )
		{
			for ( var bone = 0; bone < BoneCount; bone++ ) map[bone] = bone;
			return map;
		}
		var targets = new Dictionary<string, int>( StringComparer.OrdinalIgnoreCase );
		for ( var bone = 0; bone < BoneCount; bone++ )
		{
			var name = GetBoneName( bone );
			if ( !string.IsNullOrWhiteSpace( name ) ) targets.TryAdd( name, bone );
		}
		for ( var local = 0; local < owner.BoneCount; local++ )
		{
			var name = owner.GetBoneName( local );
			if ( string.IsNullOrWhiteSpace( name ) || !targets.TryGetValue( name, out var target ) )
			{
				Log.Trace( $"Animation library bone '{name}' has no name-matched target; ignored." );
				continue;
			}
			if ( map[target] < 0 ) map[target] = local;
		}
		return map;
	}
}
