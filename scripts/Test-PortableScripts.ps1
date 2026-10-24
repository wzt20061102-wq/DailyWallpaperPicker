# SPDX-License-Identifier: GPL-3.0-only
#requires -Version 7.0
[CmdletBinding()]
param([string]$ScratchDirectory = (Join-Path ([IO.Path]::GetTempPath()) ('DailyWallpaperPicker-script-tests-' + [Guid]::NewGuid().ToString('N'))))
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$scratch = [IO.Path]::GetFullPath($ScratchDirectory)
if (Test-Path -LiteralPath $scratch) { throw 'Use a new empty ScratchDirectory; existing contents are never removed.' }
New-Item -ItemType Directory -Path $scratch -ErrorAction Stop | Out-Null
$results = [Collections.Generic.List[object]]::new()
function Assert-Check {
    param([string]$Name, [bool]$Condition)
    $results.Add([pscustomobject]@{ name = $Name; passed = $Condition })
    if (-not $Condition) { throw "Failed: $Name" }
}
function Assert-Rejected {
    param([string]$Name, [scriptblock]$Action, [string]$Message)
    $caught = $false
    try { & $Action | Out-Null } catch { $caught = $_.Exception.Message -like "*$Message*" }
    Assert-Check $Name $caught
}

# Only copying inert fixtures is real. Every Task Scheduler command is shadowed
# below, so this script cannot register, enable, remove, or start a real task.
if (Test-Path Variable:global:DwPortableTestState) { throw 'A portable-script test is already active in this PowerShell session.' }
$global:DwPortableTestState = [pscustomobject]@{ Tasks = @(); RegisterCount = 0; EnableCount = 0; RemoveCount = 0 }
function Get-ScheduledTask { [CmdletBinding()] param([string]$TaskPath) return $global:DwPortableTestState.Tasks }
function New-ScheduledTaskAction { param($Execute, $Argument, $WorkingDirectory) return [pscustomobject]@{ Execute = $Execute; Arguments = $Argument; WorkingDirectory = $WorkingDirectory } }
function New-ScheduledTaskTrigger { param([switch]$AtLogOn, $User) return [pscustomobject]@{ UserId = $User } }
function New-ScheduledTaskPrincipal { param($UserId, $LogonType, $RunLevel) return [pscustomobject]@{ UserId = $UserId; LogonType = $LogonType; RunLevel = $RunLevel } }
function New-ScheduledTaskSettingsSet { param([switch]$AllowStartIfOnBatteries, [switch]$DontStopIfGoingOnBatteries, $MultipleInstances, $ExecutionTimeLimit) return [pscustomobject]@{ MultipleInstances = $MultipleInstances } }
function New-ScheduledTask { param($Action, $Trigger, $Principal, $Settings, $Description) return [pscustomobject]@{ Actions = @($Action); Triggers = @($Trigger); Principal = $Principal; Description = $Description; Settings = $Settings } }
function Register-ScheduledTask {
    [CmdletBinding()] param($TaskName, $TaskPath, $InputObject)
    if (@($global:DwPortableTestState.Tasks | Where-Object TaskName -IEQ $TaskName).Count -gt 0) { throw 'Task already exists.' }
    $global:DwPortableTestState.RegisterCount++
    $InputObject | Add-Member TaskName $TaskName
    $InputObject | Add-Member TaskPath $TaskPath
    $InputObject | Add-Member State 'Ready'
    $global:DwPortableTestState.Tasks += $InputObject
    return $InputObject
}
function Enable-ScheduledTask {
    [CmdletBinding()] param($TaskName, $TaskPath)
    $global:DwPortableTestState.EnableCount++
    ($global:DwPortableTestState.Tasks | Where-Object TaskName -IEQ $TaskName).State = 'Ready'
}
function Unregister-ScheduledTask {
    [CmdletBinding(SupportsShouldProcess)] param($TaskName, $TaskPath)
    $global:DwPortableTestState.RemoveCount++
    $global:DwPortableTestState.Tasks = @($global:DwPortableTestState.Tasks | Where-Object TaskName -INE $TaskName)
}
function Start-ScheduledTask { throw 'Starting a task is forbidden in the release-script tests.' }
function Start-Process { throw 'Launching an application is forbidden in the release-script tests.' }

try {
    $errors = @()
    foreach ($file in Get-ChildItem -LiteralPath $PSScriptRoot -Filter '*.ps1' -File) {
        $tokens = $null
        $parseErrors = $null
        [Management.Automation.Language.Parser]::ParseFile($file.FullName, [ref]$tokens, [ref]$parseErrors) | Out-Null
        $errors += @($parseErrors)
    }
    Assert-Check 'scripts parse under PowerShell 7' ($errors.Count -eq 0)
    $fixture = Join-Path $scratch 'release fixture'
    New-Item -ItemType Directory -Path (Join-Path $fixture 'scripts') | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $fixture 'docs') | Out-Null
    $files = @('DailyWallpaperPicker.exe', 'SteamWorker.exe', 'WallpaperReadback.exe', 'Launch.cmd', 'LICENSE', 'LICENSE.STEAMWORKS-EXCEPTION', 'THIRD_PARTY_NOTICES.md', 'VERSION', 'build-info.json')
    foreach ($file in $files) { Set-Content -LiteralPath (Join-Path $fixture $file) -Value "Inert test fixture: $file" -Encoding UTF8 }
    foreach ($file in @('README.md', 'docs\KNOWN_LIMITATIONS.md', 'docs\VALIDATION.md')) {
        Set-Content -LiteralPath (Join-Path $fixture $file) -Value "Optional public documentation: $file" -Encoding UTF8
    }
    foreach ($file in @('Install.ps1', 'Enable-Startup.ps1', 'Disable-Startup.ps1', 'Startup.Common.ps1')) {
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot $file) -Destination (Join-Path $fixture "scripts\$file")
    }
    Set-Content -LiteralPath (Join-Path $fixture 'private-do-not-copy.txt') -Value 'excluded' -Encoding UTF8
    $install = Join-Path $scratch 'isolated install'
    $plan = & (Join-Path $PSScriptRoot 'Install.ps1') -SourceDirectory $fixture -InstallRoot $install -WhatIf
    Assert-Check 'install WhatIf creates nothing' ($plan.Status -eq 'WhatIf' -and -not (Test-Path -LiteralPath $install))
    $empty = New-Item -ItemType Directory -Path (Join-Path $scratch 'incomplete release')
    Assert-Rejected 'incomplete releases fail before copying' { & (Join-Path $PSScriptRoot 'Install.ps1') -SourceDirectory $empty.FullName -InstallRoot $install } 'Release file not found'
    Assert-Check 'failed validation leaves no target' (-not (Test-Path -LiteralPath $install))
    $installed = & (Join-Path $PSScriptRoot 'Install.ps1') -SourceDirectory $fixture -InstallRoot $install
    Assert-Check 'isolated installation succeeds without launching' ($installed.Status -eq 'Installed' -and -not $installed.AppStarted -and -not $installed.StartupEnabled)
    Assert-Check 'installation data starts empty' (@(Get-ChildItem -LiteralPath (Join-Path $install 'data') -Force).Count -eq 0)
    Assert-Check 'non-whitelisted files are excluded' (-not (Test-Path -LiteralPath (Join-Path $install 'private-do-not-copy.txt')))
    $copied = @(Get-ChildItem -LiteralPath $install -Recurse -Force -File)
    Assert-Check 'only whitelisted files and optional public docs are installed' ($copied.Count -eq 16)
    $allHashesMatch = $true
    foreach ($file in $copied) {
        $relative = [IO.Path]::GetRelativePath($install, $file.FullName)
        if ((Get-FileHash -LiteralPath $file.FullName).Hash -cne (Get-FileHash -LiteralPath (Join-Path $fixture $relative)).Hash) { $allHashesMatch = $false }
    }
    Assert-Check 'all installed file hashes match source' $allHashesMatch
    Assert-Rejected 'existing installations are never overwritten' { & (Join-Path $PSScriptRoot 'Install.ps1') -SourceDirectory $fixture -InstallRoot $install } 'InstallRoot already exists'
    Assert-Check 'staging folders are absent after success' (@(Get-ChildItem -LiteralPath $scratch -Directory -Force | Where-Object Name -Like '.DailyWallpaperPicker-install-*').Count -eq 0)

    $exe = Join-Path $install 'DailyWallpaperPicker.exe'
    $enableScript = Join-Path $PSScriptRoot 'Enable-Startup.ps1'
    $disableScript = Join-Path $PSScriptRoot 'Disable-Startup.ps1'
    $plan = & $enableScript -ExecutablePath $exe -WhatIf
    Assert-Check 'startup WhatIf registers nothing' ($plan.Status -eq 'WhatIf' -and $global:DwPortableTestState.RegisterCount -eq 0)
    $enabled = & $enableScript -ExecutablePath $exe
    Assert-Check 'startup uses adjacent data and public task name' ($enabled.Status -eq 'Enabled' -and $enabled.TaskName -ceq 'DailyWallpaperPicker-PublicPreview' -and $enabled.DataDirectory -eq (Join-Path $install 'data') -and $global:DwPortableTestState.RegisterCount -eq 1)
    $again = & $enableScript -ExecutablePath $exe
    Assert-Check 'startup enable is idempotent' ($again.Status -eq 'AlreadyEnabled' -and $global:DwPortableTestState.RegisterCount -eq 1)
    $owned = $global:DwPortableTestState.Tasks[0]
    $owned.State = 'Disabled'
    $plan = & $enableScript -ExecutablePath $exe -WhatIf
    Assert-Check 'disabled startup WhatIf stays disabled' ($plan.Status -eq 'WhatIf' -and $owned.State -eq 'Disabled' -and $global:DwPortableTestState.EnableCount -eq 0)
    $enabled = & $enableScript -ExecutablePath $exe
    Assert-Check 'verified disabled task can be enabled' ($enabled.Status -eq 'Enabled' -and $owned.State -eq 'Ready' -and $global:DwPortableTestState.EnableCount -eq 1)

    $sid = $owned.Principal.UserId
    $owned.Principal.UserId = [Security.Principal.WindowsIdentity]::GetCurrent().Name
    $byAccountName = & $enableScript -ExecutablePath $exe
    Assert-Check 'account-name principals resolve to the same owner SID' ($byAccountName.Status -eq 'AlreadyEnabled')
    $owned.Principal.UserId = $sid
    $owned.Principal.UserId = 'S-1-0-0'
    Assert-Rejected 'enable refuses another owner SID' { & $enableScript -ExecutablePath $exe } 'do not match'
    Assert-Rejected 'disable refuses another owner SID' { & $disableScript -ExecutablePath $exe } 'do not match'
    $owned.Principal.UserId = $sid
    $path = $owned.Actions[0].Execute
    $owned.Actions[0].Execute = Join-Path $scratch 'another.exe'
    Assert-Rejected 'disable refuses another executable path' { & $disableScript -ExecutablePath $exe } 'do not match'
    $owned.Actions[0].Execute = $path
    $argsBefore = $owned.Actions[0].Arguments
    $owned.Actions[0].Arguments = '--unexpected'
    Assert-Rejected 'disable refuses changed arguments' { & $disableScript -ExecutablePath $exe } 'do not match'
    $owned.Actions[0].Arguments = $argsBefore
    $workingDirectory = $owned.Actions[0].WorkingDirectory
    $owned.Actions[0].WorkingDirectory = $scratch
    Assert-Rejected 'disable refuses another working directory' { & $disableScript -ExecutablePath $exe } 'do not match'
    $owned.Actions[0].WorkingDirectory = $workingDirectory
    $owned.Actions += [pscustomobject]@{ Execute = 'other.exe'; Arguments = ''; WorkingDirectory = '' }
    Assert-Rejected 'disable refuses a task with extra actions' { & $disableScript -ExecutablePath $exe } 'do not match'
    $owned.Actions = @($owned.Actions[0])
    $description = $owned.Description
    $owned.Description = 'Unknown task'
    Assert-Rejected 'same-name unknown tasks are refused' { & $enableScript -ExecutablePath $exe } 'do not match'
    $owned.Description = $description
    $global:DwPortableTestState.Tasks += [pscustomobject]@{ TaskName = 'DailyWallpaperPicker' }
    Assert-Rejected 'legacy startup prevents duplicate public startup' { & $enableScript -ExecutablePath $exe } 'earlier DailyWallpaperPicker startup task exists'
    $plan = & $disableScript -ExecutablePath $exe -WhatIf
    Assert-Check 'disable WhatIf removes nothing' ($plan.Status -eq 'WhatIf' -and $global:DwPortableTestState.RemoveCount -eq 0)
    $disabled = & $disableScript -ExecutablePath $exe
    Assert-Check 'disable removes only owned public startup' ($disabled.Status -eq 'Disabled' -and $global:DwPortableTestState.RemoveCount -eq 1 -and $global:DwPortableTestState.Tasks.Count -eq 1 -and $global:DwPortableTestState.Tasks[0].TaskName -ceq 'DailyWallpaperPicker')
    $again = & $disableScript -ExecutablePath $exe
    Assert-Check 'disable is idempotent' ($again.Status -eq 'NotEnabled' -and $global:DwPortableTestState.RemoveCount -eq 1)
    $global:DwPortableTestState.Tasks = @()
    Assert-Rejected 'wrong executable names are refused' { & $enableScript -ExecutablePath (Join-Path $fixture 'SteamWorker.exe') } 'must point to DailyWallpaperPicker.exe'
    Assert-Rejected 'data paths with quotes are refused' { & $enableScript -ExecutablePath $exe -DataDirectory 'C:\bad"path' } 'quotes or line breaks'
    Assert-Rejected 'a missing executable cannot be enabled' { & $enableScript -ExecutablePath (Join-Path $scratch 'missing\DailyWallpaperPicker.exe') } 'Executable not found'
    Assert-Check 'testing did not run a real application or task operation' ($global:DwPortableTestState.RegisterCount -eq 1 -and $global:DwPortableTestState.EnableCount -eq 1 -and $global:DwPortableTestState.RemoveCount -eq 1)
}
finally {
    [pscustomobject]@{
        generatedUtc = [DateTime]::UtcNow.ToString('o')
        passed = @($results | Where-Object passed).Count
        total = $results.Count
        results = @($results.ToArray())
        taskScheduler = 'In-memory mocks only; no real task registrations or removals.'
        installation = 'Inert fixtures in a new caller-selected scratch folder; no app executed.'
    } | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $scratch 'results.json') -Encoding UTF8
    Remove-Variable -Name DwPortableTestState -Scope Global
}
$results | Format-Table -AutoSize
if (@($results | Where-Object { -not $_.passed }).Count -gt 0) { exit 1 }
