#Requires -Version 5.1
<#
.SYNOPSIS
    Clean build artifacts and temporary files for the LingoFuse project.

.DESCRIPTION
    Refactored from clear_.bat.
    1. Recursively delete files matching a set of extensions.
    2. Recursively delete a set of generated directories.
    3. Recurse into .\lingofuse and run its own clear_.ps1.
    4. Run .\csharp_solved\clean.ps1.
#>

[CmdletBinding()]
param()

# -------------------------------------------------------------------------
# Helpers
# -------------------------------------------------------------------------
function Write-Status {
    param([string]$Message)
    Write-Host "[clean] $Message" -ForegroundColor Cyan
}

function Write-Skip {
    param([string]$Message)
    Write-Host "[clean] skipped: $Message" -ForegroundColor DarkGray
}

# -------------------------------------------------------------------------
# 1. Delete files by extension (recursive)
#    Equivalent to: del /s *.exe  del /s *.ini  ...
# -------------------------------------------------------------------------
$filePatterns = @(
    '*.exe'
    '*.ini'
    '*.local'
    '*.identcache'
    '*.lps'
    '*.spec'
    '*.pdb'
)

foreach ($pattern in $filePatterns) {
    $files = Get-ChildItem -Path . -Filter $pattern -File -Recurse -Force -ErrorAction SilentlyContinue
    if ($files) {
        Write-Status ("Removing {0} file(s) matching '{1}'" -f $files.Count, $pattern)
        $files | Remove-Item -Force -ErrorAction SilentlyContinue
    }
    else {
        Write-Skip "no files matching '$pattern'"
    }
}

# -------------------------------------------------------------------------
# 2. Delete directories (recursive)
#    Equivalent to: rd /q /s <dir>
# -------------------------------------------------------------------------
$dirsToRemove = @(
    '.\lib'
    '.\mcp_configs'
    '.\build'
    '.\dist'
    '.\__pycache__'
    '.\llm_common\__pycache__'
)

foreach ($dir in $dirsToRemove) {
    if (Test-Path -LiteralPath $dir) {
        Write-Status "Removing directory '$dir'"
        Remove-Item -LiteralPath $dir -Recurse -Force -ErrorAction SilentlyContinue
    }
    else {
        Write-Skip "directory '$dir' does not exist"
    }
}

# -------------------------------------------------------------------------
# 3. Recurse into .\lingofuse and run its own clear script
#    Equivalent to: cd .\lingofuse\ && call clear_.bat && cd ..
# -------------------------------------------------------------------------
$subProjectDir = '.\lingofuse'
if (Test-Path -LiteralPath $subProjectDir -PathType Container) {
    Write-Status "Entering '$subProjectDir'"
    Push-Location -LiteralPath $subProjectDir
    try {
        $childScript = '.\clear_.ps1'
        if (Test-Path -LiteralPath $childScript -PathType Leaf) {
            & $childScript
        }
        else {
            Write-Skip "child script '$childScript' not found in '$subProjectDir'"
        }
    }
    finally {
        Pop-Location
    }
}
else {
    Write-Skip "subdirectory '$subProjectDir' does not exist"
}

# -------------------------------------------------------------------------
# 4. Run csharp_solved\clean.ps1
#    Equivalent to: powershell -NoProfile -ExecutionPolicy Bypass -File ".\csharp_solved\clean.ps1"
# -------------------------------------------------------------------------
$csharpCleanScript = '.\csharp_solved\clean.ps1'
if (Test-Path -LiteralPath $csharpCleanScript -PathType Leaf) {
    Write-Status "Running '$csharpCleanScript'"
    & $csharpCleanScript
}
else {
    Write-Skip "script '$csharpCleanScript' not found"
}

Write-Status 'Done.'