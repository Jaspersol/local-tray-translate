Write-Host "=== TranslateLite Publish ===" -ForegroundColor Cyan
Write-Host ""

$projectDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$outputDir = Join-Path (Split-Path -Parent (Split-Path -Parent $projectDir)) "publish"

Write-Host "Publishing to: $outputDir" -ForegroundColor Yellow
Write-Host ""

dotnet publish $projectDir -c Release -p:PublishProfile=FolderProfile -o $outputDir

if ($LASTEXITCODE -eq 0) {
    Write-Host ""
    Write-Host "Publish succeeded!" -ForegroundColor Green
    Write-Host "Output: $outputDir" -ForegroundColor Green
    Write-Host "Executable: $outputDir\TranslateLite.exe" -ForegroundColor Green
    Write-Host ""
    Write-Host "You can distribute the entire 'publish' folder as a portable application."
    Write-Host "To create an installer, use tools like Inno Setup or WiX Toolset."
} else {
    Write-Host ""
    Write-Host "Publish failed with exit code $LASTEXITCODE" -ForegroundColor Red
}
