# Projetinhos de PC

Coleção de utilitários nativos sob medida para Windows (além de configurações personalizadas de jogo) criados para resolver necessidades reais do dia a dia — controle direto de periféricos sem software pesado em segundo plano, ponte nativa de áudio Windows → Android via cabo USB/ADB (C++ AAudio), automação inteligente ligada a jogos, indicadores em tempo real na bandeja do sistema e binds de treino no CS2.

---

## Para que serve este repositório?

Muitos drivers e softwares de fabricantes (como suites de mouse/headset e overlays) consomem bastante memória RAM, iniciam múltiplos serviços extras e muitas vezes não oferecem exatamente o comportamento que queremos.

Este repositório reúne **5 projetos independentes**, organizados por funcionalidade, que conversam diretamente com o hardware, com a rede local/USB e com as APIs nativas do Windows usando apenas recursos já embutidos no sistema operacional.

---

## Lógica de pensamento por trás dos projetos

Todos os utilitários seguem quatro princípios:

1. **Conversar direto com o hardware e com o Windows (sem camadas pesadas):**
   Em vez de subir apps Electron/Chromium ou dependências de terceiros, usamos chamadas nativas do Windows (**Win32 P/Invoke** em `hid.dll`, `setupapi.dll`, `user32.dll`, `kernel32.dll` e **COM Interop WASAPI**) para trocar pacotes HID diretamente com receptores 2.4 GHz / USB, capturar áudio loopback ou ler propriedades do sistema.
2. **Zero dependências externas de runtime:**
   Os utilitários em C# são compilados diretamente pelo compilador nativo do **.NET Framework 4.x** (`csc.exe`) que já vem instalado em todo Windows 10/11 — basta um único executável leve ou scripts PowerShell/VBScript nativos.
3. **Automação orientada a eventos reais:**
   Os programas reagem ao contexto real de uso: detectam quando o *League of Legends* abre ou fecha, ajustam automaticamente o *polling rate* de 8000 Hz para 1000 Hz durante a partida, oferecem um pop-up sob demanda para carregar ou bloquear mods/overlays, detectam o celular Android plugado no cabo USB com pop-up de conexão e atualizam ícones sem roubar o foco da tela cheia.
4. **Higiene de execução e estabilidade:**
   - **Instância única (`Mutex`)** para nunca duplicar processos.
   - **Limpeza de recursos** (destruição explícita de ícones `HICON` no GDI+, encerramento limpo da bandeja sem ícones fantasma e restauração de arquivos sem BOM).
   - **Separação de privilégios** via Agendador de Tarefas (*Task Scheduler*) quando o Windows exige execução elevada (ex.: contornar bloqueio UIPI em jogos abertos como Administrador ou editar hooks IFEO no Registro).

---

## Catálogo de Projetos

| Projeto | Pasta | O que resolve | Tecnologias principais |
| :--- | :--- | :--- | :--- |
| **ATK DPI & Polling Rate Quick Switch** | [`atk-dpi-quick-switch/`](./atk-dpi-quick-switch/README.md) | Troca instantânea de DPI (800 / 1600 / 3200) e Polling Rate (1K ↔ 8K) via atalhos globais com OSD flutuante e redução automática para 1000 Hz no LoL para evitar travamentos no motor do jogo. | C# (.NET 4.x), Win32 HID (`hid.dll` / `setupapi.dll`), `RegisterHotKey` + `Raw Input`, WinForms + GDI+, PowerShell |
| **Rose & LTK AutoSync** | [`rose-ltk-autosync/`](./rose-ltk-autosync/README.md) | Garante que Rose, LTK Manager, Pengu Loader e Porofessor/Overwolf não fiquem abertos fora do LoL; ao abrir o cliente, abre o Porofessor e pergunta se você quer ativar Rose/LTK naquela sessão. | PowerShell, VBScript (`WScript.Shell`), Windows Task Scheduler, Registro (IFEO), WMI/CIM |
| **Peripheral Battery Tray** | [`peripheral-battery-tray/`](./peripheral-battery-tray/README.md) | Mostra a bateria em tempo real com **1 ícone separado por periférico** na bandeja oculta (`^`) do Windows (ATK X1 Ultimate 2.0, Corsair HS80 RGB e Bluetooth automático). | C# (.NET 4.x), Win32 HID Overlapped I/O, protocolo Corsair Bragi, SetupAPI Bluetooth (`DEVPKEY_Bluetooth_Battery`), WinForms + GDI+ |
| **Phone Audio Bridge** | [`phone-audio-bridge/`](./phone-audio-bridge/README.md) | Transforma o celular Android em receptor de áudio **100% nativo via Cabo USB (ADB)** para usar fone Bluetooth no PC sem adaptador Bluetooth nem navegador: captura WASAPI Event-Driven (`10 ms` + keepalive em silêncio), abre pop-up automático ao plugar o cabo, implanta sozinho o mini-APK C++ **AAudio** (`localabstract` Unix socket, zero TCP/Nagle) e traz controle adaptativo de latência (`10 → 1 ms` silencioso ou auto-estabilização sob demanda). | C# (.NET 4.x), WASAPI Loopback (`avrt.dll` Pro Audio), `adb forward localabstract`, Android NDK C++17 (`libaaudio`), JNI, `ForegroundService` (`mediaPlayback`) |
| **CS2 Treino & Binds** | [`cs2-treino-binds/`](./cs2-treino-binds/README.md) | Arquivos `.cfg` prontos para treino no Counter-Strike 2: modo treino livre de granadas/spray com binds (`V`, `I`, `K`, `O`, `P`), modo vs 10 bots difíceis (`treino2`), `autoexec.cfg` e instalador automático. | Source 2 CFG (`treino.cfg`, `treino2.cfg`, `autoexec.cfg`), PowerShell |

---

## Tecnologias utilizadas no repositório

* **C# (.NET Framework 4.x / `csc.exe`):** Linguagem principal para os utilitários de bandeja, áudio e comunicação HID de baixa latência, compilados diretamente pelo compilador nativo `C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe`.
* **Win32 API (`P/Invoke`) & COM Interop:**
  * `hid.dll` e `setupapi.dll`: descoberta de interfaces USB HID (`VID/PID`, `UsagePage/Usage`), leitura/escrita de relatórios HID e consulta de propriedades Bluetooth do sistema.
  * **WASAPI Loopback (`IMMDeviceEnumerator`, `IAudioClient`, `IAudioCaptureClient`)**: captura direta do fluxo de áudio do Windows sem drivers virtuais nem bibliotecas externas.
  * `kernel32.dll` / `winmm.dll`: I/O assíncrono (`FILE_FLAG_OVERLAPPED` + `CancelIoEx`) e temporização precisa (`timeBeginPeriod`).
  * `user32.dll`: atalhos globais (`RegisterHotKey`), captura direta de teclado (`Raw Input`), janelas OSD sem roubar foco (`WS_EX_NOACTIVATE`) e gerenciamento de handles `HICON`.
* **Túnel USB/ADB Nativo & AAudio C++ (JNI/NDK):** Ponte direta via `adb forward` + socket Unix `localabstract` com protocolo binário enxuto (sem HTTP, sem WebSocket, sem navegador e sem firewall), alimentando um Ring-Buffer *lock-free* em C++17 com reamostragem cúbica de Hermite + controlador PI + AAudio `PERFORMANCE_MODE_LOW_LATENCY` em `ForegroundService` no Android.
* **Windows Forms + GDI+:** Renderização dinâmica de ícones 32×32 na bandeja do sistema, QR Codes nítidos e cards visuais com transparência e bordas arredondadas.
* **PowerShell + VBScript (`wscript.exe`):** Automação de sistema, manipulação de processos, regras de Firewall e criação de tarefas agendadas; o `.vbs` permite rodar o monitor PowerShell em segundo plano sem piscar janela de console.
* **Windows Task Scheduler & Registro (`HKCU` / `HKLM`):** Inicialização automática com ou sem elevação (Admin/UIPI), configuração de persistência e controle de ganchos *Image File Execution Options (IFEO)*.
* **Counter-Strike 2 (Source 2 CFG):** Scripts de treino com aliases, binds de utilitários e ajustes de `autoexec`.

---

## Estrutura do repositório

```text
projetinhos_de_pc/
├── .gitignore
├── README.md
├── atk-dpi-quick-switch/
│   ├── AtkQuickSwitch.cs          # Código-fonte C# (HID, Raw Input, OSD, watcher LoL)
│   ├── ConfigurarAtkAdmin.ps1     # Registra tarefa agendada elevada (Admin/UIPI)
│   ├── build.ps1                  # Compila AtkQuickSwitch.exe com csc.exe
│   └── README.md                  # Documentação detalhada do projeto
├── cs2-treino-binds/
│   ├── InstalarCfgsCS2.ps1        # Localiza a pasta cfg do CS2 e instala com backup
│   ├── README.md                  # Lista completa das binds, aliases e uso
│   ├── autoexec.cfg               # FPS, console, baixa latência e telemetria
│   ├── treino.cfg                 # Treino livre (granadas, spray, noclip, binds V/I/K/O/P)
│   └── treino2.cfg                # Treino real contra 10 bots difíceis (TR ou CT)
├── peripheral-battery-tray/
│   ├── Program.cs                 # Código-fonte C# (ATK + Corsair HS80 + Bluetooth + ícones)
│   ├── build.ps1                  # Compila PeripheralBatteryTray.exe com csc.exe
│   └── README.md                  # Documentação detalhada e explicação sobre limitações 2.4 GHz
├── phone-audio-bridge/
│   ├── android/                   # Receptor Android nativo C++17 AAudio + JNI (sem Gradle)
│   ├── Program.cs                 # App Windows C# (WASAPI, ADB auto-deploy, latência adaptativa, UI/bandeja)
│   ├── build.ps1                  # Compila APK (opcional) + embute no PhoneAudioBridge.exe via csc.exe
│   └── README.md                  # Guia completo da arquitetura USB/ADB nativa v2.0
└── rose-ltk-autosync/
    ├── ConfigurarTarefasRose.ps1  # Configura tarefas \Rose e \RoseAutoSync + limpeza IFEO
    ├── RoseAutoSync.ps1           # Monitor em loop (LoL, Rose, LTK, Pengu, Porofessor)
    ├── RoseAutoSync.vbs           # Launcher oculto sem janela de console
    └── README.md                  # Documentação detalhada do projeto
```

---

## Como clonar e rodar

### Pré-requisitos
* **Windows 10 ou Windows 11 (64 bits)**
* **PowerShell** e **.NET Framework 4.x** *(ambos já vêm instalados nativamente no Windows)*
* **Git** (opcional, para clonar via terminal)

### 1. Clonar o repositório
```powershell
git clone https://github.com/AntonioCiorceroJunior/projetinhos_de_pc.git
cd projetinhos_de_pc
```

### 2. Escolher e rodar o projeto desejado

* **ATK DPI & Polling Rate Quick Switch:**
  ```powershell
  cd atk-dpi-quick-switch
  .\build.ps1
  Start-Process .\AtkQuickSwitch.exe
  ```
  *(Para funcionar também dentro de jogos abertos como Administrador e iniciar com o Windows, execute `.\ConfigurarAtkAdmin.ps1` em um PowerShell **como Administrador** — veja [`atk-dpi-quick-switch/README.md`](./atk-dpi-quick-switch/README.md).)*

* **Rose & LTK AutoSync:**
  ```powershell
  cd rose-ltk-autosync
  # Execute em um PowerShell como Administrador:
  .\ConfigurarTarefasRose.ps1
  ```
  *(Veja todos os detalhes e pré-requisitos em [`rose-ltk-autosync/README.md`](./rose-ltk-autosync/README.md).)*

* **Peripheral Battery Tray:**
  ```powershell
  cd peripheral-battery-tray
  .\build.ps1
  Start-Process .\PeripheralBatteryTray.exe
  ```
  *(Clique com o botão direito em qualquer ícone gerado na setinha `^` e marque **"Iniciar com o Windows"** se desejar inicialização automática — veja [`peripheral-battery-tray/README.md`](./peripheral-battery-tray/README.md).)*

* **Phone Audio Bridge:**
  ```powershell
  cd phone-audio-bridge
  .\build.ps1
  Start-Process .\PhoneAudioBridge.exe
  ```
  *(Ao plugar o celular Android via cabo USB com Depuração USB ativa, o app exibe um pop-up para instalar/conectar o receptor nativo C++ AAudio automaticamente em 1 clique — veja [`phone-audio-bridge/README.md`](./phone-audio-bridge/README.md).)*

* **CS2 Treino & Binds:**
  ```powershell
  cd cs2-treino-binds
  .\InstalarCfgsCS2.ps1
  ```
  *(Copia `treino.cfg`, `treino2.cfg` e `autoexec.cfg` para a pasta `game\csgo\cfg` do CS2; no console do jogo execute `exec treino` ou `exec treino2` — veja [`cs2-treino-binds/README.md`](./cs2-treino-binds/README.md).)*

> **Nota sobre Política de Execução do PowerShell:** caso o Windows bloqueie a execução de algum `.ps1` localmente, rode com:
> `powershell -ExecutionPolicy Bypass -File .\nome-do-script.ps1`

---

## Por que alguns dispositivos sem fio não mostram bateria no PC?

Em resumo simples: enquanto dispositivos **Bluetooth** seguem um padrão universal do Windows que sempre informa a bateria, muitos receptores USB **2.4 GHz** (principalmente de teclados e headsets plug-and-play) funcionam como uma **via de mão única** — eles só enviam teclas ou áudio para o PC e o chip do receptor não implementa nenhum canal de resposta para perguntas de bateria.

Já mouses/headsets com canal bidirecional (como o **ATK X1 Ultimate 2.0** e o **Corsair HS80 RGB**) permitem enviar uma pergunta pelo protocolo HID e receber a porcentagem exata em tempo real. A explicação completa com analogia e exemplos reais está em [`peripheral-battery-tray/README.md`](./peripheral-battery-tray/README.md#por-que-alguns-dispositivos-n%C3%A3o-conseguem-mostrar-a-bateria).
