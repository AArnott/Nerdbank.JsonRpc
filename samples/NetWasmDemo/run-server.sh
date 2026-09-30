#!/usr/bin/env bash
# Runs the ASP.NET Core server on http://localhost:5080 (static client files + /rpc WebSocket endpoint).
set -euo pipefail
cd "$(dirname "$0")/Server"
dotnet run --urls "${URLS:-http://0.0.0.0:5080}" "$@"
