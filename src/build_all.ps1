# build_all.ps1
# Run all build scripts for the LingoFuse project in order.
#
# Order:
#   1. build_llm_service.ps1        (Python / LLM service)
#   2. build_mcp_api_tool.ps1       (Python / MCP API tool)
#   3. build_bridge.ps1             (Python / lingofuse bridge)
#   4. csharp_solved\build.ps1      (C# solution)

$ErrorActionPreference = 'Stop'

# Resolve the script's own directory and always run from there.
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location -LiteralPath $scriptDir

# -------------------------------------------------------------------------
# Task list
#   Type  : 'ps1' or 'bat'
#   Path  : relative to $scriptDir
#   Name  : display name (optional, defaults to Path)
# -------------------------------------------------------------------------
$tasks = @(
    @{ Type = 'ps1'; Path = 'build_llm_service.ps1';   Name = 'build_llm_service.ps1'   }
    @{ Type = 'ps1'; Path = 'build_mcp_api_tool.ps1';  Name = 'build_mcp_api_tool.ps1'  }
    @{ Type = 'ps1'; Path = 'build_bridge.ps1';        Name = 'build_bridge.ps1'        }
    @{ Type = 'ps1'; Path = 'csharp_solved\build.ps1'; Name = 'csharp_solved\build.ps1' }
)

$total = $tasks.Count
$index = 0
$sw    = [System.Diagnostics.Stopwatch]::StartNew()

foreach ($t in $tasks) {
    $index++
    $display = if ($t.Name) { $t.Name } else { $t.Path }
    $full    = Join-Path $scriptDir $t.Path

    Write-Host ""
    Write-Host ("=== [{0}/{1}] {2} ===" -f $index, $total, $display) -ForegroundColor Cyan

    # --- Existence check ------------------------------------------------
    if (-not (Test-Path -LiteralPath $full -PathType Leaf)) {
        Write-Host "SKIP: file not found - $full" -ForegroundColor Yellow
        continue
    }

    $itemSw = [System.Diagnostics.Stopwatch]::StartNew()

    # --- Execute --------------------------------------------------------
    # Run inside its own directory so relative paths inside the child
    # script resolve correctly, then restore the original location.
    Push-Location -LiteralPath (Split-Path -Parent $full)
    try {
        if ($t.Type -eq 'ps1') {
            # Run ps1 in the current session (keeps env vars, cwd, etc.)
            & $full
        }
        else {
            # bat / cmd scripts must be run via cmd.exe
            & cmd.exe /c "`"$full`""
            # Propagate the child exit code to $LASTEXITCODE for ps1 tasks
            # is not needed; cmd /c already sets it.
        }
    }
    finally {
        Pop-Location
    }

    $code = $LASTEXITCODE
    $itemSw.Stop()
    $secs = '{0:N1}' -f $itemSw.Elapsed.TotalSeconds

    # --- Result ---------------------------------------------------------
    if ($code -ne 0 -and $null -ne $code) {
        Write-Host ("FAIL: {0} exit code={1}, {2} s" -f $display, $code, $secs) -ForegroundColor Red
        throw "Build failed: $display"
    }
    else {
        Write-Host ("OK:   {0} done in {1} s" -f $display, $secs) -ForegroundColor Green
    }
}

$sw.Stop()
$totalSecs = '{0:N1}' -f $sw.Elapsed.TotalSeconds
Write-Host ""
Write-Host "=== All builds done in $totalSecs s ===" -ForegroundColor Green