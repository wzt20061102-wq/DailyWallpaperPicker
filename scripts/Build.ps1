# SPDX-License-Identifier: GPL-3.0-only
param([string]$OutputDirectory = '', [string]$SourceRevision = '')
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSVersion.Major -lt 7) { throw 'PowerShell 7 or later is required.' }
if (-not [Environment]::Is64BitOperatingSystem) { throw '64-bit Windows is required.' }
$root = Split-Path -Parent $PSScriptRoot
$output = if ($OutputDirectory) { [IO.Path]::GetFullPath($OutputDirectory) } else { Join-Path $root 'artifacts\bin' }
$framework = Join-Path ([Environment]::GetFolderPath('Windows')) 'Microsoft.NET\Framework64\v4.0.30319'
$msbuild = Join-Path $framework 'MSBuild.exe'
if (-not (Test-Path -LiteralPath $msbuild)) { throw '.NET Framework 4.x MSBuild is missing. Install .NET Framework 4.8 on Windows.' }
$version = (Get-Content -LiteralPath (Join-Path $root 'VERSION') -Raw -Encoding UTF8).Trim()
if ($version -notmatch '^(\d+)\.(\d+)\.(\d+)(?:-preview\.(\d+))?$') { throw 'Unsupported VERSION format.' }
$assemblyVersion = "$($Matches[1]).$($Matches[2]).$($Matches[3]).0"
$revisionNumber = if ($Matches.ContainsKey(4)) { $Matches[4] } else { '0' }
$fileVersion = "$($Matches[1]).$($Matches[2]).$($Matches[3]).$revisionNumber"
if (-not $SourceRevision -and (Get-Command git -ErrorAction SilentlyContinue)) {
    $revision = & git -C $root rev-parse HEAD 2>$null
    $gitExit = $LASTEXITCODE
    if ($gitExit -eq 0) { $SourceRevision = "$revision".Trim() }
}
if ($SourceRevision -and $SourceRevision -notmatch '^[0-9a-f]{40}$') { throw 'SourceRevision must be a full Git commit hash.' }
$informationVersion = if ($SourceRevision) { "$version+$SourceRevision" } else { "$version+source" }
New-Item -ItemType Directory -Path $output -Force | Out-Null
$generated = Join-Path $output 'generated'
New-Item -ItemType Directory -Path $generated -Force | Out-Null
$versionFile = Join-Path $generated 'VersionInfo.cs'
$metadata = @"
// Generated from VERSION by Build.ps1. Do not edit.
using System.Reflection;
[assembly: AssemblyCompany("wzt20061102-wq")]
[assembly: AssemblyProduct("DailyWallpaperPicker")]
[assembly: AssemblyCopyright("Copyright (C) 2026 wzt20061102-wq")]
[assembly: AssemblyVersion("$assemblyVersion")]
[assembly: AssemblyFileVersion("$fileVersion")]
[assembly: AssemblyInformationalVersion("$informationVersion")]
"@
[IO.File]::WriteAllText($versionFile, $metadata, [Text.UTF8Encoding]::new($false))
$arguments = @(
    (Join-Path $root 'DailyWallpaperPicker.sln'), '/nologo', '/verbosity:minimal', '/t:Rebuild',
    '/p:Configuration=Release', '/p:Platform=x64', ("/p:OutputPath=$output\"),
    ("/p:GeneratedVersionFile=$versionFile"), ("/p:FrameworkPathOverride=$framework"), ("/p:ArtifactsRoot=$output\intermediate")
)
& $msbuild @arguments
$buildExit = $LASTEXITCODE
if ($buildExit -ne 0) { throw "Solution build failed: $buildExit" }
Copy-Item -LiteralPath (Join-Path $root 'VERSION') -Destination (Join-Path $output 'VERSION')
$records = foreach ($name in @('DailyWallpaperPicker.exe', 'SteamWorker.exe', 'WallpaperReadback.exe', 'DailyWallpaperPicker.Tests.exe')) {
    $file = Join-Path $output $name
    if (-not (Test-Path -LiteralPath $file) -or (Get-Item -LiteralPath $file).Length -eq 0) { throw "Missing output: $name" }
    $actualVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($file)
    if ($actualVersion.FileVersion -cne $fileVersion -or $actualVersion.ProductVersion -cne $informationVersion) { throw "Version mismatch: $name" }
    [pscustomobject]@{ name = $name; sha256 = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash; fileVersion = $actualVersion.FileVersion; productVersion = $actualVersion.ProductVersion }
}
@{ version = $version; sourceRevision = $SourceRevision; buildExitCode = $buildExit; files = @($records); createdUtc = [DateTime]::UtcNow.ToString('o') } |
    ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $output 'build-info.json') -Encoding UTF8
$records
