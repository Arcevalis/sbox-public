# The scene viewport: scene view, game view, and the handover between them

How the editor's 3D viewport works — the widget stack it is built from, how the editor camera is
driven in scene view, what physically changes when you press Play, and how input ownership moves
from Qt to SDL and back.

Everything stated as **MEASURED** was read out of this tree at `39812e76` and has a runnable
command in the [appendix](#appendix--reproducing-every-measurement). **INFERENCE** is marked as
such.

Companion documents: [`INPUT-ARCHITECTURE.md`](INPUT-ARCHITECTURE.md) — the three executables and
the Qt→SDL bridge that the game view depends on;
[`bootstrap-linux/linux-input.md`](bootstrap-linux/linux-input.md) — the known Linux defects, two
of which live in this viewport.

---

## 1. Summary

One widget renders both views. Nothing is created or destroyed on the Play transition; the same
`SceneRenderingWidget` is re-pointed at a different scene, a different camera, and a different
input owner.

| | Scene view | Game view | Ejected |
| --- | --- | --- | --- |
| `SceneViewWidget.CurrentView` | `Scene` | `Game` | `GameEjected` |
| Scene rendered | editor session's scene | `Game.ActiveScene` | `Game.ActiveScene` |
| Camera | `_editorCamera` (hidden, `IsSceneEditorCamera`) | `null` → falls back to `Scene.Camera` | `_ejectCamera` |
| Who owns OS input for it | Qt (polled) | SDL (`RegisterWindowWithSDL`) | Qt (polled) |
| `SceneViewportWidget.IsGameView` | `false` | `true` | `false` |
| `OnEditorPreFrame` | runs | **early-returns** | runs |
| Engine overlays / game HUD | no | yes | no |
| Editor scene ticking | yes | no | no |
| Toolbar | tools, grid, gizmo options | resolution, ms, FPS | resolution, ms, FPS |

```
 SceneViewWidget                      one per scene tab, owns the layout + the play toolbar
   └─ SceneViewportWidget  ×1..4      camera state, editor input, gizmo instance
        ├─ SceneRenderingWidget       the native window: swapchain, FocusMode.Click  ← SDL binds here
        └─ SceneOverlayWidget         a separate top-level Qt::Tool window laid over it
```

The Play button does not swap widgets. It runs `EditorScene.Play()`, which creates a
`GameEditorSession`, and then `SceneViewWidget.OnScenePlay` reaches into viewport 0 and hands its
`SceneRenderingWidget` to `GameMode.SetPlayWidget` — the single place in the tree where a Qt
widget handle is registered with SDL.

---

## 2. The widget stack

### 2.1 `SceneViewWidget` — the tab

`game/addons/tools/Code/Scene/SceneView/SceneViewWidget.cs`. One per scene editor session tab.
It owns the layout (`ViewportLayoutMode`: One, Two, Three×4, Four), a `Dictionary<int,
SceneViewportWidget> _viewports`, the shared `EditorToolManager Tools`, and the `ViewportTools`
toolbar strip.

Its most important line is the session accessor (`:43`):

```csharp
public SceneEditorSession Session => _editorSession.GameSession ?? _editorSession;
```

MEASURED — `GameSession` is non-null exactly while playing
(`engine/Sandbox.Tools/Scene/Session/SceneEditorSession.Game.cs:8-21`). So every consumer of
`Session` — camera, selection, gizmos, tools — retargets from the editor scene to the live game
scene the instant `SetPlaying()` runs, with no call sites changed.

`Frame()` (`:91`) also stops driving the editor scene once in game view:

```csharp
if ( CurrentView != ViewMode.Game )
{
    session.Tick();                                  // deferred undo buffer, camera cookie saves
    ...
    session.Scene.EditorTick( RealTime.Now, RealTime.Delta );
}
```

⇒ In game view the editor scene is not ticked at all; the running scene is ticked by the normal
engine loop instead.

### 2.2 `SceneViewportWidget` — one pane

`SceneViewportWidget.cs`, 882 lines plus seven partials. It holds no rendering of its own. It
owns:

- `ViewportState State` — camera position/rotation, ortho height, 2D mode, grid axis, render
  mode, post-processing toggles. Persisted per viewport id to `ProjectCookie`
  (`SceneView.Viewport{Id}.Settings`, `:45`).
- three cameras — `_editorCamera` (created by `Renderer.CreateSceneEditorCamera()`),
  `_ejectCamera` (lazily, on first eject), and `_activeCamera` which points at whichever is in
  use, or `null` in game view.
- `Gizmo.Instance GizmoInstance` — actually the renderer's, aliased at `:68`.
- the `SceneRenderingWidget Renderer` and the `SceneOverlayWidget Overlay`.

Note the focus handling in the constructor (`:75`, `:82`):

```csharp
FocusMode = FocusMode.TabOrClickOrWheel;
...
Overlay = new SceneOverlayWidget( this );
...
FocusMode = FocusMode.None;
```

MEASURED — the second assignment wins, so the viewport container itself is never focusable.
Keyboard focus lands on the child `SceneRenderingWidget`, which is `FocusMode.Click`
(`engine/Sandbox.Tools/Qt/SceneRenderingWidget.cs:53`). This matters: `GameMode` focuses and
blurs the *renderer*, and the game's mouse capture is driven off that widget's focus.

### 2.3 `SceneRenderingWidget` — the native window

`engine/Sandbox.Tools/Qt/SceneRenderingWidget.cs`. A `Frame` configured as a real X11/Win32
child window with its own swapchain (`:37-55`):

```csharp
SetFlag( Flag.WA_NativeWindow, true );
SetFlag( Flag.WA_PaintOnScreen, true );
SetFlag( Flag.WA_NoSystemBackground, true );
SetFlag( Flag.WA_OpaquePaintEvent, true );

OnPaintOverride = () => true;     // Qt's paint cycle suppressed entirely — see below

SwapChain = WidgetUtil.CreateSwapChain( _widget, RenderSettings.Instance.AntiAliasQuality.ToEngine() );
FocusMode = FocusMode.Click;
```

`WA_NativeWindow` is what makes `_widget.winId()` a real window handle — without it there is
nothing for `RegisterWindowWithSDL` or `SetEngineState` to be given. The `OnPaintOverride`
suppression carries a Linux-specific comment in the source: on XWayland Qt's software paint fires
on enter/click expose events and briefly clears the native window before the next present,
flashing.

Every instance registers into a static `All` set (`:11`, `:56`) and is rendered from one place.

### 2.4 `SceneOverlayWidget` — the 2D layer on top

`SceneOverlayWidget.cs`. It is **not** a child in the Qt sense — it is a frameless top-level
`Qt::Tool` window that chases the viewport's screen geometry every frame (`:14`, `:31`; the geometry chase is `:47-59`):

```csharp
WindowFlags = WindowFlags.FramelessWindowHint | WindowFlags.Tool;
...
TransparentForMouseEvents = true;
```

This is where the viewport's floating buttons live, and it was the subject of
[`linux-input.md`](bootstrap-linux/linux-input.md) §1 — on X11 this made the viewport accept no
input at all. `WA_TransparentForMouseEvents` is in-process hit-testing only, and worse,
`QWidget::setAttribute` is a **no-op** for it (`qwidget.cpp:11096`); the only thing that reaches
the X server is `Qt::WindowTransparentForInput`, and Qt promotes the attribute to that flag only
while constructing or reparenting. So the overlay kept a full XShape input region and the server
delivered every click over the viewport to it, where Qt dropped them.

Fixed: `Widget.RefreshInputRegion()` now maintains the region directly as the union of the visible
children, via `engine/Sandbox.Tools/Qt/X11InputRegion.cs`, and `SceneOverlayWidget` refreshes it
from its per-frame geometry hash.

---

## 3. The render loop is also the input loop

MEASURED — the editor's viewport update runs inside the engine's render callback, not from a Qt
event handler.

```
EngineLoop.OnClientOutput()                      EngineLoop.cs:506
  └─ if ( Application.IsEditor )
       IToolsDll.Current.OnRender()              ToolsDll.cs:63
         └─ SceneRenderingWidget.RenderAll()     SceneRenderingWidget.cs:259
              foreach visible widget → Render()  :161
                   Scene.Push() + GizmoInstance.Push()
                     ├─ PreFrame()               → OnPreFrame → SceneViewportWidget.OnEditorPreFrame
                     └─ RenderScene()            camera.AddToRenderList( SwapChain, Size * DpiScale )
                   if ( GameMode.IsPlayWidget( this ) )
                     CCameraRenderer.RenderOverlay( SwapChain )     :177-179
                   g_pRenderDevice.Present( SwapChain )             :182
```

Two consequences worth stating plainly:

- **All editor camera movement and gizmo interaction happens in `OnEditorPreFrame`**, which is a
  render-time callback. There is no `OnMouseMove`-driven camera path.
- **The game's HUD/overlay is drawn only into the registered play widget** —
  `CCameraRenderer.RenderOverlay` is gated on `GameMode.IsPlayWidget`. In a four-viewport layout
  during play, only viewport 0 gets the overlay.

Separately, `ManagedTools.RunFrame()` (`engine/Sandbox.Tools/ManagedTools.cs:101`) calls
`Application.StartFrame()` and then dispatches `tool.frame`, which is what
`[EditorEvent.Frame]` methods hang off — `SceneViewWidget.Frame`, `ViewportTools.OnFrame`,
`SceneOverlayWidget.UpdateDimensions` and so on. `StartFrame()` is what computes the polled
deltas the camera code reads (`engine/Sandbox.Tools/Qt/Application.cs:90-102`):

```csharp
CursorDelta = (UnscaledCursorPosition - lastCursorPos) / DpiScale;
lastCursorPos = UnscaledCursorPosition;
MouseWheelDelta = accumulatedCursorDelta;
accumulatedCursorDelta = Vector2.Zero;
```

`CursorDelta` is derived by differencing `QCursor::pos()` once per frame. `MouseWheelDelta` is
the one part that is genuinely event-driven — accumulated by the Qt global hook
`ManagedTools.GlobalMouseWheel` (`:172-179`).

---

## 4. Scene view: how the camera is driven

`SceneViewportWidget.OnEditorPreFrame()` (`SceneViewportWidget.cs:529`) is the whole of it. In
order:

```csharp
if ( IsGameView ) return;                    // :532 — game view opts out entirely

UpdateInputState();                          // who owns the mouse this frame
if ( !isActive && !hasMouseInput ) return;

MousePosition = Renderer.FromScreen( Application.CursorPosition ) * Renderer.DpiScale;
UpdateCamera();                              // push State → _activeCamera, handle ortho blend
DressPrefabScene();

GizmoInstance.Input.IsHovered = hasMouseFocus;
bool gizmoInputUsed = UpdateOrientationGizmo( hasMouseFocus );   // the corner axis widget wins first

if ( IsActiveWindow )
{
    ... blockCamera / blockCameraForToolInput bookkeeping ...
    if ( !shouldBlockOrbit )
    {
        if ( GizmoInstance.OrbitCamera( _activeCamera, Renderer, ref cameraOrbitDistance ) ) { ... }
        else if ( GizmoInstance.FirstPersonCamera( _activeCamera, Renderer, State.View == ViewMode.Perspective ) ) { ... }
    }
    State.CameraPosition = _activeCamera.WorldPosition;          // write back
    State.CameraRotation = _activeCamera.WorldRotation;
}

Renderer.UpdateGizmoInputs( GizmoInstance.Input.IsHovered && hasMouseFocus );
... grid, hover, Tools.Frame, DrawSelection, drag-drops, overlays ...
```

### 4.1 Deciding whether this pane owns the mouse

`UpdateInputState()` (`:140`) — MEASURED, and note the comment on why it is not simply "is the
cursor over me":

```csharp
if ( Application.MouseButtons != MouseButtons.None ) { mouseWasPressed = true; framesAfterRelease = 0; return; }
if ( mouseWasPressed )                                { mouseWasPressed = false; framesAfterRelease = 3; return; }
if ( framesAfterRelease > 0 )                         { framesAfterRelease--; return; }

// tony: ... we were previously using Application.HoveredWidget but Qt is unreliable at providing
// the hovered widget at fractional DPI scales, and I can't figure out why
hasMouseInput = IsActiveWindow && Renderer.IsUnderMouse;
```

So ownership is latched for the duration of a drag plus three frames, and only re-evaluated when
no button is down. This is what lets a right-drag leave the pane without handing the camera to a
neighbouring viewport.

### 4.2 The controls

MEASURED — `game/addons/tools/Code/Extensions/SceneEditorExtensions.cs`. Everything below reads
`Application.*` and `EditorShortcuts.IsDown`; none of it sees a Qt event.

| Input | Function | Where |
| --- | --- | --- |
| RMB drag | mouse-look (yaw/pitch, pitch clamped ±89°) | `FirstPersonCamera:73`, `:127-143` |
| RMB + `WASD`/`QE` | fly, smooth-damped toward `cameraTarget` | `:170-183`, `:223-236` |
| RMB + wheel | adjust `EditorPreferences.CameraSpeed` | `:108-125` |
| MMB drag | pan (ortho-aware scaling) | `:145-168` |
| Wheel (no RMB) | dolly forward, or ortho zoom about the cursor | `:196-220` |
| Alt + LMB drag | orbit around a pivot `distance` in front | `OrbitCamera:249`, `:304` |
| Alt + RMB drag | dolly the orbit distance | `:269-300` |

Two side effects worth knowing about:

- **Shortcuts are suppressed while flying.** `FirstPersonCamera:87` sets
  `EditorShortcuts.AllowShortcuts = false`, which is a 0.05 s decaying gate
  (`EditorShortcuts.cs:10-15`) re-armed every frame the mouse is held — otherwise `WASD` would
  fire editor shortcuts.
- **The canvas grabs focus.** `canvas.Focus()` at `:88` (and `:262` for orbit) means starting a
  camera drag focuses the `SceneRenderingWidget`.

### 4.3 Cursor wrapping

`LockCursorToCanvas( Widget canvas, int margin = 16 )` (`:47`) keeps the pointer inside the pane
during a drag by teleporting it to the opposite edge. It is the source of two open defects
(`linux-input.md` §3 and §4): it reads through `Application.CursorPosition` (`QCursor::pos()`)
and writes through `Application.UnscaledCursorPosition` (`SetNativeCursorPos`), and it returns
`true` on a wrap, which both call sites use to zero the frame's delta.

---

## 5. Pressing Play

### 5.1 The chain

MEASURED:

```
ViewportTools.Center.cs:85   PlayStop()
  └─ EditorScene.Play( session )                       engine/Sandbox.Tools/Scene/EditorScene.cs:240
       Game.IsPlaying = true                                                                 :247
       EditorEvent.Run( "scene.startplay" )                                                  :251
       ├─ playMode  → IGameInstanceDll.Current.EditorPlay()      (loads the real game)        :259
       └─ !playMode → clone the session scene into a fresh Game.ActiveScene and Load()     :262-289
       SceneEditorSession.Active.SetPlaying( Game.ActiveScene )                              :297
       EditorEvent.Run( "scene.play" )                                                       :298
            └─ SceneViewWidget.OnScenePlay()           SceneViewWidget.Game.cs:18
                 CurrentView = ViewMode.Game
                 _gameViewport = _viewports.FirstOrDefault().Value        ← always viewport 0
                 _gameViewport.SetGameView()
                 OnViewModeChanged()
                 _viewportTools.UpdateViewportFromCookie()                ← restores the resolution mode
```

`SetPlaying` (`SceneEditorSession.Game.cs:12`) constructs a `GameEditorSession` wrapping the live
scene, carries the selection across by path, and makes it active. From that moment
`SceneViewWidget.Session` resolves to the game scene.

### 5.2 What `SetGameView` actually does

`SceneViewportWidget.Game.cs:39`:

```csharp
public void SetGameView()
{
    GameMode.SetPlayWidget( Renderer );
    IsGameView = true;

    _gizmoSceneObject?.Delete();
    _gizmoSceneObject = null;

    Tools.DisposeAll();
}
```

and `GameMode.SetPlayWidget` (`engine/Sandbox.Tools/GameMode.cs:22`) is the handover proper:

```csharp
// Blur before registering so SDL's fresh wrapper can't snapshot this widget as its
// keyboard focus window - relative mouse mode is driven from the main editor window
widget.Blur();

widget.Focused += WidgetFocused;
widget.Blurred += WidgetBlurred;
widget.MouseTracking = true;
widget.MouseMove += OnPlayWidgetMouseMove;

NativeEngine.InputSystem.RegisterWindowWithSDL( widget._widget.winId() );
g_pEngineServiceMgr.SetEngineState( widget._widget.winId(), widget.SwapChain );
g_pRenderDevice.SetSwapChainIsMainWindow( widget.SwapChain, true );

_inPlay = widget;
widget.Focus();
```

Three separate registrations, all keyed on the same `winId()`:

- `RegisterWindowWithSDL` — `engine/Definitions/engine/inputsystem.def:8`, one of the four
  editor-only entries in that file. SDL wraps the foreign window handle so engine input can
  target it.
- `SetEngineState` — `engine/Definitions/engine/g_pEngineServiceMgr.def:9`, tells the engine
  which window and swapchain the game is running in.
- `SetSwapChainIsMainWindow` — so `GetGPUFrameTimeMS` reports the *game's* frame time, which is
  what the game-view toolbar's "ms / FPS" labels display (`ViewportTools.Game.cs:56-68`).

### 5.3 The camera and scene swap

`SceneViewportWidget.OnViewModeChanged( viewMode )` (`SceneViewportWidget.Game.cs:13`) is called
on **every** viewport, not just the game one:

```csharp
Renderer.Scene = Session.Scene;                    // now the game scene
GizmoInstance.Selection = Session.Selection;

if ( _editorCamera.IsValid() && _editorCamera.Scene != Session.Scene )
{
    _editorCamera.DestroyGameObject();             // editor camera lived in the old scene
    _editorCamera = Renderer.CreateSceneEditorCamera();
}

_activeCamera = viewMode switch
{
    ViewMode.Game        => null,
    ViewMode.GameEjected => _ejectCamera,
    _                    => _editorCamera,
};

Renderer.Camera = _activeCamera;
Renderer.EnableEngineOverlays = IsGameView;
```

`Renderer.Camera == null` makes `GetSceneCamera()` fall through to `Scene.Camera`
(`SceneRenderingWidget.cs:212-224`) — i.e. the game's own main camera. That is the entire
"switch to the game's view".

**A subtlety worth flagging.** The `null` assignment only *sticks* on the game viewport. Every
other viewport still runs `OnEditorPreFrame` → `UpdateCamera()`, whose first branch
(`SceneViewportWidget.cs:243-255`) sees an invalid `_activeCamera` and immediately rebuilds an
editor camera in the new scene. So in a multi-viewport layout, pressing Play gives you one pane
showing the game through its own camera and the rest free-flying editor cameras **inside the
live game scene**. INFERENCE (high confidence, from the control flow): this is deliberate — it is
what makes multi-viewport useful during play — but it is emergent from the early-return rather
than stated anywhere.

### 5.4 Chrome changes

`SceneViewWidget.OnViewModeChanged()` (`SceneViewWidget.Game.cs:74`):

```csharp
_viewportTools.Rebuild();
_sidePanel?.Visible = CurrentView != ViewMode.Game;
foreach ( var viewport in _viewports.Values ) viewport.OnViewModeChanged( CurrentView );
```

`ViewportTools.Rebuild()` (`ViewportTools.cs:27`) branches on `CurrentView == Game` and builds
either the scene toolbar (tools, grid, gizmo options, tool extensions) or the game toolbar
(resolution mode button, frame time, FPS). The play toolbar in the centre is built either way.

---

## 6. Game view: how input reaches the game

Nothing in this viewport routes game input. Once `RegisterWindowWithSDL` has run, the ordinary
engine path from [`INPUT-ARCHITECTURE.md`](INPUT-ARCHITECTURE.md) takes over —
`g_pInputService.Pump()` → `SDL_PollEvent` → `InputRouter.On*` → the game's `InputContext`. What
the editor contributes is three things.

**1. It stops forcing `Ignore`.** `engine/Sandbox.Engine/Systems/UI/UISystem.cs:329`:

```csharp
if ( Application.IsEditor && !Game.IsPlaying )
{
    mouseState = InputContext.InputState.Ignore;
    buttonState = InputContext.InputState.Ignore;
}
```

`Game.IsPlaying` was set true at `EditorScene.cs:247`, so this gate stops firing and the game
context starts reporting `Game`/`UI` states. `InputRouter.Frame()`
(`engine/Sandbox.Engine/Systems/Input/InputRouter.cs:91`) then finds an `activeMouse` in `Game`
state, enters `mouseCaptureMode`, and calls `SetRelativeMouseMode( true )` — mouse capture.

**2. Focus gates capture.** `GameMode`'s two handlers (`GameMode.cs:70`, `:81`) forward the play
widget's Qt focus state into the native input system:

```csharp
NativeEngine.InputSystem.OnEditorGameFocusChange( _inPlay._widget.winId(), true /* or false */ );
```

So clicking away from the viewport into the inspector releases the game's grip on the mouse, and
clicking back re-takes it.

**3. It patches the position gap while unfocused.** `GameMode.cs:89` — MEASURED as the only place
in the tree where managed code calls an `InputRouter.On*` handler directly:

```csharp
private static void OnPlayWidgetMouseMove( Vector2 local )
{
    // SDL handles position when the widget is focused; only fill in the gap when unfocused.
    if ( _inPlay is null || _inPlay.IsFocused ) return;

    var pos = new Vector2( (int)local.x, (int)local.y );
    var delta = pos - InputRouter.MouseCursorPosition;
    InputRouter.OnMousePositionChange( pos.x, pos.y, delta.x, delta.y );
}
```

This is why `MouseTracking = true` is set in `SetPlayWidget`.

---

## 7. Getting back out

Three routes, in increasing order of how much they undo.

### 7.1 Escape — release the mouse, keep playing

`InputRouter.OnKey` (`InputRouter.Input.cs:239-245`) → `OnEscapePressed` → `Input.EscapePressed`,
consumed at `engine/Sandbox.Tools/ToolsDll.cs:288`:

```csharp
// Escape was pressed in game and wasn't swallowed
// so lets change focus from the game window to the main editor
// window, which is going to free the mouse cursor from being captured
if ( Game.IsPlaying && Input.EscapePressed )
{
    EditorWindow.Focus();
    Input.EscapePressed = false;
}
```

Focusing the main window blurs the play widget → `WidgetBlurred` → `OnEditorGameFocusChange(
false )` → capture released. The game keeps running. (In `sbox` the same flag is consumed by
`MenuDll.LateTick()` to open the pause menu instead, guarded on `!Application.IsEditor`.)

### 7.2 F8 / Eject — take the camera back, keep playing

`SceneViewWidget.Game.cs:42-70`:

```csharp
[Shortcut( "editor.eject", "F8", ShortcutType.Window )]
static void EjectShortcut() => Current?.ToggleEject();

public void ToggleEject()
{
    if ( !Session.IsPlaying ) return;
    CurrentView = CurrentView == ViewMode.Game ? ViewMode.GameEjected : ViewMode.Game;

    if ( CurrentView == ViewMode.Game )        _gameViewport.OnPossessGame();
    else if ( CurrentView == ViewMode.GameEjected ) _gameViewport.OnEject();

    OnViewModeChanged();
    ...
}
```

`OnEject()` (`SceneViewportWidget.Game.cs:64`) calls `GameMode.ClearPlayMode()` — full SDL
unregistration — clears `IsGameView` so `OnEditorPreFrame` starts running again, releases the
forced size, seeds `State.CameraPosition/Rotation` from the *game's* camera transform so the view
does not jump, and lazily creates `_ejectCamera`. `OnPossessGame()` (`:86`) is `SetGameView()`
again by another name.

⇒ Eject is a full input handover in both directions, performed while the game is still running.

Function keys reach the editor even while the game holds input:
`InputRouter.Input.cs:250-261` routes `F1`–`F12` to `IToolsDll.OnFunctionKey` before any context
sees them, and `EditorShortcuts._timeSinceGlobalShortcut` then suppresses the duplicate when Qt
delivers the same key 50 ms later (`ManagedTools.cs:211`).

### 7.3 Stop — tear it all down

`EditorScene.Stop()` (`EditorScene.cs:301`) stops the session, destroys `Game.ActiveScene` inside
a `Networking.DisconnectScope()`, clears `Game.IsPlaying`/`IsPaused`, resets
`Mouse.Visibility`, then runs `scene.stop` → `SceneViewWidget.OnSceneStop`
(`SceneViewWidget.Game.cs:31`) → `_gameViewport.ClearGameView()` →
`GameMode.ClearPlayMode()` + `SetDefaultSize()`, and `OnViewModeChanged()` puts every viewport
back on its editor camera and rebuilds the scene toolbar.

`ClearPlayMode` (`GameMode.cs:47`) is the exact inverse of `SetPlayWidget`: blur, unhook the three
handlers, `UnregisterWindowFromSDL`, `SetSwapChainIsMainWindow( false )`, null `_inPlay`.

---

## 8. Game view sizing

`SceneViewportWidget.Sizing.cs`. In game view the pane can be pinned to a resolution or an aspect
ratio so you are looking at what the shipped game will look like.

- `SetDefaultSize()` (`:16`) — free; clears constraints back to `QT_MAX_SIZE`, the sentinel Qt
  treats as "unset fixed size" (`:10`).
- `SetAspectRatio( float )` (`:34`) — leaves the size dynamic and does the work in the
  `SizeHint()` override (`:73`), fitting to parent width or height depending on which way the
  parent's aspect runs.
- `SetResolution( Vector2 )` (`:45`) — hard `MaximumSize`/`FixedSize`.

The picker is `ResolutionModeButton` (`ViewportTools.Game.cs:71`), whose selection is stored in
the `EditorDisplayMode` project cookie and re-applied on the next Play by
`UpdateViewportFromCookie()` — called from `OnScenePlay` and from `ToggleEject` when possessing
back in.

The label next to it reports the actual backbuffer size, `Renderer.Size * Renderer.DpiScale`
(`:140-144`), which is also exactly the size passed to `AddToRenderList`
(`SceneRenderingWidget.cs:131`).

---

## 9. Consequences

- **The two views fail independently.** Scene view is Qt polling inside a render callback; game
  view is SDL events through `InputRouter`. They share the widget and the swapchain and nothing
  else. A scene-view camera bug cannot affect the game, and vice versa.
- **Focus is the load-bearing signal.** Mouse capture in play mode is driven entirely by the
  `SceneRenderingWidget`'s Qt focus, forwarded through `OnEditorGameFocusChange`. Anything that
  steals Qt focus — a modal, a dock, `EditorWindow.Focus()` from the Escape handler — releases
  the game's mouse.
- **Only viewport 0 can be the game view.** `_viewports.FirstOrDefault().Value`
  (`SceneViewWidget.Game.cs:23`). There is no selection of which pane plays.
- **On Linux, the game view depends on the Qt→SDL bridge; the scene view does not.** Scene view
  needs only that Qt delivers events and that `QCursor::pos()` and `SetNativeCursorPos` agree —
  that half now works (`linux-input.md` §1, fixed). Game view additionally needs the bridge to keep
  forwarding once play starts, and it does not: it goes silent at the play transition, and because
  the engine sets `SDL_VIDEO_X11_EXTERNAL_WINDOW_INPUT = 0` for every window it wraps, SDL has no
  independent source to fall back on (`linux-input.md` §7, open). The cursor-warp defects
  (`linux-input.md` §3, §4) remain, in the scene-view half.
- **`Renderer.IsUnderMouse` and `IsActiveWindow` are the hover contract.** The deliberate move
  away from `Application.HoveredWidget` (`SceneViewportWidget.cs:164-167`) means fractional-DPI
  hover bugs surface as a pane that will not take the camera, not as missing events.

---

## Appendix — reproducing every measurement

Run from the repo root.

```bash
# The widget stack
sed -n '35,83p'   game/addons/tools/Code/Scene/SceneView/SceneViewportWidget.cs
sed -n '37,56p'   engine/Sandbox.Tools/Qt/SceneRenderingWidget.cs
sed -n '10,32p'   game/addons/tools/Code/Scene/SceneView/SceneOverlayWidget.cs

# The render loop is the input loop
sed -n '506,521p' engine/Sandbox.Engine/Core/EngineLoop.cs
sed -n '58,67p'   engine/Sandbox.Tools/ToolsDll.cs
sed -n '161,183p' engine/Sandbox.Tools/Qt/SceneRenderingWidget.cs
sed -n '101,127p' engine/Sandbox.Tools/ManagedTools.cs
sed -n '90,102p'  engine/Sandbox.Tools/Qt/Application.cs

# Scene-view input ownership and camera
sed -n '133,169p' game/addons/tools/Code/Scene/SceneView/SceneViewportWidget.cs
sed -n '529,600p' game/addons/tools/Code/Scene/SceneView/SceneViewportWidget.cs
sed -n '47,62p'   game/addons/tools/Code/Extensions/SceneEditorExtensions.cs   # LockCursorToCanvas
sed -n '73,100p'  game/addons/tools/Code/Extensions/SceneEditorExtensions.cs   # FirstPersonCamera
sed -n '246,310p' game/addons/tools/Code/Extensions/SceneEditorExtensions.cs   # OrbitCamera
sed -n '10,15p'   engine/Sandbox.Tools/EditorShortcuts.cs                      # AllowShortcuts gate

# The play chain
sed -n '85,93p'   game/addons/tools/Code/Scene/SceneView/ViewportTools.Center.cs
sed -n '240,299p' engine/Sandbox.Tools/Scene/EditorScene.cs
sed -n '12,30p'   engine/Sandbox.Tools/Scene/Session/SceneEditorSession.Game.cs
sed -n '17,40p'   game/addons/tools/Code/Scene/SceneView/SceneViewWidget.Game.cs
sed -n '13,60p'   game/addons/tools/Code/Scene/SceneView/SceneViewportWidget.Game.cs
sed -n '22,60p'   engine/Sandbox.Tools/GameMode.cs

# The three registrations, at the .def level
sed -n '8,11p'    engine/Definitions/engine/inputsystem.def
sed -n '5,14p'    engine/Definitions/engine/g_pEngineServiceMgr.def

# Input ownership while playing
sed -n '325,335p' engine/Sandbox.Engine/Systems/UI/UISystem.cs      # the editor gate
sed -n '91,125p'  engine/Sandbox.Engine/Systems/Input/InputRouter.cs # capture / relative mouse
sed -n '70,97p'   engine/Sandbox.Tools/GameMode.cs                   # focus + the direct OnMousePositionChange

# Getting back out
sed -n '239,262p' engine/Sandbox.Engine/Systems/Input/InputRouter.Input.cs   # escape, F1-F12
sed -n '274,293p' engine/Sandbox.Tools/ToolsDll.cs                            # escape consumer
sed -n '42,70p'   game/addons/tools/Code/Scene/SceneView/SceneViewWidget.Game.cs  # eject
sed -n '301,332p' engine/Sandbox.Tools/Scene/EditorScene.cs                   # stop

# Sizing
cat game/addons/tools/Code/Scene/SceneView/SceneViewportWidget.Sizing.cs
sed -n '71,140p'  game/addons/tools/Code/Scene/SceneView/ViewportTools.Game.cs

# Only viewport 0 ever becomes the game view
grep -n '_viewports.FirstOrDefault' game/addons/tools/Code/Scene/SceneView/SceneViewWidget.Game.cs

# The only direct managed call into an InputRouter handler
grep -rn 'InputRouter.On' --include='*.cs' engine/Sandbox.Tools game/addons
```
