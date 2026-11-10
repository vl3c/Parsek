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
    /// Golden characterization of <c>BackgroundRecorder.InitializeLoadedState</c>, recorded against
    /// the method before its blocks were extracted (wave 2, W9b-3).
    ///
    /// The method is reached by reflection with a DEAD vessel stub (Unity's <c>==</c> reads it as
    /// null, so the <c>v.transform</c> read is skipped, while its managed fields stay readable):
    /// environment classification (atmosphere, airless, surface, EVA, no body), the queued
    /// environment override, the seed point (clamped to now, applied to the recording or to the
    /// section), the part-event seed skip, a missing recording, the inherited engine merge, and the
    /// debris parent-anchor contract up to the Absolute fallback. The live-parent Relative seed
    /// branch needs a loaded parent vessel, which no headless lookup returns; its extracted units
    /// carry direct tests.
    ///
    /// Each case dumps every log line, the exception type (if any), the recorder's whole per-pid
    /// state and the recording.
    /// </summary>
    [Collection("Sequential")]
    public class InitializeLoadedStateCharacterizationTests : IDisposable
    {
        private const uint Pid = 4201u;
        private const uint ParentPid = 4202u;
        private const string RecId = "bg-init-rec";
        private const string ParentRecId = "bg-init-parent";

        private readonly List<string> logLines = new List<string>();
        private readonly CultureInfo savedCulture;
        private readonly Planetarium savedPlanetarium;

        public InitializeLoadedStateCharacterizationTests()
        {
            savedCulture = Thread.CurrentThread.CurrentCulture;
            // Several log lines format numbers with the thread culture; the golden is
            // recorded under the invariant one so a ro-RO host reads the same text.
            Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;
            RecordingStore.SuppressLogging = true;
            GameStateStore.SuppressLogging = true;
            // ParsekSettings' static constructor logs once per AppDomain; run it before the
            // sink is installed so the golden does not depend on which test touched it first.
            System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(ParsekSettings).TypeHandle);
            ParsekLog.ResetTestOverrides();
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.ClockOverrideForTesting = () => 1000.0;
            RecordingStore.ResetForTesting();
            savedPlanetarium = InstallPlanetarium(300.0);
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

        private sealed class Fixture
        {
            public BackgroundRecorder Bg;
            public Recording Rec;
        }

        private static Fixture Build(bool withRecording = true, bool debris = false, bool withPartEvents = false)
        {
            var tree = new RecordingTree { Id = "bg-init-tree" };
            Recording rec = null;
            if (withRecording)
            {
                rec = new Recording
                {
                    RecordingId = RecId,
                    TreeId = tree.Id,
                    VesselName = "Init Rec",
                    VesselPersistentId = Pid,
                    ExplicitStartUT = 250.0,
                    ExplicitEndUT = 280.0,
                };
                if (withPartEvents)
                {
                    rec.PartEvents.Add(new PartEvent
                    {
                        ut = 260.0,
                        partPersistentId = 77u,
                        eventType = PartEventType.LightOn,
                        partName = "light",
                    });
                }
                if (debris)
                {
                    rec.IsDebris = true;
                    rec.ParentAnchorRecordingId = ParentRecId;
                    tree.Recordings[ParentRecId] = new Recording
                    {
                        RecordingId = ParentRecId,
                        TreeId = tree.Id,
                        VesselName = "Init Parent",
                        VesselPersistentId = ParentPid,
                        ExplicitStartUT = 250.0,
                        ExplicitEndUT = 280.0,
                    };
                }
                tree.Recordings[RecId] = rec;
            }
            tree.BackgroundMap[Pid] = RecId;
            return new Fixture { Bg = new BackgroundRecorder(tree), Rec = rec };
        }

        private static Vessel Dead(double altitude = 1000.0, bool atmosphere = true, bool noBody = false,
            Vessel.Situations situation = Vessel.Situations.FLYING, VesselType type = VesselType.Ship,
            double srfSpeed = 50.0, double heightFromTerrain = 900.0, bool ocean = false)
        {
            CelestialBody body = null;
            if (!noBody)
            {
                body = Body(atmosphere ? "Kerbin" : "Mun", atmosphere, atmosphere ? 70000.0 : 0.0);
                body.ocean = ocean;
                body.Radius = atmosphere ? 600000.0 : 200000.0;
            }
            Vessel v = Vessel(Pid, alive: false, body: body, altitude: altitude);
            v.situation = situation;
            v.vesselType = type;
            v.srfSpeed = srfSpeed;
            v.heightFromTerrain = (float)heightFromTerrain;
            return v;
        }

        private static TrajectoryPoint Seed(double ut) => new TrajectoryPoint
        {
            ut = ut, latitude = -0.1, longitude = -74.5, altitude = 1200.0, bodyName = "Kerbin",
            velocity = new UnityEngine.Vector3(1f, 2f, 3f), recordedGroundClearance = double.NaN
        };

        private void Init(StringBuilder sb, string label, Fixture f, Vessel v, double ut,
            InheritedEngineState? inherited = null, string recordingId = RecId)
        {
            SetPlanetariumUT(ut);
            logLines.Clear();
            string threw = "none";
            MethodInfo init = typeof(BackgroundRecorder).GetMethod("InitializeLoadedState",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(init);
            try
            {
                init.Invoke(f.Bg, new object[] { v, Pid, recordingId, inherited });
            }
            catch (TargetInvocationException e)
            {
                threw = e.InnerException?.GetType().Name ?? "TargetInvocationException";
            }
            sb.Append("-- init ").Append(label).Append(" ut=").Append(D(ut)).Append(" threw=").Append(threw).Append('\n');
            foreach (string l in logLines)
                sb.Append("  log ").Append(l).Append('\n');
            DumpRecorder(sb, f.Bg, Pid);
            DumpRecording(sb, f.Rec);
        }

        private void Queue(Fixture f, SegmentEnvironment? env = null, TrajectoryPoint? seed = null)
        {
            // OnVesselBackgrounded queues the override / seed and, with no live vessel found,
            // leaves them pending for InitializeLoadedState to consume.
            SetPlanetariumUT(299.0);
            f.Bg.OnVesselBackgrounded(Pid, initialEnvironmentOverride: env, initialTrajectoryPoint: seed);
        }

        private string Run(string name, Action<StringBuilder> body)
        {
            var sb = new StringBuilder();
            sb.Append("=== ").Append(name).Append('\n');
            ParsekLog.ResetRateLimitsForTesting();
            body(sb);
            return sb.ToString();
        }

        private IEnumerable<string> Cases()
        {
            yield return Run("I01 flying in atmosphere, nothing queued", sb =>
            {
                Fixture f = Build();
                Init(sb, "a", f, Dead(), 300.0);
            });
            yield return Run("I02 above the atmosphere", sb =>
            {
                Fixture f = Build();
                Init(sb, "a", f, Dead(altitude: 90000.0), 300.0);
            });
            yield return Run("I03 airless body", sb =>
            {
                Fixture f = Build();
                Init(sb, "a", f, Dead(altitude: 5000.0, atmosphere: false), 300.0);
            });
            yield return Run("I04 landed", sb =>
            {
                Fixture f = Build();
                Init(sb, "a", f, Dead(altitude: 70.0, situation: Vessel.Situations.LANDED, srfSpeed: 0.0,
                    heightFromTerrain: 1.5), 300.0);
            });
            yield return Run("I05 splashed on an ocean body", sb =>
            {
                Fixture f = Build();
                Init(sb, "a", f, Dead(altitude: 0.0, situation: Vessel.Situations.SPLASHED, srfSpeed: 2.0,
                    heightFromTerrain: -1.0, ocean: true), 300.0);
            });
            yield return Run("I06 EVA kerbal on the surface", sb =>
            {
                Fixture f = Build();
                Init(sb, "a", f, Dead(altitude: 70.0, situation: Vessel.Situations.LANDED, type: VesselType.EVA,
                    srfSpeed: 1.0, heightFromTerrain: 0.5), 300.0);
            });
            yield return Run("I07 no main body", sb =>
            {
                Fixture f = Build();
                Init(sb, "a", f, Dead(noBody: true), 300.0);
            });
            yield return Run("I08 queued environment override", sb =>
            {
                Fixture f = Build();
                Queue(f, env: SegmentEnvironment.SurfaceStationary);
                Init(sb, "a", f, Dead(), 300.0);
            });
            yield return Run("I09 seed point applied to the recording", sb =>
            {
                Fixture f = Build();
                Queue(f, seed: Seed(299.5));
                Init(sb, "a", f, Dead(), 300.0);
            });
            yield return Run("I10 seed point after now is clamped", sb =>
            {
                Fixture f = Build();
                Queue(f, seed: Seed(305.0));
                Init(sb, "a", f, Dead(), 300.0);
            });
            yield return Run("I11 recording already has part events", sb =>
            {
                Fixture f = Build(withPartEvents: true);
                Queue(f, seed: Seed(299.5));
                Init(sb, "a", f, Dead(), 300.0);
            });
            yield return Run("I12 recording missing from the tree", sb =>
            {
                Fixture f = Build();
                Queue(f, seed: Seed(299.5));
                Init(sb, "a", f, Dead(), 300.0, recordingId: "bg-init-missing");
            });
            yield return Run("I13 inherited engine state", sb =>
            {
                Fixture f = Build();
                var inherited = new InheritedEngineState
                {
                    activeEngineKeys = new HashSet<ulong> { (100000UL << 8) | 0u },
                    engineThrottles = new Dictionary<ulong, float> { { (100000UL << 8) | 0u, 0.75f } },
                    activeRcsKeys = new HashSet<ulong>(),
                    rcsThrottles = new Dictionary<ulong, float>(),
                };
                Init(sb, "a", f, Dead(), 300.0, inherited: inherited);
            });
            yield return Run("I14 debris contract, parent vessel not loaded: Absolute fallback", sb =>
            {
                Fixture f = Build(debris: true);
                f.Bg.QueueDebrisSeedParentAnchorPoint(Pid, ParentRecId, Seed(299.4));
                Queue(f, seed: Seed(299.5));
                Init(sb, "a", f, Dead(), 300.0);
            });
            yield return Run("I15 debris recording without a seed", sb =>
            {
                Fixture f = Build(debris: true);
                Init(sb, "a", f, Dead(), 300.0);
            });
            yield return Run("I16 debris contract, recording already has part events", sb =>
            {
                Fixture f = Build(debris: true, withPartEvents: true);
                Queue(f, seed: Seed(299.5));
                Init(sb, "a", f, Dead(), 300.0);
            });
            yield return Run("I17 debris contract, recording missing from the tree", sb =>
            {
                Fixture f = Build(debris: true);
                Queue(f, seed: Seed(299.5));
                Init(sb, "a", f, Dead(), 300.0, recordingId: "bg-init-missing");
            });
            yield return Run("I18 second init replaces the loaded state", sb =>
            {
                Fixture f = Build();
                Queue(f, seed: Seed(299.5));
                Init(sb, "a", f, Dead(), 300.0);
                Init(sb, "b", f, Dead(altitude: 90000.0), 310.0);
            });
        }

        [UnityEntryFact]
        public void InitializeLoadedState_Golden()
        {
            var sb = new StringBuilder();
            foreach (string c in Cases())
                sb.Append(c);
            AssertGolden("initialize-loaded-state.golden.txt", sb.ToString());
        }
    }
}
