# Voyage transfer

Moving a character between world-servers. **v1 is instant** — the timed
open-water crossing is deliberately deferred (see non-goals).

## Flow (instant, Phase 3)

1. Player boards a boat / reaches the world edge.
2. Client sends `RequestVoyage(targetWorldId)` to the gateway.
3. World-server A is told to release the character: it flushes any pending
   diffs, removes the entity, and returns the authoritative character state.
4. Gateway persists that state against the account and marks the character as
   in-transit (no world-server owns it).
5. Gateway hands the state to world-server B, which admits the character and
   returns a connection ticket.
6. Gateway replies to the client with B's address plus the ticket.
7. Client disconnects from A, plays the voyage transition, connects to B.

## Rules

- A character is owned by **exactly one** world-server at a time. The
  in-transit state exists so a crash mid-transfer can never duplicate items.
  This is enforced at **every** join, not only voyage arrivals: a plain
  (re)connect calls `POST /characters/{id}/claim` before loading, and is
  rejected if another world already owns the character live. Without this, a
  direct join never checked ownership at all — see `docs/nightly/LOG.md`
  2026-08-08.
- The client is never trusted with character state during handoff — it carries
  a ticket, not an inventory.
- If B refuses or times out, the gateway returns the character to A. Failure
  must be recoverable without operator intervention.

## Deferred

- Timed crossings with a traversable open-water region.
- PvP during voyage. Not until instant transfer is solid and shipped.
