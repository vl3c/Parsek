# Refactor-5 Slice 4 Proposal — Cross-File Owner Proposals (Pass 2)

**Date:** 2026-06-14. **Status:** Proposal (not implemented). Discuss before
landing — these move code across files.
**Roadmap:** `docs/dev/plans/refactor-5-slices.md` (shared rules + validation gate).

Pass 2 = a proven-identical block moved to a new owner behind compatibility
wrappers, with the original call sites unchanged. Each item below is independent;
order them by ascending risk and land each as its own proposal + PR + clean-context
review. **Every dedup requires a semantic-identity proof of the moved blocks
first** (checklist item 10).

## 4.1 `RouteNodeCodec` — shared ConfigNode codec (RouteCodec ↔ RouteProofCodec)

**Highest dedup value, highest risk.** These sub-codecs are field-for-field
identical across the two files (the source comments already document the deliberate
shape-match):

- `SerializeEndpoint` / `DeserializeEndpoint` (RouteCodec @479/@491) ≡
  `SerializeRouteEndpoint` / `DeserializeRouteEndpoint` (RouteProofCodec @701/@714)
  — same keys, same sparse pid-0 rule, same `"R"`/InvariantCulture.
- The inventory-item codec and the `ResourceAmount` manifest codec.
- `ParseConnectionKind` (RouteCodec @775 ≡ RouteProofCodec @494).

**Do NOT** fold the resource-*manifest* codec: RouteCodec writes a **flat**
`name=amount`, RouteProofCodec writes **nested** `RESOURCE{name,amount,maxAmount}`
nodes — different on-disk shapes.

Proposal: a `RouteNodeCodec` owner with `Serialize/DeserializeEndpoint`,
`Serialize/DeserializeInventoryItem`, `Serialize/DeserializeResourceAmount`, and
`ParseConnectionKind`. Both files keep their existing method names as wrappers.

**Risk:** byte/field-order-critical across **two independent frozen on-disk
surfaces** (the ROUTE node and the per-Recording route-proof node; gen-4 schema, no
migrations). The round-trip serialization suites are the safety net and MUST stay
green to the byte. Pure (ConfigNode only).
**Validate:** `--filter "FullyQualifiedName~RouteCodec"` AND
`--filter "FullyQualifiedName~RouteProofSerialization"` (both byte-roundtrip
guards), then the full non-injection gate. Land alone, no other change in the PR.

## 4.2 Anchor world-frame / context-factory owner (Production ↔ Recorded)

~150 duplicated lines across `Rendering/ProductionAnchorWorldFrameResolver.cs` and
`RecordedRelativeAnchorPoseResolver.cs`: `TryFindFocusTree`,
`ResolveAbsoluteWorldPosition`, `ResolveBodyWorldRotation`,
`TryResolveOrbitalAnchorPose`, `ResolveBody`, and the context-build.

**Not a mechanical dedup — the bodies diverge:**

- `ResolveBody` uses `b.bodyName` (Production) vs `b.name` (Recorded), and catches
  **different exception types** (`catch {}` vs `catch (TypeInitializationException)`).
- The two `TryBuildContext` overloads wire **different** live-anchor delegates
  (`tryResolveLiveAnchorTransform` vs `tryResolveLiveLaunchMatchedAnchorPose`).
- The Recorded resolver has an extra `[ERS-exempt]` `TryFindRecordingById` branch.

So this needs a **deliberate decision on the `ResolveBody` divergence** before any
move (the two are not behavior-equivalent today; a naive merge changes body-lookup
semantics). Propose a shared `AnchorResolverContextFactory` / `AnchorWorldFrameHelpers`
that takes the delegate(s) + the body-lookup strategy as parameters, so each caller
keeps its current behavior exactly.

**Risk:** `runtime` (FlightGlobals / RecordingStore-coupled). The `[ERS-exempt]`
scope must be preserved (grep gate `scripts/grep-audit-ers-els.ps1`).
**Validate:** `--filter "FullyQualifiedName~ProductionAnchorWorldFrameResolverTests|FullyQualifiedName~RecordedRelativeAnchorPoseResolverTests|FullyQualifiedName~RelativeAnchorResolverTests"`
+ in-game map/TS anchor validation.

## 4.3 `RouteResourceTankIterator` — live tank-walk owner

The loaded/unloaded `Part.Resources` / `ProtoPartResourceSnapshot` tank-walk with
the `ShouldDeliverToResource` flow gate is reimplemented **3×**:

- `Logistics/LiveDeliveryWriters` (reads stored + capacity, then writes).
- `Logistics/LiveOriginDebitWriters` (reads stored, then writes the opposite sign).
- `Logistics/LiveDeliveryCapacityProbe` (reads free capacity / first empty slot).

Propose a shared iterator that enumerates matching tanks (loaded + unloaded) and
invokes a per-tank callback; each consumer keeps its **distinct accumulation /
mutation** (delivery vs debit vs probe). Only the enumeration + flow-gate move;
mutation stays in each writer.

**Risk:** `runtime` — live resource mutation, no headless coverage of the live
branch. Sequence after the Slice 1/2 pure work.
**Validate:** `--filter "FullyQualifiedName~LiveDeliveryWritersTests|FullyQualifiedName~LiveOriginDebitWritersTests|FullyQualifiedName~CapacityProbeTests"`
+ an in-game route delivery/debit run (load + unload a destination/origin vessel).

## 4.4 (Optional) `RenderTraceFormat` — DOC-DEFERRED, do not start yet

The byte-identical formatter set (`FormatVector3d`/`FormatVector3`/`FormatQuaternion`/
`FormatDouble`/`Token`/`Bool`/`ShortId`) is duplicated across `GhostRenderTrace`,
`MapRenderTrace`, and `LedgerTrace`. CLAUDE.md **explicitly defers** this shared
owner and forbids touching `GhostRenderTrace.cs`. Recorded here only so it isn't
rediscovered as "new"; not actionable until that doc-level deferral is lifted.
