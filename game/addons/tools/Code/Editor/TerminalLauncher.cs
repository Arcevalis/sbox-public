namespace Editor;

/// <summary>
/// Opens a command inside the user's terminal emulator on Linux, so tools that need a
/// real console (the dedicated server's stats overlay and command input) behave like
/// their Windows console window. Windows needs nothing: spawning a console exe from a
/// GUI app already allocates a console there.
/// </summary>
internal static class TerminalLauncher
{
	// Note: no lambdas or delegates in this class. The hotloader upgrades live
	// delegate instances by matching compiler-generated lambda methods across
	// recompiles, and lambdas stored in static state (like prefix builders here)
	// fail that match and turn into throwing stubs. Plain data + switch only.
	private static readonly string[] TerminalBinaries = new[]
	{
		"x-terminal-emulator",
		"gnome-terminal",
		"konsole",
		"xfce4-terminal",
		"xterm",
	};

	/// <summary>
	/// Emulator arguments preceding the command. The execute flag (-e, -x, --)
	/// must stay last so it catches the command and its arguments.
	/// </summary>
	private static string[] GetPrefixArgs( int index, string workingDirectory )
	{
		switch ( index )
		{
			case 1: return new[] { $"--working-directory={workingDirectory}", "--" };
			// --separate bypasses the single-instance forwarder, which can swallow -e.
			case 2: return new[] { "--separate", "--workdir", workingDirectory, "-e" };
			// -x executes the remainder of the command line. Confirm on an Xfce soak run.
			case 3: return new[] { $"--working-directory={workingDirectory}", "-x" };
			default: return new[] { "-e" };
		}
	}

	/// <summary>
	/// Session variables that abort a foreign terminal emulator when inherited. The
	/// editor session carries its own bundled Qt and native libs (LD_PRELOAD,
	/// LD_LIBRARY_PATH) plus Qt tuning (QT_*) that kill a system terminal on
	/// startup - the launch then silently no-ops: Start succeeds, no window, no
	/// child. They are stripped from the terminal's environment and restored on
	/// the inner command via env, so the server keeps the exact environment a
	/// direct spawn would have.
	/// </summary>
	private static readonly string[] ScrubbedVariables =
	[
		"LD_PRELOAD",
		"LD_LIBRARY_PATH",
		"QT_QPA_PLATFORM",
		"QT_QPA_PLATFORM_PLUGIN_PATH",
		"QT_PLUGIN_PATH",
		"QT_XCB_GL_INTEGRATION",
		"QT_WAYLAND_RECONNECT",
		"QML_IMPORT_PATH",
		"QML2_IMPORT_PATH",
	];

	/// <summary>
	/// Finds a terminal emulator: $TERMINAL first, then well-known fallbacks.
	/// prefixArgs are the emulator's own arguments that precede the command and
	/// its arguments. Returns false when nothing is installed.
	/// </summary>
	private static bool TryGetTerminal( string workingDirectory, out string fileName, out string[] prefixArgs )
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

		for ( var i = 0; i < TerminalBinaries.Length; i++ )
		{
			var path = CodeEditors.CodeEditorLocator.Find( TerminalBinaries[i] );
			if ( path is null ) continue;

			fileName = path;
			prefixArgs = GetPrefixArgs( i, workingDirectory );
			return true;
		}

		fileName = null;
		prefixArgs = null;
		return false;
	}

	/// <summary>
	/// Builds a terminal-wrapped launch: terminal, its prefix, env with the scrubbed
	/// session variables restored, then the command and its arguments. The caller must
	/// remove each name in scrubbedNames from the child's environment so the terminal
	/// starts clean while the inner command keeps the direct-spawn environment.
	/// Returns false when no terminal emulator is installed.
	/// </summary>
	public static bool TryBuildCommand( string command, IList<string> commandArgs, string workingDirectory,
		out string fileName, out List<string> argv, out List<string> scrubbedNames )
	{
		fileName = null;
		argv = null;
		scrubbedNames = null;

		if ( !TryGetTerminal( workingDirectory, out fileName, out var prefixArgs ) )
			return false;

		argv = new List<string>( prefixArgs );
		scrubbedNames = new List<string>();

		var envPath = CodeEditors.CodeEditorLocator.Find( "env" ) ?? "/usr/bin/env";
		argv.Add( envPath );

		foreach ( var name in ScrubbedVariables )
		{
			var value = Environment.GetEnvironmentVariable( name );
			if ( value is null ) continue;

			scrubbedNames.Add( name );
			argv.Add( $"{name}={value}" );
		}

		argv.Add( command );
		argv.AddRange( commandArgs );
		return true;
	}
}
