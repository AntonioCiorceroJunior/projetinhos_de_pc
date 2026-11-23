param(
    [string]$Cs2CfgPath = ""
)

$ErrorActionPreference = 'Stop'

function Find-Cs2CfgDirectory {
    param([string]$ExplicitPath)

    if (-not [string]::IsNullOrWhiteSpace($ExplicitPath)) {
        if (Test-Path $ExplicitPath) {
            return $ExplicitPath
        }
        throw "Caminho informado não encontrado: $ExplicitPath"
    }

    $defaultCandidates = @(
        "$env:ProgramFiles (x86)\Steam\steamapps\common\Counter-Strike Global Offensive\game\csgo\cfg",
        "$env:ProgramFiles\Steam\steamapps\common\Counter-Strike Global Offensive\game\csgo\cfg",
        "D:\SteamLibrary\steamapps\common\Counter-Strike Global Offensive\game\csgo\cfg",
        "E:\SteamLibrary\steamapps\common\Counter-Strike Global Offensive\game\csgo\cfg"
    )

    foreach ($cand in $defaultCandidates) {
        if (Test-Path $cand) {
            return $cand
        }
    }

    $vdfCandidates = @(
        "$env:ProgramFiles (x86)\Steam\steamapps\libraryfolders.vdf",
        "$env:ProgramFiles\Steam\steamapps\libraryfolders.vdf"
    )
    foreach ($vdf in $vdfCandidates) {
        if (Test-Path $vdf) {
            $lines = Get-Content -Path $vdf -Encoding UTF8
            foreach ($line in $lines) {
                if ($line -match '"path"\s+"([^"]+)"') {
                    $libRoot = $Matches[1] -replace '\\\\', '\'
                    $cfg = Join-Path $libRoot 'steamapps\common\Counter-Strike Global Offensive\game\csgo\cfg'
                    if (Test-Path $cfg) {
                        return $cfg
                    }
                }
            }
        }
    }

    return ""
}

$sourceDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$targetDir = Find-Cs2CfgDirectory -ExplicitPath $Cs2CfgPath

if ([string]::IsNullOrWhiteSpace($targetDir)) {
    throw "Pasta cfg do CS2 não encontrada automaticamente. Informe com -Cs2CfgPath 'C:\...\game\csgo\cfg'."
}

$files = @('treino.cfg', 'treino2.cfg', 'autoexec.cfg')
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'

foreach ($name in $files) {
    $src = Join-Path $sourceDir $name
    $dst = Join-Path $targetDir $name

    if (-not (Test-Path $src)) {
        throw "Arquivo de origem ausente: $src"
    }

    if (Test-Path $dst) {
        $srcHash = (Get-FileHash -Path $src -Algorithm SHA256).Hash
        $dstHash = (Get-FileHash -Path $dst -Algorithm SHA256).Hash
        if ($srcHash -ne $dstHash) {
            $bak = "$dst.$stamp.bak"
            Copy-Item -Path $dst -Destination $bak -Force
            Write-Output "Backup criado: $bak"
        }
    }

    Copy-Item -Path $src -Destination $dst -Force
    Write-Output "Instalado: $dst"
}

Write-Output "Concluído! No console do CS2 use 'exec treino' ou 'exec treino2'."
