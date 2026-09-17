param([string]$Runtime = 'win-x64', [string]$OutputRoot)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
if (-not $OutputRoot) { $OutputRoot = Join-Path $projectRoot 'artifacts' }
$publishRoot = Join-Path $OutputRoot "ClipboardPlus-$Runtime"
dotnet publish (Join-Path $projectRoot 'ClipboardPlus\ClipboardPlus.csproj') -c Release -r $Runtime --self-contained true -p:PublishSingleFile=false -o $publishRoot
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
Copy-Item -LiteralPath (Join-Path $projectRoot 'README.md') -Destination $publishRoot
foreach ($name in @('LICENSE', 'CHANGELOG.md', 'CONTRIBUTING.md', 'SECURITY.md', 'THIRD_PARTY_NOTICES.md')) { Copy-Item -LiteralPath (Join-Path $projectRoot $name) -Destination $publishRoot }
Copy-Item -LiteralPath (Join-Path $projectRoot 'licenses') -Destination $publishRoot -Recurse -Force
$releaseDocs = Join-Path $publishRoot 'docs'
New-Item -ItemType Directory -Path $releaseDocs -Force | Out-Null
Copy-Item -Path (Join-Path $projectRoot 'docs\*.md') -Destination $releaseDocs
Copy-Item -Path (Join-Path $projectRoot 'docs\*.json') -Destination $releaseDocs
Copy-Item -LiteralPath (Join-Path $projectRoot 'docs\images') -Destination $releaseDocs -Recurse -Force
$legacyVerification = Join-Path $publishRoot 'VERIFICATION.md'
if (Test-Path -LiteralPath $legacyVerification) { Remove-Item -LiteralPath $legacyVerification }
$archivePath = Join-Path $OutputRoot "ClipboardPlus-$Runtime.zip"
Compress-Archive -Path (Join-Path $publishRoot '*') -DestinationPath $archivePath -Force
Get-FileHash -LiteralPath $archivePath -Algorithm SHA256 | Format-List
Write-Output "Release: $publishRoot"
Write-Output "Archive: $archivePath"
