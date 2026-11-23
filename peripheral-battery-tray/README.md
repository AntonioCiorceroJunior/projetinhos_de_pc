# Peripheral Battery Tray (`PeripheralBatteryTray`)

Utilitário nativo e super leve para Windows que cria **1 ícone separado por periférico** na bandeja oculta (`^`, perto do relógio), mostrando a porcentagem exata da bateria em tempo real sem depender de suítes pesadas ou telas abertas.

---

## O que ele mostra e faz

* **1 ícone independente por dispositivo:**
  - **Mouse ATK X1 Ultimate 2.0** (`VID 0x373B` — cabo USB e dongle 8K sem fio)
  - **Headset Corsair HS80 RGB Wireless** (`VID 0x1B1C` — dongle sem fio `0x0A6B` e cabo USB `0x0A69`)
  - **Qualquer periférico Bluetooth compatível** conectado ao Windows (detecção automática)
* **Ícone dinâmico em 32×32 px (GDI+):** Exibe o número da bateria em destaque, um mini símbolo identificando o tipo (mouse / headset / Bluetooth), barra proporcional e cores por nível:
  - 🟢 **Verde** (≥ 50%) · 🟡 **Amarelo** (21–49%) · 🔴 **Vermelho** (≤ 20%) · 🔵 **Ciano + raio** (Carregando) · ⚪ **Cinza `Zz`/`--`** (Em repouso / Desligado)
* **Tooltip detalhado ao passar o mouse:** Mostra nome, porcentagem, modo de conexão e voltagem da célula (no ATK).
* **Alerta de bateria baixa:** Dispara uma notificação balão única quando algum dispositivo cai para `<= 15%` fora do carregador.
* **Menu no clique direito (dedicado em cada ícone):**
  - Resumo atualizado de todos os periféricos monitorados
  - **Atualizar agora** *(ou duplo clique no ícone)*
  - **Ocultar periféricos desligados** *(salvo no Registro em `HKCU\Software\PeripheralBatteryTray`)*
  - **Iniciar com o Windows** *(salvo em `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`)*
  - **Sair** *(remove todos os ícones sem deixar fantasmas e encerra de forma limpa)*

---

## Como funciona tecnicamente

| Dispositivo | Canal / Protocolo | Detalhes de implementação |
| :--- | :--- | :--- |
| **ATK X1 Ultimate 2.0** | USB HID `0x373B`, `FF02:0002` (17 bytes) | Envia pacote `[0x08, 0x04, ..., 0x49]`, valida checksum (`soma & 0xFF == 0x55`), lê % (`byte 6`), estado de carregamento (`byte 7`) e voltagem em mV (`bytes 8–9`). Faz suavização de 2 falhas consecutivas para evitar pisca-pisca quando o mouse entra em repouso (`Zz`). |
| **Corsair HS80 RGB** | Corsair **Bragi** via `0x1B1C`, `FF42:0001` (64 bytes) | Consulta primeiro a propriedade `0x36` no dongle (`0x08`) para saber se o fone está ligado; se ativo, lê bateria em décimos de % (`0x0F` no alvo `0x09`) e status de carga (`0x10`). Abre handles compartilhados (`FILE_SHARE_READ \| WRITE`) e fecha imediatamente após cada leitura para coexistir com o `iCUE.exe`. |
| **Bluetooth automático** | Windows SetupAPI (`setupapi.dll`) | Varre dispositivos presentes consultando `DEVPKEY_Bluetooth_Battery` (`{104EA319-6EE2-4701-BD47-8DDBF425BBE5}, 2`) e remove duplicados pelo `DEVPKEY_Device_ContainerId`. |
| **I/O Assíncrono Seguro** | `ReadFile` Overlapped + `CancelIoEx` | Leituras usam `FILE_FLAG_OVERLAPPED` com timeout curto e buffer fixado (`GCHandle.Alloc`) para que nenhum dispositivo dormente trave o app ou a bandeja. |

---

## Por que alguns dispositivos não conseguem mostrar a bateria?

Muita gente se pergunta: *"Se o meu teclado sem fio e o meu outro fone também têm bateria e estão ligados no PC por um receptor USB, por que o Windows não mostra a bateria deles?"*

A resposta simples está em como a conversa entre o aparelho e o receptor **2.4 GHz** foi fabricada:

1. **Via de mão única vs. via de mão dupla:**
   - Um receptor **2.4 GHz** comum (como o `25A7:FA70` de vários teclados sem fio, ou o dongle de áudio `10D6:B011` de fones plug-and-play) funciona como uma **via de mão única**: o teclado só grita *"tecla A pressionada"* para o PC, ou o dongle de áudio só recebe som e comandos de volume. O chip dentro do receptor **não tem uma rota de resposta programada** para responder quando o computador pergunta *"quantos % de bateria você tem?"*.
   - Quando consultamos a porta de configuração desses receptores, eles sempre respondem o mesmo número fixo de identificação de fábrica (ou a taxa de áudio `48000 Hz`), nunca a bateria real.
2. **Aparelhos com telemetria bidirecional (como ATK X1 Ultimate e Corsair HS80):**
   - Já esses dispositivos possuem um canal extra dedicado ("mão dupla") justamente para o software da marca conversar com o chip do mouse/headset. O **PeripheralBatteryTray** usa exatamente esse canal para fazer a pergunta direta e mostrar a % real, sem precisar deixar os softwares pesados das marcas abertos.
3. **E o Bluetooth?**
   - Todo dispositivo **Bluetooth** com suporte a bateria segue um padrão universal criado para sistemas operacionais (*GATT Battery Service `0x180F`*): o próprio Windows já lê a porcentagem e disponibiliza para aplicativos. Por isso, vários teclados e fones baratos só mostram bateria no Windows quando ligados via **Bluetooth** (desde que o PC tenha receptor Bluetooth), e não pelo dongle 2.4 GHz.

---

## Como compilar e rodar

### 1. Compilar
```powershell
cd peripheral-battery-tray
.\build.ps1
```
Isso gera `PeripheralBatteryTray.exe` usando o compilador nativo do .NET Framework 4.x (`csc.exe`).

### 2. Testar leituras no terminal (modo diagnóstico `--probe`)
Antes mesmo de abrir os ícones, você pode testar a leitura instantânea gravando em um arquivo texto:
```powershell
.\PeripheralBatteryTray.exe --probe .\probe_test.txt
Get-Content .\probe_test.txt
```

### 3. Executar na bandeja (`^`)
```powershell
Start-Process .\PeripheralBatteryTray.exe
```
Os ícones aparecerão na setinha **`^`** perto do relógio (no Windows 11, novos ícones começam ocultos na setinha `^` por padrão — caso queira algum fixo na barra, basta arrastá-lo para fora da setinha).

Para iniciar automaticamente com o Windows, clique com o botão direito em qualquer um dos ícones e marque **"Iniciar com o Windows"**.

---

## Arquivos desta pasta

* [`Program.cs`](./Program.cs) — Código-fonte completo (leitores HID/SetupAPI, renderizador GDI+ e gerenciador da bandeja).
* [`build.ps1`](./build.ps1) — Script de compilação via `csc.exe`.
