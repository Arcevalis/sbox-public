# bootstrap-linux

Native-side work for the Linux port. Everything here is C; managed fixes live in `engine/`
and `game/addons/` instead.

## Why this exists

The shipped Linux natives — `libtier0.so`, `libengine2.so`, `libtoolframework2.so` and the rest
of `game/bin/linuxsteamrt64/` — are **binary-only and stripped**. There is no `src/` in the
public distribution, so they cannot be rebuilt. Anything that needs changing below the managed
layer is therefore written as a **shim**: a small `.so` that interposes an exported symbol,
forwards to the real implementation via `dlsym( RTLD_NEXT, … )`, and observes or rewrites what
crosses that boundary.

Two constraints define what a *managed* fix can reach, both discovered the hard way:

- **The interop boundary cannot be extended.** `Interop.Engine.cs` hands `libengine2`'s
  `igen_engine` an ABI hash (`nativeInit( 4531, … )`) and indexes a function table by fixed slot
  (up to 2941). Adding a `.def` entry changes the hash *and* shifts every slot after it, so the
  shipped binary rejects it. Editing `.def` files needs a native rebuild we cannot do here.
- **Exported C symbols are still reachable.** A `[DllImport( "libtier0" )]` resolves through the
  library's `DT_NEEDED` chain with no hash involved, which is what makes a shim viable at all —
  and it also gives managed code a way out when the interop table has no entry for what it needs.
  `engine/Sandbox.Tools/Qt/X11InputRegion.cs` is the first use of that in this tree: `QWidget.def`
  exposes no region API and none can be added, so it P/Invokes `XShapeCombineRectangles` on
  `libXext.so.6` directly. Everything else still goes through the generated interop tables.

  Watch the name mapping: `DLLImportResolver.cs:41-47` maps Linux `{name}.so` with **no `lib`
  prefix**, while `Interop.cs:219-223` maps `lib{name}.so`. `[DllImport( "SDL3" )]` will not find
  `libSDL3.so`. Use a full soname, or `NativeLibrary.TryLoad` to bypass the resolver.

## Layout

```
patches/              C shims and probes, built on demand
  sdlspy.c            LD_PRELOAD shim over SDL's event plumbing and window wrapping
  sdlhint.c           LD_PRELOAD shim over SDL_SetHint - logs the hints the engine sets, and can
                      override SDL_VIDEO_X11_EXTERNAL_WINDOW_INPUT (see linux-input.md §7)
  probe.c             dumps the X11 window tree with each window's ShapeInput region
  drive.c             XTest input driver + X11 pointer-grab state reporter

launch/               the run-*.sh entry points; everything is launched through these
  run-editor.sh       the base - HarfBuzz preload, LD_LIBRARY_PATH, cd into game/
  run-editor-debug.sh run-editor.sh plus SBOX_INPUT_DEBUG, and SPY=1 for the SDL shim
  run-sbox-gdb.sh     game/sbox under gdb, .NET-safe signals
  run-sweeper-gdb.sh  same, pointed at a project instead of the launcher menu

gdb/                  gdb drivers for native traces
  present-trace.gdb   signal disposition + driver, sourced by launch/run-sbox-gdb.sh
  present-trace.py    auto-backtrace on the Vulkan present stall

issues/               issue drafts, written to be pasted into GitHub as-is
logs/                 captured gdb traces; split/ holds them grouped by stack signature

linux-input.md        known editor-input defects on Linux, with their measurements
```

The architecture these sit under — the three executables, the Qt→SDL bridge, why it is
Linux-only — is [`../INPUT-ARCHITECTURE.md`](../INPUT-ARCHITECTURE.md). For a dependency check on
the shipped natives, run `../bootstrap.sh`: it reports every binary in `game/bin/linuxsteamrt64`
as OK or FAIL, lists what is missing, and prompts before building. That measures the current
binaries rather than repeating a checked-in symbol list that goes stale. `--skip-deps` skips it.

`libsdlspy.so` is built by `SPY=1 launch/run-editor-debug.sh`; the others are one-liners:

```bash
gcc -D_GNU_SOURCE -O1 -o probe probe.c -lXext -lX11
gcc            -O1 -o drive drive.c -lXtst -lX11
gcc -shared -fPIC -O1 -o libsdlspy.so sdlspy.c -ldl
gcc -shared -fPIC -O1 -o libsdlhint.so sdlhint.c -ldl
```

## How a shim is written

```c
int SDL_PushEvent( void *e )
{
    static int (*real)( void * );
    if ( !real ) real = dlsym( RTLD_NEXT, "SDL_PushEvent" );

    /* observe or rewrite e here */

    return real ? real( e ) : 0;
}
```

Load it ahead of the engine with `LD_PRELOAD`. `launch/run-editor-debug.sh` does this while
preserving the HarfBuzz preload that `launch/run-editor.sh` needs — order matters, HarfBuzz stays
first.

Symbol versioning is not an obstacle: SDL exports `SDL_PushEvent@@SDL3_0.0.0`, and an
unversioned definition in a preloaded object still interposes it.

### Keep shims off the render path

Learned the hard way. A shim that wrapped `SDL_SetWindowRelativeMouseMode` and `SDL_PushEvent`
started producing

```
[engine/RenderSystem] The selected graphics queue does not support presenting a swapchain image
ERROR: Wayland display connection closed by server (fatal)
```

in exactly the runs where it was loaded, and the failures disappeared the moment it was removed.
Neither call has anything to do with presentation, so the mechanism was never established — but
the correlation was clean and that is enough reason not to do it.

Two rules follow:

- Prefer interposing **configuration** (`SDL_SetHint`) or doing **pure observation** with no change
  to arguments or return values. `sdlspy.c` is the model: forward everything untouched, count on
  the side.
- **Always re-verify against a no-shim baseline** before believing any result, good or bad. The
  swapchain failure looked like a real engine bug until a clean run showed it wasn't.

## Diagnosis

gdb traces are the usual tool, but not the only one. What actually paid off on the input work:

| Tool | Answers |
| --- | --- |
| `readelf --dyn-syms -W … \| c++filt` | what a stripped binary really exports/imports |
| `LD_PRELOAD` counter shim | whether a call happens at all, and how often |
| `XShapeGetRectangles` | whether a window blocks or passes clicks |
| `XQueryPointer` / `XGrabPointer` | pointer state, and whether something holds an exclusive grab |
| `xwininfo` / `probe` | the window tree, geometry, and stacking |

The point is to *measure* the native side rather than infer it. Several plausible-sounding
theories about this port died on contact with a measurement.

Two things make gdb awkward here, both handled in `gdb/present-trace.gdb`. The .NET runtime
uses realtime signals for thread suspension (`SIGRTMIN`/`SIGRTMIN+1`, reported as SIG34/SIG35)
and takes `SIGSEGV` for its own hardware-exception handling — at gdb's defaults the session
stops thousands of times before the engine finishes loading. And a backtrace taken from a
Python `Breakpoint.stop()` override fails with "Selected thread is running", because gdb has
not finished stopping the other threads yet; breakpoint *command lists* run after everything is
stopped, so triggers belong there.

## Running the editor with diagnostics

```bash
bootstrap-linux/launch/run-editor-debug.sh              # sweeper, managed diagnostics
bootstrap-linux/launch/run-editor-debug.sh --dry-run    # print the setup, launch nothing
SPY=1 bootstrap-linux/launch/run-editor-debug.sh        # also preload the SDL spy - see below
```

`SBOX_INPUT_DEBUG=1` is always set, giving `[routerdbg]` and `[gamemode]` lines in
`game/logs/sbox-dev.log`. The `libsdlspy.so` preload is **opt-in**: it interposes
`SDL_PushEvent` and `SDL_SetWindowRelativeMouseMode`, and interposing on the render path has
produced a Vulkan present failure and a fatal Wayland disconnect (`linux-input.md` gotchas). Use it
only for what managed code structurally cannot see — whether the Qt→SDL bridge is emitting at all,
the actual `SDL_SetWindowMouseGrab`, and which SDL window id events are stamped with — and re-verify
any finding against a no-spy run.

It always launches via `-project`, because `sbox-dev` without one re-execs `sbox-launcher` as a
**separate process** and returns — the editor you end up looking at is not the one you launched,
and no environment variables reach it.

Note that `CDirWatcher` is not implemented on Linux, so the editor does not hot-reload addon
source. Restart it after editing anything under `game/addons/`.

See [`linux-input.md`](linux-input.md) for the known input defects and their measurements, and
[`../INPUT-ARCHITECTURE.md`](../INPUT-ARCHITECTURE.md) for how the input paths are built.
