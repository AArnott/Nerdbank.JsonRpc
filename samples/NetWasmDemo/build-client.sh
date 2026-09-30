#!/usr/bin/env bash
# Builds the NetWasm browser client into Client/publish/browser (served by the Server project).
# Pass -p:NetWasmOptimization=O1 for a much faster (but larger) dev build than the default Oz.
set -euo pipefail
cd "$(dirname "$0")/Client"
dotnet publish -c Release -o publish "$@"
