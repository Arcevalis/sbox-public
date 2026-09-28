using System.IO;

namespace Editor;

/// <summary>
/// Platform-correct file names for the executables we ship alongside the game folder.
/// The build emits extensionless binaries on Linux/macOS and .exe on Windows, so spawn
/// sites must never hardcode ".exe" - Linux and Windows are two different binaries.
/// </summary>
internal static class GameExecutables
{
	/// <summary>
	/// File name of a shipped executable: "name.exe" on Windows, bare "name" everywhere else.
	/// </summary>
	public static string FileName( string baseName )
	{
		return OperatingSystem.IsWindows() ? $"{baseName}.exe" : baseName;
	}

	/// <summary>
	/// Full path to a shipped executable under the game folder. The full path matters:
	/// with UseShellExecute=false Linux resolves bare names via PATH only, while Windows
	/// also searches the working directory - a bare name works on Windows and fails on Linux.
	/// </summary>
	public static string FullPath( string baseName )
	{
		return Path.Combine( Environment.CurrentDirectory, FileName( baseName ) );
	}
}
