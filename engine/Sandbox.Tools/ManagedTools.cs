using NativeEngine;
using Sandbox.Engine;
using Sandbox.Internal;
using System;

namespace Editor;


internal static class ManagedTools
{
	/// <summary>
	/// This gets set when the assemblies are changed, which means we may have new tools and menu options.
	/// This ultimately triggers a tools.refresh on the next frame.
	/// </summary>
	public static bool AssembliesDirty { get; internal set; }

	/// <summary>
	/// On startup we start logging in. We can continue to log in during startup, in the background.
	/// But we really want to have been logged in by the time we start accessing the API properly.
	/// </summary>
	internal static Task AccountLoginTask;

	public static void InitStart()
	{
		Log.Info( $"Editor Startup version {Sandbox.Application.Version}" );
		//
		// Init steam and log into the api
		//
		{
			Steamworks.SteamClient.Init( (int)Sandbox.Application.AppId );
			AccountLoginTask = AccountInformation.Update();
		}

		InitFilesystem();

		Logging.SetRule( "*", EditorCookie.Get<LogLevel>( "DefaultLoggingLevel", LogLevel.Info ) );

		ConVarSystem.AddAssembly( Global.Assembly, "editor" );

		BindSystem = new Sandbox.Bind.BindSystem( "Tools" );
		BindSystem.ThrottleUpdates = true;
		BindSystem.CatchExceptions = true;

		InitQt();

		// Makes sure we have a Filesystem.Content early, even if projects aren't added yet
		FileSystem.RebuildContentPath();
	}

	public static void InitFilesystem()
	{
		FileSystem.Root.CreateDirectory( "/.source2/http" );
		FileSystem.WebCache = new LocalFileSystem( FileSystem.Root.GetFullPath( "/.source2/http" ) );

		FileSystem.Temporary = new LocalFileSystem( FileSystem.Root.GetFullPath( "/.source2" ) );
	}

	public static void InitQt()
	{
		// Qt needs real paths on disk, but which case those are spelled in is the filesystem's
		// business, not ours - so ask it rather than gluing strings onto the working directory.
		QDir.addSearchPath( "toolimages", FileSystem.Root.GetFullPath( "/core/tools/images" ) );
		QDir.addSearchPath( "toolimages", FileSystem.Root.GetFullPath( "/addons/tools/assets" ) );

		// Same for the fonts, which is what bit: this folder is really called Assets, so
		// enumerating a spelled-out path found nothing and Qt never got a font.
		const string fontFolder = "/core/fonts";

		foreach ( var file in FileSystem.Root.FindFile( fontFolder, "*.ttf" ) )
		{
			QFontDatabase.addApplicationFont( FileSystem.Root.GetFullPath( $"{fontFolder}/{file}" ) );
		}
	}

	private static FileWatch stylesWatcher;

	public static void InitFinish()
	{
		stylesWatcher = FileSystem.Root.Watch( "/addons/tools/assets/styles/*" );
		stylesWatcher.OnChangedFile += ( x ) => Application.ReloadStyles();

		//
		// Wait for tools addons to finish compiling
		//
		Log.Info( "Compiling Tools" );
		RunFrame();

		while ( AccountLoginTask is not null && !AccountLoginTask.IsCompleted )
		{
			Sandbox.Engine.IToolsDll.Current?.Spin();
			System.Threading.Thread.Sleep( 16 );
		}

		if ( !string.IsNullOrWhiteSpace( AccountInformation.AvatarJson ) )
			Avatar.AvatarJson = AccountInformation.AvatarJson;
	}

	static bool _wasActiveWindow;

	public static void RunFrame()
	{
		Application.StartFrame();

		// Release all shortcut key states when the editor loses focus (alt-tab)
		var isActive = EditorMainWindow.Current?.IsActiveWindow ?? false;
		if ( _wasActiveWindow && !isActive )
		{
			EditorShortcuts.ReleaseAll();
		}
		_wasActiveWindow = isActive;

		if ( AssembliesDirty )
		{
			AssembliesDirty = false;
			EditorEvent.Run( "refresh" );
		}

		EngineLoop.RunAsyncTasks();

		using ( Performance.Scope( "Tool Binds" ) )
		{
			BindSystem.Tick();
		}

		EditorEvent.Run( "tool.frame" );
		AssetThumbnail.Frame();
	}

	public static void Shutdown()
	{
		stylesWatcher?.Dispose();

		// Panel UI windows and their swap chains, before the render device goes away
		PanelWindow.DisposeAll();
		SceneRenderingWidget.ShutdownRendering();
		EngineLoop.DrainFrameEndDisposables();

		AssetSystem.Shutdown();
	}

	internal static void NativeHook( string name, IntPtr arg0, IntPtr arg1, IntPtr arg2 )
	{
		var method = typeof( ManagedTools ).GetMethod( name, System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic );
		if ( method == null )
		{
			Log.Warning( $"Unknown tool hook: {name}" );
			return;
		}

		var parameters = method.GetParameters();

		if ( parameters.Length == 0 )
		{
			method?.Invoke( null, null );
			return;
		}

		if ( parameters.Length == 1 )
		{
			method?.Invoke( null, new object[] { (Native.QWidget)arg0 } );
			return;
		}
	}

	internal static void GlobalMousePressed( int button )
	{
		EditorEvent.Run( "qt.mousepressed" );
	}

	internal static void GlobalMouseReleased( int button )
	{
		if ( button == 1 )
		{
			Widget.CurrentlyPressedWidget = null;
		}
	}

	internal static void GlobalMouseWheel( int x, int y, int modifiers )
	{
		if ( EditorShortcuts.InvokeWheel( y, QtHelpers.Translate( (QtKeyboardModifiers)modifiers ) ) )
			return;

		Application.accumulatedCursorDelta += new Vector2( x / 120, y / 120 );
	}

	internal static bool GlobalKeyPressed( bool press, QKeyEvent keyEvent )
	{
		var ev = new KeyEvent( keyEvent );
		KeyCode key = ev.Key;

		if ( key == KeyCode.AsciiTilde || key == KeyCode.QuoteLeft )
		{
			EditorWindow.Focus();
			return true;
		}
		else
		{
			// The play widget is a foreign window: a native Esc grab can deliver the press to Qt
			// while the SDL-wrapped subwindow never sees it, so the engine would never hear it.
			// Forward it here, unless SDL just delivered the same press (shared delivery ledger).
			// Either way the game owns the keyboard, so claim the event instead of running
			// editor shortcuts for it.
			if ( OperatingSystem.IsLinux() && key == KeyCode.Escape && GameMode.GameHasKeyboardFocus && !ev.IsAutoRepeat )
			{
				if ( !InputRouter.WasKeyDeliveredRecently( ButtonCode.KEY_ESCAPE, press, Sandbox.RealTime.GlobalNow ) )
				{
					InputDebug.Event( "sdlkeys", $"forwarding esc to engine (press={press})" );
					InputRouter.OnKey( ButtonCode.KEY_ESCAPE, ButtonCode.KEY_ESCAPE, press, false, InputRouter.KeyDeliverySource.Qt );
				}
				else
				{
					InputDebug.Event( "sdlkeys", "esc already delivered by SDL, swallowing Qt duplicate" );
				}

				// Mark the Qt event accepted: the bool return only suppresses editor
				// shortcuts, propagation (and focus handling) stops only via acceptance.
				ev.Accepted = true;
				return true;
			}

			// The game owns the keyboard: single choke for every other key. Claiming a Qt
			// event stops bridge forwarding, so never claim unless the engine provably has
			// the press (fresh in the ledger) or we forward it manually (stale). Unmappable
			// keys keep status-quo passthrough; repeats ride the SDL path like before.
			if ( OperatingSystem.IsLinux() && GameMode.GameHasKeyboardFocus && !IsEditorReservedKey( key ) )
			{
				if ( !press )
				{
					EditorShortcuts.Release( ev.Name.ToUpperInvariant() );
				}
				else
				{
					var text = ev.Text;
					if ( !string.IsNullOrEmpty( text ) )
					{
						foreach ( var c in text )
						{
							if ( !char.IsControl( c ) )
								InputRouter.OnText( c.ToString() );
						}
					}
				}

				if ( ev.IsAutoRepeat || !TryMapQtKey( ev.Name, out var mapped ) )
					return false;

				if ( InputRouter.WasKeyDeliveredRecently( mapped, press, Sandbox.RealTime.GlobalNow ) )
				{
					InputDebug.Event( "qtkeys", $"already delivered by SDL, swallowing Qt duplicate ({ev.Name} press={press})" );
					ev.Accepted = true;
					return true;
				}

				InputDebug.Event( "qtkeys", $"forwarding to engine ({ev.Name} press={press})" );
				InputRouter.OnKey( mapped, mapped, press, false, InputRouter.KeyDeliverySource.Qt );
				// Accepted pins focus on the play widget: without it Qt keeps routing
				// the press into focusNextPrevChild, blurs the widget (killing the SDL
				// path via HasMouseFocus), and a hold starves within seconds.
				ev.Accepted = true;
				return true;
			}

			if ( press )
			{
				var modifiers = ev.KeyboardModifiers;
				var baseKey = ev.Name.ToUpperInvariant();
				var modifiedKey = baseKey;

				if ( modifiers != KeyboardModifiers.None )
				{
					EditorShortcuts.Press( baseKey );
					if ( modifiers.HasFlag( KeyboardModifiers.Shift ) && ev.Key != KeyCode.Shift ) modifiedKey = "SHIFT+" + modifiedKey;
					if ( modifiers.HasFlag( KeyboardModifiers.Alt ) && ev.Key != KeyCode.Alt ) modifiedKey = "ALT+" + modifiedKey;
					if ( modifiers.HasFlag( KeyboardModifiers.Ctrl ) && ev.Key != KeyCode.Control ) modifiedKey = "CTRL+" + modifiedKey;
				}

				// If we're "in game" these will be passed by the InputRouter whilst we're not focused.
				// SDL-first wins: OnKey runs OnFunctionKey, and the Qt twin for the same press
				// finds it fresh in the shared delivery ledger and suppresses its own invoke.
				if ( ev.Key >= KeyCode.F1 && ev.Key <= KeyCode.F12 && TryMapQtKey( ev.Name, out var fnKey ) &&
					InputRouter.WasKeyDeliveredRecently( fnKey, true, Sandbox.RealTime.GlobalNow ) )
					return false;

				// Try with modifier first.
				if ( modifiers != KeyboardModifiers.None && EditorShortcuts.Invoke( modifiedKey ) )
					return true;

				// If no shortcut was invoked with modifier, try without.
				if ( EditorShortcuts.Invoke( baseKey ) )
					return true;
			}
			else
			{
				EditorShortcuts.Release( ev.Name.ToUpperInvariant() );
			}
		}

		return false;
	}

	static bool IsEditorReservedKey( KeyCode key )
	{
		return key == KeyCode.Escape || (key >= KeyCode.F1 && key <= KeyCode.F12);
	}

	/// <summary>
	/// Map a Qt key name (<see cref="KeyEvent.Name"/>) to its engine <see cref="ButtonCode"/>.
	/// Qt spellings mostly match engine key names
	/// (<see cref="Sandbox.Engine.KeyTranslation.StringToButtonCode"/> is case-insensitive),
	/// so only the known drifts need aliasing. Returns false for keys with no engine
	/// equivalent - callers keep status-quo passthrough, never claim. Generic Qt modifiers
	/// land on the left engine variants (same class as status quo).
	/// </summary>
	internal static bool TryMapQtKey( string qtName, out ButtonCode code )
	{
		var name = qtName switch
		{
			"Esc" => "ESCAPE",
			"Backtab" => "TAB",
			"Left" => "LEFTARROW",
			"Right" => "RIGHTARROW",
			"Up" => "UPARROW",
			"Down" => "DOWNARROW",
			"PageUp" => "PGUP",
			"PageDown" => "PGDN",
			"Insert" => "INS",
			"Delete" => "DEL",
			"Control" => "CTRL",
			"Meta" => "LWIN",
			"Super_L" => "LWIN",
			"Super_R" => "RWIN",
			"Menu" => "APP",
			"Print" => "PRINTSCREEN",
			"KP_Add" => "KP_PLUS",
			"KP_Multiply" => "KP_MULTIPLY",
			"KP_Divide" => "KP_DIVIDE",
			_ => qtName,
		};

		code = Sandbox.Engine.KeyTranslation.StringToButtonCode( name );
		return code != ButtonCode.BUTTON_CODE_INVALID;
	}

	internal static bool GlobalShortcutPressed()
	{
		return !EditorShortcuts.AllowShortcuts;
	}

	internal static void RunConsoleCommand( string command ) => Sandbox.ConVarSystem.Run( command );

	internal static void OnToolCommand( string v )
	{
		var parts = v.SplitQuotesStrings();
		if ( parts.Length == 0 )
			return;

		EditorEvent.Run( $"command {parts[0]}" );
	}

	internal static void StartSplashScreen()
	{
		// Panel UI is drawn before Bootstrap.Init gets to its normal material preload.
		Material.Preload();
		FontManager.Instance.LoadAll( EngineFileSystem.CoreContent );
		EditorSplashScreen.Singleton = new EditorSplashScreen();
		EditorSplashScreen.Pump();

		g_pToolFramework2.Spin();
	}
	/// <summary>
	/// Attempted to be called every 16ms, even when Qt is blocking
	/// </summary>
	public static void OnQtHeartbeat()
	{
		EditorSplashScreen.Pump();
		BlockingLoopPumper.Pump();
	}
}
