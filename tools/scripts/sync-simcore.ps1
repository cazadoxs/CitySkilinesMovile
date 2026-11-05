# Compila Sim.Core (netstandard2.1) y copia Urbe.Sim.Core.dll al proyecto de Unity.
# Uso: powershell -File tools/scripts/sync-simcore.ps1
# La DLL se commitea para que el proyecto abra sin pasos previos (M2-01).
$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$proj = Join-Path $root 'sim\Sim.Core\Sim.Core.csproj'
$dest = Join-Path $root 'unity\URBE\Assets\Plugins\SimCore'
dotnet build $proj -c Release --nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'dotnet build fallo' }
$out = Join-Path $root 'sim\Sim.Core\bin\Release\netstandard2.1'
New-Item -ItemType Directory -Force $dest | Out-Null
Copy-Item (Join-Path $out 'Urbe.Sim.Core.dll') $dest -Force
Write-Host "Copiada Urbe.Sim.Core.dll a $dest"
