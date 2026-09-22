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
	/// Shift+Escape was pressed in editor play mode. Kept separate so game input cannot consume it.
	/// </summary>
	internal static bool EditorPauseMenuWasPressed { get; set; }

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

	/// <summary>
	/// Installed by the editor (<c>Editor.GameMode</c>) while a play widget is registered on Linux.
	/// Called once a frame with whether the game wants the mouse; returns true if the editor has
	/// taken the pointer itself, in Qt.
	/// <para>
	/// It has to, because SDL cannot. An X11 pointer grab redirects pointer events to the grabbing
	/// client, and on Linux the Qt→SDL bridge is the only thing feeding the engine - so asking
	/// SDL to grab starves the very source the game
	/// depends on. Measured: while <c>sdlRelMode=True</c>, Qt delivered <b>zero</b> mouse moves and
	/// <c>InputRouter</c> zero events, and both resumed the instant SDL let go.
	/// </para>
	/// <para>
	/// While this returns true, SDL relative mode is never asked for, the cursor is the editor's to
	/// hide, and the capture watchdog is bypassed - it exists to catch exactly the starvation this
	/// avoids.
	/// </para>
	/// </summary>
	internal static Func<bool, bool> ManagedMouseCapture { get; set; }

	public static void Frame()
	{
		var activeMouse = Contexts.FirstOrDefault( x => x.MouseState != InputContext.InputState.Ignore );
		var activeKeyboard = Contexts.FirstOrDefault( x => x.KeyboardState != InputContext.InputState.Ignore );

		// Capture mode could either come from being in game (in which case input is sent to the game)
		// or from a Panel.CaptureMode - in which case input is sent to the panel/ui
		bool mouseCaptureMode = activeMouse is not null && activeMouse.MouseState == InputContext.InputState.Game;
		mouseCaptureMode = mouseCaptureMode || (activeMouse?.MouseCapture ?? false);

		bool wantsCapture = mouseCaptureMode;

		// If the editor has taken the pointer in Qt, trust it: no SDL grab, no watchdog.
		bool managedCapture = ManagedMouseCapture?.Invoke( wantsCapture ) ?? false;

		mouseCaptureMode = managedCapture ? wantsCapture : AllowMouseCapture( mouseCaptureMode );

		MouseCursorVisible = !mouseCaptureMode && (activeMouse is not null && activeMouse.MouseState == InputContext.InputState.UI);

		// SDL's mouse focus is not authoritative while the editor owns the pointer - the events
		// SDL sees are injected, and injection never updates its focus.
		if ( !managedCapture && !WindowInput.HasMouseFocus() ) MouseCursorVisible = true;

		if ( InputDebug.Enabled )
		{
			// Interop calls, so only built when the variable is set
			var state = $"mouseState={activeMouse?.MouseState.ToString() ?? "none"} panelCapture={activeMouse?.MouseCapture ?? false} " +
				$"wantsCapture={wantsCapture} allowed={mouseCaptureMode} " +
				$"watchdog(armed={captureWatchdogArmed},satisfied={captureWatchdogSatisfied},tripped={captureWatchdogTripped}) " +
				$"hasMouseFocus={WindowInput.HasMouseFocus()} appActive={WindowInput.IsAppActive()} " +
				$"sdlRelMode={WindowInput.GetRelativeMouseMode()} cursorVisible={MouseCursorVisible}";

			InputDebug.OnChange( "routerdbg", "frame", state, $"events={DeliveredEventCount}" );

			// A steady state logs nothing, so we would not be able to tell a capture that is
			// delivering from one that has gone silent. Tick the delivery rate once a second
			// while a capture is wanted.
			if ( wantsCapture && timeSinceDeliveryReport > 1.0f )
			{
				InputDebug.Event( "routerdbg", $"delivered={DeliveredEventCount - lastReportedEventCount}/s while captured (allowed={mouseCaptureMode})" );
				lastReportedEventCount = DeliveredEventCount;
				timeSinceDeliveryReport = 0;
			}
		}

		if ( mouseCaptureMode )
		{
			// save the cursor position
			if ( mouseCapturePosition is null )
			{
				mouseCapturePosition = MouseCursorPosition;
			}

			if ( !managedCapture ) SetRelativeMouseMode( true );
		}
		else
		{
			if ( !managedCapture ) SetRelativeMouseMode( false );

			// restore cursor position
			if ( mouseCapturePosition is not null )
			{
				SetCursorPosition( mouseCapturePosition.Value );
				mouseCapturePosition = null;
			}
		}

		// While the editor holds the pointer it owns the cursor too - it hides it on the Qt widget,
		// which is the thing actually drawing it. SetCursorStandard is SDL-side and does not reach a
		// Qt-owned window, which is why the cursor stayed visible with MouseCursorVisible=False.
		if ( activeMouse is not null && !managedCapture )
		{
			SdlCursors.SetCursor( MouseCursorVisible ? activeMouse.MouseCursor : "none" );
		}

		KeyboardFocusPanel = activeKeyboard?.KeyboardFocusPanel;
		WindowInput.SetIMEAllowed( KeyboardFocusPanel is not null );
		if ( KeyboardFocusPanel is not null )
		{
			var rect = KeyboardFocusPanel is Panel panel ? panel.ImeCaretRect : KeyboardFocusPanel.Rect;
			WindowInput.SetIMETextLocation( (int)rect.Left, (int)rect.Top, (int)rect.Width, (int)rect.Height );
		}

		MouseCursorDelta = 0;
		EscapeWasPressed = false;
		EditorPauseMenuWasPressed = false;

		// Only the UI that has the mouse gets to show a tooltip - the one underneath it loses its hover
		foreach ( var context in Contexts )
		{
			context.TargetUISystem?.Tooltips.SetHovered( context == activeMouse ? activeMouse.MouseFocusPanel as Panel : null, MouseCursorPosition );
		}
	}

	/// <summary>
	/// How long a mouse capture may deliver nothing at all before we assume it is broken and let go.
	/// A working capture produces motion almost immediately, so this only ever fires on a capture
	/// that has taken the cursor and gone silent.
	/// </summary>
	const float CaptureWatchdogSeconds = 2.0f;

	/// <summary>
	/// How long a tripped watchdog stays tripped before letting capture try again. Long enough that
	/// a genuinely broken capture is not retried in a tight loop, short enough to recover.
	/// </summary>
	const float CaptureRetrySeconds = 10.0f;

	static bool captureWatchdogArmed;
	static bool captureWatchdogSatisfied;
	static bool captureWatchdogTripped;
	static int captureWatchdogEventCount;
	static RealTimeSince timeSinceCaptureBegan;

	static int lastReportedEventCount;
	static RealTimeSince timeSinceDeliveryReport;

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

		// A trip used to latch for as long as the request stood, and in game view the request never
		// drops - UISystem holds mouseState=Game for the whole session - so one trip disabled capture
		// permanently (measured: tripped at +2s, still tripped 18s later). Re-arm periodically so a
		// capture that becomes viable can engage again.
		if ( captureWatchdogTripped )
		{
			if ( timeSinceCaptureBegan < CaptureRetrySeconds ) return false;

			captureWatchdogArmed = false;
			captureWatchdogTripped = false;
		}

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

		InputDebug.Event( "routerdbg", $"SetRelativeMouseMode( {state} ) -> SDL grab" );

		WindowInput.SetRelativeMouseMode( state );
	}

	static void SetCursorPosition( Vector2 pos )
	{
		if ( !WindowInput.IsAppActive() ) return;
		if ( !WindowInput.HasMouseFocus() ) return;

		WindowInput.SetCursorPosition( (int)pos.x, (int)pos.y, GameWindow.Current?.Handle ?? IntPtr.Zero );
	}

	internal static void Shutdown()
	{
		KeyboardFocusPanel = null;
	}

	internal static void ShutdownUserCursors()
	{
		if ( Application.IsHeadless )
			return;

		SdlCursors.ShutdownUserCursors();
	}

	internal static void CreateUserCursor( BaseFileSystem filesystem, string name, string filepath, int hotX, int hotY )
	{
		Assert.False( Application.IsHeadless );

		if ( string.IsNullOrWhiteSpace( name ) )
			return;

		if ( string.IsNullOrWhiteSpace( filepath ) )
			return;

		if ( SdlCursors.HasUserCursor( name ) )
			return;

		if ( !filesystem.FileExists( filepath ) )
			return;

		SdlCursors.LoadCursorFromFile( filepath, name, hotX, hotY );
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

	internal static void CloseApplication()
	{
		Application.Exit();
	}
}
