# Phone Audio Bridge v2.0 — USB Nativo (C# + C++ AAudio)

Utilitário nativo e portátil para Windows que transforma o **celular Android conectado no cabo USB** em um receptor de áudio de baixíssima latência para o PC — **sem navegador, sem QR Code, sem Wi-Fi e sem precisar liberar firewall** — permitindo usar qualquer fone Bluetooth (ou fone P2/USB-C) conectado ao celular para ouvir o áudio do computador com atraso mínimo.

---

## Como funciona na prática

```text
[Áudio do Windows (WASAPI Loopback 10 ms + Âncora Silenciosa 48 kHz)]
    ──▶ [PhoneAudioBridge.exe (C# .NET 4.x + MMCSS "Pro Audio", NoDelay)]
    ──▶ [adb forward tcp:8765 localabstract:phoneaudiobridge]  (Unix socket = zero TCP/Nagle no Android)
    ──▶ [PhoneAudioReceiver (Mini-APK C++17 AAudio LOW_LATENCY em ForegroundService)]
    ──▶ [Fone Bluetooth / P2 / USB-C]
```

1. **Pluge e conecte em 1 clique:** deixe o `PhoneAudioBridge.exe` aberto (ou minimizado na bandeja `^`). Assim que você pluga o cabo USB com **Depuração USB** ativa, o aplicativo detecta o aparelho pelo ADB e abre um pop-up perguntando: *"Deseja conectar o áudio do PC neste aparelho pelo cabo USB agora?"*.
2. **Instalação automática do receptor (embutido no `.exe`):** o mini-APK Android (`PhoneAudioReceiver.apk`, `arm64-v8a`) já vai **embutido dentro do próprio `PhoneAudioBridge.exe`**. Na primeira conexão, o app instala o receptor automaticamente via `adb install -r -g`, abre o túnel Unix `localabstract:phoneaudiobridge` e inicia a reprodução em primeiro/segundo plano (`ForegroundService` tipo `mediaPlayback`).
3. **Continua tocando com a tela desligada:** ao contrário do navegador Chrome no Android, o `AudioBridgeService` usa `FOREGROUND_SERVICE_TYPE_MEDIA_PLAYBACK` + `PARTIAL_WAKE_LOCK` + stream nativo C++ AAudio. Pode apagar a tela do celular sem engasgos.

---

## Latência Adaptativa Inteligente (`1–120 ms`, padrão `10 ms`)

O motor de controle adaptativo (`AdaptiveLatencyController`) busca o menor atraso físico suportado pelo seu conjunto PC + USB + celular sem incomodar:

* **Padrão inicial em `10 ms`:** já abre sincronizado 1:1 com o período nativo de `10 ms` do mixer WASAPI do Windows (`480 frames @ 48 kHz`).
* **Redução silenciosa automática (`10 → 9 → ... → 1 ms`):**
  * Se passar **`6,0 s` consecutivos com `0` cortes**, o app reduz silenciosamente **`-1 ms`** sem exibir nenhum pop-up e testa por `3,5 s`.
  * Enquanto permanecer em `0` cortes, continua descendo `1 ms` por etapa até **`1 ms`**.
  * Se detectar qualquer corte na etapa reduzida, volta silenciosamente **`+1 ms`** e trava naquele piso mínimo estável (sem oscilar).
* **Estabilização assistida sob demanda:**
  * Se ocorrerem cortes sustentados (**`≥ 4` eventos em `3,0 s`**), aparece um aviso na tela perguntando se você deseja estabilizar automaticamente.
  * Clicando em **Sim**, o app sobe o buffer em degraus (`+2 ms` abaixo de `20 ms`, `+5 ms` acima de `20 ms`, máximo `120 ms`) a cada `2,5 s` até completar `4,0 s` seguidos com `0` cortes.
  * Se você mexer no slider manual (`1–120 ms`), o piso anterior é zerado e o ajuste automático aguarda `10 s` antes de voltar a otimizar.

---

## Engenharia de Baixa Latência & Resultados Medidos em Hardware Real

### 1. Eliminação da trava TCP/Nagle (`adb reverse` 47 ms → `localabstract` 2 ms)
* Na v1 (navegador via `adb reverse` TCP), o `adbd` do Android acumulava frames de áudio por **`40–80 ms`** esperando *Delayed ACK* do kernel Linux.
* Na v2 nativa, usamos **`adb forward tcp:8765 localabstract:phoneaudiobridge`**: o cliente Android conecta em um socket Unix *abstract* direto (sem pilha TCP dentro do Android), com protocolo binário enxuto (`CONFIG`, `PCM`, `SET_TARGET_MS`, `SET_GAIN`, `PING/PONG`, `STATS`, `STOP`).

### 2. Relógio mestre WASAPI por Âncora Silenciosa (`IAudioRenderClient`)
* No Windows, o `WASAPI Loopback` compartilhado para de disparar eventos quando nenhum som está tocando. Um *keepalive* por timer gera colisões e rajadas duplicadas com o driver ao voltar do silêncio.
* A solução definitiva usada em capturadores profissionais (OBS/MSDN) é abrir um **`IAudioClient` + `IAudioRenderClient` silencioso compartilhado** (`AUDCLNT_BUFFERFLAGS_SILENT = 2`) no mesmo dispositivo de saída: custo zero de CPU, áudio 100% mudo, mantendo o hardware WASAPI pulsando exatamente em **`48.000,0 Hz`** (`10,0 ms` contínuos).

### 3. Telemetria real medida no **Motorola Edge 60 Pro (Android 17, `arm64-v8a`)**
* **Caminho de áudio Android:** `AAudio` `PERFORMANCE_MODE_LOW_LATENCY` no `AudioOut_1D` (`FastMixer`), `48.000 Hz`, `burst = 256 frames` (`5,33 ms`), `buf = 512 frames` (`10,67 ms`), **`0` xruns de HAL**.
* **Transporte USB ADB:** `pktGapMaxMs = 10–12 ms`, `rttMs = 2–4 ms`, **`0` resyncs**.
* **Comportamento adaptativo ao vivo:** iniciou estável em **`10 ms`**, desceu sozinho para **`9 ms`** após `6 s` limpos, detectou `1` micro-underrun em `9 ms` e travou automaticamente no piso físico de **`10 ms`**.

> **Nota física sobre o Bluetooth (Celular → Fone):** a ponte **PC → Celular** opera na casa de **`~10 ms`** com `0` resyncs, mas o próprio enlace Bluetooth A2DP entre celular e fone adiciona a latência inerente ao codec/buffer do fone (tipicamente `100–200 ms`, ou `40–80 ms` se o fone tiver *Modo Jogo*). No cabo P2/USB-C do celular o atraso ponta-a-ponta cai ao mínimo possível.

---

## Desconexão limpa ao desligar o Bluetooth (sem reconectar sozinho)

* O `AudioBridgeService` monitora simultaneamente **`ACTION_AUDIO_BECOMING_NOISY`**, **`AudioDeviceCallback.onAudioDevicesRemoved`** (A2DP, BLE Audio, fones com fio/USB-C) e o **`errorCallback` (`AAUDIO_ERROR_DISCONNECTED`)** do AAudio.
* Ao desligar o Bluetooth ou remover o fone — mesmo com a tela do celular apagada — o receptor nativo **encerra imediatamente** o AAudio, envia o sinal `0x86` ao PC para desligar o envio, fecha o túnel e encerra o serviço (`START_NOT_STICKY`).
* O app nunca se registra para eventos de reconexão Bluetooth e mantém o aparelho silenciado no Watcher até que você desconecte/reconecte o cabo ou clique manualmente em **Conectar e Ouvir Agora**.

---

## Como compilar e usar

### Pré-requisitos
* **Windows 10/11 (64 bits)** com `.NET Framework 4.x` nativo.
* **`adb.exe`** (Google Platform-Tools — descoberto automaticamente via pasta do exe, `ANDROID_HOME`, `PATH` do Usuário/Máquina e pacotes WinGet).
* **Para recompilar o APK Android (`-RebuildAndroid`):** JDK 17 + Android SDK Platform 35 + Build-Tools 35.0.0 + NDK (`arm64-v8a`, C++17 com `-static-libstdc++` e alinhamento `-Wl,-z,max-page-size=16384` para páginas de 16 KB do Android 15–17). *(Se o arquivo `PhoneAudioReceiver.apk` já estiver compilado na pasta, `.\build.ps1` usa-o direto sem precisar de NDK/SDK.)*

### Compilar o executável portátil (e opcionalmente copiar para a Área de Trabalho)
```powershell
cd phone-audio-bridge
.\build.ps1 -RebuildAndroid -CopyToDesktop
```
*(Sem recompilar a parte Android: `.\build.ps1 -CopyToDesktop`)*

### Executar
```powershell
Start-Process .\PhoneAudioBridge.exe
```
Flags opcionais de linha de comando:
* `--minimized`: inicia direto oculto na bandeja do sistema (`^`) — usado pela opção **"Iniciar com o Windows"**.
* `--auto-connect`: conecta automaticamente no primeiro celular USB detectado ao abrir.
* `--self-test [caminho_relatorio.txt]`: executa a bateria de autoteste.

---

## Autoteste técnico (`--self-test`)

Valida **WASAPI Loopback Event-Driven**, integridade do **`PhoneAudioReceiver.apk` embutido** (`classes.dex` + `lib/arm64-v8a/libphoneaudioreceiver.so`), descoberta do **`adb.exe`**, **protocolo binário loopback** (`CONFIG`, `PCM`, `SET_TARGET_MS`, `PING/PONG`, `STATS`, `STOP`) e máquina de estados do **`AdaptiveLatencyController`**:

```powershell
.\PhoneAudioBridge.exe --self-test .\selftest_out.txt
Get-Content .\selftest_out.txt
```

---

## Arquivos desta pasta

* [`Program.cs`](./Program.cs) — App Windows completo em C# 5.0 (WASAPI Loopback + âncora render silenciosa, ADB auto-deploy, cliente binário `localabstract`, `AdaptiveLatencyController`, UI WinForms, pop-ups e bandeja).
* [`build.ps1`](./build.ps1) — Compila `PhoneAudioBridge.exe` com o `PhoneAudioReceiver.apk` embutido como recurso gerenciado (`/resource`).
* [`android/AndroidManifest.xml`](./android/AndroidManifest.xml) — Manifesto (`minSdk 29`, `targetSdk 35`, `FOREGROUND_SERVICE_MEDIA_PLAYBACK`).
* [`android/jni/native_audio_engine.cpp`](./android/jni/native_audio_engine.cpp) — Motor C++17 (socket Unix `localabstract`, ring-buffer lock-free SPSC, Hermite cúbica, PI ASRC, crossfade Hann, PLC, AAudio `LOW_LATENCY` e JNI).
* [`android/java/com/antoniociorcero/phoneaudiobridge/`](./android/java/com/antoniociorcero/phoneaudiobridge/) — `NativeBridge.java`, `AudioBridgeService.java` (`ForegroundService` + desconexão Bluetooth limpa) e `BridgeActivity.java`.
* [`android/build_android.ps1`](./android/build_android.ps1) — Compila `libphoneaudioreceiver.so`, empacota DEX/APK com `aapt2`/`javac`/`d8`, alinha com `zipalign` e assina via `apksigner` sem Gradle.
