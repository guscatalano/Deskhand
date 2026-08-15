# 08 — Web Dashboard (`wwwroot/index.html`)

A **single self-contained HTML file** (~574 lines: inline CSS + vanilla JS, no framework, no build step,
no external requests). Served at `/` by the HTTP host. It talks to the API **same-origin**, so it never
sends a token (the server trusts same-origin browsers — see `07-http-server.md`). Theme-aware
(light/dark via `prefers-color-scheme` + a manual Theme toggle that sets `data-theme`).

Keep this file conceptual — it is UI glue over the endpoints in `07`.

## Layout

- **Top bar:** brand + a "live" dot; machine/user status; a desktop-state **pill**
  (`default`/`secure`/`locked`/`unknown`, colored); monitor count; two tab buttons
  (**Explorer**, **Screen & Input**); an **arm/disarm** ghost button; a **Log** toggle; a **Theme** toggle.
- **Two tabs**, swapped by toggling an `active` class.

## Explorer tab (default)

A two-pane grid (collapses to stacked rows under 900px):

- **Left — tree column.**
  - A **window picker** `<select>` populated from `GET /windows` (label `[ControlType] name`), with a `↻`
    refresh button.
  - Buttons: **Foreground (3s)** (counts down 3s so you can click the real target, then loads
    `GET /foreground`), **Desktop root** (`POST /uia/tree {depth:0}`), **Focused** (`GET /focused`),
    **↻ Refresh** (reloads the current root and re-selects your element by matching
    controlType+name+automationId), and a **depth** selector (1–4).
  - A **filter** box that hides loaded tree nodes not matching the text.
  - The **tree** itself: each node renders `[ControlType] name #automationId`. Carets **lazy-expand** by
    calling `POST /uia/tree {rootRef, depth:1, maxChildren:200}` the first time they're opened. Clicking a
    node selects it.
- **Right — detail column.** For the selected element:
  - A **breadcrumb** built from DOM ancestry (clickable).
  - Header: control-type chip + name.
  - **Action buttons**, each hitting an endpoint: **Invoke** (`/uia/invoke`), **Focus** (`/uia/set-focus`),
    **Toggle** (`/uia/toggle`), **Expand**/**Collapse** (`/uia/expand-collapse`), **Select** (`/uia/select`),
    **Capture element** (`/capture/element`, shown inline), **Capture window (no raise)**
    (`/capture/window` by ref — WGC, no focus change), **Copy ref**, and an inline **Set value** row
    (`/uia/set-value`).
  - **Patterns** chips (from `ElementInfoDto.Patterns`).
  - **All properties** — fetched from `GET /uia/element/{ref}/properties`, rendered as a filterable
    key→value table.

## Screen & Input tab

A responsive card grid:

- **Screen card.** Monitor `<select>` (`all (virtual)` + each monitor), format `png`/`jpeg`, **Capture**
  button → `POST /capture/screen`. The result is drawn into a **viewer**. Clicking the image converts the
  click to **desktop coordinates** (`rect.x + fx*rect.width`, etc.), drops a marker, and — per the **on
  click** selector — either just reads the coordinates, **🔍 picks the element** at that pixel (`POST
  /uia/element-from-point`, showing its controlType/name/class with an "open in Explorer →" link), or
  performs **move / left / right / double** via the mouse endpoints ("control mode" warning shown for the
  input actions; "pick mode" hint for the picker). The read coordinates auto-fill the Input card's x/y.
  The picker is the reliable way to target elements in Chromium/Electron apps, whose tree is thin/unstable.
- **Secure desktop card (Phase 2).** A **Capture input desktop** button → `POST /capture/input-desktop`;
  shows the returned `desktopName (kind)` + note and the image if `success`. As a normal user this captures
  Default; only the SYSTEM Secure Helper captures the secure desktop.
- **Input card.** Direct mouse (x/y/button, **Move**/**Click**), **scroll** (dx/dy), **type** text, and
  **keys** chord fields — each posting to the corresponding endpoint. Enter submits the type/keys fields.
- **Safety card.** Checkboxes bound to `POST /control`: **Armed** (master), **input enabled**, **capture
  enabled**, **toast on every screenshot**; shows the **Ctrl+Alt+Pause** hotkey hint and the audit-log
  directory (from `GET /control`).

## Cross-cutting JS

- `api(path, method, body)` — thin `fetch` wrapper: sets JSON content-type when there's a body, logs every
  call to the activity drawer, and throws an `Error` carrying `.status` and `.data` on non-2xx.
- `guard(fn)` — wraps handlers so a thrown API error becomes a toast rather than an unhandled rejection.
- **Activity log drawer** (bottom-right) — last 250 requests with method/path/status.
- **Polling** — `refresh()` (machine/desktop/monitors) and `refreshControl()` (arm state) run on load and
  every **5 seconds**; the arm button and safety checkboxes reflect server state.

## Notes

- The in-page `#toast` element (bottom-center) is the dashboard's *own* status toast — distinct from the
  WinForms **screenshot** toast the server pops (see `10-governance-and-safety.md`).
- The dashboard is purely a convenience client; every capability it exposes is a plain endpoint you can hit
  with `curl` or from the MCP host.
