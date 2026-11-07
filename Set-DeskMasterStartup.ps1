[CmdletBinding()]
param([switch]$Remove)

$ErrorActionPreference = 'Stop'
$userSid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
$taskName = "DeskMaster-$userSid"
$legacyRunKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'

$scheduler = New-Object -ComObject Schedule.Service
$scheduler.Connect()
$tasks = $scheduler.GetFolder('\')

if ($Remove) {
    if ($tasks.GetTasks(0) | Where-Object Name -eq $taskName) {
        $tasks.DeleteTask($taskName, 0)
    }
}
else {
    $exe = Join-Path $PSScriptRoot 'bin\Release\net8.0-windows\DeskMaster.exe'
    if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) {
        throw 'Build DeskMaster in Release mode before enabling startup.'
    }

    $task = $scheduler.NewTask(0)
    $task.RegistrationInfo.Description = 'Restore saved DeskMaster folders on the desktop after sign-in.'
    $task.Settings.DisallowStartIfOnBatteries = $false
    $task.Settings.StopIfGoingOnBatteries = $false
    $task.Settings.ExecutionTimeLimit = 'PT0S'

    $logon = $task.Triggers.Create(9) # TASK_TRIGGER_LOGON
    $logon.UserId = $userSid
    $logon.Delay = 'PT20S'

    $action = $task.Actions.Create(0) # TASK_ACTION_EXEC
    $action.Path = $exe
    $action.Arguments = '--show-folders'
    $action.WorkingDirectory = Split-Path -Parent $exe

    # TASK_CREATE_OR_UPDATE (6) with TASK_LOGON_INTERACTIVE_TOKEN (3).
    $null = $tasks.RegisterTaskDefinition($taskName, $task, 6, $userSid, $null, 3, $null)
}

# Remove the previous registration only after the task change succeeds.
if ((Test-Path -LiteralPath $legacyRunKey) -and (Get-Item -LiteralPath $legacyRunKey).GetValueNames() -contains 'DeskMaster') {
    Remove-ItemProperty -LiteralPath $legacyRunKey -Name DeskMaster
}
