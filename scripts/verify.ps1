<#
  Runs every automated check. Nothing here starts, moves or records a real application.
    1. Editor type check and tests, desktop build (scripts\build.ps1)
    2. Host unit and media tests (tests\Studio.Host.Tests)
    3. Desktop checks against the neutral test fixture, off-screen and without taking focus:
         --self-test      capture, development bridge, 60 fps wall-clock recording, render and MP4 encode
         --app-view-test  hosting, snap-back, Studio minimize, 3D switch, hide-to-tray, reconnect, release
  -SkipDesktop skips step 3.
#>
param([switch]$SkipDesktop)
$ErrorActionPreference = 'Stop'
$studioRoot = Split-Path -Parent $PSScriptRoot
Push-Location -LiteralPath $studioRoot
try {
    & (Join-Path $PSScriptRoot 'build.ps1')
    dotnet test tests/Studio.Host.Tests/Studio.Host.Tests.csproj -c Release -p:NuGetAudit=false
    if ($LASTEXITCODE -ne 0) { throw 'Host tests failed.' }
    if ($SkipDesktop) { Write-Host 'Desktop checks skipped.'; exit 0 }

    $fixtureOut = Join-Path $studioRoot 'artifacts\test\fixture'
    dotnet build tests/Studio.Fixture/Studio.Fixture.csproj -c Release -p:NuGetAudit=false -o $fixtureOut -v:q
    if ($LASTEXITCODE -ne 0) { throw 'Test fixture build failed.' }
    $fixture = Join-Path $fixtureOut 'Studio Test Fixture.exe'
    $studio = Join-Path $studioRoot 'artifacts\studio\LaunchReel Studio.exe'
    $failed = @()
    foreach ($check in @(@{ Flag = '--self-test'; Proof = 'proof' }, @{ Flag = '--app-view-test'; Proof = 'app-view-proof' })) {
        $data = Join-Path $studioRoot ('.studio-state\verify\' + $check.Flag.TrimStart('-'))
        if (Test-Path -LiteralPath $data) { Remove-Item -LiteralPath $data -Recurse -Force }
        $process = Start-Process -FilePath $studio -ArgumentList @('--data-root', "`"$data`"", $check.Flag, "`"$fixture`"") -PassThru
        if (-not $process.WaitForExit(180000)) { $process.Kill(); throw "$($check.Flag) timed out." }
        $report = Join-Path $data "$($check.Proof)\report.json"
        Write-Host "`n$($check.Flag) -> $report"
        if (Test-Path -LiteralPath $report) { Get-Content -LiteralPath $report -Raw | Write-Host } else { Write-Host 'No report was written.' }
        if ($process.ExitCode -ne 0) { $failed += $check.Flag }
    }
    if ($failed) { throw "Desktop checks failed: $($failed -join ', ')" }
    Write-Host "`nAll checks passed."
} finally { Pop-Location }
