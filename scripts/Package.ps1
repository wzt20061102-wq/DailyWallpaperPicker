# SPDX-License-Identifier: GPL-3.0-only
param([string]$OutputDirectory='')
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
if($PSVersionTable.PSVersion.Major -lt 7){throw 'PowerShell 7 or later is required.'}
Add-Type -AssemblyName System.IO.Compression.FileSystem
$root=Split-Path -Parent $PSScriptRoot
$version=(Get-Content -LiteralPath (Join-Path $root 'VERSION') -Encoding UTF8 -Raw).Trim()
if($version -notmatch '^\d+\.\d+\.\d+(?:-[a-z0-9.]+)?$'){throw 'Invalid version'}
$output=if($OutputDirectory){[IO.Path]::GetFullPath($OutputDirectory)}else{Join-Path $root 'artifacts\release'}
if(Test-Path -LiteralPath $output){throw 'Choose a new package output directory; existing releases are preserved.'}
$job=Join-Path $root ('artifacts\package-'+[Guid]::NewGuid().ToString('N'))
$bin=Join-Path $job 'bin'
New-Item -ItemType Directory -Path $output,(Join-Path $job 'portable'),(Join-Path $job 'source')|Out-Null
$portable=Join-Path $job 'portable'
$source=Join-Path $job 'source'
$rootFiles=@('README.md','LICENSE','LICENSE.STEAMWORKS-EXCEPTION','THIRD_PARTY_NOTICES.md','VERSION','Launch.cmd','.gitignore','.gitattributes')
foreach($name in $rootFiles){Copy-Item -LiteralPath (Join-Path $root $name) -Destination $source}
foreach($folder in @('src','scripts','docs')){Copy-Item -LiteralPath (Join-Path $root $folder) -Destination (Join-Path $source $folder) -Recurse}
$allowed=@('.cs','.ps1','.md','.cmd','.manifest','.ico','.yml')
$privatePattern='(?i)(C:\\Users\\|'+('codex'+':/'+'/threads/')+'|github_pat_[A-Za-z0-9_]+|ghp_[A-Za-z0-9]{20,})'
foreach($file in @(Get-ChildItem -LiteralPath $source -File -Recurse -Force)){
 $relative=[IO.Path]::GetRelativePath($source,$file.FullName).Replace('\','/')
 if($relative -match '(^|/)(data|cache|thumbnails|testdata|evidence|\.git)/' -or ($file.Extension -notin $allowed -and $relative -notin $rootFiles)){throw "Unexpected source package file: $relative"}
 if($file.Extension -ne '.ico'){
  $text=Get-Content -LiteralPath $file.FullName -Raw -Encoding UTF8
  if($text -match $privatePattern){throw "Private data pattern in $relative"}
 }
}
# Build and test the frozen source copy that will be included in the source ZIP.
& (Join-Path $source 'scripts\Build.ps1') -OutputDirectory $bin
$tests=& (Join-Path $source 'scripts\Test.ps1') -BinaryDirectory $bin
& (Join-Path $source 'scripts\Test-PortableScripts.ps1')
foreach($name in @('DailyWallpaperPicker.exe','SteamWorker.exe','WallpaperReadback.exe')){Copy-Item -LiteralPath (Join-Path $bin $name) -Destination $portable}
foreach($name in @('README.md','LICENSE','LICENSE.STEAMWORKS-EXCEPTION','THIRD_PARTY_NOTICES.md','VERSION','Launch.cmd')){Copy-Item -LiteralPath (Join-Path $source $name) -Destination $portable}
Copy-Item -LiteralPath (Join-Path $source 'docs') -Destination (Join-Path $portable 'docs') -Recurse
New-Item -ItemType Directory -Path (Join-Path $portable 'scripts')|Out-Null
foreach($name in @('Install.ps1','Enable-Startup.ps1','Disable-Startup.ps1','Startup.Common.ps1')){Copy-Item -LiteralPath (Join-Path $source ('scripts\'+$name)) -Destination (Join-Path $portable 'scripts')}
function Write-Manifest([string]$folder){
 $lines=@(Get-ChildItem -LiteralPath $folder -File -Recurse -Force|Sort-Object FullName|ForEach-Object {((Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant())+'  '+[IO.Path]::GetRelativePath($folder,$_.FullName).Replace('\','/')})
 $lines|Set-Content -LiteralPath (Join-Path $folder 'SHA256SUMS.txt') -Encoding UTF8
}
function Zip-Verified([string]$folder,[string]$target){
 $expected=@{};foreach($file in @(Get-ChildItem -LiteralPath $folder -File -Recurse -Force)){$expected[[IO.Path]::GetRelativePath($folder,$file.FullName).Replace('\','/')]=(Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash}
 if($expected.Count -eq 0){throw 'Empty package'}
 [IO.Compression.ZipFile]::CreateFromDirectory($folder,$target,[IO.Compression.CompressionLevel]::Optimal,$false)
 $zip=[IO.Compression.ZipFile]::OpenRead($target);$seen=[Collections.Generic.HashSet[string]]::new()
 try{foreach($entry in $zip.Entries){if($entry.FullName.EndsWith('/')){continue};if(-not $expected.ContainsKey($entry.FullName) -or -not $seen.Add($entry.FullName)){throw 'ZIP entry mismatch'};$stream=$entry.Open();$hasher=[Security.Cryptography.SHA256]::Create();try{$hash=[BitConverter]::ToString($hasher.ComputeHash($stream)).Replace('-','')}finally{$hasher.Dispose();$stream.Dispose()};if($hash -ne $expected[$entry.FullName]){throw 'ZIP hash mismatch'}}}finally{$zip.Dispose()}
 if($seen.Count -ne $expected.Count){throw 'ZIP incomplete'}
 [pscustomobject]@{name=[IO.Path]::GetFileName($target);files=$seen.Count;sha256=(Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash.ToLowerInvariant()}
}
Write-Manifest $source
Write-Manifest $portable
$records=@(Zip-Verified $source (Join-Path $output ("DailyWallpaperPicker-$version-source.zip"));Zip-Verified $portable (Join-Path $output ("DailyWallpaperPicker-$version-win-x64.zip")))
$records|ForEach-Object {$_.sha256+'  '+$_.name}|Set-Content -LiteralPath (Join-Path $output 'SHA256SUMS.txt') -Encoding UTF8
@{version=$version;offlineTests=$tests;packages=$records;createdUtc=[DateTime]::UtcNow.ToString('o')}|ConvertTo-Json -Depth 6|Set-Content -LiteralPath (Join-Path $job 'package-report.json') -Encoding UTF8
$records
Write-Output "Package directory: $output"
