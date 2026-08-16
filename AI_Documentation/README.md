# Deskhand — Reconstruction Documentation

This folder is a complete, exact technical specification of the **Deskhand** project, written so that a
human developer or an AI coding agent could recreate it from an empty folder without seeing the original
source. Every type name, method name, P/Invoke signature, package version, and file path here matches the
real code.

## What Deskhand is

Deskhand is a **single-machine Windows desktop-automation system**. It exposes three capability families —
**UI Automation** (via FlaUI/UIA3), **screen capture** (GDI, PrintWindow, and Windows.Graphics.Capture),
and **synthetic input** (SendInput) — for the local Windows box, over **two independent hosts** that share
**one backend contract** (`IAutomationBackend`): a **loopback HTTP server** with a single-page web
dashboard, and an **MCP (Model Context Protocol) stdio server** that presents the same capabilities as
tools to an LLM. Governance (kill switch, capability gates, audit log, screenshot toast) is enforced once,
at the backend seam, so both hosts inherit it. A five-phase roadmap keeps a future gRPC fleet backend and
an RDP-protocol backend able to implement the same contract without touching the host layer. Phases 1–3
(Default desktop, secure-desktop capture primitive + helpers, reliability/safety) are substantially built.

## The document set

| File | Contents |
|---|---|
| `01-architecture.md` | Component/process model, the `IAutomationBackend` seam and why it exists, the two hosts, data flow of a single tool call, diagrams. |
| `02-environment-and-dependencies.md` | Exact SDK, `TargetFramework` and why, every NuGet package + version, per-project SDK type and settings. |
| `03-core-backend.md` | `IAutomationBackend` full surface, `StaExecutor`, `ElementRegistry`, all DTOs, exception types. |
| `04-uia.md` | FlaUI/UIA3 usage: automation object, GetDesktop, ConditionFactory, control patterns, all-properties read, `wait_for_element`, gotchas. |
| `05-capture.md` | The three capture strategies and when each is used; the full WGC/Direct3D11 pipeline; capture gotchas. |
| `06-input-and-dpi.md` | SendInput mouse/keyboard mechanics, DPI awareness, the foreground-lock fix. |
| `07-http-server.md` | ASP.NET Core minimal API, security model, endpoint list, capture response shapes, error→status mapping. |
| `08-web-dashboard.md` | The single-file `wwwroot/index.html` dashboard, conceptually. |
| `09-mcp-server.md` | The MCP stdio host, tool attributes, image content, an example client `mcp.json`. |
| `10-governance-and-safety.md` | `GovernedBackend`, `ControlState`, `AuditLog`, `KillSwitch`, the toast. |
| `11-secure-desktop.md` | Phase 2: session/desktop model, `SecureCapture`, the Secure Helper, the Broker. |
| `12-recreation-walkthrough.md` | Ordered, step-by-step recipe to rebuild the whole thing. **The most important file.** |
| `13-roadmap.md` | The five phases: what is done and what remains. |
| `14-events-hooks-recording.md` | The event feed, process/window hooks, screen recording (GIF/AVI + retention), user-input recording, and RDP-over-fleet. |
| `15-test-plan.md` | Acceptance checklist: per-subsystem test cases + PASS criteria — how an AI knows it built Deskhand correctly. |

## Tech stack (exact)

| Layer | Choice | Exact version |
|---|---|---|
| Runtime / SDK | .NET (SDK 10.0.x present; projects target net9) | `net9.0-windows10.0.19041.0` |
| Language | C# (implicit usings, nullable enabled) | C# 13 (net9) |
| Platform | x64 only | `<Platforms>x64</Platforms>` |
| UI Automation | FlaUI.Core | `4.0.0` |
| UI Automation | FlaUI.UIA3 | `4.0.0` |
| Imaging | System.Drawing.Common | `9.0.0` |
| GPU capture (WGC) | Vortice.Direct3D11 | `3.8.3` |
| MCP server | ModelContextProtocol | `2.2.0` |
| Generic Host (MCP) | Microsoft.Extensions.Hosting | `9.0.0` |
| HTTP host | ASP.NET Core minimal API | in `Microsoft.NET.Sdk.Web` (net9) |
| Toast UI | WinForms | `<UseWindowsForms>true</UseWindowsForms>` |
| WinRT projections (WGC) | `Windows.Graphics.Capture` etc. | via the `10.0.19041.0` OS target |

> Note: the original design doc (`Deskhand.html`) names ".NET 8 (LTS)"; the actual built code targets
> **.NET 9** (`net9.0-windows10.0.19041.0`). This documentation follows the code, not the design doc.
