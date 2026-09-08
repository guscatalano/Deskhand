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
        """Raw request. Returns (status, value) where value is parsed JSON, plain text, or — for a binary
        response (an image, a download) — the raw `bytes`. `timeout` overrides the client default for this
        one call — use a generous value for a step that can legitimately run for minutes (an agent turn, a long
        wait_*, a shell command, a big fetch/dump) so it isn't aborted by the short read timeout.

        We ask for JSON explicitly and never UTF-8-decode a binary body: capture endpoints return
        {..., "imageBase64"} as JSON, which you base64-decode. Decoding raw image bytes as UTF-8 is lossy
        (replacement chars where bytes fall outside valid UTF-8) and silently corrupts the image."""
        url = self.base + path
        data = json.dumps(body).encode() if body is not None else None
        req = urllib.request.Request(url, data=data, method=method.upper())
        req.add_header("Accept", "application/json")  # never get a raw binary body by surprise
        if body is not None:
            req.add_header("Content-Type", "application/json")
        if self.token:
            req.add_header("Authorization", "Bearer " + self.token)
        try:
            with urllib.request.urlopen(req, timeout=timeout or self.timeout) as r:
                return r.status, _body(r)
        except urllib.error.HTTPError as e:
            return e.code, _body(e)
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


def _body(r):
    """Decode a response by its Content-Type. Text/JSON -> parsed; binary -> raw bytes (never a lossy
    utf-8 decode). `r` is an http.client response or an HTTPError, both of which expose read()/headers."""
    raw = r.read()
    ctype = (r.headers.get("Content-Type") or "").lower()
    if not ctype or "json" in ctype or ctype.startswith("text/"):
        return _parse(raw.decode("utf-8", "replace"))
    return raw  # image/*, application/octet-stream, etc. — hand back bytes untouched


def _parse(raw):
    if not raw:
        return None
    try:
        return json.loads(raw)
    except Exception:
        return raw
