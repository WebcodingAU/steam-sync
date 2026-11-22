# AdVoid's nightly check: is ad blocking still working on YouTube and Twitch? Runs the unit tests, the HTTP checks
# (tools/LiveCheck) and the real-window checks (Gaming Enjoyer's tools/WatchCheck, which uses the AdVoid copy that
# ships in steam-sync/advoid), and writes every report plus summary.json to .nightly\<date>\ (git-ignored).
#
#   ./tools/nightly-check.ps1 [-SteamSync C:\Repos\steam-sync] [-TwitchSeconds 180] [-YouTubeSeconds 40]
#
# Exit code 0 when nothing is wrong, 1 when something is (summary.json says what). A check that saw no ad is not a
# failure; it's reported as "no-ad-seen" / "inconclusive".
param([string]$SteamSync = 'C:\Repos\steam-sync', [int]$TwitchSeconds = 180, [int]$YouTubeSeconds = 40)
$ErrorActionPreference = 'Continue'
$root = Split-Path $PSScriptRoot -Parent
$out = Join-Path $root ".nightly\$(Get-Date -Format 'yyyy-MM-dd_HHmm')"
New-Item -ItemType Directory -Force $out | Out-Null
$summary = [ordered]@{ at = (Get-Date).ToString('o'); out = $out; checks = [ordered]@{} }

function Save-Check($name, $file, $code) {
    $result = switch ($code) { 0 { 'ok' } 2 { 'no-ad-seen' } default { 'problems' } }
    $problems = @()
    try { $problems = @((Get-Content $file -Raw | ConvertFrom-Json).problems) } catch { $problems = @("no report (exit $code)") }
    $summary.checks[$name] = [ordered]@{ result = $result; problems = $problems; report = $file }
    Write-Host ("{0,-18} {1}{2}" -f $name, $result, $(if ($problems) { ': ' + ($problems -join ' | ') } else { '' }))
}

# 1. Unit tests (the AdVoid in this repo).
$tests = Join-Path $out 'tests.txt'
dotnet test (Join-Path $root 'AdVoid.slnx') -nologo *> $tests
$summary.checks['unit tests'] = [ordered]@{ result = $(if ($LASTEXITCODE) { 'problems' } else { 'ok' }); report = $tests;
    problems = @(if ($LASTEXITCODE) { (Select-String -Path $tests -Pattern 'Failed |error ' | Select-Object -First 10 | ForEach-Object { $_.Line.Trim() }) }) }
Write-Host ("{0,-18} {1}" -f 'unit tests', $summary.checks['unit tests'].result)

# 2. Over HTTP: what the sites send now, compared with what AdVoid expects.
$liveCheck = Join-Path $root 'tools\LiveCheck'
dotnet build $liveCheck -nologo -v q | Out-Null
$yt = Join-Path $out 'livecheck-youtube.json'
dotnet run --project $liveCheck --no-build -- youtube > $yt; Save-Check 'youtube data' $yt $LASTEXITCODE
$tw = Join-Path $out 'livecheck-twitch.json'
dotnet run --project $liveCheck --no-build -- twitch $TwitchSeconds > $tw; Save-Check 'twitch playlists' $tw $LASTEXITCODE

# 3. In the app's own window: an ad on screen, a stall or a black picture. The Twitch channel is the one where an ad
#    was just seen (new viewers often get one when they open the stream), else the top ungated channel.
$watch = Join-Path $SteamSync 'tools\WatchCheck'
dotnet build $watch -nologo -v q | Out-Null
$exe = Join-Path $watch 'bin\Debug\net10.0-windows\WatchCheck.exe'
$wy = Join-Path $out 'watch-youtube.json'
& $exe youtube $YouTubeSeconds > $wy; Save-Check 'youtube window' $wy $LASTEXITCODE
$channel = try { $j = Get-Content $tw -Raw | ConvertFrom-Json; if ($j.adBreak.channel) { $j.adBreak.channel } else { $j.masters[0].channel } } catch { $null }
if ($channel) {
    $wt = Join-Path $out 'watch-twitch.json'
    & $exe twitch $channel $TwitchSeconds > $wt; Save-Check 'twitch window' $wt $LASTEXITCODE
} else {
    $summary.checks['twitch window'] = [ordered]@{ result = 'problems'; problems = @('no channel to watch (the Twitch HTTP check found none)') }
}

$bad = @($summary.checks.Values | Where-Object { $_.result -eq 'problems' })
$summary.result = if ($bad) { 'problems' } else { 'ok' }
$summary | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $out 'summary.json') -Encoding utf8
Write-Host "overall: $($summary.result)  ($out)"
exit $(if ($bad) { 1 } else { 0 })
