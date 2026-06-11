using System.Collections.Generic;
using Parsek;
using Parsek.Logistics;
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
        }

        public void Dispose()
        {
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
