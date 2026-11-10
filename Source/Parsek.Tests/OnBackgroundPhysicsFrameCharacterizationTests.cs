using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Threading;
using Xunit;
using static Parsek.Tests.BackgroundRecorderCharacterizationSupport;

namespace Parsek.Tests
{
    /// <summary>
    /// Golden characterization of <see cref="BackgroundRecorder.OnBackgroundPhysicsFrame(Vessel)"/>,
    /// recorded against the method before its blocks were extracted (wave 2, W9b-3).
    ///
    /// Headless reach: every early exit (no vessel, a dead vessel, no map entry, a missing or
    /// destroyed recording, packed with and without on-rails state, loaded without loaded state),
    /// the part poll, the finalization-cache refresh, the environment transition, the debris tier,
    /// the anchor detection, the proximity interval and its change line, the out-of-range skip with
    /// its silence window, the in-range silence close and the high-fidelity override. The sampling
    /// tail starts with <c>Krakensbane.GetFrameVelocity()</c> and <c>bgVessel.transform</c>, which
    /// mono cannot run headless, so an in-range tick records the exception type there; the units
    /// extracted from that tail carry their own direct tests.
    ///
    /// Each case dumps every log line, the exception type (if any), the recorder's whole per-pid
    /// state (loaded / on-rails state fields, pending dictionaries, finalization cache) and the
    /// recording after each tick.
    /// </summary>
    [Collection("Sequential")]
    public class OnBackgroundPhysicsFrameCharacterizationTests : IDisposable
    {
        private const uint Pid = 4101u;
        private const uint ParentPid = 4102u;
        private const string RecId = "bg-frame-rec";
        private const string ParentRecId = "bg-frame-parent";

        private readonly List<string> logLines = new List<string>();
        private readonly CultureInfo savedCulture;
        private readonly Planetarium savedPlanetarium;

        public OnBackgroundPhysicsFrameCharacterizationTests()
        {
            savedCulture = Thread.CurrentThread.CurrentCulture;
            // Several log lines format numbers with the thread culture; the golden is
            // recorded under the invariant one so a ro-RO host reads the same text.
            Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;
            RecordingStore.SuppressLogging = true;
            GameStateStore.SuppressLogging = true;
            ParsekLog.ResetTestOverrides();
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.ClockOverrideForTesting = () => 1000.0;
            RecordingStore.ResetForTesting();
            savedPlanetarium = InstallPlanetarium(200.0);
        }

        public void Dispose()
        {
            Planetarium.fetch = savedPlanetarium;
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            Thread.CurrentThread.CurrentCulture = savedCulture;
        }

        // ------------------------------------------------------------ fixtures

        private sealed class Fixture
        {
            public RecordingTree Tree;
            public BackgroundRecorder Bg;
            public Recording Rec;
        }

        private static Fixture Build(bool inMap = true, bool withRecording = true, string mapRecId = RecId,
            bool debris = false)
        {
            var tree = new RecordingTree { Id = "bg-frame-tree" };
            Recording rec = null;
            if (withRecording)
            {
                rec = new Recording
                {
                    RecordingId = RecId,
                    TreeId = tree.Id,
                    VesselName = "Frame Rec",
                    VesselPersistentId = Pid,
                    ExplicitStartUT = 100.0,
                    ExplicitEndUT = 150.0,
                };
                if (debris)
                {
                    rec.IsDebris = true;
                    rec.ParentAnchorRecordingId = ParentRecId;
                    tree.Recordings[ParentRecId] = new Recording
                    {
                        RecordingId = ParentRecId,
                        TreeId = tree.Id,
                        VesselName = "Frame Parent",
                        VesselPersistentId = ParentPid,
                        ExplicitStartUT = 100.0,
                        ExplicitEndUT = 150.0,
                    };
                }
                tree.Recordings[RecId] = rec;
            }
            if (inMap)
                tree.BackgroundMap[Pid] = mapRecId;
            var bg = new BackgroundRecorder(tree);
            return new Fixture { Tree = tree, Bg = bg, Rec = rec };
        }

        private static Vessel LiveVessel(double altitude = 1000.0, bool packed = false,
            bool atmosphere = true)
        {
            return Vessel(Pid, alive: true, body: Body("Kerbin", atmosphere, 70000.0),
                altitude: altitude, packed: packed);
        }

        private static void InitLoaded(Fixture f, TrajectoryPoint? seed = null)
        {
            // OnVesselBackgrounded queues the seed and, with no live vessel found, leaves it
            // pending; InitializeLoadedState (on a dead vessel stub: no transform read) then
            // builds the loaded state from it, as production does for a loaded vessel.
            SetPlanetariumUT(199.0);
            f.Bg.OnVesselBackgrounded(Pid, initialTrajectoryPoint: seed);
            MethodInfo init = typeof(BackgroundRecorder).GetMethod("InitializeLoadedState",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(init);
            Vessel dead = Vessel(Pid, alive: false, body: Body("Kerbin", true, 70000.0), altitude: 1000.0);
            init.Invoke(f.Bg, new object[] { dead, Pid, RecId, null });
        }

        private void Tick(StringBuilder sb, string label, Fixture f, Vessel v, double ut)
        {
            SetPlanetariumUT(ut);
            logLines.Clear();
            string threw = "none";
            try
            {
                f.Bg.OnBackgroundPhysicsFrame(v);
            }
            catch (Exception e)
            {
                threw = e.GetType().Name;
            }
            sb.Append("-- tick ").Append(label).Append(" ut=").Append(D(ut)).Append(" threw=").Append(threw).Append('\n');
            foreach (string l in logLines)
                sb.Append("  log ").Append(l).Append('\n');
            DumpRecorder(sb, f.Bg, Pid);
            DumpRecording(sb, f.Rec);
        }

        private string Run(string name, Action<StringBuilder> body)
        {
            var sb = new StringBuilder();
            sb.Append("=== ").Append(name).Append('\n');
            ParsekLog.ResetRateLimitsForTesting();
            body(sb);
            return sb.ToString();
        }

        // ------------------------------------------------------------ cases

        private IEnumerable<string> Cases()
        {
            yield return Run("F01 null tree", sb =>
            {
                Fixture f = Build();
                typeof(BackgroundRecorder).GetField("tree", BindingFlags.Instance | BindingFlags.NonPublic)
                    .SetValue(f.Bg, null);
                Tick(sb, "a", f, LiveVessel(), 200.0);
            });
            yield return Run("F02 null vessel", sb =>
            {
                Fixture f = Build();
                Tick(sb, "a", f, null, 200.0);
            });
            yield return Run("F03 dead vessel stub", sb =>
            {
                Fixture f = Build();
                InitLoaded(f);
                Tick(sb, "a", f, Vessel(Pid, alive: false, body: null), 200.0);
            });
            yield return Run("F04 not in background map", sb =>
            {
                Fixture f = Build(inMap: false);
                Tick(sb, "a", f, LiveVessel(), 200.0);
            });
            yield return Run("F05 map entry with no recording", sb =>
            {
                Fixture f = Build(withRecording: false);
                Tick(sb, "a", f, LiveVessel(), 200.0);
                Tick(sb, "b-rate-limited", f, LiveVessel(), 200.5);
            });
            yield return Run("F05b map entry with empty id", sb =>
            {
                Fixture f = Build(mapRecId: "");
                Tick(sb, "a", f, LiveVessel(), 200.0);
            });
            yield return Run("F06 destroyed recording", sb =>
            {
                Fixture f = Build();
                InitLoaded(f);
                f.Rec.MarkDestroyedAtTerminal(180.0, "characterization");
                Tick(sb, "a", f, LiveVessel(), 200.0);
            });
            yield return Run("F07 packed without on-rails state", sb =>
            {
                Fixture f = Build();
                InitLoaded(f);
                Tick(sb, "a", f, LiveVessel(packed: true), 200.0);
            });
            yield return Run("F08 packed with on-rails state", sb =>
            {
                Fixture f = Build();
                Tick(sb, "a", f, LiveVessel(packed: true), 200.0);
            });
            yield return Run("F09 loaded without loaded state", sb =>
            {
                Fixture f = Build();
                Tick(sb, "a", f, LiveVessel(), 200.0);
            });
            yield return Run("F10 in range first tick", sb =>
            {
                Fixture f = Build();
                InitLoaded(f);
                f.Bg.SetDistanceOverrideForTesting(v => 100.0);
                Tick(sb, "a", f, LiveVessel(), 200.0);
                Tick(sb, "b-same-interval", f, LiveVessel(), 200.02);
            });
            yield return Run("F11 out of range then back", sb =>
            {
                Fixture f = Build();
                InitLoaded(f);
                f.Bg.SetDistanceOverrideForTesting(v => 1.0e7);
                Tick(sb, "a-out", f, LiveVessel(), 200.0);
                Tick(sb, "b-out", f, LiveVessel(), 201.0);
                f.Bg.SetDistanceOverrideForTesting(v => 2000.0);
                Tick(sb, "c-in", f, LiveVessel(), 202.0);
            });
            yield return Run("F12 interval tiers change", sb =>
            {
                Fixture f = Build();
                InitLoaded(f);
                double dist = 100.0;
                f.Bg.SetDistanceOverrideForTesting(v => dist);
                Tick(sb, "a-100", f, LiveVessel(), 200.0);
                dist = 1500.0;
                Tick(sb, "b-1500", f, LiveVessel(), 200.5);
                dist = 3000.0;
                Tick(sb, "c-3000", f, LiveVessel(), 201.0);
                dist = 2400.0;
                Tick(sb, "d-2400", f, LiveVessel(), 201.5);
            });
            yield return Run("F13 high fidelity overrides the range", sb =>
            {
                Fixture f = Build();
                InitLoaded(f, new TrajectoryPoint
                {
                    ut = 199.5, latitude = 1.0, longitude = 2.0, altitude = 900.0, bodyName = "Kerbin"
                });
                f.Bg.SetDistanceOverrideForTesting(v => 1.0e7);
                Tick(sb, "a", f, LiveVessel(), 200.0);
            });
            yield return Run("F14 debris child of an unresolved parent", sb =>
            {
                Fixture f = Build(debris: true);
                InitLoaded(f);
                f.Bg.SetDistanceOverrideForTesting(v => 100.0);
                Tick(sb, "a-in", f, LiveVessel(), 200.0);
                f.Bg.SetDistanceOverrideForTesting(v => 1.0e7);
                Tick(sb, "b-out", f, LiveVessel(), 201.0);
            });
            yield return Run("F15 environment change above the atmosphere", sb =>
            {
                Fixture f = Build();
                InitLoaded(f);
                f.Bg.SetDistanceOverrideForTesting(v => 1.0e7);
                Tick(sb, "a", f, LiveVessel(altitude: 90000.0), 200.0);
                Tick(sb, "b", f, LiveVessel(altitude: 90000.0), 205.0);
                Tick(sb, "c", f, LiveVessel(altitude: 90000.0), 215.0);
            });
            yield return Run("F16 airless body", sb =>
            {
                Fixture f = Build();
                InitLoaded(f);
                f.Bg.SetDistanceOverrideForTesting(v => 1.0e7);
                Tick(sb, "a", f, LiveVessel(altitude: 5000.0, atmosphere: false), 200.0);
            });
        }

        [UnityEntryFact]
        public void OnBackgroundPhysicsFrame_Golden()
        {
            var sb = new StringBuilder();
            foreach (string c in Cases())
                sb.Append(c);
            AssertGolden("on-background-physics-frame.golden.txt", sb.ToString());
        }
    }
}
