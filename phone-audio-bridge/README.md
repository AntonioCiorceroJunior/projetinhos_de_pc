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
  * **Modo Wi-Fi / Rede Local (~60 ms de buffer):** Prático e sem fios, usando pacotes de 20 ms via WebSocket.
  * **Modo Cabo USB (~15 ms de buffer / 10 ms por bloco):** Alinhado 1:1 ao período nativo de 10 ms do WASAPI (480 frames a 48 kHz) com suporte automático a:
    1. **Ancoragem USB (*USB Tethering*):** Detecta automaticamente a placa de rede USB RNDIS/Apple/Android e troca o QR Code para o IP do cabo assim que a ancoragem é ativada.
    2. **Túnel ADB (recomendado para 10–15 ms liso no Android):** Se `adb.exe` estiver disponível no PC e a Depuração USB estiver ativa, cria `adb reverse tcp:8765 tcp:8765` automaticamente (`http://127.0.0.1:8765/?mode=usb`), liberando `AudioWorklet` em contexto seguro e dispensando firewall.
* **Motor Ring-Buffer Contínuo (ASRC PI + Hermite + PLC) & Desconexão Limpa:** Substitui agendamentos fragmentados por um ring buffer pré-alocado (`16384` frames) com compensação contínua de *clock drift* via controlador PI, interpolação cúbica de Hermite, ocultação suave de micro-perdas (*PLC* por espelhamento) e **desconexão definitiva** se o Bluetooth/áudio do celular for desligado — sem reconectar sozinho depois.
* **Seleção de dispositivo de saída & medidor de nível ao vivo:** Captura o dispositivo padrão do Windows ou uma saída específica, com barra VU em tempo real e alerta visual de conexão.
* **Bandeja do Sistema (`^`):** Permite ocultar na área de notificação, alternar entre modos Wi-Fi/USB e marcar **"Iniciar com o Windows"**.

---

## Como funciona tecnicamente

| Camada | Implementação / Tecnologia |
| :--- | :--- |
| **Captura de Áudio (Windows)** | **WASAPI Loopback Event-Driven** (`AUDCLNT_STREAMFLAGS_LOOPBACK \| EVENTCALLBACK`) + **MMCSS `"Pro Audio"`** (`avrt.dll`) + `timeBeginPeriod(1)`, com caminho rápido direto para blocos de **10 ms** (480 frames) e fallback automático para polling. |
| **Servidor HTTP + WebSocket** | Servidor **TCP / HTTP / WebSocket (RFC 6455)** puro sobre `TcpListener` com `NoDelay = true`, thread `WS-Writer` elevada via MMCSS `"Pro Audio"`, frame binário único pré-montado (zero cópia por cliente) e fila anti-drop de 24 pacotes. |
| **Gerador de QR Code** | Codificador **ISO/IEC 18004 (Versões 1–5, Nível M)** escrito do zero em C# com aritmética GF(256), Reed-Solomon, seleção de máscara de menor penalidade e renderização GDI+ nítida. |
| **Player no Navegador** | **Ring-Buffer Contínuo** em **`AudioWorklet` (2,7 ms / 128 samples)** quando em contexto seguro (`127.0.0.1` via ADB) ou **`ScriptProcessorNode(256)` (`5,3 ms`)** em HTTP local; controlador **PI**, **interpolação cúbica de Hermite**, **PLC por espelhamento** de onda, slider `10–180 ms`, presets (`10 / 15 / 60 ms`), `MediaSession` e `WakeLock`. |

---

## Engenharia de Baixa Latência (10–15 ms) e Referências

Para manter o áudio liso em **10–15 ms** sem picotes mesmo no navegador do celular, o projeto combina técnicas usadas em áudio profissional e streaming em tempo real:

1. **Alinhamento 1:1 com o motor WASAPI do Windows (`10 ms`) + Event-Driven + MMCSS `"Pro Audio"`:**
   - O mixer compartilhado do Windows processa áudio em períodos de **10,0 ms** (480 samples a 48 kHz). Usar `AUDCLNT_STREAMFLAGS_EVENTCALLBACK` acorda a thread de captura imediatamente via evento do kernel quando o bloco fecha, e `AvSetMmThreadCharacteristicsW("Pro Audio")` protege a captura e o envio TCP contra atrasos do agendador (documentação oficial Microsoft WASAPI / MMCSS).
2. **Thread de Áudio dedicada (`AudioWorklet`) vs Thread Principal (`ScriptProcessorNode`):**
   - Navegadores só expõem `AudioWorklet` (quantum fixo de 128 samples = **2,67 ms** em thread C++ dedicada, sem pausas de Garbage Collector da UI) em **contextos seguros** (`https://` ou `http://127.0.0.1`).
   - Pelo **Túnel ADB** (`http://127.0.0.1:8765/?mode=usb`), o Chrome Android já entra direto em **Worklet 2,7 ms**.
   - Se usar **Ancoragem USB** sem ADB (`http://192.168.x.x`), o app usa automaticamente o mesmo Ring Buffer em `ScriptProcessorNode(256)` (`5,3 ms`). *(Usuários avançados no Chrome Android também podem habilitar `chrome://flags/#unsafely-treat-insecure-origin-as-secure` com a URL USB para liberar o `AudioWorklet` fora de `127.0.0.1`.)*
3. **ASRC (Controlador PI + Interpolação Cúbica de Hermite) e PLC por Espelhamento:**
   - Inspirado em arquiteturas abertas como **`JoJoBond/3LAS`**, **WebRTC `NetEq`** e **`AudioRelay`**: em vez de agendar vários `AudioBufferSourceNode` separados (que acumulam jitter e travam em GC), o fluxo passa por um **Ring Buffer circular pré-alocado** (`16384` frames) reamostrado continuamente via **interpolação cúbica de Hermite (4 pontos Catmull-Rom)** guiada por um **controlador PI** suave (`±1,4%`), absorvendo diferenças de clock (48 kHz PC ↔ 44,1/48 kHz celular).
   - Se algum pacote atrasar 1–6 ms na rede/USB, o **PLC (*Packet Loss Concealment*)** espelha suavemente a forma de onda recente com decaimento e faz *crossfade* de 0,67 ms na volta — evitando o "tec/picote" seco de zerar o som.
4. **Atenção à latência do próprio Bluetooth (Celular → Fone):**
   - A ponte **PC → Celular** consegue rodar em **10–15 ms** pelo Cabo USB, mas o enlace **Bluetooth** entre o celular e o fone adiciona sua própria latência (tipicamente **100–200 ms** em SBC/AAC comum, ou **40–80 ms** em modos *Low Latency* / *Gaming Mode* do fone). Para menor atraso total em vídeos e jogos, ative o **Modo Jogo / Baixa Latência** do seu fone Bluetooth (quando disponível).

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
   - **Android:** vá em **Configurações → Rede e internet / Ponto de acesso → Ancoragem USB** (*USB Tethering*) e ative. Assim que a placa USB subir no Windows, o app muda automaticamente o QR Code para o IP do cabo (`[Cabo USB] ...`). **Escaneie ou abra esse novo link USB** no navegador (se continuar na aba antiga do Wi-Fi, o áudio ainda estará passando pelo Wi-Fi!).
   - **iPhone:** vá em **Ajustes → Acesso Pessoal**, ative e toque em **Confiar** no aviso de computador.
   - *(Opcional Android com ADB)*: com **Depuração USB** ativa e `adb.exe` instalado, clique em **Tentar Túnel ADB** e acesse `http://127.0.0.1:8765/?mode=usb` no Chrome do celular.
3. Conecte seu fone Bluetooth no celular, escaneie o QR Code atualizado e toque em **Conectar e Ouvir Áudio**.

> **Dica sobre o som no PC:** Como a captura usa *WASAPI Loopback*, o app copia o áudio que está tocando na saída selecionada no Windows. Se você não quiser ouvir o som simultaneamente em outra caixa/headset do PC, selecione como saída no Windows um monitor sem alto-falantes ligados (ou reduza o volume físico da caixa) e deixe o volume do Windows normal — ou ajuste o dispositivo desejado no combo **Fonte de Áudio**.

---

## O que acontece se eu desligar e ligar o Bluetooth do celular?

* **Não reconecta sozinho ao PC:** como o computador não tem pareamento Bluetooth direto com o fone, religar o Bluetooth do celular **nunca** vai conectar o fone ao computador por conta própria — mesmo com o PC ligado.
* **Desconexão automática ao desligar o Bluetooth:** quando você desliga o Bluetooth (ou desconecta o fone) enquanto a aba está aberta, o Android/iOS pausa o áudio da página e o player **encerra na hora a sessão com o PC** (fecha o canal WebSocket e desliga qualquer tentativa de reconexão em segundo plano).
* **Ao religar o Bluetooth depois:** o fone conecta normalmente apenas ao seu celular. O áudio do PC só volta a tocar se você abrir o player e tocar manualmente em **Conectar e Ouvir Áudio**.

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
