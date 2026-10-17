using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Sandbox.Mounting.Source1;

internal sealed partial class MdlFile
{
	private readonly StudioSeqDesc[] _sequences = Array.Empty<StudioSeqDesc>();
	private readonly string[] _sequenceNames = Array.Empty<string>();
	private readonly string[] _sequenceActivities = Array.Empty<string>();
	private readonly int[][] _sequenceAnimDescs = Array.Empty<int[]>();
	private readonly StudioAnimDesc[] _animDescs = Array.Empty<StudioAnimDesc>();
	private readonly string[] _animDescNames = Array.Empty<string>();
	private readonly bool[] _animDescNameValid = Array.Empty<bool>();

	public int SequenceCount => _sequences.Length;
	public int AnimDescCount => _animDescs.Length;

	public StudioSeqDesc GetSequence( int index ) => _sequences[index];
	public string GetSequenceName( int index ) => _sequenceNames[index];
	public string GetSequenceActivity( int index ) => _sequenceActivities[index];
	public int[] GetSequenceAnimDescs( int index ) => _sequenceAnimDescs[index];
	public StudioAnimDesc GetAnimDesc( int index ) => _animDescs[index];

	/// <summary>Raw descriptor name, or empty when missing or malformed. Check HasAnimDescName before name-based linking.</summary>
	public string GetAnimDescName( int index ) => _animDescNames[index];
	public bool HasAnimDescName( int index ) => _animDescNameValid[index];

	/// <summary>
	/// Reads the local sequence and animation description tables. Sequence data is an
	/// enhancement: unreadable tables leave the model static rather than failing it.
	/// </summary>
	private (StudioSeqDesc[] Sequences, string[] Names, string[] Activities, int[][] Tables, StudioAnimDesc[] Descs, string[] DescNames, bool[] DescNameValid) ReadSequences( Stream stream )
	{
		var sequences = Array.Empty<StudioSeqDesc>();
		var names = Array.Empty<string>();
		var activities = Array.Empty<string>();
		var tables = Array.Empty<int[]>();
		var descs = Array.Empty<StudioAnimDesc>();
		var descNames = Array.Empty<string>();
		var descNameValid = Array.Empty<bool>();

		try
		{
			ValidateRange<StudioAnimDesc>( stream, _header.LocalAnimIndex, _header.NumLocalAnims );
			stream.Seek( _header.LocalAnimIndex, SeekOrigin.Begin );
			descs = LumpReader<StudioAnimDesc>.ReadLumpFromStream( stream, _header.NumLocalAnims );
			descNames = new string[descs.Length];
			descNameValid = new bool[descs.Length];
			for ( var index = 0; index < descs.Length; ++index )
			{
				descNames[index] = string.Empty;
				try
				{
					var entry = (long)_header.LocalAnimIndex + (long)index * Marshal.SizeOf<StudioAnimDesc>();
					var name = ReadOptionalRelativeString( stream, entry, descs[index].NameIndex );
					if ( !string.IsNullOrWhiteSpace( name ) )
					{
						descNames[index] = name;
						descNameValid[index] = true;
					}
				}
				catch ( Exception e ) when ( e is InvalidDataException or EndOfStreamException or ArgumentOutOfRangeException )
				{
					Log.Trace( $"Animation descriptor {index} name was not imported: {e.Message}" );
				}
			}

			ValidateRange<StudioSeqDesc>( stream, _header.LocalSeqIndex, _header.NumLocalSeq );
			sequences = new StudioSeqDesc[_header.NumLocalSeq];
			names = new string[_header.NumLocalSeq];
			activities = new string[_header.NumLocalSeq];
			tables = new int[_header.NumLocalSeq][];

			stream.Seek( _header.LocalSeqIndex, SeekOrigin.Begin );
			LumpReader<StudioSeqDesc>.ReadLumpFromStream( stream, _header.NumLocalSeq, ( index, sequence ) =>
			{
				var entry = stream.Position;
				sequences[index] = sequence;
				names[index] = string.Empty;
				activities[index] = string.Empty;
				tables[index] = Array.Empty<int>();
				try
				{
					var name = ReadOptionalRelativeString( stream, entry, sequence.LabelIndex );
					names[index] = string.IsNullOrWhiteSpace( name ) ? $"sequence_{index}" : name;
					activities[index] = ReadOptionalRelativeString( stream, entry, sequence.ActivityNameIndex );
					tables[index] = ReadAnimDescTable( stream, entry, sequence, descs.Length );
				}
				catch ( Exception e ) when ( e is InvalidDataException or ArgumentOutOfRangeException )
				{
					Log.Trace( $"{names[index]}: sequence data was not imported: {e.Message}" );
					names[index] = $"sequence_{index}";
					activities[index] = string.Empty;
					tables[index] = Array.Empty<int>();
				}
				stream.Seek( entry, SeekOrigin.Begin );
			} );
		}
		catch ( Exception e ) when ( e is InvalidDataException or ArgumentOutOfRangeException )
		{
			Log.Trace( $"Sequence table was not imported: {e.Message}" );
			return (sequences, names, activities, tables, descs, descNames, descNameValid);
		}

		return (sequences, names, activities, tables, descs, descNames, descNameValid);
	}

	/// <summary>
	/// Expose authored samples of a blend grid as separate clips until runtime pose
	/// blending is supported. The first slot is not representative of an "all" sequence.
	/// Reserve real sequence names before calling so samples cannot hide other clips.
	/// </summary>
	private IEnumerable<(int DescIndex, string Name)> GetSequenceAnimationSamples( int sequenceIndex, HashSet<string> names )
	{
		var indices = GetSequenceAnimDescs( sequenceIndex );
		var sequenceName = GetSequenceName( sequenceIndex );
		if ( indices.Length == 1 )
		{
			yield return (indices[0], sequenceName);
			yield break;
		}

		var seen = new HashSet<int>();
		var width = GetSequence( sequenceIndex ).GroupSize;
		for ( var slot = 0; slot < indices.Length; slot++ )
		{
			var descIndex = indices[slot];
			// Edge cells can repeat the same sample (for example at a yaw wrap).
			if ( !seen.Add( descIndex ) ) continue;
			var name = HasAnimDescName( descIndex ) ? GetAnimDescName( descIndex )
				: $"{sequenceName}_blend_{slot % width}_{slot / width}";
			if ( !names.Add( name ) )
			{
				var qualified = $"{sequenceName}_{name}";
				name = qualified;
				for ( var suffix = 2; !names.Add( name ); suffix++ )
					name = $"{qualified}_{suffix}";
			}
			yield return (descIndex, name);
		}
	}

	/// <summary>
	/// Blend-slot animation description indices of one sequence (shorts at
	/// <c>entry + AnimIndexIndex</c>, <c>GroupSize * GroupSize2</c> of them).
	/// Empty when the sequence carries none or the table is implausible.
	/// </summary>
	private int[] ReadAnimDescTable( Stream stream, long entry, StudioSeqDesc sequence, int animDescCount )
	{
		if ( sequence.AnimIndexIndex <= 0 || sequence.NumBlends <= 0 )
			return Array.Empty<int>();
		var count = (long)sequence.GroupSize * sequence.GroupSize2;
		if ( count <= 0 || count > 1024 )
			return Array.Empty<int>();
		ValidateRange<short>( stream, entry + sequence.AnimIndexIndex, (int)count );
		stream.Seek( entry + sequence.AnimIndexIndex, SeekOrigin.Begin );
		using var reader = new BinaryReader( stream, Encoding.UTF8, leaveOpen: true );
		var indices = new int[count];
		for ( var i = 0; i < indices.Length; i++ )
		{
			var value = reader.ReadInt16();
			if ( value < 0 || value >= animDescCount )
				return Array.Empty<int>();
			indices[i] = value;
		}
		return indices;
	}
}
