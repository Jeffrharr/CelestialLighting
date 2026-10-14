#!/bin/bash
set -e
cd "$(dirname "$0")"
/home/deck/.dotnet10/dotnet build -c Release "$@"
