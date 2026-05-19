using System;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Coverage for VesselSpawner.ResolveLocalizedVesselNameInSnapshot — the wrap
    /// inside NormalizeBackedUpSnapshotFromLiveVessel that strips raw "#autoLOC_..."
    /// tokens out of pv.Save's VESSEL.name field before _vessel.craft is persisted.
    /// The end-to-end "Localizer actually resolves the token" path needs live KSP;
    /// see the LocalizedName-category in-game test for that.
    /// </summary>
    [Collection("Sequential")]
    public class SnapshotVesselNameLocalizationTests : IDisposable
    {
        public SnapshotVesselNameLocalizationTests()
        {
            ParsekLog.SuppressLogging = true;
        }

        public void Dispose()
        {
            ParsekLog.SuppressLogging = false;
        }

        [Fact]
        public void NullSnapshot_DoesNotThrow()
        {
            VesselSpawner.ResolveLocalizedVesselNameInSnapshot(null);
        }

        [Fact]
        public void MissingNameValue_LeavesSnapshotUnchanged()
        {
            var snapshot = new ConfigNode("VESSEL");
            VesselSpawner.ResolveLocalizedVesselNameInSnapshot(snapshot);
            Assert.Null(snapshot.GetValue("name"));
        }

        [Fact]
        public void EmptyNameValue_LeavesEmpty()
        {
            var snapshot = new ConfigNode("VESSEL");
            snapshot.AddValue("name", "");
            VesselSpawner.ResolveLocalizedVesselNameInSnapshot(snapshot);
            Assert.Equal("", snapshot.GetValue("name"));
        }

        [Fact]
        public void RegularName_LeavesUnchanged()
        {
            var snapshot = new ConfigNode("VESSEL");
            snapshot.AddValue("name", "My Rocket");
            VesselSpawner.ResolveLocalizedVesselNameInSnapshot(snapshot);
            Assert.Equal("My Rocket", snapshot.GetValue("name"));
        }

        [Fact]
        public void NonLocKeyHashPrefix_LeavesUnchanged()
        {
            // A user-authored vessel called literally "#foobar" must not be mangled.
            var snapshot = new ConfigNode("VESSEL");
            snapshot.AddValue("name", "#foobar");
            VesselSpawner.ResolveLocalizedVesselNameInSnapshot(snapshot);
            Assert.Equal("#foobar", snapshot.GetValue("name"));
        }

        [Fact]
        public void AutoLocToken_LocalizerUnavailable_LeavesToken()
        {
            // Under xUnit the KSP Localizer is not initialized, so ResolveLocalizedName
            // returns the input unchanged. The wrap must not mutate the snapshot in
            // that case.
            var snapshot = new ConfigNode("VESSEL");
            snapshot.AddValue("name", "#autoLOC_501224");
            VesselSpawner.ResolveLocalizedVesselNameInSnapshot(snapshot);
            Assert.Equal("#autoLOC_501224", snapshot.GetValue("name"));
        }
    }
}
