"""Verifiers: read the final state via Deskhand and return (passed: bool, detail: str).

A task's `check` is one of these typed specs, evaluated after the agent finishes.
Add new verifier types here as tasks need them."""


def evaluate(dh, check):
    t = (check or {}).get("type")
    fn = _VERIFIERS.get(t)
    if fn is None:
        return False, f"unknown check type '{t}'"
    try:
        return fn(dh, check)
    except Exception as e:
        return False, f"check errored: {e}"


def _clipboard_contains(dh, c):
    got = (dh.clipboard() or {}).get("text") or ""
    want = c["text"]
    return want in got, f"clipboard={got!r} want~{want!r}"


def _window_title_contains(dh, c):
    want = c["text"].lower()
    titles = [(w.get("title") or "") for w in (dh.windows() or [])]
    hit = next((t for t in titles if want in t.lower()), None)
    return hit is not None, (f"matched window '{hit}'" if hit else f"no window title ~ {c['text']!r}")


def _process_running(dh, c):
    want = c["name"].lower()
    procs = {(w.get("process") or "").lower() for w in (dh.windows() or [])}
    return want in procs, f"processes={sorted(procs)} want={c['name']!r}"


def _ocr_contains(dh, c):
    text = ((dh.ocr_screen() or {}).get("text") or "").lower()
    want = c["text"].lower()
    return want in text, (f"OCR contains {c['text']!r}" if want in text else f"OCR missing {c['text']!r}")


def _health_ok(dh, c):
    return bool((dh.health() or {}).get("ok")), "health"


_VERIFIERS = {
    "clipboard_contains": _clipboard_contains,
    "window_title_contains": _window_title_contains,
    "process_running": _process_running,
    "ocr_contains": _ocr_contains,
    "health_ok": _health_ok,
}
