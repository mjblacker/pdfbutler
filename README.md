# PdfButler

A fast, minimal PDF reader for Windows and Linux, built on .NET 10.

PdfButler does one job: open PDFs quickly and let you read them. It has no account, no cloud features, no AI and no editor.
It opens the file read-only, never changes it, never runs document JavaScript and never goes online on a document's behalf.

- **Fast.** First page on screen in about 0.2 s from launch and under 100 ms after opening (published Native AOT build, Windows 10).
- **Multiplatform.** F# on .NET 10 with an [Avalonia](https://avaloniaui.net/) UI. Windows is tested. Linux builds, but hasn't been fully tested yet.
- **Reliable rendering.** Uses [PDFium](https://pdfium.googlesource.com/pdfium/), the engine in Chrome. Each PDF opens in its own
  helper process, so a document that crashes the engine only closes its own tab.
- **Well tested.** Results are checked against the PDF spec, an independent engine (pdf.js) and reviewed reference images,
  across a corpus of more than 100 real-world documents.

> **Status:** early private beta. See [`docs/PLAN.md`](docs/PLAN.md) for progress and known limitations.

## Features

- Open from the file picker, drag and drop, the command line or Windows "Open with". Password-protected PDFs are supported.
- Tabs for many open PDFs (or a sidebar list), with optional session restore.
- Continuous scrolling, zoom (including Ctrl+wheel around the pointer), fit width / fit page, and rotation.
- Navigation: page numbers and page labels, the document outline, internal links, and Back / Forward history.
- Text search with highlights, text selection and copy.
- Shows existing annotations and form field values.
- External links (http, https, mailto) open only after you confirm the full URL.
- Light and dark themes, and keyboard shortcuts for everything.

## Not yet supported

These are standard reader features we're likely to add:

- Printing
- Filling in and saving forms
- Signing documents, and checking existing digital signatures
- Recent files list, and remembering your position in files you've closed
- Select all
- Thumbnail sidebar
- Presentation / full-screen mode
- Dynamic XFA forms (shown with a warning, the page renders blank)

PdfButler is meant to stay a small, fast reader. Out of scope: AI features (chat, summaries and so on), PDF editing,
page reorganising, conversion, OCR and running document JavaScript.

## Building and running

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/en-us/download/dotnet/10.0).

```text
dotnet build PdfButler.slnx
dotnet run --project src/PdfButler.App -- file.pdf
dotnet test PdfButler.slnx
```

The corpus tests need the test documents downloaded first:

```text
dotnet run --project tools/PdfButler.Corpus -- fetch
```

To build a deployable Native AOT folder and ZIP (testers install nothing; publishing needs the MSVC C++ workload on Windows,
or clang and zlib1g-dev on Linux, and the Linux folder must be published on Linux):

```text
dotnet publish src/PdfButler.App -p:PublishProfile=win-x64
dotnet publish src/PdfButler.App -p:PublishProfile=linux-x64
```

More detail:

- [`docs/DEPLOYMENT.md`](docs/DEPLOYMENT.md): publishing and Windows file association scripts
- [`docs/RENDERER.md`](docs/RENDERER.md): the per-document helper process
- [`corpus/README.md`](corpus/README.md): the test corpus
- [`AGENTS.md`](AGENTS.md): repository layout, testing rules and conventions for contributors

## Licences

Third-party components and their licences are listed in [`THIRD_PARTY_NOTICES.md`](THIRD_PARTY_NOTICES.md).
