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
- **Sidebar views with counts:** All, Recent (the last 100 fonts you used), Favorites, Active, Inactive, Always active and
  Installed, then your Collections, **Folders** and Tags. Collections, Folders and Tags can be collapsed.
- **Fonts grouped by folder:** the grid shows your fonts under a heading for each folder they are in (a folder you added
  shows by name; folders inside it by their path from there). Click a folder in the sidebar to see just that one.
- **Activation circles:** a round switch next to every font, every folder heading, and every folder and collection in
  the sidebar. Solid green means all active, a ring with a dot means some, hollow means none. Click it to switch the
  whole group on (or off, when everything in it is already on). Installed fonts are left alone.
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
| Ctrl+5 / 6 / 7 | Show Recent / Active / Inactive fonts |
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
- `active-fonts.txt`: a record of which fonts FontA has switched on, so that after a crash or a forced close it can show
  the fonts Windows still has active as active. Ignored after a Windows restart.
- `crash.log`: written if FontA hits an unexpected error. Please include it when reporting a problem.
- Debug builds (not release builds) honour a `FONTA_DATA_DIR` environment variable to use a different folder, handy for
  trying things out without touching your real library. Release builds ignore it on purpose.
- `thumbs\`: cached thumbnails. Safe to delete at any time; they are re-rendered on demand, and files unused for
  60 days are removed at startup.

Your original font files are never modified. They are only copied if you use **Install**.

## Security

FontA reads files and folders you don't control, runs entirely as you (it never needs administrator rights), and updates
itself from the internet, so it is built to distrust its inputs. The full policy, including how to report a problem,
is in [SECURITY.md](SECURITY.md). In short:

- **Hostile fonts:** FontA validates every font with its own bounds-checked parser before anything else touches it,
  refuses oversized files, and never indexes, previews or activates a font that fails. The parser is fuzz-tested.
- **Hostile folders:** scans never follow junctions or symbolic links, so a crafted folder can't loop a scan or reach
  outside the folder you chose.
- **Tampered data:** all SQL is parameterised, and uninstalling only ever deletes a font file inside the per-user fonts
  folder, no matter what the database says.
- **Updates:** only from `github.com` over HTTPS, from the repository built into the app, with checksum
  verification and no downgrades. Release builds ignore environment variables, so nothing can redirect them.
- **Supply chain:** NuGet packages are locked to exact versions and hashes (`packages.lock.json`), CI fails on
  dependencies with known vulnerabilities, and the release workflow pins its actions to exact commits, takes the
  tag name as data rather than script, only releases commits on `main`, and has write access only in the job that
  publishes. Each release carries `SHA256SUMS.txt`.

**Honest limits:** Windows' own font engine renders the previews, so a flaw in Windows could be reached through a font
that passes FontA's checks (keep Windows updated and only add folders you trust). Builds are not code-signed yet.

### Protect the update channel (do this on GitHub)

Every installed copy of FontA trusts whatever is published on the repository's releases, so whoever controls the
repository controls what runs on your users' PCs. These settings are on GitHub and can't be set from the code:

1. **Turn on two-factor authentication** for your GitHub account (Settings > Password and authentication). This is the
   single most important step.
2. **Protect release tags**: Settings > Rules > Rulesets > New tag ruleset. Target `v*`, and enable *Restrict
   deletions* and *Block force pushes* (and *Restrict updates*). A release tag should never be moved or removed.
3. **Protect `main`**: a branch ruleset that blocks force pushes and deletion, and requires the **CI** check to pass.
4. **Turn on Dependabot alerts and security updates**, **secret scanning** and **push protection** (Settings > Code
   security). The repository already has a Dependabot config for NuGet packages and Actions.
5. **Enable private vulnerability reporting** (Settings > Code security), so people can follow SECURITY.md.
6. **Limit Actions** (Settings > Actions > General): allow only GitHub-owned actions, and set *Workflow permissions* to
   *Read repository contents*. The release job asks for the write access it needs itself.
7. Consider **code signing** (see below) and keep any signing certificate in repository secrets only.

## Updating FontA

FontA updates itself from **GitHub Releases** using [Velopack](https://velopack.io). You publish a release; installed
copies find it, download it in the background, and install it when FontA is quit (or straight away if you choose
**Restart now** on the banner). Downloads are small: Velopack sends only what changed between versions (about 0.2 MB
for a small change, against a ~64 MB full package).

### One-time setup

1. **Source stays private, releases are public.** Installed copies read updates straight from GitHub Releases without
   signing in, and a private repository would need an access key stored inside the app, which every user could
   extract. So the source lives in the private `GusRabo/FontA`, and the **public** `GusRabo/FontA-releases` holds
   nothing but the release files (installer, update packages, `SHA256SUMS.txt`) and a short README.
2. **Create the releases repository** (once): a new **public** repository named `FontA-releases`, initialised with a
   README so it has a first commit. In its Settings, turn on *Immutable releases*.
3. **Give the workflow access to it** (once): create a *fine-grained personal access token* (GitHub > Settings >
   Developer settings) that can reach **only** `FontA-releases`, with **Contents: Read and write**, and short expiry
   you are happy to renew. Add it to the private repository as the Actions secret **`RELEASES_TOKEN`**
   (Settings > Secrets and variables > Actions). The workflow stops with a clear message if it is missing.

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

Prefer to build on your own PC? `scripts\release.ps1 -Version 0.2.0 -Repo https://github.com/GusRabo/FontA-releases` builds
and packs into `releases\` without sending anything anywhere. Add `-Upload` (with a `GITHUB_TOKEN` that can write to the
repository) to publish.

### What to know

- **Only the installed copy updates.** A build you run with `dotnet run` or from `bin\` has no installer, so Settings
  shows *Update automatically* as unavailable and says why.
- **Your library is safe.** The installed app lives in `%LOCALAPPDATA%\FontAApp`; your library and settings stay in
  `%LOCALAPPDATA%\FontA`. They are deliberately different folders: uninstalling deletes the app folder, and it must
  never contain your data. (This is why the package ID is `FontAApp`, not `FontA`.)
- **Releases are immutable.** The releases repository has GitHub's *immutable releases* turned on, so once a release is
  published its files can't be added to, changed or replaced, by anyone. The workflow therefore uploads everything
  (including `SHA256SUMS.txt`) to a draft and publishes it last. If a published release has a mistake, fix it with a
  new version rather than editing it.
- **Quitting installs the update.** Closing the window does not, if *Hide to tray on close* is on, because FontA is
  still running. Use **Restart now** on the banner, or **Quit FontA** in the tray menu.
- **Start on system start keeps working** after updates: it points at the app's stable `current` folder, and is
  repaired on every start anyway.
- **The update address** is `<UpdateRepoUrl>` in `src/FontA.App/FontA.App.csproj`, set to
  `https://github.com/GusRabo/FontA-releases`. Change it there (and `RELEASES_REPO` in the release workflow) if the
  releases repository ever moves.
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

Debug builds only: set `FONTA_UPDATE_SOURCE` to a folder (or URL) holding `vpk pack` output and that build will use it
instead of GitHub. Release builds ignore the variable, so it can't be used to redirect updates. To test, publish two
versions with `-c Debug`, pack both into one folder, run the older one from its portable zip, and watch it update.

## Known limitations

- **Activating many fonts is slow, and that is Windows, not FontA.** Switching a font on system-wide makes Windows update
  its font list for every open app. On the PC used for testing that cost about half a second per font, so a folder of 300
  fonts takes about 2.5 minutes (and deactivating takes as long). It runs in the background with a progress line, a time
  estimate and a Cancel button, FontA keeps working meanwhile, and it asks before long jobs. Using several threads does not
  help; only process-private activation is instant, but those fonts would be invisible to your other apps.
- If FontA is closed while it is still activating, it stops, then deactivates what it had switched on, which can take a
  moment for a big batch.

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
