# SPDX-License-Identifier: GPL-3.0-only
param([string]$OutputDirectory='')
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
if($PSVersionTable.PSVersion.Major -lt 7){throw 'PowerShell 7 or later is required.'}
if(-not [Environment]::Is64BitOperatingSystem){throw '64-bit Windows is required.'}
$root=Split-Path -Parent $PSScriptRoot
$source=Join-Path $root 'src'
$output=if($OutputDirectory){[IO.Path]::GetFullPath($OutputDirectory)}else{Join-Path $root 'artifacts\bin'}
$framework=Join-Path ([Environment]::GetFolderPath('Windows')) 'Microsoft.NET\Framework64\v4.0.30319'
$compiler=Join-Path $framework 'csc.exe'
if(-not (Test-Path -LiteralPath $compiler)){throw '.NET Framework 4.x compiler is missing. Install .NET Framework 4.8 on Windows.'}
New-Item -ItemType Directory -Path $output -Force|Out-Null
$arguments=@('/nologo','/target:winexe','/platform:x64','/optimize+','/utf8output',('/out:'+(Join-Path $output 'DailyWallpaperPicker.exe')),('/win32manifest:'+(Join-Path $source 'app.manifest')),('/win32icon:'+(Join-Path $source 'picker.ico')))
foreach($name in @('System.dll','System.Core.dll','System.Web.Extensions.dll','System.Xaml.dll','System.Drawing.dll','System.Windows.Forms.dll')){$arguments+='/r:'+(Join-Path $framework $name)}
foreach($name in @('PresentationCore.dll','PresentationFramework.dll','WindowsBase.dll')){$arguments+='/r:'+(Join-Path $framework ('WPF\'+$name))}
foreach($name in @('Models.cs','Infrastructure.cs','PickerController.cs','PickerWindow.cs','SelfTests.cs','Program.cs')){$arguments+=Join-Path $source $name}
& $compiler @arguments
$compileExit=$LASTEXITCODE
if($compileExit -ne 0){throw "Application compilation failed: $compileExit"}
foreach($name in @('SteamWorker','WallpaperReadback')){
 $arguments=@('/nologo','/target:winexe','/platform:x64','/optimize+','/utf8output',('/out:'+(Join-Path $output ($name+'.exe'))))
 foreach($assembly in @('System.dll','System.Core.dll','System.Web.Extensions.dll')){$arguments+='/r:'+(Join-Path $framework $assembly)}
 $arguments+=Join-Path $source ($name+'.cs')
 & $compiler @arguments
 $compileExit=$LASTEXITCODE
 if($compileExit -ne 0){throw "$name compilation failed: $compileExit"}
}
foreach($name in @('DailyWallpaperPicker.exe','SteamWorker.exe','WallpaperReadback.exe')){
 $file=Join-Path $output $name
 if(-not (Test-Path -LiteralPath $file) -or (Get-Item -LiteralPath $file).Length -eq 0){throw "Missing output: $name"}
 [pscustomobject]@{name=$name;sha256=(Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash}
}
