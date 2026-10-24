# SPDX-License-Identifier: GPL-3.0-only
param([string]$BinaryDirectory='',[string]$OutputDirectory='')
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
if($PSVersionTable.PSVersion.Major -lt 7){throw 'PowerShell 7 or later is required.'}
$root=Split-Path -Parent $PSScriptRoot
$bin=if($BinaryDirectory){[IO.Path]::GetFullPath($BinaryDirectory)}else{Join-Path $root 'artifacts\bin'}
$output=if($OutputDirectory){[IO.Path]::GetFullPath($OutputDirectory)}else{Join-Path ([IO.Path]::GetTempPath()) ('dwp-tests-'+[Guid]::NewGuid().ToString('N').Substring(0,8))}
if(Test-Path -LiteralPath $output){throw 'Choose a new test output directory. Existing files are preserved.'}
$exe=Join-Path $bin 'DailyWallpaperPicker.exe'
if(-not (Test-Path -LiteralPath $exe)){throw 'Build the application first.'}
New-Item -ItemType Directory -Path $output|Out-Null
$started=[DateTime]::UtcNow
$process=Start-Process -FilePath $exe -ArgumentList @('--self-test','--data-dir',('"'+$output+'"')) -PassThru -WindowStyle Hidden
if(-not $process.WaitForExit(120000)){$process.Kill();throw 'Offline tests timed out.'}
$testExit=$process.ExitCode
$resultFile=Join-Path $output 'tests.json'
if(-not (Test-Path -LiteralPath $resultFile) -or (Get-Item -LiteralPath $resultFile).LastWriteTimeUtc -lt $started){throw 'Fresh test results missing.'}
$result=Get-Content -LiteralPath $resultFile -Raw -Encoding UTF8|ConvertFrom-Json
if($testExit -ne 0 -or $result.status -ne 'passed' -or $result.total -lt 82 -or $result.passed -ne $result.total -or $result.failed -ne 0){throw "Tests failed: exit=$testExit, passed=$($result.passed)/$($result.total). See $resultFile"}
if($result.safety.realSteamCalled -or $result.safety.desktopCalled -or $result.safety.realUserDirectoriesModified){throw 'Offline test boundary failed.'}
[pscustomobject]@{status='passed';total=$result.total;failed=$result.failed;exitCode=$testExit;report=$resultFile}
