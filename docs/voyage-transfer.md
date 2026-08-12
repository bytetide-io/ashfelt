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
  This is enforced by `character.owner_world_id` on every join, not only a
  voyage arrival: a normal (non-voyage) Hello claims ownership through
  `POST /characters/{id}/claim` before loading, exactly as a voyage arrival
  claims it through `/voyage/claim`. Without that normal-join claim, the same
  character could be loaded live on two world-servers at once (two client
  instances, or a reconnect racing a still-open session) — see
  `infra/migrations/005_ownership_claim.sql`.
- The client is never trusted with character state during handoff — it carries
  a ticket, not an inventory.
- If B refuses or times out, the gateway returns the character to A. Failure
  must be recoverable without operator intervention.
- A claim held more than 300s without a matching release is stale and may be
  reclaimed — the backstop for a world-server that crashes without ever
  disconnecting its players. A clean disconnect releases immediately instead
  of waiting out that window.

## Deferred

- Timed crossings with a traversable open-water region.
- PvP during voyage. Not until instant transfer is solid and shipped.
