using System;
using System.IO;
using System.Collections.Generic;
using Parsek;
using Xunit;

namespace Parsek.Tests.Logistics
{
    /// <summary>
    /// Pure-helper tests for the dock-partner-PID resolution path. These were added
    /// after a real playtest failure where two Parsek-tracked vessels docked but the
    /// merged child carried no <c>RouteConnectionWindow</c>, because the legacy
    /// <c>FindAbsorbedDockPartnerPid</c> only inspected the active tree's
    /// <c>BackgroundMap</c> — invisible to a partner whose recording was committed in
    /// a prior tree.
    ///
    /// The new resolver derives the partner PID from the couple event directly, and
    /// a separate validation gate decides whether that partner is route-eligible
    /// (must have a known Parsek recording, either active-tree or committed).
    /// </summary>
    [Collection("Sequential")]
    public class DockPartnerResolverTests
    {
        // --- ResolveDockPartnerPidFromEvent ---

        // catches: resolver picking 'self' or returning 0 when partner side is non-zero.
        [Fact]
        public void Resolve_TargetCase_ReturnsFromSide()
        {
            // Target = our recorder survived as the merged vessel.
            // KSP convention in the docked tuple: data.to is the survivor, data.from is the absorbed.
            // self = mergedPid = toPid. Partner = fromPid.
            uint fromPid = 111u;
            uint toPid = 222u;
            uint selfPid = 222u;
            uint partner = ParsekFlight.ResolveDockPartnerPidFromEvent(fromPid, toPid, selfPid);
            Assert.Equal(111u, partner);
        }

        // catches: resolver mishandling the absorbed (initiator) case.
        [Fact]
        public void Resolve_InitiatorCase_ReturnsToSide()
        {
            // Initiator = we were absorbed. selfPid != mergedPid (toPid).
            uint fromPid = 111u;
            uint toPid = 222u;
            uint selfPid = 111u;
            uint partner = ParsekFlight.ResolveDockPartnerPidFromEvent(fromPid, toPid, selfPid);
            Assert.Equal(222u, partner);
        }

        // catches: resolver returning either side when both are equal to self.
        [Fact]
        public void Resolve_BothEqualSelf_ReturnsZero()
        {
            uint partner = ParsekFlight.ResolveDockPartnerPidFromEvent(42u, 42u, 42u);
            Assert.Equal(0u, partner);
        }

        // catches: resolver returning 0 for a side that is 0 instead of falling through.
        [Fact]
        public void Resolve_FromZero_FallsThroughToTo()
        {
            uint partner = ParsekFlight.ResolveDockPartnerPidFromEvent(0u, 555u, 0u);
            Assert.Equal(555u, partner);
        }

        // catches: resolver returning a zero PID as a "valid" partner.
        [Fact]
        public void Resolve_BothZero_ReturnsZero()
        {
            uint partner = ParsekFlight.ResolveDockPartnerPidFromEvent(0u, 0u, 0u);
            Assert.Equal(0u, partner);
        }

        // --- IsKnownDockPartnerForRoute ---

        // catches: validation gate accepting a partner that matches our own vessel PID.
        [Fact]
        public void Known_SelfPid_ReturnsFalse()
        {
            var rec = new Recording { VesselPersistentId = 42u };
            var active = new List<Recording> { rec };
            bool known = ParsekFlight.IsKnownDockPartnerForRoute(
                partnerPid: 42u, selfVesselPid: 42u,
                activeTreeRecordings: active, committedRecordings: new List<Recording>());
            Assert.False(known);
        }

        // catches: validation gate accepting a zero PID.
        [Fact]
        public void Known_ZeroPartner_ReturnsFalse()
        {
            bool known = ParsekFlight.IsKnownDockPartnerForRoute(
                partnerPid: 0u, selfVesselPid: 1u,
                activeTreeRecordings: null, committedRecordings: null);
            Assert.False(known);
        }

        // catches: validation gate missing an active-tree match.
        [Fact]
        public void Known_ActiveTreeMatch_ReturnsTrue()
        {
            var active = new List<Recording>
            {
                new Recording { VesselPersistentId = 1u },
                new Recording { VesselPersistentId = 222u },
            };
            bool known = ParsekFlight.IsKnownDockPartnerForRoute(
                partnerPid: 222u, selfVesselPid: 1u,
                activeTreeRecordings: active, committedRecordings: new List<Recording>());
            Assert.True(known);
        }

        // catches: validation gate failing to consult RecordingStore.CommittedRecordings —
        // the exact regression the playtest exposed (partner has a committed recording
        // from a prior tree but is not in the new tree's BackgroundMap).
        [Fact]
        public void Known_CommittedRecordingsMatch_ReturnsTrue()
        {
            var committed = new List<Recording>
            {
                new Recording { VesselPersistentId = 333u },
            };
            bool known = ParsekFlight.IsKnownDockPartnerForRoute(
                partnerPid: 333u, selfVesselPid: 1u,
                activeTreeRecordings: null, committedRecordings: committed);
            Assert.True(known);
        }

        // catches: validation gate accepting a truly foreign vessel (no Parsek recording
        // anywhere). Routes should not be created against KSP-visible-but-Parsek-unknown
        // vessels in v0.
        [Fact]
        public void Known_ForeignVessel_ReturnsFalse()
        {
            var active = new List<Recording> { new Recording { VesselPersistentId = 1u } };
            var committed = new List<Recording> { new Recording { VesselPersistentId = 2u } };
            bool known = ParsekFlight.IsKnownDockPartnerForRoute(
                partnerPid: 999u, selfVesselPid: 1u,
                activeTreeRecordings: active, committedRecordings: committed);
            Assert.False(known);
        }

        // catches: validation gate crashing on null inputs (defensive — neither registry
        // is guaranteed to be non-null mid-session, e.g. during tree teardown).
        [Fact]
        public void Known_BothRegistriesNull_ReturnsFalse()
        {
            bool known = ParsekFlight.IsKnownDockPartnerForRoute(
                partnerPid: 555u, selfVesselPid: 1u,
                activeTreeRecordings: null, committedRecordings: null);
            Assert.False(known);
        }

        // catches: validation gate skipping null Recording entries instead of crashing.
        [Fact]
        public void Known_NullEntriesInRegistry_AreIgnored()
        {
            var active = new List<Recording>
            {
                null,
                new Recording { VesselPersistentId = 777u },
                null,
            };
            bool known = ParsekFlight.IsKnownDockPartnerForRoute(
                partnerPid: 777u, selfVesselPid: 1u,
                activeTreeRecordings: active, committedRecordings: null);
            Assert.True(known);
        }

        // --- ResolveTargetSideAbsorbedPid (D5 same-tree re-dock, SD-1) ---

        // catches: a TARGET-side dock with a same-tree background partner recording ONE
        // parent. onPartCouple fires pre-couple, so the partner is intact; the couple event
        // names it, and it is a BackgroundMap member.
        [Fact]
        public void TargetAbsorbed_PartnerInBackgroundMap_IsTheAbsorbedSide()
        {
            uint absorbed = ParsekFlight.ResolveTargetSideAbsorbedPid(
                partnerPidFromEvent: 2009145679u, mergedPid: 3620499050u,
                backgroundMapPids: new HashSet<uint> { 2009145679u });
            Assert.Equal(2009145679u, absorbed);
        }

        // catches: a foreign or cross-tree partner (not in this tree's map) becoming a
        // merge parent. Those docks stay single-parent.
        [Fact]
        public void TargetAbsorbed_PartnerOutsideBackgroundMap_IsZero()
        {
            Assert.Equal(0u, ParsekFlight.ResolveTargetSideAbsorbedPid(
                555u, 1u, new HashSet<uint> { 777u }));
        }

        // catches: the old "vessel gone or partless" scan coming back. An UNLOADED on-rails
        // background stage reads zero parts, so while the active ship docks a foreign
        // partner that stage must NOT be taken as the second parent (it would be stamped
        // Docked and truncated). With the partner in the map beside it, the partner wins.
        [Fact]
        public void TargetAbsorbed_UnloadedBackgroundStage_IsNeverTheAbsorbedSide()
        {
            const uint unloadedStagePid = 4242u;
            Assert.Equal(0u, ParsekFlight.ResolveTargetSideAbsorbedPid(
                partnerPidFromEvent: 555u, mergedPid: 1u,
                backgroundMapPids: new HashSet<uint> { unloadedStagePid }));
            Assert.Equal(555u, ParsekFlight.ResolveTargetSideAbsorbedPid(
                partnerPidFromEvent: 555u, mergedPid: 1u,
                backgroundMapPids: new HashSet<uint> { unloadedStagePid, 555u }));
        }

        // catches: the merged vessel itself (or an unresolved event partner) being taken as
        // the absorbed side, and a null map crashing mid-teardown.
        [Fact]
        public void TargetAbsorbed_DegenerateInputs_AreZero()
        {
            Assert.Equal(0u, ParsekFlight.ResolveTargetSideAbsorbedPid(
                1u, 1u, new HashSet<uint> { 1u }));
            Assert.Equal(0u, ParsekFlight.ResolveTargetSideAbsorbedPid(
                0u, 1u, new HashSet<uint> { 0u }));
            Assert.Equal(0u, ParsekFlight.ResolveTargetSideAbsorbedPid(555u, 1u, null));
        }

        // catches: the live OnPartCouple TARGET branch no longer routing through the
        // resolver with the active tree's map, or a map-shape heuristic returning. Line
        // comments are stripped first so a commented-out call cannot satisfy the gate.
        [Fact]
        public void TargetAbsorbed_OnPartCoupleTargetBranch_RoutesThroughResolver()
        {
            string src = StripLineComments(
                File.ReadAllText(FindParsekFlightSource()).Replace("\r\n", "\n"));
            int branch = src.IndexOf("if (isTarget)", StringComparison.Ordinal);
            Assert.True(branch >= 0, "OnPartCouple no longer branches on isTarget");
            int resolve = src.IndexOf(
                "absorbedPid = ResolveTargetSideAbsorbedPid(\n                            "
                + "partnerPidFromEvent, mergedPid, activeTree.BackgroundMap.Keys);",
                branch, StringComparison.Ordinal);
            int initiator = src.IndexOf("absorbedPid = recorder.RecordingVesselId;", branch,
                StringComparison.Ordinal);
            Assert.True(resolve > branch && initiator > resolve,
                "the TARGET branch must assign absorbedPid through ResolveTargetSideAbsorbedPid "
                + "over activeTree.BackgroundMap.Keys");
            Assert.Equal(0, CountOccurrences(src, "FindAbsorbedDockPartnerPid"));
        }

        private static string StripLineComments(string src)
        {
            var sb = new System.Text.StringBuilder(src.Length);
            foreach (string line in src.Split('\n'))
            {
                int c = line.IndexOf("//", StringComparison.Ordinal);
                sb.Append(c >= 0 ? line.Substring(0, c) : line).Append('\n');
            }
            return sb.ToString();
        }

        private static int CountOccurrences(string text, string needle)
        {
            int count = 0;
            for (int i = text.IndexOf(needle, StringComparison.Ordinal); i >= 0;
                 i = text.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
                count++;
            return count;
        }

        private static string FindParsekFlightSource()
        {
            string dir = AppDomain.CurrentDomain.BaseDirectory;
            while (!string.IsNullOrEmpty(dir))
            {
                string candidate = Path.Combine(dir, "Source", "Parsek", "ParsekFlight.cs");
                if (File.Exists(candidate)) return candidate;
                dir = Path.GetDirectoryName(dir);
            }
            return Path.GetFullPath(Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory,
                "..", "..", "..", "..", "Parsek", "ParsekFlight.cs"));
        }
    }
}
