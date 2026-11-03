// Cross-checks cached pdf.js corpus files against the MD5s in pdf.js's own test manifest
// (at the pinned commit). Independent of our SHA-256 pins: catches a wrong pin on first fetch.
// usage: node verify-pdfjs-md5.mjs <manifest.json> <cacheDir>
import fs from "node:fs";
import path from "node:path";
import crypto from "node:crypto";

const [manifestPath, cacheDir] = process.argv.slice(2);
const manifest = JSON.parse(fs.readFileSync(manifestPath, "utf8").split("\n").filter((l) => !l.trim().startsWith("//")).join("\n"));
const commit = manifest.sources.pdfjs.commit;
const upstream = await (await fetch(`https://raw.githubusercontent.com/mozilla/pdf.js/${commit}/test/test_manifest.json`)).json();
const md5ByFile = new Map(upstream.map((e) => [e.file.replace(/^pdfs\//, ""), e.md5]));

let ok = 0, bad = 0, unlisted = 0;
for (const doc of manifest.documents.filter((d) => d.source === "pdfjs")) {
  const name = doc.path.split("/").pop();
  const file = path.join(cacheDir, "pdfjs", ...doc.path.split("/"));
  const expected = md5ByFile.get(name);
  if (!expected) { unlisted++; console.log(`unlisted upstream: ${name}`); continue; }
  const actual = crypto.createHash("md5").update(fs.readFileSync(file)).digest("hex");
  if (actual === expected) ok++; else { bad++; console.log(`MISMATCH ${name}: ${actual} != ${expected}`); }
}
console.log(`pdf.js md5: ${ok} ok, ${bad} mismatched, ${unlisted} not in upstream manifest`);
process.exit(bad > 0 ? 1 : 0);
