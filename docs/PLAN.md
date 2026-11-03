# PdfButler plan and handover

Last updated: 2026-11-03. This file continues `reports/Lightweight PDF viewer build plan.md` (the research). See `AGENTS.md` for how to work in the repo.

## Where we are

| Plan gate | Status |
|---|---|
| **1. Compatibility** (rendering, text geometry, search, links/outline, passwords, annotation/form display on discriminating cases) | **Done on Windows.** Not yet run on Linux. |
| **2. Vertical slice** (open → owner thread → visible page → scroll/zoom → selection/search, instrumented) | **Done on Windows**: engine, view model and UI built and tested (below). Needs hand testing on a real desktop. |
| 3. Hardening (security policy, resource bounds, keyboard/DPI, packaging) | Engine limits/inert actions/typed errors, tab bounds, renderer crash isolation and private deployment packaging done. Desktop DPI/accessibility and OS sandbox still outstanding. |
| 4. Private beta | Not started. |

Tests: **61 core + 167 engine (129 corpus documents + 1 extended) + 52 app/IPC/lifecycle = 280**, all passing on Windows.

### Latest increment: many tabs, settings and session restore

- **Tab strip without a scroll bar.** Tabs shrink from 220 to 110 DIPs to fit, then the strip scrolls: ◀ ▶ arrows and an
  "all open PDFs" ⌄ list with a filter appear only when tabs overflow (the list shows from two tabs). The mouse wheel scrolls
  the strip, the selected tab is always scrolled fully into view, and middle-click closes a tab. The strip height never changes.
  Sizing/scrolling rules are pure (`Core/TabStrip.fs`) with property tests; `App/TabStripPanel.fs` only lays out.
- **Sidebar layout** (setting): open PDFs listed down the left with full names, count and a settings button.
- **Settings window** (⚙ button or Ctrl+,), applied and saved on every change: tab layout, maximum open PDFs
  (**default 30**, 1–100), ask before closing several PDFs, reopen last session, theme (system/light/dark) and default zoom
  for newly opened PDFs. Stored in `%LOCALAPPDATA%\PdfButler\settings.json` (Linux `~/.local/share/PdfButler/`), parsed
  field by field so a bad value only resets that field (`Core/Settings.fs`).
- **Session restore** (off by default): on close, `session.json` keeps paths, the 0-based top page per tab and the selected tab,
  never passwords. With restore off the file is deleted. Restored tabs start **no helper until first selected**; missing files are
  skipped (existence checked off the UI thread). Files given on the command line are opened after the restored tabs and selected.
  The saved page is shown with `Viewer.ShowPage`, which adds no Back entry. `--measure` never reads or writes preferences.
- Follow-up: put idle tabs to sleep (close their helper and reopen on selection) so open-tab count is not bounded by memory.

### Previous increment: renderer crash isolation

- Opening a tab now immediately shows an indeterminate loader with the filename and a cloud-download
  explanation. It remains until the first visible page pixels or page-error placeholder, and clears on
  open failure/close. Delayed-open tests cover feedback, tab switching and closing without resurrection.
- **One helper process per PDF**, launched from the same executable in headless `--renderer` mode.
  A PDFium crash/hang now fails only its document; other tabs and the UI remain usable. **Reopen PDF**
  starts a fresh renderer. Closing a tab kills its helper without waiting for native work.
- Private, bounded, versioned binary stdin/stdout IPC carries pixels and document data; paths/passwords
  are not process arguments. Each dispatched request has a 60-second timeout. Helpers exit on parent-pipe EOF.
- All UI tests now use real helpers. Additional tests cover forced helper termination during open and in
  active/inactive tabs, retry, independent documents, timeout/cancellation/cleanup, bounded decoding and exact data/pixel transport.
- **Crash isolation, not an OS sandbox.** Helpers retain the user's permissions. Windows is tested;
  Linux uses the same process/pipe implementation but has not yet been run. See [`RENDERER.md`](RENDERER.md).
- Historical startup/memory figures below predate this change. `--measure` now reports renderer memory
  separately from UI memory; remeasure total memory and startup before making new performance claims.
- Both ReadyToRun targets publish successfully. The Windows folder passes a `--measure`
  first-page smoke test; Linux cross-publishing is not Linux runtime verification.

### Previous increment: tabs, branding and deployment

- Branded, light/dark-aware header with an original folded-document/bow-tie icon and wordmark (`assets/`).
- Up to **10 PDFs** (now a setting, default 30) in named, closeable tabs. Page/scroll/zoom/rotation/history, outline, search and selection belong to each tab.
  Duplicate paths select their existing tab (case-insensitive on Windows). Ctrl+Tab / Ctrl+Shift+Tab switch; Ctrl+W closes only the active tab.
  Picker, drag/drop and command line accept multiple files. Inactive tabs release raster caches and cancel queued renders;
  the active raster cache is still limited to 128 MiB (native document/text memory is separate).
- New files start at **100%, manual zoom**, including on large monitors and after resizing. Fit modes remain explicit commands.
- Visible **Back / Forward** history buttons, plus the existing Alt+Left / Alt+Right shortcuts.
- Windows single-instance activation: subsequent launches forward absolute file paths through a bounded, current-user-only named pipe.
  `--measure` bypasses this so each measurement is a fresh process. Linux currently opens separate instances.
- Release/framework-dependent/ReadyToRun **publish profiles** for win-x64 and linux-x64: managed DLLs bundled into the executable,
  native libraries beside it, dependency notices included and debug symbols excluded. Testers install the .NET 10 x64 Runtime.
  Standard outputs are cleaned automatically; custom directories require explicit cleanup opt-in. Successful publishes create a sibling `.zip`.
- Per-user Windows install/register/unregister scripts with `-WhatIf`. Registration adds Open with / Default Apps eligibility and an optional
  classic PDF context-menu verb. It **never changes the default PDF app** or touches Windows' protected UserChoice key.
  On Windows 11 the classic verb normally appears under Show more options. Actual registration has not been applied to this development machine.
- GitHub Actions matrix configured for Windows and Ubuntu 24.04, core corpus cache keyed by manifest hash. First remote run / Linux verification pending.
- `THIRD_PARTY_NOTICES.md` and bundled dependency license texts added. No test PDF or derived corpus data enters the app publish folder.

Deployment commands and limitations: **[`DEPLOYMENT.md`](DEPLOYMENT.md)**. The Windows deployment folder has been built and smoke-tested with `--measure`;
all three Windows integration scripts have been checked with `-WhatIf` only. The Linux x64 folder cross-publishes successfully from Windows, but has not been run on Linux.
This is **private deployment**, not completion of the sandbox/public-release gate.

### Decisions made (and why)

- **Own P/Invoke instead of PDFiumCore.** I inspected PDFiumCore 157.0.8100 as the plan asked. It maps C `unsigned long` to 64-bit everywhere,
  which is wrong on Windows where `unsigned long` is 32-bit. Text/link/bookmark APIs we need also weren't in its public surface.
  We use the same bblanchon native packages (now 158.0.8125, non-V8) with ~45 hand-checked signatures (`Native.fs`). Checking them against the headers caught a real bug before it shipped:
  `FPDFBitmap_BGRx` is 3, not 2.
- **Single global owner thread with a priority queue per process** (`Worker.fs`). The UI uses one helper
  per document; the direct in-process engine remains available to engine tests/tools.
- **Form widgets are displayed** through a form environment with every host callback NULL, plus `FPDF_FFLDraw`. Static-XFA documents with an AcroForm
  fallback (`xfa-foreground`) display fine. Dynamic XFA (`xfa-full`) renders blank, and the UI must warn about it.
- **Independent oracle = pdf.js in Node.** It has already caught five real issues:
  1. SASLprep was missing for AES-256 passwords.
  2. PDFium returned text outside the crop box. It's now filtered.
  3. Static XFA was being treated as unsupported.
  4. Links with invalid targets were silently dropped. They're now kept as `Unsupported` so the UI can explain them.
  5. My own oracle was truncating text.
- **Search runs in managed code** over the engine's text layer (`Core/Search.fs`), not `FPDFText_Find*`. Matching rules are documented and tested.
- **UI in code, not XAML.** Explicit, no reflection bindings. View state goes through the pure `Viewer.update`.

## UI (gate 2): first version built

`src/PdfButler.App` is in the solution and works end to end: open (picker, drag/drop, command line, password prompt), continuous scroll,
zoom (buttons, box, Ctrl+wheel around the pointer), fit width/page, rotate, page box with labels, outline panel, search bar with
debounce and highlights, drag/double-click selection and copy, links with confirmation, notices for dynamic XFA and signatures,
an error panel, and the keyboard shortcuts below.

| File | What it does |
|---|---|
| `Diagnostics.fs` | Timing marks since process start, JSON snapshot for `--measure`. |
| `RendererProcess.fs`, `RendererProtocol.fs`, `RendererHost.fs` | Per-document helper ownership, bounded binary IPC and headless PDFium host. |
| `PageRenderer.fs` | Tile planning (whole page ≤ 6 Mpx and ≤ 4096 px per side, else 1024 px tiles) and the 128 MiB bitmap cache (`Renderer`). |
| `PdfView.fs` | `PageCanvas` (drawing, pointer input, search, selection) and `DocumentView` (canvas + scroll bars). |
| `Dialogs.fs` | Password, confirm, info, close-several and settings dialogs. |
| `TabStripPanel.fs` | Shrinking, offset-scrolled tab row (rules in `Core/TabStrip.fs`). |
| `SettingsStore.fs` | Atomic, failure-tolerant `settings.json` / `session.json` storage. |
| `AppTypes.fs` | Shared UI types (`Centre`). |
| `DocumentPane.fs` | One document's toolbar, notices, search, outline, centre, status, keyboard and async open/close flow. |
| `MainWindow.fs` | Branded window, bounded tab ownership (top strip or sidebar), lazy restored tabs, settings, multi-file picker/drop, tab shortcuts. |
| `SingleInstance.fs` | Bounded current-user Windows activation pipe and instance ownership. |
| `Program.fs` | `App` (Fluent, follows system light/dark), entry point, `--measure <file>`. |

Pure parts went into Core with tests: `Core/PageInput.fs` parses the page box (exact label, then number, then label ignoring case) and the zoom box
("150", "150%"), and builds "Page iv (4 of 300)".

Dialogs and the URL launcher are replaceable properties on `MainWindow` (`AskPassword`, `ConfirmLink`, `LaunchUri`), so tests never show a dialog or launch anything.
Only http, https and mailto links are offered (after a confirmation showing the full URL). Other schemes, including `file:`, and unsupported actions only show a status message.

Keyboard: Ctrl+O open, Ctrl+W close tab, Ctrl+, settings, Ctrl+Tab / Ctrl+Shift+Tab next/previous tab, Ctrl+F find, F3 / Shift+F3 next/previous hit, Esc (close search, then clear selection), Ctrl+G page box,
Ctrl+C copy, Ctrl+= / Ctrl+− zoom, Ctrl+0 fit page, Ctrl+1 100%, Ctrl+2 fit width, Ctrl+Shift+= / Ctrl+Shift+− rotate,
PageUp/PageDown, Space/Shift+Space, Home/End, arrows, N/P, Alt+Left/Right. While typing in a box, only the Ctrl, F3, Esc and Alt shortcuts apply.

### UI tests (`tests/PdfButler.App.Tests`)

Headless Avalonia with real Skia rendering, the real engine, synthetic PDFs and a few corpus files. All 10 planned scenarios, plus notices and Ctrl+W:
ink position checked against the PDF spec's geometry, keyboard/label navigation and history, pointer-anchored zoom (≤ 1 DIP),
rotation quadrant read from the rendered frame, search highlight pixels and F3 cycling, the no-text message, drag-select + clipboard,
link/outline navigation with confirm and refusal, password retry/cancel, malformed and missing files, a broken page, A→B switching without stale tiles,
and the cache staying ≤ 128 MiB while scrolling 1500 pages at 200%.
Five further tests cover independent tab state/cache suspension/duplicate activation, the tab cap and inactive close,
close-during-password-open, real Windows pipe forwarding (spaces/Unicode paths), and multi-file command-line parsing.
The tab-state test saves synthetic-document light/dark frames to `artifacts/app/tabs-light.png` and `tabs-dark.png`; both have been reviewed.
Eight more cover the overflowing strip (arrows, reveal, fixed height, lazy tabs; frame `tabs-overflow.png`), session restore at the saved page,
the empty session when restore is off, the sidebar layout (`tabs-sidebar.png`), the close-prompt setting, default zoom, and the settings window.

Harness notes: `pumpUntil` runs the real dispatcher loop briefly, so timers such as the search debounce fire. `saveFrame w "name"` writes
`artifacts/app/name.png` when you need to look at a failure.

**Bug found by the UI tests:** copying across lines lost the line break. PDFium writes a line break as two generated characters (`\r\n`)
and `TextLayer.visibleOnly` kept only the first. Fixed, with a Core regression test. The pdf.js text comparison didn't catch it because it ignores whitespace.

### First measurements (Release, managed JIT, tracemonkey, 8 runs on the dev machine)

| | Result | Target |
|---|---|---|
| Launch → first page on screen | 1.07–1.14 s | ≤ 1.5 s |
| Open → first page on screen | ≈ 175 ms (cold engine) | ≤ 500 ms |
| Working set at first page | ≈ 160 MB | ≤ 300 MB |

About 860 ms of the launch time is before the window opens (framework start and JIT). ReadyToRun should cut that.
`PdfButler.exe --measure file.pdf` prints the JSON (on Windows it attaches to the parent console) and exits with 0 on success,
2 if the file didn't open, 3 on a 60 s timeout.

### Published-build measurements (2026-11-03)

Windows 10 22H2 x64 (10.0.19045), self-contained Release, 100% opening zoom. **20 fresh processes per document per mode**,
one discarded warm-up per document, warm filesystem cache. Modes ran sequentially, not concurrently. Percentiles use nearest rank.
"Open" is `first-page-presented - open-start` in a fresh process (not a warmed-up second tab).

| Document | R2R launch p50 / p95 | JIT launch p50 / p95 | R2R open p50 / p95 | R2R working set p95 |
|---|---|---|---|---|
| pdf.js tracemonkey (14 pages) | 575.5 / 603.4 ms | 1157.1 / 1194.1 ms | 122.5 / 133.6 ms | 148.5 MiB |
| PDF 2.0 UTF-8 test | 579.2 / 592.5 ms | 1153.4 / 1196.8 ms | 128.0 / 132.4 ms | 150.2 MiB |
| PDF 2.0 image with BPC | 561.2 / 581.8 ms | 1143.6 / 1163.7 ms | 112.3 / 121.0 ms | 139.8 MiB |

**Decision: keep ReadyToRun enabled for folder publishing.** It roughly halves launch time here and meets the original startup/memory targets.
Ordinary edit/run builds stay JIT; NativeAOT is deferred, not needed to meet the current targets.
Raw samples are local/ignored: `artifacts/startup-r2r.json` and `startup-jit.json`. Reproduce with `tools/Measure-Startup.fsx` (see deployment guide).
SumatraPDF wasn't found in the usual installation folders, so the independent viewer comparison remains outstanding.
This is not a cold-disk, high-DPI or hostile-document performance claim.

### Still to do in the UI

- Hand testing on a real desktop: file picker, drag and drop from Explorer, high-DPI monitors and DPI changes, dark theme, screen reader names.
  The picker and drag/drop aren't covered by the headless tests.
- Not built yet: remembering the position of files closed before the session ended, recent files, Select all. Session restore keeps the top page of tabs still open at exit.
- Outline children are built lazily on first expand. Very large outlines haven't been timed.

### Next

- Performance follow-up: compare with SumatraPDF when installed; measure warm second-tab opens and cold-disk/DPI cases.
  The ≥20-run, three-document JIT/ReadyToRun comparison is complete above; ReadyToRun is the publishing default.
- Linux: run the full suite on one named distro (glibc x64). Expect a few golden differences from fonts. Add per-platform baselines only with a reason.
- CI: review the first GitHub Actions Windows/Ubuntu 24.04 run; investigate rather than suppress any Linux golden differences.
- Notices: Windows/Linux pinned PDFium archives' notice texts match (line endings aside). Review application licensing before public redistribution.
- Private corpus: `corpus/private/` is now gitignored; private manifest loading/tests are not wired up yet. Never upload private artifacts.

## Risks and known limitations

- **PDFium is crash-isolated, not sandboxed.** Per-document helpers can be killed/restarted, but still have
  the user's permissions. OS restrictions and brokered read-only document access are needed before distributing
  to others (plan gate 3). A native exploit or system-wide memory exhaustion is not contained by this change.
- Request cancellation can't interrupt a running native render; tab close or the IPC timeout can terminate
  its helper. Large pages are tiled to bound the cost of each call.
- Page-tree repair differs from pdf.js on some malformed files (see the manifest's `discrepancies`). Missing pages show a placeholder.
- The ISO 32000 extended document is local-only. Its oracle output and goldens are gitignored.
- The `pdfjs` corpus files have unknown individual rights. Their derived oracle text and goldens are committed: review this before publishing the repo.
