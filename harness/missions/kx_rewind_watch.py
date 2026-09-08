"""Mission kx_rewind_watch: fly a staged Kerbal X, shed its stages, commit +
Rewind-to-LAUNCH, put a second craft on the pad, and WATCH the first flight
replay as a ghost while the clock walks the whole recorded span.

THE POINT OF THE LANE is the KSP.log it produces: one continuous flight in which
Parsek records a real multi-stage ascent, retires it into a committed tree, throws
the world back to fifteen seconds before that launch, and then renders the very
same flight as a ghost - spawn, map marker, trajectory, watch camera, retire -
while a live vessel sits on the pad watching it happen. Nothing about that render
is judged HERE. This mission's own contract is exactly "the flights flew and the
sequence was driven"; every render claim belongs to the SPEC's log contracts
(``enterwatchmode complete: index=`` and the render-manifest family) reading the
log this flight writes. That split is the harness README's mission-vs-Parsek
orthogonality rule, and it is why a REFUSED ``EnterWatchMode`` is RECORDED by the
machine and flown past rather than failing the mission: a driver-INVALID would
discard the evidence those contracts read and retry an intermittent product defect
into a PASS.

THE PHASE PLAN (``mlib.kxrw_decide``; every state name is an ``mlib.KXRW_*``):

    ROLLOUT   -> PRELAUNCH  (launch_vessel craftName; wait for the NAME back)
    PRELAUNCH -> ASCENT
      -> (BOOSTER-CUT -> BOOSTER-STAGE) x boosterStageCount -> ASCENT
      -> CORE-CUT -> CORE-DISCARD -> COAST
      -> TREE-STATE   (seam RecordingState, capture `tree=`)
      -> COMMIT       (seam CommitTree; its OK stamps the recorded END UT)
      -> STOP         (seam StopRecording)
      -> RECORDER-IDLE(seam RecordingState until it READS recording=false)
      -> REWIND       (seam InvokeRewindToLaunch tree=<captured>)
      -> SPACECENTER  (the OBSERVED backward clock)
      -> AUTORECORD-OFF (seam SetSetting autoRecordOnLaunch=false)
      -> WATCHER-LAUNCH -> WATCHER-READY
      -> MAP-VIEW     (seam EnterMapView)               } verdicts RECORDED,
      -> MAP-EXIT     (seam ExitMapView - the operator  } never fatal
                       rule: WATCHING happens in FLIGHT
                       view, so the map is closed again
                       before watch entry)
      -> WATCH        (seam EnterWatchMode tree=<same>, }
                       HELD until the replay window is
                       open, RE-ASKED while Parsek says
                       `no-watchable-ghost`)
      -> PLAYBACK-WAIT-> DONE

THE IMPACT PROFILE (``impactProfile``, OPT-IN, default false) is the one branch in
that plan. With the key omitted nothing above moves - the phase graph, the emitted
actions and the assertion rows are byte-identical. With it declared THE ASCENT IS
STILL THE ONE ABOVE, every drop and the fueled-core discard included; the flight is
then ended by a DELIBERATE CRASH instead of by a commit, and the tree reaches the
rewind through the Space Center:

    ... -> COAST -> TREE-STATE  (the same RecordingState probe; the id is
                       captured while a recorder is still live, because after the
                       crash the tree is stashed and CommitTree refuses it)
      -> IMPACT-AUTORECORD-OFF  (seam SetSetting autoRecordOnLaunch=false, under
                       its own wire tag: the break-up hands active-vessel to a
                       surviving FRAGMENT, and an armed trigger opens a second
                       recording tree on it)
      -> IMPACT-COAST (fall; the impact is OBSERVED as EITHER a vessel_lost
                       snapshot OR a frozen-telemetry trip, and the last finite
                       UT becomes the recorded END)
      -> IMPACT-SETTLE(hold while the C# destruction coalescer finishes)
      -> SC-EXIT      (seam ExitToSpaceCenter - what actually commits the tree)
      -> SC-COMMITTED (seam ListHandles kind=committed: READ that the tree
                       actually committed)
      -> TEMP-LAUNCH -> TEMP-READY  (a throwaway launch of the RECORDED craft,
                       from the SPACE CENTER, NOT the watcher - see the crew
                       paragraph below; the pre-rewind clock is stamped on the
                       frame the rewind goes out)
      -> REWIND       (unchanged from here on)

WHAT MAKES THE STACK FALL is the ordinary core gate, not a special cut: CORE-CUT
cuts the throttle and disengages the AP, CORE-DISCARD drops the fueled Mainsail,
and this lane never presses the Poodle - so the pod stack is unpowered and
parachute-less from about 60 km, and it reaches the ground on its own.

THE CRASH HAPPENS FAR FROM THE PAD, and the history of that choice matters more
than the choice. An earlier revision cut the throttle at the LAST BOOSTER DROP
and left the stack falling back within 330-374 m of the pad (measured, runs
2026-09-08_1130 / _1157_a2 and 2026-09-08_1302 / _1331_a2), and the launch that
followed hung. The first hypothesis was the pad: KSP's
``PreFlightTests.LaunchSiteClear.Test()`` waits on an obstruction dialog nobody is
there to dismiss. Moving the cut to the core discard (73 km downrange, runs
2026-09-08_1429 / _1503_a2) REFUTED it - only the launch clamps sat at the pad and
the launch hung the same way (and ``LaunchSiteClear`` waves Debris through
anyway). The real blocker is the ROSTER, in the crew paragraph below. The far
crash is KEPT because it is the shape every green flight flew and the shape the
GS-7 tokens are cut to, not because the pad needs it; the scene was never the
blocker either (TEMP-LAUNCH is the same kRPC call GS-4's post-rewind
WATCHER-LAUNCH issues from SPACECENTER).

WHY THE DISARM PRECEDES THE FALL. When the stack breaks up KSP hands the active
vessel to a surviving fragment, and with ``autoRecordOnLaunch`` still armed Parsek
opens a NEW recording tree on it (measured: ``StartRecording: clearing stale chain
state without active tree``). The exit gate this lane pins reads
the tree state (``hasActiveTree=false hasPendingTree=true`` on the stash shape,
``hasActiveTree=true hasPendingTree=false`` on the pending-split shape), so a
fragment recording can refuse the very exit the commit depends on, and either way
it pollutes the save the spec's log contracts read. The disarm cannot run any earlier - that same setting is what auto-started
this flight's own recording at the PRELAUNCH click - so it goes out one frame after
the tree id is captured, and a non-OK is FATAL.

WHY THE SPACE CENTER HOP, because it reads like a detour and is not. The crash
closes in one of two shapes (measured): a slow near-vertical impact goes through
``ParsekFlight.ShowPostDestructionTreeMergeDialog``, which finalizes the tree and
STASHES it as pending (``CommitTree`` is then refused ``no-active-tree``); a fast
impact from the core-discard apoapsis closes through the pending-split path with
the tree still active and its vessel gone. ``ExitToSpaceCenter`` proceeds under
autoMerge=true in BOTH shapes and the tree auto-commits on arrival in SPACECENTER,
which is why the hop is the shape-independent commit; ``InvokeRewindToLaunch`` is
RequiresFlight, which is the ONLY reason a throwaway craft is put on the pad at
all. That craft supplies a
FLIGHT scene and nothing else: the rewind's quicksave predates it, so it is
rewound out of existence and the real watcher is launched afterwards by the
unchanged WATCHER-LAUNCH phase.

THE THROWAWAY IS THE RECORDED CRAFT, AND THE WATCHER CANNOT BE IT. The crash
leaves an ALL-MISSING crew roster - the pod's crew died in the impact and the rest
went with the recovered pad occupant (measured on GS-7 round 3, runs
2026-09-08_1429 / _1503_a2) - so ``DefaultCrewForVessel`` builds an EMPTY manifest
for the next launch. A craft whose command module declares ``minimumCrew = 1``,
which is the Mk1 pod the Jumping Flea is built on, then has NO CONTROL SOURCE:
``PreFlightTests.NoControlSources`` raises its "launch anyway?" dialog and kRPC's
``LaunchVessel`` yields on ``WaitForVesselPreFlightChecks`` forever, because only
that check's own callback sets ``preFlightChecksComplete``. The recorded Kerbal X
carries the RC-L01 ``probeStackLarge`` (``minimumCrew = 0``) and launches
unattended, so TEMP-LAUNCH re-launches IT and TEMP-READY reads the ROLLOUT's
expected name and situations back. THE REWIND DOES NOT REFILL THE POOL (measured
on round 4, runs 2026-09-08_1627 / _a2: the throwaway launched and the rewind
completed, then the Flea hung on the identical ``NoControlSources`` line): the
``parsek_rw_*`` quicksave carries the crew aboard the recorded craft, the strip
reserves them, and the ledger keeps them dead (``Reservation: '<name>'
endUT=INDEFINITE (Dead)``). GS-4 / GS-8's post-rewind Flea flies because their
crew LIVE - ``CrewReservationManager.ReserveCrewIn`` hires a stand-in for every
reserved kerbal and skips Dead ones - so a spec that turns the profile on must
also name a probe-cored ``watcherCraftName`` (GS-7 uses the
``GS1 Auto-Chute Booster``, whose Octo2 launches it empty).

The exit's own OK stays a COMMANDED reading
throughout - it says a scene changed, never that a tree was committed, so
SC-COMMITTED goes and reads the count. Nothing here saves or reloads a game: the
rewind is commanded in the same process the arrival's in-memory commit landed in.

WATCH HOLDS AND THEN KEEPS ASKING, and the GS-4 reading run is why. It issued one
EnterWatchMode at 00:48:27, five seconds before the parent ghost's
``phase=MeshSpawned ... vessel=Kerbal X`` at 00:48:32, and Parsek rightly answered
REJECTED ``no-watchable-ghost``: the committed recording replays at its RECORDED
absolute UTs, so nothing is watchable until the clock reaches ``launch_ut``. Watch
never entered and the parent then derendered as a stale past-end ghost, costing two
required tokens on a flight that was otherwise clean. The machine now waits out
``watchEntryLeadSeconds`` past its own ``launch_ut`` stamp and re-asks that one
refusal under a fresh tag until ``watchEntryRetryFrames``. Everything else about
the render verbs is unchanged: still recorded, still never fatal.

THE LANE ROLLS ITS OWN SUBJECT OUT FIRST, and that is not ceremony.
``activate_next_stage`` stages WHATEVER IS ACTIVE, and what is active at scene
entry is whatever the fixture left on the pad - which for this lane's save is a
DIFFERENT craft, sitting in ``PRE_LAUNCH`` on the LaunchPad. Without ROLLOUT the
mission flies, records, commits, rewinds and watches the wrong vessel, and every
assertion row passes while it does: nothing downstream reads a craft identity.
The gate is the NAME read back off the active vessel
(``TelemetrySnapshot.vessel_name``, opt-in below), because a launch is a scene
RELOAD and the frames on either side of it are otherwise indistinguishable when
both craft sit on the same pad. The name it compares against is declared
SEPARATELY from the craft file name (``rolloutExpectedVesselName`` /
``watcherExpectedVesselName``): ``launch_vessel`` resolves a FILE, but what reads
back is that file's ``ship =`` line - and stock craft files write it as a
``#autoLOC_*`` localization token that KSP surfaces RAW.

THE STAGING PLAN IS READ OFF ``harness/fixtures/ships/Kerbal X.craft``, not
guessed: istg=6 lights the Mainsail + six radial LV-T45s and releases the clamps,
istg 5/4/3 drop the three radial-booster pairs, istg=2 drops the STILL-FUELED
core, and istg 1/0 (Poodle ignition, pod separation) are deliberately NEVER
pressed - the top stack coasts suborbital. The one hard ordering rule is that the
istg=2 click goes out only after the throttle has been READ BACK at zero; see the
``mlib`` section header for why a commanded cut is not enough.

NOTHING HERE STARTS THE RECORDER, AND THAT IS THE CONTRACT. The scenario pins
``autoRecordOnLaunch=true`` in its seam prelude and issues NO ``StartRecording``
step; Parsek's first-staging-on-the-pad trigger
(``ParsekFlight.DecideStageActivateAutoRecord``) starts the Kerbal X recording on
PRELAUNCH's OWN stage click. That trigger is an EVENT, so nothing fires at
``LoadGame`` for a pre-placed pad craft. The machine therefore never assumes a live
recorder: the first thing that reads recorder state at all is TREE-STATE, and it
fails closed on an empty ``tree=`` payload - which is exactly the shape
"auto-record never fired" produces, so that give-up already names this whole
failure class. The mirror obligation is the AUTORECORD-OFF phase: the setting is
still armed after the rewind, so it is turned off BEFORE the watcher launches and
brings a second recorder with it. The impact profile has a SECOND such site,
IMPACT-AUTORECORD-OFF, for a different second recorder: a fragment that survives
the break-up inherits active-vessel while the trigger is still armed.

WHAT THIS MISSION DOES NOT DO (by construction):
  - It never flies the watcher craft, and it never RECORDS it. The watcher (the
    Jumping Flea on GS-4 / GS-8, the probe-cored Booster on GS-7) exists to give
    the flight scene a live vessel, so a ghost has somewhere to be watched FROM;
    AUTORECORD-OFF is what keeps its launch from authoring a recording of its own.
  - It uses NO time warp (v1). The playback wait is real time, bounded by a FRAME
    cap rather than a UT budget, because past the rewind the clock has moved
    backwards and a stuck clock is exactly what a UT budget cannot see.
  - It asserts nothing about ghosts, markers, polylines or the watch camera.

A HANDOFF mission: it certifies that the flights flew and the sequence was driven,
and declares through ``mlib.MISSION_HANDOFF_CONTRACTS`` what it does NOT verify
(the ghost's part-event replay and its render lifecycle), which belong to the spec's
logContracts and ghostLifecycle evaluator - the split the orthogonality note above
describes, and what the runner's verdict line prints as ``handoff mission - ...
not verified here``.

This is a THIN shell: every decision is the pure ``mlib.kxrw_decide`` phase machine
and ``mlib.evaluate_kxrw_assertions``; the flight, connect, logging, seam transport
and result write are the shared ``mission_runner`` runtime. ``import krpc`` never
happens at module top -- it is lazy inside ``mission_runner.KrpcMissionControl.
open`` -- so this module imports clean on the base interpreter (no venv), which is
what lets unittest discovery and the fake-telemetry tests import it without krpc.

GPLv3 (a derivative of the kRPC client; see mission_runner). ASCII only.
"""

from __future__ import annotations

import os
import sys
from typing import List, Optional

# Self-sufficient path bootstrap: as a subprocess this file's dir (missions/) is
# sys.path[0]; put it on the path so ``import mission_runner`` resolves, and
# mission_runner puts missions/lib on the path for mlib.
_HERE = os.path.dirname(os.path.abspath(__file__))
if _HERE not in sys.path:
    sys.path.insert(0, _HERE)

import mission_runner  # noqa: E402
import mlib  # noqa: E402

MISSION_NAME = "kx_rewind_watch"


def build_state(params: dict):
    """Build the mlib KX-REWIND-WATCH phase-machine initial state from the
    missionParams dict."""
    return mlib.kxrw_initial_state(mlib.kxrw_params_from_dict(params))


def decide(state, snapshot):
    return mlib.kxrw_decide(state, snapshot)


def evaluate(frames, params: dict, state=None) -> List[mlib.AssertionOutcome]:
    # Every row is MACHINE-CARRIED evidence stamped on the frame that produced it
    # (the B5/B6/R1 shape). The frames are read for nothing but the peak apoapsis:
    # the tail of this flight runs across a just-reloaded scene where a transient
    # read failure would flip a finished pass into a spurious FLAKE.
    return mlib.evaluate_kxrw_assertions(frames, mlib.kxrw_params_from_dict(params),
                                         state)


def make_control() -> mission_runner.MissionControl:
    # RAW kRPC, no MechJeb, and that is a REQUIREMENT rather than a preference:
    # this lane discards a still-FUELED core on a deliberate click, and MechJeb's
    # ascent autostage would fight the staging sequence for control of it. The
    # gravity turn is flown on kRPC's native AutoPilot instead
    # (mlib.ACTION_AP_SET_PITCH_HEADING).
    #
    # ONE opt-in telemetry channel: `read_vessel_name`, and it is REQUIRED rather
    # than nice-to-have. Both of this lane's launches (the subject's ROLLOUT and the
    # watcher's) gate on the active vessel READING BACK the craft name they asked
    # for, and `vessel_name`'s "" UNREAD sentinel matches no declared name - so
    # dropping this flag does not degrade the mission, it deadlocks both gates into
    # their named give-ups. Pinned by a cell in test_kx_rewind_watch.py.
    #
    # Everything else any gate reads is in the BASE snapshot: `ut`, `altitude`,
    # `apoapsis`, `situation`, `throttle`, `available_thrust`, `vessel_lost`, and
    # the three generalized seam fields. That list is spelled out rather than
    # assumed because of the GS-2 flight-1 lesson - a shell comment claiming a field
    # was "in the base snapshot" when it was behind an opt-in cost that lane a whole
    # flight.
    return mission_runner.KrpcMissionControl(use_mechjeb=False,
                                             read_vessel_name=True,
                                             client_name=MISSION_NAME)


SPEC = mission_runner.MissionSpec(
    name=MISSION_NAME,
    build_state=build_state,
    decide=decide,
    evaluate=evaluate,
    make_control=make_control,
    # NO WARP ANYWHERE (v1). The ascent is 1x by construction and the playback wait
    # is deliberately real time, so any warp state at all is unexpected and should
    # flake the run rather than silently compress the very replay this lane exists
    # to watch.
    allow_rails_warp=False,
    max_physics_warp=0.0,
    # No settle tail (the R1 precedent, sharpened by this lane's reload straddle):
    # every assertion is machine-carried evidence, and the frames AFTER the terminal
    # are read in a scene that has already been torn down and rebuilt once.
    settle_frames=0,
)


def main(argv: Optional[List[str]] = None) -> int:
    return mission_runner.main_from_spec(SPEC, argv)


if __name__ == "__main__":
    sys.exit(main())
