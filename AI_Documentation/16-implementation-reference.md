# 16 — Implementation Reference & Handoff

A deep, code-level map for an AI continuing this project. Docs `01`–`13` cover the original design;
`14`/`15` cover events/hooks/recording/RDP + the test plan; **this file is the current-state ground truth**
with exact files, types, wiring, the full API surface, build/run, and open items. When in doubt, read the
file named here — paths are repo-relative and current as of the latest `main`.

## Repo map (projects)

| Project (`src/…`) | AssemblyName / kind | Responsibility |
|---|---|---|
| `Deskhand.Core` | library | The seam + all logic: `IAutomationBackend`, DTOs (`Models.cs`), `Services/*` (UIA, capture, input, recorder, input-recorder), `Events/*`, `Governance/*`, `Macros/*`, `Fleet/*`. |
| `Deskhand.McpTools` | library | `DeskhandTools.cs` — the `[McpServerToolType]` shared by both MCP hosts (stdio + HTTP). |
| `Deskhand.Ui` | library (WinForms) | `ToastNotifier` (capture toast), `RecordingIndicator` (persistent banner). Both `ICaptureNotifier`/`IActivityIndicator`. |
| `Deskhand.Http` | `deskhand-http.exe` | Local host: dashboard (`wwwroot/index.html`) **and** MCP over HTTP at `/mcp`, one process, shared governed backend. Port `DESKHAND_PORT` (default 8791). |
| `Deskhand.Mcp` | `deskhand-mcp.exe` | Stdio MCP host (same tools, same backend). |
| `Deskhand.Fleet.Server` | `deskhand-fleet.exe` | Fleet hub: agent WS endpoint, fleet dashboard, fleet MCP at `/mcp`, RDP connector manager. Port `DESKHAND_FLEET_PORT` (default 8799). |
| `Deskhand.Fleet.Agent` | `deskhand-agent.exe` | Runs on a target PC; dials OUT to the fleet; serves the local backend + observation services. |
| `Deskhand.Fleet.Launcher` | Windows Service | Spawns per-session agents (WTS APIs + CreateProcessAsUser). |
| `Deskhand.Rdp` | `deskhand-rdp.exe` | Zero-install RDP backend (`mstscax`); CLI + `--fleet` connector mode. `lib/*.dll` are AxImp interop. |

**⚠ exe names**: fleet server/agent are `deskhand-fleet.exe` / `deskhand-agent.exe` (NOT `-fleet-server`/`-fleet-agent`).

## The backend seam (`Deskhand.Core/IAutomationBackend.cs`)

One interface, 30+ members: orientation (`GetDesktopState/MachineInfo/ForegroundWindow/FocusedElement/GetTopLevelWindows/GetProcesses`), `LaunchProcess`, UIA read (`GetTree/Find/WaitForElement/GetElement/GetAllProperties/GetElementFromPoint`), UIA act (`Invoke/SetValue/Toggle/ExpandCollapse/Select/SetFocus`), capture (`CaptureScreen/Region/Window/WindowByRef/Element/InputDesktop`), input (`MouseMove/Click/Down/Up/Scroll/TypeText/SendKeys`).

**Implementations** (all implement the full interface):
- `LocalAutomationBackend` — in-session. **UIA** (COM, not thread-safe) is marshalled onto one `StaExecutor` STA thread; **capture** (GDI) and **input** (SendInput) are thread-agnostic and run **off** it (input serialized on `_inputGate` so concurrent actions don't interleave), so a screenshot / input action / UIA query can proceed concurrently instead of queuing behind each other (ref-based captures resolve the element on the STA first, then capture off it). Owns `UiaService`. **`LaunchProcess`** watches for the window three ways so packaged/Store apps work: the launched process's `MainWindowHandle`, then a **new top-level window** (diffed against a pre-launch snapshot) owned by the launched pid **or a process whose name relates to the launched exe** (the Win11 Notepad/Terminal handoff to a different pid), then a fallback by pid. The wait is capped by `waitForWindowMs` (**default 10 s** — sized for slow VMs; polls every 100 ms, returns early on a window; `0` = don't wait). Failure handling: a bad path throws (surfaced as the real Win32 message — the MCP `launch_process` tool is wrapped in `Try()` so it isn't the SDK's generic error), while a NULL `Process.Start` (shell reused a process — a URL/doc opened in an already-running app) is **not** treated as an error: it returns a normal result and still tries to catch a new window.
- `GovernedBackend(inner, ControlState, AuditLog, ICaptureNotifier?, MacroRecorder?)` — decorator: kill-switch gates (`RequireInput`/`RequireCapture`), audits every call, capture toast (**debounced ≤1/6s** — this stopped the fleet toast storm), feeds the macro recorder. **This is what the hosts register as `IAutomationBackend`.**
- `RemoteAgentBackend(IAgentLink)` — server-side; forwards each call over the fleet WS to an agent.
- `RdpBackend(RdpHost, hostName)` — capture + input only; everything UIA/process/launch throws `NotSupportedException` via `No<T>()`.

Element refs are opaque strings (`el_…`) from an in-memory `ElementRegistry` in `UiaService`. `Resolve()` re-resolves a stale entry from a stored selector recipe (hwnd + name/autoId/controlType); truly-gone ⇒ `StaleElementException` ⇒ HTTP 404. **Refs do not survive a host restart** (registry is in-memory) — this is why URL deep-linking shows a proper "gone" error.

## Non-backend services (`Deskhand.Core`)

- `Services/UiaService.cs` — FlaUI wrapper. `GetTree` is bounded to **`TreeNodeBudget = 4000`** nodes and `maxChildren`≤500 (Chromium a11y can be enormous). `GetElementFromPoint` = `_automation.FromPoint`. `GetProcesses` = `Process.GetProcesses()` correlated with top-level windows by pid.
- `Events/EventHub.cs` — ring buffer (500) + SSE channels. Event types: `focus_changed`, `window_opened` (from `UiaService.StartEvents`), `process_started`, `process_exited`.
- `Events/ProcessWatcher.cs` — polls process set every 1s → publishes process events; `WaitForProcess(event,name?,pid?,timeoutMs)` blocking.
- `Services/ScreenRecorder.cs` + `RecordingEncoders.cs` — GDI frame timer → `AviMjpegWriter` (RIFF/MJPG) or `GifWriter` (GDI+ per-frame quantize, re-wrapped into looping GIF89a with delays). Hard `MaxDurationMs` auto-stop (≤5min). Files → `%LOCALAPPDATA%\Deskhand\recordings`, audited (`recording_saved`), **24h auto-delete** (`recording_expired`, janitor every 6h). Constructed with `AuditLog`.
- `Services/InputRecorder.cs` — global `WH_MOUSE_LL`/`WH_KEYBOARD_LL` hooks on an STA thread; a worker thread resolves each click's element (via injected `Func<int,int,ElementInfoDto?>` = raw `LocalAutomationBackend.GetElementFromPoint`, **unaudited** to avoid flooding). Coalesces printable keys into `text` runs; decodes injected Unicode (`VK_PACKET`). Takes optional `ICaptureNotifier` + `IActivityIndicator` → **persistent banner + toast while recording** (consent). Event kinds: `click`(+element), `scroll`, `text`, `key`.
- `Services/ProcessDumper.cs` — full-memory `.dmp` via `MiniDumpWriteDump` (enables SeDebugPrivilege best-effort). Dir `%LOCALAPPDATA%\Deskhand\dumps`, audited (`process_dump`), 24h auto-delete, streamed download. Gated on `ControlState.Armed` at the host layer. Dumps can be **hundreds of MB–GB** (Notepad ≈ 387 MB) — retention matters.
- `Services/RegistryService.cs` — **static**, read-only. `Browse(path)` → `RegKeyDto(path, hive, subKeys[], values[], error?)`. Empty path = hive roots (HKLM/HKCU/HKCR/HKU/HKCC). Access-denied keys return `error`, never throw.
- `Services/StartMenuService.cs` — **static**. `List()` → the `.lnk/.url` shortcuts under both Start Menu\Programs trees. Launch via `LaunchProcess(path)`. No UWP.
- `Services/VirtualDesktopService.cs` — **static**, documented `IVirtualDesktopManager` only. `ListByWindow()` groups visible top-level windows by desktop GUID (current first); `MoveWindowToCurrent(hwnd)` / `MoveWindowToDesktop(hwnd, guid)`. No list/switch/create (undocumented, per-build). COM runs fine off the request thread.
- `Services/ScreenshotStore.cs` — save capture bytes to `%LOCALAPPDATA%\Deskhand\screenshots` (audited `screenshot_saved`, 24h janitor). `Save(bytes,format)`→ `{FileName,File,SizeBytes}`; `PathFor`/`List`. Used by the capture `save` option.
- `Services/FileSystemService.cs` — **static**, file manager. `Browse(path)` (drives when empty), `ReadFileBase64`/`WriteFileBase64` (≤25 MB), `Delete` (Recycle Bin via `SHFileOperation` unless permanent; refuses drive roots), `Rename`/`Move`/`Copy` (recursive), `Zip`/`Unzip`. Mutations gated on armed + audited at the host layer.
- `Services/ShellService.cs` — **static**, one-shot exec. `Run(shell,command,cwd,timeoutMs)` spawns powershell/pwsh/cmd, captures stdout/stderr/exit, output-capped, timeout kills the tree. **OFF unless `DESKHAND_ENABLE_SHELL`**, then host-gated on armed + audited.
- **Inventory services** (all **static**, read-only): `SystemInfoService` (OS/BuildLab, uptime, CPU+live load, memory, disks, network, firewall, and **`SessionsService`** WTS sessions folded in), `HardwareInfoService` (disks→partitions→volumes, Windows updates/KBs, PnP devices, drivers, audio devices, and `Detail()`: BIOS/board/computer, GPUs with **true VRAM via DXGI** `IDXGIAdapter1`, monitors via `WmiMonitorID`, RAM sticks), `AudioService` (Core Audio COM — default playback/recording device, volume %, mute), `SoftwareService` (installed programs from the registry, services, startup, env, printers, shares, scheduled-tasks COM), `SecurityService` (TPM/SecureBoot/BitLocker/activation/Defender+AV/pending-reboot — elevation-gated items degrade to unknown), `UsersService` (local users + groups with membership via `netapi32`), `PowerService` (AC/battery/wear/plan), `NetConnectionsService` (TCP/UDP + owning PID via `GetExtendedTcpTable`), `DiagnosticsService` (recent event-log errors via `EventLogReader`, disk SMART health). Most use `System.Management` (WMI); `System.Diagnostics.EventLog` + `System.Management` are the added Core packages.
- `Governance/` — `ControlState` (armed/input/capture/notify, `FromEnvironment`), `AuditLog` (JSONL, `HashImage`), `KillSwitch` (Ctrl+Alt+Pause), `ICaptureNotifier`, `IActivityIndicator`.
- `Macros/` — `MacroRecorder`/`MacroPlayer` (the agent's OWN actions; distinct from `InputRecorder` = the user's).

## Host DI wiring (who constructs what)

`Deskhand.Http/Program.cs` and `Deskhand.Mcp/Program.cs` build the same graph:
```
controlState, auditLog, captureNotifier(ToastNotifier), macroRecorder, eventHub
localBackend = new LocalAutomationBackend(); localBackend.StartEvents(eventHub)
processWatcher = new ProcessWatcher(eventHub)
screenRecorder = new ScreenRecorder(auditLog)
recordingIndicator = new RecordingIndicator()          // Http/Mcp
inputRecorder = new InputRecorder(localBackend.GetElementFromPoint, captureNotifier, recordingIndicator)
IAutomationBackend = new GovernedBackend(localBackend, controlState, auditLog, captureNotifier, macroRecorder)
+ AddMcpServer().WithHttpTransport()/WithStdioServerTransport().WithToolsFromAssembly(DeskhandTools)
```
`Deskhand.Http` also serves `wwwroot` static files with **`Cache-Control: no-cache` on `.html`** and `ContentRootPath = AppContext.BaseDirectory` + `StaticWebAssetsEnabled=false` + `Content Update wwwroot CopyToOutputDirectory` (the fix for the old `GET / → 404`).

## Fleet architecture

Agent **dials out** (WebSocket) to the server — no inbound port on the target. `Deskhand.Core/Fleet/`:
- `FleetProtocol.cs` — `FleetMethods` (string consts), `AgentHello(AgentId, MachineName, MachineInfoDto)`, `FleetCommand`/`FleetResult`, `FleetJson`.
- `AgentServices` — bundles `Backend` + optional `Events`/`Processes`/`Recorder`/`Input` + `RdpInstallAgent` delegate. Threaded through `AgentConnection.RunForeverAsync` → `ConnectOnceAsync` → `AgentDispatcher.Dispatch(cmd, services)`.
  - **Hello uses `services.Backend.GetMachineInfo().MachineName`**, not `Environment.MachineName` — so an RDP connector registers under the *remote* host.
- `AgentDispatcher.Dispatch` — big switch: backend methods → `svc.Backend`; observation methods → `svc.Events/Processes/Recorder/Input` (throws a clean error if that agent lacks the service); `RdpInstallAgent` → the delegate.
- Server side: `RemoteAgentBackend(link)` (IAutomationBackend over RPC) + `RemoteAgentObserver(link)` (events/hooks/recording/input/install — returns raw `JsonElement`).
- `Deskhand.Fleet.Server/Program.cs`: `A(id)` → RemoteAgentBackend, `O(id)` → RemoteAgentObserver. `RdpConnectorManager` spawns/kills `deskhand-rdp --fleet` connectors (password via `DESKHAND_RDP_PASSWORD` env, not cmdline; connector exe from `DESKHAND_RDP_PATH` or next to the server).
- Agent (`Deskhand.Fleet.Agent/Program.cs`) wires ALL observation services + `RecordingIndicator`, so fleet-driven recording shows consent on that PC.

## Full API surface

**Local MCP tools (~81)** — `DeskhandTools.cs` — includes `deskhand_dump_process` (full-memory dump, gated on armed; result carries a `url:/dumps/{name}`), `deskhand_registry_browse`, `deskhand_browse_files`, the **file-ops** (gated on armed + audited): `read_file`/`write_file`, `delete_path` (→ Recycle Bin unless permanent), `rename_path`, `move_path`, `copy_path`, `zip`, `unzip`; `deskhand_run_command` (one-shot PowerShell/cmd — off unless `DESKHAND_ENABLE_SHELL`, then armed+audited); and the **inventory** group (read-only): `system_info`, `sessions`, `disks`, `windows_updates`, `devices`, `drivers`, `audio_devices`, `audio_defaults`, `hardware_detail`, `installed_programs`, `services`, `startup_items`, `env_vars`, `printers`, `shares`, `scheduled_tasks`, `security_posture`, `local_users`, `local_groups`, `power`, `net_connections`, `event_errors`, `disk_health`. **Capture tools** take `save=true` → save to the screenshots dir + return `url:/screenshots/{name}` (text) instead of the inline image. HTTP mirrors the file/shell tools: `GET /fs` (browse), `GET /fs/download` (stream), `POST /fs/download-zip` (multi→zip), `POST /fs/upload` (multipart), `POST /fs/{delete,rename,move,copy,zip,unzip}`, `POST /shell/run`. Core tool groups: orientation (`machine_info, desktop_state, list_windows, list_processes, foreground_window, focused_element`), governance (`control_status, disarm, arm`), macros (`macro_start/stop/status/expect/play`), events/hooks (`get_events, wait_for_process`), UIA read (`get_tree, find_elements, wait_for_element, get_element, get_all_properties, element_from_point`), UIA act (`invoke, set_value, toggle, expand_collapse, select, set_focus`), recording (`record_start/stop/status`), user-input (`user_input_start/stop/get`), capture (`capture_screen/region/window/element/input_desktop`), input (`mouse_move/click/scroll, type_text, send_keys`).

**Fleet MCP tools (40)** — `FleetTools.cs`, all `deskhand_agent_*` (incl. `registry_browse`, `dump_process` — routed to the agent; the `.dmp` stays on the agent, registry blocked on RDP agents): `list_agents` + `info, list_windows, list_processes, foreground, get_tree, find, wait_for_element, get_properties, element_from_point, capture_screen, capture_element, invoke, set_value, set_focus, click, move, type, keys, launch, get_events, wait_for_process, record_start/stop/status, user_input_start/stop/get`, and the **files + shell** parity (all native-agent-only; RDP agents return a clean error): `browse_files, read_file, write_file, delete_path, rename_path, move_path, copy_path, zip, unzip, run_command, system_info, dumps` (list; **download an agent dump** via `GET /agents/{id}/dumps/{name}` — the server reads the `.dmp` off the agent as base64, capped <1.5 GB). Fleet HTTP mirrors them under `/agents/{id}/fs`, `/agents/{id}/fs/{download,write,delete,rename,move,copy,zip,unzip}`, `/agents/{id}/shell/run`, `/agents/{id}/system`, `/agents/{id}/dumps`, `/agents/{id}/dumps/{name}`. `run_command` still needs the agent to have `DESKHAND_ENABLE_SHELL` set. The agent-side ops aren't `armed`-gated (the fleet token is the trust boundary, as with dump/registry).

**Local HTTP routes** — `Deskhand.Http/Program.cs`: `/health` (also reports `requiresToken`/`tls`), `/token` (gated — hands the token to the trusted dashboard), `/machine, /desktop/state, /foreground, /focused, /windows, /processes, /process/launch, /process/wait, /uia/*, /capture/{screen,region,window,element,input-desktop}` (each takes `save`), `/screenshots`, `/screenshots/{name}`, `/mouse/*, /keyboard/*, /control, /macro/*, /events/poll, /events (SSE), /record/*, /recordings/{id}, /input/record/*, /process/dump, /dumps, /dumps/{name}, /registry?path=`, the **file manager** `/fs`, `/fs/{download,download-zip,upload,delete,rename,move,copy,zip,unzip}`, `/shell/run`, `/apps`, `/desktops`, and the **inventory** routes `/system`, `/sessions`, `/audio/default`, `/security`, `/users`, `/groups`, `/power`, `/net/connections`, `/hardware/{disks,updates,devices,drivers,audio,detail}`, `/software/{programs,services,startup,env,printers,shares,tasks}`, `/diagnostics/{events,disk-health}`, and `/mcp`.

**Fleet HTTP routes** — `Deskhand.Fleet.Server/Program.cs`: `/health, /agents, /fleet/audit, /fleet/rdp/{connect,list,disconnect,install}, /agents/{id}/…` (machine, windows, processes, process/launch, process/wait, uia/*, events, record/*, recordings/{recId}, input/record/*, registry, process/dump, **dumps, dumps/{name}**, capture/*, mouse/*, keyboard/*, apps, desktops, **fs, fs/{download,write,delete,rename,move,copy,zip,unzip}, shell/run, system**), `/mcp`.

## Dashboards (`*/wwwroot/index.html`, single-file, no build)

- **Local** (`Deskhand.Http`): a two-row topbar (status bar + a `.tabbar` tab strip). Tabs: **Explorer · Processes · Files · Registry ¦ System · Apps · Hardware · Software ¦ Shell · Screen · Connect** (see `18-dashboard-ux-architecture.md` for each). Explorer+Processes share a two-pane grid; the rest are single scrolling panes lazy-loaded on first open. `selectNode` derives its detail/tree from the clicked node's `.tab`. **URL deep-linking** via `history.replaceState` (`#explorer/<winRef>/<selRef>`, `#processes/<pid>/…`, `#files/<path>`, `#registry/<key>`, restored with stale-ref errors). System/Hardware/Software/Apps render the inventory; Connect gives copy-paste MCP configs + the token; Files is a full file manager; Shell runs one-shot commands.
- **Fleet** (`Deskhand.Fleet.Server`): grid of agent tiles (live thumbnails **OFF by default** — "▶ Go live", auto-pause when hidden). Detail view = capture + click-to-control + pick, **plus per-agent Files / Shell / System sub-tabs** (`wireAgentPanels`, native agents only). RDP tiles: `RDP` pill, "✕ Disconnect RDP", "⬇ Install native agent". `＋ RDP` modal → `/fleet/rdp/connect`. `#agent=<id>` deep-link. Both dashboards served `no-cache`.

## RDP details (`Deskhand.Rdp`)

- `RdpHost.cs` hosts `AxMsRdpClient10NotSafeForScripting` off-screen on an STA thread. `ConnectAsync(host,user,domain,pass,timeout,nla=true,port=0)` sets `EnableCredSspSupport=nla` (`--no-nla` for mock/legacy), `NegotiateSecurityLayer=true`, `AuthenticationLevel=2`, `RedirectDrives=true`, `KeyboardHookMode=2` (Win key → remote); `port>0` sets `RDPPort` for a non-standard port. **TLS handshake:** it offers the negotiated security layer and warns-then-connects on an unverifiable cert — a background timer polls for the modal "identity cannot be verified" dialog (`#32770` / control 14004) and clicks Yes (`AcceptCertWarningIfPresent`, nothing persisted). *This replaced `AuthenticationLevel=0`, which requested standard RDP, skipped TLS, and faulted mstscax (SEHException) against a TLS-only server.* Capture = `PrintWindow(PW_RENDERFULLCONTENT)` of the control. **Input** posts to the render child `IHWindowClass` (focused first); **coords mapped** capture-space→child-space via `ClientToScreen` offset. `--diag` dumps child windows. `RunCommand` = Win+R chord → type → Enter (the install bootstrap). `ToTsClient` maps `C:\…`→`\\tsclient\C\…`.
- `Program.cs`: `<host> <user> <pass> [--domain/--size/--capture/--timeout/--diag/--no-nla/--port]`; `--fleet <ws> [--id]` joins the fleet (Backend=RdpBackend, `RdpInstallAgent` delegate set). Password also read from `DESKHAND_RDP_PASSWORD` env.
- **Tests** (`tests/Deskhand.Rdp.Tests`, xUnit + SkippableFact): `MockRdpFixture` launches the sibling **`mock-rdp`** server (a TLS-only MS-RDPBCGR mock; `MOCK_RDP_DIR` or `%USERPROFILE%\source\repos\mock-rdp`) on a free port, then the test drives the built **`deskhand-rdp.exe` as a subprocess** and asserts `CONNECTED` + a valid captured PNG. It runs the exe out-of-process on purpose: the mstscax control throws a native SEHException in the test host's message loop. Skips cleanly when the mock repo or the exe isn't present, so CI stays green (CI builds the project via the slnx but doesn't run it — run `dotnet test` locally with the mock checked out).
- **Install-over-RDP flow**: `/fleet/rdp/install {id}` → `RemoteAgentObserver.InstallAgent` → agent dispatch → the connector's `RdpInstallAgent` delegate → `RunCommand("\"\\tsclient\C\…\deskhand-agent.exe\" ws://fleet")`. Needs a **self-contained** `deskhand-agent.exe` next to `deskhand-rdp.exe` (`installer/publish-agent.ps1`) or `DESKHAND_AGENT_PATH`.

## Build & run

- **Build:** `dotnet build Deskhand.slnx -c Release`. TFM `net9.0-windows10.0.19041.0`, x64.
- **⚠ Build gotchas:** a running host locks its `bin` DLLs → `MSB3021/3027` copy errors; stop the exe first. Single-project builds output to `bin\x64\Release\net9.0-…` (find the newest exe by `LastWriteTime`). `dotnet build` with two project args fails — build one target or the `.slnx`.
- **Run (typical dev):**
  ```
  $env:DESKHAND_PORT=8791; deskhand-http.exe            # dashboard + MCP @ /mcp
  deskhand-fleet.exe                                     # fleet hub @ 8799 (set DESKHAND_RDP_PATH for +RDP)
  deskhand-agent.exe ws://127.0.0.1:8799/agent/connect  # a native agent
  ```
- **MCP client:** `claude mcp add --transport http deskhand http://127.0.0.1:8791/mcp` (+ `deskhand-fleet` @ 8799/mcp).
- **Acceptance:** run through `15-test-plan.md`.

### Exposing the port to the network (`DESKHAND_BIND`)

Both HTTP hosts bind **loopback only** by default. To open a host to the LAN *on demand*:
- **Local server** (`Deskhand.Http/Program.cs`): set `DESKHAND_BIND` = `any`/`0.0.0.0`/`*` (all
  interfaces) or a specific local IP. **Fail-fast:** if `DESKHAND_BIND` is set to a non-loopback
  value and `DESKHAND_TOKEN` is empty, the process prints a refusal and `Environment.Exit(3)` — you
  cannot expose an unauthenticated server. When `external`, the security middleware changes: it skips
  the loopback `Host` check, drops the tokenless same-origin trust (Sec-Fetch-Site is forgeable
  off-box), and requires the token for **every** request including `/mcp` and the browser dashboard.
  `/health` stays open; static `index.html` is served unauthenticated but is inert without the token.
  `BearerOf(ctx)` accepts the token from `Authorization: Bearer` **or** a `?token=` query param (the
  latter for `EventSource("/events")` and any `<img src>`, which can't set headers).
- **Fleet server** (`Deskhand.Fleet.Server/Program.cs`): `DESKHAND_FLEET_BIND=any` +
  mandatory `DESKHAND_FLEET_TOKEN` (same fail-fast). When exposed, `/mcp` no longer bypasses the
  token; `/agent/connect` and `/health` are always exempt (the former authenticates itself).
- **Dashboards** read the token from `?token=` on first load, scrub it from the URL bar
  (`history.replaceState`), stash it in `sessionStorage` (`deskhand_token` / `deskhand_fleet_token`),
  and attach it as a bearer header in `api()`; a `401` with no token triggers a `prompt()` + retry.

### Optional HTTPS (`Deskhand.Core/TlsSupport.cs`)

`TlsSupport.FromEnvironment(prefix)` returns an `X509Certificate2?` (null → stay on HTTP). Sources:
`<prefix>TLS_CERT` (a `.pfx`, + optional `<prefix>TLS_PASSWORD`) loaded via `X509CertificateLoader`,
or `<prefix>TLS=self-signed` which generates an ephemeral serverAuth cert (CN=machine; SAN localhost
+ hostname + IPv4s) and round-trips it through a PFX so Kestrel gets a persistable key (avoids the
Windows "ephemeral key set" bind failure). Prefix is `DESKHAND_` (local) / `DESKHAND_FLEET_` (fleet).
Both `Program.cs` pass the cert into a `void Https(ListenOptions o)` given to every `Listen*` call and
flip `scheme` to `https` for banners/`allowedOrigins`. Caveat: the fleet's spawned RDP connector still
dials `ws://` loopback, so fleet TLS + the built-in RDP connector don't mix (native agents dial the
URL you give them, so use `wss://` there). No cert rotation/ACME — reverse-proxy for that.

### CI (`.github/workflows/build.yml`)

`windows-latest`: `dotnet build Deskhand.slnx -c Release`, then a **pwsh smoke test** that runs the
built `deskhand-http.exe` three ways — loopback `/health`==200, `DESKHAND_BIND=any` with no token
refuses to start (exit 3), and `DESKHAND_TLS=self-signed` `/health` over HTTPS ==200 — then builds
MSI (`installer/build-msi.ps1`, **WiX pinned to 5.0.2** to dodge the v6/v7 OSMF EULA gate `WIX7015`;
the script now checks `$LASTEXITCODE` so a failed `wix build` can't silently drop the MSI), MSIX, and a
**self-contained `deskhand.zip`** (`dotnet publish -r win-x64 --self-contained`, `deskhand-http.exe` at
the zip root — what `database/provision/install-deskhand.ps1` extracts to `C:\Deskhand`). A `release`
job attaches all three to the GitHub Release on `v*` tags. The fail-fast check runs before backend/UIA
construction (robust headless); the live-server checks rely on the runner's interactive desktop.

## Open items / not yet verified

- **RDP install last-mile**: the full RPC chain is verified (non-RDP agent returns a clean "not an RDP connector" error), but the actual remote Win+R bootstrap needs a real RDP target + a published self-contained agent. Fragile (UAC, login screen, AV, focus, drive-redirection policy). Fallback if Win+R misfires: click Start instead.
- **RDP synthetic input** is `mstscax`-version-sensitive; `--diag` reveals the input-surface class if it isn't `IHWindowClass`. If clicks scale-drift toward an edge (vs constant offset), add a scale factor in `RdpHost.Map`.
- **Fleet recording** captures the agent's local screen via its own `ScreenRecorder` (good). But `ScreenRecorder` uses GDI on the machine it runs on — it does NOT pull from `RdpBackend` capture, so recording an *RDP-only* agent wouldn't capture the RDP session (RDP agents have no `Recorder` service anyway → clean error).
- **Explorer deep-link re-select** matches by identity (controlType+name+automationId); duplicate unnamed nodes (many blank `Pane`s) may select the first match.
- **Elevation-gated inventory** (TPM, BitLocker, SMART predict, sometimes power capacities) returns unknown/empty when Deskhand runs unelevated (its default). Secure Boot, activation, Defender, pending-reboot, and everything else read fine unelevated.
- **GPU VRAM** now comes from DXGI (`IDXGIAdapter1`, uncapped); WMI `AdapterRAM` is still shown as a fallback if the DXGI name doesn't match.
- Automated tests: only `tests/Deskhand.Rdp.Tests` (skips without the mock); everything else is the manual `15-test-plan.md`. CI runs build + a smoke test and publishes MSI/MSIX + a self-contained `deskhand.zip` on `v*` tags.

## Gotchas that looked like bugs but weren't (save time)

- **Empty-body HTTP 400 on `/uia/tree`** = an *array* `rootRef` (a `/windows` filter matched several windows) serialized into a string field. Pass one scalar ref.
- **Chromium/Edge `get_tree` 404** = top-level window refs go stale within ms; use `element_from_point`. `--force-renderer-accessibility` only helps a *fresh* instance (Chromium single-instance; new `--user-data-dir`).
- **Stale dashboard UI** = browser cached `index.html` (now fixed with `no-cache`; older builds needed Ctrl+F5).
- **`VK_231` in input recording** = injected Unicode via `VK_PACKET`; decoded now.
- Git warns `LF will be replaced by CRLF` on every commit — harmless.
