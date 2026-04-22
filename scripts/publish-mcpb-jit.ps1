#Requires -Version 7.0
<#
.SYNOPSIS
    Builds JIT (non-AOT) self-contained single-file MCPB packages for MCP Database Query App.

.DESCRIPTION
    Uses the standard .NET runtime bundled as a self-extracting single file.
    Larger binary and slower startup than AOT, but cross-compiles to any RID
    from any host and needs no platform linker — useful for CI and fallback.
#>
param(
    [string[]]$RuntimeIdentifiers = @()
)

. (Join-Path $PSScriptRoot "_publish-common.ps1")

Write-Host "==> Building MCP Database Query App MCPB packages (JIT, self-contained single-file)..."

Invoke-McpbPublish `
    -RuntimeIdentifiers $RuntimeIdentifiers `
    -PublishArgs @(
        "--self-contained", "true",
        "-p:PublishAot=false",
        "-p:PublishSingleFile=true",
        "-p:PublishTrimmed=false",
        "-p:IncludeNativeLibrariesForSelfExtract=true",
        "-p:EnableCompressionInSingleFile=true",
        "-p:DebugType=none",
        "-p:DebugSymbols=false",
        "-p:PublishReadyToRun=false"
    ) `
    -PackageSuffix "-jit"
