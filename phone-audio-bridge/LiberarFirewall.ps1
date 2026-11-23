param(
    [int]$Port = 8765
)

$ErrorActionPreference = 'Stop'

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal($identity)
$isAdmin = $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

if (-not $isAdmin) {
    Write-Output "Solicitando permissão de Administrador para liberar a porta TCP $Port no Firewall..."
    $args = "-NoProfile -ExecutionPolicy Bypass -File `"$($MyInvocation.MyCommand.Path)`" -Port $Port"
    Start-Process powershell -Verb RunAs -ArgumentList $args -Wait
    exit 0
}

$ruleName = "PhoneAudioBridge TCP $Port"
netsh advfirewall firewall delete rule name="$ruleName" | Out-Null
netsh advfirewall firewall add rule name="$ruleName" dir=in action=allow protocol=TCP localport=$Port profile=any | Out-Null

if ($LASTEXITCODE -ne 0) {
    throw "Falha ao criar regra no Firewall do Windows para a porta $Port."
}

Write-Output "Regra '$ruleName' aplicada com sucesso (Entrada TCP $Port, todos os perfis)."
