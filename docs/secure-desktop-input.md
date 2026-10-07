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

These **may** work for the lock/logon surfaces (the kiosk / auto-logon case — untested here because
testing it risks locking the operator out), which historically accept SYSTEM/uiAccess input.

## What does NOT work: the UAC consent prompt

Driving the **UAC consent prompt** (`consent.exe`) by synthetic input is **blocked by Windows** and
no mechanism we deploy on demand defeats it. Tested exhaustively on a live prompt:

| Approach | On Winlogon | uiAccess token | `SendInput` result |
|---|---|---|---|
| SYSTEM helper | yes (attached) | no | ACCESS_DENIED (Win32 5) |
| Signed uiAccess helper (launched on Default) | no (`OpenInputDesktop(Winlogon)` denied) | yes | ACCESS_DENIED |
| SYSTEM helper + self-granted `TokenUIAccess=1`, attached to Winlogon | yes | yes | ACCESS_DENIED |

Even with **all three ingredients present and verified**, the consent UI refuses the injection. This
is a deliberate UAC security boundary: only genuine human input, or an accessibility tool that Windows
itself launches on the secure desktop via AtBroker, reaches the consent prompt. (Registering Deskhand
as such an AT is the only untested avenue, and Microsoft may still block the consent "Yes".)

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
