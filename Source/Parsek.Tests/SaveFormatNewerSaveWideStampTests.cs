using System;
using System.Collections.Generic;
using System.Linq;
using Parsek.Logistics;
using Parsek.Tests.SaveRoundTrip;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// A save written by a NEWER Parsek (save-wide <c>parsekSaveFormat</c> above this build's),
    /// loaded and saved again by this build: the cloud round trip of the stores the save-wide
    /// stamp versions (supply routes, vessel names). Design-save-format-policy section 4
    /// ("Save-wide stamp": one Warn, the unstamped stores load best effort) and section 7
    /// ("the stores under the save-wide stamp have no hold") are what this pins.
    ///
    /// The policy is silent on the number written back. Today the next save stamps this
    /// build's own number, which matches the stores it rewrites in its own shape (their
    /// unknown keys dropped, an unknown enum value coerced); keeping the newer number would
    /// label this build's shape as the newer one. Which of the two the policy wants is an owner
    /// question (todo SAVE-WIDE-STAMP-NEWER-WRITEBACK); this test pins today's behaviour so a
    /// change to either side is deliberate.
    /// </summary>
    [Collection("Sequential")]
    public class SaveFormatNewerSaveWideStampTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public SaveFormatNewerSaveWideStampTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            RecordingStore.SuppressLogging = true;
            ResetStores();
        }

        public void Dispose()
        {
            ResetStores();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        private static void ResetStores()
        {
            SaveFormat.ResetForTesting();
            RouteStore.ResetForTesting();
            RouteEventLog.ResetForTesting();
            RetiredRouteCargoStore.ResetForTesting();
            VesselNameStore.ResetForTesting();
            RecordingStore.ResetForTesting();
        }

        private const string Guid1 = "0f8fad5b-d9cb-469f-a165-70867728950e";

        /// <summary>A PARSEK node as a newer build would write it.</summary>
        private static ConfigNode BuildNewerParsekNode(out string routeId)
        {
            int newer = SaveFormat.CurrentSaveFormat + 1;
            var parsek = new ConfigNode("SCENARIO");
            parsek.AddValue("name", "ParsekScenario");
            parsek.AddValue(SaveFormat.SaveFormatKey, newer.ToString(System.Globalization.CultureInfo.InvariantCulture));
            parsek.AddValue(SaveFormat.WrittenByKey, "9.9.9.0");

            // A route this build can read, with keys only the newer build knows.
            var rt = new FieldRoundTrip(RouteSaveRoundTripTests.Specs(), seed: 61000);
            Route route = rt.Build<Route>();
            routeId = route.Id;
            RouteStore.AddRoute(route);
            RouteStore.SaveRoutesTo(parsek);
            RouteStore.ResetForTesting();
            RouteEventLog.ResetForTesting();
            RetiredRouteCargoStore.ResetForTesting();
            ConfigNode routeNode = parsek.GetNode("ROUTES").GetNode("ROUTE");
            routeNode.AddValue("futureRouteKey", "newer-build-value");
            routeNode.AddNode("FUTURE_ROUTE_PAYLOAD").AddValue("k", "v");

            // A vessel-name decision with an unknown key, child node and enum value.
            ConfigNode names = parsek.AddNode(VesselNameStore.NodeName);
            names.AddValue("nextSeq", "2");
            names.AddValue("futureStoreKey", "newer-build-value");
            ConfigNode d = names.AddNode(VesselNameStore.DecisionNodeName);
            d.AddValue("seq", "1");
            d.AddValue("guid", Guid1);
            d.AddValue("name", "Gael Express");
            d.AddValue("source", "FutureSource");
            d.AddValue("ut", "123.5");
            d.AddValue("futureDecisionKey", "newer-build-value");
            d.AddNode("FUTURE_DECISION_PAYLOAD").AddValue("k", "v");
            return parsek;
        }

        [Fact]
        public void NewerSaveWideStamp_LoadSave_WarnsOnce_RewritesStoresInThisBuildsShape_AndStampsCurrent()
        {
            ConfigNode loaded = BuildNewerParsekNode(out string routeId);
            string newerRaw = loaded.GetValue(SaveFormat.SaveFormatKey);

            // Load: the stamp classifies Newer with one Warn naming both numbers and the writer.
            Assert.Equal(SaveFormatClass.Newer, SaveFormat.ClassifyLoadedScenarioNode(loaded, hasParsekData: true));
            Assert.Single(logLines, l => l.Contains("[WARN][SaveFormat]")
                && l.Contains("parsekSaveFormat=" + newerRaw + " > current=" + SaveFormat.CurrentSaveFormat)
                && l.Contains("writtenBy=9.9.9.0"));

            // The unstamped stores load best effort: what this build can read loads.
            Assert.Equal(1, RouteStore.LoadRoutesFrom(loaded));
            Assert.Equal(routeId, RouteStore.CommittedRoutes.Single().Id);
            VesselNameStore.Load(loaded);
            Assert.Contains(logLines, l => l.Contains("Loaded " + VesselNameStore.NodeName) && l.Contains("decisions=1"));

            // Save, as OnSave does: each store into a fresh node, then the stamp.
            var saved = new ConfigNode("SCENARIO");
            RouteStore.SaveRoutesTo(saved);
            VesselNameStore.Save(saved);
            SaveFormat.StampScenarioNode(saved);

            // The stamp is written as this build's own number (no hold of the newer one).
            Assert.Equal(SaveFormat.CurrentSaveFormat.ToString(System.Globalization.CultureInfo.InvariantCulture),
                saved.GetValue(SaveFormat.SaveFormatKey));
            Assert.NotEqual(newerRaw, saved.GetValue(SaveFormat.SaveFormatKey));
            Assert.Equal(SaveFormat.WriterVersion(), saved.GetValue(SaveFormat.WrittenByKey));

            // The route is kept, in this build's shape: the newer build's keys are gone.
            ConfigNode savedRoute = saved.GetNode("ROUTES").GetNode("ROUTE");
            Assert.NotNull(savedRoute);
            Assert.Null(savedRoute.GetValue("futureRouteKey"));
            Assert.Null(savedRoute.GetNode("FUTURE_ROUTE_PAYLOAD"));

            // The decision is kept, its unknown keys dropped and its unknown source coerced.
            ConfigNode savedNames = saved.GetNode(VesselNameStore.NodeName);
            Assert.Null(savedNames.GetValue("futureStoreKey"));
            ConfigNode savedDecision = savedNames.GetNodes(VesselNameStore.DecisionNodeName).Single();
            Assert.Equal("Gael Express", savedDecision.GetValue("name"));
            Assert.Null(savedDecision.GetValue("futureDecisionKey"));
            Assert.Null(savedDecision.GetNode("FUTURE_DECISION_PAYLOAD"));
            Assert.Equal(VesselNameSource.ParsekRename.ToString(), savedDecision.GetValue("source"));

            // A reload of the rewritten save reads as current: no second newer-save Warn.
            logLines.Clear();
            Assert.Equal(SaveFormatClass.Current, SaveFormat.ClassifyLoadedScenarioNode(saved, hasParsekData: true));
            Assert.DoesNotContain(logLines, l => l.Contains("[WARN][SaveFormat]"));
        }
    }
}
