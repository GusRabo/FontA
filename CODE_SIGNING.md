# Code signing policy

> **Status (13 October 2026): releases are not code-signed yet.** FontA is applying for free code signing from the
> [SignPath Foundation](https://signpath.org/). Until that is approved, Windows SmartScreen and Smart App Control can
> warn about, or block, the installer. Verify a download against the `SHA256SUMS.txt` attached to each release.

When the application is approved, this page applies and the line below goes on the README and on every release page:

> Free code signing provided by [SignPath.io](https://signpath.io/), certificate by [SignPath Foundation](https://signpath.org/).

## What is signed

Only software built from this repository's own source, by this repository's own release workflow:

- FontA's own programs and libraries: `FontA.App.exe`, `FontA.App.dll`, `FontA.Core.dll` and `FontA.Rendering.dll`
- the installer (`FontAApp-win-Setup.exe`)

Third-party libraries that ship inside FontA (for example SQLite, Vortice, SharpGen and Velopack) are **not** signed by
this project. They are signed by their own publishers where those publishers sign them, and are otherwise included
exactly as published. Their licences are open-source licences compatible with FontA's GPL-3.0.

FontA is licensed under the GNU GPL v3 (see [LICENSE](LICENSE)) and contains no proprietary code.

## Who does what

FontA has a single maintainer, who holds every role below.

| Role | What it means | Who |
|---|---|---|
| **Author** (committer) | Trusted to change the source code | [GusRabo](https://github.com/GusRabo) |
| **Reviewer** | Reviews changes from anyone who is not an author, such as pull requests, before they are merged | [GusRabo](https://github.com/GusRabo) |
| **Approver** | Approves each signing request and so decides whether a release is signed | [GusRabo](https://github.com/GusRabo) |

All members must use multi-factor authentication for GitHub and for SignPath. If more maintainers join, they are
listed here, and nobody can approve a signing request for code they alone wrote and merged without review.

## How a release is signed

1. A version tag (`v1.2.3`) is pushed to the `main` branch. GitHub Actions builds FontA from that exact commit and
   runs the tests. The build runs on GitHub's own runners, so anyone can check what it did.
2. The workflow sends the built, unsigned files to SignPath. SignPath verifies they came from this repository's
   workflow for that commit.
3. The approver reviews the request and approves it by hand. Nothing is signed automatically.
4. The signed files are packed into the installer, and the release is published together with `SHA256SUMS.txt`.

## Checking a signature

In PowerShell:

```powershell
Get-AuthenticodeSignature .\FontAApp-win-Setup.exe | Format-List Status, SignerCertificate
```

`Status` should be `Valid` and the signer should be the SignPath Foundation. The same works on the installed
`FontA.App.exe` in `%LOCALAPPDATA%\FontAApp\current`.

## Privacy

See [PRIVACY.md](PRIVACY.md). In short: FontA does not send information to other systems, apart from checking GitHub
for updates, which you can turn off in Settings.

## Uninstalling

Uninstall FontA from *Settings > Apps*. The installer changes nothing outside your own Windows account: no services,
no drivers, no system folders. Your library and settings stay in `%LOCALAPPDATA%\FontA` until you delete that folder.

## Reporting a problem

If you think a signed FontA file is not what it should be, tell us through the private route in
[SECURITY.md](SECURITY.md). The maintainer will help with any investigation into a reported violation.
