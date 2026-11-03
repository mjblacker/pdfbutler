# Renderer crash isolation

PdfButler starts **one helper process per open PDF**, on both Windows and Linux. A PDFium
crash affects that document, not the window or the other tabs. The affected tab shows an error
and **Reopen PDF** starts a fresh helper. Reopening resets that document's view state and asks
for its password again if needed; there is no automatic crash/retry loop.

## Process boundary

- The helper is the same installed executable, started with the internal `--renderer` argument.
  This mode runs before Avalonia, window placement, measurement or single-instance activation.
  No extra executable or installation step is needed. Keep the existing deployment folder intact.
- The UI uses `RendererEngine` / `RemoteDocument` (`RendererProcess.fs`). It never initialises
  PDFium. `RendererHost.fs` owns the real `PdfEngine` and one document. All native calls still
  run on that helper's single PDFium owner thread.
- Private redirected stdin/stdout carry a versioned binary request/reply protocol. Document
  paths and passwords travel in the pipe, never in process arguments or logs. Stderr is drained
  and discarded, not displayed or recorded.
- The protocol transfers metadata, text geometry, links, outline, resolved destinations and
  raw BGRx pixels. It bounds frames (80 MiB), strings (1 MiB), page metadata (250,000 pages),
  image dimensions/stride, text/link/outline counts and outline depth. There is no general-purpose
  object deserialisation. Rendered pixels retain the engine's 64 MiB allocation ceiling.
- Each proxy schedules requests by priority. Cancelling queued work skips it; cancelling an
  already running request discards its result but still consumes the reply to preserve framing.

## Failure and cleanup

- Process exit, broken IPC or invalid replies fail the affected document's requests. Expected
  page-load/size-limit errors remain local operation errors rather than taking down the helper.
- Each dispatched request (including open) has a **60-second deadline** covering transmission,
  native work and the reply. A timeout terminates the helper. This can reject a legitimate but
  very slow PDF; it is a responsiveness policy, not a security resource quota.
- Closing a tab/window terminates its helpers immediately, without waiting for PDFium to return
  or close the native document. The engine is read-only and has nothing to save. Cancelled,
  superseded and failed opens also release their processes.
- A separate helper-side reader detects the parent closing its input pipe and exits the process,
  even if the PDFium owner thread is stuck. Windows helpers suppress native error-reporting dialogs.

`--measure` still reports UI-process `workingSetMB` / `privateMB`; it additionally reports
`rendererWorkingSetMB` / `rendererPrivateMB` for the measured document. Account for both when
comparing memory usage. Starting a runtime per PDF and copying pixels over a pipe add overhead;
the old in-process startup/memory measurements are historical, not a claim about this version.

## Not an OS sandbox

The helper **retains the user's permissions**, opens the supplied path itself (read-only), and
has no OS-enforced filesystem, network, process-creation or memory restrictions. Moving PDFium
out of process contains ordinary crashes/hangs, not a native-code exploit or system-wide OOM.
OS restrictions and brokered file handles/descriptors remain the public-release security gate.

The launcher/protocol use cross-platform .NET APIs, not Windows-only IPC. Windows tests cover
forced helper death during open and in active/inactive tabs, retry, unaffected documents, timeouts,
cancellation, process/pipe cleanup, protocol limits and exact rendering/data transport. The complete
UI suite also uses real helpers. Linux execution still needs verification on the supported distro.
