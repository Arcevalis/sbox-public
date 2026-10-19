using System.Collections.Concurrent;
using System.IO;
using Zio;
using Zio.FileSystems;

namespace Sandbox;

/// <summary>
/// A physical filesystem that resolves paths case-insensitively on Linux.
/// </summary>
internal sealed class CaseInsensitivePhysicalFileSystem : PhysicalFileSystem
{
	/// <summary>
	/// Real directory path -> the names inside it.
	/// </summary>
	private readonly ConcurrentDictionary<string, DirectoryEntries> _directoryCache = new( StringComparer.Ordinal );

	protected override string ConvertPathToInternalImpl( UPath path )
	{
		return ResolvePathCasing( base.ConvertPathToInternalImpl( path ) );
	}

	/// <summary>
	/// Resolve an absolute native path for System.IO, preferring its exact disk spelling.
	/// </summary>
	internal static string ResolveNativeCasing( string path )
	{
		if ( !OperatingSystem.IsLinux() )
			return path;

		return _shared.Value.ResolvePathCasing( path );
	}

	private static readonly Lazy<CaseInsensitivePhysicalFileSystem> _shared = new( () => new CaseInsensitivePhysicalFileSystem() );

	private string ResolvePathCasing( string path )
	{
		if ( string.IsNullOrEmpty( path ) || !Path.IsPathRooted( path ) || Exists( path ) )
			return path;

		var components = path.Split( '/', StringSplitOptions.RemoveEmptyEntries );
		var resolved = "/";

		for ( var i = 0; i < components.Length; i++ )
		{
			if ( !TryResolveComponent( resolved, components[i], out var realName ) )
			{
				// Preserve missing components for callers creating a new path.
				return Append( resolved, string.Join( '/', components, i, components.Length - i ) );
			}

			resolved = Append( resolved, realName );
		}

		return resolved;
	}

	private static bool Exists( string path ) => File.Exists( path ) || Directory.Exists( path );

	private static string Append( string directory, string name )
	{
		return directory == "/" ? $"/{name}" : $"{directory}/{name}";
	}

	/// <summary>
	/// Prefer exact disk spelling; refresh stale cached aliases and misses.
	/// </summary>
	/// <param name="directory">The real directory to look in.</param>
	/// <param name="component">The name to resolve.</param>
	/// <param name="realName">The name as it is spelled on disk, or null if it wasn't found.</param>
	private bool TryResolveComponent( string directory, string component, out string realName )
	{
		if ( Exists( Append( directory, component ) ) )
		{
			realName = component;
			return true;
		}

		var entries = GetDirectoryEntries( directory );

		if ( entries is not null && entries.TryResolve( component, out realName ) && Exists( Append( directory, realName ) ) )
			return true;

		entries = GetDirectoryEntries( directory, true );

		if ( entries is not null && entries.TryResolve( component, out realName ) )
			return true;

		realName = null;
		return false;
	}

	/// <summary>
	/// The names inside <paramref name="directory"/>, or null if it doesn't exist.
	/// </summary>
	/// <param name="directory">The real directory path to list.</param>
	/// <param name="refresh">Read the directory again instead of trusting a cached listing.</param>
	private DirectoryEntries GetDirectoryEntries( string directory, bool refresh = false )
	{
		if ( refresh )
			_directoryCache.TryRemove( directory, out _ );
		else if ( _directoryCache.TryGetValue( directory, out var cached ) )
			return cached;

		DirectoryEntries entries;

		if ( !Directory.Exists( directory ) )
			return null;

		try
		{
			entries = new DirectoryEntries( new DirectoryInfo( directory ).GetFileSystemInfos() );

			_directoryCache.TryAdd( directory, entries );

			return entries;
		}
		catch
		{
			return null;
		}
	}

	//
	// Cache invalidation for mutations
	//

	private void InvalidateParent( string resolvedPath )
	{
		var parent = Path.GetDirectoryName( resolvedPath );

		if ( parent is not null )
			_directoryCache.TryRemove( parent, out _ );
	}

	protected override void CreateDirectoryImpl( UPath path )
	{
		base.CreateDirectoryImpl( path );
		var resolved = ConvertPathToInternal( path );
		InvalidateParent( resolved );
		_directoryCache.TryRemove( resolved, out _ );
	}

	protected override void DeleteDirectoryImpl( UPath path, bool isRecursive )
	{
		var resolved = ConvertPathToInternal( path );
		base.DeleteDirectoryImpl( path, isRecursive );
		InvalidateParent( resolved );
		_directoryCache.TryRemove( resolved, out _ );
	}

	protected override void DeleteFileImpl( UPath path )
	{
		var resolved = ConvertPathToInternal( path );
		base.DeleteFileImpl( path );
		InvalidateParent( resolved );
	}

	protected override void MoveDirectoryImpl( UPath srcPath, UPath destPath )
	{
		var resolvedSrc = ConvertPathToInternal( srcPath );
		base.MoveDirectoryImpl( srcPath, destPath );
		InvalidateParent( resolvedSrc );
		InvalidateParent( ConvertPathToInternal( destPath ) );
		_directoryCache.TryRemove( resolvedSrc, out _ );
	}

	protected override void MoveFileImpl( UPath srcPath, UPath destPath )
	{
		var resolvedSrc = ConvertPathToInternal( srcPath );
		base.MoveFileImpl( srcPath, destPath );
		InvalidateParent( resolvedSrc );
		InvalidateParent( ConvertPathToInternal( destPath ) );
	}

	protected override void CopyFileImpl( UPath srcPath, UPath destPath, bool overwrite )
	{
		base.CopyFileImpl( srcPath, destPath, overwrite );
		InvalidateParent( ConvertPathToInternal( destPath ) );
	}

	protected override Stream OpenFileImpl( UPath path, FileMode mode, FileAccess access, FileShare share )
	{
		var stream = base.OpenFileImpl( path, mode, access, share );

		if ( mode is FileMode.Create or FileMode.CreateNew or FileMode.OpenOrCreate )
			InvalidateParent( ConvertPathToInternal( path ) );

		return stream;
	}

	/// <summary>
	/// Clear all caches (e.g. after external file changes).
	/// </summary>
	internal void InvalidateCache()
	{
		_directoryCache.Clear();
	}

	/// <summary>
	/// The names in a single directory, looked up ignoring case.
	/// </summary>
	private sealed class DirectoryEntries
	{
		private readonly Dictionary<string, string> names;

		/// <summary>
		/// Names that lost the case-insensitive race above, so we can still find them by their
		/// exact spelling. Stays null for the overwhelming majority of directories, which don't
		/// contain two names differing only by case.
		/// </summary>
		private readonly HashSet<string> alsoSpelled;

		public DirectoryEntries( FileSystemInfo[] infos )
		{
			names = new Dictionary<string, string>( infos.Length, StringComparer.OrdinalIgnoreCase );

			foreach ( var info in infos )
			{
				if ( !names.TryAdd( info.Name, info.Name ) )
				{
					alsoSpelled ??= new HashSet<string>( StringComparer.Ordinal );
					alsoSpelled.Add( info.Name );
				}
			}
		}

		/// <summary>
		/// Find the real on-disk name for <paramref name="name"/>, preferring an exact match.
		/// </summary>
		public bool TryResolve( string name, out string realName )
		{
			if ( alsoSpelled is not null && alsoSpelled.Contains( name ) )
			{
				realName = name;
				return true;
			}

			return names.TryGetValue( name, out realName );
		}
	}
}
