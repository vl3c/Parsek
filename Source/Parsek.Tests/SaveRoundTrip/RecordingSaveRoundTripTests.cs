using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Parsek.Tests.SaveRoundTrip
{
    /// <summary>
    /// The field contracts of the recording metadata a RECORDING node in the .sfs carries,
    /// shared by the per-recording round-trip and the tree round-trip. Every field of
    /// <see cref="Recording"/> not listed here must survive
    /// <c>RecordingTreeRecordCodec.SaveRecordingInto</c> + <c>LoadRecordingFrom</c>.
    /// </summary>
    internal static class RecordingMetadataSpecs
    {
        private const string TrajectorySidecar =
            "trajectory sidecar, covered by TrajectorySidecarRoundTripTests";
        private const string SnapshotSidecar =
            "vessel / ghost snapshot sidecar (_vessel.craft / _ghost.craft), not the RECORDING node";
        private const string HydrationState =
            "runtime sidecar-hydration state set by LoadRecordingFiles for this session";
        private const string DisplayCache =
            "runtime memo keyed by a content signature, rebuilt on first read";
        private const string SpawnRetryState =
            "per-session spawn retry / give-up bookkeeping, deliberately reset so a reload retries the spawn";
        private const string SpawnSafetyVerdict =
            "terminal-orbit spawn-safety verdict detail, re-derived from the terminal orbit on the next spawn "
            + "attempt (only the durable refusal flag + reason code persist)";

        /// <summary>16 lower-case hex characters: the only shape the sidecar-hash reader accepts.</summary>
        private static int hashCounter;
        private static string NextHash() =>
            (0x1000000000000000UL + (ulong)System.Threading.Interlocked.Increment(ref hashCounter))
                .ToString("x16", System.Globalization.CultureInfo.InvariantCulture);

        internal static FieldSpec Recording() => new FieldSpec(typeof(Recording))
            .NotPersisted(TrajectorySidecar,
                "Points", "OrbitSegments", "PartEvents", "FlagEvents", "SegmentEvents", "TrackSections")
            .NotPersisted(SnapshotSidecar, "VesselSnapshot", "GhostVisualSnapshot")
            .NotPersisted("index into the committed list, recomputed by every RecordingOptimizer pass",
                "LoopSyncParentIdx")
            .NotPersisted("dirty flag for the next OnSave sidecar write", "FilesDirty")
            .NotPersisted("process-lifetime flag, see its declaration: lost on quit by design",
                "SidecarEpochAdvancePending")
            .NotPersisted(HydrationState,
                "SidecarVersionsResolved", "SidecarLoadFailed", "SidecarLoadFailureReason",
                "LoadedTrajectorySchemaGeneration", "LoadedTrajectoryMagicTag",
                "LoadedTrajectorySchemaCompatible", "LoadedTrajectorySchemaFailureReason",
                "VesselSnapshotHydrationFailed")
            .NotPersisted(DisplayCache,
                "SegmentBodyDisplayLabelCacheValid", "SegmentBodyDisplayLabelCache",
                "SegmentBodyDisplayLabelCachePointCount", "SegmentBodyDisplayLabelCacheTrackSectionCount",
                "SegmentBodyDisplayLabelCacheSegmentBodyName", "SegmentBodyDisplayLabelCacheStartBodyName",
                "SegmentBodyDisplayLabelCacheLastPointBodyName",
                "BodyTransitionSequenceCache", "BodyTransitionSequenceCachePointCount",
                "BodyTransitionSequenceCacheTrackSectionCount", "BodyTransitionSequenceCacheSegmentBodyName",
                "BodyTransitionSequenceCacheStartBodyName", "BodyTransitionSequenceCacheLastPointBodyName",
                "cachedPreReFlyAnchorRecordingSessionId", "cachedPreReFlyAnchorRecording",
                "CachedStats", "CachedStatsPointCount")
            .NotPersisted("one-frame marker for the finalize that follows a committed-copy repair",
                "RestoredFromCommittedTreeThisFrame")
            .NotPersisted("marks a transient serializer-only Recording, never a stored one",
                "IsDetachedSyntheticSnapshot")
            .NotPersisted(SpawnRetryState,
                "SpawnAttempts", "CollisionBlockCount", "SpawnAbandoned", "WalkbackExhausted",
                "DuplicateBlockerRecovered", "SpawnDeathCount", "TerminalSpawnSafetyDeferred")
            .NotPersisted(SpawnSafetyVerdict,
                "TerminalSpawnSafetyReason", "TerminalSpawnSafetyDecisionUT", "TerminalSpawnNextAttemptUT",
                "TerminalSpawnSafetyAltitude", "TerminalSpawnSafetySafeAltitude",
                "TerminalSpawnSafetyPeriapsisAltitude", "TerminalSpawnSafetyApoapsisAltitude",
                "TerminalSpawnSafetyPressure")
            .NotPersisted("stop-time diagnostic distance, read only by log lines (MaxDistanceFromLaunch is the "
                + "persisted one)", "DistanceFromLaunch")
            .NotPersisted("scene-exit diagnostic, no decision reads it", "SceneExitSituation")
            .Derived("not written; re-derived on load as SpawnedVesselPersistentId != 0", "VesselSpawned")
            // The reader keeps a CannotSpawnSafely flag only for the durable spawn-death reason;
            // any other reason is dropped on load for re-evaluation (TerminalOrbitSpawnSafety.IsDurableRefusal).
            .Fill("TerminalSpawnSafetyReasonCode", () => TerminalOrbitSpawnSafety.ReasonSpawnedVesselDied)
            .Fill("PrecHash", () => NextHash())
            .Fill("VesselSnapshotHash", () => NextHash())
            .Fill("GhostSnapshotHash", () => NextHash())
            .Fill("cachedPreReFlyAnchorRecording", () => null)
            .Then(o =>
            {
                var rec = (Recording)o;
                // SupersedeTargetId is kept on load only on a NotCommitted provisional.
                rec.MergeState = MergeState.NotCommitted;
                // The PRE_REFLY_ANCHOR node is written only for the snapshot's own session.
                rec.PreReFlyAnchorSessionId = "anchor-session-" + rec.RecordingId;
                CanonicalizeRouteProof(rec);
            });

        internal static FieldSpec StartDockedSeamHalf() => new FieldSpec(typeof(StartDockedSeamHalf))
            .NotPersisted("capture-time working data consumed by the bind in the same session "
                + "(RouteProofCodec.SerializeOriginProof)",
                "PartPersistentIds", "StartResources", "StartInventory");

        // The reader recomputes each item's kind key from its STOREDPART snapshot, merges
        // items of one kind and sorts by kind (VesselSnapshotOps.NormalizeLoadedInventoryPayloadItems),
        // so a filled item carries a distinct part name and its own kind key, and every filled
        // list is put in kind order (CanonicalizeInventory) before it is saved.
        internal static FieldSpec InventoryPayloadItem() => new FieldSpec(typeof(InventoryPayloadItem))
            .Fill("StoredPartSnapshot", () =>
            {
                var node = new ConfigNode("STOREDPART");
                node.AddValue("partName", "storedPart" + System.Threading.Interlocked.Increment(ref hashCounter));
                return node;
            })
            .Then(o =>
            {
                var item = (InventoryPayloadItem)o;
                item.IdentityHash = VesselSnapshotOps.ComputeInventoryPayloadKindKey(item.StoredPartSnapshot);
            });

        internal static void CanonicalizeInventory(List<InventoryPayloadItem> items)
        {
            items?.Sort((a, b) => string.CompareOrdinal(a?.IdentityHash ?? "", b?.IdentityHash ?? ""));
        }

        internal static void CanonicalizeRouteProof(Recording rec)
        {
            if (rec.RouteOriginProof != null)
            {
                CanonicalizeInventory(rec.RouteOriginProof.StartTransportInventory);
                CanonicalizeInventory(rec.RouteOriginProof.EndTransportInventory);
            }
            if (rec.RouteConnectionWindows != null)
            {
                foreach (RouteConnectionWindow w in rec.RouteConnectionWindows)
                {
                    if (w == null) continue;
                    CanonicalizeInventory(w.DockTransportInventory);
                    CanonicalizeInventory(w.UndockTransportInventory);
                    CanonicalizeInventory(w.DockEndpointInventory);
                    CanonicalizeInventory(w.UndockEndpointInventory);
                }
            }
        }

        /// <summary>
        /// The PRE_REFLY_ANCHOR lists are encoded by TrajectoryTextSidecarCodec, whose element
        /// layout (and its constraints, such as checkpoints only on OrbitalCheckpoint sections)
        /// is covered by the trajectory sidecar round-trip. Passing the written lists through
        /// that codec on their own isolates what this suite owns: that the RECORDING node carries
        /// the anchor lists to the codec and back.
        /// </summary>
        internal static void NormalizeAnchorThroughTrajectoryCodec(Recording rec)
        {
            var temp = new Recording
            {
                Points = rec.PreReFlyAnchorPoints ?? new List<TrajectoryPoint>(),
                OrbitSegments = rec.PreReFlyAnchorOrbitSegments ?? new List<OrbitSegment>(),
                TrackSections = rec.PreReFlyAnchorTrackSections ?? new List<TrackSection>(),
            };
            var node = new ConfigNode("PRE_REFLY_ANCHOR");
            TrajectoryTextSidecarCodec.SerializeTrajectoryInto(node, temp);
            var back = new Recording { RecordingFormatVersion = rec.RecordingFormatVersion };
            TrajectoryTextSidecarCodec.DeserializeTrajectoryFrom(node, back);
            rec.PreReFlyAnchorPoints = back.Points;
            rec.PreReFlyAnchorOrbitSegments = back.OrbitSegments;
            rec.PreReFlyAnchorTrackSections = back.TrackSections;
        }

        internal static FieldSpec[] All() => new[]
        {
            Recording(), StartDockedSeamHalf(), InventoryPayloadItem(),
        };
    }

    /// <summary>
    /// Field-level save/load round-trip of a <see cref="Recording"/>'s RECORDING node metadata
    /// (the codec, its manifest and route-proof sub-codecs, the RecordingStore forwarders and
    /// <see cref="SurfacePosition"/>). Runs under a comma-decimal culture so a culture-sensitive
    /// number format reds too.
    /// </summary>
    [Collection("Sequential")]
    [SaveRoundTripCovers(typeof(RecordingTreeRecordCodec))]
    [SaveRoundTripCovers(typeof(RecordingManifestCodec))]
    [SaveRoundTripCovers(typeof(RouteProofCodec))]
    [SaveRoundTripCovers(typeof(RecordingStore))]
    [SaveRoundTripCovers(typeof(SurfacePosition))]
    public class RecordingSaveRoundTripTests : IDisposable
    {
        public RecordingSaveRoundTripTests()
        {
            ParsekLog.SuppressLogging = true;
            RecordingStore.SuppressLogging = true;
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            RecordingStore.SuppressLogging = false;
            RecordingStore.ResetForTesting();
        }

        private static Recording SaveAndLoad(Recording written, out ConfigNode node)
        {
            node = new ConfigNode("RECORDING");
            RecordingTreeRecordCodec.SaveRecordingInto(node, written);
            var read = new Recording();
            RecordingTreeRecordCodec.LoadRecordingFrom(node, read);
            return read;
        }

        [Fact]
        public void Recording_EveryMetadataFieldSurvivesSaveAndLoad()
        {
            FieldRoundTrip.UnderCommaCulture(() =>
            {
                var rt = new FieldRoundTrip(RecordingMetadataSpecs.All());
                var written = rt.Build<Recording>();
                Recording read = SaveAndLoad(written, out _);
                RecordingMetadataSpecs.NormalizeAnchorThroughTrajectoryCodec(written);

                var problems = rt.Diff(written, read, "Recording");
                Assert.True(problems.Count == 0, FieldRoundTrip.Report("Recording", problems));
            });
        }

        [Fact]
        public void Recording_SchemaStampsAreTheCurrentOnes()
        {
            var rt = new FieldRoundTrip(RecordingMetadataSpecs.All());
            var written = rt.Build<Recording>();
            Recording read = SaveAndLoad(written, out _);
            Assert.Equal(RecordingStore.CurrentRecordingFormatVersion, read.RecordingFormatVersion);
            Assert.Equal(RecordingStore.CurrentRecordingSchemaGeneration, read.RecordingSchemaGeneration);
        }

        [Fact]
        public void RecordingStoreForwarders_RoundTripTheirSubset()
        {
            // The RecordingStore Serialize*/Deserialize* helpers forward the manifests and the
            // route proof to their codecs; drive them directly, in the order the RECORDING node
            // writes them, and diff exactly the fields they own.
            string[] owned =
            {
                "CrewEndStates", "StartResources", "EndResources", "StartInventory", "EndInventory",
                "StartCrew", "EndCrew", "TransferTargetVesselPid", "TransferKind",
                "RouteConnectionWindows", "RouteOriginProof", "RouteRunManifest", "RunManifestVoided",
                "RouteHarvestWindows",
            };
            FieldRoundTrip.UnderCommaCulture(() =>
            {
                var rt = new FieldRoundTrip(RecordingMetadataSpecs.All());
                var written = rt.Build<Recording>();
                var node = new ConfigNode("RECORDING");
                RecordingStore.SerializeCrewEndStates(node, written);
                RecordingStore.SerializeResourceManifest(node, written);
                RecordingStore.SerializeInventoryManifest(node, written);
                RecordingStore.SerializeCrewManifest(node, written);
                RecordingStore.SerializeRouteProofMetadata(node, written);

                var read = new Recording();
                RecordingStore.DeserializeCrewEndStates(node, read);
                RecordingStore.DeserializeResourceManifest(node, read);
                RecordingStore.DeserializeInventoryManifest(node, read);
                RecordingStore.DeserializeCrewManifest(node, read);
                RecordingStore.DeserializeRouteProofMetadata(node, read);

                var problems = new List<string>();
                foreach (var f in FieldRoundTrip.GetFields(typeof(Recording)))
                {
                    string name = FieldRoundTrip.LogicalName(f);
                    if (owned.Contains(name))
                        problems.AddRange(rt.Diff(f.GetValue(written), f.GetValue(read), "Recording." + name));
                }
                Assert.True(problems.Count == 0, FieldRoundTrip.Report("RecordingStore forwarders", problems));
            });
        }

        [Fact]
        public void SurfacePosition_EveryFieldSurvivesSaveAndLoad()
        {
            FieldRoundTrip.UnderCommaCulture(() =>
            {
                var rt = new FieldRoundTrip(new FieldSpec[0]);
                var written = rt.Build<SurfacePosition>();
                var node = new ConfigNode("SURFACE_POSITION");
                SurfacePosition.SaveInto(node, written);
                SurfacePosition read = SurfacePosition.LoadFrom(node);

                var problems = rt.Diff(written, read, "SurfacePosition");
                Assert.True(problems.Count == 0, FieldRoundTrip.Report("SurfacePosition", problems));
            });
        }

        [Fact]
        public void RecordingSpec_ListsOnlyRealFields()
        {
            // FieldSpec construction throws on a name that is not a field; building every spec
            // proves no entry went stale after a rename.
            Assert.NotEmpty(RecordingMetadataSpecs.All());
        }

        [Fact]
        public void Harness_DetectsARecordingFieldTheCodecDrops()
        {
            // Proves the gate can fail: a RECORDING node missing a key the codec writes must
            // report that field.
            var rt = new FieldRoundTrip(RecordingMetadataSpecs.All());
            var written = rt.Build<Recording>();
            var node = new ConfigNode("RECORDING");
            RecordingTreeRecordCodec.SaveRecordingInto(node, written);
            Assert.True(node.HasValue("evaCrewName"));
            node.RemoveValue("evaCrewName");
            var read = new Recording();
            RecordingTreeRecordCodec.LoadRecordingFrom(node, read);

            var problems = rt.Diff(written, read, "Recording");
            Assert.Contains(problems, p => p.StartsWith("Recording.EvaCrewName:", StringComparison.Ordinal));
        }
    }
}
