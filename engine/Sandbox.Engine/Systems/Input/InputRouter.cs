using NativeEngine;
using Sandbox.Internal;
using Sandbox.UI;

namespace Sandbox.Engine;

/// <summary>
/// This is where input is sent to from the engine. This is the first place input is routed to.
/// From here it tries to route it to the menu, game menu and client - in that order. That should
/// really be abstracted out though, so we can use this properly in Standalone.
/// </summary>
internal static partial class InputRouter
{
	/// <summary>
	/// True if the cursor is visible
	/// </summary>
	public static bool MouseCursorVisible { get; private set; }

	/// <summary>
	/// The mouse cursor position. Or the last position if it's now invisible.
	/// </summary>
	public static Vector2 MouseCursorPosition { get; private set; }

	/// <summary>
	/// The mouse cursor delta
	/// </summary>
	public static Vector2 MouseCursorDelta { get; private set; }

	/// <summary>
	/// The panel we're keyboard focusing on
	/// </summary>
	public static IPanel KeyboardFocusPanel { get; set; }

	/// <summary>
	/// The position in which we entered capture/relative mode
	/// </summary>
	static Vector2? mouseCapturePosition;

	/// <summary>
	/// True if an "exit game" button is pressed, escape on keyboard
	/// </summary>
	public static bool EscapeIsDown { get; private set; }

	/// <summary>
	/// The escape button was pressed this frame. 
	/// The game is allowed to consume this. Then it will go to the menu.
	/// This is distinct from EscapeIsDown, because that is used to close the game when held down.
	/// </summary>
	public static bool EscapeWasPressed { get; set; }

	/// <summary>
	/// Time since escape was pressed
	/// </summary>
	static RealTimeSince TimeSinceEscapePressed { get; set; }

	/// <summary>
	/// Buttons that are currently pressed
	/// </summary>
	static HashSet<ButtonCode> PressedButtons = new HashSet<ButtonCode>();

	/// <summary>
	/// Controller buttons that are currently pressed
	/// </summary>
	static HashSet<GamepadCode> PressedControllerButtons = new HashSet<GamepadCode>();

	/// <summary>
	/// Returns the number of seconds escape has been held down
	/// </summary>
	public static float EscapeTime => EscapeIsDown ? TimeSinceEscapePressed.Relative : 0;

	/// <summary>
	/// Return the input contexts of each context, in order of priority
	/// </summary>
	static IEnumerable<InputContext> Contexts
	{
		get
		{
			if ( IMenuDll.Current is not null )
			{
				var menu = IMenuDll.Current.InputContext;
				if ( menu is not null ) yield return menu;
			}

			// if we even have a game menu!
			if ( IGameInstance.Current is not null )
			{
				var gamemenu = IGameInstanceDll.Current.InputContext;
				if ( gamemenu is not null ) yield return gamemenu;
			}
		}
	}

	public static void Frame()
	{
		var activeMouse = Contexts.Where( x => x.MouseState != InputContext.InputState.Ignore ).FirstOrDefault();
		var activeKeyboard = Contexts.Where( x => x.KeyboardState != InputContext.InputState.Ignore ).FirstOrDefault();

		// Capture mode could either come from being in game (in which case input is sent to the game)
		// or from a Panel.CaptureMode - in which case input is sent to the panel/ui
		bool mouseCaptureMode = activeMouse is not null && activeMouse.MouseState == InputContext.InputState.Game;
		mouseCaptureMode = mouseCaptureMode || (activeMouse?.MouseCapture ?? false);

		mouseCaptureMode = AllowMouseCapture( mouseCaptureMode );

		MouseCursorVisible = !mouseCaptureMode && (activeMouse is not null && activeMouse.MouseState == InputContext.InputState.UI);
		if ( !InputSystem.HasMouseFocus() ) MouseCursorVisible = true;

		if ( mouseCaptureMode )
		{
			// save the cursor position
			if ( mouseCapturePosition is null )
			{
				mouseCapturePosition = MouseCursorPosition;
			}

			SetRelativeMouseMode( true );
		}
		else
		{
			SetRelativeMouseMode( false );

			// restore cursor position
			if ( mouseCapturePosition is not null )
			{
				SetCursorPosition( mouseCapturePosition.Value );
				mouseCapturePosition = null;
			}
		}

		if ( activeMouse is not null )
		{
			SetCursorType( activeMouse.MouseCursor );
		}

		if ( activeKeyboard is not null )
		{
			KeyboardFocusPanel = activeKeyboard.KeyboardFocusPanel;
		}

		if ( KeyboardFocusPanel is null )
		{
			NativeEngine.InputSystem.SetIMEAllowed( false );
		}
		else
		{
			NativeEngine.InputSystem.SetIMEAllowed( true );
			var rect = KeyboardFocusPanel.Rect;
			NativeEngine.InputSystem.SetIMETextLocation( (int)rect.Left, (int)rect.Top, (int)rect.Width, (int)rect.Height );
		}

		MouseCursorDelta = 0;
		EscapeWasPressed = false;

		TooltipSystem.SetHovered( activeMouse?.MouseFocusPanel ?? null );
	}

	/// <summary>
	/// How long a mouse capture may deliver nothing at all before we assume it is broken and let go.
	/// A working capture produces motion almost immediately, so this only ever fires on a capture
	/// that has taken the cursor and gone silent.
	/// </summary>
	const float CaptureWatchdogSeconds = 2.0f;

	static bool captureWatchdogArmed;
	static bool captureWatchdogSatisfied;
	static bool captureWatchdogTripped;
	static int captureWatchdogEventCount;
	static RealTimeSince timeSinceCaptureBegan;

	/// <summary>
	/// Guards against mouse capture locking the user out of the editor.
	/// <para>
	/// On Linux the editor's input reaches the engine only by way of the Qt→SDL bridge. When the
	/// game takes the mouse, SDL grabs and confines the pointer, which takes pointer events away
	/// from Qt - and if nothing is coming back the other way, there is no route left to generate
	/// the Escape that would release it. The cursor is stuck inside the viewport, the Stop button
	/// cannot be clicked, and the only way out is to kill the editor.
	/// </para>
	/// <para>
	/// So: if a capture delivers no input at all for <see cref="CaptureWatchdogSeconds"/>, refuse
	/// it for the rest of this capture request. The game keeps running and the editor stays usable.
	/// </para>
	/// </summary>
	static bool AllowMouseCapture( bool wantsCapture )
	{
		if ( !OperatingSystem.IsLinux() ) return wantsCapture;

		// The request dropped - forget everything, so a later capture gets a fresh chance.
		if ( !wantsCapture )
		{
			captureWatchdogArmed = false;
			captureWatchdogSatisfied = false;
			captureWatchdogTripped = false;
			return false;
		}

		if ( captureWatchdogTripped ) return false;
		if ( captureWatchdogSatisfied ) return true;

		if ( !captureWatchdogArmed )
		{
			captureWatchdogArmed = true;
			captureWatchdogEventCount = DeliveredEventCount;
			timeSinceCaptureBegan = 0;
			return true;
		}

		// Something arrived, so the capture is delivering. Stop watching it.
		if ( DeliveredEventCount != captureWatchdogEventCount )
		{
			captureWatchdogSatisfied = true;
			return true;
		}

		if ( timeSinceCaptureBegan < CaptureWatchdogSeconds )
			return true;

		captureWatchdogTripped = true;

		Log.Warning( $"Mouse capture delivered no input for {CaptureWatchdogSeconds}s - releasing the cursor so the editor stays usable. " +
			"The game is still running; F5 stops it, F8 ejects to the editor camera." );

		return false;
	}

	static bool? relativeMouseMode;

	/// <summary>
	/// Only tell the input system when the mode actually changes. Frame() runs this every frame on
	/// both branches, which on X11 means asking SDL to grab the pointer hundreds of times a second
	/// - and a grab that loses a race against the server's implicit button grab makes SDL retry for
	/// five seconds and then give up on grabbing for the rest of the process.
	/// </summary>
	static void SetRelativeMouseMode( bool state )
	{
		if ( relativeMouseMode == state ) return;

		relativeMouseMode = state;
		NativeEngine.InputSystem.SetRelativeMouseMode( state );
	}

	static void SetCursorPosition( Vector2 pos )
	{
		if ( !g_pInputService.IsAppActive() ) return;
		if ( !InputSystem.HasMouseFocus() ) return;

		g_pInputService.SetCursorPosition( (int)pos.x, (int)pos.y );
	}

	static string CursorName { get; set; }

	static readonly CaseInsensitiveDictionary<InputStandardCursor_t> CursorLookup = new()
	{
		{ "none", InputStandardCursor_t.None },
		{ "arrow", InputStandardCursor_t.Arrow },
		{ "ibeam", InputStandardCursor_t.IBeam },
		{ "text", InputStandardCursor_t.IBeam },
		{ "crosshair", InputStandardCursor_t.Crosshair },
		{ "pointer", InputStandardCursor_t.Hand },
		{ "hand", InputStandardCursor_t.Hand },
		{ "progress", InputStandardCursor_t.WaitArrow },
		{ "wait", InputStandardCursor_t.HourGlass },
		{ "hourglass", InputStandardCursor_t.HourGlass },
		{ "move", InputStandardCursor_t.SizeALL },
		{ "sizenesw", InputStandardCursor_t.SizeNESW },
		{ "nesw-resize", InputStandardCursor_t.SizeNESW },
		{ "sizenwse", InputStandardCursor_t.SizeNWSE },
		{ "nwse-resize", InputStandardCursor_t.SizeNWSE },
		{ "sizewe", InputStandardCursor_t.SizeWE },
		{ "ew-resize", InputStandardCursor_t.SizeWE },
		{ "sizens", InputStandardCursor_t.SizeNS },
		{ "ns-resize", InputStandardCursor_t.SizeNS },
		{ "not-allowed", InputStandardCursor_t.No },
	};

	static readonly HashSet<string> UserCursors = new();

	static readonly CaseInsensitiveDictionary<string> CursorAliases = new()
	{
		{ "text", "ibeam" },
		{ "pointer", "hand" },
		{ "hourglass", "wait" },
		{ "nesw-resize", "sizenesw" },
		{ "nwse-resize", "sizenwse" },
		{ "ew-resize", "sizewe" },
		{ "ns-resize", "sizens" },
	};

	static void SetCursorType( string name )
	{
		name = MouseCursorVisible ? string.IsNullOrWhiteSpace( name ) ? "arrow" : name.ToLower() : "none";

		if ( CursorAliases.TryGetValue( name, out var canonical ) )
			name = canonical;

		if ( name == CursorName )
			return;

		if ( name == "none" )
		{
			InputSystem.SetCursorStandard( InputStandardCursor_t.None );
		}
		else if ( UserCursors.Contains( name ) )
		{
			InputSystem.SetCursorUser( name );
		}
		else if ( CursorLookup.TryGetValue( name, out var found ) )
		{
			InputSystem.SetCursorStandard( found );
		}
		else
		{
			name = "arrow";
			if ( name == CursorName )
				return;

			InputSystem.SetCursorStandard( InputStandardCursor_t.Arrow );
		}

		CursorName = name;
	}

	internal static void Shutdown()
	{
		KeyboardFocusPanel = null;
	}

	internal static void ShutdownUserCursors()
	{
		if ( Application.IsHeadless )
			return;

		UserCursors.Clear();
		InputSystem.ShutdownUserCursors();
	}

	internal static void CreateUserCursor( BaseFileSystem filesystem, string name, string filepath, int hotX, int hotY )
	{
		Assert.False( Application.IsHeadless );

		if ( string.IsNullOrWhiteSpace( name ) )
			return;

		if ( string.IsNullOrWhiteSpace( filepath ) )
			return;

		if ( UserCursors.Contains( name ) )
			return;

		if ( !filesystem.FileExists( filepath ) )
			return;

		if ( !InputSystem.LoadCursorFromFile( filepath, name, hotX, hotY ) )
			return;

		UserCursors.Add( name.ToLower() );
	}

	/// <summary>
	/// An input context wants to set the cursor position
	/// </summary>
	internal static void SetCursorPosition( InputContext inputContext, Vector2 vector2 )
	{
		var activeMouse = Contexts.Where( x => x.MouseState != InputContext.InputState.Ignore )
							.FirstOrDefault();

		if ( activeMouse != inputContext )
			return;

		// if this is set, we're in capture mode - so just update the position
		// which will update the position of the cursor when we come out of it
		if ( mouseCapturePosition is not null )
		{
			mouseCapturePosition = vector2;
			return;
		}

		SetCursorPosition( vector2 );
	}

	/// <summary>
	/// Return true if button is pressed
	/// </summary>
	public static bool IsButtonDown( ButtonCode code )
	{
		return PressedButtons.Contains( code );
	}

	/// <summary>
	/// Return true if button is pressed
	/// </summary>
	private static void SetButtonState( ButtonCode code, bool state )
	{
		if ( state ) PressedButtons.Add( code );
		else PressedButtons.Remove( code );
	}

	/// <summary>
	/// Return true if button is pressed
	/// </summary>
	public static bool IsButtonDown( GamepadCode code )
	{
		return PressedControllerButtons.Contains( code );
	}

	/// <summary>
	/// Return true if button is pressed
	/// </summary>
	private static void SetButtonState( GamepadCode code, bool state )
	{
		if ( state ) PressedControllerButtons.Add( code );
		else PressedControllerButtons.Remove( code );
	}

	/// <summary>
	/// A console command from the engine.
	/// </summary>
	internal static void OnConsoleCommand( string v )
	{
		ConVarSystem.Run( v );
	}

	internal static void CloseApplication()
	{
		Application.Exit();
	}
}
