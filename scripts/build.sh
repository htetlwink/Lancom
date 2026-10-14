#!/usr/bin/env bash
set -e

DOTNET_CMD="dotnet"
if [ -f "$HOME/.dotnet/dotnet" ]; then
    DOTNET_CMD="$HOME/.dotnet/dotnet"
fi

echo "========================================================"
echo "         Building Lancom Solution on Linux (.NET 9)     "
echo "========================================================"

$DOTNET_CMD build Lancom.sln -c Release

echo "Publishing win-x64 Standalone Executables..."
$DOTNET_CMD publish src/Lancom.App/Lancom.App.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o ./dist/app
$DOTNET_CMD publish src/Lancom.Relay/Lancom.Relay.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o ./dist/relay

echo "Build and publish finished successfully!"
echo "Artifacts:"
ls -lh ./dist/app/Lancom.exe
ls -lh ./dist/relay/Lancom.Relay.exe
