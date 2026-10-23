# SPDX-License-Identifier: GPL-3.0-only
#requires -Version 7.0
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-DwFullPath {
    param([Parameter(Mandatory)][string]$Path)
    if ([string]::IsNullOrWhiteSpace($Path) -or $Path.Contains('"') -or $Path.Contains("`r") -or $Path.Contains("`n")) {
        throw 'A file path cannot be empty or contain quotes or line breaks.'
    }
    return [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($Path))
}

function Get-DwStartupSpec {
    param(
        [Parameter(Mandatory)][string]$ExecutablePath,
        [Parameter(Mandatory)][string]$DataDirectory,
        [switch]$RequireExecutable
    )
    if (-not $IsWindows) { throw 'Startup tasks are supported only on Windows with PowerShell 7.' }
    $exe = Get-DwFullPath $ExecutablePath
    if ([IO.Path]::GetFileName($exe) -cne 'DailyWallpaperPicker.exe') {
        throw 'ExecutablePath must point to DailyWallpaperPicker.exe.'
    }
    if ($RequireExecutable -and -not (Test-Path -LiteralPath $exe -PathType Leaf)) {
        throw "Executable not found: $exe"
    }
    $data = Get-DwFullPath $DataDirectory
    # A trailing directory separator must not escape the command-line closing quote.
    if ($data.EndsWith('\')) { throw 'DataDirectory must be a folder below the drive root.' }
    $sid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    return [pscustomobject]@{
        TaskName = 'DailyWallpaperPicker-PublicPreview'
        TaskPath = '\'
        ExecutablePath = $exe
        WorkingDirectory = [IO.Path]::GetDirectoryName($exe)
        DataDirectory = $data
        Arguments = '--data-dir "' + $data + '"'
        Sid = $sid
        Description = "DailyWallpaperPicker public preview | schema=1 | owner=$sid"
    }
}

function Get-DwRootTasks {
    # Enumerating the root avoids treating a failed query as an absent task.
    return @(Get-ScheduledTask -TaskPath '\' -ErrorAction Stop)
}

function Assert-DwOwnedTask {
    param([Parameter(Mandatory)]$Task, [Parameter(Mandatory)]$Spec)
    $actions = @($Task.Actions)
    $ownerSid = [string]$Task.Principal.UserId
    if ($ownerSid -cne $Spec.Sid) {
        try {
            # Task Scheduler may display the principal as DOMAIN\name after
            # accepting a SID. Resolve it before enforcing current-user ownership.
            $ownerSid = ([Security.Principal.NTAccount]::new($ownerSid)).Translate([Security.Principal.SecurityIdentifier]).Value
        }
        catch { $ownerSid = '' }
    }
    $matches = $Task.TaskName -ceq $Spec.TaskName -and
        $Task.TaskPath -ceq $Spec.TaskPath -and
        $Task.Description -ceq $Spec.Description -and
        $ownerSid -ceq $Spec.Sid -and
        $actions.Count -eq 1
    if ($matches) {
        $action = $actions[0]
        $matches = ([string]$action.Execute -ieq $Spec.ExecutablePath) -and
            ([string]$action.WorkingDirectory -ieq $Spec.WorkingDirectory) -and
            ([string]$action.Arguments -ceq $Spec.Arguments)
    }
    if (-not $matches) {
        throw 'A task with this name exists but its owner, path, or arguments do not match. It was not changed.'
    }
}
