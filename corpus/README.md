# Test corpus

`manifest.json` lists every public test document: where it comes from (pinned upstream commit), its SHA-256, tags,
which pages get golden images, the password if any, expectations, and investigated discrepancies.

## Layers

| Layer | Where | Committed? |
|---|---|---|
| Core public corpus (~129 files: pdf.js, PDFium, PDF Association PDF 2.0 examples, veraPDF) | `cache/<source>/...` | Bytes **no**. Manifest, oracle results and goldens **yes**. |
| Extended (large, opt-in: `fetch --extended`) | `cache/web/...` | Nothing derived from it. `.gitignore` excludes its oracle and goldens. |
| Private documents (your own files) | not yet wired up; plan: `corpus/private/` + a private manifest | Never. |

## Rights

- `pdfium` (BSD/Apache), `pdf20` (CC BY-SA 4.0) and `verapdf` (CC BY 4.0) files are redistributable with attribution.
- `pdfjs` test files are mostly from public bug reports and their individual rights are **unknown**. Their bytes are never committed.
  But `oracle/*.json` contains extracted text and `goldens/` contains rendered pages of them. **Review this before making the repository public.**
- `web/iso32000-2008` is Adobe's published copy of ISO 32000-1. For local testing only.

## Manifest fields

```jsonc
{ "id": "pdfjs/tracemonkey",          // <source>/<name>, unique
  "sha256": "...",                    // pinned on first fetch (--pin); a mismatch fails the fetch
  "source": "pdfjs", "path": "test/pdfs/tracemonkey.pdf",
  "tags": ["smoke", "text"],          // free-form; "smoke" marks the quick subset
  "tier": "core",                     // or "extended"
  "pages": [0, 1],                    // pages with golden images (default [0])
  "password": "abc",                  // user password, if any
  "expect": {
    "open": "ok",                     // ok | password | error | any (must not crash or hang)
    "pageCount": 14,
    "text": [{ "page": 0, "contains": "..." }],
    "search": [{ "query": "...", "minHits": 3 }],
    "outlineMin": 1,
    "forms": "acroform"               // none | acroform | xfa-foreground | xfa-full
  },
  "discrepancies": [{ "check": "text:0", "reason": "why this difference is acceptable" }],  // check names come from test output; "render:*" wildcard ok
  "visualMaxDiff": 0.002,             // optional per-document tolerance
  "textMinSimilarity": 0.85 }         // optional per-document oracle text threshold
```

## Adding a document

1. Add an entry without `sha256`. Prefer a pinned upstream source. For a new source, add it to `sources` with license notes.
2. `dotnet run --project tools/PdfButler.Corpus -- fetch --pin --only <id>`. Check that the file is what you expect.
3. `dotnet run --project tools/PdfButler.Corpus -- oracle --only <id>`
4. `dotnet test tests/PdfButler.Engine.Tests --filter "FullyQualifiedName~CorpusTests"`. Investigate every disagreement.
   Fix the code, or record a `discrepancies` entry with a reason.
5. `PDFBUTLER_BLESS=missing` once, **open the new PNGs in `goldens/` and check them**, then commit.

When a real-world failure is found in a private file, reduce it to a minimal public or synthetic case if possible
(`tests/PdfButler.TestSupport/SyntheticPdf.fs`) and add that instead.
