param([string]$Version = '1.1.0', [string]$Compiler = "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", [switch]$InstallerTest)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$releaseRoot = Join-Path $projectRoot "artifacts\releases\v$Version"
$payload = Join-Path $releaseRoot 'ClipboardPlus-win-x64'
if (-not (Test-Path -LiteralPath $Compiler)) { throw 'Install Inno Setup 6, or pass -Compiler with the path to ISCC.exe.' }
if (-not (Test-Path -LiteralPath (Join-Path $payload 'ClipboardPlus.exe'))) { throw 'Publish the self-contained application to the versioned release folder first.' }
$outputRoot = if ($InstallerTest) { Join-Path $releaseRoot 'installer-test' } else { $releaseRoot }
New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null
$compilerArguments = @('/Qp', "/DAppVersion=$Version", "/DPayloadDir=$payload", "/O$outputRoot")
if ($InstallerTest) { $compilerArguments += '/DInstallerTest=1' }
$compilerArguments += Join-Path $projectRoot 'installer\ClipboardPlus.iss'
& $Compiler @compilerArguments
if ($LASTEXITCODE -ne 0) { throw 'Installer compilation failed.' }
Get-Item -LiteralPath (Join-Path $outputRoot "ClipboardPlus-Setup-$Version-win-x64.exe") | Select-Object FullName,Length
