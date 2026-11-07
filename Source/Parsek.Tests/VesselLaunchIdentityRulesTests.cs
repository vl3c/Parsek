using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// VesselLaunchIdentity reads the vessel-name link rows and hydrates a dropped end snapshot
    /// through two rules their owners install (<see cref="VesselLaunchIdentity.GuidLinkRule"/>,
    /// <see cref="VesselLaunchIdentity.SnapshotHydrateRule"/>), so the identity module does not
    /// reference the stores. These pin that the rules are installed by the owners and by the
    /// committed store, that each consult site answers what the direct store call answers, and
    /// that a missing link rule can only mean there are no link rows (the duplicate-respawn
    /// guard of a resumed spawned vessel depends on the rows being read).
    /// </summary>
    [Collection("Sequential")]
    public class VesselLaunchIdentityRulesTests : IDisposable
    {
        private const uint BakedPid = 4242u;
        private const string LaunchOneGuid = "11111111111111111111111111111111";
        private const string LaunchTwoGuid = "22222222222222222222222222222222";
        private const string SpawnGuid = "55555555555555555555555555555555";
        private const string MiddleGuid = "33333333333333333333333333333333";

        public VesselLaunchIdentityRulesTests()
        {
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            VesselNameStore.ResetForTesting();
            VesselNameStore.NowUtProviderForTesting = () => 1000.0;
        }

        public void Dispose()
        {
            VesselNameStore.EnsureLaunchIdentityLinkRuleInstalled();
            RecordingSidecarStore.EnsureLaunchIdentityHydrateRuleInstalled();
            VesselNameStore.ResetForTesting();
            RecordingStore.ResetForTesting();
            ParsekLog.ResetTestOverrides();
        }

        private static ConfigNode SnapshotOf(string guid)
        {
            var node = new ConfigNode("VESSEL");
            node.AddValue("pid", guid);
            node.AddValue("persistentId", BakedPid.ToString(System.Globalization.CultureInfo.InvariantCulture));
            return node;
        }

        // An adoption-shaped stamp (spawned pid == recorded pid) of launch one.
        private static Recording AdoptedLaunchOne(ConfigNode snapshot = null)
        {
            return new Recording
            {
                RecordingId = "rec_rules",
                TreeId = "tree_rules",
                VesselName = "Kerbal X",
                VesselPersistentId = BakedPid,
                RecordedVesselGuid = LaunchOneGuid,
                VesselSpawned = true,
                SpawnedVesselPersistentId = BakedPid,
                VesselSnapshot = snapshot,
            };
        }

        [Fact]
        public void Rules_AreInstalledByTheirOwnersAndByTheCommittedStore()
        {
            Assert.True(RecordingStore.LaunchIdentityRulesInstalledForTesting);
            Assert.True(VesselNameStore.LaunchIdentityLinkRuleInstalledForTesting);
            Assert.True(RecordingSidecarStore.LaunchIdentityHydrateRuleInstalledForTesting);

            Assert.Equal(
                new Func<string, string, bool>(VesselNameStore.GuidContinuesIdentity),
                VesselLaunchIdentity.GuidLinkRule);
            Assert.Equal(
                new Func<Recording, bool>(RecordingSidecarStore.TryHydrateVesselSnapshotFromSidecar),
                VesselLaunchIdentity.SnapshotHydrateRule);
        }

        [Fact]
        public void Installers_AreIdempotentAndRestoreANulledRule()
        {
            try
            {
                VesselLaunchIdentity.GuidLinkRule = null;
                VesselLaunchIdentity.SnapshotHydrateRule = null;

                Assert.True(VesselNameStore.EnsureLaunchIdentityLinkRuleInstalled());
                Assert.True(RecordingSidecarStore.EnsureLaunchIdentityHydrateRuleInstalled());
                Assert.True(VesselNameStore.EnsureLaunchIdentityLinkRuleInstalled());

                Assert.Equal(
                    new Func<string, string, bool>(VesselNameStore.GuidContinuesIdentity),
                    VesselLaunchIdentity.GuidLinkRule);
                Assert.Equal(
                    new Func<Recording, bool>(RecordingSidecarStore.TryHydrateVesselSnapshotFromSidecar),
                    VesselLaunchIdentity.SnapshotHydrateRule);
            }
            finally
            {
                VesselNameStore.EnsureLaunchIdentityLinkRuleInstalled();
                RecordingSidecarStore.EnsureLaunchIdentityHydrateRuleInstalled();
            }
        }

        [Theory]
        [InlineData(LaunchOneGuid, LaunchOneGuid)]
        [InlineData(LaunchOneGuid, null)]
        [InlineData(null, SpawnGuid)]
        [InlineData(LaunchOneGuid, SpawnGuid)]
        [InlineData(LaunchOneGuid, LaunchTwoGuid)]
        [InlineData(LaunchOneGuid, MiddleGuid)]
        public void LiveGuidIsRecordedLaunchOrLinked_AnswersTheDirectStoreAnswer(string recorded, string live)
        {
            Assert.True(VesselNameStore.AddLink(SpawnGuid, MiddleGuid, VesselIdentityLinkKind.Resume, "test"));
            Assert.True(VesselNameStore.AddLink(MiddleGuid, LaunchOneGuid, VesselIdentityLinkKind.Spawn, "test"));

            bool expected = !VesselLaunchIdentity.GuidsConclusivelyDiffer(recorded, live)
                || VesselNameStore.GuidContinuesIdentity(live, recorded);
            Assert.Equal(expected, VesselLaunchIdentity.LiveGuidIsRecordedLaunchOrLinked(recorded, live));
        }

        [Fact]
        public void LiveGuidIsRecordedLaunchOrLinked_LinkedSpawnTrue_UnlinkedRelaunchFalse()
        {
            Assert.False(VesselLaunchIdentity.LiveGuidIsRecordedLaunchOrLinked(LaunchOneGuid, SpawnGuid));
            Assert.True(VesselNameStore.AddLink(SpawnGuid, LaunchOneGuid, VesselIdentityLinkKind.Spawn, "test"));
            Assert.True(VesselLaunchIdentity.LiveGuidIsRecordedLaunchOrLinked(LaunchOneGuid, SpawnGuid));
            Assert.False(VesselLaunchIdentity.LiveGuidIsRecordedLaunchOrLinked(LaunchOneGuid, LaunchTwoGuid));
        }

        [Fact]
        public void SpawnClaimStillHeld_LinkRowKeepsTheClaimAgainstAKnownOtherSnapshotGuid()
        {
            // A resumed spawned vessel: the live vessel carries the spawn's fresh guid and the
            // end snapshot names launch one. Without the link row the claim is dropped (and the
            // destructive readers would respawn); with it the claim is kept.
            Recording rec = AdoptedLaunchOne();
            Func<Recording, string> snapshotGuid = r => LaunchOneGuid;

            Assert.False(VesselLaunchIdentity.SpawnClaimStillHeld(rec, BakedPid, SpawnGuid, snapshotGuid));
            Assert.False(VesselLaunchIdentity.SpawnStampHeldBy(rec, BakedPid, SpawnGuid));

            Assert.True(VesselNameStore.AddLink(SpawnGuid, LaunchOneGuid, VesselIdentityLinkKind.Spawn, "test"));

            Assert.True(VesselLaunchIdentity.SpawnStampHeldBy(rec, BakedPid, SpawnGuid));
            Assert.True(VesselLaunchIdentity.SpawnClaimStillHeld(rec, BakedPid, SpawnGuid, snapshotGuid));
            // An unrelated relaunch stays refused.
            Assert.False(VesselLaunchIdentity.SpawnClaimStillHeld(rec, BakedPid, LaunchTwoGuid, snapshotGuid));
        }

        [Fact]
        public void LinkRule_NullReadsUnlinked_WhichIsTheStoreAnswerWithNoRows()
        {
            // The rule is installed from the store's own type initialiser and the rows live in
            // that store's static fields, so a row cannot exist while the rule is missing. The
            // only state a null rule can meet is "no rows", where both answer "not linked".
            Recording rec = AdoptedLaunchOne();
            Func<Recording, string> snapshotGuid = r => LaunchOneGuid;
            Assert.Equal(0, VesselNameStore.LinkCount);
            bool installedHeld = VesselLaunchIdentity.SpawnClaimStillHeld(rec, BakedPid, SpawnGuid, snapshotGuid);
            bool installedLinked = VesselLaunchIdentity.LiveGuidIsRecordedLaunchOrLinked(LaunchOneGuid, SpawnGuid);
            try
            {
                VesselLaunchIdentity.GuidLinkRule = null;
                Assert.Equal(installedHeld, VesselLaunchIdentity.SpawnClaimStillHeld(rec, BakedPid, SpawnGuid, snapshotGuid));
                Assert.Equal(installedLinked, VesselLaunchIdentity.LiveGuidIsRecordedLaunchOrLinked(LaunchOneGuid, SpawnGuid));
                Assert.False(VesselNameStore.GuidContinuesIdentity(SpawnGuid, LaunchOneGuid));
            }
            finally
            {
                VesselNameStore.EnsureLaunchIdentityLinkRuleInstalled();
            }

            // Adding a row goes through the store, whose initialiser already installed the rule.
            Assert.True(VesselNameStore.AddLink(SpawnGuid, LaunchOneGuid, VesselIdentityLinkKind.Spawn, "test"));
            Assert.NotNull(VesselLaunchIdentity.GuidLinkRule);
            Assert.True(VesselLaunchIdentity.SpawnClaimStillHeld(rec, BakedPid, SpawnGuid, snapshotGuid));
        }

        [Fact]
        public void ReadRecordingSnapshotGuid_HydratesOnlyADroppedSnapshot_ThroughTheRule()
        {
            var calls = new List<string>();
            try
            {
                VesselLaunchIdentity.SnapshotHydrateRule = r =>
                {
                    calls.Add(r.RecordingId);
                    r.VesselSnapshot = SnapshotOf(LaunchTwoGuid);
                    return true;
                };

                // An in-memory snapshot is read as is; the rule is not called.
                Assert.Equal(LaunchOneGuid,
                    VesselLaunchIdentity.ReadRecordingSnapshotGuid(AdoptedLaunchOne(SnapshotOf(LaunchOneGuid))));
                Assert.Empty(calls);

                // A dropped snapshot is hydrated through the rule, then read.
                Recording dropped = AdoptedLaunchOne();
                Assert.Equal(LaunchTwoGuid, VesselLaunchIdentity.ReadRecordingSnapshotGuid(dropped));
                Assert.Equal(new[] { "rec_rules" }, calls);

                // A rule that throws reads as unknown, as a throwing sidecar read did.
                VesselLaunchIdentity.SnapshotHydrateRule = r => throw new InvalidOperationException("boom");
                Assert.Null(VesselLaunchIdentity.ReadRecordingSnapshotGuid(AdoptedLaunchOne()));

                // No rule hydrates nothing: the guid reads unknown, which keeps a spawn claim.
                VesselLaunchIdentity.SnapshotHydrateRule = null;
                Assert.Null(VesselLaunchIdentity.ReadRecordingSnapshotGuid(AdoptedLaunchOne()));
                Assert.True(VesselLaunchIdentity.SpawnClaimStillHeldCore(
                    AdoptedLaunchOne(), BakedPid, SpawnGuid, false, null));
            }
            finally
            {
                RecordingSidecarStore.EnsureLaunchIdentityHydrateRuleInstalled();
            }
        }

        [Fact]
        public void ReadRecordingSnapshotGuid_InstalledRule_HydratesTheSidecarLikeTheDirectCall()
        {
            string tempSaveRoot = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "parsek-launch-identity-rules-" + Guid.NewGuid().ToString("N"));
            try
            {
                RecordingPaths.SaveRootOverrideForTesting = tempSaveRoot;

                // No sidecar: the rule and the direct call both leave the snapshot null.
                Recording missingViaRule = AdoptedLaunchOne();
                Recording missingDirect = AdoptedLaunchOne();
                Assert.Null(VesselLaunchIdentity.ReadRecordingSnapshotGuid(missingViaRule));
                Assert.False(RecordingSidecarStore.TryHydrateVesselSnapshotFromSidecar(missingDirect));
                Assert.Null(missingViaRule.VesselSnapshot);
                Assert.Null(missingDirect.VesselSnapshot);

                // A sidecar on disk: the rule hydrates it and the guid is read from it.
                string path = RecordingPaths.ResolveSaveScopedPath(
                    RecordingPaths.BuildVesselSnapshotRelativePath("rec_rules"));
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path));
                RecordingSidecarStore.WriteSnapshotSidecarForTesting(path, SnapshotOf(LaunchTwoGuid));

                Recording viaRule = AdoptedLaunchOne();
                Recording direct = AdoptedLaunchOne();
                Assert.Equal(LaunchTwoGuid, VesselLaunchIdentity.ReadRecordingSnapshotGuid(viaRule));
                Assert.True(RecordingSidecarStore.TryHydrateVesselSnapshotFromSidecar(direct));
                Assert.Equal(VesselLaunchIdentity.TryReadVesselGuid(direct.VesselSnapshot),
                    VesselLaunchIdentity.TryReadVesselGuid(viaRule.VesselSnapshot));

                // The destructive readers' default reader goes through the rule: the hydrated
                // guid is another launch, so with no link row the adoption claim is dropped.
                Assert.False(VesselLaunchIdentity.SpawnClaimStillHeld(AdoptedLaunchOne(), BakedPid, SpawnGuid));
            }
            finally
            {
                RecordingPaths.SaveRootOverrideForTesting = null;
                try
                {
                    if (System.IO.Directory.Exists(tempSaveRoot))
                        System.IO.Directory.Delete(tempSaveRoot, true);
                }
                catch (System.IO.IOException) { }
            }
        }
    }
}
