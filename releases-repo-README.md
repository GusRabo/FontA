# FontA releases

Downloads for **FontA**, a font manager for Windows. This repository only holds the release files; the source code is
in a private repository.

## Install

Download **FontAApp-win-Setup.exe** from the [latest release](../../releases/latest) and run it. No administrator
rights are needed.

FontA then updates itself from the releases here.

## Check your download

Each release includes `SHA256SUMS.txt`. In PowerShell:

```powershell
Get-FileHash .\FontAApp-win-Setup.exe -Algorithm SHA256
```

The hash must match the line for that file in `SHA256SUMS.txt`.

Releases are immutable: once published, their files can't be changed.
