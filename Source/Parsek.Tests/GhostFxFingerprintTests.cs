using System.Collections.Generic;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Pure-function tests for the ghost FX fingerprint formatting used by the
    /// stock-vs-Waterfall A/B equivalence tooling. Canonical form must be stable:
    /// InvariantCulture, fixed rounding, order-independent, clone-suffix-free.
    /// </summary>
    public class GhostFxFingerprintTests
    {
        [Fact]
        public void FormatEntry_RoundsAndUsesInvariantCulture()
        {
            string entry = GhostFxFingerprint.FormatEntry(
                "fx_exhaustFlame_yellow_medium(Clone)", "thrustTransform",
                new Vector3(0.123f, -2.179f, 0f),
                new Vector3(270.0001f, 0f, 359.6f),
                new Vector3(1f, 1f, 1f),
                0.451f, 0.749f);

            Assert.Equal(
                "fx_exhaustFlame_yellow_medium<thrustTransform pos=(0.12,-2.18,0.00) " +
                "rot=(270,0,360) scale=(1.00,1.00,1.00) size=0.45 speed=0.75",
                entry);
        }

        [Fact]
        public void FormatEntry_NullNamesBecomePlaceholders()
        {
            string entry = GhostFxFingerprint.FormatEntry(
                null, null, Vector3.zero, Vector3.zero, Vector3.one, 1f, 1f);

            Assert.StartsWith("?<?", entry);
        }

        [Fact]
        public void BuildFingerprint_SortsEntriesAndHandlesEmpty()
        {
            var entries = new List<string> { "b-entry", "a-entry", "c-entry" };
            Assert.Equal("a-entry|b-entry|c-entry", GhostFxFingerprint.BuildFingerprint(entries));

            Assert.Equal("(none)", GhostFxFingerprint.BuildFingerprint(new List<string>()));
            Assert.Equal("(none)", GhostFxFingerprint.BuildFingerprint(null));
        }

        [Fact]
        public void BuildFingerprint_OrderIndependent()
        {
            var a = new List<string> { "x", "y" };
            var b = new List<string> { "y", "x" };
            Assert.Equal(
                GhostFxFingerprint.BuildFingerprint(a),
                GhostFxFingerprint.BuildFingerprint(b));
        }

        [Fact]
        public void DescribeCurves_FormatsKeyCounts()
        {
            Assert.Equal("em0/sp0", GhostFxFingerprint.DescribeCurves(0, 0));
            Assert.Equal("em5/sp3", GhostFxFingerprint.DescribeCurves(5, 3));
        }
    }
}
