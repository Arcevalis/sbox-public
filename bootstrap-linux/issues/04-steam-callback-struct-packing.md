# Linux: Steam callback structs are packed for Windows, `Dispatch` marshals garbage pointers

## Environment

| | |
| --- | --- |
| Repo commit | `70647f994acb16cf780654dcfe3b5fee738a9f15` |
| Binary | `game/sbox` (game client), under `launch/run-sbox-gdb.sh` |
| .NET | 10.0.111 |
| Run | 2026-08-23 22:36:39 → 22:38:20 |
| Engine log | `game/logs/sbox-2026-08-23.2.log` |
| gdb log | `bootstrap-linux/logs/present-trace-sbox-20260823-223639.log` |

## Summary

`Steamworks.Dispatch` marshals every incoming Steam callback with
`Marshal.PtrToStructure` against a struct whose `Pack` is hardcoded to **8**. The Steamworks SDK
packs callback structs to **4** under GCC (Linux/macOS) and 8 only under MSVC. Every field after
the first therefore reads from the wrong offset. For the `HTML_*` callbacks — whose fields are
`const char *` — the wrong offset yields a garbage pointer, and marshalling it throws
`NullReferenceException` from inside the CSTR marshaller.

Two such throws in this run, both while the MOTD web surface was loading. The process took
`SIGABRT` seconds after the second one.

## The gdb log has no backtrace — here is why

`bootstrap-linux/logs/present-trace-sbox-20260823-223639.log` contains no stack. In full:

```
[present-trace] armed on Warning -> breakpoint 1, 1 location(s)

Thread 1 "sbox" received signal SIGABRT, Aborted.
__pthread_kill_implementation (threadid=<optimized out>, signo=6, no_tid=0) at ./nptl/pthread_kill.c:44
⚠️ warning: 44	./nptl/pthread_kill.c: No such file or directory

[present-trace] 0 matching message(s), 0 backtrace dump(s)
```

`present-trace.py` only dumps threads from a breakpoint on tier0 `Warning()` filtered to the
present-stall message set. It has no `gdb.events.stop` handler, so an abort halts the session at
the prompt and dumps nothing; the driver's trailing `quit` then ended the run. The abort site is
**unrecorded** — the crash is timestamp-correlated with what follows, not proven by a stack.

The usable trace for this issue is the managed one in `game/logs/sbox-2026-08-23.2.log`.

## Trace

`game/logs/sbox-2026-08-23.2.log:5559` — 22:37:59.1891, and again identically at 22:38:19.2165 (`:5572`):

```
2026/08/23 22:37:59.1891  [Generic] Object reference not set to an instance of an object.
System.NullReferenceException: Object reference not set to an instance of an object.
   at System.SpanHelpers.IndexOfNullByte(Byte* searchSpace)
   at System.String.Ctor(SByte* value)
   at System.StubHelpers.CSTRMarshaler.ConvertToManaged(IntPtr cstr)
   at System.Runtime.InteropServices.Marshal.PtrToStructureHelper(IntPtr ptr, Object structure, Boolean allowValueClasses)
   at System.Runtime.InteropServices.Marshal.PtrToStructure(IntPtr ptr, Type structureType)
   at Steamworks.Utility.ToType[T](IntPtr ptr)               Platform/Steam/Utility/Utility.cs:14
   at Steamworks.Dispatch.<>c__DisplayClass10_0`1.<Install>b__0(IntPtr x)
                                                             Platform/Steam/Classes/Dispatch.cs:182
   at Steamworks.Dispatch.ProcessCallback(CallbackType type, IntPtr data, Int32 dataSize, Boolean isServer)
                                                             Platform/Steam/Classes/Dispatch.cs:61
   at Steamworks.Dispatch.OnClientCallback(Int32 type, IntPtr data, Int32 dataSize, Boolean isServer)
                                                             Platform/Steam/Classes/Dispatch.cs:21
```

Read bottom-up, that is: Steam delivers a callback → `ProcessCallback` hands the raw `data`
pointer to the installed action → the action is `Dispatch.Install<T>`'s closure
`x => p( x.ToType<T>() )` → `Marshal.PtrToStructure` walks the struct's fields → a `string` field
is read as `char *` and handed to `CSTRMarshaler`, which scans for a NUL byte from an address
that is not a string.

The exception is caught and logged by `OnClientCallback`'s `try/catch` (`Dispatch.cs:23`), so the
callback is silently dropped rather than fatal on its own.

### Surrounding context — both throws land on browser activity

```
22:37:59.0082  [ConnectingMenuWebMOTD] mounting browser after local layout containerSize=1511x818 url="…/FrostyServer.html…"
22:37:59.1174  [ConnectingMenuWebMOTD] browser surface ready containerSize=1511x818 surfaceSize=1512x818 limited=True
22:37:59.1891  ← NullReferenceException (above)
22:37:59.6111  [ConnectingMenuMOTD] native fetch failed … reason="The page did not contain the supported Legacy Rusted MOTD structure."
22:38:00.0004  [ConnectingMenuPresentation] panel layout … motdMode=web-live motdReady=False motdLoading=False
22:38:19.2165  ← NullReferenceException (identical stack)
[gdb]          Thread 1 "sbox" received signal SIGABRT
```

`WebSurface.Init()` (`engine/Sandbox.Engine/Utility/WebSurface.cs:39-44`) installs exactly six
callbacks, four of which carry `const char *` fields:

```csharp
Dispatch.Install<HTML_NeedsPaint_t>     ( … );   // no strings
Dispatch.Install<HTML_StartRequest_t>   ( … );   // PchURL, PchTarget, PchPostData
Dispatch.Install<HTML_URLChanged_t>     ( … );   // PchURL, PchPostData, PchPageTitle
Dispatch.Install<HTML_ChangedTitle_t>   ( … );   // PchTitle
Dispatch.Install<HTML_FinishedRequest_t>( … );   // PchURL, PchPageTitle
Dispatch.Install<HTML_SetCursor_t>      ( … );   // no strings
```

These are the only string-bearing callbacks installed anywhere in the engine — the Steam friends
and networking callbacks (`SteamFriends.cs:27-30`,
`GameNetworkSystem.Static.cs:244-248`) are all integer/handle payloads, which is why nothing threw
before the browser mounted.

## Cause

`engine/Sandbox.Engine/Platform/Steam/Utility/Platform.cs:13`:

```csharp
internal const int StructPlatformPackSize = 8;   // no platform switch
```

Used by 192 struct declarations in `Platform/Steam/`, including every `HTML_*` callback.

The Steamworks SDK selects packing by compiler: `VALVE_CALLBACK_PACK_SMALL` (pack 4) under GCC,
`VALVE_CALLBACK_PACK_LARGE` (pack 8) under MSVC. On Linux the native side lays these structs out
at pack 4; the managed side reads them at pack 8.

Measured layouts (`Marshal.OffsetOf`, x64):

| Struct | Pack 8 (what we use) | Pack 4 (what Linux Steam sends) |
| --- | --- | --- |
| `HTML_StartRequest_t` | size 40 — `H@0 U@8 T@16 P@24 R@32` | size 32 — `H@0 U@4 T@12 P@20 R@28` |
| `HTML_URLChanged_t` | size 48 — `H@0 U@8 P@16 R@24 T@32 N@40` | size 36 — `H@0 U@4 P@12 R@20 T@24 N@32` |
| `HTML_ChangedTitle_t` | size 16 — `H@0 T@8` | size 12 — `H@0 T@4` |

The browser handle at offset 0 survives; every pointer after it is read 4 bytes late — half of one
pointer glued to half of the next. That is the address `IndexOfNullByte` is scanning.

This also means the callbacks that *don't* throw are not therefore correct: `HTML_NeedsPaint_t`
has no strings, so its misread fields (texture pointer, width, height, scroll offsets) are
consumed silently.

## Cheap confirmation

`ProcessCallback` receives `dataSize` from Steam and never compares it to the struct it is about
to marshal (`Dispatch.cs:38-66`); each generated struct already carries `_datasize =
Marshal.SizeOf( … )`. Logging the two side by side at the top of `ProcessCallback` should print
32 vs 40 for `HTML_StartRequest`, 36 vs 48 for `HTML_URLChanged`, and equal values on Windows.
That distinguishes a packing mismatch from a stale/freed `data` pointer without a debugger.

For a native stack on the abort itself, `present-trace.py` needs a `gdb.events.stop` handler
that dumps on fatal signals — or, minimally, run with `SBOX_GDB_STOP_SEGV=1` and type `thread
apply all bt full` at the prompt before quitting, which is what this log was missing.

## Fixing it is not a one-line `#if`

Upstream Facepunch.Steamworks switches this constant with `#if PLATFORM_WIN`. That does not
transfer here: `Pack` must be a compile-time constant, `TargetFramework` is plain `net10.0`, and
`game/bin/managed/` is **one** set of assemblies serving both Windows and Linux (see `CLAUDE.md`),
so there is no build in which a `#if` or an `OperatingSystem.IsLinux()` test can pick the layout.
The options, in increasing order of work:

1. **Per-platform struct pairs.** Have the generator emit a pack-4 twin for the affected callback
   structs and select between them at runtime in `Utility.ToType<T>` / `Dispatch.Install<T>`.
   Mechanical, but doubles the generated surface.
2. **Hand-marshal the callback payloads.** Read fields by offset (`Marshal.ReadInt32`,
   `Marshal.ReadIntPtr`, `Marshal.PtrToStringUTF8`) from a per-platform offset table instead of
   `PtrToStructure`. Confines the platform knowledge to one place; loses the declarative structs.
3. **Split the managed output per platform**, which is a build-layout change well beyond this bug.

Whichever is chosen, `Platform.StructPackSize = 4` (the SDK's always-small structs, 23 uses) is
already correct and should not be touched.

## Severity

Any Steam callback with a pointer or 64-bit field is misread on Linux. Observed effect so far is
confined to the in-game web surface — dropped `HTML_*` events, browser callbacks that never fire —
plus an unattributed `SIGABRT` in the same window. Reading arbitrary addresses as strings is not a
bug that stays cosmetic.
