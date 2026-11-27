using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Part parity P9 X2 (design-part-ghost-parity.md, Appendix G.7 and "X2 as built"): the
    /// ghost and recorder seams the burst and dust cases read. None changes what a player sees;
    /// each cell pins either a pure value equal to today's inline one, or that the seam is inert
    /// until a test arms it.
    /// </summary>
    [Collection("Sequential")]
    public class PartParityGhostSeamTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public PartParityGhostSeamTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            GhostTransientFxRecord.Disarm();
        }

        public void Dispose()
        {
            GhostTransientFxRecord.Disarm();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        private static string Root() => SyntheticRecordingTests.ResolveProjectRoot();

        private static string Read(params string[] parts)
            => PartParityStateRulesTests.StripComments(File.ReadAllText(Path.Combine(
                new[] { Root(), "Source", "Parsek" }.Concat(parts).ToArray())));

        private static string Body(string text, string signature)
        {
            string body = PartParityStateRulesTests.MethodBody(text, signature);
            Assert.True(body != null, signature + " moved; this cell is vacuous");
            return body;
        }

        #region PartEvent builders (G.2.1)

        [Fact]
        public void BuildDecoupledPartEvent_IsTheEventTheRecordersWrote()
        {
            PartEvent e = FlightRecorder.BuildDecoupledPartEvent(4242u, "radialDecoupler", 123.5);
            Assert.Equal(123.5, e.ut);
            Assert.Equal(4242u, e.partPersistentId);
            Assert.Equal(PartEventType.Decoupled, e.eventType);
            Assert.Equal("radialDecoupler", e.partName);
            Assert.Equal(0f, e.value);
            Assert.Equal(0, e.moduleIndex);
        }

        [Fact]
        public void BuildDecoupledPartEvent_NullName_ReadsUnknown()
        {
            Assert.Equal("unknown", FlightRecorder.BuildDecoupledPartEvent(1u, null, 0.0).partName);
        }

        [Theory]
        [InlineData(PartEventType.Destroyed)]
        [InlineData(PartEventType.ParachuteDestroyed)]
        public void BuildDestroyedPartEvent_CarriesTheClassifiedDeath(PartEventType death)
        {
            PartEvent e = FlightRecorder.BuildDestroyedPartEvent(77u, "fuelTank", 9.25, death);
            Assert.Equal(9.25, e.ut);
            Assert.Equal(77u, e.partPersistentId);
            Assert.Equal(death, e.eventType);
            Assert.Equal("fuelTank", e.partName);
            Assert.Equal(0f, e.value);
            Assert.Equal(0, e.moduleIndex);
            Assert.Equal("unknown", FlightRecorder.BuildDestroyedPartEvent(77u, null, 9.25, death).partName);
        }

        [Fact]
        public void BuildDestroyedPartEvent_WithClassifyPartDeath_MatchesTheDieHandlersChain()
        {
            // A canopy out when the part dies is a ParachuteDestroyed; anything else Destroyed.
            var states = new Dictionary<uint, int> { [5u] = 2 };
            PartEventType chute = FlightRecorder.ClassifyPartDeath(5u, true, states);
            Assert.Equal(PartEventType.ParachuteDestroyed,
                FlightRecorder.BuildDestroyedPartEvent(5u, "parachuteSingle", 1.0, chute).eventType);
            PartEventType plain = FlightRecorder.ClassifyPartDeath(6u, false, states);
            Assert.Equal(PartEventType.Destroyed,
                FlightRecorder.BuildDestroyedPartEvent(6u, "fuelTank", 1.0, plain).eventType);
        }

        // A decouple or death event built in place. In the two recorder files the die handler's
        // classified type is the local evtType, so there that name counts too.
        private const string InlineDecoupleOrDeath =
            @"new\s+PartEvent\s*\{[^}]*eventType\s*=\s*PartEventType\.(Decoupled|Destroyed|ParachuteDestroyed)\b";
        private const string InlineInRecorder =
            @"new\s+PartEvent\s*\{[^}]*eventType\s*=\s*(PartEventType\.(Decoupled|Destroyed|ParachuteDestroyed)|evtType)\b";

        /// <summary>The text with the two builders' own bodies cut out (they ARE the construction).</summary>
        private static string WithoutBuilders(string text)
        {
            foreach (string sig in new[] { "internal static PartEvent BuildDecoupledPartEvent(",
                                           "internal static PartEvent BuildDestroyedPartEvent(" })
            {
                string body = PartParityStateRulesTests.MethodBody(text, sig);
                if (body != null) text = text.Replace(body, "{}");
            }
            return text;
        }

        [Fact]
        public void BothRecorders_BuildDecoupleAndDeathEventsThroughTheBuilders()
        {
            string fr = Read("Recording", "FlightRecorder.PartEvents.cs");
            string bg = Read("Recording", "BackgroundRecorder.GameEventHandlers.cs");
            Assert.Matches(InlineDecoupleOrDeath, fr);   // the builder itself: the cut below is not vacuous
            Assert.DoesNotMatch(InlineInRecorder, WithoutBuilders(fr));
            Assert.DoesNotMatch(InlineInRecorder, bg);

            Assert.Contains("PartEvents.Add(BuildDestroyedPartEvent(\n                p.persistentId, p.partInfo?.name, Planetarium.GetUniversalTime(), evtType));",
                Body(fr, "private void OnPartDie(Part p)").Replace("\r\n", "\n"));
            Assert.Contains("var evtType = ClassifyPartDeath(p.persistentId, hasChute, parachuteStates);",
                Body(fr, "private void OnPartDie(Part p)"));
            Assert.Contains("PartEvents.Add(BuildDecoupledPartEvent(",
                Body(fr, "internal void RecordJointBreakDecoupledEvent("));
            Assert.Contains("PartEvents.Add(BuildDecoupledPartEvent(partPid, partName, ut));",
                Body(fr, "internal int RecordFallbackDecoupleEvent("));

            string bgDie = Body(bg, "internal void OnBackgroundPartDie(");
            Assert.Contains("FlightRecorder.ClassifyPartDeath(", bgDie);
            Assert.Contains("treeRec.PartEvents.Add(FlightRecorder.BuildDestroyedPartEvent(", bgDie);
            Assert.Contains("treeRec.PartEvents.Add(FlightRecorder.BuildDecoupledPartEvent(",
                Body(bg, "internal void OnBackgroundPartJointBreak("));

            // The recorders hand the builders the same reads: the joint child, its name, the UT.
            foreach (string text in new[] { fr, bg })
            {
                foreach (Match m in Regex.Matches(text, @"BuildDecoupledPartEvent\(\s*joint\.Child[^;]*;"))
                    Assert.Matches(@"^BuildDecoupledPartEvent\(\s*joint\.Child\.persistentId,\s*joint\.Child\.partInfo\?\.name,\s*jointBreakUT\)\);$", m.Value);
                foreach (Match m in Regex.Matches(text, @"Add\((?:FlightRecorder\.)?BuildDestroyedPartEvent\([^;]*;"))
                    Assert.Matches(@"BuildDestroyedPartEvent\(\s*p\.persistentId,\s*p\.partInfo\?\.name,\s*Planetarium\.GetUniversalTime\(\),\s*evtType\)\);$", m.Value);
            }
            Assert.Equal(2, Regex.Matches(fr, @"\bBuildDecoupledPartEvent\(").Count - 1);  // minus the declaration
            Assert.Equal(1, Regex.Matches(bg, @"\bBuildDecoupledPartEvent\(").Count);
            Assert.Equal(1, Regex.Matches(fr, @"\bBuildDestroyedPartEvent\(").Count - 1);
            Assert.Equal(1, Regex.Matches(bg, @"\bBuildDestroyedPartEvent\(").Count);
        }

        [Fact]
        public void NoProductionFile_BuildsADecoupleOrDeathEventInline()
        {
            // In-game tests author fixture events by hand; every production writer goes through
            // the builders, so the part parity burst driver (X4) cannot drift from the recorders.
            var offenders = new List<string>();
            string src = Path.Combine(Root(), "Source", "Parsek");
            foreach (string file in Directory.GetFiles(src, "*.cs", SearchOption.AllDirectories))
            {
                string rel = file.Substring(src.Length + 1).Replace('\\', '/');
                if (rel.StartsWith("InGameTests/", StringComparison.Ordinal)) continue;
                string text = WithoutBuilders(PartParityStateRulesTests.StripComments(File.ReadAllText(file)));
                if (Regex.IsMatch(text, InlineDecoupleOrDeath)) offenders.Add(rel);
            }
            Assert.True(offenders.Count == 0, string.Join("\n", offenders));
        }

        [Fact]
        public void ThePin_CatchesAnInlineDecoupleOrDeathEvent()
        {
            Assert.Matches(InlineDecoupleOrDeath,
                "x.Add(new PartEvent { ut = u, partPersistentId = p, eventType = PartEventType.Decoupled, partName = n });");
            Assert.Matches(InlineInRecorder,
                "x.Add(new PartEvent\n{\n ut = u,\n eventType = evtType,\n partName = n\n});");
            Assert.Matches(InlineDecoupleOrDeath,
                "x.Add(new PartEvent\n{\n ut = u,\n eventType = PartEventType.ParachuteDestroyed\n});");
            Assert.DoesNotMatch(InlineDecoupleOrDeath,
                "x.Add(new PartEvent { ut = u, eventType = PartEventType.EngineIgnited });");
        }

        #endregion

        #region TerminalExplosionPower

        [Theory]
        [InlineData(0f, 0.0)]
        [InlineData(-3f, 0.0)]
        [InlineData(5f, 0.25)]
        [InlineData(10f, 0.5)]
        [InlineData(20f, 1.0)]
        [InlineData(45f, 1.0)]
        public void TerminalExplosionPower_IsTheLengthOverTwentyClamped(float length, double expected)
        {
            Assert.Equal(expected, GhostPlaybackEngine.TerminalExplosionPower(length), 6);
        }

        [Fact]
        public void TerminalExplosionPower_EqualsTheOldInlineValueBitForBit()
        {
            for (int i = -40; i <= 800; i++)
            {
                float length = i * 0.0625f;
                double old = Mathf.Clamp01(length / GhostAudioPresets.VesselLengthPowerDivisor);
                Assert.Equal(old, GhostPlaybackEngine.TerminalExplosionPower(length));
            }
            Assert.True(double.IsNaN(GhostPlaybackEngine.TerminalExplosionPower(float.NaN)));
        }

        [Fact]
        public void PlayDestroyedExplosion_ReadsTerminalExplosionPower()
        {
            string body = Body(Read("Ghost", "GhostPlaybackEngine.SpawnDestroy.cs"),
                "private void PlayDestroyedExplosion(");
            Assert.Contains("double power = TerminalExplosionPower(vesselLength);", body);
            Assert.DoesNotContain("VesselLengthPowerDivisor", body);
        }

        #endregion

        #region Launch dust frame body

        // DriveLaunchDustFrame and DriveLaunchDustToZero call ParticleSystem natives in their
        // bodies, so the Windows .NET host refuses them on entry; mono (CI) carries these four.

        private static GhostPlaybackState DustState(bool pendingBuild)
        {
            return new GhostPlaybackState
            {
                vesselName = "Dust",
                launchDustPendingBuild = pendingBuild,
                launchDustGroundRefAltitude = 70.0,
                lastInterpolatedAltitude = 75.0,
            };
        }

        [UnityEntryFact]
        public void DriveLaunchDustFrame_OutOfReach_WritesNothing()
        {
            var state = DustState(pendingBuild: true);
            int count = 0;
            // 40 m or more above the reference: no dust, no build, the counter untouched.
            GhostPlaybackEngine.DriveLaunchDustFrame(3, state, 1f, 110.0, 70.0, ref count);
            Assert.Equal(0, count);
            Assert.True(state.launchDustPendingBuild);
            Assert.Null(state.launchDustInfo);
            Assert.Equal(70.0, state.launchDustGroundRefAltitude);
        }

        [UnityEntryFact]
        public void DriveLaunchDustFrame_NoPendingBuild_BuildsNothing()
        {
            var state = DustState(pendingBuild: false);
            int count = 0;
            GhostPlaybackEngine.DriveLaunchDustFrame(3, state, 1f, 75.0, 70.0, ref count);
            Assert.Equal(0, count);
            Assert.Null(state.launchDustInfo);
        }

        [UnityEntryFact]
        public void DriveLaunchDustFrame_AtTheBuildCap_DefersAndKeepsThePendingFlag()
        {
            var state = DustState(pendingBuild: true);
            int count = GhostPlayback.MaxLazyReentryBuildsPerFrame;
            GhostPlaybackEngine.DriveLaunchDustFrame(3, state, 1f, 75.0, 70.0, ref count);
            Assert.Equal(GhostPlayback.MaxLazyReentryBuildsPerFrame, count);
            Assert.True(state.launchDustPendingBuild);
            Assert.Null(state.launchDustInfo);
            Assert.Contains(logLines, l => l.Contains("[GhostVisual]")
                && l.Contains("Launch dust build throttled for ghost #3")
                && l.Contains("used " + GhostPlayback.MaxLazyReentryBuildsPerFrame + "/"));
        }

        [UnityEntryFact]
        public void DriveLaunchDustFrame_ReadsTheGroundReferenceAndAltitudeItIsPassed()
        {
            // The ghost's own fields say "in reach"; the passed reference says 100 m below the
            // case altitude, so the frame body (not the state) decides: nothing happens.
            var state = DustState(pendingBuild: true);
            int count = 0;
            GhostPlaybackEngine.DriveLaunchDustFrame(3, state, 1f, 200.0, 100.0, ref count);
            Assert.Equal(0, count);
            Assert.True(state.launchDustPendingBuild);
            // And the reverse: the state out of reach, the passed pair in reach, at the cap.
            state.lastInterpolatedAltitude = 500.0;
            count = GhostPlayback.MaxLazyReentryBuildsPerFrame;
            GhostPlaybackEngine.DriveLaunchDustFrame(4, state, 1f, 105.0, 100.0, ref count);
            Assert.Contains(logLines, l => l.Contains("Launch dust build throttled for ghost #4"));
        }

        [Fact]
        public void UpdateLaunchDust_EndsInTheFrameBodyWithTheGhostsOwnInputs()
        {
            string text = Read("Ghost", "GhostPlaybackEngine.ReentryFx.cs");
            string body = Body(text, "private void UpdateLaunchDust(");
            Assert.Matches(@"if \(double\.IsInfinity\(state\.launchDustGroundRefAltitude\)\)\s*return;\s*"
                + @"DriveLaunchDustFrame\(recIdx, state, enginePower, state\.lastInterpolatedAltitude,\s*"
                + @"state\.launchDustGroundRefAltitude, ref frameLazyReentryBuildCount\);\s*\}$", body);
            string frame = Body(text, "internal static void DriveLaunchDustFrame(");
            Assert.DoesNotContain("lastInterpolatedAltitude", frame);
            Assert.DoesNotContain("frameLazyReentryBuildCount", frame);
            // The one ground-reference write left is the "never" latch of a failed build.
            Assert.Single(Regex.Matches(frame, @"state\.launchDustGroundRefAltitude\b").Cast<Match>());
            Assert.Contains("state.launchDustGroundRefAltitude = double.NegativeInfinity;", frame);
        }

        #endregion

        #region Transient FX record

        [Fact]
        public void Record_Unarmed_NotesNothing()
        {
            Assert.False(GhostTransientFxRecord.IsArmed);
            GhostTransientFxRecord.Note(GhostTransientFxKind.PartPuff, Vector3.one, double.NaN, 2f, null);
            Assert.Empty(GhostTransientFxRecord.Entries);
            Assert.DoesNotContain(logLines, l => l.Contains("[TransientFx]"));
        }

        [Fact]
        public void Record_Armed_KeepsEveryStartInOrder()
        {
            GhostTransientFxRecord.Arm();
            GhostTransientFxRecord.Note(GhostTransientFxKind.StockExplosion, new Vector3(1f, 2f, 3f), 0.75, float.NaN, null);
            GhostTransientFxRecord.Note(GhostTransientFxKind.CustomExplosion, new Vector3(4f, 5f, 6f), double.NaN, 12f, null);
            GhostTransientFxRecord.Note(GhostTransientFxKind.PartPuff, new Vector3(7f, 8f, 9f), double.NaN, 1.5f, null);

            var e = GhostTransientFxRecord.Entries;
            Assert.Equal(3, e.Count);
            Assert.Equal(new[] { 0, 1, 2 }, e.Select(x => x.Sequence));
            Assert.Equal(new[] { GhostTransientFxKind.StockExplosion, GhostTransientFxKind.CustomExplosion,
                GhostTransientFxKind.PartPuff }, e.Select(x => x.Kind));
            Assert.Equal(new Vector3(4f, 5f, 6f), e[1].Position);
            Assert.Equal(0.75, e[0].Power);
            Assert.True(float.IsNaN(e[0].Size));
            Assert.True(double.IsNaN(e[1].Power));
            Assert.Equal(12f, e[1].Size);
            Assert.Equal(1.5f, e[2].Size);
            Assert.Contains(logLines, l => l.Contains("[TransientFx]") && l.Contains("Transient FX #1 CustomExplosion at (4.00,5.00,6.00)")
                && l.Contains("size=12") && l.Contains("object=no"));
        }

        [Fact]
        public void Record_IsReadOnlyToItsReaders()
        {
            GhostTransientFxRecord.Arm();
            GhostTransientFxRecord.Note(GhostTransientFxKind.PartPuff, Vector3.zero, double.NaN, 1f, null);
            var list = (IList<GhostTransientFxEntry>)GhostTransientFxRecord.Entries;
            Assert.True(list.IsReadOnly);
            Assert.Throws<NotSupportedException>(() => list.Add(default(GhostTransientFxEntry)));
            Assert.Throws<NotSupportedException>(() => list.RemoveAt(0));
            Assert.Throws<NotSupportedException>(() => list[0] = default(GhostTransientFxEntry));
            Assert.Single(GhostTransientFxRecord.Entries);
        }

        [Fact]
        public void Record_ArmOpensAFreshRecord_DisarmDropsIt()
        {
            GhostTransientFxRecord.Arm();
            GhostTransientFxRecord.Note(GhostTransientFxKind.PartPuff, Vector3.zero, double.NaN, 1f, null);
            var first = GhostTransientFxRecord.Entries;
            GhostTransientFxRecord.Arm();
            Assert.Empty(GhostTransientFxRecord.Entries);
            Assert.Single(first);   // an earlier reader's view is not cleared under it
            Assert.Contains(logLines, l => l.Contains("Transient FX record re-armed (dropped 1 entries)"));
            GhostTransientFxRecord.Note(GhostTransientFxKind.PartPuff, Vector3.zero, double.NaN, 1f, null);
            Assert.Equal(0, GhostTransientFxRecord.Entries[0].Sequence);
            GhostTransientFxRecord.Disarm();
            Assert.False(GhostTransientFxRecord.IsArmed);
            Assert.Empty(GhostTransientFxRecord.Entries);
            Assert.Contains(logLines, l => l.Contains("Transient FX record disarmed after 1 entries"));
        }

        [Fact]
        public void TryTriggerStockExplosionFx_NotesAQueuedExplosionWithItsPower()
        {
            GhostTransientFxRecord.Arm();
            var exploded = new List<double>();
            bool ok = GhostVisualBuilder.TryTriggerStockExplosionFx(
                new Vector3(10f, 20f, 30f), 0.4, out string failure,
                isFxMongerAvailable: () => true, explode: (p, pw) => exploded.Add(pw));
            Assert.True(ok);
            Assert.Null(failure);
            Assert.Equal(new[] { 0.4 }, exploded);
            var e = Assert.Single(GhostTransientFxRecord.Entries);
            Assert.Equal(GhostTransientFxKind.StockExplosion, e.Kind);
            Assert.Equal(new Vector3(10f, 20f, 30f), e.Position);
            Assert.Equal(0.4, e.Power);
            Assert.Null(e.Object);
        }

        [Fact]
        public void TryTriggerStockExplosionFx_NotesNothingItDidNotStart()
        {
            GhostTransientFxRecord.Arm();
            Assert.False(GhostVisualBuilder.TryTriggerStockExplosionFx(
                Vector3.zero, 1.0, out string unavailable, isFxMongerAvailable: () => false,
                explode: (p, pw) => { }));
            Assert.Equal("no live FXMonger instance", unavailable);
            Assert.False(GhostVisualBuilder.TryTriggerStockExplosionFx(
                Vector3.zero, 1.0, out string threw, isFxMongerAvailable: () => true,
                explode: (p, pw) => throw new InvalidOperationException("boom")));
            Assert.Equal("boom", threw);
            Assert.Empty(GhostTransientFxRecord.Entries);
        }

        [Fact]
        public void TryTriggerStockExplosionFx_Unarmed_StillQueuesExactlyAsBefore()
        {
            var exploded = new List<double>();
            Assert.True(GhostVisualBuilder.TryTriggerStockExplosionFx(
                Vector3.zero, 0.9, out string failure, isFxMongerAvailable: () => true,
                explode: (p, pw) => exploded.Add(pw)));
            Assert.Null(failure);
            Assert.Equal(new[] { 0.9 }, exploded);
            Assert.Empty(GhostTransientFxRecord.Entries);
        }

        [Fact]
        public void ParseksOwnEffects_AreNotedWithTheirObjectBeforeTheyAreReturned()
        {
            string text = Read("Ghost", "GhostVisualBuilder.TransientFx.cs");
            Assert.Matches(@"GhostTransientFxRecord\.Note\(\s*GhostTransientFxKind\.CustomExplosion, worldPosition, double\.NaN, vesselLength, obj\);\s*return obj;\s*\}$",
                Body(text, "internal static GameObject SpawnExplosionFx("));
            Assert.Matches(@"GhostTransientFxRecord\.Note\(\s*GhostTransientFxKind\.PartPuff, worldPosition, double\.NaN, partScale, obj\);\s*return obj;\s*\}$",
                Body(text, "internal static GameObject SpawnPartPuffFx("));
            // Every Note in production is one of these three starts.
            int notes = 0;
            string src = Path.Combine(Root(), "Source", "Parsek");
            foreach (string file in Directory.GetFiles(src, "*.cs", SearchOption.AllDirectories))
                notes += Regex.Matches(PartParityStateRulesTests.StripComments(File.ReadAllText(file)),
                    @"GhostTransientFxRecord\.Note\(").Count;
            Assert.Equal(3, notes);
        }

        #endregion

        #region Reentry isolation frame

        private static readonly MethodInfo DriveLayers = typeof(GhostPlaybackEngine).GetMethod(
            "DriveReentryLayers", BindingFlags.Instance | BindingFlags.NonPublic);

        private static void Drive(GhostPlaybackEngine engine, ReentryFxInfo info, float intensity)
        {
            Assert.NotNull(DriveLayers);
            DriveLayers.Invoke(engine, new object[]
            {
                info, intensity, Vector3.zero, 7, "Kerbin", 30000.0, 3f, "Probe", null,
            });
        }

        [Fact]
        public void Isolation_Predicates()
        {
            Assert.False(GhostPlaybackEngine.ReentryIsolationHidesFireAndShell(ReentryIsolation.None));
            Assert.True(GhostPlaybackEngine.ReentryIsolationHidesFireAndShell(ReentryIsolation.FireAndShell));
            Assert.True(GhostPlaybackEngine.ReentryIsolationHidesFireAndShell(ReentryIsolation.FireShellAndGlow));
            Assert.Equal(0.8f, GhostPlaybackEngine.ReentryIsolationGlowIntensity(0.8f, ReentryIsolation.None));
            Assert.Equal(0.8f, GhostPlaybackEngine.ReentryIsolationGlowIntensity(0.8f, ReentryIsolation.FireAndShell));
            Assert.Equal(0f, GhostPlaybackEngine.ReentryIsolationGlowIntensity(0.8f, ReentryIsolation.FireShellAndGlow));
        }

        [Fact]
        public void Isolation_UnarmedByDefault_ConsumeIsInert()
        {
            var info = new ReentryFxInfo();
            Assert.Equal(ReentryIsolation.None, info.isolationArmed);
            Assert.False(info.fireHiddenForIsolation);
            Assert.Equal(ReentryIsolation.None, GhostPlaybackEngine.ConsumeReentryIsolation(info, 1));
            Assert.Equal(ReentryIsolation.None, GhostPlaybackEngine.ConsumeReentryIsolation(null, 1));
            Assert.DoesNotContain(logLines, l => l.Contains("Reentry isolation"));
        }

        [Fact]
        public void Isolation_ArmNeedsBuiltReentryFx()
        {
            var state = new GhostPlaybackState { vesselName = "Probe" };
            Assert.False(GhostPlaybackEngine.ArmReentryIsolationForOneFrame(state, ReentryIsolation.FireAndShell));
            Assert.False(GhostPlaybackEngine.ArmReentryIsolationForOneFrame(null, ReentryIsolation.FireAndShell));
            Assert.Contains(logLines, l => l.Contains("Reentry isolation not armed for \"Probe\": no reentry FX built"));
            state.reentryFxInfo = new ReentryFxInfo();
            Assert.True(GhostPlaybackEngine.ArmReentryIsolationForOneFrame(state, ReentryIsolation.FireShellAndGlow));
            Assert.Equal(ReentryIsolation.FireShellAndGlow, state.reentryFxInfo.isolationArmed);
        }

        [Fact]
        public void Isolation_ConsumeAppliesOnce()
        {
            var info = new ReentryFxInfo { isolationArmed = ReentryIsolation.FireAndShell };
            Assert.Equal(ReentryIsolation.FireAndShell, GhostPlaybackEngine.ConsumeReentryIsolation(info, 9));
            Assert.Equal(ReentryIsolation.None, info.isolationArmed);
            Assert.Equal(ReentryIsolation.None, GhostPlaybackEngine.ConsumeReentryIsolation(info, 9));
            Assert.Single(logLines.Where(l => l.Contains("Reentry isolation frame applied to ghost #9: mode=FireAndShell")));
        }

        [Fact]
        public void Isolation_HidesTheFireForOneDrive_AndTheNextDriveRestoresIt()
        {
            var engine = new GhostPlaybackEngine(positioner: null);
            var info = new ReentryFxInfo { isolationArmed = ReentryIsolation.FireAndShell };
            Drive(engine, info, 0.6f);
            Assert.Equal(ReentryIsolation.None, info.isolationArmed);
            Assert.True(info.fireHiddenForIsolation);
            Assert.Contains(logLines, l => l.Contains("Reentry sheath hidden for isolation on ghost #7"));
            Drive(engine, info, 0.6f);
            Assert.False(info.fireHiddenForIsolation);
            Assert.Contains(logLines, l => l.Contains("Reentry sheath restored for isolation on ghost #7"));
            int writes = logLines.Count(l => l.Contains("Reentry sheath"));
            Drive(engine, info, 0.6f);
            Assert.Equal(writes, logLines.Count(l => l.Contains("Reentry sheath")));
        }

        [Fact]
        public void Isolation_TwoFramesInARow_KeepTheFireHiddenUntilTheFirstPlainDrive()
        {
            var engine = new GhostPlaybackEngine(positioner: null);
            var info = new ReentryFxInfo { isolationArmed = ReentryIsolation.FireAndShell };
            Drive(engine, info, 0.6f);
            info.isolationArmed = ReentryIsolation.FireShellAndGlow;
            Drive(engine, info, 0.6f);
            Assert.True(info.fireHiddenForIsolation);
            Assert.Single(logLines.Where(l => l.Contains("Reentry sheath hidden")));
            Drive(engine, info, 0.6f);
            Assert.False(info.fireHiddenForIsolation);
        }

        [Fact]
        public void DriveReentryLayers_ReadsTheIsolationOnlyThroughItsSeam()
        {
            string body = Body(Read("Ghost", "GhostPlaybackEngine.ReentryFx.cs"), "private void DriveReentryLayers(");
            Assert.Contains("ReentryIsolation isolation = ConsumeReentryIsolation(info, recIdx);", body);
            Assert.Contains("float heat = GhostAeroFx.ThermalShare(intensity, info.aeroState);", body);
            Assert.Contains("float glowIntensity = ReentryIsolationGlowIntensity(heat, isolation);", body);
            Assert.Contains("if (glowIntensity <= GhostVisualBuilder.ReentryLayerAThreshold)", body);
            Assert.Contains("Mathf.InverseLerp(GhostVisualBuilder.ReentryLayerAThreshold, 1f, glowIntensity)", body);
            // The sheath offer carries the isolation, so the renderer logs it as Isolated.
            Assert.Contains("GhostAeroFxRenderer.OfferSheath(info, state?.ghost, intensity, info.aeroState,", body);
            Assert.Contains("surfaceVel, recIdx, isolateFire);", body);
            // The char is the body, not an effect: it keeps the heat share, not the glow isolation.
            Assert.Contains("GhostPlaybackLogic.ApplyColorChangerCharState(state, heat);", body);
            Assert.Equal(2, Regex.Matches(body, @"\bglowIntensity\b").Count - 1);
        }

        #endregion
    }
}
