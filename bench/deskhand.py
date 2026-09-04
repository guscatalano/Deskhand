"""Tiny Deskhand HTTP client for the benchmark harness (stdlib only — no pip)."""
import json
import urllib.request
import urllib.error


class Deskhand:
    def __init__(self, base="http://127.0.0.1:8791", token=None, timeout=120):
        self.base = base.rstrip("/")
        self.token = token
        self.timeout = timeout

    def call(self, method, path, body=None, timeout=None):
        """Raw request. Returns (status, parsed-json-or-text). `timeout` overrides the client default for this
        one call — use a generous value for a step that can legitimately run for minutes (an agent turn, a long
        wait_*, a shell command, a big fetch/dump) so it isn't aborted by the short read timeout."""
        url = self.base + path
        data = json.dumps(body).encode() if body is not None else None
        req = urllib.request.Request(url, data=data, method=method.upper())
        if body is not None:
            req.add_header("Content-Type", "application/json")
        if self.token:
            req.add_header("Authorization", "Bearer " + self.token)
        try:
            with urllib.request.urlopen(req, timeout=timeout or self.timeout) as r:
                raw = r.read().decode("utf-8", "replace")
                return r.status, _parse(raw)
        except urllib.error.HTTPError as e:
            raw = e.read().decode("utf-8", "replace")
            return e.code, _parse(raw)
        except Exception as e:
            return 0, {"error": str(e)}

    # convenience reads used by verifiers
    def clipboard(self):
        return self.call("GET", "/clipboard")[1]

    def windows(self):
        return self.call("GET", "/windows/all")[1]

    def ocr_screen(self):
        return self.call("POST", "/ocr/screen", {})[1]

    def health(self):
        return self.call("GET", "/health")[1]

    # episode recording
    def episode_start(self, task, model="bench"):
        return self.call("POST", "/episode/start", {"task": task, "model": model})[1]

    def episode_stop(self, success, note=None):
        return self.call("POST", "/episode/stop", {"success": success, "note": note})[1]


def _parse(raw):
    if not raw:
        return None
    try:
        return json.loads(raw)
    except Exception:
        return raw
