# Record & replay a driven session as a regression test

Deskhand can record a sequence of driven actions as a **named macro**, add **assertions**, and **replay** it
later — against the current machine, after a code change — returning a loud pass/fail report. UIA steps
re-resolve their target at replay (so a replay survives DPI/resolution/layout changes rather than firing at a
stale pixel). This is the "replay it after you changed some stuff" path: a demo becomes a regression test.

Everything below was validated end to end (including across two separate processes) against a throwaway Notepad.

## Record

1. Drive the flow you want to capture. Recording hooks the **governed backend**, so every state-changing action
   you take — `type_text`, `mouse_click`, `invoke`, `set_value`, `set_focus`, `launch_process`, … — is captured.
   UIA acts store a *re-resolvable selector* (name / automationId / controlType / className), not a pixel.

   ```
   deskhand_macro_start
   deskhand_launch_process  path="notepad.exe"
   deskhand_type            text="summarize the quarterly report"     # or set_value / click / invoke …
   ```

2. Add checkpoints that a replay must satisfy:

   | Tool | Checks |
   |---|---|
   | `deskhand_macro_expect` | a UIA **element appears** (name/automationId/controlType/className) |
   | `deskhand_macro_assert_text` | an element's **text** equals / contains an expected string — read via the UIA Value/Text pattern (**reliable**; use this to assert a typed prompt or a field value) |
   | `deskhand_macro_assert` | a non-element condition: `window` title / `process` / `file` / `registry` / `text` (OCR). `expect="gone"` inverts. Use OCR `text` only for canvas/no-UIA content — prefer `assert_text` for fields |

   ```
   deskhand_macro_assert_text  controlType="Document" expected="summarize the quarterly report" contains=true
   deskhand_macro_assert       target="window" match="Notepad" expect="present"
   ```

3. Stop. `deskhand_macro_stop` returns the macro as JSON (persistable), e.g.:

   ```json
   {"steps":[{"tMs":0,"kind":"input","method":"type_text","args":{"text":"summarize the quarterly report"}}]}
   ```

## Persist

- `deskhand_macro_save  name="quarterly-demo"` — saves to `%LOCALAPPDATA%\Deskhand\macros` (survives a restart).
- Or keep the `macro_stop` JSON **in your repo** as the demo definition, and import it on a fresh machine with
  `deskhand_macro_import  name="quarterly-demo"  macroJson=<the JSON>`.
- `deskhand_macro_list` / `deskhand_macro_delete` manage saved macros.

## Replay (the regression test)

```
deskhand_macro_replay  name="quarterly-demo"            # or: macroJson=<repo JSON>  (no save needed)
```

Returns a per-step report:

```json
{ "passed": false, "steps": 4, "failures": 1,
  "results": [ { "index": 3, "kind": "assert-text", "method": "assert_text", "ok": false,
                 "detail": "element text != expected — got '…'" } ] }
```

- `passed` is the headline. A failed assertion, or a UIA element that can no longer be re-resolved, is a **loud
  failure** (not a silent no-op) — which is exactly what tells you a code change broke the flow.
- `stopOnFailure` (default true) halts at the first break; pass false to run the whole macro and collect every failure.
- `speed > 1` replays faster.

## Reading back / verifying

`deskhand_get_text  reference=<el>` reads an element's Value/Text content — the counterpart to `set_value`/`type`,
and what `assert_text` uses under the hood.

## Notes

- Record happens through the governed backend, so the **kill switch must be armed** and actions are audited.
- Prefer semantic targeting (`deskhand_click` / `deskhand_type` by name/automationId/text) while recording — those
  resolve to re-resolvable UIA acts, keeping the replay robust. Raw `mouse_click{x,y}` records coordinates, which rot.
