# Creates app data folder and copies DB if provided
param(
  [string]$DbSource = ""
)

$AppDir = Join-Path $env:APPDATA "MovieDb"
New-Item -ItemType Directory -Force -Path $AppDir | Out-Null

if ($DbSource -and (Test-Path $DbSource)) {
  Copy-Item $DbSource (Join-Path $AppDir "movies.db") -Force
  Write-Host "Copied DB to $AppDir"
} else {
  Write-Host "No DB copied. Place your movies.db into $AppDir"
}
