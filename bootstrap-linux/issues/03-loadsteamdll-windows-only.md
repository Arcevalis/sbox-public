# Linux: `AppSystem.LoadSteamDll()` is Windows-only, so the game skips the Steam DLL pinning the editor does

## Environment

| | |
| --- | --- |
| Repo commit | `70647f994acb16cf780654dcfe3b5fee738a9f15` |
| Affects | `game/sbox` (game client). `game/sbox-dev` (editor) is unaffected. |

## Summary

The cross-platform fix for loading the Steam API library was applied to `QtAppSystem` (used by
the editor) but not to `AppSystem` (used by the game). On Linux the game's `LoadSteamDll()`
early-returns and does nothing, so the deliberate "pin our own copy" protection described in its
own doc comment silently does not happen.

## The two implementations

`engine/Sandbox.AppSystem/AppSystem.cs:378` — used by the **game** via `GameAppSystem`:

```csharp
/// Explicitly load the Steam Api dll from our bin folder, so that it doesn't accidentally
/// load one from c:\system32\ or something. …
protected void LoadSteamDll()
{
    if ( !OperatingSystem.IsWindows() )
        return;

    var dllName = $"{Environment.CurrentDirectory}\\bin\\win64\\steam_api64.dll";
    if ( !NativeLibrary.TryLoad( dllName, out steamApiDll ) )
    {
        throw new System.Exception( "Couldn't load bin/win64/steam_api64.dll" );
    }
}
```

`engine/Sandbox.AppSystem/QtAppSystem.cs:93` — used by the **editor**:

```csharp
protected void LoadSteamDll()
{
    // Windows ships this as steam_api64, everywhere else it's plain steam_api.
    // GetNativeLibraryName only sorts out the lib prefix and the extension, not the name.
    var baseName = OperatingSystem.IsWindows() ? "steam_api64" : "steam_api";
    var fileName = Sandbox.Interop.GetNativeLibraryName( baseName );
    var dllName = System.IO.Path.Combine( AppContext.BaseDirectory, NetCore.NativeDllPath, fileName );

    if ( !NativeLibrary.TryLoad( dllName, out steamApiDll ) )
    {
        throw new System.Exception( $"Couldn't load {dllName}" );
    }
}
```

The second is correct on all platforms. The first carries two Windows-only assumptions — the
`steam_api64` name and the hardcoded `\\bin\\win64\\` path with backslash separators — and papers
over them with an early return.

## Impact

Low, but real and asymmetric:

- The game does not pre-pin `libsteam_api.so` from `bin/linuxsteamrt64/`, so the process is free
  to bind whichever copy the loader finds first. That is exactly what the doc comment says the
  call exists to prevent.
- Game and editor take different startup paths on the same platform, which makes
  Steam-related behaviour differ between them for no intended reason.
- Nothing currently fails because of this — the managed P/Invokes resolve through
  `DLLImportResolver` once it is installed (see issue 02).

## Suggested fix

Converge both on the `QtAppSystem` implementation — ideally by hoisting it to the shared base and
deleting the duplicate, so a future platform fix cannot be applied to only one of them again.

## Related

Found while diagnosing issue 02. Not its cause; the two are independent.
