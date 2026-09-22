param([switch]$Clipboard, [switch]$Media, [switch]$Benchmark, [switch]$InputTest, [switch]$LargeCode)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
dotnet build (Join-Path $projectRoot 'ClipboardPlus\ClipboardPlus.csproj') -c Release
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
$artifactRoot = Join-Path $projectRoot 'artifacts'
New-Item -ItemType Directory -Path $artifactRoot -Force | Out-Null
$reportPath = Join-Path $artifactRoot 'test-results.json'
$arguments = @('--self-test', '--report', ('"' + $reportPath + '"'))
if ($Clipboard -or $LargeCode) { $arguments += '--clipboard-test' }
if ($LargeCode) { $arguments += '--large-code-test' }
if ($InputTest) { $arguments += '--input-test' }
if ($Benchmark) { $arguments += '--benchmark' }
if ($Media) {
    $ffmpeg = Get-Command ffmpeg -ErrorAction Stop
    $fixtures = Join-Path $artifactRoot 'fixtures'
    New-Item -ItemType Directory -Path $fixtures -Force | Out-Null
    & $ffmpeg.Source -hide_banner -loglevel error -f lavfi -i 'sine=frequency=440:sample_rate=44100' -t 2 -y (Join-Path $fixtures 'preview.wav')
    if ($LASTEXITCODE -ne 0) { throw 'Audio fixture generation failed.' }
    & $ffmpeg.Source -hide_banner -loglevel error -f lavfi -i 'sine=frequency=440:sample_rate=44100' -t 4 -y (Join-Path $fixtures 'preview.mp3')
    if ($LASTEXITCODE -ne 0) { throw 'MP3 fixture generation failed.' }
    & $ffmpeg.Source -hide_banner -loglevel error -f lavfi -i 'testsrc2=size=640x360:rate=24' -t 2 -c:v h264_mf -pix_fmt yuv420p -y (Join-Path $fixtures 'preview.mp4')
    if ($LASTEXITCODE -ne 0) { throw 'Video fixture generation failed. Generate a two-second H.264 MP4 at artifacts/fixtures/preview.mp4 with an available encoder.' }
    $arguments += @('--media-test', ('"' + $fixtures + '"'))
}
$executable = Join-Path $projectRoot 'ClipboardPlus\bin\Release\net10.0-windows10.0.19041.0\ClipboardPlus.exe'
$windowMode = if ($InputTest) { 'Normal' } else { 'Hidden' }
$process = Start-Process -FilePath $executable -ArgumentList $arguments -WindowStyle $windowMode -PassThru
$process.WaitForExit()
$results = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
$results | Where-Object { $_.summary -or $_.benchmark } | ConvertTo-Json -Depth 5
Write-Output "Detailed report: $reportPath"
if ($process.ExitCode -ne 0) { throw "Test run failed (exit $($process.ExitCode))." }
