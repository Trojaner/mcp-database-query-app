#!/usr/bin/env bash
# Shared helpers for publish-mcpb*.sh wrappers. Wrappers set PUBLISH_ARGS
# (array passed verbatim to `dotnet publish`) and optionally PACKAGE_SUFFIX
# (appended to the .mcpb filename, e.g. "-aot"), then call mcpb_publish.

mcpb_publish() {
    set -euo pipefail

    local SCRIPT_DIR ROOT_DIR PROJECT OUTPUT_DIR MANIFEST
    SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[1]}")" && pwd)"
    ROOT_DIR="$(dirname "$SCRIPT_DIR")"
    PROJECT="$ROOT_DIR/src/McpDatabaseQueryApp.Server/McpDatabaseQueryApp.Server.csproj"
    OUTPUT_DIR="$ROOT_DIR/artifacts/mcpb"
    MANIFEST="$ROOT_DIR/manifest.json"

    if [ "${#RIDS[@]}" -eq 0 ]; then
        local HOST_RID
        HOST_RID="$(dotnet --info | awk -F': +' '/^ RID: /{print $2; exit}')"
        if [ -z "$HOST_RID" ]; then
            echo "ERROR: Could not detect host RID from 'dotnet --info'." >&2
            exit 1
        fi
        RIDS=("$HOST_RID")
        echo "No RIDs supplied; defaulting to host RID: $HOST_RID"
    fi

    local RID PUBLISH_DIR RID_DIR PACKAGE_MANIFEST
    for RID in "${RIDS[@]}"; do
        echo ""
        echo "--- Publishing for $RID ---"
        RID_DIR="${RID}${PACKAGE_SUFFIX:-}"
        PUBLISH_DIR="$OUTPUT_DIR/$RID_DIR"
        rm -rf "$PUBLISH_DIR"

        dotnet publish "$PROJECT" -c Release -r "$RID" "${PUBLISH_ARGS[@]}" -o "$PUBLISH_DIR/server"

        PACKAGE_MANIFEST="$PUBLISH_DIR/manifest.json"
        cp "$MANIFEST" "$PACKAGE_MANIFEST"

        case "$RID" in
            win-*)
                if command -v jq &> /dev/null; then
                    local tmp
                    tmp="$(mktemp)"
                    jq '.server.entry_point = "server/mcp-database-query-app.exe"
                        | .server.mcp_config.command = "${__dirname}/server/mcp-database-query-app.exe"' \
                        "$PACKAGE_MANIFEST" > "$tmp"
                    mv "$tmp" "$PACKAGE_MANIFEST"
                else
                    echo "WARN: jq not found; Windows manifest not patched with .exe suffix." >&2
                fi
                ;;
        esac

        echo "--- Packing $RID ---"
        if command -v mcpb &> /dev/null; then
            mcpb pack "$PUBLISH_DIR" "$OUTPUT_DIR/mcp-database-query-app-$RID_DIR.mcpb"
        else
            echo "WARN: mcpb CLI not found. Install with: npm install -g @anthropic-ai/mcpb"
            echo "      Skipping pack step. Published files are in: $PUBLISH_DIR"
        fi
    done

    echo ""
    echo "==> Done. Packages in: $OUTPUT_DIR"
    ls -la "$OUTPUT_DIR"/*.mcpb 2>/dev/null || echo "(No .mcpb files — install mcpb CLI to generate them)"
}
