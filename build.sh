#!/bin/bash
set -e
cd "$(dirname "$0")/Source"
FrameworkPathOverride=/usr/lib/mono/4.8-api /home/deck/.dotnet/dotnet build -c Release "$@"
