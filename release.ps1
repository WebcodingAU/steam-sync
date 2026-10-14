# Builds Gaming Enjoyer and publishes it to the public releases repo that installed copies update from.
#
#   ./release.ps1              version from <Version> in the csproj; builds, packs and uploads
#   ./release.ps1 -NoUpload    builds and packs only (artifacts\releases\GamingEnjoyer-win-Setup.exe)
#
# Needs the .NET 10 SDK, vpk (dotnet tool install -g vpk) and gh signed in to GitHub (its token does the upload).
param([switch]$NoUpload)
$ErrorActionPreference = 'Stop'

$csproj = Join-Path $PSScriptRoot 'src\SteamSync\SteamSync.csproj'
$version = ([xml](Get-Content $csproj)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
$repo = 'https://github.com/WebcodingAU/gaming-enjoyer-releases'
$publish = Join-Path $PSScriptRoot 'artifacts\publish'
$releases = Join-Path $PSScriptRoot 'artifacts\releases'
$env:Path += ";$env:USERPROFILE\.dotnet\tools"

Write-Host "Gaming Enjoyer $version"
# Start clean: vpk refuses to pack a version that is already in the output folder (e.g. from a -NoUpload run),
# and the previous release is downloaded fresh below anyway.
foreach ($dir in $publish, $releases) { if (Test-Path $dir) { Remove-Item $dir -Recurse -Force } }
dotnet publish $csproj -c Release -r win-x64 --self-contained -o $publish -nologo -v q
if ($LASTEXITCODE) { throw 'dotnet publish failed' }

$token = if ($NoUpload) { $null } else { gh auth token }
if ($token) {
    # The previous release lets vpk build a small delta package, so updates download only what changed.
    vpk download github --repoUrl $repo --token $token -o $releases -c win
    if ($LASTEXITCODE) { Write-Warning 'No previous release downloaded (first release?); this one ships full only.' }
}

vpk pack -u GamingEnjoyer -v $version -r win-x64 -p $publish -e SteamSync.exe -o $releases `
    --packTitle 'Gaming Enjoyer' --packAuthors 'gamingenjoyer.com' `
    -i (Join-Path $PSScriptRoot 'src\SteamSync\app.ico') `
    --framework webview2 --shortcuts StartMenuRoot
if ($LASTEXITCODE) { throw 'vpk pack failed' }

if ($token) {
    vpk upload github --repoUrl $repo --token $token -o $releases --publish --releaseName "Gaming Enjoyer $version" --tag "v$version"
    if ($LASTEXITCODE) { throw 'vpk upload failed' }
    Write-Host "Published: $repo/releases/tag/v$version"
}
