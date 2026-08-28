# Input architecture: `sbox`, `sbox-standalone`, `sbox-dev`

How input gets registered and delivered in each of the three executables, on Linux and on
Windows, and why the Linux editor needs a bridge that no other configuration needs.

Everything stated as **MEASURED** was read out of this tree or out of the shipped binaries, and
every measurement has a runnable command in the [appendix](#appendix--reproducing-every-measurement).
**INFERENCE** is marked as such.

Companion documents: [`bootstrap-linux/linux-input.md`](bootstrap-linux/linux-input.md) — the
known editor-input defects on Linux, with their measurements.
For a live dependency check on the shipped natives, run [`./bootstrap.sh`](bootstrap.sh) — it
reports every binary as OK or FAIL before building — rather than trusting any checked-in symbol
list.

---

## 1. Summary

| | `sbox` | `sbox-standalone` | `sbox-dev` |
| --- | --- | --- | --- |
| Role | retail client → menu → gamemode | boots straight into one project | editor |
| AppSystem | `GameAppSystem` | `StandaloneAppSystem` | `EditorAppSystem` |
| Flags | `IsGameApp` | `IsGameApp \| IsStandaloneGame` | `IsGameApp \| IsEditor` |
| Qt loaded | no | no | **yes** (`libtoolframework2`) |
| Who reads OS input | SDL's own backend | SDL's own backend | **Qt** (Linux) / raw input + SDL (Windows) |
| `InputRouter` contexts | Menu, GameInstance | GameInstance only | Menu, GameInstance |
| Editor UI input | — | — | Qt widget events + polling, **never** `InputRouter` |

All three converge on **one** managed entry point and diverge entirely in what feeds it:

```
                     ┌─ sbox / sbox-standalone ──── SDL's own x11/win32 backend pump
 SDL event queue ◄───┤
                     └─ sbox-dev, LINUX ONLY ────── Qt xcb → CQtInputBridge → SDL_PushEvent
         │
         └─ libengine2  SDL_PollEvent
              └─ igen_engine, managed export slots 8–23
                   └─ Sandbox.Engine.InputRouter.On*
                        └─ InputContext  (menu DLL, game-instance DLL)
```

The editor's **own UI** input is not on that diagram at all. It is Qt widget events delivered
straight into managed `Widget.OnMousePress` / `OnKeyPress`, plus per-frame polling of
`Application.MouseButtons` / `CursorPosition` / `KeyboardModifiers`. There is no editor
`InputContext`, so nothing arbitrates between the two systems.

---

## 2. The one trunk

Every executable pumps input the same way, unconditionally, once per frame.

`engine/Sandbox.Engine/Core/EngineLoop.cs:225-232`:

```csharp
/// <summary>
/// Pumps the input system
/// </summary>
static void UpdateInput()
{
    using var __ = PerformanceStats.Timings.Input.Scope();

    g_pInputService.Pump();
}
```

called from `FrameStart()` at `EngineLoop.cs:253`. `Pump()` is declared at
`engine/Definitions/common/inputservice.def:22`; underneath it, `libengine2` drains the SDL queue
with `SDL_PollEvent` and calls back into managed.

### 2.1 The ABI boundary

`engine/Definitions/engine/inputrouter.def` declares the whole native→managed input surface —
16 entries, and nothing else crosses:

```
managed static class Sandbox.Engine.InputRouter
{
	static void OnMouseMotion( float dx, float dy );
	static void OnMousePositionChange( float x, float y, float dx, float dy );
	static void OnMouseButton( ButtonCode button, bool state, int ikeymods );
	static void OnKey( ButtonCode scanButtonCode, ButtonCode keyButtonCode, bool state, bool repeating, int ikeymods );
	static void OnText( uint key );
	static void OnWindowActive( bool active );
	static void OnMouseWheel( int x, int y, int ikeymods );
	static void OnImeStart();
	static void OnImeComposition( string text, bool final );
	static void OnImeEnd();
	static void OnGameControllerButton( int deviceId, GameControllerCode button, bool state );
	static void OnGameControllerAxis( int deviceId, GameControllerAxis axis, int value );
	static void OnGameControllerConnected( int joystickId, int deviceId );
	static void OnGameControllerDisconnected( int deviceId );
	static void OnConsoleCommand( string command );
	static void CloseApplication();
}
```

MEASURED — these bind to **indices 8–23** of the 84-entry managed export table handed to
`igen_engine`, `engine/Sandbox.Engine/Interop.Engine.cs:15429` and `:15440-15455`:

```
slot   8  line 15440  SandboxEngine_InputRouter_OnMouseMotion
slot   9  line 15441  SandboxEngine_InputRouter_OnMousePositionChange
...
slot  23  line 15455  SandboxEngine_InputRouter_CloseApplication
```

(`linux-input.md` calls these "igen_engine slots 9-24" — the same range, counted from 1.)

The reverse direction, managed→native, is `engine/Definitions/engine/inputsystem.def`. Three of
its entries exist **only** for the editor: `RegisterWindowWithSDL` / `UnregisterWindowFromSDL`
(`:8-9`), `SetEditorMainWindow` (`:10`), `OnEditorGameFocusChange` (`:11`).

### 2.2 Per-frame order

```
EngineLoop.FrameStart()
  :253  UpdateInput()          → g_pInputService.Pump() → SDL_PollEvent → InputRouter.On*
  :286  IMenuDll/IGameInstanceDll/IToolsDll .Tick()
  :325  SimulateUI()           → UISystem.TickInput() → InputContext.UpdateInputFromUI()
  :337  if ( !Application.IsHeadless ) InputRouter.Frame()   → capture / cursor / IME
```

So native dispatch happens *first*, the UI recomputes each context's `InputState` *after*, and
capture state is applied *last*, one frame behind the events that caused it.

---

## 3. Native module roles

MEASURED, `readelf --dyn-syms` on `game/bin/linuxsteamrt64/`:

| Module | SDL role | Qt5 imports |
| --- | --- | ---: |
| `libtier0.so` | window, display and clipboard ownership — `SDL_CreateWindowWithProperties`, `SDL_SetWindowMouseGrab`, `SDL_GetKeyboardFocus`. **No event API at all.** | 0 |
| `libengine2.so` | the only **consumer** — `SDL_PollEvent`, plus `SDL_StartTextInput`, `SDL_SetTextInputArea`, `SDL_SetWindowRelativeMouseMode`, `SDL_WarpMouseGlobal`, `SDL_GetKeyFromScancode`, `SDL_SetModState`, gamepad/haptic | 0 |
| `libtoolframework2.so` | the only **producer** — `SDL_PushEvent`, `SDL_GetWindowID`, `SDL_GetKeyFromScancode`, `SDL_SetModState` | **2290** |
| `librendersystemvulkan.so` | `SDL_Vulkan_CreateSurface` / `SDL_Vulkan_GetInstanceExtensions` only | 0 |

Two facts follow directly:

- **Exactly one producer and one consumer of SDL events exist in the entire shipped tree.**
  `SDL_PushEvent` appears in `libtoolframework2.so` and nowhere else; `SDL_PollEvent` appears in
  `libengine2.so` and nowhere else.
- **`libengine2` links no Qt and `libtoolframework2` links no engine input.** Their
  `DT_NEEDED` sets:

```
libtier0.so          → libSDL3.so.0, libuuid, libstdc++, libm, libgcc_s, libc, libdl, libpthread
libengine2.so        → libSDL3.so.0, libtier0.so, libsteam_api.so, …          (no Qt)
libtoolframework2.so → libQt5Core/Gui/Widgets, libqtadvanceddocking,
                       libSDL3.so.0, libtier0.so, libfbxsdk, libz, …
```

Only one Qt platform plugin ships — `qt5_plugins/platforms/libqxcb.so` — and the engine forces
Qt onto it (`engine/Definitions/tools/QtMisc/QApp.def:73-78`):

```cpp
// xcb is the only platform plugin we ship, so don't leave Qt to pick wayland from the
// session and abort. An explicit choice in the environment still wins.
if ( qEnvironmentVariableIsEmpty( "QT_QPA_PLATFORM" ) )
{
    qputenv( "QT_QPA_PLATFORM", "xcb" );
}
```

⇒ On a Wayland session the editor runs through XWayland. There is no Wayland path.

---

## 4. Boot paths

### 4.1 `sbox` — retail client

`engine/Launcher/Sbox/Launcher.cs:16-35`:

```csharp
public class GameAppSystem : AppSystem
{
    public override void Init()
    {
        LoadSteamDll();
        TestSystemRequirements();
        base.Init();

        CreateGame();
        CreateMenu();

        var createInfo = new AppSystemCreateInfo()
        {
            WindowTitle = "s&box",
            Flags = AppSystemFlags.IsGameApp
        };

        InitGame( createInfo );
    }
}
```

No Qt, no tools mode. The window is created natively inside `SourceEngineInit`
(`engine/Sandbox.AppSystem/AppSystem.cs:358`), SDL owns it outright, and both input contexts
exist. `Game.IsPlaying` defaults to `true`
(`engine/Sandbox.Engine/Game/Game/Game.Scene.cs:10`), so the editor gate in §7.2 never fires.

### 4.2 `sbox-standalone`

`engine/Sandbox.AppSystem/StandaloneAppSystem.cs:20-45` — identical in shape, with one
structural omission:

```csharp
Standalone.SetupFromManifest( LoadManifest() );
Application.IsStandalone = true;
Application.AppId = Standalone.Manifest.AppId;

CreateGame();                       // <-- and no CreateMenu()

var createInfo = new AppSystemCreateInfo()
{
    WindowTitle = Standalone.Manifest.Name,
    Flags = AppSystemFlags.IsGameApp | AppSystemFlags.IsStandaloneGame
};

if ( Utility.CommandLine.HasSwitch( "-headless" ) )
    createInfo.Flags |= AppSystemFlags.IsConsoleApp;
```

**`CreateMenu()` is never called.** That is the entire input difference from `sbox`:
`IMenuDll.Current` stays null, so `InputRouter.Contexts` yields exactly one context, there is no
menu overlay that can veto the game's mouse (`GameInstanceDll.cs:513-517`), and Escape has no
pause-menu owner. `-headless` adds `IsConsoleApp` → `Application.IsHeadless` → `InputRouter.Frame()`
is skipped entirely (`EngineLoop.cs:337`).

### 4.3 `sbox-dev` — editor

`engine/Launcher/SboxDev/Launcher.cs:23-36` — **without `-project`, no AppSystem is ever
created**:

```csharp
if ( !HasCommandLineSwitch( "-project" ) && !HasCommandLineSwitch( "-test" ) )
{
    // we pass the command line, so we can pass it on to the sbox-launcher (for -game etc)
    ProcessStartInfo info = new ProcessStartInfo( NetCore.GetExecutablePath( "sbox-launcher" ), Environment.CommandLine );

    // Only let the shell start it on Windows - on Linux UseShellExecute goes through
    // xdg-open, which opens the launcher in a web browser rather than running it.
    info.UseShellExecute = OperatingSystem.IsWindows();
    ...
    Process.Start( info );
    return 0;
}

var appSystem = new EditorAppSystem();
appSystem.Run();
```

The editor you end up looking at is a **different process**, and no environment variable you set
on the first one reaches it. Always pass `-project <.sbproj>` when debugging input.

`engine/Sandbox.AppSystem/EditorAppSystem.cs:8-32` then calls `CreateMenu()`, `CreateGame()` **and**
`CreateEditor()`, with `Flags = IsGameApp | IsEditor`. `IsEditor` becomes
`_appSystem.SetInToolsMode()` (`AppSystem.cs:324-327`) and comes back to managed as
`Application.IsEditor` (`Bootstrap.cs:30`).

`CreateEditor()` brings up the Qt world. Note that `sbox-launcher` (the project picker) is a
fourth configuration again: `LauncherAppSystem : QtAppSystem` runs `QApp.exec()` with **no engine
loop at all** (`engine/Sandbox.AppSystem/QtAppSystem.cs:43-58`), so it has no SDL and no
`InputRouter`.

---

## 5. The Linux Qt→SDL bridge

### 5.1 What it is

MEASURED — `engine/Definitions/tools/QtMisc/QApp.def:99-105`, the tail of `QApp::Initialize()`:

```cpp
new QApplication( argc, argArray.Base() );

// Linux only, and a no-op elsewhere. There is no WNDPROC to subclass on X11, so Qt is
// the only thing that ever sees input for a tools window - this passes it on to the
// engine. Has to be after the QApplication exists, since it hangs off it.
ToolFramework2_InstallQtInputBridge();
```

The implementation (`src/toolframework2/qtinputbridge.cpp`) is not in the public repo, but it is
present in the shipped Linux binary and absent from the shipped Windows one:

```
libtoolframework2.so   →  CQtInputBridge
                          "ToolFramework2_InstallQtInputBridge: no QApplication yet,
                           tools input will not reach the engine"
toolframework2.dll     →  (neither string; zero SDL_ references at all)
```

⇒ **MEASURED: the Qt→SDL event bridge exists only on Linux, and only in the editor.**

### 5.2 Why it has to exist

Because on X11 Qt and SDL cannot both own input for the same window, and SDL offers no supported
way to inject input other than the event queue.

**MEASURED — `SDL_Send*` is unreachable from outside libSDL3.** `SDL_SetKeyboardFocus`,
`SDL_SendKeyboardKey`, `SDL_SendMouseMotion`, `SDL_SendMouseButton` and `SDL_SetMouseFocus` are
declared in `src/events/SDL_keyboard_c.h` / `SDL_mouse_c.h` but appear nowhere in
`src/dynapi/SDL_dynapi.sym`, and `SDL3-shared` is built with `C_VISIBILITY_PRESET "hidden"`
(`CMakeLists.txt:4129-4135`). Confirmed against the shipped library: it exports `SDL_PushEvent`,
`SDL_PeepEvents`, `SDL_PollEvent`, `SDL_PumpEvents` and no `SDL_Send*` input entry points.

That single fact explains `libtoolframework2`'s import list exactly — it is the minimum set
needed to hand-build an `SDL_Event`:

| Import | Purpose |
| --- | --- |
| `SDL_GetWindowID` | the `windowID` to stamp into the event |
| `SDL_SetModState` | push Qt's modifier state into SDL (`src/events/SDL_keyboard.c:952`; documented as *"does not change the keyboard state, only the key modifier flags"*) |
| `SDL_GetKeyFromScancode` | derive `event.key.key` the way SDL would |
| `SDL_PushEvent` | deliver it |

**MEASURED — `SDL_PushEvent` bypasses the video backend.** `src/events/SDL_events.c:1831-1846`
runs the watchers and filter, then `SDL_PeepEvents(..., SDL_ADDEVENT, ...)`; it never touches
`_this->PumpEvents`. Two constraints come with that:

- `SDL_INIT_EVENTS` must be up or `SDL_PeepEvents` returns −1 (`SDL_events.c:1188-1196`) — which
  is why `libtoolframework2` imports `SDL_Init`.
- `SDL_PollEvent` **always** pumps the backend first (`SDL_events.c:1579`, `:1723-1727`), and
  `SDL_PumpEventsInternal` asserts main-thread (`:1528`). So the engine's normal poll still runs
  SDL's own x11 pump alongside the injected events.

**MEASURED — why Qt is the one reading X.** Not because it wins a fight with SDL, but because
**the engine tells SDL not to try.**

`libtier0`/`libtoolframework2` set `SDL_VIDEO_X11_EXTERNAL_WINDOW_INPUT = 0` before every foreign
window they wrap — observed at runtime by interposing `SDL_SetHint`, and both modules import it.
In SDL's external-window path (`src/video/x11/SDL_x11window.c:553-571`) that hint gates the entire
`SetupWindowInput()` call:

```c
if (SDL_GetHintBoolean(SDL_HINT_VIDEO_X11_EXTERNAL_WINDOW_INPUT, true)) {
    SetupWindowInput(_this, window);
}
```

At `"0"` SDL skips `XSelectInput`, `X11_Xinput2Select` **and**
`X11_Xinput2SelectMouseAndKeyboard`, so `xinput2_mouse_enabled` stays false. SDL never asks X for
input on that window at all. The hint's own documentation — *"Don't call XSelectInput(), assuming
the native window code has done it already"* (`include/SDL3/SDL_hints.h:4221-4235`, default `"1"`)
— describes exactly this arrangement: Qt is the native window code, and the bridge carries what it
sees over to SDL.

⚠️ **Correction to an earlier revision of this document.** It claimed SDL selects
`ButtonPress|ButtonRelease|PointerMotion` on the foreign window, and inferred "exactly one of Qt
and SDL can win". Both are wrong:

- On any XI2-capable server (this one included) SDL zeroes those bits before calling
  `XSelectInput` — `SDL_x11window.c:489-522` sets `x11_pointer_events = 0` once
  `xinput2_mouse_enabled` is true, and takes button/motion through an XI2 selection instead. XI2
  selections are per-client and **not** exclusive, so Qt and SDL can both select on the same
  window.
- The X11 exclusive-select set is only `SubstructureRedirect`, `ResizeRedirect` and
  `ButtonPress` — `ButtonRelease` and the motion masks are freely shareable.

So there is no `BadAccess` contention in this configuration, and the reason the bridge exists is
the design decision above, not an X11 arbitration failure. The **consequence** of that decision is
recorded in [`bootstrap-linux/linux-input.md`](bootstrap-linux/linux-input.md) §7: because SDL has
no independent source, anything the bridge fails to forward is simply lost.

**MEASURED — SDL cannot share Qt's X connection.** SDL opens two of its own
(`src/video/x11/SDL_x11video.c:106,140`) and there is no X11 equivalent of the Wayland
`SDL_PROP_GLOBAL_VIDEO_WAYLAND_WL_DISPLAY_POINTER`. Wayland has a documented Qt-interop recipe
(`docs/README-wayland.md:155-235`) in which SDL imports Qt's `wl_display` and `wl_surface`; X11
has no counterpart. Since only `libqxcb.so` ships, that path is unavailable here.

**MEASURED — pointer grabs are exclusive across clients.** Qt grabs with `xcb_grab_pointer`
(`qxcbwindow.cpp:2307`) or XI2 (`qxcbconnection_xi2.cpp:832`); SDL grabs with `XGrabPointer`
(`SDL_x11window.c:2089`) and, after 5 s of `AlreadyGrabbed`, gives up permanently
(`:2100 broken_pointer_grab = true; // don't try again.`). SDL's own comment at `:2072-2079`
notes XI2 stealing implicit grabs on button press. This bears directly on the open relative-mouse
defects in `linux-input.md`.

**MEASURED — threading.** Qt reads X on a dedicated thread (`QXcbEventQueue::run` blocking in
`xcb_wait_for_event`, `qxcbeventqueue.cpp:228`); SDL asserts main-thread for pumping
(`SDL_events.c:1528`). Injection therefore has to happen on Qt's GUI thread, not its event thread.

### 5.3 The cost of injecting instead of sending

**MEASURED + INFERENCE.** Hand-pushed events do not update SDL's cached input state:
`SDL_GetKeyboardState` returns `keyboard->keystate` (`src/events/SDL_keyboard.c:935-943`), written
only inside `SDL_SendKeyboardKeyInternal`; `SDL_GetMouseState` likewise
(`src/events/SDL_mouse.c:1224-1235`). An engine driven by injected events must therefore be purely
event-driven and never consult SDL's state getters — consistent with `libengine2` importing
`SDL_PollEvent` and none of the state getters. `SDL_SetModState` is the one piece of SDL state
that *can* be poked from outside, which is exactly why it is imported.

Keyboard focus is the other casualty. `SDL_SetKeyboardFocus` is unexported, and
`SDL_SendKeyboardKeyInternal` stamps `event.key.windowID` from `keyboard->focus`
(`SDL_keyboard.c:649`) — not from an argument. An injector must set `windowID` itself, and SDL's
notion of which window has keyboard focus is never updated by injection. This is the mechanism
behind `InputRouter.cs:104` (`if ( !InputSystem.HasMouseFocus() ) MouseCursorVisible = true;`)
misbehaving under the editor, described in `linux-input.md` §4.

### 5.4 Version note

The local checkouts are Qt **5.15.2** (`qt/valve/qtbase/.qmake.conf:9` — Facepunch's fork of
Valve's build; the only input-relevant patch is a missing-dead-keysym fix in
`qxkbcommon.cpp`) and SDL **3.5.0** upstream (`SDL/include/SDL3/SDL_version.h:47-65`). The repo
ships SDL **3.4.14** (`libSDL3.so.0.4.14`) and Qt **5.15.2** runtime libraries. Line numbers cited
from the SDL checkout are 3.5.0's; the behaviour cited is present in 3.4.14 (the
`SDL_VIDEO_X11_EXTERNAL_WINDOW_INPUT` hint is documented "since 3.2.10"), but exact lines will
drift.

---

## 6. The Windows path, for contrast

MEASURED:

| | Linux | Windows |
| --- | --- | --- |
| toolframework2 SDL imports | `SDL_PushEvent`, `SDL_GetWindowID`, `SDL_GetKeyFromScancode`, `SDL_SetModState`, `SDL_Init`, `SDL_Quit`, `SDL_SetHint`, `SDL_GetTicksNS` | **none — zero `SDL_` references** |
| toolframework2 input imports | (via SDL) | `USER32`: `RegisterRawInputDevices`, `GetRawInputData`, `GetAsyncKeyState`, `GetKeyState`, `PostMessageA`, `GetCursorPos`, `SetCursorPos` |
| tier0 foreign-window property | `SDL.window.create.x11.window` | `SDL.window.create.win32.hwnd` |
| tier0 / engine2 SDL surface | same on both platforms | same on both platforms |

Both platforms wrap a **foreign** window handle in tier0 — the engine never creates its own
window when embedded. The difference is what happens next: on Windows the wrapped handle is a
real `HWND`, and SDL's Win32 backend receives its messages through the normal window procedure,
so no synthesis is needed. The editor supplements that with raw input for high-resolution mouse
data and `PostMessageA` for explicit forwarding.

That is the whole content of the `QApp.def` comment — *"There is no WNDPROC to subclass on X11"*.
The bridge is not a Linux workaround bolted onto a portable design; it is the Linux half of a
design that is per-platform from the start.

---

## 7. The managed layer

### 7.1 `InputRouter` has no editor context

`engine/Sandbox.Engine/Systems/Input/InputRouter.cs:73-91`:

```csharp
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
```

`IToolsDll` is never consulted. There are exactly two registration sites, both creating an
`InputContext` + `UISystem` pair in their DLL's `Bootstrap()`:
`engine/Sandbox.Menu/MenuDll.cs:519-534` and
`engine/Sandbox.GameInstance/GameInstanceDll.cs:948-963`. Nothing in
`game/addons/tools/Code/` references `InputRouter` at all.

Note the game context is gated on `IGameInstance.Current` — a live *session* — not on the DLL, so
it does not participate until a game is actually running.

### 7.2 The editor gate

`engine/Sandbox.Engine/Systems/UI/UISystem.cs:329-333`, inside `TickInput()`:

```csharp
if ( Application.IsEditor && !Game.IsPlaying )
{
    mouseState = InputContext.InputState.Ignore;
    buttonState = InputContext.InputState.Ignore;
}
```

In the editor, until play mode starts, the one remaining context reports `Ignore` for both mouse
and keyboard, so `InputRouter.Frame()` finds `activeMouse == null`: no relative mouse mode, no
cursor hiding. In `sbox` and `sbox-standalone` `Game.IsPlaying` is `true` from the start and this
branch never runs.

### 7.3 Editor UI input is polled, not routed

Editor input arrives two ways, neither through `InputRouter`:

1. **Per-widget Qt events.** `engine/Definitions/tools/QtWidgets/QWidget.def:277-315` declares
   `managed class Editor.Widget` with `InternalMousePressEvent`, `InternalKeyPressEvent`,
   `InternalFocusInEvent` and the rest; native `CWidget` subclasses `QWidget` and forwards its
   event overrides into managed, landing on `Widget.OnMousePress` / `OnKeyPress` /… in
   `engine/Sandbox.Tools/Qt/Widget.Events.cs`.
2. **Application-wide hooks.** `engine/Definitions/tools/tools.def:12-17` declares
   `GlobalMousePressed`, `GlobalMouseReleased`, `GlobalMouseWheel`, `GlobalKeyPressed`,
   `GlobalShortcutPressed`, implemented at `engine/Sandbox.Tools/ManagedTools.cs:161-232`.
   `GlobalKeyPressed` is the entire editor shortcut path — it builds `CTRL+`/`SHIFT+`/`ALT+`
   prefixed key names and calls `EditorShortcuts.Invoke`.

On top of that, the viewport *polls*: `SceneRenderingWidget.UpdateGizmoInputs`
(`engine/Sandbox.Tools/Qt/SceneRenderingWidget.cs:185-210`) and
`SceneViewportWidget.OnEditorPreFrame` read `Application.KeyboardModifiers`,
`Application.CursorPosition`, `Application.MouseButtons` and `EditorShortcuts.IsDown` — and they
do so from `OnPreFrame`, i.e. from inside the engine's *render* callback, not from a Qt event
handler.

### 7.4 The play handover

`engine/Sandbox.Tools/GameMode.cs:22-45` — this is where a Qt widget is handed to SDL:

```csharp
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
    ...
    _inPlay = widget;

    widget.Focus();
}
```

`winId()` is the X11 `Window` (or `HWND`); `RegisterWindowWithSDL` is one of the editor-only
`inputsystem.def` entries from §2.1. Focus changes are forwarded with `OnEditorGameFocusChange`
(`:70-87`), and `ClearPlayMode()` (`:47-65`) unwinds all of it.

`GameMode.cs:89-99` is the **only** place in the tree where managed code calls an
`InputRouter.On*` handler directly:

```csharp
private static void OnPlayWidgetMouseMove( Vector2 local )
{
    // SDL handles position when the widget is focused; only fill in the gap when unfocused.
    if ( _inPlay is null || _inPlay.IsFocused )
        return;

    var pos = new Vector2( (int)local.x, (int)local.y );
    var delta = pos - InputRouter.MouseCursorPosition;

    InputRouter.OnMousePositionChange( pos.x, pos.y, delta.x, delta.y );
}
```

### 7.5 Getting back out

While the game holds input there are exactly two routes back to the editor:

- **F1–F12** — `engine/Sandbox.Engine/Systems/Input/InputRouter.Input.cs:250-261`:

  ```csharp
  if ( scanButtonCode >= ButtonCode.KEY_F1 && scanButtonCode <= ButtonCode.KEY_F12 )
  {
      if ( !down || repeat ) return;

      IToolsDll.Current?.OnFunctionKey( scanButtonCode, modifiers );
      ...
  }
  ```

  `ToolsDll.OnFunctionKey` sets `EditorShortcuts._timeSinceGlobalShortcut = 0`, which
  `ManagedTools.GlobalKeyPressed:211` uses to suppress the same key when Qt later sees it.

- **Escape** — `InputRouter.OnKey` → `OnEscapePressed` → `Input.EscapePressed`, consumed by
  `engine/Sandbox.Tools/ToolsDll.cs:285-292`, which focuses the editor window and so releases
  mouse capture. In `sbox` the same flag is consumed instead by `MenuDll.LateTick()` to open the
  pause menu, guarded on `!Application.IsEditor`.

---

## 8. Consequences

### 8.1 What the structure predicts

- **The editor and the game fail independently** because they share no code below
  `EngineLoop.FrameStart` — one is Qt widget events and polling, the other is SDL → `InputRouter`.
  A fix in one is invisible to the other.
- **Anything reading SDL's cached input state is broken under the editor on Linux**, by
  construction (§5.3) — injected events update the queue, never the state. Symptoms show up as
  focus and capture state, not as missing events.
- **`sbox-standalone` is the cleanest reproduction case** for engine-side input bugs: same SDL
  path as `sbox`, one input context, no menu, no Qt. If something reproduces in `sbox-dev` and in
  `sbox-standalone`, it is not a Qt problem.
- **Pointer-grab conflicts are inherent** (§5.2) rather than incidental — any editor feature that
  grabs the pointer in Qt while the game wants relative mouse mode is contending for one
  server-wide resource.

### 8.2 Where a fix belongs

Following [`fix-in-the-layer-that-owns-the-bug`](bootstrap-linux/README.md): the bridge itself is
native and unbuildable here, so bridge defects can only be observed (via `LD_PRELOAD` shims) or
worked around above it. Defects in `InputRouter`, `InputContext`, `UISystem.TickInput` or the Qt
managed layer are ordinary managed bugs and should be fixed in `engine/` — expressed with
`OperatingSystem.IsLinux()` checks, never `#if`, since `game/bin/managed/` is one set of binaries
for both platforms.

### 8.3 The known defects

[`bootstrap-linux/linux-input.md`](bootstrap-linux/linux-input.md) is the defect log. As of
2026-08-26, two are **fixed and verified at runtime** and one is newly characterised:

- **§1 viewport click-through — fixed.** `SceneOverlayWidget`, a top-level `Qt::Tool` window over
  the viewport, kept a full XShape input region and swallowed every click; keyboard failed as a
  consequence, since the viewport is `FocusMode.Click`. The root cause is deeper than "a top level
  has no parent to propagate to": `QWidget::setAttribute` is a **no-op** for
  `WA_TransparentForMouseEvents` (`qwidget.cpp:11096`), and Qt promotes it to
  `Qt::WindowTransparentForInput` only in `adjustFlags` (`:971`), which runs only at construction
  and reparent. Fixed by maintaining the input region directly —
  `engine/Sandbox.Tools/Qt/X11InputRegion.cs` and `Widget.RefreshInputRegion()`.
- **§5 `SetRelativeMouseMode` re-asserted every frame — fixed.** Measured live at ~150 calls/s.
  Debounced to fire only on change. This mattered more than it looked: SDL's grab path retries for
  five seconds and then latches `broken_pointer_grab` for the life of the process.
- **§7 game view — partially working, root cause identified.** When the game takes the mouse, SDL
  grabs and confines the pointer; an active X11 grab redirects pointer events to the grabbing
  client, so **Qt goes blind and the bridge is starved** — and because the engine sets
  `SDL_VIDEO_X11_EXTERNAL_WINDOW_INPUT = 0` (§5.2), SDL never selected input on that window either,
  so nothing picks the events up. A watchdog in `InputRouter.AllowMouseCapture()` now refuses a
  capture that delivers nothing for 2 s, which stops play mode trapping the editor; input then
  arrives intermittently as capture engages and is released. That is a safety net, not a fix.

§2–§4 (look discarded on `HasMouseFocus()`, the `QCursor::pos()`-read vs `SetNativeCursorPos`-write
mismatch, the wrap-returns-`true` delta zeroing) remain confirmed in source but not re-measured.
Note §2's premise leaned on the event-mask claim corrected in §5.2 and should be re-derived before
it is trusted.

**`SBOX_INPUT_DEBUG` is implemented.** `engine/Sandbox.Engine/Systems/Input/InputDebug.cs` reads the
variable and emits `[routerdbg]` (capture state, watchdog, `HasMouseFocus`, SDL relative mode, event
delivery rate) and `[gamemode]` (the play handover, focus changes, Qt mouse-move rate) into
`game/logs/sbox-dev.log`. `[inputdbg]`, the scene-viewport tag `run-editor-debug.sh` also greps for,
is still unclaimed. Everything logs on change or at 1 Hz — an unconditional log from
`InputRouter.Frame()` is the mistake `linux-input.md` §5 records.

---

## Appendix — reproducing every measurement

Run from the repo root.

```bash
# Which modules import which SDL symbols (Linux)
for lib in libtier0.so libengine2.so libtoolframework2.so librendersystemvulkan.so; do
  echo "== $lib"
  readelf --dyn-syms -W game/bin/linuxsteamrt64/$lib \
    | awk '$7=="UND" && $8 ~ /^SDL_/ {print $8}' | sed 's/@.*//' | sort -u | tr '\n' ' '
  echo
done

# The single producer and the single consumer
cd game/bin/linuxsteamrt64
for f in *.so; do
  readelf --dyn-syms -W "$f" | awk '$7=="UND"{print $8}' | sed 's/@.*//' \
    | grep -qE '^SDL_(PushEvent|PollEvent)$' && echo "$f"
done; cd -

# Qt import counts and library dependencies
readelf --dyn-syms -W game/bin/linuxsteamrt64/libtoolframework2.so \
  | awk '$7=="UND"{print $8}' | grep -c '^_ZN[0-9]*Q\|^_ZNK[0-9]*Q'
readelf -d game/bin/linuxsteamrt64/libtoolframework2.so | grep NEEDED

# The bridge: present on Linux, absent on Windows
strings -a game/bin/linuxsteamrt64/libtoolframework2.so | grep -i QtInputBridge
strings -a game/bin/win64/toolframework2.dll          | grep -i QtInputBridge   # no output

# Windows toolframework2 has no SDL at all, and uses raw input instead
strings -a game/bin/win64/toolframework2.dll | grep -cE '^SDL_[A-Za-z]'          # 0
objdump -p game/bin/win64/toolframework2.dll | grep 'DLL Name'

# Foreign-window properties, both platforms
strings -a game/bin/linuxsteamrt64/libtier0.so | grep '^SDL\.window\.create'
strings -a game/bin/win64/tier0.dll            | grep '^SDL\.window\.create'

# Only the xcb platform plugin ships (imageformats/ also exists; platforms/ is the input-relevant one)
find game/bin/linuxsteamrt64/qt5_plugins/platforms -name '*.so'

# Shipped SDL version
ls game/bin/linuxsteamrt64/libSDL3.so.0.*

# The ABI boundary
cat engine/Definitions/engine/inputrouter.def
sed -n '8,11p' engine/Definitions/engine/inputsystem.def       # editor-only entries
grep -n 'InputRouter' engine/Sandbox.Engine/Interop.Engine.cs | sed -n '1,20p'

# The trunk, the contexts, the editor gate, the handover
sed -n '225,232p' engine/Sandbox.Engine/Core/EngineLoop.cs
sed -n '73,91p'   engine/Sandbox.Engine/Systems/Input/InputRouter.cs
sed -n '329,333p' engine/Sandbox.Engine/Systems/UI/UISystem.cs
sed -n '22,45p'   engine/Sandbox.Tools/GameMode.cs
sed -n '99,105p'  engine/Definitions/tools/QtMisc/QApp.def

# What reads SBOX_INPUT_DEBUG, and what emits the tags
grep -rn 'SBOX_INPUT_DEBUG' --include='*.cs' engine game
grep -rn 'InputDebug\.' --include='*.cs' engine
```

Against the local upstream checkouts (`~/Documents/GitHub/SDL`, `~/Documents/GitHub/qt`):

```bash
cd ~/Documents/GitHub/SDL
grep -n 'SDL_SendKeyboardKey\|SDL_SetKeyboardFocus\|SDL_SendMouseMotion' src/dynapi/SDL_dynapi.sym   # no output
sed -n '1831,1846p' src/events/SDL_events.c            # SDL_PushEvent bypasses PumpEvents
sed -n '556,571p'   src/video/x11/SDL_x11window.c      # foreign window wrap
sed -n '490,523p'   src/video/x11/SDL_x11window.c      # SetupWindowInput: the input mask
sed -n '4221,4235p' include/SDL3/SDL_hints.h           # SDL_VIDEO_X11_EXTERNAL_WINDOW_INPUT
sed -n '103,140p'   src/video/x11/SDL_x11video.c       # SDL opens its own connections

cd ~/Documents/GitHub/qt/valve/qtbase
sed -n '1219,1235p' src/plugins/platforms/xcb/qxcbwindow.cpp   # XFixes input shape region
sed -n '250,264p'   src/plugins/platforms/xcb/qxcbwindow.cpp   # Qt's event masks
sed -n '2284,2316p' src/plugins/platforms/xcb/qxcbwindow.cpp   # setMouseGrabEnabled
```
