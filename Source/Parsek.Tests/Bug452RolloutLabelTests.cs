using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Bug #452: a cancelled-rollout <c>FundsSpending(VesselBuild)</c> action persists in
    /// the ledger after #445 (so the funds total stays in sync with KSP's deduction), but
    /// previously rendered with the same generic <c>"Vessel build"</c> label as an adopted
    /// (recording-tagged) build cost — players had no way to tell which entries belonged
    /// to a cancelled rollout vs. a real flight.
    ///
    /// Fix: append a <c>"(cancelled rollout)"</c> suffix to the description when the
    /// action has <c>RecordingId == null</c> and <c>DedupKey</c> starts with
    /// <c>"rollout:"</c>. The discriminator is also consumed by the Timeline renderer,
    /// so both views label the entry consistently. Adopted rollouts (which clear the
    /// <c>DedupKey</c> as part of <c>TryAdoptRolloutAction</c>) and ordinary
    /// recording-tagged build costs continue to render with the existing label.
    /// </summary>
    public class Bug452RolloutLabelTests
    {
        // MakeUnclaimedRollout uses the legacy bare-key shape "rollout:<UT>". Current
        // main's OnVesselRolloutSpending emits the long form "rollout:<UT>|pid=|site=|vessel="
        // via BuildRolloutDedupKey — both shapes satisfy StartsWith("rollout:", Ordinal),
        // so the predicate behavior is identical. A dedicated long-form fact below pins
        // the current-production shape against silent drift.
        //
        // MakeAdoptedRollout and MakeOrdinaryBuildAction are intentionally shape-identical
        // (RecordingId set, DedupKey null). Both deserve their own named helper because
        // they represent two different real-world codepaths that land in the same state:
        //   - Adopted: OnVesselRolloutSpending wrote an entry with a rollout: DedupKey,
        //     then TryAdoptRolloutAction assigned RecordingId and cleared DedupKey.
        //   - Ordinary: the recording-side delta path wrote a build cost directly with
        //     RecordingId set and no DedupKey ever assigned.
        // The predicate must return false for both; distinct helpers keep the tests
        // self-documenting about which regression is being guarded against.

        private static GameAction MakeUnclaimedRollout(double ut = 100.0, float cost = 5000f) =>
            new GameAction
            {
                UT = ut,
                Type = GameActionType.FundsSpending,
                FundsSpendingSource = FundsSpendingSource.VesselBuild,
                FundsSpent = cost,
                RecordingId = null,
                DedupKey = "rollout:" + ut.ToString("R", System.Globalization.CultureInfo.InvariantCulture)
            };

        private static GameAction MakeAdoptedRollout(string recordingId = "rec-A", double ut = 100.0, float cost = 5000f) =>
            new GameAction
            {
                UT = ut,
                Type = GameActionType.FundsSpending,
                FundsSpendingSource = FundsSpendingSource.VesselBuild,
                FundsSpent = cost,
                // TryAdoptRolloutAction sets RecordingId AND clears DedupKey on adoption.
                RecordingId = recordingId,
                DedupKey = null
            };

        private static GameAction MakeOrdinaryBuildAction(string recordingId = "rec-A", double ut = 100.0, float cost = 5000f) =>
            new GameAction
            {
                UT = ut,
                Type = GameActionType.FundsSpending,
                FundsSpendingSource = FundsSpendingSource.VesselBuild,
                FundsSpent = cost,
                // Recording-side delta path: RecordingId set, no DedupKey at all.
                RecordingId = recordingId,
                DedupKey = null
            };

        // ----------------------------------------------------------------
        // IsUnclaimedRolloutAction predicate
        // ----------------------------------------------------------------

        [Fact]
        public void IsUnclaimedRolloutAction_NullRecordingIdAndRolloutDedupKey_True()
        {
            // The headline #452 case: rollout was written by OnVesselRolloutSpending and
            // never adopted by a recording — it still carries the rollout: dedup tag.
            Assert.True(GameActionDisplay.IsUnclaimedRolloutAction(MakeUnclaimedRollout()));
        }

        [Fact]
        public void IsUnclaimedRolloutAction_AdoptedAction_False()
        {
            // After adoption, RecordingId is set and DedupKey is cleared. The entry now
            // belongs to a real recorded flight and must NOT carry the suffix.
            Assert.False(GameActionDisplay.IsUnclaimedRolloutAction(MakeAdoptedRollout()));
        }

        [Fact]
        public void IsUnclaimedRolloutAction_OrdinaryRecordingBuildAction_False()
        {
            // Recording-side delta path: PreLaunchFunds-to-first-point produced a
            // build cost. RecordingId is set, no DedupKey. Must render as a normal build.
            Assert.False(GameActionDisplay.IsUnclaimedRolloutAction(MakeOrdinaryBuildAction()));
        }

        [Fact]
        public void IsUnclaimedRolloutAction_NonRolloutDedupKey_False()
        {
            // Regression guard: FundsSpending(Other) part purchases also use DedupKey
            // (set to the part name). They must NOT match the rollout suffix path.
            var partPurchase = new GameAction
            {
                Type = GameActionType.FundsSpending,
                FundsSpendingSource = FundsSpendingSource.Other,
                FundsSpent = 600f,
                RecordingId = null,
                DedupKey = "liquidEngine"
            };
            Assert.False(GameActionDisplay.IsUnclaimedRolloutAction(partPurchase));
        }

        [Fact]
        public void IsUnclaimedRolloutAction_DedupKeyIsRolloutWithoutColon_False()
        {
            // Contract pin: the predicate uses StartsWith("rollout:", Ordinal) — the
            // colon is mandatory so the literal "rollout" without a colon (e.g. an
            // accidental typo from a future producer) does NOT match. Locks the colon
            // contract against an inadvertent StartsWith("rollout") regression.
            var noColon = new GameAction
            {
                Type = GameActionType.FundsSpending,
                FundsSpendingSource = FundsSpendingSource.VesselBuild,
                FundsSpent = 5000f,
                RecordingId = null,
                DedupKey = "rollout"
            };
            Assert.False(GameActionDisplay.IsUnclaimedRolloutAction(noColon));
        }

        [Fact]
        public void IsUnclaimedRolloutAction_ProductionShapeDedupKey_True()
        {
            // Pin the current-main DedupKey shape produced by BuildRolloutDedupKey:
            // "rollout:<UT>|pid=<n>|site=<esc>|vessel=<esc>". The predicate must match
            // the long form too, not just the legacy bare-key shape used by the other
            // unclaimed-rollout fact. Guards against silent drift if a future producer
            // changes the key shape without updating the predicate.
            var longKey = new GameAction
            {
                UT = 100.0,
                Type = GameActionType.FundsSpending,
                FundsSpendingSource = FundsSpendingSource.VesselBuild,
                FundsSpent = 5000f,
                RecordingId = null,
                DedupKey = "rollout:100|pid=12345|site=LaunchPad|vessel=Kerbal%20X"
            };
            Assert.True(GameActionDisplay.IsUnclaimedRolloutAction(longKey));
            Assert.Equal(
                "Vessel build -5000" + GameActionDisplay.CancelledRolloutSuffix,
                GameActionDisplay.GetDescription(longKey, Game.Modes.CAREER));
        }

        [Fact]
        public void IsUnclaimedRolloutAction_RecordingIdSetAndRolloutDedupKeyNonNull_False()
        {
            // Defensive contract: the predicate's RecordingId short-circuit must win
            // over a rollout: DedupKey. If a future adoption path ever forgets to clear
            // DedupKey (TryAdoptRolloutAction currently does so at LedgerOrchestrator
            // ~line 2787), an adopted entry would otherwise incorrectly acquire the
            // suffix. Pin the short-circuit ordering explicitly.
            var adoptedWithStaleDedup = new GameAction
            {
                UT = 100.0,
                Type = GameActionType.FundsSpending,
                FundsSpendingSource = FundsSpendingSource.VesselBuild,
                FundsSpent = 5000f,
                RecordingId = "rec-A",
                DedupKey = "rollout:100"
            };
            Assert.False(GameActionDisplay.IsUnclaimedRolloutAction(adoptedWithStaleDedup));
        }

        [Fact]
        public void IsUnclaimedRolloutAction_DedupKeyIsRolloutPrefixOnly_True()
        {
            // Documentary: an action whose DedupKey is exactly "rollout:" (empty after
            // the prefix) still satisfies the predicate. Current production cannot
            // produce this shape — BuildRolloutDedupKey always appends ut.ToString("R"),
            // so the suffix is never empty — but the predicate accepts it. Pin the
            // behavior so a future tightening (e.g. requiring a non-empty suffix) is a
            // conscious decision rather than a drift.
            var prefixOnly = new GameAction
            {
                Type = GameActionType.FundsSpending,
                FundsSpendingSource = FundsSpendingSource.VesselBuild,
                FundsSpent = 5000f,
                RecordingId = null,
                DedupKey = "rollout:"
            };
            Assert.True(GameActionDisplay.IsUnclaimedRolloutAction(prefixOnly));
        }

        [Fact]
        public void IsUnclaimedRolloutAction_VesselBuildButNoDedupKey_False()
        {
            // A FundsSpending(VesselBuild) with neither RecordingId nor DedupKey should
            // not be misidentified as a rollout — without the rollout: tag we cannot
            // assert that it came from the OnVesselRolloutSpending path.
            var orphan = new GameAction
            {
                Type = GameActionType.FundsSpending,
                FundsSpendingSource = FundsSpendingSource.VesselBuild,
                FundsSpent = 5000f,
                RecordingId = null,
                DedupKey = null
            };
            Assert.False(GameActionDisplay.IsUnclaimedRolloutAction(orphan));
        }

        [Fact]
        public void IsUnclaimedRolloutAction_NonFundsSpendingType_False()
        {
            var earning = new GameAction
            {
                Type = GameActionType.FundsEarning,
                FundsAwarded = 5000f
            };
            Assert.False(GameActionDisplay.IsUnclaimedRolloutAction(earning));
        }

        [Fact]
        public void IsUnclaimedRolloutAction_NullAction_False()
        {
            Assert.False(GameActionDisplay.IsUnclaimedRolloutAction(null));
        }

        // ----------------------------------------------------------------
        // GameActionDisplay.GetDescription (Actions / Ledger window)
        // ----------------------------------------------------------------

        [Fact]
        public void GetDescription_UnclaimedRollout_AppendsCancelledRolloutSuffix()
        {
            // The user-visible #452 fix: the cancelled-rollout entry now carries a
            // suffix that distinguishes it from an adopted build cost.
            string desc = GameActionDisplay.GetDescription(MakeUnclaimedRollout(cost: 5000f), Game.Modes.CAREER);
            Assert.Equal("Vessel build -5000" + GameActionDisplay.CancelledRolloutSuffix, desc);
        }

        [Fact]
        public void GetDescription_AdoptedRollout_NoSuffix()
        {
            // Adopted by a recording — render as the existing plain label, no suffix.
            string desc = GameActionDisplay.GetDescription(MakeAdoptedRollout(cost: 5000f), Game.Modes.CAREER);
            Assert.Equal("Vessel build -5000", desc);
        }

        [Fact]
        public void GetDescription_OrdinaryRecordingBuildCost_NoSuffix()
        {
            // Regression guard: a normal recording-side build action without any
            // rollout: dedup tag must continue to render unchanged.
            string desc = GameActionDisplay.GetDescription(MakeOrdinaryBuildAction(cost: 5000f), Game.Modes.CAREER);
            Assert.Equal("Vessel build -5000", desc);
        }

        // ----------------------------------------------------------------
        // TimelineEntryDisplay.GetGameActionText (Timeline window)
        // ----------------------------------------------------------------

        [Fact]
        public void TimelineGetGameActionText_UnclaimedRollout_AppendsCancelledRolloutSuffix()
        {
            // Both renderers (Actions and Timeline) must agree, so the suffix is
            // also present in the per-frame Timeline view.
            string text = TimelineEntryDisplay.GetGameActionText(
                MakeUnclaimedRollout(cost: 5000f), vesselName: null, currentMode: Game.Modes.CAREER);
            Assert.Equal("Build -5000" + GameActionDisplay.CancelledRolloutSuffix, text);
        }

        [Fact]
        public void TimelineGetGameActionText_AdoptedRollout_NoSuffix()
        {
            string text = TimelineEntryDisplay.GetGameActionText(
                MakeAdoptedRollout(cost: 5000f), vesselName: null, currentMode: Game.Modes.CAREER);
            Assert.Equal("Build -5000", text);
        }

        [Fact]
        public void TimelineGetGameActionText_OrdinaryRecordingBuildCost_NoSuffix()
        {
            string text = TimelineEntryDisplay.GetGameActionText(
                MakeOrdinaryBuildAction(cost: 5000f), vesselName: null, currentMode: Game.Modes.CAREER);
            Assert.Equal("Build -5000", text);
        }
    }
}
