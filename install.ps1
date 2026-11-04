# Installs or updates the brain CLI on Windows from the GitHub release.
#
#   irm https://raw.githubusercontent.com/JackM36/agent-memory/main/install.ps1 | iex
#
# Settings (environment variables, since piped scripts take no parameters):
#   AGENT_BRAIN_VERSION      a version such as 0.3.1 (default: the latest release)
#   AGENT_BRAIN_INSTALL_DIR  install folder (default: %LOCALAPPDATA%\agent-brain\bin)
#   AGENT_BRAIN_REPOSITORY   owner/repo to download from (default: JackM36/agent-memory)
#   AGENT_BRAIN_BASE_URL     download folder instead of the GitHub release (mirrors, testing)
#   AGENT_BRAIN_NO_MODIFY_PATH  set to 1 to leave PATH alone
#
# The folder is added to your user PATH. Running the script again updates brain in place, so MCP registrations
# and hooks that call "brain" keep working. "brain upgrade" does the same from inside the tool.

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

function Install-AgentBrain {
    param(
        [string] $Version = $env:AGENT_BRAIN_VERSION,
        [string] $InstallDir = $env:AGENT_BRAIN_INSTALL_DIR,
        [string] $Repository = $env:AGENT_BRAIN_REPOSITORY
    )

    if (-not $Repository) { $Repository = 'JackM36/agent-memory' }
    if (-not $InstallDir) { $InstallDir = Join-Path $env:LOCALAPPDATA 'agent-brain\bin' }

    $arch = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture
    $rid = switch ($arch) {
        'X64' { 'win-x64' }
        'Arm64' { 'win-arm64' }
        default { throw "Unsupported architecture: $arch. Build from source instead (see README)." }
    }

    $base = if ($env:AGENT_BRAIN_BASE_URL) {
        $env:AGENT_BRAIN_BASE_URL.TrimEnd('/')
    } elseif ($Version) {
        "https://github.com/$Repository/releases/download/v$($Version.TrimStart('v'))"
    } else {
        "https://github.com/$Repository/releases/latest/download"
    }

    $asset = "brain-$rid.zip"
    $temp = Join-Path ([System.IO.Path]::GetTempPath()) ("agent-brain-" + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $temp | Out-Null
    try {
        Write-Host "Downloading $asset from $base"
        Invoke-WebRequest -Uri "$base/$asset" -OutFile (Join-Path $temp $asset) -UseBasicParsing
        Invoke-WebRequest -Uri "$base/SHA256SUMS" -OutFile (Join-Path $temp 'SHA256SUMS') -UseBasicParsing

        $expected = (Get-Content (Join-Path $temp 'SHA256SUMS') |
            Where-Object { $_ -match "^\s*([0-9a-fA-F]{64})\s+\*?$([regex]::Escape($asset))\s*$" } |
            ForEach-Object { $Matches[1] }) | Select-Object -First 1
        if (-not $expected) { throw "SHA256SUMS has no entry for $asset." }
        $actual = (Get-FileHash -Algorithm SHA256 (Join-Path $temp $asset)).Hash
        if ($actual -ne $expected.ToUpperInvariant()) { throw "Checksum mismatch for $asset; nothing was installed." }

        $unpacked = Join-Path $temp 'unpacked'
        Expand-Archive -Path (Join-Path $temp $asset) -DestinationPath $unpacked

        New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
        foreach ($file in Get-ChildItem -File $unpacked) {
            $target = Join-Path $InstallDir $file.Name
            if (Test-Path $target) {
                # A running brain (an agent's MCP server) cannot be overwritten, but it can be renamed.
                Remove-Item "$target.old" -Force -ErrorAction SilentlyContinue
                Rename-Item $target "$($file.Name).old" -Force
            }

            Move-Item $file.FullName $target
        }

        Get-ChildItem -File $InstallDir -Filter '*.old' | Remove-Item -Force -ErrorAction SilentlyContinue
    }
    finally {
        Remove-Item $temp -Recurse -Force -ErrorAction SilentlyContinue
    }

    $userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
    if (-not $env:AGENT_BRAIN_NO_MODIFY_PATH -and -not (($userPath -split ';') -contains $InstallDir)) {
        [Environment]::SetEnvironmentVariable('Path', ($userPath.TrimEnd(';') + ";$InstallDir").TrimStart(';'), 'User')
        Write-Host "Added $InstallDir to your user PATH (open a new terminal to pick it up)."
    }

    if (-not (($env:Path -split ';') -contains $InstallDir)) { $env:Path = "$env:Path;$InstallDir" }

    $installed = & (Join-Path $InstallDir 'brain.exe') --version
    Write-Host "Installed brain $installed in $InstallDir"
    Write-Host "Next: cd into a project and run 'brain init'. Upgrade later with 'brain upgrade'."
}

Install-AgentBrain
