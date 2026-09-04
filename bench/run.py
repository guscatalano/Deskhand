"""Deskhand benchmark runner.

For each task: start an episode -> run setup -> agent attempts it -> verify -> stop the episode (labelled) ->
score. Prints a scoreboard and writes results.json. The score is SELF-RELATIVE: compare your own runs across
Deskhand versions, models, or settings (it is NOT comparable to WindowsAgentArena's leaderboard — that needs
WAA's own environment + evaluators).

    python run.py --base http://127.0.0.1:8791 --token SECRET --agent scripted --tasks tasks
"""
import argparse
import glob
import json
import os
import time

from deskhand import Deskhand
from agents import AGENTS
from checks import evaluate


def load_tasks(path):
    files = [path] if path.endswith(".json") else sorted(glob.glob(os.path.join(path, "*.json")))
    return [(f, json.load(open(f, encoding="utf-8"))) for f in files]


def run_task(dh, agent, task):
    dh.episode_start(task.get("instruction", task["id"]), model=agent.name)
    t0 = time.time()
    err = None
    try:
        for step in task.get("setup", []):
            dh.call(step.get("method", "POST"), step["path"], step.get("body"))
        agent.solve(dh, task)
    except Exception as e:
        err = str(e)
    passed, detail = evaluate(dh, task.get("check"))
    dh.episode_stop(success=passed, note=detail)
    return {"id": task["id"], "passed": passed, "detail": detail, "error": err, "ms": int((time.time() - t0) * 1000)}


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--base", default="http://127.0.0.1:8791")
    ap.add_argument("--token", default=os.environ.get("DESKHAND_TOKEN"))
    ap.add_argument("--agent", default="scripted", choices=list(AGENTS))
    ap.add_argument("--tasks", default="tasks")
    ap.add_argument("--out", default="results.json")
    a = ap.parse_args()

    dh = Deskhand(a.base, a.token)
    if not (dh.health() or {}).get("ok"):
        raise SystemExit(f"Deskhand not reachable at {a.base} (is deskhand-http running?)")

    agent = AGENTS[a.agent]()
    tasks = load_tasks(a.tasks)
    results = [run_task(dh, agent, t) for _, t in tasks]

    passed = sum(1 for r in results if r["passed"])
    print(f"\nDeskhand benchmark — agent={a.agent}  base={a.base}")
    print("-" * 60)
    for r in results:
        mark = "PASS" if r["passed"] else "FAIL"
        print(f"  [{mark}] {r['id']:<28} {r['ms']:>6}ms  {r['detail']}")
    print("-" * 60)
    print(f"  SCORE: {passed}/{len(results)} ({(100*passed//max(1,len(results)))}%)\n")

    json.dump({"agent": a.agent, "base": a.base, "passed": passed, "total": len(results), "results": results},
              open(a.out, "w", encoding="utf-8"), indent=2)
    raise SystemExit(0 if passed == len(results) else 1)


if __name__ == "__main__":
    main()
