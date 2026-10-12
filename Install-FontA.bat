@echo off
rem ==========================================================================
rem  Install-FontA.bat - installs FontA for the current Windows user.
rem
rem  It downloads the latest FontA installer from the project's GitHub release
rem  (https://github.com/GusRabo/FontA/releases), checks the download against the
rem  SHA-256 checksum published with that release, and only then runs it.
rem  No administrator rights are needed.
rem
rem  Offline install: put FontAApp-win-Setup.exe and SHA256SUMS.txt (both from the
rem  same release page) in the same folder as this file. They are checked the same
rem  way and nothing is downloaded.
rem
rem  This one file contains both the batch part and the PowerShell script below
rem  the marker line, so it can be copied anywhere on its own.
rem ==========================================================================
title FontA installer
echo.
echo  FontA installer
echo  ---------------
echo.
rem The paths go through variables set here, so folder names with spaces or apostrophes are safe.
set "FONTA_BAT=%~f0"
set "FONTA_DIR=%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -Command "$t = [IO.File]::ReadAllText($env:FONTA_BAT); $i = $t.IndexOf('#'+'POWERSHELL#'); Invoke-Expression $t.Substring($i); $rc = Install-FontA -LocalDir $env:FONTA_DIR; exit $rc"
set RC=%errorlevel%
echo.
if %RC%==0 (echo  Done.) else (echo  The installation did not complete. Nothing was changed on this PC.)
echo.
pause
exit /b %RC%

#POWERSHELL#
function Install-FontA {
    param(
        # Where releases are downloaded from. Anything other than GitHub is only accepted together with -NoRun (testing).
        [string] $BaseUrl = 'https://github.com/GusRabo/FontA/releases/latest/download',
        # A folder that may already hold FontAApp-win-Setup.exe and SHA256SUMS.txt for an offline install.
        [string] $LocalDir = '',
        # Download and verify, but don't run the installer (used by the tests).
        [switch] $NoRun
    )

    $name = 'FontAApp-win-Setup.exe'
    $sumsName = 'SHA256SUMS.txt'
    $ProgressPreference = 'SilentlyContinue'     # the progress bar makes large downloads much slower
    try { [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12 } catch { }

    if (-not $NoRun -and $BaseUrl -notmatch '^https://github\.com/GusRabo/FontA/') {
        Write-Host "  Refusing to run an installer from $BaseUrl" -ForegroundColor Red
        return 2
    }
    if (-not [Environment]::Is64BitOperatingSystem) {
        Write-Host '  FontA needs 64-bit Windows.' -ForegroundColor Red; return 3
    }
    if ([Environment]::OSVersion.Version.Build -lt 17763) {
        Write-Host '  FontA needs Windows 10 version 1809 or newer.' -ForegroundColor Red; return 3
    }

    $tmp = Join-Path ([IO.Path]::GetTempPath()) ('FontA-install-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $tmp | Out-Null
    try {
        $setup = Join-Path $tmp $name
        $sums = Join-Path $tmp $sumsName

        $haveLocal = $LocalDir -and (Test-Path (Join-Path $LocalDir $name)) -and (Test-Path (Join-Path $LocalDir $sumsName))
        if ($haveLocal) {
            Write-Host '  Using the installer and checksums found next to this file.'
            Copy-Item (Join-Path $LocalDir $name) $setup
            Copy-Item (Join-Path $LocalDir $sumsName) $sums
        } else {
            Write-Host '  Downloading the latest FontA installer (about 75 MB). This can take a minute...'
            Invoke-WebRequest "$BaseUrl/$name" -OutFile $setup -UseBasicParsing
            try { Invoke-WebRequest "$BaseUrl/$sumsName" -OutFile $sums -UseBasicParsing } catch { Remove-Item $sums -ErrorAction SilentlyContinue }
        }

        # --- verify before running anything ---
        if (Test-Path $sums) {
            $expected = $null
            foreach ($line in Get-Content $sums) {
                if ($line -match '^([0-9a-fA-F]{64})\s+\*?(.+?)\s*$' -and $Matches[2] -eq $name) { $expected = $Matches[1].ToLower() }
            }
            if (-not $expected) {
                Write-Host "  The checksum list has no entry for $name, so the download can't be verified." -ForegroundColor Red
                return 4
            }
            $actual = (Get-FileHash $setup -Algorithm SHA256).Hash.ToLower()
            if ($actual -ne $expected) {
                Write-Host '  CHECKSUM MISMATCH: the installer does not match the published checksum.' -ForegroundColor Red
                Write-Host '  It may be corrupted or tampered with, so it was NOT run.' -ForegroundColor Red
                return 4
            }
            Write-Host '  Checksum verified (SHA-256).' -ForegroundColor Green
        } else {
            Write-Host '  No checksum file was available, so this download cannot be verified.' -ForegroundColor Yellow
            $answer = $null
            try { $answer = Read-Host '  Install anyway? Type YES to continue (anything else cancels)' } catch { }
            if ($answer -cne 'YES') { Write-Host '  Cancelled.'; return 4 }
        }

        if ($NoRun) { Write-Host '  (test mode: not running the installer)'; return 0 }

        if (Test-Path (Join-Path $env:LOCALAPPDATA 'FontAApp')) {
            Write-Host '  FontA is already installed here; this will update or repair it. Your library is kept.'
        }
        Write-Host '  Running the installer...'
        $p = Start-Process $setup -Wait -PassThru
        if ($p.ExitCode -ne 0) {
            Write-Host "  The installer reported exit code $($p.ExitCode)." -ForegroundColor Red
            return 5
        }
        Write-Host '  FontA is installed. Find it in the Start menu.' -ForegroundColor Green
        return 0
    }
    catch {
        Write-Host "  Something went wrong: $($_.Exception.Message)" -ForegroundColor Red
        return 1
    }
    finally {
        Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
    }
}
#ENDPOWERSHELL#
