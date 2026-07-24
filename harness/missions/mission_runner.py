"""Shared mission-shell runtime for the M-B1 flown missions (design M-B1).

This is the THIN I/O shell the two mission programs (``b1_pad_hop.py`` /
``b2_lko_ascent.py``) share. It owns everything that is NOT a pure decision:

  - the injectable telemetry/control SEAM (``MissionControl``): the real
    ``KrpcMissionControl`` wraps the kRPC client (imported LAZILY inside
    ``open()`` so this module -- and the mission shells -- import clean on the
    BASE interpreter with NO krpc installed, which is what lets the unittest
    discovery + the fake-telemetry tests run without the venv); a test injects a
    fake with the same surface,
  - the bounded connect-retry loop (driven by ``mlib.decide_connect_retry``),
  - the bounded per-frame fly loop (driven by a mission's ``mlib`` phase machine),
  - the mission-result JSON write (via ``mlib.serialize_mission_result``),
  - the ``[Mission][LEVEL][Phase]`` diagnostic logging (design "Diagnostic
    Logging"), and the CLI parsing.

EVERY decision is delegated to ``mlib`` (the pure, kRPC-free, unittest-covered
library): the phase machines, the assertion evaluators, the connect-retry
decision, and the result serialization all live there. This module only performs
the I/O the decisions dictate. It NEVER hangs: connect is bounded by a budget +
attempt cap, every phase is bounded by an ``mlib`` phase budget, and the whole
fly loop is bounded by a wall-clock deadline (the ``--budget`` the harness
passes); every terminal path writes the mission-result JSON and returns an exit
code (0 only on MISSION-OK), and any exception is caught (never a hang), with the
kRPC connection closed in a ``finally``.

Exception taxonomy (design edges 5 / 8 vs 9), classified by WHEN + WHERE it
originates:
  - PRE-connect / setup / an internal non-kRPC bug (a None dereference, a bad
    param) -> MISSION-ERROR (subkind tooling-mission).
  - POST-connect kRPC RPC / connection-drop exception (a mid-flight socket reset,
    a dropped stream, a vessel-invalid RPC error) -> MISSION-FLAKE (subkind
    autopilot-flake), retryable on a fresh boot -- a transient the mission-validity
    gate keeps out of the Parsek-defect bucket.
The pre/post split is a ``connected`` flag; the kRPC-vs-internal origin split is
the pure ``mlib.classify_post_connect_exception`` (mlib never imports krpc).

GPLv3 NOTICE: the mission shells (this file + ``b1_pad_hop.py`` /
``b2_lko_ascent.py``) import the kRPC client (GPLv3) and are therefore themselves
taken as GPLv3 (a derivative of the GPL client). The obligation is confined to
these kRPC-importing mission shells and never reaches Parsek, run.py, hlib,
provlib, or the kRPC-free ``mlib`` (design "The dependency decision").

ASCII only; stdlib only outside the lazily-imported kRPC client; LF line endings.
"""

from __future__ import annotations

import argparse
import json
import math
import os
import sys
import threading
import time
import traceback
from collections import deque
from dataclasses import dataclass, replace
from typing import Callable, Dict, List, Optional, Tuple

# The mission shells run as subprocesses (``python missions/<name>.py ...``) with
# their own directory (missions/) as sys.path[0]; the pure decision library lives
# in missions/lib. Put it on the path BEFORE importing mlib so the shell is
# self-sufficient whether launched as a script or imported by the tests (where
# missions/lib is already the discovery root).
_MISSIONS_DIR = os.path.dirname(os.path.abspath(__file__))
_MLIB_DIR = os.path.join(_MISSIONS_DIR, "lib")
if _MLIB_DIR not in sys.path:
    sys.path.insert(0, _MLIB_DIR)

import mlib  # noqa: E402  (deliberately after the sys.path bootstrap above)


# ---------------------------------------------------------------------------
# Bounded connect-retry defaults (design "Connection lifecycle" step 2: "up to
# 10 attempts over 30 s"). These are shell constants, not missionParams: the spec
# tunes the FLIGHT budgets, the connect budget is a fixed server-bind allowance.
# ---------------------------------------------------------------------------

CONNECT_BUDGET_SECONDS = 30.0
CONNECT_MAX_ATTEMPTS = 10
CONNECT_BACKOFF_SECONDS = 3.0

# Consecutive telemetry-read failures that mean the active vessel is GONE (KSP
# destroyed the craft and handed active-vessel to invalid debris) rather than a
# one-off transient. Below this the read re-raises (the existing transient path);
# at or above it read_snapshot emits a vessel_lost snapshot the phase machine
# terminates on (design "First live B1 flown-mission run": vessel-destroyed
# terminal).
READ_FAIL_STREAK_LIMIT = 3

# Mid-mission command-seam CommitTree bounded poll (route 1, section 3.2). The
# CommitTree verb executes on the Unity main thread in one frame, so the response
# appears fast; the bound only exists so a wedged addon can never hang the fly
# loop -- expiry yields TIMEOUT (the machine flakes it, driver-INVALID). WALL
# seconds (the game-time station-commit phase budget is the machine's separate
# backstop).
SEAM_COMMIT_POLL_SECONDS = 120.0
SEAM_COMMIT_POLL_INTERVAL = 0.5

# Per-frame telemetry poll cadence. The fly loop reads a snapshot, decides, and
# executes actions this often; the design's telemetry log line is rate-limited to
# ~1 Hz over this cadence.
POLL_INTERVAL_SECONDS = 0.5

# Bounded settle-tail sampled AFTER the phase machine reaches a real terminal
# (LANDED / ORBIT), before evaluating the assertions. The design requires "K
# consecutive in-tolerance snapshots" once warp has settled to 1x (guardrails:
# MechJeb warp-stutter debounce); the phase machine reaches ORBIT on the FIRST
# frame that crosses the transition threshold, so a few settled samples are needed
# for the K-consecutive periapsis / eccentricity debounce to have data. Sized one
# above the default debounce depth. Not sampled on a flake (we are aborting).
DEFAULT_SETTLE_FRAMES = mlib.DEFAULT_DEBOUNCE_K + 1

# A course-correction plan below this total dv (m/s) is removed instead of
# executed: it is smaller than the NodeExecutor's 1.0 m/s tolerance (which
# never engages on such a node -- sixth live B5 flight 2026-07-22) and the
# residual it would fix is within the flyby floor's margin anyway.
NEGLIGIBLE_CORRECTION_DV = 0.5

# Native-warp watchdog (Path A, docs/dev/research/native-warp-to-ut.md):
# WALL seconds of game-UT standstill tolerated while a WarpService warp is
# active before the watchdog acts. First action on a stall is the pause probe
# (a dialog pause does NOT freeze the kRPC server -- PauseServerWithGame
# defaults false -- so KRPC.Paused is readable AND clearable from the primary
# connection); a stall that persists WITHOUT a pause (or after clearing one)
# cancels the warp so the mission never rides a wedged continuation.
WARP_STALL_WALL_SECONDS = 10.0

# WarpService thread-join allowance on cancel (the server discards the
# dropped connection's continuation on its next FixedUpdate; the thread's
# blocked receive raises out well inside this).
WARP_CANCEL_JOIN_SECONDS = 5.0

# Live observability (design docs/dev/design-live-observability.md Phase 2).
# MACHINE-STATE line cadence (2a): the decision state verbatim, rate-limited
# alongside the ~1 Hz telemetry line.
MACHINE_STATE_INTERVAL_SECONDS = 5.0
# Event-window ring buffer depth (2c): at the 0.5 s poll cadence, 20 frames
# is a ~10 s flight-data-recorder window dumped once on transition / flake /
# vessel-lost / gate-flip.
RING_BUFFER_FRAMES = 20
# Live status file rewrite cadence (2d): results/<runId>_status.json,
# atomic tmp+os.replace, best-effort (never blocks the fly loop).
STATUS_WRITE_INTERVAL_SECONDS = 2.0


class WarpStallTracker:
    """PURE stall detector for the native-warp watchdog: tracks the last time
    game UT ADVANCED (wall-clock stamped) and reports a stall once it has
    stood still for ``stall_seconds`` of wall time while a warp is active.
    The caller resets it whenever no warp is active. Kept free of I/O so the
    watchdog decision is unit-testable headless."""

    def __init__(self, stall_seconds: float = WARP_STALL_WALL_SECONDS) -> None:
        self.stall_seconds = float(stall_seconds)
        self._last_ut: Optional[float] = None
        self._last_advance_wall: Optional[float] = None

    def reset(self) -> None:
        self._last_ut = None
        self._last_advance_wall = None

    def update(self, wall_now: float, ut: float) -> bool:
        """Feed one (wall clock, game UT) sample; True = UT has not advanced
        for at least ``stall_seconds`` of wall time. A non-finite UT counts
        as no-advance (fail closed toward detection: a stalled/unreadable
        clock is exactly the wedge class the watchdog exists for)."""
        finite = isinstance(ut, (int, float)) and not isinstance(ut, bool) \
            and math.isfinite(ut)
        if self._last_advance_wall is None:
            self._last_ut = float(ut) if finite else None
            self._last_advance_wall = wall_now
            return False
        if finite and (self._last_ut is None or float(ut) > self._last_ut + 1e-6):
            self._last_ut = float(ut)
            self._last_advance_wall = wall_now
            return False
        return (wall_now - self._last_advance_wall) >= self.stall_seconds


class WarpService:
    """Owns a DEDICATED second kRPC connection whose only job is to sit inside
    the blocking ``SpaceCenter.WarpTo`` RPC, on a daemon background thread --
    the primary telemetry connection never blocks (per-connection RPC
    serialization, pinned kRPC Core.cs; research doc section 1). The thread
    touches ONLY its own connection object; shared state (target, error text)
    sits behind a lock. The thread NEVER raises into the main loop: any
    exception (including the deliberate socket close from ``cancel``) is
    logged and the service goes idle.

    Cancel contract (research doc): close the warp socket -- the server
    discards the continuation on its next FixedUpdate -- then the PRIMARY
    connection zeroes both warp factors (the dropped stepper leaves the rate
    where it was)."""

    def __init__(self, host: str, rpc_port: int, stream_port: int,
                 connect_fn: Optional[Callable] = None,
                 client_name: str = "parsek-warp") -> None:
        self._addr = (str(host), int(rpc_port), int(stream_port))
        self._client_name = client_name
        self._connect_fn = connect_fn        # test seam; None = krpc.connect
        self._lock = threading.Lock()
        self._conn = None
        self._thread: Optional[threading.Thread] = None
        self._target_ut = float("nan")
        # Per-dispatch cancellation (delta-review B1): every warp_to_ut
        # mints a FRESH Event + a generation number the thread captures. A
        # zombie thread from a timed-out cancel join (old thread stuck in
        # krpc.connect past the 5 s bound) then sees ITS OWN still-set event
        # -- the next dispatch can no longer clear it out from under the
        # zombie -- and its state writes (conn store, target NaN) are
        # generation-guarded so it can never clobber the live warp's state.
        self._cancelled = threading.Event()
        self._generation = 0

    # -- state ---------------------------------------------------------------

    @property
    def active(self) -> bool:
        t = self._thread
        return t is not None and t.is_alive()

    @property
    def target_ut(self) -> float:
        """The commanded target UT while active, NaN when idle (the
        TelemetrySnapshot.warping_to source: fail closed to idle)."""
        with self._lock:
            return self._target_ut if self.active else float("nan")

    # -- commands ------------------------------------------------------------

    def warp_to_ut(self, ut: float, primary_sc) -> None:
        """Fire-and-forget native warp: spawn the daemon thread that connects
        its own client and blocks inside SpaceCenter.WarpTo. An active warp is
        cancelled first (kRPC WarpTo cannot retarget; research doc: WarpTo
        while engaged is a no-op server-side for the STOCK path and a second
        continuation for ours -- never let two run)."""
        if self.active:
            self.cancel(primary_sc)
        with self._lock:
            self._target_ut = float(ut)
            self._generation += 1
            generation = self._generation
            cancelled = threading.Event()
            self._cancelled = cancelled
        thread = threading.Thread(
            target=self._run, args=(float(ut), generation, cancelled),
            name="parsek-warp-to-ut", daemon=True)
        with self._lock:
            self._thread = thread
        thread.start()
        _stdout_sink(mlib.format_mission_log_line(
            "Info", "Warp", "native warp_to dispatched target ut=%.3f" % float(ut)))

    def cancel(self, primary_sc) -> None:
        """Hard-cancel: drop the warp connection, join the thread (bounded),
        then zero BOTH warp factors from the primary connection (the dropped
        continuation leaves the rate where it was). Idempotent."""
        self._cancelled.set()
        with self._lock:
            conn = self._conn
            self._conn = None
            thread = self._thread
        if conn is not None:
            try:
                conn.close()
            except Exception as exc:
                _stdout_sink(mlib.format_mission_log_line(
                    "Warn", "Warp", "warp connection close failed: %s" % (exc,)))
        if thread is not None and thread.is_alive():
            thread.join(timeout=WARP_CANCEL_JOIN_SECONDS)
        with self._lock:
            self._thread = None
            self._target_ut = float("nan")
        if primary_sc is not None:
            # The dropped stepper leaves warp at its last rate: always pair
            # the socket drop with the factor reset (research doc, residual
            # risks). Best-effort: a dead primary is the transport-drop path.
            try:
                primary_sc.rails_warp_factor = 0
                primary_sc.physics_warp_factor = 0
            except Exception as exc:
                _stdout_sink(mlib.format_mission_log_line(
                    "Warn", "Warp", "post-cancel factor reset failed: %s" % (exc,)))
        _stdout_sink(mlib.format_mission_log_line(
            "Info", "Warp", "native warp cancelled (factors zeroed)"))

    def close(self) -> None:
        """Teardown at mission end: drop the warp connection + thread. No
        factor reset (the whole client is going away)."""
        self._cancelled.set()
        with self._lock:
            conn = self._conn
            self._conn = None
            thread = self._thread
            self._thread = None
            self._target_ut = float("nan")
        if conn is not None:
            try:
                conn.close()
            except Exception:
                pass
        if thread is not None and thread.is_alive():
            thread.join(timeout=WARP_CANCEL_JOIN_SECONDS)

    # -- background thread ---------------------------------------------------

    def _run(self, ut: float, generation: int,
             cancelled: threading.Event) -> None:
        """Thread body: connect the dedicated client, sit inside WarpTo, go
        idle. EVERY exception is caught and logged -- the deliberate cancel
        (socket closed under the blocked receive) lands here too and is
        logged at Info, anything else at Warn. Nothing propagates.
        ``generation``/``cancelled`` are THIS dispatch's own (delta-review
        B1): a zombie from a timed-out cancel join checks its own event and
        writes shared state only while its generation is still current."""
        conn = None
        try:
            if self._connect_fn is not None:
                conn = self._connect_fn()
            else:
                import krpc  # LAZY: only the warp thread needs it
                host, rpc_port, stream_port = self._addr
                conn = krpc.connect(name=self._client_name, address=host,
                                    rpc_port=rpc_port, stream_port=stream_port)
            with self._lock:
                stale = cancelled.is_set() or self._generation != generation
                if not stale:
                    self._conn = conn
            if stale:
                try:
                    conn.close()
                except Exception:
                    pass
                return
            conn.space_center.warp_to(ut)
            _stdout_sink(mlib.format_mission_log_line(
                "Info", "Warp", "native warp_to completed at target ut=%.3f" % ut))
        except Exception as exc:
            level = "Info" if cancelled.is_set() else "Warn"
            _stdout_sink(mlib.format_mission_log_line(
                level, "Warp", "warp thread ended (%s: %s)"
                % (type(exc).__name__, str(exc)[:160])))
        finally:
            with self._lock:
                if self._generation == generation:
                    if self._conn is conn:
                        self._conn = None
                    self._target_ut = float("nan")


# ---------------------------------------------------------------------------
# Injectable telemetry/control seam (design Mental Model "kRPC calls behind an
# injectable interface"). The pure mlib decisions drive this; the real impl wraps
# kRPC, a test injects a fake with the same surface.
# ---------------------------------------------------------------------------


class MissionControl:
    """The telemetry/control seam a mission drives. All kRPC access sits behind
    this interface so the pure ``mlib`` decisions never touch the client and the
    fake-telemetry tests inject a scripted stand-in.

    Contract:
      - ``open(host, rpc_port, stream_port)`` establishes the connection or raises
        (the connect-retry loop catches the raise and decides RETRY/TIMEOUT).
      - ``client_version`` / ``server_version`` are readable after a successful
        ``open`` (the on-connect ABI check reads them).
      - ``read_snapshot()`` returns a frozen ``mlib.TelemetrySnapshot`` of the
        live flight quantities.
      - ``perform(action)`` executes one ``mlib.Action`` (throttle/stage/chute for
        B1, MechJeb calls for B2).
      - ``close()`` tears the connection down (called in a ``finally``; must
        swallow its own teardown errors or raise them for the caller to swallow).
    """

    client_version: str = ""
    server_version: str = ""

    def open(self, host: str, rpc_port: int, stream_port: int) -> None:
        raise NotImplementedError

    def read_snapshot(self) -> "mlib.TelemetrySnapshot":
        raise NotImplementedError

    def perform(self, action: "mlib.Action") -> None:
        raise NotImplementedError

    def close(self) -> None:
        raise NotImplementedError

    def configure_seam(self, commands_path: str, responses_path: str,
                       commit_id: str) -> None:
        """Wire the mid-mission command-seam bridge (route 1, section 3.2). The
        default is a no-op; only ``KrpcMissionControl`` (and a bdock fake)
        implement it. run_mission calls it AFTER make_control when the mission
        was spawned with seam args, so a non-B-DOCK control cleanly ignores it."""
        return None


class KrpcMissionControl(MissionControl):
    """Real telemetry/control seam: wraps the kRPC client. ``import krpc`` is
    LAZY (inside ``open``), so importing this module on the base interpreter (no
    krpc) succeeds -- only an actual flight, in the mission venv, imports the
    client.

    NOTE (PENDING-OPERATOR): the exact kRPC API surface below cannot be verified
    offline in this environment (no venv, no server). It is a best-effort mapping
    of the design's phase actions + telemetry fields to the 0.5.x client API; the
    live B1/B2 runbook (design Test Plan "PENDING-OPERATOR") is where it is
    confirmed. The fake-telemetry tests exercise the SHELL's control flow, not
    this class. Reads are defensive: a missing MechJeb surface leaves the B2
    carried-evidence flags at their benign defaults.
    """

    def __init__(self, use_mechjeb: bool = False, client_name: str = "parsek-mission",
                 read_docking: bool = False, read_crew: bool = False,
                 read_chute: bool = False) -> None:
        self._use_mechjeb = use_mechjeb
        self._client_name = client_name
        # OPT-IN B-DOCK docking/rendezvous/transfer telemetry (design section 5.2).
        # OFF for B1/B2/B4/B5/B7 so their read_snapshot stays byte-identical (they
        # never touch the MechJeb target controller / docking-port surface). The
        # bdock shell constructs this True.
        self._read_docking = bool(read_docking)
        # OPT-IN crew-count telemetry (FORGE-LKO). OFF everywhere else so their
        # read_snapshot stays byte-identical (crew_count keeps its -1 unread
        # sentinel, which fails every crew gate closed). ONE extra RPC per poll,
        # taken only by the forge that must certify its fixture is CREWED.
        self._read_crew = bool(read_crew)
        # OPT-IN parachute-state telemetry (EVA-4). OFF everywhere else so every other
        # mission's read_snapshot stays byte-identical (craft_chute_state keeps its ""
        # unread sentinel, which fails every chute gate closed). ONE extra RPC per poll,
        # taken only by the mission whose whole terminal depends on OBSERVING the canopy
        # rather than trusting that it commanded one (EVA-4 flight-1 lesson).
        self._read_chute = bool(read_chute)
        self._conn = None
        self._mechjeb = None
        self._ascent = None
        # --- B-DOCK handle caching (P9 / Q4). Never name/pid; captured kRPC
        # object handles resolved while the object was reachable.
        self._station_vessel = None          # captured at STATION-COMMIT
        self._station_port = None            # its top docking port
        self._station_tanks: Dict[str, object] = {}   # resource -> station-side tank part
        self._active_transfer = None         # the in-flight ResourceTransfer handle
        # --- Mid-mission command-seam bridge (route 1, section 3.2). Set via
        # configure_seam(); the reserved command-id + the channel file paths the
        # ACTION_PARSEK_COMMIT_TREE case writes/polls. None = no seam configured
        # (any non-B-DOCK mission never emits the action).
        self._seam_commands_path: Optional[str] = None
        self._seam_responses_path: Optional[str] = None
        self._seam_commit_id: Optional[str] = None
        self._seam_commit_result: str = ""   # rides TelemetrySnapshot.seam_commit_result
        # Latches True the first time the AscentAutopilot reads as enabled, so
        # "complete" is never inferred BEFORE the autopilot has ever been engaged
        # (NIT 8: pre-engage, enabled==False must NOT read as ascent-complete=True).
        self._mj_ever_enabled = False
        # Consecutive telemetry-read failures. A one-off error still re-raises (the
        # existing transient path); only a SUSTAINED streak (>= _READ_FAIL_LIMIT)
        # means the active vessel is gone -- KSP destroyed the craft and handed
        # active-vessel to invalid debris -- so we emit a vessel_lost snapshot the
        # phase machine terminates on rather than let the mission burn its budget.
        self._read_fail_streak = 0
        self.client_version = ""
        self.server_version = ""
        # Native-warp service (Path A): built lazily on the first warp_to_ut
        # action, with the address captured at open(). The stall tracker is
        # the pure watchdog core; wall clock injectable for tests.
        self._warp: Optional[WarpService] = None
        self._warp_stall = WarpStallTracker()
        self._addr: Optional[Tuple[str, int, int]] = None

    def open(self, host: str, rpc_port: int, stream_port: int) -> None:
        import krpc  # LAZY: the base interpreter must import this shell with no krpc.

        self._addr = (str(host), int(rpc_port), int(stream_port))
        self._conn = krpc.connect(
            name=self._client_name, address=host,
            rpc_port=int(rpc_port), stream_port=int(stream_port))
        status = self._conn.krpc.get_status()
        self.server_version = str(getattr(status, "version", ""))
        self.client_version = str(getattr(krpc, "__version__", ""))
        if self._use_mechjeb:
            # KRPC.MechJeb service; the AscentAutopilot is the B2 driver.
            self._mechjeb = self._conn.mech_jeb
            self._ascent = self._mechjeb.ascent_autopilot

    def read_snapshot(self) -> "mlib.TelemetrySnapshot":
        # Wrapped so a SUSTAINED read-failure streak (the active vessel destroyed +
        # handed to invalid debris) becomes a vessel_lost snapshot the phase machine
        # terminates on, while a one-off transient still re-raises (the existing
        # error path). The streak resets on any successful read.
        try:
            sc = self._conn.space_center
            v = sc.active_vessel
            orbit = v.orbit
            # Handle caching (review N-B1): every attribute access on a kRPC
            # proxy is one RPC round trip -- fetch each intermediate object
            # (body, resources, control) exactly once per poll.
            body = orbit.body
            resources = v.resources
            control_handle = v.control
            flight_srf = v.flight(body.reference_frame)
            mj_enabled = False
            mj_complete = False
            if self._ascent is not None:
                try:
                    mj_enabled = bool(self._ascent.enabled)
                    if mj_enabled:
                        self._mj_ever_enabled = True
                    # The AscentAutopilot disables itself once the ascent is complete;
                    # "complete" = engaged EARLIER (latched) AND now disabled. Before it
                    # has ever been engaged, disabled is PRELAUNCH, NOT complete (NIT 8).
                    mj_complete = self._mj_ever_enabled and not mj_enabled
                except Exception as exc:
                    # Log-what-you-gate-on (review N-B2): the ascent-complete
                    # gate reads these; a silent False on a read error would
                    # be indistinguishable from a real not-complete.
                    _stdout_sink(mlib.format_mission_log_line(
                        "Warn", "Telemetry",
                        "MechJeb ascent enabled/complete read failed (%s: %s); "
                        "reporting enabled=False complete=False this frame"
                        % (type(exc).__name__, str(exc)[:120])))
                    mj_enabled = False
                    mj_complete = False
            warp_mode, warp_rate = _read_warp_state(sc)
            # AutoPilot pointing error (degrees). kRPC raises when the AP is not
            # engaged; NaN then, which the B4 attitude AND-gate treats as
            # not-aligned (a wedged AP ends as the bounded deorbit-budget flake,
            # never a throttle-up on an unknown attitude).
            try:
                ap_error = float(v.auto_pilot.error)
            except Exception:
                ap_error = float("nan")
            nodes = control_handle.nodes
            # Patched-conic arrival evidence (finding 16): the NEXT orbit's
            # body + periapsis, read only while an SOI change is predicted.
            # Own try/except: a conic repatch mid-read must degrade to the
            # fail-closed NaN/"", never count toward the vessel-lost streak.
            next_body = ""
            next_pe = float("nan")
            try:
                tts = float(orbit.time_to_soi_change)
                if math.isfinite(tts):
                    next_orbit = orbit.next_orbit
                    if next_orbit is not None:
                        next_body = str(next_orbit.body.name)
                        next_pe = float(next_orbit.periapsis_altitude)
            except Exception:
                next_body = ""
                next_pe = float("nan")
            # --- B-DOCK docking / rendezvous / transfer telemetry (opt-in, section
            # 5.2). All default fail-closed; only read when self._read_docking, so
            # the B1/B2/B4/B5/B7 snapshot is byte-identical. Each read is in its
            # own try/except: a docking-surface read must degrade to the fail-
            # closed sentinel, NEVER count toward the vessel-lost read-fail streak.
            target_distance = float("nan")
            target_rel_speed = float("nan")
            docking_state = ""
            target_set = False
            mj_rv_enabled = False
            mj_dock_enabled = False
            vessel_count = 0
            transfer_complete = False
            transfer_amount = float("nan")
            monopropellant = float("nan")
            angular_velocity = float("nan")
            sas_enabled = False
            rcs_enabled = False
            docking_ap_status = ""
            if self._read_docking:
                target_distance, target_rel_speed, target_set = \
                    self._read_target_controller()
                mj_rv_enabled, mj_dock_enabled = self._read_docking_ap_enabled()
                docking_state = self._read_docking_state(v)
                try:
                    vessel_count = len(sc.vessels)
                except Exception:
                    vessel_count = 0
                transfer_complete, transfer_amount = self._read_active_transfer()
                try:
                    monopropellant = float(resources.amount("MonoPropellant"))
                except Exception:
                    monopropellant = float("nan")
                # Prox-ops observability (flight-10): the tumble signal + control
                # + AP-status reads, each fail-closed in its own try/except so a
                # fault degrades to the sentinel and never trips the vessel-lost
                # read-fail streak.
                angular_velocity = self._read_angular_velocity(v)
                try:
                    sas_enabled = bool(control_handle.sas)
                except Exception:
                    sas_enabled = False
                try:
                    rcs_enabled = bool(control_handle.rcs)
                except Exception:
                    rcs_enabled = False
                docking_ap_status = self._read_docking_ap_status()
            # Crew count (opt-in, FORGE-LKO). Own try/except with the -1 unread
            # sentinel: a crew read fault must degrade to fail-closed, NEVER
            # count toward the vessel-lost read-fail streak.
            crew_count = -1
            if self._read_crew:
                try:
                    crew_count = int(v.crew_count)
                except Exception:
                    crew_count = -1
            # Parachute state (opt-in, EVA-4). Own try/except with the "" unread
            # sentinel: a chute read fault must degrade to fail-closed (the EVA window
            # cannot open on a blind frame), NEVER count toward the vessel-lost
            # read-fail streak.
            craft_chute_state = ""
            if self._read_chute:
                craft_chute_state = self._read_craft_chute_state(v)
            snapshot = mlib.TelemetrySnapshot(
                ut=float(sc.ut),
                altitude=float(flight_srf.surface_altitude),
                vertical_speed=float(flight_srf.vertical_speed),
                apoapsis=float(orbit.apoapsis_altitude),
                periapsis=float(orbit.periapsis_altitude),
                eccentricity=float(orbit.eccentricity),
                inclination=math.degrees(float(orbit.inclination)),
                situation=_situation_name(v.situation),
                # PENDING-OPERATOR / v1 LIMITATION: this reads the VESSEL-TOTAL SolidFuel,
                # NOT the active-stage total. It is correct for the v1 B1 fixture (a
                # single-SRB pad-hop, where vessel-total == active-stage SolidFuel), and
                # the B1 "ASCENT -> COAST when solid fuel exhausted" transition relies on
                # that. A MULTI-SRB or asparagus-staged craft would keep vessel-total
                # SolidFuel > 0 while an earlier SRB stage is spent, so this would NOT
                # detect the per-stage burnout -- reading the active decouple stage's own
                # SolidFuel (v.resources_in_decouple_stage / a stage-scoped query) is the
                # deferred fix when a multi-SRB fixture lands.
                stage_solid_fuel=float(v.resources.amount("SolidFuel")),
                mj_autopilot_enabled=mj_enabled,
                mj_ascent_complete=mj_complete,
                ap_error=ap_error,
                warp_mode=warp_mode,
                warp_rate=warp_rate,
                # SOI body + pending-node evidence (B5 cross-SOI gates + the
                # PLAN/BURN transitions) + the first node's remaining dv (the
                # DIY correction burner's cut/overshoot gates). Direct reads: a
                # failure re-raises into the surrounding try/except and counts
                # toward the vessel-lost read-fail streak (the fly loop
                # tolerates non-transport raises). The machine-side guards for
                # an EMPTY body ("" = no reading) and a NaN node_dv fail closed.
                body=str(body.name),
                node_count=len(nodes),
                node_dv=(float(nodes[0].remaining_delta_v) if nodes else float("nan")),
                liquid_fuel=float(resources.amount("LiquidFuel")),
                electric_charge=float(resources.amount("ElectricCharge")),
                throttle=float(control_handle.throttle),
                # Flameout-staging evidence (twenty-second flight): total
                # thrust the ACTIVE engines can produce right now; 0.0 while
                # a burn is commanded = the active stage is dry/flamed out
                # (the machine pops the next stage, bounded).
                available_thrust=float(v.available_thrust),
                # Warp-toward-node + SOI-approach warp bounds (operator
                # directive 2026-07-22). Node.UT is the burn instant of the
                # first pending node (NaN with no node: the machine's
                # warp-toward-node stair fails closed to 1x).
                # Orbit.TimeToSOIChange is NaN when no SOI change is on the
                # trajectory (pinned kRPC 0.5.4 Orbit.cs: UTsoi - UT, negative
                # -> NaN), which the machine's SOI bound skips (fail open).
                node_ut=(float(nodes[0].ut) if nodes else float("nan")),
                time_to_soi=float(orbit.time_to_soi_change),
                next_body=next_body,
                next_pe=next_pe,
                # Native warp state: target UT while the WarpService warp is
                # active, NaN when idle (the machine's do-not-touch-rails
                # gate + self-healing re-issue read this).
                warping_to=(self._warp.target_ut if self._warp is not None
                            else float("nan")),
                # B-DOCK docking / rendezvous / transfer + the mid-mission seam
                # commit result (all fail-closed defaults when not read_docking).
                target_distance=target_distance,
                target_rel_speed=target_rel_speed,
                docking_state=docking_state,
                target_set=target_set,
                mj_rendezvous_enabled=mj_rv_enabled,
                mj_docking_enabled=mj_dock_enabled,
                vessel_count=vessel_count,
                transfer_complete=transfer_complete,
                transfer_amount=transfer_amount,
                monopropellant=monopropellant,
                craft_chute_state=craft_chute_state,
                seam_commit_result=self._seam_commit_result,
                # Prox-ops observability (flight-10): tumble / control / AP-status.
                angular_velocity=angular_velocity,
                sas_enabled=sas_enabled,
                rcs_enabled=rcs_enabled,
                docking_ap_status=docking_ap_status,
                # Crew aboard (-1 = not read / read failed; fails every crew gate
                # closed).
                crew_count=crew_count,
            )
            self._read_fail_streak = 0
            self._warp_watchdog(sc, snapshot.ut)
            return snapshot
        except Exception:
            self._read_fail_streak += 1
            if self._read_fail_streak < READ_FAIL_STREAK_LIMIT:
                # A one-off / transient read error: preserve the existing behaviour
                # (the run_mission handler classifies a post-connect kRPC/socket drop
                # as a flake, an internal bug as an error).
                raise
            # Sustained streak: the active vessel is gone. Emit a vessel_lost snapshot
            # (best-effort UT so the phase-entry clock still advances) instead of
            # re-raising, so the phase machine reaches its vessel-lost terminal.
            ut = 0.0
            try:
                ut = float(self._conn.space_center.ut)
            except Exception:
                ut = 0.0
            _stdout_sink(mlib.format_mission_log_line(
                "Warn", "Telemetry",
                "vessel-lost: telemetry read failed %d consecutive samples; "
                "emitting vessel_lost snapshot ut=%s"
                % (self._read_fail_streak, _fmt(ut))))
            return mlib.TelemetrySnapshot(ut=ut, vessel_lost=True)

    def perform(self, action: "mlib.Action") -> None:
        sc = self._conn.space_center
        v = sc.active_vessel
        control = v.control
        kind = action.kind
        if kind == mlib.ACTION_SET_THROTTLE:
            control.throttle = float(action.value if action.value is not None else 0.0)
            # Immediate readback (thirteenth flight diagnosability): a set that
            # does not stick means another controller (MechJeb thrust module)
            # is zeroing the throttle every frame -- name it loudly.
            try:
                back = float(control.throttle)
                if abs(back - float(action.value or 0.0)) > 0.01:
                    _stdout_sink(mlib.format_mission_log_line(
                        "Warn", "Throttle",
                        "set_throttle %.2f did not stick (readback %.2f): another "
                        "controller holds the throttle" % (float(action.value or 0.0), back)))
            except Exception:
                pass
        elif kind == mlib.ACTION_CUT_THROTTLE:
            control.throttle = 0.0
        elif kind == mlib.ACTION_ACTIVATE_STAGE:
            control.activate_next_stage()
        elif kind == mlib.ACTION_DEPLOY_CHUTE:
            # Fire every deployable parachute (stock action group not assumed).
            for p in v.parts.parachutes:
                try:
                    p.deploy()
                except Exception:
                    pass
        elif kind == mlib.ACTION_SET_CHUTE_DEPLOY_ALTITUDE:
            # Raise the stock full-deploy altitude on every parachute (kRPC
            # Parachute.DeployAltitude, the stock PAW tweakable). EVA-4 does this in the
            # same frame it arms, so the module's first ACTIVE FixedUpdate already sees
            # the raised value. Per-part try/except: a non-stock chute (RealChute) throws
            # on the setter and must not abort the remaining parts or the frame.
            target_alt = float(action.value)
            set_count = 0
            for p in v.parts.parachutes:
                try:
                    p.deploy_altitude = target_alt
                    set_count += 1
                except Exception:
                    continue
            _stdout_sink(mlib.format_mission_log_line(
                "Info", "Chute",
                "set deploy_altitude=%.0fm on %d parachute(s)" % (target_alt, set_count)))
        elif kind == mlib.ACTION_MJ_SET_TARGET_APOAPSIS:
            self._ascent.desired_orbit_altitude = float(action.value)
        elif kind == mlib.ACTION_MJ_ENABLE_AUTOSTAGE:
            self._ascent.autostage = True
        elif kind == mlib.ACTION_MJ_ENGAGE_ASCENT:
            self._ascent.enabled = True
        elif kind == mlib.ACTION_MJ_EXECUTE_CIRCULARIZATION:
            # Execute the circularization node IF the autopilot left one.
            # MechJeb's AscentAutopilot usually performs the circularization
            # burn itself before self-disabling, so an empty node list here is
            # the NORMAL completed case - and ExecuteAllNodes on an empty list
            # is a server-side RPCError (first live B2 run 2026-07-20).
            if len(control.nodes) > 0:
                self._mechjeb.node_executor.execute_all_nodes()
        elif kind == mlib.ACTION_AP_POINT_RETROGRADE:
            # kRPC's NATIVE AutoPilot (NOT MechJeb SmartASS): point orbital
            # retrograde. Surface verified against the installed krpc 0.5.4
            # python client source (harness/missions/.venv): Vessel.auto_pilot,
            # AutoPilot.reference_frame / target_direction setters, engage();
            # Vessel.orbital_reference_frame's y-axis is the orbital PROGRADE
            # direction, so retrograde is (0, -1, 0) in that frame. Live
            # behavior proof rides the first B4 operator run.
            # Defensive: if the (abnormal) circularize path left MechJeb's node
            # executor running, abort it first - two autopilots fighting over
            # attitude wedges the flip until the deorbit budget flakes (Fable
            # review of PR #1335, NIT).
            if self._mechjeb is not None:
                try:
                    self._mechjeb.node_executor.abort()
                except Exception:
                    pass
            ap = v.auto_pilot
            ap.reference_frame = v.orbital_reference_frame
            ap.target_direction = (0.0, -1.0, 0.0)
            # Heavy-stack tuning: the AP's default 5s deceleration time is
            # tuned for agile craft and limit-cycles on a low-torque stack
            # like the post-circularization Kerbal X (pod reaction wheel
            # turning the whole orbiter), overshooting the target repeatedly.
            # A longer deceleration window makes the approach critically
            # damped at the cost of a slower (but converging) flip.
            try:
                ap.deceleration_time = (15.0, 15.0, 15.0)
            except Exception:
                pass  # tuning is best-effort; the attitude gate is the safety
            ap.engage()
        elif kind == mlib.ACTION_AP_DISENGAGE:
            v.auto_pilot.disengage()
        elif kind == mlib.ACTION_WARP_TO:
            # Blocking RAILS warp to the machine-chosen target UT (bounded hops;
            # mlib emits now + warpHopSeconds per decision frame). Blocking is
            # acceptable: the fly loop's poll gap simply widens across the call,
            # and the B4 phase budgets are game-time so the warped span is
            # budgeted correctly. The warp guard permits RAILS via the spec's
            # allow_rails_warp.
            sc.warp_to(float(action.value))
        elif kind == mlib.ACTION_SET_TARGET_BODY:
            # B5: target the transfer body (the ManeuverPlanner's OperationTransfer
            # is a Hohmann-to-target and needs it). Surface verified against the
            # pinned kRPC 0.5.4 source (SpaceCenter.TargetBody settable property,
            # SpaceCenter.Bodies name dict).
            sc.target_body = sc.bodies[str(action.text)]
        elif kind == mlib.ACTION_MJ_PLAN_TRANSFER:
            # KRPC.MechJeb 0.8.1: maneuver_planner.operation_transfer.make_nodes()
            # (a Hohmann transfer to the current target). INTERCEPT-ONLY BUT
            # TARGETED -- semantics verified against the DECOMPILED MechJeb
            # 2.15.1 OperationGeneric (2026-07-21):
            #   capture=False      -> the GUI's "intercept only" checkbox
            #                         (Capture = !intercept_only); a single TLI
            #                         node, no arrival/insertion second node
            #                         (which the first live flight showed parks
            #                         the flyby machine).
            #   plan_capture=False -> belt+braces (only read when Capture).
            #   rendezvous=True    -> the TARGETED-INTERCEPT flag: it flows into
            #                         DeltaVAndTimeForHohmannTransfer as the
            #                         "arrive AT the target" mode. False is the
            #                         GUI's phase-blind "Transfer" (reach the
            #                         target's ALTITUDE at arbitrary phase) --
            #                         forcing it False on the third live flight
            #                         produced a deterministic no-encounter
            #                         coast (ap 11.4M, fell back to Kerbin) with
            #                         a persistent ~403 m/s re-aim demand.
            # A plan with no valid window/target throws a server-side
            # OperationException: log + swallow, node_count stays 0, and the
            # machine's bounded re-plan cadence owns the retry (re-issuing is
            # safe ONLY while no node exists).
            try:
                op = self._mechjeb.maneuver_planner.operation_transfer
                op.capture = False
                op.plan_capture = False
                op.rendezvous = True
                op.make_nodes()
            except Exception as exc:
                _stdout_sink(mlib.format_mission_log_line(
                    "Warn", "Plan", "operation_transfer.make_nodes failed: %s" % (exc,)))
        elif kind == mlib.ACTION_MJ_PLAN_INTERPLANETARY_TRANSFER:
            # KRPC.MechJeb 0.8.1 maneuver_planner.operation_interplanetary_transfer
            # (pinned source ManeuverPlanner.cs:79 OperationInterplanetaryTransfer
            # KRPCProperty -> MuMech.OperationInterplanetaryTransfer; the only
            # surface is WaitForPhaseAngle, Maneuver/OperationInterplanetaryTransfer.cs).
            # WaitForPhaseAngle=True plans the ejection node at the next transfer
            # window (up to ~1 synodic ahead). Same throw/log/swallow contract as
            # operation_transfer: a no-window plan throws server-side, node_count
            # stays 0, and the machine's bounded re-plan owns the retry.
            try:
                op = self._mechjeb.maneuver_planner.operation_interplanetary_transfer
                op.wait_for_phase_angle = True
                op.make_nodes()
            except Exception as exc:
                _stdout_sink(mlib.format_mission_log_line(
                    "Warn", "Plan",
                    "operation_interplanetary_transfer.make_nodes failed: %s" % (exc,)))
        elif kind == mlib.ACTION_MJ_PLAN_COURSE_CORRECT:
            # KRPC.MechJeb 0.8.1: course-correct the existing target encounter to
            # the machine-chosen flyby periapsis (metres). Same throw/log/swallow
            # contract as the transfer plan; MechJeb transiently sees no encounter
            # right after a burn and the machine re-plans on its bounded cadence.
            # DV CAP (action.limit, m/s): a genuine course correction is a SMALL
            # tweak. The second live flight (2026-07-21) had MechJeb plan an
            # oversized "correction" (executing it re-shaped the transfer,
            # ap 11.4M -> 16.6M, and wedged the executor until the burn budget
            # flaked). An over-cap plan's nodes are removed here, so the machine
            # sees node_count stay 0 and PLAN-CORRECTION's bounded fall-through
            # coasts on the raw Hohmann intercept instead.
            try:
                op = self._mechjeb.maneuver_planner.operation_course_correction
                op.course_correct_final_pe_a = float(action.value)
                nodes = op.make_nodes()
                if nodes:
                    total_dv = 0.0
                    for n in nodes:
                        total_dv += abs(float(n.delta_v))
                    cap = float(action.limit) if action.limit else 0.0
                    # PURE decider (review SF-3): thresholds live in
                    # mlib.classify_correction_plan (unit-tested); this block
                    # only performs the verdict. NaN dv classifies over_cap
                    # (fail closed: an unquantifiable plan never flies).
                    verdict = mlib.classify_correction_plan(
                        total_dv, cap, NEGLIGIBLE_CORRECTION_DV)
                    if verdict == mlib.PLAN_OVER_CAP:
                        v.control.remove_nodes()
                        _stdout_sink(mlib.format_mission_log_line(
                            "Warn", "Plan",
                            "course-correction dv %.1f m/s exceeds cap %.1f; "
                            "plan removed (correction disqualified, coast will "
                            "fly the raw intercept)" % (total_dv, cap)))
                    elif verdict == mlib.PLAN_NEGLIGIBLE:
                        # A node smaller than the executor's own 1.0 m/s
                        # tolerance never engages (sixth live flight: execute
                        # issued, no warp, no burn); a sub-0.5 m/s residual is
                        # also within the flyby floor's margin. Skip the round.
                        v.control.remove_nodes()
                        _stdout_sink(mlib.format_mission_log_line(
                            "Info", "Plan",
                            "course-correction dv %.2f m/s is negligible "
                            "(< %.1f); plan removed (trajectory already good)"
                            % (total_dv, NEGLIGIBLE_CORRECTION_DV)))
                    else:
                        # classify=fly (design-live-observability 2b): the
                        # ACCEPTED verdict was the only silent disposition --
                        # log it so every classify outcome is a sparse event.
                        _stdout_sink(mlib.format_mission_log_line(
                            "Info", "Plan",
                            "course-correction dv %.2f m/s classified fly "
                            "(cap %.1f, floor %.1f); plan accepted"
                            % (total_dv, cap, NEGLIGIBLE_CORRECTION_DV)))
            except Exception as exc:
                _stdout_sink(mlib.format_mission_log_line(
                    "Warn", "Plan",
                    "operation_course_correction.make_nodes failed: %s" % (exc,)))
        elif kind == mlib.ACTION_MJ_EXECUTE_NODES:
            # Hand the planned node(s) to MechJeb's NodeExecutor with autowarp:
            # it rails-warps to each node and burns it (the warp guard permits
            # RAILS via allow_rails_warp). Guarded on node count like the B2
            # circularize case: ExecuteAllNodes on an empty list is a server-side
            # RPCError (first live B2 run 2026-07-20).
            # NOTE: no tolerance override -- the KRPC.MechJeb 0.8.1 wrapper
            # never initializes its Tolerance backing object (InitInstance
            # sets only leadTime, verified in the pinned source), so setting
            # it throws server-side and the executor keeps its 0.1 default.
            # TLI-scale nodes execute fine on the defaults (the far-node
            # autowarp branch carries them); corrections use the DIY burner.
            if len(control.nodes) > 0:
                ne = self._mechjeb.node_executor
                ne.autowarp = True
                ne.execute_all_nodes()
        elif kind == mlib.ACTION_SET_RAILS_WARP:
            # Non-blocking rails warp (operator design critique: warp changes
            # only when an action is imminent -- no more per-hop ramp sawtooth,
            # no blocking RPC to wedge under a dialog). The server clamps the
            # factor to the altitude-legal maximum; 0 = 1x. The machine now
            # pre-clamps via mlib.max_legal_rails_factor so the commanded
            # factor is achievable and escalates as the vessel climbs (KSP
            # never auto-raises a clamped rate).
            sc.rails_warp_factor = int(action.value)
        elif kind == mlib.ACTION_SET_PHYSICS_WARP:
            # Non-blocking PHYSICS warp (the correction-burn attitude flip
            # runs at 2x; the machine always drops to 0 before throttle-up).
            # kRPC clamps to [0, 3] (pinned 0.5.4 SpaceCenter.cs
            # PhysicsWarpFactor setter); precedent for flipping under mild
            # physics warp is MechJeb's own WarpToUT 2.0x physics cap
            # (decompiled 2.15.1 MechJebModuleWarpController).
            sc.physics_warp_factor = int(action.value)
        elif kind == mlib.ACTION_WARP_TO_UT:
            # Native fire-and-forget warp (Path A): the WarpService's
            # dedicated second connection blocks inside SpaceCenter.WarpTo on
            # a daemon thread; this primary connection keeps polling. An
            # active warp is cancelled + re-issued (retarget contract).
            self._ensure_warp_service().warp_to_ut(float(action.value), sc)
            self._warp_stall.reset()
        elif kind == mlib.ACTION_CANCEL_WARP:
            # Hard-cancel the native warp: drop the warp socket (server
            # discards the continuation next FixedUpdate) + zero both warp
            # factors from THIS primary connection. Idempotent when idle.
            if self._warp is not None:
                self._warp.cancel(sc)
            self._warp_stall.reset()
        elif kind == mlib.ACTION_AP_POINT_NODE:
            # DIY correction burner (live finding 8): point kRPC's NATIVE
            # AutoPilot along the first node's burn vector. Node.ReferenceFrame's
            # y-axis IS the burn vector (pinned kRPC source); same heavy-stack
            # deceleration tuning as the B4 retro flip.
            # (perform's OWN control local, NOT read_snapshot's cached
            # control_handle -- that local lives in a different method; the
            # twentieth flight died on exactly that NameError at
            # CORRECTION-BURN entry.)
            nodes = control.nodes
            if len(nodes) > 0:
                # Release MechJeb's throttle hold FIRST (eleventh live flight,
                # via the new thr= readback: after the TLI executor runs,
                # set_throttle commands read back 0.000 -- MechJeb's thrust
                # controller keeps zeroing the throttle until the executor's
                # full abort teardown runs). This mirrors B4's proven
                # AP_POINT_RETROGRADE pattern, whose SET_THROTTLE always
                # worked because it aborts the executor before engaging the
                # native AP. Executor re-use poisoning is moot: corrections
                # never touch the executor again.
                if self._mechjeb is not None:
                    # LOUD abort (thirteenth flight: corrections still zero-
                    # thrust + 0.07 deg/s crawl -- something retains vessel
                    # control; a silently-failed abort would explain it, so
                    # log the outcome + the executor's enabled state).
                    try:
                        self._mechjeb.node_executor.abort()
                        _stdout_sink(mlib.format_mission_log_line(
                            "Info", "Point", "executor abort ok; enabled=%s"
                            % (self._mechjeb.node_executor.enabled,)))
                    except Exception as exc:
                        _stdout_sink(mlib.format_mission_log_line(
                            "Warn", "Point", "executor abort FAILED: %s" % (exc,)))
                # SAS/RCS off before engaging the kRPC AP (standard practice;
                # a stock SAS hold left on by MechJeb's teardown would cancel
                # most of the wheel torque = exactly the measured 0.07 deg/s
                # crawl that survived every AP-tuning change).
                try:
                    v.control.sas = False
                    v.control.rcs = False
                except Exception:
                    pass
                # SmartASS OFF: force-release MechJeb's ATTITUDE controller
                # (the crawl + throttle hold survived the executor abort AND
                # sas=False on the fourteenth flight -- MechJeb's attitude
                # module is the remaining candidate holder, and Smart A.S.S.
                # Off is its exposed release lever; pinned-source verified
                # SmartASSAutopilotMode.Off + Update(false)).
                try:
                    sa = self._mechjeb.smart_ass
                    sa.autopilot_mode = self._mechjeb.SmartASSAutopilotMode.off
                    sa.update(False)
                    _stdout_sink(mlib.format_mission_log_line(
                        "Info", "Point", "smart-ass forced OFF"))
                except Exception as exc:
                    _stdout_sink(mlib.format_mission_log_line(
                        "Warn", "Point", "smart-ass OFF failed: %s" % (exc,)))
                ap = v.auto_pilot
                ap.reference_frame = nodes[0].reference_frame
                ap.target_direction = (0.0, 1.0, 0.0)
                # deceleration_time (15,15,15), the B4-PROVEN tuning. Finding
                # 9(c) had this BACKWARDS: on a low-torque craft this window
                # RAISES the angular-velocity cap (the AP permits spin it can
                # stop within the window; omega_max ~ alpha * window). With
                # the override removed the twelfth flight crawled at
                # ~0.05 deg/s under kRPC's default ~0.5 s stopping profile;
                # B4's measured 0.5 deg/s flip runs (15,15,15). Operator
                # observed the 1x multi-hour coast live and flagged it.
                try:
                    ap.deceleration_time = (15.0, 15.0, 15.0)
                except Exception:
                    pass  # best-effort; the give-up bound owns a slow flip
                ap.engage()
        elif kind == mlib.ACTION_MJ_ABORT_AND_CLEAR_NODES:
            # B5 burn-exit cleanup: remove every remaining node, so the coast
            # hops are not suppressed by node_count > 0 and no unwanted burn
            # ever flies. Deliberately NO node_executor.abort() call: MechJeb's
            # executor aborts ITSELF on the next physics frame once the node
            # list is empty (decompiled 2.15.1 OnFixedUpdate: !_hasNodes ->
            # Abort()), and an EXTERNAL Abort() is the prime suspect for the
            # poisoned re-engage observed on flights 6-7 (every fresh
            # ExecuteAllNodes works; every post-external-abort one never
            # starts). Letting MechJeb run its own abort path keeps its
            # attitude/thrust user bookkeeping consistent.
            v.control.remove_nodes()
        # --- B-DOCK actions (design section 5.1). Handle-based selection (P9):
        # never name/pid; the runner resolves each intent against a captured kRPC
        # object handle. All best-effort + try/excepted where a live surface may
        # be absent; the machine's bounded give-ups own a non-advancing outcome.
        elif kind == mlib.ACTION_LAUNCH_VESSEL:
            # kRPC v0.5.4 SpaceCenter.launch_vessel(craft_directory, name,
            # launch_site, recover=True, crew=None, ...): resolves
            # <save>/Ships/VAB/<name>.craft, recovers any vessel already on the
            # pad, then StartWithNewLaunch (a FLIGHT->FLIGHT reload focused on the
            # new craft). crew MUST be passed as an explicit EMPTY LIST by KEYWORD:
            # the installed 0.5.4 client stub orders recover BEFORE crew (a
            # positional list lands on recover: "argument 3 must be bool"), and its
            # crew=None default fails protobuf coercion (None -> List(string)
            # ValueError) before the RPC is even sent - both caught by the first
            # FORGE-bdock-station live runs. The server doc contract is "Pass an
            # empty list to use default crew assignments" (pinned source
            # SpaceCenter.cs LaunchVessel), so [] = default manifest, controllable
            # pod. Named crew seeding (the EVA-3 3-crew pod) is threaded here via
            # action.crew, a tuple of KERBAL NAMES: crew=[names] launches the pod
            # with exactly those kerbals aboard, crew=[] keeps the default
            # manifest. By NAME, never a count -- kRPC 0.5.4 exposes no
            # roster-enumeration API (only get_kerbal(name) + launch_vessel(crew:
            # List[str])), so a count could not be resolved to names server-side.
            # launch_site is likewise threaded from action.launch_site (None ->
            # "LaunchPad"). Re-resolve the MechJeb handles against the NEW active
            # vessel and reset the ascent-complete latch + read-fail streak so the
            # fresh craft is judged from its own engage.
            launch_site = getattr(action, "launch_site", None) or "LaunchPad"
            crew_names = getattr(action, "crew", None)
            crew_arg = [str(n) for n in crew_names] if crew_names else []
            sc.launch_vessel("VAB", str(action.text), str(launch_site),
                             crew=crew_arg)
            self._mj_ever_enabled = False
            self._read_fail_streak = 0
            if self._use_mechjeb:
                try:
                    self._mechjeb = self._conn.mech_jeb
                    self._ascent = self._mechjeb.ascent_autopilot
                except Exception as exc:
                    _stdout_sink(mlib.format_mission_log_line(
                        "Warn", "Launch",
                        "MechJeb re-resolve after launch failed: %s" % (exc,)))
        elif kind == mlib.ACTION_CAPTURE_STATION:
            # Capture the Station handle (P9/Q4) while it IS the active vessel: the
            # vessel, its top docking port, and its per-resource station-side tanks.
            # ANSWER to P9 (flight-13): the VESSEL handle survives the later
            # launch_vessel FLIGHT reload (kRPC keys it by the vessel id, stable
            # on-rails), but the PART handles (port + tanks) do NOT -- the reload
            # destroys/recreates every Part object, so a captured Part proxy resolves
            # to a destroyed part server-side. The port + tanks are therefore
            # captured only as LAST-RESORT fallbacks; SET_TARGET_DOCKING_PORT,
            # _read_docking_state, and the transfer all RE-RESOLVE the parts LIVE.
            self._station_vessel = v
            try:
                ports = v.parts.docking_ports
                self._station_port = ports[0] if ports else None
            except Exception:
                self._station_port = None
            self._station_tanks = {}
            for res in ("LiquidFuel", "MonoPropellant"):
                part = self._find_tank_with_resource(v, res)
                if part is not None:
                    self._station_tanks[res] = part
            _stdout_sink(mlib.format_mission_log_line(
                "Info", "Capture",
                "captured station handle port=%s tanks=%s"
                % (self._station_port is not None, sorted(self._station_tanks.keys()))))
        elif kind == mlib.ACTION_PARSEK_COMMIT_TREE:
            self._perform_seam_commit()
        elif kind == mlib.ACTION_SET_TARGET_VESSEL:
            if self._station_vessel is not None:
                sc.target_vessel = self._station_vessel
            else:
                _stdout_sink(mlib.format_mission_log_line(
                    "Warn", "Target", "set_target_vessel: no captured station handle"))
        elif kind == mlib.ACTION_SET_TARGET_DOCKING_PORT:
            self._set_target_docking_port_live(sc)
        elif kind == mlib.ACTION_MJ_ENABLE_RENDEZVOUS:
            rv = self._mechjeb.rendezvous_autopilot
            if action.value is not None:
                rv.desired_distance = float(action.value)
            if action.limit is not None:
                rv.max_phasing_orbits = float(action.limit)
            # Force node-executor autowarp ON at rendezvous enable (flight 12):
            # the rendezvous AP consults the shared executor's autowarp for its
            # between-burn / phasing-wait warping, and we only ever set it
            # inside OTHER actions - so whether a rendezvous warped was luck.
            # Flight 12 ran its entire phasing wait at 1x (game ut == wall
            # second-for-second, warpMode NONE) and ate the whole mission
            # budget; flight 11 with identical machine code happened to warp.
            try:
                self._mechjeb.node_executor.autowarp = True
            except Exception as exc:
                _stdout_sink(mlib.format_mission_log_line(
                    "Warn", "Rendezvous", "autowarp set failed: %s" % (exc,)))
            rv.enabled = True
        elif kind == mlib.ACTION_MJ_KILL_REL_VEL:
            # Belt+braces to the rendezvous AP's own terminal match: plan +
            # execute a kill-relative-velocity node. Throw/log/swallow (a
            # no-target plan throws server-side; the machine's match-velocity gate
            # owns the outcome).
            #
            # Flight 5 sat ~4300 s in MATCH-VELOCITY: operation_kill_rel_vel is a
            # TimedOperation whose DEFAULT time selector is closest approach, which
            # -- after the rendezvous AP has already delivered us to ~approach
            # distance -- can land the node nearly a full orbit ahead. (a) clear
            # any stale node first (a re-issue must not stack nodes), then (b) point
            # the op at XFromNow + ~15 s lead so the burn is imminent.
            try:
                control.remove_nodes()
            except Exception as exc:
                _stdout_sink(mlib.format_mission_log_line(
                    "Warn", "Match",
                    "remove_nodes before kill-rel-vel failed: %s" % (exc,)))
            try:
                op = self._mechjeb.maneuver_planner.operation_kill_rel_vel
                # The KRPC.MechJeb service generates snake_case python attributes
                # from the C# KRPCProperty names: TimedOperation.TimeSelector ->
                # op.time_selector; TimeSelector.TimeReference / .LeadTime ->
                # .time_reference / .lead_time; the MechJeb-service enum value
                # XFromNow -> mech_jeb.TimeReference.x_from_now. Defensive: fall
                # back to the default selector on any AttributeError (an API-shape
                # drift must not stop the node from planning).
                try:
                    ts = op.time_selector
                    ts.time_reference = self._mechjeb.TimeReference.x_from_now
                    ts.lead_time = 15.0
                except Exception as exc:
                    _stdout_sink(mlib.format_mission_log_line(
                        "Warn", "Match",
                        "kill-rel-vel time-selector default kept (retarget failed: %s)"
                        % (exc,)))
                op.make_nodes()
                if len(control.nodes) > 0:
                    ne = self._mechjeb.node_executor
                    ne.autowarp = True
                    ne.execute_all_nodes()
            except Exception as exc:
                _stdout_sink(mlib.format_mission_log_line(
                    "Warn", "Match", "operation_kill_rel_vel failed: %s" % (exc,)))
        elif kind == mlib.ACTION_MJ_ENABLE_DOCKING:
            dk = self._mechjeb.docking_autopilot
            if action.value is not None:
                dk.speed_limit = float(action.value)
            dk.enabled = True
        elif kind == mlib.ACTION_MJ_DISABLE_DOCKING:
            try:
                self._mechjeb.docking_autopilot.enabled = False
            except Exception as exc:
                _stdout_sink(mlib.format_mission_log_line(
                    "Warn", "Dock", "disable docking AP failed: %s" % (exc,)))
        elif kind == mlib.ACTION_MJ_ABORT_NODE_EXEC:
            # Flight-8 prox-ops rule: no pending maneuver execution may survive
            # into terminal approach. MATCH-VELOCITY can complete in ~0.5 s (rel-
            # speed already under the floor) with the kill-rel-vel node still
            # PENDING in the executor with autowarp=True; that node then rails-
            # warps to ~92 m, packing clears the docking-port target, and the
            # docking AP NREs forever. Abort the executor + clear the nodes BEFORE
            # DOCK enables the docking AP. Best-effort (Warn-and-continue).
            try:
                self._mechjeb.node_executor.abort()
            except Exception as exc:
                _stdout_sink(mlib.format_mission_log_line(
                    "Warn", "Dock", "node_executor.abort() failed: %s" % (exc,)))
            try:
                control.remove_nodes()
            except Exception as exc:
                _stdout_sink(mlib.format_mission_log_line(
                    "Warn", "Dock", "remove_nodes at DOCK entry failed: %s" % (exc,)))
        elif kind == mlib.ACTION_SET_SAS:
            # Flight-10 tumble fix: hold attitude after a stage separation / before
            # the docking AP takes over. control.sas is the primary; sas_mode is a
            # separate best-effort (a craft with no stability-assist availability
            # must still get SAS on).
            try:
                control.sas = True
            except Exception as exc:
                _stdout_sink(mlib.format_mission_log_line(
                    "Warn", "Attitude", "set sas=True failed: %s" % (exc,)))
            try:
                control.sas_mode = sc.SASMode.stability_assist
            except Exception as exc:
                _stdout_sink(mlib.format_mission_log_line(
                    "Warn", "Attitude",
                    "set sas_mode=stability_assist failed: %s" % (exc,)))
        elif kind == mlib.ACTION_SET_RCS:
            on = bool(action.value) if action.value is not None else True
            try:
                control.rcs = on
            except Exception as exc:
                _stdout_sink(mlib.format_mission_log_line(
                    "Warn", "Attitude", "set rcs=%s failed: %s" % (on, exc)))
        elif kind == mlib.ACTION_START_RESOURCE_TRANSFER:
            self._start_resource_transfer(sc, v, action)
        elif kind == mlib.ACTION_UNDOCK:
            # Resolve the DOCKED port LIVE from the merged active vessel
            # (flight 15: the captured pre-reload handle answered "The docking
            # port is not docked" - the stale-part-handle family, same as the
            # flight-13 targeting fix). Post-dock the active vessel carries
            # BOTH mated ports; undock whichever reports docked, captured
            # handle as last resort.
            undocked = False
            try:
                for port in v.parts.docking_ports:
                    try:
                        st_name = getattr(port.state, "name", "") or ""
                        if str(st_name).lower() == "docked":
                            port.undock()
                            undocked = True
                            _stdout_sink(mlib.format_mission_log_line(
                                "Info", "Undock", "undocked live-resolved port"))
                            break
                    except Exception as exc:
                        _stdout_sink(mlib.format_mission_log_line(
                            "Warn", "Undock",
                            "live port undock candidate failed: %s" % (exc,)))
            except Exception as exc:
                _stdout_sink(mlib.format_mission_log_line(
                    "Warn", "Undock", "live port enumeration failed: %s" % (exc,)))
            if not undocked and self._station_port is not None:
                try:
                    self._station_port.undock()
                    _stdout_sink(mlib.format_mission_log_line(
                        "Info", "Undock", "undocked via captured handle"))
                except Exception as exc:
                    _stdout_sink(mlib.format_mission_log_line(
                        "Warn", "Undock", "port.undock() failed: %s" % (exc,)))
            elif not undocked and self._station_port is None:
                _stdout_sink(mlib.format_mission_log_line(
                    "Warn", "Undock", "undock: no docked port resolved"))
        else:
            raise ValueError("unknown action kind: %r" % (kind,))

    # ---- B-DOCK helpers (handle capture, seam bridge, docking telemetry) ----

    def configure_seam(self, commands_path: str, responses_path: str,
                       commit_id: str) -> None:
        """Wire the mid-mission command-seam bridge (route 1, section 3.2): the
        reserved command-id + the two channel file paths the
        ACTION_PARSEK_COMMIT_TREE case writes / polls."""
        self._seam_commands_path = str(commands_path) if commands_path else None
        self._seam_responses_path = str(responses_path) if responses_path else None
        self._seam_commit_id = str(commit_id) if commit_id else None
        _stdout_sink(mlib.format_mission_log_line(
            "Info", "Seam",
            "seam bridge configured commit-id=%s commands=%s responses=%s"
            % (self._seam_commit_id, self._seam_commands_path, self._seam_responses_path)))

    def _perform_seam_commit(self) -> None:
        """Route-1 mid-mission CommitTree: write a CommitTree command with the
        reserved id into the request channel, then bounded-poll the response
        channel for that id. The result rides TelemetrySnapshot.seam_commit_result
        ("OK" advances, "ERROR"/"TIMEOUT" flakes the machine). Never raises: a
        missing seam config / an IO fault / a poll expiry all resolve to a
        fail-closed result token the machine flakes on, never a MISSION-ERROR."""
        self._seam_commit_result = ""
        if not (self._seam_commands_path and self._seam_responses_path
                and self._seam_commit_id):
            _stdout_sink(mlib.format_mission_log_line(
                "Warn", "Seam", "commit requested but no seam configured -> ERROR"))
            self._seam_commit_result = "ERROR"
            return
        cid = self._seam_commit_id
        try:
            with open(self._seam_commands_path, "a", encoding="utf-8") as fh:
                fh.write("id=%s cmd=CommitTree\n" % cid)
        except OSError as exc:
            _stdout_sink(mlib.format_mission_log_line(
                "Warn", "Seam", "commit command write failed: %s -> ERROR" % (exc,)))
            self._seam_commit_result = "ERROR"
            return
        _stdout_sink(mlib.format_mission_log_line(
            "Info", "Seam", "commit command written id=%s cmd=CommitTree; polling" % cid))
        deadline = time.monotonic() + SEAM_COMMIT_POLL_SECONDS
        while time.monotonic() < deadline:
            verdict = self._read_seam_response(cid)
            if verdict is not None:
                self._seam_commit_result = "OK" if verdict == "OK" else "ERROR"
                _stdout_sink(mlib.format_mission_log_line(
                    "Info", "Seam", "commit response id=%s verdict=%s -> %s"
                    % (cid, verdict, self._seam_commit_result)))
                return
            time.sleep(SEAM_COMMIT_POLL_INTERVAL)
        self._seam_commit_result = "TIMEOUT"
        _stdout_sink(mlib.format_mission_log_line(
            "Warn", "Seam", "commit poll expired (%ds) id=%s -> TIMEOUT"
            % (int(SEAM_COMMIT_POLL_SECONDS), cid)))

    def _read_seam_response(self, commit_id: str) -> Optional[str]:
        """Return the verdict of the FIRST terminal response line for commit_id in
        the response channel (id/cmd/verdict key=value tokens), or None if none
        yet. First-wins mirrors the seam's crash-recovery rewrite contract."""
        try:
            with open(self._seam_responses_path, "r", encoding="utf-8",
                      errors="replace") as fh:
                lines = fh.readlines()
        except OSError:
            return None
        for line in lines:
            fields: Dict[str, str] = {}
            for tok in line.split():
                if "=" in tok:
                    k, _, val = tok.partition("=")
                    if k and k not in fields:
                        fields[k] = val
            if fields.get("id") == commit_id and "verdict" in fields:
                return fields["verdict"]
        return None

    def _find_tank_with_resource(self, vessel, resource: str):
        """A part on ``vessel`` carrying > 0 of ``resource`` (best-effort; the
        first match). Returns a kRPC Part handle or None. Per-part reads are
        try/excepted so one odd part never aborts the scan."""
        try:
            parts = vessel.parts.all
        except Exception:
            return None
        for part in parts:
            try:
                if part.resources.amount(resource) > 0.0:
                    return part
            except Exception:
                continue
        return None

    def _start_resource_transfer(self, sc, v, action: "mlib.Action") -> None:
        """Start a kRPC ResourceTransfer between the transport + station tanks
        (Q4 handle-based selection). The station-side tank is the handle captured
        at STATION-COMMIT; the transport-side tank is a merged-vessel part with the
        resource that is NOT that station tank. Direction rides action.limit
        (DELIVER = transport->station, PICKUP = station->transport). Best-effort:
        a failure logs a warn and leaves _active_transfer None, and the machine's
        transfer-stall give-up owns the non-advancing outcome."""
        resource = str(action.text)
        amount = float(action.value) if action.value is not None else 0.0
        direction = float(action.limit) if action.limit is not None else mlib.TRANSFER_DIR_DELIVER
        # Re-resolve BOTH tanks LIVE from the docked active vessel (flight-13: the
        # captured self._station_tanks are PRE-launch-reload handles, destroyed by
        # the reload exactly like the port handle, so the transfer would hit the
        # same stale-handle wall). Partition the merged part tree at the mated
        # docked-port pair; fall back to live-first-two, then the captured handle.
        station_tank, transport_tank, tank_path = self._resolve_transfer_tanks_live(
            v, resource)
        if station_tank is None or transport_tank is None:
            _stdout_sink(mlib.format_mission_log_line(
                "Warn", "Transfer",
                "transfer %s amt=%.1f: could not resolve tanks (station=%s transport=%s)"
                % (resource, amount, station_tank is not None, transport_tank is not None)))
            self._active_transfer = None
            return
        if direction == mlib.TRANSFER_DIR_DELIVER:
            from_part, to_part = transport_tank, station_tank
            label = "transport->station"
        else:
            from_part, to_part = station_tank, transport_tank
            label = "station->transport"
        try:
            # kRPC 0.5.4: ResourceTransfer.start is a CLASSMETHOD on the
            # generated service class (RPC ResourceTransfer_static_Start), NOT
            # an attribute of the SpaceCenter instance (flight 14:
            # "'SpaceCenter' object has no attribute 'resource_transfer'").
            # The class gets its _client stamped at connect, so the module
            # import path is callable after connection; prefer an
            # instance-attached class if this client version exposes one.
            rt_cls = getattr(sc, "ResourceTransfer", None)
            if rt_cls is None:
                from krpc.services.spacecenter import ResourceTransfer as rt_cls
            self._active_transfer = rt_cls.start(
                from_part, to_part, resource, amount)
            _stdout_sink(mlib.format_mission_log_line(
                "Info", "Transfer",
                "started %s amt=%.1f %s (tanks via %s)"
                % (resource, amount, label, tank_path)))
        except Exception as exc:
            _stdout_sink(mlib.format_mission_log_line(
                "Warn", "Transfer", "ResourceTransfer.start failed: %s" % (exc,)))
            self._active_transfer = None

    def _resolve_transfer_tanks_live(self, v, resource):
        """(station_tank, transport_tank, path) resolved LIVE from the docked active
        vessel (flight-13). Primary: partition the merged part tree at the mated
        docked-port pair; the TRANSPORT (Interceptor) side holds the active CONTROL
        part (it launched last + is the active/controlling craft), the STATION side
        is the other. Fallback: the first two distinct resource-bearing parts (side
        assignment then arbitrary). Last resort: the captured station tank + a live
        transport tank.

        TODO(flight-14): the station/transport SIDE assignment (which drives the
        recorded route-window delta SIGNS the offline oracle checks) is validated
        only by the partition heuristic here -- confirm it in a live run against the
        recorded deltas; if the oracle reds on flipped signs, key the sides off the
        captured station VESSEL guid / a per-side marker resource instead. The
        bounded TRANSFER-stall liveness watchdog covers a mis-resolved-tank stall in
        the meantime (a stalled transfer fast-flakes, never hangs)."""
        # Primary: docked-port partition.
        station_tank, transport_tank = self._partition_docked_tanks(v, resource)
        if station_tank is not None and transport_tank is not None:
            return station_tank, transport_tank, "partition"
        # Fallback: first two distinct live resource-bearing parts (arbitrary side).
        first = self._find_transport_tank(v, resource, None)
        second = self._find_transport_tank(v, resource, first)
        if first is not None and second is not None:
            return second, first, "live-first-two(TODO:sides-arbitrary)"
        # Last resort: the captured (possibly stale) station tank + a live transport.
        captured = self._station_tanks.get(resource)
        transport = self._find_transport_tank(v, resource, captured)
        return captured, transport, "captured-fallback(TODO:stale-handle)"

    def _partition_docked_tanks(self, v, resource):
        """Partition the docked active vessel at the mated docked-port pair and pick
        (station_tank, transport_tank) holding ``resource``. Transport = the side
        with the active control part. Returns (None, None) if it cannot resolve
        (the caller falls back). Every kRPC read is guarded."""
        try:
            docked = [p for p in v.parts.docking_ports
                      if str(getattr(p.state, "name", "")).strip().lower() == "docked"]
        except Exception:
            return None, None
        if not docked:
            return None, None
        port = docked[0]
        try:
            near = port.part            # this port's part (side A)
            across = port.docked_part   # the mated port's part (side B)
        except Exception:
            return None, None
        if near is None or across is None:
            return None, None
        side_a = self._collect_side_parts(near, across)
        if not side_a:
            return None, None
        try:
            all_parts = list(v.parts.all)
        except Exception:
            return None, None
        try:
            side_b = [p for p in all_parts if p not in side_a]
        except Exception:
            return None, None
        if not side_b:
            return None, None
        try:
            control_part = v.parts.controlling
        except Exception:
            control_part = None
        if control_part is not None and control_part in side_a:
            transport_side, station_side = list(side_a), side_b
        else:
            transport_side, station_side = side_b, list(side_a)
        station_tank = self._first_resource_part(station_side, resource)
        transport_tank = self._first_resource_part(transport_side, resource)
        if station_tank is None or transport_tank is None:
            return None, None
        return station_tank, transport_tank

    def _collect_side_parts(self, start, stop_at):
        """BFS the part tree from ``start`` over parent/children WITHOUT crossing the
        docked joint at ``stop_at`` (the mated port's part). Returns the set of parts
        on ``start``'s side. Every kRPC read guarded."""
        seen = []
        stack = [start]
        while stack:
            part = stack.pop()
            if part is None:
                continue
            try:
                if any(part == s for s in seen):
                    continue
                if stop_at is not None and part == stop_at:
                    continue
            except Exception:
                continue
            seen.append(part)
            neighbors = []
            try:
                neighbors.extend(list(part.children))
            except Exception:
                pass
            try:
                parent = part.parent
                if parent is not None:
                    neighbors.append(parent)
            except Exception:
                pass
            for n in neighbors:
                if n is None:
                    continue
                try:
                    if n == stop_at:
                        continue
                except Exception:
                    continue
                stack.append(n)
        return seen

    def _first_resource_part(self, parts, resource):
        """The first part in ``parts`` holding a positive amount of ``resource``, or
        None. Every kRPC read guarded."""
        for part in parts:
            try:
                if part.resources.amount(resource) > 0.0:
                    return part
            except Exception:
                continue
        return None

    def _find_transport_tank(self, vessel, resource: str, station_tank):
        """A merged-vessel part with ``resource`` that is NOT ``station_tank`` (a
        live first-distinct-resource-part finder used by the transfer fallbacks)."""
        try:
            parts = vessel.parts.all
        except Exception:
            return None
        for part in parts:
            try:
                if part.resources.amount(resource) <= 0.0:
                    continue
                if station_tank is not None and part == station_tank:
                    continue
                return part
            except Exception:
                continue
        return None

    def _read_target_controller(self) -> Tuple[float, float, bool]:
        """(target_distance, target_rel_speed, target_set) from MechJeb's target
        controller. Fail-closed on any read fault (NaN / NaN / False)."""
        try:
            tc = self._mechjeb.target_controller
        except Exception:
            return float("nan"), float("nan"), False
        target_set = False
        try:
            target_set = bool(tc.normal_target_exists)
        except Exception:
            target_set = False
        distance = float("nan")
        try:
            distance = float(tc.distance)
        except Exception:
            distance = float("nan")
        rel_speed = float("nan")
        try:
            rv = tc.relative_velocity
            rel_speed = math.sqrt(sum(float(c) * float(c) for c in rv))
        except Exception:
            rel_speed = float("nan")
        if not (math.isfinite(rel_speed) and math.isfinite(distance)):
            # Pinned-stack fallback (flights 6+7): KRPC.MechJeb 0.8.1's
            # RelativeVelocity casts MechJeb's value to (Vector3) server-side
            # (TargetController.cs:109) and the unbox throws against MechJeb
            # 2.15.1's Vector3d property, so that read NaNs on EVERY call;
            # and once the target is a docking PORT (the DOCK phase's
            # set_target_docking_port), tc.distance goes dark too (flight 7:
            # DOCK flew blind, both None). Compute both from kRPC core
            # instead: resolve the target vessel directly OR via the target
            # port's parent vessel, then rel-speed = |active velocity in the
            # target's orbital frame| and distance = |active position in the
            # target's frame| (the stock docking-tutorial approach, no
            # MechJeb surface involved).
            try:
                sc = self._conn.space_center
                tv = sc.target_vessel
                if tv is None:
                    tp = sc.target_docking_port
                    tv = tp.part.vessel if tp is not None else None
                if tv is not None:
                    av = sc.active_vessel
                    if not math.isfinite(rel_speed):
                        vel = av.velocity(tv.orbital_reference_frame)
                        rel_speed = math.sqrt(
                            sum(float(c) * float(c) for c in vel))
                    if not math.isfinite(distance):
                        pos = av.position(tv.reference_frame)
                        distance = math.sqrt(
                            sum(float(c) * float(c) for c in pos))
                    target_set = True
            except Exception:
                pass
        return distance, rel_speed, target_set

    def _read_docking_ap_enabled(self) -> Tuple[bool, bool]:
        """(rendezvous_enabled, docking_enabled) MechJeb AP Enabled latches.
        Fail-closed False on a read fault (the machine's latch never falsely
        flips)."""
        rv = False
        dk = False
        try:
            rv = bool(self._mechjeb.rendezvous_autopilot.enabled)
        except Exception:
            rv = False
        try:
            dk = bool(self._mechjeb.docking_autopilot.enabled)
        except Exception:
            dk = False
        return rv, dk

    def _set_target_docking_port_live(self, sc) -> None:
        """Set the target docking port, resolved LIVE from the rendezvous target
        vessel (flight-13 root cause of EVERY dock failure since flight 7):
        ACTION_SET_TARGET_DOCKING_PORT used to assign the captured self._station_port
        -- a handle captured at STATION-COMMIT, BEFORE the launch_vessel FLIGHT
        reload destroyed/recreated every Part object. The stale handle resolves
        server-side to a destroyed ModuleDockingNode; KSP's SetVesselTarget on a
        destroyed ITargetable silently CLEARS the target (tgtD went None right after
        the set in every flight), and MechJeb then refuses to engage the docking AP
        with no port target (the "enable never took" / benched-NRE family). The
        VESSEL target survived the reload all along, which masked this. Resolve the
        port from sc.target_vessel (alive) instead; keep the captured handle only as
        a last resort when a live vessel target exists; and if there is NO target
        vessel, leave the target alone rather than clobber it with a dead handle."""
        try:
            tv = sc.target_vessel
        except Exception:
            tv = None
        live_port = None
        path = "none"
        if tv is not None:
            try:
                ports = list(tv.parts.docking_ports)
            except Exception:
                ports = []
            states = []
            for p in ports:
                try:
                    states.append(getattr(p.state, "name", ""))
                except Exception:
                    states.append("")
            idx = mlib.pick_ready_port_index(states)
            if idx is not None:
                live_port = ports[idx]
                path = "live-ready" if str(states[idx]).strip().lower() == "ready" else "live-first"
        if live_port is not None:
            sc.target_docking_port = live_port
            try:
                st = getattr(live_port.state, "name", "?")
            except Exception:
                st = "?"
            _stdout_sink(mlib.format_mission_log_line(
                "Info", "Target",
                "set_target_docking_port: live port via %s state=%s" % (path, st)))
        elif tv is not None and self._station_port is not None:
            # A live target vessel exists but exposed no docking port; the captured
            # handle is the last resort (may be stale, but tv is set so the vessel
            # target is real).
            sc.target_docking_port = self._station_port
            _stdout_sink(mlib.format_mission_log_line(
                "Warn", "Target",
                "set_target_docking_port: LAST-RESORT captured handle "
                "(target vessel has no live docking port)"))
        else:
            # No target vessel: never clear a working target with a dead handle.
            _stdout_sink(mlib.format_mission_log_line(
                "Warn", "Target",
                "set_target_docking_port: no live target vessel; leaving target "
                "unchanged (not clobbering with a possibly-dead captured handle)"))

    def _read_docking_state(self, v) -> str:
        """The docking-port state.name (kRPC DockingPortState) normalized to the
        machine's PascalCase, or "" (fail-closed: matches no gate). Resolved LIVE
        from the ACTIVE vessel's ports (flight-13: the captured self._station_port
        is a PRE-reload handle, so its .state read faulted to "" in every
        post-reload flight, blinding the DOCK-done and undock gates). Post-merge the
        active vessel carries BOTH mated ports; any 'docked'/'docking' port is
        authoritative, and a post-undock split reads e.g. 'ready'. Falls back to the
        captured handle only if the active vessel exposes no readable port."""
        try:
            ports = list(v.parts.docking_ports)
        except Exception:
            ports = []
        states = []
        for p in ports:
            try:
                states.append(mlib.normalize_docking_state(getattr(p.state, "name", "")))
            except Exception:
                continue
        states = [s for s in states if s]
        if states:
            for want in (mlib.DOCKING_STATE_DOCKED, mlib.DOCKING_STATE_DOCKING):
                if want in states:
                    return want
            return states[0]
        # Last fallback: the captured handle (may be stale post-reload).
        if self._station_port is not None:
            try:
                return mlib.normalize_docking_state(getattr(self._station_port.state, "name", ""))
            except Exception:
                return ""
        return ""

    def _read_craft_chute_state(self, v) -> str:
        """The active vessel's aggregate stock-parachute state (kRPC
        ParachuteState.name, decompiled KRPC.SpaceCenter.Services.Parts: stowed / armed /
        semi_deployed / deployed / cut) normalized to the machine's PascalCase, or ""
        (fail-closed: matches no gate, so the EVA window can never open on a blind frame).

        The MOST-DEPLOYED port wins across a multi-chute craft: Deployed beats
        SemiDeployed beats everything else. EVA-4's window gates on Deployed, and a craft
        is "under full canopy" as soon as any of its chutes is - a min/first-wins read
        would hide that behind an unrelated stowed spare.

        Resolved LIVE from the active vessel every poll (never a captured handle: the same
        stale-handle trap that blinded the B-DOCK docking gates post-reload)."""
        try:
            chutes = list(v.parts.parachutes)
        except Exception:
            return ""
        states = []
        for p in chutes:
            try:
                states.append(mlib.normalize_parachute_state(getattr(p.state, "name", "")))
            except Exception:
                continue
        states = [s for s in states if s]
        if not states:
            return ""
        for want in (mlib.CHUTE_STATE_DEPLOYED, mlib.CHUTE_STATE_SEMI_DEPLOYED):
            if want in states:
                return want
        return states[0]

    def _read_angular_velocity(self, v) -> float:
        """|angular velocity| (rad/s) in the vessel's ORBITAL reference frame --
        the tumble signal (flight-10). The orbital frame excludes body rotation,
        so a stabilized ship reads ~0 and a tumble reads high. Fail-closed NaN on
        any read fault (the DOCK progress watchdog never counts an unread angvel as
        tumble-killed progress)."""
        try:
            av = v.angular_velocity(v.orbital_reference_frame)
            return math.sqrt(sum(float(c) * float(c) for c in av))
        except Exception:
            return float("nan")

    def _read_docking_ap_status(self) -> str:
        """MechJeb docking_autopilot.status (KRPC.MechJeb DockingAutopilot.Status),
        truncated to 60 chars; "" (fail-closed) on any read fault. Diagnosability
        only -- what the AP thinks it is doing (the flight-10 blind spot)."""
        try:
            status = self._mechjeb.docking_autopilot.status
            return str(status)[:60] if status else ""
        except Exception:
            return ""

    def _read_active_transfer(self) -> Tuple[bool, float]:
        """(complete, amount) of the in-flight ResourceTransfer, or (False, NaN)
        when there is none / the read faults (fail-closed for the transfer gate)."""
        rt = self._active_transfer
        if rt is None:
            return False, float("nan")
        complete = False
        amount = float("nan")
        try:
            complete = bool(rt.complete)
        except Exception:
            complete = False
        try:
            amount = float(rt.amount)
        except Exception:
            amount = float("nan")
        return complete, amount

    def _ensure_warp_service(self) -> "WarpService":
        if self._warp is None:
            if self._addr is None:
                raise RuntimeError("warp_to_ut before open(): no address")
            host, rpc_port, stream_port = self._addr
            self._warp = WarpService(host, rpc_port, stream_port,
                                     client_name=self._client_name + "-warp")
        return self._warp

    def _warp_watchdog(self, sc, ut: float) -> None:
        """Per-poll native-warp watchdog (fly-loop contract, research doc):
        while a WarpService warp is active, a game-UT standstill of
        WARP_STALL_WALL_SECONDS first probes KRPC.Paused -- a dialog pause
        does NOT freeze the kRPC server (PauseServerWithGame defaults false),
        so the pause is cleared remotely and the warp resumes -- and cancels
        the warp when the stall has no pause to clear (a wedged continuation
        must never outlive the watchdog). Never raises: every probe is
        best-effort and a failure escalates to the cancel path."""
        warp = self._warp
        if warp is None or not warp.active:
            self._warp_stall.reset()
            return
        if not self._warp_stall.update(time.monotonic(), ut):
            return
        paused = False
        try:
            paused = bool(self._conn.krpc.paused)
        except Exception as exc:
            _stdout_sink(mlib.format_mission_log_line(
                "Warn", "Warp", "watchdog pause probe failed: %s" % (exc,)))
        if paused:
            _stdout_sink(mlib.format_mission_log_line(
                "Warn", "Warp",
                "UT stalled %ds mid-warp with game PAUSED; clearing pause"
                % int(self._warp_stall.stall_seconds)))
            try:
                self._conn.krpc.paused = False
                self._warp_stall.reset()
                return
            except Exception as exc:
                _stdout_sink(mlib.format_mission_log_line(
                    "Warn", "Warp", "unpause failed (%s); cancelling warp" % (exc,)))
        else:
            _stdout_sink(mlib.format_mission_log_line(
                "Warn", "Warp",
                "UT stalled %ds mid-warp (not paused); cancelling warp"
                % int(self._warp_stall.stall_seconds)))
        try:
            warp.cancel(sc)
        finally:
            self._warp_stall.reset()

    def close(self) -> None:
        warp = self._warp
        self._warp = None
        if warp is not None:
            try:
                warp.close()
            except Exception:
                pass
        conn = self._conn
        self._conn = None
        if conn is not None:
            conn.close()


def _situation_name(situation) -> str:
    """Normalize a kRPC VesselSituation enum to the design's UPPER token
    (LANDED / SPLASHED / PRE_LAUNCH / ...). kRPC exposes the enum member's
    ``name`` in lower_snake ('landed', 'splashed'); the design's landed set is
    ('LANDED', 'SPLASHED'), so upper-casing the name lines the two up."""
    name = getattr(situation, "name", None)
    if name is None:
        name = str(situation).rsplit(".", 1)[-1]
    return str(name).upper()


def _read_warp_state(sc):
    """Read (warp_mode, warp_rate) derived from ONE warp_rate sample. The rate
    is read exactly once and the mode classified from THAT sample: reading them
    as two separate RPCs let a ramp crossing 1.0 report mode NONE with a rate
    above 1, which the guard treats as an inconsistent state (Fable review of
    the PR #1328 tail, SF-1: a single-sample false MISSION-FLAKE risked on
    every warp-engagement ramp). Defensive: an unknown surface reports
    (NONE, 1.0)."""
    try:
        rate = float(getattr(sc, "warp_rate", 1.0))
        if not (rate > 1.0):
            return "NONE", rate
        mode = getattr(sc, "warp_mode", None)
        name = getattr(mode, "name", None) or str(mode)
        return ("RAILS" if "rail" in str(name).lower() else "PHYSICS"), rate
    except Exception:
        return "NONE", 1.0


# ---------------------------------------------------------------------------
# Pure ABI version check (design "Connection lifecycle" step 3). Testable.
# ---------------------------------------------------------------------------


def major_minor_mismatch(client_version: str, server_version: str) -> bool:
    """True iff client and server MAJOR.MINOR differ (an ABI-incompatible pair) or
    either version is unparseable (a foreign / unknown server, edge 2/3). A patch
    difference (0.5.4 vs 0.5.3) is NOT a mismatch. On True the mission aborts
    MISSION-ERROR rather than flying against a mismatched RPC surface."""
    c = _major_minor(client_version)
    s = _major_minor(server_version)
    if c is None or s is None:
        return True
    return c != s


def _major_minor(version: str) -> Optional[Tuple[int, int]]:
    parts = str(version or "").strip().split(".")
    if len(parts) < 2:
        return None
    try:
        return (int(parts[0]), int(parts[1]))
    except (TypeError, ValueError):
        return None


# ---------------------------------------------------------------------------
# Diagnostic logging (design "Diagnostic Logging"). The sink is injectable so the
# tests capture lines; default is stdout (captured by run.py into the harness log).
# ---------------------------------------------------------------------------


@dataclass
class MissionLogger:
    """Thin ``[Mission][LEVEL][Phase]`` line emitter (format from
    ``mlib.format_mission_log_line``). ``sink`` is a callable taking one line;
    default writes to stdout. ``rate_limit`` throttles the per-frame telemetry
    line to ~1 Hz keyed by a tag (batch-counting convention: one summary line,
    not one per frame)."""
    sink: Callable[[str], None]
    clock: Callable[[], float]
    _last_emit: dict = None

    def __post_init__(self) -> None:
        if self._last_emit is None:
            self._last_emit = {}

    def emit(self, level: str, phase: str, message: str) -> None:
        self.sink(mlib.format_mission_log_line(level, phase, message))

    def info(self, phase: str, message: str) -> None:
        self.emit("Info", phase, message)

    def warn(self, phase: str, message: str) -> None:
        self.emit("Warn", phase, message)

    def error(self, phase: str, message: str) -> None:
        self.emit("Error", phase, message)

    def verbose(self, phase: str, message: str) -> None:
        self.emit("Verbose", phase, message)

    def verbose_rate_limited(self, key: str, phase: str, message: str,
                             interval: float = 1.0) -> None:
        now = self.clock()
        last = self._last_emit.get(key)
        if last is None or (now - last) >= interval:
            self._last_emit[key] = now
            self.emit("VerboseRateLimited", phase, message)


def _stdout_sink(line: str) -> None:
    sys.stdout.write(line + "\n")


# ---------------------------------------------------------------------------
# Live status file (design-live-observability 2d): the mission atomically
# rewrites results/<runId>_status.json every ~2 s so the supervisor-side
# status.py reads decoded machine state instead of parsing the log tail.
# BEST-EFFORT by contract: every failure is swallowed (a stale/missing status
# file degrades status.py to log parsing; it must NEVER block the fly loop).
# ---------------------------------------------------------------------------


def status_path_for(result_path: str) -> str:
    """Derive the live status-file path from the mission-result path:
    ``<runId>_mission.json`` -> ``<runId>_status.json`` (the shape status.py
    prefers); any other result name gets a ``.status.json`` sibling."""
    if result_path.endswith("_mission.json"):
        return result_path[:-len("_mission.json")] + "_status.json"
    return result_path + ".status.json"


class StatusFileWriter:
    """Cadence-bounded atomic JSON status writer. ``maybe_write(builder)``
    calls the zero-arg ``builder`` only when the interval elapsed (payload
    construction is skipped off-cadence too), serializes deterministically,
    writes ``path + '.tmp'`` and ``os.replace``s it into place (same atomic
    pattern as ``_write_result_file``). Every exception -- builder, dumps,
    filesystem -- is swallowed: the status file is observability, never a
    mission dependency."""

    def __init__(self, path: str,
                 clock: Callable[[], float] = time.monotonic,
                 interval: float = STATUS_WRITE_INTERVAL_SECONDS,
                 base: Optional[Dict] = None) -> None:
        self.path = path
        self.interval = float(interval)
        self._clock = clock
        self._last_write: Optional[float] = None
        self.writes = 0          # diagnostics/tests
        self.failures = 0        # diagnostics/tests
        # Static payload fields (mission name, port) merged into every write.
        self.base: Dict = dict(base or {})
        # Last ~10 sparse event lines (Info/Warn/Error), fed by the logger
        # sink tee run_mission installs; rides the payload's "events" block.
        self.recent_events: deque = deque(maxlen=10)

    def maybe_write(self, builder: Callable[[], Dict]) -> bool:
        """Write if due; True only when a write happened."""
        try:
            now = self._clock()
            if (self._last_write is not None
                    and (now - self._last_write) < self.interval):
                return False
            payload = builder()
            text = json.dumps(payload, sort_keys=True)
            tmp = self.path + ".tmp"
            with open(tmp, "w", encoding="ascii", newline="\n") as fh:
                fh.write(text)
            os.replace(tmp, self.path)
            self._last_write = now
            self.writes += 1
            return True
        except Exception:  # noqa: BLE001 -- best-effort by contract (2d/2e)
            self.failures += 1
            return False


# ---------------------------------------------------------------------------
# Bounded connect + fly loops (I/O; every wait bounded, design "A mission never
# hangs"). Every decision is delegated to mlib.
# ---------------------------------------------------------------------------


@dataclass(frozen=True)
class ConnectResult:
    ok: bool
    attempts: int
    connected_seconds: float          # NaN when never connected
    last_error: Optional[str]


def connect_with_retry(
    control: MissionControl, host: str, rpc_port: int, stream_port: int,
    log: MissionLogger,
    budget: float = CONNECT_BUDGET_SECONDS,
    max_attempts: int = CONNECT_MAX_ATTEMPTS,
    backoff: float = CONNECT_BACKOFF_SECONDS,
    clock: Callable[[], float] = time.monotonic,
    sleep: Callable[[float], None] = time.sleep,
) -> ConnectResult:
    """Bounded connect loop (design "Connection lifecycle" step 2). Attempts to
    ``open`` the seam; on each failure ``mlib.decide_connect_retry`` decides RETRY
    (sleep the backoff, try again) vs TIMEOUT (give up). Bounded by BOTH the
    attempt cap and the wall budget, so an unreachable server can never retry
    forever. Returns a ``ConnectResult``; ``ok=False`` maps to
    MISSION-CONNECT-TIMEOUT."""
    start = clock()
    attempts = 0
    last_error: Optional[str] = None
    while True:
        attempts += 1
        try:
            control.open(host, rpc_port, stream_port)
            connected = clock() - start
            log.info("Connect", "connected in %ss attempts=%d serverVersion=%s clientVersion=%s"
                     % (_fmt(connected), attempts, control.server_version, control.client_version))
            return ConnectResult(True, attempts, connected, None)
        except Exception as exc:  # noqa: BLE001 -- any connect failure is a retry candidate
            last_error = "%s: %s" % (type(exc).__name__, exc)
            elapsed = clock() - start
            log.verbose_rate_limited(
                "connect", "Connect",
                "connect attempt=%d/%d elapsed=%s/%s" % (attempts, max_attempts, _fmt(elapsed), _fmt(budget)))
            if mlib.decide_connect_retry(elapsed, attempts, budget, max_attempts) == mlib.CONNECT_RETRY:
                sleep(backoff)
                continue
            log.warn("Connect", "connect timeout after %d attempts / %ss -> %s"
                     % (attempts, _fmt(elapsed), mlib.MISSION_CONNECT_TIMEOUT))
            return ConnectResult(False, attempts, float("nan"), last_error)


def fly_loop(
    control: MissionControl, initial_state, decide, log: MissionLogger,
    deadline: float,
    clock: Callable[[], float] = time.monotonic,
    sleep: Callable[[float], None] = time.sleep,
    poll_interval: float = POLL_INTERVAL_SECONDS,
    settle_frames: int = DEFAULT_SETTLE_FRAMES,
    allow_rails_warp: bool = False,
    max_physics_warp: float = 0.0,
    status_writer: Optional[StatusFileWriter] = None,
):
    """Drive a mission's ``mlib`` phase machine to completion, bounded by BOTH the
    machine's per-phase budgets (inside ``decide``) AND a wall-clock ``deadline``
    (the ``--budget`` the harness passes). Returns ``(final_state, frames)``.

    Each frame: read a snapshot, feed it to the pure ``decide`` (which returns the
    next state + the actions to perform), execute those actions on the seam, log a
    transition when the phase changes, and rate-limit a telemetry line. The loop
    NEVER blocks unbounded: if the machine wedges without hitting its own phase
    budget (e.g. frozen UT), the wall deadline forces a MISSION-FLAKE naming the
    stuck phase, so the shell always terminates and writes a result.

    Physics-warp guard (design edge 7): each frame the snapshot's warp state is
    checked via the pure ``mlib.is_unexpected_warp`` (PHYSICS warp is never
    permitted; RAILS warp only when ``allow_rails_warp``). An unexpected warp state
    is a determinism violation -> MISSION-FLAKE naming the phase + the warp state,
    with the WARP log line, so a stray high-warp request never silently distorts
    the recorded trajectory.

    On a REAL terminal (LANDED / ORBIT, ``state.verdict is None``) the loop then
    samples a bounded SETTLE-TAIL of ``settle_frames`` more snapshots before
    returning, so the assertion evaluators' K-consecutive debounce has settled
    orbit data (design guardrails: sample only after warp settles, require K
    consecutive in-tolerance snapshots). A flake terminal skips the settle-tail."""
    state = initial_state
    frames: List = []
    # Seed the exception-state slot immediately: without this, a raise BEFORE
    # the loop body's first publish (e.g. a malformed build_state result) would
    # attach the PREVIOUS mission's final state to THIS mission's error result
    # (Fable review of the PR #1328 tail, NIT-2).
    _FLY_LOOP_LAST_STATE["state"] = state
    try:
        return _fly_loop_body(control, state, decide, log, deadline, clock, sleep,
                              poll_interval, settle_frames, allow_rails_warp, max_physics_warp, frames,
                              status_writer)
    except Exception as exc:
        # Preserve the ADVANCED machine state for the shell's error result:
        # without this a mid-flight kRPC error reported phasesReached=[] even
        # though the machine had reached CIRCULARIZE (first live B2 run).
        # ``mission_state`` rides the exception; run_mission reads it back.
        if not hasattr(exc, "mission_state"):
            exc.mission_state = _FLY_LOOP_LAST_STATE.get("state", state)  # type: ignore[attr-defined]
        raise


# The loop body publishes its current state here each frame so the fly_loop
# wrapper can attach it to an escaping exception (single-threaded shell; the
# dict is overwritten per call and read only on the unwind path).
_FLY_LOOP_LAST_STATE: Dict = {}


def _fly_loop_body(control, state, decide, log, deadline, clock, sleep,
                   poll_interval, settle_frames, allow_rails_warp, max_physics_warp, frames,
                   status_writer=None):
    warp_violations = 0
    # Event-window ring buffer (design-live-observability 2c): the last
    # RING_BUFFER_FRAMES raw snapshots in compact one-line form, dumped ONCE
    # at Verbose on transition / flake / vessel-lost / gate-flip so the
    # frames BETWEEN rate-limited telemetry samples are recoverable post-hoc.
    ring: deque = deque(maxlen=RING_BUFFER_FRAMES)
    while not state.done:
        _FLY_LOOP_LAST_STATE["state"] = state
        if clock() >= deadline:
            log.warn(state.phase, "wall budget elapsed in phase %s -> %s"
                     % (state.phase, mlib.MISSION_FLAKE))
            _dump_event_window(log, state.phase, ring, "wall-budget-flake")
            return replace(state, verdict=mlib.MISSION_FLAKE, flake_phase=state.phase, done=True), frames
        try:
            snapshot = control.read_snapshot()
        except Exception as exc:  # noqa: BLE001
            if mlib.is_transport_drop_exception(type(exc).__name__):
                raise  # dead connection: the retryable-flake path (edges 5/8)
            # A server-ANSWERED read failure (vessel-state RPC error, e.g. the
            # maneuver-nodes read on a just-destroyed vessel -- seventh live B5
            # flight): tolerate and poll on. Bounded inherently: the control
            # seam's read-fail streak escalates to a vessel_lost snapshot
            # within READ_FAIL_STREAK_LIMIT consecutive failures, so at most
            # two of these continues happen before the machine reaches its
            # honest vessel-lost terminal.
            log.warn(state.phase, "telemetry read failed (%s: %s); polling on"
                     % (type(exc).__name__, str(exc)[:160]))
            sleep(poll_interval)
            continue
        frames.append(snapshot)
        ring.append(mlib.format_snapshot_compact(snapshot))
        # Edge 7: an unexpected physics (or, for B1, any) warp state distorts the
        # flight; flake naming the phase + warp state rather than record a warped
        # run. DEBOUNCED to two CONSECUTIVE violating samples (Fable review of
        # the PR #1328 tail, SF-1/SF-2): TimeWarp.CurrentRate is a jittery,
        # continuously-ramping quantity, so a single sample caught mid-ramp or
        # on a frame-hitch spike must not kill an otherwise clean flight; a
        # REAL unexpected warp state persists across the 0.5s poll gap.
        if mlib.is_unexpected_warp(snapshot.warp_mode, snapshot.warp_rate, allow_rails_warp,
                                   max_physics_warp=max_physics_warp):
            warp_violations += 1
            log.warn(state.phase, "unexpected %s-warp x%s in phase %s (allow_rails=%s) strike %d/2"
                     % (snapshot.warp_mode, _fmt(snapshot.warp_rate), state.phase,
                        allow_rails_warp, warp_violations))
            if warp_violations >= 2:
                log.warn(state.phase, "unexpected warp persisted 2 consecutive samples -> %s"
                         % (mlib.MISSION_FLAKE,))
                _dump_event_window(log, state.phase, ring, "warp-flake")
                return replace(state, verdict=mlib.MISSION_FLAKE, flake_phase=state.phase, done=True), frames
        else:
            warp_violations = 0
        prev_state = state
        prev_phase = state.phase
        state, actions = decide(state, snapshot)
        # Re-publish post-decide: a perform() failure below must report the
        # phase the machine had ALREADY entered this frame.
        _FLY_LOOP_LAST_STATE["state"] = state
        if state.phase != prev_phase:
            # avThr rides every transition line: the B-DOCK SEPARATE->ORBIT /
            # SEPARATE->PHASING handoff must show the orbital stage still has
            # thrust for the rendezvous (available_thrust > 0), and it is a cheap
            # diagnosability channel for every other transition too.
            log.info(state.phase, "phase %s -> %s ut=%s alt=%s ap=%s vsurf=%s avThr=%s"
                     % (prev_phase, state.phase, _fmt(snapshot.ut), _fmt(snapshot.altitude),
                        _fmt(snapshot.apoapsis), _fmt(snapshot.vertical_speed),
                        _fmt(snapshot.available_thrust)))
        # GATE-EVIDENCE lines (design-live-observability 2b): every sparse
        # machine latch/gate flip logs the exact values that decided it, on
        # the frame it happened -- a single-frame transient (e.g. the
        # attitude-error dip that opens the throttle gate between telemetry
        # samples) is loud by definition, independent of any rate limit.
        gate_changes = mlib.diff_machine_state(prev_state, state)
        for change in gate_changes:
            log.info(state.phase,
                     "gate %s | ut=%s alt=%s nodeDv=%s apErr=%s thr=%s avThr=%s "
                     "nextPe=%s warp=%sx%s"
                     % (change, _fmt(snapshot.ut), _fmt(snapshot.altitude),
                        _fmt(snapshot.node_dv), _fmt(snapshot.ap_error),
                        _fmt(snapshot.throttle), _fmt(snapshot.available_thrust),
                        _fmt(snapshot.next_pe), snapshot.warp_mode,
                        _fmt(snapshot.warp_rate)))
        for action in actions:
            control.perform(action)
            log.info(state.phase, "action %s value=%s%s"
                     % (action.kind, _fmt(action.value),
                        (" text=%s" % action.text) if getattr(action, "text", None) else ""))
        # MATCH-VELOCITY diagnostic (flight-5: the phase had NO per-frame line, so
        # a stuck rel-speed silently ate the whole wall). Rate-limited per-phase
        # key carrying the fields the gate reads, so a stall is greppable + rides
        # the status file (targetDistance / targetRelSpeed added to snapshot_dict).
        if state.phase == mlib.BDOCK_MATCH_VELOCITY:
            log.verbose_rate_limited(
                "match", state.phase,
                "match-velocity tgtDist=%s tgtRelV=%s nodes=%d nodeDv=%s ut=%s"
                % (_fmt(snapshot.target_distance), _fmt(snapshot.target_rel_speed),
                   snapshot.node_count, _fmt(snapshot.node_dv), _fmt(snapshot.ut)))
        # DOCK diagnostic (flight-10: the operator watched a tumble for ~28 min
        # with DOCK logging NOTHING between entry and the give-up dump). Rate-
        # limited per-phase key carrying the prox-ops signature (tumble / control /
        # AP status) so a stall is greppable + rides the status file.
        if state.phase == mlib.BDOCK_DOCK:
            log.verbose_rate_limited(
                "dock", state.phase,
                "dock dist=%s relSpd=%s dockState=%s angVel=%s sas=%s rcs=%s "
                "apStatus=%s apEnabled=%s ut=%s"
                % (_fmt(snapshot.target_distance), _fmt(snapshot.target_rel_speed),
                   snapshot.docking_state or "?", _fmt(snapshot.angular_velocity),
                   snapshot.sas_enabled, snapshot.rcs_enabled,
                   snapshot.docking_ap_status or "?", snapshot.mj_docking_enabled,
                   _fmt(snapshot.ut)))
        # alt/vspeed/body/nodes ride the line too: B4 attempt-1 (2026-07-21)
        # stalled in REENTRY with a line that omitted altitude, leaving
        # frozen-physics vs normal-coast undiagnosable from the log. Log what
        # the machines gate on.
        log.verbose_rate_limited(
            "telemetry", state.phase,
            "telemetry ap=%s pe=%s ecc=%s inc=%s alt=%s vspd=%s body=%s nodes=%d "
            "nodeDv=%s nodeUt=%s tts=%s nextBody=%s nextPe=%s warpTo=%s lf=%s "
            "ec=%s thr=%s avThr=%s situation=%s chute=%s warp=%sx%s apErr=%s ut=%s"
            % (_fmt(snapshot.apoapsis), _fmt(snapshot.periapsis), _fmt(snapshot.eccentricity),
               _fmt(snapshot.inclination), _fmt(snapshot.altitude),
               _fmt(snapshot.vertical_speed), snapshot.body or "?", snapshot.node_count,
               _fmt(snapshot.node_dv), _fmt(snapshot.node_ut), _fmt(snapshot.time_to_soi),
               snapshot.next_body or "?", _fmt(snapshot.next_pe),
               _fmt(snapshot.warping_to), _fmt(snapshot.liquid_fuel),
               _fmt(snapshot.electric_charge), _fmt(snapshot.throttle),
               _fmt(snapshot.available_thrust), snapshot.situation,
               snapshot.craft_chute_state or "-", snapshot.warp_mode,
               _fmt(snapshot.warp_rate), _fmt(snapshot.ap_error), _fmt(snapshot.ut)))
        # MACHINE-STATE line (design-live-observability 2a): the decision
        # state verbatim on a ~5 s cadence, so an operator report maps to
        # machine state without inference.
        log.verbose_rate_limited(
            "machine", state.phase,
            mlib.format_machine_state(state, snapshot.ut),
            interval=MACHINE_STATE_INTERVAL_SECONDS)
        # Event-window dump (2c): once per trigger frame, most significant
        # reason wins (a terminal frame usually also flips gates).
        dump_reason = None
        if state.done and getattr(state, "verdict", None) is not None:
            dump_reason = "terminal-%s" % state.verdict
        elif snapshot.vessel_lost:
            dump_reason = "vessel-lost"
        elif state.phase != prev_phase:
            dump_reason = "phase-transition"
        elif gate_changes:
            dump_reason = "gate-flip"
        if dump_reason is not None:
            _dump_event_window(log, state.phase, ring, dump_reason)
        # LIVE STATUS FILE (2d): atomic best-effort rewrite every ~2 s; the
        # builder only runs when the write is due, and every failure is
        # swallowed inside maybe_write (never blocks the fly loop).
        if status_writer is not None:
            status_writer.maybe_write(
                lambda: _build_status_payload(status_writer, state, snapshot))
        if state.done:
            break
        sleep(poll_interval)

    # Settle-tail (real terminal only): gather K-ish settled samples for debounce.
    # A DOWN-terminal state (mlib.B1State.skip_settle_tail, operator decision
    # 2026-07-20 option A) skips the tail: the vessel is GONE, so the tail would
    # only gather vessel_lost / garbage frames. LANDED / ORBIT / SPLASHDOWN keep it.
    if state.verdict is None and getattr(state, "skip_settle_tail", False):
        log.info(state.phase, "settle tail skipped: terminal marks vessel gone "
                              "(skip_settle_tail set)")
    elif state.verdict is None:
        for _ in range(max(0, settle_frames)):
            if clock() >= deadline:
                break
            snapshot = control.read_snapshot()
            frames.append(snapshot)
            log.verbose_rate_limited(
                "settle", state.phase,
                "settle ap=%s pe=%s ecc=%s inc=%s situation=%s"
                % (_fmt(snapshot.apoapsis), _fmt(snapshot.periapsis), _fmt(snapshot.eccentricity),
                   _fmt(snapshot.inclination), snapshot.situation))
            sleep(poll_interval)
    return state, frames


def _dump_event_window(log, phase: str, ring, reason: str) -> None:
    """Dump the event-window ring buffer ONCE at Verbose (design 2c): a
    header naming the trigger + one compact line per buffered frame, oldest
    first. Bounded by RING_BUFFER_FRAMES; triggers are sparse by contract."""
    lines = list(ring)
    log.verbose(phase, "window dump reason=%s frames=%d (oldest first)"
                % (reason, len(lines)))
    for index, line in enumerate(lines):
        log.verbose(phase, "window[%02d/%d] %s" % (index + 1, len(lines), line))


def _build_status_payload(status_writer: "StatusFileWriter", state,
                          snapshot) -> Dict:
    """The live status-file payload (design 2d): static base fields + phase +
    machine-state dict + decoded snapshot + the last sparse events. Pure
    apart from the wall timestamp."""
    payload = dict(status_writer.base)
    payload["schema"] = 1
    payload["wallWritten"] = time.time()
    payload["phase"] = getattr(state, "phase", "?")
    payload["phasesReached"] = list(getattr(state, "phases_reached", ()))
    verdict = getattr(state, "verdict", None)
    if verdict is not None:
        payload["verdict"] = verdict
    payload["machine"] = mlib.machine_state_dict(state, snapshot.ut)
    payload["snapshot"] = mlib.snapshot_dict(snapshot)
    payload["events"] = list(status_writer.recent_events)
    return payload


# ---------------------------------------------------------------------------
# Mission spec (the per-mission wiring b1_pad_hop / b2_lko_ascent supply) + the
# generic runner that flies it. This is the whole mission control flow; the two
# shells just build a MissionSpec and call run_mission.
# ---------------------------------------------------------------------------


@dataclass(frozen=True)
class MissionSpec:
    """Everything the generic runner needs to fly one mission, supplied by the
    concrete shell (b1/b2):
      - ``name``:        the mission id written into the result.
      - ``build_state``: params-dict -> the mlib initial phase-machine state.
      - ``decide``:      the mlib per-frame decision fn ``(state, snapshot) -> (state, actions)``.
      - ``evaluate``:    ``(frames, params, state) -> list[mlib.AssertionOutcome]``.
        ``state`` is the TERMINATED phase-machine state (carried evidence: B1's
        DOWN terminal, B4's phases_reached / chute_deployed); shells that only
        need frames accept and ignore it (``state=None`` default).
      - ``make_control``: () -> a real MissionControl (KrpcMissionControl); tests
        inject their own control and never call this.
      - ``allow_rails_warp``: whether RAILS warp is a PERMITTED warp state for this
        mission's fly loop (design edge 7). B1 flies 1x throughout (False); B2
        permits RAILS on its exo-atmospheric coast (True). PHYSICS warp is never
        permitted for either. An unexpected warp state flakes the mission.
    """
    name: str
    build_state: Callable[[dict], object]
    decide: Callable
    evaluate: Callable
    make_control: Callable[[], MissionControl]
    allow_rails_warp: bool = False
    # Highest PERMITTED physics-warp rate (0.0 = never, the B1 default). B2
    # sets 2.0: MechJeb's AscentAutopilot engages its own 2x physics warp
    # during ascent and KRPC.MechJeb 0.8.1 exposes no toggle for it (observed
    # live 2026-07-20). Above the bound (plus the ramp allowance) the warp
    # guard still flakes the mission.
    max_physics_warp: float = 0.0
    # Settle-tail frames sampled after a real terminal (review SF-4). B1/B2/B4
    # keep the default: their assertion evaluators run K-consecutive debounce
    # windows over the FRAMES and need settled post-terminal samples. B5/B6
    # set 0: every assertion is machine-carried evidence (evaluate discards
    # frames), so the tail only adds reads that can transiently fail after
    # RETURN and flip a finished pass into a spurious FLAKE.
    settle_frames: int = DEFAULT_SETTLE_FRAMES


def run_mission(
    spec: MissionSpec, params: dict, host: str, rpc_port: int, stream_port: int,
    result_path: str, budget: float,
    control: Optional[MissionControl] = None,
    log: Optional[MissionLogger] = None,
    clock: Callable[[], float] = time.monotonic,
    sleep: Callable[[float], None] = time.sleep,
    writer: Optional[Callable[[str, str], None]] = None,
    status_writer: Optional[StatusFileWriter] = None,
    seam_config: Optional[Dict] = None,
) -> int:
    """Fly ``spec`` end-to-end and write the mission-result JSON. Returns the
    process exit code (0 only on MISSION-OK; nonzero on every non-OK verdict so
    run.py has a fallback signal if the result file is missing, edge 12).

    The whole body is wrapped so NOTHING escapes as a hang or an unwritten result:
    connect (bounded) -> ABI check -> fly (bounded) -> evaluate assertions ->
    resolve verdict; any exception -> MISSION-ERROR with a traceback; the seam is
    ALWAYS closed in the ``finally`` before the result is written, so the client
    is gone before the harness sends FlushAndQuit (design step 5 / edge 10).

    ``control`` / ``log`` / ``writer`` are injectable for the fake-telemetry
    tests; production passes none and gets the real kRPC seam, stdout logging, and
    a filesystem write."""
    if log is None:
        log = MissionLogger(sink=_stdout_sink, clock=clock)
    if control is None:
        control = spec.make_control()
    # Mid-mission command-seam bridge (route 1, section 3.2): wire the reserved
    # command-id + channel paths into the control when the mission was spawned
    # with seam args. A control lacking configure_seam (or a mission that never
    # emits ACTION_PARSEK_COMMIT_TREE) is unaffected -- the base no-op ignores it.
    if seam_config and hasattr(control, "configure_seam"):
        try:
            control.configure_seam(
                seam_config.get("commands_path"),
                seam_config.get("responses_path"),
                seam_config.get("commit_id"))
        except Exception as exc:  # noqa: BLE001 -- never a hang; the machine flakes
            log.warn("Seam", "configure_seam failed: %s (mid-mission commit will ERROR)" % (exc,))
    if status_writer is None and writer is None:
        # Production run (real filesystem result writer): stand up the live
        # status file next to the result (design-live-observability 2d).
        # Tests that inject an in-memory result writer get NO status file
        # unless they pass one explicitly (hermetic by default).
        status_writer = StatusFileWriter(
            status_path_for(result_path), clock=clock,
            base={"mission": spec.name, "rpcPort": rpc_port})
    if status_writer is not None:
        # Tee the logger sink: every sparse Info/Warn/Error line also lands
        # in the status payload's last-10 events (telemetry / machine /
        # settle lines are VerboseRateLimited and excluded by prefix).
        base_sink = log.sink

        def _tee_sink(line: str, _orig=base_sink,
                      _events=status_writer.recent_events) -> None:
            if (line.startswith("[Mission][Info]")
                    or line.startswith("[Mission][Warn]")
                    or line.startswith("[Mission][Error]")):
                _events.append(line)
            _orig(line)

        log.sink = _tee_sink
    if writer is None:
        writer = _write_result_file

    wall_start = clock()
    verdict = mlib.MISSION_ERROR
    reason = "mission did not run"
    phases_reached: List[str] = []
    connect_attempts = 0
    connected_seconds = float("nan")
    assertion_rows: List = []
    error: Optional[str] = None
    # Track whether the kRPC connect (+ ABI check) succeeded, so a post-connect
    # exception is classified by the mission-validity gate (edge 5/8: a connection
    # drop or kRPC RPC error is a MISSION-FLAKE, not a MISSION-ERROR); only a
    # pre-connect / setup / internal non-kRPC exception stays MISSION-ERROR (edge 9).
    connected = False

    try:
        cr = connect_with_retry(control, host, rpc_port, stream_port, log,
                                clock=clock, sleep=sleep)
        connect_attempts = cr.attempts
        if not cr.ok:
            verdict = mlib.MISSION_CONNECT_TIMEOUT
            reason = "server unreachable within connect budget"
            error = cr.last_error
        else:
            connected_seconds = cr.connected_seconds
            if major_minor_mismatch(control.client_version, control.server_version):
                verdict = mlib.MISSION_ERROR
                reason = ("kRPC version mismatch client=%s server=%s"
                          % (control.client_version, control.server_version))
                error = reason
                log.error("Connect", "version mismatch client=%s server=%s -> %s"
                          % (control.client_version, control.server_version, mlib.MISSION_ERROR))
            else:
                connected = True
                state = spec.build_state(params)
                deadline = wall_start + float(budget)
                state, frames = fly_loop(control, state, spec.decide, log, deadline,
                                         clock=clock, sleep=sleep,
                                         settle_frames=spec.settle_frames,
                                         allow_rails_warp=spec.allow_rails_warp,
                                         max_physics_warp=spec.max_physics_warp,
                                         status_writer=status_writer)
                phases_reached = list(state.phases_reached)
                outcomes = spec.evaluate(frames, params, state)
                for o in outcomes:
                    v = o.value
                    non_finite = isinstance(v, float) and not math.isfinite(v)
                    if non_finite:
                        log.warn("Assert", "assert %s non-finite value; unmet-for-now" % (o.name,))
                    log.info("Assert", "assert %s value=%s met=%s"
                             % (o.name, ("null" if non_finite else _fmt(v)), o.met))
                assertion_rows = [o.to_dict() for o in outcomes]
                verdict, reason = mlib.resolve_flight_verdict(state, outcomes)
    except Exception as exc:  # noqa: BLE001 -- caught so nothing escapes as a hang / unwritten result
        error = traceback.format_exc()
        # Recover the advanced machine state fly_loop attached to the exception
        # so the error result reports the phases the flight actually reached.
        lost_state = getattr(exc, "mission_state", None)
        if lost_state is not None and getattr(lost_state, "phases_reached", None):
            phases_reached = list(lost_state.phases_reached)
        if connected:
            # A drop / kRPC RPC error AFTER a successful connect is autopilot-flake
            # (retryable on a fresh boot); a non-kRPC internal error stays ERROR.
            exc_verdict = mlib.classify_post_connect_exception(
                type(exc).__module__, type(exc).__name__)
            if exc_verdict == mlib.MISSION_FLAKE:
                verdict = mlib.MISSION_FLAKE
                # Carry the exception MESSAGE, not just the type: an RPCError's
                # server-side text names the failing procedure/cause, and losing
                # it cost a full diagnose cycle on the first live B2 run.
                reason = ("connection dropped / kRPC error post-connect: %s: %s"
                          % (type(exc).__name__, str(exc)[:300]))
                log.warn("Verdict", "post-connect drop (%s: %s) -> %s"
                         % (type(exc).__name__, str(exc)[:300], mlib.MISSION_FLAKE))
            else:
                verdict = mlib.MISSION_ERROR
                reason = "unexpected exception in mission"
                log.error("Verdict", "unexpected post-connect exception -> %s"
                          % (mlib.MISSION_ERROR,))
        else:
            verdict = mlib.MISSION_ERROR
            reason = "unexpected exception in mission (pre-connect / setup)"
            log.error("Verdict", "unexpected pre-connect exception -> %s" % (mlib.MISSION_ERROR,))
    finally:
        closed = False
        try:
            control.close()
            closed = True
        except Exception as exc:  # noqa: BLE001
            log.warn("Connect", "disconnect error swallowed: %s" % (exc,))
        log.verbose("Connect", "disconnect (finally) closed=%s" % (closed,))

    wall_seconds = clock() - wall_start
    log.info("Verdict", "mission verdict=%s reason=%s phasesReached=%s wall=%ss"
             % (verdict, reason, phases_reached, _fmt(wall_seconds)))

    result = mlib.build_mission_result(
        mission=spec.name,
        verdict=verdict,
        reason=reason,
        phases_reached=phases_reached,
        connect_attempts=connect_attempts,
        connected_seconds=connected_seconds,
        rpc_port=rpc_port,
        assertions=assertion_rows,
        wall_seconds=round(wall_seconds, 3),
        krpc_client_version=str(getattr(control, "client_version", "") or ""),
        krpc_server_version=str(getattr(control, "server_version", "") or ""),
        error=error,
    )
    writer(result_path, mlib.serialize_mission_result(result))
    return 0 if verdict == mlib.MISSION_OK else 1


def _write_result_file(path: str, text: str) -> None:
    """Write the mission-result text via a temp-file + atomic replace, so a reader
    (run.py) never sees a half-written result. Creates the parent dir if needed."""
    parent = os.path.dirname(os.path.abspath(path))
    if parent and not os.path.isdir(parent):
        os.makedirs(parent, exist_ok=True)
    tmp = path + ".tmp"
    with open(tmp, "w", encoding="ascii", newline="\n") as fh:
        fh.write(text)
    os.replace(tmp, path)


def _fmt(value) -> str:
    """Format a value for a log line: floats to 3dp, None as 'none', everything
    else via str. Kept locale-independent (no thousands separators)."""
    if value is None:
        return "none"
    if isinstance(value, float):
        if not math.isfinite(value):
            return "nan"
        return ("%.3f" % value)
    return str(value)


# ---------------------------------------------------------------------------
# CLI (design Mental Model: --params <json> --rpc-host --rpc-port --stream-port
# --result <path> --budget <s>). Shared by both mission shells.
# ---------------------------------------------------------------------------


def build_arg_parser(mission_name: str) -> argparse.ArgumentParser:
    p = argparse.ArgumentParser(
        prog=mission_name,
        description="M-B1 flown mission %s (kRPC autopilot driver)." % mission_name)
    p.add_argument("--params", required=True,
                   help="missionParams block as a JSON object string")
    p.add_argument("--rpc-host", default="127.0.0.1", help="kRPC RPC host")
    p.add_argument("--rpc-port", type=int, default=50000, help="kRPC RPC port")
    p.add_argument("--stream-port", type=int, default=50001, help="kRPC stream port")
    p.add_argument("--result", required=True,
                   help="path to write the mission-result JSON")
    p.add_argument("--budget", type=float, required=True,
                   help="mission wall-clock budget in seconds (the mission-step budget)")
    # Mid-mission command-seam bridge (route 1, section 3.2). Optional: only
    # B-DOCK emits ACTION_PARSEK_COMMIT_TREE, and run.py passes these only for an
    # autopilot mission with a channel. A mission that never emits the action
    # ignores them entirely.
    p.add_argument("--seam-commands", default=None,
                   help="path to the seam command channel (parsek-test-commands.txt)")
    p.add_argument("--seam-responses", default=None,
                   help="path to the seam response channel (parsek-test-responses.txt)")
    p.add_argument("--seam-commit-id", default=None,
                   help="reserved monotonic command-id for the mid-mission CommitTree")
    return p


def main_from_spec(spec: MissionSpec, argv: Optional[List[str]] = None) -> int:
    """Parse the CLI, decode ``--params`` JSON, and fly ``spec``. This is the
    entry the two shells call from ``__main__``. A malformed ``--params`` is a
    MISSION-ERROR written to the result path (never an uncaught crash), so run.py
    still reads a verdict."""
    args = build_arg_parser(spec.name).parse_args(argv)
    log = MissionLogger(sink=_stdout_sink, clock=time.monotonic)
    log.info("Spawn", "mission start name=%s rpc=%s:%d stream=%d budget=%ss result=%s"
             % (spec.name, args.rpc_host, args.rpc_port, args.stream_port, _fmt(args.budget), args.result))
    try:
        params = json.loads(args.params)
        if not isinstance(params, dict):
            raise ValueError("--params must be a JSON object, got %s" % type(params).__name__)
    except Exception as exc:  # noqa: BLE001 -- a bad --params is MISSION-ERROR, not a crash
        log.error("Spawn", "bad --params: %s -> %s" % (exc, mlib.MISSION_ERROR))
        result = mlib.build_mission_result(
            mission=spec.name, verdict=mlib.MISSION_ERROR,
            reason="bad --params json", phases_reached=[], connect_attempts=0,
            connected_seconds=float("nan"), rpc_port=args.rpc_port, assertions=[],
            wall_seconds=0.0, krpc_client_version="", krpc_server_version="",
            error=traceback.format_exc())
        _write_result_file(args.result, mlib.serialize_mission_result(result))
        return 1
    seam_config = None
    if args.seam_commit_id and args.seam_commands and args.seam_responses:
        seam_config = {"commands_path": args.seam_commands,
                       "responses_path": args.seam_responses,
                       "commit_id": args.seam_commit_id}
        log.info("Spawn", "seam bridge armed commit-id=%s" % args.seam_commit_id)
    return run_mission(spec, params, args.rpc_host, args.rpc_port, args.stream_port,
                       args.result, args.budget, log=log, seam_config=seam_config)
