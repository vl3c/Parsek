using System;
using System.Collections.Generic;
using System.IO;
using Parsek;
using Parsek.MapRender;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Phase-4 guard for the decision-only shadow driver's PURE core: the scope classifier (which
    /// ghosts the MVP shadows vs skips) and the end-to-end pipeline composition DecideForGhost
    /// (assemble → sample → decide). The scene-iterating RunFrame is KSP-coupled and validated
    /// in-game; here we lock the decision logic the reconciler signal depends on.
    ///
    /// What makes it fail: a re-aim / overlap member is shadowed (emitting reconciler noise the MVP
    /// must skip), or the composed pipeline routes a faithful ghost to the wrong treatment / fails to
    /// hold across a gap.
    ///
    /// Mutates ShadowRenderDriver static state (the per-pid caches / seams via DecideForGhost and the
    /// test seams), so it runs in the Sequential collection.
    /// </summary>
    [Collection("Sequential")]
    public class ShadowRenderDriverTests
    {
        // ---- ClassifyScope (per-member: heliocentric member skipped, faithful members shadowed) ----

        [Fact]
        public void ClassifyScope_NoUnit_NotHeliocentric_IsFaithful()
        {
            // Faithful non-loop recording (or non-member) → shadow it.
            Assert.Equal(ShadowRenderDriver.ShadowScope.Faithful,
                ShadowRenderDriver.ClassifyScope(memberIsHeliocentric: false, hasUnit: false, overlapCadenceSeconds: 0, spanSeconds: 0));
        }

        [Fact]
        public void ClassifyScope_HeliocentricMember_SkipsReaim()
        {
            // The Sun-relative transfer member is the re-synthesized one → skip.
            Assert.Equal(ShadowRenderDriver.ShadowScope.SkipReaim,
                ShadowRenderDriver.ClassifyScope(memberIsHeliocentric: true, hasUnit: true, overlapCadenceSeconds: 1000, spanSeconds: 1000));
        }

        [Fact]
        public void ClassifyScope_FaithfulMemberOfReaimMission_IsShadowed()
        {
            // A Kerbin-departure / Duna-arrival member of a re-aimed mission is NOT heliocentric → it
            // is faithful and IS shadowed (the key fix: skip per member, not per mission). This is the
            // exact in-game case: a Duna mission's Kerbin-orbit parking member.
            Assert.Equal(ShadowRenderDriver.ShadowScope.Faithful,
                ShadowRenderDriver.ClassifyScope(memberIsHeliocentric: false, hasUnit: true, overlapCadenceSeconds: 1000, spanSeconds: 1000));
        }

        [Fact]
        public void ClassifyScope_OverlapMember_SkipsOverlap()
        {
            // launch cadence (200s) shorter than span (1000s) → several instances live at once → skip.
            Assert.Equal(ShadowRenderDriver.ShadowScope.SkipOverlap,
                ShadowRenderDriver.ClassifyScope(memberIsHeliocentric: false, hasUnit: true, overlapCadenceSeconds: 200, spanSeconds: 1000));
        }

        [Fact]
        public void ClassifyScope_HeliocentricTakesPriorityOverOverlap()
        {
            // A heliocentric member that also overlaps is skipped as re-aim (the more specific reason).
            Assert.Equal(ShadowRenderDriver.ShadowScope.SkipReaim,
                ShadowRenderDriver.ClassifyScope(memberIsHeliocentric: true, hasUnit: true, overlapCadenceSeconds: 200, spanSeconds: 1000));
        }

        [Fact]
        public void ClassifyScope_DegenerateSpan_IsFaithful()
        {
            Assert.Equal(ShadowRenderDriver.ShadowScope.Faithful,
                ShadowRenderDriver.ClassifyScope(memberIsHeliocentric: false, hasUnit: true, overlapCadenceSeconds: 5, spanSeconds: 0));
        }

        // ---- ShouldSkipReaimSegment (COVERAGE-AWARE skip-lift, the critical fix from review) ----
        // skip = intentVisible && frameBodyIsStar && memberIsReaimOwner
        //        && !(chainHasReaimedSegments && sampleInSegment)

        [Fact]
        public void ShouldSkipReaimSegment_ReaimedWindow_OnHeliocentricLeg_Draws()
        {
            // THE FIX: a re-aim owner ON its re-aimed heliocentric leg (chainHasReaimedSegments &&
            // sampleInSegment) must DRAW the re-aimed conic, not skip to legacy. Killing icon-off-orbit.
            Assert.False(ShadowRenderDriver.ShouldSkipReaimSegment(
                intentVisible: true, frameBodyIsStar: true, memberIsReaimOwner: true,
                chainHasReaimedSegments: true, sampleInSegment: true));
        }

        [Fact]
        public void ShouldSkipReaimSegment_ReaimedWindow_InTrimGap_Skips()
        {
            // THE REVIEW'S BUG CASE: re-aimed window but the sample is NOT in a covering segment (a trim gap
            // between the recorded escape/capture legs and the trimmed transfer, OR a held interior gap). The
            // held intent is still Visible and star-bodied, but with sampleInSegment=false the Director must
            // SKIP (hide), matching the legacy hide-in-gap contract; without this term a held stale Sun conic
            // would be driven across the gap.
            Assert.True(ShadowRenderDriver.ShouldSkipReaimSegment(
                intentVisible: true, frameBodyIsStar: true, memberIsReaimOwner: true,
                chainHasReaimedSegments: true, sampleInSegment: false));
        }

        [Fact]
        public void ShouldSkipReaimSegment_DeclinedWindow_Skips()
        {
            // Resolver declined the window (chainHasReaimedSegments=false): the chain carries the RECORDED
            // wrong-aimed Sun leg of a re-aim owner -> SKIP (fall to legacy), even on a covering segment.
            Assert.True(ShadowRenderDriver.ShouldSkipReaimSegment(
                intentVisible: true, frameBodyIsStar: true, memberIsReaimOwner: true,
                chainHasReaimedSegments: false, sampleInSegment: true));
        }

        [Fact]
        public void ShouldSkipReaimSegment_FaithfulNonOwner_StarLeg_Draws()
        {
            // A real NON-looped interplanetary recording (memberIsReaimOwner=false) on its Sun leg is
            // faithful and intentionally rendered (the widening): DO NOT skip.
            Assert.False(ShadowRenderDriver.ShouldSkipReaimSegment(
                intentVisible: true, frameBodyIsStar: true, memberIsReaimOwner: false,
                chainHasReaimedSegments: false, sampleInSegment: true));
        }

        [Fact]
        public void ShouldSkipReaimSegment_FaithfulKerbinLeg_DoesNotSkip()
        {
            // A re-aim owner's FAITHFUL Kerbin-escape / destination-arrival leg has frameBodyIsStar=false,
            // so it is never matched and always renders (the "Kerbal X" hyperbolic escape would otherwise
            // be dropped -> icon-off-orbit regression).
            Assert.False(ShadowRenderDriver.ShouldSkipReaimSegment(
                intentVisible: true, frameBodyIsStar: false, memberIsReaimOwner: true,
                chainHasReaimedSegments: true, sampleInSegment: true));
        }

        [Fact]
        public void ShouldSkipReaimSegment_HiddenIntent_NeverSkips()
        {
            // A hidden intent has no active segment to classify; nothing to skip, for every combination of
            // the remaining flags.
            Assert.False(ShadowRenderDriver.ShouldSkipReaimSegment(
                intentVisible: false, frameBodyIsStar: true, memberIsReaimOwner: true,
                chainHasReaimedSegments: true, sampleInSegment: false));
            Assert.False(ShadowRenderDriver.ShouldSkipReaimSegment(
                intentVisible: false, frameBodyIsStar: true, memberIsReaimOwner: true,
                chainHasReaimedSegments: false, sampleInSegment: true));
        }

        // ---- BuildChainSignature (window token is the load-bearing cache discriminator under re-aim) ----

        [Fact]
        public void BuildChainSignature_DifferentWindowIndex_ProducesDifferentSignature()
        {
            // A synodic-window advance changes the re-aimed geometry but NOT the recorded OrbitSegments.Count
            // (re-aim replaces one heliocentric leg, count is stable), so without the |w{window} token the
            // cache would NOT invalidate and the stale prior-window chain would keep rendering.
            var traj = new MockTrajectory
            {
                RecordingId = "rec-win",
                Points = new List<TrajectoryPoint> { Pt(0, "Kerbin"), Pt(2, "Kerbin") },
                OrbitSegments = new List<OrbitSegment>
                {
                    new OrbitSegment { startUT = 10, endUT = 30, bodyName = "Sun" },
                },
            };
            string sig0 = ShadowRenderDriver.BuildChainSignature(traj, 0, 40, windowIndex: 0);
            string sig1 = ShadowRenderDriver.BuildChainSignature(traj, 0, 40, windowIndex: 1);
            Assert.NotEqual(sig0, sig1);
        }

        [Fact]
        public void BuildChainSignature_SameWindowIndex_IsStable()
        {
            var traj = new MockTrajectory
            {
                RecordingId = "rec-win",
                Points = new List<TrajectoryPoint> { Pt(0, "Kerbin"), Pt(2, "Kerbin") },
                OrbitSegments = new List<OrbitSegment>
                {
                    new OrbitSegment { startUT = 10, endUT = 30, bodyName = "Sun" },
                },
            };
            Assert.Equal(
                ShadowRenderDriver.BuildChainSignature(traj, 0, 40, windowIndex: 3),
                ShadowRenderDriver.BuildChainSignature(traj, 0, 40, windowIndex: 3));
        }

        [Fact]
        public void BuildChainSignature_NonReaim_WindowMinusOne_StaysUniquePerMember()
        {
            // windowIndex = -1 for every non-re-aim member; two different members still produce distinct
            // signatures via the recording id, and the same member's signature is stable.
            var a = new MockTrajectory { RecordingId = "rec-a", Points = new List<TrajectoryPoint> { Pt(0, "Kerbin"), Pt(2, "Kerbin") } };
            var b = new MockTrajectory { RecordingId = "rec-b", Points = new List<TrajectoryPoint> { Pt(0, "Kerbin"), Pt(2, "Kerbin") } };
            Assert.NotEqual(
                ShadowRenderDriver.BuildChainSignature(a, 0, 40, windowIndex: -1),
                ShadowRenderDriver.BuildChainSignature(b, 0, 40, windowIndex: -1));
            Assert.Equal(
                ShadowRenderDriver.BuildChainSignature(a, 0, 40, windowIndex: -1),
                ShadowRenderDriver.BuildChainSignature(a, 0, 40, windowIndex: -1));
        }

        // ---- DecideForGhost composition (faithful, Empty units = identity span clock, null surface) ----

        private static TrajectoryPoint Pt(double ut, string body)
            => new TrajectoryPoint { ut = ut, bodyName = body };

        // ascent (Kerbin 0..8, TracedPath) → orbit (Kerbin 10..30, StockConic) → arrival (Mun 32..36, TracedPath)
        private static MockTrajectory FaithfulChain()
            => new MockTrajectory
            {
                RecordingId = "rec-shadow",
                VesselName = "Jeb's Ride",
                Points = new List<TrajectoryPoint>
                {
                    Pt(0, "Kerbin"), Pt(2, "Kerbin"), Pt(4, "Kerbin"), Pt(6, "Kerbin"), Pt(8, "Kerbin"),
                    Pt(32, "Mun"), Pt(34, "Mun"), Pt(36, "Mun"),
                },
                OrbitSegments = new List<OrbitSegment>
                {
                    new OrbitSegment { startUT = 10, endUT = 30, bodyName = "Kerbin", semiMajorAxis = 700000, eccentricity = 0 },
                },
            };

        private static GhostRenderIntent Decide(double currentUT, GhostRenderIntent prior = default(GhostRenderIntent))
            => ShadowRenderDriver.DecideForGhost(
                FaithfulChain(), committedIndex: 0, windowStartUT: 0, windowEndUT: 40,
                currentUT: currentUT, units: GhostPlaybackLogic.LoopUnitSet.Empty, surface: null, prior: prior);

        [Fact]
        public void DecideForGhost_InOrbitSegment_IsStockConicVisible()
        {
            var intent = Decide(20.0);
            Assert.True(intent.Visible);
            Assert.Equal(Treatment.StockConic, intent.Treatment);
            Assert.Equal("Kerbin", intent.FrameBodyName);
            Assert.Equal(20.0, intent.DriveUT); // identity span clock under Empty units
        }

        [Fact]
        public void DecideForGhost_InAscentPoints_IsTracedPathVisible()
        {
            var intent = Decide(4.0);
            Assert.True(intent.Visible);
            Assert.Equal(Treatment.TracedPath, intent.Treatment);
            Assert.Equal("Kerbin", intent.FrameBodyName);
        }

        [Fact]
        public void DecideForGhost_InArrivalPoints_IsTracedPathOnDestinationBody()
        {
            var intent = Decide(34.0);
            Assert.True(intent.Visible);
            Assert.Equal(Treatment.TracedPath, intent.Treatment);
            Assert.Equal("Mun", intent.FrameBodyName);
        }

        [Fact]
        public void DecideForGhost_PastWindowEnd_IsHidden()
        {
            var intent = Decide(50.0);
            Assert.False(intent.Visible);
            Assert.Equal(Treatment.None, intent.Treatment);
        }

        [Fact]
        public void DecideForGhost_InInteriorGap_HoldsPriorVisible()
        {
            var prior = Decide(20.0);          // visible StockConic on the orbit
            var held = Decide(31.0, prior);    // 31 is in the [30,32] gap before the Mun arrival run
            Assert.True(held.Visible);          // held, not blinked off
            Assert.Equal(prior.Treatment, held.Treatment);
            Assert.Equal(prior.DriveUT, held.DriveUT);
        }

        [Fact]
        public void DecideForGhost_InInteriorGap_NoPrior_IsHidden()
        {
            var intent = Decide(31.0);          // gap, nothing drawn yet
            Assert.False(intent.Visible);
        }

        // ---- Integration #2: an OVERLAP member is now PROCESSED (no longer skipped to legacy) ----
        // The Director renders an overlap member as ONE ghost at the span-clock head-UT. RunFrame is
        // Unity-coupled (Time.frameCount), so the PURE proof is that the same assemble→sample→decide
        // composition RunFrame runs (DecideForGhost) produces a visible intent for an overlap member,
        // at the selected-cycle head-UT the legacy single head would use. (The ClassifyScope tests above
        // keep the SkipOverlap enum; production simply no longer drops on it - locked by the source gate.)

        // A single-member SELF-OVERLAP unit: span [0,40], CadenceSeconds == span (single span instance)
        // but OverlapCadenceSeconds < span (so it classifies as overlap). Member window = full span. This
        // is what a Kerbin launch-to-orbit mission looped shorter than its length produces on the map.
        private static GhostPlaybackLogic.LoopUnitSet OverlapUnitFullSpan()
        {
            var unit = new GhostPlaybackLogic.LoopUnit(
                0, new[] { 0 }, spanStartUT: 0, spanEndUT: 40, cadenceSeconds: 40,
                phaseAnchorUT: 0, overlapCadenceSeconds: 10);
            Assert.True(GhostPlaybackLogic.UnitMemberOverlaps(unit)); // must be overlap, else the test is moot
            var unitsByOwner = new Dictionary<int, GhostPlaybackLogic.LoopUnit> { { 0, unit } };
            var ownerByIndex = new Dictionary<int, int> { { 0, 0 } };
            return new GhostPlaybackLogic.LoopUnitSet(unitsByOwner, ownerByIndex);
        }

        [Fact]
        public void DecideForGhost_OverlapMember_OnOrbit_IsProcessedAndVisible()
        {
            // liveUT 60: elapsed 60, cadence 40 -> cycle 1, phaseInCycle 20 -> span-clock loopUT 20, which
            // is inside the FaithfulChain orbit segment [10,30]. The overlap member is PROCESSED (not
            // dropped) and renders the StockConic at the span-clock head-UT, exactly like the legacy single
            // head. WHAT MAKES IT FAIL: re-introducing the RunFrame overlap skip would route this to legacy
            // and DecideForGhost would never be reached for an overlap member in production.
            var units = OverlapUnitFullSpan();
            double expectedHead = GhostPlaybackLogic.ResolveTrackingStationSampleUT(
                i: 0, memberStartUT: 0, memberEndUT: 40, liveUT: 60.0, units, out bool hidden);
            Assert.False(hidden);
            Assert.Equal(20.0, expectedHead, 6); // selected-cycle head-UT

            var intent = ShadowRenderDriver.DecideForGhost(
                FaithfulChain(), committedIndex: 0, windowStartUT: 0, windowEndUT: 40,
                currentUT: 60.0, units: units, surface: null, prior: default(GhostRenderIntent));

            Assert.True(intent.Visible);                       // processed + rendered, not skipped
            Assert.Equal(Treatment.StockConic, intent.Treatment);
            Assert.Equal("Kerbin", intent.FrameBodyName);
            Assert.Equal(expectedHead, intent.DriveUT, 6);     // at the legacy single-head UT
        }

        // ---- Source gate: RunFrame no longer DROPS an overlap member (the skip is lifted) ----

        private static string ReadShadowRenderDriverSource()
            => ReadMapRenderSource("ShadowRenderDriver.cs");

        // Read a Source/Parsek/MapRender/<fileName> file for a source-text gate. Shared by the spine-swap
        // source gates that span the THREE spine files (ShadowRenderDriver + ChainSampler + GhostRenderDirector).
        private static string ReadMapRenderSource(string fileName)
        {
            string root = Path.GetFullPath(Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", ".."));
            string path = Path.Combine(root, "Source", "Parsek", "MapRender", fileName);
            if (!File.Exists(path))
            {
                // Fallback layout (mirrors MapPresenceSeamTests): some checkouts root at Parsek/.
                path = Path.Combine(root, "Parsek", "MapRender", fileName);
            }
            Assert.True(File.Exists(path), $"Source file not found at {path}");
            return File.ReadAllText(path);
        }

        // Strips trailing // line comments so the doc prose (which legitimately names SkipOverlap /
        // continue) does not trip the negative gate; the assertions are about real statements.
        private static string StripLineComments(string source)
        {
            var sb = new System.Text.StringBuilder(source.Length);
            foreach (string line in source.Split('\n'))
            {
                int idx = line.IndexOf("//", StringComparison.Ordinal);
                sb.Append(idx >= 0 ? line.Substring(0, idx) : line);
                sb.Append('\n');
            }
            return sb.ToString();
        }

        // Collapse every run of whitespace (incl. newlines / CRLF / tabs) to a single space so the
        // source gate is robust to formatting and line endings.
        private static string CollapseWhitespace(string source)
        {
            var sb = new System.Text.StringBuilder(source.Length);
            bool inWs = false;
            foreach (char c in source)
            {
                if (char.IsWhiteSpace(c))
                {
                    if (!inWs) { sb.Append(' '); inWs = true; }
                }
                else { sb.Append(c); inWs = false; }
            }
            return sb.ToString();
        }

        // ---- Phase 5b: the cutover flag is GONE; the spine is unconditional ----
        // The two old flag pins (const-defaults-on + seam-is-redundant) died with the flag; the
        // repo-wide deletion lock is the grep-audit gate (GrepAuditMapRenderPhaseSpineDriveTests /
        // scripts/grep-audit-map-render-phase-spine-drive.ps1) forbidding the deleted symbols under
        // Source/Parsek/. The source gate below pins the post-5b RunFrame shape.

        [Fact]
        public void RunFrame_SpineUnconditional_AssemblerIsExceptionFallbackOnly_SourceGate()
        {
            // Phase 5b: the spine-select no longer reads a flag - the PhaseChain samples whenever the
            // factory built one, and the assembler chain survives ONLY as the loud-warned exception
            // fallback (phaseChain null = the factory threw). A regression that re-introduced a flag
            // read into the selector, or deleted the fenced fallback, is caught here.
            string normalized = CollapseWhitespace(StripLineComments(ReadShadowRenderDriverSource()));

            Assert.Contains("if (phaseChain != null)", normalized);
            Assert.Contains("ChainSampler.Sample(phaseChain, currentUT, units)", normalized);
            // The fenced exception fallback: warn + assembler sample.
            Assert.Contains("WarnSpineAssemblerFallback(pid, traj.RecordingId, currentUT);", normalized);
            Assert.Contains("ChainSampler.Sample(chain, currentUT, units)", normalized);
        }

        // The swapped spine is THREE files (ShadowRenderDriver + ChainSampler + GhostRenderDirector); the
        // deorbit-decoupling gate must scan ALL of them, not just the driver. A future edit that pulled the
        // deorbit clock into the sampler or the director (e.g. a sampler that consumed the transfer-leg head
        // UT) would re-introduce the Phase-3<->Phase-6 coupling this gate forbids, and these per-file
        // assertions would FAIL. All three are currently clean.
        [Theory]
        [InlineData("ShadowRenderDriver.cs")]
        [InlineData("ChainSampler.cs")]
        [InlineData("GhostRenderDirector.cs")]
        public void SwappedSpine_DoesNotConsumeDeorbitClock_SourceGate(string spineFile)
        {
            // The deorbit-head / captureShift clock is consumed only by the legacy polyline Driver + the
            // span clock, NOT the swapped spine (no Phase-3<->Phase-6 coupling). Confirm each spine file
            // never references those symbols, so Phase 3 parity does not hide a clock coupling.
            string normalized = CollapseWhitespace(StripLineComments(ReadMapRenderSource(spineFile)));
            Assert.DoesNotContain("ResolveTransferLegHeadUT", normalized);
            Assert.DoesNotContain("deorbitHead", normalized);
            Assert.DoesNotContain("captureShift", normalized);
        }

        [Fact]
        public void RunFrame_DoesNotSkipOverlapMembers_SourceGate()
        {
            // Integration #2 lifted the RunFrame overlap early-skip. A regression that re-introduces it
            // (a "scope == ShadowScope.SkipOverlap" guard that continues / drops the member) is caught
            // here. The classifier and its ClassifyScope tests stay (the enum is retained), so this gate
            // is specifically about the RunFrame DROP, not the enum value.
            // Normalize: strip // comments, then collapse all runs of whitespace to single spaces so the
            // gate is robust to formatting / CRLF. The old drop looked like:
            //   if (... == ShadowScope.SkipOverlap) { skipOverlap++; continue; }
            string normalized = CollapseWhitespace(StripLineComments(ReadShadowRenderDriverSource()));

            // The old per-drop counter is gone (it was unique to the dropped overlap path)...
            Assert.DoesNotContain("skipOverlap++", normalized);
            // ...and no SkipOverlap comparison leads into a continue-drop block (whitespace-tolerant).
            Assert.DoesNotContain("SkipOverlap) { skipOverlap++; continue; }", normalized);

            // Positively: an overlap-classified member is COUNTED-then-PROCEEDS (the diagnostic counter
            // increments WITHOUT a continue), proving the member flows into the normal pipeline.
            Assert.Contains("overlapShadowed++", normalized);
        }

        // ---- Phase 4a: re-home the TracedPath owned-draw decision to the intent (flag-gated) ----

        [Fact]
        public void IsDirectorTracedPathActiveFromIntent_FreshIntentStamp_IsActive()
        {
            // The intent-sourced sibling honors the SAME +/-SeedFreshnessFrames freshness window the
            // legacy side-channel uses, so the flag-ON owned-draw routing reads the same shape.
            const uint pid = 4001u;
            try
            {
                ShadowRenderDriver.SetTracedPathIntentStampForTesting(pid, intentFrame: 100);
                Assert.True(ShadowRenderDriver.IsDirectorTracedPathActiveFromIntent(pid, 100));
                Assert.True(ShadowRenderDriver.IsDirectorTracedPathActiveFromIntent(
                    pid, 100 + ShadowRenderDriver.SeedFreshnessFrames));
                // Beyond the freshness window: stale, not active.
                Assert.False(ShadowRenderDriver.IsDirectorTracedPathActiveFromIntent(
                    pid, 100 + ShadowRenderDriver.SeedFreshnessFrames + 1));
                // A pid with no intent stamp is never active.
                Assert.False(ShadowRenderDriver.IsDirectorTracedPathActiveFromIntent(pid + 1, 100));
            }
            finally
            {
                ShadowRenderDriver.Reset();
            }
        }

        [Fact]
        public void IsTracedPathOwnedThisFrame_CollapsedToIntentSource_FlagGone_SourceGate()
        {
            // Phase 5b (the flag-GONE pin): the selector is COLLAPSED onto the single intent source -
            // the legacy else-branch (and the legacy side-channel it read) were deleted with the cutover
            // flag. A regression that re-introduced a second source / a flag read into the selector is
            // caught here; the repo-wide symbol lock is the grep-audit gate
            // (GrepAuditMapRenderPhaseSpineDriveTests).
            string normalized = CollapseWhitespace(StripLineComments(ReadShadowRenderDriverSource()));
            Assert.Contains(
                "internal static bool IsTracedPathOwnedThisFrame(uint pid, int currentFrame) "
                + "{ return IsDirectorTracedPathActiveFromIntent(pid, currentFrame); }",
                normalized);
        }

        [Fact]
        public void IsTracedPathOwnedThisFrame_ReadsIntentSource_SingleStamp()
        {
            // Phase 5b: the owned-draw routing reads the single intent-sourced stamp. An intent stamp
            // owns the leg; no stamp -> not owned. (The old legacy-only-stamp arm died with the
            // side-channel.)
            const uint pid = 4003u;
            try
            {
                ShadowRenderDriver.SetTracedPathIntentStampForTesting(pid, intentFrame: 70);
                Assert.True(ShadowRenderDriver.IsTracedPathOwnedThisFrame(pid, 70));

                ShadowRenderDriver.SetTracedPathIntentStampForTesting(pid, intentFrame: -1);
                Assert.False(ShadowRenderDriver.IsTracedPathOwnedThisFrame(pid, 70));
            }
            finally
            {
                ShadowRenderDriver.Reset();
            }
        }

        [Fact]
        public void RunFrame_StampsIntentUnconditionally_SourceGate()
        {
            // Phase 5b: the single intent stamp is written UNCONDITIONALLY on a visible TracedPath
            // intent (every consumer reads it - the owned-draw routing, the proto/marker consumers,
            // IsDirectorTracking). A regression that re-gated the stamp (a consumer would then read a
            // missing signal) is caught here.
            string normalized = CollapseWhitespace(StripLineComments(ReadShadowRenderDriverSource()));
            Assert.Contains("tracedPathIntentByPid[pid] = UnityFrame();", normalized);
        }

        // ---- Phase 4b origin / 5b collapse: the IMGUI marker draw's TracedPath disjunct ----
        // The marker-draw decision (GhostMapPresence.ShouldDrawNonProtoMarkerForGhost, routed by both
        // the flight-map + TS marker call sites) composes THREE disjuncts; only the directorTracedPath
        // one is something the spine's intent decides, and it reads the SAME shared selector the
        // polyline Driver routes on (IsTracedPathOwnedThisFrame - single intent source since 5b), so the
        // marker decision, the polyline owned-draw, and the proto/marker consumers can never desync. The
        // other two disjuncts (polylineOwning actual-draw, iconSuppressed = the KEPT no-conic floor)
        // have no intent equivalent and stay on their sources. These tests lock the SELECTOR the marker
        // site reads; the Unity-coupled wrapper's static reads are covered by the in-game test (project
        // rule: Unity-coupled -> in-game).

        [Fact]
        public void MarkerTracedPathDisjunct_TracksIntentSource_SingleStamp()
        {
            // The marker decision's TracedPath disjunct tracks the single intent-sourced stamp: an
            // intent stamp makes the disjunct true; no stamp -> false.
            const uint pid = 4102u;
            try
            {
                ShadowRenderDriver.SetTracedPathIntentStampForTesting(pid, intentFrame: 90);
                Assert.True(ShadowRenderDriver.IsTracedPathOwnedThisFrame(pid, 90));

                ShadowRenderDriver.SetTracedPathIntentStampForTesting(pid, intentFrame: -1);
                Assert.False(ShadowRenderDriver.IsTracedPathOwnedThisFrame(pid, 90));
            }
            finally
            {
                ShadowRenderDriver.Reset();
            }
        }

        [Fact]
        public void MarkerTracedPathDisjunct_SharedSelector_DecisionConsistent()
        {
            // The no-desync proof for the marker site (Phase 5b single-source collapse): the marker's
            // TracedPath disjunct and the proto-icon suppression in GhostOrbitLinePatch read the SAME
            // selector over the SAME single stamp, so exactly one of {proto icon, our marker} draws -
            // no double, no gap. Model the stamp and assert disjunct -> decision consistency.
            const uint pid = 4103u;
            try
            {
                ShadowRenderDriver.SetTracedPathIntentStampForTesting(pid, intentFrame: 210);

                bool disjunct = ShadowRenderDriver.IsTracedPathOwnedThisFrame(pid, 210);
                bool decision = GhostMapPresence.ResolveMarkerDrawDecision(
                    directorTracedPathActive: disjunct, polylineOwning: false, iconSuppressedLegacy: false);

                Assert.True(disjunct);
                Assert.True(decision);
            }
            finally
            {
                ShadowRenderDriver.Reset();
            }
        }

        [Fact]
        public void MarkerSite_TracedPathDisjunct_ReadsSharedSelector_SourceGate()
        {
            // The marker-draw site (GhostMapPresence.ShouldDrawNonProtoMarkerForGhost) must resolve its
            // directorTracedPathActive disjunct through the shared selector IsTracedPathOwnedThisFrame
            // (the same source the polyline Driver + the proto icon/line suppress patches route on). A
            // regression that re-introduced a second source at the marker site is caught here.
            string normalized = CollapseWhitespace(StripLineComments(ReadGhostMapPresenceSource()));

            // The disjunct is assigned from the shared selector.
            Assert.Contains(
                "directorTracedPathActive = Parsek.MapRender.ShadowRenderDriver.IsTracedPathOwnedThisFrame(",
                normalized);
        }

        // Read Source/Parsek/<fileName> for a source-text gate (a top-level source file, NOT under
        // MapRender/). Mirrors ReadMapRenderSource's root resolution + the Parsek/-rooted fallback.
        private static string ReadParsekSource(string fileName)
        {
            string root = Path.GetFullPath(Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", ".."));
            string path = Path.Combine(root, "Source", "Parsek", fileName);
            if (!File.Exists(path))
                path = Path.Combine(root, "Parsek", fileName);
            Assert.True(File.Exists(path), $"Source file not found at {path}");
            return File.ReadAllText(path);
        }

        private static string ReadGhostMapPresenceSource()
            => ReadParsekSource("GhostMapPresence.cs");
    }
}
