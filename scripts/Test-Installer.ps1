param([string]$Version = '1.1.1')
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$releaseRoot = Join-Path $projectRoot "artifacts\releases\v$Version"
$setup = Join-Path $releaseRoot "installer-test\ClipboardPlus-Setup-$Version-win-x64.exe"
$testRoot = [IO.Path]::GetFullPath((Join-Path $projectRoot ('artifacts\installer-smoke-' + [Guid]::NewGuid().ToString('N'))))
$workspaceArtifacts = [IO.Path]::GetFullPath((Join-Path $projectRoot 'artifacts')) + [IO.Path]::DirectorySeparatorChar
if (-not $testRoot.StartsWith($workspaceArtifacts, [StringComparison]::OrdinalIgnoreCase)) { throw 'Test installation must stay inside workspace artifacts.' }
New-Item -ItemType Directory -Path $testRoot -Force | Out-Null
$installDir = Join-Path $testRoot 'app'
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$uninstallKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{570517AD-D3FC-49F0-B772-495E88AE9E19}_is1'
$startupBefore = (Get-ItemProperty -LiteralPath $runKey -Name ClipboardPlus -ErrorAction SilentlyContinue).ClipboardPlus
$startLink = Join-Path ([Environment]::GetFolderPath('Programs')) 'Clipboard Plus Installer Test.lnk'
$desktopLink = Join-Path ([Environment]::GetFolderPath('DesktopDirectory')) 'Clipboard Plus Installer Test.lnk'
function Install-Probe([string]$logName) {
    $arguments = @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/SP-', ('/DIR="' + $installDir + '"'), '/TASKS="startup,desktopicon"', ('/LOG="' + (Join-Path $testRoot $logName) + '"'))
    $job = Start-Process -FilePath $setup -ArgumentList $arguments -WindowStyle Hidden -PassThru
    $job.WaitForExit()
    if ($job.ExitCode -ne 0) { throw "Installer failed: $($job.ExitCode)" }
}
Install-Probe 'install.log'
$installedExe = Join-Path $installDir 'ClipboardPlus.exe'
if (-not (Test-Path -LiteralPath $installedExe) -or -not (Test-Path -LiteralPath $startLink) -or -not (Test-Path -LiteralPath $desktopLink)) { throw 'Installer did not create application and shortcuts.' }
$startup = (Get-ItemProperty -LiteralPath $runKey -Name ClipboardPlusInstallerTest).ClipboardPlusInstallerTest
if ($startup -ne ('"' + $installedExe + '" --background')) { throw 'Installer startup command is incorrect.' }
if ((Get-ItemProperty -LiteralPath $uninstallKey).DisplayVersion -ne $Version) { throw 'Windows Apps registration is missing or incorrect.' }
Set-Content -LiteralPath (Join-Path $installDir 'keep-me.txt') -Value 'User-created file must survive upgrade and uninstall.'
Install-Probe 'upgrade.log'
if (-not (Test-Path -LiteralPath (Join-Path $installDir 'keep-me.txt'))) { throw 'Upgrade removed a user-created file.' }
$appCheck = Start-Process -FilePath $installedExe -ArgumentList @('--self-test', '--report', ('"' + (Join-Path $testRoot 'app-test-results.json') + '"')) -WindowStyle Hidden -PassThru
$appCheck.WaitForExit()
if ($appCheck.ExitCode -ne 0) { throw 'Installed application regression test failed.' }
$uninstaller = Join-Path $installDir 'unins000.exe'
$removeJob = Start-Process -FilePath $uninstaller -ArgumentList @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', ('/LOG="' + (Join-Path $testRoot 'uninstall.log') + '"')) -WindowStyle Hidden -PassThru
$removeJob.WaitForExit()
if ($removeJob.ExitCode -ne 0) { throw 'Uninstaller failed.' }
for ($i = 0; $i -lt 100 -and (Test-Path -LiteralPath $installedExe); $i++) { Start-Sleep -Milliseconds 200 }
if ((Test-Path -LiteralPath $installedExe) -or (Test-Path -LiteralPath $startLink) -or (Test-Path -LiteralPath $desktopLink) -or (Test-Path -LiteralPath $uninstallKey)) { throw 'Uninstall left installed application/shortcuts/registration.' }
if ((Get-ItemProperty -LiteralPath $runKey -Name ClipboardPlusInstallerTest -ErrorAction SilentlyContinue).ClipboardPlusInstallerTest) { throw 'Uninstall left the test startup entry.' }
if (-not (Test-Path -LiteralPath (Join-Path $installDir 'keep-me.txt'))) { throw 'Uninstall removed a user-created file.' }
$startupAfter = (Get-ItemProperty -LiteralPath $runKey -Name ClipboardPlus -ErrorAction SilentlyContinue).ClipboardPlus
if ($startupBefore -ne $startupAfter) { throw 'Installer test changed the real app startup preference.' }
$result = [pscustomobject]@{ result='passed'; checks=@('Per-user installation','Desktop and Start menu shortcuts','Optional startup command','Windows Apps registration','Upgrade preserves user-created files','Installed application core tests','Uninstall removes installed files, shortcuts and registry entries','Uninstall preserves user-created files','Real app startup preference unchanged'); logDirectory=$testRoot }
$result | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath (Join-Path $projectRoot 'artifacts\installer-test-results.json')
$result | ConvertTo-Json -Depth 3
