using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace Parsek.InGameTests
{
    /// <summary>
    /// Live checks of a replayed ghost's engine plumes and FX objects, sized to run after a
    /// Re-Fly, a revert or a rewind (todo NEXT-TESTS-TO-BUILD-2026-11-11 item 7, audit B8;
    /// lane FXR-1). Three cells:
    ///
    /// <para>(1) every live primary ghost engine holds the power its recording's consumed
    /// part events give it (<see cref="GhostFxIntegrity.ExpectedEnginePower"/>), and while the
    /// ghost, the part and the ghost's FX are shown its plume is lit exactly when that power is
    /// above zero; (2) every particle system and KSP emitter an engine or RCS info holds is
    /// alive, sits under its own ghost's root and belongs to one ghost part only, no recording
    /// is held by two primary ghosts, and no <c>Parsek_Timeline_</c> ghost root is left in the
    /// scene without an owner; (3) no live ghost replays a recording the timeline holds
    /// inactive (superseded by a Re-Fly merge or retired by a rewind), and each ghost slot's
    /// recording id is the committed recording at that index.</para>
    ///
    /// <para>Each cell skips with a named reason when the scene holds nothing to judge, so a
    /// lane that pins <c>passed=N</c> reds when the context it set up is missing.</para>
    /// </summary>
    public class GhostFxIntegrityInGameTests
    {
        private static readonly CultureInfo IC = CultureInfo.InvariantCulture;
        private const string Tag = "GhostFxIntegrity";
        private const string GhostRootPrefix = "Parsek_Timeline_";

        /// <summary>Real-time budget of the engine sampling loop.</summary>
        internal const float MaxSampleSeconds = 4f;

        /// <summary>Stop the engine sampling loop after this many frames.</summary>
        internal const int TargetFrames = 30;

        /// <summary>Bound on per-engine detail lines.</summary>
        private const int MaxDetailLines = 20;

        /// <summary>Real-time budget for the first live ghost to appear. A batch starts by
        /// tearing every timeline ghost down (<c>PerformBetweenRunCleanup</c>), so each cell
        /// first waits for the playback engine to rebuild one.</summary>
        internal const float GhostWaitSeconds = 8f;

        [InGameTest(Category = "GhostFxIntegrity", Scene = GameScenes.FLIGHT,
            Description = "Every live ghost engine holds the power its recording's consumed part events give it (last engine event per part and module, decouple and destroy zero it, no engine event at all auto-starts), and while shown its plume is lit exactly when that power is above zero; a key fails on 2 consecutive sampled frames (lane FXR-1, audit B8)")]
        public IEnumerator LiveGhostEngineFxMatchesRecordedEvents()
        {
            ParsekFlight flight = ParsekFlight.Instance;
            if (flight == null || flight.Engine == null)
            {
                InGameAssert.Skip("needs a ParsekFlight instance with a playback engine (FLIGHT scene)");
                yield break;
            }

            yield return WaitForLiveGhost(flight);

            var c = new EngineCounters();
            var streak = new Dictionary<string, int>(StringComparer.Ordinal);
            var failedKeys = new HashSet<string>(StringComparer.Ordinal);
            var failures = new List<string>();
            var detailed = new HashSet<string>(StringComparer.Ordinal);
            int detailLines = 0;
            int frames = 0;
            float started = Time.realtimeSinceStartup;

            while (Time.realtimeSinceStartup - started < MaxSampleSeconds && frames < TargetFrames)
            {
                yield return new WaitForEndOfFrame();
                if (ParsekFlight.Instance != flight || flight.Engine == null)
                    break;
                frames++;
                SampleEngines(flight.Engine, c, streak, failedKeys, failures, detailed, ref detailLines);
            }

            ParsekLog.Info(Tag, GhostFxIntegrity.FormatEngineSummary(
                HighLogic.LoadedScene.ToString(), frames, c.MaxGhosts, c.MaxEngines, c.Judged, c.Ok,
                failedKeys.Count, c.Lit, c.Dark, c.FxHidden, c.Unsupported));

            if (failures.Count > 0)
            {
                InGameAssert.Fail(failedKeys.Count.ToString(IC)
                    + " ghost engine(s) disagree with their recording: "
                    + string.Join(" | ", failures.ToArray()));
                yield break;
            }
            if (c.Judged == 0)
            {
                InGameAssert.Skip("no live ghost engine was judged in " + frames.ToString(IC)
                    + " frames (ghosts=" + c.MaxGhosts.ToString(IC)
                    + " unsupported=" + c.Unsupported.ToString(IC) + ")");
                yield break;
            }
        }

        private sealed class EngineCounters
        {
            public int MaxGhosts, MaxEngines, Judged, Ok, Lit, Dark, FxHidden, Unsupported;
        }

        private static void SampleEngines(GhostPlaybackEngine engine, EngineCounters c,
            Dictionary<string, int> streak, HashSet<string> failedKeys, List<string> failures,
            HashSet<string> detailed, ref int detailLines)
        {
            // [ERS-exempt] (folder allowlisted): ghost slots are raw committed-list indices.
            IReadOnlyList<Recording> committed = RecordingStore.CommittedRecordings;
            int ghosts = 0;
            int engines = 0;
            var seenThisFrame = new HashSet<string>(StringComparer.Ordinal);

            foreach (var kv in engine.ghostStates)
            {
                GhostPlaybackState state = kv.Value;
                if (state == null || state.ghost == null || state.engineInfos == null
                    || state.engineInfos.Count == 0)
                    continue;
                int idx = kv.Key;
                if (committed == null || idx < 0 || idx >= committed.Count || committed[idx] == null
                    || !string.Equals(committed[idx].RecordingId, state.recordingId, StringComparison.Ordinal))
                {
                    c.Unsupported++;
                    continue;
                }
                Recording rec = committed[idx];
                if (rec.LoopPlayback)
                {
                    // A loop cycle re-primes the cursor and the orphan auto-start on its own
                    // schedule; this cell judges one-shot replays.
                    c.Unsupported++;
                    continue;
                }
                ghosts++;
                bool ghostShown = state.ghost.activeInHierarchy && !state.visualFxSuppressed
                    && !state.pauseHidden && !state.deferVisibilityUntilPlaybackSync;

                foreach (var e in state.engineInfos)
                {
                    EngineGhostInfo info = e.Value;
                    if (info == null) continue;
                    engines++;
                    float expected = GhostFxIntegrity.ExpectedEnginePower(
                        rec.PartEvents, state.partEventIndex, info.partPersistentId, info.moduleIndex,
                        state.partTree);
                    Transform partT = GhostVisualBuilder.FindGhostPartTransform(
                        state.ghost, info.partPersistentId);
                    bool fxVisible = ghostShown && partT != null && partT.gameObject.activeInHierarchy;
                    int systems, playing;
                    CountPlume(info, out systems, out playing);

                    GhostFxIntegrity.EngineVerdict verdict = GhostFxIntegrity.JudgeEngine(
                        expected, info.currentPower, fxVisible, systems, playing);
                    c.Judged++;
                    if (!fxVisible) c.FxHidden++;
                    else if (expected > 0f && playing > 0) c.Lit++;
                    else if (expected <= 0f && playing == 0) c.Dark++;

                    string key = (state.recordingId ?? "?") + "/" + e.Key.ToString(IC);
                    seenThisFrame.Add(key);
                    string line = "rec=" + (state.recordingId ?? "?")
                        + " vessel='" + (state.vesselName ?? "?") + "'"
                        + " part='" + (info.partName ?? "?") + "'"
                        + " pid=" + info.partPersistentId.ToString(IC)
                        + " midx=" + info.moduleIndex.ToString(IC)
                        + " cursor=" + state.partEventIndex.ToString(IC)
                        + " expected=" + expected.ToString("0.###", IC)
                        + " live=" + info.currentPower.ToString("0.###", IC)
                        + " fxVisible=" + (fxVisible ? "1" : "0")
                        + " systems=" + systems.ToString(IC)
                        + " playing=" + playing.ToString(IC);

                    if (verdict == GhostFxIntegrity.EngineVerdict.Ok)
                    {
                        c.Ok++;
                        streak[key] = 0;
                        if (detailLines < MaxDetailLines && detailed.Add(key))
                        {
                            detailLines++;
                            ParsekLog.Verbose(Tag, "engine ok " + line);
                        }
                        continue;
                    }

                    int run;
                    streak.TryGetValue(key, out run);
                    run++;
                    streak[key] = run;
                    if (run >= GhostFxIntegrity.MismatchFramesToFail && failedKeys.Add(key))
                    {
                        string message = verdict + " " + line;
                        if (failures.Count < 10) failures.Add(message);
                        ParsekLog.Warn(Tag, "engine " + message);
                    }
                }
            }

            // A key that is not seen on a frame (ghost destroyed, slot moved) starts over.
            var stale = new List<string>();
            foreach (var key in streak.Keys)
                if (!seenThisFrame.Contains(key)) stale.Add(key);
            for (int i = 0; i < stale.Count; i++) streak[stale[i]] = 0;

            if (ghosts > c.MaxGhosts) c.MaxGhosts = ghosts;
            if (engines > c.MaxEngines) c.MaxEngines = engines;
        }

        /// <summary>Counts the plume pieces that can show (alive, on an active object) and
        /// how many of them are emitting: a Unity system that is playing or a KSP emitter
        /// whose <c>emit</c> flag is set.</summary>
        private static void CountPlume(EngineGhostInfo info, out int systems, out int playing)
        {
            systems = 0;
            playing = 0;
            for (int i = 0; info.particleSystems != null && i < info.particleSystems.Count; i++)
            {
                ParticleSystem ps = info.particleSystems[i];
                if (ps == null || !ps.gameObject.activeInHierarchy) continue;
                systems++;
                if (ps.isPlaying) playing++;
            }
            for (int i = 0; info.kspEmitters != null && i < info.kspEmitters.Count; i++)
            {
                KspEmitterRef r = info.kspEmitters[i];
                if (r.emitter == null || r.emitField == null || !r.emitter.gameObject.activeInHierarchy)
                    continue;
                systems++;
                object emit = null;
                try { emit = r.emitField.GetValue(r.emitter); }
                catch (Exception) { emit = null; }
                if (emit is bool && (bool)emit) playing++;
            }
        }

        [InGameTest(Category = "GhostFxIntegrity", Scene = GameScenes.FLIGHT,
            Description = "Every particle system and KSP emitter a live ghost's engine or RCS info holds is alive, under its own ghost's root and held by one ghost part only; no recording is held by two primary ghosts; no Parsek_Timeline_ ghost root stays in the scene unowned on two sampled frames (lane FXR-1, audit B8)")]
        public IEnumerator LiveGhostFxSystemsOwnedOnce()
        {
            ParsekFlight flight = ParsekFlight.Instance;
            if (flight == null || flight.Engine == null)
            {
                InGameAssert.Skip("needs a ParsekFlight instance with a playback engine (FLIGHT scene)");
                yield break;
            }

            yield return WaitForLiveGhost(flight);
            yield return new WaitForEndOfFrame();
            if (ParsekFlight.Instance != flight || flight.Engine == null)
            {
                InGameAssert.Skip("the flight scene changed while sampling");
                yield break;
            }
            GhostPlaybackEngine engine = flight.Engine;

            var refs = new List<GhostFxIntegrity.FxSystemRef>();
            var primaryIds = new List<string>();
            int ghosts = 0;
            foreach (var kv in engine.ghostStates)
            {
                GhostPlaybackState state = kv.Value;
                if (state == null || state.ghost == null) continue;
                ghosts++;
                primaryIds.Add(state.recordingId);
                CollectRefs(state, "#" + kv.Key.ToString(IC), refs);
            }
            foreach (var kv in engine.overlapGhosts)
            {
                if (kv.Value == null) continue;
                for (int i = 0; i < kv.Value.Count; i++)
                {
                    GhostPlaybackState state = kv.Value[i];
                    if (state == null || state.ghost == null) continue;
                    ghosts++;
                    CollectRefs(state, "#" + kv.Key.ToString(IC) + "/overlap" + i.ToString(IC), refs);
                }
            }
            List<string> unownedFirst = UnownedGhostRoots(engine);

            yield return new WaitForEndOfFrame();
            List<string> unowned = (ParsekFlight.Instance == flight && flight.Engine != null)
                ? GhostFxIntegrity.InBothSamples(unownedFirst, UnownedGhostRoots(flight.Engine))
                : new List<string>();

            GhostFxIntegrity.OwnershipCounts counts = GhostFxIntegrity.CountOwnership(refs);
            List<string> shared = GhostFxIntegrity.SharedRecordingIds(primaryIds);
            ParsekLog.Info(Tag, GhostFxIntegrity.FormatOwnershipSummary(
                HighLogic.LoadedScene.ToString(), ghosts, counts, shared.Count, unowned.Count));

            var problems = new List<string>(counts.Problems);
            for (int i = 0; i < shared.Count; i++)
                problems.Add("recording " + shared[i] + " held by two primary ghosts");
            for (int i = 0; i < unowned.Count; i++)
                problems.Add("ghost root '" + unowned[i] + "' in the scene with no engine owner");
            for (int i = 0; i < problems.Count && i < MaxDetailLines; i++)
                ParsekLog.Warn(Tag, "fx ownership problem: " + problems[i]);

            if (!counts.Clean || shared.Count > 0 || unowned.Count > 0)
            {
                InGameAssert.Fail("ghost FX ownership: " + string.Join(" | ", problems.ToArray()));
                yield break;
            }
            if (counts.Systems == 0)
            {
                InGameAssert.Skip("no live ghost holds an engine or RCS FX system (ghosts="
                    + ghosts.ToString(IC) + ")");
                yield break;
            }
        }

        private static void CollectRefs(GhostPlaybackState state, string ghostKey,
            List<GhostFxIntegrity.FxSystemRef> refs)
        {
            Transform root = state.ghost.transform;
            if (state.engineInfos != null)
            {
                foreach (var info in state.engineInfos.Values)
                {
                    if (info == null) continue;
                    AddSystems(info.particleSystems, info.kspEmitters, info.partPersistentId, ghostKey,
                        root, refs);
                }
            }
            if (state.rcsInfos != null)
            {
                foreach (var info in state.rcsInfos.Values)
                {
                    if (info == null) continue;
                    AddSystems(info.particleSystems, info.kspEmitters, info.partPersistentId, ghostKey,
                        root, refs);
                }
            }
        }

        private static void AddSystems(List<ParticleSystem> systems, List<KspEmitterRef> emitters,
            uint pid, string ghostKey, Transform root, List<GhostFxIntegrity.FxSystemRef> refs)
        {
            for (int i = 0; systems != null && i < systems.Count; i++)
            {
                ParticleSystem ps = systems[i];
                bool destroyed = ps == null;
                refs.Add(new GhostFxIntegrity.FxSystemRef
                {
                    GhostKey = ghostKey,
                    PartPid = pid,
                    InstanceId = destroyed ? 0 : ps.GetInstanceID(),
                    Destroyed = destroyed,
                    UnderOwnGhost = !destroyed && ps.transform.IsChildOf(root),
                });
            }
            for (int i = 0; emitters != null && i < emitters.Count; i++)
            {
                MonoBehaviour em = emitters[i].emitter;
                bool destroyed = em == null;
                refs.Add(new GhostFxIntegrity.FxSystemRef
                {
                    GhostKey = ghostKey,
                    PartPid = pid,
                    InstanceId = destroyed ? 0 : em.GetInstanceID(),
                    Destroyed = destroyed,
                    UnderOwnGhost = !destroyed && em.transform.IsChildOf(root),
                });
            }
        }

        private static List<string> UnownedGhostRoots(GhostPlaybackEngine engine)
        {
            var names = new List<string>();
            GameObject[] roots =
                UnityEngine.SceneManagement.SceneManager.GetActiveScene().GetRootGameObjects();
            for (int i = 0; i < roots.Length; i++)
            {
                GameObject go = roots[i];
                if (go == null || go.name == null
                    || !go.name.StartsWith(GhostRootPrefix, StringComparison.Ordinal))
                    continue;
                if (!engine.OwnsGhostGameObject(go))
                    names.Add(go.name + "@" + go.GetInstanceID().ToString(IC));
            }
            return names;
        }

        [InGameTest(Category = "GhostFxIntegrity", Scene = GameScenes.FLIGHT,
            Description = "No live ghost (primary or overlap) replays a recording the timeline holds inactive (superseded by a Re-Fly merge, or retired by a rewind), and every ghost slot holds the committed recording at its index (lane FXR-1, audit B8)")]
        public IEnumerator NoLiveGhostReplaysAnInactiveRecording()
        {
            ParsekFlight flight = ParsekFlight.Instance;
            if (flight == null || flight.Engine == null)
            {
                InGameAssert.Skip("needs a ParsekFlight playback engine and a ParsekScenario (FLIGHT scene)");
                yield break;
            }
            yield return WaitForLiveGhost(flight);
            ParsekScenario scenario = ParsekScenario.Instance;
            if (ParsekFlight.Instance != flight || flight.Engine == null
                || object.ReferenceEquals(null, scenario))
            {
                InGameAssert.Skip("needs a ParsekFlight playback engine and a ParsekScenario (FLIGHT scene)");
                yield break;
            }

            // [ERS-exempt] (folder allowlisted): the inactive set is computed over the raw
            // committed list, exactly as the flight playback host computes it.
            IReadOnlyList<Recording> committed = RecordingStore.CommittedRecordings;
            Dictionary<string, TimelineInactiveReason> inactive =
                EffectiveState.ComputeTimelineInactiveRecordingIds(
                    committed, scenario.RecordingSupersedes, scenario.RecordingRewindRetirements);
            int inactiveIds = 0;
            foreach (var v in inactive.Values)
                if (v != TimelineInactiveReason.None) inactiveIds++;

            GhostPlaybackEngine engine = flight.Engine;
            var liveIds = new List<string>();
            var mismatches = new List<string>();
            int ghosts = 0;
            foreach (var kv in engine.ghostStates)
            {
                GhostPlaybackState state = kv.Value;
                if (state == null || state.ghost == null) continue;
                ghosts++;
                liveIds.Add(state.recordingId);
                CheckIndex(committed, kv.Key, state, mismatches);
            }
            foreach (var kv in engine.overlapGhosts)
            {
                if (kv.Value == null) continue;
                for (int i = 0; i < kv.Value.Count; i++)
                {
                    GhostPlaybackState state = kv.Value[i];
                    if (state == null || state.ghost == null) continue;
                    ghosts++;
                    liveIds.Add(state.recordingId);
                    CheckIndex(committed, kv.Key, state, mismatches);
                }
            }

            List<string> replaying = GhostFxIntegrity.LiveInactive(liveIds, inactive);
            int supersedeRows = scenario.RecordingSupersedes != null ? scenario.RecordingSupersedes.Count : 0;
            ParsekLog.Info(Tag, GhostFxIntegrity.FormatSupersedeSummary(
                HighLogic.LoadedScene.ToString(), ghosts, supersedeRows, inactiveIds,
                replaying.Count, mismatches.Count));

            if (replaying.Count > 0 || mismatches.Count > 0)
            {
                var problems = new List<string>();
                for (int i = 0; i < replaying.Count; i++)
                    problems.Add("live ghost replays inactive recording " + replaying[i]);
                problems.AddRange(mismatches);
                for (int i = 0; i < problems.Count && i < MaxDetailLines; i++)
                    ParsekLog.Warn(Tag, "inactive recording problem: " + problems[i]);
                InGameAssert.Fail(string.Join(" | ", problems.ToArray()));
                yield break;
            }
            if (inactiveIds == 0)
            {
                InGameAssert.Skip("no supersede row or rewind retirement marks a committed recording inactive");
                yield break;
            }
            if (ghosts == 0)
            {
                InGameAssert.Skip("no live ghost to judge (" + inactiveIds.ToString(IC)
                    + " inactive recording(s))");
                yield break;
            }
        }

        /// <summary>Yields until the engine holds a built, shown primary ghost, or the
        /// <see cref="GhostWaitSeconds"/> budget runs out (the cell then judges what is
        /// there, and skips when that is nothing).</summary>
        private static IEnumerator WaitForLiveGhost(ParsekFlight flight)
        {
            float started = Time.realtimeSinceStartup;
            int waited = 0;
            while (Time.realtimeSinceStartup - started < GhostWaitSeconds)
            {
                if (ParsekFlight.Instance != flight || flight.Engine == null)
                    yield break;
                foreach (var state in flight.Engine.ghostStates.Values)
                {
                    if (state != null && state.ghost != null && state.ghost.activeInHierarchy
                        && !state.deferVisibilityUntilPlaybackSync)
                    {
                        if (waited > 0)
                            ParsekLog.Verbose(Tag, "live ghost after " + waited.ToString(IC) + " frame(s)");
                        yield break;
                    }
                }
                waited++;
                yield return null;
            }
            ParsekLog.Verbose(Tag, "no live ghost after " + waited.ToString(IC) + " frame(s)");
        }

        private static void CheckIndex(IReadOnlyList<Recording> committed, int idx,
            GhostPlaybackState state, List<string> mismatches)
        {
            string committedId = committed != null && idx >= 0 && idx < committed.Count
                && committed[idx] != null ? committed[idx].RecordingId : null;
            if (string.IsNullOrEmpty(state.recordingId)) return;
            if (!string.Equals(committedId, state.recordingId, StringComparison.Ordinal))
                mismatches.Add("ghost slot #" + idx.ToString(IC) + " holds " + state.recordingId
                    + " where the committed list has " + (committedId ?? "nothing"));
        }
    }
}
