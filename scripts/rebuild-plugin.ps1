# Rebuilds the civil3d-mcp Node server and the Civil 3D plugin after source changes.
# Run from anywhere:  powershell -NoProfile -ExecutionPolicy Bypass -File C:\Dev\Civil3D-mcp\scripts\rebuild-plugin.ps1
# Civil 3D must be CLOSED (the plugin DLL is locked while it is loaded).
param(
  [string] $Civil3DReferencesPath = ""   # default: ..\C_References next to the plugin project
)
$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$pluginDir = Join-Path $repoRoot "Civil3D-MCP-Plugin"

Write-Host "== Node server (TypeScript -> build/)" -ForegroundColor Cyan
Push-Location $repoRoot
try {
  & npm.cmd run build
  if ($LASTEXITCODE -ne 0) { throw "npm run build failed." }
} finally { Pop-Location }

Write-Host "== Civil 3D plugin (dotnet build Release)" -ForegroundColor Cyan
$dll = Join-Path $pluginDir "bin\Release\net8.0-windows\Civil3DMcpPlugin.dll"
$locked = $false
if (Test-Path $dll) {
  try { $s = [System.IO.File]::Open($dll, 'Open', 'ReadWrite', 'None'); $s.Close() } catch { $locked = $true }
}
if ($locked) { throw "Civil3DMcpPlugin.dll is in use - close Civil 3D first, then run this script again." }

Push-Location $pluginDir
try {
  $args = @("build", "-c", "Release")
  if ($Civil3DReferencesPath) { $args += "/p:Civil3DReferencesPath=$Civil3DReferencesPath" }
  & dotnet @args
  if ($LASTEXITCODE -ne 0) { throw "dotnet build failed." }
} finally { Pop-Location }

Write-Host ""
Write-Host "Build OK." -ForegroundColor Green
Write-Host "Plugin DLL: $dll"
Write-Host "Next: start Civil 3D, NETLOAD the DLL (or let the startup suite load it), then restart the Claude desktop app so the MCP server reloads build/."
