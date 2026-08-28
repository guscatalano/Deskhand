# 19 — Deskhand vs. computer-use agent frameworks (c/ua and the category)

Where Deskhand sits relative to the "give an AI a computer" frameworks — chiefly **c/ua**
(trycua: the `Computer` + `Agent` SDKs, Lume/Lumier virtualization, cloud containers), but the
contrasts generalize to any screenshot-driven computer-use stack. This is orientation for a
developer or agent deciding *which shape of tool a task wants*, not a feature audit — c/ua moves
fast, so treat its specifics as directional.

## One-line framing

- **c/ua** is a **sandbox-first, vision-first agent platform**: boot a throwaway VM/container, let a
  model look at the screen and click coordinates, and make it safe by *isolation*.
- **Deskhand** is a **real-machine, accessibility-first capability provider**: drive the actual
  Windows box (and a fleet of real PCs, and RDP hosts) through the UI Automation tree and structured
  input, and make it safe by *governance* (kill switch, audit, consent).

They solve adjacent problems from opposite ends. Both happen to speak **MCP**, so either can be an
agent's tool source — and Deskhand could even be the tool layer under a c/ua-style agent loop.

## The axes that matter

| Axis | **Deskhand** | **c/ua** |
|---|---|---|
| **Control paradigm** | **Semantic** — UIA tree, `element-from-point`, control patterns (`Invoke`, `SetValue`, `wait_for_element`). Acts on *named elements*, not just pixels. | **Vision-first** — screenshot → model → click `(x,y)` / type. A coordinate-based computer-use loop. |
| **Target** | The **real** local Windows machine + a **fleet** of real PCs + **RDP** into existing hosts. | **Sandboxed VMs / containers** you provision (macOS/Linux via Lume on Apple Silicon, Windows, cloud). |
| **Safety model** | **Governance** — kill switch, capability gates, JSONL audit log, capture toast, persistent consent banner. Safety *around* a machine you can't discard. | **Isolation** — the agent runs in a disposable VM; blast radius is the sandbox. |
| **Who drives** | A **tool/capability provider** (MCP + HTTP). The agent (e.g. Claude) is *external*. | Ships the **Agent SDK too** — the agent loop is part of the product; it both exposes and drives. |
| **Platform** | **Windows-only**, native FlaUI/UIA3 on .NET 9. | **Cross-platform**, Apple-Silicon-centric virtualization with cloud fallback. |
| **Element addressing** | Structured tree + element-from-point; act without vision; **deep-link to a specific element**. | Primarily "look at the screenshot and click the pixel." |
| **Distribution** | WebSocket reverse-RPC **fleet** + **zero-install RDP** into machines you already have. | Provision/boot VMs, or use hosted containers. |
| **Observation** | First-class: event feed, process/window hooks, screen recording, **user-input recording with consent**, per-agent over the fleet. | Focused on the agent *acting*; observation is the screenshot stream it drives from. |
| **MCP** | Yes — exposes ~47 local + ~29 fleet tools. | Yes — exposes an MCP server too. |

## The underlying bets

**c/ua's bet:** the model can look at a screen and act like a person, so keep the control layer thin
and general (any GUI, any OS in a VM, no accessibility dependency) and make it *safe by making the
computer disposable*. The costs are vision's brittleness (pixel drift, occlusion, no stable element
identity) and that you're driving a **VM, not the real box**.

**Deskhand's bet:** the accessibility tree is richer and more reliable than pixels, and the target
worth automating is the **machine that already exists** — your desktop, a lab of real PCs, an RDP
host you can't throw away. So it invests in **structured element access** (so an agent can act and
deep-link without vision) and in **governance** (audit / consent / kill switch) *instead of*
isolation, because there's nothing disposable to fall back on.

Neither bet is strictly better; they're tuned to different jobs.

## Choosing

- *"Reliably automate or observe Windows apps on machines that already exist, with an audit trail and
  a human-visible kill switch"* → **Deskhand's** model fits.
- *"Give an agent a safe, throwaway computer to freely poke at, cross-platform, and let the model
  figure out the GUI from pixels"* → **c/ua's** model fits.
- *"I want the semantic reliability of the accessibility tree but the free-driving agent loop of a
  computer-use framework"* → point a c/ua-style (or any MCP) agent at **Deskhand's MCP tools**; you
  get element-addressed actions inside an external agent loop, on the real machine, under Deskhand's
  governance.

## What Deskhand deliberately does *not* do (and why)

- **No bundled agent loop.** Deskhand stops at the capability seam (`IAutomationBackend` + services)
  and exposes MCP/HTTP. The model/agent is someone else's concern. This keeps governance enforced at
  one seam regardless of which agent drives.
- **No VM provisioning / cross-platform.** It commits to native Windows so it can use UIA and reach
  real fleet/RDP machines, rather than abstracting an OS it boots.
- **No pixel-only fallback as the primary path.** Capture exists (GDI / PrintWindow / WGC) and an
  agent *can* work from screenshots, but the intended path is the semantic tree; capture is for
  verification, recording, and the cases UIA can't reach (e.g. RDP, which is capture+input only).
