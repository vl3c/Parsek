using System.Collections.Generic;
using Parsek;
using Parsek.Logistics;
using Parsek.Tests.Generators;
using Xunit;

namespace Parsek.Tests.Logistics
{
    /// <summary>
    /// Pins the M2 transferability rule (design 19.4 M2 item 1, plan D1):
    /// any defined resource is routable (modded included), EC/IntakeAir are
    /// always excluded, undefined names are excluded with a named reason,
    /// and a missing <see cref="PartResourceLibrary"/> fails OPEN (treats
    /// names as defined) with a one-shot log instead of rejecting every
    /// resource headlessly.
    /// </summary>
    [Collection("Sequential")]
    public class ResourceTransferabilityTests : System.IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public ResourceTransferabilityTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            // The null-library fallback logs at Verbose; force the gate open
            // so the one-shot assertion sees the line.
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ResourceTransferability.ResetForTesting();
        }

        public void Dispose()
        {
            ResourceTransferability.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        // catches: a defined mod resource (the whole point of M2) being
        // rejected by the rule.
        [Fact]
        public void IsRoutable_DefinedModResource_True()
        {
            ResourceTransferability.DefinitionLookupOverrideForTesting =
                CrpFixtures.DefinedLookup;

            Assert.True(ResourceTransferability.IsRoutableResource(
                CrpFixtures.Karbonite, out string reason));
            Assert.Null(reason);
        }

        // catches: ElectricCharge sneaking back into admission outputs - it
        // always HAS a definition, so the always-ignored check must outrank
        // the defined check.
        [Fact]
        public void IsRoutable_ElectricCharge_False()
        {
            ResourceTransferability.DefinitionLookupOverrideForTesting = _ => true;

            Assert.False(ResourceTransferability.IsRoutableResource(
                "ElectricCharge", out string reason));
            Assert.Equal(ResourceTransferability.ReasonAlwaysIgnored, reason);
        }

        // catches: IntakeAir dropping out of the always-ignored pair.
        [Fact]
        public void IsRoutable_IntakeAir_False()
        {
            ResourceTransferability.DefinitionLookupOverrideForTesting = _ => true;

            Assert.False(ResourceTransferability.IsRoutableResource(
                "IntakeAir", out string reason));
            Assert.Equal(ResourceTransferability.ReasonAlwaysIgnored, reason);
        }

        // catches: an undefined name (uninstalled mod) flowing through as
        // routable, or the reason string drifting (the analysis-side log and
        // the skip dispatch both key on it).
        [Fact]
        public void IsRoutable_UndefinedName_FalseWithReason()
        {
            ResourceTransferability.DefinitionLookupOverrideForTesting =
                CrpFixtures.DefinedLookup;

            Assert.False(ResourceTransferability.IsRoutableResource(
                CrpFixtures.UninstalledModResource, out string reason));
            Assert.Equal(ResourceTransferability.ReasonUndefined, reason);
        }

        // catches: null / empty names slipping through as routable (the
        // manifest walks skip them before reaching the rule, but the rule
        // must hold on its own).
        [Fact]
        public void IsRoutable_EmptyName_False()
        {
            Assert.False(ResourceTransferability.IsRoutableResource(
                null, out string nullReason));
            Assert.Equal(ResourceTransferability.ReasonEmptyName, nullReason);

            Assert.False(ResourceTransferability.IsRoutableResource(
                string.Empty, out string emptyReason));
            Assert.Equal(ResourceTransferability.ReasonEmptyName, emptyReason);
        }

        // catches: the headless / early-load probe rejecting EVERY resource.
        // PartResourceLibrary.Instance is null in xUnit (it is a MonoBehaviour
        // singleton assigned in Awake), so with no test override this walks
        // the PRODUCTION probe: it must fail open (defined) and log the
        // fallback exactly once, not once per probed name.
        [Fact]
        public void IsRoutable_NullLibrary_DefaultsDefined_LogsOnce()
        {
            Assert.Null(ResourceTransferability.DefinitionLookupOverrideForTesting);

            Assert.True(ResourceTransferability.IsRoutableResource(
                CrpFixtures.Karbonite, out string reason));
            Assert.Null(reason);
            Assert.True(ResourceTransferability.IsRoutableResource(
                CrpFixtures.MetallicOre, out _));

            int fallbackLines = logLines.FindAll(l =>
                l.Contains("[Route]") &&
                l.Contains("PartResourceLibrary unavailable; treating resource names as defined")).Count;
            Assert.Equal(1, fallbackLines);
        }

        // catches: the always-ignored set growing or shrinking silently (the
        // design names exactly these two).
        [Fact]
        public void IsAlwaysIgnored_OnlyEcAndIntakeAir()
        {
            Assert.True(ResourceTransferability.IsAlwaysIgnored("ElectricCharge"));
            Assert.True(ResourceTransferability.IsAlwaysIgnored("IntakeAir"));
            Assert.False(ResourceTransferability.IsAlwaysIgnored("LiquidFuel"));
            Assert.False(ResourceTransferability.IsAlwaysIgnored(CrpFixtures.Karbonite));
            Assert.False(ResourceTransferability.IsAlwaysIgnored(null));
        }
    }
}
