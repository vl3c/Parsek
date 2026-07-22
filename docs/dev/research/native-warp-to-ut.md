# Native fire-and-forget Warp-to-UT for the kRPC mission harness

Research report, 2026-07-22. Question: how do we get a GAME-managed
"warp to UT / warp to node" (auto-adapting warp factor, per-body altitude
limits handled by the game) while the Python fly loop keeps polling
telemetry non-blockingly?

Sources read (all read-only):

- kRPC 0.5.4 source: `harness/provision/.cache/krpc-src` (git, HEAD = v0.5.4
  line; paths cited as `repo:path:line`)
- KSP 1.12.5 `Assembly-CSharp.dll` decompiled with ilspycmd (TimeWarp class;
  line refs are to the decompile scratch file, member names are stable)
- MechJeb2 2.15.1.0 decompile (MuMech.MechJebModuleWarpController)
- KRPC.MechJeb 0.8.1 source: `harness/provision/.cache/krpc_mechjeb-src`
- kRPC Python client 0.5.4 in `harness/missions/.venv`
- Provisioner: `harness/provision/provision.py`, `provlib.py`, `pins.toml`

## 1. What kRPC's SpaceCenter.WarpTo actually does server-side

`service/SpaceCenter/src/Services/SpaceCenter.cs:818-831` (`WarpTo`):

- It does NOT call KSP's native `TimeWarp.fetch.WarpTo`. It implements its
  own stepper: each execution computes
  `rate = Clamp(ut - now, 1, maxRailsRate)` (i.e. target rate = seconds
  remaining), then calls `RailsWarpAtRate(rate)` or, when rails warp at
  factor 1 is not allowed (`CanRailsWarpAt()`, line 765-786: altitude below
  `TimeWarp.fetch.GetAltitudeLimit(factor, mainBody)` or throttle > 0),
  `PhysicsWarpAtRate(...)`.
- If `now < ut` it throws
  `YieldException<Action>(() => WarpTo(ut, ...))` (line 828). The kRPC core
  catches this and re-queues the continuation for the NEXT FixedUpdate
  (`core/src/Core.cs:468-470`, re-run via the swap at `Core.cs:486-489`).
  So server-side it is a per-frame continuation; only the ISSUING CLIENT's
  request stays open until arrival.
- Rate adaptation is both directions:
  - UP: `IncreaseRailsWarp` (SpaceCenter.cs:873-889) refuses to exceed
    `MaximumRailsWarpFactor` (SpaceCenter.cs:793-801), which walks
    `CanRailsWarpAt(i)` per factor, i.e. the per-body
    `timeWarpAltitudeLimits` table via `GetAltitudeLimit`. It also waits for
    the previous rate change to take effect (0.01 tolerance check).
  - DOWN: `RailsWarpAtRate` (SpaceCenter.cs:848-857) steps down when
    seconds-remaining falls below the current rate, giving a deceleration
    ramp into the target UT. Independent of kRPC, KSP itself force-clamps a
    now-too-high rate every physics frame (see section 3, TimeWarp.FixedUpdate),
    so descending below an altitude limit mid-warp is caught by the game even
    between kRPC steps.
- On arrival it sets factor 0 (`SetWarpFactor(HIGH, 0)`, line 829-830) and
  the RPC finally returns.

Precision note: kRPC's stepper has no stock-style overshoot correction; it
just ramps down as remaining time shrinks. Arrival is close but not
frame-exact (stock's own WarpTo rewinds the clock to the decel point, kRPC
does not).

### Scheduler concurrency (the load-bearing fact)

`core/src/Core.cs`:

- `RPCServerUpdate` (Core.cs:412-497) runs once per FixedUpdate. It executes
  ALL pending continuations from ALL clients in one loop
  (Core.cs:452-476). A yielded continuation goes to
  `rpcYieldedContinuations` and runs again next update.
- `PollRequests` (Core.cs:683-736) polls every connected client for new
  requests but SKIPS clients that already have a pending or yielded
  continuation (`pollRequestsCurrentClients`, Core.cs:688-698). Consequence:
  RPCs are serialized PER CONNECTION, not globally. A connection blocked in
  `WarpTo` does not delay any other connection's RPCs.
- A continuation whose client disconnected is silently dropped
  (Core.cs:455-456: `if (!continuation.Client.Connected) continue;`). This
  is a usable remote-cancel: kill the socket, the warp stepper dies next
  frame (the warp RATE however stays where it was; someone must then set
  factor 0).
- Pause behavior: the server update is driven from `Addon.FixedUpdate`
  (server/src/Addon.cs:365-371), but `Addon.Update` (Addon.cs:381-385) keeps
  calling it while the game is PAUSED unless `PauseServerWithGame` is set,
  and that config defaults to false (`core/src/Configuration.cs:43`). So a
  dialog pause does NOT freeze the kRPC server: other connections keep
  answering, and `KRPC.Paused` is readable AND writable as an RPC
  (`core/src/Service/KRPC/KRPC.cs:199-210`), so the harness can detect and
  clear a pause remotely. The WarpTo continuation itself keeps spinning
  during pause without progress (Planetarium time frozen) and resumes
  normally on unpause; it is not cancelled by pause.

### Why the past wedge happened

The Python client (`.venv/.../krpc/client.py:172-204`) is strictly
synchronous: `_invoke` takes `_rpc_connection_lock`, sends, and blocks on
`receive_message`. There is no async invoke, no request pipelining, and a
second thread sharing the same Client just queues on that lock. So a
blocking `warp_to` on the PRIMARY connection wedges every other RPC in the
process that uses that Client. Streams are the one exception: stream updates
arrive on a separate stream connection serviced by a daemon thread
(client.py:66-74), so already-created streams keep updating even while the
RPC channel is blocked; but you cannot create/remove streams or issue any
call (including a cancel) until the warp returns. With a modal dialog pause,
UT stops advancing, WarpTo never completes, and the single-connection client
is fully wedged: that is the observed mission-killer.

### Stream trick: ruled out

`core/src/Service/ProcedureCallStream.cs:19-20` throws
"Cannot create a stream for a procedure that does not return a value".
`WarpTo` returns void, so it cannot be hosted in a stream. (Mechanically a
stream WOULD re-run a yielded continuation each update, StreamContinuation.cs,
but the void gate blocks WarpTo specifically.)

## 2. Non-blocking trigger options from Python

(a) SECOND CONNECTION + background thread: fully supported. Each
`krpc.connect()` is an independent TCP pair (RPC + stream) with its own
server-side client identity; the scheduler facts above guarantee the
primary connection's polling is unaffected while the warp connection sits
in the WarpTo continuation. Cost: one extra TCP connect + `GetServices`
handshake at client construction (client.py:40-43); trivial. Cancel path:
`conn.close()` closes the socket (connection.py:20-22); the server drops
the continuation on the next update; then the primary sets
`RailsWarpFactor = 0` to stop the residual warp.

Conflict analysis (primary sets RailsWarpFactor while WarpTo runs): both
execute in the same per-FixedUpdate loop; the yielded WarpTo continuation
is already in `rpcContinuations` when new requests are appended
(Core.cs:486-489 swap + PollRequests append), so WarpTo runs first and the
primary's set lands after it within that frame; next frame WarpTo re-asserts
(one factor step per frame). Net effect: they fight, WarpTo effectively wins
within 1-2 frames. Also note the kRPC `RailsWarpFactor` setter zeroes main
throttle as a side effect (SpaceCenter.cs:736-744). Rule: while a warp
connection owns warp, the primary must not touch warp factors except as the
deliberate post-cancel cleanup.

(b) ASYNC INVOKE / BATCHING in the protocol: not available in the 0.5.4
Python client. The wire protocol does support multi-call requests
(`request.calls` is repeated; Core.cs logs `request.Calls`), but the Python
client always sends exactly one call and blocks for the response
(client.py:180-186). No public fire-and-forget surface exists.

(c) STREAM-BASED trick: ruled out (void return, section 1).

## 3. KSP native TimeWarp.fetch.WarpTo (1.12.5)

Decompile: TimeWarp class, Assembly-CSharp. Members cited by name.

- `public void WarpTo(double UT, double maxTimeWarping = 8.0,
  double minTimeWarping = 2.5)` (decompile lines 2161-2185): PUBLIC, stable,
  and genuinely fire-and-forget. It only sets `setAutoWarp = true` and
  stores `warpToUT` / min / max; it returns immediately. The two optional
  params are TIME-IN-WARP bounds in real seconds, not rates:
  `getMaxWarpRateForTravel` (2519-2583) picks the smallest rate index whose
  time-in-warp `<= maxTimeWarping` (default: aim to spend 2.5-8 real seconds
  warping).
- `Update()` consumes the flag (546-567): computes the rate index once and
  starts `StartCoroutine(autoWarpTo(UT, ...))`.
- `autoWarpTo` coroutine (2258-2517):
  - engages `autoWarpEngaged`, locks player controls with
    `ControlTypes.WARPTO_LOCK` ("TimeWarpTo") for the duration;
  - each FixedUpdate, while `current_rate_index < rateIdx`, calls
    `getMaxOnRailsRateIdx(rateIdx, lookAhead: true, ...)` (1473+) and steps
    UP only as far as allowed. That helper enforces: EVA-on-ladder,
    per-body altitude limits (`GetAltitudeLimit(i, body)` =
    `body.timeWarpAltitudeLimits[i]`, 1232-1235), atmosphere pressure,
    about-to-crash lookahead, and SOI-transition proximity
    (`ClampRateToOrbitTransitions(rate, orbit, maxIdx 3, 50 s)`, called at
    1924). So the rate steps back UP automatically after a temporary clamp
    (e.g. after leaving a low-altitude band or passing an SOI boundary).
  - DOWN-clamping mid-warp is not the coroutine's job: KSP's own
    `TimeWarp.FixedUpdate` (1063-1116) re-validates the current index every
    physics frame while on rails and not landed
    (`setRate(current_rate_index, instantChange:false, instantIfLower:true,
    forceSwitch:false)` at 1114, which routes through `getMaxOnRailsRateIdx`
    at 1301 and drops the rate, instantly for atmosphere/crash reasons). This
    runs regardless of who set the rate; it is the game-native answer to the
    "KSP silently clamps but never raises" problem: WarpTo's coroutine is
    exactly the missing raiser.
  - Arrival: it computes the deceleration start point from the warp-rate
    lerp physics (`getWarpAccel` / `getRateChangeTravel`, 2326-2328), breaks
    the loop there, REWINDS the clock slightly
    (`Planetarium.SetUniversalTime(num - fixedDeltaTime * 0.5)`, 2464) and
    ramps to 1x, logging over/undershoot. Frame-exact arrival, better than
    kRPC's stepper.
  - Cancellation: player TIME_WARP_STOP key (2329-2342), or a player
    warp-decrease/increase key that successfully changes the rate
    (2343-2418), calls `CancelAutoWarp` and aborts. `CancelAutoWarp(int
    rateIdx = -1, ...)` (2187-2256) is public and programmatically callable.
- Edge cases:
  - ALREADY ENGAGED: `WarpTo` while `autoWarpEngaged` posts a screen message
    and does NOTHING (2163-2180). No retarget; you must `CancelAutoWarp()`
    first, then re-issue.
  - TARGET UT IN THE PAST: `getMaxWarpRateForTravel` returns rate index 0
    for `warpDeltaTime < minTimeInWarp` (2524-2537), the coroutine's loop
    condition fails immediately and, because the index is already 0, the
    rewind branch is skipped. Harmless no-op (one frame of control lock).
  - SOI CHANGE: nothing cancels the coroutine; the per-frame revalidation
    switches to the new body's altitude table and the transition clamp
    bounds rate to index <= 3 within 50 s of the boundary, then steps back
    up. Warp-to-UT persists across SOI changes.
  - DIALOG PAUSE: the coroutine yields `WaitForFixedUpdate` (2268); with
    timescale 0 it simply stalls and resumes after unpause. Not cancelled.
  - MODE: it is rails-only logic; if the vessel is in atmosphere the rate
    stays clamped low by `getMaxOnRailsRateIdx`'s pressure check rather than
    switching to physics warp.
- NOT EXPOSED via kRPC 0.5.4 (grep: no caller of `fetch.WarpTo` in the kRPC
  repo; SpaceCenter.WarpTo rolls its own, section 1).

## 4. MechJeb's MechJebModuleWarpController

Decompile of MechJeb2 2.15.1.0, `MuMech.MechJebModuleWarpController`:

- `WarpToUT(double UT, double maxRate = -1)` (decompile 109-155): sets the
  persistent target `warpToUT` and issues one rate command; `OnFixedUpdate`
  (58-64) re-invokes `WarpToUT(warpToUT)` every physics frame while
  `warpToUT > 0`. Fire-and-forget from the caller's perspective; the module
  is always Enabled (constructor, line 28).
- Adaptation: target rate = seconds remaining (143), clamped to maxRate;
  `WarpRegularAtRate` (157-170) steps down when the current rate exceeds it
  and up when the next rate fits. `IncreaseRegularWarp` (222-256) checks
  `TimeWarp.fetch.GetAltitudeLimit(idx+1, mainBody)` against current
  altitude (236-244) plus a 2 s retry debounce, so up-steps honor altitude
  limits; down-clamps for altitude are again covered by KSP's own
  FixedUpdate revalidation. Below the factor-1 altitude limit it uses
  physics warp at up to 2x (146-149). `useQuickWarp` (123-140) additionally
  caps rate by time-to-patch-end (SOI awareness).
- Termination: when `UT <= now` it sets `warpToUT = 0` and stops commanding
  (113-117); the ramp-down happens naturally as remaining time shrinks. No
  clock rewind; arrival is approximate (typically within a few seconds of
  overshoot at high rates, less with useQuickWarp).
- Cancellation: `MinimumWarp()` (309-318) zeroes `warpToUT` and drops to
  rate 0. `OnUpdate` (49-56) also detects a player/manual rate change and
  releases its pause state.

### Exposing it through KRPC.MechJeb

- Confirmed NOT exposed in 0.8.1 (darchambault): `git grep -i
  "WarpController|MechJebModuleWarp"` over the repo returns nothing; the
  only warp mentions are `NodeExecutor.Autowarp` and AscentAutopilot's
  `WarpCountDown` settings.
- Wrapper cost is small. Pattern per `NodeExecutor.cs`: a `[KRPCClass]`
  ComputerModule subclass with static reflection handles resolved in
  `InitType` and `[KRPCMethod]`/`[KRPCProperty]` members. A
  `WarpController.cs` exposing `WarpToUT(double, double)`, `MinimumWarp()`,
  and the `warpToUT` property is ~70-90 lines, plus two lines in
  `MechJeb.cs` (`modules.Add("WarpController", new WarpController())` in
  `InitType`, one static accessor property; the registry resolves the
  MechJeb type by name via `GetComputerModule("WarpController")` ->
  "MechJebModuleWarpController", MechJeb.cs:144-146).
- BUT provisioning is the real cost. `pins.toml [krpc_mechjeb]` installs the
  PREBUILT release zip (downloadUrl + releaseZipSha256); the source clone at
  `.cache/krpc_mechjeb-src` exists only for the read-only pin identity
  assertion. Building a patched fork would need:
  1. our own fork/branch of darchambault/KRPC.MechJeb carrying the wrapper
     (new sourceRepo + commit pin), and
  2. a new provisioner phase modeled on BUILD-TT
     (`provision.py:phase_build_tt` 540-616 + `_build_testingtools`
     1865-1945): git-show/export the sources at the pin, author an SDK-style
     net472 shim csproj with HintPaths into the dev KSP `Managed` dir and
     the kRPC release refs (`_extract_krpc_refs` already extracts
     KRPC.Core/KRPC.SpaceCenter/Google.Protobuf; KRPC.MechJeb additionally
     references KRPC.dll, UnityEngine.dll, UnityEngine.CoreModule.dll, and
     Assembly-CSharp.dll per `KRPC.MechJeb.csproj`), `dotnet build`, assert,
     hash, install. All machinery exists but it is a real phase (~150-250
     lines of provisioner + pins churn + the fork to maintain). Estimate:
     1-2 days including verification, plus permanent fork upkeep.
- Available TODAY with zero changes: `MechJeb.NodeExecutor` with
  `Autowarp = true` + `ExecuteOneNode()` (NodeExecutor.cs:42-46, 60-63) is a
  non-blocking RPC that warps to and executes the next node using MechJeb's
  warp controller, cancellable via `Abort()`. If the mission machine is
  willing to let MechJeb do the burn, "warp to node" needs no new code at
  all.

## 5. Ranking and recommendation

| Path | Non-blocking | Native adaptation | Impl cost | Wedge risk | Provisioning |
|---|---|---|---|---|---|
| A. Second connection running SpaceCenter.WarpTo | yes (primary unaffected) | good: altitude-aware up/down + KSP FixedUpdate clamp; no exact-UT snap | tiny (Python only) | low: cancel = close warp socket + factor 0; pause does not freeze server (PauseServerWithGame=false) | none |
| B. Parsek command-seam verb calling TimeWarp.fetch.WarpTo | yes (seam is file-drop) | best: the exact stock mechanism incl. SOI clamp + exact-UT rewind | small C# (2 verbs) in our own mod | low: CancelAutoWarp verb; pause stalls but harness can unpause via KRPC.Paused | none (Parsek already built+deployed) |
| C. KRPC.MechJeb fork exposing WarpController | yes | good (MechJeb stepper + KSP clamp; no exact snap) | ~80 line wrapper BUT fork + new build phase | low-medium (MinimumWarp cancel; reflection breakage risk on MechJeb bumps) | highest: new BUILD phase + fork upkeep |
| D. Status quo poll-driven RailsWarpFactor re-command | yes | ours to maintain (already replicated tables) | sunk | known | none |
| (D2) NodeExecutor.Autowarp for warp-to-node only | yes | good | zero | Abort() available | none |

RECOMMENDED: Path A (second-connection WarpTo thread) as the immediate
mechanism, with D2 opportunistically for node execution if MechJeb flies
the burn anyway. Path B is the best long-term fidelity play if we ever
need stock-exact arrival or want to drop the second connection; it is our
own mod and the M-A2 seam already has verb precedent (`TimeJump` uses
`Planetarium.SetUniversalTime`; a `WarpToUT`/`CancelWarp` verb pair calling
`TimeWarp.fetch.WarpTo` / `CancelAutoWarp` follows the same shape). Path C
is strictly dominated by B for us: same "write C# + own the deploy" class
of work, but in someone else's reflection-based codebase plus a new
provisioner phase.

### Integration sketch for Path A

```python
import threading, krpc

class WarpService:
    """Owns a dedicated kRPC connection whose only job is to sit inside
    SpaceCenter.WarpTo. The primary connection never blocks."""

    def __init__(self, address, rpc_port, stream_port):
        self._addr = (address, rpc_port, stream_port)
        self._conn = None
        self._thread = None
        self._error = None

    @property
    def active(self):
        return self._thread is not None and self._thread.is_alive()

    def warp_to(self, ut, max_rails_rate=100000.0, max_physics_rate=2.0):
        if self.active:
            raise RuntimeError("warp already in progress; cancel first")
        a, rp, sp = self._addr
        # stream port 0: no stream connection needed on the warp client
        self._conn = krpc.connect(name="warp", address=a,
                                  rpc_port=rp, stream_port=sp)
        self._error = None

        def _run():
            try:
                self._conn.space_center.warp_to(
                    ut, max_rails_rate, max_physics_rate)
            except Exception as exc:   # socket closed by cancel(), or RPC error
                self._error = exc

        self._thread = threading.Thread(target=_run, daemon=True)
        self._thread.start()

    def cancel(self, primary_conn):
        """Hard-cancel: drop the warp connection (server discards the
        continuation next FixedUpdate, Core.cs:455), then zero the rate
        from the primary connection."""
        if self._conn is not None:
            try:
                self._conn.close()
            finally:
                self._conn = None
        if self._thread is not None:
            self._thread.join(timeout=5)
            self._thread = None
        primary_conn.space_center.rails_warp_factor = 0
        primary_conn.space_center.physics_warp_factor = 0

    def poll_done(self):
        """Non-blocking completion check for the fly loop."""
        if self.active:
            return False
        err = self._error
        self._error = None
        if err is not None:
            raise err
        return True
```

Fly-loop contract:

- While `warp.active`, the loop keeps polling telemetry on the primary
  connection (RPCs and streams both keep working; per-connection
  serialization, Core.cs:683-698).
- Watchdogs: (1) if `KRPC.Paused` (krpc.paused on the primary) turns true
  mid-warp, either unpause (`conn.krpc.paused = False`) or `cancel()`;
  (2) if `ut_now` stops advancing while unpaused, or the active vessel
  changes/dies (WarpTo will surface an RPCError through `poll_done`), call
  `cancel()`; (3) absolute deadline = expected arrival + margin.
- Do not touch `rails_warp_factor` from the primary while a warp is active
  (last-writer-per-frame fight, and the setter zeroes throttle;
  SpaceCenter.cs:736-744). Delete the per-body altitude-limit table logic;
  it is subsumed by `MaximumRailsWarpFactor` inside the server stepper plus
  KSP's own FixedUpdate clamp.
- Cost per warp: one TCP connect + GetServices handshake (tens of ms).
  Reusing one long-lived warp connection is also fine; it only matters that
  it is not the telemetry connection.

### Residual risks, Path A

- No graceful remote cancel exists in the protocol (WarpTo takes no token);
  socket-drop is the documented-behavior cancel (continuation discarded for
  disconnected clients). After a drop, warp keeps running at the last rate
  until the primary zeroes it: always pair close() with the factor reset.
- kRPC's stepper leaves warp at factor 0 only on natural completion; on
  client error paths assume nothing and reset factors.
- Arrival precision is a ramp-down, not stock's exact-UT snap. For "warp to
  node minus lead time" this is fine (pad the target UT by a few seconds);
  if we ever need frame-exact arrival, that is the trigger to build Path B.
