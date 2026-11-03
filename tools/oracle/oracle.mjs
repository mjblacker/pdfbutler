// Independent oracle: extracts facts about each corpus PDF with Mozilla pdf.js, a separately
// implemented engine. The .NET tests compare PDFium's answers against these files.
//
// usage: node oracle.mjs <manifest.json> <cacheDir> <outDir> [--only <id>]
//
// pdf.js runs with scripting, XFA and font-face loading disabled; nothing is evaluated.

import fs from "node:fs";
import path from "node:path";
import { createRequire } from "node:module";
import { getDocument, version } from "pdfjs-dist/legacy/build/pdf.mjs";

const require = createRequire(import.meta.url);
const pdfjsRoot = path.dirname(require.resolve("pdfjs-dist/package.json"));
// pdf.js requires forward slashes and a trailing slash, even on Windows.
const asDir = (p) => p.replaceAll("\\", "/") + "/";
const standardFontDataUrl = asDir(path.join(pdfjsRoot, "standard_fonts"));
const cMapUrl = asDir(path.join(pdfjsRoot, "cmaps"));

const [manifestPath, cacheDir, outDir, ...rest] = process.argv.slice(2);
const onlyIdx = rest.indexOf("--only");
const only = onlyIdx >= 0 ? rest[onlyIdx + 1] : null;

// The manifest allows whole-line // comments.
const manifestText = fs.readFileSync(manifestPath, "utf8")
  .split("\n").filter((l) => !l.trim().startsWith("//")).join("\n");
const manifest = JSON.parse(manifestText);

const MAX_PAGES = 50;
const MAX_TEXT = 60000;

const safeId = (id) => id.replace(/[^A-Za-z0-9.\-\/]/g, "_").replaceAll("/", "__");

function countOutline(items) {
  if (!items) return 0;
  let n = 0;
  for (const it of items) n += 1 + countOutline(it.items);
  return n;
}

async function inspect(doc) {
  const file = path.join(cacheDir, doc.source, ...doc.path.split("/"));
  if (!fs.existsSync(file)) return null;
  const data = new Uint8Array(fs.readFileSync(file));
  const result = { id: doc.id, engine: `pdf.js ${version}`, ok: false, error: null, numPages: 0, pages: [], outline: null, labels: null, hasForms: false };
  let pdf;
  let task;
  try {
    task = getDocument({
      data,
      password: doc.password ?? undefined,
      isEvalSupported: false,
      enableXfa: false,
      disableFontFace: true,
      useSystemFonts: false,
      standardFontDataUrl,
      cMapUrl,
      cMapPacked: true,
      verbosity: 0,
      stopAtErrors: false,
    });
    pdf = await task.promise;
  } catch (e) {
    result.error = `${e?.name ?? "Error"}: ${e?.message ?? e}`;
    return result;
  }
  try {
    result.ok = true;
    result.numPages = pdf.numPages;
    const n = Math.min(pdf.numPages, MAX_PAGES);
    for (let i = 1; i <= n; i++) {
      const entry = { index: i - 1, width: 0, height: 0, rotate: 0, text: "", links: 0, widgets: 0, annotations: 0, error: null };
      try {
        const page = await pdf.getPage(i);
        const vp = page.getViewport({ scale: 1 });
        entry.width = vp.width;
        entry.height = vp.height;
        entry.rotate = page.rotate;
        const tc = await page.getTextContent();
        let text = "";
        for (const item of tc.items) {
          if (typeof item.str === "string") text += item.str;
          if (item.hasEOL) text += "\n";
        }
        entry.text = text.slice(0, MAX_TEXT);
        const annots = await page.getAnnotations();
        entry.annotations = annots.length;
        entry.links = annots.filter((a) => a.subtype === "Link").length;
        entry.widgets = annots.filter((a) => a.subtype === "Widget").length;
        page.cleanup();
      } catch (e) {
        entry.error = String(e?.message ?? e);
      }
      result.pages.push(entry);
    }
    try {
      const outline = await pdf.getOutline();
      result.outline = outline ? { top: outline.length, total: countOutline(outline), titles: outline.slice(0, 20).map((o) => o.title) } : null;
    } catch (e) { result.outline = null; }
    try { result.labels = await pdf.getPageLabels(); } catch (e) { result.labels = null; }
    try {
      const fields = await pdf.getFieldObjects();
      result.hasForms = !!fields && Object.keys(fields).length > 0;
    } catch (e) { result.hasForms = false; }
  } finally {
    await task.destroy();
  }
  return result;
}

fs.mkdirSync(outDir, { recursive: true });
let written = 0, missing = 0;
for (const doc of manifest.documents) {
  if (only && doc.id !== only) continue;
  const r = await inspect(doc);
  if (!r) { missing++; continue; }
  fs.writeFileSync(path.join(outDir, safeId(doc.id) + ".json"), JSON.stringify(r, null, 1) + "\n");
  written++;
  if (!r.ok || process.env.ORACLE_VERBOSE) console.log(`${doc.id}: ${r.ok ? `${r.numPages} pages` : r.error}`);
}
console.log(`oracle: wrote ${written}, missing from cache ${missing}`);
