using System.Collections.Generic;
using Parsek;
using Parsek.Logistics;
using Parsek.Tests.Generators;
using Xunit;

namespace Parsek.Tests.Logistics
{
    /// <summary>
    /// Phase 5 of the Route store plan: pin
    /// <see cref="RouteProofHasher.ComputeRouteProofHashFromRecording"/> against
    /// silent fingerprint drift. Each test names the regression it catches.
    /// </summary>
    [Collection("Sequential")]
    public class RouteProofHashTests : System.IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public RouteProofHashTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ResourceTransferability.ResetForTesting();
        }

        public void Dispose()
        {
            ResourceTransferability.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        // ---------- Fixture helpers ----------

        private static InventoryPayloadItem InvItem(string identity, string part, int qty)
        {
            return new InventoryPayloadItem
            {
                IdentityHash = identity,
                PartName = part,
                VariantName = "Default",
                Quantity = qty,
                SlotsTaken = 1,
                StoredResources = new Dictionary<string, ResourceAmount>
                {
                    { "LiquidFuel", new ResourceAmount { amount = 50.0, maxAmount = 100.0 } }
                }
            };
        }

        private static Recording RecordingWithProof()
        {
            return new Recording
            {
                RecordingId = "rec-proof-A",
                RouteConnectionWindows = new List<RouteConnectionWindow>
                {
                    new RouteConnectionWindow
                    {
                        WindowId = "win-1",
                        DockUT = 100.5,
                        UndockUT = 200.75,
                        TransferTargetVesselPid = 4242u,
                        TransferKind = RouteConnectionKind.DockingPort,
                        TransportPartPersistentIds = new List<uint> { 11u, 22u },
                        EndpointPartPersistentIds = new List<uint> { 33u },
                        DockTransportResources = new Dictionary<string, ResourceAmount>
                        {
                            { "LiquidFuel", new ResourceAmount { amount = 1000.0, maxAmount = 1000.0 } }
                        },
                        UndockTransportResources = new Dictionary<string, ResourceAmount>
                        {
                            { "LiquidFuel", new ResourceAmount { amount = 250.0, maxAmount = 1000.0 } }
                        },
                        DockEndpointResources = new Dictionary<string, ResourceAmount>
                        {
                            { "Oxidizer", new ResourceAmount { amount = 500.0, maxAmount = 500.0 } }
                        },
                        UndockEndpointResources = new Dictionary<string, ResourceAmount>
                        {
                            { "Oxidizer", new ResourceAmount { amount = 1250.0, maxAmount = 1500.0 } }
                        },
                        DockEndpointInventory = new List<InventoryPayloadItem>
                        {
                            InvItem("identA", "scienceModule", 1),
                            InvItem("identB", "battery", 4)
                        }
                    }
                },
                RouteOriginProof = new RouteOriginProof
                {
                    StartDockedOriginVesselPid = 8888u,
                    StartTransportResources = new Dictionary<string, ResourceAmount>
                    {
                        { "MonoPropellant", new ResourceAmount { amount = 200.0, maxAmount = 200.0 } }
                    }
                }
            };
        }

        // ---------- Tests ----------

        // catches: locale or ordering drift, especially InvariantCulture regressions.
        [Fact]
        public void Hash_DeterministicAcrossRuns()
        {
            Recording rec = RecordingWithProof();

            string first = RouteProofHasher.ComputeRouteProofHashFromRecording(rec);
            string second = RouteProofHasher.ComputeRouteProofHashFromRecording(rec);

            Assert.Equal(first, second);
            Assert.NotEqual(RouteProofHasher.NoRouteProofSentinel, first);
        }

        // catches: changing the empty-state behavior silently — empty proof
        // recordings must produce a stable, recognizable sentinel.
        [Fact]
        public void Hash_NoRouteProof_StableSentinel()
        {
            var emptyA = new Recording { RecordingId = "rec-empty-A" };
            var emptyB = new Recording { RecordingId = "rec-empty-B" };

            string hashA = RouteProofHasher.ComputeRouteProofHashFromRecording(emptyA);
            string hashB = RouteProofHasher.ComputeRouteProofHashFromRecording(emptyB);

            Assert.Equal(RouteProofHasher.NoRouteProofSentinel, hashA);
            Assert.Equal(RouteProofHasher.NoRouteProofSentinel, hashB);
            // Sentinel comparison is INTENTIONALLY equal regardless of
            // unrelated fields — the contract is "no proof, no fingerprint."
            Assert.Equal(hashA, hashB);
        }

        // catches: a hash that ignores a route-timing-relevant field on
        // RouteConnectionWindow (TransferTargetVesselPid identifies the
        // partner vessel — drift here means the route is delivering to a
        // different vessel than the source proved).
        [Fact]
        public void Hash_DiffersOnTransferTargetVesselPid()
        {
            Recording recA = RecordingWithProof();
            string hashA = RouteProofHasher.ComputeRouteProofHashFromRecording(recA);

            Recording recB = RecordingWithProof();
            recB.RouteConnectionWindows[0].TransferTargetVesselPid = 9999u;
            string hashB = RouteProofHasher.ComputeRouteProofHashFromRecording(recB);

            Assert.NotEqual(hashA, hashB);
        }

        // catches: hash that ignores window timing (UndockUT shifts mean the
        // route's transit duration is no longer what the source recorded).
        [Fact]
        public void Hash_DiffersOnDockUT()
        {
            Recording recA = RecordingWithProof();
            string hashA = RouteProofHasher.ComputeRouteProofHashFromRecording(recA);

            Recording recB = RecordingWithProof();
            recB.RouteConnectionWindows[0].DockUT = 999.0;
            string hashB = RouteProofHasher.ComputeRouteProofHashFromRecording(recB);

            Assert.NotEqual(hashA, hashB);
        }

        // catches: hash that doesn't bottom out on payload identity.
        // Identity hash is the only field that proves "same physical part
        // instance" across save/load.
        [Fact]
        public void Hash_DiffersOnEndpointInventoryItemIdentityHash()
        {
            Recording recA = RecordingWithProof();
            string hashA = RouteProofHasher.ComputeRouteProofHashFromRecording(recA);

            Recording recB = RecordingWithProof();
            recB.RouteConnectionWindows[0].DockEndpointInventory[0].IdentityHash = "totally-different-identity";
            string hashB = RouteProofHasher.ComputeRouteProofHashFromRecording(recB);

            Assert.NotEqual(hashA, hashB);
        }

        // catches: an order-sensitive hash that would drift on a stable
        // harmless edit (inventory order is not authored by the player —
        // every redock relists items in storage order).
        [Fact]
        public void Hash_StableOnInventoryItemReorder()
        {
            Recording recA = RecordingWithProof();
            string hashA = RouteProofHasher.ComputeRouteProofHashFromRecording(recA);

            Recording recB = RecordingWithProof();
            // Reverse the two items in DockEndpointInventory.
            var inv = recB.RouteConnectionWindows[0].DockEndpointInventory;
            var tmp = inv[0];
            inv[0] = inv[1];
            inv[1] = tmp;

            string hashB = RouteProofHasher.ComputeRouteProofHashFromRecording(recB);

            Assert.Equal(hashA, hashB);
        }

        // catches: origin-proof field missing from hash inputs. The
        // StartDockedOriginVesselPid is the proof anchor for non-KSC origin
        // routes; drift here means the route's debit authority has shifted.
        [Fact]
        public void Hash_DiffersOnOriginProofStartDockedOriginVesselPid()
        {
            Recording recA = RecordingWithProof();
            string hashA = RouteProofHasher.ComputeRouteProofHashFromRecording(recA);

            Recording recB = RecordingWithProof();
            recB.RouteOriginProof.StartDockedOriginVesselPid = 11111u;
            string hashB = RouteProofHasher.ComputeRouteProofHashFromRecording(recB);

            Assert.NotEqual(hashA, hashB);
        }

        // catches (M2 resource generality): a stock-name assumption in the
        // canonical hash lines - CRP-named manifests must hash exactly as
        // deterministically as stock names, and the NAME must participate
        // (renaming a witnessed resource is a different witnessed transfer).
        [Fact]
        public void Hash_CrpNames_DeterministicAndNameSensitive()
        {
            Recording crpA = RecordingWithCrpProof();
            Recording crpB = RecordingWithCrpProof();
            string hashA = RouteProofHasher.ComputeRouteProofHashFromRecording(crpA);
            string hashB = RouteProofHasher.ComputeRouteProofHashFromRecording(crpB);

            Assert.Equal(hashA, hashB);
            Assert.NotEqual(RouteProofHasher.NoRouteProofSentinel, hashA);

            // Same amounts under a different resource name set: stock-name
            // baseline must hash differently.
            Assert.NotEqual(
                RouteProofHasher.ComputeRouteProofHashFromRecording(RecordingWithProof()),
                hashA);
        }

        // catches (M2 D2 boundary pin): the transferability rule leaking into
        // the hash. The hash pins the witnessed transfer, name-agnostically;
        // whether a name currently has a PartResourceDefinition (mod installed
        // or not) must not change the fingerprint, or uninstalling a resource
        // mod would flip every affected route to SourceChanged on load.
        [Fact]
        public void Hash_IndependentOfResourceDefinitionLookup()
        {
            Recording rec = RecordingWithCrpProof();

            ResourceTransferability.DefinitionLookupOverrideForTesting = _ => true;
            string hashAllDefined = RouteProofHasher.ComputeRouteProofHashFromRecording(rec);

            ResourceTransferability.DefinitionLookupOverrideForTesting = _ => false;
            string hashNoneDefined = RouteProofHasher.ComputeRouteProofHashFromRecording(rec);

            Assert.Equal(hashAllDefined, hashNoneDefined);
        }

        // RecordingWithProof with the resource names swapped to the shared
        // CRP fixture set (amounts/pids unchanged), including the deliberately
        // UNDEFINED name: capture and hashing are name-agnostic, so even an
        // uninstalled mod's resource stays part of the fingerprint.
        private static Recording RecordingWithCrpProof()
        {
            Recording rec = RecordingWithProof();
            RouteConnectionWindow window = rec.RouteConnectionWindows[0];
            window.DockTransportResources = new Dictionary<string, ResourceAmount>
            {
                { CrpFixtures.Karbonite, new ResourceAmount { amount = 1000.0, maxAmount = 1000.0 } }
            };
            window.UndockTransportResources = new Dictionary<string, ResourceAmount>
            {
                { CrpFixtures.Karbonite, new ResourceAmount { amount = 250.0, maxAmount = 1000.0 } }
            };
            window.DockEndpointResources = new Dictionary<string, ResourceAmount>
            {
                { CrpFixtures.MetallicOre, new ResourceAmount { amount = 500.0, maxAmount = 500.0 } }
            };
            window.UndockEndpointResources = new Dictionary<string, ResourceAmount>
            {
                { CrpFixtures.MetallicOre, new ResourceAmount { amount = 1250.0, maxAmount = 1500.0 } },
                { CrpFixtures.UninstalledModResource, new ResourceAmount { amount = 12.25, maxAmount = 50.0 } }
            };
            rec.RouteOriginProof.StartTransportResources = new Dictionary<string, ResourceAmount>
            {
                { CrpFixtures.Supplies, new ResourceAmount { amount = 200.0, maxAmount = 200.0 } }
            };
            return rec;
        }

        // catches (M2 D10 gate pin / review BLOCKER 3): the sentinel gate not
        // widened - a recording carrying ONLY a run manifest (most lineage legs
        // of a mining run) must be hashable, or a rewrite dropping the manifest
        // could never flip SourceChanged.
        [Fact]
        public void Hash_RunManifestOnlyRecording_NotSentinel()
        {
            var rec = new Recording
            {
                RecordingId = "rec-run-manifest-only",
                RouteRunManifest = new RouteRunCargoManifest
                {
                    TransportPartPersistentIds = new List<uint> { 100u, 200u },
                    StartTransportResources = new Dictionary<string, ResourceAmount>
                    {
                        { "Ore", new ResourceAmount { amount = 0.0, maxAmount = 1500.0 } }
                    },
                    EndTransportResources = new Dictionary<string, ResourceAmount>
                    {
                        { "Ore", new ResourceAmount { amount = 1200.0, maxAmount = 1500.0 } }
                    },
                    EndCaptured = true
                }
            };

            string hash = RouteProofHasher.ComputeRouteProofHashFromRecording(rec);

            Assert.NotEqual(RouteProofHasher.NoRouteProofSentinel, hash);
            Assert.Equal(hash, RouteProofHasher.ComputeRouteProofHashFromRecording(rec));
        }

        // catches: the run-manifest quantities not participating in the
        // fingerprint - a rewrite that changes the witnessed start/end amounts
        // must flip the hash.
        [Fact]
        public void Hash_ChangesWhenRunManifestPresent()
        {
            Recording recA = RecordingWithProof();
            string hashWithout = RouteProofHasher.ComputeRouteProofHashFromRecording(recA);

            Recording recB = RecordingWithProof();
            recB.RouteRunManifest = new RouteRunCargoManifest
            {
                TransportPartPersistentIds = new List<uint> { 11u, 22u },
                StartTransportResources = new Dictionary<string, ResourceAmount>
                {
                    { "Ore", new ResourceAmount { amount = 0.0, maxAmount = 100.0 } }
                },
                EndCaptured = false
            };
            string hashWith = RouteProofHasher.ComputeRouteProofHashFromRecording(recB);

            Assert.NotEqual(hashWithout, hashWith);

            // ...and the quantity itself is load-bearing.
            recB.RouteRunManifest.StartTransportResources["Ore"] =
                new ResourceAmount { amount = 50.0, maxAmount = 100.0 };
            Assert.NotEqual(hashWith, RouteProofHasher.ComputeRouteProofHashFromRecording(recB));
        }

        // catches: the explicit completion marker not participating - a
        // start-only manifest and a complete-with-no-resources manifest are
        // different witnessed states (round-2 correction 5).
        [Fact]
        public void Hash_ChangesOnRunManifestEndCaptured()
        {
            var recA = new Recording
            {
                RecordingId = "rec-end-captured",
                RouteRunManifest = new RouteRunCargoManifest
                {
                    TransportPartPersistentIds = new List<uint> { 11u },
                    EndCaptured = false
                }
            };
            string startOnly = RouteProofHasher.ComputeRouteProofHashFromRecording(recA);

            recA.RouteRunManifest.EndCaptured = true;
            string complete = RouteProofHasher.ComputeRouteProofHashFromRecording(recA);

            Assert.NotEqual(startOnly, complete);
        }

        private static RouteHarvestWindow HarvestWindowFixture()
        {
            return new RouteHarvestWindow
            {
                WindowId = "harvest-1000",
                StartUT = 1000.0,
                EndUT = 1600.5,
                OpenedAtRecordingStart = true,
                ClosedAtRecordingStop = false,
                StartTransportResources = new Dictionary<string, ResourceAmount>
                {
                    { "Ore", new ResourceAmount { amount = 0.0, maxAmount = 1500.0 } }
                },
                EndTransportResources = new Dictionary<string, ResourceAmount>
                {
                    { "Ore", new ResourceAmount { amount = 850.25, maxAmount = 1500.0 } }
                }
            };
        }

        // catches (M2 D10 gate pin / review BLOCKER 3): a recording carrying
        // ONLY harvest windows must be hashable - the sentinel gate covers all
        // four data forms.
        [Fact]
        public void Hash_HarvestOnlyRecording_NotSentinel()
        {
            var rec = new Recording
            {
                RecordingId = "rec-harvest-only",
                RouteHarvestWindows = new List<RouteHarvestWindow> { HarvestWindowFixture() }
            };

            string hash = RouteProofHasher.ComputeRouteProofHashFromRecording(rec);

            Assert.NotEqual(RouteProofHasher.NoRouteProofSentinel, hash);
            Assert.Equal(hash, RouteProofHasher.ComputeRouteProofHashFromRecording(rec));
        }

        // catches: harvest-window quantities or span markers not participating
        // in the fingerprint.
        [Fact]
        public void Hash_ChangesOnHarvestWindowQuantitiesAndSpan()
        {
            Recording recA = RecordingWithProof();
            string baseline = RouteProofHasher.ComputeRouteProofHashFromRecording(recA);

            Recording recB = RecordingWithProof();
            recB.RouteHarvestWindows = new List<RouteHarvestWindow> { HarvestWindowFixture() };
            string withWindow = RouteProofHasher.ComputeRouteProofHashFromRecording(recB);
            Assert.NotEqual(baseline, withWindow);

            recB.RouteHarvestWindows[0].EndTransportResources["Ore"] =
                new ResourceAmount { amount = 1.0, maxAmount = 1500.0 };
            string changedQuantity = RouteProofHasher.ComputeRouteProofHashFromRecording(recB);
            Assert.NotEqual(withWindow, changedQuantity);

            recB.RouteHarvestWindows[0].EndTransportResources["Ore"] =
                new ResourceAmount { amount = 850.25, maxAmount = 1500.0 };
            recB.RouteHarvestWindows[0].StartUT = 999.0;
            Assert.NotEqual(withWindow, RouteProofHasher.ComputeRouteProofHashFromRecording(recB));
        }

        // catches (M2 D10 exclusion pin, the M1 D5 precedent): the open-time
        // location fields and ActiveConverters strings leaking into the hash.
        // They are resolution/diagnostic metadata, not the witnessed transfer -
        // including them would flip routes to SourceChanged on harmless
        // metadata edits.
        [Fact]
        public void Hash_IgnoresHarvestLocationAndConverterIds()
        {
            var recA = new Recording
            {
                RecordingId = "rec-harvest-loc",
                RouteHarvestWindows = new List<RouteHarvestWindow> { HarvestWindowFixture() }
            };
            string hashA = RouteProofHasher.ComputeRouteProofHashFromRecording(recA);

            var recB = new Recording
            {
                RecordingId = "rec-harvest-loc",
                RouteHarvestWindows = new List<RouteHarvestWindow> { HarvestWindowFixture() }
            };
            recB.RouteHarvestWindows[0].BodyName = "Minmus";
            recB.RouteHarvestWindows[0].Latitude = -0.55;
            recB.RouteHarvestWindows[0].Longitude = 78.25;
            recB.RouteHarvestWindows[0].Altitude = 2412.5;
            recB.RouteHarvestWindows[0].SituationAtOpen = 1;
            recB.RouteHarvestWindows[0].ActiveConverters = new List<string>
            {
                "100:ModuleResourceHarvester:Drill-O-Matic"
            };
            string hashB = RouteProofHasher.ComputeRouteProofHashFromRecording(recB);

            Assert.Equal(hashA, hashB);
        }

        // catches (M2 review follow-up MINOR 3): the sticky RunManifestVoided
        // tombstone leaking into the hash or its gate. It is capture-lifecycle
        // bookkeeping, not the witnessed transfer - hashing it would flip a
        // route to SourceChanged the moment its source leg backgrounds.
        [Fact]
        public void Hash_UnchangedByRunManifestVoidedFlag()
        {
            Recording recA = RecordingWithProof();
            string hashA = RouteProofHasher.ComputeRouteProofHashFromRecording(recA);

            Recording recB = RecordingWithProof();
            recB.RunManifestVoided = true;
            Assert.Equal(hashA, RouteProofHasher.ComputeRouteProofHashFromRecording(recB));

            // The flag alone does not make a proof-less recording hashable
            // either - it stays at the sentinel like any pre-M2 recording.
            var voidedOnly = new Recording
            {
                RecordingId = "voided-only",
                RunManifestVoided = true
            };
            Assert.Equal(RouteProofHasher.NoRouteProofSentinel,
                RouteProofHasher.ComputeRouteProofHashFromRecording(voidedOnly));
        }

        // catches (M2 D10 byte-stability pin): ANY drift in the canonical bytes
        // emitted for a recording WITHOUT the M2 run-manifest / harvest-window
        // fields. The constant below was computed against the pre-M2 hasher
        // (gate = windows||origin only, no sparse-append blocks); the widened
        // gate and the appended blocks must emit NOTHING for absent data, so
        // every pre-M2 recording keeps this exact fingerprint and
        // RouteStore.RevalidateSources never flips existing routes to
        // SourceChanged on load.
        [Fact]
        public void Hash_PreM2Recording_ByteStable()
        {
            Recording rec = RecordingWithProof();
            Assert.Equal(
                "538f2b91a99a6139",
                RouteProofHasher.ComputeRouteProofHashFromRecording(rec));
        }

        // catches (M3 D9 byte-stability pin): the recording-side proof hash drifting
        // because of the M3 pickup direction. M3 (plan D8/D9) adds the pickup
        // direction as a DERIVED, per-stop ROUTE-shape field (RouteStop.PickupManifest
        // serialized via RouteCodec, NOT a Recording field) - NO new hashed
        // RouteConnectionWindow field, NO RouteProofHasher / RouteProofCodec /
        // RouteProofMetadata change. So the recording proof hash MUST stay
        // byte-identical to the pre-M2 / pre-M3 fingerprint, and
        // RouteStore.RevalidateSources never flips a pre-M3 / delivery-only route to
        // SourceChanged on load. The constant is the SAME as
        // Hash_PreM2Recording_ByteStable on purpose: M3 touched nothing the hasher
        // reads. A drift here means a pickup direction leaked into the recording proof.
        [Fact]
        public void Hash_PreM3Recording_ByteStable()
        {
            Recording rec = RecordingWithProof();
            Assert.Equal(
                "538f2b91a99a6139",
                RouteProofHasher.ComputeRouteProofHashFromRecording(rec));
        }

        // catches: the origin endpoint descriptor fields (M1 / D5) leaking into
        // the proof hash. They are DELIBERATELY excluded: the hash pins the
        // witnessed transfer, coordinates are resolution metadata. Including
        // them would flip every pre-descriptor docked-origin route to
        // SourceChanged on load via RouteStore.RevalidateSources.
        [Fact]
        public void Hash_UnchangedByOriginDescriptorFields()
        {
            Recording recA = RecordingWithProof();
            string hashA = RouteProofHasher.ComputeRouteProofHashFromRecording(recA);

            Recording recB = RecordingWithProof();
            recB.RouteOriginProof.StartDockedOriginBodyName = "Minmus";
            recB.RouteOriginProof.StartDockedOriginLatitude = -0.55;
            recB.RouteOriginProof.StartDockedOriginLongitude = 78.25;
            recB.RouteOriginProof.StartDockedOriginAltitude = 2412.5;
            recB.RouteOriginProof.StartDockedOriginIsSurface = true;
            recB.RouteOriginProof.StartDockedOriginSituation = 1;
            string hashB = RouteProofHasher.ComputeRouteProofHashFromRecording(recB);

            Assert.Equal(hashA, hashB);
        }
    }
}
