# AGENTS.md — PdfButler

A fast, read-only PDF reader for Windows and Linux. F# on .NET 10, Avalonia 12 UI, PDFium engine.
Background research: `reports/Lightweight PDF viewer build plan.md`. Current status and next steps: `docs/PLAN.md`.

## Scope

- **In:** reading. Open, continuous scroll, zoom, rotate, navigate (outline, links, labels, history), select/copy, search,
  and display of existing annotations and form values.
- **Out for now:** editing, form filling/saving, printing, signature verification, OCR, XFA, JavaScript.
  Form filling may come later. Keep the engine boundary narrow enough to add it without a rewrite.
- **Never:** write to the opened file, run document JavaScript, follow launch/remote/file actions, or fetch anything from the network on the document's behalf.

## Layout

```
src/PdfButler.Core      Pure F#: types, geometry, layout, viewer state machine, history, selection, search, LRU cache, settings/session, tab strip sizing. No native code.
src/PdfButler.Pdfium    Owned PDFium adapter: P/Invoke (Native.fs), single owner thread (Worker.fs), document API (Engine.fs).
src/PdfButler.App       Avalonia UI, per-document helper/protocol/host, page canvas, bitmap cache, --measure.
tests/PdfButler.Core.Tests     Unit + FsCheck property tests for Core.
tests/PdfButler.Engine.Tests   Worker, synthetic ground-truth, lifecycle/errors, corpus contract suite.
tests/PdfButler.App.Tests      Headless UI tests: real Skia frames, real engine, synthetic PDFs.
tests/PdfButler.TestSupport    PNG codec, image diff, synthetic PDF writer, corpus manifest/oracle loading.
tools/PdfButler.Corpus  Corpus tool: fetch (pinned + SHA-256), oracle, list.
tools/oracle            Node + pdf.js "oracle": independent engine whose answers the tests compare against.
tools/inspect.fsx       Ad-hoc: dump what PDFium sees for one corpus document vs the oracle.
corpus/                 manifest.json (committed), oracle/*.json and goldens/** (committed), cache/ (ignored).
```

## Commands

```powershell
dotnet build PdfButler.slnx
dotnet test PdfButler.slnx                                      # everything (≈20 s once the corpus is fetched)
dotnet run --project src/PdfButler.App -- file.pdf              # run the viewer
src/PdfButler.App/bin/Release/net10.0/PdfButler.exe --measure file.pdf   # timings JSON, then exit (build -c Release first)
dotnet test tests/PdfButler.Engine.Tests --filter "FullyQualifiedName!~CorpusTests"   # no corpus needed

# Corpus (first time on a machine, or after manifest changes)
dotnet run --project tools/PdfButler.Corpus -- fetch            # downloads core tier into corpus/cache, verifies SHA-256
dotnet run --project tools/PdfButler.Corpus -- fetch --extended # + large opt-in documents (not redistributable)
npm ci --prefix tools/oracle                                    # pdf.js oracle (needs Node 20+)
dotnet run --project tools/PdfButler.Corpus -- oracle           # regenerate corpus/oracle/*.json
node tools/oracle/verify-pdfjs-md5.mjs corpus/manifest.json corpus/cache   # cross-check pdf.js files vs upstream MD5s
dotnet fsi tools/inspect.fsx pdfjs/tracemonkey 0                # inspect one document/page (build Engine.Tests first)
```

Test environment variables:

| Variable | Effect |
|---|---|
| `PDFBUTLER_BLESS=missing` | Write golden PNGs that don't exist yet. `all` overwrites every golden. **Look at the images before committing.** |
| `PDFBUTLER_ALLOW_MISSING_CORPUS=1` | Skip corpus tests instead of failing when `corpus/cache` is empty (e.g. offline CI). |
| `PDFBUTLER_TRACE=1` | App only: print startup/first-page timing marks to stderr. |

Failing corpus checks write `report.txt`, `pN-actual.png` and `pN-diff.png` to `artifacts/corpus/<id>/`.

## Testing system (read before changing engine or core code)

Four independent sources of truth, so a bug can't hide behind "it matches itself":

1. **Pure unit/property tests** (`Core.Tests`): geometry round-trips, layout vs brute force, pointer-anchored zoom invariants,
   search normalisation, selection, LRU accounting, history, SASLprep.
2. **Synthetic ground truth** (`Engine.Tests/SyntheticTests.fs`): PDFs generated in code with known content. Checked against the
   *PDF spec's* geometry rules (`SyntheticPdf.expectedDisplayPoint`), not against PDFium. Covers every /Rotate, offset/negative boxes,
   crop boxes, view rotations, tiles vs whole-page renders, 1500-page labels, giant pages and size limits, links/outline.
3. **Independent engine oracle**: pdf.js (`corpus/oracle/*.json`) provides page counts, page sizes, text, link counts, outline sizes and page labels.
   The corpus suite requires PDFium to agree.
4. **Reviewed golden images** (`corpus/goldens/`): visual regression with a tolerance. There is also a guard that fails if a page renders blank while pdf.js finds text on it.

The corpus contract (`CorpusTests.fs`) runs every check for a document and reports all failures together.
If a disagreement is investigated and judged acceptable, record it in the manifest entry's `discrepancies` with a **reason**.
Never just loosen thresholds. Read `corpus/README.md` before adding documents.

Mutation check: deliberately breaking `Geometry.displayToView` for R90 makes the synthetic render test fail. Keep it that way.

## Engine rules (PDFium)

- **Every PDFium call runs on the single owner thread** (`Worker`), for all documents. PDFium is not thread-safe at all.
  Public `PdfDocument` methods queue work; never call `Native.*` elsewhere.
- The UI uses `RendererEngine`/`RemoteDocument`: **one helper process per PDF**, running the same executable
  with `--renderer` before UI/single-instance startup. Only the helper initialises PDFium. Direct engine tests/tools
  remain in-process. This is crash isolation, not an OS sandbox; see `docs/RENDERER.md`. Keep passwords out of arguments/logs.
- Priorities: `Interactive` (visible pages, open) > `Prefetch` > `Background` (search). Cancellation only removes *queued* work.
- No finalizers that touch PDFium. Documents must be disposed (`CloseAsync`/`Dispose`).
- Files are opened with `FileAccess.Read` + `FileShare.ReadWrite|Delete` through a custom read callback: never written, never locked against others.
- Form fill info is version 1 with all callbacks NULL. The native build is bblanchon non-V8 (no JS/XFA). Widgets are drawn with `FPDF_FFLDraw`
  after `FPDF_RenderPageBitmap` (the `FPDF_ANNOT` flag does not draw widgets).
- Bitmaps: app-owned BGRx (`FPDFBitmap_BGRx = 3`, *not* 2), pre-filled white, size-checked before allocation (`Limits`).
- Text is filtered to the visible page box (`TextLayer.visibleOnly`) so search never finds hidden text.
- Passwords: tried as UTF-8, then SASLprep (needed for AES-256/R6), then Latin-1. Never log them.
- Native signatures must match the headers shipped in the bblanchon package
  (`~/.nuget/packages/bblanchon.pdfium.win32/<ver>/build/native/include/pdfium/`). C `unsigned long` → `CULong`.
- Native library is resolved only from the app directory (`Native.installResolver`).
- Upgrading PDFium: bump `PdfiumNativeVersion` in `Directory.Packages.props`, re-check signatures against the new headers,
  run the full suite, and review any golden diffs by eye. Don't bulk re-bless.

## Coordinate spaces (Core/Geometry.fs)

- **page space**: PDF user space, points, y-up. Only the engine sees it (`Destination` X/Y are raw page space).
- **display space**: points, y-down, origin at the top-left of the visible page box, *after* the page's /Rotate, before view rotation.
  All engine outputs (char boxes, link rects, `DisplayDestination`) use this.
- **view space**: DIPs inside one page box after zoom and view rotation (`Geometry.displayToView`).
- **canvas space**: DIPs in the continuous page strip (`Layout`). Scroll offsets are canvas coordinates.
- Zoom 1.0 = 100% = 96/72 DIPs per point. Device pixels = DIPs × render scaling.

## Conventions and gotchas

- F# only, `net10.0`, central package versions in `Directory.Packages.props`. FSharp.Core is referenced explicitly (central package management drops the implicit reference).
- Tests use **xunit v3** (required by Avalonia.Headless.XUnit 12) and plain **FsCheck 3** inside `[<Fact>]` (FsCheck.Xunit is v2-only).
- In F# method calls, `x = y` as an argument is parsed as a *named argument*. Bind it first: `let same = (x = y)`.
- Avalonia 12 differs from 11: clipboard is `ClipboardExtensions.SetTextAsync`; drag/drop is `e.DataTransfer.TryGetFiles()`;
  use `TopLevel.GetTopLevel(visual)`; `TextBox.PlaceholderText`; no `Avalonia.Diagnostics`; explicit `UseSkia` needs `UseHarfBuzz`.
- UI is built in code, with no XAML and no reflection bindings. View state transitions go through `Viewer.update` (pure, tested).
- Keep pure logic in Core with tests. The App should only wire effects.
- Don't commit `corpus/cache/`, `artifacts/`, or derived data of `web/` (non-redistributable) documents.
