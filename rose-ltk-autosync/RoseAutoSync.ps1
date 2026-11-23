# RoseAutoSync.ps1
# Controla Rose, LTK (ltk-manager / ltk_patcher_host), Pengu Loader (config.ini + IFEO) e Porofessor/Overwolf:
# - Ao iniciar fora do LoL: encerra Rose, LTK e Overwolf/Porofessor e desativa o Pengu Loader.
# - Ao abrir o League of Legends: inicia o Porofessor/Overwolf e pergunta se deseja rodar Rose e LTK.
#   - Sim: reativa o Pengu Loader (disabled=0 + IFEO) e inicia a tarefa \Rose (ou Rose.exe).
#   - Nao / Fechar / Tempo esgotado: encerra Rose/LTK, desativa o Pengu Loader (disabled=1 + remove IFEO) e limpa a UX se ja injetada.
# - Ao fechar o League of Legends: encerra Rose, LTK e Overwolf/Porofessor e desativa o Pengu Loader.

$ErrorActionPreference = 'SilentlyContinue'

# Garante instancia unica do monitor usando Mutex
$mutexName = "Global\RoseAutoSyncWatcherMutex"
$createdNew = $false
$mutex = New-Object System.Threading.Mutex($true, $mutexName, [ref]$createdNew)
if (-not $createdNew) {
    exit 0
}

$roseLocalDir = Join-Path $env:LOCALAPPDATA "Rose"
$logPath = Join-Path $roseLocalDir "RoseAutoSync.log"
$roseExe = "C:\Program Files\Rose\Rose.exe"
$overwolfExe = "C:\Program Files (x86)\Overwolf\OverwolfLauncher.exe"
$porofessorArgs = "-launchapp pibhbkkgefgheeglaeemkkfjlhidhcedalapdggh -from-desktop"

$configIniPath = Join-Path $roseLocalDir "config.ini"
$penguLoaderDir = Join-Path $roseLocalDir "Pengu Loader"
$penguCoreDll = Join-Path $penguLoaderDir "core.dll"
$penguExe = Join-Path $penguLoaderDir "Pengu Loader.exe"
$penguDebuggerValue = 'rundll32 "' + $penguCoreDll + '", #6000 '
$ifeoPaths = @(
    "HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\LeagueClientUx.exe",
    "HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\LeagueClientUx.exe"
)
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)

$lolProcessNames = @("LeagueClient", "LeagueClientUx", "League of Legends")
$roseProcessNames = @("Rose")
$ltkProcessNames = @("ltk-manager", "ltk_patcher_host")
$overwolfProcessNames = @("Overwolf", "OverwolfBrowser", "OverwolfHelper", "OverwolfHelper64", "OverwolfLauncher")

function Write-SyncLog {
    param([string]$Message)
    try {
        $line = "[{0}] {1}" -f (Get-Date -Format "yyyy-MM-dd HH:mm:ss"), $Message
        Add-Content -Path $logPath -Value $line -Encoding UTF8 -ErrorAction SilentlyContinue
        if (Test-Path $logPath) {
            $lines = Get-Content -Path $logPath -ErrorAction SilentlyContinue
            if ($null -ne $lines -and $lines.Count -gt 200) {
                $lines | Select-Object -Last 120 | Set-Content -Path $logPath -Encoding UTF8 -ErrorAction SilentlyContinue
            }
        }
    }
    catch {
    }
}

function Test-AnyProcessRunning {
    param([string[]]$Names)
    $procs = Get-Process -Name $Names -ErrorAction SilentlyContinue
    return ($null -ne $procs -and @($procs).Count -gt 0)
}

function Test-LolRunning {
    return (Test-AnyProcessRunning -Names $lolProcessNames)
}

function Test-LolMatchRunning {
    return (Test-AnyProcessRunning -Names @("League of Legends"))
}

function Test-RoseRunning {
    return (Test-AnyProcessRunning -Names $roseProcessNames)
}

function Test-LtkRunning {
    return (Test-AnyProcessRunning -Names $ltkProcessNames)
}

function Test-OverwolfRunning {
    return (Test-AnyProcessRunning -Names @("Overwolf", "OverwolfLauncher", "OverwolfBrowser"))
}

function Test-PenguInjectedInClient {
    $procs = Get-CimInstance Win32_Process -Filter "Name = 'rundll32.exe'" -ErrorAction SilentlyContinue |
        Where-Object { $_.CommandLine -like '*Pengu Loader*core.dll*' }
    return ($null -ne $procs -and @($procs).Count -gt 0)
}

function Set-RoseConfigDisabled {
    param([bool]$Disabled)
    try {
        if (-not (Test-Path $configIniPath)) {
            return
        }
        $targetVal = if ($Disabled) { "1" } else { "0" }
        $rawBytes = [System.IO.File]::ReadAllBytes($configIniPath)
        $hasBom = ($rawBytes.Length -ge 3 -and $rawBytes[0] -eq 0xEF -and $rawBytes[1] -eq 0xBB -and $rawBytes[2] -eq 0xBF)
        $content = $utf8NoBom.GetString($rawBytes, $(if ($hasBom) { 3 } else { 0 }), $rawBytes.Length - $(if ($hasBom) { 3 } else { 0 }))

        if ($content -match '(?m)^disabled\s*=\s*\d+') {
            $updated = [regex]::Replace($content, '(?m)^disabled\s*=\s*\d+', "disabled = $targetVal")
        }
        elseif ($content -match '(?m)^\[General\]\r?\n') {
            $updated = [regex]::Replace($content, '(?m)^(\[General\]\r?\n)', "`$1disabled = $targetVal`r`n", 1)
        }
        else {
            $updated = "[General]`r`ndisabled = $targetVal`r`n" + $content
        }

        if ($hasBom -or $updated -ne $content) {
            [System.IO.File]::WriteAllText($configIniPath, $updated, $utf8NoBom)
        }
    }
    catch {
        Write-SyncLog "Erro ao atualizar config.ini: $($_.Exception.Message)"
    }
}

function Test-PenguIfeoActive {
    $props = Get-ItemProperty -Path $ifeoPaths[0] -Name 'Debugger' -ErrorAction SilentlyContinue
    return ($null -ne $props -and $props.Debugger -like '*Pengu Loader*core.dll*')
}

function Disable-RosePengu {
    param([string]$Reason = "desativar")
    Set-RoseConfigDisabled -Disabled $true
    foreach ($p in $ifeoPaths) {
        if (Test-Path $p) {
            $props = Get-ItemProperty -Path $p -Name 'Debugger' -ErrorAction SilentlyContinue
            if ($null -ne $props -and $props.Debugger -like '*Pengu Loader*core.dll*') {
                Remove-ItemProperty -Path $p -Name 'Debugger' -ErrorAction SilentlyContinue
            }
        }
    }
    Write-SyncLog "Disable-RosePengu ($Reason): disabled=1, ifeoAtivo=$(Test-PenguIfeoActive)"
}

function Enable-RosePengu {
    param([string]$Reason = "ativar")
    Set-RoseConfigDisabled -Disabled $false
    if (Test-Path $penguCoreDll) {
        $primaryIfeo = $ifeoPaths[0]
        if (-not (Test-Path $primaryIfeo)) {
            New-Item -Path $primaryIfeo -Force -ErrorAction SilentlyContinue | Out-Null
        }
        Set-ItemProperty -Path $primaryIfeo -Name 'Debugger' -Value $penguDebuggerValue -Type String -ErrorAction SilentlyContinue
    }
    Write-SyncLog "Enable-RosePengu ($Reason): disabled=0, ifeoAtivo=$(Test-PenguIfeoActive)"
}

function Request-CleanLeagueClientUxRestart {
    if ((Test-LolMatchRunning) -or -not (Test-AnyProcessRunning -Names @("LeagueClientUx"))) {
        return
    }
    if (Test-Path $penguExe) {
        Start-Process -FilePath $penguExe -ArgumentList "--restart-client", "--silent" -WindowStyle Hidden -Wait -ErrorAction SilentlyContinue
        Write-SyncLog "Solicitado reinicio limpo da UX do LoL (Pengu removido)."
    }
}

function Confirm-RoseAndLtkLaunch {
    $shell = New-Object -ComObject WScript.Shell -ErrorAction Stop
    # 4 = Sim/Nao, 32 = Pergunta, 256 = Botao Nao padrao, 4096 = SystemModal, 65536 = SetForeground
    $popupFlags = 4 -bor 32 -bor 256 -bor 4096 -bor 65536
    $msg = "O League of Legends abriu.`r`n`r`nQuer rodar o Rose e o LTK agora?`r`n(Se clicar em Nao, eles serao fechados e nao ficarao em segundo plano.)"
    [int]$answer = $shell.Popup($msg, 30, "Abrir Rose e LTK?", $popupFlags)
    return $answer
}

function Start-RoseApp {
    $task = Get-ScheduledTask -TaskName "Rose" -ErrorAction SilentlyContinue
    if ($null -ne $task) {
        Start-ScheduledTask -TaskName "Rose" -ErrorAction SilentlyContinue
        Write-SyncLog "Usuario aceitou: tarefa \Rose iniciada."
        return
    }
    if (Test-Path $roseExe) {
        Start-Process -FilePath $roseExe -ErrorAction SilentlyContinue
        Write-SyncLog "Usuario aceitou: Rose.exe iniciado diretamente."
    }
}

function Stop-RoseAndLtk {
    param([string]$Reason = "limpeza")
    $hadTask = $false
    $task = Get-ScheduledTask -TaskName "Rose" -ErrorAction SilentlyContinue
    if ($null -ne $task -and $task.State -eq 'Running') {
        $hadTask = $true
        Stop-ScheduledTask -TaskName "Rose" -ErrorAction SilentlyContinue
    }

    $targets = $roseProcessNames + $ltkProcessNames
    $wasRunning = (Test-AnyProcessRunning -Names $targets) -or $hadTask

    # Fecha Rose antes do LTK para evitar que o Rose reinicie o ltk_patcher_host
    Stop-Process -Name $roseProcessNames -Force -ErrorAction SilentlyContinue
    Stop-Process -Name $ltkProcessNames -Force -ErrorAction SilentlyContinue

    if (Test-AnyProcessRunning -Names $targets) {
        Start-Sleep -Milliseconds 400
        Stop-Process -Name $roseProcessNames -Force -ErrorAction SilentlyContinue
        Stop-Process -Name $ltkProcessNames -Force -ErrorAction SilentlyContinue
    }

    Disable-RosePengu -Reason $Reason

    $stillAlive = Test-AnyProcessRunning -Names $targets
    if ($wasRunning -or $stillAlive) {
        Write-SyncLog "Stop-RoseAndLtk ($Reason): pendente=$($stillAlive)"
    }
}

function Stop-AllLolCompanions {
    param([string]$Reason = "lol-fechado")
    Stop-RoseAndLtk -Reason $Reason
    Stop-Process -Name $overwolfProcessNames -Force -ErrorAction SilentlyContinue
    Remove-ItemProperty -Path "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run" -Name "Overwolf" -ErrorAction SilentlyContinue
}

Write-SyncLog "Monitor RoseAutoSync iniciado (PID=$PID)."

# Ao iniciar o monitor: se o LoL estiver fechado, garante que nada fique aberto e que Pengu esteja desativado
if (-not (Test-LolRunning)) {
    Stop-AllLolCompanions -Reason "inicio-sem-lol"
}

# Estados por sessao do LoL: 'none' | 'accepted' | 'declined'
$sessionDecision = 'none'
$declineGraceUntil = [datetime]::MinValue
$lastOverwolfAttempt = [datetime]::MinValue

while ($true) {
    $lolRunning = Test-LolRunning
    $roseRunning = Test-RoseRunning
    $ltkRunning = Test-LtkRunning
    $owRunning = Test-OverwolfRunning

    if ($lolRunning) {
        $now = Get-Date
        if (-not $owRunning -and (Test-Path $overwolfExe) -and ($now - $lastOverwolfAttempt).TotalSeconds -ge 20) {
            $lastOverwolfAttempt = $now
            Start-Process -FilePath $overwolfExe -ArgumentList $porofessorArgs -ErrorAction SilentlyContinue
        }

        if ($sessionDecision -eq 'none' -and -not (Test-LolMatchRunning)) {
            [int]$answer = -1
            try {
                $answer = Confirm-RoseAndLtkLaunch
            }
            catch {
                Write-SyncLog "Falha ao exibir popup de confirmacao: $($_.Exception.Message)"
                $answer = -1
            }

            if ($answer -eq 6 -and (Test-LolRunning)) {
                $sessionDecision = 'accepted'
                Write-SyncLog "Usuario aceitou Rose/LTK (resposta=6)."
                Enable-RosePengu -Reason "usuario-aceitou"
                if (-not (Test-RoseRunning)) {
                    Start-RoseApp
                }
            }
            else {
                $sessionDecision = 'declined'
                $declineGraceUntil = (Get-Date).AddSeconds(12)
                Write-SyncLog "Usuario recusou Rose/LTK (resposta=$answer)."
                Stop-RoseAndLtk -Reason "usuario-nao-aceitou"
                if ((Test-PenguInjectedInClient) -and -not (Test-LolMatchRunning)) {
                    Write-SyncLog "Pengu residual detectado no cliente apos recusa; limpando UX."
                    Request-CleanLeagueClientUxRestart
                }
            }
        }
        elseif ($sessionDecision -eq 'declined' -and (Get-Date) -lt $declineGraceUntil) {
            if ($roseRunning -or $ltkRunning) {
                Stop-RoseAndLtk -Reason "bloqueio-pos-recusa"
            }
        }
    }
    elseif ($sessionDecision -ne 'none' -or $roseRunning -or $ltkRunning -or $owRunning) {
        # Confirma que o LoL realmente fechou (o cliente pode reiniciar brevemente ao trocar de tela)
        Start-Sleep -Seconds 5
        if (-not (Test-LolRunning)) {
            $sessionDecision = 'none'
            $declineGraceUntil = [datetime]::MinValue
            Stop-AllLolCompanions -Reason "lol-encerrado"
        }
    }

    Start-Sleep -Seconds 3
}
