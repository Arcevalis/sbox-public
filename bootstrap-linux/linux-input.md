# Editor input on Linux — defect log

Defects in the editor's input path on Linux, each with the measurement that found it. For **how
the input paths are built** — the three executables, the Qt→SDL bridge and why it exists — see
[`../INPUT-ARCHITECTURE.md`](../INPUT-ARCHITECTURE.md). This file is the defect log only.

Environment as measured: Qt 5.15.2 (xcb), SDL 3.4.14 shipped, Wayland session via XWayland,
`XDG_SESSION_TYPE=wayland`, `DISPLAY=:0`.

**Status — 2026-08-26.** §1 and §5 are **fixed and verified at runtime**. §7 is new: the
game-view blackout, root cause narrowed to a named mechanism but not yet fixed. §2–§4 remain
"cause confirmed in source, symptom not re-measured" — and note that §2's premise is now doubtful,
because the focus argument it rests on was built on a claim about SDL's event-mask selection that
turned out to be wrong (see [`../INPUT-ARCHITECTURE.md`](../INPUT-ARCHITECTURE.md) §5.2).

Before this pass, the symptom was: **the viewport accepted no mouse and no keyboard input at all,
in scene view or game view, while the rest of the editor's Qt input worked normally.** Scene view
is now working. Game view is not.

---

## 1. The scene viewport swallowed every click — **FIXED**

`SceneOverlayWidget` is a **top-level** `Qt::Tool` window parked exactly over the viewport, made
click-through with `TransparentForMouseEvents`
(`game/addons/tools/Code/Scene/SceneView/SceneOverlayWidget.cs:14`, `:31`):

```csharp
WindowFlags = WindowFlags.FramelessWindowHint | WindowFlags.Tool;
...
TransparentForMouseEvents = true;
```

### Measured, against the running editor

`bootstrap-linux/patches/probe` while the editor was up, plus `xwininfo`:

```
0x5a0002a   1179x736  @ +453+169   depth 24   ShapeInput=FULL (blocks)   ← SceneRenderingWidget
0x5a0002d   1179x736  @ +453+169   depth 32   ShapeInput=FULL (blocks)   ← SceneOverlayWidget (top-level)
```

Pixel-identical geometry, and `xwininfo -events` showed **both** selecting the full mask including
`ButtonPress`. The server hit-tests root children, finds the overlay on top, and delivers every
click there; Qt then drops it. Keyboard failure follows as a consequence rather than independently:
the viewport is `FocusMode.Click` (`engine/Sandbox.Tools/Qt/SceneRenderingWidget.cs:53`), so a
window that can never be clicked can never take focus.

Confirmed live by clearing the region on the running process with `XShapeCombineRectangles`
(0 rects) — scene-view input started working immediately, with no code change.

### Why the attribute never reached the server

Reading Qt 5.15.2 (`~/Documents/GitHub/qt/valve/qtbase`) gives the precise mechanism, and it is
worse than "a top level has no parent to propagate to":

- **`QWidget::setAttribute` is a documented no-op for this attribute** — `qwidget.cpp:11096-11097`
  is literally `case Qt::WA_TransparentForMouseEvents: break;`.
- The only thing that reaches X is `Qt::WindowTransparentForInput`, applied by
  `QXcbWindow::setTransparentForMouseEvents` (`qxcbwindow.cpp:1211-1236`) as an XFixes input-shape
  region with `nrect = 0`, and called only from `setWindowFlags` (`:918`).
- Qt promotes the attribute to that flag in `QWidgetPrivate::adjustFlags`
  (`qwidget.cpp:971-972`) — but `adjustFlags` runs only from `QWidgetPrivate::init` (`:1046`) and
  `setParent_sys` (`:10610`). **Setting the attribute after construction never reaches it.**

Independent confirmation from the live process: the overlay carried Qt's `defaultEventMask`
(including `ButtonPress`), not the `transparentForInputEventMask` that `setWindowFlags` installs
when the flag is set (`qxcbwindow.cpp:249-264`, `:905-918`). The flag was never applied.

### The fix

`WA_TransparentForMouseEvents` is broken for **any** top-level on X11, not just this one —
`game/addons/tools/Code/Editor/Welcome/WelcomeOverlay.cs:11-14` has the byte-identical pattern. So
the fix is in the Qt layer that owns the broken promise, not in the scene view:

- **`engine/Sandbox.Tools/Qt/X11InputRegion.cs`** (new) — `XShapeCombineRectangles` via P/Invoke on
  `libXext.so.6`, over its own X connection. Needed because `QWidget.def` exposes no region API and
  none can be added (ABI hash), and Qt's own flag is all-or-nothing — it would also kill input to
  the overlay's own tool widgets (`EditorTool.AddOverlay`, `EditorTool.cs:236`).
- **`Widget.TransparentForMouseEvents`** now also maintains the region when the widget is a
  top-level on Linux, and **`Widget.RefreshInputRegion()`** (public, so the runtime-compiled addon
  can reach it) recomputes it as the union of visible children.
- **`SceneOverlayWidget.UpdateDimensions`** calls it from the existing per-frame geometry hash,
  widened to include the children's rects. Per-frame is deliberate: Qt destroys and recreates the
  XID on reparenting (docking a viewport), and `OnWinIdChanged` is `internal virtual` and
  unreachable from an addon.

Verified after the change, in a fresh process with no shims loaded:

```
0x5a0002d   1216x736  @ +480,183   ShapeInput=EMPTY (click-through)
```

Scene view confirmed working: camera, selection, keyboard.

## 2. Mouse look is silently discarded

`engine/Sandbox.Engine/Systems/Input/InputRouter.cs:103-104`:

```csharp
MouseCursorVisible = !mouseCaptureMode && (activeMouse is not null && activeMouse.MouseState == InputContext.InputState.UI);
if ( !InputSystem.HasMouseFocus() ) MouseCursorVisible = true;
```

and `engine/Sandbox.Engine/Systems/Input/Input.cs:95-96`:

```csharp
if ( MouseCursorVisible )
    AnalogLook = default;
```

So any frame where SDL reports no mouse focus throws the look delta away entirely. Under the
editor SDL's focus tracking is unreliable by construction: input is injected with
`SDL_PushEvent`, and the focus-setting entry points (`SDL_SetKeyboardFocus`, `SDL_SetMouseFocus`)
are not exported from libSDL3 — see `../INPUT-ARCHITECTURE.md` §5.3.

`SetCursorPosition` has the same guard on both `IsAppActive()` and `HasMouseFocus()`
(`InputRouter.cs:157-158`), and `SetCursorType` picks `"none"` only when `!MouseCursorVisible`
(`:204`).

## 3. Mouse look accelerates while held

Holding a direction winds the turn rate up. `ComputeAnalogLook` is purely proportional, so
acceleration means the *delta itself* is growing.

`LockCursorToCanvas` measures the pointer's offset from the viewport edge and warps it — but
**reads and writes through different APIs**
(`game/addons/tools/Code/Extensions/SceneEditorExtensions.cs:47-62`):

```csharp
var pos = canvas.FromScreen( Application.CursorPosition );          // QCursor::pos()
...
Application.UnscaledCursorPosition += (newPos - pos) * canvas.DpiScale;   // CQUtils::SetNativeCursorPos
```

When a warp does not fully land, the residue is still there next frame and is measured again on
top of the new movement, compounding every frame. A read and a write that go through the same
path — or a synchronous X11 warp, so the next frame cannot measure a warp still in flight —
would remove the compounding. A single-frame clamp would stop a dropped warp becoming a spin.

## 4. Scene-view right-drag dies at the viewport edge

Hold RMB and leave the viewport and the camera stops. `LockCursorToCanvas` wraps the cursor, and
because the wrap returns `true` the caller zeroes the delta — so every frame is either a zeroed
delta or a bogus full-width one. Both call sites do this
(`SceneEditorExtensions.cs:92-93` and `:267-268`):

```csharp
if ( lockCursor && LockCursorToCanvas( canvas ) )
    delta = Vector2.Zero;
```

Traced frame by frame. Almost certainly the same read/write mismatch as §3.

## 5. `SetRelativeMouseMode` re-asserted every frame — **FIXED**

`InputRouter.Frame()` called it unconditionally on both branches, once per frame, whether or not
the mode was changing (`InputRouter.cs:114`, `:118`).

Measured live with `libsdlspy.so`: **`setRelMode` climbing ~150/second**, continuously, with the
editor merely sitting open.

This is not just wasted calls. Reading SDL (`SDL_x11window.c:2050-2120`): `X11_SetWindowMouseGrab`
retries `XGrabPointer` 100 times at 50 ms — **up to five seconds** — and on final failure sets
`data->videodata->broken_pointer_grab = true` with the comment `// don't try again.` That is a
**process-lifetime latch on `SDL_VideoData`**: once set, every later grab for every window is
skipped. SDL's own comment at `:2071-2079` notes XI2 grabs the pointer on button press, which
returns `AlreadyGrabbed`. At 150 attempts/second a collision is close to certain.

Fixed by debouncing in `InputRouter` — a `static bool?` remembering the last state, so the native
call fires only on an actual change. Verified: **`setRelMode=1`, flat.**

## 6. Unverified

- **Clicks end to end.** Counts confirmed they reach `InputRouter`, but the sweeper sample has no
  click handler — its entire input surface is `Input.AnalogLook` (yaw only) and `Input.AnalogMove`.
  Needs a scene with a `PlayerController` to see clicks act.
- **Escape / F-keys while captured.** `InputRouter.OnKey` → `IToolsDll.OnFunctionKey` →
  `EditorShortcuts.Invoke` (`InputRouter.Input.cs:250-261`) and the Escape route
  (`ToolsDll.cs:285-292`) are the only ways back to the editor while the game holds input.
  Present in source, not re-tested.

## 7. Game view gets no input at all — **root cause narrowed, not fixed**

With §1 fixed, scene view works and game view still receives nothing. This is a *different*
defect, not a leftover of §1.

### Measured

`libsdlspy.so`, one session, sweeper sample. `push` is `SDL_PushEvent` (the Qt→SDL bridge, the
only producer); `pollHit` is `SDL_PollEvent` returning an event (libengine2, the only consumer):

```
21:24:38   push=24     pollHit=40      ← testing scene view
21:24:39   push=331    pollHit=346
21:24:42   push=3234   pollHit=3250
21:24:44   push=3799   pollHit=3815    ← Play pressed; SDL window 5 created for the play widget
21:24:45   push=3800   pollHit=3817    ← setRelMode 1->2, the game takes the mouse
21:26:31   push=3800   pollHit=3817    ← two minutes later, unchanged
```

**The bridge stops emitting the instant play mode starts, and nothing replaces it.** Not a
routing problem — a total absence of events.

Two supporting facts from the same session:

- **Every** event the bridge pushes is stamped with the SDL_WindowID of the **main editor
  window**, never the viewport's own SDL window, even for events over the viewport. The play
  widget's window received `pushed=0`. This mismatch is real but is *not* the blocker — the events
  stop entirely, so there is nothing left to mis-address.
- The engine sets **`SDL_VIDEO_X11_EXTERNAL_WINDOW_INPUT = 0`** before every foreign window it
  wraps (observed by interposing `SDL_SetHint`; both `libtier0` and `libtoolframework2` import it).

### Why that hint matters

`SDL_x11window.c`, the entire external-window path:

```c
if (w) {
    window->flags |= SDL_WINDOW_EXTERNAL;
    if (!SetupWindowData(_this, window, w)) return false;
    if (SDL_GetHintBoolean(SDL_HINT_VIDEO_X11_EXTERNAL_WINDOW_INPUT, true)) {
        SetupWindowInput(_this, window);
    }
    return true;
}
```

At `"0"` that skips **all** of `SetupWindowInput` — not just `XSelectInput`, but
`X11_Xinput2Select` and `X11_Xinput2SelectMouseAndKeyboard` too, so `xinput2_mouse_enabled` stays
false. SDL therefore never asks X for input on the play widget.

On Windows this is correct: `toolframework2.dll` has zero SDL references and the real `HWND`
receives messages through the window procedure. On Linux it leaves the bridge as the *only*
possible source — and the bridge goes quiet exactly when the game needs it.

### Play mode used to trap the editor — **mitigated**

Worse than "the game gets no input": pressing Play **locked the cursor inside the viewport bounds,
hid it everywhere else, and made the Stop button unclickable.** The only way out was to kill the
editor.

That is the signature of an X11 pointer grab. SDL grabs and confines the pointer when the game
takes the mouse, and an active grab **redirects all pointer events to the grabbing client** — so
Qt stops seeing them, the bridge has nothing to forward, and the route that would carry the Escape
keystroke back out is the very route that just died. The `push` counter freezing immediately after
`setRelMode` went 1→2 is that ordering, not the bridge choosing to stop.

⚠️ This also means an earlier reading in this file was backwards: the bridge is most likely **fine
and starved**, not broken.

Mitigated in `InputRouter.AllowMouseCapture()`: on Linux, if a mouse capture delivers **no input at
all** for 2 seconds, the capture is refused for the rest of that capture request. A working capture
produces motion almost immediately, so this only fires on a capture that has taken the cursor and
gone silent. The game keeps running and the editor stays usable.

Escape hatches that do **not** depend on the bridge, because they are dispatched by
`EditorShortcuts` from `ManagedTools.GlobalKeyPressed` (a direct Qt→managed callback):

- **F5** — `editor.toggle-play` (`EditorScene.cs:190`), stops play mode.
- **F8** — `editor.eject` (`SceneViewWidget.Game.cs:42`), returns to the editor camera.

An X11 pointer grab takes the pointer only, never the keyboard, so these should reach Qt even while
the pointer is confined.

### Next step, untested

Force the hint back on so SDL sets up its own input on the wrapped window. It has to be done from
a shim (`patches/sdlhint.c`): the engine's explicit `SDL_SetHint` beats the environment variable of
the same name. Per SDL's source this should not contend with Qt — SDL does not take core
`ButtonPress` while XI2 is up, and XI2 selections are not exclusive between clients.

Unknowns if it works: double delivery in the editor (bridge *and* SDL both feeding the queue), and
whether SDL's focus tracking is good enough for the play widget given `SDL_SetKeyboardFocus` is
unexported.

## 8. Editor aborts on shutdown

Closing the editor can abort with a stack overflow, on the teardown path:

```
Stack overflow.
   at Native.QWidget.winId()
   at Editor.GameMode.ClearPlayMode()
   at Editor.SceneViewportWidget.ClearGameView()
   at Editor.SceneViewWidget.OnSceneStop()
   ...
   at Editor.SceneDock.OnDestroyed()
   at Editor.QObject.NativeShutdown()
```

`SceneDock.OnDestroyed` destroys the session, which stops play mode, which reaches
`GameMode.ClearPlayMode` and calls `winId()` on a widget that is already being torn down
(`GameMode.cs:59`). Observed once; not on any path touched by the §1/§5 fixes. Not investigated.

## Gotchas

- `game/addons/` compiles at runtime as a **separate assembly** and can only see `public` API
  from `Sandbox.Tools`. Referencing an `internal` type there fails the whole addon compile with
  "inaccessible due to its protection level".
- `run-editor.sh` `cd`s to `game/` first, so a relative `-project` path resolves against `game/`.
- `sbox-dev` without `-project` re-execs `sbox-launcher` as a separate process
  (`engine/Launcher/SboxDev/Launcher.cs:23-36`) — the editor you end up looking at is not the one
  you launched, and no environment variable reaches it.
- `SBOX_INPUT_DEBUG` is currently a **no-op**: `run-editor-debug.sh` exports it and greps for
  `[inputdbg]` / `[routerdbg]` / `[gamemode]`, but no `.cs` file in the tree reads it or emits
  those tags. Today that script yields `libsdlspy.so` counters and nothing else.
- Synthetic input (`xdotool`) is unreliable on a Wayland session — trials reported the pointer
  over the wrong window. Real input, or X-level state queries, are trustworthy; XTest is not.
- **`XWarpPointer` does work** on this XWayland session — measured, the pointer lands exactly where
  asked. So §3/§4 are not "Wayland refuses to warp"; the read/write API mismatch is the real
  suspect and still needs tracing.
- **Do not interpose SDL functions on the render path.** A shim that wrapped
  `SDL_SetWindowRelativeMouseMode` and `SDL_PushEvent` produced
  `The selected graphics queue does not support presenting a swapchain image` plus a fatal Wayland
  disconnect, in exactly the runs where it was loaded, and the failures vanished when it was
  removed. Keep shims to configuration calls (`SDL_SetHint`) or pure observation, and always
  re-verify against a no-shim baseline before believing a result.
- `libsdlspy.so` appends to its log. Truncate it per run or you will read two sessions as one.
- `.editorconfig` sets `end_of_line = crlf` while `.cs` files are LF in a Linux checkout, so
  `format --verify` fails tree-wide here. Pre-existing, unrelated to input.
