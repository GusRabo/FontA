# FontA

A fast, native Windows font manager: browse folders of fonts, preview them with custom text, organise them with
favorites, tags and collections, and activate them temporarily without installing. Built with C# and WinUI 3.

This is a clean-room implementation inspired by the behaviour of FontBase; it contains none of FontBase's code.

## Features

- **Font folders:** pick any folder (local, external drive or network share) with **Add folder** and FontA
  remembers it. It rescans your folders when it starts and watches them while it runs, so fonts you drop in or delete
  show up on their own. Folders that are offline (an unplugged drive) are skipped and their fonts are kept until the
  folder returns. Manage them under **Settings** (the gear button, or Ctrl+,).
- **Folder scan** with live progress, cancel, and fonts appearing in the grid as they are found. Rescans only
  re-read files that changed, and drop fonts whose files were deleted.
- **Settings:** hide to the tray on close, start with Windows, start hidden, deactivate fonts on exit, and the folder
  and unused-font options. Most things are off until you turn them on. On by default: deactivating fonts on exit (as before),
  rescanning and watching your folders, and automatic updates (installed copies only; see *Updating FontA*). Only one copy of FontA runs at a time; starting it again brings the running one
  forward.
- **Fast grid:** virtualized cards with thumbnails drawn by DirectWrite on a pool of renderers, cached in memory
  and on disk.
- **Preview pane:** editable sample text, size, one slider per variable-font axis, and a checkbox per OpenType
  feature.
- **Organise:** favorites, tags, and collections, with a sidebar to filter by each.
  Right-click a collection in the sidebar to rename or delete it.
- **Activate / deactivate** fonts for the current Windows session. No admin rights, no files copied. Fonts you turn
  on are deactivated when FontA closes, unless you mark them **Always**.
- **Always active:** mark a font **Always** and FontA remembers it, activates it on every launch, and leaves it
  active after it closes (until you sign out). A sidebar entry lists them.
- **Deactivate unused fonts (opt-in):** turn it on in Settings, set the number of days (30 by default), and
  FontA deactivates active or always-active fonts you haven't used in that time. It checks at startup and hourly, tells
  you what it did, and offers **Undo**. A font counts as used when you preview it, activate or deactivate it, or change
  its Always setting in FontA. Windows doesn't record which apps use which fonts, so FontA can't see use inside other
  apps: a font you only use in, say, Photoshop can be deactivated. Installed fonts are never touched. Turning the
  feature on starts everyone's clock at that moment, so nothing is deactivated right away.
- **Install for my account:** the **Install** button on a card installs the font for your Windows user the way
  Windows' own per-user install does: the file is copied to `%LOCALAPPDATA%\Microsoft\Windows\Fonts` and registered
  under `HKCU\Software\Microsoft\Windows NT\CurrentVersion\Fonts`. It then survives sign-out and restart without
  FontA running. No admin rights needed, and **Uninstall** reverses it. Needs Windows 10 1809 or later.

## Keyboard shortcuts

Press **F1** in the app for this list. Font actions apply to the selected font.

| Keys | Action |
|---|---|
| Ctrl+F | Search fonts |
| Ctrl+L | Edit the sample text |
| Ctrl+O | Add a folder of fonts |
| Ctrl+, | Open settings |
| Ctrl+N | New collection |
| F2 | Rename the selected collection |
| Ctrl+1 / 2 / 3 / 4 | Show All fonts / Favorites / Always active / Installed |
| Ctrl+D | Favorite or unfavorite |
| Ctrl+Enter | Activate or deactivate |
| Ctrl+Shift+A | Toggle always active |
| Ctrl+Shift+I | Install or uninstall for your account |
| Ctrl+= / Ctrl+- / Ctrl+0 | Larger / smaller / reset preview text |
| Esc | Cancel a running scan, otherwise clear the search |
| F1 | Show the shortcut list |

## Requirements

- Windows 10 version 1809 (build 17763) or later; Windows 11 recommended
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) (x64)
- No Visual Studio needed; the command line is enough. The Windows App SDK is restored from NuGet and bundled
  with the app, so there is no separate runtime to install.

```powershell
winget install Microsoft.DotNet.SDK.8
```

## Build

From the repository root:

```powershell
dotnet build src/FontA.App -p:Platform=x64
```

The `-p:Platform=x64` flag is required for the app project. The first build restores NuGet packages and takes
a little longer.

## Run

```powershell
dotnet run --project src/FontA.App -p:Platform=x64
```

Or launch the built executable directly:

```
src\FontA.App\bin\Debug\net8.0-windows10.0.19041.0\win-x64\FontA.App.exe
```

Then click **Add folder...** and pick a folder containing `.ttf`, `.otf`, `.ttc` or `.otc` files
(for example `C:\Windows\Fonts`).

## Test

```powershell
dotnet test tests/FontA.Tests
```

The tests use fonts that ship with Windows (Arial, Calibri, Bahnschrift, Verdana, Tahoma) from
`C:\Windows\Fonts`, so they run on Windows only. One timing test compares a single renderer to the pool
and is skipped on machines with fewer than 4 cores.

## Project layout

| Project | What it holds |
|---|---|
| `src/FontA.Core` | UI-free library. OpenType reader (names, weight, variable axes, feature tags), the SQLite font index with tags and collections, and font activation via `AddFontResourceEx`. |
| `src/FontA.Rendering` | DirectWrite/Direct2D text rendering (via Vortice) with variation axes and OpenType features, plus the two-level thumbnail cache and renderer pool. |
| `src/FontA.App` | The WinUI 3 app: sidebar, virtualized grid, preview pane, scan progress. Unpackaged and self-contained. |
| `tests/FontA.Tests` | xUnit tests for the reader, library, scanning, rendering and thumbnail cache. |

## Where data lives

Everything is stored per user in `%LOCALAPPDATA%\FontA`:

- `library.db`: the SQLite index (fonts, favorites, tags, collections, remembered folders, settings)
- `crash.log`: written if FontA hits an unexpected error. Please include it when reporting a problem.
- Set the `FONTA_DATA_DIR` environment variable to use a different folder (handy for trying things out without
  touching your real library).
- `thumbs\`: cached thumbnails. Safe to delete at any time; they are re-rendered on demand, and files unused for
  60 days are removed at startup.

Your original font files are never modified. They are only copied if you use **Install**.

## Updating FontA

FontA updates itself from **GitHub Releases** using [Velopack](https://velopack.io). You publish a release; installed
copies find it, download it in the background, and install it when FontA is quit (or straight away if you choose
**Restart now** on the banner). Downloads are small: Velopack sends only what changed between versions (about 0.2 MB
for a small change, against a ~64 MB full package).

### One-time setup

1. **Put the project on GitHub.** The repository should be **public**. Updates are read straight from its releases, and
   a private repository would need an access key stored inside the app, which every user could extract.
   ```powershell
   git init
   git add .
   git commit -m "FontA"
   git remote add origin https://github.com/GusRabo/FontA.git
   git push -u origin main
   ```
2. In the repository's **Settings > Actions > General > Workflow permissions**, allow *Read and write permissions*, so
   the release workflow can publish. Nothing else needs configuring: the workflow sets the update address itself from
   the repository it runs in.

### Publishing an update

1. Make your changes and run `dotnet test`.
2. Tag and push:
   ```powershell
   git tag v0.2.0
   git push origin v0.2.0
   ```
3. GitHub Actions runs the tests, builds, packs, and publishes release **v0.2.0**. The tag *is* the version number
   (`v0.2.0` becomes `0.2.0`), so there is nothing to edit.

People install **FontAApp-win-Setup.exe** from the release page the first time. After that they never need it again:
FontA checks about 15 seconds after it starts and every 6 hours, as long as *Update automatically* is on in Settings.
**Settings > About > Check for updates** checks on demand.

Prefer to build on your own PC? `scripts\release.ps1 -Version 0.2.0 -Repo https://github.com/GusRabo/FontA` builds
and packs into `releases\` without sending anything anywhere. Add `-Upload` (with a `GITHUB_TOKEN` that can write to the
repository) to publish.

### What to know

- **Only the installed copy updates.** A build you run with `dotnet run` or from `bin\` has no installer, so Settings
  shows *Update automatically* as unavailable and says why.
- **Your library is safe.** The installed app lives in `%LOCALAPPDATA%\FontAApp`; your library and settings stay in
  `%LOCALAPPDATA%\FontA`. They are deliberately different folders: uninstalling deletes the app folder, and it must
  never contain your data. (This is why the package ID is `FontAApp`, not `FontA`.)
- **Quitting installs the update.** Closing the window does not, if *Hide to tray on close* is on, because FontA is
  still running. Use **Restart now** on the banner, or **Quit FontA** in the tray menu.
- **Start on system start keeps working** after updates: it points at the app's stable `current` folder, and is
  repaired on every start anyway.
- **The update address** is `<UpdateRepoUrl>` in `src/FontA.App/FontA.App.csproj`, set to
  `https://github.com/GusRabo/FontA`. Change it there if the repository ever moves; releases built by the workflow use
  whichever repository they are built in.
- **Privacy:** an update check contacts `api.github.com` and nothing else, and sends no information about you or your
  fonts. Turn *Update automatically* off to stop the background checks.

### Changing what is stored

- Add a step to `Migrate` in `src/FontA.Core/Library/FontLibrary.cs` and raise `FontLibrary.SchemaVersion`. Steps must
  be safe to run twice. New settings need no migration: add a property to `AppSettings` with a default.
- A newer library is never opened by an older FontA. It stops with a clear message instead of corrupting the data, so
  keep a copy of `library.db` before trying an older build.
- Run `dotnet test` before tagging. It checks that old libraries still open and migrate.

### Signing (recommended, optional)

Without a code-signing certificate, Windows SmartScreen shows a warning the first time someone runs `Setup.exe`, and
more so for a new, little-downloaded app. Velopack supports signing: add `--signParams "..."` to the `vpk pack` step in
`.github/workflows/release.yml` and store the certificate as a repository secret. Everything works without it.

### Testing updates without GitHub

Set `FONTA_UPDATE_SOURCE` to a folder (or URL) holding `vpk pack` output and an installed copy will use it instead of
GitHub. That is how the update path was tested: pack two versions into one folder, run the older one from its portable
zip, and watch it update to the newer.

## Known limitations

- The first scroll through a large library renders thumbnails on demand; later scrolls are served from the cache.
- Loading the font list reads every matching row on the UI thread, which may stutter on libraries of tens of
  thousands of fonts.
- Always-active fonts are re-activated when FontA starts. They are not installed into Windows, so after a sign-out
  or restart they stay off until you open FontA again. Use **Install** for fonts that must always be there.
- Installing copies the font file (everything else in FontA leaves your files alone). Some older apps and services
  ignore per-user fonts. If Windows still has an installed font open, uninstalling removes the registration but the
  copied file may linger until the apps using it close.
- If you delete the original file of an installed font, FontA keeps the font in your library (with its favorite, tags
  and collections) and points it at the installed copy, so you can still find and uninstall it. Fonts that were not
  installed are removed from the library, together with their tags and collection memberships. This happens on
  rescan and in a background check each time FontA starts.
- A font whose file disappears while FontA is running shows "File missing" on its card once its thumbnail fails
  to load; it is removed at the next rescan or restart.
