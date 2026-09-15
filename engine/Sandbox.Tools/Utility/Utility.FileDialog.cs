using System.IO;
namespace Editor;

public static partial class EditorUtility
{
	/// <summary>
	/// Open a file save dialog. Returns null on cancel, else the absolute path of the target file.
	/// Pass an empty extension to show all files (e.g. when picking an executable on Linux/macOS).
	/// </summary>
	public static string SaveFileDialog( string title, string extension, string defaultPath )
	{
		extension = (extension ?? "").Trim( '.' ).Trim();

		var directory = defaultPath;
		if ( !string.IsNullOrEmpty( directory ) && directory.Contains( "." ) )
			directory = Path.GetDirectoryName( directory );

		var fd = new FileDialog( null );
		fd.Title = title;
		if ( !string.IsNullOrEmpty( directory ) )
			fd.Directory = directory;
		var fileName = string.IsNullOrEmpty( defaultPath ) ? "" : Path.GetFileName( defaultPath );
		if ( !string.IsNullOrEmpty( fileName ) )
			fd.SelectFile( fileName );
		fd.SetFindFile();
		fd.SetModeSave();
		if ( !string.IsNullOrEmpty( extension ) )
		{
			fd.DefaultSuffix = $".{extension}";
			fd.SetNameFilter( $"{extension} (*.{extension})" );
		}

		if ( !fd.Execute() )
			return null;

		return fd.SelectedFile;
	}

	/// <summary>
	/// Open a file open dialog. Returns null on cancel, else the absolute path of the target file.
	/// Pass an empty extension to show all files (e.g. when picking an executable on Linux/macOS).
	/// </summary>
	public static string OpenFileDialog( string title, string extension, string defaultPath )
	{
		extension = (extension ?? "").Trim( '.' ).Trim();

		var fd = new FileDialog( null );
		fd.Title = title;
		var directory = string.IsNullOrEmpty( defaultPath ) ? "" : Path.GetDirectoryName( defaultPath );
		if ( !string.IsNullOrEmpty( directory ) )
			fd.Directory = directory;
		var fileName = string.IsNullOrEmpty( defaultPath ) ? "" : Path.GetFileName( defaultPath );
		if ( !string.IsNullOrEmpty( fileName ) )
			fd.SelectFile( fileName );
		fd.SetFindFile();
		fd.SetModeOpen();
		if ( !string.IsNullOrEmpty( extension ) )
		{
			fd.DefaultSuffix = $".{extension}";
			fd.SetNameFilter( $"{extension} (*.{extension})" );
		}

		if ( !fd.Execute() )
			return null;

		return fd.SelectedFile;
	}
}
