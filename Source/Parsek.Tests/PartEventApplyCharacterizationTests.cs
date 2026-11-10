using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Runtime.Serialization;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Characterization of <see cref="GhostPlaybackLogic.ApplyPartEvents"/>, written against the
    /// original method before its switch and its frame tail were extracted.
    ///
    /// ApplyPartEvents returns early unless the state holds a live ghost. A headless test cannot build a
    /// GameObject, so the "live" ghost here is an uninitialized GameObject whose native pointer field is
    /// set to a non-zero value: Unity's managed null check reads that pointer and nothing else, so the
    /// method runs past its guard and every arm whose handler stays on managed code for an empty state
    /// runs exactly as it does in game. An arm whose handler reaches a native call ends the call on mono's
    /// refusal, which the transcript records. The fake ghost is only ever compared against null; every
    /// cell that holds one is a <see cref="UnityEntryFactAttribute"/> (the Windows .NET Framework host
    /// refuses Unity's own null-check helper on entry), and the null-ghost cells are plain facts.
    ///
    /// The transcript holds every log line, the rate-limit keys each call touched, the cursor, the
    /// looped-audio cap calls and the deployable transition list after each call.
    /// </summary>
    [Collection("Sequential")]
    public class PartEventApplyCharacterizationTests : IDisposable
    {
        private readonly List<string> transcript = new List<string>();

        public PartEventApplyCharacterizationTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.TestSinkForTesting = line =>
            {
                if (!line.Contains("[Settings]"))
                    transcript.Add("LOG " + line);
            };
            ParsekLog.VerboseOverrideForTesting = true;
            GhostPlaybackLogic.ResetForTesting();
            GhostPlaybackLogic.EnforceLoopedAudioPlaybackCapOverrideForTesting = s =>
            {
                transcript.Add("CAP enforce");
                return true;
            };
        }

        public void Dispose()
        {
            GhostPlaybackLogic.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        // ------------------------------------------------------------------
        // Fixtures
        // ------------------------------------------------------------------

        /// <summary>
        /// A GameObject that Unity's managed equality reads as alive: no constructor ran (that is
        /// native), and the cached native pointer is non-zero, which is the first thing
        /// <c>Object.IsNativeObjectAlive</c> checks. Never dereferenced by the cells that hold one
        /// except through calls that end on mono's refusal.
        /// </summary>
        internal static GameObject FakeLiveGhost()
        {
            var go = (GameObject)FormatterServices.GetUninitializedObject(typeof(GameObject));
            FieldInfo ptr = typeof(UnityEngine.Object).GetField(
                "m_CachedPtr", BindingFlags.Instance | BindingFlags.NonPublic);
            ptr.SetValue(go, new IntPtr(1));
            return go;
        }

        private static PartEvent Evt(double ut, PartEventType type, uint pid, float value = 0.5f)
            => new PartEvent
            {
                ut = ut,
                partPersistentId = pid,
                eventType = type,
                partName = "test.part",
                value = value,
                moduleIndex = 0,
            };

        private static MockTrajectory Rec(params PartEvent[] events)
        {
            var rec = new MockTrajectory();
            rec.PartEvents = new List<PartEvent>(events);
            return rec;
        }

        private static string Describe(GhostPlaybackState state)
        {
            string transitions = state.activeDeployableTransitions == null
                ? "null"
                : state.activeDeployableTransitions.Count.ToString(CultureInfo.InvariantCulture);
            string logical = state.logicalPartIds == null
                ? "null"
                : state.logicalPartIds.Count.ToString(CultureInfo.InvariantCulture);
            return "cursor=" + state.partEventIndex.ToString(CultureInfo.InvariantCulture)
                + " transitions=" + transitions + " logical=" + logical;
        }

        private void Apply(
            string label, int recIdx, IPlaybackTrajectory rec, double ut, GhostPlaybackState state,
            bool allowTransientEffects = true)
        {
            ParsekLog.ResetRateLimitsForTesting();
            transcript.Add("CALL " + label + " #" + recIdx.ToString(CultureInfo.InvariantCulture)
                + " ut=" + ut.ToString("R", CultureInfo.InvariantCulture)
                + " transient=" + allowTransientEffects + " " + Describe(state));
            try
            {
                GhostPlaybackLogic.ApplyPartEvents(recIdx, rec, ut, state, allowTransientEffects);
                transcript.Add("RET " + Describe(state));
            }
            catch (Exception ex) when (IsHeadlessUnityRefusal(ex))
            {
                transcript.Add("RET unity-native-refused " + Describe(state));
            }
            transcript.Add("KEYS " + string.Join(" | ", RateLimitKeys()));
        }

        // The rate-limit keys this call touched (the store is reset before each call), sorted.
        private static List<string> RateLimitKeys()
        {
            PropertyInfo prop = typeof(ParsekLog).GetProperty(
                "rateLimitStateByKey", BindingFlags.Static | BindingFlags.NonPublic);
            var dict = (System.Collections.IDictionary)prop.GetValue(null);
            var keys = new List<string>();
            foreach (object k in dict.Keys)
                keys.Add((string)k);
            keys.Sort(StringComparer.Ordinal);
            return keys;
        }

        private static bool IsHeadlessUnityRefusal(Exception ex)
            => ex is MissingMethodException || ex is SecurityException || ex is MethodAccessException;

        private static readonly Regex NumberPattern = new Regex(
            @"-?\d+(\.\d+)?(E[+-]?\d+)?", RegexOptions.CultureInvariant);

        // Every number becomes the hex of its double bits, so runtime "R" formatting cannot move the
        // digest while a changed value still does.
        private static string Canonical(string text)
            => NumberPattern.Replace(text, m =>
            {
                double v = double.Parse(m.Value, NumberStyles.Float, CultureInfo.InvariantCulture);
                return BitConverter.DoubleToInt64Bits(v).ToString("X16", CultureInfo.InvariantCulture);
            });

        private static string Fnv1a64(string s)
        {
            ulong h = 14695981039346656037UL;
            byte[] bytes = Encoding.UTF8.GetBytes(s);
            for (int i = 0; i < bytes.Length; i++)
            {
                h ^= bytes[i];
                h *= 1099511628211UL;
            }
            return h.ToString("X16", CultureInfo.InvariantCulture);
        }

        private void AssertGolden(string expectedDigest, int expectedLines)
        {
            string text = string.Join("\n", transcript);
            string actual = "digest=" + Fnv1a64(Canonical(text)) + " lines=" + transcript.Count;
            string expected = "digest=" + expectedDigest + " lines=" + expectedLines;
            Assert.True(expected == actual, "golden mismatch\nexpected " + expected + "\nactual   " + actual
                + "\n--- transcript ---\n" + text);
        }

        private void AssertTranscript(params string[] expected)
        {
            string text = string.Join("\n", transcript);
            Assert.True(string.Join("\n", expected) == text,
                "transcript mismatch\n--- expected ---\n" + string.Join("\n", expected)
                + "\n--- actual ---\n" + text);
        }

        // ------------------------------------------------------------------
        // Early returns (no live ghost needed)
        // ------------------------------------------------------------------

        [Fact]
        public void NoEvents_ReturnsBeforeTheGhostCheck_AndRunsNoFrameTail()
        {
            // A queued deployable transition the frame tail would drop: it survives, so the tail never ran.
            var state = new GhostPlaybackState
            {
                activeDeployableTransitions = new List<DeployableGhostInfo> { null },
            };
            var nullList = new MockTrajectory { PartEvents = null };

            Apply("null-list", 3, nullList, 100.0, state);
            Apply("empty-list", 3, Rec(), 100.0, state);

            AssertTranscript(
                "CALL null-list #3 ut=100 transient=True cursor=0 transitions=1 logical=null",
                "RET cursor=0 transitions=1 logical=null",
                "KEYS ",
                "CALL empty-list #3 ut=100 transient=True cursor=0 transitions=1 logical=null",
                "RET cursor=0 transitions=1 logical=null",
                "KEYS ");
        }

        [Fact]
        public void NullGhost_LogsUnderTheRecordingKey_AndKeepsTheCursor()
        {
            var state = new GhostPlaybackState
            {
                activeDeployableTransitions = new List<DeployableGhostInfo> { null },
            };
            var rec = Rec(Evt(10.0, PartEventType.Docked, 100000u));

            Apply("null-ghost", 7, rec, 100.0, state);

            AssertTranscript(
                "CALL null-ghost #7 ut=100 transient=True cursor=0 transitions=1 logical=null",
                "LOG [Parsek][VERBOSE][Flight] ApplyPartEvents: ghost is null for recording #7",
                "RET cursor=0 transitions=1 logical=null",
                "KEYS Flight|apply-part-events-null-ghost-7");
        }

        // ------------------------------------------------------------------
        // Live ghost: the cursor walk and the frame tail
        // ------------------------------------------------------------------

        [UnityEntryFact]
        public void CursorWalk_ConsumesEventsAtOrBeforeUT_StopsAtTheFirstLaterOne_AndRunsTheTailEveryCall()
        {
            var state = new GhostPlaybackState { ghost = FakeLiveGhost() };
            // Docked (default arm) and the two parachute arms stay on managed code with an empty state.
            // The 40 s event sits before the 30 s one: the walk stops at 40 and never reaches 30 until
            // the clock passes 40.
            var rec = Rec(
                Evt(10.0, PartEventType.Docked, 100000u),
                Evt(20.0, PartEventType.ParachuteCut, 101111u),
                Evt(20.0, PartEventType.ParachuteCut, 102222u),
                Evt(20.0, PartEventType.Undocked, 100000u),
                Evt(40.0, PartEventType.ParachuteRepacked, 101111u),
                Evt(30.0, PartEventType.Docked, 103333u));

            state.activeDeployableTransitions = new List<DeployableGhostInfo> { null };
            Apply("before-first", 5, rec, 5.0, state);
            state.activeDeployableTransitions = new List<DeployableGhostInfo> { null };
            Apply("at-twenty", 5, rec, 20.0, state);
            Apply("caught-up", 5, rec, 35.0, state);
            Apply("past-all", 5, rec, 50.0, state);
            Apply("again", 5, rec, 60.0, state);

            AssertTranscript(
                "CALL before-first #5 ut=5 transient=True cursor=0 transitions=1 logical=null",
                "RET cursor=0 transitions=0 logical=null",
                "KEYS ",
                "CALL at-twenty #5 ut=20 transient=True cursor=0 transitions=1 logical=null",
                "LOG [Parsek][VERBOSE][Flight] Applied 4 part events for ghost #5 (evtIdx now 4)",
                "LOG [Parsek][VERBOSE][GhostPartEvents] apply family=Docked surface=visibility rec=5 pid=100000 applied=0 skipped=1 reason=unhandled-event-type",
                "LOG [Parsek][VERBOSE][GhostPartEvents] apply family=ParachuteCut surface=parachute rec=5 pid=101111 applied=0 skipped=2 reason=no-family-state",
                "LOG [Parsek][VERBOSE][GhostPartEvents] apply family=Undocked surface=visibility rec=5 pid=100000 applied=0 skipped=1 reason=unhandled-event-type",
                "RET cursor=4 transitions=0 logical=null",
                "KEYS Flight|part-events-5",
                "CALL caught-up #5 ut=35 transient=True cursor=4 transitions=0 logical=null",
                "RET cursor=4 transitions=0 logical=null",
                "KEYS ",
                "CALL past-all #5 ut=50 transient=True cursor=4 transitions=0 logical=null",
                "LOG [Parsek][VERBOSE][Flight] Applied 2 part events for ghost #5 (evtIdx now 6)",
                "LOG [Parsek][VERBOSE][GhostPartEvents] apply family=ParachuteRepacked surface=parachute rec=5 pid=101111 applied=0 skipped=1 reason=no-family-state",
                "LOG [Parsek][VERBOSE][GhostPartEvents] apply family=Docked surface=visibility rec=5 pid=103333 applied=0 skipped=1 reason=unhandled-event-type",
                "RET cursor=6 transitions=0 logical=null",
                "KEYS Flight|part-events-5",
                "CALL again #5 ut=60 transient=True cursor=6 transitions=0 logical=null",
                "RET cursor=6 transitions=0 logical=null",
                "KEYS ");
        }

        [UnityEntryFact]
        public void EngineAudioTouched_EnforcesTheLoopedAudioCapOnce_AfterTheWalk()
        {
            const uint pid = 100000u;
            var state = new GhostPlaybackState
            {
                ghost = FakeLiveGhost(),
                audioMuted = true,
                audioInfos = new Dictionary<ulong, AudioGhostInfo>
                {
                    { FlightRecorder.EncodeEngineKey(pid, 0), new AudioGhostInfo { partPersistentId = pid } },
                },
            };
            var rec = Rec(
                Evt(10.0, PartEventType.EngineIgnited, pid, 0.75f),
                Evt(11.0, PartEventType.EngineThrottle, pid, 0.25f),
                Evt(12.0, PartEventType.EngineShutdown, pid, 0f));

            Apply("engine", 2, rec, 12.0, state);

            Assert.Equal(0f, state.audioInfos[FlightRecorder.EncodeEngineKey(pid, 0)].currentPower);
            AssertTranscript(
                "CALL engine #2 ut=12 transient=True cursor=0 transitions=null logical=null",
                "CAP enforce",
                "LOG [Parsek][VERBOSE][Flight] Applied 3 part events for ghost #2 (evtIdx now 3)",
                "LOG [Parsek][VERBOSE][GhostPartEvents] apply family=EngineIgnited surface=engine-fx rec=2 pid=100000 applied=0 skipped=1 reason=no-family-state",
                "LOG [Parsek][VERBOSE][GhostPartEvents] apply family=EngineIgnited surface=engine-audio rec=2 pid=100000 applied=1 skipped=0 reason=applied",
                "LOG [Parsek][VERBOSE][GhostPartEvents] apply family=EngineThrottle surface=engine-fx rec=2 pid=100000 applied=0 skipped=1 reason=no-family-state",
                "LOG [Parsek][VERBOSE][GhostPartEvents] apply family=EngineThrottle surface=engine-audio rec=2 pid=100000 applied=1 skipped=0 reason=applied",
                "LOG [Parsek][VERBOSE][GhostPartEvents] apply family=EngineShutdown surface=engine-fx rec=2 pid=100000 applied=0 skipped=1 reason=no-family-state",
                "LOG [Parsek][VERBOSE][GhostPartEvents] apply family=EngineShutdown surface=engine-audio rec=2 pid=100000 applied=1 skipped=0 reason=applied",
                "RET cursor=3 transitions=null logical=null",
                "KEYS Flight|part-events-2 | GhostPartEvents|apply-2-EngineThrottle-engine-audio"
                    + " | GhostPartEvents|apply-2-EngineThrottle-engine-fx");
        }

        /// <summary>
        /// Every PartEventType member, one event each, on a fresh state with a live ghost and an
        /// empty part set. Pins which family and surface each arm tallies, the outcome each handler
        /// reports for a ghost that carries nothing, the arms that reach a native call (recorded as a
        /// refusal), and the aggregate line, run once with transient effects on and once off.
        /// </summary>
        [UnityEntryFact]
        public void EveryFamily_OneEventOnAnEmptyLiveGhost_TalliesItsArm()
        {
            foreach (bool transient in new[] { true, false })
            {
                foreach (PartEventType family in Enum.GetValues(typeof(PartEventType)))
                {
                    var state = new GhostPlaybackState
                    {
                        ghost = FakeLiveGhost(),
                        logicalPartIds = new HashSet<uint> { 100000u, 101111u },
                    };
                    var rec = Rec(Evt(100.0, family, 100000u, 0.5f));
                    Apply(family.ToString(), 9, rec, 100.0, state, transient);
                }
            }

            AssertGolden("A029E20F5F71854C", 436);
        }
        // ------------------------------------------------------------------
        // The extracted units, driven directly
        // ------------------------------------------------------------------

        private static List<string> Rendered(GhostPartEventApplyTally tally, int recIdx)
        {
            List<string> lines = tally.RenderLines(recIdx);
            lines.Sort(StringComparer.Ordinal);
            return lines;
        }

        [Fact]
        public void ApplyOnePartEvent_UnhandledType_TalliesTheVisibilitySurface_AndLeavesTheFlags()
        {
            var tally = new GhostPartEventApplyTally();
            var state = new GhostPlaybackState();
            bool visibilityChanged = true, needsReentryMeshRebuild = false, audioPowerTouched = false;
            HashSet<uint> placed = null;

            foreach (PartEventType type in new[] { PartEventType.Docked, PartEventType.Undocked, (PartEventType)9977 })
            {
                GhostPlaybackLogic.ApplyOnePartEvent(
                    tally, state, null, null, null, Evt(10.0, type, 100000u), 10.0, true,
                    ref visibilityChanged, ref needsReentryMeshRebuild, ref audioPowerTouched, ref placed);
            }

            Assert.True(visibilityChanged);
            Assert.False(needsReentryMeshRebuild);
            Assert.False(audioPowerTouched);
            Assert.Null(placed);
            Assert.Equal(new List<string>
            {
                "apply family=9977 surface=visibility rec=4 pid=100000 applied=0 skipped=1 reason=unhandled-event-type",
                "apply family=Docked surface=visibility rec=4 pid=100000 applied=0 skipped=1 reason=unhandled-event-type",
                "apply family=Undocked surface=visibility rec=4 pid=100000 applied=0 skipped=1 reason=unhandled-event-type",
            }, Rendered(tally, 4));
            Assert.Empty(transcript);
        }

        [UnityEntryFact]
        public void ApplyOnePartEvent_InventoryPlaced_AllocatesThePlacedSetOnce_AndMarksVisibility()
        {
            var tally = new GhostPartEventApplyTally();
            var state = new GhostPlaybackState();
            var logical = new HashSet<uint>();
            bool visibilityChanged = false, needsReentryMeshRebuild = false, audioPowerTouched = false;
            HashSet<uint> placed = null;

            GhostPlaybackLogic.ApplyOnePartEvent(
                tally, state, null, logical, null, Evt(10.0, PartEventType.InventoryPartPlaced, 101111u),
                10.0, true, ref visibilityChanged, ref needsReentryMeshRebuild, ref audioPowerTouched, ref placed);
            HashSet<uint> first = placed;
            GhostPlaybackLogic.ApplyOnePartEvent(
                tally, state, null, logical, null, Evt(11.0, PartEventType.InventoryPartPlaced, 102222u),
                11.0, true, ref visibilityChanged, ref needsReentryMeshRebuild, ref audioPowerTouched, ref placed);

            Assert.Same(first, placed);
            Assert.Equal(new[] { 101111u, 102222u }, new List<uint>(placed));
            Assert.Equal(new[] { 101111u, 102222u }, new List<uint>(logical));
            Assert.True(visibilityChanged);
            Assert.False(needsReentryMeshRebuild);
            Assert.False(audioPowerTouched);
            Assert.Equal(new List<string>
            {
                "apply family=InventoryPartPlaced surface=inventory rec=1 pid=101111 applied=0 skipped=2 reason=no-info-for-part",
            }, Rendered(tally, 1));
        }

        [UnityEntryFact]
        public void ApplyOnePartEvent_EngineWithTrackedAudio_SetsAudioPowerTouched_AndTalliesBothSurfaces()
        {
            const uint pid = 100000u;
            var tally = new GhostPartEventApplyTally();
            var state = new GhostPlaybackState
            {
                audioMuted = true,
                audioInfos = new Dictionary<ulong, AudioGhostInfo>
                {
                    { FlightRecorder.EncodeEngineKey(pid, 0), new AudioGhostInfo { partPersistentId = pid } },
                },
            };
            bool visibilityChanged = false, needsReentryMeshRebuild = false, audioPowerTouched = false;
            HashSet<uint> placed = null;

            GhostPlaybackLogic.ApplyOnePartEvent(
                tally, state, null, null, null, Evt(10.0, PartEventType.EngineIgnited, pid, 0.75f),
                10.0, true, ref visibilityChanged, ref needsReentryMeshRebuild, ref audioPowerTouched, ref placed);

            Assert.True(audioPowerTouched);
            Assert.False(visibilityChanged);
            Assert.Equal(0.75f, state.audioInfos[FlightRecorder.EncodeEngineKey(pid, 0)].currentPower);
            Assert.Equal(new List<string>
            {
                "apply family=EngineIgnited surface=engine-audio rec=0 pid=100000 applied=1 skipped=0 reason=applied",
                "apply family=EngineIgnited surface=engine-fx rec=0 pid=100000 applied=0 skipped=1 reason=no-family-state",
            }, Rendered(tally, 0));
            // The cap is the walk's job (FinishPartEventWalk), never the arm's.
            Assert.DoesNotContain("CAP enforce", transcript);
        }

        [Fact]
        public void FinishPartEventWalk_AdvancesTheCursor_CapsAudio_ThenLogsTheAggregateAndEachFamily()
        {
            var state = new GhostPlaybackState { partEventIndex = 2 };
            var tally = new GhostPartEventApplyTally();
            tally.Record(PartEventType.Docked, GhostPartEventSurface.Visibility, 100000u,
                GhostPartEventOutcome.UnhandledEventType);

            GhostPlaybackLogic.FinishPartEventWalk(6, state, 5, true, tally);

            Assert.Equal(5, state.partEventIndex);
            AssertTranscript(
                "CAP enforce",
                "LOG [Parsek][VERBOSE][Flight] Applied 3 part events for ghost #6 (evtIdx now 5)",
                "LOG [Parsek][VERBOSE][GhostPartEvents] apply family=Docked surface=visibility rec=6 pid=100000 applied=0 skipped=1 reason=unhandled-event-type");
            Assert.Equal(new List<string> { "Flight|part-events-6" }, RateLimitKeys());
        }

        [Fact]
        public void FinishPartEventWalk_NothingConsumed_WritesNoLine_AndNoCap()
        {
            var state = new GhostPlaybackState { partEventIndex = 4 };

            GhostPlaybackLogic.FinishPartEventWalk(6, state, 4, false, null);

            Assert.Equal(4, state.partEventIndex);
            Assert.Empty(transcript);
            Assert.Empty(RateLimitKeys());
        }

        [Fact]
        public void RefreshGhostAfterPartVisibilityChange_KeepsAnEarlierRebuildRequest_AndAddsNoneOfItsOwn()
        {
            // No compound parts and no live ghost: neither compound pass reports a change, so the
            // request comes only from the walk, and the pivot pass returns on the null ghost.
            var state = new GhostPlaybackState();
            var placed = new HashSet<uint> { 100000u };

            bool rebuild = false;
            GhostPlaybackLogic.RefreshGhostAfterPartVisibilityChange(state, null, placed, ref rebuild);
            Assert.False(rebuild);

            rebuild = true;
            GhostPlaybackLogic.RefreshGhostAfterPartVisibilityChange(state, null, null, ref rebuild);
            Assert.True(rebuild);
            Assert.Empty(transcript);
        }
    }
}
