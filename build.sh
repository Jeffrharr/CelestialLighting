#!/bin/bash
set -e
cd "$(dirname "$0")/Source"
# Builds both game versions: net481 -> 1.6/Assemblies, net10.0 -> 1.7/Assemblies. Needs the .NET 10
# SDK, kept in its own ~/.dotnet10 rather than ~/.dotnet so it does not become the default SDK for
# every other mod's build. Pass `-f net481` or `-f net10.0` to build one version only.
FrameworkPathOverride=/usr/lib/mono/4.8-api /home/deck/.dotnet10/dotnet build -c Release "$@"
