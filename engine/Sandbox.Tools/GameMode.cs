using System;
using Sandbox.Engine;

namespace Editor;

/// <summary>
/// Registers a widget with the input system to use SDL and manages
/// inputs and focus as it relates to the editor's game widget.
/// </summary>
public static class GameMode
{
	static Widget _inPlay;

	/// <summary>
	/// Is a render widget the active play widget
	/// </summary>
	internal static bool IsPlayWidget( SceneRenderingWidget widget ) => widget == _inPlay;

	/// <summary>
	/// Is the play widget holding keyboard focus - i.e. does the running game own what the user
	/// is typing, rather than the editor. This is the same focus that gates mouse capture in
	/// <see cref="WidgetFocused"/>, so the two stay in step.
	/// </summary>
	internal static bool GameHasKeyboardFocus => _inPlay.IsValid() && _inPlay.IsFocused;

	/// <summary>
	/// Given a widget, register it for SDL input, and tell the engine this is the swapchain we have
	/// </summary>
	/// <param name="widget"></param>
	public static void SetPlayWidget( SceneRenderingWidget widget )
	{
		if ( _inPlay == widget ) return;

		// Blur before registering so SDL's fresh wrapper can't snapshot this widget as its
		// keyboard focus window - relative mouse mode is driven from the main editor window
		widget.Blur();

		widget.Focused += WidgetFocused;
		widget.Blurred += WidgetBlurred;
		widget.MouseTracking = true;
		widget.MouseMove += OnPlayWidgetMouseMove;

		NativeEngine.InputSystem.RegisterWindowWithSDL( widget._widget.winId() );
		g_pEngineServiceMgr.SetEngineState( widget._widget.winId(), widget.SwapChain );

		// The play widget is where the game renders, so make it the main window: flip the existing
		// m_bIsMainWindow flag so GetGPUFrameTimeMS reports the running game's GPU frame time.
		g_pRenderDevice.SetSwapChainIsMainWindow( widget.SwapChain, true );

		_inPlay = widget;

		InputRouter.ManagedMouseCapture = OnManagedMouseCapture;

		widget.Focus();

		InputDebug.Event( "gamemode", $"SetPlayWidget winId={widget._widget.winId()} size={widget.Size} focused={widget.IsFocused}" );
	}

	public static void ClearPlayMode()
	{
		if ( _inPlay is null )
			return;

		InputDebug.Event( "gamemode", "ClearPlayMode" );

		// Give the pointer back before we let go of the widget, or the cursor stays hidden
		InputRouter.ManagedMouseCapture = null;

		if ( _capturing )
		{
			_capturing = false;
			_hasLastLocal = false;

			if ( _inPlay.IsValid() )
				_inPlay.Cursor = CursorShape.None;
		}

		_inPlay.Blur();

		_inPlay.Focused -= WidgetFocused;
		_inPlay.Blurred -= WidgetBlurred;
		_inPlay.MouseMove -= OnPlayWidgetMouseMove;
		_inPlay.MouseTracking = false;

		NativeEngine.InputSystem.UnregisterWindowFromSDL( _inPlay._widget.winId() );

		if ( _inPlay is SceneRenderingWidget playWidget )
			g_pRenderDevice.SetSwapChainIsMainWindow( playWidget.SwapChain, false );

		_inPlay = null;
	}

	/// <summary>
	/// When the editor gains focus of the game widget, tell the input system so it'll mouse capture (if it wants to)
	/// </summary>
	private static void WidgetFocused( FocusChangeReason reason )
	{
		if ( _inPlay is null )
			return;

		InputDebug.Event( "gamemode", $"play widget focused ({reason})" );

		NativeEngine.InputSystem.OnEditorGameFocusChange( _inPlay._widget.winId(), true );
	}

	/// <summary>
	/// When the editor loses focus of the game widget, tell the input system so it stops trying to do mouse capture.
	/// </summary>
	private static void WidgetBlurred( FocusChangeReason reason )
	{
		if ( _inPlay is null )
			return;

		InputDebug.Event( "gamemode", $"play widget blurred ({reason})" );

		NativeEngine.InputSystem.OnEditorGameFocusChange( _inPlay._widget.winId(), false );
	}

	/// <summary>
	/// How close to the viewport edge the pointer may get before we recentre it. Warping only near
	/// the edges means ordinary movement is a plain difference between two Qt event positions, with
	/// no warp in the middle of it to go wrong.
	/// <para>
	/// KNOWN GAP: this assumes the pointer cannot travel further than the margin between two Qt
	/// events, and a fast enough flick does - at the low end of the 40-190 moves/s Qt was measured
	/// delivering, one event can cover ~75px. Once it is outside the widget Qt stops sending it
	/// MouseMove at all, so the warp that would pull it back never fires. See
	/// <c>bootstrap-linux/linux-input.md</c> §7c.
	/// </para>
	/// </summary>
	const float CaptureEdgeMargin = 64.0f;

	static bool _capturing;
	static Vector2 _lastLocal;
	static bool _hasLastLocal;

	/// <summary>
	/// Take the pointer for the running game, in Qt rather than through SDL. Installed on
	/// <see cref="InputRouter.ManagedMouseCapture"/> while a play widget is registered; see the
	/// remarks there for why SDL cannot do this on Linux.
	/// </summary>
	static bool OnManagedMouseCapture( bool wantsCapture )
	{
		// Windows has raw input and a real window procedure, so SDL's own relative mode works and
		// there is nothing to route around.
		if ( !OperatingSystem.IsLinux() ) return false;
		if ( !_inPlay.IsValid() ) return false;

		// Only while the game actually has focus, so clicking into the inspector gives the mouse back
		var capture = wantsCapture && _inPlay.IsFocused;

		if ( capture == _capturing )
			return _capturing;

		_capturing = capture;
		_hasLastLocal = false;

		if ( capture )
		{
			// Hide it on the Qt widget - the thing that actually draws the cursor over this window
			_inPlay.Cursor = CursorShape.Blank;
			WarpToCentre();
		}
		else
		{
			_inPlay.Cursor = CursorShape.None;
		}

		InputDebug.Event( "gamemode", $"managed capture {(capture ? "engaged" : "released")}" );

		return _capturing;
	}

	/// <summary>
	/// Put the pointer back in the middle of the viewport so a turn never runs out of screen.
	/// </summary>
	static void WarpToCentre()
	{
		if ( !_inPlay.IsValid() ) return;

		var centre = _inPlay.Size * 0.5f;

		// Read and write the same coordinate space. linux-input.md §3 is what happens otherwise:
		// reading QCursor::pos() while writing SetNativeCursorPos leaves the residue of a warp that
		// did not land to be measured again next frame, compounding every frame. MouseEvent's local
		// position, ToScreen() and Application.CursorPosition are all Qt logical coordinates.
		Application.CursorPosition = _inPlay.ToScreen( centre );

		_lastLocal = centre;
		_hasLastLocal = true;
	}

	/// <summary>
	/// Turn Qt's absolute positions into the relative deltas the game wants. Because we only ever
	/// report differences, a full 360 is just as many events as a small turn - nothing clamps at the
	/// viewport edge.
	/// </summary>
	static void OnCapturedMouseMove( Vector2 local )
	{
		if ( !_hasLastLocal )
		{
			_lastLocal = local;
			_hasLastLocal = true;
			return;
		}

		var delta = local - _lastLocal;
		_lastLocal = local;

		// A warp that did not land would otherwise arrive as one enormous delta and spin the camera.
		// Bound it to something no genuine movement between two events can reach, so a dropped warp
		// costs one ignored event instead of a spin.
		var limit = _inPlay.Size * 0.5f;
		if ( MathF.Abs( delta.x ) > limit.x || MathF.Abs( delta.y ) > limit.y )
			return;

		if ( delta != Vector2.Zero )
			InputRouter.OnMouseMotion( delta.x, delta.y );

		var size = _inPlay.Size;
		if ( local.x < CaptureEdgeMargin || local.y < CaptureEdgeMargin ||
			 local.x > size.x - CaptureEdgeMargin || local.y > size.y - CaptureEdgeMargin )
		{
			WarpToCentre();
		}
	}

	static int qtMouseMoveCount;
	static int lastReportedQtMouseMoves;
	static RealTimeSince timeSinceQtMoveReport;

	private static void OnPlayWidgetMouseMove( Vector2 local )
	{
		// Whether Qt still sees the pointer over the play widget is the whole question behind
		// linux-input.md §7 - an SDL pointer grab redirects events to SDL and starves Qt, which is
		// the bridge's only source. Count them here, where Qt hands them to us.
		if ( InputDebug.Enabled )
		{
			qtMouseMoveCount++;

			if ( timeSinceQtMoveReport > 1.0f )
			{
				InputDebug.Event( "gamemode", $"qt mouse moves={qtMouseMoveCount - lastReportedQtMouseMoves}/s local={local} focused={_inPlay?.IsFocused}" );
				lastReportedQtMouseMoves = qtMouseMoveCount;
				timeSinceQtMoveReport = 0;
			}
		}

		// While we hold the pointer ourselves, this is the game's only source of mouse movement
		if ( _capturing )
		{
			OnCapturedMouseMove( local );
			return;
		}

		// SDL handles position when the widget is focused; only fill in the gap when unfocused.
		if ( _inPlay is null || _inPlay.IsFocused )
			return;

		var pos = new Vector2( (int)local.x, (int)local.y );
		var delta = pos - InputRouter.MouseCursorPosition;

		InputRouter.OnMousePositionChange( pos.x, pos.y, delta.x, delta.y );
	}
}
