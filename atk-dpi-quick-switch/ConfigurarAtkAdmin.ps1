# ConfigurarAtkAdmin.ps1
# Configura o AtkQuickSwitch para iniciar automaticamente com privilegio elevado (RunLevel Highest)
# no logon via Agendador de Tarefas, resolvendo o bloqueio UIPI do Windows em jogos/apps como Administrador
# sem exibir UAC e evitando instancia duplicada no HKCU Run.

$ErrorActionPreference = 'Stop'
$currentUser = "$env:USERDOMAIN\$env:USERNAME"
$scriptDir = $PSScriptRoot
$exePath = Join-Path $scriptDir 'AtkQuickSwitch.exe'

if (-not (Test-Path $exePath)) {
    throw "Executavel nao encontrado em '$exePath'. Execute '.\build.ps1' primeiro para compilar."
}

# 1. Remove entrada duplicada em HKCU Run (agora sera iniciado pela tarefa elevada \AtkQuickSwitch)
Remove-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'AtkQuickSwitch' -ErrorAction SilentlyContinue

# 2. Registra a tarefa \AtkQuickSwitch com Interactive + Highest
$action = New-ScheduledTaskAction -Execute $exePath -WorkingDirectory $scriptDir
$trigger = New-ScheduledTaskTrigger -AtLogOn -User $currentUser
$principal = New-ScheduledTaskPrincipal -UserId $currentUser -LogonType Interactive -RunLevel Highest
$settings = New-ScheduledTaskSettingsSet `
    -AllowStartIfOnBatteries `
    -DontStopIfGoingOnBatteries `
    -ExecutionTimeLimit ([TimeSpan]::Zero) `
    -MultipleInstances IgnoreNew `
    -StartWhenAvailable

Register-ScheduledTask -TaskName 'AtkQuickSwitch' -Action $action -Trigger $trigger -Principal $principal -Settings $settings -Force | Out-Null

# 3. Reinicia o processo atual ja no nivel elevado
Get-Process -Name 'AtkQuickSwitch' -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -eq $exePath } |
    Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 300
Start-ScheduledTask -TaskName 'AtkQuickSwitch'
