using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Finding L8: an F9 (or a resumed save) rebuilds the recorders from the quicksave's tree,
    /// and the finalization caches are in-memory only, so every background member starts with
    /// none. Two halves: a member whose vessel the loaded game lacks (stock dropped it at the
    /// save) follows its own recorded path to its natural ending instead of a SubOrbital stamp
    /// in mid-air, and every live member gets its cache rebuilt from its vessel at the restore.
    /// Kerbin-like fixture: R = 600 km, atmosphere 70 km, deletion altitude 22 km.
    /// </summary>
    [Collection("Sequential")]
    public class QuickloadFinalizationCacheTests : IDisposable
    {
        private const double KerbinRadius = 600000.0;
        private const double KerbinAtmosphereDepth = 70000.0;
        private const double KerbinGravParameter = 3.5316e12;

        private readonly List<string> logLines = new List<string>();

        public QuickloadFinalizationCacheTests()
        {
            RecordingStore.ResetForTesting();
            RecordingFinalizationCacheProducer.ResetForTesting();
            IncompleteBallisticSceneExitFinalizer.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekFlight.TerminalInferenceBodyRadiusResolverForTesting =
                name => name == "Kerbin" ? KerbinRadius : (double?)null;
        }

        public void Dispose()
        {
            ParsekFlight.TerminalInferenceBodyRadiusResolverForTesting = null;
            RecordingFinalizationCacheProducer.ResetForTesting();
            IncompleteBallisticSceneExitFinalizer.ResetForTesting();
            RecordingStore.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        // ---- missing member: follows its recorded path ----

        [Fact]
        public void MissingMember_FallingLastOrbit_FollowsItToAPredictedImpact()
        {
            Recording rec = FallingOrbitDebris("rec_fall", 100);
            RecordingTree tree = TreeWith(rec);
            var bg = new BackgroundRecorder(tree);

            int count = bg.CloseMissingBackgroundMembersAtLoad(
                pid => false, "RestoreActiveTreeFromPending", KerbinBodies, null);

            Assert.Equal(1, count);
            Assert.Equal(TerminalState.Destroyed, rec.TerminalStateValue);
            Assert.True(rec.TerminalPredicted);
            OrbitSegment[] tail = rec.OrbitSegments.Where(s => s.isPredicted).ToArray();
            Assert.NotEmpty(tail);
            Assert.True(tail[0].startUT >= 1100.0 - 1e-6);
            Assert.True(rec.ExplicitEndUT > 1100.0);
            Assert.Equal(rec.ExplicitEndUT, tail[tail.Length - 1].endUT, 6);
            Assert.False(tree.BackgroundMap.ContainsKey(100));

            string summary = logLines.Single(l => l.Contains("Load check:") && l.Contains("background member(s) have no vessel"));
            Assert.Contains("followedRecordedTail=1", summary);
            Assert.Contains("closed=0", summary);
            Assert.Contains("100:tail-Destroyed@", summary);
            Assert.Contains("(recorded-tail:last-orbit-segment)", summary);
            Assert.Contains(logLines, l => l.Contains("[BgRecorder]")
                && l.Contains("follows its recorded path from last-orbit-segment")
                && l.Contains("terminal=Destroyed"));
        }

        [Fact]
        public void MissingMember_FallingLastPoint_ReseedsAndFollowsItToAPredictedImpact()
        {
            Recording rec = FallingPointDebris("rec_pt", 200);
            RecordingTree tree = TreeWith(rec);
            var bg = new BackgroundRecorder(tree);
            int reseeds = 0;
            Func<Recording, TrajectoryPoint, OrbitSegment?> reseed = (r, p) =>
            {
                reseeds++;
                Assert.Equal(1000.0, p.ut);
                return FallingSegment(startUT: p.ut, endUT: p.ut, epoch: p.ut);
            };

            bg.CloseMissingBackgroundMembersAtLoad(pid => false, "unit", KerbinBodies, reseed);

            Assert.Equal(1, reseeds);
            Assert.Equal(TerminalState.Destroyed, rec.TerminalStateValue);
            Assert.True(rec.TerminalPredicted);
            Assert.Contains(rec.OrbitSegments, s => s.isPredicted);
            Assert.True(rec.ExplicitEndUT > 1000.0);
            Assert.Contains(logLines, l => l.Contains("200:tail-Destroyed@")
                && l.Contains("(recorded-tail:last-point)"));
        }

        [Fact]
        public void MissingMember_NoBodyFacts_KeepsTheSubOrbitalStampAsBefore()
        {
            // The pre-fix outcome, and still the outcome wherever the extrapolator has no
            // body facts (headless, or FlightGlobals unavailable): SubOrbital at the last
            // known UT with no tail.
            Recording rec = FallingOrbitDebris("rec_fall", 100);
            RecordingTree tree = TreeWith(rec);
            var bg = new BackgroundRecorder(tree);

            bg.CloseMissingBackgroundMembersAtLoad(pid => false, "unit", () => null, null);

            Assert.Equal(TerminalState.SubOrbital, rec.TerminalStateValue);
            Assert.DoesNotContain(rec.OrbitSegments, s => s.isPredicted);
            Assert.Equal(1200.0, rec.ExplicitEndUT);
            Assert.Contains(logLines, l => l.Contains("100:SubOrbital@1200.0(last-orbit-segment-unstable)"));
            Assert.Contains(logLines, l => l.Contains("recorded tail declined: body-facts-unavailable"));
        }

        [Fact]
        public void MissingMember_LiveEntryPointHeadless_KeepsTheSubOrbitalStamp()
        {
            Recording rec = FallingOrbitDebris("rec_fall", 100);
            RecordingTree tree = TreeWith(rec);
            var bg = new BackgroundRecorder(tree);

            bg.CloseMissingBackgroundMembersAtLoad(pid => false, "unit");

            Assert.Equal(TerminalState.SubOrbital, rec.TerminalStateValue);
            Assert.DoesNotContain(rec.OrbitSegments, s => s.isPredicted);
        }

        [Fact]
        public void MissingMember_ReseededStableOrbit_IsNotAdoptedAndStaysSubOrbital()
        {
            // An Orbiting ending would make a vessel stock removed spawnable.
            Recording rec = FallingPointDebris("rec_pt", 200);
            RecordingTree tree = TreeWith(rec);
            var bg = new BackgroundRecorder(tree);
            Func<Recording, TrajectoryPoint, OrbitSegment?> reseed =
                (r, p) => Segment(KerbinRadius + 100000.0, 0.0, 0.0, p.ut, p.ut, p.ut);

            bg.CloseMissingBackgroundMembersAtLoad(pid => false, "unit", KerbinBodies, reseed);

            Assert.Equal(TerminalState.SubOrbital, rec.TerminalStateValue);
            Assert.DoesNotContain(rec.OrbitSegments, s => s.isPredicted);
            Assert.Contains(logLines, l => l.Contains("ending-not-adopted:Orbiting"));
        }

        [Fact]
        public void MissingMember_OpenParachuteAtLastPoint_DeclinesTheBallisticTail()
        {
            Recording rec = FallingPointDebris("rec_pt", 200);
            rec.PartEvents.Add(new PartEvent
            {
                ut = 900.0,
                partPersistentId = 4242,
                eventType = PartEventType.ParachuteDeployed
            });
            RecordingTree tree = TreeWith(rec);
            var bg = new BackgroundRecorder(tree);
            int reseeds = 0;
            Func<Recording, TrajectoryPoint, OrbitSegment?> reseed = (r, p) =>
            {
                reseeds++;
                return FallingSegment(p.ut, p.ut, p.ut);
            };

            bg.CloseMissingBackgroundMembersAtLoad(pid => false, "unit", KerbinBodies, reseed);

            Assert.Equal(0, reseeds);
            Assert.Equal(TerminalState.SubOrbital, rec.TerminalStateValue);
            Assert.Contains(logLines, l => l.Contains("open-parachute pid=4242"));
        }

        [Fact]
        public void MissingMember_StableOrbitAndSurface_KeepTheirStampsWithoutResolvingBodies()
        {
            var stable = new Recording
            {
                RecordingId = "rec_o",
                VesselPersistentId = 300,
                IsDebris = true,
                ExplicitEndUT = 1300.0
            };
            stable.OrbitSegments.Add(Segment(700000.0, 0.01, 0.0, 1000.0, 1290.0, 1000.0, predicted: false));
            RecordingTree tree = TreeWith(stable);
            var bg = new BackgroundRecorder(tree);
            int resolves = 0;

            bg.CloseMissingBackgroundMembersAtLoad(pid => false, "unit",
                () => { resolves++; return KerbinBodies(); }, null);

            Assert.Equal(0, resolves);
            Assert.Equal(TerminalState.Orbiting, stable.TerminalStateValue);
            Assert.Equal(1300.0, stable.ExplicitEndUT);
        }

        [Theory]
        [InlineData("last-orbit-segment-unstable", true)]
        [InlineData("last-point", true)]
        [InlineData("last-orbit-segment", false)]
        [InlineData("last-point+surface-position", false)]
        [InlineData("surface-position", false)]
        [InlineData("no-trajectory", false)]
        [InlineData(null, false)]
        public void IsRecordedTailEvidence_OnlyTheTwoSubOrbitalReadings(string evidence, bool expected)
        {
            Assert.Equal(expected, BackgroundRecorder.IsRecordedTailEvidence(evidence));
        }

        // ---- live members: caches rebuilt at the restore ----

        [Fact]
        public void RestoredTree_BackgroundMembersStartWithNoCache()
        {
            // The pre-fix state after an F9: the recorder is built from the quicksave's tree,
            // and nothing refreshes an on-rails member until it opens an orbit segment.
            RecordingTree tree = TreeWith(FallingOrbitDebris("rec_a", 100), FallingOrbitDebris("rec_b", 101));
            var bg = new BackgroundRecorder(tree);
            bg.UpdateOnRails(1500.0);

            Assert.False(bg.HasFinalizationCache(100));
            Assert.False(bg.HasFinalizationCache(101));
            Assert.False(bg.GetOnRailsHasOpenSegment(100));
        }

        [Fact]
        public void SelectRestoreCacheRebuildTargets_LiveUnendedMembersOnly()
        {
            Recording live = FallingOrbitDebris("rec_live", 100);
            Recording gone = FallingOrbitDebris("rec_gone", 101);
            Recording ended = FallingOrbitDebris("rec_ended", 102);
            ended.TerminalStateValue = TerminalState.Recovered;
            Recording destroyed = FallingOrbitDebris("rec_destroyed", 103);
            destroyed.VesselDestroyed = true;
            RecordingTree tree = TreeWith(live, gone, ended, destroyed);
            tree.BackgroundMap[104] = "rec_missing_from_tree";
            var alive = new HashSet<uint> { 100, 102, 103, 104 };

            var targets = BackgroundRecorder.SelectRestoreCacheRebuildTargets(tree, alive.Contains);

            Assert.Single(targets);
            Assert.Equal(100u, targets[0].VesselPid);
            Assert.Equal("rec_live", targets[0].RecordingId);
        }

        [Theory]
        [InlineData(true, false, true)]
        [InlineData(true, true, false)]
        [InlineData(false, true, false)]
        [InlineData(false, false, false)]
        public void ResolveRestoreCacheOwner_UnpackedLoadedIsLoaded(bool loaded, bool packed,
            bool expectLoadedOwner)
        {
            Assert.Equal(
                expectLoadedOwner ? FinalizationCacheOwner.BackgroundLoaded : FinalizationCacheOwner.BackgroundOnRails,
                BackgroundRecorder.ResolveRestoreCacheOwner(loaded, packed));
        }

        [Fact]
        public void RebuildFinalizationCachesAfterRestore_RefreshesEachLiveMemberOnceAndLogsOneSummary()
        {
            RecordingTree tree = TreeWith(
                FallingOrbitDebris("rec_rails", 100),
                FallingOrbitDebris("rec_loaded", 101),
                FallingOrbitDebris("rec_gone", 102));
            var bg = new BackgroundRecorder(tree);
            Vessel onRails = StubVessel(100, loaded: false, packed: true);
            Vessel loaded = StubVessel(101, loaded: true, packed: false);
            var vessels = new Dictionary<uint, Vessel> { [100] = onRails, [101] = loaded };
            var calls = new List<(Vessel v, string rec, FinalizationCacheOwner owner)>();

            int refreshed = bg.RebuildFinalizationCachesAfterRestore(
                pid => vessels.TryGetValue(pid, out Vessel v) ? v : null,
                "RestoreActiveTreeFromPending",
                (v, recId, owner) => calls.Add((v, recId, owner)));

            Assert.Equal(2, refreshed);
            Assert.Equal(2, calls.Count);
            Assert.Same(onRails, calls[0].v);
            Assert.Equal("rec_rails", calls[0].rec);
            Assert.Equal(FinalizationCacheOwner.BackgroundOnRails, calls[0].owner);
            Assert.Same(loaded, calls[1].v);
            Assert.Equal("rec_loaded", calls[1].rec);
            Assert.Equal(FinalizationCacheOwner.BackgroundLoaded, calls[1].owner);
            string summary = Assert.Single(logLines, l => l.Contains("Restore cache rebuild:"));
            Assert.Contains("[Parsek][INFO][BgRecorder]", summary);
            Assert.Contains("reason=RestoreActiveTreeFromPending", summary);
            Assert.Contains("liveMembers=2 refreshed=2 loaded=1 onRails=1", summary);
        }

        [Fact]
        public void EnsureBackgroundRecorderAttached_RebuildsCachesAfterTheMissingMemberCheck()
        {
            string src = ParsekFlightSourceFiles.ReadAll(System.IO.Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory,
                "..", "..", "..", "..", "..", "Source", "Parsek"));
            int attach = src.IndexOf("internal void EnsureBackgroundRecorderAttached(", StringComparison.Ordinal);
            Assert.True(attach >= 0);
            int end = src.IndexOf("void OnVesselSituationChange(", attach, StringComparison.Ordinal);
            // Line comments stripped: a commented-out call must not read as the call.
            string body = System.Text.RegularExpressions.Regex.Replace(
                src.Substring(attach, end - attach), @"//[^\r\n]*", "");
            int close = body.IndexOf("CloseMissingBackgroundMembersAtLoad(", StringComparison.Ordinal);
            int rebuild = body.IndexOf("RebuildFinalizationCachesAfterRestore(", StringComparison.Ordinal);
            Assert.True(close >= 0, "the missing-member check must stay in the attach");
            Assert.True(rebuild > close, "the cache rebuild must follow the missing-member check");
        }

        // ---- fixtures ----

        private static IReadOnlyDictionary<string, ExtrapolationBody> KerbinBodies()
        {
            return new Dictionary<string, ExtrapolationBody>
            {
                ["Kerbin"] = new ExtrapolationBody
                {
                    Name = "Kerbin",
                    GravitationalParameter = KerbinGravParameter,
                    Radius = KerbinRadius,
                    AtmosphereDepth = KerbinAtmosphereDepth,
                    DeletionAltitude = OrbitClearanceTestValues.KerbinDeletionAltitude,
                    TerrainAltitude = (double latitude, double longitude, out double altitude) =>
                    {
                        altitude = 0.0;
                        return true;
                    },
                }
            };
        }

        // Apoapsis 90 km, periapsis 200 km under the surface: a fall to the ground.
        private static OrbitSegment FallingSegment(double startUT, double endUT, double epoch)
        {
            const double pe = -200000.0;
            const double ap = 90000.0;
            double sma = KerbinRadius + 0.5 * (pe + ap);
            double ecc = (ap - pe) / (2.0 * KerbinRadius + pe + ap);
            return Segment(sma, ecc, Math.PI, startUT, endUT, epoch, predicted: false);
        }

        private static OrbitSegment Segment(double sma, double ecc, double meanAnomalyAtEpoch,
            double startUT, double endUT, double epoch, bool predicted = false)
        {
            return new OrbitSegment
            {
                bodyName = "Kerbin",
                startUT = startUT,
                endUT = endUT,
                semiMajorAxis = sma,
                eccentricity = ecc,
                meanAnomalyAtEpoch = meanAnomalyAtEpoch,
                epoch = epoch,
                isPredicted = predicted
            };
        }

        private static Recording FallingOrbitDebris(string recId, uint pid)
        {
            var rec = new Recording
            {
                RecordingId = recId,
                VesselName = "Falling Debris",
                VesselPersistentId = pid,
                IsDebris = true,
                ExplicitEndUT = 1200.0
            };
            rec.OrbitSegments.Add(FallingSegment(1000.0, 1100.0, 1000.0));
            return rec;
        }

        private static Recording FallingPointDebris(string recId, uint pid)
        {
            var rec = new Recording
            {
                RecordingId = recId,
                VesselName = "Falling Debris",
                VesselPersistentId = pid,
                IsDebris = true,
                ExplicitEndUT = 1000.0
            };
            rec.Points.Add(new TrajectoryPoint
            {
                ut = 990.0, latitude = 0.0, longitude = 0.0, altitude = 31000.0,
                bodyName = "Kerbin", velocity = new UnityEngine.Vector3(200f, -100f, 0f)
            });
            rec.Points.Add(new TrajectoryPoint
            {
                ut = 1000.0, latitude = 0.0, longitude = 0.01, altitude = 30000.0,
                bodyName = "Kerbin", velocity = new UnityEngine.Vector3(200f, -100f, 0f)
            });
            return rec;
        }

        private static RecordingTree TreeWith(params Recording[] recordings)
        {
            var tree = new RecordingTree { Id = "tree-l8", TreeName = "L8" };
            foreach (Recording rec in recordings)
            {
                tree.Recordings[rec.RecordingId] = rec;
                tree.BackgroundMap[rec.VesselPersistentId] = rec.RecordingId;
            }
            return tree;
        }

        private static Vessel StubVessel(uint pid, bool loaded, bool packed)
        {
            var vessel = (Vessel)FormatterServices.GetUninitializedObject(typeof(Vessel));
            vessel.persistentId = pid;
            vessel.loaded = loaded;
            vessel.packed = packed;
            return vessel;
        }
    }
}
