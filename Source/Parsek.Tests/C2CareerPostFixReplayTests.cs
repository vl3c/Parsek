using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// The POST-FIX sibling of <see cref="C2CareerLedgerReplayTests"/>: replay the REAL
    /// recalculation engine over a career that was EARNED BY A DRIVEN FLIGHT on current
    /// code, and diff the reconstructed pools against KSP's own on-disk save.
    ///
    /// THE FIXTURE. <c>Source/Parsek.Tests/Fixtures/C2CareerPostFix/</c> is the save
    /// produced by harness run <c>2026-08-19_1912_L3-career-science-recover</c> - flown
    /// over the harness save fixture `career-science-pad`, on branch
    /// `career-science-craft` (the two names are a fixture and a branch, not one
    /// thing) - hand-copied verbatim. The mission flew every phase
    /// (PRELAUNCH -> ASCENT -> COAST -> DESCENT -> LANDED -> COLLECT -> TRANSMIT ->
    /// RECOVER -> RECOVERED, MISSION-OK): a career pad hop that ran three science
    /// experiments, TRANSMITTED one, and RECOVERED the craft. That makes it the first
    /// committed fixture in the repo whose career EARNED rather than SPENT - 13 ledger
    /// actions carrying three `ScienceEarning` rows, one of them the recovery subject
    /// (`recovery@KerbinFlew`, method Recovered), and five stock milestones.
    ///
    /// WHY IT IS A SECOND FIXTURE AND NOT A REPLACEMENT. `C2Career` is FROZEN PRE-FIX
    /// DATA: a real hand-played career carrying a STRATEGY currency exchange, whose
    /// science leg the capture layer dropped at the time it was recorded. A capture-side
    /// fix cannot retro-fill a committed `ledger.pgld`, so those cells keep asserting
    /// the pre-fix shape and keep passing. This fixture does NOT retire them and does
    /// not re-measure them - it carries no strategy leg at all. The two fixtures answer
    /// different questions:
    ///
    ///   C2Career        a hand-played career with a strategy exchange, PRE-fix capture.
    ///   C2CareerPostFix a driven flight-earned career, POST-fix capture, no strategy.
    ///
    /// WHAT THIS FIXTURE ACTUALLY ESTABLISHES, and it is NOT what the wave predicted.
    /// The wave expected funds and science to close tight and reputation to be the one
    /// needing a window. All three diverge, each by a different and separately named
    /// amount, and NONE of the three is a recalc fault:
    ///
    ///   FUNDS reconstructs 4558 LOW - the vessel-recovery funds credit is never
    ///     written as a ledger row (CAREER-RECOVERY-FUNDS-NOT-LEDGERED).
    ///   SCIENCE reconstructs 100 LOW - the career's science SEED was captured as 0 on
    ///     a save whose science pool was 100
    ///     (CAREER-SCIENCE-SEED-LOST-ON-FLIGHT-ROUTE).
    ///   REPUTATION reconstructs 0.00148 LOW - two +1 milestone awards land at
    ///     1.9985168 against KSP's 1.99999881, cause open
    ///     (CAREER-MILESTONE-REP-AWARD-RECONSTRUCTS-LOW).
    ///
    /// WHAT DOES CLOSE, and it is the thing the forge was built to prove: the three
    /// EARNED science subjects reconstruct EXACTLY (3 + 3.6000001430511475 + 5, against
    /// the save's own three per-subject entries). The earning path works; the seed and
    /// the recovery-funds paths are what do not.
    ///
    /// All three divergences are PINNED as magnitudes rather than tolerated inside
    /// windows, in the C2Career style: they are DATA-ERA pins over data captured before
    /// those gaps were closed, so a capture-side fix cannot move them and a RECALC-side
    /// change must. Each has an entry in `docs/dev/todo-and-known-bugs.md` carrying the
    /// evidence from the flight log.
    ///
    /// THE CONSEQUENCE FOR STRICT ARMING (career-ledger B.4): this fixture is the
    /// subject that deferral was waiting for - a career carrying recorded crewed
    /// recoveries and populated per-identity facets - and it is NOT yet armable,
    /// because two of its three pools do not close. The three entries above are what
    /// stand between it and wave 3.
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
        private const double SaveFunds = 536558.0;
        private const double SaveScience = 111.599998;
        private const double SaveReputation = 1.99999881;

        // The pools the run STARTED from, inherited unchanged from `career-science-pad`
        // (itself `fresh-career`'s). They are what the seed rows should have carried.
        private const double StartFunds = 500000.0;
        private const double StartScience = 100.0;
        private const double StartReputation = 0.0;

        // THE TWO PINNED DIVERGENCES. Each is a DATA-ERA pin: the magnitude is a
        // property of what the capture layer wrote on 2026-08-19, not of the recalc.
        //
        // FUNDS: stock paid 4558 for the recovered Jumping Flea and the ledger carries
        // no row for it. The flight log names the exact branch - Parsek's own
        // pre-computation of the recovery value came out zero
        // (`Recovery processing captured: ... fundsEarned=0.0 ... recoveryFactor=1`), so
        // `OnVesselRecoveryFunds` took its "stock expected zero recovery funds" skip and
        // never paired the deferred credit - and then the FundsChanged event arrived
        // anyway (`Game state: FundsChanged +4558 (VesselRecovery) -> 536558`). The
        // EVENT channel is healthy; the LEDGER ROW is what is missing.
        private const double PinnedFundsShortfall = 4558.0;

        // SCIENCE: `initialScience = 0` on a save whose science pool was 100. Not a
        // silent drop - a correct guard firing on a broken input. `DeferredSeed` read
        // `Funding=500000, Science=null, Rep=null`, so only funds seeded; by the time
        // the science seed was retried at scene exit, science timeline actions already
        // existed and `SeedInitialScience` correctly refused to treat the then-current
        // 106.6 as initial. Treating it as initial would have double-counted the
        // earnings; the defect is upstream, in the seed never landing.
        private const double PinnedScienceShortfall = 100.0;

        // REPUTATION: the third pinned divergence, and the one whose CAUSE is still
        // open. Two milestone awards of +1 each reconstruct to 1.9985168 against KSP's
        // own 1.99999881. See the cell for why this fixture separates the question
        // C2Career could not.
        private const double PinnedReputationShortfall = 0.001482011980590725;

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
        public void FixtureLedger_LoadsAllThirteenActions()
        {
            var actions = LoadFixtureLedger();
            Assert.Equal(13, actions.Count);
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

            // The recovery subject's science leg IS captured - which is what makes the
            // uncaptured FUNDS leg below a leg-level gap rather than a missing hook.
            Assert.Equal(5.0, bySubject["recovery@KerbinFlew"].ScienceAwarded, 4);

            // Five stock milestones fired across the hop (RecordsSpeed, RecordsAltitude,
            // FirstLaunch, Kerbin/Science, FirstCrewToSurvive).
            Assert.Equal(5, actions.FindAll(a => a.Type == GameActionType.MilestoneAchievement).Count);
        }

        [Fact]
        public void FixtureLedger_HasNoFundsEarningRow_TheUncapturedRecoveryCredit()
        {
            // CAREER-RECOVERY-FUNDS-NOT-LEDGERED, stated by SHAPE rather than by
            // magnitude, so it survives a re-forged fixture the way C2Career's structural
            // cell does. Stock paid 4558 for the recovered craft - the save's funds moved
            // from 532000 to 536558 and the FundsChanged event fired keyed
            // 'VesselRecovery' - and the ledger carries not one `FundsEarning` row.
            //
            // The ONLY funds a replay of this ledger can reconstruct are the seed plus
            // the five milestone awards. That is exactly the pinned shortfall below.
            //
            // WHEN THE CAPTURE IS FIXED THIS CELL FLIPS, which is the point: a re-harvest
            // on fixed code makes `FundsEarning` non-empty and reds here, and the
            // pinned-magnitude cell closes at the same time.
            var actions = LoadFixtureLedger();
            var fundsEarnings = actions.FindAll(a => a.Type == GameActionType.FundsEarning);
            Assert.True(fundsEarnings.Count == 0,
                $"expected NO FundsEarning row in this pre-fix-capture fixture; found " +
                $"{fundsEarnings.Count.ToString(IC)}. Movement here means the FIXTURE " +
                "changed (a re-harvest on fixed code), not the recalc - and on a " +
                "re-harvest the expectation becomes 'a FundsEarning keyed to the vessel " +
                "recovery carries the 4558'.");
        }

        [Fact]
        public void FixtureLedger_ScienceSeedIsZeroOnACareerThatStartedAtOneHundred()
        {
            // CAREER-SCIENCE-SEED-LOST-ON-FLIGHT-ROUTE, stated by shape. The funds seed
            // landed correctly on the same load; the science and reputation seeds did
            // not, because their KSP instances read null at the deferred-seed frame.
            // Reputation is invisible in VALUE here (this career genuinely started at 0),
            // which is precisely why science is the one that exposes the path.
            var actions = LoadFixtureLedger();

            var fundsSeed = actions.Find(a => a.Type == GameActionType.FundsInitial);
            Assert.NotNull(fundsSeed);
            Assert.Equal(StartFunds, fundsSeed.InitialFunds, 4);

            var scienceSeed = actions.Find(a => a.Type == GameActionType.ScienceInitial);
            Assert.NotNull(scienceSeed);
            Assert.True(Math.Abs(scienceSeed.InitialScience) < 0.0001,
                $"expected the seed to be 0 in this pre-fix-capture fixture (the save " +
                $"started at {StartScience.ToString("R", IC)}); found " +
                $"{scienceSeed.InitialScience.ToString("R", IC)}. A nonzero value means " +
                "the fixture was re-harvested on fixed code - at which point the science " +
                "assertion in RealEngine_ReplaysPostFixLedger_PoolsVsSave should close.");

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

            // REPUTATION: a SMALL REAL DIVERGENCE, and this fixture is the cleanest
            // measurement of it anyone has. C2Career carries the same family
            // (-0.00364) and its note says "tighten when a post-fix fixture is
            // harvested" because the question - second small leak, or curve rounding? -
            // could not be separated from that career's strategy leg. Here it can:
            // this career has NO strategy, NO contracts, a rep seed of 0 that is
            // genuinely correct, and exactly TWO reputation inputs, the +1 awards on
            // `RecordsSpeed` and `RecordsAltitude`. KSP's own pool lands at 1.99999881,
            // i.e. float32 2.0; the replay lands at 1.9985168.
            //
            // So the divergence is NOT strategy-related and NOT seed-related: it is in
            // how the MILESTONE reputation award itself is modelled, and it is a
            // fractional shortfall on a two-award career. That narrows C2Career's open
            // question considerably and is why this is pinned as a MAGNITUDE rather
            // than left inside a 0.01 window - a window that wide would have called
            // 1.9985168 "closed" and lost the signal that identifies the path.
            //
            // WHY IT IS NOT CHASED HERE: the cause is genuinely unknown (stock applies
            // a curve on `AddReputation`, and whether Parsek's model or stock's rounding
            // owns the 0.00148 needs the award path read, not guessed). Recorded in
            // CAREER-MILESTONE-REP-AWARD-RECONSTRUCTS-LOW.
            Assert.True(Math.Abs((SaveReputation - reconRep) - PinnedReputationShortfall) < 0.0001,
                "REPUTATION shortfall moved off its pinned value. " + report);

            // FUNDS: reconstructs LOW by exactly the uncaptured vessel-recovery credit.
            // PINNED, not tolerated. The seed (500000) plus the five milestone awards
            // (14400 + 14400 + 800 + 1600 + 800 = 32000) is every funds row this ledger
            // has, and 532000 is what a replay can therefore reach; the save says 536558.
            // A capture-side fix cannot move a committed ledger.pgld, so this magnitude
            // is stable until the fixture is re-harvested. If it moves WITHOUT a
            // re-harvest, a RECALC-side change has occurred and must be investigated.
            Assert.True(Math.Abs((SaveFunds - reconFunds) - PinnedFundsShortfall) < 0.01,
                "FUNDS shortfall moved off its pinned value (the uncaptured " +
                "vessel-recovery credit). " + report);

            // SCIENCE: reconstructs LOW by exactly the lost seed. The three earnings
            // themselves reconstruct PERFECTLY - 3 + 3.6000001430511475 + 5 = 11.6, and
            // the save's own three per-subject entries read 3 / 3.60000014 / 5 - so the
            // earning path this forge was built to exercise is sound. The whole shortfall
            // is the missing 100-point seed, which is why the tolerance below is tight
            // enough to catch a single mis-credited subject.
            Assert.True(Math.Abs((SaveScience - reconScience) - PinnedScienceShortfall) < 0.01,
                "SCIENCE shortfall moved off its pinned value (the lost career seed). " + report);

            // ...and the positive statement of that last sentence, so a regression in the
            // earning path cannot hide inside the seed shortfall: what the engine DID
            // reconstruct is exactly the three earned subjects.
            Assert.True(Math.Abs(reconScience - 11.600000143051147) < 0.0001,
                "the three earned science subjects no longer reconstruct exactly. " + report);
        }
    }
}
