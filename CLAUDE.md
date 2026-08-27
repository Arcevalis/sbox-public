# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this repo is

The public source distribution of **s&box** — Facepunch's game engine: Valve's Source 2 native
core with a .NET 10 managed layer and a Qt-based editor. Upstream targets Windows; this checkout
also carries **Linux port work** (see *Linux* below), which is why `bootstrap.sh`, `run-*.sh`,
`check-linux-deps.sh` and `bootstrap-linux/` exist alongside the upstream `Bootstrap.bat`.

**The native C++ source is not in this repo.** There is no `src/`. `game/bin/win64/` and
`game/bin/linuxsteamrt64/` hold prebuilt, stripped binaries; the build detects a public
distribution (no `public/` or `steamworks/` directory at the root — see
`Steps/Build.cs:IsPublicSourceDistribution`) and downloads artifacts matching the current commit
from `artifacts.sbox.game` instead of compiling native code. Anything requiring a native rebuild
cannot be done here.

## Build / test / format

Everything goes through **`sboxbuild`** (`engine/Tools/SboxBuild`), which resolves all paths from
the current working directory, so **run it from the repo root**:

```bash
dotnet run --project ./engine/Tools/SboxBuild/SboxBuild.csproj -- <command>
```

| Task | Command |
| --- | --- |
| Full bootstrap | `./bootstrap.sh` (Linux) / `Bootstrap.bat` (Windows) |
| Managed build | `sboxbuild build --config Developer` |
| Rebuild from clean | `sboxbuild build --clean` |
| Format | `sboxbuild format` — `--verify` for the CI check |
| Tests | `sboxbuild test --filter "TestCategory!=LiveBackend"` |
| Solutions for an IDE | `sboxbuild generate-solutions` |
| `sboxbuild --help` | lists every step command |

`build` runs InteropGen → (native, skipped here) → managed. `BuildManaged` wipes
`game/bin/managed/` and rebuilds `engine/Sandbox-Engine.slnx` with
`-p:TreatWarningsAsErrors=true`, then publishes the single-file launchers into `game/`
(`sbox`, `sbox-dev`, `sbox-launcher`, `sbox-standalone`, `sbox-server`).

**`build-shaders` and `build-content` are Windows-only** — they shell out to
`game/bin/managed/shadercompiler.exe` and `game/bin/win64/contentbuilder.exe`, which do not
exist on Linux. `bootstrap.sh` warns and continues past both; do not treat that as a failure.

### Tests

MSTest, three tiers run in order by `Steps/Test.cs`, a failing tier stops the run:

- `Sandbox.Test.Unit` — runs with `FACEPUNCH_ENGINE` **removed**; must work with no native
  engine. A test that needs native DLLs belongs in `Sandbox.Test.Engine`.
- `Sandbox.Test.Engine`, `Sandbox.Test.Integration` — run with `FACEPUNCH_ENGINE=<repo>/game`.

A new project under `engine/Tests/` is not picked up until it is added to the `Projects` array in
`engine/Tools/SboxBuild/Steps/Test.cs`.

Single test / class, bypassing sboxbuild (from `engine/`):

```bash
dotnet test Tests/Sandbox.Test.Unit/Sandbox.Test.Unit.csproj -c Release --filter "FullyQualifiedName~MyTestClass"
FACEPUNCH_ENGINE=/path/to/repo/game dotnet test Tests/Sandbox.Test.Engine/Sandbox.Test.Engine.csproj -c Release --filter "Name~MyTest"
```

## Architecture

### The managed/native boundary is generated and fixed

`engine/Definitions/*.def` (listed by `engine/manifest.def`) describe every call crossing between
C# and the native modules. **InteropGen** runs at the start of every build and emits the
`engine/**/Interop.*.cs` files — these are **generated and gitignored; never hand-edit them**.
On the public build native emission is suppressed (`InteropGen( skipNative: isPublicSource )`).

The generated code hands the native module an **ABI hash** and indexes a **fixed-slot function
table** (e.g. `Interop.Engine.cs` → `libengine2`'s `igen_engine`). Adding or reordering a `.def`
entry changes the hash and shifts every later slot, so the shipped binary rejects it. **Editing
`.def` files requires a native rebuild that this repo cannot do.** Plain `[DllImport]` against an
exported C symbol (e.g. `libtier0`) has no hash and does still work.

Alongside InteropGen, `engine/CodeGen.Targets` runs `Tools/CodeGen` before `CoreCompile` on
engine projects, rewriting sources into `obj/.generated/`; `Sandbox.Generator` is a Roslyn source
generator on top of that.

### Managed layering (`engine/Sandbox-Engine.slnx`)

All of these output to `game/bin/managed/`, one shared set of assemblies for every platform.

- **Tier0** — `Sandbox.System`, `.Filesystem`, `.Reflection`, `.Bind`, `.Event`, `.Razor`,
  `.Services`, `.Hotload` (assembly hot-swap), `.Compiling` (Roslyn compilation of user addons).
- **Tier1** — `Sandbox.Engine`: interop, scene/GameObject system, resources, platform layer.
- **Tier2** — `Sandbox.AppSystem` (which app system boots: game / tool / standalone / test),
  `Sandbox.GameInstance`, `Sandbox.Menu`, `Sandbox.Tools`.
- **`Sandbox.Tools`** is the managed Qt binding layer — `Qt/`, widgets, dock manager, graphics
  views — plus editor infrastructure. Editor *content* lives in addons, not here.
- **`engine/Launcher/*`** — the thin exes; each publishes framework-dependent single-file.

### Addons are where the editor and game content live

`game/addons/{base,citizen,menu,tools}` and `game/editor/{Hammer,ShaderGraph,ActionGraph,MovieMaker,DooEditor}`
are `.sbproj` packages of C#/Razor/SCSS compiled by `Sandbox.Compiling` at runtime, not part of
the engine solution. `game/addons/tools` (the "Base Editor Library") is the editor UI;
`game/addons/menu` is the main menu. `game/samples/sweeper` is a sample game project;
`game/templates/` seeds new projects.

`sboxbuild build-addons` regenerates `game/s&box.slnx` via `sbox-dev -generatesolution` and
builds it, then runs `MenuBuild` — it needs `sbox-dev.exe`, so it is a Windows path today.

## Conventions

- Style is enforced by `.editorconfig` and `sboxbuild format --verify` in CI: **tabs**, Allman
  braces, and **spaces inside parentheses** — `void Foo( int x )`, `if ( x )`. Match it.
- Target framework is plain `net10.0` and `game/bin/managed/` serves both platforms, so **there
  is no `WINDOWS` symbol** — express platform differences as `OperatingSystem.IsWindows()` /
  `IsLinux()` runtime checks, never `#if`. See `Sandbox.Engine/Platform/DLLImportResolver.cs`,
  `Sandbox.AppSystem/QtAppSystem.cs`.
- `.gitignore` denies everything by default and re-allows source extensions; a new file type
  needs an explicit `!` rule to be committable.
- `.gitattributes` forces CRLF under `game/**` (content hashes) except `game/**/*.cs`.

## Linux

`bootstrap-linux/README.md` is the entry point for native shim work;
`bootstrap-linux/issues/` holds written-up port bugs with evidence. For input specifically:
`INPUT-ARCHITECTURE.md` (repo root) is how the input paths are built across the three
executables and both platforms, and `bootstrap-linux/linux-input.md` is the known-defect log.

### Upstream Qt and SDL sources (local, outside this repo)

The engine's two windowing/input dependencies are checked out on this machine. Read them for
mechanism questions instead of guessing — Qt and SDL behaviour is the load-bearing half of most
Linux input and window bugs.

| | Path | What it is |
| --- | --- | --- |
| **Qt** | `~/Documents/GitHub/qt` | `Facepunch/qt` — Facepunch's build of Valve's build. The full Qt tree is vendored under `valve/`, so qtbase is `valve/qtbase/`. **Qt 5.15.2** (`valve/qtbase/.qmake.conf:9`), matching the shipped `libQt5*.so.5.15.2`. History is squashed, so "not upstream" means anything after the `baseline` commit. |
| **SDL** | `~/Documents/GitHub/SDL` | Upstream `libsdl-org/SDL`, unmodified. Checkout is **3.5.0** (`include/SDL3/SDL_version.h`); the repo ships **3.4.14** (`libSDL3.so.0.4.14`), so cited line numbers drift — verify behaviour, don't trust line numbers across that gap. |

Most-used subpaths:

```
qt/valve/qtbase/src/plugins/platforms/xcb/   the only platform plugin that ships (libqxcb.so)
qt/valve/qtbase/src/widgets/kernel/          QWidget event delivery, hit-testing
qt/valve/qtbase/src/gui/kernel/              QWindowSystemInterface hand-off

SDL/src/events/                              the queue: SDL_PushEvent / PollEvent / PumpEvents
SDL/src/video/x11/                           x11 backend, incl. foreign-window wrapping
SDL/src/dynapi/SDL_dynapi.sym                what is actually exported — check here before
                                             assuming a symbol is callable from outside SDL
```

**Do not write code into either tree.** They are references. SDL's own `CLAUDE.md` states
outright that AI must not be used to generate code for contributions to that project; reading and
analysis are fine, patches are not.

- **Launch through the scripts, not the binary.** `./run-editor.sh` `LD_PRELOAD`s the engine's
  `libHarfBuzzSharp.so` — without it the system HarfBuzz pulled in by Qt/fontconfig collides on
  the same unversioned `hb_*` symbols and glibc aborts with `free(): invalid pointer`. It also
  sets `LD_LIBRARY_PATH` and `cd`s into `game/` (content paths are relative to the cwd).
- **`sbox-dev` with no `-project` re-execs `sbox-launcher` as a separate process** and returns.
  The editor you end up looking at is then not the one you launched and receives none of your
  environment. Always pass `-project <.sbproj>` when debugging.
- `./run-editor-debug.sh` adds `SBOX_INPUT_DEBUG=1` and the `libsdlspy.so` SDL shim;
  `./run-sbox-gdb.sh` / `./run-sweeper-gdb.sh` run under gdb with .NET-safe signal handling and
  auto-backtrace on the Vulkan present stall; `./check-linux-deps.sh` reports missing shared
  libraries and symbol-version problems in `game/bin/linuxsteamrt64/`.
- **Qt5, not Qt6.** `game/bin/linuxsteamrt64/` ships Qt 5.15 and the managed Qt enums were
  authored against it (`Editor.FileDialog.Option` is correct under Qt5, wrong under Qt6).
- **`CDirWatcher` is not implemented on Linux** — the editor does not hot-reload addon source.
  Restart it after editing anything under `game/addons/`.
- Linux has no `game/_redist` equivalent, so `BuildManaged` stages the SDK's shared framework
  into **`game/dotnet/`** and the apphosts resolve against it (`AppHostRelativeDotNet`).
  A missing `game/dotnet` means the launchers fall through to whatever is in `/usr/share/dotnet`.
- **Fix a defect in the layer that owns it.** Native shims (`bootstrap-linux/patches/`, built
  from C and `LD_PRELOAD`ed, since the shipped `.so`s are stripped and unbuildable) for problems
  below the interop boundary; `engine/` or `game/addons/` for editor logic that Linux merely
  exposes. Managed code must never reference the native port work.
