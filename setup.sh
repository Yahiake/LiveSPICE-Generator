#!/usr/bin/env bash
# setup.sh - Setup script for LiveSPICE-Generator
set -e

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
THIRD_PARTY_DIR="$SCRIPT_DIR/third_party"
LIVESPICE_DIR="$THIRD_PARTY_DIR/LiveSPICE"
SIBLING_LIVESPICE="$(dirname "$SCRIPT_DIR")/LiveSPICE"
LIVESPICE_REPO="${1:-https://github.com/dsharlet/LiveSPICE.git}"

echo "=== Setting up LiveSPICE-Generator ==="

RESOLVED_LIVESPICE=""

if [ -f "$LIVESPICE_DIR/Circuit/Circuit.csproj" ]; then
    echo "Found LiveSPICE at $LIVESPICE_DIR"
    RESOLVED_LIVESPICE="$LIVESPICE_DIR"
elif [ -f "$SIBLING_LIVESPICE/Circuit/Circuit.csproj" ]; then
    echo "Found sibling LiveSPICE at $SIBLING_LIVESPICE"
    RESOLVED_LIVESPICE="$SIBLING_LIVESPICE"
else
    echo "LiveSPICE not found. Cloning recursively from $LIVESPICE_REPO..."
    mkdir -p "$THIRD_PARTY_DIR"
    git clone --recurse-submodules --depth 1 "$LIVESPICE_REPO" "$LIVESPICE_DIR"
    echo "Successfully cloned LiveSPICE into $LIVESPICE_DIR"
    RESOLVED_LIVESPICE="$LIVESPICE_DIR"
fi

CA_PATH="$RESOLVED_LIVESPICE/ComputerAlgebra/ComputerAlgebra/ComputerAlgebra.csproj"
if [ ! -f "$CA_PATH" ]; then
    echo "ComputerAlgebra submodule missing. Initializing submodules..."
    git -C "$RESOLVED_LIVESPICE" submodule update --init --recursive
    if [ ! -f "$CA_PATH" ]; then
        echo "Failed to initialize ComputerAlgebra submodule at $CA_PATH"
        exit 1
    fi
    echo "Successfully initialized submodules."
fi

echo "Building LiveSPICE-Generator..."
dotnet build "$SCRIPT_DIR/LiveSPICE-Generator.sln"
echo "Build succeeded! You can now run livespice-gen."
