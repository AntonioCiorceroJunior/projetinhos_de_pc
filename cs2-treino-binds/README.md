# CS2 Treino & Binds (`cs2-treino-binds`)

Coleção pronta de arquivos `.cfg` para **Counter-Strike 2 (CS2)** com binds de treino livre (granadas, *noclip*, spray e lineups), modo de combate contra 10 bots na dificuldade máxima e configurações base de `autoexec`.

---

## Arquivos incluídos

| Arquivo | Finalidade | Comando no console do CS2 |
| :--- | :--- | :--- |
| [`treino.cfg`](./treino.cfg) | **Treino livre de utilitários, spray e movimentação** (sem bots, munição infinita, preview/rastro de granadas, impactos de tiro e binds rápidas). | `exec treino` |
| [`treino2.cfg`](./treino2.cfg) | **Treino real contra 10 bots difíceis** (limpa binds de cheat visual do treino livre, economia normal, vida normal, bots nível máximo sem respawn instantâneo). | `exec treino2` |
| [`autoexec.cfg`](./autoexec.cfg) | **Configuração automática ao abrir o jogo** (console ativado, limite de FPS, baixa latência pós-tick e telemetria discreta de frametime). | Automático *(ou `exec autoexec`)* |
| [`InstalarCfgsCS2.ps1`](./InstalarCfgsCS2.ps1) | Script PowerShell que detecta a pasta `game\csgo\cfg` do CS2 (inclusive em outras bibliotecas Steam) e copia os `.cfg` criando backup `.bak` se houver diferença. | `.\InstalarCfgsCS2.ps1` |

---

## Binds do `treino.cfg` (Treino Livre)

| Tecla | Ação | Comando executado |
| :--- | :--- | :--- |
| `V` | Ativa/desativa voo livre (*noclip*) | `noclip` |
| `I` | Repete a última granada lançada | `sv_rethrow_last_grenade` |
| `K` | Limpa smokes e molotovs/incendiárias ativas no mapa | `ent_fire smokegrenade_projectile kill; ent_fire inferno kill` |
| `O` | Reinicia o round imediatamente | `mp_restartgame 1` |
| `P` | Entrega todas as granadas principais (HE, Flash, Smoke, Molotov, Incendiária) | `give weapon_hegrenade; ...` |

**Destaques extras do `treino.cfg`:**
* `sv_cheats 1`, `mp_roundtime 60`, compra liberada em qualquer lugar com `$60.000` (`mp_buy_anywhere 1`).
* Sem bots (`bot_kick`, `bot_quota 0`) e imortalidade (`buddha 1`, `buddha_reset_hp 100`).
* Preview ao vivo de trajetória (`cl_grenadepreview 1`, `sv_grenade_trajectory_prac_pipreview 1`) e impactos de tiro (`sv_showimpacts 1`).
* Armas padrão de início: **M4A1-S + USP-S** (CT) e **AK-47 + Glock** (TR) com colete + capacete (`mp_free_armor 2`).

---

## Comandos do `treino2.cfg` (Treino vs 10 Bots Difíceis)

Ao carregar `exec treino2`, o jogo desfaz as ajudas visuais e binds de treino livre (`unbind` em `v`, `i`, `k`, `p`), mantendo `O` para reiniciar o round, e inicia **você contra 10 bots TR no nível máximo** (`bot_difficulty 3`, `custom_bot_difficulty 3`, `sv_infinite_ammo 0`, `buddha 0`).

| Comando no console | O que faz |
| :--- | :--- |
| `treino2_vs_t` | Remove todos os bots e adiciona **10 bots no time TR** (para você jogar de CT) + reinicia o round |
| `treino2_vs_ct` | Remove todos os bots e adiciona **10 bots no time CT** (para você jogar de TR) + reinicia o round |
| `O` *(tecla)* | Reinicia o round (`mp_restartgame 1`) |

---

## Configurações do `autoexec.cfg`

* `fps_max "300"` e `fps_max_ui "120"`
* `con_enable "1"` (habilita o console de desenvolvedor)
* `engine_low_latency_sleep_after_client_tick "true"` (melhora resposta de entrada/quadros)
* `cq_netgraph "0"` e `cl_hud_telemetry_frametime_show "2"` (exibe FPS/frametime discreto no canto superior direito)
* `host_writeconfig`

---

## Como instalar e usar

### Instalação automática (PowerShell)
```powershell
cd cs2-treino-binds
powershell -ExecutionPolicy Bypass -File .\InstalarCfgsCS2.ps1
```
*(Se o CS2 estiver em uma pasta personalizada, passe `-Cs2CfgPath "C:\Caminho\...\game\csgo\cfg"`.)*

### Instalação manual
Copie `treino.cfg`, `treino2.cfg` e `autoexec.cfg` para a pasta de configurações do CS2:
```text
...\Steam\steamapps\common\Counter-Strike Global Offensive\game\csgo\cfg\
```

### No jogo
1. Abra um mapa offline/treino no CS2.
2. Abra o console (`'` ou tecla configurada) e digite:
   - `exec treino` — para treinar smokes, flashes, molotovs e spray.
   - `exec treino2` — para treinar trocação contra 10 bots difíceis.
3. *(Opcional)* Nas **Opções de Inicialização** do CS2 na Steam, adicione `+exec autoexec.cfg` caso queira garantir a carga do `autoexec.cfg` a cada abertura.
