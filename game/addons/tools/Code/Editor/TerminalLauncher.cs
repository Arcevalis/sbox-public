namespace Editor;

/// <summary>
/// Opens a command inside the user's terminal emulator on Linux, so tools that need a
/// real console (the dedicated server's stats overlay and command input) behave like
/// their Windows console window. Windows needs nothing: spawning a console exe from a
/// GUI app already allocates a console there.
/// </summary>
internal static class TerminalLauncher
{
	private static readonly (string Binary, Func<string, string[]> PrefixArgs)[] Terminals =
	[
		// Debian alternatives entry - resolves to the desktop's default terminal.
		( "x-terminal-emulator", _ => ["-e"] ),
		( "gnome-terminal", dir => [$"--working-directory={dir}", "--"] ),
		( "konsole", dir => ["--workdir", dir, "-e"] ),
		// -x executes the remainder of the command line. Confirm on an Xfce soak run.
		( "xfce4-terminal", dir => [$"--working-directory={dir}", "-x"] ),
		( "xterm", _ => ["-e"] ),
	];

	/// <summary>
	/// Finds a terminal emulator for the command: $TERMINAL first, then well-known
	/// fallbacks. prefixArgs are the emulator's own arguments that precede the command
	/// and its arguments. Returns false when nothing is installed.
	/// </summary>
	public static bool TryGetTerminal( string workingDirectory, out string fileName, out string[] prefixArgs )
	{
		var preferred = Environment.GetEnvironmentVariable( "TERMINAL" );
		if ( !string.IsNullOrWhiteSpace( preferred ) )
		{
			var preferredPath = CodeEditors.CodeEditorLocator.Find( preferred.Trim() );
			if ( preferredPath is not null )
			{
				fileName = preferredPath;
				prefixArgs = ["-e"];
				return true;
			}
		}

		foreach ( var (binary, prefix) in Terminals )
		{
			var path = CodeEditors.CodeEditorLocator.Find( binary );
			if ( path is null ) continue;

			fileName = path;
			prefixArgs = prefix( workingDirectory );
			return true;
		}

		fileName = null;
		prefixArgs = null;
		return false;
	}
}
