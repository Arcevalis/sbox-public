# Linux: `QueuePresentAndWait()` gives up waiting for a present, frames dropped

## Environment

| | |
| --- | --- |
| Repo commit | `70647f994acb16cf780654dcfe3b5fee738a9f15` |
| Binary | `game/sbox` (game client, not the editor) |
| GPU driver | NVIDIA proprietary `580.173.02` |
| Vulkan layers | Steam overlay (`steamoverlayvulkanlayer.so`) active in the present path |
| .NET | 10.0.11 |
| Debugger | gdb 17.1 |

## Summary

The Vulkan swapchain repeatedly fails to retire a queued present. `CSwapChainBase::QueuePresentAndWait()`
exhausts its wait budget and returns false, which makes `Present()` report the frame as not
presented and the client output for that frame is skipped.

Backtraces show `VKRenderThread` blocked *inside* `vkQueuePresentKHR`, waiting on a DRM syncobj
timeline that does not signal. The present is submitted; it is not coming back.

**This report establishes where the engine blocks, not why the driver does not signal.** Root
cause is undetermined.

## Engine log

```
2026/08/22 02:07:46.9699  [engine/Engine] CSwapChainBase::QueuePresentAndWait() looped for 21 iterations without a present event.
2026/08/22 02:07:47.7771  [engine/Engine] CSwapChainBase::QueuePresentAndWait() looped for 21 iterations without a present event.
...
```

36 occurrences across two windows: `02:07:46.97 → 02:07:56.81` and `02:13:32 → 02:13:56`.
Always **exactly 21 iterations**. The game continued running normally between and after them.

## What 21 iterations means

From the shipped `librendersystemvulkan.so` (stripped; reconstructed by disassembly):

- `CSwapChainBase` ctor at `+0xba490` sets max-outstanding-presents (`this+0x164`) to **1**.
- `QueuePresentAndWait` at `+0x122870` returns immediately if `maxOutstanding > queued - completed`.
- Otherwise it waits on the device's present-completed `CThreadEvent` in **10 ms** slices, at most
  `outstanding + 20` times.
- On exhaustion it emits the warning and returns **false**.

So `21 = outstanding(1) + 20`: one un-retired present, and **~210 ms** of waiting per occurrence.
The sibling "Hitch alert" message never fired, which is consistent — that needs > 0.5 s.

## Where it blocks

`VKRenderThread`, **byte-identical across all 19 captured dumps**:

```
#0  __GI___ioctl (fd=235, request=3224397002)
#1  drmIoctl ()                       from libdrm.so.2
#2  drmSyncobjTimelineWait ()         from libdrm.so.2
#3  ?? ()                             from libnvidia-glcore.so.580.173.02
#4  ?? ()                             from libGLX_nvidia.so.0
#5  ?? ()                             from libGLX_nvidia.so.0
#6  ?? ()                             from libnvidia-glcore.so.580.173.02
#7  ?? ()                             from libnvidia-glcore.so.580.173.02
#8  ?? ()                             from libnvidia-glcore.so.580.173.02
#9  vkQueuePresentKHR ()              from steamoverlayvulkanlayer.so
#10 ?? ()                             from librendersystemvulkan.so
...
#15 ?? ()                             from libtier0.so
#16 start_thread ()
#17 __GI___clone3 ()
```

`request=3224397002` = `0xc03064ca` → dir RW, type `'d'` (DRM), nr `0xCA`, size 48 =
**`DRM_IOCTL_SYNCOBJ_TIMELINE_WAIT`**.

`libGLX_nvidia.so.0` here is NVIDIA's Vulkan ICD, not a GL fallback — expected, not a red flag.

## Reporting thread

The thread that emits the warning, as module-relative offsets against
`librendersystemvulkan.so` (build-id `18f7724355084fc7e74d164a25a419a9b2e279ed`):

```
#0  Warning ()                       libtier0.so
#1  +0x12297a                        librendersystemvulkan.so  — QueuePresentAndWait, give-up branch
#2  +0x11c49b                        librendersystemvulkan.so  — Present(), taking the return value
#3  +0x1224b5                        librendersystemvulkan.so  — Present()'s caller
#4  +0xd786b                         libengine2.so
```

These offsets resolve directly against the `.dbg` files the build produced (`.gnu_debuglink`
names them: `librendersystemvulkan.so.dbg`, `libengine2.so.dbg`, `libtier0.so.dbg`). They are not
shipped and are absent from the artifact manifest.

## What is ruled out

- **No other thread is doing anything.** Of ~150 threads, only `VKRenderThread` is non-idle;
  everything else is parked in futex/poll/nanosleep. No lock contention, no managed-side
  involvement, nothing in `vkAcquireNextImageKHR`.
- **No swapchain recreation.** Zero occurrences in the engine log of `out-of-date`,
  `suboptimal`, `surface lost`, `Hitch alert`, or `main thread is more than %d frames ahead`.
  The surface never went stale and the main thread never raced ahead.

## Impact

Every occurrence drops one frame of client output. The engine already accounts for this
(`EngineLoop.LoopFrames` vs `EngineLoop.RenderedFrames`), so it degrades to stutter rather than
failing outright.

## Notable

`steamoverlayvulkanlayer.so` is interposed on `vkQueuePresentKHR`. The engine's own sibling error
string in this binary calls this class of problem out by name — *"a GPU overlay (e.g. Discord)
may be using this window"* — making the overlay the cheapest thing to eliminate first.

## Caveat on cadence

The ~1 s spacing between occurrences is probably an artifact of the tracer: each capture froze
all ~150 threads and wrote ~3,400 lines, and the engine's wall-clock timestamps advance during
that freeze. The stall itself is only 210 ms. **Do not read "one per second" as the real rate**
without a low-overhead re-run.

## Reproduction / capture

`bootstrap-linux/gdb/present-trace.py` breaks on tier0's `Warning`, matches the format string,
dumps all threads and auto-continues. `bootstrap-linux/gdb/split-trace.py` groups the resulting
occurrences by stack signature.

```
36 hit(s), 20 with backtraces -> 2 distinct signature(s)
  group-01   19 occurrence(s)  02:07:46 -> 02:13:40  [VKRenderThread, sbox]
  group-02    1 occurrence(s)  02:07:54 -> 02:07:54  [SDLAudioP15, VKRenderThread, sbox]
```

Group 02 is group 01 with the audio thread incidentally awake — the same stall.

## Open questions

- Does it survive with the Steam overlay disabled?
- Does it survive with vsync off?
- What is the true occurrence rate without the debugger attached?
- Is `fd=235` the render node or the NVIDIA control device?
