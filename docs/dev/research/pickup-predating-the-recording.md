# Accepting a pickup witnessed by the PREVIOUS recording

Closes ROUTE-ORIGIN-PROOF-PICKUP-PREDATING-THE-RECORDING. The player case: dock the
tanker to the base, transfer fuel, quicksave and quit; tomorrow load, undock, fly the
run. Tomorrow's recording starts already docked, so it never witnesses the inflow, the
bind stamps `pickup=Carried pickupValidated=0`, and the run cannot become a route.
"Undocked with cargo aboard" is NOT the fix - a full tanker that only delivered would
count, which is the wrong-debit the design forbids. The evidence has to come from the
window the PREVIOUS recording of the same vessel already holds.

## (a) Where the lookup runs

At the undock bind, on the `ParsekFlight` side. `CreateSplitBranch`
(`ParsekFlight.cs:5662`) already holds `activeTree` and `parentRec` and already passes
`parentRec.RouteConnectionWindows` into the binder; reaching a SIBLING recording is one
walk over `activeTree.Recordings` plus `activeTree.BranchPoints`, no plumbing. The
DECISION stays pure (`RouteProofCapture.ClassifyPredecessorPickup`), and so does the
traversal (`TryResolveOriginProofPredecessor`, a pure static over `RecordingTree`), so
both are drivable headlessly.

## (b) How the predecessor is identified

Three edges, tried in order, all inside `activeTree` and all gated by
`VesselLaunchIdentity.RecordingsShareLaunch` (never a bare `persistentId` match):

1. the recording's `ParentBranchPointId` -> that branch point's `ParentRecordingIds`;
2. `ParentRecordingId` (the EVA / chain linkage field);
3. the chain predecessor: same `ChainId` + `ChainBranch`, the largest `ChainIndex`
   strictly below this one.

Edge 1 carries the operator's case. Measured, not assumed: on the ordinary quit / reload
the previous session's recording is RE-ADOPTED and appended to IN PLACE
(`ParsekFlight.TryTakeCommittedTreeForSpawnedVesselRestore:14946`,
`ResumeCommittedActiveRecording:4257`) - same tree, same recording, so window and undock
land on ONE recording and the ordinary window path already works. The start-docked family
arises on the OTHER re-entry, the stock Fly / Switch-To route (`TryConsumeStockActionIntent`
-> `SwitchSegmentBuilder.CreateSwitchContinuationSegment`), which builds a NEW recording in
the SAME tree linked only by a `BranchPointType.VesselSwitchContinuation` branch point.

THE WALK STOPS AT THE FIRST RESOLVED PREDECESSOR - it does not keep walking edges looking
for one that happens to carry a window. A resolved predecessor with no windows (a BACKGROUND
continuation, admitted on the same terms because nothing here reads a recorder state, only
the persisted list) ends the search at `NoWindows`: fail-closed, one edge, no scan. The tree
ROOT resolves nothing and fails closed with `no-predecessor`. The search never leaves
`activeTree`: the standalone-continuation fallback
(`ParsekFlight.StartStandaloneContinuationSegment:9644`) mints a NEW tree precisely because
the launch guid gate FAILED, and a predecessor we cannot prove is the same launch must not
supply evidence.

## (c) Which window counts

The LATEST window on the predecessor by `DockUT`, and only that one. Never a scan for "any
window that gained" - cherry-picking across windows is exactly the wrong-debit hazard, and
the last thing that happened at a seam is what the transport left with. It counts only when
all of:

* `WindowNamesHalfAsTransport(window, transportHalfPids, originHalfPids)` - the same
  predicate the in-recording bind uses: the window's transport pid set overlaps the half now
  resolved as the transport AND its endpoint pid set overlaps the half now being named the
  origin. A window with a different partner is not evidence.
* the LAUNCH-UNIQUE key agrees when both sides know it: `window.EndpointRootPartUId` (new,
  additive - the endpoint vessel's ROOT PART `flightID`, captured at the dock from the
  pre-couple partner snapshot) equals `originHalf.RootPartUId`. `flightID` is assigned per
  launch and is not craft-baked, so this is the one identity a `persistentId` set cannot
  supply. Unknown on either side degrades to the pid-set overlap above, as every other
  identity site here does with an unknown guid.
* `DockUT <= recording start UT`, and for a COMPLETE window also `UndockUT <= start`. A
  window that closes after this recording started is this recording's own business.

Two admissible shapes, and the operator's case is the second:

* COMPLETE window (`UndockUT` set, at or before the start): the rise is measured inside the
  window, `DockTransportResources/Inventory` -> `UndockTransportResources/Inventory`.
* OPEN window (still docked when the predecessor ended - which is WHY this recording starts
  docked): the rise is measured `DockTransportResources/Inventory` -> this recording's own
  transport-half START manifests. The span is entirely docked, so it is the same causal flow
  read across two records. This shape additionally requires the window's transport pid set to
  EQUAL the seam-derived transport half's, because the two manifests come from different
  records and a drifted part set would corrupt the delta; the complete shape needs no such
  check, both its manifests being the window's own.

A RE-DOCK at the same origin therefore reads `NoRise` and is refused, and that is the
intended reading rather than a gap: a recording carries at most ONE window - the single
writer at `ParsekFlight.cs:6620` ASSIGNS a one-element list at the dock merge, it does not
append - so the second dock's window lives on a later recording and the earlier rise sits
two edges back, out of reach of a walk that goes one edge and stops.

## (d) What "rise" means

Exactly what it means everywhere else: `ClassifyOriginPickup` on the two manifests, reused
verbatim. Resources OR inventory, the always-ignored environmental set excluded, a null
baseline unevaluable rather than a fabricated zero. Only its `Gain` admits, and it becomes
`OriginPickupKind.GainFromPredecessorWindow`, which validates exactly like `Gain`.

## (e) Fail-closed cases, and where to read them

Two DIFFERENT log surfaces, spelled differently on purpose:

* the WALK's outcome is a Verbose `RouteOriginProof predecessor walk:` line, whose
  kebab-case `reason=` is one of `no-tree`, `no-predecessor`, `predecessor-not-same-launch`,
  `parent-recording`, `chain-predecessor`. This is the only place a refusal to RESOLVE is
  named.
* the RULE's outcome is the bind line's `predecessorPickup=`, a Pascal-case
  `PredecessorPickupOutcome` member: `NoWindows`, `NoPartnerMatch`, `PartnerRootMismatch`,
  `WindowAfterRecordingStart`, `TransportPartSetDrift`, `Unmeasurable`, `NoRise`,
  `GainFromPredecessorWindow`. Both no-predecessor cases (unresolved, and resolved-but-
  window-less) collapse to `NoWindows` here - the walk line is what separates them. It reads
  `not-consulted` when the predecessor was never asked.

Every refusal leaves the pickup unvalidated and the proof keeps its `Carried` stamp exactly
as before, so this pass can cost a route and can never cost a wrong debit. The predecessor is
consulted ONLY when the dock was NOT witnessed in this recording and the in-recording pickup
is not already a `Gain`: a WITNESSED dock that moved nothing onto the transport is positive
delivery evidence and must stay refused.

## (f) Rejected alternatives

* **A "loaded" flag carried on the vessel across recordings.** It is the `Carried` reading
  with a longer memory: a flag says the transport HAS cargo, not that a witnessed event put
  it there, so a full tanker that only ever delivered would set it. Same wrong debit, now
  persisted.
* **A cargo snapshot at the chain boundary.** It measures the boundary, not the seam: it
  cannot say WHICH vessel supplied the rise, and the whole content of an origin is the
  partner identity. It also needs a new persisted surface on every recording, where the
  window already exists and already names both halves.
* **Completing the predecessor's window at the recorder stop.** One line, and wrong:
  `IsComplete` means "the pair separated" to `RouteAnalysisEngine`, so a stop-closed window
  would read as a finished dock-transfer-undock cycle and build routes from a vessel that is
  still docked.
* **Letting the predecessor window also choose the transport HALF.** Out of scope: this
  package changes pickup VALIDATION only. A wrong half selection makes the partner match fail
  and the evidence is refused, which is the fail-closed direction.
