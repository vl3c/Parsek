using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests.Analyzer
{
    // Guards the M-A1 core model types (design doc "Data Model").
    public class AnalyzerModelTests
    {
        // Guards: the report sort is (level DESC, ...). If the enum numbering
        // drifts, StaleFixture/Fail/Warn/Info would sort in the wrong order and
        // every downstream report diff would churn. Pin the numeric contract.
        [Fact]
        public void VerdictLevel_NumericOrder_IsInfoWarnFailStaleAscending()
        {
            Assert.Equal(0, (int)VerdictLevel.Info);
            Assert.Equal(1, (int)VerdictLevel.Warn);
            Assert.Equal(2, (int)VerdictLevel.Fail);
            Assert.Equal(3, (int)VerdictLevel.StaleFixture);
        }

        // Guards: rules iterate model collections without null-guarding. A default
        // AnalyzerModel must expose empty (non-null) collections so a rule over a
        // no-footprint save cannot NRE.
        [Fact]
        public void AnalyzerModel_Defaults_AreEmptyNotNull()
        {
            var model = new AnalyzerModel();

            Assert.NotNull(model.Recordings);
            Assert.Empty(model.Recordings);
            Assert.NotNull(model.Trees);
            Assert.Empty(model.Trees);
            Assert.NotNull(model.Tombstones);
            Assert.Empty(model.Tombstones);
            Assert.NotNull(model.SupersedeRelations);
            Assert.Empty(model.SupersedeRelations);
            Assert.NotNull(model.Ledger);
            Assert.Empty(model.Ledger);
            Assert.NotNull(model.LoadFaults);
            Assert.Empty(model.LoadFaults);
            Assert.NotNull(model.SidecarSchema);
            Assert.Empty(model.SidecarSchema);
            Assert.Null(model.CareerSave);
            Assert.Null(model.FixtureStamp);
        }

        // Guards: the Finding constructor field wiring (positional args) matches
        // the field it names, so a downstream reporter never mislabels a finding.
        [Fact]
        public void Finding_Constructor_AssignsAllFields()
        {
            var f = new Finding(
                "INV1-UT-MONOTONIC",
                VerdictLevel.Fail,
                "rec-1",
                3,
                "back-stepping UT",
                "TrajectoryPoint.ut");

            Assert.Equal("INV1-UT-MONOTONIC", f.RuleId);
            Assert.Equal(VerdictLevel.Fail, f.Level);
            Assert.Equal("rec-1", f.Target);
            Assert.Equal(3, f.SectionIndex);
            Assert.Equal("back-stepping UT", f.Message);
            Assert.Equal("TrajectoryPoint.ut", f.CitedContract);
        }

        // Guards: LoadFault / FixtureStamp positional field wiring.
        [Fact]
        public void LoadFault_And_FixtureStamp_Constructors_AssignAllFields()
        {
            var lf = new LoadFault("a/b.prec", "trajectory", "text-sidecar-unsupported", "rec-9");
            Assert.Equal("a/b.prec", lf.FilePath);
            Assert.Equal("trajectory", lf.FileKind);
            Assert.Equal("text-sidecar-unsupported", lf.Reason);
            Assert.Equal("rec-9", lf.RecordingId);

            var stamp = new FixtureStamp(4, "synthetic");
            Assert.Equal(4, stamp.SchemaGeneration);
            Assert.Equal("synthetic", stamp.Provenance);
        }

        // Guards: SidecarSchema round-trips the (generation, formatVersion) tuple by
        // named element, so INV5's generation-mismatch read stays correct.
        [Fact]
        public void SidecarSchema_TupleElements_AreNamed()
        {
            var model = new AnalyzerModel
            {
                SidecarSchema = new Dictionary<string, (int Generation, int FormatVersion)>
                {
                    ["rec-1"] = (4, 1),
                },
            };

            Assert.Equal(4, model.SidecarSchema["rec-1"].Generation);
            Assert.Equal(1, model.SidecarSchema["rec-1"].FormatVersion);
        }
    }
}
