# PDF Butler — private beta

## Before running

Install the **.NET 10 x64 Runtime** from:
https://dotnet.microsoft.com/en-us/download/dotnet/10.0

You need the regular **.NET Runtime**, not the SDK or ASP.NET Core. On Linux, use your
distro's .NET instructions to install `dotnet-runtime-10.0` and Avalonia's X11/font/GL
dependencies. `dotnet --list-runtimes` should show `Microsoft.NETCore.App 10.0.x` for x64.
Skip this prerequisite only if the developer specifically supplied a self-contained build.

## Run

1. Extract the ZIP into a new, dedicated folder. Do not run from inside the ZIP.
2. Keep all extracted files together, including native libraries and `licenses/`.
3. On Windows, run `PdfButler.exe`. On Linux, run `./PdfButler` (use `chmod +x PdfButler`
   first if extraction did not preserve executable permissions).
4. Open a PDF with Ctrl+O, or drag PDFs onto the window.

No installation or administrator access is required for portable use. Optional Windows
installation / Open with registration is explained in `DEPLOYMENT.md`; registration never
changes the default PDF application automatically. Builds are unsigned, so Windows may
show a reputation warning. Do not disable system-wide security settings to run a test build.

## Updating and feedback

Close all PDF Butler windows and extract the next ZIP into a fresh folder. Do not merge a
new version into a running or older installation. Update optional Windows registration if
you move the application (see `DEPLOYMENT.md`).

When reporting a problem, include the build/ZIP name, OS version and reproduction steps.
Do not send private PDFs or screenshots containing sensitive information without approval.

This is a **private test build**, not a security-hardened public release. PDF rendering is
crash-isolated but not OS-sandboxed. Prefer trusted documents during testing. See
`RENDERER.md` and `DEPLOYMENT.md` for limitations.
