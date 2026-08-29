using System;
using System.Collections.Generic;
using System.IO;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Proves the two committed harness fixtures of the pre-Parsek backup lane
    /// (`PPB-1` / `PPB-2`) land where their specs assume, MEASURED AGAINST THE REAL
    /// PRODUCTION PREDICATE rather than against a description of it.
    ///
    /// <para><b>Why this cannot be left to the harness.</b> `harness/lib` is Python and
    /// can only reason about the save TEXT: "no `name = ParsekScenario` line", "a VESSEL
    /// node exists". The gate the fixtures have to satisfy is C# -
    /// <see cref="PreParsekBackup.HasParsekGameplayFootprint"/> over a parsed
    /// <c>ConfigNode</c>, <see cref="PreParsekBackup.IsBrandNewEmptySave"/> over a
    /// <see cref="CareerSaveParser"/> snapshot, and
    /// <see cref="PreParsekBackup.ShouldBackup"/> over both. Only THIS suite can run
    /// them, so only this suite can say a fixture reads "eligible" rather than merely
    /// "looks stripped". The PPB-1 spec's whole existence rests on that word.</para>
    ///
    /// <para><b>The negative control is the point.</b> `career-earned-pad` - the BASE
    /// `preparsek-untouched-career` is derived from - is asserted here to read
    /// <c>already-parsek-footprint</c>. Without it, a strip that silently removed
    /// nothing would still pass every "no footprint" assertion, because the probe would
    /// be answering False for a save it was never given anything to find.</para>
    ///
    /// <para>Reads `harness/fixtures/saves/`, the same cross-project read
    /// <c>CareerSaveParserFixtureTests</c> already does.</para>
    /// </summary>
    [Collection("Sequential")]
    public class PreParsekBackupFixtureShapeTests : IDisposable
    {
        private const string UntouchedFixture = "preparsek-untouched-career";
        private const string BrandNewFixture = "preparsek-brandnew-career";
        private const string FootprintCarryingBase = "career-earned-pad";

        private readonly List<string> logLines = new List<string>();

        public PreParsekBackupFixtureShapeTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        private static string FixtureDir(string name)
        {
            return Path.Combine(SyntheticRecordingTests.ResolveProjectRoot(),
                                "harness", "fixtures", "saves", name);
        }

        /// <summary>
        /// The decision the cold-OnLoad hook would take on a fixture, computed through the
        /// EXACT call chain <see cref="PreParsekBackup.MaybeBackupOnFirstColdContact"/>
        /// uses: parse the on-disk save, probe the footprint (node + `Parsek/` subdir),
        /// probe brand-new, probe backup-folder, then ask ShouldBackup.
        /// </summary>
        private static bool DecideFor(string fixtureName, out string reason,
                                      out bool footprint, out bool brandNew)
        {
            string dir = FixtureDir(fixtureName);
            string persistent = Path.Combine(dir, "persistent.sfs");
            Assert.True(File.Exists(persistent), $"fixture save not found at '{persistent}'");

            ConfigNode root = ConfigNode.Load(persistent);
            Assert.NotNull(root);

            bool parsekSubdir = Directory.Exists(Path.Combine(dir, "Parsek"));
            footprint = PreParsekBackup.HasParsekGameplayFootprint(root, parsekSubdir);
            brandNew = PreParsekBackup.IsBrandNewEmptySave(CareerSaveParser.Parse(root));
            bool isBackupFolder = PreParsekBackup.IsParsekBackupFolder(dir, fixtureName);

            return PreParsekBackup.ShouldBackup(
                isColdLoad: true, markerExists: false, footprintPresent: footprint,
                isBackupFolder: isBackupFolder, isBrandNewEmpty: brandNew, out reason);
        }

        [Fact]
        public void UntouchedCareerFixture_ReadsEligible()
        {
            bool shouldBackUp = DecideFor(
                UntouchedFixture, out string reason, out bool footprint, out bool brandNew);

            Assert.False(footprint,
                $"{UntouchedFixture} carries a Parsek footprint; the backup would never fire");
            Assert.False(brandNew,
                $"{UntouchedFixture} reads brand-new-empty; the backup would be skipped");
            Assert.True(shouldBackUp);
            // The literal is what PPB-1's log contracts are written against.
            Assert.Equal("eligible", reason);
        }

        [Fact]
        public void BrandNewCareerFixture_ReadsBrandNewEmpty()
        {
            bool shouldBackUp = DecideFor(
                BrandNewFixture, out string reason, out bool footprint, out bool brandNew);

            Assert.False(footprint,
                $"{BrandNewFixture} must be footprint-FREE: if the skip came from a "
                + "footprint instead of emptiness, PPB-2 would prove the wrong thing");
            Assert.True(brandNew);
            Assert.False(shouldBackUp);
            Assert.Equal("brand-new-empty", reason);
        }

        [Fact]
        public void TheDerivationBase_StillReadsAlreadyFootprinted()
        {
            // THE NEGATIVE CONTROL. `preparsek-untouched-career` is this save with Parsek's
            // own state deleted; if the base ever stopped carrying that state, the strip
            // would be a no-op and the "footprint-free" assertions above would be vacuous.
            bool shouldBackUp = DecideFor(
                FootprintCarryingBase, out string reason, out bool footprint, out _);

            Assert.True(footprint,
                $"{FootprintCarryingBase} no longer carries a Parsek footprint, so the "
                + $"derivation of {UntouchedFixture} removes nothing and its "
                + "footprint-free assertions prove nothing");
            Assert.False(shouldBackUp);
            Assert.Equal("already-parsek-footprint", reason);
        }

        [Fact]
        public void UntouchedCareerFixture_KeepsTheCareerItsSpecDependsOn()
        {
            // PPB-1 pins `scene=FLIGHT`, which follows from the fixture having a focusable
            // vessel, and its `brandNew=False` claim follows from the career being non-empty.
            // Both are properties of the PARSED save, so they are asserted through the parser
            // rather than by counting lines.
            string persistent = Path.Combine(FixtureDir(UntouchedFixture), "persistent.sfs");
            CareerSaveSnapshot snap = CareerSaveParser.Parse(ConfigNode.Load(persistent));

            Assert.True(snap.Parsed, snap.Reason);
            Assert.NotEmpty(snap.Vessels);
            Assert.NotEmpty(snap.ActiveContractGuids.Count > 0
                ? (IEnumerable<string>)snap.ActiveContractGuids
                : snap.ContractGuidsAllStates);
            Assert.NotEmpty(snap.SubjectScience);
        }

        [Fact]
        public void BothFixtures_CarryTheLoadMenuFilesTheBackupCopies()
        {
            // `PerformBackup` copies persistent.sfs + persistent.loadmeta (+ craft dirs when
            // present). A fixture missing the loadmeta would make PPB-1's Load-menu shape
            // assertion pass on a backup that KSP could not render a card for.
            foreach (string name in new[] { UntouchedFixture, BrandNewFixture })
            {
                string dir = FixtureDir(name);
                Assert.True(File.Exists(Path.Combine(dir, "persistent.sfs")), name);
                Assert.True(File.Exists(Path.Combine(dir, "persistent.loadmeta")), name);
                Assert.False(Directory.Exists(Path.Combine(dir, "Parsek")), name);
            }
        }
    }
}
