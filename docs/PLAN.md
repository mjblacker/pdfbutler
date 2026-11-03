# PdfButler plan and handover

Last updated: 2026-11-03. This file continues `reports/Lightweight PDF viewer build plan.md` (the research). See `AGENTS.md` for how to work in the repo.

## Where we are

| Plan gate | Status |
|---|---|
| **1. Compatibility** (rendering, text geometry, search, links/outline, passwords, annotation/form display on discriminating cases) | **Done on Windows.** Not yet run on Linux. |
| **2. Vertical slice** (open → owner thread → visible page → scroll/zoom → selection/search, instrumented) | **Done on Windows**: engine, view model and UI built and tested (below). Needs hand testing on a real desktop. |
| 3. Hardening (security policy, resource bounds, keyboard/DPI, packaging) | Partly done in the engine (limits, inert actions, typed errors). UI parts not started. |
| 4. Private beta | Not started. |

Tests: 51 core + 167 engine (129 corpus documents + 1 extended) + 16 UI, all passing in about 20 seconds.

### Decisions made (and why)

- **Own P/Invoke instead of PDFiumCore.** I inspected PDFiumCore 157.0.8100 as the plan asked. It maps C `unsigned long` to 64-bit everywhere,
  which is wrong on Windows where `unsigned long` is 32-bit. Text/link/bookmark APIs we need also weren't in its public surface.
  We use the same bblanchon native packages (now 158.0.8125, non-V8) with ~45 hand-checked signatures (`Native.fs`). Checking them against the headers caught a real bug before it shipped:
  `FPDFBitmap_BGRx` is 3, not 2.
- **Single global owner thread with a priority queue** (`Worker.fs`). In-process for now (see Risks).
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
| `PageRenderer.fs` | Tile planning (whole page ≤ 6 Mpx and ≤ 4096 px per side, else 1024 px tiles) and the 128 MiB bitmap cache (`Renderer`). |
| `PdfView.fs` | `PageCanvas` (drawing, pointer input, search, selection) and `DocumentView` (canvas + scroll bars). |
| `Dialogs.fs` | Password, confirm and info dialogs. |
| `MainWindow.fs` | Toolbar, notice bar, search bar, outline, centre (document / welcome / error), status bar, keyboard, open/close flow. |
| `Program.fs` | `App` (Fluent, follows system light/dark), entry point, `--measure <file>`. |

Pure parts went into Core with tests: `Core/PageInput.fs` parses the page box (exact label, then number, then label ignoring case) and the zoom box
("150", "150%"), and builds "Page iv (4 of 300)".

Dialogs and the URL launcher are replaceable properties on `MainWindow` (`AskPassword`, `ConfirmLink`, `LaunchUri`), so tests never show a dialog or launch anything.
Only http, https and mailto links are offered (after a confirmation showing the full URL). Other schemes, including `file:`, and unsupported actions only show a status message.

Keyboard: Ctrl+O open, Ctrl+W close, Ctrl+F find, F3 / Shift+F3 next/previous hit, Esc (close search, then clear selection), Ctrl+G page box,
Ctrl+C copy, Ctrl+= / Ctrl+− zoom, Ctrl+0 fit page, Ctrl+1 100%, Ctrl+2 fit width, Ctrl+Shift+= / Ctrl+Shift+− rotate,
PageUp/PageDown, Space/Shift+Space, Home/End, arrows, N/P, Alt+Left/Right. While typing in a box, only the Ctrl, F3, Esc and Alt shortcuts apply.

### UI tests (`tests/PdfButler.App.Tests`)

Headless Avalonia with real Skia rendering, the real engine, synthetic PDFs and a few corpus files. All 10 planned scenarios, plus notices and Ctrl+W:
ink position checked against the PDF spec's geometry, keyboard/label navigation and history, pointer-anchored zoom (≤ 1 DIP),
rotation quadrant read from the rendered frame, search highlight pixels and F3 cycling, the no-text message, drag-select + clipboard,
link/outline navigation with confirm and refusal, password retry/cancel, malformed and missing files, a broken page, A→B switching without stale tiles,
and the cache staying ≤ 128 MiB while scrolling 1500 pages at 200%.

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

### Still to do in the UI

- Hand testing on a real desktop: file picker, drag and drop from Explorer, high-DPI monitors and DPI changes, dark theme, screen reader names.
  The picker and drag/drop aren't covered by the headless tests.
- Not built yet: back/forward buttons (keyboard only for now), remembering the last position per file, recent files, Select all.
- Outline children are built lazily on first expand. Very large outlines haven't been timed.

### Next

- Full `--measure` runs: launch-to-first-page and open-to-first-page, ≥ 20 repetitions, p50/p95, several documents.
  Compare with installed SumatraPDF. Then decide on ReadyToRun or NativeAOT based on the measurements.
- Linux: run the full suite on one named distro (glibc x64). Expect a few golden differences from fonts. Add per-platform baselines only with a reason.
- CI: GitHub Actions matrix (windows-latest, ubuntu-latest) with the corpus cached by manifest hash.
- `THIRD_PARTY_NOTICES`: PDFium LICENSE plus the bblanchon package's bundled notices, Avalonia (MIT), and corpus attributions (CC BY / CC BY-SA).
- Private corpus: `corpus/private/manifest.json` (gitignored), same tests, no artifacts uploaded.

## Risks and known limitations

- **PDFium runs in-process.** A crash on a hostile file takes the app down, and there is no OS sandbox. That's acceptable for private use. A restartable,
  OS-restricted helper process is needed before distributing to others (plan gate 3).
- Cancellation can't interrupt a running native render. Large pages are tiled to bound the cost of each call.
- Page-tree repair differs from pdf.js on some malformed files (see the manifest's `discrepancies`). Missing pages show a placeholder.
- The ISO 32000 extended document is local-only. Its oracle output and goldens are gitignored.
- The `pdfjs` corpus files have unknown individual rights. Their derived oracle text and goldens are committed: review this before publishing the repo.
