using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Tests for <see cref="KerbalsModule.ReverseMapCrewNamesInSnapshot"/>, the
    /// snapshot-capture-time helper that rewrites stand-in crew names back to
    /// the original kerbal names. Called from
    /// <see cref="VesselSpawner.TryBackupSnapshot"/> so every captured snapshot
    /// persists originals — not whichever stand-in happens to be seated on the
    /// live vessel at capture time.
    /// </summary>
    [Collection("Sequential")] // touches CrewReservationManager static replacements
    public class ReverseMapCrewNamesInSnapshotTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public ReverseMapCrewNamesInSnapshotTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            CrewReservationManager.ResetReplacementsForTesting();
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            CrewReservationManager.ResetReplacementsForTesting();
        }

        private static ConfigNode BuildSnapshot(params (uint pid, string[] crew)[] parts)
        {
            var snap = new ConfigNode("VESSEL");
            for (int p = 0; p < parts.Length; p++)
            {
                var part = snap.AddNode("PART");
                part.AddValue("persistentId", parts[p].pid.ToString());
                if (parts[p].crew != null)
                {
                    for (int c = 0; c < parts[p].crew.Length; c++)
                        part.AddValue("crew", parts[p].crew[c]);
                }
            }
            return snap;
        }

        private static List<string> CrewOf(ConfigNode snapshot, int partIndex)
        {
            var parts = snapshot.GetNodes("PART");
            var values = parts[partIndex].GetValues("crew");
            return new List<string>(values);
        }

        [Fact]
        public void NullSnapshot_ReturnsZero_NoCrash()
        {
            int rewritten = KerbalsModule.ReverseMapCrewNamesInSnapshot(
                null, new Dictionary<string, string>(), "test");
            Assert.Equal(0, rewritten);
        }

        [Fact]
        public void SnapshotWithoutCrewParts_ReturnsZero()
        {
            var snap = new ConfigNode("VESSEL");
            // No PART nodes at all
            int rewritten = KerbalsModule.ReverseMapCrewNamesInSnapshot(
                snap, new Dictionary<string, string>(), "test");
            Assert.Equal(0, rewritten);
        }

        [Fact]
        public void SnapshotWithOnlyOriginalNames_ReturnsZero_NamesUnchanged()
        {
            CrewReservationManager.SetReplacement("Jebediah Kerman", "Leia Kerman");
            var snap = BuildSnapshot(
                (100000u, new[] { "Jebediah Kerman", "Bill Kerman", "Bob Kerman" }));

            int rewritten = KerbalsModule.ReverseMapCrewNamesInSnapshot(
                snap, CrewReservationManager.CrewReplacements, "test-originals");

            Assert.Equal(0, rewritten);
            Assert.Equal(new List<string> { "Jebediah Kerman", "Bill Kerman", "Bob Kerman" },
                CrewOf(snap, 0));
        }

        [Fact]
        public void SnapshotWithStandInName_RewritesToOriginal_ViaReplacementsDict()
        {
            CrewReservationManager.SetReplacement("Jebediah Kerman", "Leia Kerman");
            var snap = BuildSnapshot(
                (100000u, new[] { "Leia Kerman", "Bill Kerman", "Bob Kerman" }));

            int rewritten = KerbalsModule.ReverseMapCrewNamesInSnapshot(
                snap, CrewReservationManager.CrewReplacements, "test-replacements");

            Assert.Equal(1, rewritten);
            Assert.Equal(new List<string> { "Jebediah Kerman", "Bill Kerman", "Bob Kerman" },
                CrewOf(snap, 0));
            Assert.Contains(logLines, l => l.Contains("[KerbalsModule]")
                && l.Contains("ReverseMapCrewNamesInSnapshot")
                && l.Contains("rewrote 1")
                && l.Contains("test-replacements"));
        }

        [Fact]
        public void SnapshotWithMultipleStandIns_RewritesAll()
        {
            CrewReservationManager.SetReplacement("Jebediah Kerman", "Leia Kerman");
            CrewReservationManager.SetReplacement("Bill Kerman", "Han Kerman");
            CrewReservationManager.SetReplacement("Bob Kerman", "Luke Kerman");
            var snap = BuildSnapshot(
                (100000u, new[] { "Leia Kerman", "Han Kerman", "Luke Kerman" }));

            int rewritten = KerbalsModule.ReverseMapCrewNamesInSnapshot(
                snap, CrewReservationManager.CrewReplacements, "test-three");

            Assert.Equal(3, rewritten);
            Assert.Equal(new List<string> { "Jebediah Kerman", "Bill Kerman", "Bob Kerman" },
                CrewOf(snap, 0));
        }

        [Fact]
        public void MultiplePartsWithMixedCrew_RewritesOnlyStandIns()
        {
            CrewReservationManager.SetReplacement("Jebediah Kerman", "Leia Kerman");
            CrewReservationManager.SetReplacement("Bill Kerman", "Han Kerman");
            var snap = BuildSnapshot(
                (100000u, new[] { "Leia Kerman", "Valentina Kerman" }),  // 1 stand-in, 1 original
                (100001u, new[] { "Han Kerman" }),                        // 1 stand-in
                (100002u, new string[0]));                                // empty seats

            int rewritten = KerbalsModule.ReverseMapCrewNamesInSnapshot(
                snap, CrewReservationManager.CrewReplacements, "test-mixed");

            Assert.Equal(2, rewritten);
            Assert.Equal(new List<string> { "Jebediah Kerman", "Valentina Kerman" }, CrewOf(snap, 0));
            Assert.Equal(new List<string> { "Bill Kerman" }, CrewOf(snap, 1));
            Assert.Empty(CrewOf(snap, 2));
        }

        [Fact]
        public void EmptyReplacementsDict_LeavesNamesUnchanged()
        {
            // No replacements registered, no slot fallback context — every name is treated as original.
            var snap = BuildSnapshot(
                (100000u, new[] { "Leia Kerman", "Han Kerman" }));

            int rewritten = KerbalsModule.ReverseMapCrewNamesInSnapshot(
                snap, new Dictionary<string, string>(), "test-empty-dict");

            Assert.Equal(0, rewritten);
            Assert.Equal(new List<string> { "Leia Kerman", "Han Kerman" }, CrewOf(snap, 0));
        }

        [Fact]
        public void NullReplacementsDict_DoesNotCrash()
        {
            // No slots harness available in xUnit, so a name with no replacement-dict hit
            // and no slot-chain match simply passes through; verifies the null-replacements
            // path does not throw.
            var snap = BuildSnapshot(
                (100000u, new[] { "Leia Kerman" }));

            int rewritten = KerbalsModule.ReverseMapCrewNamesInSnapshot(
                snap, null, "test-null-dict");

            Assert.Equal(0, rewritten);
            Assert.Equal(new List<string> { "Leia Kerman" }, CrewOf(snap, 0));
        }

        [Fact]
        public void EmptyCrewName_IsPreservedNotDropped()
        {
            // Defensive: an empty/null crew slot entry should pass through unchanged.
            var snap = new ConfigNode("VESSEL");
            var part = snap.AddNode("PART");
            part.AddValue("crew", "Jebediah Kerman");
            part.AddValue("crew", ""); // empty seat marker
            part.AddValue("crew", "Bill Kerman");

            int rewritten = KerbalsModule.ReverseMapCrewNamesInSnapshot(
                snap, new Dictionary<string, string>(), "test-empty-entry");

            Assert.Equal(0, rewritten);
            var values = part.GetValues("crew");
            Assert.Equal(3, values.Length);
            Assert.Equal("Jebediah Kerman", values[0]);
            Assert.Equal("", values[1]);
            Assert.Equal("Bill Kerman", values[2]);
        }

        [Fact]
        public void StandInRewrite_PreservesOrderWithinPart()
        {
            CrewReservationManager.SetReplacement("Bill Kerman", "Han Kerman");
            var snap = BuildSnapshot(
                (100000u, new[] { "Jebediah Kerman", "Han Kerman", "Bob Kerman" }));

            int rewritten = KerbalsModule.ReverseMapCrewNamesInSnapshot(
                snap, CrewReservationManager.CrewReplacements, "test-order");

            Assert.Equal(1, rewritten);
            // Order matters for seat indexing on the spawn side.
            Assert.Equal(new List<string> { "Jebediah Kerman", "Bill Kerman", "Bob Kerman" },
                CrewOf(snap, 0));
        }

        [Fact]
        public void ZeroRewrites_DoesNotEmitSummaryLog()
        {
            // Avoid log spam when nothing changed (TryBackupSnapshot fires often).
            var snap = BuildSnapshot(
                (100000u, new[] { "Jebediah Kerman" }));

            int rewritten = KerbalsModule.ReverseMapCrewNamesInSnapshot(
                snap, new Dictionary<string, string>(), "test-no-spam");

            Assert.Equal(0, rewritten);
            Assert.DoesNotContain(logLines, l => l.Contains("ReverseMapCrewNamesInSnapshot"));
        }
    }
}
