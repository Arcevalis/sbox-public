# Issue drafts — Linux

Four independent issues, written to be pasted into GitHub as-is. All from repo commit
`70647f994acb16cf780654dcfe3b5fee738a9f15`, binary `game/sbox`. 01–03 come from the
2026-08-22 present-stall investigation, 04 from the 2026-08-23 Steam dispatch crash.

| # | File | Issue | Severity | Cause known? |
| --- | --- | --- | --- | --- |
| 01 | [01-vulkan-present-stall.md](01-vulkan-present-stall.md) | `QueuePresentAndWait()` gives up waiting for a present; frames dropped | Stutter, recoverable | **No** — location pinpointed, root cause open |
| 02 | [02-steam-api64-prejit-race.md](02-steam-api64-prejit-race.md) | PreJIT races `DllImportResolver`, spurious `steam_api64` DllNotFoundException | Cosmetic (log noise) | Yes |
| 03 | [03-loadsteamdll-windows-only.md](03-loadsteamdll-windows-only.md) | `AppSystem.LoadSteamDll()` is Windows-only; game skips DLL pinning the editor does | Low, latent | Yes |
| 04 | [04-steam-callback-struct-packing.md](04-steam-callback-struct-packing.md) | Steam callback structs packed 8 (MSVC) not 4 (GCC); `Dispatch` marshals garbage pointers, `HTML_*` callbacks throw | Dropped callbacks, unsafe reads | Yes |

02 and 03 both touch Steam startup but are independent — 03 is not the cause of 02.

## Evidence

- **Trace:** `bootstrap-linux/logs/present-trace-sbox-20260822-020633.log` — 36 warnings, 20 with
  full all-thread backtraces (the remaining 16 hit the dump cap).
- **Grouped:** `bootstrap-linux/logs/split/present-trace-sbox-20260822-020633/` — 2 signatures,
  one real (19 occurrences).
- **Engine log:** `game/logs/sbox-2026-08-23.2.log`.
- **04:** managed stack traces in `game/logs/sbox-2026-08-23.2.log:5559` and `:5572`. Its gdb log,
  `bootstrap-linux/logs/present-trace-sbox-20260823-223639.log`, caught the `SIGABRT` but dumped
  no stack — `present-trace.py` only dumps from the `Warning()` breakpoint, not on fatal signals.

## Tooling used

| Tool | Purpose |
| --- | --- |
| `run-sbox-gdb.sh` | Launches `game/sbox` under gdb with .NET-safe signal handling |
| `bootstrap-linux/gdb/present-trace.py` | Breaks on tier0 `Warning`, filters by message, dumps all threads, auto-continues |
| `bootstrap-linux/gdb/split-trace.py` | Groups occurrences by stack signature into one file per distinct stall |

## Symbol situation

The shipped natives are stripped — no `.symtab`, no `.debug_info`, only `.dynsym`. They do carry
`.gnu_debuglink` pointing at `librendersystemvulkan.so.dbg`, `libengine2.so.dbg`,
`libtier0.so.dbg`, so those files exist on the build machine, but:

- none are present anywhere on this system, and
- the artifact manifest for this commit lists **11,889 files and zero debug artifacts**
  (no `.dbg`, `.debug`, or `.pdb`).

So stacks are reported as `module+0xoffset` against build-id
`18f7724355084fc7e74d164a25a419a9b2e279ed`, which resolves exactly against those `.dbg` files for
anyone who has them. Publishing them would make every future Linux trace directly readable.
