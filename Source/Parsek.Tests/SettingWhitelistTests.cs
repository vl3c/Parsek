using System.Collections.Generic;
using Parsek.TestCommands;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Pure coverage for the SetSetting whitelist (<see cref="SettingWhitelist"/>).
    /// Guards three contracts: typed parse + range acceptance, the security boundary
    /// (an arbitrary field name is rejected, never reflectively set), and the
    /// persistence-route column (the 8 sidecar-tracked settings carry the exact
    /// ParsekSettingsPersistence.Record* selector; the 8 GameParameters-only ones do
    /// not). A regression in the route column writes a tracked setting only to the
    /// live field, which ParsekScenario.OnLoad's ApplyTo would silently revert at the
    /// next save load.
    /// </summary>
    public class SettingWhitelistTests
    {
        // ----- Accept + type/range -----

        [Theory]
        [InlineData("autoRecordOnLaunch", "true", true)]
        [InlineData("autoRecordOnLaunch", "false", false)]
        [InlineData("autoMerge", "true", true)]
        [InlineData("verboseLogging", "false", false)]
        [InlineData("ghostRenderTracing", "true", true)]
        public void Accept_Bool(string name, string raw, bool expected)
        {
            var r = SettingWhitelist.TryApply(name, raw);
            Assert.True(r.Accepted);
            Assert.Equal(SettingValueType.Bool, r.Type);
            Assert.Equal(expected, r.BoolValue);
        }

        [Theory]
        [InlineData("samplingDensity", "0", 0)]
        [InlineData("samplingDensity", "1", 1)]
        [InlineData("samplingDensity", "2", 2)]
        [InlineData("transitedBodyRotationModeIndex", "2", 2)]
        public void Accept_Int_InRange(string name, string raw, int expected)
        {
            var r = SettingWhitelist.TryApply(name, raw);
            Assert.True(r.Accepted);
            Assert.Equal(SettingValueType.Int, r.Type);
            Assert.Equal(expected, r.IntValue);
        }

        [Fact]
        public void Accept_Float_InRange()
        {
            var r = SettingWhitelist.TryApply("ghostAudioVolume", "0.7");
            Assert.True(r.Accepted);
            Assert.Equal(SettingValueType.Float, r.Type);
            Assert.True(System.Math.Abs(0.7f - r.FloatValue) < 1e-5f);
        }

        // ----- Reject: type / range / locale -----

        [Theory]
        [InlineData("samplingDensity", "5")]   // out of range 0..2
        [InlineData("samplingDensity", "-1")]  // out of range
        [InlineData("transitedBodyRotationModeIndex", "3")]
        [InlineData("ghostAudioVolume", "1.5")] // out of range 0..1
        [InlineData("ghostAudioVolume", "0,7")] // comma locale -> InvariantCulture rejects
        [InlineData("autoMerge", "yes")]        // non-bool
        [InlineData("samplingDensity", "abc")]  // non-int
        public void Reject_ValueInvalid(string name, string raw)
        {
            var r = SettingWhitelist.TryApply(name, raw);
            Assert.False(r.Accepted);
            Assert.Equal("setting-value-invalid", r.RejectReason);
        }

        // ----- Security: arbitrary field never accepted -----

        [Theory]
        [InlineData("someOtherField")]
        [InlineData("autoLoopIntervalSeconds")] // a real ParsekSettings field, but NOT whitelisted
        [InlineData("")]
        public void Reject_NonWhitelisted(string name)
        {
            var r = SettingWhitelist.TryApply(name, "true");
            Assert.False(r.Accepted);
            Assert.Equal("setting-not-whitelisted", r.RejectReason);
            // The decision is pure data; there is no reflective set path to reach the field.
            Assert.Null(r.RecordMethod);
        }

        // ----- Persistence route for all 16 -----

        [Theory]
        [InlineData("autoRecordOnLaunch")]
        [InlineData("autoRecordOnEva")]
        [InlineData("autoRecordOnFirstModificationAfterSwitch")]
        [InlineData("autoMerge")]
        [InlineData("verboseLogging")]
        [InlineData("samplingDensity")]
        [InlineData("ghostAudioVolume")]
        [InlineData("transitedBodyRotationModeIndex")]
        public void Route_GameParametersOnly_NoRecordMethod(string name)
        {
            var r = SettingWhitelist.TryApply(name, DefaultRawFor(name));
            Assert.True(r.Accepted);
            Assert.Equal(PersistenceRoute.GameParameters, r.Route);
            Assert.Null(r.RecordMethod);
        }

        [Theory]
        [InlineData("ghostRenderTracing", "RecordGhostRenderTracing")]
        [InlineData("mapRenderTracing", "RecordMapRenderTracing")]
        [InlineData("ledgerTracing", "RecordLedgerTracing")]
        [InlineData("writeReadableSidecarMirrors", "RecordReadableSidecarMirrors")] // name asymmetry
        [InlineData("autoBackupExistingSaves", "RecordAutoBackupExistingSaves")]
        [InlineData("showCommittedFutureOverlays", "RecordShowCommittedFutureOverlays")]
        [InlineData("blockCommittedActions", "RecordBlockCommittedActions")]
        [InlineData("showRouteLines", "RecordShowRouteLines")]
        public void Route_SidecarTracked_CarriesExactRecordMethod(string name, string expectedMethod)
        {
            var r = SettingWhitelist.TryApply(name, "true");
            Assert.True(r.Accepted);
            Assert.Equal(PersistenceRoute.GameParametersPlusSidecar, r.Route);
            Assert.Equal(expectedMethod, r.RecordMethod);
        }

        [Fact]
        public void Whitelist_HasExactly16Entries_8Tracked()
        {
            Assert.Equal(16, SettingWhitelist.WhitelistedNames.Count);

            int tracked = 0;
            foreach (string name in SettingWhitelist.WhitelistedNames)
            {
                var r = SettingWhitelist.TryApply(name, DefaultRawFor(name));
                Assert.True(r.Accepted);
                if (r.Route == PersistenceRoute.GameParametersPlusSidecar)
                {
                    tracked++;
                    Assert.NotNull(r.RecordMethod);
                }
                else
                {
                    Assert.Null(r.RecordMethod);
                }
            }
            Assert.Equal(8, tracked);
        }

        [Fact]
        public void RecordMethodNames_MatchPersistenceMembers()
        {
            // The whitelist stores method-name strings; verify each names a real
            // ParsekSettingsPersistence member (the applier calls it by name, so a
            // typo here would silently no-op the sidecar write at runtime).
            var persistenceType = typeof(ParsekSettingsPersistence);
            foreach (string name in SettingWhitelist.WhitelistedNames)
            {
                var r = SettingWhitelist.TryApply(name, DefaultRawFor(name));
                if (r.RecordMethod == null) continue;
                var method = persistenceType.GetMethod(
                    r.RecordMethod,
                    System.Reflection.BindingFlags.Static
                    | System.Reflection.BindingFlags.NonPublic
                    | System.Reflection.BindingFlags.Public);
                Assert.True(method != null, $"missing ParsekSettingsPersistence.{r.RecordMethod} for setting {name}");
            }
        }

        private static string DefaultRawFor(string name)
        {
            switch (name)
            {
                case "samplingDensity":
                case "transitedBodyRotationModeIndex":
                    return "1";
                case "ghostAudioVolume":
                    return "0.5";
                default:
                    return "true";
            }
        }
    }
}
