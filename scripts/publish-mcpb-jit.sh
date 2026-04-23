#!/usr/bin/env bash
# Builds JIT (non-AOT) self-contained single-file MCPB packages for MCP Database Query App.
#
# Uses the standard .NET runtime bundled as a self-extracting single file.
# Larger binary and slower startup than AOT, but cross-compiles to any RID
# from any host and needs no platform linker — useful for CI and fallback.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=./_publish-common.sh
source "$SCRIPT_DIR/_publish-common.sh"

RIDS=("$@")
PUBLISH_ARGS=(
    "--self-contained" "true"
    "-p:PublishAot=false"
    "-p:PublishSingleFile=true"
    "-p:PublishTrimmed=false"
    "-p:IncludeNativeLibrariesForSelfExtract=true"
    "-p:EnableCompressionInSingleFile=true"
    "-p:DebugType=none"
    "-p:DebugSymbols=false"
    "-p:PublishReadyToRun=false"
)
PACKAGE_SUFFIX="-jit"

echo "==> Building MCP Database Query App MCPB packages (JIT, self-contained single-file)..."
mcpb_publish
