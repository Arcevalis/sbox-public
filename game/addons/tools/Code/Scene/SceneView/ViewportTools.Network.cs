using Editor.Preferences;
using Sandbox.Network;
using System.Diagnostics;

namespace Editor;

partial class ViewportTools
{
	class LobbySettings
	{
		/// <summary>
		/// Who can join this lobby?
		/// </summary>
		public LobbyPrivacy LobbyPrivacy
		{
			get => EditorUtility.Network.HostPrivacy;
			set => EditorUtility.Network.HostPrivacy = value;
		}

		/// <summary>
		/// Simulate lag by overriding latency to the specified value in ms.
		/// </summary>
		[Range( 0f, 500f ), Step( 25f )]
		public int SimulateLag
		{
			get => ConsoleSystem.GetValueInt( "net_fakelag" );
			set => ConsoleSystem.SetValue( "net_fakelag", value.ToString() );
		}

		/// <summary>
		/// Simulate packet loss as a percentage of packets lost.
		/// </summary>
		[Range( 0f, 100f ), Step( 0.5f )]
		public float SimulatePacketLoss
		{
			get => ConsoleSystem.GetValueFloat( "net_fakepacketloss" );
			set => ConsoleSystem.SetValue( "net_fakepacketloss", value.ToString() );
		}
	}

	private void OpenNetworkSettings()
	{
		var menu = new ContextMenu( this );

		{
			var widget = new Widget( menu );
			widget.OnPaintOverride = () =>
			{
				Paint.SetBrushAndPen( Theme.WidgetBackground.WithAlpha( 0.5f ) );
				Paint.DrawRect( widget.LocalRect.Shrink( 2f ), 2f );
				return true;
			};

			var cs = new ControlSheet();
			var settings = new LobbySettings();
			var settingsSo = settings.GetSerialized();

			cs.AddRow( settingsSo.GetProperty( nameof( LobbySettings.LobbyPrivacy ) ) );
			cs.AddRow( settingsSo.GetProperty( nameof( LobbySettings.SimulateLag ) ) );
			cs.AddRow( settingsSo.GetProperty( nameof( LobbySettings.SimulatePacketLoss ) ) );

			widget.Layout = cs;
			widget.Layout.Margin = 8;
			widget.MaximumWidth = 400f;

			menu.AddWidget( widget );
		}

		menu.AddSeparator();

		menu.AddOption( new( "Start Hosting", "dns", EditorUtility.Network.StartHosting ) { Enabled = !EditorUtility.Network.Active } );
		menu.AddOption( new( "Disconnect", "phonelink_erase", EditorUtility.Network.Disconnect ) { Enabled = EditorUtility.Network.Active } );

		menu.AddSeparator();
		menu.AddOption( new( "Join via new instance", "connected_tv", SpawnProcess ) { Enabled = EditorUtility.Network.Hosting } );
		menu.AddOption( new( "Migrate host to new instance", "swap_horiz", MigrateHostToNewInstance ) { Enabled = EditorUtility.Network.Hosting } );
		menu.AddOption( new( "Start dedicated server", "terminal", SpawnDedicatedServer ) );
		menu.AddSeparator();
		menu.AddOption( new( "Preferences", "tune", OpenPreferences ) );

		menu.OpenAtCursor();
	}

	void OpenPreferences()
	{
		var window = EditorPreferencesWindow.OpenEditorPreferences();
		window.SwitchPage<PageNetworking>();
	}

	static void AddUserCommandLineArgs( IList<string> args, string argumentString )
	{
		if ( string.IsNullOrWhiteSpace( argumentString ) )
			return;

		foreach ( var arg in argumentString.Split( ' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries ) )
		{
			args.Add( arg );
		}
	}

	void SpawnDedicatedServer()
	{
		var serverArgs = new List<string>
		{
			"+game",
			Project.Current.GetProjectPath()
		};
		AddUserCommandLineArgs( serverArgs, EditorPreferences.DedicatedServerCommandLineArgs );

		var serverExe = GameExecutables.FullPath( "sbox-server" );

		using var p = new Process();
		p.StartInfo.WorkingDirectory = Environment.CurrentDirectory;

		if ( OperatingSystem.IsWindows() )
		{
			p.StartInfo.FileName = serverExe;
		}
		else if ( TerminalLauncher.TryGetTerminal( p.StartInfo.WorkingDirectory, out var terminal, out var prefixArgs ) )
		{
			// Linux has no OS console window: run the server inside the user's terminal
			// emulator so its stats overlay and command input behave like the Windows console.
			p.StartInfo.FileName = terminal;
			foreach ( var arg in prefixArgs )
				p.StartInfo.ArgumentList.Add( arg );
			p.StartInfo.ArgumentList.Add( serverExe );
		}
		else
		{
			Log.Error( "Couldn't start dedicated server: no terminal emulator found (tried $TERMINAL, x-terminal-emulator, gnome-terminal, konsole, xfce4-terminal, xterm). Install one to get a server console." );
			return;
		}

		foreach ( var arg in serverArgs )
			p.StartInfo.ArgumentList.Add( arg );

		p.Start();
	}

	void SpawnProcess() => LocalInstances.Spawn();

	void MigrateHostToNewInstance() => _ = LocalInstances.MigrateHostAsync();
}
