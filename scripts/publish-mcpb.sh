#!/usr/bin/env bash
# Builds Native AOT MCPB packages for MCP Database Query App.
#
# Produces a single-binary native executable per RID. Requires the platform
# linker (MSVC on Windows, clang+zlib on Linux, Xcode CLT on macOS) and cannot
# cross-compile between operating systems — run on each target host.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=./_publish-common.sh
source "$SCRIPT_DIR/_publish-common.sh"

RIDS=("$@")
PUBLISH_ARGS=("-p:PublishAot=true" "-p:SelfContained=true")
PACKAGE_SUFFIX="-aot"

echo "==> Building MCP Database Query App MCPB packages (Native AOT)..."
mcpb_publish
