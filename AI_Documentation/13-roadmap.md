# 13 — Roadmap

Deskhand's design (`Deskhand.html`) lays out five delivery phases. Phases 1–3 are substantially built in
this repo; Phases 4–5 are "kept open" by the `IAutomationBackend` seam and are not implemented.

## Phase 1 — Prove the spine · DONE

Default desktop, end to end. HTTP (stdio for MCP) host → the shared `IAutomationBackend` → in-session
engines: FlaUI/UIA3 read + control patterns, Windows.Graphics.Capture (with PrintWindow/GDI fallbacks), and
`SendInput`. The `IAutomationBackend` seam exists from day one. Both hosts (HTTP dashboard + MCP) run.

**Built:** `LocalAutomationBackend`, `StaExecutor`, `UiaService`, `ScreenCapture`, `WgcCapture`,
`InputInjector`, `DesktopInfo`, `WindowService`, `ElementRegistry`, both hosts, the dashboard.

## Phase 2 — The hard part · SUBSTANTIALLY DONE (capture), input gated

Secure-desktop coverage. The `SecureCapture` primitive (`OpenInputDesktop` + `SetThreadDesktop` + GDI on an
MTA thread), the SYSTEM **Secure Helper** (`deskhand-secure`), the **Broker** (`deskhand-broker`,
winlogon-token duplication + `CreateProcessAsUser`), plus `POST /capture/input-desktop`, the MCP tool, and
the dashboard panel.

**Done & verified:** capture primitive, Secure Helper, and `/capture/input-desktop` against the Default
desktop.
**Needs a real elevated console (not sandbox-tested):** the Broker's SYSTEM-launch path (elevation +
`SeDebugPrivilege`).
**Not enabled:** driving *input* on the secure desktop — that requires a signed `uiAccess="true"` binary in
a trusted location plus admin policy. Capture is the reliable secure-desktop capability. Also planned but not
built: automatic input-desktop **auto-follow** across surface switches (state is *reported* per-response, not
actively followed by a supervising broker service).

## Phase 3 — Make it reliable · DONE

Handles, patterns, DPI, safety. The `elementRef` resolver with silent re-resolution and typed
`stale_element`, full control-pattern coverage, Per-Monitor-v2 correctness, and the §13-design security
controls: audit log, capture consent toast, and kill switch.

**Built:** `ElementRegistry` re-resolution, `GovernedBackend`, `ControlState`, `AuditLog`, `KillSwitch`,
`ToastNotifier`, capability env flags, `/control` + arm/disarm tools.

Not built from the design's Phase-3 wish list: a UIA `CacheRequest` batching optimization for subtree reads
(the design mentions it; the code reads properties per-element), and a configurable **redaction hook** to
mask regions before an image leaves the process.

## Phase 4 — Kept open · NOT BUILT

Fleet agent. The same in-session code behind a `FleetAgentBackend` implementing `IAutomationBackend` over
**gRPC/mTLS**, with outbound-dialing agents and a **central registry/routing** layer. Because the tool
contract is the seam, no host or tool code changes. What remains to build:

- A launcher/service that supervises per-machine agents.
- A gRPC service contract mirroring `IAutomationBackend` (UIA agent-only; capture/input portable).
- mTLS identity, an agent registry, and command routing to the right machine.

## Phase 5 — Kept open · NOT BUILT

RDP protocol backend. An `RdpProtocolBackend` that dials RDP to a host and drives it **over the wire with no
software on the target**, advertising only the **capture + input** subset of the contract (UIA needs
in-session code, so it is not offered by this backend). This is the reason the interface separates
agent-only members from transport-portable ones.

## Summary table

| Phase | Theme | Status |
|---|---|---|
| 1 | Default desktop, end to end | Done |
| 2 | Secure desktop | Capture done/verified for Default; SYSTEM broker path needs a real elevated console; secure input not enabled |
| 3 | Reliability & safety | Done (minus CacheRequest batching + redaction hook) |
| 4 | Fleet agent (gRPC/mTLS + registry) | Not built — seam ready |
| 5 | RDP protocol backend | Not built — seam ready |
