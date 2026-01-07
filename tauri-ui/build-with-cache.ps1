$env:WIX = (Resolve-Path "wix314").Path
$env:NSIS_HOME = (Resolve-Path "nsis-3.11\*").Path
Write-Host "WIX=$env:WIX"
Write-Host "NSIS_HOME=$env:NSIS_HOME"
npx tauri build --bundles msi