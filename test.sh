#!/bin/bash
set -e
cd "$(dirname "$0")/Tests/CelestialLighting.Tests"
/home/deck/.dotnet10/dotnet test "$@"
