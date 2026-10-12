# Security policy

## Reporting a vulnerability

Please **don't open a public issue** for a security problem. Use GitHub's private reporting instead:
**Security tab > Report a vulnerability** on https://github.com/GusRabo/FontA. Include what you did, what you expected,
and what happened; a sample file or steps that reproduce it help most. You will get a reply as soon as possible, and
fixes ship as a normal update, which installed copies pick up on their own.

## Supported versions

Only the latest release is supported. FontA updates itself, so please update before reporting.

## What FontA treats as untrusted

FontA reads files and folders from places you don't control, so it assumes they may be hostile:

| Input | How it is handled |
|---|---|
| Font files | Parsed by FontA's own bounds-checked reader before anything else touches them. A font that fails the checks is never indexed, previewed or activated. Files over 256 MB are refused. The parser is fuzz-tested. |
| Folders | Scans never enter junctions or symbolic links, so a crafted folder can't loop a scan or reach outside the folder you chose. |
| Names inside fonts | Used as text only. Before one reaches the registry it is stripped of control characters and length-limited. |
| The library database | All SQL is parameterised. Paths read back from it are checked before anything is deleted: an uninstall only ever deletes a font file inside the per-user fonts folder. |
| Updates | Fetched only from `github.com` over HTTPS, from the repository baked into the build. Velopack verifies each package's checksum, and refuses downgrades. |
| Environment variables | Release builds ignore them. The variables that redirect the data folder or the update source exist in debug builds only. |

## What FontA does not do

- It runs entirely as you. It never asks for administrator rights and never changes anything machine-wide (no services,
  no drivers, no system folders).
- It sends no telemetry. The only network traffic is the update check to `api.github.com` (and the download from
  GitHub when an update exists).
- It never executes anything from a font file or a folder you add.

## Known limits

- **Windows renders the fonts.** Previews and activation use Windows' own font engine (DirectWrite and GDI), which runs
  inside FontA's process. A flaw in Windows itself could be reached through a specially crafted font that passes
  FontA's own checks. Keep Windows up to date, and don't add folders you don't trust.
- **Builds are not code-signed yet**, so Windows SmartScreen may warn on first run. Verify a download against the
  `SHA256SUMS.txt` attached to each release.
- **The update channel is only as safe as the GitHub account behind it.** Protect that account (two-factor
  authentication) and the repository's `v*` tags; see the README's security section.
