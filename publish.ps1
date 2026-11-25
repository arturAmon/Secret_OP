# Builds everything to share into .\dist:
#   SecretOP-Setup-vX.Y.Z.exe   the installer to send to friends (includes your popular builds)
#   SecretOP-vX.Y.Z.zip         the same app without installing (exe + README)
# Usage:  powershell -ExecutionPolicy Bypass -File .\publish.ps1 [-NoBuilds] [-Release]
#   -NoBuilds   leave your collected popular builds out of the installer
#   -Release    also publish it on GitHub as release vX.Y.Z (bump <Version> in LeagueOverlay.csproj and commit first)
# Needs Inno Setup 6 for the installer (winget install JRSoftware.InnoSetup --scope user); without it only the zip is made.
param([switch]$NoBuilds, [switch]$Release)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

dotnet test LeagueOverlay.Tests --nologo -v q --filter "Category!=RealData"
if ($LASTEXITCODE -ne 0) { throw "Tests failed; not publishing." }

if (Test-Path dist) { Remove-Item dist -Recurse -Force }
# DebugType=none as a global property also covers LeagueOverlay.Core, so no .pdb files end up next to the exe.
dotnet publish LeagueOverlay -p:PublishProfile=Share -p:DebugType=none --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "Publish failed." }
Copy-Item README.txt dist\README.txt
$version = (Get-Item dist\SecretOP.exe).VersionInfo.ProductVersion -replace '\+.*$', ''

# Portable zip (no builds inside: friends can import a builds file from Settings instead).
Compress-Archive -Path dist\SecretOP.exe, dist\README.txt -DestinationPath "dist\SecretOP-v$version.zip"

# Your collected popular builds, for the installer.
$isccArgs = @("/Q", "/DAppVersion=$version")
if (-not $NoBuilds) {
    $p = Start-Process dist\SecretOP.exe -ArgumentList '--export-builds', "`"$PWD\dist\PopularBuilds.lobuilds`"" -Wait -PassThru
    if ($p.ExitCode -eq 0 -and (Test-Path dist\PopularBuilds.lobuilds)) { $isccArgs += "/DWithBuilds" }
    else { Write-Warning "Couldn't export your builds; the installer won't include them." }
}

$iscc = @("$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe", "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe") |
    Where-Object { Test-Path $_ } | Select-Object -First 1
if ($iscc) {
    & $iscc @isccArgs installer\LeagueOverlay.iss
    if ($LASTEXITCODE -ne 0) { throw "Installer build failed." }
} else {
    Write-Warning "Inno Setup not found, so no installer was built (winget install JRSoftware.InnoSetup --scope user)."
}

if ($Release) {
    # Publish to GitHub: tag vX.Y.Z on the current commit, attach installer and zip, mark as latest.
    $gh = (Get-Command gh -ErrorAction SilentlyContinue).Source
    if (-not $gh) { $gh = "$env:ProgramFiles\GitHub CLI\gh.exe" }
    if (git status --porcelain) { throw "Commit your changes before releasing, so the release matches the code on GitHub." }
    git push -q
    & $gh release create "v$version" "dist\SecretOP-Setup-v$version.exe" "dist\SecretOP-v$version.zip" `
        --title "Secret OP $version" --generate-notes --latest
    if ($LASTEXITCODE -ne 0) { throw "GitHub release failed." }
}

Get-ChildItem dist -File | Format-Table Name, @{ n = 'Size (MB)'; e = { [math]::Round($_.Length / 1MB, 1) } } -AutoSize
"Send dist\SecretOP-Setup-v$version.exe to friends. Your API key, settings and saved games are not in it" +
    $(if ($isccArgs -contains '/DWithBuilds') { "; your collected popular builds are." } else { "." })
