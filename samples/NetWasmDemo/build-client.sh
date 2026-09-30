#!/usr/bin/env bash
# Builds the NetWasm browser client into Client/publish/browser (served by the Server project).
# Takes ~4.5 minutes (NetWasm compile + wasm-opt) and produces a ~31 MB module.
set -euo pipefail
cd "$(dirname "$0")/Client"
dotnet publish -c Release -o publish "$@"
