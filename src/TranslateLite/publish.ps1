$ErrorActionPreference = "Stop"

Write-Host "=== TranslateLite Publish ===" -ForegroundColor Cyan
Write-Host ""

$projectDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoDir = Split-Path -Parent (Split-Path -Parent $projectDir)
$outputDir = Join-Path $repoDir "publish"

Write-Host "Publishing to: $outputDir" -ForegroundColor Yellow
Write-Host ""

dotnet publish $projectDir -c Release -p:PublishProfile=FolderProfile -o $outputDir
if ($LASTEXITCODE -ne 0) {
    Write-Host ""
    Write-Host "Publish failed with exit code $LASTEXITCODE" -ForegroundColor Red
    exit $LASTEXITCODE
}

# Version comes from csproj so the zip name, checksum files and release tag agree.
$csproj = Join-Path $projectDir "TranslateLite.csproj"
$version = (Select-Xml -Path $csproj -XPath "/Project/PropertyGroup/Version" | Select-Object -First 1).Node.InnerText.Trim()
if (-not $version) { throw "Cannot read <Version> from $csproj" }

$zipName = "LocalTrayTranslate-$version-win-x64.zip"
$zipPath = Join-Path $repoDir $zipName
$shaPath = "$zipPath.sha256"
$sumsPath = Join-Path $repoDir "SHA256SUMS.txt"

Add-Type -AssemblyName System.IO.Compression.FileSystem
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
# includeBaseDirectory = $false: the zip root holds the app files directly.
[System.IO.Compression.ZipFile]::CreateFromDirectory($outputDir, $zipPath, [System.IO.Compression.CompressionLevel]::Optimal, $false)

# Both update paths need checksums: the API path reads <zip>.sha256 (or the
# asset digest), the direct-link fallback reads SHA256SUMS.txt to learn the
# asset name and hash. LF line endings, no BOM.
$hash = (Get-FileHash -Path $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
$utf8 = [System.Text.UTF8Encoding]::new($false)
[System.IO.File]::WriteAllText($shaPath, "$hash  $zipName`n", $utf8)
[System.IO.File]::WriteAllText($sumsPath, "$hash  $zipName`n", $utf8)

Write-Host ""
Write-Host "Publish succeeded!" -ForegroundColor Green
Write-Host "Package:  $zipPath" -ForegroundColor Green
Write-Host "Checksums: $shaPath ; $sumsPath" -ForegroundColor Green
Write-Host ""
Write-Host "Upload all 3 files to the Release (both update paths need the checksums):"
Write-Host "  $zipName"
Write-Host "  $(Split-Path -Leaf $shaPath)"
Write-Host "  SHA256SUMS.txt"
