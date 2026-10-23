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

# The file may already have been removed; matching its recorded absolute path is sufficient.
$spec = Get-DwStartupSpec -ExecutablePath $ExecutablePath -DataDirectory $DataDirectory
$existing = @(Get-DwRootTasks | Where-Object TaskName -IEQ $spec.TaskName)
if ($existing.Count -eq 0) {
    return [pscustomobject]@{ Status = 'NotEnabled'; TaskName = $spec.TaskName }
}
if ($existing.Count -ne 1) { throw 'Multiple matching task entries were found. No task was changed.' }
Assert-DwOwnedTask -Task $existing[0] -Spec $spec
if ($PSCmdlet.ShouldProcess($spec.TaskName, 'Remove this verified current-user startup task')) {
    Unregister-ScheduledTask -TaskName $spec.TaskName -TaskPath $spec.TaskPath -Confirm:$false -ErrorAction Stop
    if (@(Get-DwRootTasks | Where-Object TaskName -IEQ $spec.TaskName).Count -ne 0) {
        throw 'Removal returned, but the task is still present.'
    }
    return [pscustomobject]@{ Status = 'Disabled'; TaskName = $spec.TaskName }
}
return [pscustomobject]@{ Status = 'WhatIf'; TaskName = $spec.TaskName }
