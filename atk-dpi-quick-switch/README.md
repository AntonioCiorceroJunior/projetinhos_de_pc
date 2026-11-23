# ATK DPI & Polling Rate Quick Switch (`AtkQuickSwitch`)

Utilitário nativo para Windows que permite alternar **DPI (800 / 1600 / 3200)** e **Polling Rate (1000 Hz ↔ 8000 Hz)** do mouse **ATK X1 Ultimate 2.0** via atalhos globais ou menu da bandeja, exibindo um **OSD flutuante** no topo da tela e reduzindo automaticamente o polling rate para **1000 Hz** enquanto uma partida de *League of Legends* está aberta.

---

## Para que serve?

1. **Troca instantânea sem abrir o ATK HUB:** Alterne DPI e Polling Rate de qualquer lugar usando `Ctrl+Alt+F10` / `F11` sem precisar abrir interface pesada.
2. **Correção automática para o League of Legends:** O motor do LoL costuma apresentar quedas de FPS/engasgos em **8000 Hz** (`8K`). O aplicativo monitora o processo `League of Legends`:
   - Se a partida abrir e o mouse estiver em `8000 Hz`, ele muda automaticamente para `1000 Hz`.
   - Quando a partida termina, restaura automaticamente para `8000 Hz` *(apenas se a redução tiver sido feita automaticamente)*.
3. **Feedback visual sem roubar foco:** Mostra um card moderno (OSD) no topo da tela usando `WS_EX_NOACTIVATE` e `WS_EX_TRANSPARENT`, sem minimizar ou tirar foco do jogo.

---

## Como funciona (Lógica e Tecnologias)

| Componente | Tecnologia / Detalhe técnico |
| :--- | :--- |
| **Comunicação USB/2.4G** | Win32 P/Invoke (`hid.dll` + `setupapi.dll`) buscando dispositivos **`VID 0x373B`**, `UsagePage 0xFF02`, `Usage 0x0002` com relatórios de 17 bytes (Report ID `0x08`). |
| **Leitura e gravação na EEPROM** | Lê e grava os registradores do mouse via comandos `0x08` (leitura) e `0x07` (*SetEEPROM*), calculando o byte de CRC (`85 - valor`) e decodificando a tabela de DPI do sensor **PAW3950 Ultra**. |
| **Captura híbrida de atalhos** | Combina `RegisterHotKey` (`WM_HOTKEY`) com **Raw Input** (`WM_INPUT` + `RIDEV_INPUTSINK`) com deduplicação de 150 ms — assim o atalho funciona mesmo dentro de jogos em tela cheia ou com foco exclusivo. |
| **OSD flutuante** | `Form` sem bordas desenhado com **GDI+** (`LinearGradientBrush`, cantos arredondados) e `SW_SHOWNOACTIVATE` para nunca roubar o foco. |
| **Instância única e CLI** | Protegido por `Mutex` (`Local\AtkQuickSwitch_SingleInstance`), além de suportar mudança direta via linha de comando (`--set-rate`). |

---

## Atalhos e Controles

| Ação | Atalho | Observação |
| :--- | :--- | :--- |
| **Alternar DPI** (`800 → 1600 → 3200`) | `Ctrl + Alt + F10` | Atualiza o estágio ativo na EEPROM do mouse |
| **Alternar Polling Rate** (`1000 Hz ↔ 8000 Hz`) | `Ctrl + Alt + F11` | Cancela o estado de troca automática manual da sessão |
| **Mostrar status atual (OSD)** | `Ctrl + Alt + F12` | Também acionável com duplo clique no ícone da bandeja (`8K`) |

### Modo linha de comando (CLI)
Você pode definir o polling rate diretamente por script ou terminal (`125`, `250`, `500`, `1000`, `2000`, `4000`, `8000`):
```powershell
.\AtkQuickSwitch.exe --set-rate 1000
```

---

## Como compilar e rodar

### 1. Compilar o executável
Não é necessário instalar SDK nem Visual Studio — o script usa o `csc.exe` nativo do Windows:
```powershell
cd atk-dpi-quick-switch
.\build.ps1
```
Isso gera `AtkQuickSwitch.exe` na mesma pasta.

### 2. Testar manualmente
```powershell
Start-Process .\AtkQuickSwitch.exe
```
Um ícone **`8K`** aparecerá na área de notificação do Windows com menu de clique direito para escolher DPI específico (`800`, `1600`, `3200`), alternar Polling Rate e sair.

### 3. Instalar na inicialização com privilégio elevado (Recomendado para jogos)
Por padrão, o Windows bloqueia mensagens de atalho global vindas de apps comuns para janelas abertas como **Administrador** (proteção **UIPI**). O script [`ConfigurarAtkAdmin.ps1`](./ConfigurarAtkAdmin.ps1) resolve isso registrando uma tarefa no Agendador de Tarefas com `RunLevel Highest` (sem pedir tela de UAC no logon):

Abra o **PowerShell como Administrador** e execute:
```powershell
cd atk-dpi-quick-switch
powershell -ExecutionPolicy Bypass -File .\ConfigurarAtkAdmin.ps1
```

---

## Arquivos desta pasta

* [`AtkQuickSwitch.cs`](./AtkQuickSwitch.cs) — Código-fonte completo da aplicação.
* [`build.ps1`](./build.ps1) — Script de compilação via `.NET Framework 4.x` (`csc.exe`).
* [`ConfigurarAtkAdmin.ps1`](./ConfigurarAtkAdmin.ps1) — Configura inicialização automática elevada no Windows Task Scheduler.
