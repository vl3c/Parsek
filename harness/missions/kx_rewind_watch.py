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
brings a second recorder with it.

WHAT THIS MISSION DOES NOT DO (by construction):
  - It never flies the watcher craft, and it never RECORDS it. The Jumping Flea
    exists to give the flight scene a live vessel, so a ghost has somewhere to be
    watched FROM; AUTORECORD-OFF is what keeps its launch from authoring a
    recording of its own.
  - It uses NO time warp (v1). The playback wait is real time, bounded by a FRAME
    cap rather than a UT budget, because past the rewind the clock has moved
    backwards and a stuck clock is exactly what a UT budget cannot see.
  - It asserts nothing about ghosts, markers, polylines or the watch camera.

NOT a handoff mission: it terminates on exactly the outcome it certifies, so it is
deliberately absent from ``mlib.MISSION_HANDOFF_CONTRACTS`` (every mission but
EVA-4 is).

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
