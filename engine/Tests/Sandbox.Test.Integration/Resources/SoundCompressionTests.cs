using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace ResourceTests;

[TestClass]
public class SoundCompressionTests
{
	[TestMethod]
	public void CompressesPcmAndFlacByDefaultWithoutChangingSources()
	{
		using var files = new Fixtures();
		var wave = Wave( 48123, 2 );
		var path = files.Write( "stereo.wav", wave );
		var compiled = files.Compile( path );
		Assert.AreEqual( SoundFormat.Opus, compiled.Format );
		Assert.AreEqual( 48123, compiled.Frames );
		Assert.IsTrue( compiled.Bytes.Length < wave.Length / 2 );
		CollectionAssert.AreEqual( wave, File.ReadAllBytes( path ) );

		path = files.Copy( "tone.flac" );
		compiled = files.Compile( path );
		Assert.AreEqual( SoundFormat.Opus, compiled.Format );
		Assert.AreEqual( 48000, compiled.Frames );
	}

	[TestMethod]
	public void PreservesCompressedPayloadsAndAppliesRequestedProcessing()
	{
		foreach ( var extension in new[] { "mp3", "ogg", "opus" } )
		{
			using var files = new Fixtures();
			var path = files.Copy( $"tone.{extension}" );
			var original = File.ReadAllBytes( path );
			var compiled = files.Compile( path );
			var expected = extension switch { "mp3" => SoundFormat.MP3, "ogg" => SoundFormat.Vorbis, _ => SoundFormat.Opus };
			Assert.AreEqual( expected, compiled.Format );
			if ( extension == "ogg" ) CollectionAssert.AreEqual( original, compiled.Payload );
			if ( extension == "opus" ) CollectionAssert.AreEqual( OpusPackets( original ), compiled.Payload );
			if ( extension == "mp3" )
			{
				var start = original.AsSpan( 0, 3 ).SequenceEqual( "ID3"u8 )
					? 10 + (original[6] << 21 | original[7] << 14 | original[8] << 7 | original[9]) : 0;
				CollectionAssert.AreEqual( original[start..], compiled.Payload );
			}

			// Gain/mono/trim edits decode the original source before encoding the result.
			var processed = files.Write( $"processed.{extension}", original );
			File.WriteAllText( processed + ".meta", JsonSerializer.Serialize( new { guid = Guid.NewGuid(), gain = -6 } ) );
			Assert.AreEqual( SoundFormat.Opus, files.Compile( processed ).Format );
			CollectionAssert.AreEqual( original, File.ReadAllBytes( processed ) );
		}
	}

	[TestMethod]
	public void PreservesOptOutSmallSoundsAndLoopBoundaries()
	{
		using var files = new Fixtures();
		var path = files.Write( "uncompressed.wav", Wave( 48000, 1 ) );
		File.WriteAllText( path + ".meta", JsonSerializer.Serialize( new { guid = Guid.NewGuid(), compress = false } ) );
		Assert.AreEqual( SoundFormat.PCM16, files.Compile( path ).Format );
		path = files.Write( "tiny.wav", Wave( 8, 1 ) );
		Assert.AreEqual( SoundFormat.PCM16, files.Compile( path ).Format );
		path = files.Write( "loop.wav", Wave( 48000, 1 ) );
		File.WriteAllText( path + ".meta", JsonSerializer.Serialize( new { guid = Guid.NewGuid(), loop = true, start = 0.25, end = 0.5 } ) );
		var compiled = files.Compile( path );
		Assert.AreEqual( 12000, compiled.LoopStart );
		Assert.AreEqual( 24000, compiled.LoopEnd );
	}

	static byte[] Wave( int frames, int channels )
	{
		using var stream = new MemoryStream();
		using var writer = new BinaryWriter( stream );
		writer.Write( "RIFF"u8 ); writer.Write( 36 + frames * channels * 2 ); writer.Write( "WAVEfmt "u8 );
		writer.Write( 16 ); writer.Write( (short)1 ); writer.Write( (short)channels ); writer.Write( 48000 );
		writer.Write( 48000 * channels * 2 ); writer.Write( (short)(channels * 2) ); writer.Write( (short)16 );
		writer.Write( "data"u8 ); writer.Write( frames * channels * 2 );
		for ( var i = 0; i < frames; i++ )
			for ( var channel = 0; channel < channels; channel++ )
				writer.Write( (short)(12000 * Math.Sin( i * 2 * Math.PI * (440 + channel * 440) / 48000 )) );
		return stream.ToArray();
	}

	static byte[] OpusPackets( byte[] source )
	{
		using var result = new MemoryStream();
		using var packet = new MemoryStream();
		var index = 0;
		for ( var page = 0; page < source.Length; )
		{
			var segments = source[page + 26];
			var cursor = page + 27 + segments;
			for ( var i = 0; i < segments; i++ )
			{
				var count = source[page + 27 + i];
				packet.Write( source, cursor, count ); cursor += count;
				if ( count == 255 ) continue;
				if ( index++ >= 2 ) result.Write( packet.ToArray() );
				packet.SetLength( 0 );
			}
			page = cursor;
		}
		return result.ToArray();
	}

	sealed class Compiled( byte[] bytes )
	{
		public byte[] Bytes => bytes;
		int Data
		{
			get
			{
				var table = 8 + BitConverter.ToInt32( bytes, 8 );
				for ( var i = 0; i < BitConverter.ToInt32( bytes, 12 ); i++ )
				{
					var entry = table + i * 12;
					if ( BitConverter.ToUInt32( bytes, entry ) == 0x41544144 ) return entry + 4 + BitConverter.ToInt32( bytes, entry + 4 );
				}
				throw new InvalidDataException( "Missing DATA block" );
			}
		}
		public SoundFormat Format => (SoundFormat)bytes[Data + 2];
		public int Frames => BitConverter.ToInt32( bytes, Data + 8 );
		public int LoopStart => BitConverter.ToInt32( bytes, Data + 4 );
		public int LoopEnd => BitConverter.ToInt32( bytes, Data + 44 );
		public byte[] Payload => bytes[BitConverter.ToInt32( bytes, 0 )..];
	}

	sealed class Fixtures : IDisposable
	{
		readonly string engine = Environment.GetEnvironmentVariable( "FACEPUNCH_ENGINE" );
		readonly string sources;
		public Fixtures()
		{
			sources = Path.Combine( Path.GetTempPath(), $"rc_sound_{Guid.NewGuid():N}" );
			Directory.CreateDirectory( sources );
		}
		public string Write( string file, byte[] bytes ) { var path = Path.Combine( sources, file ); File.WriteAllBytes( path, bytes ); return path; }
		public string Copy( string file ) => Write( file, File.ReadAllBytes( Path.Combine( AppContext.BaseDirectory, "Resources", "Audio", file ) ) );
		public Compiled Compile( string path )
		{
			var start = new ProcessStartInfo( Path.Combine( engine, "bin", "win64", "resourcecompiler.exe" ) )
			{ UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
			foreach ( var arg in new[] { "-f", "-noassert", "-skiprendersystem", "-searchpaths", $"core|{sources}", path } ) start.ArgumentList.Add( arg );
			using var process = Process.Start( start );
			var output = process.StandardOutput.ReadToEndAsync();
			var error = process.StandardError.ReadToEndAsync();
			if ( !process.WaitForExit( 60000 ) ) { process.Kill( entireProcessTree: true ); Assert.Fail( "Sound compiler timed out" ); }
			Assert.AreEqual( 0, process.ExitCode, output.Result + error.Result );
			var compiled = Path.ChangeExtension( path, ".vsnd_c" );
			return new Compiled( File.ReadAllBytes( compiled ) );
		}
		public void Dispose()
		{
			Directory.Delete( sources, recursive: true );
		}
	}
}
