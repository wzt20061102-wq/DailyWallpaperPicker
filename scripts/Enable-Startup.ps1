# SPDX-License-Identifier: GPL-3.0-only
#requires -Version 7.0
[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$ExecutablePath = (Join-Path (Split-Path -Parent $PSScriptRoot) 'DailyWallpaperPicker.exe'),
    [string]$DataDirectory
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Startup.Common.ps1')
if ([string]::IsNullOrWhiteSpace($DataDirectory)) {
    $DataDirectory = Join-Path ([IO.Path]::GetDirectoryName((Get-DwFullPath $ExecutablePath))) 'data'
}

$spec = Get-DwStartupSpec -ExecutablePath $ExecutablePath -DataDirectory $DataDirectory -RequireExecutable
$tasks = @(Get-DwRootTasks)
if (@($tasks | Where-Object TaskName -IEQ 'DailyWallpaperPicker').Count -gt 0) {
    throw 'The earlier DailyWallpaperPicker startup task exists. No new task was created. Keep using the existing installation, or remove its startup task intentionally before enabling this preview.'
}
$existing = @($tasks | Where-Object TaskName -IEQ $spec.TaskName)
if ($existing.Count -gt 1) { throw 'Multiple matching task entries were found. No task was changed.' }
if ($existing.Count -eq 1) {
    Assert-DwOwnedTask -Task $existing[0] -Spec $spec
    if ([string]$existing[0].State -eq 'Disabled') {
        if ($PSCmdlet.ShouldProcess($spec.TaskName, 'Enable this verified current-user startup task')) {
            Enable-ScheduledTask -TaskName $spec.TaskName -TaskPath $spec.TaskPath -ErrorAction Stop | Out-Null
            $readback = @(Get-DwRootTasks | Where-Object TaskName -IEQ $spec.TaskName)
            if ($readback.Count -ne 1) { throw 'Enable returned, but the task could not be read back.' }
            Assert-DwOwnedTask -Task $readback[0] -Spec $spec
            if ([string]$readback[0].State -eq 'Disabled') { throw 'Enable returned, but the task is still disabled.' }
            return [pscustomobject]@{ Status = 'Enabled'; TaskName = $spec.TaskName; ExecutablePath = $spec.ExecutablePath; DataDirectory = $spec.DataDirectory }
        }
        return [pscustomobject]@{ Status = 'WhatIf'; TaskName = $spec.TaskName }
    }
    return [pscustomobject]@{ Status = 'AlreadyEnabled'; TaskName = $spec.TaskName; ExecutablePath = $spec.ExecutablePath; DataDirectory = $spec.DataDirectory }
}

if ($PSCmdlet.ShouldProcess($spec.TaskName, 'Register a current-user logon task (does not launch now)')) {
    $action = New-ScheduledTaskAction -Execute $spec.ExecutablePath -Argument $spec.Arguments -WorkingDirectory $spec.WorkingDirectory
    $trigger = New-ScheduledTaskTrigger -AtLogOn -User $spec.Sid
    $principal = New-ScheduledTaskPrincipal -UserId $spec.Sid -LogonType Interactive -RunLevel Limited
    $settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -MultipleInstances IgnoreNew -ExecutionTimeLimit ([TimeSpan]::Zero)
    $definition = New-ScheduledTask -Action $action -Trigger $trigger -Principal $principal -Settings $settings -Description $spec.Description
    # Do not use -Force: a task created concurrently must never be overwritten.
    Register-ScheduledTask -TaskName $spec.TaskName -TaskPath $spec.TaskPath -InputObject $definition -ErrorAction Stop | Out-Null
    $registered = @(Get-DwRootTasks | Where-Object TaskName -IEQ $spec.TaskName)
    if ($registered.Count -ne 1) { throw 'Registration returned, but the new task could not be read back.' }
    Assert-DwOwnedTask -Task $registered[0] -Spec $spec
    return [pscustomobject]@{ Status = 'Enabled'; TaskName = $spec.TaskName; ExecutablePath = $spec.ExecutablePath; DataDirectory = $spec.DataDirectory }
}
return [pscustomobject]@{ Status = 'WhatIf'; TaskName = $spec.TaskName; ExecutablePath = $spec.ExecutablePath; DataDirectory = $spec.DataDirectory }
