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
    }
}
