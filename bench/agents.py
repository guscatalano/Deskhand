"""Agents that attempt a task by driving Deskhand.

Two are provided:
  - ScriptedAgent: replays the task's `solution` steps (deterministic). Zero model needed — it measures
    whether Deskhand's primitives + the verifiers reliably accomplish/detect the task (a capability regression
    suite). This is the baseline that runs today.
  - ModelAgent: the Set-of-Mark loop (capture marks -> ask a model for a mark id/action -> act_mark -> repeat).
    Plug your own OpenAI-compatible endpoint via `model_fn`. This is where a real agent's success rate comes
    from; left as a thin, clearly-marked seam so the harness is model-agnostic.

A step is a raw request: {"method": "POST", "path": "/clipboard", "body": {...}}."""


class ScriptedAgent:
    name = "scripted"

    def solve(self, dh, task):
        for step in task.get("solution", []):
            dh.call(step.get("method", "POST"), step["path"], step.get("body"))


class ModelAgent:
    """Set-of-Mark loop. model_fn(instruction, image_b64, marks) -> {"tool": "...", "args": {...}} or {"done": true}."""
    name = "model"

    def __init__(self, model_fn, max_steps=15):
        self.model_fn = model_fn
        self.max_steps = max_steps

    def solve(self, dh, task):
        for _ in range(self.max_steps):
            # capture with Set-of-Mark: image + legend the model grounds on
            _, res = dh.call("POST", "/ux/marks", {"maxMarks": 60})
            marks = (res or {}).get("marks", [])
            image_b64 = (res or {}).get("imageBase64")
            decision = self.model_fn(task["instruction"], image_b64, marks)
            if not decision or decision.get("done"):
                return
            tool, args = decision.get("tool"), decision.get("args", {})
            if tool == "act_mark":
                dh.call("POST", "/ux/act-mark", args)
            elif tool:
                dh.call(args.get("method", "POST"), args["path"], args.get("body"))
            dh.call("POST", "/vision/wait-stable", {"settleMs": 500, "timeoutMs": 3000})


AGENTS = {"scripted": lambda **kw: ScriptedAgent()}
