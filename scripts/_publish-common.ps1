#Requires -Version 7.0

# Shared helpers for publish-mcpb-*.ps1. Dot-source this from a wrapper that
# sets $PublishArgs (an array passed verbatim to `dotnet publish`) and, if
# desired, $PackageSuffix (appended to the .mcpb filename, e.g. "-aot").

function Invoke-McpbPublish {
    param(
        [AllowEmptyCollection()] [string[]]$RuntimeIdentifiers = @(),
        [Parameter(Mandatory)] [string[]]$PublishArgs,
        [string]$PackageSuffix = ""
    )

    $ErrorActionPreference = "Stop"

    $ScriptDir = $PSScriptRoot
    $RootDir = Split-Path $ScriptDir -Parent
    $Project = Join-Path $RootDir "src/McpDatabaseQueryApp.Server/McpDatabaseQueryApp.Server.csproj"
    $OutputDir = Join-Path $RootDir "artifacts/mcpb"
    $Manifest = Join-Path $RootDir "manifest.json"

    if (-not $RuntimeIdentifiers -or $RuntimeIdentifiers.Count -eq 0) {
        $hostRid = (dotnet --info | Select-String "RID:\s+(\S+)").Matches[0].Groups[1].Value
        if (-not $hostRid) {
            throw "Could not detect host RID from 'dotnet --info'."
        }
        $RuntimeIdentifiers = @($hostRid)
        Write-Host "No -RuntimeIdentifiers supplied; defaulting to host RID: $hostRid"
    }

    foreach ($rid in $RuntimeIdentifiers) {
        Write-Host ""
        Write-Host "--- Publishing for $rid ---"

        $ridDir = if ($PackageSuffix) { "$rid$PackageSuffix" } else { $rid }
        $publishDir = Join-Path $OutputDir $ridDir
        if (Test-Path $publishDir) {
            Remove-Item $publishDir -Recurse -Force -Confirm:$false
        }

        $allArgs = @("publish", $Project, "-c", "Release", "-r", $rid) + $PublishArgs + @("-o", (Join-Path $publishDir "server"))
        dotnet @allArgs
        if ($LASTEXITCODE -ne 0) {
            throw "dotnet publish failed for $rid"
        }

        $packageManifest = Join-Path $publishDir "manifest.json"
        Copy-Item $Manifest -Destination $packageManifest

        if ($rid -like "win-*") {
            $json = Get-Content $packageManifest -Raw | ConvertFrom-Json
            $json.server.entry_point = "server/mcp-database-query-app.exe"
            $json.server.mcp_config.command = '${__dirname}/server/mcp-database-query-app.exe'
            $json | ConvertTo-Json -Depth 20 | Set-Content $packageManifest -Encoding utf8
        }

        Write-Host "--- Packing $rid ---"
        $mcpb = Get-Command mcpb -ErrorAction SilentlyContinue
        if ($mcpb) {
            $mcpbFile = Join-Path $OutputDir "mcp-database-query-app-$ridDir.mcpb"
            mcpb pack $publishDir $mcpbFile
        } else {
            Write-Warning "mcpb CLI not found. Install with: npm install -g @anthropic-ai/mcpb"
            Write-Warning "Skipping pack step. Published files are in: $publishDir"
        }
    }

    Write-Host ""
    Write-Host "==> Done. Packages in: $OutputDir"
    Get-ChildItem (Join-Path $OutputDir "*.mcpb") -ErrorAction SilentlyContinue | Format-Table Name, Length
}
