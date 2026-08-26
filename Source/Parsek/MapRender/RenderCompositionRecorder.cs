using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace Parsek.MapRender
{
    /// <summary>
    /// M-A7 Layer 1 RECORDER: the thin, env-gated subscriber that feeds the pure
    /// <see cref="RenderCompositionManifest"/> accumulation core from the seams the render pipeline
    /// already exposes, and writes the manifest out
    /// (docs/dev/design-autotest-render-composition.md).
    ///
    /// <para><b>Gating.</b> <c>PARSEK_RENDER_MANIFEST=1</c>, read ONCE at Awake (the M-A3
    /// <c>AutorunHooks</c> / M-A2 command-seam pattern). Unset = every hook returns on a cached bool
    /// before any work or allocation, <see cref="Update"/> returns immediately, nothing is written
    /// anywhere. <see cref="ForceEnabledForTesting"/> mirrors
    /// <c>MapRenderTrace.ForceEnabledForTesting</c> so an in-game cell can arm the recorder mid
    /// session (the env var cannot be re-read).</para>
    ///
    /// <para><b>Deliberately independent of <c>mapRenderTracing</c></b> (the tracer is a
    /// player-visible setting; this is an automation-only env hook). The manifest header records the
    /// live tracing state, because two capture families - the tangent-seam evaluation and the probe's
    /// seam-endpoint sampling - are tracing-gated at their own evaluation sites and are simply ABSENT
    /// from a manifest-only run. The Python verifier treats that as a DEFINED unevaluable, never a
    /// pass; harness lanes arm both.</para>
    ///
    /// <para><b>ECall isolation.</b> Every Unity-native read lives in its own tiny NoInlining method
    /// (the <c>MapRenderProbe</c> discipline), so mono's JIT never walks an ECall while the pure core
    /// is exercised headlessly.</para>
    ///
    /// <para><b>No gameplay effect, ever.</b> Nothing here touches warp, camera, or render state; the
    /// hooks are single guarded calls at existing decision points and add no branching to any render
    /// path.</para>
    /// </summary>
    [KSPAddon(KSPAddon.Startup.Instantly, true /* once */)]
    internal sealed class RenderCompositionRecorder : MonoBehaviour
    {
        private const string Tag = "RenderManifest";

        /// <summary>The environment variable that arms the recorder.</summary>
        internal const string EnvVarName = "PARSEK_RENDER_MANIFEST";

        /// <summary>The ONLY value that arms it (exact match, fail-closed).</summary>
        internal const string ArmValue = "1";

        /// <summary>The manifest file, written to the KSP root beside <c>parsek-test-results.txt</c>.</summary>
        internal const string ManifestFileName = "parsek-render-manifest.txt";

        internal const string ReasonVerb = "verb";
        internal const string ReasonSceneExit = "scene-exit";
        internal const string ReasonProcessTeardown = "process-teardown";

        internal const string ErrorNotArmed = "manifest-not-armed";

        private static RenderCompositionRecorder instance;
        private static bool envArmed;

        /// <summary>Test / in-game-cell override; mirrors <c>MapRenderTrace.ForceEnabledForTesting</c>.</summary>
        internal static bool ForceEnabledForTesting;

        /// <summary>True when the recorder is accumulating. Checked first in EVERY hook.</summary>
        internal static bool IsEnabled => ForceEnabledForTesting || envArmed;

        /// <summary>True when the env var was armed at Awake (recorded in the manifest header).</summary>
        internal static bool EnvArmed => envArmed;

        /// <summary>
        /// STICKY was-ever-on latch behind the manifest header's <c>mapRenderTracingOn</c> key: true
        /// once <c>MapRenderTrace.IsEnabled</c> has been observed true on ANY armed frame since the
        /// last <see cref="Reset"/>, and never cleared by the tracer going quiet again.
        ///
        /// <para>WHY STICKY. Two of the manifest's capture families - the rigid tangent evaluation
        /// and the probe's seam-endpoint sampling - are tracing-gated at their own sites, so the
        /// header bit exists to tell a consumer whether the ABSENCE of those records is evidence or a
        /// missing instrument. Read at the export instant it answered the wrong question: a teardown
        /// export can land after the setting was cleared (or after a scene bounce re-read it) and
        /// stamp <c>False</c> onto a manifest full of tracing-gated records, which the Python
        /// verifier then reads as <c>seam-data-unavailable-tracing-off</c>. The latch is cleared in
        /// <see cref="Reset"/> alongside the records themselves, so it never describes a population
        /// it did not accumulate with.</para>
        /// </summary>
        private static bool mapRenderTracingWasEverOn;

        /// <summary>The sticky bit's current value (header input + test seam).</summary>
        internal static bool MapRenderTracingWasEverOn => mapRenderTracingWasEverOn;

        /// <summary>
        /// Latches <see cref="MapRenderTracingWasEverOn"/>. Called once per ARMED frame (and once
        /// more at export, so a manifest taken before the first armed Update still reports honestly).
        /// Cost on an already-latched frame is one static bool read and a predicted branch; the
        /// unlatched case adds <c>MapRenderTrace.IsEnabled</c>, itself a static bool plus a settings
        /// field read. Nothing here touches Unity, so the headless cells can drive it directly.
        /// </summary>
        internal static void LatchMapRenderTracing()
        {
            if (!mapRenderTracingWasEverOn && MapRenderTrace.IsEnabled)
                mapRenderTracingWasEverOn = true;
        }

        /// <summary>PURE fail-closed env gate: ONLY the literal <c>"1"</c> arms the recorder.</summary>
        internal static bool IsArmed(string envValue) => envValue == ArmValue;

        private static readonly RenderCompositionManifest manifest = new RenderCompositionManifest();

        static RenderCompositionRecorder()
        {
            // The core is Unity-free by contract, so the ONE late Unity read it needs (the marker
            // re-sample at dwell close) is installed from here as a delegate. A headless manifest
            // built directly in a test leaves it null and closes dwells with the stamped triple.
            manifest.MarkerResampler = ResampleMarkerAtDwellClose;
        }

        /// <summary>
        /// The late marker read <see cref="RenderCompositionManifest"/> calls when a dwell closes with
        /// a <c>false</c> marker decision. ECall-isolated + fail-closed: on any throw it reports "not
        /// sampled" so the stamped triple is left exactly as the Director wrote it.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool ResampleMarkerAtDwellClose(
            uint pid, out bool decision, out bool tracedPath, out bool polyline, out bool iconSuppressed)
        {
            decision = false;
            tracedPath = false;
            polyline = false;
            iconSuppressed = false;
            try
            {
                decision = GhostMapPresence.ShouldDrawNonProtoMarkerForGhost(
                    pid, out tracedPath, out polyline, out iconSuppressed);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        // ---- recorder-owned state (never the tracer's dicts - copy the pattern, not the storage) ----
        private static readonly Dictionary<string, string> planSignatureByHost =
            new Dictionary<string, string>(StringComparer.Ordinal);
        private static readonly Dictionary<string, GhostPlaybackLogic.LoopUnitSet> unitsByHost =
            new Dictionary<string, GhostPlaybackLogic.LoopUnitSet>(StringComparer.Ordinal);
        private static readonly Dictionary<uint, string> chainSignatureByPid = new Dictionary<uint, string>();
        private static readonly Dictionary<uint, string[]> phaseKindsByPid = new Dictionary<uint, string[]>();
        private static readonly Dictionary<uint, TruthSample> truthByPid = new Dictionary<uint, TruthSample>();
        private static readonly HashSet<string> previousDrewSet = new HashSet<string>(StringComparer.Ordinal);
        private static readonly List<string> appearedScratch = new List<string>();
        private static readonly List<string> disappearedScratch = new List<string>();
        private static readonly Dictionary<string, bool> reaimWindowSeen =
            new Dictionary<string, bool>(StringComparer.Ordinal);

        private struct TruthSample
        {
            public string Body;
            public double X;
            public double Y;
            public double Z;
        }

        // ---- Hold-run detection thresholds --------------------------------------------------
        // A hold is OBSERVED, never recomputed: the render clock stops advancing while live time keeps
        // running. The detector ACCUMULATES stalled live time rather than testing one frame step, which
        // is what makes it work at BOTH ends of the warp range. At 1x no single frame step is anywhere
        // near a second, so a per-frame floor could never fire; and a warp DROP in the middle of a hold
        // shortens the steps without ending the hold, which a per-frame floor would misread as a
        // release. Accumulation sees the same run either way, because the run's identity is the frozen
        // render clock, not the size of the live step.
        //
        // The stationarity test is RELATIVE, not a fixed absolute window. A hold freezes the render
        // clock EXACTLY (the hold formula returns a constant phase), so near-zero is the true signal;
        // ordinary advance is one live step of clock per live step of time. The epsilon below is only
        // a CEILING on the window - the window actually applied is
        // min(epsilon, 0.5 * liveStep), so an ordinary 1x frame (live step ~0.02 s, loop step ~0.02 s)
        // can never fall inside it, and a high-warp frame is judged against its own step rather than
        // against a constant. A fixed 0.25 s window would have been ABOVE ordinary 1x clock advance,
        // so plain 1x playback accumulated a phantom stall and a real hold that released at 1x never
        // emitted its release.
        //
        // The stall floor is what separates a real hold from an ordinary hitch; a hold shorter than it
        // produces NO pair and is below-resolution to the verifier, never a mismatch (the RC-COVER
        // resolution model).
        internal const double HoldStationaryLoopUtEpsilonSeconds = 0.25;
        internal const double HoldMinStallSeconds = 5.0;

        /// <summary>Per-unit clock-run state: the hold detector plus the per-owner last-emitted clock
        /// state that keeps the periodic kinds from building a debounce key on unchanged frames.</summary>
        private struct UnitClockState
        {
            public bool HavePrevious;
            public double PreviousLoopUT;
            public double PreviousUT;
            public long PreviousCycleIndex;

            /// <summary>True while consecutive frames have shown a stationary render clock.</summary>
            public bool InStall;
            /// <summary>Accumulated LIVE seconds of the current stall.</summary>
            public double StallSeconds;
            /// <summary>The last MOVING frame that preceded the stall (its live UT / loopUT / cycle).</summary>
            public double StallStartUT;
            public double StallStartLoopUT;
            public long StallCycleIndex;

            /// <summary>True once the stall crossed <see cref="HoldMinStallSeconds"/> and engaged.</summary>
            public bool HoldEngaged;
            /// <summary>The engaged run's 0-based ordinal within its (owner, cycle).</summary>
            public int HoldOrdinal;
            public bool HaveOrdinalCycle;
            public long OrdinalCycle;
            public int NextOrdinal;

            // Last-emitted periodic clock state (allocation guard, NOT a second debounce: the
            // manifest's dedupe table stays the authority for rare kinds and for re-visits).
            public bool HaveEmittedCycle;
            public long LastEmittedCycle;
            public bool HaveEmittedTail;
            public long LastEmittedTailCycle;
            public bool HaveEmittedSecondary;
            public long LastEmittedSecondaryCycle;

            // ---- Inter-cycle-tail FALLBACK close (armed here, applied a frame later) --------
            // Armed when the `inter-cycle-tail` clock event is emitted; applied on a STRICTLY
            // LATER frame. See TailCloseFallbackIsDue for why the deferral is what makes the
            // fallback byte-invariant on a run whose frame-sampled close already worked.
            /// <summary>True while a tail close is armed and not yet applied.</summary>
            public bool HavePendingTailClose;
            /// <summary>The emitted tail event's UT - the instant a stale dwell closes AT.</summary>
            public double PendingTailCloseUT;
            /// <summary>The ENDING cycle's start instant, which bounds the sweep below - see
            /// <see cref="ApplyPendingTailClose"/>. Without it the sweep reaches back into earlier
            /// cycles whose dwells are legitimately left open.</summary>
            public double PendingTailCloseCycleStartUT;

            /// <summary>The UT at which the CURRENT cycle's rollover was emitted.</summary>
            public bool HaveCycleStartUT;
            public double CycleStartUT;
        }

        private static readonly Dictionary<int, UnitClockState> clockStateByOwner =
            new Dictionary<int, UnitClockState>();

        /// <summary>The latest sampled recorded-clock instant per unit (owner index -> loopUT).</summary>
        private static readonly Dictionary<int, double> loopUtByOwner = new Dictionary<int, double>();

        // ---- Armed-path allocation guards ---------------------------------------------------
        // Every hook below runs once per armed ghost per frame. None of them ALLOCATES on a frame that
        // changed nothing; the manifest's own debounces stay in place as the correctness backstop, and
        // these only decide whether it is worth building the string the backstop would compare.

        /// <summary>Memoized committedIndex -> ownerIndex, rebuilt only when a plan signature or a
        /// host's unit-set instance changes (a linear walk of every host's OwnerByIndex per dwell
        /// frame is otherwise the single most expensive thing on the armed path).</summary>
        private static readonly Dictionary<int, int> ownerIndexByCommittedIndex = new Dictionary<int, int>();
        private static bool ownerIndexMapValid;

        /// <summary>The last line-branch decision triple per pid, compared field-wise BEFORE any
        /// allocation, recId lookup or enum ToString.</summary>
        private struct LineBranchDecision
        {
            public bool Have;
            public string Reason;
            public bool LineActive;
            public int Coverage;
        }

        private static readonly Dictionary<uint, LineBranchDecision> lineBranchDecisionByPid =
            new Dictionary<uint, LineBranchDecision>();

        // ---- Enum token tables ---------------------------------------------------------------
        // Enum.ToString() allocates + reflects on EVERY call. These tables spell each token ONCE at
        // static init, indexed by the enum's own integer value. They are built by ENUMERATING the enum
        // rather than by writing the member names in source, which is load-bearing for
        // RenderWindowCoverage: the LineBlinkWindowExitExemptionTests source gate matches
        // `RenderWindowCoverage\s*\.\s*(Inside|Outside)` and reserves those spellings for the four
        // measuring decision sites in GhostOrbitLinePatch.
        private static readonly string[] renderWindowCoverageTokens =
            BuildEnumTokens(typeof(MapRenderTrace.RenderWindowCoverage));
        private static readonly string[] treatmentTokens = BuildEnumTokens(typeof(Treatment));
        private static readonly string[] coverageTokens = BuildEnumTokens(typeof(Coverage));

        private static string[] BuildEnumTokens(Type enumType)
        {
            Array values = Enum.GetValues(enumType);
            int max = 0;
            for (int i = 0; i < values.Length; i++)
            {
                int v = Convert.ToInt32(values.GetValue(i), CultureInfo.InvariantCulture);
                if (v > max) max = v;
            }
            var table = new string[max + 1];
            for (int i = 0; i < values.Length; i++)
            {
                object value = values.GetValue(i);
                int v = Convert.ToInt32(value, CultureInfo.InvariantCulture);
                if (v >= 0)
                    table[v] = value.ToString();
            }
            return table;
        }

        private static string TokenOf(string[] table, int value)
            => (value >= 0 && value < table.Length && table[value] != null)
                ? table[value] : value.ToString(CultureInfo.InvariantCulture);

        /// <summary>
        /// Test seam: the token this recorder would emit for one value of one of the three tabled
        /// enums. Exists so a cell can prove the tables equal <c>Enum.ToString()</c> for every value
        /// WITHOUT spelling any member name in test source either.
        /// </summary>
        internal static string EnumTokenForTesting(Type enumType, object value)
        {
            int v = Convert.ToInt32(value, CultureInfo.InvariantCulture);
            if (enumType == typeof(MapRenderTrace.RenderWindowCoverage))
                return TokenOf(renderWindowCoverageTokens, v);
            if (enumType == typeof(Treatment))
                return TokenOf(treatmentTokens, v);
            if (enumType == typeof(Coverage))
                return TokenOf(coverageTokens, v);
            throw new ArgumentOutOfRangeException(nameof(enumType), "no token table for " + enumType);
        }

        /// <summary>Clears every accumulated record + recorder-owned state (tests + scene lifecycle).</summary>
        internal static void Reset()
        {
            manifest.Reset();
            planSignatureByHost.Clear();
            unitsByHost.Clear();
            chainSignatureByPid.Clear();
            phaseKindsByPid.Clear();
            truthByPid.Clear();
            previousDrewSet.Clear();
            appearedScratch.Clear();
            disappearedScratch.Clear();
            reaimWindowSeen.Clear();
            clockStateByOwner.Clear();
            loopUtByOwner.Clear();
            ownerIndexByCommittedIndex.Clear();
            ownerIndexMapValid = false;
            lineBranchDecisionByPid.Clear();
            // The sticky tracing bit describes the records above, so it dies with them.
            mapRenderTracingWasEverOn = false;
        }

        /// <summary>Test-only: the accumulation core the hooks feed.</summary>
        internal static RenderCompositionManifest ManifestForTesting => manifest;

        // =====================================================================================
        //  Addon lifecycle
        // =====================================================================================

        void Awake()
        {
            if (instance != null)
            {
                Destroy(gameObject);
                return;
            }
            instance = this;
            DontDestroyOnLoad(gameObject);

            // Read ONCE. The env var is the entire arm gate; it is never re-read.
            string envValue = ReadEnvVar();
            envArmed = IsArmed(envValue);

            if (envArmed)
            {
                ParsekLog.Info(Tag, "armed (" + EnvVarName + "=1): render composition manifest recording");
                GameEvents.onGameSceneSwitchRequested.Add(OnGameSceneSwitchRequested);
                GameEvents.onGameStateLoad.Add(OnGameStateLoad);
            }
            else
            {
                ParsekLog.Info(Tag, "inert: " + EnvVarName + "="
                    + (envValue == null ? "(unset)" : "'" + envValue + "'"));
            }
        }

        void OnDestroy()
        {
            if (instance != this)
                return;
            if (envArmed)
            {
                GameEvents.onGameSceneSwitchRequested.Remove(OnGameSceneSwitchRequested);
                GameEvents.onGameStateLoad.Remove(OnGameStateLoad);
                TryAutoFlush(ReasonProcessTeardown);
            }
            instance = null;
        }

        /// <summary>
        /// Scene-exit flush. Fires BEFORE the switch, so the manifest is written from live state.
        /// Deliberately NOT piggybacked on <c>MapRenderProbe</c>'s handler (separate ownership).
        /// </summary>
        private void OnGameSceneSwitchRequested(GameEvents.FromToAction<GameScenes, GameScenes> action)
        {
            TryAutoFlush(ReasonSceneExit);
            Reset();
        }

        /// <summary>
        /// Cross-save PARTITION: a manifest spanning a save load would mix recording-id namespaces, so
        /// the accumulation is dropped here rather than appended to. The scene switch that precedes a
        /// load has already flushed, so nothing observed is lost.
        /// </summary>
        private void OnGameStateLoad(ConfigNode node)
        {
            ParsekLog.Info(Tag, "partition: game state load - dropping "
                + manifest.ClosedDwellCount.ToString(CultureInfo.InvariantCulture)
                + " accumulated dwell(s) so recording-id namespaces never mix");
            Reset();
        }

        void Update()
        {
            // Provably inert when unarmed: return on the cached bool before any work.
            if (!IsEnabled)
                return;
            // Sticky was-ever-on tracing latch: sampled here rather than at export because the
            // export instant is not when the tracing-gated records were captured (see the field).
            LatchMapRenderTracing();
            SampleUnitClocks();
        }

        // =====================================================================================
        //  Per-frame unit-clock sampling (capture point 4)
        // =====================================================================================

        /// <summary>
        /// One <c>ComputeSpanLoopFrame</c> call per known unit per frame (armed-only cost) to derive
        /// the cycle-rollover / inter-cycle-tail / boundary-overlap-secondary clock events. The pure
        /// clock is NEVER modified - it is called, not hooked (purity contract + hot path).
        /// </summary>
        private static void SampleUnitClocks()
        {
            if (unitsByHost.Count == 0)
                return;
            double ut = CurrentUT();
            if (double.IsNaN(ut) || double.IsInfinity(ut) || ut <= 0.0)
                return;

            foreach (KeyValuePair<string, GhostPlaybackLogic.LoopUnitSet> hostEntry in unitsByHost)
            {
                GhostPlaybackLogic.LoopUnitSet units = hostEntry.Value;
                if (units == null || units.Count == 0)
                    continue;
                foreach (KeyValuePair<int, GhostPlaybackLogic.LoopUnit> kv in units.UnitsByOwner)
                {
                    GhostPlaybackLogic.LoopUnit unit = kv.Value;
                    GhostPlaybackLogic.SpanLoopFrame frame = GhostPlaybackLogic.ComputeSpanLoopFrame(
                        ut, unit.PhaseAnchorUT, unit.SpanStartUT, unit.SpanEndUT, unit.CadenceSeconds,
                        unit.RelaunchSchedule, unit.LoiterCuts,
                        unit.ArrivalHoldSeconds, unit.ArrivalHoldAtUT, unit.ArrivalAlignPeriodSeconds,
                        unit.LaunchBodyRotationPeriodSeconds, unit.LaunchHoldEngaged, unit.RecordedSoiExitUT,
                        unit.ArrivalJointSecondaryPeriodSeconds, unit.ArrivalJointSecondaryToleranceSeconds,
                        unit.ArrivalJointMaxWholeHoldPeriods);
                    if (!frame.Resolved)
                        continue;
                    ObserveUnitFrame(unit.OwnerIndex, ut, in frame);
                }
            }
        }

        /// <summary>
        /// One unit's per-frame clock observation. Loads the unit's state ONCE, emits only the periodic
        /// events whose subject actually changed since this owner's last emit, folds in the hold-run
        /// detector, and stores the state back.
        ///
        /// <para>The per-owner last-emitted fields are an ALLOCATION guard, not a second debounce: on
        /// an unchanged frame they skip the <c>BuildClockEventKey</c> string concat that would
        /// otherwise run once per unit per frame for the whole session. The manifest's dedupe table is
        /// still the authority - it catches re-visits (a cycle index seen, left, and returned to) that
        /// a single last-value field cannot.</para>
        /// </summary>
        private static void ObserveUnitFrame(
            int ownerIndex, double ut, in GhostPlaybackLogic.SpanLoopFrame frame)
        {
            clockStateByOwner.TryGetValue(ownerIndex, out UnitClockState st);

            // A tail close armed on an EARLIER frame becomes due here, before this frame emits
            // anything of its own. Deliberately first: the fallback's whole safety argument is that
            // it never runs on the frame that armed it.
            if (st.HavePendingTailClose && TailCloseFallbackIsDue(st.PendingTailCloseUT, ut))
            {
                ApplyPendingTailClose(
                    ownerIndex, st.PendingTailCloseUT, st.PendingTailCloseCycleStartUT);
                st.HavePendingTailClose = false;
                st.PendingTailCloseUT = 0.0;
                st.PendingTailCloseCycleStartUT = 0.0;
            }

            if (!st.HaveEmittedCycle || st.LastEmittedCycle != frame.CycleIndex)
            {
                st.HaveEmittedCycle = true;
                st.LastEmittedCycle = frame.CycleIndex;
                // The cycle's start instant, remembered ONLY to bound the tail fallback's sweep.
                // Nothing else reads it and no record carries it.
                st.HaveCycleStartUT = true;
                st.CycleStartUT = ut;
                manifest.AppendClockEventIfChanged(
                    RenderCompositionManifest.ClockCycleRollover,
                    ownerIndex, frame.CycleIndex, ut,
                    0.0, frame.LoopUT, 0.0, null);
            }

            if (frame.IsInInterCycleTail
                && (!st.HaveEmittedTail || st.LastEmittedTailCycle != frame.CycleIndex))
            {
                st.HaveEmittedTail = true;
                st.LastEmittedTailCycle = frame.CycleIndex;
                manifest.AppendClockEventIfChanged(
                    RenderCompositionManifest.ClockInterCycleTail,
                    ownerIndex, frame.CycleIndex, ut,
                    0.0, frame.LoopUT, 0.0, null);
                // ARM the fallback close at THIS instant. Nothing is closed yet - see
                // TailCloseFallbackIsDue. The sweep is bounded BELOW by the ending cycle's own
                // start: an owner accumulates open dwells from earlier cycles that are left open
                // on purpose, and they are not this tail's to retire. With no rollover seen yet
                // (arming before any cycle event) the bound is the event itself, which sweeps
                // nothing - the conservative direction.
                st.HavePendingTailClose = true;
                st.PendingTailCloseUT = ut;
                st.PendingTailCloseCycleStartUT = st.HaveCycleStartUT ? st.CycleStartUT : ut;
            }

            if (frame.HasSecondary
                && (!st.HaveEmittedSecondary || st.LastEmittedSecondaryCycle != frame.SecondaryCycleIndex))
            {
                st.HaveEmittedSecondary = true;
                st.LastEmittedSecondaryCycle = frame.SecondaryCycleIndex;
                AppendBoundaryOverlapSecondary(manifest, ownerIndex, ut, in frame);
            }

            loopUtByOwner[ownerIndex] = frame.LoopUT;
            ObserveUnitHoldRun(ownerIndex, ref st, ut, frame.LoopUT, frame.CycleIndex);
            clockStateByOwner[ownerIndex] = st;
        }

        // =====================================================================================
        //  Inter-cycle-tail FALLBACK close
        //  (bug V6M-CYCLE0-ARRIVALLOITER-DWELL-CLOSE-RECORD-LOST)
        // =====================================================================================

        /// <summary>
        /// Is an armed tail close DUE at <paramref name="nowUT"/>? Only on a STRICTLY LATER frame
        /// than the one that armed it, and that deferral is the whole safety argument - not a
        /// nicety.
        ///
        /// <para><b>The defect this exists for.</b> A dwell is closed by
        /// <c>RenderCompositionManifest.ObserveDwellFrame</c> only when a LATER frame arrives for the
        /// same pid carrying a CHANGED identity. A per-cycle ghost that stops being sampled while its
        /// dwell is open therefore never gets a close, and the dwell runs to export as
        /// <c>openAtExport</c>. The verifier keeps open dwells out of the CLOSED population by
        /// design, so a cycle whose ArrivalLoiter dwell lost only its CLOSE reads as a cycle that
        /// never had one - which is how a correctly rendered loop turned into an RC-CYCLE
        /// non-isomorphism on 1 of 6 archived flights. Measured: on the red flight the ghost was
        /// observed ZERO times in the <c>7 -&gt; -1</c> successor state, against 4/6/7/7/10/11/15
        /// frames on the green ones, and it was the sparsest-sampling flight of the six.</para>
        ///
        /// <para><b>Why the close cannot happen on the arming frame.</b> The recorder's
        /// <c>Update</c> and the Director render path that feeds <c>NoteDirectorIntent</c> are two
        /// MonoBehaviour callbacks with NO pinned relative order (the ownerIndex stamp in
        /// <c>NoteDirectorIntent</c> says so in as many words: the latest per-unit sample is "this
        /// frame's, or, if the recorder's Update trails this render path, the immediately preceding
        /// frame's"). Closing at the emission instant would therefore be correct in one order and
        /// WRONG in the other: if the clock leads the render path, the still-open dwell on the
        /// arming frame is the one the render path is about to close itself, and closing it here
        /// would steal its <c>TRANSITION</c> - the successor would open with no prior and emit none.
        /// A frame later the ambiguity is gone: the render path has had a whole frame to run, so a
        /// live ghost has been sampled in its successor state and is no longer stale, while a ghost
        /// that really did stop being sampled still is. The fallback then fires on exactly the shape
        /// it was written for and is byte-invariant on every run whose frame-sampled close worked.</para>
        ///
        /// <para>NaN/Inf on either side answers false: an unusable instant is not a licence to
        /// retire a record.</para>
        /// </summary>
        internal static bool TailCloseFallbackIsDue(double pendingEventUT, double nowUT)
        {
            if (double.IsNaN(pendingEventUT) || double.IsInfinity(pendingEventUT))
                return false;
            if (double.IsNaN(nowUT) || double.IsInfinity(nowUT))
                return false;
            return nowUT > pendingEventUT;
        }

        /// <summary>
        /// Applies one owner's due tail close and logs what it retired. The manifest core is
        /// deliberately log-free, so it reports what it closed and the reporting happens here.
        /// A run with nothing stale closes nothing and logs nothing, which is every run whose
        /// frame-sampled close already landed.
        /// </summary>
        private static void ApplyPendingTailClose(
            int ownerIndex, double eventUT, double cycleStartUT)
        {
            int closed = manifest.FallbackCloseStaleOwnerDwells(
                ownerIndex, eventUT, cycleStartUT, out uint firstPid, out string firstPhaseKind);
            if (closed <= 0)
                return;
            ParsekLog.Info(Tag, string.Format(CultureInfo.InvariantCulture,
                "dwell fallback-close owner={0} eventUT={1} cycleStartUT={2} closed={3}"
                + " firstPid={4} firstPhase={5}"
                + " reason=inter-cycle-tail-stale: the dwell was never sampled in its successor"
                + " state, so the frame-sampled close could not run",
                ownerIndex.ToString(CultureInfo.InvariantCulture),
                eventUT.ToString("R", CultureInfo.InvariantCulture),
                cycleStartUT.ToString("R", CultureInfo.InvariantCulture),
                closed.ToString(CultureInfo.InvariantCulture),
                firstPid.ToString(CultureInfo.InvariantCulture),
                firstPhaseKind ?? "none"));
        }

        /// <summary>
        /// THE PINNED boundary-overlap-secondary convention (schema v1.1, supervisor decision 6),
        /// factored out of the sampling loop so it is drivable from a test with a REAL
        /// <see cref="GhostPlaybackLogic.SpanLoopFrame"/> rather than pinned by reading source text.
        ///
        /// <para><c>cycleIndex</c> is the PRIMARY cycle index (<c>frame.CycleIndex</c>, the continuing
        /// instance N the camera follows) and <c>detailA</c> is the SECONDARY cycle index
        /// (<c>frame.SecondaryCycleIndex</c>, == N+1 by construction: the early-launching concurrent
        /// ghost). <c>detailB</c> is the secondary's loopUT and <c>detailS</c> the decision token. A
        /// reader that took <c>cycleIndex</c> for the secondary would recompute the boundary-overlap
        /// gate one window early, which is exactly the mistake this convention exists to prevent.</para>
        /// </summary>
        internal static void AppendBoundaryOverlapSecondary(
            RenderCompositionManifest sink, int ownerIndex, double ut,
            in GhostPlaybackLogic.SpanLoopFrame frame)
        {
            if (sink == null || !frame.Resolved || !frame.HasSecondary)
                return;
            sink.AppendClockEventIfChanged(
                RenderCompositionManifest.ClockBoundaryOverlapSecondary,
                ownerIndex, frame.CycleIndex, ut,
                frame.SecondaryCycleIndex, frame.SecondaryLoopUT, 0.0, "secondary-live");
        }

        /// <summary>
        /// OBSERVATION-DERIVED hold detection: a run of frames in which live UT keeps advancing while
        /// the unit's render clock stands still. The pair is a MEASUREMENT, not a re-run of the hold
        /// formula - a leg that recomputed the formula the product ran would prove nothing.
        ///
        /// <para><b>Progress accumulation, not frame-step stationarity.</b> Each frame whose render
        /// clock did not move (RELATIVE test: <c>|delta loopUT| &lt; min(epsilon, 0.5 * liveStep)</c>,
        /// so ordinary 1x advance never qualifies and a genuine freeze always does) adds its LIVE step
        /// to a stall accumulator; a frame on which the clock advances resets it. The run ENGAGES the
        /// moment the accumulator crosses
        /// <see cref="HoldMinStallSeconds"/> and is emitted RETROACTIVELY, stamped at the stall's start
        /// (the last moving frame's UT, loopUT and cycle) rather than at the crossing. That is what
        /// makes the detector warp-independent: at 1x the stall is many small steps that add up, and a
        /// warp DROP mid-hold merely shrinks the steps - the render clock is still frozen, so the same
        /// run continues instead of reading as a release and a second engage.</para>
        ///
        /// <para><b>Multiple stalls per cycle.</b> Every run gets a 0-based ORDINAL within its
        /// (owner, cycle), carried as <c>detailA</c> on BOTH the engage and the release, so a cycle
        /// that holds twice produces two distinguishable pairs instead of one collapsed by the
        /// debounce. <c>cycleIndex</c> is the cycle at engage on both events.</para>
        ///
        /// <para>The held duration reported on release is the accumulated live seconds of the stall,
        /// accurate to within one local frame step at each end.</para>
        /// </summary>
        private static void ObserveUnitHoldRun(
            int ownerIndex, ref UnitClockState st, double ut, double loopUT, long cycleIndex)
        {
            if (double.IsNaN(loopUT) || double.IsInfinity(loopUT)
                || double.IsNaN(ut) || double.IsInfinity(ut))
                return;

            if (st.HavePrevious)
            {
                double liveStep = ut - st.PreviousUT;

                // A frame that carried no live time carries no evidence either way: it can neither
                // extend a stall (nothing accumulated) nor prove the clock resumed. Leave the run
                // state untouched and just re-stamp the previous sample.
                if (!(liveStep > 0.0))
                {
                    st.HavePrevious = true;
                    st.PreviousLoopUT = loopUT;
                    st.PreviousUT = ut;
                    st.PreviousCycleIndex = cycleIndex;
                    return;
                }

                // RELATIVE stationarity - see the const block above. The window is the epsilon
                // CEILING clamped to half this frame's live step, so ordinary advance (one second of
                // clock per second of live time) is never inside it at ANY warp, while a genuine
                // freeze (delta exactly 0) always is.
                double window = System.Math.Min(HoldStationaryLoopUtEpsilonSeconds, 0.5 * liveStep);
                bool clockStationary = System.Math.Abs(loopUT - st.PreviousLoopUT) < window;

                if (clockStationary)
                {
                    if (!st.InStall)
                    {
                        st.InStall = true;
                        st.StallSeconds = 0.0;
                        st.StallStartUT = st.PreviousUT;
                        st.StallStartLoopUT = st.PreviousLoopUT;
                        st.StallCycleIndex = st.PreviousCycleIndex;
                    }
                    st.StallSeconds += liveStep;

                    if (!st.HoldEngaged && st.StallSeconds >= HoldMinStallSeconds)
                    {
                        st.HoldEngaged = true;
                        st.HoldOrdinal = NextHoldOrdinal(ref st, st.StallCycleIndex);
                        manifest.AppendClockEventIfChanged(
                            RenderCompositionManifest.ClockHoldEngage,
                            ownerIndex, st.StallCycleIndex, st.StallStartUT,
                            st.HoldOrdinal, st.StallStartLoopUT, 0.0, null);
                    }
                }
                else
                {
                    if (st.HoldEngaged)
                    {
                        manifest.AppendClockEventIfChanged(
                            RenderCompositionManifest.ClockHoldRelease,
                            ownerIndex, st.StallCycleIndex, ut,
                            st.HoldOrdinal, st.StallStartLoopUT, st.StallSeconds, null);
                    }
                    st.HoldEngaged = false;
                    st.InStall = false;
                    st.StallSeconds = 0.0;
                }
            }

            st.HavePrevious = true;
            st.PreviousLoopUT = loopUT;
            st.PreviousUT = ut;
            st.PreviousCycleIndex = cycleIndex;
        }

        /// <summary>
        /// The next 0-based run ordinal for <paramref name="cycleIndex"/>, restarting at 0 whenever the
        /// engaging cycle differs from the one the counter is tracking. Ordinals are per (owner, cycle)
        /// because that is exactly the tuple the clock-event debounce keys on.
        /// </summary>
        private static int NextHoldOrdinal(ref UnitClockState st, long cycleIndex)
        {
            if (!st.HaveOrdinalCycle || st.OrdinalCycle != cycleIndex)
            {
                st.HaveOrdinalCycle = true;
                st.OrdinalCycle = cycleIndex;
                st.NextOrdinal = 0;
            }
            int ordinal = st.NextOrdinal;
            st.NextOrdinal = ordinal + 1;
            return ordinal;
        }

        /// <summary>
        /// Test seam for the hold detector: drives ONE observed frame for one owner through the exact
        /// production path (state load, per-frame emit decisions, hold run, state store), so a cell can
        /// measure the run detector without a Unity clock.
        /// </summary>
        internal static void ObserveHoldFrameForTesting(
            int ownerIndex, double ut, double loopUT, long cycleIndex)
        {
            clockStateByOwner.TryGetValue(ownerIndex, out UnitClockState st);
            ObserveUnitHoldRun(ownerIndex, ref st, ut, loopUT, cycleIndex);
            clockStateByOwner[ownerIndex] = st;
        }

        /// <summary>
        /// Resolves the owning unit's owner index for a committed member index through the live unit
        /// sets' <c>OwnerByIndex</c> map (schema v1.1, supervisor decision 5). Returns false for an
        /// index no unit claims, which is why the DWELL key is OMITTED rather than defaulted.
        /// </summary>
        private static bool TryResolveOwnerIndex(int committedIndex, out int ownerIndex)
        {
            ownerIndex = 0;
            if (committedIndex < 0)
                return false;
            if (!ownerIndexMapValid)
                RebuildOwnerIndexMap();
            return ownerIndexByCommittedIndex.TryGetValue(committedIndex, out ownerIndex);
        }

        /// <summary>
        /// Rebuilds the committedIndex -> ownerIndex memo from every host's <c>OwnerByIndex</c>. Only
        /// ever runs after a plan signature (or a host's unit-set instance) changed, so the
        /// per-dwell-frame cost is one dictionary probe instead of a walk over every host's whole map.
        /// FIRST host wins on a collision, which is the same precedence the walk it replaces had.
        /// </summary>
        private static void RebuildOwnerIndexMap()
        {
            ownerIndexByCommittedIndex.Clear();
            foreach (KeyValuePair<string, GhostPlaybackLogic.LoopUnitSet> hostEntry in unitsByHost)
            {
                GhostPlaybackLogic.LoopUnitSet units = hostEntry.Value;
                if (units == null)
                    continue;
                foreach (KeyValuePair<int, int> kv in units.OwnerByIndex)
                {
                    if (!ownerIndexByCommittedIndex.ContainsKey(kv.Key))
                        ownerIndexByCommittedIndex[kv.Key] = kv.Value;
                }
            }
            ownerIndexMapValid = true;
        }

        // =====================================================================================
        //  H1-H3: PLAN capture (once per host per builder signature)
        // =====================================================================================

        internal static void NotePlan(
            string host, string signature, GhostPlaybackLogic.LoopUnitSet units,
            IReadOnlyList<Recording> committed, IReadOnlyList<Mission> missions)
        {
            if (!IsEnabled)
                return;
            if (string.IsNullOrEmpty(host) || units == null)
                return;

            // Invalidate the owner-index memo whenever this host's unit-set INSTANCE changes, not just
            // when its signature does: the memo is derived from OwnerByIndex, so a rebuilt set with an
            // unchanged signature would otherwise be read through a stale map.
            if (!unitsByHost.TryGetValue(host, out GhostPlaybackLogic.LoopUnitSet priorUnits)
                || !ReferenceEquals(priorUnits, units))
                ownerIndexMapValid = false;
            unitsByHost[host] = units;

            if (planSignatureByHost.TryGetValue(host, out string prior)
                && string.Equals(prior, signature, StringComparison.Ordinal))
                return;
            planSignatureByHost[host] = signature;
            ownerIndexMapValid = false;

            long sigHash = RenderCompositionManifest.StableHash(signature);
            int emitted = 0, skipped = 0;
            foreach (KeyValuePair<int, GhostPlaybackLogic.LoopUnit> kv in units.UnitsByOwner)
            {
                var rec = BuildPlanUnitRecord(host, sigHash, kv.Value, committed, missions);
                if (rec == null || manifest.AppendPlanUnit(rec) < 0) skipped++; else emitted++;
            }

            ParsekLog.Verbose(Tag, string.Format(CultureInfo.InvariantCulture,
                "plan captured: host={0} units={1} skipped={2} sigHash={3}",
                host, emitted, skipped, sigHash));
        }

        private static RenderCompositionManifest.PlanUnitRecord BuildPlanUnitRecord(
            string host, long sigHash, GhostPlaybackLogic.LoopUnit unit,
            IReadOnlyList<Recording> committed, IReadOnlyList<Mission> missions)
        {
            var rec = new RenderCompositionManifest.PlanUnitRecord
            {
                Host = host,
                SignatureHash = sigHash,
                OwnerIndex = unit.OwnerIndex,
                SpanStartUT = unit.SpanStartUT,
                SpanEndUT = unit.SpanEndUT,
                CadenceSeconds = unit.CadenceSeconds,
                OverlapCadenceSeconds = unit.OverlapCadenceSeconds,
                PhaseAnchorUT = unit.PhaseAnchorUT,
                IsReaim = unit.IsReaim,
                HasRelaunchSchedule = unit.RelaunchSchedule != null,
                ArrivalHoldSeconds = unit.ArrivalHoldSeconds,
                ArrivalHoldAtUT = unit.ArrivalHoldAtUT,
                ArrivalAlignPeriodSeconds = unit.ArrivalAlignPeriodSeconds,
                ArrivalJointSecondaryPeriodSeconds = unit.ArrivalJointSecondaryPeriodSeconds,
                ArrivalJointSecondaryToleranceSeconds = unit.ArrivalJointSecondaryToleranceSeconds,
                ArrivalJointMaxWholeHoldPeriods = unit.ArrivalJointMaxWholeHoldPeriods,
                ArrivalAmberReason = unit.ArrivalAmberReason,
                LaunchBodyRotationPeriodSeconds = unit.LaunchBodyRotationPeriodSeconds,
                LaunchHoldEngaged = unit.LaunchHoldEngaged,
                RecordedSoiExitUT = unit.RecordedSoiExitUT,
                RecordedDeorbitUT = unit.RecordedDeorbitUT,
                DescentEndUT = unit.DescentEndUT,
                DestinationBodyRotationPeriodSeconds = unit.DestinationBodyRotationPeriodSeconds,
                LoiterPeriodSeconds = unit.LoiterPeriodSeconds,
                CaptureShiftSeconds = unit.CaptureShiftSeconds,
                ParkingConicEndUT = unit.ParkingConicEndUT,
                TransferMemberIndex = unit.TransferMemberIndex,
                FirstDeorbitLegStartUT = unit.FirstDeorbitLegStartUT,
                TransferMemberRecordingId = unit.TransferMemberRecordingId,
            };

            int[] members = unit.MemberIndices;
            if (members != null)
            {
                for (int i = 0; i < members.Length; i++)
                {
                    int idx = members[i];
                    Recording r = (committed != null && idx >= 0 && idx < committed.Count)
                        ? committed[idx] : null;
                    double fallbackStart = r != null ? r.StartUT : double.NaN;
                    double fallbackEnd = r != null ? r.EndUT : double.NaN;
                    rec.Members.Add(new RenderCompositionManifest.PlanMemberRecord
                    {
                        Index = idx,
                        RecId = r != null ? r.RecordingId : "",
                        StartUT = unit.MemberStartUT(idx, fallbackStart),
                        EndUT = unit.MemberEndUT(idx, fallbackEnd),
                    });
                }
            }

            IReadOnlyList<GhostPlaybackLogic.LoopCut> cuts = unit.LoiterCuts;
            if (cuts != null)
            {
                for (int i = 0; i < cuts.Count; i++)
                {
                    rec.LoiterCuts.Add(new RenderCompositionManifest.PlanCutRecord
                    {
                        StartUT = cuts[i].StartUT,
                        LengthSeconds = cuts[i].LengthSeconds,
                    });
                }
            }

            int[] descent = unit.DescentMemberIndices;
            if (descent != null && descent.Length > 0)
                rec.DescentMemberIndices = JoinInts(descent);

            // The two body NAMES (schema v1.1, supervisor decision 1). Only a re-aim unit's ReaimPlan
            // states them, so they are emitted for that population and omitted for every other - never
            // guessed from a period, which is exactly the near-miss heuristic they exist to replace.
            if (unit.IsReaim && unit.ReaimPlan.HasValue)
            {
                Parsek.Reaim.ReaimMissionPlan plan = unit.ReaimPlan.Value;
                rec.LaunchBodyName = plan.LaunchBody;
                rec.DestinationBodyName = plan.TargetBody;
            }

            if (unit.ReaimSchedule.HasValue)
            {
                Parsek.Reaim.ReaimWindowPlanner.ReaimWindowSchedule s = unit.ReaimSchedule.Value;
                rec.ReaimSchedule = new RenderCompositionManifest.PlanReaimScheduleRecord
                {
                    FirstDepartureUT = s.FirstDepartureUT,
                    SynodicPeriodSeconds = s.SynodicPeriodSeconds,
                    TofSeconds = s.TofSeconds,
                    PhaseAnchorUT = s.PhaseAnchorUT,
                    CadenceSeconds = s.CadenceSeconds,
                    Prograde = s.Prograde,
                };
            }

            rec.Route = TryBuildPlanRouteRecord(unit, committed, missions);
            return rec;
        }

        /// <summary>
        /// Resolves the committed <c>Route</c> backing this unit, when any: a route-backed unit's
        /// members come from the route's own <c>RecordingIds</c>, so one membership intersection
        /// identifies it without reaching into the mission store's tree topology. Returns null for a
        /// manual (non-route) mission unit.
        /// </summary>
        private static RenderCompositionManifest.PlanRouteRecord TryBuildPlanRouteRecord(
            GhostPlaybackLogic.LoopUnit unit, IReadOnlyList<Recording> committed,
            IReadOnlyList<Mission> missions)
        {
            int[] members = unit.MemberIndices;
            if (members == null || members.Length == 0 || committed == null)
                return null;

            IReadOnlyList<Parsek.Logistics.Route> routes = Parsek.Logistics.RouteStore.CommittedRoutes;
            if (routes == null || routes.Count == 0)
                return null;

            for (int ri = 0; ri < routes.Count; ri++)
            {
                Parsek.Logistics.Route route = routes[ri];
                if (route == null || !route.IsLoopRoute || route.RecordingIds == null)
                    continue;
                bool match = false;
                for (int m = 0; m < members.Length && !match; m++)
                {
                    int idx = members[m];
                    if (idx < 0 || idx >= committed.Count || committed[idx] == null)
                        continue;
                    string recId = committed[idx].RecordingId;
                    for (int k = 0; k < route.RecordingIds.Count; k++)
                    {
                        if (string.Equals(route.RecordingIds[k], recId, StringComparison.Ordinal))
                        { match = true; break; }
                    }
                }
                if (!match)
                    continue;

                List<string> bodies = null;
                if (route.DispatchWindowPeriod == 0.0)
                {
                    bodies = new List<string>(members.Length);
                    for (int m = 0; m < members.Length; m++)
                    {
                        int idx = members[m];
                        if (idx < 0 || idx >= committed.Count || committed[idx] == null)
                            continue;
                        Recording r = committed[idx];
                        string body = !string.IsNullOrEmpty(r.StartBodyName) ? r.StartBodyName : r.SegmentBodyName;
                        if (!string.IsNullOrEmpty(body))
                            bodies.Add(body);
                    }
                }

                return new RenderCompositionManifest.PlanRouteRecord
                {
                    RouteId = route.Id,
                    BackingMissionTreeId = route.BackingMissionTreeId,
                    RecordedDockUT = route.RecordedDockUT,
                    RecordedOriginUndockUT = route.RecordedOriginUndockUT,
                    DispatchWindowPeriod = route.DispatchWindowPeriod,
                    Scope = Parsek.Display.RouteTrajectoryLineRenderer.ClassifyRouteScope(
                        route.DispatchWindowPeriod, bodies).ToString(),
                    ExcludedIntervalKeys = JoinSorted(route.ExcludedIntervalKeys),
                };
            }

            // No route matched; `missions` stays the union the host built (route-owned backing
            // missions included) and is not needed once the route itself was not found.
            return null;
        }

        // =====================================================================================
        //  H4: CHAIN capture (once per pid per chain signature)
        // =====================================================================================

        internal static void NoteChainBuild(
            uint pid, string recId, int committedIndex, string signature, long windowIndex,
            double currentUT, PhaseChain phaseChain, GhostRenderChain assemblerChain,
            bool hasReaimedSegments)
        {
            if (!IsEnabled)
                return;

            chainSignatureByPid[pid] = signature;

            var rec = new RenderCompositionManifest.ChainBuildRecord
            {
                Pid = pid,
                RecId = recId,
                CommittedIndex = committedIndex,
                UT = currentUT,
                Signature = signature,
                WindowIndex = windowIndex,
                Provenance = phaseChain != null ? "spine" : "assembler-fallback",
                HasReaimedSegments = hasReaimedSegments,
                SeamSource = "assembler",
            };

            string[] kinds;
            if (phaseChain != null && phaseChain.Phases != null)
            {
                IReadOnlyList<TrajectoryPhase> phases = phaseChain.Phases;
                kinds = new string[phases.Count];
                for (int i = 0; i < phases.Count; i++)
                {
                    TrajectoryPhase p = phases[i];
                    kinds[i] = PhaseKindTokens.ToToken(p.Kind);
                    var anchor = p.Anchor as AnchorFrame.BodyAnchor;
                    rec.Phases.Add(new RenderCompositionManifest.ChainPhaseRecord
                    {
                        Kind = kinds[i],
                        Provenance = SegmentProvenanceTokens.ToToken(p.Provenance),
                        Body = anchor != null ? anchor.BodyName : (p.Anchor != null ? p.Anchor.ToToken() : ""),
                        StartUT = p.StartUt,
                        EndUT = p.EndUt,
                    });
                }
            }
            else if (assemblerChain != null && assemblerChain.Segments != null)
            {
                IReadOnlyList<RenderSegment> segs = assemblerChain.Segments;
                kinds = new string[segs.Count];
                for (int i = 0; i < segs.Count; i++)
                {
                    kinds[i] = segs[i].Kind.ToString();
                    rec.Phases.Add(new RenderCompositionManifest.ChainPhaseRecord
                    {
                        Kind = kinds[i],
                        Provenance = "unknown",
                        Body = segs[i].FrameBodyName,
                        StartUT = segs[i].StartUT,
                        EndUT = segs[i].EndUT,
                    });
                }
            }
            else
            {
                kinds = new string[0];
            }
            phaseKindsByPid[pid] = kinds;

            // SEAM KINDS COME FROM THE LEGACY ASSEMBLER CHAIN by supervisor decision: PhaseFactory
            // builds every typed phase with NULL seams ("seam reproduction is a Phase-3 concern"), so
            // the only live per-boundary seam kind in production is RenderSegment.LeadingSeam.
            if (assemblerChain != null && assemblerChain.Segments != null)
            {
                IReadOnlyList<RenderSegment> segs = assemblerChain.Segments;
                for (int i = 1; i < segs.Count; i++)
                {
                    rec.Seams.Add(new RenderCompositionManifest.ChainSeamRecord
                    {
                        BoundaryIndex = i,
                        Kind = SeamKindToken(segs[i].LeadingSeam),
                    });
                }
            }

            manifest.AppendChainBuild(rec);
            NoteReaimWindow(recId, windowIndex, committedIndex, currentUT);
        }

        /// <summary>Grep-stable token for the 2-value assembler <see cref="SeamKind"/>.</summary>
        internal static string SeamKindToken(SeamKind kind)
        {
            switch (kind)
            {
                case SeamKind.Rigid: return "rigid";
                case SeamKind.FlexibleSoi: return "flexible-soi";
                default: return "none";
            }
        }

        /// <summary>
        /// Emits one <c>reaim-window</c> clock event per (memberId, windowIndex) observed, carrying the
        /// RE-TIMED arrival instant. The resolver cache is lazy + window-banded, so this is a
        /// per-window append at observation time, never a plan-time precompute.
        /// </summary>
        private static void NoteReaimWindow(string recId, long windowIndex, int ownerIndex, double currentUT)
        {
            if (windowIndex < 0 || string.IsNullOrEmpty(recId))
                return;
            string key = recId + "|" + windowIndex.ToString(CultureInfo.InvariantCulture);
            if (reaimWindowSeen.ContainsKey(key))
                return;
            if (!Parsek.Reaim.ReaimPlaybackResolver.Shared.TryGetWindowRetimedArrivalUT(
                    recId, windowIndex, out double retimedArrivalUT, out double captureShiftSeconds))
                return;   // window not resolved yet - retried on a later frame, never recorded blank
            reaimWindowSeen[key] = true;
            manifest.AppendClockEventIfChanged(
                RenderCompositionManifest.ClockReaimWindow,
                ownerIndex, windowIndex, currentUT,
                windowIndex, retimedArrivalUT, captureShiftSeconds, recId);
        }

        // =====================================================================================
        //  H5-H7: Director stamp, ratified skip, clock defer
        // =====================================================================================

        internal static void NoteDirectorIntent(
            uint pid, string recId, int committedIndex, double currentUT,
            in GhostRenderIntent prior, in GhostRenderIntent intent,
            Coverage coverage, int segmentIndex, bool spineDroveTypedChain)
        {
            if (!IsEnabled)
                return;

            chainSignatureByPid.TryGetValue(pid, out string chainSig);
            string phaseKind = ResolvePhaseKind(pid, segmentIndex, coverage, prior, intent);

            // The two enum tokens come from the static tables, never Enum.ToString(): this runs once
            // per armed ghost per frame, and the dwell key string itself is now built only when
            // RenderCompositionManifest.ObserveDwellFrame's field-wise compare says the identity moved.
            var sample = default(RenderCompositionManifest.DwellSample);
            sample.Pid = pid;
            sample.RecId = recId;
            sample.CommittedIndex = committedIndex;
            sample.ChainSignature = chainSig;
            sample.SegmentIndex = segmentIndex;
            sample.PhaseKind = phaseKind;
            sample.Treatment = TokenOf(treatmentTokens, (int)intent.Treatment);
            sample.Visible = intent.Visible;
            sample.Coverage = TokenOf(coverageTokens, (int)coverage);
            sample.FrameBody = intent.FrameBodyName;
            sample.CurrentUT = currentUT;
            sample.HeadUT = intent.DriveUT;
            sample.WarpRate = UnityWarpRate();
            sample.PhysicsWarp = UnityPhysicsWarp();
            sample.MarkerDecision = GhostMapPresence.ShouldDrawNonProtoMarkerForGhost(
                pid, out bool markerTracedPath, out bool markerPolyline, out bool markerIconSuppressed);
            sample.MarkerTracedPath = markerTracedPath;
            sample.MarkerPolyline = markerPolyline;
            sample.MarkerIconSuppressed = markerIconSuppressed;
            // Owner index + the RECORDED clock this frame rendered (schema v1.1, decisions 4 + 5).
            // Both are OMITTED when the member index maps to no live unit; the latest per-unit
            // SpanLoopFrame sample is this frame's (or, if the recorder's Update trails this render
            // path, the immediately preceding frame's), so the verifier's containment test carries a
            // one-frame-step margin rather than pretending the stamp is exact.
            if (TryResolveOwnerIndex(committedIndex, out int ownerIndex))
            {
                sample.HasOwnerIndex = true;
                sample.OwnerIndex = ownerIndex;
                if (loopUtByOwner.TryGetValue(ownerIndex, out double loopUT))
                {
                    sample.HasLoopUT = true;
                    sample.LoopUT = loopUT;
                }
            }
            if (truthByPid.TryGetValue(pid, out TruthSample truth))
            {
                sample.HasTruth = true;
                sample.TruthBody = truth.Body;
                sample.TruthX = truth.X;
                sample.TruthY = truth.Y;
                sample.TruthZ = truth.Z;
            }

            // spineDroveTypedChain is already carried by the CHAIN_BUILD record's provenance for this
            // pid's signature; the dwell references that chain by signature, so the flag needs no
            // second serialization here.
            manifest.ObserveDwellFrame(in sample);
        }

        /// <summary>
        /// The dwell's phase kind: the chain phase at <paramref name="segmentIndex"/> when the sample
        /// landed IN a segment; "hold" when the Director held a byte-identical prior intent across an
        /// interior gap (the <c>GhostRenderDirector</c> hold contract, which is what RC-HOLD measures);
        /// "none" otherwise.
        /// </summary>
        private static string ResolvePhaseKind(
            uint pid, int segmentIndex, Coverage coverage,
            in GhostRenderIntent prior, in GhostRenderIntent intent)
        {
            if (segmentIndex >= 0
                && phaseKindsByPid.TryGetValue(pid, out string[] kinds)
                && kinds != null && segmentIndex < kinds.Length)
                return kinds[segmentIndex];

            if (coverage == Coverage.InInteriorGap && intent.Visible && prior.Visible
                && prior.Treatment == intent.Treatment
                && prior.DriveUT == intent.DriveUT
                && string.Equals(prior.FrameBodyName, intent.FrameBodyName, StringComparison.Ordinal))
                return "hold";

            return "none";
        }

        internal static void NoteRatifiedSkip(uint pid, double currentUT, string reason)
        {
            if (!IsEnabled)
                return;
            manifest.NoteRatifiedSkip(pid, currentUT, reason);
        }

        internal static void NoteClockDefer(double currentUT, int ghostCount)
        {
            if (!IsEnabled)
                return;
            // ghostCount is the frame's ghost population; the record aggregates DEFERRED FRAMES, and a
            // whole-frame defer hides every ghost regardless of the count, so it is not serialized.
            manifest.NoteClockDefer(currentUT);
        }

        // =====================================================================================
        //  H8: ownership publish
        // =====================================================================================

        /// <summary>
        /// The actual-draw publish set. The UT is read HERE, behind the arm gate, rather than passed
        /// in: the call site runs on every completed polyline walk, armed or not, so a
        /// <c>Planetarium.GetUniversalTime()</c> in its argument list would be an ECall the unarmed
        /// path pays for and nothing reads. There is no warp-rate parameter for the same reason - the
        /// schema never carried one.
        /// </summary>
        internal static void NoteOwnershipPublish(HashSet<string> drewSet)
        {
            if (!IsEnabled)
                return;
            double currentUT = CurrentUT();
            Parsek.Display.GhostTrajectoryPolylineRenderer.DiffDrawnSets(
                previousDrewSet, drewSet, appearedScratch, disappearedScratch);
            for (int i = 0; i < appearedScratch.Count; i++)
                manifest.AppendOwnershipChange(appearedScratch[i], currentUT, appeared: true);
            for (int i = 0; i < disappearedScratch.Count; i++)
                manifest.AppendOwnershipChange(disappearedScratch[i], currentUT, appeared: false);

            previousDrewSet.Clear();
            if (drewSet != null)
            {
                foreach (string id in drewSet)
                    previousDrewSet.Add(id);
            }
        }

        // =====================================================================================
        //  H9 / H11: seam records
        // =====================================================================================

        internal static void NoteSeamTangent(
            uint pid, string recId, int legIndex, double currentUT,
            bool continuous, double angleRadians, double toleranceRadians)
        {
            if (!IsEnabled)
                return;
            // Admit BEFORE allocating: at the seam cap this hook is still called every evaluation, and
            // the record it would build is guaranteed to be dropped. The admit does the whole cap
            // preamble including the TRUNCATED accounting, so a false is already counted.
            if (!manifest.TryAdmitSeamRecord(
                    pid, RenderCompositionManifest.SeamTangentSection,
                    RenderCompositionManifest.SeamTangentKind))
                return;
            manifest.AddAdmittedSeamTangent(new RenderCompositionManifest.SeamTangentRecord
            {
                Pid = pid,
                RecId = recId,
                LegIndex = legIndex,
                UT = currentUT,
                Continuous = continuous,
                AngleRadians = angleRadians,
                ToleranceRadians = toleranceRadians,
            });
        }

        internal static void NoteSeamEndpoint(
            uint pid, string recId, double currentUT, in MapRenderProbe.SeamEndpointSample sample)
        {
            if (!IsEnabled)
                return;
            if (!manifest.TryAdmitSeamRecord(
                    pid, RenderCompositionManifest.SeamEndpointSection,
                    RenderCompositionManifest.SeamEndpointKind))
                return;
            SeamEndpointOracle.SeamEndpointResult result = sample.Result;
            manifest.AddAdmittedSeamEndpoint(new RenderCompositionManifest.SeamEndpointRecord
            {
                Pid = pid,
                RecId = recId,
                UT = currentUT,
                Sampled = sample.Sampled,
                SkipReason = sample.SkipReason,
                Ratio = result.Ratio,
                EndpointDistanceMeters = result.EndpointDistanceMeters,
                SoiRadiusMeters = result.SoiRadiusMeters,
                RatioTolerance = result.RatioTolerance,
                OutsideSoi = result.OutsideSoi,
                FromBody = sample.FromBody,
                ToBody = sample.ToBody,
                RecordedSeamUT = sample.RecordedSeamUT,
                SeamUT = sample.SeamUT,
                ClockConvention = sample.ClockConvention,
                SeedKind = sample.SeedKind,
            });
        }

        // =====================================================================================
        //  H12: anomaly echo
        // =====================================================================================

        /// <summary>
        /// Echoes one tracer anomaly raise into the manifest, TWICE over:
        ///
        /// <list type="bullet">
        /// <item>a STANDALONE <c>OBSERVED.ANOMALY_ECHO</c> record for EVERY raise, carrying the pid key
        /// verbatim. The tracer keys some surfaces by a non-numeric name, and many raises land with no
        /// dwell open for their pid - both used to be dropped here, which left a verifier unable to
        /// tell "no anomaly was raised" from "the raise had nowhere to land";</item>
        /// <item>the DWELL-embedded per-reason aggregation, unchanged, for the numeric-pid raises that
        /// do land inside an open dwell - that is the one that gives an anomaly its dwell context.</item>
        /// </list>
        ///
        /// <para><paramref name="effUT"/> is the tracer's effective-clock instant for the raise; the
        /// record stamps the LIVE <paramref name="currentUT"/>, which is the clock every other OBSERVED
        /// record is stamped on.</para>
        /// </summary>
        internal static void NoteAnomalyRaise(
            string pidKey, string reason, double currentUT, double effUT, string recId)
        {
            if (!IsEnabled)
                return;
            manifest.AppendAnomalyEchoRecord(pidKey, recId, reason, currentUT);
            if (uint.TryParse(pidKey, NumberStyles.Integer, CultureInfo.InvariantCulture, out uint pid))
                manifest.NoteAnomalyEcho(pid, reason);
        }

        // =====================================================================================
        //  H10: line-branch funnel
        // =====================================================================================

        internal static void NoteLineBranch(
            uint pid, string reason, bool lineActive, int drawIcons, bool iconSuppressed,
            double currentUT, double startUT, double endUT,
            MapRenderTrace.RenderWindowCoverage coverage)
        {
            if (!IsEnabled)
                return;

            // DEBOUNCE FIRST, on the same (reason, lineActive, coverage) triple the manifest keys on,
            // by a cheap struct compare against the last decision for this pid. This hook runs once per
            // ghost per frame from the line-branch funnel and the state changes rarely, so everything
            // expensive - the record allocation, the recId reverse lookup, the enum token - has to sit
            // BEHIND this check rather than in front of the manifest's own (equivalent) debounce.
            int coverageValue = (int)coverage;
            if (lineBranchDecisionByPid.TryGetValue(pid, out LineBranchDecision last)
                && last.Have
                && last.LineActive == lineActive
                && last.Coverage == coverageValue
                && string.Equals(last.Reason, reason, StringComparison.Ordinal))
                return;
            lineBranchDecisionByPid[pid] = new LineBranchDecision
            {
                Have = true,
                Reason = reason,
                LineActive = lineActive,
                Coverage = coverageValue,
            };

            // startUT / endUT are the branch's applied window bounds. They are carried in the signature
            // (they are what a Phase-3 widening would serialize) but schema v1's LINE_BRANCH node keeps
            // only the decision triple, which is what RC-COVER / RC-OWN consume.
            manifest.AppendLineBranchIfChanged(new RenderCompositionManifest.LineBranchRecord
            {
                Pid = pid,
                RecId = GhostMapPresence.FindRecordingIdByVesselPid(pid),
                UT = currentUT,
                Reason = reason,
                LineActive = lineActive,
                DrawIcons = drawIcons,
                IconSuppressed = iconSuppressed,
                // NEVER spell the coverage enum members here: exactly four measuring decisions in
                // GhostOrbitLinePatch may do that (LineBlinkWindowExitExemptionTests source gate).
                // The token table is built by ENUMERATING the enum, so no member name appears in
                // source on this side either.
                Coverage = TokenOf(renderWindowCoverageTokens, coverageValue),
            });
        }

        // =====================================================================================
        //  H13-H15: route overview line
        // =====================================================================================

        internal static void NoteRouteLineBuild(
            string routeId, long signature, double dockClipUT, double dispatchWindowPeriod,
            int scope, int resolvableMembers, int groups, int totalLegs, int transferLegsDropped)
        {
            if (!IsEnabled)
                return;
            manifest.AppendRouteLineBuild(new RenderCompositionManifest.RouteLineBuildRecord
            {
                RouteId = routeId,
                Signature = signature,
                DockClipUT = dockClipUT,
                DispatchWindowPeriod = dispatchWindowPeriod,
                Scope = ((Parsek.Display.RouteTrajectoryLineRenderer.RouteLineScope)scope).ToString(),
                ResolvableMembers = resolvableMembers,
                Groups = groups,
                TotalLegs = totalLegs,
                TransferLegsDropped = transferLegsDropped,
                UT = CurrentUT(),
            });
        }

        internal static void NoteRouteLegDeferred(string routeId, string memberRecId)
        {
            if (!IsEnabled)
                return;
            manifest.NoteRouteLegDeferred(routeId, memberRecId);
        }

        internal static void NoteRouteCoDrawViolation(string routeId, string memberRecId, int frame)
        {
            if (!IsEnabled)
                return;
            manifest.AppendRouteCoDrawViolation(routeId, memberRecId, CurrentUT(), frame);
        }

        // =====================================================================================
        //  H18: probe truth positions
        // =====================================================================================

        internal static void NoteProbeTruth(
            uint pid, string recId, double currentUT, string bodyName, Vector3d bodyRelPos)
        {
            if (!IsEnabled)
                return;
            // ONE latest sample per pid (struct overwrite, no allocation). The dwell driver stamps it
            // onto the open/close endpoints; when it is absent the transition simply carries no
            // positions and the verifier treats position clauses as defined-unevaluable.
            truthByPid[pid] = new TruthSample
            {
                Body = bodyName,
                X = bodyRelPos.x,
                Y = bodyRelPos.y,
                Z = bodyRelPos.z,
            };
        }

        // =====================================================================================
        //  Clock-event taps that are not per-frame (descent + route delivery clock)
        // =====================================================================================

        /// <summary>
        /// One descent render-phase event per cycle (the <c>DescentRenderTrace</c> classification is
        /// already exactly-once-per-cycle-event; the recorder debounces per (member, cycle, phase) on
        /// top so the multiple per-frame resolver call sites collapse).
        /// </summary>
        internal static void NoteDescentPhaseEvent(
            int memberIndex, long cycleIndex, double currentUT, string phaseToken,
            double triggerUT, double entryUT, double rotResidualDeg, double headUT)
        {
            if (!IsEnabled)
                return;
            // detailD = the RESOLVED descent head (schema v1.1, supervisor decision 3). NaN before the
            // trigger (the descent is still hidden and there IS no head), and a NaN is not written:
            // the key is omitted so the verifier reads "not measured" rather than a NaN literal.
            bool hasHead = !double.IsNaN(headUT) && !double.IsInfinity(headUT);
            manifest.AppendClockEventIfChanged(
                RenderCompositionManifest.ClockDescentPhase,
                memberIndex, cycleIndex, currentUT,
                triggerUT, entryUT, rotResidualDeg, phaseToken,
                hasHead, headUT);
        }

        /// <summary>
        /// The route DELIVERY clock's crossing of <c>RecordedDockUT</c>. NOTE: this is a DIFFERENT
        /// clock from the render clock - <c>RouteLoopClock.TryGetRouteLoopState</c> forwards only the
        /// relaunch schedule and the loiter cuts, omitting the hold / launch-hold / joint arguments, so
        /// on a hold-carrying unit the two diverge. RC-ROUTE can check exactly that.
        /// </summary>
        internal static void NoteRouteDockCrossing(
            int ownerIndex, long dockCycleIndex, double currentUT, string routeId)
        {
            if (!IsEnabled)
                return;
            manifest.AppendClockEventIfChanged(
                RenderCompositionManifest.ClockRouteDockCrossing,
                ownerIndex, dockCycleIndex, currentUT,
                dockCycleIndex, 0.0, 0.0, routeId);
        }

        // =====================================================================================
        //  Export
        // =====================================================================================

        /// <summary>
        /// Flushes the accumulation and writes <see cref="ManifestFileName"/> to the KSP root via the
        /// shared safe-write (tmp + rename). SYNCHRONOUS - the M-A2 verb is single-phase.
        /// </summary>
        internal static bool TryExportNow(
            string reason, out string path, out int dwells, out int transitions,
            out int planUnits, out int clockEvents, out string error)
        {
            path = null;
            dwells = 0;
            transitions = 0;
            planUnits = 0;
            clockEvents = 0;
            error = null;

            if (!IsEnabled)
            {
                error = ErrorNotArmed;
                return false;
            }

            // NOTE: export does NOT close the open dwells. The builder snapshots them as
            // `openAtExport=True` records at the export instant without touching accumulation state,
            // so a build or write that fails changes nothing and a later REAL close still lands on a
            // live dwell instead of on one the failed export already retired.
            double exportUT = CurrentUT();

            // One last latch so an export taken before this process's first armed Update (or from a
            // scene whose Update never ran) still reports honestly; the bit is STICKY, so this can
            // only ever turn it on, never wash out a tracing period the frames already recorded.
            LatchMapRenderTracing();

            var header = new RenderCompositionManifest.ManifestHeader(
                exportUT,
                reason ?? ReasonVerb,
                CurrentSceneName(),
                CurrentSaveName(),
                envArmed,
                ForceEnabledForTesting,
                mapRenderTracingWasEverOn);

            ConfigNode file;
            try
            {
                file = manifest.BuildFileNode(in header);
            }
            catch (Exception ex)
            {
                error = "build-failed:" + ex.GetType().Name;
                ParsekLog.Warn(Tag, "manifest build failed: " + ex.GetType().Name + ": " + ex.Message);
                return false;
            }

            string target = Path.Combine(KspRootPath(), ManifestFileName);
            try
            {
                FileIOUtils.SafeWriteConfigNode(file, target, Tag);
            }
            catch (Exception ex)
            {
                error = "write-failed:" + ex.GetType().Name;
                ParsekLog.Warn(Tag, "manifest write failed for '" + target + "': "
                    + ex.GetType().Name + ": " + ex.Message);
                return false;
            }

            path = target;
            wroteAnyManifestThisProcess = true;
            // Dwell RECORDS written, which after the non-destructive export is closed + still-open.
            dwells = manifest.ClosedDwellCount + manifest.OpenDwellCount;
            transitions = manifest.TransitionCount;
            planUnits = manifest.PlanUnitCount;
            clockEvents = manifest.ClockEventCount;

            ParsekLog.Info(Tag, string.Format(CultureInfo.InvariantCulture,
                "manifest exported reason={0} path={1} planUnits={2} chains={3} dwells={4} "
                + "transitions={5} clockEvents={6} lineBranches={7} ownership={8} seamTangents={9} "
                + "seamEndpoints={10} routeBuilds={11} coDraw={12} truncated={13}",
                header.ExportReason, target, planUnits, manifest.ChainBuildCount, dwells, transitions,
                clockEvents, manifest.LineBranchCount, manifest.OwnershipChangeCount,
                manifest.SeamTangentCount, manifest.SeamEndpointCount, manifest.RouteLineBuildCount,
                manifest.RouteCoDrawViolationCount, manifest.TruncationCount));
            return true;
        }

        /// <summary>Grep token for the skipped auto-flush.</summary>
        internal const string AutoFlushSkipReason = "no-new-observation";

        /// <summary>
        /// PURE auto-flush decision. The file is written to ONE fixed path, so every auto flush after
        /// the first overwrites whatever the previous one wrote - and a scene bounce or a teardown
        /// after the accumulation was Reset would clobber a real, populated manifest with an empty one.
        ///
        /// <para>The guard is therefore about NEW OBSERVATION, not about emptiness: skip only when
        /// this process has ALREADY written a manifest and nothing observed has accumulated since
        /// (no dwells open or closed, no transitions). The FIRST flush of a process always writes, even
        /// with no dwells at all, because a KSC / tracking-station-only session legitimately produces a
        /// manifest of plan + clock records and nothing else - and that manifest is the evidence.</para>
        ///
        /// <para>The VERB export is never routed through here; an explicit export always writes.</para>
        /// </summary>
        internal static bool ShouldSkipAutoFlush(
            int closedDwells, int openDwells, int transitions, bool alreadyWroteThisProcess)
            => alreadyWroteThisProcess && closedDwells == 0 && openDwells == 0 && transitions == 0;

        /// <summary>Set on the first successful export of this process and NEVER cleared - it is a
        /// property of the output file, which outlives every scene and every Reset.</summary>
        private static bool wroteAnyManifestThisProcess;

        /// <summary>Test-only: clears the process-wide "a manifest was written" latch.</summary>
        internal static void ResetWroteManifestLatchForTesting() => wroteAnyManifestThisProcess = false;

        /// <summary>
        /// Auto-flush on a lifecycle boundary (scene exit / teardown).
        /// </summary>
        private static void TryAutoFlush(string reason)
        {
            if (!IsEnabled)
                return;
            if (ShouldSkipAutoFlush(
                    manifest.ClosedDwellCount, manifest.OpenDwellCount, manifest.TransitionCount,
                    wroteAnyManifestThisProcess))
            {
                // The skip is correct (see ShouldSkipAutoFlush), but it DOES drop whatever plan and
                // clock records this follow-on scene accumulated. Name the counts so the evidence
                // loss is visible in the log instead of being silent.
                ParsekLog.Verbose(Tag, string.Format(CultureInfo.InvariantCulture,
                    "auto-flush skipped reason={0} trigger={1} droppedPlanUnits={2} "
                    + "droppedClockEvents={3}: a manifest was already written this process and "
                    + "nothing has been observed since, so writing would clobber it with an empty one",
                    AutoFlushSkipReason, reason ?? "?",
                    manifest.PlanUnitCount, manifest.ClockEventCount));
                return;
            }
            TryExportNow(reason, out string _, out int _, out int _, out int _, out int _, out string _);
        }

        // =====================================================================================
        //  Unity-ECall isolation: every Unity-native read in its own tiny method so the JIT never
        //  walks an ECall on an unreachable branch while the pure core is unit-tested (mono runs a
        //  failing type initializer at JIT of the CALLING method - see CLAUDE.md).
        // =====================================================================================

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static string ReadEnvVar()
        {
            try { return Environment.GetEnvironmentVariable(EnvVarName); }
            catch (Exception) { return null; }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static double CurrentUT()
        {
            try { return Planetarium.GetUniversalTime(); }
            catch (Exception) { return double.NaN; }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static double UnityWarpRate()
        {
            try { return TimeWarp.CurrentRate; }
            catch (Exception) { return 1.0; }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool UnityPhysicsWarp()
        {
            try { return TimeWarp.WarpMode == TimeWarp.Modes.LOW; }
            catch (Exception) { return false; }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static string CurrentSceneName()
        {
            try { return HighLogic.LoadedScene.ToString(); }
            catch (Exception) { return ""; }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static string CurrentSaveName()
        {
            try { return HighLogic.CurrentGame != null ? (HighLogic.CurrentGame.Title ?? "") : ""; }
            catch (Exception) { return ""; }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static string KspRootPath()
        {
            try { return KSPUtil.ApplicationRootPath ?? ""; }
            catch (Exception) { return ""; }
        }

        // ---- small pure helpers ----

        private static string JoinInts(int[] values)
        {
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < values.Length; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(values[i].ToString(CultureInfo.InvariantCulture));
            }
            return sb.ToString();
        }

        private static string JoinSorted(HashSet<string> values)
        {
            if (values == null || values.Count == 0)
                return "";
            var list = new List<string>(values);
            list.Sort(StringComparer.Ordinal);
            return string.Join(";", list.ToArray());
        }
    }
}
