# Deskhand benchmark (`bench/`)

A tiny, self-hosted harness to **measure** whether Deskhand + an agent actually completes Windows tasks —
turning "I changed something, hope it still works" into a number you can watch move.

**What the score means:** it is **self-relative**. Run the same task pack across your own Deskhand versions,
models, or settings (marks on/off, one model vs another) and compare the deltas. It is deliberately *not*
comparable to WindowsAgentArena's public leaderboard — that requires WAA's own VM environment and evaluators
(see `AI_Documentation` / the c/ua comparison). This harness answers "did my change help?", not "how do we rank
vs the field?".

## Run it

1. Start Deskhand: `deskhand-http.exe` (armed; set `DESKHAND_TOKEN` if you use one).
2. `cd bench`
3. `python run.py --base http://127.0.0.1:8791 --token YOUR_TOKEN --agent scripted`

Output: a per-task PASS/FAIL scoreboard + a `%` score, and `results.json`. Each attempt is also recorded as a
Deskhand **episode** (`/episodes/{id}`) so you can inspect exactly what happened.

## How it's built

- **Tasks** (`tasks/*.json`): `{ id, instruction, setup[], solution[], check }`.
  - `setup` / `solution` steps are raw Deskhand requests: `{ "method": "POST", "path": "/clipboard", "body": {...} }`.
  - `check` is a typed verifier (see `checks.py`): `clipboard_contains`, `window_title_contains`,
    `process_running`, `ocr_contains`, `health_ok`. Add more as tasks need them.
- **Agents** (`agents.py`):
  - `scripted` (default) — replays the task's `solution`. No model needed; it measures whether Deskhand's
    primitives + verifiers reliably accomplish/detect the task (a capability regression suite).
  - `model` — the Set-of-Mark loop (capture marks → ask a model for a mark/action → `act_mark` → wait-stable).
    Plug your OpenAI-compatible endpoint via `model_fn` to get a *real agent's* success rate. This is the seam
    that makes the harness model-agnostic; wire it to your proxy.
- **Runner** (`run.py`): setup → agent → verify → episode-labelled → scoreboard.

## Add a task

Drop a JSON in `tasks/`. Give it a `solution` (for the scripted baseline) and a `check`. Example — "set Chrome
as default browser" would `setup` a known state, the model agent would drive it, and `check` would read the
final state (e.g. a registry verifier you add to `checks.py`).
