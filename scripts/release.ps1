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
dotnet publish src/FontA.App -c Release -r win-x64 -p:Platform=x64 -p:SelfContained=true "-p:Version=$Version" "-p:UpdateRepoUrl=$Repo" -o publish
if ($LASTEXITCODE) { throw "dotnet publish failed" }

if ($Upload) {
    # Deltas need the previous release's packages. On the very first release there is nothing to download.
    dotnet vpk download github --repoUrl $Repo --token $Token
}

dotnet vpk pack --packId $packId --packVersion $Version --packDir publish --mainExe FontA.App.exe --packTitle FontA --icon assets/fonta.ico
if ($LASTEXITCODE) { throw "vpk pack failed" }

if ($Upload) {
    dotnet vpk upload github --repoUrl $Repo --token $Token --publish --releaseName "FontA $Version" --tag "v$Version"
    if ($LASTEXITCODE) { throw "vpk upload failed" }
    "Published FontA $Version to $Repo"
} else {
    "Packed FontA $Version into releases\ (not uploaded). Add -Upload to publish it."
}
