# Rose, LTK Manager & Porofessor AutoSync (`RoseAutoSync`)

Orquestrador leve e invisível para Windows que gerencia o ciclo de vida completo de **Rose**, **LTK Manager** (`ltk-manager` / `ltk_patcher_host`), **Pengu Loader** e **Porofessor / Overwolf** em função do *League of Legends*.

---

## Para que serve?

* **PC limpo fora do jogo:** Quando você liga o computador ou fecha o LoL, garante que Rose, LTK Manager, Overwolf/Porofessor e o gancho do Pengu Loader fiquem totalmente encerrados e desativados (sem processos em segundo plano nem entrada automática do Overwolf no `Run`).
* **Escolha por sessão ao abrir o LoL:** Assim que o cliente do League of Legends abre:
  1. Inicia o **Porofessor (via Overwolf)**.
  2. Exibe uma pergunta rápida (*"Quer rodar o Rose e o LTK agora?"*, com timeout de 30s e padrão em **Não**).
     - **Sim:** Reativa o `config.ini` do Pengu Loader (`disabled = 0`), restaura o hook IFEO em `LeagueClientUx.exe` e inicia a tarefa/executável do Rose + LTK.
     - **Não / Fechar / Tempo esgotado:** Mantém Rose e LTK encerrados, desativa o Pengu (`disabled = 1` + remove IFEO) e, se o Pengu já tiver sido injetado na UX do cliente, solicita reinício limpo da UX sem fechar sua partida.
* **Limpeza automática ao fechar o jogo:** Confirma que o cliente realmente fechou (aguardando 5s para evitar falsos positivos entre telas) e encerra todos os companheiros automaticamente.

---

## Como funciona por baixo dos panos

* **Launcher silencioso ([`RoseAutoSync.vbs`](./RoseAutoSync.vbs)):** Usa `WScript.Shell` com modo de janela `0` para iniciar o PowerShell em segundo plano sem abrir nem piscar janela de prompt na tela.
* **Instância única (`Mutex`):** `Global\RoseAutoSyncWatcherMutex` impede que duas cópias do monitor rodem ao mesmo tempo.
* **Controle preciso do Pengu Loader:**
  * Edita `%LOCALAPPDATA%\Rose\config.ini` sempre em **UTF-8 sem BOM** para evitar corromper a leitura do loader.
  * Gerencia a chave de registro *Image File Execution Options (IFEO)* de `LeagueClientUx.exe` (`HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\LeagueClientUx.exe`).
* **Ordem segura de encerramento:** Encerra `Rose` antes de `ltk-manager` / `ltk_patcher_host` para impedir que o processo pai tente reabrir o patcher automaticamente.
* **Configuração de tarefas ([`ConfigurarTarefasRose.ps1`](./ConfigurarTarefasRose.ps1)):** Remove o gatilho de logon da tarefa `\Rose` (para ele nunca abrir sozinho no boot) e registra `\RoseAutoSync` com `RunLevel Highest` para poder alternar o IFEO sem janelas de UAC.

---

## Pré-requisitos

* **Windows 10 ou 11**
* **Rose** instalado em `C:\Program Files\Rose\Rose.exe` (com pasta de dados em `%LOCALAPPDATA%\Rose`) e/ou **LTK Manager**
* *(Opcional)* **Overwolf + Porofessor** instalado em `C:\Program Files (x86)\Overwolf\OverwolfLauncher.exe`
* **Privilégio de Administrador** para executar [`ConfigurarTarefasRose.ps1`](./ConfigurarTarefasRose.ps1) (necessário para editar tarefas elevadas e chaves `HKLM\...\Image File Execution Options`)

---

## Como instalar e rodar

1. Copie ou mantenha a pasta `rose-ltk-autosync` em um local fixo no PC (ex.: `%LOCALAPPDATA%\Rose` ou onde preferir — o `.vbs` resolve o caminho do `.ps1` automaticamente pela própria pasta).
2. Abra o **PowerShell como Administrador** e execute:
   ```powershell
   cd rose-ltk-autosync
   powershell -ExecutionPolicy Bypass -File .\ConfigurarTarefasRose.ps1
   ```
3. Pronto! O monitor passará a iniciar automaticamente com o Windows (tarefa `\RoseAutoSync`) e já ficará ativo na sessão atual.
4. Os logs rotativos de diagnóstico ficam salvos em `%LOCALAPPDATA%\Rose\RoseAutoSync.log` (mantendo automaticamente as últimas 120 linhas).

> **Teste manual rápido (sem agendar tarefa):** você pode clicar duas vezes em [`RoseAutoSync.vbs`](./RoseAutoSync.vbs) para testar na hora. Porém, sem elevação de Administrador, a manipulação das chaves `HKLM\...\Image File Execution Options` será ignorada pelo Windows.

---

## Arquivos desta pasta

* [`RoseAutoSync.ps1`](./RoseAutoSync.ps1) — Script principal de monitoramento e sincronização.
* [`RoseAutoSync.vbs`](./RoseAutoSync.vbs) — Wrapper VBScript para execução 100% oculta no Windows.
* [`ConfigurarTarefasRose.ps1`](./ConfigurarTarefasRose.ps1) — Configurador das tarefas agendadas (`\Rose` e `\RoseAutoSync`) e limpeza inicial de IFEO.
