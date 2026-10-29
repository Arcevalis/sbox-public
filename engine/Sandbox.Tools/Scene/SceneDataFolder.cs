using System;
using System.IO;
using System.Linq;

namespace Editor;

internal static class SceneDataFolder
{
	internal static string GetPath( string sourcePath ) => GetOriginalPath( sourcePath.LowercaseFileName() );

	static string GetOriginalPath( string sourcePath )
	{
		var extension = Path.GetExtension( sourcePath ).Replace( ".", "_" );
		return $"{Path.ChangeExtension( sourcePath, null )}{extension}_data";
	}

	internal static string Prepare( string sourcePath )
	{
		var canonical = GetPath( sourcePath );
		var directory = Path.GetDirectoryName( canonical );
		if ( string.IsNullOrEmpty( directory ) ) directory = ".";

		if ( OperatingSystem.IsLinux() && Directory.Exists( directory ) )
		{
			var name = Path.GetFileName( canonical );
			var variants = Directory.EnumerateFileSystemEntries( directory )
				.Where( path => string.Equals( Path.GetFileName( path ), name, StringComparison.OrdinalIgnoreCase ) )
				.OrderBy( path => path, StringComparer.Ordinal ).ToArray();
			if ( variants.Length > 1 )
				throw new IOException( $"Scene data paths '{string.Join( "', '", variants )}' share one identity. Reconcile them before writing scene resources." );

			if ( variants.Length == 1 )
			{
				var legacy = variants[0];
				if ( !Directory.Exists( legacy ) )
					throw new IOException( $"Scene data path '{legacy}' is not a directory." );
				if ( !string.Equals( Path.GetFullPath( canonical ), Path.GetFullPath( legacy ), StringComparison.Ordinal ) )
					Directory.Move( legacy, canonical );
			}
		}

		Directory.CreateDirectory( canonical );
		return canonical;
	}

	static bool SameDirectory( string left, string right )
	{
		var comparison = OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
		return string.Equals( Path.GetFullPath( left ), Path.GetFullPath( right ), comparison );
	}

	/// Move a source's data tree to its new identity. Never merges: the destination
	/// must be free. Returns whether anything moved; missing trees are a no-op.
	internal static bool Relocate( string oldSourcePath, string newSourcePath )
	{
		var oldDirectory = GetPath( oldSourcePath );
		var newDirectory = GetPath( newSourcePath );
		if ( SameDirectory( oldDirectory, newDirectory ) ) return false;
		if ( !Directory.Exists( oldDirectory ) ) return false;

		if ( Directory.Exists( newDirectory ) )
			throw new IOException( $"Scene data path '{newDirectory}' already exists. Reconcile it before moving scene resources." );

		if ( OperatingSystem.IsLinux() )
		{
			var directory = Path.GetDirectoryName( newDirectory );
			if ( string.IsNullOrEmpty( directory ) ) directory = ".";
			if ( Directory.Exists( directory ) )
			{
				var name = Path.GetFileName( newDirectory );
				var clash = Directory.EnumerateFileSystemEntries( directory )
					.Where( path => string.Equals( Path.GetFileName( path ), name, StringComparison.OrdinalIgnoreCase ) )
					.OrderBy( path => path, StringComparer.Ordinal ).FirstOrDefault();
				if ( clash is not null )
					throw new IOException( $"Scene data path '{clash}' shares one identity with '{newDirectory}'. Reconcile them before moving scene resources." );
			}
		}

		Directory.Move( oldDirectory, newDirectory );
		return true;
	}

	/// Recycle a source's data tree. Missing trees are a no-op.
	internal static void Delete( string sourcePath )
	{
		var directory = GetPath( sourcePath );
		if ( Directory.Exists( directory ) )
			EditorUtility.SendToRecycleBin( directory );
	}
}
