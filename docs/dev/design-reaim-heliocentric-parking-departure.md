# Design: Engage re-aim on a heliocentric-parking (two-burn) interplanetary departure

*Status: SHIPPED on branch `reaim-heliocentric-parking-departure` (classifier fix pinned by xUnit;
render + P4 acceptance pending in-game). Designed via a multi-agent workflow (4-reader understand
pass, 3 independent approaches each adversarially refuted, synthesis) and the two scope forks below
were maintainer-decided 2026-06-15.*

## The problem

Save `s15`, looped mission "Kerbal X #2" (tree `ced78481`, transfer member `aa48920e`): a valid
looped Kerbin->Duna landing renders broken on the map (launch polyline draws, then no transfer
continuation, the icon teleports, all lines vanish) and the Missions tab reads "not aligned". One
root cause: re-aim DECLINES the mission (`ReaimDiag ... transferMemberSegs=0`), so it replays
faithfully at an arbitrary loop phase (the transfer aims where Duna was, the arrival ghost renders at
live Duna, the lines retire). The synodic-window readout already exists for re-aim-ENGAGED missions
(`MissionsWindowUI` IsReaim path), so making re-aim engage fixes BOTH facets at once.

The transfer member records a HELIOCENTRIC PARKING orbit: the vessel escaped Kerbin into a
near-circular solar orbit co-orbital with Kerbin (`sma=1.407e10` vs Kerbin's `1.36e10`, ecc 0.033),
coasted there ~1.4 revolutions (~152 days), then burned for Duna (`sma=1.791e10`). In
`ReaimClassifier.Classify` the transfer-run walk-back breaks at the 21.4% SMA step (>
`DefaultAStepRelThreshold = 0.05`), leaving the transfer run = the last Sun coast only, whose
predecessor is the Sun parking segment, so it hits the explicit decline *"transfer departs from a
heliocentric parking orbit or mid-course correction (deferred); staying faithful"*. The design
deferred this ("Lambert assumes r1 = launch body"); Duna One (a direct single-pass transfer) does not
hit it.

(Not a duplication bug: the `.prec.txt` readable mirror shows each orbit segment twice - flat list +
TrackSection checkpoints - but the binary load is 14, per KSP.log `postOrbitSegments=14`. Not a
#1149 / map-render-merge regression. Not P4: `reaim-dest-loiter-retimer` only affects re-aim-ENGAGED
arrival hold and is inert here.)

## The decision: loiter-reuse (Approach B + admissibility graft)

Three approaches were scored adversarially:

- **A (minimal: source `r1` from the recorded coast state) - REJECTED (22).** Self-contradictory: the
  synodic-window machinery is congruent only for the launch-body/target pair, so feeding a stale
  inertial `r1` desyncs the departure from `D_k` and reproduces the exact "aims where Duna was"
  failure it set out to fix.
- **B (loiter-reuse: classifier-verdict flip, keep `r1 = launchBody`) - CHOSEN (38).**
- **C (general free heliocentric departure state) - SHELVED (38).** More machinery (rewires five
  files), introduces a real park->transfer seam discontinuity (it window-rotates `r1` while rendering
  the park verbatim), and its general `r1`-override has a fail-closed hole with no admissibility
  filter. Its extra generality (true MCC, multi-burn, wide-eccentric park) is unvalidated by any
  recorded fixture; kept as a documented future cut, not built.

**Key fact verified against the render path (refutes the adversaries' main objection):** the verbatim
heliocentric park leg is NOT an "inertial-fixed miss". A re-aim ghost renders only from
`OrbitSegments` (`ReaimedTrajectory`), reconstructed into a live `Orbit(..., Sun)`. The Sun does not
move, so the Sun-bodied park replays on its true recorded inertial track every loop - identically to
how the recorded escape and capture legs already replay verbatim in the shipped direct case. The park
does not aim at Duna and is not supposed to (the player had not yet burned); only the transfer leg
targets Duna, and only the transfer leg is re-aimed.

**The graft (makes the contract genuinely fail-closed):** B keeps `r1 = launchBody`, which is a valid
approximation only when the burn point sits near the launch body. The shipped downstream proximity
gate validates only the ARRIVAL end, so it cannot catch a wrong departure. The new predicate adds a
classifier-side admissibility check: engage only when the park is near-circular AND co-orbital with
the launch body.

## Mechanism (as built)

The per-window `r1` / `r2` / `tof` / synodic-window solve is UNCHANGED from the shipped direct case.
The only behaviour change is one conjunct on the "transfer departs from a heliocentric parking orbit"
decline in `ReaimClassifier.Classify`:

```
bool sunPredecessor = transferStartIdx - 1 >= 0
    && segs[transferStartIdx - 1].bodyName == commonAncestor;
bool parkingDeparture = sunPredecessor
    && IsHeliocentricParkingDeparture(segs, transferStartIdx, launchBody, commonAncestor, bodyInfo);
if (sunPredecessor && !parkingDeparture)
    return Unsupported("transfer departs from a heliocentric parking orbit or mid-course correction ...");
```

`IsHeliocentricParkingDeparture` (pure, fail-closed):

1. **Park-run identity** - reuses `ReaimLoiterCompressor.DetectRuns` (the SAME detector `ComputeCuts`
   uses), finds the common-ancestor loiter run whose end coincides with the transfer's predecessor.
   If no >=1-rev solar loiter run ends there, the predecessor is a sub-period MCC arc -> false ->
   declined.
2. **Empty-cut scope** - engage only when EVERY common-ancestor (solar) run keeps `DefaultKeepRevs`
   (1), so no heliocentric loiter cut fires. The scan is over ALL solar runs, not just the
   predecessor: `ComputeCuts` runs downstream on the whole member and would excise whole periods from
   a multi-rev solar park ANYWHERE before the transfer, firing the `cutBeforeDeparture` composition
   that is unvalidated on a heliocentric run (deferred). A launch-body loiter cut is the existing
   validated L2 trim and is NOT gated. Engaging therefore adds no park-driven anchor shift on top of
   the re-aim schedule's pad-aligned anchor. (This is distinct from the direct-transfer
   byte-identical claim below: engaging re-aim DOES move the anchor off the faithful one - that is the
   whole point - it just adds no EXTRA park cut.)
3. **Admissibility gate** - near-circular at BOTH the run anchor AND the burn point (`ecc <= 0.1`;
   `DetectRuns` merges on sma only, so ecc can drift within a same-sma run), AND co-orbital with the
   launch body at the burn point (the last park segment's sma within 10% of the launch body's
   heliocentric sma). The launch body's heliocentric sma is derived from `IBodyInfo.OrbitPeriod` +
   `GravParameter(commonAncestor)` via Kepler's third law (`a = cbrt(mu * (T / 2*pi)^2)`) - pure, no
   Unity. Any degenerate / NaN / unknown-mu input -> false. KNOWN LIMITATION: sma + ecc bound the
   departure ORBIT, not the vessel's true-anomaly PHASE on it (see the risk register); the gate fails
   closed, so a pathological out-of-phase park reproduces the prior faithful render, never corruption.

When it engages, `transferStart` (and therefore `RecordedDepartureUT` and `tof`) is unchanged - the
tof is the trans-target burn -> arrival, EXCLUDING the park. The park is left outside the
`ReplaceHeliocentricLeg` window and re-timed (here: not cut) by the existing loiter machinery.

### Null-path proof (direct transfers stay byte-identical)

A direct transfer (Duna One) departs the transfer run at the launch-body SOI exit, so
`segs[transferStartIdx - 1].bodyName == "Kerbin"`, not the common ancestor `"Sun"`. `sunPredecessor`
is false, the new predicate is never evaluated, and the `Supported` return is textually unchanged
(only the diagnostic `DepartedFromHeliocentricPark` flag is added, defaulting false). No file other
than `ReaimClassifier.cs` changes behaviour; the `MissionLoopUnitBuilder` edits are logging only.

## Dead-end compliance

1. No per-element LAN/Kepler rotation - the park renders verbatim; the transfer uses the existing
   whole-conic Lambert; the choice is a scheduling scalar.
2. Descent stays body-fixed - untouched.
3. No body-fixed -> inertial longitude lift - positions come from recorded inertial Kepler elements.
4. Transfer draw window not extended into the SOI - `RecordedDepartureUT` stays the burn UT.
5. No single joint resonance - cadence stays synodic; the park is only ever excised in WHOLE periods
   (here zero), never scanned as a continuous phase lever.

## Scope (maintainer-decided 2026-06-15)

- **Departure + Missions UI only.** Engaging re-aim fixes the departure-side render and the
  "not aligned" readout. The Duna landing's ~131 deg rotational alignment stays the separate Phase-4
  `DestinationArrivalSolver` work (orthogonal; not wired here).
- **Empty-cut parks only.** Multi-rev parks (`wholeRevs > keepRevs`) decline -> faithful; a follow-up
  with a multi-rev fixture + composed launch-side-shift validation can lift this.

## Risk register (surviving concerns)

- `r1 = launchBody` is an approximation for a two-burn departure -> mitigated by the admissibility
  gate (near-circular + co-orbital); a park that drifts the burn point far from the launch body is
  declined.
- **True-anomaly PHASE gap (accepted, fails closed).** The admissibility gate bounds the departure
  ORBIT (sma + ecc), not the vessel's PHASE on it. A near-circular co-orbital park can have the
  vessel at an arbitrary true anomaly - up to ~180 deg / ~2 AU out of phase from the launch body at
  the burn - and still pass both gates, so `r1 = launchBody` would mis-approximate the real burn
  point. This is an accepted limitation of the chosen pure (no-live-position) ecc+sma form (Open Q2/Q4
  decision): it FAILS CLOSED (a mis-aimed `r1` reproduces the prior faithful render, not new
  corruption), the downstream encounter check can still reject a transfer that misses the target, and
  a real two-burn departure phases to end near the launch body at the burn (you burn when you reach
  the transfer point, which for a co-orbital park is near the body). If a future recording exposes the
  gap, add a true-anomaly proximity check (needs an `IBodyInfo` position-at-UT accessor).
- The park->transfer handoff is the same sub-pixel-at-map-scale seam the shipped escape->transfer
  handoff already tolerates (B never moves `RecordedDepartureUT` and never window-rotates the park).
- Too-long-tof trap (the M1 failure mode): `transferStart` is unchanged, so the park is never folded
  into the tof - pinned by the `RecordedTransferTofSeconds == arrival - transferStart` test.

## Tests

`ReaimClassifierTests` (+12): the s15 two-burn fixture engages with the correct departure UT / tof /
`DepartedFromHeliocentricPark`; multi-rev / eccentric / wide-sma parks decline; a non-adjacent
earlier multi-rev solar park declines (the all-runs empty-cut scan); a same-sma park eccentric at the
burn point declines (burn-point ecc check); the predicate's loiter / admissibility / fail-closed
branches; the Kepler sma round-trip. The existing sub-period
(`Classify_HeliocentricLoiterBeforeTransfer_Declined`) and same-sma >1-rev decline fences stay green.
Full headless suite green (15696; InjectAllRecordings env-flake excluded).

**Acceptance pending in-game:** load s15, loop "Kerbal X #2", confirm `ReaimDiag plan.Supported=true
parking=True`, `ENGAGED re-aim Kerbin->Duna`, the Missions tab shows the synodic window, the map
renders the full transfer, and (bonus) P4 `dest-trim` fires on the Duna destination loiter
(`sma=495883`, ~11.63 revs) - which this fix unblocks (P4 / PR #1155 cannot be exercised until re-aim
engages; do not modify #1155).
