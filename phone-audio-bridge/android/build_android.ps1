$ErrorActionPreference = 'Stop'

$androidDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$bridgeDir = Split-Path -Parent $androidDir
$buildDir = Join-Path $androidDir 'build'
$outApk = Join-Path $bridgeDir 'PhoneAudioReceiver.apk'

# 1. Locate JDK 17+
$jdkDir = $null
if ($env:JAVA_HOME -and (Test-Path (Join-Path $env:JAVA_HOME 'bin\javac.exe'))) {
    $jdkDir = $env:JAVA_HOME
} else {
    $jdkDir = Get-ChildItem 'C:\Program Files\Microsoft\jdk-*' -Directory -ErrorAction SilentlyContinue |
        Sort-Object Name -Descending | Select-Object -First 1 -ExpandProperty FullName
}
if (-not $jdkDir -or -not (Test-Path (Join-Path $jdkDir 'bin\javac.exe'))) {
    throw "JDK 17+ not found. Checked JAVA_HOME and C:\Program Files\Microsoft\jdk-*"
}
$javac = Join-Path $jdkDir 'bin\javac.exe'
$keytool = Join-Path $jdkDir 'bin\keytool.exe'

# 2. Locate Android SDK / Build-Tools / NDK
$sdkRoot = @($env:ANDROID_HOME, $env:ANDROID_SDK_ROOT, 'C:\Android\Sdk', (Join-Path $env:LOCALAPPDATA 'Android\Sdk')) |
    Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
if (-not $sdkRoot) {
    throw "Android SDK not found under ANDROID_HOME, C:\Android\Sdk or LocalAppData\Android\Sdk"
}

$platformJar = Get-ChildItem (Join-Path $sdkRoot 'platforms\android-*\android.jar') -ErrorAction SilentlyContinue |
    Sort-Object FullName -Descending | Select-Object -First 1 -ExpandProperty FullName
if (-not $platformJar) { throw "No android.jar found in $sdkRoot\platforms" }

$btDir = Get-ChildItem (Join-Path $sdkRoot 'build-tools') -Directory -ErrorAction SilentlyContinue |
    Sort-Object Name -Descending | Select-Object -First 1 -ExpandProperty FullName
if (-not $btDir) { throw "No build-tools found in $sdkRoot\build-tools" }

$aapt2 = Join-Path $btDir 'aapt2.exe'
$d8 = Join-Path $btDir 'd8.bat'
$zipalign = Join-Path $btDir 'zipalign.exe'
$apksigner = Join-Path $btDir 'apksigner.bat'

$ndkDir = Get-ChildItem (Join-Path $sdkRoot 'ndk') -Directory -ErrorAction SilentlyContinue |
    Sort-Object Name -Descending | Select-Object -First 1 -ExpandProperty FullName
if (-not $ndkDir) { throw "No NDK found in $sdkRoot\ndk" }

$clangpp = Join-Path $ndkDir 'toolchains\llvm\prebuilt\windows-x86_64\bin\aarch64-linux-android29-clang++.cmd'
if (-not (Test-Path $clangpp)) { throw "NDK clang++ not found: $clangpp" }

Write-Output "JDK:      $jdkDir"
Write-Output "SDK:      $sdkRoot"
Write-Output "Platform: $platformJar"
Write-Output "BuildBt:  $btDir"
Write-Output "NDK:      $ndkDir"

# Prepare clean staging dirs
if (Test-Path $buildDir) {
    Get-ChildItem $buildDir -Exclude 'debug.keystore' | Remove-Item -Recurse -Force
}
$objDir = Join-Path $buildDir 'obj'
$libDir = Join-Path $buildDir 'lib\arm64-v8a'
$dexDir = Join-Path $buildDir 'dex'
$genDir = Join-Path $buildDir 'gen'
New-Item -ItemType Directory -Path $objDir, $libDir, $dexDir, $genDir -Force | Out-Null

# 3. Compile C++17 AAudio JNI library (arm64-v8a + 16KB page alignment)
$cppSrc = Join-Path $androidDir 'jni\native_audio_engine.cpp'
$soOut = Join-Path $libDir 'libphoneaudioreceiver.so'
Write-Output "=== Compiling C++17 AAudio JNI library ==="
& $clangpp -shared -fPIC -O3 -std=c++17 -static-libstdc++ -Wall -Wextra '-Wl,-z,max-page-size=16384' -o $soOut $cppSrc -laaudio -llog
if ($LASTEXITCODE -ne 0) { throw "clang++ failed ($LASTEXITCODE)" }

# 4. Link AndroidManifest into base APK via aapt2
$manifest = Join-Path $androidDir 'AndroidManifest.xml'
$unalignedApk = Join-Path $buildDir 'unaligned.apk'
$alignedApk = Join-Path $buildDir 'aligned.apk'
Write-Output "=== Running aapt2 link ==="
& $aapt2 link -o $unalignedApk --manifest $manifest -I $platformJar --java $genDir --auto-add-overlay
if ($LASTEXITCODE -ne 0) { throw "aapt2 link failed ($LASTEXITCODE)" }

# 5. Compile Java sources
$javaFiles = @(Get-ChildItem (Join-Path $androidDir 'java') -Filter '*.java' -Recurse | ForEach-Object { $_.FullName })
Write-Output "=== Compiling $($javaFiles.Count) Java sources ==="
& $javac --release 11 -encoding UTF-8 -classpath $platformJar -d $objDir @javaFiles
if ($LASTEXITCODE -ne 0) { throw "javac failed ($LASTEXITCODE)" }

# 6. Convert .class to classes.dex via d8
$classFiles = @(Get-ChildItem $objDir -Filter '*.class' -Recurse | ForEach-Object { $_.FullName })
Write-Output "=== Converting to DEX via d8 ==="
$env:JAVA_HOME = $jdkDir
& $d8 --min-api 29 --lib $platformJar --output $dexDir @classFiles
if ($LASTEXITCODE -ne 0) { throw "d8 failed ($LASTEXITCODE)" }
$dexFile = Join-Path $dexDir 'classes.dex'
if (-not (Test-Path $dexFile)) { throw "classes.dex was not generated" }

# 7. Package classes.dex + lib/arm64-v8a/libphoneaudioreceiver.so into unaligned.apk
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$fs = [System.IO.File]::Open($unalignedApk, [System.IO.FileMode]::Open, [System.IO.FileAccess]::ReadWrite)
try {
    $zip = New-Object System.IO.Compression.ZipArchive($fs, [System.IO.Compression.ZipArchiveMode]::Update)
    try {
        [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $dexFile, 'classes.dex', [System.IO.Compression.CompressionLevel]::Optimal) | Out-Null
        [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $soOut, 'lib/arm64-v8a/libphoneaudioreceiver.so', [System.IO.Compression.CompressionLevel]::NoCompression) | Out-Null
    }
    finally {
        $zip.Dispose()
    }
}
finally {
    $fs.Dispose()
}

# 8. Align APK (4-byte zipalign, preserves uncompressed page-aligned .so)
Write-Output "=== Aligning APK ==="
& $zipalign -f -p 4 $unalignedApk $alignedApk
if ($LASTEXITCODE -ne 0) { throw "zipalign failed ($LASTEXITCODE)" }

# 9. Create debug.keystore if needed & sign APK
$keystore = Join-Path $buildDir 'debug.keystore'
if (-not (Test-Path $keystore)) {
    Write-Output "=== Generating debug.keystore ==="
    & $keytool -genkeypair -v -keystore $keystore -storepass android -alias androiddebugkey -keypass android -keyalg RSA -keysize 2048 -validity 10000 -dname 'CN=PhoneAudioBridge,O=ProjetinhosDePc,C=BR'
    if ($LASTEXITCODE -ne 0) { throw "keytool failed ($LASTEXITCODE)" }
}

Write-Output "=== Signing APK ==="
& $apksigner sign --ks $keystore --ks-key-alias androiddebugkey --ks-pass pass:android --key-pass pass:android --out $outApk $alignedApk
if ($LASTEXITCODE -ne 0) { throw "apksigner sign failed ($LASTEXITCODE)" }

Write-Output "=== Verifying APK ==="
& $apksigner verify --verbose $outApk
if ($LASTEXITCODE -ne 0) { throw "apksigner verify failed ($LASTEXITCODE)" }

$apkSize = (Get-Item $outApk).Length
Write-Output "Android APK build succeeded: $outApk ($apkSize bytes)"
