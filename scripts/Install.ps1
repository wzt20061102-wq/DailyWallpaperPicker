# SPDX-License-Identifier: GPL-3.0-only
#requires -Version 7.0
[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$SourceDirectory = (Split-Path -Parent $PSScriptRoot),
    [string]$InstallRoot = (Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'DailyWallpaperPicker-PublicPreview')
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Startup.Common.ps1')
if (-not $IsWindows) { throw 'This Windows application requires Windows and PowerShell 7.' }

$source = Get-DwFullPath $SourceDirectory
$target = Get-DwFullPath $InstallRoot
$parent = [IO.Path]::GetDirectoryName($target)
if ([string]::IsNullOrEmpty($parent) -or -not (Test-Path -LiteralPath $parent -PathType Container)) {
    throw 'The parent of InstallRoot must already exist. Choose a new folder below an existing directory.'
}
if (Test-Path -LiteralPath $target) {
    throw 'InstallRoot already exists. Nothing was overwritten. Use the existing copy or choose a new folder.'
}

# No wallpaper, saved preference, Steam library, or local test data is copied.
$files = @(
    'DailyWallpaperPicker.exe', 'SteamWorker.exe', 'WallpaperReadback.exe',
    'Launch.cmd', 'LICENSE', 'LICENSE.STEAMWORKS-EXCEPTION', 'THIRD_PARTY_NOTICES.md',
    'scripts\Install.ps1', 'scripts\Enable-Startup.ps1', 'scripts\Disable-Startup.ps1',
    'scripts\Startup.Common.ps1'
)
foreach ($optional in @('README.md', 'docs\KNOWN_LIMITATIONS.md', 'docs\VALIDATION.md')) {
    if (Test-Path -LiteralPath (Join-Path $source $optional) -PathType Leaf) { $files += $optional }
}
$expected = @{}
foreach ($relative in $files) {
    $file = Join-Path $source $relative
    if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { throw "Release file not found: $relative" }
    $expected[$relative] = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash
}
if (-not $PSCmdlet.ShouldProcess($target, 'Copy the verified release files and create an empty data folder (does not run the app or enable startup)')) {
    return [pscustomobject]@{ Status = 'WhatIf'; InstallRoot = $target; FileCount = $files.Count }
}

# Stage beside the destination; an atomic rename refuses an existing installation,
# including a directory created by somebody else after the initial check.
$stage = Join-Path $parent ('.DailyWallpaperPicker-install-' + [Guid]::NewGuid().ToString('N'))
$stage = Get-DwFullPath $stage
try {
    New-Item -ItemType Directory -Path $stage -ErrorAction Stop | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $stage 'scripts') -ErrorAction Stop | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $stage 'data') -ErrorAction Stop | Out-Null
    if (@($files | Where-Object { $_.StartsWith('docs\', [StringComparison]::Ordinal) }).Count -gt 0) {
        New-Item -ItemType Directory -Path (Join-Path $stage 'docs') -ErrorAction Stop | Out-Null
    }
    foreach ($relative in $files) {
        Copy-Item -LiteralPath (Join-Path $source $relative) -Destination (Join-Path $stage $relative) -ErrorAction Stop
        if ((Get-FileHash -LiteralPath (Join-Path $stage $relative) -Algorithm SHA256).Hash -cne $expected[$relative]) {
            throw "Copy verification failed: $relative"
        }
    }
    $actual = @(Get-ChildItem -LiteralPath $stage -Recurse -Force -File | ForEach-Object { [IO.Path]::GetRelativePath($stage, $_.FullName) })
    if ($actual.Count -ne $files.Count -or @(Compare-Object ($files | Sort-Object) ($actual | Sort-Object)).Count -ne 0) {
        throw 'The installed file list differs from the release whitelist.'
    }
    $directories = @(Get-ChildItem -LiteralPath $stage -Recurse -Force -Directory | ForEach-Object { [IO.Path]::GetRelativePath($stage, $_.FullName) })
    $expectedDirectories = @('data', 'scripts') + @($files | ForEach-Object { [IO.Path]::GetDirectoryName($_) } | Where-Object { -not [string]::IsNullOrEmpty($_) })
    if (@(Compare-Object ($expectedDirectories | Sort-Object -Unique) ($directories | Sort-Object)).Count -ne 0) {
        throw 'The installed directory list differs from the expected layout.'
    }
    if (@(Get-ChildItem -LiteralPath (Join-Path $stage 'data') -Force).Count -ne 0) { throw 'The data folder must be empty.' }
    [IO.Directory]::Move($stage, $target)
    return [pscustomobject]@{ Status = 'Installed'; InstallRoot = $target; FileCount = $files.Count; DataDirectory = (Join-Path $target 'data'); StartupEnabled = $false; AppStarted = $false }
}
finally {
    if (Test-Path -LiteralPath $stage) {
        $safeParent = [IO.Path]::GetDirectoryName($stage)
        if ($safeParent -ine $parent -or -not ([IO.Path]::GetFileName($stage).StartsWith('.DailyWallpaperPicker-install-', [StringComparison]::Ordinal))) {
            throw 'Refusing to remove an unexpected staging path.'
        }
        Remove-Item -LiteralPath $stage -Recurse -Force -ErrorAction Stop
    }
}
