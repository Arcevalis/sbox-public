# Editor input on Linux — defect log

Defects in the editor's input path on Linux, each with the measurement that found it. For **how
the input paths are built** — the three executables, the Qt→SDL bridge and why it exists — see
[`../INPUT-ARCHITECTURE.md`](../INPUT-ARCHITECTURE.md). This file is the defect log only.

Environment as measured: Qt 5.15.2 (xcb), SDL 3.4.14 shipped, Wayland session via XWayland,
`XDG_SESSION_TYPE=wayland`, `DISPLAY=:0`.

**Status — 2026-08-27.**

| | state |
| --- | --- |
| §1 viewport swallowed every click | **fixed**, verified at runtime |
| §5 `SetRelativeMouseMode` spam | **fixed**, verified at runtime |
| §7 game view | **partially working** — input arrives, but capture oscillates; root cause identified, not fixed |
| §7 key names (W/S vs A/D) | **fixed**, verified at runtime — scene view keyboard fully works |
| §7b editor swallows the game's keys | **fixed**, verified at runtime — game view keyboard works |
| §7c pointer never captured in game view | **fixed**, verified at runtime — hides, confines, 360° works |
| §7c fast flick escapes the viewport | open, mechanism identified, not investigated |
| §8 editor aborts on shutdown | new, not investigated |
| §2–§4 | cause in source, symptom not re-measured |

Before this pass the symptom was: **the viewport accepted no mouse and no keyboard input at all, in
scene view or game view, while the rest of the editor's Qt input worked normally.** Scene view now
works fully. Game view receives input but only intermittently, for the reason in §7.

Two earlier readings in this file were wrong and have been corrected in place rather than left
standing — flagged with ⚠️ where they appear:

- §2's premise leaned on a claim about SDL's event-mask selection that turned out to be false
  (see [`../INPUT-ARCHITECTURE.md`](../INPUT-ARCHITECTURE.md) §5.2). Re-derive it before trusting it.
- "The bridge stops emitting at play" was backwards. The bridge is **starved**, by SDL's pointer
  grab taking pointer events away from Qt.

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

## 2. Mouse look is silently discarded — ⚠️ premise not observed in practice

⚠️ **Measured against a running editor in §7c and it did not hold.** `HasMouseFocus()` tracked focus
correctly for the whole of a play session, and `MouseCursorVisible` stayed `False` throughout, so
nothing was discarding `AnalogLook`. The code below is still shaped the way it is described, but do
not cite this section as the cause of a look bug without re-measuring — `[routerdbg]` now reports
`hasMouseFocus` and `cursorVisible` directly.


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

### Runtime status after the watchdog — game view partially works

With the watchdog in, game view **receives input**: mouse look drives the camera and A/D move the
cube. The cursor is no longer trapped and Stop is clickable.

But the watchdog fired **9 times in a single play session**, which is the real finding. It is not
refusing capture once and settling — the system is oscillating:

```
capture engages → SDL grabs and confines the pointer → Qt is starved of pointer events
  → 2s with nothing delivered → watchdog refuses capture → SDL ungrabs
  → Qt sees the pointer again → input flows → capture re-engages → repeat
```

That is the mechanism, confirmed end to end. An active X11 pointer grab **redirects pointer events
to the grabbing client**, so Qt — and therefore the bridge — goes blind for as long as the grab
holds. Input working *intermittently* is the signature.

⇒ The watchdog is a safety net, not a fix. The fix has to stop the grab starving Qt, either by
giving SDL its own input selection on that window (the hint, above) or by feeding `InputRouter`
from Qt events directly in managed — `GameMode.cs:88-98` already does exactly that for cursor
position and is the obvious pattern to extend.

### W and S do not move the cube; A and D do — **ROOT CAUSE FOUND**

Bindings are stock — `Forward`/`Backward`/`Left`/`Right` = W/S/A/D
(`engine/Sandbox.Engine/Systems/Input/Input.Common.cs:14-17`), and the sweeper project defines no
`InputActions` of its own. So the question is who consumes the key first: editor shortcuts are
dispatched from `ManagedTools.GlobalKeyPressed`, which returns `true` to **swallow** the key before
it can reach the game.

The answer is that **`KeyEvent.Name` is wrong on Linux for exactly the letters that work**, so no
editor shortcut matches them and they fall through to the game.

`KeyEvent.GetKeyName()` (`engine/Sandbox.Tools/Qt/Event/MouseEvent.cs`) switched on
`NativeKeyCode` — `QKeyEvent::nativeVirtualKey()` — against a table of **Win32 virtual-key
codes**. On X11 that field is not a VK code: `QXcbKeyboard::handleKeyEvent` passes the **keysym**
as `nativeVirtualKey` (`qxcbkeyboard.cpp:890`, `:935` —
`handleExtendedKeyEvent( window, time, type, qtcode, modifiers, code, sym, state, text, autorep )`,
where `sym = xkb_state_key_get_one_sym( xkbState, code )`).

The two ranges overlap without agreeing. An unshifted letter arrives as its **lowercase** keysym,
and the lowercase ASCII range is exactly Windows' numpad range:

| key | X11 keysym | Windows table says | shortcut match |
| --- | --- | --- | --- |
| `a` | `XK_a` 0x61 | `VK_NUMPAD1` → `"KP_1"` | none → **falls through to the game** |
| `d` | `XK_d` 0x64 | `VK_NUMPAD4` → `"KP_4"` | none → **falls through to the game** |
| `w` | `XK_w` 0x77 | not in the table | → `KeyCode.W` → `"W"` → `scene.move-forward` / `tools.position-tool` **swallow it** |
| `s` | `XK_s` 0x73 | not in the table | → `KeyCode.S` → `"S"` → `scene.move-backward` **swallows it** |

0x6C (`l`) and 0x70–0x7A (`p`–`z`) are the gaps in Windows' VK numbering, which is why W and S
name correctly and A–K, M, N, O do not. That is the whole of the W/S-vs-A/D asymmetry: it is not
key-specific, and it is not the §7 oscillation.

The same bug breaks the editor's own keyboard far more widely, since `Ctrl` does not shift the
keysym level — every `CTRL+<letter>` in the a–k, m–o range is misnamed, and three of them collide
with real bindings:

| pressed | named | actually invokes |
| --- | --- | --- |
| `Ctrl+A` | `CTRL+KP_1` | whatever `CTRL+KP_1` is bound to |
| `Ctrl+C` | `CTRL+KP_3` | whatever `CTRL+KP_3` is bound to |
| `Ctrl+M` | `CTRL+KP_MINUS` → falls back to bare `KP_MINUS` | the `KP_MINUS` shortcut |
| `Ctrl+D`, `Ctrl+F`, `Ctrl+G`, `Ctrl+I`, `Ctrl+K`, `Ctrl+N`, `Ctrl+O` | `CTRL+KP_*` | nothing — dead |

Digits, `Space` and `;` work by coincidence: `XK_0`–`XK_9` (0x30–0x39), `XK_space` (0x20) and
`XK_semicolon` (0x3B) happen to equal their VK codes. `XK_Return` is 0xFF0D, not 0x0D, so Enter
falls through to the `KeyCode` path and names correctly anyway.

### Fixed

`GetKeyName()` now dispatches on platform rather than assuming Win32:

- `NameFromVirtualKey( uint )` — the original table, unchanged, used when
  `OperatingSystem.IsWindows()`.
- `NameFromKeysym( uint )` — new, used otherwise. Printable ASCII keysyms are the ASCII code, so
  `XK_a`–`XK_z` and `XK_A`–`XK_Z` both name the uppercase letter and `XK_0`–`XK_9` name the digit;
  punctuation is spelled out; the keypad is mapped from 0xFF80–0xFFB9, including the num-lock-off
  keysyms (`XK_KP_Home` → `"KP_7"` and friends) that the Windows path never sees because Win32
  reports `VK_NUMPAD*` regardless.

Keysym values verified against `/usr/include/X11/keysymdef.h`.

**Verified at runtime.** Scene view keyboard now works fully — WASD/QE fly, and the gizmo tool
shortcuts (position/rotation/scale) all respond.

And the predicted consequence landed exactly as expected: **game view then lost A and D too.**
With the names correct, `scene.move-left` / `scene.move-right` match and `GlobalKeyPressed` swallows
A and D just as it already swallowed W and S. A and D were only ever reaching the game because the
name was garbage.

On Windows that swallow is harmless — `toolframework2.dll` uses raw input and SDL's win32 backend
gets its own copy through the window procedure, so the game still sees the key. On Linux the Qt
bridge is the **only** source (`../INPUT-ARCHITECTURE.md` §5.2), so anything the editor swallows is
gone. Which leads to:

## 7b. The editor swallowed the game's keys — **FIXED in source, not yet re-measured**

`ManagedTools.GlobalKeyPressed` returns `true` to consume a key whenever an editor shortcut matches,
and it does so regardless of whether the running game currently owns the keyboard. In game view the
play widget (`SceneRenderingWidget`) holds Qt focus, and it is a descendant of `SceneViewportWidget`
and `SceneViewWidget` — so `Entry.IsAccessible`'s `ContainsFocus` gate is satisfied and every
widget-scoped scene-view shortcut fires and consumes. That is the whole of `W`/`A`/`S`/`D`/`Q`/`E`,
plus the gizmo tools on `W`/`E`/`R` (`[Shortcut( "tools.position-tool", "w", typeof( SceneViewWidget ) )]`,
`PositionEditorTool.cs:165`), plus every window-scoped single-key binding.

### The fix

Gate the dispatch on who owns the keyboard, in the layer that owns the swallow:

- **`GameMode.GameHasKeyboardFocus`** (new, internal) — `_inPlay.IsValid() && _inPlay.IsFocused`.
  Deliberately the *same* focus signal that already gates mouse capture through
  `WidgetFocused`/`OnEditorGameFocusChange`, so keyboard and mouse ownership cannot disagree.
- **`ManagedTools.GlobalKeyPressed`** — on Linux, while that is true, pass the key straight through
  (`return false`) instead of running `EditorShortcuts.Invoke`. Key releases still call
  `EditorShortcuts.Release`, so a key held across the play transition cannot stay latched `IsDown`.
- **`ManagedTools.IsEditorReservedKey`** — the exception list, kept deliberately narrow: **F1–F12**
  and **Escape**. That is not an arbitrary choice; it is the same set the engine itself reserves
  ahead of any input context (`InputRouter.Input.cs:250-261` routes F1–F12 to
  `IToolsDll.OnFunctionKey`), and it is what keeps the documented escape hatches alive — **F5**
  stop-play, **F8** eject, Escape to release capture. Without it this fix would trap you in play
  mode with no keyboard route out.

Linux-gated (`OperatingSystem.IsLinux()`) rather than unconditional. The behaviour is arguably
correct on both platforms — pressing `W` in a running game should not switch the editor to the
position tool — but on Windows the swallow costs nothing, and that path is not testable from this
checkout. Worth raising upstream rather than changing blind.

## 7c. The pointer was never captured — **FIXED in source, not yet re-measured**

With keyboard working, the mouse was not: in game view the cursor stayed **visible**, was **not
confined** to the viewport, and could be panned off it, so a 360° turn was impossible.

### Measured

`SBOX_INPUT_DEBUG` is no longer a no-op — `InputDebug` (new,
`engine/Sandbox.Engine/Systems/Input/InputDebug.cs`) emits the `[routerdbg]` / `[gamemode]` tags
`run-editor-debug.sh` has always grepped for. Everything logs **on change only**, or rate-limited to
1 Hz; §5 is the standing warning about logging from `Frame()`.

One sweeper session, `./run-editor-debug.sh` (no SDL spy), trimmed to the transitions:

```
42.03  [gamemode]  SetPlayWidget winId=92274730 size=1179,736 focused=True
42.06  [routerdbg] wantsCapture=True allowed=True watchdog(armed=True,tripped=False)
                   hasMouseFocus=True sdlRelMode=False cursorVisible=False events=0
42.06  [routerdbg] SetRelativeMouseMode( True ) -> SDL grab
42.16  [routerdbg] ... sdlRelMode=True ... events=0
43.06  [routerdbg] delivered=0/s while captured (allowed=True)
44.06  [routerdbg] ... allowed=False watchdog(armed=True,tripped=True) ...   ← watchdog trips
44.06  [routerdbg] SetRelativeMouseMode( False ) -> SDL grab
44.30  [gamemode]  qt mouse moves=1/s                                        ← Qt can see again
45.07  [routerdbg] delivered=282/s while captured (allowed=False)
45.31  [gamemode]  qt mouse moves=115/s
...
52.99  [routerdbg] ... allowed=False watchdog(armed=True,tripped=True) ...   ← still tripped
02.91  [gamemode]  ClearPlayMode
```

Three findings, two confirming and one **refuting** a standing theory:

1. **The SDL grab starves Qt completely.** Between 42.16 and 44.07, with `sdlRelMode=True`, Qt
   delivered **zero** mouse moves — not one `[gamemode] qt mouse moves` line — and `InputRouter`
   received **zero** events. Both resumed within 250 ms of SDL letting go. §7's mechanism, now
   measured directly from both ends rather than inferred from `SDL_PushEvent` counters.
2. **The watchdog latches permanently.** It tripped once at +2 s and `tripped=True` for the
   remaining 18 s, through a blur and a refocus. `captureWatchdogTripped` clears only when
   `wantsCapture` drops, and `UISystem.TickInput` holds `mouseState=Game` for the whole session, so
   nothing ever cleared it. ⚠️ §7's "fired 9 times in a single play session" does **not** reproduce
   — this is one trip that never re-arms, not an oscillation.
3. ⚠️ **`HasMouseFocus()` is not lying — §2's premise does not hold here.** It read `True` whenever
   the widget was focused and `False` when blurred, tracking correctly throughout. `cursorVisible`
   was `False` for the whole of play, so `MouseCursorVisible` was **not** forcing `AnalogLook` to
   zero. §2 should not be cited for this defect.

Finding 3 leaves the visible cursor needing a different explanation, and there is only one: with
`MouseCursorVisible=False`, `SetCursorType` picks `"none"` and calls
`InputSystem.SetCursorStandard( None )` — which is **SDL-side**. The play widget is a **Qt** window
and Qt sets its own cursor, so the SDL hide never reaches it.

### The fix

Stop asking SDL to grab. Qt owns that window; let Qt hold the pointer and feed the engine directly —
the pattern `OnPlayWidgetMouseMove` already used for cursor position, extended to relative motion.

- **`InputRouter.ManagedMouseCapture`** (new, `Func<bool,bool>`) — installed by `GameMode` while a
  play widget is registered. `Frame()` calls it with whether the game wants the mouse; a `true`
  return means the editor has taken it, and then SDL relative mode is never asked for, the watchdog
  is bypassed (it exists to catch exactly the starvation this avoids), `SetCursorType` is skipped,
  and the `HasMouseFocus()` override at `:106` is skipped. Pure managed state — no `.def` change, so
  no native rebuild.
- **`GameMode.OnManagedMouseCapture`** — engages only while `_inPlay.IsFocused`, so clicking into the
  inspector hands the mouse back. Hides via `_inPlay.Cursor = CursorShape.Blank` — Qt's own cursor,
  the one actually drawn over that window.
- **`GameMode.OnCapturedMouseMove`** — reports `local - lastLocal` to `InputRouter.OnMouseMotion`,
  the *relative* entry point, so a 360° turn is just more events and nothing clamps at the edge.
  Recentres only within `CaptureEdgeMargin` (64 px) of an edge, so ordinary movement is a plain
  difference between two Qt positions with no warp inside it.
- **Not repeating §3/§4.** Those come from reading `QCursor::pos()` and writing `SetNativeCursorPos`
  — different coordinate spaces, so an unlanded warp is re-measured and compounds every frame. Here
  `MouseEvent.LocalPosition`, `Widget.ToScreen()` and `Application.CursorPosition` (`QCursor::setPos`)
  are all Qt logical coordinates: a matched read/write pair. A per-event delta larger than half the
  viewport is dropped, so a warp that does not land costs one ignored event instead of a spin.

No double delivery: while captured, `mouseCapturePosition` is non-null, and
`OnMousePositionChange` returns early on that (`InputRouter.Input.cs:112`), so the bridge's absolute
positions are already swallowed and `OnMouseMotion` is the only feed.

### Watchdog re-armed

Kept — it still guards the SDL path — but the permanent latch is fixed: a trip now expires after
`CaptureRetrySeconds` (10 s) instead of standing for as long as the request does. Finding 2 above is
why.

### Runtime status — works, with one open gap

Verified: the cursor hides, the pointer stays in the viewport, and a full 360° turn works.

**Open: a fast enough flick escapes the viewport.** Slow movement is confined correctly; a quick one
gets out. Not yet investigated, but the mechanism is almost certainly the margin:

`OnCapturedMouseMove` only recentres once the pointer is already within `CaptureEdgeMargin` (64 px)
of an edge, so confinement assumes the pointer cannot travel more than that between two Qt events.
It can. §7c measured Qt delivering **40–190 moves/s** — at the low end of that a 3000 px/s flick
covers ~75 px per event, clearing the margin in one step. And once the pointer is outside the widget
Qt stops sending it `MouseMove` at all (nothing is grabbing it), so the warp that would pull it back
never fires: the miss is self-sustaining, not recovered on the next event.

Three candidate fixes, cheapest first — none measured:

1. **Scale the margin to the observed speed** — `max( 64, k × |delta| )`, so the trigger zone always
   exceeds one event's travel. One line, no new API, but it is still a race with a fast enough flick.
2. **Recentre on every event, not just near the edge.** The pointer then never approaches an edge,
   which removes the race entirely. Costs one warp per event and leans harder on the unlanded-warp
   path — though the half-viewport delta clamp already bounds that, and §3's compounding cannot
   apply here because the read and write are the same coordinate space.
3. **A real confine.** `QWidget::grabMouse` is not in `QWidget.def` and cannot be added (ABI hash),
   but `X11InputRegion.cs` already establishes the pattern for reaching X directly by P/Invoke over
   its own connection — `XGrabPointer` with `confine_to` and `owner_events=True`, or an XFixes
   pointer barrier. ⚠️ An X pointer grab is what starved Qt in the first place (§7c finding 1);
   `owner_events=True` should keep events flowing to Qt, but that needs measuring, not assuming.

(2) is the likely answer, with (1) as the one-line stopgap.

## 7d. Mouse buttons and wheel — **verified working, no defect**

Clicking appeared dead in game view. It is not: **the sweeper sample has nothing that reads a mouse
button.** `CubeController.cs` is the only gameplay component in `minimal.scene` and its entire input
surface is `Input.AnalogLook` and `Input.AnalogMove`; `.sbproj` declares no `InputActions`, so the
project inherits the defaults. §6 had flagged exactly this and it caught us anyway — nothing was
listening, so no amount of working input would have shown.

Measured end to end with temporary instrumentation (since removed), one play session:

```
context button MouseLeft  pressed=True mouseState=Game name='MOUSE1' hasGameButtonHandler=True
action 'attack1' PRESSED from button 'MOUSE1'
context button MouseRight pressed=True mouseState=Game name='MOUSE2' hasGameButtonHandler=True
action 'attack2' PRESSED from button 'MOUSE2'
context button MouseMiddle pressed=True mouseState=Game name='MOUSE3' hasGameButtonHandler=True
button 'MOUSE3' (MouseMiddle) down=True matched NO action in collection 'local.sweeper'
```

So the whole chain works: the bridge delivers the button, `InputSystem.CodeToString` resolves it to
`MOUSE1`/`MOUSE2`, and the action fires. `MOUSE3` matching nothing is **correct** — the default set
binds only mouse1 and mouse2 (`Input.Common.cs:14-30`), nothing to middle-click. The 0.1 s
window-activation swallow (`InputRouter.Input.cs:26-30`) never fired once.

⇒ To test clicks, use a gamemode that actually consumes them. Sweeper cannot show you.

### Two loose ends, neither chased

- **Spurious button releases.** Counted over one session: MouseLeft 29 press / **36** release,
  MouseRight 3 / **4** — releases arriving with no matching press. Harmless as things stand:
  `Input.OnButton` does `if ( !activeButtons.Remove( button ) ) return;` (`Input.Actions.cs:242`), so
  a release for a button that was never pressed is discarded before it reaches an action. Source
  unknown; presumably the bridge.
- **One key press produces ~12 action events.** A single physical `W` logged
  `action 'Forward' PRESSED` **12 times across 35 ms**, then one release ~4 s later when the key was
  let go. Mouse buttons produce exactly one. Not auto-repeat — X autorepeat starts after ~500 ms and
  runs at ~30 Hz, this is ~3 ms apart and immediate. **Unresolved, two candidates, not
  distinguished:** either 12 real key events arrive at `InputRouter.OnKey`, or one event fans out
  over 12 duplicate binds in the `local.sweeper` collection inside
  `collection.EnumerateWithButton( button )`. The measurement that separates them is a counter in
  `OnKey` itself. Severity is probably low — `AccumActionsPressed |= 1UL << i` is idempotent, and
  `Flip()` (`Input.Context.cs:108-127`) latches once per frame — but 35 ms spans ~2 frames at 60 fps,
  so `Pressed` could fire on two consecutive frames rather than one.

### Next step, only if the managed capture is not enough

Force `SDL_VIDEO_X11_EXTERNAL_WINDOW_INPUT` back on so SDL sets up its own input on the wrapped
window. It has to be done from a shim (`patches/sdlhint.c`): the engine's explicit `SDL_SetHint`
beats the environment variable of the same name. Per SDL's source this should not contend with Qt —
SDL does not take core `ButtonPress` while XI2 is up, and XI2 selections are not exclusive between
clients.

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
- `SBOX_INPUT_DEBUG` **works now** — `InputDebug` emits `[routerdbg]` from `InputRouter` and
  `[gamemode]` from the play handover, into `game/logs/sbox-dev.log`. (`[inputdbg]`, the scene
  viewport tag, is still unclaimed.) `./run-editor-debug.sh` no longer preloads `libsdlspy.so` —
  the spy is opt-in behind `SPY=1`, because it interposes `SDL_PushEvent` and
  `SDL_SetWindowRelativeMouseMode`, which is the render-path hazard warned about below. Reach for
  `SPY=1` only for what managed code cannot see: whether the bridge is emitting at all, the actual
  `SDL_SetWindowMouseGrab`, and which SDL window id events carry. Anything logged from
  `InputRouter.Frame()` must be on-change or rate-limited; §5 is what happens otherwise.
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
- **Do not `kill -9` the editor.** It writes scene state on shutdown (`SceneDock.OnDestroyed` →
  `SceneEditorSession.Destroy`, the same path as §8), and a `SIGKILL` landing mid-save **truncates
  the scene file** — `game/samples/sweeper/Assets/scenes/minimal.scene` was reduced from 233 KB to
  `{}` this way, which presents as the project opening completely empty. Use `pkill -x sbox-dev`
  (SIGTERM) or close from the UI, and check `git status` on the project after a crash. Recovery is
  `git checkout -- <scene>` if the file is tracked.
- `.editorconfig` sets `end_of_line = crlf` while `.cs` files are LF in a Linux checkout, so
  `format --verify` fails tree-wide here. Pre-existing, unrelated to input.
