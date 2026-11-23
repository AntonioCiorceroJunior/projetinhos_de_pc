$ErrorActionPreference = 'Stop'

$dir = Split-Path -Parent $MyInvocation.MyCommand.Path
$src = Join-Path $dir 'Program.cs'
$out = Join-Path $dir 'PhoneAudioBridge.exe'
$csc = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"

if (-not (Test-Path $csc)) {
    throw "csc.exe not found at $csc"
}

Get-Process -Name 'PhoneAudioBridge' -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -eq $out } |
    Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 300

& $csc /codepage:65001 /nologo /target:winexe /optimize+ /platform:x64 "/out:$out" /r:System.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll $src
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

Write-Output "Build succeeded: $out"
