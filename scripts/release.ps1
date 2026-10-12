<#
  Builds a FontA release on your own PC. The GitHub Actions workflow does the same thing when you push a tag, so you
  only need this to test packaging, or if you'd rather not use Actions.

  Pack only (nothing is sent anywhere):
      scripts\release.ps1 -Version 0.2.0 -Repo https://github.com/you/FontA

  Also upload to GitHub as a published release (needs a token that can write to the repository):
      $env:GITHUB_TOKEN = '...'
      scripts\release.ps1 -Version 0.2.0 -Repo https://github.com/you/FontA -Upload

  Output goes to releases\. Setup.exe in there is what people install.
#>
param(
    [Parameter(Mandatory)] [string] $Version,
    [Parameter(Mandatory)] [string] $Repo,       # e.g. https://github.com/you/FontA (baked into the build so it knows where to look for updates)
    [switch] $Upload,
    [string] $Token = $env:GITHUB_TOKEN
)
$ErrorActionPreference = 'Stop'
if ($Version -notmatch '^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$') { throw "Version '$Version' should look like 1.2.3" }
if ($Upload -and -not $Token) { throw "Uploading needs a token: set GITHUB_TOKEN or pass -Token" }

$root = Resolve-Path (Join-Path $PSScriptRoot '..')
Set-Location $root
$packId = 'FontAApp'   # not "FontA": Velopack owns %LOCALAPPDATA%\<packId> and deletes it on uninstall, and FontA keeps your library in %LOCALAPPDATA%\FontA

dotnet tool restore
if ($LASTEXITCODE) { throw "dotnet tool restore failed" }

dotnet test tests/FontA.Tests --configuration Release
if ($LASTEXITCODE) { throw "Tests failed; not releasing." }

Remove-Item publish -Recurse -Force -ErrorAction SilentlyContinue
dotnet publish src/FontA.App -c Release -p:Platform=x64 "-p:Version=$Version" "-p:UpdateRepoUrl=$Repo" -o publish
if ($LASTEXITCODE) { throw "dotnet publish failed" }

if ($Upload) {
    # Deltas need the previous release's packages. On the very first release there is nothing to download.
    dotnet vpk download github --repoUrl $Repo --token $Token
}

dotnet vpk pack --packId $packId --packVersion $Version --packDir publish --mainExe FontA.App.exe --packTitle FontA --icon assets/fonta.ico
if ($LASTEXITCODE) { throw "vpk pack failed" }

if ($Upload) {
    # This repository uses immutable releases: nothing can be added once a release is published. So upload as a draft,
    # attach the checksums, then publish (the same order the GitHub workflow uses).
    dotnet vpk upload github --repoUrl $Repo --token $Token --releaseName "FontA $Version" --tag "v$Version"
    if ($LASTEXITCODE) { throw "vpk upload failed" }

    $pattern = 'Setup\.exe$|Portable\.zip$|-' + [regex]::Escape($Version) + '-(full|delta)\.nupkg$'
    $lines = Get-ChildItem releases -File | Where-Object { $_.Name -match $pattern } | Sort-Object Name |
        ForEach-Object { "{0}  {1}" -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLower(), $_.Name }
    $lines | Set-Content releases/SHA256SUMS.txt -Encoding ascii

    if (Get-Command gh -ErrorAction SilentlyContinue) {
        $env:GH_TOKEN = $Token
        $repoName = ($Repo -replace '^https://github.com/', '')
        gh release upload "v$Version" releases/SHA256SUMS.txt --repo $repoName --clobber
        if ($LASTEXITCODE) { throw "Could not attach the checksums." }
        gh release edit "v$Version" --repo $repoName --draft=false --latest
        if ($LASTEXITCODE) { throw "Could not publish the release." }
        "Published FontA $Version to $Repo"
    } else {
        "Uploaded FontA $Version to $Repo as a DRAFT. The GitHub CLI (gh) isn't installed, so: attach releases\SHA256SUMS.txt to the draft on GitHub, then publish it."
    }
} else {
    "Packed FontA $Version into releases\ (not uploaded). Add -Upload to publish it."
}
