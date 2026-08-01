# Security policy

Ashfall is a **server-authoritative** multiplayer game, so "security" here covers
both the usual software vulnerabilities and **anything that lets a client cheat
the authority** — moving through terrain, duplicating items, forging another
player's actions, or reading state it shouldn't (interest management is an
invariant, not a nicety).

## Reporting a vulnerability

**Please do not open a public issue for a security problem.** A public report
hands the exploit to everyone before it's fixed.

Instead, report it privately:

- **Email:** hc@bytetide.io
- Or use GitHub's **[private vulnerability reporting](https://docs.github.com/en/code-security/security-advisories/guidance-on-reporting-and-writing-information-about-vulnerabilities/privately-reporting-a-security-vulnerability)**
  ("Report a vulnerability" under the repository's **Security** tab), if enabled.

Include as much as you can:

- What the issue is and the impact you think it has.
- Steps to reproduce, or a proof-of-concept.
- Which component is affected — client, world-server, gateway, or `sim-core`.
- Any suggested fix, if you have one.

## What to expect

- We'll acknowledge your report as soon as we reasonably can.
- We'll keep you updated as we investigate and work on a fix.
- We'll credit you when the fix ships, unless you'd prefer to stay anonymous.

## Scope

Things especially worth reporting:

- **Authority bypass** — a client causing a state change the server should have
  rejected (invariant #1): teleporting, speed/flight past the movement bounds,
  item duplication, placing/harvesting out of reach.
- **Cross-player forgery** — acting as, or reading private state of, another
  player.
- **Interest-management leaks** — receiving entities or chunks that aren't near
  the player (invariant #6).
- **Gateway / persistence** — anything touching account or character data
  (injection, auth bypass, data exposure).
- Standard vulnerabilities in any dependency or service.

Because no world has shipped yet and there's no production deployment to attack,
much of this is about **hardening the design** — a report that shows "a client
could do X the server never checks" is exactly as valuable as a live exploit.

Thanks for helping keep Ashfall — and its players — safe.
