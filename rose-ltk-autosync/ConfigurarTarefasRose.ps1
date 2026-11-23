# ConfigurarTarefasRose.ps1
# Deve ser executado via UAC (Administrador) apenas para ajustar as tarefas agendadas e o hook IFEO:
# 1. Remove o gatilho de inicio automatico no logon da tarefa \Rose (so abre quando aceito).
# 2. Configura a tarefa \RoseAutoSync com RunLevel Highest para controlar Rose, LTK e Pengu Loader.
# 3. Se o Rose nao estiver rodando, garante config.ini com disabled = 1 e remove o hook IFEO do Pengu Loader.
# 4. Remove a entrada duplicada do Run no Registro e reinicia o monitor atualizado (sem tocar no LoL em andamento).

$ErrorActionPreference = 'Stop'
$currentUser = "$env:USERDOMAIN\$env:USERNAME"
$scriptDir = $PSScriptRoot
$vbsPath = Join-Path $scriptDir 'RoseAutoSync.vbs'
$configIniPath = Join-Path $env:LOCALAPPDATA 'Rose\config.ini'
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
$ifeoPaths = @(
    'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\LeagueClientUx.exe',
    'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\LeagueClientUx.exe'
)

if (-not (Test-Path $vbsPath)) {
    throw "Launcher nao encontrado em '$vbsPath'."
}

# 1. Remove duplicata em HKCU Run
Remove-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'RoseAutoSync' -ErrorAction SilentlyContinue

# 2. Remove todos os gatilhos de \Rose preservando Acao e Principal originais
$roseTask = Get-ScheduledTask -TaskName 'Rose' -ErrorAction SilentlyContinue
if ($null -ne $roseTask) {
    $roseXmlText = Export-ScheduledTask -TaskName 'Rose'
    [xml]$roseXml = $roseXmlText
    $triggersNode = $roseXml.DocumentElement.SelectSingleNode('*[local-name()="Triggers"]')
    if ($null -ne $triggersNode -and $triggersNode.HasChildNodes) {
        $triggersNode.RemoveAll()
        Register-ScheduledTask -TaskName 'Rose' -Xml $roseXml.OuterXml -Force | Out-Null
    }
}

# 3. Atualiza \RoseAutoSync para Interactive + HighestAvailable sem limite de tempo
$action = New-ScheduledTaskAction -Execute 'wscript.exe' -Argument ('"' + $vbsPath + '"')
$trigger = New-ScheduledTaskTrigger -AtLogOn -User $currentUser
$principal = New-ScheduledTaskPrincipal -UserId $currentUser -LogonType Interactive -RunLevel Highest
$settings = New-ScheduledTaskSettingsSet `
    -AllowStartIfOnBatteries `
    -DontStopIfGoingOnBatteries `
    -ExecutionTimeLimit ([TimeSpan]::Zero) `
    -MultipleInstances IgnoreNew `
    -StartWhenAvailable

Register-ScheduledTask -TaskName 'RoseAutoSync' -Action $action -Trigger $trigger -Principal $principal -Settings $settings -Force | Out-Null

# 4. Se o Rose nao estiver em execucao, desativa o Pengu Loader em config.ini (sem BOM) e remove o Debugger IFEO
#    Importante: nao encerra LeagueClient, LeagueClientUx, League of Legends nem rundll32.
$roseRunning = Get-Process -Name 'Rose' -ErrorAction SilentlyContinue
if (-not $roseRunning) {
    if (Test-Path $configIniPath) {
        $rawBytes = [System.IO.File]::ReadAllBytes($configIniPath)
        $hasBom = ($rawBytes.Length -ge 3 -and $rawBytes[0] -eq 0xEF -and $rawBytes[1] -eq 0xBB -and $rawBytes[2] -eq 0xBF)
        $content = $utf8NoBom.GetString($rawBytes, $(if ($hasBom) { 3 } else { 0 }), $rawBytes.Length - $(if ($hasBom) { 3 } else { 0 }))
        if ($content -match '(?m)^disabled\s*=\s*\d+') {
            $updated = [regex]::Replace($content, '(?m)^disabled\s*=\s*\d+', 'disabled = 1')
        }
        elseif ($content -match '(?m)^\[General\]\r?\n') {
            $updated = [regex]::Replace($content, '(?m)^(\[General\]\r?\n)', "`$1disabled = 1`r`n", 1)
        }
        else {
            $updated = "[General]`r`ndisabled = 1`r`n" + $content
        }
        [System.IO.File]::WriteAllText($configIniPath, $updated, $utf8NoBom)
    }

    foreach ($p in $ifeoPaths) {
        if (Test-Path $p) {
            $props = Get-ItemProperty -Path $p -Name 'Debugger' -ErrorAction SilentlyContinue
            if ($null -ne $props -and $props.Debugger -like '*Pengu Loader*core.dll*') {
                Remove-ItemProperty -Path $p -Name 'Debugger' -ErrorAction SilentlyContinue
            }
        }
    }
}

# 5. Encerra monitor antigo para liberar o Mutex e garante limpeza se o LoL estiver fechado
$oldWatchers = Get-CimInstance Win32_Process -Filter "Name = 'powershell.exe'" -ErrorAction SilentlyContinue |
    Where-Object { $_.CommandLine -like '*RoseAutoSync.ps1*' -and $_.ProcessId -ne $PID }
foreach ($proc in $oldWatchers) {
    Stop-Process -Id $proc.ProcessId -Force -ErrorAction SilentlyContinue
}

$lolRunning = Get-Process -Name 'LeagueClient', 'LeagueClientUx', 'League of Legends' -ErrorAction SilentlyContinue
if (-not $lolRunning) {
    Stop-Process -Name 'Rose', 'ltk_patcher_host', 'ltk-manager' -Force -ErrorAction SilentlyContinue
}

Start-Sleep -Milliseconds 400
Start-ScheduledTask -TaskName 'RoseAutoSync'
