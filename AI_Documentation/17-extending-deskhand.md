# 17 — Extending Deskhand (cookbook)

Concrete, copy-adaptable recipes for adding capabilities, with the **exact files to touch, in order**, and
the gotchas that waste time. Read `16-implementation-reference.md` first for the map. Anchors here are
search strings (e.g. `AddSingleton(screenRecorder)`), stable across edits — grep for them.

**Decision: which recipe?**
- Reads/acts that belong to "driving a UI" (capture, input, a UIA operation) → **Recipe B** (backend seam) so
  they work over the fleet and RDP automatically.
- A self-contained capability with its own state/lifecycle (a recorder, a dumper, a browser) → **Recipe A**
  (standalone service). Route to the fleet with **Recipe C** if per-agent access is wanted.
- Pure per-machine info with no backend semantics (registry, start menu, virtual desktops) → **Recipe A** with a
  `static` service (no DI object needed).

---

## Recipe A — a standalone service (+ MCP + HTTP [+ dashboard])

The pattern behind `ScreenRecorder`, `ProcessDumper`, `RegistryService`, `StartMenuService`, `VirtualDesktopService`.

1. **`src/Deskhand.Core/Services/<Name>Service.cs`** — the logic. Return `record` DTOs (they serialize as
   camelCase JSON automatically). Conventions:
   - Static class if stateless (registry/apps/desktops). Instance class if it has state/lifecycle
     (recorder/dumper) — then take `Deskhand.Core.Governance.AuditLog? audit = null` and audit key actions.
   - **Saved files** → `Path.Combine(Environment.GetFolderPath(SpecialFolder.LocalApplicationData), "Deskhand", "<kind>")`,
     audited on write, and add a 24h janitor (`System.Threading.Timer`, cleanup on ctor + every 6h) — copy the
     `CleanupExpired` block from `ScreenRecorder.cs`.
   - Never throw across the API boundary for expected states — return an `error` field (see `RegistryService`)
     or a clear exception the host maps (ArgumentException→400, Unauthorized→handled).
2. **DI (instance services only)** — `src/Deskhand.Http/Program.cs` and `src/Deskhand.Mcp/Program.cs`, in the
   block near `AddSingleton(screenRecorder)`: `var x = new XService(auditLog); … builder.Services.AddSingleton(x);`.
   Static services need no DI.
3. **HTTP** — `src/Deskhand.Http/Program.cs`, near `api.MapGet("/processes"…)`:
   ```csharp
   api.MapGet("/foo", (XService x) => Results.Ok(x.List()));            // instance: inject
   api.MapGet("/bar", (string? path) => Results.Ok(XService.Do(path))); // static: call directly
   ```
   Add request records at the bottom next to `record PidRequest(int Pid);`. Gate sensitive/heavy actions:
   `if (!st.Armed) return Results.Json(new { error = "disarmed", type = "disarmed" }, statusCode: 403);` (inject `ControlState st`).
4. **MCP** — `src/Deskhand.McpTools/DeskhandTools.cs` (ONE file, shared by both MCP hosts). Add a static method:
   ```csharp
   [McpServerTool(Name = "deskhand_foo"), Description("… what + when + caveats …")]
   public static string Foo(XService x, ControlState state, int arg) {   // DI params first, then tool args
       if (!state.Armed) return "{\"error\":\"disarmed\"}";
       return Json(x.Do(arg));
   }
   ```
   `Json(...)` and `AsImage(...)` helpers are already in the file. Static services: just call `XService.Do(...)` — no DI param.
5. **Dashboard (optional)** — see Recipe D.
6. **Verify**: build, then `Invoke-RestMethod .../foo -Headers @{Origin=...}`; confirm the MCP tool via
   `tools/list` on `/mcp`.

---

## Recipe B — a new backend (`IAutomationBackend`) method

The pattern behind `GetElementFromPoint` and `GetProcesses`. Thread it through EVERY implementer or it won't compile.

1. **`src/Deskhand.Core/IAutomationBackend.cs`** — declare it.
2. **`src/Deskhand.Core/LocalAutomationBackend.cs`** — implement, marshalling UIA/capture/input onto the STA
   thread: `public T Foo(…) => _sta.Invoke(() => _uia.Foo(…));` (add the real work to `Services/UiaService.cs`).
3. **`src/Deskhand.Core/Governance/GovernedBackend.cs`** — decorate: reads → `Audited("foo", detail, () => inner.Foo(…))`;
   input-class → `RequireInput("foo"); …`; capture-class → `Capture("foo", …)`.
4. **`src/Deskhand.Core/Fleet/RemoteAgentBackend.cs`** — `Call<T>(FleetMethods.Foo, new { … })` (or `Send` for void).
5. **`src/Deskhand.Core/Fleet/FleetProtocol.cs`** — `public const string Foo = "foo";`.
6. **`src/Deskhand.Core/Fleet/AgentDispatcher.cs`** — in the switch: `FleetMethods.Foo => b.Foo(a.Str("x")!, a.Int("y")),`
   (use the `JsonArgs` helpers `Str/Int/IntN/Long/Bool/Obj<T>`).
7. **`src/Deskhand.Rdp/RdpBackend.cs`** — `public T Foo(…) => No<T>();` (RDP can't do UIA).
8. **HTTP + MCP + fleet HTTP + FleetTools** — expose like the neighbours (`/uia/element-from-point`,
   `deskhand_element_from_point`, `/agents/{id}/uia/element-from-point`, `deskhand_agent_element_from_point`).

---

## Recipe C — fleet-route an observation service (non-backend)

The pattern behind events / recording / input / dump / registry / apps / desktops. Observation services live
OUTSIDE `IAutomationBackend`, so they ride a separate rail.

1. **`FleetProtocol.cs`** — `public const string Foo = "foo";`.
2. **`Deskhand.Core/Fleet/AgentServices.cs`** — if the feature needs an instance service, add
   `public Services.XService? X { get; init; }`. (Static services need nothing.)
3. **`Deskhand.Core/Fleet/AgentDispatcher.cs`** — add a `case` in the **observation switch** (top of `Dispatch`,
   before the backend switch). Gate on a native-only service so RDP agents error cleanly:
   ```csharp
   case FleetMethods.Foo:
       if (svc.Dumper is null) throw new InvalidOperationException("Not available on an RDP agent (would read the connector).");
       return Services.XService.Do(a.Str("path"));   // or Req(svc.X, "x").Do(...)
   ```
   `Req(svc.X, "x")` throws a clean "This agent has no x service" if null.
4. **`Deskhand.Fleet.Agent/Program.cs`** — wire the instance service into the `new AgentServices { … }` initializer
   (the RDP connector in `Deskhand.Rdp/Program.cs` deliberately does NOT, so those calls error there).
5. **`Deskhand.Core/Fleet/RemoteAgentObserver.cs`** — `public JsonElement Foo(string? p) => Call(FleetMethods.Foo, new { p });`
   (returns the agent's raw JSON).
6. **`Deskhand.Fleet.Server/Program.cs`** — endpoint using the `O(id)` helper:
   `app.MapGet("/agents/{id}/foo", (string id, string? p) => Results.Ok(O(id).Foo(p)));`.
7. **`Deskhand.Fleet.Server/FleetTools.cs`** — `[McpServerTool(Name="deskhand_agent_foo")] … => Raw(O(r, audit, agentId, "foo").Foo(...));`
   (`O(...)` audits the action; `Raw(JsonElement)` passes the agent's JSON through).

---

## Recipe D — a dashboard tab (`src/Deskhand.Http/wwwroot/index.html`, single file)

The pattern behind the Processes and Registry tabs. Everything is vanilla JS + inline CSS; no build step; the host
serves it `no-cache` so a plain refresh shows changes.

1. **Tab button** — next to `data-tab="processes"`: `<button class="tabbtn" data-tab="foo">Foo</button>`.
2. **Panel** — before `<!-- SCREEN + INPUT -->`: `<div class="tab" id="tab-foo"> … </div>`.
3. **CSS** — a two-pane tab needs the grid (join `#tab-explorer.active, #tab-processes.active`); a single-pane
   scrolling tab joins `#tab-screen, #tab-registry { overflow:auto; }`. **The grid is opt-in per tab** — a tab left
   at the default `display:block` shows no right/detail pane (this was a real bug).
4. **JS** — `api(path, method, body)` throws on non-2xx with `err.status`/`err.data`; wrap handlers in `guard(fn)`
   for toasts. Load lazily in the tab handler (`if(b.dataset.tab==="foo"&&!fooLoaded) loadFoo()`).
5. **Deep-link** — mirror state to the hash with `history.replaceState(null,"","#foo/"+id)` when it changes, and
   restore it in `restoreFromHash` (early in the chrome section). Refs go stale on server restart → show a proper
   error and drop the dead hash (see the `#explorer` restore for the template).
6. **Shared element detail** — `selectNode(info, selEl)` derives its detail/tree panes from the clicked node's
   `.tab`, so reusing `renderNode` inside a new tab "just works" if the tab has `.tree` + `.detailcol` elements.

The **fleet** dashboard (`Deskhand.Fleet.Server/wwwroot/index.html`) is separate: agent tiles + a detail view;
live capture is OFF by default (`let paused=true`), `#agent=<id>` deep-link, RDP tiles get pill + buttons.

---

## Conventions (apply everywhere)

- **Governance**: gate anything that injects input, captures, or is heavy/sensitive on `ControlState.Armed`
  (backend methods via `GovernedBackend`; standalone services at the host/tool layer). Everything is audited via
  `AuditLog` (JSONL under `%LOCALAPPDATA%\Deskhand`). Capture-class also fires the debounced toast.
- **Sensitive output** (dumps, memory, keystrokes): audit it, retain it in the predefined dir with 24h auto-delete,
  and say so in the tool `Description` and any confirm dialog.
- **Consent for observation** of the user (input recording): persistent banner + toast via `IActivityIndicator` +
  `ICaptureNotifier`.
- **DTOs**: `record` types in `Models.cs` or beside the service; JSON is camelCase (System.Text.Json defaults).
- **COM**: shell/virtual-desktop COM has worked off the request thread so far; if you hit `RPC_E_WRONG_THREAD`,
  marshal onto `LocalAutomationBackend._sta`.

## Build / test / commit loop (exact)

```
# 1. Stop the host(s) that lock the DLLs you're rebuilding (else MSB3021/3027 copy errors)
Get-Process deskhand-http,deskhand-fleet,deskhand-agent,deskhand-mcp | Stop-Process -Force
# 2. Build (whole solution to keep outputs consistent)
dotnet build Deskhand.slnx -c Release --nologo
# 3. Run the newest exe (single-project builds land in bin\x64\Release\net9.0-…; pick newest by LastWriteTime)
$env:DESKHAND_PORT=8791; Start-Process <…>\deskhand-http.exe -WindowStyle Hidden
# 4. Test: Invoke-RestMethod http://localhost:8791/<route> -Headers @{Origin='http://localhost:8791'}
#    Fleet: start deskhand-fleet.exe (+ DESKHAND_RDP_PATH for +RDP) and a deskhand-agent.exe pointing at it.
```
Traps: `dotnet build` with two `src/…` args fails (build one target or the `.slnx`); exe names are
`deskhand-fleet.exe`/`deskhand-agent.exe`; a running host holds its DLLs; the dashboard is `no-cache` so no Ctrl+F5
needed. Verify against `15-test-plan.md`. Commit per feature; end messages with the Co-Authored-By trailer.
