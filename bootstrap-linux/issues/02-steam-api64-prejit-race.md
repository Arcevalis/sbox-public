# Linux: PreJIT races the DllImportResolver, spurious `steam_api64` DllNotFoundException at startup

## Environment

| | |
| --- | --- |
| Repo commit | `70647f994acb16cf780654dcfe3b5fee738a9f15` |
| Binary | `game/sbox` (game client) |
| .NET | 10.0.11 |

## Summary

On Linux the game logs a `DllNotFoundException` for `steam_api64` during startup. It is
**cosmetic** — Steam works fine for the rest of the session — but it is noise in every log and
misleads anyone diagnosing a Steam problem.

Cause is a startup ordering race: the background PreJIT tasks begin *before*
`DLLImportResolver.SetupResolvers()` installs the resolver that knows `steam_api64` must become
`libsteam_api` on non-Windows.

## Log

```
2026/08/22 02:06:34.3880  [PreJit] Unable to load shared library 'steam_api64' or one of its dependencies.
  /…/game/steam_api64.so: cannot open shared object file: No such file or directory
  /usr/lib/dotnet/shared/Microsoft.NETCore.App/10.0.11/steam_api64.so: …
  /…/game/bin/managed/steam_api64.so: …
  /…/game/libsteam_api64.so: …
  /…/game/steam_api64: …
  /…/game/libsteam_api64: …
   - Steamworks.ISteamFriends.IntPtr SteamAPI_SteamFriends_v018()
```

**Every probed path is a `steam_api64` spelling. Not one attempt at `libsteam_api`** — which is
the only name `ResolveFromNativePath` can produce. That is the proof the resolver had not been
installed yet; .NET fell through to default probing.

## Ordering

`GameAppSystem.Init()` — `engine/Launcher/Sbox/Launcher.cs:18`:

```csharp
LoadSteamDll();          // no-op on Linux (see issue 03)
TestSystemRequirements();
base.Init();
CreateGame();            // AppSystem.cs:77  → GameInstanceDll.Create()
CreateMenu();
InitGame( createInfo );  // AppSystem.cs:317
```

- `CreateGame()` → `GameInstanceDll.Create()` (`GameInstanceDll.cs:924`) fires three background
  PreJIT tasks, one over `typeof( Bootstrap ).Assembly` — that is `Sandbox.Engine`, which
  contains the Steamworks P/Invokes.
- `InitGame()` → `AppSystem.cs:351` → `Bootstrap.PreInit()` → `DLLImportResolver.SetupResolvers()`
  — **two calls later**.

`PreJITAsync` calls `RuntimeHelpers.PrepareMethod` on every method, which forces P/Invoke
resolution. The background thread reaches
`Steamworks.ISteamFriends.SteamAPI_SteamFriends_v018()` before the resolver exists.

## Relevant code

`engine/Sandbox.Engine/Platform/Steam/Utility/Platform.cs:14`

```csharp
internal const string LibraryName = "steam_api64";
```

`engine/Sandbox.Engine/Platform/DLLImportResolver.cs:37`

```csharp
if ( libraryName == "steam_api64" && !OperatingSystem.IsWindows() )
{
    libraryName = "libsteam_api";
}
```

## Why Linux only

Windows default probing looks for `steam_api64.dll` — the correct name there — and finds it in
the app directory, so the race is invisible. On Linux the correct name is `libsteam_api.so`,
which only the resolver knows, so losing the race means failing.

## Why it is harmless

`PreJITAsync` catches per-method and logs, then continues (`Sandbox.Reflection/Utility.cs:279`).
Once `PreInit` installs the resolver, real calls resolve normally — `SteamAPI_Init` had already
succeeded 0.24 s *earlier*, and Steam functioned for the whole session.

## Verification

The resolver's target is genuinely loadable, so nothing else is wrong with the path:

```
$ cd game && python3 -c "import ctypes; ctypes.CDLL('bin/linuxsteamrt64/libsteam_api.so')"
  LOADED ok      # with and without LD_LIBRARY_PATH
```

`Steamworks.ISteamFriends` is in `Sandbox.Engine` — the same assembly as `DLLImportResolver` and
`Bootstrap` — so assembly-registration coverage is not the problem; only timing is.

## Suggested fix

Move `DLLImportResolver.SetupResolvers()` ahead of `CreateGame()`. It only walks loaded
assemblies and hooks `AssemblyLoad`, and its one dependency — `NetCore.NativeDllPath` — is set
much earlier by `LauncherEnvironment.Init()` (`Startup.cs:15`). Calling it from
`AppSystem.Init()` closes the window for every launcher at once. `Bootstrap.PreInit` can keep its
existing call; `TryRegister` already dedupes on `assembly.FullName`.
