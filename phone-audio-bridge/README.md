# Phone Audio Bridge (`PhoneAudioBridge`)

Utilitário nativo e leve para Windows que transforma o **navegador do seu celular** (Android ou iPhone) em um receptor de áudio sem precisar instalar nenhum aplicativo no telefone — permitindo usar **qualquer fone Bluetooth pareado no celular** para ouvir o áudio do PC em tempo real via **QR Code**, com **modo Wi-Fi** e **modo Cabo USB de menor atraso**.

---

## Por que este projeto existe?

Muitos PCs de mesa não têm placa Bluetooth embutida (ou têm receptores USB 2.4 GHz proprietários de mouse/teclado/headset que não servem como Bluetooth). Em vez de ficar sem áudio ou instalar suítes pesadas, o **Phone Audio Bridge** cria uma ponte direta:

```text
[Áudio do Windows (WASAPI Loopback)] ──▶ [PhoneAudioBridge.exe] ──(Wi-Fi ou Cabo USB)──▶ [Navegador do Celular] ──▶ [Fone Bluetooth]
```

---

## Principais Recursos

* **QR Code gerado na hora (100% offline/nativo):** Escaneie com a câmera do celular para abrir o player direto no navegador (Chrome/Safari), sem instalar app no celular.
* **Dois modos otimizados de transmissão:**
  * **Modo Wi-Fi / Rede Local (~55 ms de buffer):** Prático e sem fios, usando pacotes de 20 ms via WebSocket.
  * **Modo Cabo USB (~20 ms de buffer):** Reduz drasticamente o *jitter* e o delay usando blocos de **10 ms**, com suporte automático a:
    1. **Ancoragem USB (*USB Tethering*):** Detecta automaticamente a placa de rede USB RNDIS/Apple/Android e ajusta o QR Code para o IP do cabo.
    2. **Túnel ADB (bônus para Android):** Se `adb.exe` estiver disponível no PC e a Depuração USB estiver ativa, cria `adb reverse tcp:8765 tcp:8765` automaticamente (`http://127.0.0.1:8765/?mode=usb`), dispensando Ancoragem USB e passando direto sem bloqueio de firewall.
* **Player Mobile com Controle Adaptativo de Drift (micro-PLL):** Ajusta suavemente a taxa de reprodução (`0.994x` a `1.006x`) para evitar acúmulo de atraso com o passar das horas, além de *silence gating* (não envia pacotes vazios quando o PC está em silêncio).
* **Seleção de dispositivo de saída & medidor de nível ao vivo:** Captura o dispositivo padrão do Windows ou uma saída específica, com barra VU em tempo real e alerta visual de conexão.
* **Bandeja do Sistema (`^`):** Permite ocultar na área de notificação, alternar entre modos Wi-Fi/USB e marcar **"Iniciar com o Windows"**.

---

## Como funciona tecnicamente

| Camada | Implementação / Tecnologia |
| :--- | :--- |
| **Captura de Áudio (Windows)** | **WASAPI Loopback** via **COM Interop puro** (`IMMDeviceEnumerator`, `IAudioClient`, `IAudioCaptureClient`) em thread dedicada com `timeBeginPeriod(1)`, convertendo Float32/PCM/multicanal para PCM Int16 estéreo sem bibliotecas externas. |
| **Servidor HTTP + WebSocket** | Servidor **TCP / HTTP / WebSocket (RFC 6455)** puro sobre `TcpListener` com `NoDelay = true` e fila limitada por cliente (3–4 pacotes) para nunca acumular atraso se a rede oscilar. |
| **Gerador de QR Code** | Codificador **ISO/IEC 18004 (Versões 1–5, Nível M)** escrito do zero em C# com aritmética GF(256), Reed-Solomon, seleção de máscara de menor penalidade e renderização GDI+ nítida. |
| **Player no Navegador** | **HTML5 + Web Audio API** (`AudioContext` com `latencyHint: 'interactive'`), slider de buffer manual (`15–150 ms`), volume (`0–150%`), medição real de ping RTT, `MediaSession` keepalive e `WakeLock`. |

---

## Como compilar e usar

### 1. Compilar o executável
Não é necessário instalar SDK nem Visual Studio — o script compila com o `csc.exe` nativo do Windows:
```powershell
cd phone-audio-bridge
.\build.ps1
```
Isso gera `PhoneAudioBridge.exe` na mesma pasta.

Para já compilar e copiar o executável portátil direto para a sua **Área de Trabalho**:
```powershell
.\build.ps1 -CopyToDesktop
```
*(O `PhoneAudioBridge.exe` é arquivo único portátil — não precisa de DLLs nem de outros arquivos ao lado.)*

### 2. Executar o aplicativo
```powershell
Start-Process .\PhoneAudioBridge.exe
```

### 3. Liberar Firewall (necessário para Wi-Fi ou Ancoragem USB)
Se o celular não conseguir abrir o link pelo QR Code, clique no botão **"Liberar Firewall (Admin)"** dentro do próprio app, ou execute:
```powershell
powershell -ExecutionPolicy Bypass -File .\LiberarFirewall.ps1
```

### 4. Como usar o Modo Cabo USB (Menor Delay)
1. No app do Windows, clique em **Cabo USB (Menor Delay)**.
2. Plugue o cabo USB do celular no computador:
   - **Android:** vá em **Configurações → Rede e internet / Ponto de acesso → Ancoragem USB** (*USB Tethering*) e ative. O app detecta o IP USB automaticamente e atualiza o QR Code.
   - **iPhone:** vá em **Ajustes → Acesso Pessoal**, ative e toque em **Confiar** no aviso de computador.
   - *(Opcional Android com ADB)*: com **Depuração USB** ativa e `adb.exe` instalado, clique em **Tentar Túnel ADB** e acesse `http://127.0.0.1:8765/?mode=usb` no Chrome do celular.
3. Conecte seu fone Bluetooth no celular, escaneie o QR Code e toque em **Conectar e Ouvir Áudio**.

> **Dica sobre o som no PC:** Como a captura usa *WASAPI Loopback*, o app copia o áudio que está tocando na saída selecionada no Windows. Se você não quiser ouvir o som simultaneamente em outra caixa/headset do PC, selecione como saída no Windows um monitor sem alto-falantes ligados (ou reduza o volume físico da caixa) e deixe o volume do Windows normal — ou ajuste o dispositivo desejado no combo **Fonte de Áudio**.

---

## O que acontece se eu desligar e ligar o Bluetooth do celular?

* **A conexão PC ↔ celular (Wi-Fi ou Cabo USB) continua ativa:** o Bluetooth existe apenas entre o **celular e o fone**. Você **não precisa** reiniciar o app no PC nem escanear o QR Code de novo.
* **Ao desligar o Bluetooth:** o fone desconecta do celular e, por padrão no Android/iOS, o sistema costuma pausar/suspender o áudio da aba (`AudioContext`) para evitar que o som do PC saia de repente no alto-falante do telefone.
* **Ao religar o Bluetooth e reconectar o fone:** o player detecta a troca de dispositivo/foco e tenta retomar o áudio automaticamente na mesma aba. Se o navegador mantiver o áudio suspenso por restrição do sistema (ex.: tela bloqueada), basta abrir a aba já conectada e tocar no botão **Retomar Áudio** (ou **Desconectar** → **Conectar e Ouvir Áudio**).

---

## Autoteste técnico (`--self-test`)

O executável inclui uma suíte de verificação automática que valida WASAPI Loopback, detecção de rede, síndromes Reed-Solomon do QR Code e handshake HTTP/WebSocket local:
```powershell
.\PhoneAudioBridge.exe --self-test .\selftest_out.txt .\selftest_qr.png
Get-Content .\selftest_out.txt
```

---

## Arquivos desta pasta

* [`Program.cs`](./Program.cs) — Código-fonte completo (WASAPI Loopback COM, Servidor HTTP/WS RFC 6455, Gerador QR Code, Player HTML5 embutido e UI WinForms + Bandeja).
* [`build.ps1`](./build.ps1) — Script de compilação via `.NET Framework 4.x` (`csc.exe`).
* [`LiberarFirewall.ps1`](./LiberarFirewall.ps1) — Cria a regra de entrada TCP `8765` no Firewall do Windows (elevação UAC).
