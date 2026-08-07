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
- Within a single world-server, a character is owned by **exactly one live
  connection**. A `Hello` presenting a `CharacterId` already attached to
  another connected `Player` evicts the older connection — flushing its
  state to the gateway first — before the new one loads. Without this, a
  quick reconnect (or two devices sharing a UUID) would run two independent
  in-memory inventories against one gateway character, and whichever session
  disconnected last would silently overwrite the other's progress.
- The client is never trusted with character state during handoff — it carries
  a ticket, not an inventory.
- If B refuses or times out, the gateway returns the character to A. Failure
  must be recoverable without operator intervention.

## Deferred

- Timed crossings with a traversable open-water region.
- PvP during voyage. Not until instant transfer is solid and shipped.
