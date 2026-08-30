# 18 — Dashboard UX Architecture (how the HTML/CSS/JS is structured)

For someone re-implementing the Deskhand dashboards. This is a deep description of *how the UX is built* —
the document skeleton, the design system, the tab/layout model, every component, and the client-side
architecture — for both single-page dashboards: the **local** console (`src/Deskhand.Http/wwwroot/index.html`,
~800 lines) and the **fleet** console (`src/Deskhand.Fleet.Server/wwwroot/index.html`, ~290 lines). No code
is reproduced; class/id/function names are given so you can find things. Pair with recipe D in `17`.

## Ground rules (both dashboards)

- **One self-contained file each.** `<!doctype html>` → `<head>` with a single inline `<style>` → `<body>`
  ending in a single inline `<script>`. **No framework, no build step, no external requests** (no CDN, no
  webfonts, no images — screenshots arrive as base64 data URIs). This is deliberate: the file is served
  straight from `wwwroot` and must work offline on localhost.
- **Vanilla JS, `"use strict"`.** DOM built imperatively with two one-line helpers: a `$` = `querySelector`
  and an `elm(tag, className)` = `createElement`. There is no templating library and no virtual DOM; handlers
  are assigned directly to `.onclick`/`addEventListener`.
- **Same-origin trust.** The page calls its own host's API without any token; the server trusts same-origin
  browsers (loopback Host + Origin checks live server-side). So `fetch` is plain relative-path calls.
- **Served `no-cache`.** The host stamps `Cache-Control: no-cache` on `.html`, so editing the file and doing a
  normal refresh always shows the new UI (no cache-busting query strings, no Ctrl+F5).
- **Fonts** are system stacks only: a monospace stack (`--mono`: Cascadia Code/Consolas…) used for all
  technical/label text, and a sans stack (`--sans`: Segoe UI…) for prose/headings.

## The design system (CSS custom properties)

Everything is token-driven — there are almost no hard-coded colors in component rules; they all reference
`var(--…)`. Tokens are declared three times to implement **three-state theming**:

1. On bare `:root` — the **light** palette (the default).
2. Inside `@media (prefers-color-scheme: dark)` guarded by `:root:not([data-theme="light"])` — the **dark**
   palette for users whose OS is dark and who haven't overridden.
3. On `:root[data-theme="dark"]` — the same dark palette again, so an explicit toggle wins in both directions.

The **Theme** button flips `data-theme` between `dark`/`light` on `<html>`; because every component reads
tokens, the whole UI recolors instantly with no per-element work.

**Token roles** (semantic, not literal — this is the key to reading the CSS):
- Surfaces: `--bg` (page), `--surface` (cards/panels), `--surface-2` (inputs, hover), `--surface-3` (insets,
  the capture viewer background).
- Text: `--ink` (primary), `--ink-2` (secondary), `--ink-3` (muted/labels/placeholders).
- Lines: `--line` (borders), `--line-2` (faint dividers).
- Accent families, each with a `-soft` background companion: `--accent` (brand teal — links, selection,
  control-type chips, primary buttons), `--action` (orange — anything that **injects real input** or is
  destructive: the "Click/Type/Send/Launch" buttons, the record dot, the click-to-control marker),
  `--good` (green — connected/ok), `--warn` (amber), `--crit` (red — refusals/errors/danger).
- `--mono`, `--sans`, `--shadow`.

The **color semantics carry meaning**: orange (`--action`) consistently marks "this touches the real
machine," so a user can tell a read-only control from an input-injecting one at a glance. Cards that inject
input get a `.act` modifier (an orange square marker in their header).

**Reusable component classes** (local dashboard):
- `.row` — a flex-wrap horizontal group (the atom of every form). `.lbl` — an uppercase mono micro-label.
- Buttons: `.btn` (neutral), `.btn.p` (primary = accent fill), `.btn.a` (action = orange fill), `.btn.sm`
  (compact), `:disabled` (dimmed). `.ghost` — the lighter topbar button style.
- `.pill` — a small rounded status chip; `.pill.default/.secure/.locked/.unknown` map desktop state to
  good/crit/warn colors. `.chip` / `.chip.on` — pattern tags in the detail panel.
- `.sect` — a titled section whose `.h` header renders an uppercase label with a trailing hairline rule and
  an optional `.count` badge (used throughout the detail panel and registry).
- `.card` — the Screen tab's panel with a header (mono title + colored square) and a `.body`.
- Inputs (`input[type=text|number]`, `select`) share one style; `:focus-visible` gets a 2px accent outline
  (keyboard-accessibility is explicit).

## Local dashboard — page skeleton

`<body>` is a vertical flexbox: a fixed **`.topbar`** on top, a flex-1 **`.tabwrap`** filling the rest, then
fixed-position overlays (`#logdrawer`, `#toast`) that float above everything.

**Topbar** (left→right): the brand (a `.dot` that turns green — class `live` — once the first API call
succeeds) · a machine/user `stat` · a "desktop" `stat` with a state `.pill` · a monitor-count `stat` · a
`.spacer` (flex:1 pushes the rest right) · the **`.tabs`** group (Explorer / Processes / Files / Registry / Shell / System / Connect / Screen &
Input) · a cross-link to the Fleet dashboard (`:8799`) · the **arm** ghost button (kill switch) · **Log** ·
**Theme**.

**Tab model.** `.tabwrap` is `position:relative`; each `.tab` is `position:absolute; inset:0; display:none`,
and `.tab.active` becomes visible. Crucially, **the visible display mode is opt-in per tab**: the base rule
is `display:block`, but `#tab-explorer.active, #tab-processes.active` override to a two-column
`display:grid` (`minmax(340px,40%) 1fr`), and `#tab-screen, #tab-registry, #tab-files, #tab-shell, #tab-system, #tab-connect` are `overflow:auto` single panes.
Forgetting to add a tab to the grid rule was a real bug (a two-pane tab with no grid shows no right pane).
Under 900px the grid collapses to stacked rows. Tab switching (JS) toggles `.active` on both the clicked
`.tabbtn` and its `#tab-…`, writes the URL hash, and lazy-loads that tab's data the first time.

## Local dashboard — the tabs

### Explorer (default) — the UIA tree browser
Two panes. **Left `.treecol`** (a vertical flexbox): a `.treetools` header with a **window picker** `<select>`
(populated from `/windows`), a `↻` refresh, then buttons **Foreground (3s)** (counts down so you can focus
the real app first), **Desktop root**, **Focused**, **↻ Refresh** (reload keeping selection), a **depth**
selector (1–4), and a **filter** box; below it the scrolling **`#tree`**. **Right `.detailcol`** = `#detail`.

**The tree component** is the heart of the UI:
- `makeRow(info)` builds one node: a `.node` wrapper containing a `.self` row (a `.caret` toggle, a `.ctype`
  control-type in accent, a `.nname` name that ellipsizes, an optional `.naid` automation-id) and an empty
  `.kids` container (indented with a left border).
- `renderNode(info, container, preChildren)` appends a row and wires two interactions: **clicking the caret**
  lazy-expands (first open fetches `/uia/tree {rootRef, depth:1, maxChildren:200}` and recurses), **clicking
  the row** calls `selectNode`. If `preChildren` is supplied (from the initial `loadRoot` fetch), it renders
  them eagerly and skips the first lazy fetch.
- `loadRoot(info, preserve)` loads a chosen window as the tree root: fetches the tree at the selected depth,
  clears `#tree`, renders, and either re-selects a previously selected element by **identity**
  (controlType+name+automationId — because refs are re-minted on every fetch) or selects the root. Used by
  the window picker, the Foreground/Desktop/Focused buttons, Refresh, and deep-link restore.
- A **filter** input hides loaded nodes whose cached label (`_label` on each node) doesn't match, walking up
  to keep ancestors of matches visible.

**The detail panel** — `selectNode(info, selEl)` is a *shared* renderer used by Explorer **and** Processes.
It figures out which pane to draw into by walking up from the clicked element to its `.tab` and grabbing that
tab's `.detailcol` and `.tree` — so the same function serves both tabs without knowing which it's in. It
renders, in order: a **breadcrumb** (`.crumbs`) reconstructed from the DOM ancestry (each crumb re-selects
that ancestor); a **header** (`.dhead`: a control-type chip + the name as an `<h1>`); an **actions** row
(`.acts`, built by a small `mk(label, cls, fn)` helper) — Invoke, Focus, Toggle, Expand, Collapse, Select,
Capture element, Capture window (no raise), Copy ref, Expect (macro), and (only in the Processes tab) Open in
Explorer; an inline **Set value** row; a hidden **capture preview** (`.preview`) that fills when you capture
the element; a **patterns** section of `.chip` tags; and the **all-properties** section — a filterable
`.props` grid (170px key column + value) fetched from `/uia/element/{ref}/properties`. Selecting also updates
the URL hash.

### Processes — process → windows → tree
Same two-pane grid as Explorer. **Left `#ptree`**: a refresh button, a live count, a **windowed-only**
checkbox, a filter, then the tree. `loadProcesses` fetches `/processes`; `renderProcList` applies the
filter/toggle; `renderProcRow` renders each process as a `.node` (tagged `data-pid` for deep-link restore)
whose caret expands to its **windows** — and each window is rendered with the *same* `renderNode`, so it
lazy-expands into the UIA tree. Clicking a process runs `showProcDetail` into `#pdetail`: pid/memory/title,
then a **windows list** each with an **Open in Explorer →** button (switches tab + `loadRoot`s that window),
and a **⤓ Full memory dump** button (confirm + size warning → `/process/dump` → download link). Selecting a
window/element reuses `selectNode` (so it lands in `#pdetail`).

### Registry — read-only browser
A single scrolling pane: a **path** input + **Go** + **↑ up**, a clickable **breadcrumb** (`#regCrumb`,
"Computer \ HKLM \ SOFTWARE \ …"), and **`#regBody`**. `loadRegistry(path)` fetches `/registry?path=…` and
renders a **keys** `.sect` (each `.regkey` row navigates deeper on click) and a **values** `.sect` (each
`.regval` is a 3-column grid: name / kind / value). Errors (access-denied) render as a red message. Navigation
is all client-side re-fetches; the hash tracks the current path.

### Files — file manager
Single scrolling pane: path input + **Go** + **↑ up**, a **toolbar** (`#fsTools`), a clickable **breadcrumb**
`#fsCrumb` "This PC \ C: \ Users \ …", and **`#fsBody`**. `loadFiles(path)` fetches `/fs?path=…`; an empty path
lists the drives (`isRoot`). Each entry is a `.fsrow` grid (**checkbox** / name / size / modified / **actions**):
folders (`.fsrow.dir`, accent name) descend on click; per-row action links are **download** (`fsDownload` —
`fetch /fs/download` with the auth header → blob → `<a download>`), **launch** (`/process/launch`), **unzip**
(on `.zip`), **rename** (prompt → `/fs/rename`), and **delete** (confirm → `/fs/delete`, → Recycle Bin; the
link is orange `.danger`). The **toolbar** has **⬆ Upload** (hidden `#fsUpload` multi-file input → multipart
`/fs/upload` into the current folder) and, gated on a non-empty selection (`fsSel` Set + `fsUpdateSel`),
**Zip** (`/fs/zip` the selected into a named archive), **Copy…** / **Move…** (prompt a destination →
`/fs/copy` / `/fs/move` per item), and **Delete** selected. Drive roots show no checkbox/actions and disable
upload. Mutating calls are armed-gated + audited server-side; `fsReload()` re-fetches after each. The hash
tracks the folder (`#files/C:\Users`), restored on load like Registry.

### Shell — one-shot command runner
A single scrolling pane: a shell `<select>` (PowerShell / pwsh / cmd), an optional `#shCwd` working-dir
input, an orange **Run** `.btn.a` (action = touches the real machine), a `#shCmd` textarea (Ctrl/⌘+Enter
runs), a muted hint that it needs `DESKHAND_ENABLE_SHELL` + armed, a `#shMeta` status line, and a `#shOut`
`<pre>` output. `runShell()` POSTs `/shell/run {shell,command,cwd}`; on success it prints exit code (green/red)
+ duration into `#shMeta` and stdout then stderr (red) into `#shOut`; a `403` (shell disabled / disarmed)
renders its message in `#shMeta`. No persisted state — each run is a fresh process.

### Connect — MCP client setup
A copy-paste helper for pointing an MCP client at this server. Shows the **endpoint** (`location.origin + /mcp`)
with a Copy button, a **client `<select>`** (Claude Code CLI, Claude Desktop, Cursor, VS Code, opencode, stdio
`deskhand-mcp.exe`, generic HTTP JSON), and a `#mcpSnippet` `<pre>` rendered by `mcpConfig(client)` with a
per-client hint + **Copy config**. Front-end only except it fetches `/health` for `requiresToken` (the server
reports the boolean, never the token); when true, the snippets include an `Authorization: Bearer <YOUR_TOKEN>`
header/line and the note tells the user to substitute it. Copy uses `navigator.clipboard`.

### System — about this machine
A responsive card grid (`.sysgrid` of `.syscard`) fetched once from `/system`: **OS** (name, edition,
DisplayVersion, build, BuildLab, arch, machine/user/domain), **Uptime** (up-for + boot time), **CPU**
(model, cores, live load with a `.meter` bar), **Memory** (used/total + load meter + page file), **Disks**
(per drive: used/total + format + a meter), **Network** (per up interface: IPv4/IPv6, gateway, DNS, MAC,
type, speed), and **Windows Firewall** (Domain/Private/Public on/off, green/red). `.meter` turns amber ≥75%,
red ≥90%. A **Refresh** button re-pulls. Read-only.

### Screen & Input — the operator console
`.inner` is a responsive grid of `.card` sections:
- **Screen**: a monitor `<select>`, format toggle, **Capture** (`/capture/screen` → draws an `<img>` into
  the `.viewer`), **Save** (client-side `<a download>` of the data URI). Below the viewer, an **on click**
  `<select>` chooses what a click on the screenshot does — *inspect* (read coords), *🔍 pick element*
  (`/uia/element-from-point`), *move / left / right / double* (real input). A `.hint` line changes to a
  "Control mode" (orange) or "Pick mode" warning per selection. Then a **🎥 record** row (format/fps/scale/
  max-seconds, Record/Stop) driving `/record/*` with a live status line. The click math lives in a `#viewer`
  click handler: it converts `clientX/Y` → a fraction of the image rect → desktop pixels using the captured
  `lastCap.rect` (origin + size), drops a `.marker`, and fills the Input card's x/y.
- **Secure desktop** (Phase 2): a button → `/capture/input-desktop`, shows the desktop kind + image.
- **Input** (an `.act` card — orange): direct mouse x/y/button (Move/Click), scroll, type, send-keys, and
  launch — each posting to its endpoint; Enter submits the type/keys/launch fields.
- **Safety**: checkboxes bound to `/control` (Armed master, input enabled, capture enabled, toast-on-capture),
  the Ctrl+Alt+Pause hint, and the audit-log path.
- **Macro**: Record/Stop/Play + speed. **UI Events**: a reverse-ordered live list fed by SSE.

## Local dashboard — client architecture

- **`api(path, method, body)`** — the single fetch wrapper. It logs every call to the activity drawer
  (`logRow`), parses JSON, and on non-2xx throws an `Error` carrying `.status` and `.data` (so callers can
  branch on 403/404). Every user action is wrapped in **`guard(fn)`**, which catches and shows the message
  via **`toast(msg, isError)`**. This is the uniform error strategy — no try/catch scattered in handlers.
- **State** is a handful of module globals: `currentRootInfo` (Explorer tree root), `selected` (current
  element), `procCurrent` (selected process), `regCurrent` (registry path), `lastCap` (`{rect}` of the last
  screenshot for coordinate mapping), and `…Loaded` flags to lazy-load tabs once.
- **URL deep-linking**: as you navigate, small helpers (`updateExplorerHash`, `updateProcHash`, and inline
  writes for registry/screen) mirror state into `location.hash` via `history.replaceState` (no history spam):
  `#explorer/<winRef>/<selRef>`, `#processes/<pid>/<selRef>`, `#registry/<path>`, `#screen`. On load,
  `restoreFromHash` clicks the right tab and rebuilds the state — for Explorer it reloads the *window* then
  re-selects the element by identity (deeper depth so nested elements are found); if a ref is stale (server
  restarted), it shows a clear "no longer available" message and drops the dead hash.
- **Live updates**: a small SSE consumer subscribes to `/events` and prepends focus/window/process events to
  the UI Events list; a `setInterval` re-polls machine/control/macro state every ~5s to keep the topbar and
  Safety card fresh; the brand dot goes `live` on first success.
- **Init** (bottom of the script): fetch machine info + windows, restore the hash, wire all the buttons, start
  the SSE + poll timers.

## Fleet dashboard — structure & differences

Same token system and three-state theming; a **sticky, blurred** topbar (`backdrop-filter`). Topbar: brand +
live dot + connected-count · a **← All PCs** back button (hidden until you open a machine) · a cross-link to
*this server's own* local console (clearly labelled — it is NOT the selected agent, which has no web UI) · a
**＋ RDP** button · a **▶ Go live** toggle (see below) · **Audit** · **Refresh** · **Theme**.

`<main>` holds two mutually-exclusive views: **`#grid`** (a responsive `auto-fill minmax(320px,1fr)` grid of
agent tiles) and **`#detail`** (one agent, toggled via a `.show` class). A tile is a `.card` with a 16:9
`.thumb` (the live screenshot, `object-fit:cover`) and a `.meta` block (name + desktop `.pill` + a mono
`.sub` line of id/monitors/elevation). RDP agents get an `RDP` pill and a small **✕** remove button; their
detail view gets a "capture + input only" note plus **✕ Disconnect RDP** and **⬇ Install native agent**.

**Opening an agent** (`openAgent`) fills `#detail`: a `.dhead` (machine + pill + RDP bits), a `.ctrls` row
(monitor select, on-click action select, a live coord readout, Save), the `.viewer` (live screenshot with a
`.marker`), and an input row (type/keys). The `#viewer` click handler (`onViewClick`) maps the click to
desktop pixels the same way the local dashboard does and either reports coords, **picks** an element
(`/agents/{id}/uia/element-from-point`), or sends **left/right/double/move** to the agent — refreshing a
frame after.

**Live capture is OFF by default** — this is a deliberate privacy choice. A `paused` flag starts `true` (the
button reads "▶ Go live"); `thumbAll` (grid) and `refreshView` (detail) early-return while paused. Polling
also auto-pauses when the page isn't visible (`visibilitychange`). Opening a single agent still fetches *one*
frame (an explicit `force` bypass), so the detail view isn't blank. Deep-link is `#agent=<id>`.

Overlays: a **`#toast`**, an **`#auditdrawer`** (a fixed panel polling `/fleet/audit`, rows colored by kind —
connect/disconnect/action), and a **`#rdpModal`** (a centered form over a dim backdrop for ＋ RDP).

## Conventions worth copying

- **One error path**: `api` throws with `.status`; `guard` toasts; dangerous/irreversible actions get a
  `confirm()` first (dumps, RDP disconnect/install, input recording).
- **Color tells the truth**: orange = touches the real machine; red = refused/error; green = ok/connected.
- **Refs are volatile**: never persist a ref as identity across reloads — re-select by controlType+name+
  automationId (what `loadRoot`'s preserve and the hash-restore do).
- **Lazy everything**: tabs load on first activation; tree nodes expand on first caret click; the fleet grid
  only screenshots when you ask it to.
- **The dashboard is glue**: it holds almost no logic beyond DOM assembly, coordinate math, and hash state —
  all behavior is server endpoints (`07`) it calls through `api`.
