# Bundled third-party license texts

`pdfium/` contains LICENSE and the complete licenses directory from bblanchon's **non-V8 Windows x64 chromium/8125** archive:
https://github.com/bblanchon/pdfium-binaries/releases/download/chromium/8125/pdfium-win-x64.tgz
Archive SHA-256: `9a964261760cff52570aabf038ab346d3a5034dd8b30d0d5fe8861235d356b8b`.
The matching Linux x64 archive was also checked:
https://github.com/bblanchon/pdfium-binaries/releases/download/chromium/8125/pdfium-linux-x64.tgz
SHA-256: `e1c3ce6e270cb5e0c20816b379ad9f2c1309972697c505f508de92bf5e7d6659`.
All its notice texts match the bundled Windows texts after CRLF/LF normalization.

`packages/` contains the license and third-party notices shipped inside the restored NuGet packages.
Regenerate with `dotnet fsi tools/Collect-Licenses.fsx` after restoring changed package versions; review the diff.
Optional `--assets <project.assets.json>` and `--output <directory>` arguments support alternative restore/output locations.
`avalonia.txt`, `fsharp.txt`, `microcom.txt` and `tmds-dbus.txt` supply texts for packages which declare a license but do not bundle it.
Their source URLs are in `THIRD_PARTY_NOTICES.md`.

The deployment profiles are Native AOT, so the .NET runtime is compiled into the executable. The publish target copies
the resolved runtime pack's `LICENSE.TXT` and `THIRD-PARTY-NOTICES.TXT` to `licenses/dotnet/` (for any self-contained publish).
A framework-dependent publish (`-p:PublishAot=false -p:SelfContained=false`) does not redistribute the runtime and omits them.
