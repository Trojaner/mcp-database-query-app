#Requires -Version 7.0
<#
.SYNOPSIS
    Builds Native AOT MCPB packages for MCP Database Query App.

.DESCRIPTION
    Produces a single-binary native executable per RID. Requires the platform
    linker (MSVC on Windows, clang+zlib on Linux, Xcode CLT on macOS) and cannot
    cross-compile between operating systems — run on each target host.
#>
param(
    [string[]]$RuntimeIdentifiers = @()
)

. (Join-Path $PSScriptRoot "_publish-common.ps1")

Write-Host "==> Building MCP Database Query App MCPB packages (Native AOT)..."

Invoke-McpbPublish `
    -RuntimeIdentifiers $RuntimeIdentifiers `
    -PublishArgs @("-p:PublishAot=true", "-p:SelfContained=true") `
    -PackageSuffix "-aot"
