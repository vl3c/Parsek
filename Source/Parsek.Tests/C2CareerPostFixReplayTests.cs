using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// THE CLOSES-TO-ZERO PROOF. Replay the REAL recalculation engine over a career that
    /// was EARNED BY A DRIVEN FLIGHT on current code, and diff the reconstructed pools
    /// against KSP's own on-disk save. All three pools now close to float noise.
    ///
    /// THE MEASUREMENT, off run 2026-08-19_2130 (recon vs the save KSP itself wrote):
    ///
    ///   pool         reconstructed        save            delta        was
    ///   FUNDS        536558               536558          0            -4558
    ///   SCIENCE      111.60000014305115   111.599998      +2.14e-06    -100
    ///   REPUTATION   1.9999990463256836   1.99999881      +2.36e-07    -0.00148
    ///
    /// Funds closes EXACTLY. The other two are float32 representation gaps against pools
    /// KSP rounded into its save, not residual leaks - the smallest real row in this
    /// ledger is 3 science / 800 funds, six orders of magnitude above either delta.
    ///
    /// THE FIXTURE. <c>Source/Parsek.Tests/Fixtures/C2CareerPostFix/</c> is the save
    /// produced by harness run <c>2026-08-19_2130_L3-career-science-recover</c> - flown
    /// over the harness save fixture `career-science-pad` - hand-copied verbatim. The
    /// mission flew every phase (PRELAUNCH -> ASCENT -> COAST -> DESCENT -> LANDED ->
    /// COLLECT -> TRANSMIT -> RECOVER -> RECOVERED, MISSION-OK, PASS on attempt 1): a
    /// career pad hop that ran three science experiments, TRANSMITTED them, and RECOVERED
    /// the craft. That makes it the only committed fixture in the repo whose career
    /// EARNED rather than SPENT - 14 ledger actions carrying three `ScienceEarning` rows,
    /// one of them the recovery subject (`recovery@KerbinFlew`), the vessel-recovery
    /// `FundsEarning` credit, five stock milestones, and all three pool seeds.
    ///
    /// THIS FIXTURE WAS RE-HARVESTED, REPLACING A WAVE-2 PREDECESSOR, AND THAT IS THE
    /// WHOLE POINT. The 2026-08-19_1912 fixture existed to PIN three divergences as
    /// magnitudes so their fixes would be provable. It pinned:
    ///
    ///   FUNDS       4558 low - the vessel-recovery credit was never written as a
    ///               ledger row       (CAREER-RECOVERY-FUNDS-NOT-LEDGERED)
    ///   SCIENCE     100 low - the career's science SEED was captured as 0 on a save
    ///               whose pool was 100
    ///                                (CAREER-SCIENCE-SEED-LOST-ON-FLIGHT-ROUTE)
    ///   REPUTATION  0.00148 low - two +1 milestone awards landing at 1.9985168
    ///               against KSP's 1.99999881
    ///                                (CAREER-MILESTONE-REP-AWARD-RECONSTRUCTS-LOW)
    ///
    /// Each entry's re-harvest clause named this exact replacement as the flip. The first
    /// two are CAPTURE-side: a fix cannot retro-fill a committed `ledger.pgld`, so they
    /// could only ever close by re-flying the spec and harvesting again over fixed code,
    /// which is what run 2026-08-19_2130 is. The third is RECALC-side and closed without
    /// a re-harvest, on this fixture's predecessor and on `C2Career` alike.
    ///
    /// WHAT THE RE-HARVEST CHANGED, AND WHAT IT DID NOT. KSP's own pools came out
    /// IDENTICAL across the two flights - 536558 / 111.599998 / 1.99999881, the same
    /// three science subjects at the same values, the same craft recovered - so the two
    /// runs differ in the LEDGER and nowhere else. That is as clean a controlled
    /// comparison as this suite could ask for: the reconstruction moved, the thing being
    /// reconstructed did not.
    ///
    /// WHY `C2Career` IS NOT RETIRED BY THIS. It is FROZEN PRE-FIX DATA: a real
    /// hand-played career carrying a STRATEGY currency exchange, whose science leg the
    /// capture layer dropped at the time it was recorded. Its capture-side pins keep
    /// asserting the pre-fix shape and keep passing. The two fixtures answer different
    /// questions:
    ///
    ///   C2Career        a hand-played career with a strategy exchange, PRE-fix capture.
    ///   C2CareerPostFix a driven flight-earned career, POST-fix capture, no strategy.
    ///
    /// THE CONSEQUENCE FOR STRICT ARMING (career-ledger B.4): this fixture is the subject
    /// that deferral was waiting for - a career carrying recorded crewed recoveries and
    /// populated per-identity facets - and all three of the entries that stood between it
    /// and arming are now closed. Arming itself is a separate, deliberate step and is NOT
    /// taken here.
    ///
    /// Non-circular by construction, exactly as the pre-fix sibling: KSP wrote the save
    /// pools, Parsek's observers wrote the ledger, and this test diffs the two
    /// independent producers headlessly. The replay runs the PRODUCTION registration
    /// (`LedgerOrchestrator.Initialize()`), so the module graph under replay is the one
    /// production builds.
    /// </summary>
    [Collection("Sequential")]
    public class C2CareerPostFixReplayTests : IDisposable
    {
        private static readonly CultureInfo IC = CultureInfo.InvariantCulture;

        // Pinned from the fixture's persistent.sfs (KSP-authored ground truth).
        // UNCHANGED ACROSS THE RE-HARVEST - see the class note: the two flights produced
        // identical pools, so these three numbers are the fixed point the reconstruction
        // is measured against.
        private const double SaveFunds = 536558.0;
        private const double SaveScience = 111.599998;
        private const double SaveReputation = 1.99999881;

        // The pools the run STARTED from, inherited unchanged from `career-science-pad`
        // (itself `fresh-career`'s). All three are now carried by seed rows.
        private const double StartFunds = 500000.0;
        private const double StartScience = 100.0;
        private const double StartReputation = 0.0;

        // The vessel-recovery funds credit, as stock paid it and as the ledger now
        // records it. The save's funds moved 532000 -> 536558 on the recovery frame and
        // the row carries the matching dedup key.
        private const double RecoveryFundsCredit = 4558.0;

        // ================================================================
        // THE CLOSURE TOLERANCES, and why each is the number it is.
        //
        // These are TIGHT by intent. The suite's whole value is that it reds on a
        // regression of any of the four fixes, and a loose window would have called the
        // pre-fix state "closed" on two of the three pools.
        // ================================================================

        // FUNDS and SCIENCE close to well under a hundredth. 0.01 is the smallest
        // currency unit that means anything in a career and is far below the magnitude
        // of any single mis-credited row (the smallest funds row here is 800, the
        // smallest science row 3).
        private const double TightTolerance = 0.01;

        // REPUTATION cannot be asked for better than float32. KSP stores the pool as a
        // float and the save prints 1.99999881; the replay accumulates in float and
        // reaches 1.9999990. The gap is 2.4e-07, i.e. about one ulp at this magnitude,
        // so 1e-05 is tight enough to catch any real regression (the defect this
        // replaced was 0.00148, six thousand times wider) while not asserting more
        // precision than a float32 pool can carry.
        private const double ReputationFloatNoiseTolerance = 1e-05;

        public C2CareerPostFixReplayTests()
        {
            LedgerOrchestrator.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            LedgerOrchestrator.Initialize();
        }

        public void Dispose()
        {
            LedgerOrchestrator.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        // ================================================================
        // Fixture resolution
        // ================================================================

        private static string ResolveFixtureDir()
        {
            // Shared root resolver: walks up probing for Source/Parsek.sln rather than
            // hard-coding how deep the xUnit output directory sits.
            string root = SyntheticRecordingTests.ResolveProjectRoot();
            string dir = Path.Combine(root, "Source", "Parsek.Tests", "Fixtures", "C2CareerPostFix");
            Assert.True(Directory.Exists(dir), $"C2CareerPostFix fixture dir not found at '{dir}'");
            return dir;
        }

        private static List<GameAction> LoadFixtureLedger()
        {
            string path = Path.Combine(ResolveFixtureDir(), "Parsek", "GameState", "ledger.pgld");
            Assert.True(File.Exists(path), $"fixture ledger not found at '{path}'");
            Assert.True(Ledger.LoadFromFile(path), "Ledger.LoadFromFile failed on the C2CareerPostFix fixture");
            return new List<GameAction>(Ledger.Actions);
        }

        private static CareerSaveSnapshot ParseFixtureSave()
        {
            string path = Path.Combine(ResolveFixtureDir(), "persistent.sfs");
            ConfigNode root = ConfigNode.Load(path);
            Assert.NotNull(root);
            CareerSaveSnapshot snap = CareerSaveParser.Parse(root);
            Assert.True(snap.Parsed, $"CareerSaveParser rejected the fixture save: {snap.Reason}");
            return snap;
        }

        // ================================================================
        // Fixture integrity
        // ================================================================

        [Fact]
        public void FixtureLedger_LoadsAllFourteenActions()
        {
            // 13 on the wave-2 fixture; the fourteenth is the vessel-recovery
            // `FundsEarning` row that capture used to drop.
            var actions = LoadFixtureLedger();
            Assert.Equal(14, actions.Count);
        }

        [Fact]
        public void FixtureSave_ParsesWithKnownPools()
        {
            var snap = ParseFixtureSave();
            Assert.True(snap.HasFunds);
            Assert.Equal(SaveFunds, snap.Funds, 6);
            Assert.True(snap.HasScience);
            Assert.Equal(SaveScience, snap.SciencePool, 4);
            Assert.True(snap.HasRep);
            Assert.Equal(SaveReputation, snap.Reputation, 4);
        }

        [Fact]
        public void FixtureRecordings_ExistOnDiskForEveryRecordingTheLedgerReferences()
        {
            // A ledger row's `recordingId` pointing at a sidecar that was not copied in
            // is the way a hand-copied save tree rots. The two ids here are the flown
            // main recording and the post-recovery continuation; the trajectory sidecar
            // is the authoritative one, so it is the one asserted. (The `.prec.txt` /
            // `.craft.txt` mirrors are DERIVED debug dumps and are deliberately not
            // carried - 395 KB of text nothing in this suite reads.)
            string recordings = Path.Combine(ResolveFixtureDir(), "Parsek", "Recordings");
            Assert.True(Directory.Exists(recordings), $"no Recordings dir at '{recordings}'");

            var referenced = new HashSet<string>();
            foreach (var action in LoadFixtureLedger())
            {
                if (!string.IsNullOrEmpty(action.RecordingId))
                    referenced.Add(action.RecordingId);
            }
            Assert.NotEmpty(referenced);

            foreach (string id in referenced)
            {
                string prec = Path.Combine(recordings, id + ".prec");
                Assert.True(File.Exists(prec),
                    $"ledger references recording '{id}' but '{prec}' was not committed");
                Assert.True(new FileInfo(prec).Length > 0, $"'{prec}' is empty");
            }
        }

        [Fact]
        public void FixtureRecordings_CarryNoStaleSidecarsFromTheSupersededHarvest()
        {
            // THE RE-HARVEST HYGIENE CELL. Replacing a fixture in place is exactly how a
            // committed save tree accumulates orphans: the new ledger stops referencing
            // the old recording ids, so the plain existence check above passes happily
            // while the previous era's 50 KB of sidecars sit alongside forever. Every
            // sidecar present must belong to a recording this ledger actually names.
            string recordings = Path.Combine(ResolveFixtureDir(), "Parsek", "Recordings");

            var referenced = new HashSet<string>();
            foreach (var action in LoadFixtureLedger())
            {
                if (!string.IsNullOrEmpty(action.RecordingId))
                    referenced.Add(action.RecordingId);
            }

            foreach (string file in Directory.GetFiles(recordings))
            {
                string name = Path.GetFileName(file);
                bool claimed = false;
                foreach (string id in referenced)
                {
                    if (name.StartsWith(id, StringComparison.Ordinal)) { claimed = true; break; }
                }
                Assert.True(claimed,
                    $"'{name}' belongs to no recording this ledger references - a leftover " +
                    "from a superseded harvest. Remove it when re-harvesting the fixture.");
            }
        }

        [Fact]
        public void FixtureSave_CarriesNoVessel_BecauseTheCraftWasRecovered()
        {
            // The whole point of the RECOVER leg, and the property that makes this a
            // vessel-recovery subject rather than another landed-craft save. The base
            // fixture ships exactly one PRELAUNCH VESSEL; this one ships none.
            var snap = ParseFixtureSave();
            Assert.Empty(snap.Vessels);
        }

        // ================================================================
        // The row families a career forge exists to produce
        // ================================================================

        [Fact]
        public void FixtureLedger_CarriesFlightScienceAndARecoverySubject()
        {
            // THE REASON THIS FIXTURE EXISTS. Every career surface the harness could
            // drive before the 2026-08-19 capability wave was a SPEND, so `ScienceEarning`
            // rows - which only a flight can produce - were reachable only from a
            // hand-played save. These are driven ones.
            var actions = LoadFixtureLedger();

            var science = actions.FindAll(a => a.Type == GameActionType.ScienceEarning);
            Assert.Equal(3, science.Count);

            var bySubject = new Dictionary<string, GameAction>();
            foreach (var a in science)
                bySubject[a.SubjectId] = a;

            Assert.True(bySubject.ContainsKey("crewReport@KerbinSrfLandedLaunchPad"));
            Assert.True(bySubject.ContainsKey("mysteryGoo@KerbinSrfLandedLaunchPad"));
            Assert.True(bySubject.ContainsKey("recovery@KerbinFlew"),
                "the recovery subject is the row family the RECOVER leg exists to produce");

            Assert.Equal(5.0, bySubject["recovery@KerbinFlew"].ScienceAwarded, 4);

            // Five stock milestones fired across the hop (RecordsSpeed, RecordsAltitude,
            // FirstLaunch, Kerbin/Science, FirstCrewToSurvive).
            Assert.Equal(5, actions.FindAll(a => a.Type == GameActionType.MilestoneAchievement).Count);
        }

        [Fact]
        public void FixtureLedger_CarriesTheVesselRecoveryFundsCredit()
        {
            // CAREER-RECOVERY-FUNDS-NOT-LEDGERED, CLOSED. This cell is the FLIPPED form
            // of the wave-2 fixture's `FixtureLedger_HasNoFundsEarningRow_...`, whose own
            // comment specified the flip in advance: "on a re-harvest the expectation
            // becomes 'a FundsEarning keyed to the vessel recovery carries the 4558'".
            // It does.
            //
            // The defect was a pre-computation: Parsek asked stock what the recovery
            // would be worth before stock had worked it out, read the resulting zero as
            // "this recovery pays nothing", and took a skip branch - while the
            // FundsChanged event then arrived carrying the full 4558. The EVENT channel
            // was always healthy; the LEDGER ROW is what was missing.
            var actions = LoadFixtureLedger();

            var fundsEarnings = actions.FindAll(a => a.Type == GameActionType.FundsEarning);
            Assert.Single(fundsEarnings);
            Assert.Equal(RecoveryFundsCredit, fundsEarnings[0].FundsAwarded, 4);

            // Keyed to the recovery specifically, not to some other credit that happens
            // to carry the same amount.
            Assert.Contains("VesselRecovery", fundsEarnings[0].DedupKey ?? string.Empty);
        }

        [Fact]
        public void FixtureLedger_SeedsAllThreePoolsAtTheCareersRealStartingValues()
        {
            // CAREER-SCIENCE-SEED-LOST-ON-FLIGHT-ROUTE, CLOSED. This cell is the FLIPPED
            // form of the wave-2 fixture's
            // `FixtureLedger_ScienceSeedIsZeroOnACareerThatStartedAtOneHundred`, which
            // asserted the seed WAS zero and predicted in its own message that "a nonzero
            // value means the fixture was re-harvested on fixed code - at which point the
            // science assertion should close". Both halves happened.
            //
            // The defect was a readiness test: the deferred seed waited for KSP's three
            // career singletons and proceeded as soon as ANY ONE of them had loaded. On
            // the flight route funds always arrive first, so science and reputation were
            // read as null and only funds seeded. By the retry at scene exit the flight
            // had already earned science, and `SeedInitialScience` correctly refused to
            // treat the then-current 106.6 as initial - a correct guard firing on a
            // broken input. The wait now requires all three.
            var actions = LoadFixtureLedger();

            var fundsSeed = actions.Find(a => a.Type == GameActionType.FundsInitial);
            Assert.NotNull(fundsSeed);
            Assert.Equal(StartFunds, fundsSeed.InitialFunds, 4);

            var scienceSeed = actions.Find(a => a.Type == GameActionType.ScienceInitial);
            Assert.NotNull(scienceSeed);
            Assert.Equal(StartScience, scienceSeed.InitialScience, 4);

            var repSeed = actions.Find(a => a.Type == GameActionType.ReputationInitial);
            Assert.NotNull(repSeed);
            Assert.Equal(StartReputation, repSeed.InitialReputation, 4);
        }

        // ================================================================
        // The adjudication: real engine vs KSP's own save
        // ================================================================

        [Fact]
        public void RealEngine_ReplaysPostFixLedger_PoolsVsSave()
        {
            // PRODUCTION registration: the ctor called LedgerOrchestrator.Initialize(),
            // so the module graph under replay is the one production builds.
            var actions = LoadFixtureLedger();

            RecalculationEngine.Recalculate(actions);

            double reconFunds = LedgerOrchestrator.Funds.GetRunningBalance();
            double reconScience = LedgerOrchestrator.Science.GetRunningScience();
            double reconRep = LedgerOrchestrator.Reputation.GetRunningRep();

            string report =
                $"funds: recon={reconFunds.ToString("R", IC)} save={SaveFunds.ToString("R", IC)} d={(reconFunds - SaveFunds).ToString("R", IC)} | " +
                $"science: recon={reconScience.ToString("R", IC)} save={SaveScience.ToString("R", IC)} d={(reconScience - SaveScience).ToString("R", IC)} | " +
                $"rep: recon={reconRep.ToString("R", IC)} save={SaveReputation.ToString("R", IC)} d={(reconRep - SaveReputation).ToString("R", IC)}";

            // FUNDS. The seed (500000), the five milestone awards
            // (14400 + 14400 + 800 + 1600 + 800 = 32000) and the vessel-recovery credit
            // (4558) are every funds row this ledger has, and they sum to the save's own
            // 536558 exactly. Pre-fix this reconstructed 4558 low, because the last of
            // those three groups did not exist.
            Assert.True(Math.Abs(reconFunds - SaveFunds) < TightTolerance,
                "FUNDS no longer closes. " + report);

            // SCIENCE. The seed (100) plus the three earned subjects
            // (3 + 3.6000001430511475 + 5 = 11.6). Pre-fix this reconstructed 100 low,
            // the whole seed. The earnings themselves always reconstructed exactly - see
            // the positive statement below, kept precisely so a regression in the
            // earning path cannot hide inside a closing seed.
            Assert.True(Math.Abs(reconScience - SaveScience) < TightTolerance,
                "SCIENCE no longer closes. " + report);

            // REPUTATION. Two +1 milestone awards, on a career with no strategy, no
            // contracts and a genuinely-zero seed - which is what made this the cleanest
            // measurement of the granular award path anyone had, and what let the
            // 0.00148 be read as arithmetic rather than guessed at as a leak. The answer
            // was the residual step: stock sizes the last step of its award loop from
            // the accumulated POST-CURVE actual, and Parsek sized it from the nominal
            // step count, which is identically zero for an integer award.
            Assert.True(Math.Abs(reconRep - SaveReputation) < ReputationFloatNoiseTolerance,
                "REPUTATION no longer closes. " + report);

            // The earned science, stated positively and tightly. This is the assertion
            // that survived unchanged from the wave-2 fixture - the earning path was
            // sound the whole time, and it is the reference point that made the seed and
            // recovery gaps legible as gaps rather than as general breakage.
            Assert.True(Math.Abs((reconScience - StartScience) - 11.600000143051147) < 0.0001,
                "the three earned science subjects no longer reconstruct exactly. " + report);
        }
    }
}
