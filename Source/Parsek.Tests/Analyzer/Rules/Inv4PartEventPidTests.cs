using System.Collections.Generic;
using System.Linq;
using Parsek;
using Parsek.Tests.Generators;
using Xunit;

namespace Parsek.Tests.Analyzer.Rules
{
    // INV4 part-event PID resolution. Fixtures build a real VESSEL snapshot via
    // VesselSnapshotBuilder (parts get pid = 100000 + idx*1111) and attach
    // PartEvents directly. Pure in-memory model; each test names the regression.
    public class Inv4PartEventPidTests
    {
        private static AnalyzerModel ModelWith(
            IEnumerable<Recording> recs, IEnumerable<LoadFault> loadFaults = null)
        {
            return new AnalyzerModel
            {
                SaveName = "inv4",
                Recordings = recs.ToList(),
                LoadFaults = (loadFaults ?? Enumerable.Empty<LoadFault>()).ToList(),
            };
        }

        private static List<Finding> Run(AnalyzerModel model)
        {
            return new Inv4PartEventPid().Evaluate(model).ToList();
        }

        // The three-part FleaRocket assigns part pids 100000, 101111, 102222.
        private static ConfigNode FleaSnapshot() =>
            VesselSnapshotBuilder.FleaRocket("Flea", "Jeb", pid: 5001).Build();

        // Guards: events whose PIDs are members of the snapshot part set produce
        // zero FAIL. Fails if the membership check rejects valid 100000+idx*1111
        // PIDs (would false-alarm on every synthetic ghost).
        [Fact]
        public void EventsMatchingSnapshotParts_NoFail()
        {
            var rec = new Recording
            {
                RecordingId = "ok0",
                VesselSnapshot = FleaSnapshot(),
                PartEvents = new List<PartEvent>
                {
                    new PartEvent { partPersistentId = 100000, eventType = PartEventType.EngineIgnited },
                    new PartEvent { partPersistentId = 101111, eventType = PartEventType.Decoupled },
                    new PartEvent { partPersistentId = 102222, eventType = PartEventType.ParachuteDeployed },
                },
            };

            Assert.Empty(Run(ModelWith(new[] { rec })));
        }

        // Guards: an event PID absent from the snapshot -> FAIL. Fails if an
        // unresolvable PID passes, silently dropping a part event at playback.
        [Fact]
        public void EventPidAbsentFromSnapshot_Fails()
        {
            var rec = new Recording
            {
                RecordingId = "bad0",
                VesselSnapshot = FleaSnapshot(),
                PartEvents = new List<PartEvent>
                {
                    new PartEvent { partPersistentId = 999999, eventType = PartEventType.Decoupled },
                },
            };

            List<Finding> findings = Run(ModelWith(new[] { rec }));

            Finding fail = Assert.Single(findings);
            Assert.Equal(Inv4PartEventPid.RuleIdConst, fail.RuleId);
            Assert.Equal(VerdictLevel.Fail, fail.Level);
            Assert.Contains("pid=999999", fail.Message);
        }

        // Guards: a resolvable event resolves against the ghost visual snapshot when
        // present (the surface playback looks parts up against). Fails if only the
        // vessel snapshot is consulted and the ghost snapshot's parts are ignored.
        [Fact]
        public void PrefersGhostVisualSnapshot()
        {
            var rec = new Recording
            {
                RecordingId = "ghost0",
                // Vessel snapshot has DIFFERENT parts; the ghost snapshot is the FleaRocket.
                VesselSnapshot = VesselSnapshotBuilder.ProbeShip("Probe", pid: 42).Build(),
                GhostVisualSnapshot = FleaSnapshot(),
                PartEvents = new List<PartEvent>
                {
                    new PartEvent { partPersistentId = 101111, eventType = PartEventType.Decoupled },
                },
            };

            Assert.Empty(Run(ModelWith(new[] { rec })));
        }

        // Guards: a recording with events but no snapshot (destroyed / showcase) ->
        // INFO, never FAIL/WARN. Fails if a missing snapshot NREs the rule or is
        // treated as a hard failure.
        [Fact]
        public void NoSnapshot_InfoOnly_NoCrash()
        {
            var rec = new Recording
            {
                RecordingId = "nosnap0",
                PartEvents = new List<PartEvent>
                {
                    new PartEvent { partPersistentId = 100000, eventType = PartEventType.Decoupled },
                },
            };

            List<Finding> findings = Run(ModelWith(new[] { rec }));

            Finding info = Assert.Single(findings);
            Assert.Equal(VerdictLevel.Info, info.Level);
            Assert.Contains("no-snapshot", info.Message);
            Assert.DoesNotContain(findings, f => f.Level == VerdictLevel.Fail || f.Level == VerdictLevel.Warn);
        }

        // Guards (edge case 23): a snapshot that failed to load -> INFO (unresolvable)
        // + WARN (snapshot load failed), never a FAIL and never a crash. Fails if a
        // load-failed snapshot NREs or is treated identically to a genuinely absent
        // one.
        [Fact]
        public void SnapshotLoadFailed_InfoPlusWarn()
        {
            var rec = new Recording
            {
                RecordingId = "loadfail0",
                PartEvents = new List<PartEvent>
                {
                    new PartEvent { partPersistentId = 100000, eventType = PartEventType.Decoupled },
                },
            };

            List<Finding> findings = Run(ModelWith(
                new[] { rec },
                new[] { new LoadFault("p", "snapshot", "snapshot-load-failed", "loadfail0") }));

            Assert.Contains(findings, f => f.Level == VerdictLevel.Info && f.Message.Contains("loadFailed=True"));
            Assert.Contains(findings, f => f.Level == VerdictLevel.Warn && f.Message.Contains("snapshot-load-failed"));
            Assert.DoesNotContain(findings, f => f.Level == VerdictLevel.Fail);
        }
    }
}
