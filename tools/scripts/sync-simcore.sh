#!/usr/bin/env bash
# Equivalente en bash de sync-simcore.ps1.
set -euo pipefail
root="$(cd "$(dirname "$0")/../.." && pwd)"
dotnet build "$root/sim/Sim.Core/Sim.Core.csproj" -c Release --nologo -v q
dest="$root/unity/URBE/Assets/Plugins/SimCore"
mkdir -p "$dest"
cp "$root/sim/Sim.Core/bin/Release/netstandard2.1/Urbe.Sim.Core.dll" "$dest/"
echo "Copiada Urbe.Sim.Core.dll a $dest"
