using System.Collections.Generic;
using Parsek.TestCommands;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// P5.1 route-dispatch coverage for <see cref="TestCommandSettingApplier.ApplySetting"/>.
    /// The security-critical property the SetSetting design column adds is that the 8
    /// sidecar-authoritative settings ALSO route through the matching
    /// <c>ParsekSettingsPersistence.Record*</c> member, while the 8 GameParameters-only
    /// settings do NOT. A tracked setting written only to the live field would be
    /// silently reverted by <c>ParsekScenario.OnLoad</c>'s <c>ApplyTo</c> at the next
    /// save load (the exact bug this column fixes). These tests drive the pure applier
    /// with spy delegates so the routing decision is verified without a live Unity
    /// <c>ParsekSettings</c>.
    /// </summary>
    public class TestCommandSettingApplierTests
    {
        // The 8 GameParameters-only names (Record* must NOT fire) and the 8 tracked names
        // (Record* MUST fire with the exact member name), verified against the design
        // whitelist table.
        private static readonly Dictionary<string, string> Expected = new Dictionary<string, string>
        {
            ["autoRecordOnLaunch"] = null,
            ["autoRecordOnEva"] = null,
            ["autoRecordOnFirstModificationAfterSwitch"] = null,
            ["autoMerge"] = null,
            ["verboseLogging"] = null,
            ["samplingDensity"] = null,
            ["ghostAudioVolume"] = null,
            ["transitedBodyRotationModeIndex"] = null,

            ["ghostRenderTracing"] = "RecordGhostRenderTracing",
            ["mapRenderTracing"] = "RecordMapRenderTracing",
            ["ledgerTracing"] = "RecordLedgerTracing",
            ["writeReadableSidecarMirrors"] = "RecordReadableSidecarMirrors",
            ["autoBackupExistingSaves"] = "RecordAutoBackupExistingSaves",
            ["showCommittedFutureOverlays"] = "RecordShowCommittedFutureOverlays",
            ["blockCommittedActions"] = "RecordBlockCommittedActions",
            ["showRouteLines"] = "RecordShowRouteLines",
        };

        private static string ValidValueFor(SettingValueType type)
        {
            switch (type)
            {
                case SettingValueType.Bool: return "true";
                case SettingValueType.Int: return "1";
                case SettingValueType.Float: return "0.5";
                default: return "true";
            }
        }

        [Fact]
        public void ApplySetting_AlwaysSetsLiveField_AndRoutesRecordOnlyForTrackedNames()
        {
            foreach (KeyValuePair<string, string> kv in Expected)
            {
                string name = kv.Key;
                string expectedRecord = kv.Value;

                // Produce the accepted decision through the real whitelist so the route +
                // Record* selector under test come from the production table, not the test.
                SettingApplyResult first = SettingWhitelist.TryApply(name, "true");
                if (!first.Accepted)
                    first = SettingWhitelist.TryApply(name, ValidValueFor(first.Type));
                Assert.True(first.Accepted, $"whitelist should accept a valid value for '{name}'");

                int liveSets = 0;
                var recordCalls = new List<string>();
                TestCommandSettingApplier.ApplySetting(
                    first,
                    r => liveSets++,
                    r => recordCalls.Add(r.RecordMethod));

                Assert.Equal(1, liveSets); // live field ALWAYS set

                if (expectedRecord == null)
                {
                    Assert.Equal(PersistenceRoute.GameParameters, first.Route);
                    Assert.Empty(recordCalls); // GameParameters-only: never routes to Record*
                }
                else
                {
                    Assert.Equal(PersistenceRoute.GameParametersPlusSidecar, first.Route);
                    Assert.Single(recordCalls);
                    Assert.Equal(expectedRecord, recordCalls[0]); // exact member name
                }
            }
        }

        [Fact]
        public void ApplySetting_TrackedName_RoutesSidecar_GameParamsName_DoesNot()
        {
            // Focused pair: one tracked, one untracked, spelling out the fix's contract.
            SettingApplyResult tracked = SettingWhitelist.TryApply("mapRenderTracing", "true");
            SettingApplyResult untracked = SettingWhitelist.TryApply("autoMerge", "true");

            var trackedRecord = new List<string>();
            TestCommandSettingApplier.ApplySetting(tracked, r => { }, r => trackedRecord.Add(r.RecordMethod));
            Assert.Equal(new[] { "RecordMapRenderTracing" }, trackedRecord);

            var untrackedRecord = new List<string>();
            TestCommandSettingApplier.ApplySetting(untracked, r => { }, r => untrackedRecord.Add(r.RecordMethod));
            Assert.Empty(untrackedRecord);
        }
    }
}
