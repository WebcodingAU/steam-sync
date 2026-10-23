# Builds Gaming Enjoyer and publishes it to the public releases repo that installed copies update from.
#
#   ./release.ps1                  version from <Version> in the csproj; builds, packs and uploads to everyone
#   ./release.ps1 -Channel beta    a test build for copies on the beta channel only: versioned <Version>-beta.N
#                                  (N = the next free number, or -Beta N) and published as a GitHub pre-release
#   ./release.ps1 -NoUpload        builds and packs only (artifacts\releases\GamingEnjoyer-win-Setup.exe)
#
# Channels: stable is Velopack's default "win" channel (releases.win.json); beta is "beta" (releases.beta.json, on
# pre-releases). Installed copies read one or the other (UpdateChannel in the app), so a beta never reaches anyone
# who hasn't switched to beta. Promote a beta by bumping nothing and running ./release.ps1 without -Channel.
#
# Needs the .NET 10 SDK, vpk (dotnet tool install -g vpk) and gh signed in to GitHub (its token does the upload).
#
# Code signing: set GAMING_ENJOYER_SIGN_THUMBPRINT (user env var) to the thumbprint of the code signing
# certificate. With Certum's cloud certificate, log in to SimplySign Desktop first so the certificate is in
# the store. vpk signs SteamSync.exe, Update.exe and Setup.exe with the signtool it ships. If the variable
# isn't set, the release is built unsigned.
param([switch]$NoUpload, [ValidateSet('stable', 'beta')][string]$Channel = 'stable', [int]$Beta = 0)
$ErrorActionPreference = 'Stop'

$csproj = Join-Path $PSScriptRoot 'src\SteamSync\SteamSync.csproj'
$version = ([xml](Get-Content $csproj)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
$repo = 'https://github.com/WebcodingAU/gaming-enjoyer-releases'
$publish = Join-Path $PSScriptRoot 'artifacts\publish'
$releases = Join-Path $PSScriptRoot 'artifacts\releases'
$env:Path += ";$env:USERPROFILE\.dotnet\tools"
$vpkChannel = if ($Channel -eq 'beta') { 'beta' } else { 'win' }
$token = if ($NoUpload) { $null } else { gh auth token }

if ($Channel -eq 'beta') {
    if ($Beta -le 0) {
        # The next beta number for this version, from the tags already published.
        $Beta = 1
        if ($token) {
            $tags = gh release list --repo WebcodingAU/gaming-enjoyer-releases --limit 100 --json tagName --jq '.[].tagName'
            $used = $tags | Where-Object { $_ -match "^v$([regex]::Escape($version))-beta\.(\d+)$" } | ForEach-Object { [int]$Matches[1] }
            if ($used) { $Beta = ($used | Measure-Object -Maximum).Maximum + 1 }
        }
    }
    $version = "$version-beta.$Beta"
}

$thumbprint = $env:GAMING_ENJOYER_SIGN_THUMBPRINT -replace '\s', ''
$sign = @()
if ($thumbprint) {
    # Check before building: SimplySign drops the certificate from the store when its session ends.
    if (-not (Test-Path "Cert:\CurrentUser\My\$thumbprint")) {
        throw "Signing certificate $thumbprint isn't in the store. Log in to SimplySign Desktop and try again."
    }
    $sign = '--signParams', "/sha1 $thumbprint /fd sha256 /tr http://time.certum.pl /td sha256"
} else {
    Write-Warning 'GAMING_ENJOYER_SIGN_THUMBPRINT is not set: this build is UNSIGNED.'
}

Write-Host "Gaming Enjoyer $version ($Channel channel)"
# Start clean: vpk refuses to pack a version that is already in the output folder (e.g. from a -NoUpload run),
# and the previous release is downloaded fresh below anyway.
foreach ($dir in $publish, $releases) { if (Test-Path $dir) { Remove-Item $dir -Recurse -Force } }
# The version goes into the exe too, so a beta build knows it is one (UpdateChannel.IsBetaBuild).
dotnet publish $csproj -c Release -r win-x64 --self-contained -o $publish -nologo -v q -p:Version=$version
if ($LASTEXITCODE) { throw 'dotnet publish failed' }

if ($token) {
    # The previous release on this channel lets vpk build a small delta package, so updates download only what changed.
    $pre = if ($Channel -eq 'beta') { @('--pre') } else { @() }
    vpk download github --repoUrl $repo --token $token -o $releases -c $vpkChannel @pre
    if ($LASTEXITCODE) { Write-Warning 'No previous release downloaded (first release?); this one ships full only.' }
}

vpk pack -u GamingEnjoyer -v $version -c $vpkChannel -r win-x64 -p $publish -e SteamSync.exe -o $releases `
    --packTitle 'Gaming Enjoyer' --packAuthors 'gamingenjoyer.com' `
    -i (Join-Path $PSScriptRoot 'src\SteamSync\app.ico') `
    --framework webview2 --shortcuts StartMenuRoot @sign
if ($LASTEXITCODE) { throw 'vpk pack failed' }

if ($thumbprint) {
    # Never publish a release that claims to be signed but isn't: installed copies would update to it.
    $setup = Get-AuthenticodeSignature (Join-Path $releases "GamingEnjoyer-$vpkChannel-Setup.exe")
    if ($setup.Status -ne 'Valid') { throw "Setup.exe signature is $($setup.Status): $($setup.StatusMessage)" }
    Write-Host "Signed by $($setup.SignerCertificate.Subject)"
}

if ($token) {
    vpk upload github --repoUrl $repo --token $token -o $releases -c $vpkChannel --publish @pre `
        --releaseName "Gaming Enjoyer $version" --tag "v$version"
    if ($LASTEXITCODE) { throw 'vpk upload failed' }
    Write-Host "Published: $repo/releases/tag/v$version"
}
