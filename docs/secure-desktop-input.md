# Secure-desktop input (UAC / lock / logon)

Deskhand can **view** the secure desktop and has an **opt-in** path that attempts to **drive** it.
This document records what works, what doesn't, and the practical way to automate a UAC approval.

## Background: the two desktops

Windows input goes to whichever desktop currently "owns input":

- **`Winsta0\Default`** — the normal user desktop, where apps live.
- **`Winsta0\Winlogon`** — the **secure desktop**: the dimmed screen that hosts the UAC consent
  prompt (when *prompt on secure desktop* is on), the lock screen, and the logon UI. It is isolated
  from the Default desktop by design, so a normal process can neither see nor drive it.

A user-session process (even elevated) cannot touch the secure desktop. Only a process that is
**SYSTEM in the console session** can attach to it, and even then **input** is heavily restricted.

## What works

### Viewing the secure desktop — supported
When interactive mode follows a desktop switch, Deskhand launches a least-privilege **SYSTEM helper**
(`deskhand-secure.exe`, started on the Winlogon desktop by `deskhand-broker.exe`, which borrows
winlogon's token) that captures the secure desktop over a local pipe. You **see** the UAC prompt /
lock screen live in the dashboard. Capture is proven end-to-end.

### Driving the secure desktop — opt-in, best-effort
Two opt-in mechanisms exist (gated behind `DESKHAND_ENABLE_SECURE_DESKTOP_INPUT=1` or an explicit
`acknowledgeRisk` in the request, armed, and audited):

1. **SYSTEM helper self-grants a uiAccess token.** The helper runs on Winlogon, attaches to the input
   desktop, and sets `TokenUIAccess=1` on its own token (allowed because SYSTEM holds
   `SeTcbPrivilege`). No code-signing required.
2. **Signed uiAccess helper** (`deskhand-uia.exe`). `POST /secure/provision-input` provisions it
   per-machine: self-sign the helper, trust that cert in this machine's Root/TrustedPublisher stores,
   then **destroy the signing key** (the embedded signature stays valid, but no reusable
   "sign-anything-trusted" key remains). `POST /secure/deprovision-input` reverses it.

## What does NOT work: driving ANY secure desktop (lock / logon / UAC) — tested, confirmed

Driving **any** secure desktop (`consent.exe` for UAC, **and** the lock / logon / credential screen)
by synthetic input is **blocked by Windows**, and no mechanism we deploy on demand defeats it. This was
initially proven for the UAC consent prompt, then — the previously "untested" case — for the **lock/logon
screen** too, on sandbox 903 (v0.2.103):

| Approach | On Winlogon | uiAccess token | `SendInput` result |
|---|---|---|---|
| SYSTEM helper | yes (attached) | no | ACCESS_DENIED (Win32 5) |
| Signed uiAccess helper (launched on Default) | no (`OpenInputDesktop(Winlogon)` denied) | yes | ACCESS_DENIED |
| SYSTEM helper + **self-granted** `TokenUIAccess=1`, attached to Winlogon | yes | yes | ACCESS_DENIED |
| **SYSTEM hybrid logon agent, `TokenUIAccess=1` set BEFORE process creation (born-uiAccess), attached to Winlogon** → **lock/logon screen** | yes | yes (verified `UIAccess=1` on the live process) | **ACCESS_DENIED (Win32 5)** |

The last row is the decisive one: a process that is SYSTEM, **born** with uiAccess (not the ineffective
post-start self-grant — win32k latches UIAccess at process creation), and attached to the Winlogon
desktop — i.e. **every ingredient present and verified** — still gets `SendInput` → `Win32 5` on the
Windows lock/logon screen. So the earlier assumption that LogonUI (itself SYSTEM) would accept
same-integrity injection is **false**: the secure desktop rejects synthetic input regardless of token,
integrity, or desktop attachment. It is a deliberate Windows security boundary (the lock/logon/consent
surfaces are trustworthy precisely because only the physical input path reaches them). The **only**
remaining avenue is registering as an accessibility tool that Windows itself launches on the secure
desktop via AtBroker — untested, a per-machine registration, and Microsoft may still block credential/
consent entry.

**Capture, by contrast, works on all of these** (the SYSTEM agent returns the live lock/logon/UAC image).
And raising the secure desktop via **SendSAS** (Ctrl+Alt+Del, `POST /secure/attention`) works from the
SYSTEM host. It is only *driving* input on the secure desktop that Windows refuses.

## The practical way to automate UAC: move it off the secure desktop

On a machine you administer, the supported way to let Deskhand drive a UAC prompt is to make it a
normal window:

- `POST /uac/config {"promptOnSecureDesktop": false}` — the consent prompt renders on the **Default**
  desktop, where Deskhand drives it like any other window (no reboot). The dashboard's secure-desktop
  banner exposes this as a one-click **"Make UAC drivable"** button.
- `POST /uac/config {"autoApprove": true}` — admins elevate without any prompt
  (`ConsentPromptBehaviorAdmin=0`).

Both are deliberate reductions of that machine's UAC protection, which is why they are explicit policy
choices rather than something Deskhand does silently.

## Security notes

- Everything above is **opt-in, armed-gated, and audited**. Nothing is enabled by default.
- The signed-uiAccess path installs a per-machine trusted root; the signing key is destroyed
  immediately after signing so it cannot be reused to mint other trusted binaries.
- The SYSTEM secure helper **idle-exits** (default 90s, `DESKHAND_SECURE_IDLE_SEC`) so it does not
  linger with a lock on `C:\Deskhand`.
- Use only on machines you own/administer, under an authorized automation context.
