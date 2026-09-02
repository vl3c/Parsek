# Cooperative Async Multiplayer - Deferred Items

Items identified during design (2026-09-01 interview, adversarial reviews, code verification, player-perspective pass) that are out of scope for v1. Each has a justification and a trigger for revisiting. Design authority: `docs/dev/design-coop-async-multiplayer.md`.

---

## Deferred from the interview (product decisions)

### D1. Excise-and-stitch salvage (Case 2 upgrade)
**What:** When a non-structural interaction fails the fit predicate, drop only the docked-window recording and re-link the pre-dock segment to the structurally identical post-undock segment with a synthetic continuation link, preserving the rest of the mission.
**Why deferred:** The synthetic link is the one genuinely new structural mechanism in the salvage ladder; maintainer direction was to ship the basics first (v1 truncates Case 2 like Case 3).
**Revisit when:** M4 has shipped and truncation verdicts are observed in real play; target v1.1.
**Status:** Open.

### D2. Contract pooling / canonical contract identity
**What:** Share contract accept/complete/fail rows cross-player by fingerprinting contract instances (type + body + parameters) so "the same" contract dedups across saves.
**Why deferred:** KSP contract instances are per-save procedural GUIDs; canonical identity is a subsystem of its own (fingerprinting, slot accounting, deadline reconciliation). v1 keeps contracts local-only with rewards pooled as plain earning rows and regenerates each clone's contract queue at join.
**Revisit when:** v2 planning; or if players report the local-only model feels wrong.
**Status:** Open.

### D3. Cross-player Fly/Switch-To continuation
**What:** Taking control of a peer's spawned vessel via Fly/Switch-To.
**Why deferred:** It is not: the maintainer ruled on 2026-09-02 that in co-op any player controls any vessel as if their own. v1 implements it through a foreign-continuation route (a new local tree linked to the foreign recording), never through the tree-replacing restore path, which stays own-tree only (design 7.7, task M4.11).
**Revisit when:** -
**Status:** Closed - in scope for v1 (design v4).

### D4. Applying visit resource deltas to canonical chains
**What:** Make a spliced visit's fuel/inventory withdrawal visible on the target's canonical state.
**Why deferred:** Decided AGAINST, not merely deferred: fuel appearing from nothing beats deleting recorded state; forward-applying deltas makes a peer's visit retroactively change what you find on your own station.
**Revisit when:** Never, unless the maintainer reverses the paradox-avoidance principle.
**Status:** Closed - product decision.

### D5. Per-player timeline colors / avatars / presence
**What:** Color-code timeline entries and ghosts per player; show who is online.
**Why deferred:** A per-player color scheme fights the existing semantic color language (earning/spending/action/recording); presence needs a channel the protocol does not have. v1 appends the owner name to entry text and adds a per-player filter.
**Revisit when:** After M2 ships and attribution has been used in play.
**Status:** Open.

### D6. Revoking or garbage-collecting a departed player's contributions
**What:** Remove a member's data from every save when they leave.
**Why deferred:** Append-only exchange; contributed history is part of everyone's timeline. Leaving is social in v1.
**Revisit when:** A concrete ask; would need a retirement-style record authored by the founder.
**Status:** Open.

### D7. Automatic checkpoint scheduling
**What:** Founder's machine checkpoints on a schedule instead of on demand.
**Why deferred:** v1 checkpoints are founder-manual (Settings button); scheduling needs quiescence detection and a policy for the "all known packets applied" precondition.
**Revisit when:** Folder growth or join times become a reported problem.
**Status:** Open.

## Deferred from the design reviews

### D8. Visit-window ghost overlap suppression
**What:** During an accepted visit window, hide the target's own ghost while the visitor's merged-vessel ghost (which contains the target's parts) renders.
**Why deferred:** Windows are short; v1 accepts the visual overlap. The derived `VisitAnnotation` carries the data a later renderer pass needs.
**Revisit when:** After M4, if overlap is noticeable in play.
**Status:** Open.

### D9. Arbitration clock hardening
**What:** Replace or augment the `(exportedRealTime, playerId)` key with NTP-style offset sampling against peers' packet timestamps, or a per-tip first-writer sentinel file (single-writer per claimant).
**Why deferred:** The current key is total and convergent; only fairness under clock skew is at stake. Changing the key needs no schema change.
**Revisit when:** Players report unfair photo-finish verdicts.
**Status:** Open.

### D10. Local view preferences on foreign missions
**What:** Let a player loop, hide, or restyle a peer's mission locally without touching the owner's loop config.
**Why deferred:** Loop config is owner-authoritative under the fence; a local-preference overlay is a new state category.
**Revisit when:** A player asks to loop a peer's mission (edge case 43).
**Status:** Open.

### D11. Stand-in attribute fidelity
**What:** Carry courage/stupidity/XP in the CREW block so derived stand-ins and materialized foreign crew match exactly across machines.
**Why deferred:** v1 carries name/trait/gender/veteran; cosmetic divergence on derived stand-ins is acceptable. M5 decides.
**Revisit when:** M5 planning.
**Status:** Open.

### D12. Join with an existing save / campaign-loss recovery of unexported work
**What:** A flow that links an existing save (with local-only missions) to a campaign, so a member whose shared folder was lost can carry never-exported missions into the founder's replacement campaign.
**Why deferred:** Requires reconciling a non-checkpoint local state against a checkpoint (a merge of two timelines rather than a clone-then-import). v1 documents the loss loudly (edge case 39).
**Revisit when:** A folder loss actually happens, or v2.
**Status:** Open.

### D13. Gloops extraction and the `.gloop` format
**What:** The engine assembly split and the standalone sharing format.
**Why deferred:** Decoupled from this feature entirely: packets are Parsek-native because citizens need ledger rows, spawning, and interaction that `.gloop` strips. Remains on the roadmap for the standalone Gloops mod.
**Revisit when:** The standalone mod is scheduled.
**Status:** Open (roadmap).

### D14. Per-peer Tracking Station proto cap
**What:** Cap the number of ghost map/TS ProtoVessels populated per peer so a 3,000-recording campaign does not spend minutes populating protos at 2/tick.
**Why deferred:** Conditional on the section 12 scale measurement (task M6.5); implemented only if the 3,000-recording row fails its budget.
**Revisit when:** M6.5 measurement.
**Status:** Open - conditional.

### D15. Incremental ledger walk
**What:** Avoid the full UT=0 replay on every recalc as the merged ledger grows to tens of thousands of rows.
**Why deferred:** Measure first (section 12); the walk is event-driven, not per-frame, and the cutoff variant already runs today.
**Revisit when:** The measured walk time at 3,000 recordings exceeds its budget.
**Status:** Open - measure first.

### D18. Per-member checkpoints
**What:** Let any member write `checkpoints/<playerId>/<n>/` (single-writer preserved) so a campaign whose founder left can still bound joiner bootstrap cost.
**Why deferred:** v1 checkpoints are founder-only and manual; a departed founder means bootstrap cost grows without bound (edge case 19). Rare in v1.
**Revisit when:** A founder actually leaves an active campaign, or v1.1.
**Status:** Open.

### D17. Full `SpawnOwnershipResolver`
**What:** Replace the emergent leaf-owns-spawn rule with one pure per-vessel resolver consumed by every spawn host.
**Why deferred:** The 13-step spawn gate is pinned by ~112 test call sites asserting reason strings plus a cross-predicate drift guard; several host-specific conditions are scene capabilities, not ownership. The fold's needs are met by a structured-result overload, coded early rejects, a stamp clear/re-point API, and routing the leaf spawner through the gate (pre-refactor C1-C3).
**Revisit when:** After M4 ships, if the ladder proves inadequate.
**Status:** Open - rejected for v1.

### D16. Schema-generation bump to 5
**What:** Bump `CurrentRecordingSchemaGeneration` at the exchange layer's birth instead of shipping additive null-defaulted fields on generation 4.
**Why deferred:** It is not: decided 2026-09-02 to bump to 5 (not public yet, no backward-compatibility need); fixture saves are re-stamped in task M2.1.
**Revisit when:** -
**Status:** Closed - decided (bump to 5, design section 11).
