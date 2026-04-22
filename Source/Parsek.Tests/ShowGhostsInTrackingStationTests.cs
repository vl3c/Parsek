using System;
using System.Collections.Generic;
using System.Reflection;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Tests for the #388 Tracking Station ghost visibility toggle.
    ///
    /// The field is exposed through KSP's <c>GameParameters.CustomParameterUI</c>
    /// framework for in-game editing, AND mirrored through
    /// <see cref="ParsekSettingsPersistence"/> so it survives rewind, quickload,
    /// and KSP session restart — the same "sticky user intent" treatment as
    /// <c>writeReadableSidecarMirrors</c>.
    /// Tests below pin BOTH contracts: a regression in either one lets the
    /// user's preference silently revert on the next load.
    /// </summary>
    [Collection("Sequential")]
    public class ShowGhostsInTrackingStationTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public ShowGhostsInTrackingStationTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekSettingsPersistence.ResetForTesting();
            // Mark the store as "loaded" without touching disk — KSPUtil.ApplicationRootPath
            // throws ECall under xUnit because Unity's runtime isn't linked. Passing null
            // as the value clears the field AND sets the internal loaded=true flag, so
            // ApplyTo below won't retry LoadIfNeeded and hit the file system.
            ParsekSettingsPersistence.SetStoredShowGhostsInTrackingStationForTesting(null);
        }

        public void Dispose()
        {
            ParsekSettingsPersistence.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        // ---- Field / attribute contract (what KSP's GameParameters framework needs) ----

        [Fact]
        public void Field_DefaultsToTrue()
        {
            var settings = new ParsekSettings();
            Assert.True(settings.showGhostsInTrackingStation,
                "Default should preserve pre-#388 behavior (ghosts visible in TS)");
        }

        [Fact]
        public void Field_HasCustomParameterUiAttribute()
        {
            FieldInfo field = typeof(ParsekSettings)
                .GetField(nameof(ParsekSettings.showGhostsInTrackingStation));

            Assert.NotNull(field);
            Assert.NotNull(field.GetCustomAttribute<GameParameters.CustomParameterUI>());
        }

        [Fact]
        public void Field_IsPublicInstanceBool()
        {
            FieldInfo field = typeof(ParsekSettings)
                .GetField(nameof(ParsekSettings.showGhostsInTrackingStation),
                    BindingFlags.Public | BindingFlags.Instance);
            Assert.NotNull(field);
            Assert.Equal(typeof(bool), field.FieldType);
        }

        // ---- Persistence contract (what ParsekSettingsPersistence needs) ----
        // These are the load/revert survival tests. They exercise the exact
        // path ParsekScenario.OnLoad runs (ApplyTo on a fresh ParsekSettings)
        // so a future refactor that forgets to wire the field into the store
        // fails loudly here instead of silently reverting user intent.

        [Fact]
        public void PersistentStore_DefaultsToNull_UntilRecorded()
        {
            Assert.Null(ParsekSettingsPersistence.GetStoredShowGhostsInTrackingStation());
        }

        [Fact]
        public void RecordShowGhostsInTrackingStation_UpdatesStore()
        {
            ParsekSettingsPersistence.SetStoredShowGhostsInTrackingStationForTesting(false);
            Assert.False(
                ParsekSettingsPersistence.GetStoredShowGhostsInTrackingStation()
                    ?? throw new InvalidOperationException("stored value unexpectedly null"));
        }

        [Fact]
        public void ApplyTo_WithNoStoredValue_DoesNotOverrideField()
        {
            var settings = new ParsekSettings { showGhostsInTrackingStation = false };
            ParsekSettingsPersistence.ApplyTo(settings);
            Assert.False(settings.showGhostsInTrackingStation,
                "ApplyTo with no stored override must leave the field untouched");
        }

        [Fact]
        public void ApplyTo_OverridesFieldWhenStoreHasValue()
        {
            // Simulate the real load/revert sequence:
            //   1. KSP's GameParameters restores whatever was in the .sfs — the
            //      "stale" value — which may be the default (true) or some other
            //      value a different save happened to have.
            //   2. ParsekScenario.OnLoad then calls ApplyTo(ParsekSettings.Current),
            //      which must overwrite the stale value with the user's last choice.
            var settings = new ParsekSettings { showGhostsInTrackingStation = true };
            ParsekSettingsPersistence.SetStoredShowGhostsInTrackingStationForTesting(false);

            ParsekSettingsPersistence.ApplyTo(settings);

            Assert.False(settings.showGhostsInTrackingStation,
                "ApplyTo must overwrite the 'stale' value from KSP's GameParameters with the persisted user choice");
            Assert.Contains(logLines, l =>
                l.Contains("[SettingsStore]") &&
                l.Contains("showGhostsInTrackingStation") &&
                l.Contains("True -> False"));
        }

        [Fact]
        public void ApplyTo_NoopWhenStoredValueMatches()
        {
            var settings = new ParsekSettings { showGhostsInTrackingStation = false };
            ParsekSettingsPersistence.SetStoredShowGhostsInTrackingStationForTesting(false);

            ParsekSettingsPersistence.ApplyTo(settings);

            Assert.False(settings.showGhostsInTrackingStation);
            Assert.DoesNotContain(logLines, l =>
                l.Contains("[SettingsStore]") &&
                l.Contains("Restored showGhostsInTrackingStation"));
        }

        [Fact]
        public void ResetForTesting_ClearsStoredShowGhostsInTrackingStation()
        {
            ParsekSettingsPersistence.SetStoredShowGhostsInTrackingStationForTesting(false);
            ParsekSettingsPersistence.ResetForTesting();
            Assert.Null(ParsekSettingsPersistence.GetStoredShowGhostsInTrackingStation());
        }

        [Fact]
        public void EffectiveShowGhostsInTrackingStation_PrefersStoreOverInMemoryDefault()
        {
            // Regression guard for the SpaceTracking.Awake race: during early
            // scene load, ParsekSettings.Current can be null and falling back
            // to `?? true` would let a disabled-toggle user briefly see ghosts
            // before ApplyTo ran. The persistence-store-first helper closes
            // that window. Simulate by setting the store to false while the
            // in-memory singleton is unavailable (which is xUnit's default —
            // no HighLogic.CurrentGame is constructed).
            ParsekSettingsPersistence.SetStoredShowGhostsInTrackingStationForTesting(false);

            Assert.False(ParsekSettingsPersistence.EffectiveShowGhostsInTrackingStation(),
                "EffectiveShowGhostsInTrackingStation must honor the stored " +
                "user choice even when ParsekSettings.Current is unavailable " +
                "(SpaceTracking.Awake pre-create path).");
        }

        [Fact]
        public void EffectiveShowGhostsInTrackingStation_DefaultsTrueWhenNothingIsSet()
        {
            // Nothing in the store, no live ParsekSettings.Current in xUnit —
            // falls back to the pre-#388 "visible" default.
            Assert.True(ParsekSettingsPersistence.EffectiveShowGhostsInTrackingStation());
        }

        [Fact]
        public void ApplyTo_IsIndependentOfOtherStoredFields()
        {
            // Regression guard: make sure wiring the new field into Save/Load/Apply
            // didn't break the previously-tracked fields. This would catch e.g.
            // a typo that reads another persisted setting into showGhostsInTrackingStation.
            var settings = new ParsekSettings
            {
                showGhostsInTrackingStation = true,
                writeReadableSidecarMirrors = true,
            };
            ParsekSettingsPersistence.SetStoredShowGhostsInTrackingStationForTesting(false);
            ParsekSettingsPersistence.SetStoredReadableSidecarMirrorsForTesting(false);

            ParsekSettingsPersistence.ApplyTo(settings);

            Assert.False(settings.showGhostsInTrackingStation);
            Assert.False(settings.writeReadableSidecarMirrors);
        }

        // ---- Post-PR-328 precedence reversal (live wins over store when resolvable) ----
        // These tests pin the P2 fix: once ParsekSettings.Current is resolvable, it is
        // authoritative. Previously the store masked any Game Parameters UI flip because
        // the helper returned the stored value without consulting Current.

        [Fact]
        public void EffectiveShowGhostsInTrackingStation_LiveTrueOverridesStoreFalse()
        {
            // Reviewer's P2 scenario: user had toggle disabled (store=false), then
            // flipped it back on via KSP Game Parameters UI (live=true). Live wins,
            // but only after ParsekScenario.OnLoad has reconciled the store into
            // ParsekSettings.Current (simulated here by MarkReconciledForTesting).
            ParsekSettingsPersistence.SetStoredShowGhostsInTrackingStationForTesting(false);
            ParsekSettingsPersistence.MarkReconciledForTesting();
            try
            {
                ParsekSettings.CurrentOverrideForTesting =
                    new ParsekSettings { showGhostsInTrackingStation = true };

                Assert.True(ParsekSettingsPersistence.EffectiveShowGhostsInTrackingStation(),
                    "Live ParsekSettings.Current must override a stale store on Game Parameters UI flip.");
            }
            finally
            {
                ParsekSettings.CurrentOverrideForTesting = null;
            }
        }

        [Fact]
        public void EffectiveShowGhostsInTrackingStation_LiveFalseOverridesStoreTrue()
        {
            // Opposite direction: toggle previously enabled, user flipped off via
            // Game Parameters UI — store still true, live false. Live wins.
            ParsekSettingsPersistence.SetStoredShowGhostsInTrackingStationForTesting(true);
            ParsekSettingsPersistence.MarkReconciledForTesting();
            try
            {
                ParsekSettings.CurrentOverrideForTesting =
                    new ParsekSettings { showGhostsInTrackingStation = false };

                Assert.False(ParsekSettingsPersistence.EffectiveShowGhostsInTrackingStation());
            }
            finally
            {
                ParsekSettings.CurrentOverrideForTesting = null;
            }
        }

        [Fact]
        public void EffectiveShowGhostsInTrackingStation_LiveDiffersFromStore_ResyncsStore()
        {
            // When live != stored we resync the store so the next cold-start
            // (before ParsekSettings.Current resolves) reads the user's real intent.
            ParsekSettingsPersistence.SetStoredShowGhostsInTrackingStationForTesting(false);
            ParsekSettingsPersistence.MarkReconciledForTesting();
            try
            {
                ParsekSettings.CurrentOverrideForTesting =
                    new ParsekSettings { showGhostsInTrackingStation = true };

                ParsekSettingsPersistence.EffectiveShowGhostsInTrackingStation();

                Assert.Equal(true, ParsekSettingsPersistence.GetStoredShowGhostsInTrackingStation());
                Assert.Contains(logLines, l =>
                    l.Contains("[SettingsStore]") &&
                    l.Contains("resyncing store") &&
                    l.Contains("Game Parameters UI"));
            }
            finally
            {
                ParsekSettings.CurrentOverrideForTesting = null;
            }
        }

        [Fact]
        public void EffectiveShowGhostsInTrackingStation_LiveMatchesStore_NoResyncLog()
        {
            // Steady state: no divergence, no resync log, no redundant Save().
            ParsekSettingsPersistence.SetStoredShowGhostsInTrackingStationForTesting(false);
            ParsekSettingsPersistence.MarkReconciledForTesting();
            try
            {
                ParsekSettings.CurrentOverrideForTesting =
                    new ParsekSettings { showGhostsInTrackingStation = false };

                logLines.Clear();
                ParsekSettingsPersistence.EffectiveShowGhostsInTrackingStation();

                Assert.DoesNotContain(logLines, l => l.Contains("resyncing store"));
            }
            finally
            {
                ParsekSettings.CurrentOverrideForTesting = null;
            }
        }

        [Fact]
        public void EffectiveShowGhostsInTrackingStation_LivePresentStoreEmpty_SeedsStore()
        {
            // First-run path: store has no value yet. Returning from live and seeding
            // the store matches the old "fall through to live, no write" behavior in
            // outcome, but also warms the store so the next cold-start uses live intent.
            ParsekSettingsPersistence.MarkReconciledForTesting();
            try
            {
                ParsekSettings.CurrentOverrideForTesting =
                    new ParsekSettings { showGhostsInTrackingStation = false };

                Assert.False(ParsekSettingsPersistence.EffectiveShowGhostsInTrackingStation());
                Assert.Equal(false, ParsekSettingsPersistence.GetStoredShowGhostsInTrackingStation());
            }
            finally
            {
                ParsekSettings.CurrentOverrideForTesting = null;
            }
        }

        // ---- P2-A regression: pre-reconciliation, live Current must NOT clobber the store ----

        [Fact]
        public void EffectiveShowGhostsInTrackingStation_BeforeReconciliation_StoreStillWins()
        {
            // The review scenario: SpaceTracking.Awake runs before OnLoad has had a
            // chance to reconcile the store into ParsekSettings.Current. At that
            // point Current may exist and hold KSP's stale value (from the .sfs or
            // the compiled default) while the store holds the user's real
            // preference. Until OnLoad completes and ApplyTo flips the reconciled
            // flag, the store must win so we don't overwrite persisted intent.
            Assert.False(ParsekSettingsPersistence.IsReconciledForTesting);
            ParsekSettingsPersistence.SetStoredShowGhostsInTrackingStationForTesting(false);
            try
            {
                ParsekSettings.CurrentOverrideForTesting =
                    new ParsekSettings { showGhostsInTrackingStation = true };

                Assert.False(ParsekSettingsPersistence.EffectiveShowGhostsInTrackingStation(),
                    "Pre-reconciliation Current must NOT override the persisted store.");
                Assert.Equal(false, ParsekSettingsPersistence.GetStoredShowGhostsInTrackingStation());
                Assert.DoesNotContain(logLines, l => l.Contains("resyncing store"));
            }
            finally
            {
                ParsekSettings.CurrentOverrideForTesting = null;
            }
        }

        [Fact]
        public void ApplyTo_FlipsReconciledFlag()
        {
            // ApplyTo is the signal that Current is trustworthy. Before the call
            // we must be unreconciled; after, reconciled.
            Assert.False(ParsekSettingsPersistence.IsReconciledForTesting);

            ParsekSettingsPersistence.ApplyTo(new ParsekSettings());

            Assert.True(ParsekSettingsPersistence.IsReconciledForTesting);
        }

        [Fact]
        public void ResetForTesting_ResetsReconciledFlag()
        {
            ParsekSettingsPersistence.MarkReconciledForTesting();
            Assert.True(ParsekSettingsPersistence.IsReconciledForTesting);

            ParsekSettingsPersistence.ResetForTesting();

            Assert.False(ParsekSettingsPersistence.IsReconciledForTesting);
        }
    }
}
