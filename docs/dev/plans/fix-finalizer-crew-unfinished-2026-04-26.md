# Fix two observed bugs from 2026-04-26 session

Source: investigation of `logs/2026-04-26_2247_investigate-finalizer-crew-unfinished/`.
Bugs reproduced from a single play session on `s13` save with branch `main` at
`c1890917`. Branch for the fix: `fix-finalizer-crew-unfinished`.

This plan ships **two independent commits** on one branch / one PR. They share
no code paths and can be reviewed and reverted independently.

The original investigation flagged a third bug ("Unfinished Flights tray empty
after a user-initiated rewind"). The first revision of this plan proposed
synthesizing a `RewindPoint` inside `RecordingStore.InitiateRewind` to fix it,
but a clean review caught that approach as fundamentally broken (fabricated
`BranchPointType.Rewind` enum value, in-memory RP wiped by `LoadGame` before
any OnSave runs, `SessionProvisional` semantics opposite of what the plan
claimed, `QuicksaveFilename` path-form mismatch with `RewindInvoker`). Bug 3
is **deferred to a separate plan** — see "Deferred work" at the bottom.

---

## Bug 1 — Finalizer classifies "Orbiting" for an orbit whose periapsis is inside atmosphere

### Symptom

User flew Kerbal X to a low Kerbin orbit with `SMA=669 km, ecc=0.0967,
PeR=636642` (periapsis altitude ≈ 36 km, well inside the 70 km atmosphere). The
recording was finalized as `Orbiting` instead of `Destroyed`. While the live
vessel was correctly classified as Orbiting by KSP (it had `vessel.situation ==
ORBITING`), the trajectory will deorbit within a couple of orbits via
atmospheric drag, so the finalized terminal state is misleading and the ghost
will replay as a stable orbit forever.

### Root cause

[`RecordingTree.DetermineTerminalState(int, Vessel)`](Source/Parsek/RecordingTree.cs:1609)
overrides `SUB_ORBITAL` to `Orbiting` whenever:

```csharp
vessel.orbit.eccentricity < 1.0
    && vessel.orbit.PeR > vessel.orbit.referenceBody.Radius
```

`PeR > Radius` only checks that the periapsis is above the **surface**. For a
body with an atmosphere, the periapsis must also be **above the atmosphere
top** for the orbit to be stable. Kerbin's atmosphere ends at 70 km, so an
orbit with `PeR - bodyR < 70000` will decay rapidly via drag.

Reference: [`RecordingFinalizationCacheProducer.IsInAtmosphere`](Source/Parsek/RecordingFinalizationCacheProducer.cs:466)
already uses `body.atmosphereDepth` for the in-flight extrapolator — the
override here just needs the same awareness.

### Fix (Commit 1)

Change the orbit-aware override to require `PeR > Radius + atmosphereDepth`
when the body has an atmosphere (`referenceBody.atmosphere == true`). For
atmosphereless bodies (Mun, Minmus, Gilly, Bop, Pol), the existing `PeR >
Radius` check is unchanged.

Implementation steps:

1. Extract a pure helper in `RecordingTree.cs` near `DetermineTerminalState`:
   ```csharp
   internal static bool IsBoundOrbitAboveAtmosphere(
       double eccentricity,
       double periapsisRadius,
       double bodyRadius,
       bool bodyHasAtmosphere,
       double atmosphereDepth)
   ```
   Returns `eccentricity < 1.0 && periapsisRadius > bodyRadius +
   (bodyHasAtmosphere ? atmosphereDepth : 0)`. `internal static` so xUnit
   can hit it.
2. Have `DetermineTerminalState(int, Vessel)` call the helper. The Vessel
   overload reads the values off `vessel.orbit` / `referenceBody`.
3. **Add `vessel.orbit.referenceBody != null` guard at the dispatch site**
   before reading `.atmosphere` / `.atmosphereDepth`. The existing code
   already null-checks `vessel?.orbit` but dereferences `referenceBody`
   without a guard — fix that as part of this commit.
4. Keep the `Info` log line; include `atmoTop` and `bodyHasAtmosphere` so
   future debugging makes the distinction visible:
   ```
   DetermineTerminalState: overriding SUB_ORBITAL to Orbiting — vessel has
   bound orbit (ecc=0.0967, PeR=636642, bodyR=600000, atmoTop=70000,
   bodyHasAtmosphere=True)
   ```
5. Spot-check sibling classifiers: `BallisticExtrapolator`,
   `IncompleteBallisticSceneExitFinalizer`, `RecordingFinalizationCacheProducer`,
   `BackgroundRecorder.cs` line 1154, `ChainSegmentManager.cs` line 785,
   `MergeDialog.cs` line 1175, `ParsekFlight.cs` lines 10006/10207, and
   `VesselSpawner.cs` line 305 — confirm none of them duplicate the buggy
   `PeR > Radius` predicate. (They all dispatch through
   `DetermineTerminalState(int, Vessel)`, so the helper update fixes them
   all.) Note in the commit message which sites were checked.

### Tests

Add to `TreeCommitTests.cs` near the existing `DetermineTerminalState_*`
tests:

- `IsBoundOrbitAboveAtmosphere_NoAtmosphere_AcceptsAboveSurface` — ecc=0.5,
  Pe=10 m above radius, atmosphere=false → true.
- `IsBoundOrbitAboveAtmosphere_NoAtmosphere_RejectsBelowSurface` — Pe inside
  body, atmosphere=false → false.
- `IsBoundOrbitAboveAtmosphere_Atmosphere_RejectsInsideAtmo` — atmosphere=true,
  atmosphereDepth=70000, Pe=36000 above surface → false (this is the bug
  scenario).
- `IsBoundOrbitAboveAtmosphere_Atmosphere_AcceptsAboveAtmo` — Pe=80000 above
  surface → true.
- `IsBoundOrbitAboveAtmosphere_Atmosphere_AtAtmosphereTopExactly` — Pe ==
  atmosphereDepth → false (pin the strict-inequality direction; KSP's
  `atmosphereDepth` is the boundary at which drag goes to zero, so the orbit
  must be strictly above to be stable).
- `IsBoundOrbitAboveAtmosphere_Hyperbolic_AlwaysFalse` — ecc=1.2 → false
  regardless of Pe.

### Risk

Low. The helper is a pure addition. The single behavioral change is that
"glancing" orbits inside atmosphere now finalize as `SubOrbital` instead of
`Orbiting`. That is the correct outcome for Parsek's playback (the ghost would
otherwise be on-railed into a surviving orbit that never decays in playback).

---

## Bug 2 — Recording starts with zero crew because stand-ins were just deleted from roster

### Symptom

Second launch of "Kerbal X" (after the first reached orbit and the user opened
the editor). The pod's manifest in the editor was correctly swapped from
reserved (Jeb/Bill/Bob) to stand-ins (Urgan/Verdorf/Sara) by
`CrewAutoAssignPatch`. But the recording sidecar
`7da119798ac841588768195c6b4a96a3_vessel.craft` has **no `crew = …` lines**
under the Mk1-3 pod, and `StartRecording: captured 0 start crew trait(s)`
appears at [KSP.log:12891](logs/.../KSP.log). All subsequent capture/breakup
events also show `0 crew`. Crew reappears in the live vessel ~2.5 minutes
later (KSP.log:17810) but the recording's start/end-crew is permanently empty.

### Root cause

On the FLIGHT scene OnLoad after the user clicks Launch, the load order is:

1. `OnLoad` → `[CrewReservation] Loaded 3 crew replacement(s)` (Urgan→Jeb etc.)
   ([KSP.log:12447](logs/.../KSP.log)).
2. `KerbalsModule.ApplyToRoster` runs ([KerbalsModule.cs:1062](Source/Parsek/KerbalsModule.cs)).
   The recordings module sees that the only existing recordings (the orbit
   chain) have no in-progress reservations now (the originals were rescued
   to Available after the first rewind), so `reservations.Count == 0`.
3. The "Step 2: Remove unused displaced stand-ins from roster" loop
   ([KerbalsModule.cs:1303-1342](Source/Parsek/KerbalsModule.cs)) walks each
   slot, checks `IsKerbalInAnyRecording(standIn)` — which only consults
   `allRecordingCrew` (committed recordings). The stand-ins are not in any
   committed recording yet (the new launch hasn't started recording!), so
   they fall through to the `Available` removal path:
   ```
   roster.TryGetStatus(standIn, out rosterStatus)
       && rosterStatus == ProtoCrewMember.RosterStatus.Available
       && roster.TryRemove(standIn)
   ```
   `Stand-in 'Urgan Kerman' displaced -> deleted (unused)` × 3
   ([KSP.log:12493](logs/.../KSP.log)).
4. KSP then loads the saved ProtoVessel, which references crew names that no
   longer exist in the roster, leaving the seats empty.
5. Recording starts at 22:41:04 with 0 crew in the snapshot.

The check `IsKerbalInAnyRecording` knows nothing about crew that is **on the
live active vessel**. The stand-ins are about to be sealed into a recording
the next frame, but the roster-cleaner has no way to see that.

### Fix (Commit 2)

In [`KerbalsModule.ApplyToRoster`](Source/Parsek/KerbalsModule.cs:1303), add a
second "is the stand-in protected?" check before the `displaced -> deleted`
path: **also keep the stand-in if it currently sits in a live (loaded) vessel
crew**.

Implementation steps:

1. **Reuse the existing `IKerbalRosterFacade.IsKerbalOnLiveVessel`** at
   [`KerbalsModule.cs:856`](Source/Parsek/KerbalsModule.cs:856), implemented
   at [`KerbalsModule.cs:963-999`](Source/Parsek/KerbalsModule.cs:963). It
   already walks `FlightGlobals.Vessels`, excludes ghost-map vessels via
   `GhostMapPresence.IsGhostMapVessel`, and has the FlightGlobals-not-ready
   try/catch. Do **not** introduce a duplicate helper.
2. In the `displaced && !isReserved && !usedInRecording` branch
   ([KerbalsModule.cs:1316-1340](Source/Parsek/KerbalsModule.cs:1316)),
   before the `Available + TryRemove` step, call
   `roster.IsKerbalOnLiveVessel(standIn)`. If it returns true, take the same
   "keep in roster" disposition the recording-used path takes (do not delete,
   log `Stand-in 'X' displaced -> retained (on live vessel)`), and bump a new
   `retainedLive` counter that is included in the `ApplyToRoster complete:`
   summary so the new behavior is observable.
3. Update the summary line so reviewers can spot the new bucket: `…
   {deletedUnused} deleted, {retiredDisplaced} displaced, {retainedLive}
   retained-live`.

### Tests

`KerbalsModuleApplyToRosterTests.cs` (or whichever existing test file targets
ApplyToRoster — pick the matching one in `Source/Parsek.Tests`) already
exercises the rescue/displaced matrix. Add:

- `ApplyToRoster_StandInOnLiveVessel_RetainedNotDeleted` — fake roster facade
  with a displaced unused stand-in; mock facade to report Available + the
  `IsKerbalOnLiveVessel` to return true → expect retained, not deleted, and
  a log line containing `retained (on live vessel)`.
- `ApplyToRoster_StandInNotOnLiveVessel_DeletedAsBefore` — same setup but
  facade `IsKerbalOnLiveVessel` returns false → existing delete path still
  fires (regression guard).
- Verify the summary line includes `retained-live={n}` for both cases.

If the existing test fixture's `IKerbalRosterFacade` mock doesn't yet wire
`IsKerbalOnLiveVessel`, extend it (the interface is already public; this
should be a one-line addition to the mock).

### Risk

Medium. The change loosens the deletion path. The risk is that genuinely
unused stand-ins occupying seats from prior aborted launches (the case the
delete-path was written for) are now retained forever. Mitigations:

- The new check fires only when the kerbal is actually on a vessel known to
  `FlightGlobals` and not a ghost-map vessel. Garbage stand-ins from ancient
  sessions won't be loaded.
- `retiredDisplaced++` (the original "used in recording" branch) keeps them
  too, so this isn't a stricter retention than already exists for an even
  smaller predicate.
- The new log bucket makes it easy to spot if the count grows unexpectedly.

### Out of scope

The deeper question — "why does the FLIGHT-scene OnLoad ApplyToRoster trigger
roster mutation BEFORE the new launch's recording starts?" — would need a
larger refactor of the load order. The local guard fixes the user-visible
symptom (recording-with-no-crew) without touching that order.

---

## Build, test, validate

```bash
cd Source/Parsek && dotnet build
cd ../../Source/Parsek.Tests && dotnet test
```

Verify deployed DLL after each commit (sibling worktrees may clobber):

```bash
ls -la "../Kerbal Space Program/GameData/Parsek/Plugins/Parsek.dll"
ls -la Source/Parsek/bin/Debug/Parsek.dll
```

If sizes/mtimes don't match, force-copy and re-grep for a distinctive new
string from the change.

In-game smoke test (manual, after building):

1. Fly to a low Kerbin orbit with periapsis below 70 km (e.g. SMA ≈ 670 km,
   ecc ≈ 0.1). Observe the recording is finalized as `Destroyed` /
   `SubOrbital`, not `Orbiting`. Compare against an orbit with Pe > 80 km
   (still classified as `Orbiting`).
2. Launch a vessel whose reserved crew is still aboard a previous mission
   (forcing stand-in substitution). Check the recording sidecar
   (`<id>_vessel.craft`) contains `crew = <Standin> Kerman` lines under the
   command pod.

## Documentation updates

Per the per-commit doc-update rule:

- `CHANGELOG.md` — two entries under the next version, one per commit.
- `docs/dev/todo-and-known-bugs.md` — two new "Fix:" entries describing each
  fix; mark this plan as referenced.
- `.claude/CLAUDE.md` — no change (no file-layout / build-command / workflow
  changes).

---

## Deferred work — Bug 3 (Unfinished Flights tray empty after user rewind)

Original symptom: user clicked the Rewind button on the Kerbal X recording in
the recordings table; the rewound subtree's debris recordings were not
classified as Unfinished Flights.

Root cause confirmed: [`RecordingStore.InitiateRewind`](Source/Parsek/RecordingStore.cs:3546)
loads the recording's launch quicksave into SpaceCenter without authoring any
`RewindPoint`. `EffectiveState.IsUnfinishedFlight` requires an RP whose
`BranchPointId` matches the recording's parent or child BP id, so the
predicate always returns false for the rewound subtree.

Why this isn't shipping in this PR: the first plan revision proposed
synthesizing an in-memory `RewindPoint` just before the `LoadGame` call, but
that has multiple defects (verified by review):

- `BranchPointType.Rewind` doesn't exist
  ([`BranchPoint.cs:6-16`](Source/Parsek/BranchPoint.cs:6) defines `Undock,
  EVA, Dock, Board, JointBreak, Launch, Breakup, Terminal` only).
- `SessionProvisional = true` is **never** reap-eligible
  ([`RewindPointReaper.IsReapEligible`](Source/Parsek/RewindPointReaper.cs:175)
  short-circuits at the SessionProvisional check), and
  [`LoadTimeSweep.IsSessionScopedProvisionalRp`](Source/Parsek/LoadTimeSweep.cs:446)
  requires a `CreatingSessionId` to ever discard. Result: every rewind would
  permanently leak one RP.
- The synthesized RP would never reach disk: `InitiateRewind` calls
  `GamePersistence.LoadGame` immediately, which wipes the in-memory
  `RewindPoints` list before any OnSave runs.
- `QuicksaveFilename = owner.RewindSaveFileName` resolves to the wrong path
  when later passed to `RewindInvoker.ResolveAbsoluteQuicksavePath` — v0.9
  RPs live at `Parsek/RewindPoints/<id>.sfs`, legacy quicksaves at
  `Parsek/Saves/<name>.sfs`.

Two viable directions for a future plan:

(a) **Extend `IsUnfinishedFlight` to recognize a non-RP signal.** When the
    recording (or a sibling in its tree) carries
    `SpawnSuppressedByRewindReason == "same-recording"`, treat the Crashed
    descendants as Unfinished. This is a UI-classification-only change with
    no save-format impact. Risk: drifts from the design's "RP existence is
    the single source of truth" invariant; needs a design doc update.

(b) **Author a proper v0.9 RewindPoint from the legacy rewind path.** Use
    `RewindPointAuthor` to write the quicksave to
    `Parsek/RewindPoints/<id>.sfs` *before* `LoadGame`, attach to the right
    BranchPoint, and let the existing reaper / sweep handle lifecycle. Risk:
    larger blast radius — must not regress the v0.9 Rewind-to-Separation flow,
    needs end-to-end manual testing.

Filing a separate plan for this. Track via the TODO doc.
