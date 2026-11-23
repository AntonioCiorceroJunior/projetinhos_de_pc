param(
    [switch]$CopyToDesktop,
    [switch]$RebuildAndroid
)

$ErrorActionPreference = 'Stop'

$dir = Split-Path -Parent $MyInvocation.MyCommand.Path
$src = Join-Path $dir 'Program.cs'
$out = Join-Path $dir 'PhoneAudioBridge.exe'
$apk = Join-Path $dir 'PhoneAudioReceiver.apk'
$androidBuild = Join-Path $dir 'android\build_android.ps1'
$csc = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"

if (-not (Test-Path $csc)) {
    throw "csc.exe not found at $csc"
}

if ($RebuildAndroid -or -not (Test-Path $apk)) {
    if (-not (Test-Path $androidBuild)) {
        throw "Android build script not found at $androidBuild"
    }
    Write-Output "Building embedded Android receiver APK..."
    & $androidBuild
    if ($LASTEXITCODE -ne 0) {
        exit $LASTEXITCODE
    }
}

if (-not (Test-Path $apk)) {
    throw "Embedded APK missing: $apk"
}

Get-Process -Name 'PhoneAudioBridge' -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -eq $out } |
    Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 300

& $csc /codepage:65001 /nologo /target:winexe /optimize+ /platform:x64 "/resource:$apk,PhoneAudioReceiver.apk" "/out:$out" /r:System.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll $src
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

Write-Output "Build succeeded: $out"

if ($CopyToDesktop) {
    $desktopDir = [Environment]::GetFolderPath('Desktop')
    if ([string]::IsNullOrWhiteSpace($desktopDir)) {
        throw "Could not resolve Desktop path."
    }
    $desktopExe = Join-Path $desktopDir 'PhoneAudioBridge.exe'
    Get-Process -Name 'PhoneAudioBridge' -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -eq $desktopExe } |
        Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 200
    Copy-Item -Path $out -Destination $desktopExe -Force
    Write-Output "Copied executable to Desktop: $desktopExe"
}
