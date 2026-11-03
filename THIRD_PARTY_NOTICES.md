# Third-party notices

PDF Butler uses the following open-source components. License texts and notices accompany the application in `licenses/`.
No test PDF, corpus text or corpus image is included in the published application.

| Component | License / source |
|---|---|
| PDFium (Google / Foxit and contributors), bblanchon non-V8 build 158.0.8125 | BSD-3-Clause plus bundled dependency notices: `licenses/pdfium/`. https://github.com/bblanchon/pdfium-binaries |
| Avalonia 12.1.5 | MIT, https://github.com/AvaloniaUI/Avalonia/blob/12.1.5/licence.md |
| FSharp.Core 10.1.303 | MIT, https://github.com/dotnet/fsharp/blob/main/License.txt |
| MicroCom.Runtime 0.11.6 | MIT, https://github.com/kekekeks/MicroCom/blob/76785efcafd91b5902fd19dd11145f6dd655b7b4/LICENSE |
| Tmds.DBus.Protocol 0.94.1 (Linux desktop integration) | MIT, https://github.com/tmds/Tmds.DBus/blob/main/COPYING |
| SkiaSharp 3.119.4 and Skia | MIT and upstream native notices, `licenses/packages/SkiaSharp*` |
| HarfBuzzSharp 8.3.1.3 and HarfBuzz | MIT and upstream native notices, `licenses/packages/HarfBuzzSharp*` |
| ANGLE native assets | BSD and upstream notices, `licenses/packages/Avalonia.Angle.Windows.Natives/` |
| Microsoft .NET runtime (self-contained publishes) | MIT and third-party licenses in `licenses/dotnet/` (copied from the resolved runtime pack) |

## Development corpus (not distributed with the app)

Sources, authors, license declarations and restrictions for test PDFs are recorded per document in `corpus/manifest.json` and `corpus/README.md`.
The CC BY / CC BY-SA documents require their individual attribution and share-alike conditions if redistributed.
The pdf.js test corpus has unresolved individual rights. Review those documents and their derived data before publishing the repository;
this file does not grant rights to redistribute them. Never include `corpus/cache`, private PDFs, oracle text or goldens in an app package.
