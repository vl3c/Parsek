using System;
using System.Collections.Generic;
using System.IO;
using Parsek;
using Parsek.Rendering;
using UnityEngine;
using Xunit;

namespace Parsek.Tests.Rendering
{
    /// <summary>
    /// Phase 5 round-trip + per-trace validation tests for the
    /// <c>CoBubbleOffsetTraces</c> block in the <c>.pann</c> binary
    /// (design doc §17.3.1, §10, §20.5 Phase 5 row).
    /// </summary>
    [Collection("Sequential")]
    public class CoBubbleSidecarRoundTripTests : IDisposable
    {
        private readonly string tempDir;
        private readonly List<string> logLines = new List<string>();

        public CoBubbleSidecarRoundTripTests()
        {
            tempDir = Path.Combine(Path.GetTempPath(),
                "parsek_pann_cobubble_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(tempDir);
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;
            SmoothingPipeline.ResetForTesting();
            SmoothingPipeline.UseCoBubbleBlendResolverForTesting = () => true;
        }

        public void Dispose()
        {
            SmoothingPipeline.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        private static CoBubbleOffsetTrace MakeTrace(string peerId, int sampleCount = 4,
            byte frameTag = 0, double startUT = 100.0, double endUT = 110.0,
            string bodyName = "Kerbin")
        {
            var uts = new double[sampleCount];
            var dx = new float[sampleCount];
            var dy = new float[sampleCount];
            var dz = new float[sampleCount];
            double step = (endUT - startUT) / Math.Max(1, sampleCount - 1);
            for (int i = 0; i < sampleCount; i++)
            {
                uts[i] = startUT + i * step;
                dx[i] = 1.0f + i;
                dy[i] = 2.0f + i;
                dz[i] = 3.0f + i;
            }
            byte[] sig = new byte[32];
            for (int i = 0; i < 32; i++) sig[i] = (byte)(i + peerId.Length);
            return new CoBubbleOffsetTrace
            {
                PeerRecordingId = peerId,
                PeerSourceFormatVersion = 8,
                PeerSidecarEpoch = 3,
                PeerContentSignature = sig,
                StartUT = startUT,
                EndUT = endUT,
                FrameTag = frameTag,
                PrimaryDesignation = 0,
                UTs = uts,
                Dx = dx,
                Dy = dy,
                Dz = dz,
                BodyName = bodyName,
            };
        }

        [Fact]
        public void Write_Read_RoundTrip_PreservesAllTraceFields()
        {
            // What makes it fail: any silent mutation of trace fields on
            // save/load would feed the blender a different offset from the
            // one detected at commit (HR-3).
            string path = Path.Combine(tempDir, "rec.pann");
            byte[] hash = PannotationsSidecarBinary.ComputeConfigurationHash(SmoothingConfiguration.Default);

            var traces = new List<CoBubbleOffsetTrace>
            {
                MakeTrace("peer-A", sampleCount: 5, frameTag: 0, startUT: 100.0, endUT: 120.0),
                MakeTrace("peer-B", sampleCount: 3, frameTag: 1, startUT: 200.0, endUT: 215.0),
                MakeTrace("peer-C", sampleCount: 2, frameTag: 0, startUT: 300.0, endUT: 305.0),
            };

            PannotationsSidecarBinary.Write(path, "rec-cobubble", 1, 8, hash,
                splines: new List<KeyValuePair<int, SmoothingSpline>>(),
                anchorCandidates: null,
                coBubbleTraces: traces);

            Assert.True(PannotationsSidecarBinary.TryProbe(path, out var probe));
            Assert.True(probe.Success);
            Assert.True(probe.Supported);

            Assert.True(PannotationsSidecarBinary.TryRead(path, probe,
                out var splines, out var cands, out var read, out string failure));
            Assert.Null(failure);
            Assert.Equal(traces.Count, read.Count);
            for (int i = 0; i < traces.Count; i++)
            {
                Assert.Equal(traces[i].PeerRecordingId, read[i].PeerRecordingId);
                Assert.Equal(traces[i].PeerSourceFormatVersion, read[i].PeerSourceFormatVersion);
                Assert.Equal(traces[i].PeerSidecarEpoch, read[i].PeerSidecarEpoch);
                Assert.Equal(traces[i].PeerContentSignature, read[i].PeerContentSignature);
                Assert.Equal(traces[i].StartUT, read[i].StartUT);
                Assert.Equal(traces[i].EndUT, read[i].EndUT);
                Assert.Equal(traces[i].FrameTag, read[i].FrameTag);
                Assert.Equal(traces[i].PrimaryDesignation, read[i].PrimaryDesignation);
                Assert.Equal(traces[i].UTs, read[i].UTs);
                Assert.Equal(traces[i].Dx, read[i].Dx);
                Assert.Equal(traces[i].Dy, read[i].Dy);
                Assert.Equal(traces[i].Dz, read[i].Dz);
                // P1-A: BodyName must round-trip so the runtime body
                // resolver can drive the FrameTag=1 inertial→world lower.
                Assert.Equal(traces[i].BodyName, read[i].BodyName);
            }
        }

        [Fact]
        public void AlgStampVersion_BumpedToSixOrLater_ForBodyNameField()
        {
            // P1-A: the BodyName field is a new on-disk schema element. v5
            // .pann files lack it; the runtime can't lower their FrameTag=1
            // offsets correctly. Bumping the alg stamp invalidates them via
            // the existing alg-stamp-drift gate so they get recomputed on
            // first load (HR-10).
            // Phase 5 review-pass-2 (P1-B + P2-B) further bumped to 7.
            Assert.True(PannotationsSidecarBinary.AlgorithmStampVersion >= 6,
                "AlgorithmStampVersion must be >= 6 after Phase 5 P1-A ships");
        }

        [Fact]
        public void AlgStampVersion_BumpedToSevenOrLater_ForRecomputeAndWindowClampFix()
        {
            // Phase 5 review-pass-2: P1-B added CoBubbleOverlapDetector.DetectAndStore
            // to the lazy recompute path so cache-miss / drifted .pann files
            // regenerate traces instead of rewriting empty blocks. P2-B
            // clamps BuildTrace to BlendMaxWindowSeconds so long-overlap
            // traces no longer store EndUTs without sample coverage. v6
            // .pann files written before these fixes have semantically
            // incorrect trace content; the alg-stamp bump drives them
            // through alg-stamp-drift on first load (HR-10).
            // Phase 5 review-pass-5 further bumped to 8.
            Assert.True(PannotationsSidecarBinary.AlgorithmStampVersion >= 7,
                "AlgorithmStampVersion must be >= 7 after Phase 5 P1-B + P2-B ship");
        }

        [Fact]
        public void AlgStampVersion_BumpedToEightOrLater_ForOffsetSignFix()
        {
            // Phase 5 review-pass-5 P1: DetectAndStore was emitting both
            // stored sides of every overlap pair with reversed-sign
            // offsets. CloneTraceWithPeer's flip condition was exactly
            // inverted; both stored sides ended up with offset = primary -
            // owner instead of owner - primary, so peer ghosts rendered
            // on the opposite side of the primary at the offset's
            // distance. v7 .pann files have wrong-sign offsets; bumping
            // the alg stamp drives them through alg-stamp-drift on
            // first load so they get recomputed with the correct sign.
            Assert.True(PannotationsSidecarBinary.AlgorithmStampVersion >= 8,
                "AlgorithmStampVersion must be >= 8 after Phase 5 review-pass-5 P1 (offset-sign fix) ships");
        }

        [Fact]
        public void Write_Read_NullBodyName_RoundTripsAsNull()
        {
            // Empty / null bodyName must round-trip through the on-disk
            // length-prefixed string. The reader normalises empty back to
            // null so the blender's null-body branch fires consistently.
            string path = Path.Combine(tempDir, "rec_null_body.pann");
            byte[] hash = PannotationsSidecarBinary.ComputeConfigurationHash(SmoothingConfiguration.Default);
            CoBubbleOffsetTrace trace = MakeTrace("peer", bodyName: null);
            PannotationsSidecarBinary.Write(path, "rec-nobody", 1, 8, hash,
                splines: new List<KeyValuePair<int, SmoothingSpline>>(),
                anchorCandidates: null,
                coBubbleTraces: new List<CoBubbleOffsetTrace> { trace });

            Assert.True(PannotationsSidecarBinary.TryProbe(path, out var probe));
            Assert.True(PannotationsSidecarBinary.TryRead(path, probe,
                out _, out _, out var read, out _));
            Assert.Single(read);
            Assert.Null(read[0].BodyName);
        }

        [Fact]
        public void EmptyCoBubbleList_RoundTripsAsEmpty()
        {
            // The writer must still emit a valid count=0 header; the
            // reader produces a non-null empty list so callers can
            // distinguish "loaded but empty" from "field missing".
            string path = Path.Combine(tempDir, "rec_empty.pann");
            byte[] hash = PannotationsSidecarBinary.ComputeConfigurationHash(SmoothingConfiguration.Default);
            PannotationsSidecarBinary.Write(path, "rec-emp", 1, 8, hash,
                splines: new List<KeyValuePair<int, SmoothingSpline>>());
            Assert.True(PannotationsSidecarBinary.TryProbe(path, out var probe));
            Assert.True(PannotationsSidecarBinary.TryRead(path, probe,
                out _, out _, out var coBubble, out _));
            Assert.NotNull(coBubble);
            Assert.Empty(coBubble);
        }

        [Fact]
        public void AlgStampVersion_IsAtLeastFive_AfterPhaseFiveShips()
        {
            // The alg-stamp bump (4 -> 5) is the mechanism that invalidates
            // pre-Phase-5 .pann files (those have CoBubbleOffsetTraces always
            // empty). The drift logic itself lives in SmoothingPipeline; this
            // test pins the constant so a future refactor can't silently
            // revert it.
            Assert.True(PannotationsSidecarBinary.AlgorithmStampVersion >= 5,
                "AlgorithmStampVersion must be >= 5 after Phase 5 ships");
        }

        [Fact]
        public void ClassifyTraceDrift_PeerMissing_ReturnsReason()
        {
            // What makes it fail: a peer that was deleted between commit and
            // load must drop the trace, not silently install stale offsets.
            SmoothingPipeline.CoBubblePeerResolverForTesting = id => null;
            CoBubbleOffsetTrace trace = MakeTrace("missing-peer");
            string reason = SmoothingPipeline.ClassifyTraceDrift(trace);
            Assert.Equal("peer-missing", reason);
        }

        [Fact]
        public void ClassifyTraceDrift_PeerFormatChanged_ReturnsReason()
        {
            CoBubbleOffsetTrace trace = MakeTrace("peer");
            trace.PeerSourceFormatVersion = 8;
            SmoothingPipeline.CoBubblePeerResolverForTesting = id =>
                new Recording { RecordingId = id, RecordingFormatVersion = 7, SidecarEpoch = trace.PeerSidecarEpoch };
            Assert.Equal("peer-format-changed", SmoothingPipeline.ClassifyTraceDrift(trace));
        }

        [Fact]
        public void ClassifyTraceDrift_PeerEpochChanged_ReturnsReason()
        {
            CoBubbleOffsetTrace trace = MakeTrace("peer");
            trace.PeerSidecarEpoch = 3;
            SmoothingPipeline.CoBubblePeerResolverForTesting = id =>
                new Recording { RecordingId = id, RecordingFormatVersion = trace.PeerSourceFormatVersion, SidecarEpoch = 4 };
            Assert.Equal("peer-epoch-changed", SmoothingPipeline.ClassifyTraceDrift(trace));
        }

        [Fact]
        public void ClassifyTraceDrift_PeerContentMismatch_ReturnsReason()
        {
            CoBubbleOffsetTrace trace = MakeTrace("peer");
            byte[] otherSig = new byte[32];
            for (int i = 0; i < 32; i++) otherSig[i] = 0xAB;
            // Phase 5 review-pass-3 P1-A: peer must have non-empty Points
            // for the signature path to fire — empty Points is now treated
            // as "still hydrating" and defers signature validation.
            SmoothingPipeline.CoBubblePeerResolverForTesting = id =>
                new Recording
                {
                    RecordingId = id,
                    RecordingFormatVersion = trace.PeerSourceFormatVersion,
                    SidecarEpoch = trace.PeerSidecarEpoch,
                    Points = new List<TrajectoryPoint>
                    {
                        new TrajectoryPoint { ut = trace.StartUT, bodyName = "Kerbin" },
                    },
                };
            SmoothingPipeline.CoBubblePeerSignatureRecomputeForTesting =
                (peer, startUT, endUT) => otherSig;
            Assert.Equal("peer-content-mismatch", SmoothingPipeline.ClassifyTraceDrift(trace));
        }

        [Fact]
        public void ClassifyTraceDrift_AllFieldsMatch_ReturnsNull()
        {
            CoBubbleOffsetTrace trace = MakeTrace("peer");
            // Same hydration constraint as the peer-content-mismatch test.
            SmoothingPipeline.CoBubblePeerResolverForTesting = id =>
                new Recording
                {
                    RecordingId = id,
                    RecordingFormatVersion = trace.PeerSourceFormatVersion,
                    SidecarEpoch = trace.PeerSidecarEpoch,
                    Points = new List<TrajectoryPoint>
                    {
                        new TrajectoryPoint { ut = trace.StartUT, bodyName = "Kerbin" },
                    },
                };
            SmoothingPipeline.CoBubblePeerSignatureRecomputeForTesting =
                (peer, startUT, endUT) => trace.PeerContentSignature;
            Assert.Null(SmoothingPipeline.ClassifyTraceDrift(trace));
        }

        [Fact]
        public void LoadOrCompute_SameTreePeer_NotYetCommitted_StillValidatesTrace()
        {
            // P1-A regression: ParsekScenario.OnLoad hydrates each recording's
            // sidecars BEFORE FinalizeTreeCommit appends the tree to
            // RecordingStore.CommittedRecordings. The pre-fix per-trace peer
            // validator walked CommittedRecordings only and dropped every
            // valid same-tree trace as peer-missing on every save load.
            //
            // The fix threads a treeLocalLoadSet (the in-progress tree's
            // Recordings dict) through ClassifyTraceDrift so peers being
            // loaded in the same pass are visible BEFORE they're committed.
            CoBubbleOffsetTrace trace = MakeTrace("same-tree-peer");
            // The production CoBubblePeerResolverForTesting seam is the
            // CommittedRecordings stand-in; leave it null to simulate the
            // OnLoad ordering where same-tree peers are not yet committed.
            SmoothingPipeline.CoBubblePeerResolverForTesting = null;
            // Recompute returns the trace's stored signature so the
            // peer-content-mismatch check passes.
            SmoothingPipeline.CoBubblePeerSignatureRecomputeForTesting =
                (peer, startUT, endUT) => trace.PeerContentSignature;

            // Phase 5 review-pass-3 P1-A made empty Points trigger the
            // peer-content-validation-deferred branch (returns null
            // without invoking the signature recompute), so this peer
            // gets a non-empty Points list to ensure the resolution path
            // is exercised end-to-end (load-set wins over null
            // CommittedRecordings, format/epoch checks pass, signature
            // path runs and returns null because the seam matches).
            var loadSet = new Dictionary<string, Recording>(StringComparer.Ordinal)
            {
                {
                    trace.PeerRecordingId,
                    new Recording
                    {
                        RecordingId = trace.PeerRecordingId,
                        RecordingFormatVersion = trace.PeerSourceFormatVersion,
                        SidecarEpoch = trace.PeerSidecarEpoch,
                        Points = new List<TrajectoryPoint>
                        {
                            new TrajectoryPoint { ut = trace.StartUT, bodyName = "Kerbin" },
                        },
                    }
                }
            };

            // Pre-fix code: drops trace as peer-missing because
            // CommittedRecordings doesn't yet contain the peer.
            // Post-fix: tree-local load set takes precedence, trace is valid.
            string driftReason = SmoothingPipeline.ClassifyTraceDrift(trace, loadSet);
            Assert.Null(driftReason);
        }

        [Fact]
        public void LoadOrCompute_PeerContentSignature_PeerStillHydrating_DefersValidation()
        {
            // Phase 5 review-pass-3 P1-A regression: even after threading
            // the tree-local load set through ResolvePeerRecording, the
            // signature recompute still ran against `peer.Points`. During
            // OnLoad each recording's .prec is deserialized sequentially,
            // so same-tree peers iterated AFTER the current recording have
            // peer.Points still empty when the current recording's .pann
            // is validated. Recomputing SHA-256 over an empty Points list
            // mismatches the stored signature, dropping every same-tree
            // trace as peer-content-mismatch on every save load — exactly
            // the production bug the prior P1-A round was supposed to
            // fix but missed for the signature leg.
            //
            // Fix: when peer.Points is null/empty, defer signature
            // validation to the runtime per-trace check in
            // CoBubbleBlender (P2-B from the earlier review pass), which
            // sees both sides fully hydrated. Format / epoch fields are
            // populated from the .sfs at tree-load time, so those gates
            // stay live. Visible-failure: emit a Verbose log so HR-9 is
            // preserved.
            CoBubbleOffsetTrace trace = MakeTrace("hydrating-peer");
            // Stub CoBubblePeerResolverForTesting to return a peer whose
            // Points is empty (the OnLoad-in-flight state).
            SmoothingPipeline.CoBubblePeerResolverForTesting = id =>
                new Recording
                {
                    RecordingId = id,
                    RecordingFormatVersion = trace.PeerSourceFormatVersion,
                    SidecarEpoch = trace.PeerSidecarEpoch,
                    // Points intentionally null/empty to simulate
                    // OnLoad-in-flight state.
                };
            // The signature recompute seam, if it ran, would return a
            // mismatching signature — proving the fix's deferral is what
            // saves the trace, not a coincidental match.
            byte[] mismatchSig = new byte[32];
            for (int i = 0; i < 32; i++) mismatchSig[i] = 0xCD;
            SmoothingPipeline.CoBubblePeerSignatureRecomputeForTesting =
                (peer, startUT, endUT) => mismatchSig;

            // Use the 3-arg overload so the deferral records an
            // ownerRecordingId (review-pass-4 plumbing).
            string driftReason = SmoothingPipeline.ClassifyTraceDrift(
                trace, treeLocalLoadSet: null, ownerRecordingId: "owner-rec");
            // Pre-fix: returns "peer-content-mismatch".
            // Post-fix: returns null and logs the deferred message
            // (canonical text "Peer Points empty during validation;
            // deferring to post-hydration sweep"). The rate-limit
            // dedup key "peer-content-validation-deferred" is only
            // used for suppression — it does NOT appear in the emitted
            // log text, so the assertion pins on the visible message.
            Assert.Null(driftReason);
            Assert.Contains(logLines, l => l.Contains("[Pipeline-CoBubble]")
                && l.Contains("Peer Points empty during validation")
                && l.Contains("hydrating-peer"));
            // Phase 5 review-pass-4: the deferral must enqueue an entry
            // for the post-hydration sweep — without this, a peer that
            // hydrates to mutated points would never be revalidated and
            // a stale offset would render for the entire session.
            Assert.Equal(1, SmoothingPipeline.DeferredCoBubbleValidationsCountForTesting);
        }

        [Fact]
        public void ClassifyTraceDrift_TreeLocalLoadSet_NullPeer_StillFallsBackToCommitted()
        {
            // P1-A defensive: when the tree-local load set entry is null
            // (defense-in-depth — shouldn't happen in production), the
            // resolver must fall back to CommittedRecordings rather than
            // accept the null peer.
            //
            // Phase 5 review-pass-3 P3-3 fix: the prior incarnation of
            // this test installed CoBubblePeerResolverForTesting, which
            // short-circuits ResolvePeerRecording BEFORE the load-set
            // check, so the load-set null-value fallback was never
            // executed and the test passed for the wrong reason. The fix
            // leaves the resolver seam null (null map → load-set lookup
            // runs → null entry → fallback to CommittedRecordings),
            // populates RecordingStore.CommittedRecordings directly with
            // the peer, and verifies the fallback fires.
            CoBubbleOffsetTrace trace = MakeTrace("fallback-peer");
            SmoothingPipeline.CoBubblePeerResolverForTesting = null;
            SmoothingPipeline.CoBubblePeerSignatureRecomputeForTesting =
                (peer, startUT, endUT) => trace.PeerContentSignature;

            var loadSet = new Dictionary<string, Recording>(StringComparer.Ordinal)
            {
                { trace.PeerRecordingId, null },
            };

            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            try
            {
                RecordingStore.AddCommittedInternal(new Recording
                {
                    RecordingId = trace.PeerRecordingId,
                    RecordingFormatVersion = trace.PeerSourceFormatVersion,
                    SidecarEpoch = trace.PeerSidecarEpoch,
                    Points = new List<TrajectoryPoint>
                    {
                        new TrajectoryPoint { ut = trace.StartUT, bodyName = "Kerbin" },
                    },
                });

                // Pre-fix path (review-pass-2): test seam returns the
                // peer, signature seam returns matching → null result
                // because the resolver-seam short-circuit ran. The
                // load-set null-entry fallback was never reached, so
                // the test was a false positive.
                // Post-fix (review-pass-3): resolver seam null forces
                // the load-set lookup; null entry skips load-set; the
                // committed-recordings walk finds the peer; the
                // signature seam matches → null result, but now from
                // the load-set-fallback path.
                Assert.Null(SmoothingPipeline.ClassifyTraceDrift(trace, loadSet));
            }
            finally
            {
                RecordingStore.ResetForTesting();
            }
        }

        [Fact]
        public void TryRead_CoBubbleCount_AboveCap_RejectedWithExceedsCap()
        {
            // The per-block cap MaxCoBubbleTraceEntries = 1000; counts above
            // it must be rejected before allocation.
            string path = Path.Combine(tempDir, "rec_overcap.pann");
            byte[] hash = PannotationsSidecarBinary.ComputeConfigurationHash(SmoothingConfiguration.Default);
            PannotationsSidecarBinary.Write(path, "recX", 1, 8, hash,
                splines: new List<KeyValuePair<int, SmoothingSpline>>());

            byte[] bytes = File.ReadAllBytes(path);
            // Locate the coBubbleCount int (last int32 in header). The block
            // order is: stringTableCount(4) + splineCount(4) + outlierCount(4)
            // + anchorCount(4) + coBubbleCount(4). The 4th int32 from the end
            // is the coBubbleCount when no payload is present.
            // Easier: scan for the trailing 5 zero ints (each block emits 0
            // count when empty); coBubbleCount is the last one. We rewrite
            // the last 4 bytes of the file (since coBubbleCount is the final
            // field for an empty-payload write).
            int len = bytes.Length;
            bytes[len - 4] = 0xE9; bytes[len - 3] = 0x03; bytes[len - 2] = 0x00; bytes[len - 1] = 0x00;
            // 0x000003E9 = 1001 — exceeds cap of 1000.
            File.WriteAllBytes(path, bytes);

            Assert.True(PannotationsSidecarBinary.TryProbe(path, out var probe));
            bool ok = PannotationsSidecarBinary.TryRead(path, probe,
                out _, out _, out _, out string reason);
            Assert.False(ok);
            Assert.Contains("co-bubble-trace", reason);
        }

        [Fact]
        public void Write_NullSignature_Throws()
        {
            // Defensive: null signatures must be rejected at write so a
            // malformed trace can't slip into a .pann.
            string path = Path.Combine(tempDir, "rec_null_sig.pann");
            byte[] hash = PannotationsSidecarBinary.ComputeConfigurationHash(SmoothingConfiguration.Default);
            CoBubbleOffsetTrace bad = MakeTrace("peer");
            bad.PeerContentSignature = null;
            Assert.Throws<ArgumentException>(() =>
                PannotationsSidecarBinary.Write(path, "recBad", 1, 8, hash,
                    splines: new List<KeyValuePair<int, SmoothingSpline>>(),
                    anchorCandidates: null,
                    coBubbleTraces: new List<CoBubbleOffsetTrace> { bad }));
        }

        // ----- Phase 5 review-pass-4: post-hydration revalidation sweep -----

        /// <summary>
        /// Defers a peer-content-signature validation by stashing one trace
        /// into <see cref="SectionAnnotationStore"/> and queuing a matching
        /// deferred entry in <see cref="SmoothingPipeline"/>. Returns the
        /// trace so callers can build their assertion expectations against
        /// the same object.
        /// </summary>
        private CoBubbleOffsetTrace DeferTraceForOwner(string ownerRecordingId, string peerRecordingId,
            byte[] expectedSignature, double startUT = 100.0, double endUT = 110.0)
        {
            CoBubbleOffsetTrace trace = MakeTrace(peerRecordingId, startUT: startUT, endUT: endUT);
            if (expectedSignature != null)
                trace.PeerContentSignature = expectedSignature;
            // Install in the store so the post-hydration sweep can find
            // and remove it on mismatch.
            SectionAnnotationStore.PutCoBubbleTrace(ownerRecordingId, trace);
            // Deferral path: peer.Points is empty so ClassifyTraceDrift
            // enqueues without invoking the signature recompute.
            SmoothingPipeline.CoBubblePeerResolverForTesting = id =>
                new Recording
                {
                    RecordingId = id,
                    RecordingFormatVersion = trace.PeerSourceFormatVersion,
                    SidecarEpoch = trace.PeerSidecarEpoch,
                    // Points intentionally empty.
                };
            // Stash a mismatch sig so a non-deferred path would surface
            // a regression instantly.
            byte[] sentinel = new byte[32];
            for (int i = 0; i < 32; i++) sentinel[i] = 0xEE;
            SmoothingPipeline.CoBubblePeerSignatureRecomputeForTesting =
                (peer, s, e) => sentinel;
            string driftReason = SmoothingPipeline.ClassifyTraceDrift(
                trace, treeLocalLoadSet: null, ownerRecordingId: ownerRecordingId);
            Assert.Null(driftReason);
            // Reset the seams used for deferral so the revalidation
            // pass exercises whichever path each test wants.
            SmoothingPipeline.CoBubblePeerResolverForTesting = null;
            SmoothingPipeline.CoBubblePeerSignatureRecomputeForTesting = null;
            return trace;
        }

        [Fact]
        public void RevalidateDeferredCoBubbleTraces_PeerHydratedToDifferentPoints_DropsTrace()
        {
            // Phase 5 review-pass-4 regression: after a deferred
            // validation in LoadOrCompute (peer.Points empty), the
            // post-hydration sweep must recompute the signature against
            // the now-populated peer and drop the trace if the peer's
            // points differ from what was stored at commit time.
            // Without this, the runtime per-trace check in
            // CoBubbleBlender (which only validates format / epoch)
            // would leave the stale offset installed for the entire
            // session.
            const string ownerId = "owner-mismatch";
            const string peerId = "peer-mismatch";
            byte[] storedSignature = new byte[32];
            for (int i = 0; i < 32; i++) storedSignature[i] = (byte)(i + 1);

            CoBubbleOffsetTrace trace = DeferTraceForOwner(ownerId, peerId, storedSignature);
            Assert.Equal(1, SmoothingPipeline.DeferredCoBubbleValidationsCountForTesting);

            // Hydrated peer with non-empty Points; recompute seam
            // returns a DIFFERENT signature (simulates partial
            // hydration / save-edit-without-epoch-bump).
            byte[] differentSignature = new byte[32];
            for (int i = 0; i < 32; i++) differentSignature[i] = 0xAB;
            var hydratedPeer = new Recording
            {
                RecordingId = peerId,
                RecordingFormatVersion = trace.PeerSourceFormatVersion,
                SidecarEpoch = trace.PeerSidecarEpoch,
                Points = new List<TrajectoryPoint>
                {
                    new TrajectoryPoint { ut = 100.0, bodyName = "Kerbin" },
                    new TrajectoryPoint { ut = 110.0, bodyName = "Kerbin" },
                },
            };
            SmoothingPipeline.CoBubblePeerSignatureRecomputeForTesting =
                (peer, s, e) => differentSignature;

            var hydrated = new Dictionary<string, Recording>(StringComparer.Ordinal)
            {
                { peerId, hydratedPeer },
            };
            int dropped = SmoothingPipeline.RevalidateDeferredCoBubbleTraces(hydrated);

            Assert.Equal(1, dropped);
            // Trace must be removed from the store.
            bool ownerHasTraces = SectionAnnotationStore.TryGetCoBubbleTraces(ownerId, out var ownerTraces);
            Assert.False(ownerHasTraces && ownerTraces != null && ownerTraces.Count > 0,
                "owner's trace list should be empty after mismatch drop");
            Assert.Contains(logLines, l => l.Contains("[INFO][Pipeline-CoBubble]")
                && l.Contains("Per-trace co-bubble invalidation (post-hydration)")
                && l.Contains("reason=peer-content-mismatch")
                && l.Contains("owner=" + ownerId)
                && l.Contains("peer=" + peerId));
            // Sweep is idempotent: a second call drains nothing.
            Assert.Equal(0, SmoothingPipeline.DeferredCoBubbleValidationsCountForTesting);
        }

        [Fact]
        public void RevalidateDeferredCoBubbleTraces_PeerSignatureMatches_KeepsTrace()
        {
            // Counter-test for the mismatch case: when the hydrated
            // peer's signature matches the trace's stored signature,
            // the trace must stay installed and no mismatch log fires.
            const string ownerId = "owner-match";
            const string peerId = "peer-match";
            byte[] storedSignature = new byte[32];
            for (int i = 0; i < 32; i++) storedSignature[i] = 0x55;

            CoBubbleOffsetTrace trace = DeferTraceForOwner(ownerId, peerId, storedSignature);
            Assert.Equal(1, SmoothingPipeline.DeferredCoBubbleValidationsCountForTesting);

            // Hydrated peer; recompute seam returns the SAME signature.
            var hydratedPeer = new Recording
            {
                RecordingId = peerId,
                RecordingFormatVersion = trace.PeerSourceFormatVersion,
                SidecarEpoch = trace.PeerSidecarEpoch,
                Points = new List<TrajectoryPoint>
                {
                    new TrajectoryPoint { ut = 100.0, bodyName = "Kerbin" },
                },
            };
            SmoothingPipeline.CoBubblePeerSignatureRecomputeForTesting =
                (peer, s, e) => storedSignature;

            var hydrated = new Dictionary<string, Recording>(StringComparer.Ordinal)
            {
                { peerId, hydratedPeer },
            };
            int dropped = SmoothingPipeline.RevalidateDeferredCoBubbleTraces(hydrated);

            Assert.Equal(0, dropped);
            // Trace remains in the store.
            Assert.True(SectionAnnotationStore.TryGetCoBubbleTraces(ownerId, out var ownerTraces));
            Assert.NotNull(ownerTraces);
            Assert.Contains(ownerTraces, t =>
                t != null && string.Equals(t.PeerRecordingId, peerId, StringComparison.Ordinal));
            // No mismatch log fired.
            Assert.DoesNotContain(logLines, l => l.Contains("[INFO][Pipeline-CoBubble]")
                && l.Contains("Per-trace co-bubble invalidation (post-hydration)")
                && l.Contains("reason=peer-content-mismatch"));
        }

        [Fact]
        public void RevalidateDeferredCoBubbleTraces_PeerStillEmpty_LogsAndDrops()
        {
            // When the peer never hydrated (Points still empty by the
            // time the post-hydration sweep runs), the trace is dropped
            // with reason=peer-still-not-hydrated and an Info log fires.
            // Retaining indefinitely would leak deferred entries across
            // the session.
            const string ownerId = "owner-empty";
            const string peerId = "peer-empty";
            byte[] storedSignature = new byte[32];
            for (int i = 0; i < 32; i++) storedSignature[i] = 0x77;

            CoBubbleOffsetTrace trace = DeferTraceForOwner(ownerId, peerId, storedSignature);
            Assert.Equal(1, SmoothingPipeline.DeferredCoBubbleValidationsCountForTesting);

            // Hydrated peer is also empty — sidecar load failed.
            var hydratedPeer = new Recording
            {
                RecordingId = peerId,
                RecordingFormatVersion = trace.PeerSourceFormatVersion,
                SidecarEpoch = trace.PeerSidecarEpoch,
                // Points intentionally empty.
            };
            var hydrated = new Dictionary<string, Recording>(StringComparer.Ordinal)
            {
                { peerId, hydratedPeer },
            };
            int dropped = SmoothingPipeline.RevalidateDeferredCoBubbleTraces(hydrated);

            Assert.Equal(1, dropped);
            bool ownerHasTraces = SectionAnnotationStore.TryGetCoBubbleTraces(ownerId, out var ownerTraces);
            Assert.False(ownerHasTraces && ownerTraces != null && ownerTraces.Count > 0,
                "owner's trace list should be empty after still-not-hydrated drop");
            Assert.Contains(logLines, l => l.Contains("[INFO][Pipeline-CoBubble]")
                && l.Contains("Per-trace co-bubble invalidation (post-hydration)")
                && l.Contains("reason=peer-still-not-hydrated")
                && l.Contains("owner=" + ownerId)
                && l.Contains("peer=" + peerId));
        }

        // --- Phase 8 review-pass-3: cross-tree global sweep ---

        [Fact]
        public void RecomputeDeferredCoBubbleTraces_CrossTreePeer_ResolvesAfterAllTreesHydrated()
        {
            // Phase 8 review-pass-3 regression: per-tree sweeps fired
            // before later trees hydrated, so a deferred entry whose
            // peer lived in a not-yet-loaded tree silently dropped.
            // Fix moved both sweeps to "after all committed trees
            // hydrate" with the union of recordings; this test pins
            // that contract by simulating the OnLoad ordering: defer
            // recA in tree T1 with empty stub for recB (in tree T2),
            // then run the global sweep against {A: full, B: full}.
            // Pre-fix (per-tree call with tree.Recordings = T1 only)
            // would emit no traces and an empty .pann; post-fix the
            // sweep sees recB and emits the cross-tree pair.
            const string idA = "rec-A-cross";
            const string idB = "rec-B-cross";

            var recA = new Recording
            {
                RecordingId = idA,
                RecordingFormatVersion = 8,
                SidecarEpoch = 1,
                Points = new List<TrajectoryPoint>
                {
                    new TrajectoryPoint { ut = 100.0, latitude = 0, longitude = 0, altitude = 0,
                        bodyName = "Kerbin", rotation = Quaternion.identity },
                    new TrajectoryPoint { ut = 109.0, latitude = 0, longitude = 0, altitude = 0,
                        bodyName = "Kerbin", rotation = Quaternion.identity },
                },
            };
            recA.TrackSections.Add(new TrackSection
            {
                referenceFrame = ReferenceFrame.Absolute,
                source = TrackSectionSource.Active,
                environment = SegmentEnvironment.ExoBallistic,
                startUT = 100.0,
                endUT = 109.0,
                anchorVesselId = 0,
                frames = new List<TrajectoryPoint>
                {
                    new TrajectoryPoint { ut = 100.0, bodyName = "Kerbin", rotation = Quaternion.identity },
                },
                checkpoints = new List<OrbitSegment>(),
                sampleRateHz = 4.0f,
            });
            var recB = new Recording
            {
                RecordingId = idB,
                RecordingFormatVersion = 8,
                SidecarEpoch = 1,
                Points = new List<TrajectoryPoint>
                {
                    new TrajectoryPoint { ut = 100.0, latitude = 0, longitude = 0, altitude = 0,
                        bodyName = "Kerbin", rotation = Quaternion.identity },
                    new TrajectoryPoint { ut = 109.0, latitude = 0, longitude = 0, altitude = 0,
                        bodyName = "Kerbin", rotation = Quaternion.identity },
                },
            };
            recB.TrackSections.Add(new TrackSection
            {
                referenceFrame = ReferenceFrame.Absolute,
                source = TrackSectionSource.Background,
                environment = SegmentEnvironment.ExoBallistic,
                startUT = 100.0,
                endUT = 109.0,
                anchorVesselId = 0,
                frames = new List<TrajectoryPoint>
                {
                    new TrajectoryPoint { ut = 100.0, bodyName = "Kerbin", rotation = Quaternion.identity },
                },
                checkpoints = new List<OrbitSegment>(),
                sampleRateHz = 4.0f,
            });

            // Detector test seams so DetectAndStore runs without
            // FlightGlobals.
            CoBubbleOverlapDetector.SamplePositionResolverForTesting =
                (rec, ut) => new Vector3d(0, 0, 0);
            CoBubbleOverlapDetector.BodyResolverForTesting = name => null;

            // Route all .pann writes (owner + peer) at temp paths so
            // RecomputeDeferredCoBubbleTraces can land actual files.
            string ownerPannPath = Path.Combine(tempDir, idA + ".pann");
            string peerPannPath = Path.Combine(tempDir, idB + ".pann");
            SmoothingPipeline.PeerPannPathResolverForTesting = id =>
                Path.Combine(tempDir, id + ".pann");

            try
            {
                // Step 1: write a stale-alg-stamp .pann for recA so the
                // deferred recompute path triggers when LoadOrCompute
                // runs with a tree-local load set.
                byte[] hash = PannotationsSidecarBinary.ComputeConfigurationHash(
                    SmoothingConfiguration.Default,
                    AnchorCandidateBuilder.ResolveUseAnchorTaxonomy(),
                    SmoothingPipeline.ResolveUseCoBubbleBlend(),
                    SmoothingPipeline.ResolveUseOutlierRejection());
                PannotationsSidecarBinary.Write(ownerPannPath, idA,
                    sourceSidecarEpoch: recA.SidecarEpoch,
                    sourceRecordingFormatVersion: recA.RecordingFormatVersion,
                    configurationHash: hash,
                    splines: new List<KeyValuePair<int, SmoothingSpline>>());
                byte[] bytes = File.ReadAllBytes(ownerPannPath);
                bytes[8] = 6; bytes[9] = 0; bytes[10] = 0; bytes[11] = 0;
                File.WriteAllBytes(ownerPannPath, bytes);

                // Step 2: simulate the OnLoad sequential hydration —
                // recA's LoadRecordingFiles fires while recB lives in
                // a different tree (T2) NOT yet hydrated. The tree-
                // local load set passed here only covers T1 (recA).
                // Pre-fix: per-tree sweep ran with this same set;
                // recB was never visible. Post-fix: deferred path
                // enqueues recA and waits for the global sweep.
                var t1LoadSet = new Dictionary<string, Recording>(StringComparer.Ordinal)
                {
                    { idA, recA },
                };
                Assert.Equal(0, SmoothingPipeline.DeferredCoBubbleRecomputesCountForTesting);
                SmoothingPipeline.LoadOrCompute(recA, ownerPannPath, t1LoadSet);
                Assert.Equal(1, SmoothingPipeline.DeferredCoBubbleRecomputesCountForTesting);

                // Step 3: PRE-fix demonstration — running the sweep
                // per-tree with T1's recordings only (the pre-rev3
                // ParsekScenario behavior) drains the deferred set
                // without seeing recB, so no cross-tree traces emit.
                // The pre-fix `.pann` ends up with an empty
                // CoBubbleOffsetTraces block. We re-enqueue afterwards
                // to demonstrate that step 4 (the post-fix global
                // sweep) DOES find the cross-tree pair.
                int prefixProcessed = SmoothingPipeline.RecomputeDeferredCoBubbleTraces(t1LoadSet);
                Assert.Equal(1, prefixProcessed);
                Assert.True(PannotationsSidecarBinary.TryProbe(ownerPannPath, out var prefixProbe));
                Assert.True(PannotationsSidecarBinary.TryRead(ownerPannPath, prefixProbe,
                    out _, out _, out List<CoBubbleOffsetTrace> prefixTraces, out _));
                Assert.NotNull(prefixTraces);
                Assert.Empty(prefixTraces);
                // Pre-fix bug shape pinned: per-tree recompute saw only
                // {idA} so no overlap pair was emittable. recB.pann was
                // therefore not produced either.
                Assert.False(File.Exists(peerPannPath),
                    $"peer .pann should NOT exist after per-tree-only sweep; got {peerPannPath}");

                // Step 4: re-stale the .pann (step 3 wrote a fresh
                // file with the current alg stamp) so the second
                // LoadOrCompute call's drift gate fires again, then
                // re-enqueue recA (simulating a fresh OnLoad pass)
                // and run the POST-fix global sweep with the union of
                // T1 + T2 recordings.
                byte[] freshBytes = File.ReadAllBytes(ownerPannPath);
                freshBytes[8] = 6; freshBytes[9] = 0; freshBytes[10] = 0; freshBytes[11] = 0;
                File.WriteAllBytes(ownerPannPath, freshBytes);
                SmoothingPipeline.LoadOrCompute(recA, ownerPannPath, t1LoadSet);
                Assert.Equal(1, SmoothingPipeline.DeferredCoBubbleRecomputesCountForTesting);

                var allTrees = new Dictionary<string, Recording>(StringComparer.Ordinal)
                {
                    { idA, recA },
                    { idB, recB },
                };
                int processed = SmoothingPipeline.RecomputeDeferredCoBubbleTraces(allTrees);
                Assert.Equal(1, processed);
                Assert.Equal(0, SmoothingPipeline.DeferredCoBubbleRecomputesCountForTesting);

                // recA.pann now contains a trace pointing at recB
                // (cross-tree), proving the global sweep saw both
                // recordings.
                Assert.True(PannotationsSidecarBinary.TryProbe(ownerPannPath, out var probe));
                Assert.True(PannotationsSidecarBinary.TryRead(ownerPannPath, probe,
                    out _, out _, out List<CoBubbleOffsetTrace> aTraces, out _));
                Assert.NotNull(aTraces);
                Assert.NotEmpty(aTraces);
                Assert.Contains(aTraces, t =>
                    t != null && string.Equals(t.PeerRecordingId, idB, StringComparison.Ordinal));

                // And recB.pann was symmetrically written by the sweep's
                // PersistPeerPannFiles step.
                Assert.True(File.Exists(peerPannPath),
                    $"peer .pann should be written at {peerPannPath}");
                Assert.True(PannotationsSidecarBinary.TryProbe(peerPannPath, out var probeB));
                Assert.True(PannotationsSidecarBinary.TryRead(peerPannPath, probeB,
                    out _, out _, out List<CoBubbleOffsetTrace> bTraces, out _));
                Assert.NotNull(bTraces);
                Assert.NotEmpty(bTraces);
                Assert.Contains(bTraces, t =>
                    t != null && string.Equals(t.PeerRecordingId, idA, StringComparison.Ordinal));
            }
            finally
            {
                CoBubbleOverlapDetector.ResetForTesting();
            }
        }

        [Fact]
        public void RevalidateDeferredCoBubbleTraces_PeerNotInLoadSet_LogsVerboseAndDrops()
        {
            // Phase 8 review-pass-3 HR-9 visibility: when the peer is
            // missing from the supplied load set (deleted from the save
            // file between sessions; peer-tree never loaded), the
            // sweep emits a per-entry Verbose `peer-not-in-load-set`
            // BEFORE falling back to the committed-recordings walk.
            // If the fallback also misses, the trace drops as
            // peer-still-not-hydrated; if it finds the peer in
            // CommittedRecordings, the trace is kept on signature
            // match. This test exercises the missing-everywhere case.
            const string ownerId = "owner-missing";
            const string peerId = "peer-missing-everywhere";
            byte[] storedSignature = new byte[32];
            for (int i = 0; i < 32; i++) storedSignature[i] = 0x42;

            CoBubbleOffsetTrace trace = DeferTraceForOwner(ownerId, peerId, storedSignature);
            Assert.Equal(1, SmoothingPipeline.DeferredCoBubbleValidationsCountForTesting);

            // Hydrated set excludes peerId entirely; CommittedRecordings
            // is empty (default test ctor state). Pre-fix: only the
            // peer-still-not-hydrated Info fired. Post-fix: an
            // additional peer-not-in-load-set Verbose fires first.
            var hydrated = new Dictionary<string, Recording>(StringComparer.Ordinal)
            {
                // Owner is in the set so the sweep snapshot drains it,
                // but peer is intentionally absent.
                {
                    ownerId,
                    new Recording
                    {
                        RecordingId = ownerId,
                        RecordingFormatVersion = trace.PeerSourceFormatVersion,
                        SidecarEpoch = trace.PeerSidecarEpoch,
                        Points = new List<TrajectoryPoint>
                        {
                            new TrajectoryPoint { ut = 100.0, bodyName = "Kerbin" },
                        },
                    }
                },
            };
            int dropped = SmoothingPipeline.RevalidateDeferredCoBubbleTraces(hydrated);

            Assert.Equal(1, dropped);
            // The new Verbose log fired with the canonical text.
            Assert.Contains(logLines, l => l.Contains("[Pipeline-CoBubble]")
                && l.Contains("peer not in supplied load set")
                && l.Contains("owner=" + ownerId)
                && l.Contains("peer=" + peerId));
            // The existing peer-still-not-hydrated drop log also fired.
            Assert.Contains(logLines, l => l.Contains("[INFO][Pipeline-CoBubble]")
                && l.Contains("Per-trace co-bubble invalidation (post-hydration)")
                && l.Contains("reason=peer-still-not-hydrated")
                && l.Contains("peer=" + peerId));
            // Summary log includes the new counter.
            Assert.Contains(logLines, l => l.Contains("[Pipeline-CoBubble]")
                && l.Contains("RevalidateDeferredCoBubbleTraces summary")
                && l.Contains("peerNotInLoadSet=1"));
        }
    }
}
