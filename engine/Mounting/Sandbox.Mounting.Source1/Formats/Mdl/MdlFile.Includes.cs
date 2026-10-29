using System;
using System.IO;

namespace Sandbox.Mounting.Source1;

internal sealed partial class MdlFile
{
	private readonly string[] _includeLabels;
	private readonly string[] _includeFiles;

	public int IncludeCount => _includeFiles.Length;

	public string GetIncludeLabel( int index ) => _includeLabels[index];
	public string GetIncludeFile( int index ) => _includeFiles[index];

	/// <summary>
	/// Includes are optional metadata. Invalid tables are ignored; malformed strings
	/// leave only their own field empty, preserving the indices of valid entries.
	/// </summary>
	private (string[] Labels, string[] Files) ReadIncludes( Stream stream )
	{
		try
		{
			ValidateRange<StudioModelGroup>( stream, _header.IncludeModelOffset, _header.NumIncludeModels );
			var labels = new string[_header.NumIncludeModels];
			var files = new string[_header.NumIncludeModels];
			stream.Position = _header.IncludeModelOffset;
			LumpReader<StudioModelGroup>.ReadLumpFromStream( stream, _header.NumIncludeModels, ( index, group ) =>
			{
				var entry = stream.Position;
				labels[index] = ReadIncludeString( stream, entry, group.LabelIndex );
				files[index] = ReadIncludeString( stream, entry, group.NameIndex ).Replace( '\\', '/' );
			} );
			return (labels, files);
		}
		catch ( Exception e ) when ( e is InvalidDataException or EndOfStreamException or ArgumentOutOfRangeException )
		{
			Log.Trace( $"Include model table was not imported: {e.Message}" );
			return (Array.Empty<string>(), Array.Empty<string>());
		}
	}

	private string ReadIncludeString( Stream stream, long entry, int offset )
	{
		try
		{
			return ReadOptionalRelativeString( stream, entry, offset );
		}
		catch ( Exception e ) when ( e is InvalidDataException or EndOfStreamException or ArgumentOutOfRangeException )
		{
			Log.Trace( $"Include model string was not imported: {e.Message}" );
			return string.Empty;
		}
	}
}
