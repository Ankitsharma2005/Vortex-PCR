# Office / laptop workflow

Use one PRIVATE GitHub or GitLab repository for PCR source. Do not sync this
folder with OneDrive while Git operations or playout are running.

## First computer (this workspace)

1. Create an empty private repository (no README) and copy its HTTPS URL.
2. Install Git for Windows, and authenticate using Git Credential Manager.
3. In PowerShell at the project root:

```powershell
git init -b main
git config user.name "YOUR NAME"
git config user.email "YOUR VERIFIED EMAIL"
git remote add origin https://github.com/YOUR-ACCOUNT/YOUR-PRIVATE-REPOSITORY.git
powershell -ExecutionPolicy Bypass -File sync/Finish-Work.ps1 -Message "Initial PCR source"
```

Never put a password/token inside the URL. Credentials stay on each PC.

## Second computer

Install Git for Windows, .NET 8 SDK x64 (not just the runtime), and WebView2
Evergreen Runtime. Visual Studio 2022 with .NET desktop development is optional.
Install Blackmagic Desktop Video only when using DeckLink hardware.

```powershell
git clone https://github.com/YOUR-ACCOUNT/YOUR-PRIVATE-REPOSITORY.git NewsPlay-PCR
cd NewsPlay-PCR
powershell -ExecutionPolicy Bypass -File sync/Build-Run.ps1
```

The first build downloads NuGet dependencies. Open `desktop/NewsPlay.PCR.sln`.
Streaming additionally needs the same static FFmpeg executable on both PCs at
`tools/ffmpeg/ffmpeg.exe`; this large binary is not stored in Git. DeckLink
interop DLL and its notice are included with the engine source.

## Every handover

Before editing: `powershell -ExecutionPolicy Bypass -File sync/Start-Work.ps1`

After editing: `powershell -ExecutionPolicy Bypass -File sync/Finish-Work.ps1 -Message "Describe changes"`

Wait for **Push successful** before moving to the other PC. Pull failures stop
the script; nothing is force-pushed, reset, or automatically discarded. If both
PCs were edited without pulling, resolve the Git conflict before continuing.
Stop the development app/engine before rebuilding files it has loaded.

## What travels between PCs

PCR source, graphics templates, project files, checks, and these scripts.
MCR source, installers, SDK downloads, media, SQLite databases, licences,
activation data, logs, user profiles and generated outputs remain local.
Copy media separately using the same folder paths if sharing playlists. Each
PC uses its own `data/PCR-Development` database and device routing. Do not copy
machine-bound activation files. This workflow does not transfer chat history;
record the next step and outstanding issues in `sync/HANDOVER.md` before pushing.
