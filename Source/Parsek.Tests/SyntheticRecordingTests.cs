using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using Parsek.Rendering;
using Parsek.Tests.Generators;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Synthetic recording generator and manual save injector. Concurrency hazard:
    /// InjectAllRecordings purges <c>saves/&lt;save&gt;/Parsek/Recordings/</c> before
    /// rewriting fixtures, so never point it at a save a live KSP session is
    /// using. The purge path probes <c>KSP.log</c> and refuses when KSP appears
    /// active, but contributors should still treat reinjection as a closed-KSP
    /// operation.
    /// </summary>
    [Collection("Sequential")]
    public class SyntheticRecordingTests
    {
        private static string ProjectRoot => ResolveProjectRoot();

        private static string ResolveProjectRoot()
        {
            string current = Directory.GetCurrentDirectory();
            for (int i = 0; i < 10; i++)
            {
                if (string.IsNullOrEmpty(current))
                    break;

                if (File.Exists(Path.Combine(current, "Source", "Parsek.sln")))
                    return current;

                var parent = Directory.GetParent(current);
                if (parent == null)
                    break;
                current = parent.FullName;
            }

            // Fallback to previous relative behavior from test output directory.
            return Path.GetFullPath(
                Path.Combine(Directory.GetCurrentDirectory(), "..", "..", "..", "..", ".."));
        }

        private static string ResolveKspRoot()
        {
            string envKsp = System.Environment.GetEnvironmentVariable("KSPDIR");
            if (!string.IsNullOrWhiteSpace(envKsp))
            {
                string full = Path.GetFullPath(envKsp);
                if (Directory.Exists(full))
                    return full;
            }

            string envKspAlt = System.Environment.GetEnvironmentVariable("KSPDir");
            if (!string.IsNullOrWhiteSpace(envKspAlt))
            {
                string full = Path.GetFullPath(envKspAlt);
                if (Directory.Exists(full))
                    return full;
            }

            string[] candidates =
            {
                Path.Combine(ProjectRoot, "Kerbal Space Program"),
                Path.Combine(ProjectRoot, "..", "Kerbal Space Program")
            };

            for (int i = 0; i < candidates.Length; i++)
            {
                string candidate = Path.GetFullPath(candidates[i]);
                if (Directory.Exists(candidate))
                    return candidate;
            }

            // Keep previous behavior for CI-safe skip path when save is absent.
            return Path.GetFullPath(Path.Combine(ProjectRoot, "Kerbal Space Program"));
        }

        #region Recording Builders

        // Offsets from baseUT for each recording (30s apart)
        // Pad Walk:          +30s  to +60s   (30s EVA ghost)
        // KSC Hopper:        +60s  to +116s  (56s ghost)
        // Flea Flight:       +90s  to +180s  (90s vessel spawn)
        // Suborbital Arc:    +120s to +420s  (300s ghost)
        // KSC Pad Destroyed: +150s to +162s  (12s sphere ghost)
        // Orbit-1:           +180s to +3180s (3000s vessel spawn)
        // Close Spawn:       +210s to +222s  (12s vessel spawn)
        // Island Probe:      +240s to +420s  (180s vessel spawn)
        // EVA Board Chain:   +270s to +340s  (3-segment chain, 70s total)
        // EVA Walk Chain:    +350s to +450s  (2-segment V→EVA chain, 100s total)
        // Kerbin Ascent:     +430s to +610s  (3-segment chain, 180s total)
        // Mun Transfer:      +630s to +1330s (4-segment chain, 700s total)
        // Reentry East:      +0s   to +90s   (90s looped ghost, reentry FX)
        // Reentry Shallow:   +0s   to +120s  (120s looped ghost, reentry FX)
        // Reentry South:     +0s   to +90s   (90s looped ghost, reentry FX)

        // Approximate "upright on surface" rotation at KSC for UT ~17000.
        // Surface-relative rotation for upright vessel at KSC pad.
        // Captured empirically from v.srfRelRotation in KSP runtime.
        private const float KscRotX = -0.7009714f, KscRotY = -0.09230039f, KscRotZ = -0.09728389f, KscRotW = 0.7004681f;

        internal static RecordingBuilder PadWalk(double baseUT = 0)
        {
            // EVA: Jeb walks ~200m along the road east of the VAB (flat terrain, no structures)
            double t = baseUT + 30;
            var b = new RecordingBuilder("Pad Walk");
            b.WithDefaultRotation(KscRotX, KscRotY, KscRotZ, KscRotW);
            double baseLat = -0.0465;
            double baseLon = -74.6100;

            b.AddPoint(t,     baseLat, baseLon,          69);
            b.AddPoint(t+3,   baseLat, baseLon + 0.0002, 69);
            b.AddPoint(t+6,   baseLat, baseLon + 0.0004, 69);
            b.AddPoint(t+9,   baseLat, baseLon + 0.0006, 69);
            b.AddPoint(t+12,  baseLat, baseLon + 0.0008, 69);
            b.AddPoint(t+15,  baseLat, baseLon + 0.0010, 69);
            b.AddPoint(t+18,  baseLat, baseLon + 0.0012, 69);
            b.AddPoint(t+21,  baseLat, baseLon + 0.0014, 69);
            b.AddPoint(t+24,  baseLat, baseLon + 0.0016, 69);
            b.AddPoint(t+30,  baseLat, baseLon + 0.0018, 69);

            // Ghost-only EVA snapshot (no vessel spawn, no crew reservation)
            b.WithGhostVisualSnapshot(
                VesselSnapshotBuilder.EvaKerbal("Jebediah Kerman", pid: 55555555)
                    .AsLanded(baseLat, baseLon + 0.0018, 69));

            return b;
        }

        internal static RecordingBuilder KscHopper(double baseUT = 0)
        {
            double t = baseUT + 60;
            var b = new RecordingBuilder("KSC Hopper");
            b.WithDefaultRotation(KscRotX, KscRotY, KscRotZ, KscRotW);
            double baseLat = -0.0972;
            double baseLon = -74.5575;

            b.AddPoint(t,    baseLat, baseLon, 77);
            b.AddPoint(t+4,  baseLat, baseLon + 0.0003, 150);
            b.AddPoint(t+8,  baseLat, baseLon + 0.0008, 300);
            b.AddPoint(t+12, baseLat, baseLon + 0.0015, 500);
            b.AddPoint(t+16, baseLat, baseLon + 0.0024, 490);
            b.AddPoint(t+20, baseLat, baseLon + 0.0032, 460);
            b.AddPoint(t+24, baseLat, baseLon + 0.0038, 420);
            b.AddPoint(t+28, baseLat + 0.0002, baseLon + 0.0043, 370);
            b.AddPoint(t+32, baseLat + 0.0004, baseLon + 0.0047, 310);
            b.AddPoint(t+36, baseLat + 0.0005, baseLon + 0.0050, 250);
            b.AddPoint(t+40, baseLat + 0.0006, baseLon + 0.0052, 200);
            b.AddPoint(t+44, baseLat + 0.0006, baseLon + 0.0053, 150);
            b.AddPoint(t+48, baseLat + 0.0007, baseLon + 0.0054, 110);
            b.AddPoint(t+52, baseLat + 0.0007, baseLon + 0.0054, 88);
            b.AddPoint(t+56, baseLat + 0.0007, baseLon + 0.0054, 77);

            // Engine events: SRB ignition at launch, shutdown at burnout
            b.AddPartEvent(t, 101111, 5, "solidBooster.sm.v2", value: 1f);       // EngineIgnited
            b.AddPartEvent(t + 8, 101111, 6, "solidBooster.sm.v2");              // EngineShutdown

            // Part events: SRB decouple + parachute deploy
            b.AddPartEvent(t + 8, 101111, 0, "solidBooster.sm.v2");    // Decoupled
            b.AddPartEvent(t + 40, 102222, 2, "parachuteSingle");      // ParachuteDeployed

            // Ghost-only: visual fidelity without vessel spawn or crew reservation
            b.WithGhostVisualSnapshot(
                VesselSnapshotBuilder.FleaRocket("KSC Hopper", "Valentina Kerman", pid: 11111111)
                    .AsLanded(baseLat + 0.0007, baseLon + 0.0054, 77));

            return b;
        }

        internal static RecordingBuilder FleaFlight(double baseUT = 0)
        {
            // Real flight data: mk1pod.v2 + solidBooster.sm.v2 + parachuteSingle
            // Launch → 620m apex → parachute descent → landing
            double t = baseUT + 90;
            var b = new RecordingBuilder("Flea Flight");
            b.WithDefaultRotation(KscRotX, KscRotY, KscRotZ, KscRotW);
            double baseLat = -0.0972;
            double baseLon = -74.5575;

            b.AddPoint(t,     baseLat, baseLon, 77,                       funds: 42469);
            b.AddPoint(t+2,   baseLat, baseLon + 0.0001, 100,             funds: 42469);
            b.AddPoint(t+4,   baseLat, baseLon + 0.0004, 145,             funds: 48229, rep: 1.2f);
            b.AddPoint(t+6,   baseLat, baseLon + 0.0008, 200,             funds: 48229, rep: 1.2f);
            b.AddPoint(t+8,   baseLat, baseLon + 0.0014, 270,             funds: 48229, rep: 1.2f);
            b.AddPoint(t+10,  baseLat, baseLon + 0.0022, 345,             funds: 48229, rep: 1.2f);
            b.AddPoint(t+12,  baseLat, baseLon + 0.0032, 415,             funds: 48229, rep: 1.2f);
            b.AddPoint(t+14,  baseLat, baseLon + 0.0044, 480,             funds: 48229, rep: 1.2f);
            b.AddPoint(t+16,  baseLat, baseLon + 0.0056, 535,             funds: 48229, rep: 1.2f);
            b.AddPoint(t+18,  baseLat, baseLon + 0.0068, 580,             funds: 48229, rep: 1.2f);
            b.AddPoint(t+20,  baseLat, baseLon + 0.0080, 615,             funds: 48229, rep: 1.2f);
            // SRB burnout — coasting with lateral drift
            b.AddPoint(t+24,  baseLat, baseLon + 0.0095, 620,             funds: 48229, rep: 1.2f);
            b.AddPoint(t+28,  baseLat, baseLon + 0.0108, 605,             funds: 48229, rep: 1.2f);
            b.AddPoint(t+32,  baseLat, baseLon + 0.0118, 575,             funds: 48229, rep: 1.2f);
            b.AddPoint(t+36,  baseLat, baseLon + 0.0128, 535,             funds: 48229, rep: 1.2f);
            b.AddPoint(t+40,  baseLat, baseLon + 0.0135, 485,             funds: 48229, rep: 1.2f);
            b.AddPoint(t+45,  baseLat, baseLon + 0.0143, 415,             funds: 48229, rep: 1.2f);
            b.AddPoint(t+50,  baseLat, baseLon + 0.0150, 340,             funds: 48229, rep: 1.2f);
            // Parachute deploys — slow lateral drift under canopy
            b.AddPoint(t+55,  baseLat, baseLon + 0.0155, 270,             funds: 48229, rep: 1.2f);
            b.AddPoint(t+60,  baseLat, baseLon + 0.0160, 215,             funds: 48229, rep: 1.2f);
            b.AddPoint(t+65,  baseLat, baseLon + 0.0163, 170,             funds: 48229, rep: 1.2f);
            b.AddPoint(t+70,  baseLat, baseLon + 0.0166, 130,             funds: 48229, rep: 1.2f);
            b.AddPoint(t+75,  baseLat, baseLon + 0.0169, 100,             funds: 48229, rep: 1.2f);
            b.AddPoint(t+80,  baseLat, baseLon + 0.0172, 85,              funds: 48229, rep: 1.2f);
            b.AddPoint(t+85,  baseLat, baseLon + 0.0174, 78,              funds: 48229, rep: 1.2f);
            b.AddPoint(t+90,  baseLat, baseLon + 0.0175, 77,              funds: 48229, rep: 1.2f);

            // Engine events: SRB ignition at launch, shutdown at burnout
            b.AddPartEvent(t, 101111, 5, "solidBooster.sm.v2", value: 1f);       // EngineIgnited
            b.AddPartEvent(t + 20, 101111, 6, "solidBooster.sm.v2");             // EngineShutdown

            // Part events
            b.AddPartEvent(t + 20, 101111, 0, "solidBooster.sm.v2");   // Decoupled (SRB burnout)
            b.AddPartEvent(t + 50, 102222, 2, "parachuteSingle");      // ParachuteDeployed

            // Vessel spawn: FleaRocket with Bob — lands ~2km east of pad
            var snapshot = VesselSnapshotBuilder.FleaRocket("Flea Flight", "Bob Kerman", pid: 22222222)
                .AddResourceToPart(1, "SolidFuel", 0, 227)   // SRB emptied after flight
                .AsLanded(baseLat, baseLon + 0.0175, 77);
            b.WithVesselSnapshot(snapshot);
            b.WithTerrainHeightAtEnd(65.0);

            // Resource manifests: Flea has 227 SolidFuel at start, 0 at end
            b.WithStartResources(new Dictionary<string, ResourceAmount>
            {
                { "SolidFuel", new ResourceAmount { amount = 227.0, maxAmount = 227.0 } }
            });
            b.WithEndResources(new Dictionary<string, ResourceAmount>
            {
                { "SolidFuel", new ResourceAmount { amount = 0.0, maxAmount = 227.0 } }
            });

            return b;
        }

        internal static RecordingBuilder SuborbitalArc(double baseUT = 0)
        {
            double t = baseUT + 120;
            var b = new RecordingBuilder("Suborbital Arc");
            double lat = -0.0972;
            double lon = -74.5575;

            // Ascent with gravity-turn rotation (Z-axis pitch-over)
            b.AddPoint(t,     lat, lon, 77,                             rotZ: 0f,     rotW: 1f);
            b.AddPoint(t+5,   lat, lon, 300,                            rotZ: 0.044f, rotW: 0.999f);
            b.AddPoint(t+10,  lat, lon, 800,                            rotZ: 0.087f, rotW: 0.996f);
            b.AddPoint(t+15,  lat, lon + 0.001, 1800,                   rotZ: 0.131f, rotW: 0.991f);
            b.AddPoint(t+20,  lat, lon + 0.003, 3500,                   rotZ: 0.174f, rotW: 0.985f);
            b.AddPoint(t+25,  lat + 0.001, lon + 0.006, 6000,           rotZ: 0.216f, rotW: 0.976f);
            b.AddPoint(t+30,  lat + 0.002, lon + 0.01, 9500,            rotZ: 0.259f, rotW: 0.966f);
            // SRB burnout + decouple at t+30
            b.AddPoint(t+35,  lat + 0.003, lon + 0.015, 14000,          rotZ: 0.309f, rotW: 0.951f);
            b.AddPoint(t+40,  lat + 0.004, lon + 0.02, 19000,           rotZ: 0.342f, rotW: 0.940f);
            b.AddPoint(t+50,  lat + 0.006, lon + 0.035, 28000,          rotZ: 0.383f, rotW: 0.924f);
            b.AddPoint(t+60,  lat + 0.008, lon + 0.055, 37000,          rotZ: 0.423f, rotW: 0.906f);
            b.AddPoint(t+70,  lat + 0.01, lon + 0.08, 46000,            rotZ: 0.454f, rotW: 0.891f);
            b.AddPoint(t+80,  lat + 0.012, lon + 0.11, 55000,           rotZ: 0.500f, rotW: 0.866f);
            b.AddPoint(t+90,  lat + 0.014, lon + 0.14, 63000,           rotZ: 0.545f, rotW: 0.838f);
            b.AddPoint(t+100, lat + 0.015, lon + 0.17, 68000,           rotZ: 0.574f, rotW: 0.819f);
            b.AddPoint(t+110, lat + 0.016, lon + 0.20, 70500,           rotZ: 0.588f, rotW: 0.809f);
            b.AddPoint(t+120, lat + 0.017, lon + 0.23, 71000,           rotZ: 0.574f, rotW: 0.819f);
            // Apex — begin descent, nose tilting back
            b.AddPoint(t+130, lat + 0.018, lon + 0.26, 70200,           rotZ: 0.545f, rotW: 0.838f);
            b.AddPoint(t+140, lat + 0.019, lon + 0.29, 67000,           rotZ: 0.500f, rotW: 0.866f);
            b.AddPoint(t+160, lat + 0.021, lon + 0.35, 55000,           rotZ: 0.383f, rotW: 0.924f);
            b.AddPoint(t+180, lat + 0.023, lon + 0.42, 40000,           rotZ: 0.259f, rotW: 0.966f);
            b.AddPoint(t+210, lat + 0.026, lon + 0.52, 22000,           rotZ: 0.131f, rotW: 0.991f);
            b.AddPoint(t+240, lat + 0.028, lon + 0.62, 8000,            rotZ: 0.044f, rotW: 0.999f);
            // Parachute deploy at t+240, descent under canopy
            b.AddPoint(t+270, lat + 0.029, lon + 0.70, 1500,            rotZ: 0f,     rotW: 1f);
            b.AddPoint(t+300, lat + 0.03, lon + 0.75, 0,                rotZ: 0f,     rotW: 1f);

            // Engine events: SRB ignition at launch, shutdown at burnout
            b.AddPartEvent(t, 101111, 5, "solidBooster.sm.v2", value: 1f);       // EngineIgnited
            b.AddPartEvent(t + 30, 101111, 6, "solidBooster.sm.v2");             // EngineShutdown

            // Part event: SRB decouple after burnout
            b.AddPartEvent(t + 30, 101111, 0, "solidBooster.sm.v2");   // Decoupled

            // Ghost-only: visual fidelity without vessel spawn or crew reservation
            b.WithGhostVisualSnapshot(
                VesselSnapshotBuilder.FleaRocket("Suborbital Arc", "Bill Kerman", pid: 33333333)
                    .AsLanded(lat + 0.03, lon + 0.75, 0));

            return b;
        }

        internal static RecordingBuilder KscPadDestroyed(double baseUT = 0)
        {
            // Edge case: vessel destroyed near KSC pad. No vessel snapshot on purpose.
            double t = baseUT + 150;
            var b = new RecordingBuilder("KSC Pad Destroyed");
            b.WithDefaultRotation(KscRotX, KscRotY, KscRotZ, KscRotW);
            double lat = -0.0972;
            double lon = -74.5576;

            b.AddPoint(t + 0, lat, lon, 70, funds: 43429);
            b.AddPoint(t + 2, lat, lon, 95, funds: 43429);
            b.AddPoint(t + 4, lat, lon + 0.0002, 150, funds: 43429);
            b.AddPoint(t + 6, lat, lon + 0.0005, 210, funds: 43429);
            b.AddPoint(t + 8, lat, lon + 0.0008, 160, funds: 43429);
            b.AddPoint(t + 10, lat, lon + 0.0011, 90, funds: 43429);
            b.AddPoint(t + 12, lat, lon + 0.0012, 65, funds: 43429);

            // No snapshot intentionally to force destroyed/no-snapshot fallback path.
            b.WithTerminalState((int)TerminalState.Destroyed);
            return b;
        }

        internal static RecordingBuilder Orbit1(double baseUT = 0)
        {
            double t = baseUT + 180;
            var b = new RecordingBuilder("Orbit-1");
            double lat = -0.0972;
            double lon = -74.5575;

            // 11 ascent points with gravity-turn rotation
            b.AddPoint(t,     lat, lon, 77,                             rotZ: 0f,     rotW: 1f);
            b.AddPoint(t+50,  lat, lon + 0.005, 5000,                   rotZ: 0.131f, rotW: 0.991f);
            b.AddPoint(t+100, lat + 0.005, lon + 0.02, 15000,           rotZ: 0.259f, rotW: 0.966f);
            b.AddPoint(t+150, lat + 0.01, lon + 0.05, 30000,            rotZ: 0.383f, rotW: 0.924f);
            b.AddPoint(t+200, lat + 0.015, lon + 0.1, 45000,            rotZ: 0.500f, rotW: 0.866f);
            b.AddPoint(t+250, lat + 0.02, lon + 0.16, 58000,            rotZ: 0.588f, rotW: 0.809f);
            b.AddPoint(t+300, lat + 0.025, lon + 0.24, 68000,           rotZ: 0.643f, rotW: 0.766f);
            b.AddPoint(t+350, lat + 0.03, lon + 0.34, 75000,            rotZ: 0.683f, rotW: 0.731f);
            b.AddPoint(t+400, lat + 0.035, lon + 0.46, 79000,           rotZ: 0.700f, rotW: 0.714f);
            b.AddPoint(t+450, lat + 0.04, lon + 0.55, 80000,            rotZ: 0.707f, rotW: 0.707f);
            // Engine staging at t+450 — orbit insertion burn complete
            b.AddPoint(t+500, lat + 0.045, lon + 0.60, 80000,           rotZ: 0.707f, rotW: 0.707f);

            // Engine events: liquid engine ignition at launch, shutdown before staging
            b.AddPartEvent(t, 102222, 5, "liquidEngine", value: 1f);             // EngineIgnited
            b.AddPartEvent(t + 450, 102222, 6, "liquidEngine");                  // EngineShutdown

            // Part event: engine stage separation before orbit
            // VesselSnapshotBuilder part uids: 100000 (pod), 101111 (tank), 102222 (engine)
            b.AddPartEvent(t + 450, 102222, 0, "liquidEngine");  // Decoupled

            // Orbit segment starts at last ascent point
            double segStart = t + 500;
            double segEnd = t + 3000;
            b.AddOrbitSegment(segStart, segEnd,
                inc: 28.5, ecc: 0.001, sma: 700000,
                lan: 90, argPe: 45, mna: 0, epoch: segStart,
                body: "Kerbin",
                ofrY: 1f);

            // Multi-part vessel: command pod + fuel tank + engine (Y-up positions)
            b.WithVesselSnapshot(
                VesselSnapshotBuilder.CrewedShip("Orbit-1", "Bill Kerman", pid: 12345678)
                    .AddPart("fuelTank", position: "0,-0.75,0")
                    .AddPart("liquidEngine", position: "0,-1.5,0")
                    .AsOrbiting(sma: 700000, ecc: 0.001, inc: 28.5,
                        lan: 90, argPe: 45, mna: 0, epoch: segStart));

            return b;
        }

        internal static RecordingBuilder CloseSpawnConflict(double baseUT = 0)
        {
            // Edge case: landed vessel very near KSC to exercise spawn offset logic.
            double t = baseUT + 210;
            var b = new RecordingBuilder("Close Spawn Conflict");
            b.WithDefaultRotation(KscRotX, KscRotY, KscRotZ, KscRotW);
            double lat = -0.09718;
            double lon = -74.55755;

            b.AddPoint(t + 0, lat, lon, 70);
            b.AddPoint(t + 4, lat + 0.00002, lon + 0.00002, 70);
            b.AddPoint(t + 8, lat + 0.00003, lon + 0.00003, 70);
            b.AddPoint(t + 12, lat + 0.00003, lon + 0.00003, 70);

            b.WithVesselSnapshot(
                VesselSnapshotBuilder.FleaRocket("Close Spawn Conflict", "Jebediah Kerman", pid: 44444444)
                    .AsLanded(lat + 0.00003, lon + 0.00003, 70));
            b.WithTerrainHeightAtEnd(65.0);

            return b;
        }

        internal static RecordingBuilder IslandProbe(double baseUT = 0)
        {
            double t = baseUT + 240;
            var b = new RecordingBuilder("Island Probe");
            b.WithDefaultRotation(KscRotX, KscRotY, KscRotZ, KscRotW);
            double startLat = -0.0972;
            double startLon = -74.5575;
            double endLat = -1.52;
            double endLon = -71.97;

            b.AddPoint(t,     startLat, startLon, 77);
            b.AddPoint(t+10,  startLat, startLon, 200);
            b.AddPoint(t+20,  -0.15, -74.40, 400);
            b.AddPoint(t+30,  -0.25, -74.20, 600);
            b.AddPoint(t+40,  -0.40, -73.95, 800);
            b.AddPoint(t+50,  -0.55, -73.70, 900);
            b.AddPoint(t+60,  -0.70, -73.45, 950);
            b.AddPoint(t+70,  -0.82, -73.20, 1000);
            b.AddPoint(t+80,  -0.93, -72.95, 1000);
            b.AddPoint(t+90,  -1.03, -72.70, 1000);
            b.AddPoint(t+100, -1.12, -72.50, 1000);
            b.AddPoint(t+110, -1.20, -72.30, 950);
            b.AddPoint(t+120, -1.28, -72.15, 900);
            b.AddPoint(t+130, -1.34, -72.05, 800);
            b.AddPoint(t+140, -1.39, -71.98, 600);
            b.AddPoint(t+150, -1.43, -71.97, 400);
            b.AddPoint(t+160, -1.47, -71.97, 200);
            b.AddPoint(t+170, -1.50, -71.97, 80);
            b.AddPoint(t+175, -1.51, -71.97, 50);
            b.AddPoint(t+180, endLat, endLon, 40);

            b.WithVesselSnapshot(
                VesselSnapshotBuilder.FleaRocket("Island Probe", "Valentina Kerman", pid: 87654321)
                    .AsLanded(endLat, endLon, 40));
            b.WithTerrainHeightAtEnd(35.0);

            return b;
        }

        internal static RecordingBuilder PipelineOutlierKraken(double baseUT = 0)
        {
            // Phase 8 manual fixture: a mostly smooth atmospheric section with
            // one single-tick >2.5 km position spike. The immediate return-to-
            // path sample is also bubble-rejected, leaving 10/12 clean samples
            // so the spline can still fit and the cluster bit stays below the
            // 20% section-wide warning threshold.
            double t = baseUT + 260;
            double baseLat = -0.0972;
            double baseLon = -74.5575;
            double baseAlt = 900.0;
            Quaternion rot = new Quaternion(KscRotX, KscRotY, KscRotZ, KscRotW);

            var frames = new List<TrajectoryPoint>();
            var b = new RecordingBuilder("Pipeline Outlier Kraken")
                .WithRecordingId("pipeline-outlier-kraken")
                .WithDefaultRotation(KscRotX, KscRotY, KscRotZ, KscRotW);

            for (int i = 0; i < 12; i++)
            {
                double ut = t + i;
                double lat = baseLat + i * 0.00001;
                double lon = baseLon + i * 0.00001;
                double alt = baseAlt + i * 5.0;
                if (i == 5)
                {
                    lat = baseLat + 0.25;
                    lon = baseLon + 0.25;
                    alt = baseAlt + 300.0;
                }

                b.AddPoint(ut, lat, lon, alt);
                frames.Add(new TrajectoryPoint
                {
                    ut = ut,
                    latitude = lat,
                    longitude = lon,
                    altitude = alt,
                    rotation = rot,
                    velocity = Vector3.zero,
                    bodyName = "Kerbin",
                    recordedGroundClearance = double.NaN
                });
            }

            b.AddTrackSection(
                SegmentEnvironment.Atmospheric,
                ReferenceFrame.Absolute,
                TrackSectionSource.Active,
                t,
                t + 11.0,
                frames,
                new List<OrbitSegment>(),
                sampleRateHz: 1.0f);
            return b;
        }

        [Fact]
        public void PipelineOutlierKrakenFixture_ClassifiesBubbleSpikeWithoutCluster()
        {
            RecordingBuilder builder = PipelineOutlierKraken(baseUT: 1000.0);
            Assert.Equal("pipeline-outlier-kraken", builder.GetRecordingId());

            var rec = new Recording
            {
                RecordingId = builder.GetRecordingId(),
                TrackSections = builder.GetTrackSections()
            };
            OutlierFlags flags = OutlierClassifier.Classify(rec, 0, OutlierThresholds.Default);

            Assert.Equal(2, flags.RejectedCount);
            Assert.True(flags.IsRejected(5));
            Assert.True(flags.IsRejected(6));
            Assert.Equal(
                (byte)OutlierClassifier.ClassifierBit.BubbleRadius,
                (byte)(flags.ClassifierMask & (byte)OutlierClassifier.ClassifierBit.BubbleRadius));
            Assert.Equal(
                0,
                flags.ClassifierMask & (byte)OutlierClassifier.ClassifierBit.Cluster);
        }

        internal static RecordingBuilder[] EvaBoardChain(double baseUT = 0)
        {
            string chainId = "chain-eva-board-test";
            double t = baseUT + 270;
            double baseLat = -0.0972;
            double baseLon = -74.5575;

            // Segment 0: Vessel launch, 30s — FleaRocket with Jeb
            var seg0 = new RecordingBuilder("Flea Chain")
                .WithDefaultRotation(KscRotX, KscRotY, KscRotZ, KscRotW)
                .WithRecordingId("chain-seg0")
                .WithChainId(chainId)
                .WithChainIndex(0);

            seg0.AddPoint(t,    baseLat, baseLon,          77);
            seg0.AddPoint(t+5,  baseLat, baseLon + 0.0003, 150);
            seg0.AddPoint(t+10, baseLat, baseLon + 0.0008, 280);
            seg0.AddPoint(t+15, baseLat, baseLon + 0.0014, 350);
            seg0.AddPoint(t+20, baseLat, baseLon + 0.0020, 300);
            seg0.AddPoint(t+25, baseLat, baseLon + 0.0025, 200);
            seg0.AddPoint(t+30, baseLat, baseLon + 0.0028, 100);

            // Engine events: SRB ignition and burnout
            seg0.AddPartEvent(t, 101111, 5, "solidBooster.sm.v2", value: 1f);
            seg0.AddPartEvent(t + 15, 101111, 6, "solidBooster.sm.v2");
            seg0.AddPartEvent(t + 15, 101111, 0, "solidBooster.sm.v2"); // Decouple

            // Ghost-only (mid-chain — no VesselSnapshot)
            seg0.WithGhostVisualSnapshot(
                VesselSnapshotBuilder.FleaRocket("Flea Chain", "Jebediah Kerman", pid: 66666666)
                    .AsLanded(baseLat, baseLon + 0.0028, 100));

            // Segment 1: EVA walk, 20s — Jeb walks from vessel landing point
            double evaLon = baseLon + 0.0028;
            var seg1 = new RecordingBuilder("Jebediah Kerman")
                .WithDefaultRotation(KscRotX, KscRotY, KscRotZ, KscRotW)
                .WithRecordingId("chain-seg1")
                .WithChainId(chainId)
                .WithChainIndex(1)
                .WithParentRecordingId("chain-seg0")
                .WithEvaCrewName("Jebediah Kerman");

            seg1.AddPoint(t + 30, baseLat, evaLon,          100); // boundary anchor
            seg1.AddPoint(t + 35, baseLat, evaLon + 0.0002, 95);
            seg1.AddPoint(t + 40, baseLat, evaLon + 0.0004, 85);
            seg1.AddPoint(t + 45, baseLat, evaLon + 0.0006, 77);
            seg1.AddPoint(t + 50, baseLat, evaLon + 0.0008, 70);

            // Ghost-only (EVA mid-chain)
            seg1.WithGhostVisualSnapshot(
                VesselSnapshotBuilder.EvaKerbal("Jebediah Kerman", pid: 77777777)
                    .AsLanded(baseLat, evaLon + 0.0008, 70));

            // Segment 2: Vessel resume, 20s — back on FleaRocket
            double resumeLon = evaLon + 0.0008;
            var seg2 = new RecordingBuilder("Flea Chain")
                .WithDefaultRotation(KscRotX, KscRotY, KscRotZ, KscRotW)
                .WithRecordingId("chain-seg2")
                .WithChainId(chainId)
                .WithChainIndex(2)
                .WithParentRecordingId("chain-seg1");

            seg2.AddPoint(t + 50, baseLat, resumeLon,          70);  // boundary anchor
            seg2.AddPoint(t + 55, baseLat, resumeLon + 0.0003, 150);
            seg2.AddPoint(t + 60, baseLat, resumeLon + 0.0008, 250);
            seg2.AddPoint(t + 65, baseLat, resumeLon + 0.0012, 200);
            seg2.AddPoint(t + 70, baseLat, resumeLon + 0.0015, 100);

            // Final segment: has VesselSnapshot (spawns!)
            seg2.WithVesselSnapshot(
                VesselSnapshotBuilder.FleaRocket("Flea Chain", "Jebediah Kerman", pid: 66666666)
                    .AsLanded(baseLat, resumeLon + 0.0015, 100));
            seg2.WithTerrainHeightAtEnd(65.0);

            return new[] { seg0, seg1, seg2 };
        }

        /// <summary>
        /// 2-segment V→EVA chain based on real test flight: FleaRocket launches,
        /// arcs ~1km east, lands, then Bill goes EVA and walks ~25m.
        /// Ghost-only (no VesselSnapshot) — tests chain ghost playback and holding.
        /// </summary>
        internal static RecordingBuilder[] EvaWalkChain(double baseUT = 0)
        {
            string chainId = "chain-eva-walk-test";
            double t = baseUT + 350;
            double baseLat = -0.0972;
            double baseLon = -74.5575;

            // Segment 0: Vessel flight — FleaRocket launch, arc east, land ~1km away (75s)
            var seg0 = new RecordingBuilder("Landing Craft")
                .WithDefaultRotation(KscRotX, KscRotY, KscRotZ, KscRotW)
                .WithRecordingId("chain-walk-seg0")
                .WithChainId(chainId)
                .WithChainIndex(0);

            // Launch phase — vertical then tilting east
            seg0.AddPoint(t,      baseLat, baseLon,            77);
            seg0.AddPoint(t + 3,  baseLat, baseLon + 0.0002,   120);
            seg0.AddPoint(t + 6,  baseLat, baseLon + 0.0006,   200);
            seg0.AddPoint(t + 9,  baseLat, baseLon + 0.0012,   300);
            seg0.AddPoint(t + 12, baseLat, baseLon + 0.0020,   390);
            seg0.AddPoint(t + 15, baseLat, baseLon + 0.0030,   453); // apex
            // Descent
            seg0.AddPoint(t + 20, baseLat, baseLon + 0.0042,   420);
            seg0.AddPoint(t + 25, baseLat, baseLon + 0.0054,   360);
            seg0.AddPoint(t + 30, baseLat, baseLon + 0.0064,   290);
            seg0.AddPoint(t + 35, baseLat, baseLon + 0.0072,   220);
            seg0.AddPoint(t + 40, baseLat, baseLon + 0.0079,   160);
            seg0.AddPoint(t + 45, baseLat, baseLon + 0.0084,   110);
            seg0.AddPoint(t + 50, baseLat - 0.0005, baseLon + 0.0088, 80);
            // Parachute descent — slow final approach (KSC terrain ~67-69m)
            seg0.AddPoint(t + 55, baseLat - 0.0008, baseLon + 0.0091, 75);
            seg0.AddPoint(t + 60, baseLat - 0.0012, baseLon + 0.0093, 72);
            seg0.AddPoint(t + 65, baseLat - 0.0014, baseLon + 0.0094, 70);
            seg0.AddPoint(t + 70, baseLat - 0.0016, baseLon + 0.0095, 69);
            seg0.AddPoint(t + 75, baseLat - 0.0016, baseLon + 0.0095, 69); // landed

            // Engine events: SRB ignition and burnout
            seg0.AddPartEvent(t, 101111, 5, "solidBooster.sm.v2", value: 1f);
            seg0.AddPartEvent(t + 12, 101111, 6, "solidBooster.sm.v2");
            seg0.AddPartEvent(t + 12, 101111, 0, "solidBooster.sm.v2"); // Decouple
            seg0.AddPartEvent(t + 40, 102222, 2, "parachuteSingle");    // ParachuteDeployed

            // Continuation keeps VesselSnapshot — vessel spawns at landing position at chain end
            double landLat = baseLat - 0.0016;
            double landLon = baseLon + 0.0095;
            seg0.WithVesselSnapshot(
                VesselSnapshotBuilder.FleaRocket("Landing Craft", "Bill Kerman", pid: 88888888)
                    .AsLanded(landLat, landLon, 69));
            seg0.WithGhostVisualSnapshot(
                VesselSnapshotBuilder.FleaRocket("Landing Craft", "Bill Kerman", pid: 88888888)
                    .AsLanded(landLat, landLon, 69));
            seg0.WithTerrainHeightAtEnd(65.0);

            // Segment 1: EVA walk — Bill walks ~25m from landing point (25s)
            var seg1 = new RecordingBuilder("Bill Kerman")
                .WithDefaultRotation(KscRotX, KscRotY, KscRotZ, KscRotW)
                .WithRecordingId("chain-walk-seg1")
                .WithChainId(chainId)
                .WithChainIndex(1)
                .WithParentRecordingId("chain-walk-seg0")
                .WithEvaCrewName("Bill Kerman");

            // Boundary anchor — same position as vessel's final point
            seg1.AddPoint(t + 75, landLat,           landLon,            69);
            seg1.AddPoint(t + 80, landLat - 0.0001,  landLon + 0.0001,  69);
            seg1.AddPoint(t + 85, landLat - 0.0002,  landLon + 0.0002,  69);
            seg1.AddPoint(t + 90, landLat - 0.0003,  landLon + 0.0002,  69);
            seg1.AddPoint(t + 95, landLat - 0.0003,  landLon + 0.0003,  69);
            seg1.AddPoint(t + 100, landLat - 0.0004, landLon + 0.0003,  69);

            // Ghost-only EVA snapshot
            seg1.WithGhostVisualSnapshot(
                VesselSnapshotBuilder.EvaKerbal("Bill Kerman", pid: 99999999)
                    .AsLanded(landLat - 0.0004, landLon + 0.0003, 69));

            return new[] { seg0, seg1 };
        }

        /// <summary>
        /// 3-segment atmosphere boundary chain: atmo ascent → exo coast → atmo reentry.
        /// Tests SegmentPhase/SegmentBodyName serialization and chain grouping in UI.
        /// </summary>
        internal static RecordingBuilder[] KerbinAscentChain(double baseUT = 0)
        {
            string chainId = "chain-atmo-split-test";
            double t = baseUT + 430;
            double baseLat = -0.0972;
            double baseLon = -74.5575;

            // Segment 0: Atmospheric ascent (0-60s, 77m → 71000m)
            var seg0 = new RecordingBuilder("Kerbin Ascent")
                .WithDefaultRotation(KscRotX, KscRotY, KscRotZ, KscRotW)
                .WithRecordingId("chain-atmo-seg0")
                .WithChainId(chainId)
                .WithChainIndex(0)
                .WithSegmentPhase("atmo")
                .WithSegmentBodyName("Kerbin");

            seg0.AddPoint(t,      baseLat, baseLon,            77);
            seg0.AddPoint(t + 10, baseLat, baseLon + 0.001,    5000);
            seg0.AddPoint(t + 20, baseLat, baseLon + 0.003,    15000);
            seg0.AddPoint(t + 30, baseLat, baseLon + 0.007,    30000);
            seg0.AddPoint(t + 40, baseLat, baseLon + 0.012,    50000);
            seg0.AddPoint(t + 50, baseLat, baseLon + 0.018,    65000);
            seg0.AddPoint(t + 60, baseLat, baseLon + 0.025,    71000); // boundary crossing

            seg0.WithGhostVisualSnapshot(
                VesselSnapshotBuilder.FleaRocket("Kerbin Ascent", "Valentina Kerman", pid: 77000000)
                    .AsLanded(baseLat, baseLon, 77));

            // Segment 1: Exo-atmospheric coast (60-120s, 71000m → 80000m → 71000m)
            var seg1 = new RecordingBuilder("Kerbin Ascent")
                .WithDefaultRotation(KscRotX, KscRotY, KscRotZ, KscRotW)
                .WithRecordingId("chain-atmo-seg1")
                .WithChainId(chainId)
                .WithChainIndex(1)
                .WithParentRecordingId("chain-atmo-seg0")
                .WithSegmentPhase("exo")
                .WithSegmentBodyName("Kerbin");

            seg1.AddPoint(t + 60,  baseLat, baseLon + 0.025,  71000); // boundary anchor
            seg1.AddPoint(t + 70,  baseLat, baseLon + 0.032,  75000);
            seg1.AddPoint(t + 80,  baseLat, baseLon + 0.040,  80000); // apoapsis
            seg1.AddPoint(t + 90,  baseLat, baseLon + 0.048,  78000);
            seg1.AddPoint(t + 100, baseLat, baseLon + 0.055,  74000);
            seg1.AddPoint(t + 110, baseLat, baseLon + 0.062,  72000);
            seg1.AddPoint(t + 120, baseLat, baseLon + 0.068,  71000); // re-entry boundary

            seg1.WithGhostVisualSnapshot(
                VesselSnapshotBuilder.FleaRocket("Kerbin Ascent", "Valentina Kerman", pid: 77000000)
                    .AsLanded(baseLat, baseLon, 77));

            // Segment 2: Atmospheric reentry (120-180s, 71000m → 55m landed)
            var seg2 = new RecordingBuilder("Kerbin Ascent")
                .WithDefaultRotation(KscRotX, KscRotY, KscRotZ, KscRotW)
                .WithRecordingId("chain-atmo-seg2")
                .WithChainId(chainId)
                .WithChainIndex(2)
                .WithParentRecordingId("chain-atmo-seg1")
                .WithSegmentPhase("atmo")
                .WithSegmentBodyName("Kerbin");

            double landLat = baseLat + 0.005;
            double landLon = baseLon + 0.075;
            seg2.AddPoint(t + 120, baseLat,           baseLon + 0.068, 71000); // boundary anchor
            seg2.AddPoint(t + 130, baseLat + 0.001,   baseLon + 0.070, 55000);
            seg2.AddPoint(t + 140, baseLat + 0.002,   baseLon + 0.072, 35000);
            seg2.AddPoint(t + 150, baseLat + 0.003,   baseLon + 0.073, 15000);
            seg2.AddPoint(t + 160, baseLat + 0.004,   baseLon + 0.074, 5000);
            seg2.AddPoint(t + 170, landLat - 0.0005,  landLon - 0.0005, 500);
            seg2.AddPoint(t + 180, landLat,            landLon,          55); // landed

            seg2.WithVesselSnapshot(
                VesselSnapshotBuilder.FleaRocket("Kerbin Ascent", "Valentina Kerman", pid: 77000000)
                    .AsLanded(landLat, landLon, 55));
            seg2.WithGhostVisualSnapshot(
                VesselSnapshotBuilder.FleaRocket("Kerbin Ascent", "Valentina Kerman", pid: 77000000)
                    .AsLanded(baseLat, baseLon, 77));
            seg2.WithTerrainHeightAtEnd(55.0);

            return new[] { seg0, seg1, seg2 };
        }

        /// <summary>
        /// 4-segment chain: Kerbin atmo → Kerbin exo → Mun space → Kerbin exo (return).
        /// Tests SOI-change auto-split with orbit segments at the Mun.
        /// </summary>
        internal static RecordingBuilder[] KerbinMunTransfer(double baseUT = 0)
        {
            string chainId = "chain-mun-transfer-test";
            double t = baseUT + 630;
            double baseLat = -0.0972;
            double baseLon = -74.5575;

            // Segment 0: Atmospheric ascent (0-60s)
            var seg0 = new RecordingBuilder("Mun Transfer")
                .WithDefaultRotation(KscRotX, KscRotY, KscRotZ, KscRotW)
                .WithRecordingId("chain-mun-seg0")
                .WithChainId(chainId)
                .WithChainIndex(0)
                .WithSegmentPhase("atmo")
                .WithSegmentBodyName("Kerbin");

            seg0.AddPoint(t,      baseLat, baseLon,            77);
            seg0.AddPoint(t + 20, baseLat, baseLon + 0.003,    25000);
            seg0.AddPoint(t + 40, baseLat, baseLon + 0.012,    55000);
            seg0.AddPoint(t + 60, baseLat, baseLon + 0.025,    71000);

            seg0.WithGhostVisualSnapshot(
                VesselSnapshotBuilder.FleaRocket("Mun Transfer", "Bill Kerman", pid: 88000000)
                    .AsLanded(baseLat, baseLon, 77));

            // Segment 1: Kerbin exo — coast to Mun SOI (60-300s, with orbit segment)
            var seg1 = new RecordingBuilder("Mun Transfer")
                .WithDefaultRotation(KscRotX, KscRotY, KscRotZ, KscRotW)
                .WithRecordingId("chain-mun-seg1")
                .WithChainId(chainId)
                .WithChainIndex(1)
                .WithParentRecordingId("chain-mun-seg0")
                .WithSegmentPhase("exo")
                .WithSegmentBodyName("Kerbin");

            seg1.AddPoint(t + 60,  baseLat, baseLon + 0.025, 71000);
            seg1.AddPoint(t + 80,  baseLat, baseLon + 0.04,  100000);
            seg1.AddPoint(t + 100, baseLat, baseLon + 0.06,  150000);
            // Orbit segment for coast phase
            seg1.AddOrbitSegment(t + 100, t + 300,
                inc: 0, ecc: 0.7, sma: 3000000,
                lan: 0, argPe: 0, mna: 0.5, epoch: t + 100,
                body: "Kerbin");
            seg1.AddPoint(t + 300, 5.0, -60.0, 500000); // approaching Mun SOI

            seg1.WithGhostVisualSnapshot(
                VesselSnapshotBuilder.FleaRocket("Mun Transfer", "Bill Kerman", pid: 88000000)
                    .AsLanded(baseLat, baseLon, 77));

            // Segment 2: Mun space — orbit the Mun (300-500s)
            var seg2 = new RecordingBuilder("Mun Transfer")
                .WithDefaultRotation(KscRotX, KscRotY, KscRotZ, KscRotW)
                .WithRecordingId("chain-mun-seg2")
                .WithChainId(chainId)
                .WithChainIndex(2)
                .WithParentRecordingId("chain-mun-seg1")
                .WithSegmentPhase("space")
                .WithSegmentBodyName("Mun");

            seg2.AddPoint(t + 300, 0.0, 0.0, 50000, body: "Mun");
            // Orbit segment around the Mun
            seg2.AddOrbitSegment(t + 300, t + 500,
                inc: 5, ecc: 0.01, sma: 250000,
                lan: 0, argPe: 90, mna: 0, epoch: t + 300,
                body: "Mun");
            seg2.AddPoint(t + 500, 0.0, 90.0, 50000, body: "Mun");

            seg2.WithGhostVisualSnapshot(
                VesselSnapshotBuilder.FleaRocket("Mun Transfer", "Bill Kerman", pid: 88000000)
                    .AsLanded(baseLat, baseLon, 77));

            // Segment 3: Return to Kerbin exo (500-700s)
            var seg3 = new RecordingBuilder("Mun Transfer")
                .WithDefaultRotation(KscRotX, KscRotY, KscRotZ, KscRotW)
                .WithRecordingId("chain-mun-seg3")
                .WithChainId(chainId)
                .WithChainIndex(3)
                .WithParentRecordingId("chain-mun-seg2")
                .WithSegmentPhase("exo")
                .WithSegmentBodyName("Kerbin");

            seg3.AddPoint(t + 500, 5.0, -60.0, 500000);
            seg3.AddOrbitSegment(t + 500, t + 700,
                inc: 0, ecc: 0.8, sma: 4000000,
                lan: 0, argPe: 180, mna: 2.5, epoch: t + 500,
                body: "Kerbin");
            seg3.AddPoint(t + 700, baseLat + 1.0, baseLon + 2.0, 100000);

            seg3.WithVesselSnapshot(
                VesselSnapshotBuilder.FleaRocket("Mun Transfer", "Bill Kerman", pid: 88000000)
                    .AsOrbiting(sma: 4000000, ecc: 0.8, inc: 0));
            seg3.WithGhostVisualSnapshot(
                VesselSnapshotBuilder.FleaRocket("Mun Transfer", "Bill Kerman", pid: 88000000)
                    .AsLanded(baseLat, baseLon, 77));

            return new[] { seg0, seg1, seg2, seg3 };
        }

        internal static RecordingBuilder TruncatedPlaneCruise(double baseUT = 0)
        {
            double t = baseUT + 760;
            double lat = -0.115;
            double lon = -74.30;
            var b = new RecordingBuilder("Truncated Plane Cruise");
            b.WithDefaultRotation(KscRotX, KscRotY, KscRotZ, KscRotW);

            b.AddPoint(t + 0, lat, lon, 3000);
            b.AddPoint(t + 5, lat + 0.003, lon + 0.060, 3050);
            b.AddPoint(t + 10, lat + 0.006, lon + 0.120, 3090);
            b.AddPoint(t + 15, lat + 0.009, lon + 0.180, 3120);
            b.AddPoint(t + 20, lat + 0.012, lon + 0.240, 3140);
            b.AddPoint(t + 25, lat + 0.015, lon + 0.300, 3160);

            b.WithGhostVisualSnapshot(
                VesselSnapshotBuilder.FleaRocket("Truncated Plane Cruise", "Jebediah Kerman", pid: 88110001)
                    .AsLanded(lat + 0.015, lon + 0.300, 3160));

            return b;
        }

        internal static RecordingBuilder TruncatedSuborbitalRecording(double baseUT = 0)
        {
            double t = baseUT + 820;
            double lat = -0.0972;
            double lon = -74.5575;
            var b = new RecordingBuilder("Truncated Suborbital Recording");
            b.WithDefaultRotation(KscRotX, KscRotY, KscRotZ, KscRotW);

            b.AddPoint(t + 0, lat, lon, 15000);
            b.AddPoint(t + 20, lat + 0.004, lon + 0.040, 32000);
            b.AddPoint(t + 40, lat + 0.008, lon + 0.090, 52000);
            b.AddPoint(t + 60, lat + 0.011, lon + 0.145, 64500);
            b.AddPoint(t + 80, lat + 0.013, lon + 0.190, 70500);
            b.AddOrbitSegment(t + 80, t + 260,
                inc: 4.0, ecc: 0.78, sma: 980000,
                lan: 90, argPe: 45, mna: 0.2, epoch: t + 80,
                body: "Kerbin");

            b.WithGhostVisualSnapshot(
                VesselSnapshotBuilder.FleaRocket("Truncated Suborbital Recording", "Valentina Kerman", pid: 88110002)
                    .AsLanded(lat + 0.013, lon + 0.190, 70500));

            return b;
        }

        internal static RecordingBuilder TruncatedHyperbolicRecording(double baseUT = 0)
        {
            double t = baseUT + 900;
            double lat = -0.020;
            double lon = -72.800;
            var b = new RecordingBuilder("Truncated Hyperbolic Recording");
            b.WithDefaultRotation(KscRotX, KscRotY, KscRotZ, KscRotW);

            b.AddPoint(t + 0, lat, lon, 82000);
            b.AddPoint(t + 30, lat + 0.010, lon + 0.150, 130000);
            b.AddPoint(t + 60, lat + 0.020, lon + 0.320, 210000);
            b.AddPoint(t + 90, lat + 0.028, lon + 0.520, 320000);
            b.AddOrbitSegment(t + 60, t + 3600,
                inc: 7.0, ecc: 1.12, sma: -4500000,
                lan: 105, argPe: 15, mna: 0.1, epoch: t + 60,
                body: "Kerbin");

            b.WithVesselSnapshot(
                VesselSnapshotBuilder.FleaRocket("Truncated Hyperbolic Recording", "Bob Kerman", pid: 88110003)
                    .AsOrbiting(sma: 700000, ecc: 0.001, inc: 7.0, lan: 105, argPe: 15, mna: 0.1, epoch: t + 60));

            return b;
        }

        internal static RecordingBuilder TruncatedMunFlybyRecording(double baseUT = 0)
        {
            double t = baseUT + 980;
            double lat = -0.010;
            double lon = -69.500;
            var b = new RecordingBuilder("Truncated Mun Flyby Recording");
            b.WithDefaultRotation(KscRotX, KscRotY, KscRotZ, KscRotW);

            b.AddPoint(t + 0, lat, lon, 90000);
            b.AddPoint(t + 40, lat + 0.020, lon + 0.220, 180000);
            b.AddPoint(t + 80, lat + 0.050, lon + 0.480, 320000);
            b.AddPoint(t + 120, lat + 0.090, lon + 0.760, 510000);
            b.AddOrbitSegment(t + 60, t + 2100,
                inc: 5.0, ecc: 0.92, sma: 4200000,
                lan: 75, argPe: 20, mna: 0.4, epoch: t + 60,
                body: "Kerbin");

            b.WithVesselSnapshot(
                VesselSnapshotBuilder.FleaRocket("Truncated Mun Flyby Recording", "Bill Kerman", pid: 88110004)
                    .AsOrbiting(sma: 4200000, ecc: 0.92, inc: 5.0, lan: 75, argPe: 20, mna: 0.4, epoch: t + 60));

            return b;
        }

        internal static RecordingBuilder TruncatedMunImpactRecording(double baseUT = 0)
        {
            double t = baseUT + 1060;
            double lat = 0.0;
            double lon = 30.0;
            var b = new RecordingBuilder("Truncated Mun Impact Recording");
            b.WithDefaultRotation(KscRotX, KscRotY, KscRotZ, KscRotW);

            b.AddPoint(t + 0, lat, lon, 45000, body: "Mun");
            b.AddPoint(t + 20, lat + 0.004, lon + 0.080, 32000, body: "Mun");
            b.AddPoint(t + 40, lat + 0.008, lon + 0.150, 18000, body: "Mun");
            b.AddPoint(t + 60, lat + 0.011, lon + 0.210, 12000, body: "Mun");
            b.AddOrbitSegment(t + 20, t + 180,
                inc: 2.0, ecc: 0.12, sma: 185000,
                lan: 25, argPe: 110, mna: 0.3, epoch: t + 20,
                body: "Mun");

            b.WithGhostVisualSnapshot(
                VesselSnapshotBuilder.FleaRocket("Truncated Mun Impact Recording", "Bill Kerman", pid: 88110005)
                    .AsLanded(lat + 0.011, lon + 0.210, 12000));

            return b;
        }

        /// <summary>
        /// Steep reentry capsule descending over the sea east of KSC toward the island.
        /// High speed + mid-atmosphere density produces bright reentry FX trail.
        /// </summary>
        internal static RecordingBuilder ReentryEast(double baseUT = 0)
        {
            double t = baseUT;
            var b = new RecordingBuilder("Reentry East");
            b.WithDefaultRotation(KscRotX, KscRotY, KscRotZ, KscRotW);

            // 70km → splashdown, heading east over sea toward island
            b.AddPoint(t,      -0.35, -74.20, 70000);
            b.AddPoint(t + 8,  -0.38, -73.80, 62000);
            b.AddPoint(t + 16, -0.42, -73.35, 52000);
            b.AddPoint(t + 24, -0.47, -72.90, 42000);
            b.AddPoint(t + 32, -0.52, -72.50, 33000);
            b.AddPoint(t + 40, -0.57, -72.20, 25000);
            b.AddPoint(t + 48, -0.62, -71.98, 18000);
            b.AddPoint(t + 56, -0.66, -71.82, 12000);
            b.AddPoint(t + 66, -0.71, -71.70, 6000);
            b.AddPoint(t + 76, -0.75, -71.64, 2000);
            b.AddPoint(t + 90, -0.80, -71.60, 0);

            // Parachute deploy during slow descent
            b.AddPartEvent(t + 66, 102222, 2, "parachuteSingle"); // ParachuteDeployed

            b.WithGhostVisualSnapshot(
                VesselSnapshotBuilder.ReentryCapsule("Reentry East", "Jebediah Kerman", pid: 90000001)
                    .AsLanded(-0.80, -71.60, 0));

            return b;
        }

        /// <summary>
        /// Shallow-angle reentry capsule on a long arc over the sea.
        /// More horizontal travel produces a longer, more gradual reentry trail.
        /// </summary>
        internal static RecordingBuilder ReentryShallow(double baseUT = 0)
        {
            double t = baseUT;
            var b = new RecordingBuilder("Reentry Shallow");
            b.WithDefaultRotation(KscRotX, KscRotY, KscRotZ, KscRotW);

            // 68km → splashdown, shallow descent angle
            b.AddPoint(t,       -0.20, -74.30, 68000);
            b.AddPoint(t + 10,  -0.24, -73.80, 62000);
            b.AddPoint(t + 20,  -0.30, -73.25, 54000);
            b.AddPoint(t + 30,  -0.37, -72.70, 46000);
            b.AddPoint(t + 40,  -0.45, -72.20, 38000);
            b.AddPoint(t + 50,  -0.53, -71.80, 30000);
            b.AddPoint(t + 60,  -0.60, -71.50, 23000);
            b.AddPoint(t + 70,  -0.66, -71.28, 17000);
            b.AddPoint(t + 80,  -0.72, -71.12, 12000);
            b.AddPoint(t + 90,  -0.77, -71.00, 8000);
            b.AddPoint(t + 105, -0.83, -70.92, 3000);
            b.AddPoint(t + 120, -0.88, -70.88, 0);

            // Parachute deploy
            b.AddPartEvent(t + 90, 102222, 2, "parachuteSingle"); // ParachuteDeployed

            b.WithGhostVisualSnapshot(
                VesselSnapshotBuilder.ReentryCapsule("Reentry Shallow", "Bill Kerman", pid: 90000002)
                    .AsLanded(-0.88, -70.88, 0));

            return b;
        }

        /// <summary>
        /// Reentry capsule approaching from the north, arcing south toward the island.
        /// Different visual angle from the east-heading trajectories.
        /// </summary>
        internal static RecordingBuilder ReentrySouth(double baseUT = 0)
        {
            double t = baseUT;
            var b = new RecordingBuilder("Reentry South");
            b.WithDefaultRotation(KscRotX, KscRotY, KscRotZ, KscRotW);

            // 65km → splashdown, heading south over sea toward island
            b.AddPoint(t,      0.20,  -73.20, 65000);
            b.AddPoint(t + 8,  0.05,  -73.10, 57000);
            b.AddPoint(t + 16, -0.12, -73.00, 48000);
            b.AddPoint(t + 24, -0.30, -72.88, 39000);
            b.AddPoint(t + 32, -0.48, -72.75, 30000);
            b.AddPoint(t + 40, -0.65, -72.62, 22000);
            b.AddPoint(t + 50, -0.85, -72.45, 14000);
            b.AddPoint(t + 60, -1.00, -72.30, 7000);
            b.AddPoint(t + 72, -1.12, -72.18, 2500);
            b.AddPoint(t + 90, -1.22, -72.10, 0);

            // Parachute deploy
            b.AddPartEvent(t + 60, 102222, 2, "parachuteSingle"); // ParachuteDeployed

            b.WithGhostVisualSnapshot(
                VesselSnapshotBuilder.ReentryCapsule("Reentry South", "Valentina Kerman", pid: 90000003)
                    .AsLanded(-1.22, -72.10, 0));

            return b;
        }

        /// <summary>
        /// Generalized helper for building a static-trajectory, looping showcase recording
        /// that toggles a pair of part events every 3 seconds. Used for lights, deployables,
        /// gear, cargo bays, engines, and deployed science fixtures.
        /// </summary>
        private static RecordingBuilder BuildPartShowcaseRecording(
            double baseUT, string vesselName, string partName, int rowIndex,
            double distanceFromPadMeters, PartEventType onEvent, PartEventType offEvent,
            uint pidBase, uint evtPid, float eventValue = 0f, int moduleIndex = 0,
            Action<ConfigNode> configureGhostPartNode = null,
            double firstEventOffsetSeconds = 3.0,
            double onDurationSeconds = 3.0,
            double offDurationSeconds = 3.0,
            string companionPartName = null,
            string companionPartPosition = null,
            string companionPartRotation = null,
            double rowOffsetMeters = 0.0,
            double distanceOffsetMeters = 0.0)
        {
            double t = baseUT + 30;
            ShowcasePosition(rowIndex, distanceFromPadMeters, out double lat, out double lon, out double alt,
                rowOffsetMeters, distanceOffsetMeters);
            alt += ShowcaseAltitudeOffset(partName);

            var b = new RecordingBuilder(vesselName)
                .WithDefaultRotation(KscRotX, KscRotY, KscRotZ, KscRotW)
                .WithLoopPlayback(loop: true, intervalSeconds: 0.0)
                .WithRecordingGroup("Part Showcases");

            // Static trajectory (24s) so the visual focus is part event playback.
            for (int i = 0; i <= 8; i++)
                b.AddPoint(t + (i * 3), lat, lon, alt);

            // Toggle events across the clip. Category builders can bias on/off timing.
            double eventOffset = firstEventOffsetSeconds;
            double onStep = onDurationSeconds > 0 ? onDurationSeconds : 3.0;
            double offStep = offDurationSeconds > 0 ? offDurationSeconds : 3.0;
            bool on = true;
            for (int evtIndex = 0; evtIndex < 8; evtIndex++)
            {
                b.AddPartEvent(
                    t + eventOffset,
                    pid: evtPid,
                    type: on ? (int)onEvent : (int)offEvent,
                    partName: partName,
                    value: on ? eventValue : 0f,
                    moduleIndex: moduleIndex);
                eventOffset += on ? onStep : offStep;
                on = !on;
            }

            // rotY(-90°): upright fixture facing east (away from pad).
            var snapshotBuilder = new VesselSnapshotBuilder()
                .WithName(vesselName)
                .WithPersistentId((uint)(pidBase + rowIndex))
                .AddPart(partName, rotation: "0,-0.7071068,0,0.7071068");

            if (!string.IsNullOrEmpty(companionPartName))
            {
                snapshotBuilder.AddPart(
                    companionPartName,
                    position: companionPartPosition ?? "2.25,0,0",
                    rotation: companionPartRotation ?? "0,0.7071068,0,0.7071068",
                    parentIndex: 0);
            }

            var snap = snapshotBuilder
                .AsLanded(lat, lon, alt)
                .Build();

            if (configureGhostPartNode != null)
            {
                var partNodes = snap.GetNodes("PART");
                if (partNodes != null && partNodes.Length > 0)
                    configureGhostPartNode(partNodes[0]);
            }

            b.WithGhostVisualSnapshot(snap);

            return b;
        }

        private static RecordingBuilder BuildInflatableHeatShieldShowcaseRecording(
            double baseUT, int rowIndex, double distanceFromPadMeters)
        {
            const string partName = "InflatableHeatShield";
            const string vesselName = "Part Showcase - Inflatable Heat Shield";

            double t = baseUT + 30;
            ShowcasePosition(rowIndex, distanceFromPadMeters, out double lat, out double lon, out double alt);
            alt += ShowcaseAltitudeOffset(partName);

            var b = new RecordingBuilder(vesselName)
                .WithDefaultRotation(KscRotX, KscRotY, KscRotZ, KscRotW)
                .WithLoopPlayback(loop: true, intervalSeconds: 0.0)
                .WithRecordingGroup("Part Showcases");

            for (int i = 0; i <= 8; i++)
                b.AddPoint(t + (i * 3), lat, lon, alt);

            // Two-stage visual playback:
            // 1) jettison fairing shell, 2) inflate/deflate shield body.
            b.AddPartEvent(t + 0.5, SinglePartPid, (int)PartEventType.ShroudJettisoned, partName);
            b.AddPartEvent(t + 1.0, SinglePartPid, (int)PartEventType.DeployableExtended, partName);
            b.AddPartEvent(t + 4.5, SinglePartPid, (int)PartEventType.DeployableRetracted, partName);
            b.AddPartEvent(t + 7.5, SinglePartPid, (int)PartEventType.DeployableExtended, partName);
            b.AddPartEvent(t + 10.5, SinglePartPid, (int)PartEventType.DeployableRetracted, partName);
            b.AddPartEvent(t + 13.5, SinglePartPid, (int)PartEventType.DeployableExtended, partName);
            b.AddPartEvent(t + 16.5, SinglePartPid, (int)PartEventType.DeployableRetracted, partName);
            b.AddPartEvent(t + 19.5, SinglePartPid, (int)PartEventType.DeployableExtended, partName);

            var snap = new VesselSnapshotBuilder()
                .WithName(vesselName)
                .WithPersistentId((uint)(SpecialDeployShowcasePidBase + rowIndex))
                .AddPart(partName, rotation: "0,-0.7071068,0,0.7071068")
                .AsLanded(lat, lon, alt)
                .Build();

            b.WithGhostVisualSnapshot(snap);
            return b;
        }

        // The first part added by VesselSnapshotBuilder gets persistentId = 100000.
        // Event PIDs must match this so the ghost visual builder can find the part.
        private const uint SinglePartPid = 100000;
        private const uint LightShowcasePidBase = 88000000;
        private const uint DeployableShowcasePidBase = 89000000;
        private const uint GearShowcasePidBase = 90000000;
        private const uint CargoBayShowcasePidBase = 91000000;
        private const uint LadderShowcasePidBase = 93000000;
        private const uint RcsShowcasePidBase = 94000000;
        private const uint FairingShowcasePidBase = 95000000;
        private const uint RadiatorShowcasePidBase = 96000000;
        private const uint DrillShowcasePidBase = 97000000;
        private const uint DeployedScienceShowcasePidBase = 98000000;
        private const uint InventoryPlacementPid = 98100000;
        private const uint AnimationGroupShowcasePidBase = 98200000;
        private const uint ParachuteShowcasePidBase = 98300000;
        private const uint SpecialDeployShowcasePidBase = 98400000;
        private const uint JettisonShowcasePidBase = 98500000;
        private const uint RoboticsShowcasePidBase = 98600000;
        private const uint AeroSurfaceShowcasePidBase = 98700000;
        private const uint RobotArmScannerShowcasePidBase = 98800000;
        private const uint ControlSurfaceShowcasePidBase = 98900000;
        private const uint WheelDynamicsShowcasePidBase = 99000000;
        private const uint AnimateHeatShowcasePidBase = 99100000;
        private const uint EngineShowcasePidBase = 99200000;
        private const uint ColorChangerShowcasePidBase = 99300000;
        private const uint FlagPlantShowcasePid = 99400000;
        // Optional companion part (e.g., kerbal actor) receives the second slot.
        // Total visible showcase row entries (indices 0-235, including inventory placement).
        private const int ShowcaseRowCount = 245;
        // Split showcase into three parallel lines to avoid runway clipping.
        private static readonly int ShowcaseEntriesPerLine = (ShowcaseRowCount + 2) / 3;
        private const double ShowcaseLineSpacingMeters = 20.0;
        // Keep showcases close to the launchpad centerline without overlapping pad geometry.
        private const double ShowcaseDistanceFromPadMeters = 200.0;
        // Target top height (meters above KSC ground level). Every part's top is placed at
        // this height. With Clydesdale (topY=10.8, total ~22.3m) the bottom sits ~5.7m
        // above ground. Small parts float at ~28m — fine at 200m viewing distance.
        private const double ShowcaseTargetTopHeight = 28.0;

        // Maps partName -> topY (meters above part origin, from node_stack_top Y or visual
        // estimate). Used to compute per-part altitude offset for top-aligned showcase layout.
        // Surface-attach-only parts (lights, control surfaces, RCS, etc.) use 0.0.
        private static readonly Dictionary<string, double> ShowcasePartTopY = new Dictionary<string, double>
        {
            // ── Lights (surface-attach) ──
            { "domeLight1", 0.0 },
            { "navLight1", 0.0 },
            { "stripLight1", 0.0 },
            { "spotLight3", 0.0 },
            { "groundLight1", 0.0 },
            { "groundLight2", 0.0 },
            { "spotLight1", 0.0 },
            { "spotLight1_v2", 0.0 },
            { "spotLight2", 0.0 },
            { "spotLight2_v2", 0.0 },

            // ── Solar panels / Antennas / Radiator (surface-attach or radial) ──
            { "solarPanels4", 0.0 },
            { "largeSolarPanel", 0.0 },
            { "LgRadialSolarPanel", 0.0 },
            { "solarPanelOX10C", 0.0 },
            { "solarPanelOX10L", 0.0 },
            { "solarPanels1", 0.0 },
            { "solarPanels2", 0.0 },
            { "solarPanels3", 0.0 },
            { "solarPanels5", 0.0 },
            { "solarPanelSP10C", 0.0 },
            { "solarPanelSP10L", 0.0 },
            { "longAntenna", 0.0 },
            { "commDish", 0.0 },
            { "HighGainAntenna", 0.0 },
            { "HighGainAntenna5", 0.0 },
            { "HighGainAntenna5.v2", 0.0 },
            { "mediumDishAntenna", 0.0 },
            { "foldingRadSmall", 0.0 },
            { "foldingRadMed", 0.0 },
            { "foldingRadLarge", 0.0 },

            // ── Landing gear (surface-attach) ──
            { "SmallGearBay", 0.0 },
            { "GearSmall", 0.0 },
            { "GearMedium", 0.0 },
            { "GearLarge", 0.0 },

            // ── Landing legs (surface-attach) ──
            { "landingLeg1", 0.0 },
            { "landingLeg1-2", 0.0 },
            { "miniLandingLeg", 0.0 },

            // ── Service bays / Cargo bays ──
            { "ServiceBay.125.v2", 0.3 },
            { "ServiceBay.250.v2", 0.65 },
            { "ServiceModule18", 0.75 },
            { "ServiceModule25", 1.55 },
            { "Size1to0ServiceModule", 0.3125 },
            { "mk2CargoBayS", 0.9375 },
            { "mk2CargoBayL", 1.875 },
            { "mk3CargoBayS", 1.25 },
            { "mk3CargoBayM", 2.5 },
            { "mk3CargoBayL", 5.0 },
            { "mk3CargoRamp", 3.0 },

            // ── Engines (EngineShowcaseRecordings — shroud jettison + flame) ──
            { "liquidEngineMainsail.v2", 1.01359 },
            { "engineLargeSkipper.v2", 1.013 },
            { "SSME", 0.0 },

            // ── Ladders (surface-attach) ──
            { "telescopicLadder", 0.0 },
            { "telescopicLadderBay", 0.0 },

            // ── RCS (surface-attach) ──
            { "RCSBlock.v2", 0.0 },
            { "RCSblock.01.small", 0.0 },
            { "RCSLinearSmall", 0.0 },
            { "linearRcs", 0.0 },
            { "vernierEngine", 0.0 },

            // ── Fairings ──
            { "fairingSize1", 0.22 },
            { "fairingSize1p5", 0.22 },
            { "fairingSize2", 0.22 },
            { "fairingSize3", 0.22 },
            { "fairingSize4", 0.22 },

            // ── ISRU / Scanners (ModuleAnimationGroup) ──
            { "ISRU", 1.5 },
            { "OrbitalScanner", 0.0 },

            // ── Drills (surface-attach / radial) ──
            { "MiniDrill", 0.0 },
            { "RadialDrill", 0.0 },

            // ── Deployed science (surface-attach) ──
            { "DeployedCentralStation", 0.0 },
            { "DeployedGoExOb", 0.0 },
            { "DeployedIONExp", 0.0 },
            { "DeployedRTG", 0.0 },
            { "DeployedSatDish", 0.0 },
            { "DeployedSeismicSensor", 0.0 },
            { "DeployedSolarPanel", 0.0 },
            { "DeployedWeatherStn", 0.0 },

            // ── Animation group / Survey / Anchor ──
            { "groundAnchor", 0.12236 },
            { "SurveyScanner", 0.0 },

            // ── Parachutes (surface-attach / radial) ──
            { "parachuteSingle", 0.0 },
            { "parachuteRadial", 0.0 },
            { "parachuteDrogue", 0.0 },
            { "radialDrogue", 0.0 },
            { "parachuteLarge", 0.0 },

            // ── Special deploy animations ──
            { "roverWheelM1-F", 0.0 },
            { "GooExperiment", 0.0 },
            { "science_module", 0.49 },
            { "Magnetometer", 0.0 },
            { "InflatableHeatShield", 1.4 },       // Inverted nodes: real top is node_stack_bottom Y
            { "InflatableAirlock", 0.0 },
            { "dockingPort1", 0.0 },
            { "dockingPortLateral", 0.5753132 },
            { "GrapplingDevice", 0.0 },             // node_stack_top Y is negative; treat as 0.0
            { "smallClaw", 0.0 },                    // node_stack_top Y is negative; treat as 0.0
            { "mk2DockingPort", 0.625 },
            { "mk2LanderCabin_v2", 0.751929 },

            // ── Jets (part showcase, non-flame) ──
            { "JetEngine", 0.972875 },
            { "turboFanSize2", 2.0 },

            // ── Robotics ──
            { "hinge.01", 0.3125 },
            { "hinge.01.s", 0.10348 },
            { "hinge.03", 0.0 },                    // Negative node_stack_top; treat as 0.0
            { "hinge.03.s", 0.0 },                  // Negative node_stack_top; treat as 0.0
            { "hinge.04", 0.0 },                    // Negative node_stack_top; treat as 0.0
            { "piston.01", 1.30408 },
            { "piston.02", 0.643867 },
            { "piston.03", 1.32439 },
            { "piston.04", 0.662193 },
            { "rotoServo.00", 0.21796 },
            { "rotoServo.02", 0.21796 },
            { "rotoServo.03", 0.62483 },
            { "rotoServo.04", 0.9375 },
            { "rotor.01", 0.415 },
            { "rotor.01s", 0.343347 },
            { "rotor.02", 0.42 },
            { "rotor.02s", 0.42 },
            { "rotor.03", 1.25 },
            { "rotor.03s", 0.955512 },
            { "RotorEngine.02", 0.415 },
            { "RotorEngine.03", 0.415 },

            // ── Airbrake (surface-attach) ──
            { "airbrake1", 0.0 },

            // ── Robot arm scanners (surface-attach) ──
            { "RobotArmScanner_S1", 0.0 },
            { "RobotArmScanner_S2", 0.0 },
            { "RobotArmScanner_S3", 0.0 },

            // ── Control surfaces (surface-attach) ──
            { "AdvancedCanard", 0.0 },
            { "airlinerCtrlSrf", 0.0 },
            { "airlinerTailFin", 0.0 },
            { "CanardController", 0.0 },
            { "elevon2", 0.0 },
            { "elevon3", 0.0 },
            { "elevon5", 0.0 },
            { "largeFanBlade", 0.0 },
            { "largeHeliBlade", 0.0 },
            { "largePropeller", 0.0 },
            { "mediumFanBlade", 0.0 },
            { "mediumHeliBlade", 0.0 },
            { "mediumPropeller", 0.0 },
            { "R8winglet", 0.0 },
            { "smallCtrlSrf", 0.0 },
            { "smallFanBlade", 0.0 },
            { "smallHeliBlade", 0.0 },
            { "smallPropeller", 0.0 },
            { "StandardCtrlSrf", 0.0 },
            { "tailfin", 0.0 },
            { "winglet3", 0.0 },
            { "wingShuttleElevon1", 0.0 },
            { "wingShuttleElevon2", 0.0 },
            { "wingShuttleRudder", 0.0 },

            // ── Wheel dynamics (surface-attach) ──
            { "GearFixed", 0.0 },
            { "GearFree", 0.0 },
            { "roverWheel1", 0.0 },
            { "roverWheel2", 0.0 },
            { "roverWheel3", 0.0 },
            { "wheelMed", 0.0 },

            // ── ColorChanger cabin lights (command pods) ──
            { "mk1pod.v2", 0.6424 },
            { "mk1-3pod", 1.2894 },
            { "cupola", 0.8518 },
            { "mk2LanderCabin.v2", 0.9019 },
            { "Mark1Cockpit", 0.6424 },
            { "mk2Cockpit.Standard", 0.5 },
            { "kv1Pod", 1.025 },
            { "kv2Pod", 1.025 },
            { "kv3Pod", 1.025 },
            { "landerCabinSmall", 0.625 },
            { "Mark2Cockpit", 0.9375 },
            { "mk2Cockpit.Inline", 1.25 },
            { "mk3Cockpit.Shuttle", 3.1875 },
            { "Mk2Pod", 1.0 },
            { "crewCabin", 0.986899 },
            { "MK1CrewCabin", 0.9375 },
            { "mk2CrewCabin", 0.9375 },
            { "mk3CrewCabin", 1.875 },
            { "Large.Crewed.Lab", 1.825 },
            { "MEMLander", 1.338 },
            { "dockingPort2", 0.2828832 },
            { "dockingPortLarge", 0.29 },

            // ── ColorChanger EVA (kerbal helmet light) ──
            { "kerbalEVA", 1.0 },                   // standalone EVA kerbal (~1m head height)
            { "kerbalEVAFuture", 0.0 },
            { "kerbalEVAfemaleFuture", 0.0 },

            // ── AnimateHeat parts ──
            { "airplaneTail", 0.0 },
            { "airplaneTailB", 0.0 },
            { "avionicsNoseCone", 0.0 },
            { "CircularIntake", 0.0 },
            { "MK1IntakeFuselage", 0.9375 },
            { "nacelleBody", 0.9375 },
            { "noseConeAdapter", 1.125 },
            { "pointyNoseConeA", 0.0 },
            { "pointyNoseConeB", 0.0 },
            { "radialEngineBody", 0.9375 },
            { "ramAirIntake", 0.0 },
            { "shockConeIntake", 0.0 },
            { "standardNoseCone", 0.0 },

            // ── Jettison coverage: engine plates ──
            { "EnginePlate1p5", 0.15 },
            { "EnginePlate2", 0.2 },
            { "EnginePlate3", 0.3 },
            { "EnginePlate4", 0.4 },
            { "EnginePlate5", 0.1 },

            // ── Jettison coverage: heat shields ──
            { "HeatShield1", 0.022 },
            { "HeatShield2", 0.034 },
            { "HeatShield3", 0.25 },
            { "HeatShield1p5", 0.125 },

            // ── Jettison coverage: engines (v1 / v2 / MH) ──
            { "liquidEngine", 0.721461 },           // scale=0.1 applied
            { "liquidEngine.v2", 0.0 },
            { "liquidEngine2", 0.721461 },           // scale=0.1 applied
            { "liquidEngine2.v2", 0.0 },
            { "liquidEngine2-2.v2", 0.0 },
            { "liquidEngine3.v2", 0.0 },
            { "liquidEngineMini.v2", 0.0 },
            { "nuclearEngine", 1.40383 },
            { "toroidalAerospike", 0.0 },
            { "Mite", 0.874462 },
            { "Shrimp", 1.98582 },
            { "solidBooster.sm.v2", 0.7575 },
            { "solidBooster.v2", 1.2818375 },
            { "Size3AdvancedEngine", 1.487975 },
            { "LiquidEngineKE-1", 0.8 },
            { "LiquidEngineLV-T91", 0.84028 },
            { "LiquidEngineLV-TX87", 0.76784 },
            { "LiquidEngineRE-I2", 1.80521 },
            { "LiquidEngineRE-J10", 0.361067 },
            { "LiquidEngineRK-7", 0.75 },

            // ── Engine flame showcase: additional liquid engines ──
            { "microEngine.v2", 0.0 },
            { "radialEngineMini.v2", 0.0 },
            { "smallRadialEngine", 0.0 },
            { "smallRadialEngine.v2", 0.0 },
            { "radialLiquidEngine1-2", 0.0 },
            { "omsEngine", 0.0 },
            { "Size2LFB.v2", 4.356 },
            { "Size3EngineCluster", 1.527248 },
            { "LiquidEngineRV-1", 0.0 },
            { "RAPIER", 0.741545 },

            // ── SRB flame showcase: additional SRBs ──
            { "solidBooster1-1", 3.92 },
            { "MassiveBooster", 7.429159 },
            { "Thoroughbred", 6.14614 },
            { "Clydesdale", 10.8 },
            { "Pollux", 7.83746 },
            { "sepMotor1", 0.0 },

            // ── Jet flame showcase ──
            { "miniJetEngine", 0.0 },
            { "turboFanEngine", 1.4 },
            { "turboJet", 0.0 },
            { "ionEngine", 0.2135562 },

            // ── Engine flame showcase: LES + MH tank/engine hybrid ──
            { "LaunchEscapeSystem", 0.0 },       // No node_stack_top; surface-attach LES tower
            { "Size1p5.Tank.05", 0.0 },           // No node_stack_top; integrated engine tank

            // ── Inventory placement (kerbalEVA companion, not offset) ──
            // DeployedWeatherStn already listed above.
        };

        /// <summary>
        /// Returns the altitude offset (meters above base altitude) so the part's top aligns
        /// with <see cref="ShowcaseTargetTopHeight"/> above KSC ground level.
        /// </summary>
        private static double ShowcaseAltitudeOffset(string partName)
        {
            if (ShowcasePartTopY.TryGetValue(partName, out double topY))
                return ShowcaseTargetTopHeight - topY;
            return ShowcaseTargetTopHeight; // fallback: assume origin is at top
        }

        /// <summary>
        /// Computes lat/lon/alt for a showcase row, splitting rows across three parallel lines.
        /// Line 0 (back): 200m from pad, Line 1 (middle): 220m from pad, Line 2 (front): 240m from pad.
        /// </summary>
        private static void ShowcasePosition(int rowIndex, double distanceFromPadMeters,
            out double lat, out double lon, out double alt,
            double rowOffsetMeters = 0.0, double distanceOffsetMeters = 0.0)
        {
            const double metersPerDegree = (2.0 * Math.PI * 600000.0) / 360.0;
            const double spacingMeters = 5.0;

            int lineIndex = rowIndex / ShowcaseEntriesPerLine;
            int lineRowIndex = rowIndex % ShowcaseEntriesPerLine;

            double rowCenterOffsetMeters = -((ShowcaseEntriesPerLine - 1) * spacingMeters * 0.5);
            lat = -0.0972 + ((lineRowIndex * spacingMeters + rowCenterOffsetMeters + rowOffsetMeters) / metersPerDegree);
            lon = -74.5575 + ((distanceFromPadMeters + lineIndex * ShowcaseLineSpacingMeters + distanceOffsetMeters) / metersPerDegree);
            alt = 116.0; // 50m above ground level so engine exhaust is visible from below
        }

        /// <summary>
        /// Builds a light showcase recording with an on → blink → off cycle that exercises
        /// all 5 light event types: LightOn, LightOff, LightBlinkEnabled, LightBlinkDisabled,
        /// LightBlinkRate. 24s clip with 9 trajectory points.
        /// </summary>
        private static RecordingBuilder BuildLightBlinkShowcaseRecording(
            double baseUT, string vesselName, string lightPartName, int rowIndex)
        {
            double t = baseUT + 30;
            ShowcasePosition(rowIndex, ShowcaseDistanceFromPadMeters, out double lat, out double lon, out double alt);
            alt += ShowcaseAltitudeOffset(lightPartName);

            var b = new RecordingBuilder(vesselName)
                .WithDefaultRotation(KscRotX, KscRotY, KscRotZ, KscRotW)
                .WithLoopPlayback(loop: true, intervalSeconds: 0.0)
                .WithRecordingGroup("Part Showcases");

            // Static trajectory (24s).
            for (int i = 0; i <= 8; i++)
                b.AddPoint(t + (i * 3), lat, lon, alt);

            // On → slow blink → solid → fast blink → faster blink → solid → off.
            b.AddPartEvent(t + 0.0,  SinglePartPid, (int)PartEventType.LightOn, lightPartName);
            b.AddPartEvent(t + 6.0,  SinglePartPid, (int)PartEventType.LightBlinkEnabled, lightPartName, value: 0.5f);
            b.AddPartEvent(t + 12.0, SinglePartPid, (int)PartEventType.LightBlinkDisabled, lightPartName);
            b.AddPartEvent(t + 15.0, SinglePartPid, (int)PartEventType.LightBlinkEnabled, lightPartName, value: 2.0f);
            b.AddPartEvent(t + 18.0, SinglePartPid, (int)PartEventType.LightBlinkRate, lightPartName, value: 5.0f);
            b.AddPartEvent(t + 21.0, SinglePartPid, (int)PartEventType.LightBlinkDisabled, lightPartName);
            b.AddPartEvent(t + 22.0, SinglePartPid, (int)PartEventType.LightOff, lightPartName);

            var snap = new VesselSnapshotBuilder()
                .WithName(vesselName)
                .WithPersistentId((uint)(LightShowcasePidBase + rowIndex))
                .AddPart(lightPartName, rotation: "0,-0.7071068,0,0.7071068")
                .AsLanded(lat, lon, alt)
                .Build();

            b.WithGhostVisualSnapshot(snap);
            return b;
        }

        /// <summary>
        /// Unified engine showcase builder. Handles liquid engines, SRBs, and jets with
        /// optional shroud jettison before the flame cycle.
        /// - Liquid/jet with shroud: ShroudJettisoned at t+0.5, then 7 throttle events (8 total)
        /// - Liquid/jet without shroud: 8 throttle events (ignite→ramp→full→shutdown→restart cycle)
        /// - SRB with shroud: ShroudJettisoned at t+0.5, then ignite+shutdown (3 total)
        /// - SRB without shroud: ignite+shutdown (2 events)
        /// 24s clip with 9 trajectory points.
        /// </summary>
        private static RecordingBuilder BuildCombinedEngineShowcaseRecording(
            double baseUT, string vesselName, string enginePartName, int rowIndex,
            uint pidBase, bool isSrb = false, bool hasShroud = false)
        {
            double t = baseUT + 30;
            ShowcasePosition(rowIndex, ShowcaseDistanceFromPadMeters, out double lat, out double lon, out double alt);
            alt += ShowcaseAltitudeOffset(enginePartName);

            var b = new RecordingBuilder(vesselName)
                .WithDefaultRotation(KscRotX, KscRotY, KscRotZ, KscRotW)
                .WithLoopPlayback(loop: true, intervalSeconds: 0.0)
                .WithRecordingGroup("Part Showcases");

            for (int i = 0; i <= 8; i++)
                b.AddPoint(t + (i * 3), lat, lon, alt);

            if (isSrb)
            {
                if (hasShroud)
                    b.AddPartEvent(t + 0.5, SinglePartPid, (int)PartEventType.ShroudJettisoned, enginePartName);
                double igniteT = hasShroud ? t + 3 : t + 0;
                b.AddPartEvent(igniteT,      SinglePartPid, (int)PartEventType.EngineIgnited,  enginePartName, value: 1.0f);
                b.AddPartEvent(igniteT + 12, SinglePartPid, (int)PartEventType.EngineShutdown, enginePartName);
            }
            else
            {
                if (hasShroud)
                {
                    b.AddPartEvent(t + 0.5, SinglePartPid, (int)PartEventType.ShroudJettisoned, enginePartName);
                    b.AddPartEvent(t + 3,   SinglePartPid, (int)PartEventType.EngineIgnited,  enginePartName, value: 0.3f);
                    b.AddPartEvent(t + 6,   SinglePartPid, (int)PartEventType.EngineThrottle, enginePartName, value: 0.7f);
                    b.AddPartEvent(t + 9,   SinglePartPid, (int)PartEventType.EngineThrottle, enginePartName, value: 1.0f);
                    b.AddPartEvent(t + 12,  SinglePartPid, (int)PartEventType.EngineShutdown, enginePartName);
                    b.AddPartEvent(t + 15,  SinglePartPid, (int)PartEventType.EngineIgnited,  enginePartName, value: 1.0f);
                    b.AddPartEvent(t + 18,  SinglePartPid, (int)PartEventType.EngineThrottle, enginePartName, value: 0.5f);
                    b.AddPartEvent(t + 21,  SinglePartPid, (int)PartEventType.EngineShutdown, enginePartName);
                }
                else
                {
                    b.AddPartEvent(t + 0,  SinglePartPid, (int)PartEventType.EngineIgnited,  enginePartName, value: 0.3f);
                    b.AddPartEvent(t + 3,  SinglePartPid, (int)PartEventType.EngineThrottle, enginePartName, value: 0.7f);
                    b.AddPartEvent(t + 6,  SinglePartPid, (int)PartEventType.EngineThrottle, enginePartName, value: 1.0f);
                    b.AddPartEvent(t + 9,  SinglePartPid, (int)PartEventType.EngineShutdown, enginePartName);
                    b.AddPartEvent(t + 12, SinglePartPid, (int)PartEventType.EngineIgnited,  enginePartName, value: 1.0f);
                    b.AddPartEvent(t + 15, SinglePartPid, (int)PartEventType.EngineThrottle, enginePartName, value: 0.5f);
                    b.AddPartEvent(t + 18, SinglePartPid, (int)PartEventType.EngineThrottle, enginePartName, value: 0.15f);
                    b.AddPartEvent(t + 21, SinglePartPid, (int)PartEventType.EngineShutdown, enginePartName);
                }
            }

            var snap = new VesselSnapshotBuilder()
                .WithName(vesselName)
                .WithPersistentId((uint)(pidBase + rowIndex))
                .AddPart(enginePartName, rotation: "0,-0.7071068,0,0.7071068")
                .AsLanded(lat, lon, alt)
                .Build();

            b.WithGhostVisualSnapshot(snap);
            return b;
        }

        /// <summary>
        /// RAPIER mode-switch showcase: jet mode (midx=0) then rocket mode (midx=1).
        /// Demonstrates per-module FX switching via EngineIgnited/EngineShutdown events.
        /// </summary>
        private static RecordingBuilder BuildRapierModeSwitchShowcase(
            double baseUT, int rowIndex, uint pidBase)
        {
            double t = baseUT + 30;
            ShowcasePosition(rowIndex, ShowcaseDistanceFromPadMeters, out double lat, out double lon, out double alt);
            alt += ShowcaseAltitudeOffset("RAPIER");

            var b = new RecordingBuilder("Part Showcase - RAPIER")
                .WithDefaultRotation(KscRotX, KscRotY, KscRotZ, KscRotW)
                .WithLoopPlayback(loop: true, intervalSeconds: 0.0)
                .WithRecordingGroup("Part Showcases");

            for (int i = 0; i <= 8; i++)
                b.AddPoint(t + (i * 3), lat, lon, alt);

            // Jet mode (AirBreathing = midx 0): red exhaust
            b.AddPartEvent(t + 0,    SinglePartPid, (int)PartEventType.EngineIgnited,  "RAPIER", value: 0.5f, moduleIndex: 0);
            b.AddPartEvent(t + 3,    SinglePartPid, (int)PartEventType.EngineThrottle, "RAPIER", value: 1.0f, moduleIndex: 0);
            b.AddPartEvent(t + 6,    SinglePartPid, (int)PartEventType.EngineShutdown, "RAPIER", moduleIndex: 0);
            // Switch to rocket mode (ClosedCycle = midx 1): blue exhaust
            b.AddPartEvent(t + 6.1,  SinglePartPid, (int)PartEventType.EngineIgnited,  "RAPIER", value: 0.8f, moduleIndex: 1);
            b.AddPartEvent(t + 9,    SinglePartPid, (int)PartEventType.EngineThrottle, "RAPIER", value: 1.0f, moduleIndex: 1);
            b.AddPartEvent(t + 15,   SinglePartPid, (int)PartEventType.EngineShutdown, "RAPIER", moduleIndex: 1);
            // Back to jet mode
            b.AddPartEvent(t + 15.1, SinglePartPid, (int)PartEventType.EngineIgnited,  "RAPIER", value: 1.0f, moduleIndex: 0);
            b.AddPartEvent(t + 21,   SinglePartPid, (int)PartEventType.EngineShutdown, "RAPIER", moduleIndex: 0);

            var snap = new VesselSnapshotBuilder()
                .WithName("Part Showcase - RAPIER")
                .WithPersistentId((uint)(pidBase + rowIndex))
                .AddPart("RAPIER", rotation: "0,-0.7071068,0,0.7071068")
                .AsLanded(lat, lon, alt)
                .Build();

            b.WithGhostVisualSnapshot(snap);
            return b;
        }

        internal static RecordingBuilder[] LightShowcaseRecordings(double baseUT = 0)
        {
            return new[]
            {
                BuildLightBlinkShowcaseRecording(baseUT, "Part Showcase - Lights v1", "domeLight1", rowIndex: 0),
                BuildLightBlinkShowcaseRecording(baseUT, "Part Showcase - Light - Nav v1", "navLight1", rowIndex: 1),
                BuildLightBlinkShowcaseRecording(baseUT, "Part Showcase - Light - Strip v1", "stripLight1", rowIndex: 2),
                BuildLightBlinkShowcaseRecording(baseUT, "Part Showcase - Light - Spot v1", "spotLight3", rowIndex: 3),
                BuildLightBlinkShowcaseRecording(baseUT, "Part Showcase - Light - Ground Small v1", "groundLight1", rowIndex: 4),
                BuildLightBlinkShowcaseRecording(baseUT, "Part Showcase - Light - Ground Stand v1", "groundLight2", rowIndex: 5),
                BuildLightBlinkShowcaseRecording(baseUT, "Part Showcase - Light - Spot Mk1", "spotLight1", rowIndex: 185),
                BuildLightBlinkShowcaseRecording(baseUT, "Part Showcase - Light - Spot Mk1 v2", "spotLight1_v2", rowIndex: 186),
                BuildLightBlinkShowcaseRecording(baseUT, "Part Showcase - Light - Spot Mk2", "spotLight2", rowIndex: 187),
                BuildLightBlinkShowcaseRecording(baseUT, "Part Showcase - Light - Spot Mk2 v2", "spotLight2_v2", rowIndex: 188)
            };
        }

        // Three parallel lines (rows 0-80 at 200m, rows 81-161 at 220m, rows 162-242 at 240m from pad):
        // Back line — Lights: 0-5, Deployables: 6-23, Airplane Gear: 24-27, Landing Legs: 28-30,
        //   Cargo: 31-41, Engines (old unused): 42-44, Ladders: 45-46, RCS: 47-49, Fairings: 50-54,
        //   Extra Radiators: 55-56, Drills: 57-58, Deployed Science: 59-66,
        //   Animation Group: 67-68, Parachutes: 69-73, Special Deploy Animations: 74-85 (partial)
        // Middle line — Special Deploy (cont): 86+, Jettison (non-engine): 86-94,
        //   ColorChanger Cabin Lights: 95-116, Robotics: 117-137, AeroSurface: 138,
        //   Robot Arm Scanners: 139-141, Control Surfaces: 142-161 (partial)
        // Front line — Control Surfaces (cont): 162-165, Wheel Dynamics: 166-171,
        //   AnimateHeat: 172-184, Lights (extra): 185-188, RCS (extra): 189-190,
        //   Engines (all 47): 191-238, EVA ColorChanger: 236, Inventory Placement: 242.

        internal static RecordingBuilder[] DeployableShowcaseRecordings(double baseUT = 0)
        {
            return new[]
            {
                BuildPartShowcaseRecording(baseUT, "Part Showcase - Solar Tracking", "solarPanels4", 6,
                    ShowcaseDistanceFromPadMeters, PartEventType.DeployableExtended, PartEventType.DeployableRetracted, DeployableShowcasePidBase, SinglePartPid),
                BuildPartShowcaseRecording(baseUT, "Part Showcase - Solar Large", "largeSolarPanel", 7,
                    ShowcaseDistanceFromPadMeters, PartEventType.DeployableExtended, PartEventType.DeployableRetracted, DeployableShowcasePidBase, SinglePartPid),
                BuildPartShowcaseRecording(baseUT, "Part Showcase - Solar Radial XL", "LgRadialSolarPanel", 8,
                    ShowcaseDistanceFromPadMeters, PartEventType.DeployableExtended, PartEventType.DeployableRetracted, DeployableShowcasePidBase, SinglePartPid),
                BuildPartShowcaseRecording(baseUT, "Part Showcase - Solar OX-10C", "solarPanelOX10C", 9,
                    ShowcaseDistanceFromPadMeters, PartEventType.DeployableExtended, PartEventType.DeployableRetracted, DeployableShowcasePidBase, SinglePartPid),
                BuildPartShowcaseRecording(baseUT, "Part Showcase - Solar OX-10L", "solarPanelOX10L", 10,
                    ShowcaseDistanceFromPadMeters, PartEventType.DeployableExtended, PartEventType.DeployableRetracted, DeployableShowcasePidBase, SinglePartPid),
                BuildPartShowcaseRecording(baseUT, "Part Showcase - Solar 3x2 Shrouded", "solarPanels1", 11,
                    ShowcaseDistanceFromPadMeters, PartEventType.DeployableExtended, PartEventType.DeployableRetracted, DeployableShowcasePidBase, SinglePartPid),
                BuildPartShowcaseRecording(baseUT, "Part Showcase - Solar 1x6 Shrouded", "solarPanels2", 12,
                    ShowcaseDistanceFromPadMeters, PartEventType.DeployableExtended, PartEventType.DeployableRetracted, DeployableShowcasePidBase, SinglePartPid),
                BuildPartShowcaseRecording(baseUT, "Part Showcase - Solar 3x2", "solarPanels3", 13,
                    ShowcaseDistanceFromPadMeters, PartEventType.DeployableExtended, PartEventType.DeployableRetracted, DeployableShowcasePidBase, SinglePartPid),
                BuildPartShowcaseRecording(baseUT, "Part Showcase - Solar Flat", "solarPanels5", 14,
                    ShowcaseDistanceFromPadMeters, PartEventType.DeployableExtended, PartEventType.DeployableRetracted, DeployableShowcasePidBase, SinglePartPid),
                BuildPartShowcaseRecording(baseUT, "Part Showcase - Solar SP-10C", "solarPanelSP10C", 15,
                    ShowcaseDistanceFromPadMeters, PartEventType.DeployableExtended, PartEventType.DeployableRetracted, DeployableShowcasePidBase, SinglePartPid),
                BuildPartShowcaseRecording(baseUT, "Part Showcase - Solar SP-10L", "solarPanelSP10L", 16,
                    ShowcaseDistanceFromPadMeters, PartEventType.DeployableExtended, PartEventType.DeployableRetracted, DeployableShowcasePidBase, SinglePartPid),
                BuildPartShowcaseRecording(baseUT, "Part Showcase - Antenna Comm", "longAntenna", 17,
                    ShowcaseDistanceFromPadMeters, PartEventType.DeployableExtended, PartEventType.DeployableRetracted, DeployableShowcasePidBase, SinglePartPid),
                BuildPartShowcaseRecording(baseUT, "Part Showcase - Antenna Dish", "commDish", 18,
                    ShowcaseDistanceFromPadMeters, PartEventType.DeployableExtended, PartEventType.DeployableRetracted, DeployableShowcasePidBase, SinglePartPid),
                BuildPartShowcaseRecording(baseUT, "Part Showcase - Antenna High Gain", "HighGainAntenna", 19,
                    ShowcaseDistanceFromPadMeters, PartEventType.DeployableExtended, PartEventType.DeployableRetracted, DeployableShowcasePidBase, SinglePartPid),
                BuildPartShowcaseRecording(baseUT, "Part Showcase - Antenna HG-5", "HighGainAntenna5", 20,
                    ShowcaseDistanceFromPadMeters, PartEventType.DeployableExtended, PartEventType.DeployableRetracted, DeployableShowcasePidBase, SinglePartPid),
                BuildPartShowcaseRecording(baseUT, "Part Showcase - Antenna HG-5 v2", "HighGainAntenna5.v2", 21,
                    ShowcaseDistanceFromPadMeters, PartEventType.DeployableExtended, PartEventType.DeployableRetracted, DeployableShowcasePidBase, SinglePartPid),
                BuildPartShowcaseRecording(baseUT, "Part Showcase - Antenna Medium Dish", "mediumDishAntenna", 22,
                    ShowcaseDistanceFromPadMeters, PartEventType.DeployableExtended, PartEventType.DeployableRetracted, DeployableShowcasePidBase, SinglePartPid),
                BuildPartShowcaseRecording(baseUT, "Part Showcase - Radiator", "foldingRadSmall", 23,
                    ShowcaseDistanceFromPadMeters, PartEventType.DeployableExtended, PartEventType.DeployableRetracted, DeployableShowcasePidBase, SinglePartPid)
            };
        }

        internal static RecordingBuilder[] GearShowcaseRecordings(double baseUT = 0)
        {
            return new[]
            {
                BuildPartShowcaseRecording(baseUT, "Part Showcase - Gear Bay", "SmallGearBay", 24,
                    ShowcaseDistanceFromPadMeters, PartEventType.GearDeployed, PartEventType.GearRetracted, GearShowcasePidBase, SinglePartPid),
                BuildPartShowcaseRecording(baseUT, "Part Showcase - Gear Small", "GearSmall", 25,
                    ShowcaseDistanceFromPadMeters, PartEventType.GearDeployed, PartEventType.GearRetracted, GearShowcasePidBase, SinglePartPid),
                BuildPartShowcaseRecording(baseUT, "Part Showcase - Gear Medium", "GearMedium", 26,
                    ShowcaseDistanceFromPadMeters, PartEventType.GearDeployed, PartEventType.GearRetracted, GearShowcasePidBase, SinglePartPid),
                BuildPartShowcaseRecording(baseUT, "Part Showcase - Gear Large", "GearLarge", 27,
                    ShowcaseDistanceFromPadMeters, PartEventType.GearDeployed, PartEventType.GearRetracted, GearShowcasePidBase, SinglePartPid),
                BuildPartShowcaseRecording(baseUT, "Part Showcase - Landing Leg LT-1", "landingLeg1", 28,
                    ShowcaseDistanceFromPadMeters, PartEventType.GearDeployed, PartEventType.GearRetracted, GearShowcasePidBase, SinglePartPid),
                BuildPartShowcaseRecording(baseUT, "Part Showcase - Landing Leg LT-2", "landingLeg1-2", 29,
                    ShowcaseDistanceFromPadMeters, PartEventType.GearDeployed, PartEventType.GearRetracted, GearShowcasePidBase, SinglePartPid),
                BuildPartShowcaseRecording(baseUT, "Part Showcase - Landing Leg LT-05", "miniLandingLeg", 30,
                    ShowcaseDistanceFromPadMeters, PartEventType.GearDeployed, PartEventType.GearRetracted, GearShowcasePidBase, SinglePartPid)
            };
        }

        internal static RecordingBuilder[] CargoBayShowcaseRecordings(double baseUT = 0)
        {
            return new[]
            {
                BuildPartShowcaseRecording(baseUT, "Part Showcase - Service Bay", "ServiceBay.125.v2", 31,
                    ShowcaseDistanceFromPadMeters, PartEventType.CargoBayOpened, PartEventType.CargoBayClosed, CargoBayShowcasePidBase, SinglePartPid),
                BuildPartShowcaseRecording(baseUT, "Part Showcase - Service Bay 2.5", "ServiceBay.250.v2", 32,
                    ShowcaseDistanceFromPadMeters, PartEventType.CargoBayOpened, PartEventType.CargoBayClosed, CargoBayShowcasePidBase, SinglePartPid),
                BuildPartShowcaseRecording(baseUT, "Part Showcase - Service Module 1.8", "ServiceModule18", 33,
                    ShowcaseDistanceFromPadMeters, PartEventType.CargoBayOpened, PartEventType.CargoBayClosed, CargoBayShowcasePidBase, SinglePartPid),
                BuildPartShowcaseRecording(baseUT, "Part Showcase - Service Module 2.5", "ServiceModule25", 34,
                    ShowcaseDistanceFromPadMeters, PartEventType.CargoBayOpened, PartEventType.CargoBayClosed, CargoBayShowcasePidBase, SinglePartPid),
                BuildPartShowcaseRecording(baseUT, "Part Showcase - Service Module 1-0", "Size1to0ServiceModule", 35,
                    ShowcaseDistanceFromPadMeters, PartEventType.CargoBayOpened, PartEventType.CargoBayClosed, CargoBayShowcasePidBase, SinglePartPid),
                BuildPartShowcaseRecording(baseUT, "Part Showcase - Cargo Mk2", "mk2CargoBayS", 36,
                    ShowcaseDistanceFromPadMeters, PartEventType.CargoBayOpened, PartEventType.CargoBayClosed, CargoBayShowcasePidBase, SinglePartPid),
                BuildPartShowcaseRecording(baseUT, "Part Showcase - Cargo Mk2 Long", "mk2CargoBayL", 37,
                    ShowcaseDistanceFromPadMeters, PartEventType.CargoBayOpened, PartEventType.CargoBayClosed, CargoBayShowcasePidBase, SinglePartPid),
                BuildPartShowcaseRecording(baseUT, "Part Showcase - Cargo Mk3", "mk3CargoBayS", 38,
                    ShowcaseDistanceFromPadMeters, PartEventType.CargoBayOpened, PartEventType.CargoBayClosed, CargoBayShowcasePidBase, SinglePartPid),
                BuildPartShowcaseRecording(baseUT, "Part Showcase - Cargo Mk3 Medium", "mk3CargoBayM", 39,
                    ShowcaseDistanceFromPadMeters, PartEventType.CargoBayOpened, PartEventType.CargoBayClosed, CargoBayShowcasePidBase, SinglePartPid),
                BuildPartShowcaseRecording(baseUT, "Part Showcase - Cargo Mk3 Long", "mk3CargoBayL", 40,
                    ShowcaseDistanceFromPadMeters, PartEventType.CargoBayOpened, PartEventType.CargoBayClosed, CargoBayShowcasePidBase, SinglePartPid),
                BuildPartShowcaseRecording(baseUT, "Part Showcase - Cargo Mk3 Ramp", "mk3CargoRamp", 41,
                    ShowcaseDistanceFromPadMeters, PartEventType.CargoBayOpened, PartEventType.CargoBayClosed, CargoBayShowcasePidBase, SinglePartPid)
            };
        }

        internal static RecordingBuilder[] EngineShowcaseRecordings(double baseUT = 0)
        {
            const uint pidBase = EngineShowcasePidBase;
            return new[]
            {
                // ── Liquid engines with shroud jettison + flame (rows 191-210) ──
                BuildCombinedEngineShowcaseRecording(baseUT, "Part Showcase - LV-T30", "liquidEngine", 191, pidBase, hasShroud: true),
                BuildCombinedEngineShowcaseRecording(baseUT, "Part Showcase - LV-T30 v2", "liquidEngine.v2", 192, pidBase, hasShroud: true),
                BuildCombinedEngineShowcaseRecording(baseUT, "Part Showcase - LV-T45", "liquidEngine2", 193, pidBase, hasShroud: true),
                BuildCombinedEngineShowcaseRecording(baseUT, "Part Showcase - LV-T45 v2", "liquidEngine2.v2", 194, pidBase, hasShroud: true),
                BuildCombinedEngineShowcaseRecording(baseUT, "Part Showcase - Poodle v2", "liquidEngine2-2.v2", 195, pidBase, hasShroud: true),
                BuildCombinedEngineShowcaseRecording(baseUT, "Part Showcase - Terrier v2", "liquidEngine3.v2", 196, pidBase, hasShroud: true),
                BuildCombinedEngineShowcaseRecording(baseUT, "Part Showcase - Spark v2", "liquidEngineMini.v2", 197, pidBase, hasShroud: true),
                BuildCombinedEngineShowcaseRecording(baseUT, "Part Showcase - NERV", "nuclearEngine", 198, pidBase, hasShroud: true),
                BuildCombinedEngineShowcaseRecording(baseUT, "Part Showcase - Aerospike", "toroidalAerospike", 199, pidBase, hasShroud: true),
                BuildCombinedEngineShowcaseRecording(baseUT, "Part Showcase - Vector", "Size3AdvancedEngine", 200, pidBase, hasShroud: true),
                BuildCombinedEngineShowcaseRecording(baseUT, "Part Showcase - Kodiak", "LiquidEngineKE-1", 201, pidBase, hasShroud: true),
                BuildCombinedEngineShowcaseRecording(baseUT, "Part Showcase - Cheetah", "LiquidEngineLV-T91", 202, pidBase, hasShroud: true),
                BuildCombinedEngineShowcaseRecording(baseUT, "Part Showcase - Wolfhound", "LiquidEngineLV-TX87", 203, pidBase, hasShroud: true),
                BuildCombinedEngineShowcaseRecording(baseUT, "Part Showcase - Bobcat", "LiquidEngineRE-I2", 204, pidBase, hasShroud: true),
                BuildCombinedEngineShowcaseRecording(baseUT, "Part Showcase - Skiff", "LiquidEngineRE-J10", 205, pidBase, hasShroud: true),
                BuildCombinedEngineShowcaseRecording(baseUT, "Part Showcase - Mastodon", "LiquidEngineRK-7", 206, pidBase, hasShroud: true),
                // SRBs with shroud (rows 207-210)
                BuildCombinedEngineShowcaseRecording(baseUT, "Part Showcase - Flea v2", "solidBooster.sm.v2", 207, pidBase, isSrb: true, hasShroud: true),
                BuildCombinedEngineShowcaseRecording(baseUT, "Part Showcase - Hammer v2", "solidBooster.v2", 208, pidBase, isSrb: true, hasShroud: true),
                BuildCombinedEngineShowcaseRecording(baseUT, "Part Showcase - Mite", "Mite", 209, pidBase, isSrb: true, hasShroud: true),
                BuildCombinedEngineShowcaseRecording(baseUT, "Part Showcase - Shrimp", "Shrimp", 210, pidBase, isSrb: true, hasShroud: true),

                // ── Liquid engines flame only (rows 211-216) ──
                BuildCombinedEngineShowcaseRecording(baseUT, "Part Showcase - Ant", "microEngine.v2", 211, pidBase),
                BuildCombinedEngineShowcaseRecording(baseUT, "Part Showcase - Spider", "radialEngineMini.v2", 212, pidBase),
                BuildCombinedEngineShowcaseRecording(baseUT, "Part Showcase - Twitch", "smallRadialEngine", 213, pidBase),
                BuildCombinedEngineShowcaseRecording(baseUT, "Part Showcase - Twitch v2", "smallRadialEngine.v2", 214, pidBase),
                BuildCombinedEngineShowcaseRecording(baseUT, "Part Showcase - Thud", "radialLiquidEngine1-2", 215, pidBase),
                BuildCombinedEngineShowcaseRecording(baseUT, "Part Showcase - Cub", "omsEngine", 216, pidBase),

                // ── Liquid engines with shroud jettison + flame (rows 217-219) ──
                BuildCombinedEngineShowcaseRecording(baseUT, "Part Showcase - Mainsail", "liquidEngineMainsail.v2", 217, pidBase, hasShroud: true),
                BuildCombinedEngineShowcaseRecording(baseUT, "Part Showcase - Skipper", "engineLargeSkipper.v2", 218, pidBase, hasShroud: true),
                BuildCombinedEngineShowcaseRecording(baseUT, "Part Showcase - SSME", "SSME", 219, pidBase, hasShroud: true),
                BuildCombinedEngineShowcaseRecording(baseUT, "Part Showcase - Twin-Boar", "Size2LFB.v2", 220, pidBase),
                BuildCombinedEngineShowcaseRecording(baseUT, "Part Showcase - Mammoth", "Size3EngineCluster", 221, pidBase),
                BuildCombinedEngineShowcaseRecording(baseUT, "Part Showcase - Pug", "LiquidEngineRV-1", 222, pidBase),
                BuildRapierModeSwitchShowcase(baseUT, 223, pidBase),

                // ── SRBs flame only (rows 224-229) ──
                BuildCombinedEngineShowcaseRecording(baseUT, "Part Showcase - Thumper", "solidBooster1-1", 224, pidBase, isSrb: true),
                BuildCombinedEngineShowcaseRecording(baseUT, "Part Showcase - Kickback", "MassiveBooster", 225, pidBase, isSrb: true),
                BuildCombinedEngineShowcaseRecording(baseUT, "Part Showcase - Thoroughbred", "Thoroughbred", 226, pidBase, isSrb: true),
                BuildCombinedEngineShowcaseRecording(baseUT, "Part Showcase - Clydesdale", "Clydesdale", 227, pidBase, isSrb: true),
                BuildCombinedEngineShowcaseRecording(baseUT, "Part Showcase - Pollux", "Pollux", 228, pidBase, isSrb: true),
                BuildCombinedEngineShowcaseRecording(baseUT, "Part Showcase - Sepatron", "sepMotor1", 229, pidBase, isSrb: true),

                // ── Jets (rows 230-234) ──
                BuildCombinedEngineShowcaseRecording(baseUT, "Part Showcase - Juno", "miniJetEngine", 230, pidBase),
                BuildCombinedEngineShowcaseRecording(baseUT, "Part Showcase - Wheesley", "JetEngine", 231, pidBase),
                BuildCombinedEngineShowcaseRecording(baseUT, "Part Showcase - Whiplash", "turboFanEngine", 232, pidBase),
                BuildCombinedEngineShowcaseRecording(baseUT, "Part Showcase - Panther", "turboJet", 233, pidBase),
                BuildCombinedEngineShowcaseRecording(baseUT, "Part Showcase - Goliath", "turboFanSize2", 234, pidBase),

                // ── Special (row 235) ──
                BuildCombinedEngineShowcaseRecording(baseUT, "Part Showcase - Ion", "ionEngine", 235, pidBase),

                // ── Additional engines (rows 237-238) ──
                BuildCombinedEngineShowcaseRecording(baseUT, "Part Showcase - LES", "LaunchEscapeSystem", 238, pidBase, isSrb: true),
                BuildCombinedEngineShowcaseRecording(baseUT, "Part Showcase - FL-C1000 Tank", "Size1p5.Tank.05", 239, pidBase, isSrb: true)
            };
        }

        internal static RecordingBuilder[] LadderShowcaseRecordings(double baseUT = 0)
        {
            return new[]
            {
                BuildPartShowcaseRecording(baseUT, "Part Showcase - Ladder Telescopic", "telescopicLadder", 45,
                    ShowcaseDistanceFromPadMeters, PartEventType.DeployableExtended, PartEventType.DeployableRetracted, LadderShowcasePidBase, SinglePartPid),
                BuildPartShowcaseRecording(baseUT, "Part Showcase - Ladder Bay", "telescopicLadderBay", 46,
                    ShowcaseDistanceFromPadMeters, PartEventType.DeployableExtended, PartEventType.DeployableRetracted, LadderShowcasePidBase, SinglePartPid)
            };
        }

        internal static RecordingBuilder[] RcsShowcaseRecordings(double baseUT = 0)
        {
            return new[]
            {
                BuildPartShowcaseRecording(baseUT, "Part Showcase - RCS RV-105", "RCSBlock.v2", 47,
                    ShowcaseDistanceFromPadMeters, PartEventType.RCSActivated, PartEventType.RCSStopped, RcsShowcasePidBase, SinglePartPid,
                    eventValue: 1.0f, moduleIndex: 0,
                    firstEventOffsetSeconds: 0.0, onDurationSeconds: 4.5, offDurationSeconds: 1.5),
                BuildPartShowcaseRecording(baseUT, "Part Showcase - RCS RV-1X", "RCSblock.01.small", 48,
                    ShowcaseDistanceFromPadMeters, PartEventType.RCSActivated, PartEventType.RCSStopped, RcsShowcasePidBase, SinglePartPid,
                    eventValue: 1.0f, moduleIndex: 0,
                    firstEventOffsetSeconds: 0.0, onDurationSeconds: 4.5, offDurationSeconds: 1.5),
                BuildPartShowcaseRecording(baseUT, "Part Showcase - RCS Linear", "RCSLinearSmall", 49,
                    ShowcaseDistanceFromPadMeters, PartEventType.RCSActivated, PartEventType.RCSStopped, RcsShowcasePidBase, SinglePartPid,
                    eventValue: 1.0f, moduleIndex: 0,
                    firstEventOffsetSeconds: 0.0, onDurationSeconds: 4.5, offDurationSeconds: 1.5),
                BuildPartShowcaseRecording(baseUT, "Part Showcase - RCS Linear Port", "linearRcs", 189,
                    ShowcaseDistanceFromPadMeters, PartEventType.RCSActivated, PartEventType.RCSStopped, RcsShowcasePidBase, SinglePartPid,
                    eventValue: 1.0f, moduleIndex: 0,
                    firstEventOffsetSeconds: 0.0, onDurationSeconds: 4.5, offDurationSeconds: 1.5),
                BuildPartShowcaseRecording(baseUT, "Part Showcase - RCS Vernor", "vernierEngine", 190,
                    ShowcaseDistanceFromPadMeters, PartEventType.RCSActivated, PartEventType.RCSStopped, RcsShowcasePidBase, SinglePartPid,
                    eventValue: 1.0f, moduleIndex: 0,
                    firstEventOffsetSeconds: 0.0, onDurationSeconds: 4.5, offDurationSeconds: 1.5)
            };
        }

        private static void AddProceduralFairingModule(
            ConfigNode partNode, float baseRadius, float topHeight)
        {
            if (partNode == null) return;

            var module = new ConfigNode("MODULE");
            module.AddValue("name", "ModuleProceduralFairing");
            module.AddValue("fsm", "st_proc");

            // Minimal cross-section profile for synthetic fairing shell generation.
            var sec0 = new ConfigNode("XSECTION");
            sec0.AddValue("h", "0");
            sec0.AddValue("r", baseRadius.ToString("R", CultureInfo.InvariantCulture));
            module.AddNode(sec0);

            var sec1 = new ConfigNode("XSECTION");
            sec1.AddValue("h", topHeight.ToString("R", CultureInfo.InvariantCulture));
            sec1.AddValue("r", "0");
            module.AddNode(sec1);

            partNode.AddNode(module);
        }

        internal static RecordingBuilder[] FairingShowcaseRecordings(double baseUT = 0)
        {
            return new[]
            {
                BuildPartShowcaseRecording(baseUT, "Part Showcase - Fairing Size 1", "fairingSize1", 50,
                    ShowcaseDistanceFromPadMeters, PartEventType.FairingJettisoned, PartEventType.FairingJettisoned, FairingShowcasePidBase, SinglePartPid,
                    configureGhostPartNode: part => AddProceduralFairingModule(part, baseRadius: 0.625f, topHeight: 2.0f)),
                BuildPartShowcaseRecording(baseUT, "Part Showcase - Fairing Size 1.5", "fairingSize1p5", 51,
                    ShowcaseDistanceFromPadMeters, PartEventType.FairingJettisoned, PartEventType.FairingJettisoned, FairingShowcasePidBase, SinglePartPid,
                    configureGhostPartNode: part => AddProceduralFairingModule(part, baseRadius: 0.9375f, topHeight: 2.8f)),
                BuildPartShowcaseRecording(baseUT, "Part Showcase - Fairing Size 2", "fairingSize2", 52,
                    ShowcaseDistanceFromPadMeters, PartEventType.FairingJettisoned, PartEventType.FairingJettisoned, FairingShowcasePidBase, SinglePartPid,
                    configureGhostPartNode: part => AddProceduralFairingModule(part, baseRadius: 1.25f, topHeight: 3.2f)),
                BuildPartShowcaseRecording(baseUT, "Part Showcase - Fairing Size 3", "fairingSize3", 53,
                    ShowcaseDistanceFromPadMeters, PartEventType.FairingJettisoned, PartEventType.FairingJettisoned, FairingShowcasePidBase, SinglePartPid,
                    configureGhostPartNode: part => AddProceduralFairingModule(part, baseRadius: 1.875f, topHeight: 4.5f)),
                BuildPartShowcaseRecording(baseUT, "Part Showcase - Fairing Size 4", "fairingSize4", 54,
                    ShowcaseDistanceFromPadMeters, PartEventType.FairingJettisoned, PartEventType.FairingJettisoned, FairingShowcasePidBase, SinglePartPid,
                    configureGhostPartNode: part => AddProceduralFairingModule(part, baseRadius: 2.5f, topHeight: 6.0f))
            };
        }

        internal static RecordingBuilder[] RadiatorShowcaseRecordings(double baseUT = 0)
        {
            return new[]
            {
                BuildPartShowcaseRecording(baseUT, "Part Showcase - Radiator Medium", "foldingRadMed", 55,
                    ShowcaseDistanceFromPadMeters, PartEventType.DeployableExtended, PartEventType.DeployableRetracted, RadiatorShowcasePidBase, SinglePartPid),
                BuildPartShowcaseRecording(baseUT, "Part Showcase - Radiator Large", "foldingRadLarge", 56,
                    ShowcaseDistanceFromPadMeters, PartEventType.DeployableExtended, PartEventType.DeployableRetracted, RadiatorShowcasePidBase, SinglePartPid)
            };
        }

        internal static RecordingBuilder[] DrillShowcaseRecordings(double baseUT = 0)
        {
            return new[]
            {
                BuildPartShowcaseRecording(baseUT, "Part Showcase - Drill Junior", "MiniDrill", 57,
                    ShowcaseDistanceFromPadMeters, PartEventType.DeployableExtended, PartEventType.DeployableRetracted, DrillShowcasePidBase, SinglePartPid,
                    firstEventOffsetSeconds: 0.0, onDurationSeconds: 4.5, offDurationSeconds: 1.5),
                BuildPartShowcaseRecording(baseUT, "Part Showcase - Drill-O-Matic", "RadialDrill", 58,
                    ShowcaseDistanceFromPadMeters, PartEventType.DeployableExtended, PartEventType.DeployableRetracted, DrillShowcasePidBase, SinglePartPid,
                    firstEventOffsetSeconds: 0.0, onDurationSeconds: 4.5, offDurationSeconds: 1.5)
            };
        }

        internal static RecordingBuilder[] DeployedScienceShowcaseRecordings(double baseUT = 0)
        {
            return new[]
            {
                BuildPartShowcaseRecording(baseUT, "Part Showcase - Deployed Central Station", "DeployedCentralStation", 59,
                    ShowcaseDistanceFromPadMeters, PartEventType.DeployableExtended, PartEventType.DeployableRetracted, DeployedScienceShowcasePidBase, SinglePartPid,
                    firstEventOffsetSeconds: 0.0, onDurationSeconds: 4.5, offDurationSeconds: 1.5),
                BuildPartShowcaseRecording(baseUT, "Part Showcase - Deployed Goo Observation", "DeployedGoExOb", 60,
                    ShowcaseDistanceFromPadMeters, PartEventType.DeployableExtended, PartEventType.DeployableRetracted, DeployedScienceShowcasePidBase, SinglePartPid,
                    firstEventOffsetSeconds: 0.0, onDurationSeconds: 4.5, offDurationSeconds: 1.5),
                BuildPartShowcaseRecording(baseUT, "Part Showcase - Deployed Ion Collector", "DeployedIONExp", 61,
                    ShowcaseDistanceFromPadMeters, PartEventType.DeployableExtended, PartEventType.DeployableRetracted, DeployedScienceShowcasePidBase, SinglePartPid,
                    firstEventOffsetSeconds: 0.0, onDurationSeconds: 4.5, offDurationSeconds: 1.5),
                BuildPartShowcaseRecording(baseUT, "Part Showcase - Deployed RTG", "DeployedRTG", 62,
                    ShowcaseDistanceFromPadMeters, PartEventType.DeployableExtended, PartEventType.DeployableRetracted, DeployedScienceShowcasePidBase, SinglePartPid,
                    firstEventOffsetSeconds: 0.0, onDurationSeconds: 4.5, offDurationSeconds: 1.5),
                BuildPartShowcaseRecording(baseUT, "Part Showcase - Deployed Sat Dish", "DeployedSatDish", 63,
                    ShowcaseDistanceFromPadMeters, PartEventType.DeployableExtended, PartEventType.DeployableRetracted, DeployedScienceShowcasePidBase, SinglePartPid,
                    firstEventOffsetSeconds: 0.0, onDurationSeconds: 4.5, offDurationSeconds: 1.5),
                BuildPartShowcaseRecording(baseUT, "Part Showcase - Deployed Seismic Sensor", "DeployedSeismicSensor", 64,
                    ShowcaseDistanceFromPadMeters, PartEventType.DeployableExtended, PartEventType.DeployableRetracted, DeployedScienceShowcasePidBase, SinglePartPid,
                    firstEventOffsetSeconds: 0.0, onDurationSeconds: 4.5, offDurationSeconds: 1.5),
                BuildPartShowcaseRecording(baseUT, "Part Showcase - Deployed Solar Panel", "DeployedSolarPanel", 65,
                    ShowcaseDistanceFromPadMeters, PartEventType.DeployableExtended, PartEventType.DeployableRetracted, DeployedScienceShowcasePidBase, SinglePartPid,
                    firstEventOffsetSeconds: 0.0, onDurationSeconds: 4.5, offDurationSeconds: 1.5),
                BuildPartShowcaseRecording(baseUT, "Part Showcase - Deployed Weather Station", "DeployedWeatherStn", 66,
                    ShowcaseDistanceFromPadMeters, PartEventType.DeployableExtended, PartEventType.DeployableRetracted, DeployedScienceShowcasePidBase, SinglePartPid,
                    firstEventOffsetSeconds: 0.0, onDurationSeconds: 4.5, offDurationSeconds: 1.5)
            };
        }

        internal static RecordingBuilder[] AnimationGroupShowcaseRecordings(double baseUT = 0)
        {
            return new[]
            {
                BuildPartShowcaseRecording(baseUT, "Part Showcase - Ground Anchor", "groundAnchor", 67,
                    ShowcaseDistanceFromPadMeters, PartEventType.DeployableExtended, PartEventType.DeployableRetracted, AnimationGroupShowcasePidBase, SinglePartPid,
                    firstEventOffsetSeconds: 0.0, onDurationSeconds: 4.5, offDurationSeconds: 1.5),
                BuildPartShowcaseRecording(baseUT, "Part Showcase - Survey Scanner", "SurveyScanner", 68,
                    ShowcaseDistanceFromPadMeters, PartEventType.DeployableExtended, PartEventType.DeployableRetracted, AnimationGroupShowcasePidBase, SinglePartPid,
                    firstEventOffsetSeconds: 0.0, onDurationSeconds: 4.5, offDurationSeconds: 1.5),
                BuildPartShowcaseRecording(baseUT, "Part Showcase - ISRU", "ISRU", 240,
                    ShowcaseDistanceFromPadMeters, PartEventType.DeployableExtended, PartEventType.DeployableRetracted, AnimationGroupShowcasePidBase, SinglePartPid,
                    firstEventOffsetSeconds: 0.0, onDurationSeconds: 4.5, offDurationSeconds: 1.5),
                BuildPartShowcaseRecording(baseUT, "Part Showcase - Orbital Scanner", "OrbitalScanner", 241,
                    ShowcaseDistanceFromPadMeters, PartEventType.DeployableExtended, PartEventType.DeployableRetracted, AnimationGroupShowcasePidBase, SinglePartPid,
                    firstEventOffsetSeconds: 0.0, onDurationSeconds: 4.5, offDurationSeconds: 1.5)
            };
        }

        internal static RecordingBuilder[] ParachuteShowcaseRecordings(double baseUT = 0)
        {
            // 3-phase cycle: semi-deployed (streamer) → deployed (dome) → cut → repeat
            return new[]
            {
                BuildParachuteShowcaseRecording(baseUT, "Part Showcase - Parachute Mk16", "parachuteSingle", 69, ParachuteShowcasePidBase),
                BuildParachuteShowcaseRecording(baseUT, "Part Showcase - Parachute Mk2-R", "parachuteRadial", 70, ParachuteShowcasePidBase),
                BuildParachuteShowcaseRecording(baseUT, "Part Showcase - Drogue Mk25", "parachuteDrogue", 71, ParachuteShowcasePidBase),
                BuildParachuteShowcaseRecording(baseUT, "Part Showcase - Drogue Mk12-R", "radialDrogue", 72, ParachuteShowcasePidBase),
                BuildParachuteShowcaseRecording(baseUT, "Part Showcase - Parachute Mk16-XL", "parachuteLarge", 73, ParachuteShowcasePidBase)
            };
        }

        private static RecordingBuilder BuildParachuteShowcaseRecording(
            double baseUT, string vesselName, string partName, int rowIndex, uint pidBase)
        {
            double t = baseUT + 30;
            ShowcasePosition(rowIndex, ShowcaseDistanceFromPadMeters, out double lat, out double lon, out double alt);
            alt += ShowcaseAltitudeOffset(partName);

            var b = new RecordingBuilder(vesselName)
                .WithDefaultRotation(KscRotX, KscRotY, KscRotZ, KscRotW)
                .WithLoopPlayback(loop: true, intervalSeconds: 0.0)
                .WithRecordingGroup("Part Showcases");

            // Static trajectory (24s)
            for (int i = 0; i <= 8; i++)
                b.AddPoint(t + (i * 3), lat, lon, alt);

            // 3-phase parachute cycle: semi-deployed (3s) → deployed (3s) → cut (2s) → repeat
            double offset = 0.0;
            for (int cycle = 0; cycle < 3; cycle++)
            {
                b.AddPartEvent(t + offset, SinglePartPid, (int)PartEventType.ParachuteSemiDeployed, partName);
                offset += 3.0;
                b.AddPartEvent(t + offset, SinglePartPid, (int)PartEventType.ParachuteDeployed, partName);
                offset += 3.0;
                b.AddPartEvent(t + offset, SinglePartPid, (int)PartEventType.ParachuteCut, partName);
                offset += 2.0;
            }

            var snap = new VesselSnapshotBuilder()
                .WithName(vesselName)
                .WithPersistentId((uint)(pidBase + rowIndex))
                .AddPart(partName, rotation: "0,-0.7071068,0,0.7071068")
                .AsLanded(lat, lon, alt)
                .Build();
            b.WithGhostVisualSnapshot(snap);

            return b;
        }

        internal static RecordingBuilder[] SpecialDeployAnimationShowcaseRecordings(double baseUT = 0)
        {
            return new[]
            {
                BuildPartShowcaseRecording(baseUT, "Part Showcase - Rover Wheel M1-F", "roverWheelM1-F", 74,
                    ShowcaseDistanceFromPadMeters, PartEventType.GearDeployed, PartEventType.GearRetracted, SpecialDeployShowcasePidBase, SinglePartPid,
                    firstEventOffsetSeconds: 0.0, onDurationSeconds: 4.5, offDurationSeconds: 1.5),
                BuildPartShowcaseRecording(baseUT, "Part Showcase - Goo Experiment", "GooExperiment", 75,
                    ShowcaseDistanceFromPadMeters, PartEventType.DeployableExtended, PartEventType.DeployableRetracted, SpecialDeployShowcasePidBase, SinglePartPid,
                    firstEventOffsetSeconds: 0.0, onDurationSeconds: 4.5, offDurationSeconds: 1.5),
                BuildPartShowcaseRecording(baseUT, "Part Showcase - Science Jr", "science_module", 76,
                    ShowcaseDistanceFromPadMeters, PartEventType.DeployableExtended, PartEventType.DeployableRetracted, SpecialDeployShowcasePidBase, SinglePartPid,
                    firstEventOffsetSeconds: 0.0, onDurationSeconds: 4.5, offDurationSeconds: 1.5),
                BuildPartShowcaseRecording(baseUT, "Part Showcase - Magnetometer Boom", "Magnetometer", 77,
                    ShowcaseDistanceFromPadMeters, PartEventType.DeployableExtended, PartEventType.DeployableRetracted, SpecialDeployShowcasePidBase, SinglePartPid,
                    firstEventOffsetSeconds: 0.0, onDurationSeconds: 4.5, offDurationSeconds: 1.5),
                BuildInflatableHeatShieldShowcaseRecording(baseUT, 78, ShowcaseDistanceFromPadMeters),
                BuildPartShowcaseRecording(baseUT, "Part Showcase - Inflatable Airlock", "InflatableAirlock", 79,
                    ShowcaseDistanceFromPadMeters, PartEventType.DeployableExtended, PartEventType.DeployableRetracted, SpecialDeployShowcasePidBase, SinglePartPid,
                    firstEventOffsetSeconds: 0.0, onDurationSeconds: 4.5, offDurationSeconds: 1.5),
                BuildPartShowcaseRecording(baseUT, "Part Showcase - Docking Port Shielded", "dockingPort1", 80,
                    ShowcaseDistanceFromPadMeters, PartEventType.DeployableExtended, PartEventType.DeployableRetracted, SpecialDeployShowcasePidBase, SinglePartPid,
                    firstEventOffsetSeconds: 0.0, onDurationSeconds: 4.5, offDurationSeconds: 1.5),
                BuildPartShowcaseRecording(baseUT, "Part Showcase - Docking Port Inline", "dockingPortLateral", 81,
                    ShowcaseDistanceFromPadMeters, PartEventType.DeployableExtended, PartEventType.DeployableRetracted, SpecialDeployShowcasePidBase, SinglePartPid,
                    firstEventOffsetSeconds: 0.0, onDurationSeconds: 4.5, offDurationSeconds: 1.5),
                BuildPartShowcaseRecording(baseUT, "Part Showcase - Grappling Device", "GrapplingDevice", 82,
                    ShowcaseDistanceFromPadMeters, PartEventType.DeployableExtended, PartEventType.DeployableRetracted, SpecialDeployShowcasePidBase, SinglePartPid,
                    firstEventOffsetSeconds: 0.0, onDurationSeconds: 4.5, offDurationSeconds: 1.5),
                BuildPartShowcaseRecording(baseUT, "Part Showcase - Small Claw", "smallClaw", 83,
                    ShowcaseDistanceFromPadMeters, PartEventType.DeployableExtended, PartEventType.DeployableRetracted, SpecialDeployShowcasePidBase, SinglePartPid,
                    firstEventOffsetSeconds: 0.0, onDurationSeconds: 4.5, offDurationSeconds: 1.5),
                BuildPartShowcaseRecording(baseUT, "Part Showcase - Mk2 Docking Port", "mk2DockingPort", 84,
                    ShowcaseDistanceFromPadMeters, PartEventType.DeployableExtended, PartEventType.DeployableRetracted, SpecialDeployShowcasePidBase, SinglePartPid,
                    firstEventOffsetSeconds: 0.0, onDurationSeconds: 4.5, offDurationSeconds: 1.5),
                BuildPartShowcaseRecording(baseUT, "Part Showcase - Mk2 Lander Cabin", "mk2LanderCabin_v2", 85,
                    ShowcaseDistanceFromPadMeters, PartEventType.DeployableExtended, PartEventType.DeployableRetracted, SpecialDeployShowcasePidBase, SinglePartPid,
                    firstEventOffsetSeconds: 0.0, onDurationSeconds: 4.5, offDurationSeconds: 1.5)
            };
        }

        internal static RecordingBuilder[] RoboticsShowcaseRecordings(double baseUT = 0)
        {
            return new[]
            {
                BuildPartShowcaseRecording(baseUT, "Part Showcase - Robotics Hinge G-11", "hinge.01", 117,
                    ShowcaseDistanceFromPadMeters, PartEventType.RoboticMotionStarted, PartEventType.RoboticMotionStopped, RoboticsShowcasePidBase, SinglePartPid,
                    eventValue: 45f, firstEventOffsetSeconds: 0.0, onDurationSeconds: 4.5, offDurationSeconds: 1.5),
                BuildPartShowcaseRecording(baseUT, "Part Showcase - Robotics Hinge G-00", "hinge.01.s", 118,
                    ShowcaseDistanceFromPadMeters, PartEventType.RoboticMotionStarted, PartEventType.RoboticMotionStopped, RoboticsShowcasePidBase, SinglePartPid,
                    eventValue: 45f, firstEventOffsetSeconds: 0.0, onDurationSeconds: 4.5, offDurationSeconds: 1.5),
                BuildPartShowcaseRecording(baseUT, "Part Showcase - Robotics Hinge M-12", "hinge.03", 119,
                    ShowcaseDistanceFromPadMeters, PartEventType.RoboticMotionStarted, PartEventType.RoboticMotionStopped, RoboticsShowcasePidBase, SinglePartPid,
                    eventValue: 45f, firstEventOffsetSeconds: 0.0, onDurationSeconds: 4.5, offDurationSeconds: 1.5),
                BuildPartShowcaseRecording(baseUT, "Part Showcase - Robotics Hinge M-06", "hinge.03.s", 120,
                    ShowcaseDistanceFromPadMeters, PartEventType.RoboticMotionStarted, PartEventType.RoboticMotionStopped, RoboticsShowcasePidBase, SinglePartPid,
                    eventValue: 45f, firstEventOffsetSeconds: 0.0, onDurationSeconds: 4.5, offDurationSeconds: 1.5),
                BuildPartShowcaseRecording(baseUT, "Part Showcase - Robotics Hinge XL", "hinge.04", 121,
                    ShowcaseDistanceFromPadMeters, PartEventType.RoboticMotionStarted, PartEventType.RoboticMotionStopped, RoboticsShowcasePidBase, SinglePartPid,
                    eventValue: 45f, firstEventOffsetSeconds: 0.0, onDurationSeconds: 4.5, offDurationSeconds: 1.5),
                BuildPartShowcaseRecording(baseUT, "Part Showcase - Robotics Piston 3P6", "piston.01", 122,
                    ShowcaseDistanceFromPadMeters, PartEventType.RoboticMotionStarted, PartEventType.RoboticMotionStopped, RoboticsShowcasePidBase, SinglePartPid,
                    eventValue: 0.3f, firstEventOffsetSeconds: 0.0, onDurationSeconds: 4.5, offDurationSeconds: 1.5),
                BuildPartShowcaseRecording(baseUT, "Part Showcase - Robotics Piston 1P2", "piston.02", 123,
                    ShowcaseDistanceFromPadMeters, PartEventType.RoboticMotionStarted, PartEventType.RoboticMotionStopped, RoboticsShowcasePidBase, SinglePartPid,
                    eventValue: 0.2f, firstEventOffsetSeconds: 0.0, onDurationSeconds: 4.5, offDurationSeconds: 1.5),
                BuildPartShowcaseRecording(baseUT, "Part Showcase - Robotics Piston 1P4", "piston.03", 124,
                    ShowcaseDistanceFromPadMeters, PartEventType.RoboticMotionStarted, PartEventType.RoboticMotionStopped, RoboticsShowcasePidBase, SinglePartPid,
                    eventValue: 0.4f, firstEventOffsetSeconds: 0.0, onDurationSeconds: 4.5, offDurationSeconds: 1.5),
                BuildPartShowcaseRecording(baseUT, "Part Showcase - Robotics Piston 3P12", "piston.04", 125,
                    ShowcaseDistanceFromPadMeters, PartEventType.RoboticMotionStarted, PartEventType.RoboticMotionStopped, RoboticsShowcasePidBase, SinglePartPid,
                    eventValue: 0.9f, firstEventOffsetSeconds: 0.0, onDurationSeconds: 4.5, offDurationSeconds: 1.5),
                BuildPartShowcaseRecording(baseUT, "Part Showcase - Robotics Rotation Servo M-06", "rotoServo.00", 126,
                    ShowcaseDistanceFromPadMeters, PartEventType.RoboticMotionStarted, PartEventType.RoboticMotionStopped, RoboticsShowcasePidBase, SinglePartPid,
                    eventValue: 90f, firstEventOffsetSeconds: 0.0, onDurationSeconds: 4.5, offDurationSeconds: 1.5),
                BuildPartShowcaseRecording(baseUT, "Part Showcase - Robotics Rotation Servo M-12", "rotoServo.02", 127,
                    ShowcaseDistanceFromPadMeters, PartEventType.RoboticMotionStarted, PartEventType.RoboticMotionStopped, RoboticsShowcasePidBase, SinglePartPid,
                    eventValue: 90f, firstEventOffsetSeconds: 0.0, onDurationSeconds: 4.5, offDurationSeconds: 1.5),
                BuildPartShowcaseRecording(baseUT, "Part Showcase - Robotics Rotation Servo F-12", "rotoServo.03", 128,
                    ShowcaseDistanceFromPadMeters, PartEventType.RoboticMotionStarted, PartEventType.RoboticMotionStopped, RoboticsShowcasePidBase, SinglePartPid,
                    eventValue: 90f, firstEventOffsetSeconds: 0.0, onDurationSeconds: 4.5, offDurationSeconds: 1.5),
                BuildPartShowcaseRecording(baseUT, "Part Showcase - Robotics Rotation Servo F-33", "rotoServo.04", 129,
                    ShowcaseDistanceFromPadMeters, PartEventType.RoboticMotionStarted, PartEventType.RoboticMotionStopped, RoboticsShowcasePidBase, SinglePartPid,
                    eventValue: 90f, firstEventOffsetSeconds: 0.0, onDurationSeconds: 4.5, offDurationSeconds: 1.5),
                BuildPartShowcaseRecording(baseUT, "Part Showcase - Robotics Rotor EM-16", "rotor.01", 130,
                    ShowcaseDistanceFromPadMeters, PartEventType.RoboticMotionStarted, PartEventType.RoboticMotionStopped, RoboticsShowcasePidBase, SinglePartPid,
                    eventValue: 240f, firstEventOffsetSeconds: 0.0, onDurationSeconds: 4.5, offDurationSeconds: 1.5),
                BuildPartShowcaseRecording(baseUT, "Part Showcase - Robotics Rotor EM-16S", "rotor.01s", 131,
                    ShowcaseDistanceFromPadMeters, PartEventType.RoboticMotionStarted, PartEventType.RoboticMotionStopped, RoboticsShowcasePidBase, SinglePartPid,
                    eventValue: 240f, firstEventOffsetSeconds: 0.0, onDurationSeconds: 4.5, offDurationSeconds: 1.5),
                BuildPartShowcaseRecording(baseUT, "Part Showcase - Robotics Rotor EM-32", "rotor.02", 132,
                    ShowcaseDistanceFromPadMeters, PartEventType.RoboticMotionStarted, PartEventType.RoboticMotionStopped, RoboticsShowcasePidBase, SinglePartPid,
                    eventValue: 240f, firstEventOffsetSeconds: 0.0, onDurationSeconds: 4.5, offDurationSeconds: 1.5),
                BuildPartShowcaseRecording(baseUT, "Part Showcase - Robotics Rotor EM-32S", "rotor.02s", 133,
                    ShowcaseDistanceFromPadMeters, PartEventType.RoboticMotionStarted, PartEventType.RoboticMotionStopped, RoboticsShowcasePidBase, SinglePartPid,
                    eventValue: 240f, firstEventOffsetSeconds: 0.0, onDurationSeconds: 4.5, offDurationSeconds: 1.5),
                BuildPartShowcaseRecording(baseUT, "Part Showcase - Robotics Rotor EM-64", "rotor.03", 134,
                    ShowcaseDistanceFromPadMeters, PartEventType.RoboticMotionStarted, PartEventType.RoboticMotionStopped, RoboticsShowcasePidBase, SinglePartPid,
                    eventValue: 240f, firstEventOffsetSeconds: 0.0, onDurationSeconds: 4.5, offDurationSeconds: 1.5),
                BuildPartShowcaseRecording(baseUT, "Part Showcase - Robotics Rotor EM-64S", "rotor.03s", 135,
                    ShowcaseDistanceFromPadMeters, PartEventType.RoboticMotionStarted, PartEventType.RoboticMotionStopped, RoboticsShowcasePidBase, SinglePartPid,
                    eventValue: 240f, firstEventOffsetSeconds: 0.0, onDurationSeconds: 4.5, offDurationSeconds: 1.5),
                BuildPartShowcaseRecording(baseUT, "Part Showcase - Robotics Rotor Motor R121", "RotorEngine.02", 136,
                    ShowcaseDistanceFromPadMeters, PartEventType.RoboticMotionStarted, PartEventType.RoboticMotionStopped, RoboticsShowcasePidBase, SinglePartPid,
                    eventValue: 240f, firstEventOffsetSeconds: 0.0, onDurationSeconds: 4.5, offDurationSeconds: 1.5),
                BuildPartShowcaseRecording(baseUT, "Part Showcase - Robotics Rotor Motor R7000", "RotorEngine.03", 137,
                    ShowcaseDistanceFromPadMeters, PartEventType.RoboticMotionStarted, PartEventType.RoboticMotionStopped, RoboticsShowcasePidBase, SinglePartPid,
                    eventValue: 240f, firstEventOffsetSeconds: 0.0, onDurationSeconds: 4.5, offDurationSeconds: 1.5)
            };
        }

        internal static RecordingBuilder[] AeroSurfaceShowcaseRecordings(double baseUT = 0)
        {
            return new[]
            {
                BuildPartShowcaseRecording(baseUT, "Part Showcase - Airbrake", "airbrake1", 138,
                    ShowcaseDistanceFromPadMeters, PartEventType.DeployableExtended, PartEventType.DeployableRetracted, AeroSurfaceShowcasePidBase, SinglePartPid,
                    firstEventOffsetSeconds: 0.0, onDurationSeconds: 4.5, offDurationSeconds: 1.5)
            };
        }

        internal static RecordingBuilder[] RobotArmScannerShowcaseRecordings(double baseUT = 0)
        {
            return new[]
            {
                BuildPartShowcaseRecording(baseUT, "Part Showcase - Robot Arm Scanner S1", "RobotArmScanner_S1", 139,
                    ShowcaseDistanceFromPadMeters, PartEventType.DeployableExtended, PartEventType.DeployableRetracted, RobotArmScannerShowcasePidBase, SinglePartPid,
                    firstEventOffsetSeconds: 0.0, onDurationSeconds: 4.5, offDurationSeconds: 1.5),
                BuildPartShowcaseRecording(baseUT, "Part Showcase - Robot Arm Scanner S2", "RobotArmScanner_S2", 140,
                    ShowcaseDistanceFromPadMeters, PartEventType.DeployableExtended, PartEventType.DeployableRetracted, RobotArmScannerShowcasePidBase, SinglePartPid,
                    firstEventOffsetSeconds: 0.0, onDurationSeconds: 4.5, offDurationSeconds: 1.5),
                BuildPartShowcaseRecording(baseUT, "Part Showcase - Robot Arm Scanner S3", "RobotArmScanner_S3", 141,
                    ShowcaseDistanceFromPadMeters, PartEventType.DeployableExtended, PartEventType.DeployableRetracted, RobotArmScannerShowcasePidBase, SinglePartPid,
                    firstEventOffsetSeconds: 0.0, onDurationSeconds: 4.5, offDurationSeconds: 1.5)
            };
        }

        internal static RecordingBuilder[] ControlSurfaceShowcaseRecordings(double baseUT = 0)
        {
            return new[]
            {
                BuildPartShowcaseRecording(baseUT, "Part Showcase - Control Surface Advanced Canard", "AdvancedCanard", 142,
                    ShowcaseDistanceFromPadMeters, PartEventType.DeployableExtended, PartEventType.DeployableRetracted, ControlSurfaceShowcasePidBase, SinglePartPid,
                    firstEventOffsetSeconds: 0.0, onDurationSeconds: 4.5, offDurationSeconds: 1.5),
                BuildPartShowcaseRecording(baseUT, "Part Showcase - Control Surface Airliner", "airlinerCtrlSrf", 143,
                    ShowcaseDistanceFromPadMeters, PartEventType.DeployableExtended, PartEventType.DeployableRetracted, ControlSurfaceShowcasePidBase, SinglePartPid,
                    firstEventOffsetSeconds: 0.0, onDurationSeconds: 4.5, offDurationSeconds: 1.5),
                BuildPartShowcaseRecording(baseUT, "Part Showcase - Control Surface Airliner Tail", "airlinerTailFin", 144,
                    ShowcaseDistanceFromPadMeters, PartEventType.DeployableExtended, PartEventType.DeployableRetracted, ControlSurfaceShowcasePidBase, SinglePartPid,
                    firstEventOffsetSeconds: 0.0, onDurationSeconds: 4.5, offDurationSeconds: 1.5),
                BuildPartShowcaseRecording(baseUT, "Part Showcase - Control Surface Canard", "CanardController", 145,
                    ShowcaseDistanceFromPadMeters, PartEventType.DeployableExtended, PartEventType.DeployableRetracted, ControlSurfaceShowcasePidBase, SinglePartPid,
                    firstEventOffsetSeconds: 0.0, onDurationSeconds: 4.5, offDurationSeconds: 1.5),
                BuildPartShowcaseRecording(baseUT, "Part Showcase - Control Surface Elevon 2", "elevon2", 146,
                    ShowcaseDistanceFromPadMeters, PartEventType.DeployableExtended, PartEventType.DeployableRetracted, ControlSurfaceShowcasePidBase, SinglePartPid,
                    firstEventOffsetSeconds: 0.0, onDurationSeconds: 4.5, offDurationSeconds: 1.5),
                BuildPartShowcaseRecording(baseUT, "Part Showcase - Control Surface Elevon 3", "elevon3", 147,
                    ShowcaseDistanceFromPadMeters, PartEventType.DeployableExtended, PartEventType.DeployableRetracted, ControlSurfaceShowcasePidBase, SinglePartPid,
                    firstEventOffsetSeconds: 0.0, onDurationSeconds: 4.5, offDurationSeconds: 1.5),
                BuildPartShowcaseRecording(baseUT, "Part Showcase - Control Surface Elevon 5", "elevon5", 148,
                    ShowcaseDistanceFromPadMeters, PartEventType.DeployableExtended, PartEventType.DeployableRetracted, ControlSurfaceShowcasePidBase, SinglePartPid,
                    firstEventOffsetSeconds: 0.0, onDurationSeconds: 4.5, offDurationSeconds: 1.5),
                BuildPartShowcaseRecording(baseUT, "Part Showcase - Control Surface Large Fan Blade", "largeFanBlade", 149,
                    ShowcaseDistanceFromPadMeters, PartEventType.DeployableExtended, PartEventType.DeployableRetracted, ControlSurfaceShowcasePidBase, SinglePartPid,
                    firstEventOffsetSeconds: 0.0, onDurationSeconds: 4.5, offDurationSeconds: 1.5),
                BuildPartShowcaseRecording(baseUT, "Part Showcase - Control Surface Large Heli Blade", "largeHeliBlade", 150,
                    ShowcaseDistanceFromPadMeters, PartEventType.DeployableExtended, PartEventType.DeployableRetracted, ControlSurfaceShowcasePidBase, SinglePartPid,
                    firstEventOffsetSeconds: 0.0, onDurationSeconds: 4.5, offDurationSeconds: 1.5),
                BuildPartShowcaseRecording(baseUT, "Part Showcase - Control Surface Large Propeller", "largePropeller", 151,
                    ShowcaseDistanceFromPadMeters, PartEventType.DeployableExtended, PartEventType.DeployableRetracted, ControlSurfaceShowcasePidBase, SinglePartPid,
                    firstEventOffsetSeconds: 0.0, onDurationSeconds: 4.5, offDurationSeconds: 1.5),
                BuildPartShowcaseRecording(baseUT, "Part Showcase - Control Surface Medium Fan Blade", "mediumFanBlade", 152,
                    ShowcaseDistanceFromPadMeters, PartEventType.DeployableExtended, PartEventType.DeployableRetracted, ControlSurfaceShowcasePidBase, SinglePartPid,
                    firstEventOffsetSeconds: 0.0, onDurationSeconds: 4.5, offDurationSeconds: 1.5),
                BuildPartShowcaseRecording(baseUT, "Part Showcase - Control Surface Medium Heli Blade", "mediumHeliBlade", 153,
                    ShowcaseDistanceFromPadMeters, PartEventType.DeployableExtended, PartEventType.DeployableRetracted, ControlSurfaceShowcasePidBase, SinglePartPid,
                    firstEventOffsetSeconds: 0.0, onDurationSeconds: 4.5, offDurationSeconds: 1.5),
                BuildPartShowcaseRecording(baseUT, "Part Showcase - Control Surface Medium Propeller", "mediumPropeller", 154,
                    ShowcaseDistanceFromPadMeters, PartEventType.DeployableExtended, PartEventType.DeployableRetracted, ControlSurfaceShowcasePidBase, SinglePartPid,
                    firstEventOffsetSeconds: 0.0, onDurationSeconds: 4.5, offDurationSeconds: 1.5),
                BuildPartShowcaseRecording(baseUT, "Part Showcase - Control Surface R8 Winglet", "R8winglet", 155,
                    ShowcaseDistanceFromPadMeters, PartEventType.DeployableExtended, PartEventType.DeployableRetracted, ControlSurfaceShowcasePidBase, SinglePartPid,
                    firstEventOffsetSeconds: 0.0, onDurationSeconds: 4.5, offDurationSeconds: 1.5),
                BuildPartShowcaseRecording(baseUT, "Part Showcase - Control Surface Small", "smallCtrlSrf", 156,
                    ShowcaseDistanceFromPadMeters, PartEventType.DeployableExtended, PartEventType.DeployableRetracted, ControlSurfaceShowcasePidBase, SinglePartPid,
                    firstEventOffsetSeconds: 0.0, onDurationSeconds: 4.5, offDurationSeconds: 1.5),
                BuildPartShowcaseRecording(baseUT, "Part Showcase - Control Surface Small Fan Blade", "smallFanBlade", 157,
                    ShowcaseDistanceFromPadMeters, PartEventType.DeployableExtended, PartEventType.DeployableRetracted, ControlSurfaceShowcasePidBase, SinglePartPid,
                    firstEventOffsetSeconds: 0.0, onDurationSeconds: 4.5, offDurationSeconds: 1.5),
                BuildPartShowcaseRecording(baseUT, "Part Showcase - Control Surface Small Heli Blade", "smallHeliBlade", 158,
                    ShowcaseDistanceFromPadMeters, PartEventType.DeployableExtended, PartEventType.DeployableRetracted, ControlSurfaceShowcasePidBase, SinglePartPid,
                    firstEventOffsetSeconds: 0.0, onDurationSeconds: 4.5, offDurationSeconds: 1.5),
                BuildPartShowcaseRecording(baseUT, "Part Showcase - Control Surface Small Propeller", "smallPropeller", 159,
                    ShowcaseDistanceFromPadMeters, PartEventType.DeployableExtended, PartEventType.DeployableRetracted, ControlSurfaceShowcasePidBase, SinglePartPid,
                    firstEventOffsetSeconds: 0.0, onDurationSeconds: 4.5, offDurationSeconds: 1.5),
                BuildPartShowcaseRecording(baseUT, "Part Showcase - Control Surface Standard", "StandardCtrlSrf", 160,
                    ShowcaseDistanceFromPadMeters, PartEventType.DeployableExtended, PartEventType.DeployableRetracted, ControlSurfaceShowcasePidBase, SinglePartPid,
                    firstEventOffsetSeconds: 0.0, onDurationSeconds: 4.5, offDurationSeconds: 1.5),
                BuildPartShowcaseRecording(baseUT, "Part Showcase - Control Surface Tailfin", "tailfin", 161,
                    ShowcaseDistanceFromPadMeters, PartEventType.DeployableExtended, PartEventType.DeployableRetracted, ControlSurfaceShowcasePidBase, SinglePartPid,
                    firstEventOffsetSeconds: 0.0, onDurationSeconds: 4.5, offDurationSeconds: 1.5),
                BuildPartShowcaseRecording(baseUT, "Part Showcase - Control Surface Winglet", "winglet3", 162,
                    ShowcaseDistanceFromPadMeters, PartEventType.DeployableExtended, PartEventType.DeployableRetracted, ControlSurfaceShowcasePidBase, SinglePartPid,
                    firstEventOffsetSeconds: 0.0, onDurationSeconds: 4.5, offDurationSeconds: 1.5),
                BuildPartShowcaseRecording(baseUT, "Part Showcase - Control Surface Shuttle Elevon 1", "wingShuttleElevon1", 163,
                    ShowcaseDistanceFromPadMeters, PartEventType.DeployableExtended, PartEventType.DeployableRetracted, ControlSurfaceShowcasePidBase, SinglePartPid,
                    firstEventOffsetSeconds: 0.0, onDurationSeconds: 4.5, offDurationSeconds: 1.5),
                BuildPartShowcaseRecording(baseUT, "Part Showcase - Control Surface Shuttle Elevon 2", "wingShuttleElevon2", 164,
                    ShowcaseDistanceFromPadMeters, PartEventType.DeployableExtended, PartEventType.DeployableRetracted, ControlSurfaceShowcasePidBase, SinglePartPid,
                    firstEventOffsetSeconds: 0.0, onDurationSeconds: 4.5, offDurationSeconds: 1.5),
                BuildPartShowcaseRecording(baseUT, "Part Showcase - Control Surface Shuttle Rudder", "wingShuttleRudder", 165,
                    ShowcaseDistanceFromPadMeters, PartEventType.DeployableExtended, PartEventType.DeployableRetracted, ControlSurfaceShowcasePidBase, SinglePartPid,
                    firstEventOffsetSeconds: 0.0, onDurationSeconds: 4.5, offDurationSeconds: 1.5)
            };
        }

        internal static RecordingBuilder[] WheelDynamicsShowcaseRecordings(double baseUT = 0)
        {
            return new[]
            {
                BuildPartShowcaseRecording(baseUT, "Part Showcase - Wheel Dynamics Gear Fixed", "GearFixed", 166,
                    ShowcaseDistanceFromPadMeters, PartEventType.RoboticMotionStarted, PartEventType.RoboticMotionStopped, WheelDynamicsShowcasePidBase, SinglePartPid,
                    eventValue: 0.08f, moduleIndex: 0,
                    firstEventOffsetSeconds: 0.0, onDurationSeconds: 4.5, offDurationSeconds: 1.5),
                BuildPartShowcaseRecording(baseUT, "Part Showcase - Wheel Dynamics Gear Free", "GearFree", 167,
                    ShowcaseDistanceFromPadMeters, PartEventType.RoboticMotionStarted, PartEventType.RoboticMotionStopped, WheelDynamicsShowcasePidBase, SinglePartPid,
                    eventValue: 24f, moduleIndex: 1,
                    firstEventOffsetSeconds: 0.0, onDurationSeconds: 4.5, offDurationSeconds: 1.5),
                BuildPartShowcaseRecording(baseUT, "Part Showcase - Wheel Dynamics Rover M1", "roverWheel1", 168,
                    ShowcaseDistanceFromPadMeters, PartEventType.RoboticMotionStarted, PartEventType.RoboticMotionStopped, WheelDynamicsShowcasePidBase, SinglePartPid,
                    eventValue: 180f, moduleIndex: 2,
                    firstEventOffsetSeconds: 0.0, onDurationSeconds: 4.5, offDurationSeconds: 1.5),
                BuildPartShowcaseRecording(baseUT, "Part Showcase - Wheel Dynamics Rover S2", "roverWheel2", 169,
                    ShowcaseDistanceFromPadMeters, PartEventType.RoboticMotionStarted, PartEventType.RoboticMotionStopped, WheelDynamicsShowcasePidBase, SinglePartPid,
                    eventValue: 180f, moduleIndex: 2,
                    firstEventOffsetSeconds: 0.0, onDurationSeconds: 4.5, offDurationSeconds: 1.5),
                BuildPartShowcaseRecording(baseUT, "Part Showcase - Wheel Dynamics Rover XL3", "roverWheel3", 170,
                    ShowcaseDistanceFromPadMeters, PartEventType.RoboticMotionStarted, PartEventType.RoboticMotionStopped, WheelDynamicsShowcasePidBase, SinglePartPid,
                    eventValue: 120f, moduleIndex: 1,
                    firstEventOffsetSeconds: 0.0, onDurationSeconds: 4.5, offDurationSeconds: 1.5),
                BuildPartShowcaseRecording(baseUT, "Part Showcase - Wheel Dynamics TR-2L", "wheelMed", 171,
                    ShowcaseDistanceFromPadMeters, PartEventType.RoboticMotionStarted, PartEventType.RoboticMotionStopped, WheelDynamicsShowcasePidBase, SinglePartPid,
                    eventValue: 180f, moduleIndex: 2,
                    firstEventOffsetSeconds: 0.0, onDurationSeconds: 4.5, offDurationSeconds: 1.5)
            };
        }

        internal static RecordingBuilder[] AnimateHeatShowcaseRecordings(double baseUT = 0)
        {
            return new[]
            {
                BuildAnimateHeat3StateShowcase(baseUT, "Part Showcase - AnimateHeat Airplane Tail", "airplaneTail", 172),
                BuildAnimateHeat3StateShowcase(baseUT, "Part Showcase - AnimateHeat Airplane Tail B", "airplaneTailB", 173),
                BuildAnimateHeat3StateShowcase(baseUT, "Part Showcase - AnimateHeat Avionics Nose Cone", "avionicsNoseCone", 174),
                BuildAnimateHeat3StateShowcase(baseUT, "Part Showcase - AnimateHeat Circular Intake", "CircularIntake", 175),
                BuildAnimateHeat3StateShowcase(baseUT, "Part Showcase - AnimateHeat Mk1 Intake Fuselage", "MK1IntakeFuselage", 176),
                BuildAnimateHeat3StateShowcase(baseUT, "Part Showcase - AnimateHeat Nacelle Body", "nacelleBody", 177),
                BuildAnimateHeat3StateShowcase(baseUT, "Part Showcase - AnimateHeat Nose Cone Adapter", "noseConeAdapter", 178),
                BuildAnimateHeat3StateShowcase(baseUT, "Part Showcase - AnimateHeat Pointy Nose Cone A", "pointyNoseConeA", 179),
                BuildAnimateHeat3StateShowcase(baseUT, "Part Showcase - AnimateHeat Pointy Nose Cone B", "pointyNoseConeB", 180),
                BuildAnimateHeat3StateShowcase(baseUT, "Part Showcase - AnimateHeat Radial Engine Body", "radialEngineBody", 181),
                BuildAnimateHeat3StateShowcase(baseUT, "Part Showcase - AnimateHeat Ram Air Intake", "ramAirIntake", 182),
                BuildAnimateHeat3StateShowcase(baseUT, "Part Showcase - AnimateHeat Shock Cone Intake", "shockConeIntake", 183),
                BuildAnimateHeat3StateShowcase(baseUT, "Part Showcase - AnimateHeat Standard Nose Cone", "standardNoseCone", 184)
            };
        }

        /// <summary>
        /// Builds an AnimateHeat showcase recording with a 3-state cycle:
        /// Cold -> Medium -> Hot -> Medium -> Cold, repeating across the 24-second clip.
        /// 10 events per recording.
        /// </summary>
        private static RecordingBuilder BuildAnimateHeat3StateShowcase(
            double baseUT, string vesselName, string partName, int rowIndex)
        {
            double t = baseUT + 30;
            ShowcasePosition(rowIndex, ShowcaseDistanceFromPadMeters, out double lat, out double lon, out double alt);
            alt += ShowcaseAltitudeOffset(partName);

            var b = new RecordingBuilder(vesselName)
                .WithDefaultRotation(KscRotX, KscRotY, KscRotZ, KscRotW)
                .WithLoopPlayback(loop: true, intervalSeconds: 0.0)
                .WithRecordingGroup("Part Showcases");

            // Static trajectory (24s)
            for (int i = 0; i <= 8; i++)
                b.AddPoint(t + (i * 3), lat, lon, alt);

            // 3-state heat cycle: Cold -> Medium -> Hot -> Medium -> Cold (repeating)
            b.AddPartEvent(t + 0.0,  SinglePartPid, (int)PartEventType.ThermalAnimationMedium, partName, value: 0.5f);
            b.AddPartEvent(t + 2.0,  SinglePartPid, (int)PartEventType.ThermalAnimationHot,    partName, value: 1.0f);
            b.AddPartEvent(t + 5.0,  SinglePartPid, (int)PartEventType.ThermalAnimationMedium, partName, value: 0.5f);
            b.AddPartEvent(t + 7.0,  SinglePartPid, (int)PartEventType.ThermalAnimationCold,   partName, value: 0.0f);
            b.AddPartEvent(t + 10.0, SinglePartPid, (int)PartEventType.ThermalAnimationMedium, partName, value: 0.5f);
            b.AddPartEvent(t + 12.0, SinglePartPid, (int)PartEventType.ThermalAnimationHot,    partName, value: 1.0f);
            b.AddPartEvent(t + 15.0, SinglePartPid, (int)PartEventType.ThermalAnimationMedium, partName, value: 0.5f);
            b.AddPartEvent(t + 17.0, SinglePartPid, (int)PartEventType.ThermalAnimationCold,   partName, value: 0.0f);
            b.AddPartEvent(t + 20.0, SinglePartPid, (int)PartEventType.ThermalAnimationMedium, partName, value: 0.5f);
            b.AddPartEvent(t + 22.0, SinglePartPid, (int)PartEventType.ThermalAnimationHot,    partName, value: 1.0f);

            var snap = new VesselSnapshotBuilder()
                .WithName(vesselName)
                .WithPersistentId((uint)(AnimateHeatShowcasePidBase + rowIndex))
                .AddPart(partName, rotation: "0,-0.7071068,0,0.7071068")
                .AsLanded(lat, lon, alt)
                .Build();

            b.WithGhostVisualSnapshot(snap);

            return b;
        }

        /// <summary>
        /// Showcase recordings for parts with ModuleColorChanger cabin lights (Pattern A:
        /// toggleInFlight=true, _EmissiveColor). These toggle LightOn/LightOff events to
        /// exercise the emissive color change on command pods that use ModuleColorChanger
        /// instead of ModuleLight.
        /// </summary>
        internal static RecordingBuilder[] ColorChangerShowcaseRecordings(double baseUT = 0)
        {
            return new[]
            {
                // Pattern A: Cabin lights (command pods, rows 95-100)
                BuildPartShowcaseRecording(baseUT, "Part Showcase - ColorChanger Mk1 Pod", "mk1pod.v2", 95,
                    ShowcaseDistanceFromPadMeters, PartEventType.LightOn, PartEventType.LightOff, ColorChangerShowcasePidBase, SinglePartPid),
                BuildPartShowcaseRecording(baseUT, "Part Showcase - ColorChanger Mk1-3 Pod", "mk1-3pod", 96,
                    ShowcaseDistanceFromPadMeters, PartEventType.LightOn, PartEventType.LightOff, ColorChangerShowcasePidBase, SinglePartPid),
                BuildPartShowcaseRecording(baseUT, "Part Showcase - ColorChanger Cupola", "cupola", 97,
                    ShowcaseDistanceFromPadMeters, PartEventType.LightOn, PartEventType.LightOff, ColorChangerShowcasePidBase, SinglePartPid),
                BuildPartShowcaseRecording(baseUT, "Part Showcase - ColorChanger Mk2 Lander", "mk2LanderCabin.v2", 98,
                    ShowcaseDistanceFromPadMeters, PartEventType.LightOn, PartEventType.LightOff, ColorChangerShowcasePidBase, SinglePartPid),
                BuildPartShowcaseRecording(baseUT, "Part Showcase - ColorChanger Mk1 Cockpit", "Mark1Cockpit", 99,
                    ShowcaseDistanceFromPadMeters, PartEventType.LightOn, PartEventType.LightOff, ColorChangerShowcasePidBase, SinglePartPid),
                BuildPartShowcaseRecording(baseUT, "Part Showcase - ColorChanger Mk2 Cockpit", "mk2Cockpit.Standard", 100,
                    ShowcaseDistanceFromPadMeters, PartEventType.LightOn, PartEventType.LightOff, ColorChangerShowcasePidBase, SinglePartPid),

                // Pattern A continued: MH command pods + stock cabins (rows 101-116)
                BuildPartShowcaseRecording(baseUT, "Part Showcase - ColorChanger KV-1 Pod", "kv1Pod", 101,
                    ShowcaseDistanceFromPadMeters, PartEventType.LightOn, PartEventType.LightOff, ColorChangerShowcasePidBase, SinglePartPid),
                BuildPartShowcaseRecording(baseUT, "Part Showcase - ColorChanger KV-2 Pod", "kv2Pod", 102,
                    ShowcaseDistanceFromPadMeters, PartEventType.LightOn, PartEventType.LightOff, ColorChangerShowcasePidBase, SinglePartPid),
                BuildPartShowcaseRecording(baseUT, "Part Showcase - ColorChanger KV-3 Pod", "kv3Pod", 103,
                    ShowcaseDistanceFromPadMeters, PartEventType.LightOn, PartEventType.LightOff, ColorChangerShowcasePidBase, SinglePartPid),
                BuildPartShowcaseRecording(baseUT, "Part Showcase - ColorChanger Mk1 Lander Can", "landerCabinSmall", 104,
                    ShowcaseDistanceFromPadMeters, PartEventType.LightOn, PartEventType.LightOff, ColorChangerShowcasePidBase, SinglePartPid),
                BuildPartShowcaseRecording(baseUT, "Part Showcase - ColorChanger Mk2 Cockpit Inline", "Mark2Cockpit", 105,
                    ShowcaseDistanceFromPadMeters, PartEventType.LightOn, PartEventType.LightOff, ColorChangerShowcasePidBase, SinglePartPid),
                BuildPartShowcaseRecording(baseUT, "Part Showcase - ColorChanger Mk2 Cockpit IVA", "mk2Cockpit.Inline", 106,
                    ShowcaseDistanceFromPadMeters, PartEventType.LightOn, PartEventType.LightOff, ColorChangerShowcasePidBase, SinglePartPid),
                BuildPartShowcaseRecording(baseUT, "Part Showcase - ColorChanger Mk3 Shuttle Cockpit", "mk3Cockpit.Shuttle", 107,
                    ShowcaseDistanceFromPadMeters, PartEventType.LightOn, PartEventType.LightOff, ColorChangerShowcasePidBase, SinglePartPid),
                BuildPartShowcaseRecording(baseUT, "Part Showcase - ColorChanger Mk2 Pod", "Mk2Pod", 108,
                    ShowcaseDistanceFromPadMeters, PartEventType.LightOn, PartEventType.LightOff, ColorChangerShowcasePidBase, SinglePartPid),
                BuildPartShowcaseRecording(baseUT, "Part Showcase - ColorChanger Crew Cabin", "crewCabin", 109,
                    ShowcaseDistanceFromPadMeters, PartEventType.LightOn, PartEventType.LightOff, ColorChangerShowcasePidBase, SinglePartPid),
                BuildPartShowcaseRecording(baseUT, "Part Showcase - ColorChanger Mk1 Crew Cabin", "MK1CrewCabin", 110,
                    ShowcaseDistanceFromPadMeters, PartEventType.LightOn, PartEventType.LightOff, ColorChangerShowcasePidBase, SinglePartPid),
                BuildPartShowcaseRecording(baseUT, "Part Showcase - ColorChanger Mk2 Crew Cabin", "mk2CrewCabin", 111,
                    ShowcaseDistanceFromPadMeters, PartEventType.LightOn, PartEventType.LightOff, ColorChangerShowcasePidBase, SinglePartPid),
                BuildPartShowcaseRecording(baseUT, "Part Showcase - ColorChanger Mk3 Crew Cabin", "mk3CrewCabin", 112,
                    ShowcaseDistanceFromPadMeters, PartEventType.LightOn, PartEventType.LightOff, ColorChangerShowcasePidBase, SinglePartPid),
                BuildPartShowcaseRecording(baseUT, "Part Showcase - ColorChanger Science Lab", "Large.Crewed.Lab", 113,
                    ShowcaseDistanceFromPadMeters, PartEventType.LightOn, PartEventType.LightOff, ColorChangerShowcasePidBase, SinglePartPid),
                BuildPartShowcaseRecording(baseUT, "Part Showcase - ColorChanger MEM Lander", "MEMLander", 114,
                    ShowcaseDistanceFromPadMeters, PartEventType.LightOn, PartEventType.LightOff, ColorChangerShowcasePidBase, SinglePartPid),
                BuildPartShowcaseRecording(baseUT, "Part Showcase - ColorChanger Docking Port", "dockingPort2", 115,
                    ShowcaseDistanceFromPadMeters, PartEventType.LightOn, PartEventType.LightOff, ColorChangerShowcasePidBase, SinglePartPid),
                BuildPartShowcaseRecording(baseUT, "Part Showcase - ColorChanger Docking Port Sr", "dockingPortLarge", 116,
                    ShowcaseDistanceFromPadMeters, PartEventType.LightOn, PartEventType.LightOff, ColorChangerShowcasePidBase, SinglePartPid),

                // Pattern A: EVA kerbal helmet lights (rows 236-237)
                // Kerbals face toward pad (rotY+90°) so observer sees front, not back
                BuildPartShowcaseRecording(baseUT, "Part Showcase - ColorChanger EVA Kerbal", "kerbalEVAFuture", 236,
                    ShowcaseDistanceFromPadMeters, PartEventType.LightOn, PartEventType.LightOff, ColorChangerShowcasePidBase, SinglePartPid,
                    configureGhostPartNode: node => node.SetValue("rot", "0,0.7071068,0,0.7071068", true)),
                BuildPartShowcaseRecording(baseUT, "Part Showcase - ColorChanger EVA Kerbal Female", "kerbalEVAfemaleFuture", 237,
                    ShowcaseDistanceFromPadMeters, PartEventType.LightOn, PartEventType.LightOff, ColorChangerShowcasePidBase, SinglePartPid,
                    configureGhostPartNode: node => node.SetValue("rot", "0,0.7071068,0,0.7071068", true))
            };
        }

        internal static RecordingBuilder[] JettisonShowcaseRecordings(double baseUT = 0)
        {
            return new[]
            {
                BuildPartShowcaseRecording(baseUT, "Part Showcase - Engine Plate 1.5", "EnginePlate1p5", 86,
                    ShowcaseDistanceFromPadMeters, PartEventType.ShroudJettisoned, PartEventType.ShroudJettisoned, JettisonShowcasePidBase, SinglePartPid,
                    firstEventOffsetSeconds: 3.0),
                BuildPartShowcaseRecording(baseUT, "Part Showcase - Engine Plate 2", "EnginePlate2", 87,
                    ShowcaseDistanceFromPadMeters, PartEventType.ShroudJettisoned, PartEventType.ShroudJettisoned, JettisonShowcasePidBase, SinglePartPid,
                    firstEventOffsetSeconds: 3.0),
                BuildPartShowcaseRecording(baseUT, "Part Showcase - Engine Plate 3", "EnginePlate3", 88,
                    ShowcaseDistanceFromPadMeters, PartEventType.ShroudJettisoned, PartEventType.ShroudJettisoned, JettisonShowcasePidBase, SinglePartPid,
                    firstEventOffsetSeconds: 3.0),
                BuildPartShowcaseRecording(baseUT, "Part Showcase - Engine Plate 4", "EnginePlate4", 89,
                    ShowcaseDistanceFromPadMeters, PartEventType.ShroudJettisoned, PartEventType.ShroudJettisoned, JettisonShowcasePidBase, SinglePartPid,
                    firstEventOffsetSeconds: 3.0),
                BuildPartShowcaseRecording(baseUT, "Part Showcase - Engine Plate 5", "EnginePlate5", 90,
                    ShowcaseDistanceFromPadMeters, PartEventType.ShroudJettisoned, PartEventType.ShroudJettisoned, JettisonShowcasePidBase, SinglePartPid,
                    firstEventOffsetSeconds: 3.0),
                BuildPartShowcaseRecording(baseUT, "Part Showcase - Heat Shield 1", "HeatShield1", 91,
                    ShowcaseDistanceFromPadMeters, PartEventType.ShroudJettisoned, PartEventType.ShroudJettisoned, JettisonShowcasePidBase, SinglePartPid,
                    firstEventOffsetSeconds: 3.0),
                BuildPartShowcaseRecording(baseUT, "Part Showcase - Heat Shield 2", "HeatShield2", 92,
                    ShowcaseDistanceFromPadMeters, PartEventType.ShroudJettisoned, PartEventType.ShroudJettisoned, JettisonShowcasePidBase, SinglePartPid,
                    firstEventOffsetSeconds: 3.0),
                BuildPartShowcaseRecording(baseUT, "Part Showcase - Heat Shield 3", "HeatShield3", 93,
                    ShowcaseDistanceFromPadMeters, PartEventType.ShroudJettisoned, PartEventType.ShroudJettisoned, JettisonShowcasePidBase, SinglePartPid,
                    firstEventOffsetSeconds: 3.0),
                BuildPartShowcaseRecording(baseUT, "Part Showcase - Heat Shield 1.5", "HeatShield1p5", 94,
                    ShowcaseDistanceFromPadMeters, PartEventType.ShroudJettisoned, PartEventType.ShroudJettisoned, JettisonShowcasePidBase, SinglePartPid,
                    firstEventOffsetSeconds: 3.0)
            };
        }

        internal static RecordingBuilder InventoryPlacementShowcaseRecording(double baseUT = 0)
        {
            const int rowIndex = ShowcaseRowCount - 1;
            const string partName = "DeployedWeatherStn";
            double t = baseUT + 30;
            ShowcasePosition(rowIndex, ShowcaseDistanceFromPadMeters, out double lat, out double lon, out double alt);
            alt += ShowcaseAltitudeOffset(partName);
            var b = new RecordingBuilder("Part Showcase - Inventory Placement")
                .WithDefaultRotation(KscRotX, KscRotY, KscRotZ, KscRotW)
                .WithLoopPlayback(loop: true, intervalSeconds: 0.0)
                .WithRecordingGroup("Part Showcases");

            for (int i = 0; i <= 8; i++)
                b.AddPoint(t + (i * 3), lat, lon, alt);

            // 24s loop: place -> deploy -> retract -> remove, then repeat once.
            b.AddPartEvent(t + 0.5, SinglePartPid, (int)PartEventType.InventoryPartPlaced, partName);
            b.AddPartEvent(t + 1.0, SinglePartPid, (int)PartEventType.DeployableExtended, partName);
            b.AddPartEvent(t + 4.0, SinglePartPid, (int)PartEventType.DeployableRetracted, partName);
            b.AddPartEvent(t + 4.5, SinglePartPid, (int)PartEventType.InventoryPartRemoved, partName);
            b.AddPartEvent(t + 12.5, SinglePartPid, (int)PartEventType.InventoryPartPlaced, partName);
            b.AddPartEvent(t + 13.0, SinglePartPid, (int)PartEventType.DeployableExtended, partName);
            b.AddPartEvent(t + 16.0, SinglePartPid, (int)PartEventType.DeployableRetracted, partName);
            b.AddPartEvent(t + 16.5, SinglePartPid, (int)PartEventType.InventoryPartRemoved, partName);

            var snap = new VesselSnapshotBuilder()
                .WithName("Part Showcase - Inventory Placement")
                .WithPersistentId(InventoryPlacementPid)
                .AddPart(partName, rotation: "0,-0.7071068,0,0.7071068")
                .AddPart("kerbalEVA", position: "2.25,0,0", rotation: "0,0.7071068,0,0.7071068", parentIndex: 0)
                .AsLanded(lat, lon, alt)
                .Build();

            b.WithGhostVisualSnapshot(snap);
            return b;
        }

        internal static RecordingBuilder FlagPlantShowcaseRecording(double baseUT = 0)
        {
            const int rowIndex = ShowcaseRowCount - 1; // last row
            double t = baseUT + 30;
            ShowcasePosition(rowIndex, ShowcaseDistanceFromPadMeters, out double lat, out double lon, out double alt);
            alt += ShowcaseAltitudeOffset("kerbalEVA");

            var b = new RecordingBuilder("Part Showcase - Flag Plant")
                .WithDefaultRotation(KscRotX, KscRotY, KscRotZ, KscRotW)
                .WithLoopPlayback(loop: true, intervalSeconds: 0.0)
                .WithRecordingGroup("Part Showcases");

            for (int i = 0; i <= 8; i++)
                b.AddPoint(t + (i * 3), lat, lon, alt);

            // Flag appears 3s into the 24s loop, stays visible until loop resets
            b.AddFlagEvent(t + 3.0, "Showcase Flag", "Jeb Kerman", "Part Showcase",
                "Squad/Flags/default", lat, lon + 0.00005, alt,
                rotX: KscRotX, rotY: KscRotY, rotZ: KscRotZ, rotW: KscRotW,
                body: "Kerbin");

            // EVA kerbal ghost (to show who planted it)
            var snap = new VesselSnapshotBuilder()
                .WithName("Part Showcase - Flag Plant")
                .WithPersistentId(FlagPlantShowcasePid)
                .AddPart("kerbalEVA", rotation: "0,-0.7071068,0,0.7071068")
                .AsLanded(lat, lon, alt)
                .Build();

            b.WithGhostVisualSnapshot(snap);
            return b;
        }

        #endregion

        #region Unit Tests

        private const string ShowcasePrimaryRotation = "0,-0.7071068,0,0.7071068";

        private static double ParseInvariantDouble(string value)
        {
            return double.Parse(value, CultureInfo.InvariantCulture);
        }

        private static float ParseInvariantFloat(string value)
        {
            return float.Parse(value, CultureInfo.InvariantCulture);
        }

        private static void AssertFloatClose(float expected, float actual, float tolerance = 0.0001f)
        {
            Assert.True(
                Math.Abs(expected - actual) <= tolerance,
                $"Expected {expected.ToString("R", CultureInfo.InvariantCulture)} but got {actual.ToString("R", CultureInfo.InvariantCulture)}");
        }

        private static void AssertEventTargetsPrimaryPart(ConfigNode eventNode, ConfigNode primaryPart)
        {
            Assert.Equal(primaryPart.GetValue("persistentId"), eventNode.GetValue("pid"));
            Assert.Equal(primaryPart.GetValue("name"), eventNode.GetValue("part"));
        }

        private static void AssertAlternatingPartEvents(
            ConfigNode recording,
            PartEventType onEvent,
            PartEventType offEvent,
            double firstEventOffsetSeconds,
            double onDurationSeconds,
            double offDurationSeconds,
            float expectedOnValue,
            int expectedModuleIndex = 0,
            int expectedEventCount = 8)
        {
            ConfigNode[] events = recording.GetNodes("PART_EVENT");
            Assert.Equal(expectedEventCount, events.Length);

            ConfigNode ghost = recording.GetNode("GHOST_VISUAL_SNAPSHOT");
            Assert.NotNull(ghost);
            ConfigNode[] parts = ghost.GetNodes("PART");
            Assert.True(parts.Length >= 1, $"Missing ghost PART for {recording.GetValue("vesselName")}");
            ConfigNode primaryPart = parts[0];

            double baseUt = ParseInvariantDouble(events[0].GetValue("ut")) - firstEventOffsetSeconds;
            double expectedOffset = firstEventOffsetSeconds;
            for (int i = 0; i < events.Length; i++)
            {
                bool onPhase = (i % 2) == 0;
                ConfigNode evt = events[i];

                Assert.Equal(
                    ((int)(onPhase ? onEvent : offEvent)).ToString(CultureInfo.InvariantCulture),
                    evt.GetValue("type"));
                AssertEventTargetsPrimaryPart(evt, primaryPart);
                Assert.Equal(expectedModuleIndex.ToString(CultureInfo.InvariantCulture), evt.GetValue("midx"));

                float expectedValue = onPhase ? expectedOnValue : 0f;
                float actualValue = ParseInvariantFloat(evt.GetValue("value"));
                AssertFloatClose(expectedValue, actualValue);

                double expectedUt = baseUt + expectedOffset;
                double actualUt = ParseInvariantDouble(evt.GetValue("ut"));
                Assert.Equal(expectedUt, actualUt, 6);

                expectedOffset += onPhase ? onDurationSeconds : offDurationSeconds;
            }
        }

        [Fact]
        public void PadWalk_HasEvaGhostSnapshot()
        {
            var node = PadWalk().Build();
            Assert.Equal("Pad Walk", node.GetValue("vesselName"));
            Assert.Equal("10", node.GetValue("pointCount"));
            Assert.Equal(10, node.GetNodes("POINT").Length);
            Assert.Null(node.GetNode("VESSEL_SNAPSHOT"));

            var ghost = node.GetNode("GHOST_VISUAL_SNAPSHOT");
            Assert.NotNull(ghost);
            Assert.Equal("EVA", ghost.GetValue("type"));
        }

        [Fact]
        public void KscHopper_BuildsValidRecording()
        {
            var node = KscHopper().Build();
            Assert.Equal("KSC Hopper", node.GetValue("vesselName"));
            Assert.Equal("15", node.GetValue("pointCount"));
            Assert.Equal(15, node.GetNodes("POINT").Length);
            Assert.Empty(node.GetNodes("ORBIT_SEGMENT"));
            Assert.Null(node.GetNode("VESSEL_SNAPSHOT"));

            var ghost = node.GetNode("GHOST_VISUAL_SNAPSHOT");
            Assert.NotNull(ghost);
            Assert.Equal(3, ghost.GetNodes("PART").Length);

            // Part events: engine ignited/shutdown + SRB decouple + parachute deploy
            var partEvents = node.GetNodes("PART_EVENT");
            Assert.Equal(4, partEvents.Length);
            Assert.Equal("5", partEvents[0].GetValue("type"));   // EngineIgnited
            Assert.Equal("6", partEvents[1].GetValue("type"));   // EngineShutdown
            Assert.Equal("0", partEvents[2].GetValue("type"));   // Decoupled
            Assert.Equal("2", partEvents[3].GetValue("type"));   // ParachuteDeployed
        }

        [Fact]
        public void FleaFlight_HasVesselSnapshotAndPartEvents()
        {
            var node = FleaFlight().Build();
            Assert.Equal("Flea Flight", node.GetValue("vesselName"));

            var snapshot = node.GetNode("VESSEL_SNAPSHOT");
            Assert.NotNull(snapshot);
            Assert.Equal("Ship", snapshot.GetValue("type"));
            Assert.Equal(3, snapshot.GetNodes("PART").Length);
            Assert.Equal("Bob Kerman", snapshot.GetNodes("PART")[0].GetValue("crew"));

            // Part events: engine ignited/shutdown + SRB decouple + parachute deploy
            var partEvents = node.GetNodes("PART_EVENT");
            Assert.Equal(4, partEvents.Length);
            Assert.Equal("5", partEvents[0].GetValue("type"));   // EngineIgnited
            Assert.Equal("6", partEvents[1].GetValue("type"));   // EngineShutdown
            Assert.Equal("0", partEvents[2].GetValue("type"));   // Decoupled
            Assert.Equal("2", partEvents[3].GetValue("type"));   // ParachuteDeployed

            // Resource transition: funds increase during flight
            var points = node.GetNodes("POINT");
            double firstFunds = double.Parse(points[0].GetValue("funds"), CultureInfo.InvariantCulture);
            double lastFunds = double.Parse(points[points.Length - 1].GetValue("funds"), CultureInfo.InvariantCulture);
            Assert.True(lastFunds > firstFunds, "Funds should increase during flight");
        }

        [Fact]
        public void SuborbitalArc_BuildsValidRecording()
        {
            var node = SuborbitalArc().Build();
            Assert.Equal("Suborbital Arc", node.GetValue("vesselName"));
            Assert.Equal("25", node.GetValue("pointCount"));
            Assert.Equal(25, node.GetNodes("POINT").Length);
            Assert.Null(node.GetNode("VESSEL_SNAPSHOT"));

            var ghost = node.GetNode("GHOST_VISUAL_SNAPSHOT");
            Assert.NotNull(ghost);
            Assert.Equal(3, ghost.GetNodes("PART").Length);

            // Verify UT ordering
            var points = node.GetNodes("POINT");
            double prevUT = 0;
            foreach (var pt in points)
            {
                double ut = double.Parse(pt.GetValue("ut"), CultureInfo.InvariantCulture);
                Assert.True(ut > prevUT, $"UT {ut} should be > {prevUT}");
                prevUT = ut;
            }

            // Part events: engine ignited/shutdown + SRB decouple
            var partEvents = node.GetNodes("PART_EVENT");
            Assert.Equal(3, partEvents.Length);
            Assert.Equal("5", partEvents[0].GetValue("type"));   // EngineIgnited
            Assert.Equal("6", partEvents[1].GetValue("type"));   // EngineShutdown
            Assert.Equal("0", partEvents[2].GetValue("type"));   // Decoupled
        }

        [Fact]
        public void KscPadDestroyed_HasNoSnapshot()
        {
            var node = KscPadDestroyed().Build();
            Assert.Equal("KSC Pad Destroyed", node.GetValue("vesselName"));
            Assert.Equal(7, node.GetNodes("POINT").Length);
            Assert.Null(node.GetNode("VESSEL_SNAPSHOT"));
            Assert.Null(node.GetNode("GHOST_VISUAL_SNAPSHOT"));
        }

        [Fact]
        public void Orbit1_HasOrbitSegmentAndSnapshot()
        {
            var node = Orbit1().Build();
            Assert.Equal("Orbit-1", node.GetValue("vesselName"));
            Assert.Equal(11, node.GetNodes("POINT").Length);
            Assert.Single(node.GetNodes("ORBIT_SEGMENT"));

            var seg = node.GetNodes("ORBIT_SEGMENT")[0];
            Assert.Equal("Kerbin", seg.GetValue("body"));
            Assert.Equal("1", seg.GetValue("ofrY"));  // retrograde rotation stored

            var snapshot = node.GetNode("VESSEL_SNAPSHOT");
            Assert.NotNull(snapshot);
            Assert.Equal("Orbit-1", snapshot.GetValue("name"));
            Assert.Equal("ORBITING", snapshot.GetValue("sit"));

            // Multi-part vessel: pod + fuel tank + engine
            var parts = snapshot.GetNodes("PART");
            Assert.Equal(3, parts.Length);
            Assert.Equal("Bill Kerman", parts[0].GetValue("crew"));
            Assert.Equal("fuelTank", parts[1].GetValue("name"));
            Assert.Equal("liquidEngine", parts[2].GetValue("name"));

            // Part events: engine ignited/shutdown + stage separation
            var partEvents = node.GetNodes("PART_EVENT");
            Assert.Equal(3, partEvents.Length);
            Assert.Equal("5", partEvents[0].GetValue("type"));   // EngineIgnited
            Assert.Equal("6", partEvents[1].GetValue("type"));   // EngineShutdown
            Assert.Equal("0", partEvents[2].GetValue("type"));   // Decoupled
        }

        [Fact]
        public void Orbit1_OrbitSegment_HasAngularVelocityKeys()
        {
            // Build and check the orbit segment has no av keys by default
            var node = Orbit1().Build();
            var seg = node.GetNodes("ORBIT_SEGMENT")[0];
            Assert.Null(seg.GetValue("avX"));  // default Orbit1 has no angular velocity

            // Build a fresh one with angular velocity
            var b2 = new RecordingBuilder("Orbit-1-Spinning");
            double t = 17180;
            double segStart = t + 500, segEnd = t + 3000;
            b2.AddOrbitSegment(segStart, segEnd,
                inc: 28.5, ecc: 0.001, sma: 700000,
                lan: 90, argPe: 45, mna: 0, epoch: segStart,
                body: "Kerbin",
                avX: 0.1f);
            var node2 = b2.Build();
            var seg2 = node2.GetNodes("ORBIT_SEGMENT")[0];
            Assert.NotNull(seg2.GetValue("avX"));
            // Parse and verify
            float avX;
            Assert.True(float.TryParse(seg2.GetValue("avX"),
                NumberStyles.Float,
                CultureInfo.InvariantCulture, out avX));
            Assert.Equal(0.1f, avX, 0.00001f);  // tolerance
        }

        [Fact]
        public void TruncatedPlaneCruise_BuildsValidRecording()
        {
            var node = TruncatedPlaneCruise().Build();
            Assert.Equal("Truncated Plane Cruise", node.GetValue("vesselName"));
            Assert.Equal(6, node.GetNodes("POINT").Length);
            Assert.Empty(node.GetNodes("ORBIT_SEGMENT"));
            Assert.NotNull(node.GetNode("GHOST_VISUAL_SNAPSHOT"));
        }

        [Fact]
        public void TruncatedSuborbitalRecording_HasKerbinTailSegment()
        {
            var node = TruncatedSuborbitalRecording().Build();
            Assert.Equal("Truncated Suborbital Recording", node.GetValue("vesselName"));
            Assert.Equal(5, node.GetNodes("POINT").Length);
            Assert.Single(node.GetNodes("ORBIT_SEGMENT"));
            Assert.Equal("Kerbin", node.GetNodes("ORBIT_SEGMENT")[0].GetValue("body"));
        }

        [Fact]
        public void TruncatedHyperbolicRecording_HasHyperbolicOrbitSegment()
        {
            var node = TruncatedHyperbolicRecording().Build();
            Assert.Equal("Truncated Hyperbolic Recording", node.GetValue("vesselName"));
            Assert.Single(node.GetNodes("ORBIT_SEGMENT"));
            Assert.Equal("Kerbin", node.GetNodes("ORBIT_SEGMENT")[0].GetValue("body"));
            Assert.Equal("-4500000", node.GetNodes("ORBIT_SEGMENT")[0].GetValue("sma"));
        }

        [Fact]
        public void TruncatedMunFlybyRecording_HasKerbinOrbitSnapshot()
        {
            var node = TruncatedMunFlybyRecording().Build();
            Assert.Equal("Truncated Mun Flyby Recording", node.GetValue("vesselName"));
            Assert.Single(node.GetNodes("ORBIT_SEGMENT"));
            Assert.Equal("Kerbin", node.GetNodes("ORBIT_SEGMENT")[0].GetValue("body"));
            Assert.NotNull(node.GetNode("VESSEL_SNAPSHOT"));
        }

        [Fact]
        public void TruncatedMunImpactRecording_HasMunOrbitSegment()
        {
            var node = TruncatedMunImpactRecording().Build();
            Assert.Equal("Truncated Mun Impact Recording", node.GetValue("vesselName"));
            Assert.Single(node.GetNodes("ORBIT_SEGMENT"));
            Assert.Equal("Mun", node.GetNodes("ORBIT_SEGMENT")[0].GetValue("body"));
            Assert.NotNull(node.GetNode("GHOST_VISUAL_SNAPSHOT"));
        }

        [Fact]
        public void CloseSpawnConflict_HasLandedSnapshotNearKsc()
        {
            var node = CloseSpawnConflict().Build();
            Assert.Equal("Close Spawn Conflict", node.GetValue("vesselName"));
            var snapshot = node.GetNode("VESSEL_SNAPSHOT");
            Assert.NotNull(snapshot);
            Assert.Equal("LANDED", snapshot.GetValue("sit"));
            Assert.Equal(3, snapshot.GetNodes("PART").Length);
            Assert.Equal("Jebediah Kerman", snapshot.GetNodes("PART")[0].GetValue("crew"));
        }

        [Fact]
        public void IslandProbe_HasFleaRocketWithCrew()
        {
            var node = IslandProbe().Build();
            Assert.Equal("Island Probe", node.GetValue("vesselName"));
            Assert.Equal(20, node.GetNodes("POINT").Length);

            var snapshot = node.GetNode("VESSEL_SNAPSHOT");
            Assert.NotNull(snapshot);
            Assert.Equal("LANDED", snapshot.GetValue("sit"));
            Assert.Equal("Ship", snapshot.GetValue("type"));

            var parts = snapshot.GetNodes("PART");
            Assert.Equal(3, parts.Length);
            Assert.Equal("Valentina Kerman", parts[0].GetValue("crew"));
        }

        [Fact]
        public void ScenarioWriter_SerializesCorrectly()
        {
            var writer = new ScenarioWriter();
            writer.AddRecordingAsTree(KscHopper());

            var scenarioNode = writer.BuildScenarioNode();
            Assert.Equal("ParsekScenario", scenarioNode.GetValue("name"));
            Assert.Single(scenarioNode.GetNodes("RECORDING_TREE"));

            string text = writer.SerializeConfigNode(scenarioNode, "SCENARIO", 1);
            Assert.Contains("name = ParsekScenario", text);
            Assert.Contains("RECORDING_TREE", text);
        }

        [Fact]
        public void ScenarioWriter_AddRecordingsAsTree_PreservesChainParentLinks()
        {
            var writer = new ScenarioWriter().WithV3Format();
            var segments = EvaBoardChain();
            for (int i = 0; i < segments.Length; i++)
                segments[i].WithLoopPlayback().WithRecordingGroup("Synthetic");

            writer.AddRecordingsAsTree(segments);

            var scenarioNode = writer.BuildScenarioNode();
            var treeNodes = scenarioNode.GetNodes("RECORDING_TREE");
            Assert.Single(treeNodes);

            var tree = RecordingTree.Load(treeNodes[0]);
            Assert.Equal(segments.Length, tree.Recordings.Count);
            Assert.Equal("chain-seg0", tree.RootRecordingId);
            Assert.Equal("Flea Chain", tree.TreeName);
            Assert.Equal(tree.Id, tree.Recordings["chain-seg0"].TreeId);
            Assert.Equal(tree.Id, tree.Recordings["chain-seg1"].TreeId);
            Assert.Equal(tree.Id, tree.Recordings["chain-seg2"].TreeId);
            Assert.Equal("chain-seg0", tree.Recordings["chain-seg1"].ParentRecordingId);
            Assert.Equal("chain-seg1", tree.Recordings["chain-seg2"].ParentRecordingId);
        }

        [Fact]
        public void ScenarioWriter_InjectIntoSave_InsertsBeforeFlightstate()
        {
            string fakeSave =
                "GAME\n{\n" +
                "\tSCENARIO\n\t{\n\t\tname = SomeOther\n\t\tscene = 5\n\t}\n" +
                "\tFLIGHTSTATE\n\t{\n\t\tversion = 1.12.5\n\t}\n" +
                "}\n";

            var writer = new ScenarioWriter();
            writer.AddRecordingAsTree(KscHopper());

            string result = writer.InjectIntoSave(fakeSave);

            // ParsekScenario appears before FLIGHTSTATE
            int scenarioIdx = result.IndexOf("name = ParsekScenario");
            int flightstateIdx = result.IndexOf("FLIGHTSTATE");
            Assert.True(scenarioIdx > 0);
            Assert.True(scenarioIdx < flightstateIdx);

            // Original scenario still present
            Assert.Contains("name = SomeOther", result);
        }

        [Fact]
        public void ScenarioWriter_InjectIntoSave_ReplacesExistingParsekScenario()
        {
            string saveWithParsek =
                "GAME\n{\n" +
                "\tSCENARIO\n\t{\n\t\tname = ParsekScenario\n\t\tscene = 5\n\t\tRECORDING\n\t\t{\n\t\t\tvesselName = Old\n\t\t}\n\t}\n" +
                "\tFLIGHTSTATE\n\t{\n\t\tversion = 1.12.5\n\t}\n" +
                "}\n";

            var writer = new ScenarioWriter();
            writer.AddRecordingAsTree(KscHopper());

            string result = writer.InjectIntoSave(saveWithParsek);

            // Only one ParsekScenario
            int firstIdx = result.IndexOf("name = ParsekScenario");
            int secondIdx = result.IndexOf("name = ParsekScenario", firstIdx + 1);
            Assert.True(firstIdx > 0);
            Assert.Equal(-1, secondIdx);

            // Old recording gone, new one present
            Assert.DoesNotContain("vesselName = Old", result);
            Assert.Contains("vesselName = KSC Hopper", result);
        }

        [Fact]
        public void ScenarioWriter_InjectIntoSave_Idempotent()
        {
            string fakeSave =
                "GAME\n{\n" +
                "\tFLIGHTSTATE\n\t{\n\t\tversion = 1.12.5\n\t}\n" +
                "}\n";

            var writer = new ScenarioWriter();
            writer.AddRecordingAsTree(KscHopper());

            string first = writer.InjectIntoSave(fakeSave);
            string second = writer.InjectIntoSave(first);

            // Count occurrences of ParsekScenario
            int count = 0;
            int idx = 0;
            while ((idx = second.IndexOf("name = ParsekScenario", idx)) >= 0)
            {
                count++;
                idx += 1;
            }
            Assert.Equal(1, count);
        }

        [Fact]
        public void ScenarioWriter_InjectIntoSave_HandlesVariousWhitespace()
        {
            // Tabs + extra spacing, CRLF line endings, extra values in scenario
            string saveWithParsek =
                "GAME\r\n{\r\n" +
                "\tSCENARIO\r\n\t{\r\n" +
                "\t\tname = ParsekScenario\r\n" +
                "\t\tscene = 5, 6, 7, 8\r\n" +
                "\t\tRECORDING\r\n\t\t{\r\n" +
                "\t\t\tvesselName = Old\r\n" +
                "\t\t\tpointCount = 5\r\n" +
                "\t\t\tPOINT\r\n\t\t\t{\r\n\t\t\t\tut = 100\r\n\t\t\t}\r\n" +
                "\t\t}\r\n" +
                "\t}\r\n" +
                "\tFLIGHTSTATE\r\n\t{\r\n\t\tversion = 1.12.5\r\n\t}\r\n" +
                "}\r\n";

            var writer = new ScenarioWriter();
            writer.AddRecordingAsTree(KscHopper());

            string result = writer.InjectIntoSave(saveWithParsek);

            // Only one ParsekScenario
            int firstIdx = result.IndexOf("name = ParsekScenario");
            int secondIdx = result.IndexOf("name = ParsekScenario", firstIdx + 1);
            Assert.True(firstIdx > 0);
            Assert.Equal(-1, secondIdx);

            // Old data gone
            Assert.DoesNotContain("vesselName = Old", result);
            Assert.Contains("vesselName = KSC Hopper", result);
        }

        [Fact]
        public void VesselSnapshotBuilder_DeterministicPid()
        {
            var v1 = VesselSnapshotBuilder.ProbeShip("Test", pid: 42).Build();
            var v2 = VesselSnapshotBuilder.ProbeShip("Test", pid: 42).Build();
            Assert.Equal(v1.GetValue("pid"), v2.GetValue("pid"));
        }

        #endregion

        #region v3 External File Tests

        [Fact]
        public void SerializeTrajectory_RoundTrip_PreservesData()
        {
            RecordingStore.SuppressLogging = true;
            MilestoneStore.ResetForTesting();
            GameStateStore.SuppressLogging = true;
            try
            {
                // Build a recording with points, orbit segments, and part events
                var rec = new Recording();
                rec.RecordingId = "roundtrip_test";
                rec.Points.Add(new TrajectoryPoint
                {
                    ut = 100.5, latitude = -0.0972, longitude = -74.5575, altitude = 77.3,
                    rotation = new Quaternion(0.1f, 0.2f, 0.3f, 0.9f),
                    velocity = new Vector3(10.5f, 20.3f, -5.7f),
                    bodyName = "Kerbin", funds = 42000.5, science = 12.3f, reputation = 5.7f
                });
                rec.Points.Add(new TrajectoryPoint
                {
                    ut = 103.5, latitude = -0.0970, longitude = -74.5570, altitude = 150.0,
                    rotation = new Quaternion(0.15f, 0.25f, 0.35f, 0.85f),
                    velocity = new Vector3(15.0f, 25.0f, -3.0f),
                    bodyName = "Kerbin", funds = 42000.5, science = 12.3f, reputation = 5.7f
                });
                rec.OrbitSegments.Add(new OrbitSegment
                {
                    startUT = 200, endUT = 500, inclination = 28.5, eccentricity = 0.001,
                    semiMajorAxis = 700000, longitudeOfAscendingNode = 90,
                    argumentOfPeriapsis = 45, meanAnomalyAtEpoch = 0, epoch = 200,
                    bodyName = "Kerbin"
                });
                rec.PartEvents.Add(new PartEvent
                {
                    ut = 150, partPersistentId = 12345, eventType = PartEventType.Decoupled,
                    partName = "solidBooster"
                });

                // Serialize to ConfigNode
                var node = new ConfigNode("PARSEK_RECORDING");
                node.AddValue("version", "5");
                node.AddValue("recordingId", rec.RecordingId);
                RecordingStore.SerializeTrajectoryInto(node, rec);

                // Deserialize into a fresh recording
                var rec2 = new Recording();
                RecordingStore.DeserializeTrajectoryFrom(node, rec2);

                // Verify points
                Assert.Equal(rec.Points.Count, rec2.Points.Count);
                Assert.Equal(rec.Points[0].ut, rec2.Points[0].ut);
                Assert.Equal(rec.Points[0].latitude, rec2.Points[0].latitude);
                Assert.Equal(rec.Points[0].longitude, rec2.Points[0].longitude);
                Assert.Equal(rec.Points[0].altitude, rec2.Points[0].altitude);
                Assert.Equal(rec.Points[0].bodyName, rec2.Points[0].bodyName);
                Assert.Equal(rec.Points[0].funds, rec2.Points[0].funds);
                Assert.Equal((double)rec.Points[0].science, (double)rec2.Points[0].science, 3);
                Assert.Equal((double)rec.Points[0].reputation, (double)rec2.Points[0].reputation, 3);
                Assert.Equal(rec.Points[1].ut, rec2.Points[1].ut);

                // Verify orbit segments
                Assert.Equal(rec.OrbitSegments.Count, rec2.OrbitSegments.Count);
                Assert.Equal(rec.OrbitSegments[0].startUT, rec2.OrbitSegments[0].startUT);
                Assert.Equal(rec.OrbitSegments[0].semiMajorAxis, rec2.OrbitSegments[0].semiMajorAxis);
                Assert.Equal(rec.OrbitSegments[0].bodyName, rec2.OrbitSegments[0].bodyName);

                // Verify part events
                Assert.Equal(rec.PartEvents.Count, rec2.PartEvents.Count);
                Assert.Equal(rec.PartEvents[0].ut, rec2.PartEvents[0].ut);
                Assert.Equal(rec.PartEvents[0].partPersistentId, rec2.PartEvents[0].partPersistentId);
                Assert.Equal(rec.PartEvents[0].eventType, rec2.PartEvents[0].eventType);
                Assert.Equal(rec.PartEvents[0].partName, rec2.PartEvents[0].partName);
            }
            finally
            {
                RecordingStore.SuppressLogging = false;
            }
        }

        [Fact]
        public void SerializeTrajectory_SaveToFile_WritesExpectedContent()
        {
            RecordingStore.SuppressLogging = true;
            MilestoneStore.ResetForTesting();
            GameStateStore.SuppressLogging = true;
            string tempDir = Path.Combine(Path.GetTempPath(), "parsek_test_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                // Build recording with data
                var rec = new Recording { RecordingId = "filetest" };
                rec.Points.Add(new TrajectoryPoint
                {
                    ut = 500, latitude = 1.5, longitude = -74.0, altitude = 100,
                    rotation = new Quaternion(0, 0, 0, 1), velocity = new Vector3(5, 10, 0),
                    bodyName = "Kerbin", funds = 1000
                });
                rec.Points.Add(new TrajectoryPoint
                {
                    ut = 503, latitude = 1.6, longitude = -73.9, altitude = 200,
                    rotation = new Quaternion(0, 0, 0, 1), velocity = new Vector3(5, 15, 0),
                    bodyName = "Kerbin"
                });

                // Serialize to ConfigNode and save to file
                var precNode = new ConfigNode("PARSEK_RECORDING");
                precNode.AddValue("version", "5");
                precNode.AddValue("recordingId", "filetest");
                RecordingStore.SerializeTrajectoryInto(precNode, rec);

                string precPath = Path.Combine(tempDir, "filetest.prec");
                precNode.Save(precPath);

                // Verify file exists and contains expected content
                Assert.True(File.Exists(precPath), "Expected .prec file to be written");
                string content = File.ReadAllText(precPath);
                Assert.Contains("recordingId = filetest", content);
                Assert.Contains("version = 5", content);
                Assert.Contains("POINT", content);
                Assert.Contains("ut = 500", content);
                Assert.Contains("ut = 503", content);
                Assert.Contains("body = Kerbin", content);

                // Also save a vessel snapshot and verify
                var vesselSnapshot = VesselSnapshotBuilder.ProbeShip("Test Probe", pid: 999).Build();
                string vesselPath = Path.Combine(tempDir, "filetest_vessel.craft");
                vesselSnapshot.Save(vesselPath);

                Assert.True(File.Exists(vesselPath), "Expected _vessel.craft file to be written");
                string vesselContent = File.ReadAllText(vesselPath);
                Assert.Contains("name = Test Probe", vesselContent);
            }
            finally
            {
                RecordingStore.SuppressLogging = false;
                Directory.Delete(tempDir, true);
            }
        }

        [Fact]
        public void RecordingBuilder_BuildV3Metadata_HasNoInlineData()
        {
            var builder = new RecordingBuilder("Test Vessel")
                .WithRecordingId("abc123")
                .AddPoint(100, 0, 0, 0)
                .AddPoint(103, 0.1, 0.1, 100)
                .AddOrbitSegment(200, 500)
                .AddPartEvent(150, 12345, 0, "solidBooster")
                .WithVesselSnapshot(VesselSnapshotBuilder.ProbeShip("Test"));

            var v3Node = builder.BuildV3Metadata();

            // Has metadata
            Assert.Equal("Test Vessel", v3Node.GetValue("vesselName"));
            Assert.Equal("abc123", v3Node.GetValue("recordingId"));
            Assert.Equal(RecordingStore.CurrentRecordingFormatVersion.ToString(CultureInfo.InvariantCulture),
                v3Node.GetValue("recordingFormatVersion"));
            Assert.Equal("2", v3Node.GetValue("pointCount"));

            // No inline bulk data
            Assert.Empty(v3Node.GetNodes("POINT"));
            Assert.Empty(v3Node.GetNodes("ORBIT_SEGMENT"));
            Assert.Empty(v3Node.GetNodes("PART_EVENT"));
            Assert.Null(v3Node.GetNode("VESSEL_SNAPSHOT"));
            Assert.Null(v3Node.GetNode("GHOST_VISUAL_SNAPSHOT"));
        }

        [Fact]
        public void RecordingBuilder_WithLoopPlayback_WritesLoopMetadata()
        {
            var node = new RecordingBuilder("Loop Test")
                .WithRecordingId("loop123")
                .WithLoopPlayback(loop: true, intervalSeconds: 0.0)
                .AddPoint(100, 0, 0, 0)
                .AddPoint(108, 0, 0, 0)
                .BuildV3Metadata();

            Assert.Equal("True", node.GetValue("loopPlayback"));
            // #412: builder auto-derives the interval from trajectory duration (8 s here)
            // when the caller leaves intervalSeconds=0 with loop=true, so fixtures never
            // persist a degenerate value that triggers the ResolveLoopInterval clamp warning.
            // Duration must be >= MinCycleDuration (5 s, #443) for auto-derivation to pick
            // up the duration; shorter trajectories fall back to DefaultLoopIntervalSeconds.
            Assert.Equal("8", node.GetValue("loopIntervalSeconds"));
        }

        [Fact]
        public void RecordingBuilder_BuildTrajectoryNode_HasBulkData()
        {
            var builder = new RecordingBuilder("Test Vessel")
                .WithRecordingId("abc123")
                .AddPoint(100, 0, 0, 0)
                .AddPoint(103, 0.1, 0.1, 100)
                .AddOrbitSegment(200, 500)
                .AddPartEvent(150, 12345, 0, "solidBooster");

            var trajNode = builder.BuildTrajectoryNode();

            Assert.Equal("PARSEK_RECORDING", trajNode.name);
            Assert.Equal(RecordingStore.CurrentRecordingFormatVersion.ToString(CultureInfo.InvariantCulture),
                trajNode.GetValue("version"));
            Assert.Equal("abc123", trajNode.GetValue("recordingId"));
            Assert.Equal(2, trajNode.GetNodes("POINT").Length);
            Assert.Single(trajNode.GetNodes("ORBIT_SEGMENT"));
            Assert.Single(trajNode.GetNodes("PART_EVENT"));
        }

        [Fact]
        public void RecordingBuilder_WithCustomFormatVersion_PreservesVersion()
        {
            var builder = new RecordingBuilder("Custom Version Test")
                .WithRecordingId("customvertest")
                .WithFormatVersion(42)
                .AddPoint(100, 0, 0, 0)
                .AddPoint(103, 0.1, 0.1, 100);

            var trajNode = builder.BuildTrajectoryNode();
            Assert.Equal("42", trajNode.GetValue("version"));

            var metaNode = builder.BuildV3Metadata();
            Assert.Equal("42", metaNode.GetValue("recordingFormatVersion"));
        }

        [Fact]
        public void ScenarioWriter_V3Format_ProducesTreeNodes()
        {
            var writer = new ScenarioWriter().WithV3Format();
            writer.AddRecordingAsTree(KscHopper());

            var scenarioNode = writer.BuildScenarioNode();
            Assert.Empty(scenarioNode.GetNodes("RECORDING"));
            Assert.Single(scenarioNode.GetNodes("RECORDING_TREE"));

            var treeNode = scenarioNode.GetNodes("RECORDING_TREE")[0];
            Assert.NotNull(treeNode.GetValue("treeName"));
            Assert.Equal("KSC Hopper", treeNode.GetValue("treeName"));
        }

        [Fact]
        public void LightShowcaseRecordings_BuildExpectedShape()
        {
            var recordings = LightShowcaseRecordings(baseUT: 17000);
            Assert.Equal(10, recordings.Length);

            var first = recordings[0].Build();
            Assert.Equal("Part Showcase - Lights v1", first.GetValue("vesselName"));
            Assert.Equal("9", first.GetValue("pointCount"));
            Assert.Equal("True", first.GetValue("loopPlayback"));
            // #412: builder derives the interval from the 24 s static showcase trajectory so
            // playback loops seamlessly at the recording's own length instead of storing 0
            // (which would spam ResolveLoopInterval's clamp warning at playback time).
            Assert.Equal("24", first.GetValue("loopIntervalSeconds"));
            Assert.Equal(7, first.GetNodes("PART_EVENT").Length);

            var ghost = first.GetNode("GHOST_VISUAL_SNAPSHOT");
            Assert.NotNull(ghost);
            var parts = ghost.GetNodes("PART");
            Assert.True(parts.Length >= 1);
            Assert.Equal("domeLight1", parts[0].GetValue("name"));
            Assert.Equal("0,-0.7071068,0,0.7071068", parts[0].GetValue("rotation"));
        }

        [Fact]
        public void DeployableShowcaseRecordings_BuildExpectedShape()
        {
            var recordings = DeployableShowcaseRecordings(baseUT: 17000);
            Assert.Equal(18, recordings.Length);

            var first = recordings[0].Build();
            Assert.Equal("Part Showcase - Solar Tracking", first.GetValue("vesselName"));
            Assert.Equal("9", first.GetValue("pointCount"));
            Assert.Equal("True", first.GetValue("loopPlayback"));
            Assert.Equal(8, first.GetNodes("PART_EVENT").Length);

            // Verify event types alternate DeployableExtended / DeployableRetracted
            var events = first.GetNodes("PART_EVENT");
            Assert.Equal(((int)PartEventType.DeployableExtended).ToString(), events[0].GetValue("type"));
            Assert.Equal(((int)PartEventType.DeployableRetracted).ToString(), events[1].GetValue("type"));

            // Event PID must match the ghost part's persistentId for playback to find it
            var ghost = first.GetNode("GHOST_VISUAL_SNAPSHOT");
            Assert.NotNull(ghost);
            Assert.Equal("solarPanels4", ghost.GetNodes("PART")[0].GetValue("name"));
            string ghostPartPid = ghost.GetNodes("PART")[0].GetValue("persistentId");
            Assert.Equal(ghostPartPid, events[0].GetValue("pid"));

            var names = new[]
            {
                "solarPanels4", "largeSolarPanel", "LgRadialSolarPanel", "solarPanelOX10C", "solarPanelOX10L",
                "solarPanels1", "solarPanels2", "solarPanels3", "solarPanels5", "solarPanelSP10C",
                "solarPanelSP10L", "longAntenna", "commDish", "HighGainAntenna", "HighGainAntenna5",
                "HighGainAntenna5.v2", "mediumDishAntenna", "foldingRadSmall"
            };
            for (int i = 0; i < recordings.Length; i++)
            {
                var g = recordings[i].Build().GetNode("GHOST_VISUAL_SNAPSHOT");
                Assert.Equal(names[i], g.GetNodes("PART")[0].GetValue("name"));
            }
        }

        [Fact]
        public void GearShowcaseRecordings_BuildExpectedShape()
        {
            var recordings = GearShowcaseRecordings(baseUT: 17000);
            Assert.Equal(7, recordings.Length);

            var first = recordings[0].Build();
            Assert.Equal("Part Showcase - Gear Bay", first.GetValue("vesselName"));
            Assert.Equal("True", first.GetValue("loopPlayback"));
            Assert.Equal(8, first.GetNodes("PART_EVENT").Length);

            var events = first.GetNodes("PART_EVENT");
            Assert.Equal(((int)PartEventType.GearDeployed).ToString(), events[0].GetValue("type"));
            Assert.Equal(((int)PartEventType.GearRetracted).ToString(), events[1].GetValue("type"));

            var ghost = first.GetNode("GHOST_VISUAL_SNAPSHOT");
            Assert.NotNull(ghost);
            Assert.Equal("SmallGearBay", ghost.GetNodes("PART")[0].GetValue("name"));
            Assert.Equal(ghost.GetNodes("PART")[0].GetValue("persistentId"), events[0].GetValue("pid"));

            var names = new[] { "SmallGearBay", "GearSmall", "GearMedium", "GearLarge",
                "landingLeg1", "landingLeg1-2", "miniLandingLeg" };
            for (int i = 0; i < recordings.Length; i++)
            {
                var g = recordings[i].Build().GetNode("GHOST_VISUAL_SNAPSHOT");
                Assert.Equal(names[i], g.GetNodes("PART")[0].GetValue("name"));
            }
        }

        [Fact]
        public void CargoBayShowcaseRecordings_BuildExpectedShape()
        {
            var recordings = CargoBayShowcaseRecordings(baseUT: 17000);
            Assert.Equal(11, recordings.Length);

            var first = recordings[0].Build();
            Assert.Equal("Part Showcase - Service Bay", first.GetValue("vesselName"));
            Assert.Equal("True", first.GetValue("loopPlayback"));
            Assert.Equal(8, first.GetNodes("PART_EVENT").Length);

            var events = first.GetNodes("PART_EVENT");
            Assert.Equal(((int)PartEventType.CargoBayOpened).ToString(), events[0].GetValue("type"));
            Assert.Equal(((int)PartEventType.CargoBayClosed).ToString(), events[1].GetValue("type"));

            // Uses dot-form part name (KSP converts underscores to dots at runtime)
            var ghost = first.GetNode("GHOST_VISUAL_SNAPSHOT");
            Assert.NotNull(ghost);
            Assert.Equal("ServiceBay.125.v2", ghost.GetNodes("PART")[0].GetValue("name"));
            Assert.Equal(ghost.GetNodes("PART")[0].GetValue("persistentId"), events[0].GetValue("pid"));

            var names = new[]
            {
                "ServiceBay.125.v2", "ServiceBay.250.v2", "ServiceModule18", "ServiceModule25", "Size1to0ServiceModule",
                "mk2CargoBayS", "mk2CargoBayL", "mk3CargoBayS", "mk3CargoBayM", "mk3CargoBayL", "mk3CargoRamp"
            };
            for (int i = 0; i < recordings.Length; i++)
            {
                var g = recordings[i].Build().GetNode("GHOST_VISUAL_SNAPSHOT");
                Assert.Equal(names[i], g.GetNodes("PART")[0].GetValue("name"));
            }
        }

        [Fact]
        public void EngineShowcaseRecordings_BuildExpectedShape()
        {
            var recordings = EngineShowcaseRecordings(baseUT: 17000);
            Assert.Equal(47, recordings.Length);

            // First entry: liquid with shroud (LV-T30) → 8 events, first is ShroudJettisoned
            var first = recordings[0].Build();
            Assert.Equal("Part Showcase - LV-T30", first.GetValue("vesselName"));
            Assert.Equal("True", first.GetValue("loopPlayback"));
            Assert.Equal(8, first.GetNodes("PART_EVENT").Length);

            var events = first.GetNodes("PART_EVENT");
            Assert.Equal(((int)PartEventType.ShroudJettisoned).ToString(), events[0].GetValue("type"));
            Assert.Equal(((int)PartEventType.EngineIgnited).ToString(), events[1].GetValue("type"));
            Assert.Equal("0.3", events[1].GetValue("value"));
            Assert.Equal(((int)PartEventType.EngineThrottle).ToString(), events[2].GetValue("type"));

            var ghost = first.GetNode("GHOST_VISUAL_SNAPSHOT");
            Assert.NotNull(ghost);
            Assert.Equal("liquidEngine", ghost.GetNodes("PART")[0].GetValue("name"));
            Assert.Equal(ghost.GetNodes("PART")[0].GetValue("persistentId"), events[0].GetValue("pid"));

            // SRB with shroud (index 16 = Flea v2) → 3 events
            var srbShroud = recordings[16].Build();
            Assert.Equal("Part Showcase - Flea v2", srbShroud.GetValue("vesselName"));
            Assert.Equal(3, srbShroud.GetNodes("PART_EVENT").Length);
            var srbEvents = srbShroud.GetNodes("PART_EVENT");
            Assert.Equal(((int)PartEventType.ShroudJettisoned).ToString(), srbEvents[0].GetValue("type"));
            Assert.Equal(((int)PartEventType.EngineIgnited).ToString(), srbEvents[1].GetValue("type"));
            Assert.Equal("1", srbEvents[1].GetValue("value"));
            Assert.Equal(((int)PartEventType.EngineShutdown).ToString(), srbEvents[2].GetValue("type"));

            // Liquid flame-only (index 20 = Ant) → 8 events, first is EngineIgnited
            var flameOnly = recordings[20].Build();
            Assert.Equal("Part Showcase - Ant", flameOnly.GetValue("vesselName"));
            Assert.Equal(8, flameOnly.GetNodes("PART_EVENT").Length);
            var flameEvents = flameOnly.GetNodes("PART_EVENT");
            Assert.Equal(((int)PartEventType.EngineIgnited).ToString(), flameEvents[0].GetValue("type"));
            Assert.Equal("0.3", flameEvents[0].GetValue("value"));

            // Late large-liquid rows also use shroud+jettison (indices 26-28).
            var mainsail = recordings[26].Build();
            Assert.Equal("Part Showcase - Mainsail", mainsail.GetValue("vesselName"));
            Assert.Equal(8, mainsail.GetNodes("PART_EVENT").Length);
            Assert.Equal(((int)PartEventType.ShroudJettisoned).ToString(), mainsail.GetNodes("PART_EVENT")[0].GetValue("type"));

            var skipper = recordings[27].Build();
            Assert.Equal("Part Showcase - Skipper", skipper.GetValue("vesselName"));
            Assert.Equal(8, skipper.GetNodes("PART_EVENT").Length);
            Assert.Equal(((int)PartEventType.ShroudJettisoned).ToString(), skipper.GetNodes("PART_EVENT")[0].GetValue("type"));

            var ssme = recordings[28].Build();
            Assert.Equal("Part Showcase - SSME", ssme.GetValue("vesselName"));
            Assert.Equal(8, ssme.GetNodes("PART_EVENT").Length);
            Assert.Equal(((int)PartEventType.ShroudJettisoned).ToString(), ssme.GetNodes("PART_EVENT")[0].GetValue("type"));

            // SRB flame-only (index 33 = Thumper) → 2 events
            var srbFlame = recordings[33].Build();
            Assert.Equal("Part Showcase - Thumper", srbFlame.GetValue("vesselName"));
            Assert.Equal(2, srbFlame.GetNodes("PART_EVENT").Length);
            var srbFlameEvents = srbFlame.GetNodes("PART_EVENT");
            Assert.Equal(((int)PartEventType.EngineIgnited).ToString(), srbFlameEvents[0].GetValue("type"));
            Assert.Equal("1", srbFlameEvents[0].GetValue("value"));
            Assert.Equal(((int)PartEventType.EngineShutdown).ToString(), srbFlameEvents[1].GetValue("type"));

            // Jet (index 39 = Juno) → 8 events
            var jet = recordings[39].Build();
            Assert.Equal("Part Showcase - Juno", jet.GetValue("vesselName"));
            Assert.Equal(8, jet.GetNodes("PART_EVENT").Length);

            // Ion (index 44) → 8 events
            var ion = recordings[44].Build();
            Assert.Equal("Part Showcase - Ion", ion.GetValue("vesselName"));
            Assert.Equal(8, ion.GetNodes("PART_EVENT").Length);
        }

        [Fact]
        public void EngineShowcaseRecordings_AllEntriesFollowExpectedEventProfiles()
        {
            var recordings = EngineShowcaseRecordings(baseUT: 17000);
            Assert.Equal(47, recordings.Length);

            for (int i = 0; i < recordings.Length; i++)
            {
                ConfigNode built = recordings[i].Build();
                ConfigNode[] events = built.GetNodes("PART_EVENT");
                Assert.True(events.Length == 2 || events.Length == 3 || events.Length == 8,
                    $"Unexpected event count {events.Length} for '{built.GetValue("vesselName")}'");

                ConfigNode ghost = built.GetNode("GHOST_VISUAL_SNAPSHOT");
                Assert.NotNull(ghost);
                ConfigNode[] parts = ghost.GetNodes("PART");
                Assert.Single(parts);
                string expectedPid = parts[0].GetValue("persistentId");

                double previousUt = double.MinValue;
                for (int e = 0; e < events.Length; e++)
                {
                    Assert.Equal(expectedPid, events[e].GetValue("pid"));
                    double ut = double.Parse(events[e].GetValue("ut"), CultureInfo.InvariantCulture);
                    Assert.True(ut > previousUt,
                        $"Non-monotonic UT in '{built.GetValue("vesselName")}' at event {e}");
                    previousUt = ut;
                }

                if (events.Length == 2)
                {
                    Assert.Equal(((int)PartEventType.EngineIgnited).ToString(), events[0].GetValue("type"));
                    Assert.Equal("1", events[0].GetValue("value"));
                    Assert.Equal(((int)PartEventType.EngineShutdown).ToString(), events[1].GetValue("type"));
                    continue;
                }

                if (events.Length == 3)
                {
                    Assert.Equal(((int)PartEventType.ShroudJettisoned).ToString(), events[0].GetValue("type"));
                    Assert.Equal(((int)PartEventType.EngineIgnited).ToString(), events[1].GetValue("type"));
                    Assert.Equal("1", events[1].GetValue("value"));
                    Assert.Equal(((int)PartEventType.EngineShutdown).ToString(), events[2].GetValue("type"));
                    continue;
                }

                // RAPIER mode-switch showcase has a custom event profile (3 ignite/3 shutdown).
                string vesselName = built.GetValue("vesselName");
                if (string.Equals(vesselName, "Part Showcase - RAPIER", System.StringComparison.Ordinal))
                    continue;

                int shroudCount = 0;
                int igniteCount = 0;
                int throttleCount = 0;
                int shutdownCount = 0;
                int firstIgniteIndex = -1;
                for (int e = 0; e < events.Length; e++)
                {
                    int eventType = int.Parse(events[e].GetValue("type"), CultureInfo.InvariantCulture);
                    if (eventType == (int)PartEventType.ShroudJettisoned)
                        shroudCount++;
                    else if (eventType == (int)PartEventType.EngineIgnited)
                    {
                        igniteCount++;
                        if (firstIgniteIndex < 0)
                            firstIgniteIndex = e;
                    }
                    else if (eventType == (int)PartEventType.EngineThrottle)
                        throttleCount++;
                    else if (eventType == (int)PartEventType.EngineShutdown)
                        shutdownCount++;
                }

                Assert.True(shroudCount == 0 || shroudCount == 1);
                Assert.Equal(2, igniteCount);
                Assert.Equal(2, shutdownCount);
                Assert.Equal(shroudCount == 1 ? 3 : 4, throttleCount);
                Assert.True(firstIgniteIndex >= 0);
                Assert.Equal("0.3", events[firstIgniteIndex].GetValue("value"));

                int expectedFirstType = shroudCount == 1
                    ? (int)PartEventType.ShroudJettisoned
                    : (int)PartEventType.EngineIgnited;
                Assert.Equal(expectedFirstType.ToString(CultureInfo.InvariantCulture), events[0].GetValue("type"));
            }
        }

        [Fact]
        public void EngineShowcaseRecordings_KickbackMatchesThumperSrbProfile()
        {
            var recordings = EngineShowcaseRecordings(baseUT: 17000);
            ConfigNode thumper = null;
            ConfigNode kickback = null;

            for (int i = 0; i < recordings.Length; i++)
            {
                ConfigNode built = recordings[i].Build();
                string name = built.GetValue("vesselName");
                if (name == "Part Showcase - Thumper")
                    thumper = built;
                else if (name == "Part Showcase - Kickback")
                    kickback = built;
            }

            Assert.NotNull(thumper);
            Assert.NotNull(kickback);

            ConfigNode[] thumperEvents = thumper.GetNodes("PART_EVENT");
            ConfigNode[] kickbackEvents = kickback.GetNodes("PART_EVENT");
            Assert.Equal(2, thumperEvents.Length);
            Assert.Equal(2, kickbackEvents.Length);

            for (int i = 0; i < 2; i++)
            {
                Assert.Equal(thumperEvents[i].GetValue("type"), kickbackEvents[i].GetValue("type"));
                Assert.Equal(thumperEvents[i].GetValue("value") ?? "", kickbackEvents[i].GetValue("value") ?? "");
            }

            double thumperDuration = double.Parse(thumperEvents[1].GetValue("ut"), CultureInfo.InvariantCulture) -
                double.Parse(thumperEvents[0].GetValue("ut"), CultureInfo.InvariantCulture);
            double kickbackDuration = double.Parse(kickbackEvents[1].GetValue("ut"), CultureInfo.InvariantCulture) -
                double.Parse(kickbackEvents[0].GetValue("ut"), CultureInfo.InvariantCulture);

            Assert.Equal(thumperDuration, kickbackDuration, 6);
        }

        [Fact]
        public void EngineShowcaseRecordings_RapierModeSwitchHasCorrectEventProfile()
        {
            var recordings = EngineShowcaseRecordings(baseUT: 17000);
            ConfigNode rapier = null;
            for (int i = 0; i < recordings.Length; i++)
            {
                ConfigNode built = recordings[i].Build();
                if (built.GetValue("vesselName") == "Part Showcase - RAPIER")
                {
                    rapier = built;
                    break;
                }
            }
            Assert.NotNull(rapier);

            var events = rapier.GetNodes("PART_EVENT");
            Assert.Equal(8, events.Length);

            // Verify all events target the primary part
            var primaryPart = rapier.GetNode("GHOST_VISUAL_SNAPSHOT").GetNodes("PART")[0];
            string expectedPid = primaryPart.GetValue("persistentId");
            for (int i = 0; i < events.Length; i++)
                Assert.Equal(expectedPid, events[i].GetValue("pid"));

            // Verify UTs are monotonically increasing
            double previousUt = double.MinValue;
            for (int i = 0; i < events.Length; i++)
            {
                double ut = double.Parse(events[i].GetValue("ut"), CultureInfo.InvariantCulture);
                Assert.True(ut > previousUt, $"Non-monotonic UT at event {i}");
                previousUt = ut;
            }

            // Verify event type/moduleIndex sequence:
            // jet ignite(0), jet throttle(0), jet shutdown(0),
            // rocket ignite(1), rocket throttle(1), rocket shutdown(1),
            // jet ignite(0), jet shutdown(0)
            var expectedTypes = new[]
            {
                PartEventType.EngineIgnited,  PartEventType.EngineThrottle, PartEventType.EngineShutdown,
                PartEventType.EngineIgnited,  PartEventType.EngineThrottle, PartEventType.EngineShutdown,
                PartEventType.EngineIgnited,  PartEventType.EngineShutdown
            };
            var expectedMidx = new[] { 0, 0, 0, 1, 1, 1, 0, 0 };
            for (int i = 0; i < events.Length; i++)
            {
                Assert.Equal(((int)expectedTypes[i]).ToString(CultureInfo.InvariantCulture),
                    events[i].GetValue("type"));
                Assert.Equal(expectedMidx[i].ToString(CultureInfo.InvariantCulture),
                    events[i].GetValue("midx"));
            }

            // Verify throttle values on ignite events
            Assert.Equal("0.5", events[0].GetValue("value"));  // jet ignite at 50%
            Assert.Equal("0.8", events[3].GetValue("value"));  // rocket ignite at 80%
            Assert.Equal("1", events[6].GetValue("value"));    // jet re-ignite at 100%
        }

        [Fact]
        public void RcsShowcaseRecordings_BuildExpectedShape()
        {
            var recordings = RcsShowcaseRecordings(baseUT: 17000);
            Assert.Equal(5, recordings.Length);

            var first = recordings[0].Build();
            Assert.Equal("Part Showcase - RCS RV-105", first.GetValue("vesselName"));
            Assert.Equal("True", first.GetValue("loopPlayback"));
            Assert.Equal(8, first.GetNodes("PART_EVENT").Length);

            var events = first.GetNodes("PART_EVENT");
            Assert.Equal(((int)PartEventType.RCSActivated).ToString(), events[0].GetValue("type"));
            Assert.Equal("1", events[0].GetValue("value"));
            Assert.Equal("0", events[0].GetValue("midx"));
            Assert.Equal(((int)PartEventType.RCSStopped).ToString(), events[1].GetValue("type"));
            Assert.Equal("0", events[1].GetValue("value"));

            var ghost = first.GetNode("GHOST_VISUAL_SNAPSHOT");
            Assert.NotNull(ghost);
            Assert.Equal("RCSBlock.v2", ghost.GetNodes("PART")[0].GetValue("name"));
            Assert.Equal(ghost.GetNodes("PART")[0].GetValue("persistentId"), events[0].GetValue("pid"));

            var names = new[] { "RCSBlock.v2", "RCSblock.01.small", "RCSLinearSmall", "linearRcs", "vernierEngine" };
            for (int i = 0; i < recordings.Length; i++)
            {
                var g = recordings[i].Build().GetNode("GHOST_VISUAL_SNAPSHOT");
                Assert.Equal(names[i], g.GetNodes("PART")[0].GetValue("name"));
            }
        }

        [Fact]
        public void RcsShowcaseRecordings_UseVisibilityBiasedCadence()
        {
            var first = RcsShowcaseRecordings(baseUT: 17000)[0].Build();
            var events = first.GetNodes("PART_EVENT");
            Assert.Equal(8, events.Length);

            double[] uts = new double[events.Length];
            for (int i = 0; i < events.Length; i++)
                uts[i] = double.Parse(events[i].GetValue("ut"), CultureInfo.InvariantCulture);

            // Starts ON at recording start and keeps a long ON / short OFF duty cycle.
            Assert.Equal(17030.0, uts[0], 3);
            Assert.Equal(4.5, uts[1] - uts[0], 3);
            Assert.Equal(1.5, uts[2] - uts[1], 3);
            Assert.Equal(4.5, uts[3] - uts[2], 3);
            Assert.Equal(1.5, uts[4] - uts[3], 3);
            Assert.Equal(4.5, uts[5] - uts[4], 3);
            Assert.Equal(1.5, uts[6] - uts[5], 3);
            Assert.Equal(4.5, uts[7] - uts[6], 3);

            Assert.Equal(((int)PartEventType.RCSActivated).ToString(), events[0].GetValue("type"));
            Assert.Equal(((int)PartEventType.RCSActivated).ToString(), events[6].GetValue("type"));
            Assert.Equal(((int)PartEventType.RCSStopped).ToString(), events[7].GetValue("type"));
        }

        [Fact]
        public void RcsShowcaseRecordings_AllEntriesUseAlternatingCadenceAndPidBinding()
        {
            var recordings = RcsShowcaseRecordings(baseUT: 17000);
            Assert.Equal(5, recordings.Length);

            for (int i = 0; i < recordings.Length; i++)
            {
                ConfigNode built = recordings[i].Build();
                ConfigNode[] events = built.GetNodes("PART_EVENT");
                Assert.Equal(8, events.Length);

                ConfigNode ghost = built.GetNode("GHOST_VISUAL_SNAPSHOT");
                Assert.NotNull(ghost);
                ConfigNode[] parts = ghost.GetNodes("PART");
                Assert.Single(parts);
                string expectedPid = parts[0].GetValue("persistentId");

                for (int e = 0; e < events.Length; e++)
                {
                    bool onEvent = (e % 2) == 0;
                    Assert.Equal(expectedPid, events[e].GetValue("pid"));
                    Assert.Equal("0", events[e].GetValue("midx"));
                    Assert.Equal((onEvent ? (int)PartEventType.RCSActivated : (int)PartEventType.RCSStopped)
                        .ToString(CultureInfo.InvariantCulture), events[e].GetValue("type"));
                    Assert.Equal(onEvent ? "1" : "0", events[e].GetValue("value"));
                }

                for (int e = 1; e < events.Length; e++)
                {
                    double prevUt = double.Parse(events[e - 1].GetValue("ut"), CultureInfo.InvariantCulture);
                    double curUt = double.Parse(events[e].GetValue("ut"), CultureInfo.InvariantCulture);
                    double expectedStep = ((e - 1) % 2) == 0 ? 4.5 : 1.5;
                    Assert.Equal(expectedStep, curUt - prevUt, 3);
                }
            }
        }

        [Fact]
        public void FairingShowcaseRecordings_BuildExpectedShape()
        {
            var recordings = FairingShowcaseRecordings(baseUT: 17000);
            Assert.Equal(5, recordings.Length);

            var first = recordings[0].Build();
            Assert.Equal("Part Showcase - Fairing Size 1", first.GetValue("vesselName"));
            Assert.Equal("True", first.GetValue("loopPlayback"));
            Assert.Equal(8, first.GetNodes("PART_EVENT").Length);

            var events = first.GetNodes("PART_EVENT");
            Assert.Equal(((int)PartEventType.FairingJettisoned).ToString(), events[0].GetValue("type"));
            Assert.Equal(((int)PartEventType.FairingJettisoned).ToString(), events[1].GetValue("type"));

            var ghost = first.GetNode("GHOST_VISUAL_SNAPSHOT");
            Assert.NotNull(ghost);
            Assert.Equal("fairingSize1", ghost.GetNodes("PART")[0].GetValue("name"));
            Assert.Equal(ghost.GetNodes("PART")[0].GetValue("persistentId"), events[0].GetValue("pid"));
            Assert.NotNull(ghost.GetNodes("PART")[0].GetNode("MODULE"));

            var names = new[] { "fairingSize1", "fairingSize1p5", "fairingSize2", "fairingSize3", "fairingSize4" };
            for (int i = 0; i < recordings.Length; i++)
            {
                var g = recordings[i].Build().GetNode("GHOST_VISUAL_SNAPSHOT");
                Assert.Equal(names[i], g.GetNodes("PART")[0].GetValue("name"));
            }
        }

        [Fact]
        public void RadiatorShowcaseRecordings_BuildExpectedShape()
        {
            var recordings = RadiatorShowcaseRecordings(baseUT: 17000);
            Assert.Equal(2, recordings.Length);

            var first = recordings[0].Build();
            Assert.Equal("Part Showcase - Radiator Medium", first.GetValue("vesselName"));
            Assert.Equal("True", first.GetValue("loopPlayback"));
            Assert.Equal(8, first.GetNodes("PART_EVENT").Length);

            var events = first.GetNodes("PART_EVENT");
            Assert.Equal(((int)PartEventType.DeployableExtended).ToString(), events[0].GetValue("type"));
            Assert.Equal(((int)PartEventType.DeployableRetracted).ToString(), events[1].GetValue("type"));

            var names = new[] { "foldingRadMed", "foldingRadLarge" };
            for (int i = 0; i < recordings.Length; i++)
            {
                var g = recordings[i].Build().GetNode("GHOST_VISUAL_SNAPSHOT");
                Assert.Equal(names[i], g.GetNodes("PART")[0].GetValue("name"));
            }
        }

        [Fact]
        public void DrillShowcaseRecordings_BuildExpectedShape()
        {
            var recordings = DrillShowcaseRecordings(baseUT: 17000);
            Assert.Equal(2, recordings.Length);

            var first = recordings[0].Build();
            Assert.Equal("Part Showcase - Drill Junior", first.GetValue("vesselName"));
            Assert.Equal("True", first.GetValue("loopPlayback"));
            Assert.Equal(8, first.GetNodes("PART_EVENT").Length);

            var events = first.GetNodes("PART_EVENT");
            Assert.Equal(((int)PartEventType.DeployableExtended).ToString(), events[0].GetValue("type"));
            Assert.Equal(((int)PartEventType.DeployableRetracted).ToString(), events[1].GetValue("type"));

            var names = new[] { "MiniDrill", "RadialDrill" };
            for (int i = 0; i < recordings.Length; i++)
            {
                var g = recordings[i].Build().GetNode("GHOST_VISUAL_SNAPSHOT");
                Assert.Equal(names[i], g.GetNodes("PART")[0].GetValue("name"));
            }
        }

        [Fact]
        public void DeployedScienceShowcaseRecordings_BuildExpectedShape()
        {
            var recordings = DeployedScienceShowcaseRecordings(baseUT: 17000);
            Assert.Equal(8, recordings.Length);

            var first = recordings[0].Build();
            Assert.Equal("Part Showcase - Deployed Central Station", first.GetValue("vesselName"));
            Assert.Equal("True", first.GetValue("loopPlayback"));
            Assert.Equal(8, first.GetNodes("PART_EVENT").Length);

            var events = first.GetNodes("PART_EVENT");
            Assert.Equal(((int)PartEventType.DeployableExtended).ToString(), events[0].GetValue("type"));
            Assert.Equal(((int)PartEventType.DeployableRetracted).ToString(), events[1].GetValue("type"));

            var ghost = first.GetNode("GHOST_VISUAL_SNAPSHOT");
            Assert.NotNull(ghost);
            var parts = ghost.GetNodes("PART");
            Assert.Single(parts);
            Assert.Equal("DeployedCentralStation", parts[0].GetValue("name"));
            Assert.Equal(parts[0].GetValue("persistentId"), events[0].GetValue("pid"));

            var names = new[]
            {
                "DeployedCentralStation", "DeployedGoExOb", "DeployedIONExp", "DeployedRTG",
                "DeployedSatDish", "DeployedSeismicSensor", "DeployedSolarPanel", "DeployedWeatherStn"
            };
            for (int i = 0; i < recordings.Length; i++)
            {
                var g = recordings[i].Build().GetNode("GHOST_VISUAL_SNAPSHOT");
                var gParts = g.GetNodes("PART");
                Assert.Single(gParts);
                Assert.Equal(names[i], gParts[0].GetValue("name"));
            }
        }

        [Fact]
        public void AnimationGroupShowcaseRecordings_BuildExpectedShape()
        {
            var recordings = AnimationGroupShowcaseRecordings(baseUT: 17000);
            Assert.Equal(4, recordings.Length);

            var first = recordings[0].Build();
            Assert.Equal("Part Showcase - Ground Anchor", first.GetValue("vesselName"));
            Assert.Equal("True", first.GetValue("loopPlayback"));
            Assert.Equal(8, first.GetNodes("PART_EVENT").Length);

            var events = first.GetNodes("PART_EVENT");
            Assert.Equal(((int)PartEventType.DeployableExtended).ToString(), events[0].GetValue("type"));
            Assert.Equal(((int)PartEventType.DeployableRetracted).ToString(), events[1].GetValue("type"));

            var names = new[] { "groundAnchor", "SurveyScanner", "ISRU", "OrbitalScanner" };
            for (int i = 0; i < recordings.Length; i++)
            {
                var g = recordings[i].Build().GetNode("GHOST_VISUAL_SNAPSHOT");
                Assert.Equal(names[i], g.GetNodes("PART")[0].GetValue("name"));
            }
        }

        [Fact]
        public void ParachuteShowcaseRecordings_BuildExpectedShape()
        {
            var recordings = ParachuteShowcaseRecordings(baseUT: 17000);
            Assert.Equal(5, recordings.Length);

            var first = recordings[0].Build();
            Assert.Equal("Part Showcase - Parachute Mk16", first.GetValue("vesselName"));
            Assert.Equal("True", first.GetValue("loopPlayback"));
            Assert.Equal(9, first.GetNodes("PART_EVENT").Length); // 3 cycles × 3 events (semi, deploy, cut)

            var events = first.GetNodes("PART_EVENT");
            Assert.Equal(((int)PartEventType.ParachuteSemiDeployed).ToString(), events[0].GetValue("type"));
            Assert.Equal(((int)PartEventType.ParachuteDeployed).ToString(), events[1].GetValue("type"));
            Assert.Equal(((int)PartEventType.ParachuteCut).ToString(), events[2].GetValue("type"));

            var names = new[] { "parachuteSingle", "parachuteRadial", "parachuteDrogue", "radialDrogue", "parachuteLarge" };
            for (int i = 0; i < recordings.Length; i++)
            {
                var g = recordings[i].Build().GetNode("GHOST_VISUAL_SNAPSHOT");
                Assert.Equal(names[i], g.GetNodes("PART")[0].GetValue("name"));
            }
        }

        [Fact]
        public void SpecialDeployAnimationShowcaseRecordings_BuildExpectedShape()
        {
            var recordings = SpecialDeployAnimationShowcaseRecordings(baseUT: 17000);
            Assert.Equal(12, recordings.Length);

            var first = recordings[0].Build();
            Assert.Equal("Part Showcase - Rover Wheel M1-F", first.GetValue("vesselName"));
            Assert.Equal("True", first.GetValue("loopPlayback"));
            Assert.Equal(8, first.GetNodes("PART_EVENT").Length);

            var firstEvents = first.GetNodes("PART_EVENT");
            Assert.Equal(((int)PartEventType.GearDeployed).ToString(), firstEvents[0].GetValue("type"));
            Assert.Equal(((int)PartEventType.GearRetracted).ToString(), firstEvents[1].GetValue("type"));

            var secondEvents = recordings[1].Build().GetNodes("PART_EVENT");
            Assert.Equal(((int)PartEventType.DeployableExtended).ToString(), secondEvents[0].GetValue("type"));
            Assert.Equal(((int)PartEventType.DeployableRetracted).ToString(), secondEvents[1].GetValue("type"));

            var heatShieldEvents = recordings[4].Build().GetNodes("PART_EVENT");
            Assert.Equal(((int)PartEventType.ShroudJettisoned).ToString(), heatShieldEvents[0].GetValue("type"));
            Assert.Equal(((int)PartEventType.DeployableExtended).ToString(), heatShieldEvents[1].GetValue("type"));
            Assert.Equal(((int)PartEventType.DeployableRetracted).ToString(), heatShieldEvents[2].GetValue("type"));

            var names = new[]
            {
                "roverWheelM1-F", "GooExperiment", "science_module", "Magnetometer", "InflatableHeatShield", "InflatableAirlock",
                "dockingPort1", "dockingPortLateral", "GrapplingDevice", "smallClaw", "mk2DockingPort", "mk2LanderCabin_v2"
            };
            for (int i = 0; i < recordings.Length; i++)
            {
                var g = recordings[i].Build().GetNode("GHOST_VISUAL_SNAPSHOT");
                Assert.Equal(names[i], g.GetNodes("PART")[0].GetValue("name"));
            }
        }

        [Fact]
        public void RoboticsShowcaseRecordings_BuildExpectedShape()
        {
            var recordings = RoboticsShowcaseRecordings(baseUT: 17000);
            Assert.Equal(21, recordings.Length);

            var first = recordings[0].Build();
            Assert.Equal("Part Showcase - Robotics Hinge G-11", first.GetValue("vesselName"));
            Assert.Equal("True", first.GetValue("loopPlayback"));
            Assert.Equal(8, first.GetNodes("PART_EVENT").Length);

            var events = first.GetNodes("PART_EVENT");
            Assert.Equal(((int)PartEventType.RoboticMotionStarted).ToString(), events[0].GetValue("type"));
            Assert.Equal(((int)PartEventType.RoboticMotionStopped).ToString(), events[1].GetValue("type"));

            var names = new[]
            {
                "hinge.01", "hinge.01.s", "hinge.03", "hinge.03.s", "hinge.04",
                "piston.01", "piston.02", "piston.03", "piston.04",
                "rotoServo.00", "rotoServo.02", "rotoServo.03", "rotoServo.04",
                "rotor.01", "rotor.01s", "rotor.02", "rotor.02s", "rotor.03", "rotor.03s",
                "RotorEngine.02", "RotorEngine.03"
            };
            for (int i = 0; i < recordings.Length; i++)
            {
                var g = recordings[i].Build().GetNode("GHOST_VISUAL_SNAPSHOT");
                Assert.Equal(names[i], g.GetNodes("PART")[0].GetValue("name"));
            }
        }

        [Fact]
        public void JettisonShowcaseRecordings_BuildExpectedShape()
        {
            var recordings = JettisonShowcaseRecordings(baseUT: 17000);
            Assert.Equal(9, recordings.Length);

            var first = recordings[0].Build();
            Assert.Equal("Part Showcase - Engine Plate 1.5", first.GetValue("vesselName"));
            Assert.Equal("True", first.GetValue("loopPlayback"));
            Assert.Equal(8, first.GetNodes("PART_EVENT").Length);

            var events = first.GetNodes("PART_EVENT");
            Assert.Equal(((int)PartEventType.ShroudJettisoned).ToString(), events[0].GetValue("type"));
            Assert.Equal(((int)PartEventType.ShroudJettisoned).ToString(), events[1].GetValue("type"));

            var names = new[]
            {
                "EnginePlate1p5", "EnginePlate2", "EnginePlate3", "EnginePlate4", "EnginePlate5",
                "HeatShield1", "HeatShield2", "HeatShield3", "HeatShield1p5"
            };
            for (int i = 0; i < recordings.Length; i++)
            {
                var g = recordings[i].Build().GetNode("GHOST_VISUAL_SNAPSHOT");
                Assert.Equal(names[i], g.GetNodes("PART")[0].GetValue("name"));
            }
        }

        [Fact]
        public void AeroSurfaceShowcaseRecordings_BuildExpectedShape()
        {
            var recordings = AeroSurfaceShowcaseRecordings(baseUT: 17000);
            Assert.Single(recordings);

            var first = recordings[0].Build();
            Assert.Equal("Part Showcase - Airbrake", first.GetValue("vesselName"));
            Assert.Equal("True", first.GetValue("loopPlayback"));
            Assert.Equal(8, first.GetNodes("PART_EVENT").Length);

            var events = first.GetNodes("PART_EVENT");
            Assert.Equal(((int)PartEventType.DeployableExtended).ToString(), events[0].GetValue("type"));
            Assert.Equal(((int)PartEventType.DeployableRetracted).ToString(), events[1].GetValue("type"));

            var ghost = first.GetNode("GHOST_VISUAL_SNAPSHOT");
            Assert.NotNull(ghost);
            var part = ghost.GetNodes("PART")[0];
            Assert.Equal("airbrake1", part.GetValue("name"));
            Assert.Equal(part.GetValue("persistentId"), events[0].GetValue("pid"));
        }

        [Fact]
        public void RobotArmScannerShowcaseRecordings_BuildExpectedShape()
        {
            var recordings = RobotArmScannerShowcaseRecordings(baseUT: 17000);
            Assert.Equal(3, recordings.Length);

            var first = recordings[0].Build();
            Assert.Equal("Part Showcase - Robot Arm Scanner S1", first.GetValue("vesselName"));
            Assert.Equal("True", first.GetValue("loopPlayback"));
            Assert.Equal(8, first.GetNodes("PART_EVENT").Length);

            var events = first.GetNodes("PART_EVENT");
            Assert.Equal(((int)PartEventType.DeployableExtended).ToString(), events[0].GetValue("type"));
            Assert.Equal(((int)PartEventType.DeployableRetracted).ToString(), events[1].GetValue("type"));

            var names = new[] { "RobotArmScanner_S1", "RobotArmScanner_S2", "RobotArmScanner_S3" };
            for (int i = 0; i < recordings.Length; i++)
            {
                var g = recordings[i].Build().GetNode("GHOST_VISUAL_SNAPSHOT");
                Assert.Equal(names[i], g.GetNodes("PART")[0].GetValue("name"));
            }
        }

        [Fact]
        public void ControlSurfaceShowcaseRecordings_BuildExpectedShape()
        {
            var recordings = ControlSurfaceShowcaseRecordings(baseUT: 17000);
            Assert.Equal(24, recordings.Length);

            var first = recordings[0].Build();
            Assert.Equal("Part Showcase - Control Surface Advanced Canard", first.GetValue("vesselName"));
            Assert.Equal("True", first.GetValue("loopPlayback"));
            Assert.Equal(8, first.GetNodes("PART_EVENT").Length);

            var events = first.GetNodes("PART_EVENT");
            Assert.Equal(((int)PartEventType.DeployableExtended).ToString(), events[0].GetValue("type"));
            Assert.Equal(((int)PartEventType.DeployableRetracted).ToString(), events[1].GetValue("type"));

            var names = new[]
            {
                "AdvancedCanard", "airlinerCtrlSrf", "airlinerTailFin", "CanardController",
                "elevon2", "elevon3", "elevon5", "largeFanBlade", "largeHeliBlade", "largePropeller",
                "mediumFanBlade", "mediumHeliBlade", "mediumPropeller", "R8winglet", "smallCtrlSrf",
                "smallFanBlade", "smallHeliBlade", "smallPropeller", "StandardCtrlSrf", "tailfin",
                "winglet3", "wingShuttleElevon1", "wingShuttleElevon2", "wingShuttleRudder"
            };
            for (int i = 0; i < recordings.Length; i++)
            {
                var g = recordings[i].Build().GetNode("GHOST_VISUAL_SNAPSHOT");
                Assert.Equal(names[i], g.GetNodes("PART")[0].GetValue("name"));
            }
        }

        [Fact]
        public void WheelDynamicsShowcaseRecordings_BuildExpectedShape()
        {
            var recordings = WheelDynamicsShowcaseRecordings(baseUT: 17000);
            Assert.Equal(6, recordings.Length);

            var first = recordings[0].Build();
            Assert.Equal("Part Showcase - Wheel Dynamics Gear Fixed", first.GetValue("vesselName"));
            Assert.Equal("True", first.GetValue("loopPlayback"));
            Assert.Equal(8, first.GetNodes("PART_EVENT").Length);

            var firstEvents = first.GetNodes("PART_EVENT");
            Assert.Equal(((int)PartEventType.RoboticMotionStarted).ToString(), firstEvents[0].GetValue("type"));
            Assert.Equal(((int)PartEventType.RoboticMotionStopped).ToString(), firstEvents[1].GetValue("type"));
            Assert.Equal("0", firstEvents[0].GetValue("midx"));

            var motorEvents = recordings[2].Build().GetNodes("PART_EVENT");
            Assert.Equal("2", motorEvents[0].GetValue("midx"));
            Assert.Equal("180", motorEvents[0].GetValue("value"));

            var names = new[]
            {
                "GearFixed", "GearFree", "roverWheel1", "roverWheel2", "roverWheel3", "wheelMed"
            };
            for (int i = 0; i < recordings.Length; i++)
            {
                var g = recordings[i].Build().GetNode("GHOST_VISUAL_SNAPSHOT");
                Assert.Equal(names[i], g.GetNodes("PART")[0].GetValue("name"));
            }
        }

        [Fact]
        public void AnimateHeatShowcaseRecordings_BuildExpectedShape()
        {
            var recordings = AnimateHeatShowcaseRecordings(baseUT: 17000);
            Assert.Equal(13, recordings.Length);

            var first = recordings[0].Build();
            Assert.Equal("Part Showcase - AnimateHeat Airplane Tail", first.GetValue("vesselName"));
            Assert.Equal("True", first.GetValue("loopPlayback"));
            Assert.Equal(10, first.GetNodes("PART_EVENT").Length);

            var firstEvents = first.GetNodes("PART_EVENT");
            Assert.Equal(((int)PartEventType.ThermalAnimationMedium).ToString(), firstEvents[0].GetValue("type"));
            Assert.Equal(((int)PartEventType.ThermalAnimationHot).ToString(), firstEvents[1].GetValue("type"));
            Assert.Equal(((int)PartEventType.ThermalAnimationMedium).ToString(), firstEvents[2].GetValue("type"));
            Assert.Equal(((int)PartEventType.ThermalAnimationCold).ToString(), firstEvents[3].GetValue("type"));
            Assert.Equal("0.5", firstEvents[0].GetValue("value"));

            var names = new[]
            {
                "airplaneTail",
                "airplaneTailB",
                "avionicsNoseCone",
                "CircularIntake",
                "MK1IntakeFuselage",
                "nacelleBody",
                "noseConeAdapter",
                "pointyNoseConeA",
                "pointyNoseConeB",
                "radialEngineBody",
                "ramAirIntake",
                "shockConeIntake",
                "standardNoseCone"
            };

            for (int i = 0; i < recordings.Length; i++)
            {
                var ghost = recordings[i].Build().GetNode("GHOST_VISUAL_SNAPSHOT");
                Assert.Equal(names[i], ghost.GetNodes("PART")[0].GetValue("name"));
            }
        }

        [Fact]
        public void InventoryPlacementShowcaseRecording_BuildExpectedShape()
        {
            var rec = InventoryPlacementShowcaseRecording(baseUT: 17000).Build();
            Assert.Equal("Part Showcase - Inventory Placement", rec.GetValue("vesselName"));
            Assert.Equal("True", rec.GetValue("loopPlayback"));
            Assert.Equal(8, rec.GetNodes("PART_EVENT").Length);

            var events = rec.GetNodes("PART_EVENT");
            Assert.Equal(((int)PartEventType.InventoryPartPlaced).ToString(), events[0].GetValue("type"));
            Assert.Equal(((int)PartEventType.DeployableExtended).ToString(), events[1].GetValue("type"));
            Assert.Equal(((int)PartEventType.DeployableRetracted).ToString(), events[2].GetValue("type"));
            Assert.Equal(((int)PartEventType.InventoryPartRemoved).ToString(), events[3].GetValue("type"));

            var ghost = rec.GetNode("GHOST_VISUAL_SNAPSHOT");
            Assert.NotNull(ghost);
            var parts = ghost.GetNodes("PART");
            Assert.Equal(2, parts.Length);
            Assert.Equal("DeployedWeatherStn", parts[0].GetValue("name"));
            Assert.Equal("kerbalEVA", parts[1].GetValue("name"));
            Assert.Equal(parts[0].GetValue("persistentId"), events[0].GetValue("pid"));
        }

        [Fact]
        public void ShowcaseRecordings_PrimaryPartRotationIsConsistent()
        {
            var allShowcases = new[]
            {
                LightShowcaseRecordings(17000),
                DeployableShowcaseRecordings(17000),
                GearShowcaseRecordings(17000),
                CargoBayShowcaseRecordings(17000),
                EngineShowcaseRecordings(17000),
                LadderShowcaseRecordings(17000),
                RcsShowcaseRecordings(17000),
                FairingShowcaseRecordings(17000),
                RadiatorShowcaseRecordings(17000),
                DrillShowcaseRecordings(17000),
                DeployedScienceShowcaseRecordings(17000),
                AnimationGroupShowcaseRecordings(17000),
                ParachuteShowcaseRecordings(17000),
                SpecialDeployAnimationShowcaseRecordings(17000),
                JettisonShowcaseRecordings(17000),
                RoboticsShowcaseRecordings(17000),
                AeroSurfaceShowcaseRecordings(17000),
                RobotArmScannerShowcaseRecordings(17000),
                ControlSurfaceShowcaseRecordings(17000),
                WheelDynamicsShowcaseRecordings(17000),
                AnimateHeatShowcaseRecordings(17000),
                ColorChangerShowcaseRecordings(17000),
                new[] { InventoryPlacementShowcaseRecording(17000) }
            };

            foreach (var category in allShowcases)
            {
                foreach (var rb in category)
                {
                    var rec = rb.Build();
                    var ghost = rec.GetNode("GHOST_VISUAL_SNAPSHOT");
                    Assert.NotNull(ghost);
                    var primaryPart = ghost.GetNodes("PART")[0];
                    Assert.Equal(ShowcasePrimaryRotation, primaryPart.GetValue("rotation"));
                }
            }
        }

        [Fact]
        public void ShowcaseToggleRecordings_DefaultCadenceCategoriesUseExpectedStateMachine()
        {
            var categories = new[]
            {
                new
                {
                    Recordings = DeployableShowcaseRecordings(17000),
                    OnEvent = PartEventType.DeployableExtended,
                    OffEvent = PartEventType.DeployableRetracted
                },
                new
                {
                    Recordings = GearShowcaseRecordings(17000),
                    OnEvent = PartEventType.GearDeployed,
                    OffEvent = PartEventType.GearRetracted
                },
                new
                {
                    Recordings = CargoBayShowcaseRecordings(17000),
                    OnEvent = PartEventType.CargoBayOpened,
                    OffEvent = PartEventType.CargoBayClosed
                },
                new
                {
                    Recordings = LadderShowcaseRecordings(17000),
                    OnEvent = PartEventType.DeployableExtended,
                    OffEvent = PartEventType.DeployableRetracted
                },
                new
                {
                    Recordings = RadiatorShowcaseRecordings(17000),
                    OnEvent = PartEventType.DeployableExtended,
                    OffEvent = PartEventType.DeployableRetracted
                }
            };

            foreach (var category in categories)
            {
                foreach (var rb in category.Recordings)
                {
                    AssertAlternatingPartEvents(
                        rb.Build(),
                        category.OnEvent,
                        category.OffEvent,
                        firstEventOffsetSeconds: 3.0,
                        onDurationSeconds: 3.0,
                        offDurationSeconds: 3.0,
                        expectedOnValue: 0f);
                }
            }
        }

        [Fact]
        public void ShowcaseToggleRecordings_FastCadenceCategoriesUseExpectedStateMachine()
        {
            var categories = new[]
            {
                new
                {
                    Recordings = RcsShowcaseRecordings(17000),
                    OnEvent = PartEventType.RCSActivated,
                    OffEvent = PartEventType.RCSStopped,
                    OnValue = 1.0f
                },
                new
                {
                    Recordings = DrillShowcaseRecordings(17000),
                    OnEvent = PartEventType.DeployableExtended,
                    OffEvent = PartEventType.DeployableRetracted,
                    OnValue = 0f
                },
                new
                {
                    Recordings = DeployedScienceShowcaseRecordings(17000),
                    OnEvent = PartEventType.DeployableExtended,
                    OffEvent = PartEventType.DeployableRetracted,
                    OnValue = 0f
                },
                new
                {
                    Recordings = AnimationGroupShowcaseRecordings(17000),
                    OnEvent = PartEventType.DeployableExtended,
                    OffEvent = PartEventType.DeployableRetracted,
                    OnValue = 0f
                },
                new
                {
                    Recordings = AeroSurfaceShowcaseRecordings(17000),
                    OnEvent = PartEventType.DeployableExtended,
                    OffEvent = PartEventType.DeployableRetracted,
                    OnValue = 0f
                },
                new
                {
                    Recordings = RobotArmScannerShowcaseRecordings(17000),
                    OnEvent = PartEventType.DeployableExtended,
                    OffEvent = PartEventType.DeployableRetracted,
                    OnValue = 0f
                },
                new
                {
                    Recordings = ControlSurfaceShowcaseRecordings(17000),
                    OnEvent = PartEventType.DeployableExtended,
                    OffEvent = PartEventType.DeployableRetracted,
                    OnValue = 0f
                },
                // AnimateHeat showcases use 3-state cycle (Cold/Medium/Hot), not simple alternating.
                // Their shape is validated in AnimateHeatShowcaseRecordings_BuildExpectedShape.
            };

            foreach (var category in categories)
            {
                foreach (var rb in category.Recordings)
                {
                    AssertAlternatingPartEvents(
                        rb.Build(),
                        category.OnEvent,
                        category.OffEvent,
                        firstEventOffsetSeconds: 0.0,
                        onDurationSeconds: 4.5,
                        offDurationSeconds: 1.5,
                        expectedOnValue: category.OnValue);
                }
            }
        }

        [Fact]
        public void SpecialDeployAnimationShowcases_NonHeatShieldEntriesUseFastAlternatingStateMachine()
        {
            var recordings = SpecialDeployAnimationShowcaseRecordings(17000);
            Assert.Equal(12, recordings.Length);

            foreach (var rb in recordings)
            {
                var rec = rb.Build();
                string vesselName = rec.GetValue("vesselName");
                if (vesselName == "Part Showcase - Inflatable Heat Shield")
                    continue;

                PartEventType onEvent = vesselName == "Part Showcase - Rover Wheel M1-F"
                    ? PartEventType.GearDeployed
                    : PartEventType.DeployableExtended;
                PartEventType offEvent = vesselName == "Part Showcase - Rover Wheel M1-F"
                    ? PartEventType.GearRetracted
                    : PartEventType.DeployableRetracted;

                AssertAlternatingPartEvents(
                    rec,
                    onEvent,
                    offEvent,
                    firstEventOffsetSeconds: 0.0,
                    onDurationSeconds: 4.5,
                    offDurationSeconds: 1.5,
                    expectedOnValue: 0f);
            }
        }

        [Fact]
        public void SpecialDeployAnimationShowcases_InflatableHeatShieldUsesExpectedSequence()
        {
            var recordings = SpecialDeployAnimationShowcaseRecordings(17000);
            ConfigNode heatShield = null;

            foreach (var rb in recordings)
            {
                var rec = rb.Build();
                if (rec.GetValue("vesselName") == "Part Showcase - Inflatable Heat Shield")
                {
                    heatShield = rec;
                    break;
                }
            }

            Assert.NotNull(heatShield);
            var events = heatShield.GetNodes("PART_EVENT");
            Assert.Equal(8, events.Length);

            var expectedTypes = new[]
            {
                PartEventType.ShroudJettisoned,
                PartEventType.DeployableExtended,
                PartEventType.DeployableRetracted,
                PartEventType.DeployableExtended,
                PartEventType.DeployableRetracted,
                PartEventType.DeployableExtended,
                PartEventType.DeployableRetracted,
                PartEventType.DeployableExtended
            };
            var expectedOffsets = new[] { 0.5, 1.0, 4.5, 7.5, 10.5, 13.5, 16.5, 19.5 };

            var ghost = heatShield.GetNode("GHOST_VISUAL_SNAPSHOT");
            var primaryPart = ghost.GetNodes("PART")[0];
            double baseUt = ParseInvariantDouble(events[0].GetValue("ut")) - expectedOffsets[0];

            for (int i = 0; i < events.Length; i++)
            {
                Assert.Equal(((int)expectedTypes[i]).ToString(CultureInfo.InvariantCulture), events[i].GetValue("type"));
                AssertEventTargetsPrimaryPart(events[i], primaryPart);
                Assert.Equal("0", events[i].GetValue("midx"));
                AssertFloatClose(0f, ParseInvariantFloat(events[i].GetValue("value")));
                Assert.Equal(baseUt + expectedOffsets[i], ParseInvariantDouble(events[i].GetValue("ut")), 6);
            }
        }

        [Fact]
        public void FairingShowcaseRecordings_UseExpectedModulesAndJettisonCadence()
        {
            var expectedBaseRadius = new Dictionary<string, float>
            {
                ["Part Showcase - Fairing Size 1"] = 0.625f,
                ["Part Showcase - Fairing Size 1.5"] = 0.9375f,
                ["Part Showcase - Fairing Size 2"] = 1.25f,
                ["Part Showcase - Fairing Size 3"] = 1.875f,
                ["Part Showcase - Fairing Size 4"] = 2.5f
            };
            var expectedTopHeight = new Dictionary<string, float>
            {
                ["Part Showcase - Fairing Size 1"] = 2.0f,
                ["Part Showcase - Fairing Size 1.5"] = 2.8f,
                ["Part Showcase - Fairing Size 2"] = 3.2f,
                ["Part Showcase - Fairing Size 3"] = 4.5f,
                ["Part Showcase - Fairing Size 4"] = 6.0f
            };

            foreach (var rb in FairingShowcaseRecordings(17000))
            {
                var rec = rb.Build();
                AssertAlternatingPartEvents(
                    rec,
                    PartEventType.FairingJettisoned,
                    PartEventType.FairingJettisoned,
                    firstEventOffsetSeconds: 3.0,
                    onDurationSeconds: 3.0,
                    offDurationSeconds: 3.0,
                    expectedOnValue: 0f);

                var part = rec.GetNode("GHOST_VISUAL_SNAPSHOT").GetNodes("PART")[0];
                ConfigNode fairingModule = null;
                foreach (var module in part.GetNodes("MODULE"))
                {
                    if (module.GetValue("name") == "ModuleProceduralFairing")
                    {
                        fairingModule = module;
                        break;
                    }
                }

                Assert.NotNull(fairingModule);
                var xsections = fairingModule.GetNodes("XSECTION");
                Assert.Equal(2, xsections.Length);
                AssertFloatClose(0f, ParseInvariantFloat(xsections[0].GetValue("h")));
                AssertFloatClose(expectedBaseRadius[rec.GetValue("vesselName")], ParseInvariantFloat(xsections[0].GetValue("r")));
                AssertFloatClose(expectedTopHeight[rec.GetValue("vesselName")], ParseInvariantFloat(xsections[1].GetValue("h")));
                AssertFloatClose(0f, ParseInvariantFloat(xsections[1].GetValue("r")));
            }
        }

        [Fact]
        public void JettisonShowcaseRecordings_UseRepeatedShroudJettisonCadence()
        {
            foreach (var rb in JettisonShowcaseRecordings(17000))
            {
                AssertAlternatingPartEvents(
                    rb.Build(),
                    PartEventType.ShroudJettisoned,
                    PartEventType.ShroudJettisoned,
                    firstEventOffsetSeconds: 3.0,
                    onDurationSeconds: 3.0,
                    offDurationSeconds: 3.0,
                    expectedOnValue: 0f);
            }
        }

        [Fact]
        public void LightShowcaseRecordings_AllEntriesFollowExpectedBlinkStateMachine()
        {
            var expectedTypes = new[]
            {
                PartEventType.LightOn,
                PartEventType.LightBlinkEnabled,
                PartEventType.LightBlinkDisabled,
                PartEventType.LightBlinkEnabled,
                PartEventType.LightBlinkRate,
                PartEventType.LightBlinkDisabled,
                PartEventType.LightOff
            };
            var expectedValues = new[] { 0f, 0.5f, 0f, 2.0f, 5.0f, 0f, 0f };
            var expectedOffsets = new[] { 0.0, 6.0, 12.0, 15.0, 18.0, 21.0, 22.0 };

            foreach (var rb in LightShowcaseRecordings(17000))
            {
                var rec = rb.Build();
                var events = rec.GetNodes("PART_EVENT");
                Assert.Equal(7, events.Length);
                var primaryPart = rec.GetNode("GHOST_VISUAL_SNAPSHOT").GetNodes("PART")[0];
                double baseUt = ParseInvariantDouble(events[0].GetValue("ut"));

                for (int i = 0; i < events.Length; i++)
                {
                    Assert.Equal(((int)expectedTypes[i]).ToString(CultureInfo.InvariantCulture), events[i].GetValue("type"));
                    AssertEventTargetsPrimaryPart(events[i], primaryPart);
                    Assert.Equal("0", events[i].GetValue("midx"));
                    AssertFloatClose(expectedValues[i], ParseInvariantFloat(events[i].GetValue("value")));
                    Assert.Equal(baseUt + expectedOffsets[i], ParseInvariantDouble(events[i].GetValue("ut")), 6);
                }
            }
        }

        [Fact]
        public void ParachuteShowcaseRecordings_AllEntriesFollowThreePhaseStateCycle()
        {
            var expectedTypes = new[]
            {
                PartEventType.ParachuteSemiDeployed,
                PartEventType.ParachuteDeployed,
                PartEventType.ParachuteCut,
                PartEventType.ParachuteSemiDeployed,
                PartEventType.ParachuteDeployed,
                PartEventType.ParachuteCut,
                PartEventType.ParachuteSemiDeployed,
                PartEventType.ParachuteDeployed,
                PartEventType.ParachuteCut
            };
            var expectedOffsets = new[] { 0.0, 3.0, 6.0, 8.0, 11.0, 14.0, 16.0, 19.0, 22.0 };

            foreach (var rb in ParachuteShowcaseRecordings(17000))
            {
                var rec = rb.Build();
                var events = rec.GetNodes("PART_EVENT");
                Assert.Equal(9, events.Length);

                var primaryPart = rec.GetNode("GHOST_VISUAL_SNAPSHOT").GetNodes("PART")[0];
                double baseUt = ParseInvariantDouble(events[0].GetValue("ut"));
                for (int i = 0; i < events.Length; i++)
                {
                    Assert.Equal(((int)expectedTypes[i]).ToString(CultureInfo.InvariantCulture), events[i].GetValue("type"));
                    AssertEventTargetsPrimaryPart(events[i], primaryPart);
                    Assert.Equal("0", events[i].GetValue("midx"));
                    AssertFloatClose(0f, ParseInvariantFloat(events[i].GetValue("value")));
                    Assert.Equal(baseUt + expectedOffsets[i], ParseInvariantDouble(events[i].GetValue("ut")), 6);
                }
            }
        }

        [Fact]
        public void InventoryPlacementShowcaseRecording_UsesRepeatableLifecycleStateMachine()
        {
            var rec = InventoryPlacementShowcaseRecording(17000).Build();
            var events = rec.GetNodes("PART_EVENT");
            Assert.Equal(8, events.Length);

            var expectedTypes = new[]
            {
                PartEventType.InventoryPartPlaced,
                PartEventType.DeployableExtended,
                PartEventType.DeployableRetracted,
                PartEventType.InventoryPartRemoved,
                PartEventType.InventoryPartPlaced,
                PartEventType.DeployableExtended,
                PartEventType.DeployableRetracted,
                PartEventType.InventoryPartRemoved
            };
            var expectedOffsets = new[] { 0.5, 1.0, 4.0, 4.5, 12.5, 13.0, 16.0, 16.5 };
            var primaryPart = rec.GetNode("GHOST_VISUAL_SNAPSHOT").GetNodes("PART")[0];
            double baseUt = ParseInvariantDouble(events[0].GetValue("ut")) - expectedOffsets[0];

            for (int i = 0; i < events.Length; i++)
            {
                Assert.Equal(((int)expectedTypes[i]).ToString(CultureInfo.InvariantCulture), events[i].GetValue("type"));
                AssertEventTargetsPrimaryPart(events[i], primaryPart);
                Assert.Equal("0", events[i].GetValue("midx"));
                AssertFloatClose(0f, ParseInvariantFloat(events[i].GetValue("value")));
                Assert.Equal(baseUt + expectedOffsets[i], ParseInvariantDouble(events[i].GetValue("ut")), 6);
            }
        }

        [Fact]
        public void EngineShowcaseRecordings_AllEntriesUseDeterministicTimelineAndValues()
        {
            var recordings = EngineShowcaseRecordings(17000);
            Assert.Equal(47, recordings.Length);

            var withShroudTypes = new[]
            {
                PartEventType.ShroudJettisoned,
                PartEventType.EngineIgnited,
                PartEventType.EngineThrottle,
                PartEventType.EngineThrottle,
                PartEventType.EngineShutdown,
                PartEventType.EngineIgnited,
                PartEventType.EngineThrottle,
                PartEventType.EngineShutdown
            };
            var withShroudOffsets = new[] { 0.5, 3.0, 6.0, 9.0, 12.0, 15.0, 18.0, 21.0 };
            var withShroudValues = new[] { 0f, 0.3f, 0.7f, 1.0f, 0f, 1.0f, 0.5f, 0f };

            var flameOnlyTypes = new[]
            {
                PartEventType.EngineIgnited,
                PartEventType.EngineThrottle,
                PartEventType.EngineThrottle,
                PartEventType.EngineShutdown,
                PartEventType.EngineIgnited,
                PartEventType.EngineThrottle,
                PartEventType.EngineThrottle,
                PartEventType.EngineShutdown
            };
            var flameOnlyOffsets = new[] { 0.0, 3.0, 6.0, 9.0, 12.0, 15.0, 18.0, 21.0 };
            var flameOnlyValues = new[] { 0.3f, 0.7f, 1.0f, 0f, 1.0f, 0.5f, 0.15f, 0f };

            foreach (var rb in recordings)
            {
                var rec = rb.Build();
                string vesselName = rec.GetValue("vesselName");
                var events = rec.GetNodes("PART_EVENT");

                // RAPIER mode-switch showcase has a custom multi-midx event profile.
                if (string.Equals(vesselName, "Part Showcase - RAPIER", System.StringComparison.Ordinal))
                    continue;

                Assert.True(
                    events.Length == 2 || events.Length == 3 || events.Length == 8,
                    $"Unexpected event count {events.Length} for {vesselName}");

                var primaryPart = rec.GetNode("GHOST_VISUAL_SNAPSHOT").GetNodes("PART")[0];

                if (events.Length == 2)
                {
                    double baseUt = ParseInvariantDouble(events[0].GetValue("ut"));
                    var expectedTypes = new[] { PartEventType.EngineIgnited, PartEventType.EngineShutdown };
                    var expectedOffsets = new[] { 0.0, 12.0 };
                    var expectedValues = new[] { 1.0f, 0f };
                    for (int i = 0; i < events.Length; i++)
                    {
                        Assert.Equal(((int)expectedTypes[i]).ToString(CultureInfo.InvariantCulture), events[i].GetValue("type"));
                        AssertEventTargetsPrimaryPart(events[i], primaryPart);
                        Assert.Equal("0", events[i].GetValue("midx"));
                        AssertFloatClose(expectedValues[i], ParseInvariantFloat(events[i].GetValue("value")));
                        Assert.Equal(baseUt + expectedOffsets[i], ParseInvariantDouble(events[i].GetValue("ut")), 6);
                    }
                    continue;
                }

                if (events.Length == 3)
                {
                    double baseUt = ParseInvariantDouble(events[0].GetValue("ut")) - 0.5;
                    var expectedTypes = new[] { PartEventType.ShroudJettisoned, PartEventType.EngineIgnited, PartEventType.EngineShutdown };
                    var expectedOffsets = new[] { 0.5, 3.0, 15.0 };
                    var expectedValues = new[] { 0f, 1.0f, 0f };
                    for (int i = 0; i < events.Length; i++)
                    {
                        Assert.Equal(((int)expectedTypes[i]).ToString(CultureInfo.InvariantCulture), events[i].GetValue("type"));
                        AssertEventTargetsPrimaryPart(events[i], primaryPart);
                        Assert.Equal("0", events[i].GetValue("midx"));
                        AssertFloatClose(expectedValues[i], ParseInvariantFloat(events[i].GetValue("value")));
                        Assert.Equal(baseUt + expectedOffsets[i], ParseInvariantDouble(events[i].GetValue("ut")), 6);
                    }
                    continue;
                }

                bool hasShroud = events[0].GetValue("type") ==
                    ((int)PartEventType.ShroudJettisoned).ToString(CultureInfo.InvariantCulture);
                var expectedTypes8 = hasShroud ? withShroudTypes : flameOnlyTypes;
                var expectedOffsets8 = hasShroud ? withShroudOffsets : flameOnlyOffsets;
                var expectedValues8 = hasShroud ? withShroudValues : flameOnlyValues;
                double baseUt8 = ParseInvariantDouble(events[0].GetValue("ut")) - expectedOffsets8[0];

                for (int i = 0; i < events.Length; i++)
                {
                    Assert.Equal(((int)expectedTypes8[i]).ToString(CultureInfo.InvariantCulture), events[i].GetValue("type"));
                    AssertEventTargetsPrimaryPart(events[i], primaryPart);
                    Assert.Equal("0", events[i].GetValue("midx"));
                    AssertFloatClose(expectedValues8[i], ParseInvariantFloat(events[i].GetValue("value")));
                    Assert.Equal(baseUt8 + expectedOffsets8[i], ParseInvariantDouble(events[i].GetValue("ut")), 6);
                }
            }
        }

        [Fact]
        public void RoboticsShowcaseRecordings_UseExpectedMotionTargetsAndCadence()
        {
            var expectedOnValueByVessel = new Dictionary<string, float>
            {
                ["Part Showcase - Robotics Hinge G-11"] = 45f,
                ["Part Showcase - Robotics Hinge G-00"] = 45f,
                ["Part Showcase - Robotics Hinge M-12"] = 45f,
                ["Part Showcase - Robotics Hinge M-06"] = 45f,
                ["Part Showcase - Robotics Hinge XL"] = 45f,
                ["Part Showcase - Robotics Piston 3P6"] = 0.3f,
                ["Part Showcase - Robotics Piston 1P2"] = 0.2f,
                ["Part Showcase - Robotics Piston 1P4"] = 0.4f,
                ["Part Showcase - Robotics Piston 3P12"] = 0.9f,
                ["Part Showcase - Robotics Rotation Servo M-06"] = 90f,
                ["Part Showcase - Robotics Rotation Servo M-12"] = 90f,
                ["Part Showcase - Robotics Rotation Servo F-12"] = 90f,
                ["Part Showcase - Robotics Rotation Servo F-33"] = 90f,
                ["Part Showcase - Robotics Rotor EM-16"] = 240f,
                ["Part Showcase - Robotics Rotor EM-16S"] = 240f,
                ["Part Showcase - Robotics Rotor EM-32"] = 240f,
                ["Part Showcase - Robotics Rotor EM-32S"] = 240f,
                ["Part Showcase - Robotics Rotor EM-64"] = 240f,
                ["Part Showcase - Robotics Rotor EM-64S"] = 240f,
                ["Part Showcase - Robotics Rotor Motor R121"] = 240f,
                ["Part Showcase - Robotics Rotor Motor R7000"] = 240f
            };

            foreach (var rb in RoboticsShowcaseRecordings(17000))
            {
                var rec = rb.Build();
                string vesselName = rec.GetValue("vesselName");
                Assert.True(expectedOnValueByVessel.ContainsKey(vesselName), $"Missing expected motion target for {vesselName}");
                AssertAlternatingPartEvents(
                    rec,
                    PartEventType.RoboticMotionStarted,
                    PartEventType.RoboticMotionStopped,
                    firstEventOffsetSeconds: 0.0,
                    onDurationSeconds: 4.5,
                    offDurationSeconds: 1.5,
                    expectedOnValue: expectedOnValueByVessel[vesselName]);
            }
        }

        [Fact]
        public void WheelDynamicsShowcaseRecordings_UseExpectedModulesAndMotionTargets()
        {
            var expectedOnValueByVessel = new Dictionary<string, float>
            {
                ["Part Showcase - Wheel Dynamics Gear Fixed"] = 0.08f,
                ["Part Showcase - Wheel Dynamics Gear Free"] = 24f,
                ["Part Showcase - Wheel Dynamics Rover M1"] = 180f,
                ["Part Showcase - Wheel Dynamics Rover S2"] = 180f,
                ["Part Showcase - Wheel Dynamics Rover XL3"] = 120f,
                ["Part Showcase - Wheel Dynamics TR-2L"] = 180f
            };
            var expectedModuleIndexByVessel = new Dictionary<string, int>
            {
                ["Part Showcase - Wheel Dynamics Gear Fixed"] = 0,
                ["Part Showcase - Wheel Dynamics Gear Free"] = 1,
                ["Part Showcase - Wheel Dynamics Rover M1"] = 2,
                ["Part Showcase - Wheel Dynamics Rover S2"] = 2,
                ["Part Showcase - Wheel Dynamics Rover XL3"] = 1,
                ["Part Showcase - Wheel Dynamics TR-2L"] = 2
            };

            foreach (var rb in WheelDynamicsShowcaseRecordings(17000))
            {
                var rec = rb.Build();
                string vesselName = rec.GetValue("vesselName");
                Assert.True(expectedOnValueByVessel.ContainsKey(vesselName), $"Missing expected wheel target for {vesselName}");
                Assert.True(expectedModuleIndexByVessel.ContainsKey(vesselName), $"Missing expected wheel module for {vesselName}");
                AssertAlternatingPartEvents(
                    rec,
                    PartEventType.RoboticMotionStarted,
                    PartEventType.RoboticMotionStopped,
                    firstEventOffsetSeconds: 0.0,
                    onDurationSeconds: 4.5,
                    offDurationSeconds: 1.5,
                    expectedOnValue: expectedOnValueByVessel[vesselName],
                    expectedModuleIndex: expectedModuleIndexByVessel[vesselName]);
            }
        }

        [Fact]
        public void AllShowcaseRecordings_EventPidMatchesGhostPartPid()
        {
            // Verify the critical invariant: every showcase recording's event PIDs
            // match the ghost part's persistentId, so playback can find the part.
            var allShowcases = new[]
            {
                LightShowcaseRecordings(17000),
                DeployableShowcaseRecordings(17000),
                GearShowcaseRecordings(17000),
                CargoBayShowcaseRecordings(17000),
                EngineShowcaseRecordings(17000),
                LadderShowcaseRecordings(17000),
                RcsShowcaseRecordings(17000),
                FairingShowcaseRecordings(17000),
                RadiatorShowcaseRecordings(17000),
                DrillShowcaseRecordings(17000),
                DeployedScienceShowcaseRecordings(17000),
                AnimationGroupShowcaseRecordings(17000),
                ParachuteShowcaseRecordings(17000),
                SpecialDeployAnimationShowcaseRecordings(17000),
                JettisonShowcaseRecordings(17000),
                RoboticsShowcaseRecordings(17000),
                AeroSurfaceShowcaseRecordings(17000),
                RobotArmScannerShowcaseRecordings(17000),
                ControlSurfaceShowcaseRecordings(17000),
                WheelDynamicsShowcaseRecordings(17000),
                AnimateHeatShowcaseRecordings(17000),
                ColorChangerShowcaseRecordings(17000)
            };

            foreach (var category in allShowcases)
            {
                foreach (var rb in category)
                {
                    var rec = rb.Build();
                    var ghost = rec.GetNode("GHOST_VISUAL_SNAPSHOT");
                    Assert.NotNull(ghost);
                    string partPid = ghost.GetNodes("PART")[0].GetValue("persistentId");

                    var events = rec.GetNodes("PART_EVENT");
                    Assert.True(events.Length > 0, $"{rec.GetValue("vesselName")} has no events");
                    foreach (var evt in events)
                    {
                        Assert.True(partPid == evt.GetValue("pid"),
                            $"Event PID mismatch in '{rec.GetValue("vesselName")}': " +
                            $"event pid={evt.GetValue("pid")} but ghost part pid={partPid}");
                    }
                }
            }
        }

        [Fact]
        public void AllShowcaseRecordings_HaveUniquePositions()
        {
            // Every showcase ghost must have a distinct (lat, lon) so they don't overlap.
            var allShowcases = new[]
            {
                LightShowcaseRecordings(17000),
                DeployableShowcaseRecordings(17000),
                GearShowcaseRecordings(17000),
                CargoBayShowcaseRecordings(17000),
                EngineShowcaseRecordings(17000),
                LadderShowcaseRecordings(17000),
                RcsShowcaseRecordings(17000),
                FairingShowcaseRecordings(17000),
                RadiatorShowcaseRecordings(17000),
                DrillShowcaseRecordings(17000),
                DeployedScienceShowcaseRecordings(17000),
                AnimationGroupShowcaseRecordings(17000),
                ParachuteShowcaseRecordings(17000),
                SpecialDeployAnimationShowcaseRecordings(17000),
                JettisonShowcaseRecordings(17000),
                RoboticsShowcaseRecordings(17000),
                AeroSurfaceShowcaseRecordings(17000),
                RobotArmScannerShowcaseRecordings(17000),
                ControlSurfaceShowcaseRecordings(17000),
                WheelDynamicsShowcaseRecordings(17000),
                AnimateHeatShowcaseRecordings(17000),
                ColorChangerShowcaseRecordings(17000),
                new[] { InventoryPlacementShowcaseRecording(17000) }
            };

            var positions = new HashSet<string>();
            int expectedCount = 0;
            foreach (var category in allShowcases)
            {
                expectedCount += category.Length;
                foreach (var rb in category)
                {
                    var rec = rb.Build();
                    var pts = rec.GetNodes("POINT");
                    string key = pts[0].GetValue("lat") + "," + pts[0].GetValue("lon");
                    Assert.True(positions.Add(key),
                        $"Duplicate position in '{rec.GetValue("vesselName")}': {key}");
                }
            }
            Assert.Equal(expectedCount, positions.Count);
        }

        [Fact]
        public void ScenarioWriter_V3Format_WritesSidecarFiles()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "parsek_sidecar_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                var writer = new ScenarioWriter().WithV3Format();
                var flight = FleaFlight();
                writer.AddRecordingAsTree(flight);

                writer.WriteSidecarFiles(tempDir);

                string id = flight.GetRecordingId();
                string recDir = Path.Combine(tempDir, "Parsek", "Recordings");

                // .prec file exists and has correct content
                string precPath = Path.Combine(recDir, $"{id}.prec");
                Assert.True(File.Exists(precPath), $"Expected .prec file at {precPath}");
                TrajectorySidecarProbe probe;
                Assert.True(RecordingStore.TryProbeTrajectorySidecar(precPath, out probe));
                Assert.Equal(TrajectorySidecarEncoding.BinaryV3, probe.Encoding);
                Assert.Equal(id, probe.RecordingId);

                var restored = new Recording { RecordingId = id };
                Assert.True(RecordingStore.LoadTrajectorySidecarForTesting(precPath, restored));
                Assert.True(restored.Points.Count >= 2, "Expected binary sidecar to preserve trajectory points");

                // _vessel.craft file exists and has vessel data
                string vesselPath = Path.Combine(recDir, $"{id}_vessel.craft");
                Assert.True(File.Exists(vesselPath), $"Expected _vessel.craft at {vesselPath}");
                SnapshotSidecarProbe vesselProbe;
                Assert.True(RecordingStore.TryProbeSnapshotSidecar(vesselPath, out vesselProbe));
                Assert.Equal(SnapshotSidecarEncoding.DeflateV1, vesselProbe.Encoding);

                ConfigNode vesselSnapshot;
                Assert.True(RecordingStore.LoadSnapshotSidecarForTesting(vesselPath, out vesselSnapshot));
                Assert.Equal("Flea Flight", vesselSnapshot.GetValue("name"));
                Assert.True(File.Exists(precPath + ".txt"), $"Expected readable .prec mirror at {precPath}.txt");
                Assert.True(File.Exists(vesselPath + ".txt"), $"Expected readable vessel mirror at {vesselPath}.txt");

                // FleaFlight only has a vessel snapshot, so sidecar writing aliases the
                // effective ghost snapshot to _vessel.craft instead of writing _ghost.craft.
                string ghostPath = Path.Combine(recDir, $"{id}_ghost.craft");
                Assert.False(File.Exists(ghostPath), $"Did not expect _ghost.craft at {ghostPath}");
                Assert.False(File.Exists(ghostPath + ".txt"), $"Did not expect readable _ghost.craft mirror at {ghostPath}.txt");

                ConfigNode scenarioNode = writer.BuildScenarioNode();
                ConfigNode[] trees = scenarioNode.GetNodes("RECORDING_TREE");
                Assert.Single(trees);
                ConfigNode[] recordings = trees[0].GetNodes("RECORDING");
                Assert.Single(recordings);
                Assert.Equal("AliasVessel", recordings[0].GetValue("ghostSnapshotMode"));
            }
            finally
            {
                Directory.Delete(tempDir, true);
            }
        }

        [Fact]
        public void ScenarioWriter_AddRecordingsAsTree_V3Format_WritesSidecarFilesForEverySegment()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "parsek_sidecar_chain_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                var writer = new ScenarioWriter().WithV3Format();
                var segments = EvaWalkChain();
                for (int i = 0; i < segments.Length; i++)
                    segments[i].WithLoopPlayback().WithRecordingGroup("Synthetic");
                writer.AddRecordingsAsTree(segments);

                writer.WriteSidecarFiles(tempDir);

                string recDir = Path.Combine(tempDir, "Parsek", "Recordings");
                for (int i = 0; i < segments.Length; i++)
                {
                    string id = segments[i].GetRecordingId();
                    Assert.True(File.Exists(Path.Combine(recDir, $"{id}.prec")),
                        $"Expected .prec sidecar for segment {id}");
                    Assert.True(File.Exists(Path.Combine(recDir, $"{id}.prec.txt")),
                        $"Expected readable .prec mirror for segment {id}");

                    var snapshotModeRecording = new Recording
                    {
                        VesselSnapshot = segments[i].GetVesselSnapshot()?.CreateCopy(),
                        GhostVisualSnapshot = segments[i].GetGhostVisualSnapshot()?.CreateCopy(),
                    };
                    bool expectGhostSidecar =
                        RecordingStore.DetermineGhostSnapshotMode(snapshotModeRecording) == GhostSnapshotMode.Separate;
                    Assert.Equal(expectGhostSidecar, File.Exists(Path.Combine(recDir, $"{id}_ghost.craft")));
                    Assert.Equal(expectGhostSidecar, File.Exists(Path.Combine(recDir, $"{id}_ghost.craft.txt")));
                }
            }
            finally
            {
                Directory.Delete(tempDir, true);
            }
        }

        [Fact]
        public void ScenarioWriter_PurgeRecordingSidecars_RemovesStaleFilesFromPreviousInject()
        {
            // Simulates the InjectAllRecordings re-run scenario: an earlier inject
            // with GUID A writes sidecars to Parsek/Recordings/, then a later
            // inject with a DIFFERENT GUID B runs. Without the purge, A's sidecars
            // would linger on disk as orphans that KSP's load-time orphan sweep
            // later deletes — causing the "showcases disappeared" playtest symptom.
            string tempDir = Path.Combine(
                Path.GetTempPath(), "parsek_purge_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);

            // Minimal stock save skeleton with a FLIGHTSTATE anchor
            // (ScenarioWriter.InjectIntoSave inserts the ParsekScenario before it).
            string fakeSave =
                "GAME\n{\n" +
                "\tFLIGHTSTATE\n\t{\n\t\tversion = 1.12.5\n\t}\n" +
                "}\n";

            try
            {
                string savePath = Path.Combine(tempDir, "persistent.sfs");
                string tempPath = savePath + ".tmp";
                File.WriteAllText(savePath, fakeSave);

                string recDir = Path.Combine(tempDir, "Parsek", "Recordings");

                // --- First inject with fixed GUID A -----------------------------
                string guidA = "aaaaaaaa00000000aaaaaaaa00000000";
                var writerA = new ScenarioWriter().WithV3Format();
                writerA.AddRecordingAsTree(FleaFlight().WithRecordingId(guidA));
                writerA.InjectIntoSaveFile(savePath, tempPath);
                File.Copy(tempPath, savePath, overwrite: true);
                File.Delete(tempPath);

                // Sanity: A's sidecars landed on disk
                Assert.True(File.Exists(Path.Combine(recDir, $"{guidA}.prec")),
                    "First inject should have written GUID A .prec");

                // --- Second inject with a DIFFERENT GUID B (no purge) -----------
                // Without PurgeRecordingSidecars, A's files survive on disk.
                string guidB = "bbbbbbbb11111111bbbbbbbb11111111";
                var writerB = new ScenarioWriter().WithV3Format();
                writerB.AddRecordingAsTree(FleaFlight().WithRecordingId(guidB));
                writerB.InjectIntoSaveFile(savePath, tempPath);

                // Guard: after plain re-inject, both A and B sidecars coexist ⇒
                // this is the bug. We rely on this to prove the purge is needed.
                Assert.True(File.Exists(Path.Combine(recDir, $"{guidA}.prec")),
                    "A's stale .prec should still be on disk before purge (the bug)");
                Assert.True(File.Exists(Path.Combine(recDir, $"{guidB}.prec")),
                    "B's fresh .prec should have been written");

                // --- Now purge and re-run the second inject --------------------
                File.Copy(tempPath, savePath, overwrite: true);
                File.Delete(tempPath);
                writerB.PurgeRecordingSidecars(tempDir);
                writerB.InjectIntoSaveFile(savePath, tempPath);
                File.Copy(tempPath, savePath, overwrite: true);
                File.Delete(tempPath);

                // Only B's sidecars should remain. No .prec or snapshot files
                // whose filename starts with GUID A (which would have been swept
                // by KSP's orphan cleanup on next load).
                string[] allFiles = Directory.GetFiles(recDir);
                int aFiles = 0;
                int bFiles = 0;
                for (int i = 0; i < allFiles.Length; i++)
                {
                    string name = Path.GetFileName(allFiles[i]);
                    if (name.StartsWith(guidA, StringComparison.Ordinal)) aFiles++;
                    if (name.StartsWith(guidB, StringComparison.Ordinal)) bFiles++;
                }
                Assert.Equal(0, aFiles);
                Assert.True(bFiles > 0,
                    $"Expected B's sidecar files to be present after purge+reinject, found {bFiles} in {recDir}");

                // The only .prec file is B's.
                string[] precFiles = Directory.GetFiles(recDir, "*.prec");
                Assert.Single(precFiles);
                Assert.Equal($"{guidB}.prec", Path.GetFileName(precFiles[0]));
            }
            finally
            {
                if (Directory.Exists(tempDir))
                    Directory.Delete(tempDir, recursive: true);
            }
        }

        [Fact]
        public void ScenarioWriter_PurgeRecordingSidecars_IsNoOpOnMissingDirectory()
        {
            // PurgeRecordingSidecars should silently succeed when the target
            // Parsek/Recordings directory does not exist (fresh save dir).
            string tempDir = Path.Combine(
                Path.GetTempPath(), "parsek_purge_missing_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                var writer = new ScenarioWriter();
                writer.PurgeRecordingSidecars(tempDir);
                Assert.False(Directory.Exists(Path.Combine(tempDir, "Parsek", "Recordings")));
            }
            finally
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }

        [Fact]
        public void InjectAllRecordings_RefusesWhenKspLogLocked_NoOps()
        {
            string tempDir = Path.Combine(
                Path.GetTempPath(), "parsek_purge_locked_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            string saveDir = Path.Combine(tempDir, "test-save");
            string recordingsDir = Path.Combine(saveDir, "Parsek", "Recordings");
            Directory.CreateDirectory(recordingsDir);
            string sentinelPath = Path.Combine(recordingsDir, "sentinel.prec");
            File.WriteAllText(sentinelPath, "keep me");

            string kspLogPath = Path.Combine(tempDir, "KSP.log");
            File.WriteAllText(kspLogPath, "locked");

            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            var logLines = new List<string>();
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);

            try
            {
                using (File.Open(kspLogPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    var writer = new ScenarioWriter();
                    bool purged = writer.TryPurgeRecordingSidecarsForInject(
                        saveDir,
                        kspLogPath,
                        out string refusalMessage);

                    Assert.False(purged);
                    Assert.False(string.IsNullOrWhiteSpace(refusalMessage));
                    Assert.True(Directory.Exists(recordingsDir));
                    Assert.True(File.Exists(sentinelPath));
                    Assert.Contains("refused to purge", refusalMessage);
                    Assert.Contains("Close KSP", refusalMessage);
                    Assert.Contains(logLines, line =>
                        line.Contains("[Parsek][ERROR][SyntheticInjector]") &&
                        line.Contains("refused to purge") &&
                        line.Contains("KSP.log"));
                }
            }
            finally
            {
                ParsekLog.ResetTestOverrides();
                ParsekLog.SuppressLogging = true;
                if (Directory.Exists(tempDir))
                    Directory.Delete(tempDir, recursive: true);
            }
        }

        [Fact]
        public void InjectAllRecordings_RefusesWhenKspLogLocked_EvenWithoutPurgeTarget()
        {
            string tempDir = Path.Combine(
                Path.GetTempPath(), "parsek_guard_only_locked_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            string kspLogPath = Path.Combine(tempDir, "KSP.log");
            File.WriteAllText(kspLogPath, "locked");

            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            var logLines = new List<string>();
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);

            try
            {
                using (File.Open(kspLogPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    var writer = new ScenarioWriter();
                    bool allowed = writer.TryPurgeRecordingSidecarsForInject(
                        saveDir: null,
                        kspLogPath: kspLogPath,
                        out string refusalMessage);

                    Assert.False(allowed);
                    Assert.Contains("(purge skipped)", refusalMessage);
                    Assert.Contains(logLines, line =>
                        line.Contains("[Parsek][ERROR][SyntheticInjector]") &&
                        line.Contains("(purge skipped)") &&
                        line.Contains("KSP.log"));
                }
            }
            finally
            {
                ParsekLog.ResetTestOverrides();
                ParsekLog.SuppressLogging = true;
                if (Directory.Exists(tempDir))
                    Directory.Delete(tempDir, recursive: true);
            }
        }

        [Fact]
        public void RecordingPaths_ValidateRecordingId_RejectsInvalidIds()
        {
            Assert.False(RecordingPaths.ValidateRecordingId(null, RecordingIdValidationLogContext.Test));
            Assert.False(RecordingPaths.ValidateRecordingId("", RecordingIdValidationLogContext.Test));
            Assert.False(RecordingPaths.ValidateRecordingId("abc/def", RecordingIdValidationLogContext.Test));
            Assert.False(RecordingPaths.ValidateRecordingId("abc\\def", RecordingIdValidationLogContext.Test));
            Assert.False(RecordingPaths.ValidateRecordingId("abc..def", RecordingIdValidationLogContext.Test));
            Assert.True(RecordingPaths.ValidateRecordingId("abc123def"));
            Assert.True(RecordingPaths.ValidateRecordingId("a1b2c3d4e5f6"));
        }

        [Fact]
        public void RecordingPaths_BuildPaths_CorrectFormat()
        {
            string id = "testid123";
            Assert.Contains("testid123.prec", RecordingPaths.BuildTrajectoryRelativePath(id));
            Assert.Contains("testid123_vessel.craft", RecordingPaths.BuildVesselSnapshotRelativePath(id));
            Assert.Contains("testid123_ghost.craft", RecordingPaths.BuildGhostSnapshotRelativePath(id));
            Assert.Contains("testid123.prec.txt", RecordingPaths.BuildReadableTrajectoryMirrorRelativePath(id));
            Assert.Contains("testid123_vessel.craft.txt", RecordingPaths.BuildReadableVesselSnapshotMirrorRelativePath(id));
            Assert.Contains("testid123_ghost.craft.txt", RecordingPaths.BuildReadableGhostSnapshotMirrorRelativePath(id));

            // All paths are under Parsek/Recordings/
            Assert.StartsWith("Parsek", RecordingPaths.BuildTrajectoryRelativePath(id));
            Assert.StartsWith("Parsek", RecordingPaths.BuildVesselSnapshotRelativePath(id));
            Assert.StartsWith("Parsek", RecordingPaths.BuildGhostSnapshotRelativePath(id));
            Assert.StartsWith("Parsek", RecordingPaths.BuildReadableTrajectoryMirrorRelativePath(id));
            Assert.StartsWith("Parsek", RecordingPaths.BuildReadableVesselSnapshotMirrorRelativePath(id));
            Assert.StartsWith("Parsek", RecordingPaths.BuildReadableGhostSnapshotMirrorRelativePath(id));
        }

        #endregion

        #region Save File Injection (manual — requires save file)

        /// <summary>
        /// Read UT from FLIGHTSTATE in a save file.
        /// </summary>
        private static double ReadUTFromSave(string savePath)
        {
            string content = File.ReadAllText(savePath);
            var match = Regex.Match(content, @"FLIGHTSTATE\s*\{[^}]*?UT\s*=\s*([0-9.eE+\-]+)",
                RegexOptions.Singleline);
            if (match.Success &&
                double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double ut))
                return ut;
            return 0;
        }

        private static bool IsTruthy(string value)
        {
            if (string.IsNullOrEmpty(value)) return false;
            return value == "1" || value.Equals("true", System.StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Remove ALL VESSEL blocks from FLIGHTSTATE. A fresh pad vessel is
        /// injected afterward by EnsurePadVesselInFlightState so that KSP can
        /// enter flight without bouncing to Space Center (which would load
        /// persistent.sfs with stale vessels).
        /// </summary>
        private static string RemoveVesselBlocksFromFlightState(string content)
        {
            var lines = content.Split(new[] { "\r\n", "\n" }, System.StringSplitOptions.None);
            var result = new System.Collections.Generic.List<string>(lines.Length);
            bool inFlightState = false;
            int flightStateDepth = 0;
            int i = 0;
            while (i < lines.Length)
            {
                string trimmed = lines[i].Trim();

                // Track FLIGHTSTATE scope
                if (!inFlightState && trimmed == "FLIGHTSTATE")
                {
                    inFlightState = true;
                    flightStateDepth = 0;
                    result.Add(lines[i]);
                    i++;
                    continue;
                }

                if (inFlightState)
                {
                    if (trimmed == "{") flightStateDepth++;
                    else if (trimmed == "}")
                    {
                        flightStateDepth--;
                        if (flightStateDepth <= 0) inFlightState = false;
                    }

                    // Strip all VESSEL blocks at depth 1 (direct children of FLIGHTSTATE)
                    if (flightStateDepth == 1 && trimmed == "VESSEL")
                    {
                        i++;
                        int depth = 0;
                        while (i < lines.Length)
                        {
                            string t = lines[i].Trim();
                            if (t == "{") depth++;
                            else if (t == "}")
                            {
                                depth--;
                                if (depth <= 0) { i++; break; }
                            }
                            i++;
                        }
                        continue;
                    }
                }

                result.Add(lines[i]);
                i++;
            }

            string sep = content.Contains("\r\n") ? "\r\n" : "\n";
            return string.Join(sep, result);
        }

        /// <summary>
        /// Ensure FLIGHTSTATE has at least one VESSEL (a minimal pad rocket).
        /// KSP bounces to Space Center if FLIGHTSTATE has no vessels, which
        /// loads persistent.sfs — carrying over stale spawned vessels.
        /// </summary>
        /// <summary>
        /// Reset activeVessel to -1 so KSP loads directly into Space Center
        /// instead of attempting Flight (which bounces to Space Center via
        /// persistent.sfs when FLIGHTSTATE has no vessels).
        /// </summary>
        private static string ResetActiveVessel(string content)
        {
            var m = Regex.Match(content,
                @"(activeVessel\s*=\s*)(-?\d+)",
                RegexOptions.None);
            if (!m.Success) return content;
            return content.Substring(0, m.Groups[2].Index)
                 + "-1"
                 + content.Substring(m.Groups[2].Index + m.Groups[2].Length);
        }

        /// <summary>
        /// Upgrade all KSC facilities to max level (lvl = 2) so that
        /// Tracking Station supports vessel switching from map view.
        /// </summary>
        private static string MaxFacilityLevels(string content)
        {
            var lines = content.Split(new[] { "\r\n", "\n" }, System.StringSplitOptions.None);
            bool inFacilities = false;
            int facDepth = 0;
            for (int i = 0; i < lines.Length; i++)
            {
                string trimmed = lines[i].Trim();
                if (!inFacilities && trimmed == "name = ScenarioUpgradeableFacilities")
                {
                    inFacilities = true;
                    continue;
                }
                if (inFacilities)
                {
                    if (trimmed == "{") facDepth++;
                    else if (trimmed == "}")
                    {
                        facDepth--;
                        if (facDepth < 0) inFacilities = false;
                    }
                    else if (trimmed.StartsWith("lvl = ", System.StringComparison.Ordinal))
                    {
                        lines[i] = lines[i].Replace(trimmed, "lvl = 2");
                    }
                }
            }
            string sep = content.Contains("\r\n") ? "\r\n" : "\n";
            return string.Join(sep, lines);
        }

        /// <summary>
        /// Reads all RDNode IDs from the ModuleManager-merged tech tree file
        /// and ensures every one is present as state=Available in the save's
        /// ResearchAndDevelopment scenario.  Handles Community Tech Tree and
        /// similar mods that add nodes the stock save doesn't contain.
        /// </summary>
        private static string UnlockAllTechNodes(string content, string kspRoot)
        {
            string techTreePath = Path.Combine(kspRoot, "GameData", "ModuleManager.TechTree");
            if (!File.Exists(techTreePath))
                return content; // no merged tech tree — nothing to do

            // Parse all RDNode IDs and costs from the merged tech tree
            var techTreeLines = File.ReadAllLines(techTreePath);
            var allNodes = new System.Collections.Generic.Dictionary<string, int>(); // id → cost
            bool inRDNode = false;
            string currentId = null;
            int currentCost = 0;
            for (int i = 0; i < techTreeLines.Length; i++)
            {
                string t = techTreeLines[i].Trim();
                if (t == "RDNode" || t == "RDNode {" || t.StartsWith("RDNode"))
                {
                    inRDNode = true;
                    currentId = null;
                    currentCost = 0;
                    if (t == "RDNode {") { } // inline brace — handled below
                    continue;
                }
                if (!inRDNode) continue;
                if (t == "{") continue;
                if (t == "}" || t.StartsWith("}"))
                {
                    if (currentId != null && !allNodes.ContainsKey(currentId))
                        allNodes[currentId] = currentCost;
                    inRDNode = false;
                    continue;
                }
                if (t.StartsWith("id = ", System.StringComparison.Ordinal))
                    currentId = t.Substring(5).Trim();
                else if (t.StartsWith("cost = ", System.StringComparison.Ordinal))
                    int.TryParse(t.Substring(7).Trim(), out currentCost);
            }

            if (allNodes.Count == 0)
                return content;

            // Find existing Tech node IDs in the ResearchAndDevelopment scenario
            var saveLines = content.Split(new[] { "\r\n", "\n" }, System.StringSplitOptions.None);
            var existingIds = new System.Collections.Generic.HashSet<string>();
            bool inRD = false;
            int rdDepth = 0;
            int rdCloseLineIdx = -1; // line index of the closing brace of R&D scenario

            for (int i = 0; i < saveLines.Length; i++)
            {
                string t = saveLines[i].Trim();
                if (!inRD && t == "name = ResearchAndDevelopment")
                {
                    inRD = true;
                    continue;
                }
                if (inRD)
                {
                    if (t == "{") rdDepth++;
                    else if (t == "}")
                    {
                        rdDepth--;
                        if (rdDepth < 0)
                        {
                            rdCloseLineIdx = i;
                            inRD = false;
                        }
                    }
                    else if (t.StartsWith("id = ", System.StringComparison.Ordinal) && rdDepth > 0)
                    {
                        existingIds.Add(t.Substring(5).Trim());
                    }
                }
            }

            if (rdCloseLineIdx < 0)
                return content; // no ResearchAndDevelopment scenario found

            // Build Tech blocks for missing nodes
            var missing = new System.Text.StringBuilder();
            foreach (var kv in allNodes)
            {
                if (existingIds.Contains(kv.Key))
                    continue;
                missing.AppendLine("\t\tTech");
                missing.AppendLine("\t\t{");
                missing.AppendLine($"\t\t\tid = {kv.Key}");
                missing.AppendLine("\t\t\tstate = Available");
                missing.AppendLine($"\t\t\tcost = {kv.Value}");
                missing.AppendLine("\t\t}");
            }

            if (missing.Length == 0)
                return content; // all nodes already present

            // Insert before the closing brace of the R&D scenario
            var result = new System.Collections.Generic.List<string>(saveLines.Length + 100);
            for (int i = 0; i < saveLines.Length; i++)
            {
                if (i == rdCloseLineIdx)
                {
                    // Insert missing Tech blocks before the closing brace
                    string[] newLines = missing.ToString().Split(
                        new[] { "\r\n", "\n" }, System.StringSplitOptions.RemoveEmptyEntries);
                    for (int j = 0; j < newLines.Length; j++)
                        result.Add(newLines[j]);
                }
                result.Add(saveLines[i]);
            }

            string sep = content.Contains("\r\n") ? "\r\n" : "\n";
            return string.Join(sep, result);
        }

        private static string RemoveSpawnedPidLines(string content)
        {
            var lines = content.Split(new[] { "\r\n", "\n" }, System.StringSplitOptions.None);
            var result = new System.Collections.Generic.List<string>(lines.Length);
            for (int i = 0; i < lines.Length; i++)
            {
                if (lines[i].TrimStart().StartsWith("spawnedPid =",
                    System.StringComparison.Ordinal))
                    continue;
                result.Add(lines[i]);
            }
            string sep = content.Contains("\r\n") ? "\r\n" : "\n";
            return string.Join(sep, result);
        }

        /// <summary>
        /// Remove non-veteran Crew kerbals from inside the ROSTER node only.
        /// These are stale entries left by previous test runs (synthetic crew
        /// like Tedorf, hired replacements like Jedeny). Keeps stock veterans
        /// (Jeb/Bill/Bob/Val) and all Applicants. KERBAL blocks outside ROSTER
        /// (e.g. inside VESSEL crew manifests) are left intact.
        /// </summary>
        private static string RemoveNonVeteranCrewFromRoster(string content)
        {
            var lines = content.Split(new[] { "\r\n", "\n" }, System.StringSplitOptions.None);
            var result = new System.Collections.Generic.List<string>(lines.Length);
            bool inRoster = false;
            int rosterDepth = 0;
            int i = 0;
            while (i < lines.Length)
            {
                string trimmed = lines[i].Trim();

                // Track ROSTER scope
                if (!inRoster && trimmed == "ROSTER")
                {
                    inRoster = true;
                    rosterDepth = 0;
                    result.Add(lines[i]);
                    i++;
                    continue;
                }

                if (inRoster)
                {
                    if (trimmed == "{") rosterDepth++;
                    else if (trimmed == "}")
                    {
                        rosterDepth--;
                        if (rosterDepth <= 0) inRoster = false;
                    }

                    // Only inspect KERBAL blocks at depth 1 (direct children of ROSTER)
                    if (rosterDepth == 1 && trimmed == "KERBAL")
                    {
                        var block = new System.Collections.Generic.List<string>();
                        block.Add(lines[i]);
                        i++;
                        int depth = 0;
                        while (i < lines.Length)
                        {
                            block.Add(lines[i]);
                            string t = lines[i].Trim();
                            if (t == "{") depth++;
                            else if (t == "}")
                            {
                                depth--;
                                if (depth <= 0) { i++; break; }
                            }
                            i++;
                        }

                        bool isCrew = false;
                        bool isVeteran = false;
                        foreach (string line in block)
                        {
                            string l = line.Trim();
                            if (l == "type = Crew") isCrew = true;
                            if (l == "veteran = True") isVeteran = true;
                        }

                        if (isCrew && !isVeteran)
                            continue; // drop this block

                        result.AddRange(block);
                        continue;
                    }
                }

                result.Add(lines[i]);
                i++;
            }

            string sep = content.Contains("\r\n") ? "\r\n" : "\n";
            return string.Join(sep, result);
        }

        /// <summary>
        /// Reset veteran crew status from Missing/Assigned to Available.
        /// After --clean-start removes vessels, crew may be orphaned as Missing
        /// or still Assigned to a non-existent vessel. This fixes them.
        /// </summary>
        private static string ResetVeteranCrewStatus(string content)
        {
            // Fix state = Missing/Assigned → Available for veteran crew
            // Only affects lines inside KERBAL blocks that are veteran = True
            var lines = content.Split(new[] { "\r\n", "\n" }, System.StringSplitOptions.None);
            bool inRoster = false;
            int rosterDepth = 0;
            bool inKerbal = false;
            int kerbalStart = -1;
            int kerbalDepth = 0;

            for (int i = 0; i < lines.Length; i++)
            {
                string trimmed = lines[i].Trim();

                if (!inRoster && trimmed == "ROSTER")
                {
                    inRoster = true;
                    rosterDepth = 0;
                    continue;
                }

                if (inRoster)
                {
                    if (trimmed == "{") rosterDepth++;
                    else if (trimmed == "}")
                    {
                        rosterDepth--;
                        if (rosterDepth <= 0) inRoster = false;
                    }

                    if (rosterDepth == 1 && trimmed == "KERBAL")
                    {
                        inKerbal = true;
                        kerbalStart = i;
                        kerbalDepth = 0;
                        continue;
                    }

                    if (inKerbal)
                    {
                        if (trimmed == "{") kerbalDepth++;
                        else if (trimmed == "}")
                        {
                            kerbalDepth--;
                            if (kerbalDepth <= 0)
                            {
                                inKerbal = false;
                                // Check if this was a veteran crew and fix state.
                                // Only check top-level values (depth 1) — sub-nodes
                                // like EVACHUTE/INVENTORY have their own "state = 0".
                                bool isVeteran = false;
                                int stateLine = -1;
                                int todLine = -1;
                                int idxLine = -1;
                                int scanDepth = 0;
                                for (int j = kerbalStart; j <= i; j++)
                                {
                                    string l = lines[j].Trim();
                                    if (l == "{") scanDepth++;
                                    else if (l == "}") scanDepth--;

                                    if (scanDepth != 1) continue;
                                    if (l == "veteran = True") isVeteran = true;
                                    if (l.StartsWith("state = ", System.StringComparison.Ordinal)) stateLine = j;
                                    if (l.StartsWith("ToD = ", System.StringComparison.Ordinal)) todLine = j;
                                    if (l.StartsWith("idx = ", System.StringComparison.Ordinal)) idxLine = j;
                                }

                                if (isVeteran && stateLine >= 0)
                                {
                                    string stateVal = lines[stateLine].Trim();
                                    if (stateVal == "state = Missing" || stateVal == "state = Assigned")
                                    {
                                        string indent = lines[stateLine].Substring(0, lines[stateLine].Length - lines[stateLine].TrimStart().Length);
                                        lines[stateLine] = indent + "state = Available";
                                        if (todLine >= 0)
                                        {
                                            indent = lines[todLine].Substring(0, lines[todLine].Length - lines[todLine].TrimStart().Length);
                                            lines[todLine] = indent + "ToD = 0";
                                        }
                                        if (idxLine >= 0)
                                        {
                                            indent = lines[idxLine].Substring(0, lines[idxLine].Length - lines[idxLine].TrimStart().Length);
                                            lines[idxLine] = indent + "idx = -1";
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }

            string sep = content.Contains("\r\n") ? "\r\n" : "\n";
            return string.Join(sep, lines);
        }

        /// <summary>
        /// Set the FLIGHTSTATE UT to a fixed value.
        /// KSC sunrise ≈ phase 15180, noon ≈ 20580, sunset ≈ 25980 (mod 21600).
        /// Default 17000 → mid-morning at KSC, good lighting.
        /// Using a fixed value makes injection idempotent across re-runs.
        /// </summary>
        private static string SetUT(string content, double newUT)
        {
            var m = Regex.Match(content,
                @"(FLIGHTSTATE\s*\{[^}]*?UT\s*=\s*)([0-9.eE+\-]+)",
                RegexOptions.Singleline);
            if (!m.Success) return content;

            return content.Substring(0, m.Groups[2].Index)
                 + newUT.ToString("R", CultureInfo.InvariantCulture)
                 + content.Substring(m.Groups[2].Index + m.Groups[2].Length);
        }

        // KSC mid-morning UT (phase 17000 mod 21600 ≈ 30 min after sunrise)
        private const double KscMorningUT = 17000;

        private static void CleanSaveStart(string savePath)
        {
            if (!File.Exists(savePath))
                return;

            string content = File.ReadAllText(savePath);
            content = RemoveVesselBlocksFromFlightState(content);
            content = ResetActiveVessel(content);
            content = RemoveSpawnedPidLines(content);
            content = RemoveNonVeteranCrewFromRoster(content);
            content = ResetVeteranCrewStatus(content);
            content = SetUT(content, KscMorningUT);
            content = MaxFacilityLevels(content);
            string kspRoot = ResolveKspRoot();
            content = UnlockAllTechNodes(content, kspRoot);
            File.WriteAllText(savePath, content);

            // Stale sidecar file cleanup lives in InjectAllRecordings via the
            // guarded ScenarioWriter purge helper so the delete happens exactly
            // once per save directory, even when CleanSaveStart is invoked for
            // both persistent.sfs and the test-target save that share a dir.
        }

        [Trait("Category", "Manual")]
        [Fact]
        public void InjectAllRecordings()
        {
            string saveName = System.Environment.GetEnvironmentVariable("PARSEK_INJECT_SAVE_NAME")
                ?? "test career";
            string targetSave = System.Environment.GetEnvironmentVariable("PARSEK_INJECT_TARGET_SAVE")
                ?? "1.sfs";
            string kspRoot = ResolveKspRoot();
            // Default to clean start (strip stale vessels from FLIGHTSTATE).
            // Set PARSEK_INJECT_CLEAN_START=0 to keep existing vessels.
            string cleanEnv = System.Environment.GetEnvironmentVariable("PARSEK_INJECT_CLEAN_START");
            bool cleanStart = cleanEnv == null || IsTruthy(cleanEnv);

            string saveDir = Path.Combine(kspRoot, "saves", saveName);

            // Inject into both persistent.sfs and the target save — KSP loads
            // persistent first (sets initialLoadDone), so it must have the recordings too.
            string[] targets = { "persistent.sfs", targetSave };

            string targetPath = Path.Combine(saveDir, targetSave);
            if (!File.Exists(targetPath))
                return;

            // Refuse the entire inject up front when the target KSP install
            // looks live. The purge helper probes KSP.log with an exclusive
            // open; we reuse it even when cleanStart=false so save writes and
            // sidecar rewrites never race a running session.
            var purgeWriter = new ScenarioWriter();
            if (!purgeWriter.TryPurgeRecordingSidecarsForInject(
                    cleanStart ? saveDir : null,
                    Path.Combine(kspRoot, "KSP.log"),
                    out string refusalMessage))
                throw new Xunit.Sdk.SkipException(refusalMessage);

            // Clean BOTH saves first so they share the same daytime UT,
            // then read baseUT from the (now updated) target save.
            if (cleanStart)
            {
                foreach (string file in targets)
                {
                    string sp = Path.Combine(saveDir, file);
                    if (File.Exists(sp))
                        CleanSaveStart(sp);
                }
            }

            double baseUT = ReadUTFromSave(targetPath);

            var writer = new ScenarioWriter().WithV3Format();
            writer.AddRecordingAsTree(PadWalk(baseUT).WithLoopPlayback()
                .WithRecordingGroup("Synthetic"));

            // Pad-launch recordings — with rewind saves
            writer.AddRecordingAsTree(KscHopper(baseUT).WithLoopPlayback()
                .WithRewindSave("parsek_rw_hop001").WithRecordingGroup("Synthetic"));
            writer.AddRecordingAsTree(FleaFlight(baseUT).WithLoopPlayback()
                .WithRewindSave("parsek_rw_flea01").WithRecordingGroup("Synthetic"));
            writer.AddRecordingAsTree(SuborbitalArc(baseUT).WithLoopPlayback()
                .WithRewindSave("parsek_rw_subo01").WithRecordingGroup("Synthetic"));
            writer.AddRecordingAsTree(KscPadDestroyed(baseUT).WithLoopPlayback()
                .WithRewindSave("parsek_rw_dest01").WithRecordingGroup("Synthetic"));
            writer.AddRecordingAsTree(Orbit1(baseUT).WithLoopPlayback()
                .WithRewindSave("parsek_rw_orb001").WithRecordingGroup("Synthetic"));
            writer.AddRecordingAsTree(CloseSpawnConflict(baseUT).WithLoopPlayback()
                .WithRecordingGroup("Synthetic"));
            writer.AddRecordingAsTree(IslandProbe(baseUT).WithLoopPlayback()
                .WithRewindSave("parsek_rw_isle01").WithRecordingGroup("Synthetic"));
            writer.AddRecordingAsTree(PipelineOutlierKraken(baseUT).WithLoopPlayback()
                .WithRecordingGroup("Synthetic"));

            var lightShowcases = LightShowcaseRecordings(baseUT);
            for (int i = 0; i < lightShowcases.Length; i++)
                writer.AddRecordingAsTree(lightShowcases[i]);

            var deployableShowcases = DeployableShowcaseRecordings(baseUT);
            for (int i = 0; i < deployableShowcases.Length; i++)
                writer.AddRecordingAsTree(deployableShowcases[i]);

            var gearShowcases = GearShowcaseRecordings(baseUT);
            for (int i = 0; i < gearShowcases.Length; i++)
                writer.AddRecordingAsTree(gearShowcases[i]);

            var cargoBayShowcases = CargoBayShowcaseRecordings(baseUT);
            for (int i = 0; i < cargoBayShowcases.Length; i++)
                writer.AddRecordingAsTree(cargoBayShowcases[i]);

            var engineShowcases = EngineShowcaseRecordings(baseUT);
            for (int i = 0; i < engineShowcases.Length; i++)
                writer.AddRecordingAsTree(engineShowcases[i]);

            var ladderShowcases = LadderShowcaseRecordings(baseUT);
            for (int i = 0; i < ladderShowcases.Length; i++)
                writer.AddRecordingAsTree(ladderShowcases[i]);

            var rcsShowcases = RcsShowcaseRecordings(baseUT);
            for (int i = 0; i < rcsShowcases.Length; i++)
                writer.AddRecordingAsTree(rcsShowcases[i]);

            var fairingShowcases = FairingShowcaseRecordings(baseUT);
            for (int i = 0; i < fairingShowcases.Length; i++)
                writer.AddRecordingAsTree(fairingShowcases[i]);

            var radiatorShowcases = RadiatorShowcaseRecordings(baseUT);
            for (int i = 0; i < radiatorShowcases.Length; i++)
                writer.AddRecordingAsTree(radiatorShowcases[i]);

            var drillShowcases = DrillShowcaseRecordings(baseUT);
            for (int i = 0; i < drillShowcases.Length; i++)
                writer.AddRecordingAsTree(drillShowcases[i]);

            var deployedScienceShowcases = DeployedScienceShowcaseRecordings(baseUT);
            for (int i = 0; i < deployedScienceShowcases.Length; i++)
                writer.AddRecordingAsTree(deployedScienceShowcases[i]);
            var animationGroupShowcases = AnimationGroupShowcaseRecordings(baseUT);
            for (int i = 0; i < animationGroupShowcases.Length; i++)
                writer.AddRecordingAsTree(animationGroupShowcases[i]);
            var parachuteShowcases = ParachuteShowcaseRecordings(baseUT);
            for (int i = 0; i < parachuteShowcases.Length; i++)
                writer.AddRecordingAsTree(parachuteShowcases[i]);
            var specialDeployAnimationShowcases = SpecialDeployAnimationShowcaseRecordings(baseUT);
            for (int i = 0; i < specialDeployAnimationShowcases.Length; i++)
                writer.AddRecordingAsTree(specialDeployAnimationShowcases[i]);
            var jettisonShowcases = JettisonShowcaseRecordings(baseUT);
            for (int i = 0; i < jettisonShowcases.Length; i++)
                writer.AddRecordingAsTree(jettisonShowcases[i]);
            var roboticsShowcases = RoboticsShowcaseRecordings(baseUT);
            for (int i = 0; i < roboticsShowcases.Length; i++)
                writer.AddRecordingAsTree(roboticsShowcases[i]);
            var aeroSurfaceShowcases = AeroSurfaceShowcaseRecordings(baseUT);
            for (int i = 0; i < aeroSurfaceShowcases.Length; i++)
                writer.AddRecordingAsTree(aeroSurfaceShowcases[i]);
            var robotArmScannerShowcases = RobotArmScannerShowcaseRecordings(baseUT);
            for (int i = 0; i < robotArmScannerShowcases.Length; i++)
                writer.AddRecordingAsTree(robotArmScannerShowcases[i]);
            var controlSurfaceShowcases = ControlSurfaceShowcaseRecordings(baseUT);
            for (int i = 0; i < controlSurfaceShowcases.Length; i++)
                writer.AddRecordingAsTree(controlSurfaceShowcases[i]);
            var wheelDynamicsShowcases = WheelDynamicsShowcaseRecordings(baseUT);
            for (int i = 0; i < wheelDynamicsShowcases.Length; i++)
                writer.AddRecordingAsTree(wheelDynamicsShowcases[i]);
            var animateHeatShowcases = AnimateHeatShowcaseRecordings(baseUT);
            for (int i = 0; i < animateHeatShowcases.Length; i++)
                writer.AddRecordingAsTree(animateHeatShowcases[i]);
            var colorChangerShowcases = ColorChangerShowcaseRecordings(baseUT);
            for (int i = 0; i < colorChangerShowcases.Length; i++)
                writer.AddRecordingAsTree(colorChangerShowcases[i]);
            writer.AddRecordingAsTree(InventoryPlacementShowcaseRecording(baseUT));
            writer.AddRecordingAsTree(FlagPlantShowcaseRecording(baseUT));

            var chainSegments = EvaBoardChain(baseUT);
            chainSegments[0].WithRewindSave("parsek_rw_evab01");
            for (int i = 0; i < chainSegments.Length; i++)
                chainSegments[i].WithLoopPlayback().WithRecordingGroup("Synthetic");
            writer.AddRecordingsAsTree(chainSegments);

            var walkChainSegments = EvaWalkChain(baseUT);
            walkChainSegments[0].WithRewindSave("parsek_rw_walk01");
            for (int i = 0; i < walkChainSegments.Length; i++)
                walkChainSegments[i].WithLoopPlayback().WithRecordingGroup("Synthetic");
            writer.AddRecordingsAsTree(walkChainSegments);

            var atmoChainSegments = KerbinAscentChain(baseUT);
            atmoChainSegments[0].WithRewindSave("parsek_rw_atmo01");
            for (int i = 0; i < atmoChainSegments.Length; i++)
                atmoChainSegments[i].WithLoopPlayback().WithRecordingGroup("Synthetic");
            writer.AddRecordingsAsTree(atmoChainSegments);

            var munTransferSegments = KerbinMunTransfer(baseUT);
            munTransferSegments[0].WithRewindSave("parsek_rw_mun001");
            for (int i = 0; i < munTransferSegments.Length; i++)
                munTransferSegments[i].WithLoopPlayback().WithRecordingGroup("Synthetic");
            writer.AddRecordingsAsTree(munTransferSegments);

            writer.AddRecordingAsTree(TruncatedPlaneCruise(baseUT).WithLoopPlayback().WithRecordingGroup("Synthetic"));
            writer.AddRecordingAsTree(TruncatedSuborbitalRecording(baseUT).WithLoopPlayback().WithRecordingGroup("Synthetic"));
            writer.AddRecordingAsTree(TruncatedHyperbolicRecording(baseUT).WithLoopPlayback().WithRecordingGroup("Synthetic"));
            writer.AddRecordingAsTree(TruncatedMunFlybyRecording(baseUT).WithLoopPlayback().WithRecordingGroup("Synthetic"));
            writer.AddRecordingAsTree(TruncatedMunImpactRecording(baseUT).WithLoopPlayback().WithRecordingGroup("Synthetic"));

            writer.AddRecordingAsTree(ReentryEast(baseUT).WithLoopPlayback().WithRecordingGroup("Synthetic"));
            writer.AddRecordingAsTree(ReentryShallow(baseUT).WithLoopPlayback().WithRecordingGroup("Synthetic"));
            writer.AddRecordingAsTree(ReentrySouth(baseUT).WithLoopPlayback().WithRecordingGroup("Synthetic"));

            // Add synthetic game actions so the Actions window has visible entries
            AddSyntheticGameActions(writer, baseUT);

            // Add synthetic tree recordings (E1-E3)
            writer.AddTree(SimpleUndockTree(baseUT));
            writer.AddTree(EvaTree(baseUT));
            writer.AddTree(DestructionTree(baseUT));

            // Ghost chain test recordings (Phase 6)
            writer.AddTree(StationR0Tree(baseUT));
            writer.AddTree(StationDockingChainTree(baseUT));
            writer.AddTree(CrossTreeDockingChainTree(baseUT));
            writer.AddTree(PadRigR0Tree(baseUT));
            writer.AddTree(SpawnCollisionChainTree(baseUT));
            writer.AddTree(SurfaceBaseR0Tree(baseUT));
            writer.AddTree(SurfaceGhostChainTree(baseUT));

            // Add real recordings from the default career (if available)
            var realRecordingNodes = AddRealCareerRecordings(writer, kspRoot);

            foreach (string file in targets)
            {
                string savePath = Path.Combine(saveDir, file);
                if (!File.Exists(savePath))
                    continue;

                string tempPath = savePath + ".tmp";
                try
                {
                    writer.InjectIntoSaveFile(savePath, tempPath);

                    // Copy real recording sidecar files from frozen fixture
                    if (realRecordingNodes.Length > 0)
                    {
                        string fixtureDir = ResolveDefaultCareerFixtureDir()
                            ?? Path.Combine(kspRoot, "saves", "default");
                        CopyRealRecordingFiles(fixtureDir, saveDir, realRecordingNodes);
                    }

                    string content = File.ReadAllText(tempPath);
                    Assert.Contains("name = ParsekScenario", content);
                    Assert.Contains("vesselName = Pad Walk", content);
                    Assert.Contains("vesselName = KSC Hopper", content);
                    Assert.Contains("vesselName = Flea Flight", content);
                    Assert.Contains("vesselName = Suborbital Arc", content);
                    Assert.Contains("vesselName = KSC Pad Destroyed", content);
                    Assert.Contains("vesselName = Orbit-1", content);
                    Assert.Contains("vesselName = Close Spawn Conflict", content);
                    Assert.Contains("vesselName = Island Probe", content);
                    Assert.Contains("vesselName = Pipeline Outlier Kraken", content);
                    Assert.Contains("vesselName = Part Showcase - Lights v1", content);
                    Assert.Contains("vesselName = Part Showcase - Light - Nav v1", content);
                    Assert.Contains("vesselName = Part Showcase - Light - Strip v1", content);
                    Assert.Contains("vesselName = Part Showcase - Light - Spot v1", content);
                    Assert.Contains("vesselName = Part Showcase - Light - Ground Small v1", content);
                    Assert.Contains("vesselName = Part Showcase - Light - Ground Stand v1", content);
                    Assert.Contains("vesselName = Part Showcase - Light - Spot Mk1", content);
                    Assert.Contains("vesselName = Part Showcase - Light - Spot Mk1 v2", content);
                    Assert.Contains("vesselName = Part Showcase - Light - Spot Mk2", content);
                    Assert.Contains("vesselName = Part Showcase - Light - Spot Mk2 v2", content);
                    Assert.Contains("vesselName = Part Showcase - Solar Tracking", content);
                    Assert.Contains("vesselName = Part Showcase - Solar Large", content);
                    Assert.Contains("vesselName = Part Showcase - Solar Radial XL", content);
                    Assert.Contains("vesselName = Part Showcase - Solar OX-10C", content);
                    Assert.Contains("vesselName = Part Showcase - Solar OX-10L", content);
                    Assert.Contains("vesselName = Part Showcase - Solar 3x2 Shrouded", content);
                    Assert.Contains("vesselName = Part Showcase - Solar 1x6 Shrouded", content);
                    Assert.Contains("vesselName = Part Showcase - Solar 3x2", content);
                    Assert.Contains("vesselName = Part Showcase - Solar Flat", content);
                    Assert.Contains("vesselName = Part Showcase - Solar SP-10C", content);
                    Assert.Contains("vesselName = Part Showcase - Solar SP-10L", content);
                    Assert.Contains("vesselName = Part Showcase - Antenna Comm", content);
                    Assert.Contains("vesselName = Part Showcase - Antenna Dish", content);
                    Assert.Contains("vesselName = Part Showcase - Antenna High Gain", content);
                    Assert.Contains("vesselName = Part Showcase - Antenna HG-5", content);
                    Assert.Contains("vesselName = Part Showcase - Antenna HG-5 v2", content);
                    Assert.Contains("vesselName = Part Showcase - Antenna Medium Dish", content);
                    Assert.Contains("vesselName = Part Showcase - Radiator", content);
                    Assert.Contains("vesselName = Part Showcase - Gear Bay", content);
                    Assert.Contains("vesselName = Part Showcase - Gear Small", content);
                    Assert.Contains("vesselName = Part Showcase - Gear Medium", content);
                    Assert.Contains("vesselName = Part Showcase - Gear Large", content);
                    Assert.Contains("vesselName = Part Showcase - Landing Leg LT-1", content);
                    Assert.Contains("vesselName = Part Showcase - Landing Leg LT-2", content);
                    Assert.Contains("vesselName = Part Showcase - Landing Leg LT-05", content);
                    Assert.Contains("vesselName = Part Showcase - Service Bay", content);
                    Assert.Contains("vesselName = Part Showcase - Service Bay 2.5", content);
                    Assert.Contains("vesselName = Part Showcase - Service Module 1.8", content);
                    Assert.Contains("vesselName = Part Showcase - Service Module 2.5", content);
                    Assert.Contains("vesselName = Part Showcase - Service Module 1-0", content);
                    Assert.Contains("vesselName = Part Showcase - Cargo Mk2", content);
                    Assert.Contains("vesselName = Part Showcase - Cargo Mk2 Long", content);
                    Assert.Contains("vesselName = Part Showcase - Cargo Mk3", content);
                    Assert.Contains("vesselName = Part Showcase - Cargo Mk3 Medium", content);
                    Assert.Contains("vesselName = Part Showcase - Cargo Mk3 Long", content);
                    Assert.Contains("vesselName = Part Showcase - Cargo Mk3 Ramp", content);
                    Assert.Contains("vesselName = Part Showcase - LV-T30", content);
                    Assert.Contains("vesselName = Part Showcase - Ion", content);
                    Assert.Contains("vesselName = Part Showcase - Ladder Telescopic", content);
                    Assert.Contains("vesselName = Part Showcase - Ladder Bay", content);
                    Assert.Contains("vesselName = Part Showcase - RCS RV-105", content);
                    Assert.Contains("vesselName = Part Showcase - RCS RV-1X", content);
                    Assert.Contains("vesselName = Part Showcase - RCS Linear", content);
                    Assert.Contains("vesselName = Part Showcase - RCS Linear Port", content);
                    Assert.Contains("vesselName = Part Showcase - RCS Vernor", content);
                    Assert.Contains("vesselName = Part Showcase - Fairing Size 1", content);
                    Assert.Contains("vesselName = Part Showcase - Fairing Size 1.5", content);
                    Assert.Contains("vesselName = Part Showcase - Fairing Size 2", content);
                    Assert.Contains("vesselName = Part Showcase - Fairing Size 3", content);
                    Assert.Contains("vesselName = Part Showcase - Fairing Size 4", content);
                    Assert.Contains("vesselName = Part Showcase - Radiator Medium", content);
                    Assert.Contains("vesselName = Part Showcase - Radiator Large", content);
                    Assert.Contains("vesselName = Part Showcase - Drill Junior", content);
                    Assert.Contains("vesselName = Part Showcase - Drill-O-Matic", content);
                    Assert.Contains("vesselName = Part Showcase - Deployed Central Station", content);
                    Assert.Contains("vesselName = Part Showcase - Deployed Goo Observation", content);
                    Assert.Contains("vesselName = Part Showcase - Deployed Ion Collector", content);
                    Assert.Contains("vesselName = Part Showcase - Deployed RTG", content);
                    Assert.Contains("vesselName = Part Showcase - Deployed Sat Dish", content);
                    Assert.Contains("vesselName = Part Showcase - Deployed Seismic Sensor", content);
                    Assert.Contains("vesselName = Part Showcase - Deployed Solar Panel", content);
                    Assert.Contains("vesselName = Part Showcase - Deployed Weather Station", content);
                    Assert.Contains("vesselName = Part Showcase - Ground Anchor", content);
                    Assert.Contains("vesselName = Part Showcase - Survey Scanner", content);
                    Assert.Contains("vesselName = Part Showcase - Parachute Mk16", content);
                    Assert.Contains("vesselName = Part Showcase - Parachute Mk2-R", content);
                    Assert.Contains("vesselName = Part Showcase - Drogue Mk25", content);
                    Assert.Contains("vesselName = Part Showcase - Drogue Mk12-R", content);
                    Assert.Contains("vesselName = Part Showcase - Parachute Mk16-XL", content);
                    Assert.Contains("vesselName = Part Showcase - Rover Wheel M1-F", content);
                    Assert.Contains("vesselName = Part Showcase - Goo Experiment", content);
                    Assert.Contains("vesselName = Part Showcase - Science Jr", content);
                    Assert.Contains("vesselName = Part Showcase - Magnetometer Boom", content);
                    Assert.Contains("vesselName = Part Showcase - Inflatable Heat Shield", content);
                    Assert.Contains("vesselName = Part Showcase - Inflatable Airlock", content);
                    Assert.Contains("vesselName = Part Showcase - Docking Port Shielded", content);
                    Assert.Contains("vesselName = Part Showcase - Docking Port Inline", content);
                    Assert.Contains("vesselName = Part Showcase - Grappling Device", content);
                    Assert.Contains("vesselName = Part Showcase - Small Claw", content);
                    Assert.Contains("vesselName = Part Showcase - Mk2 Docking Port", content);
                    Assert.Contains("vesselName = Part Showcase - Mk2 Lander Cabin", content);
                    Assert.Contains("vesselName = Part Showcase - Engine Plate 1.5", content);
                    Assert.Contains("vesselName = Part Showcase - Engine Plate 2", content);
                    Assert.Contains("vesselName = Part Showcase - Engine Plate 3", content);
                    Assert.Contains("vesselName = Part Showcase - Engine Plate 4", content);
                    Assert.Contains("vesselName = Part Showcase - Engine Plate 5", content);
                    Assert.Contains("vesselName = Part Showcase - Heat Shield 1", content);
                    Assert.Contains("vesselName = Part Showcase - Heat Shield 2", content);
                    Assert.Contains("vesselName = Part Showcase - Heat Shield 3", content);
                    Assert.Contains("vesselName = Part Showcase - Heat Shield 1.5", content);
                    Assert.Contains("vesselName = Part Showcase - LV-T30", content);
                    Assert.Contains("vesselName = Part Showcase - LV-T30 v2", content);
                    Assert.Contains("vesselName = Part Showcase - LV-T45", content);
                    Assert.Contains("vesselName = Part Showcase - LV-T45 v2", content);
                    Assert.Contains("vesselName = Part Showcase - Poodle v2", content);
                    Assert.Contains("vesselName = Part Showcase - Terrier v2", content);
                    Assert.Contains("vesselName = Part Showcase - Spark v2", content);
                    Assert.Contains("vesselName = Part Showcase - NERV", content);
                    Assert.Contains("vesselName = Part Showcase - Aerospike", content);
                    Assert.Contains("vesselName = Part Showcase - Mite", content);
                    Assert.Contains("vesselName = Part Showcase - Shrimp", content);
                    Assert.Contains("vesselName = Part Showcase - Flea v2", content);
                    Assert.Contains("vesselName = Part Showcase - Hammer v2", content);
                    Assert.Contains("vesselName = Part Showcase - Vector", content);
                    Assert.Contains("vesselName = Part Showcase - Kodiak", content);
                    Assert.Contains("vesselName = Part Showcase - Cheetah", content);
                    Assert.Contains("vesselName = Part Showcase - Wolfhound", content);
                    Assert.Contains("vesselName = Part Showcase - Bobcat", content);
                    Assert.Contains("vesselName = Part Showcase - Skiff", content);
                    Assert.Contains("vesselName = Part Showcase - Mastodon", content);
                    Assert.Contains("vesselName = Part Showcase - Robotics Hinge G-11", content);
                    Assert.Contains("vesselName = Part Showcase - Robotics Hinge G-00", content);
                    Assert.Contains("vesselName = Part Showcase - Robotics Hinge M-12", content);
                    Assert.Contains("vesselName = Part Showcase - Robotics Hinge M-06", content);
                    Assert.Contains("vesselName = Part Showcase - Robotics Hinge XL", content);
                    Assert.Contains("vesselName = Part Showcase - Robotics Piston 3P6", content);
                    Assert.Contains("vesselName = Part Showcase - Robotics Piston 1P2", content);
                    Assert.Contains("vesselName = Part Showcase - Robotics Piston 1P4", content);
                    Assert.Contains("vesselName = Part Showcase - Robotics Piston 3P12", content);
                    Assert.Contains("vesselName = Part Showcase - Robotics Rotation Servo M-06", content);
                    Assert.Contains("vesselName = Part Showcase - Robotics Rotation Servo M-12", content);
                    Assert.Contains("vesselName = Part Showcase - Robotics Rotation Servo F-12", content);
                    Assert.Contains("vesselName = Part Showcase - Robotics Rotation Servo F-33", content);
                    Assert.Contains("vesselName = Part Showcase - Robotics Rotor EM-16", content);
                    Assert.Contains("vesselName = Part Showcase - Robotics Rotor EM-16S", content);
                    Assert.Contains("vesselName = Part Showcase - Robotics Rotor EM-32", content);
                    Assert.Contains("vesselName = Part Showcase - Robotics Rotor EM-32S", content);
                    Assert.Contains("vesselName = Part Showcase - Robotics Rotor EM-64", content);
                    Assert.Contains("vesselName = Part Showcase - Robotics Rotor EM-64S", content);
                    Assert.Contains("vesselName = Part Showcase - Robotics Rotor Motor R121", content);
                    Assert.Contains("vesselName = Part Showcase - Robotics Rotor Motor R7000", content);
                    Assert.Contains("vesselName = Part Showcase - Airbrake", content);
                    Assert.Contains("vesselName = Part Showcase - Robot Arm Scanner S1", content);
                    Assert.Contains("vesselName = Part Showcase - Robot Arm Scanner S2", content);
                    Assert.Contains("vesselName = Part Showcase - Robot Arm Scanner S3", content);
                    Assert.Contains("vesselName = Part Showcase - Control Surface Advanced Canard", content);
                    Assert.Contains("vesselName = Part Showcase - Control Surface Airliner", content);
                    Assert.Contains("vesselName = Part Showcase - Control Surface Airliner Tail", content);
                    Assert.Contains("vesselName = Part Showcase - Control Surface Canard", content);
                    Assert.Contains("vesselName = Part Showcase - Control Surface Elevon 2", content);
                    Assert.Contains("vesselName = Part Showcase - Control Surface Elevon 3", content);
                    Assert.Contains("vesselName = Part Showcase - Control Surface Elevon 5", content);
                    Assert.Contains("vesselName = Part Showcase - Control Surface Large Fan Blade", content);
                    Assert.Contains("vesselName = Part Showcase - Control Surface Large Heli Blade", content);
                    Assert.Contains("vesselName = Part Showcase - Control Surface Large Propeller", content);
                    Assert.Contains("vesselName = Part Showcase - Control Surface Medium Fan Blade", content);
                    Assert.Contains("vesselName = Part Showcase - Control Surface Medium Heli Blade", content);
                    Assert.Contains("vesselName = Part Showcase - Control Surface Medium Propeller", content);
                    Assert.Contains("vesselName = Part Showcase - Control Surface R8 Winglet", content);
                    Assert.Contains("vesselName = Part Showcase - Control Surface Small", content);
                    Assert.Contains("vesselName = Part Showcase - Control Surface Small Fan Blade", content);
                    Assert.Contains("vesselName = Part Showcase - Control Surface Small Heli Blade", content);
                    Assert.Contains("vesselName = Part Showcase - Control Surface Small Propeller", content);
                    Assert.Contains("vesselName = Part Showcase - Control Surface Standard", content);
                    Assert.Contains("vesselName = Part Showcase - Control Surface Tailfin", content);
                    Assert.Contains("vesselName = Part Showcase - Control Surface Winglet", content);
                    Assert.Contains("vesselName = Part Showcase - Control Surface Shuttle Elevon 1", content);
                    Assert.Contains("vesselName = Part Showcase - Control Surface Shuttle Elevon 2", content);
                    Assert.Contains("vesselName = Part Showcase - Control Surface Shuttle Rudder", content);
                    Assert.Contains("vesselName = Part Showcase - Wheel Dynamics Gear Fixed", content);
                    Assert.Contains("vesselName = Part Showcase - Wheel Dynamics Gear Free", content);
                    Assert.Contains("vesselName = Part Showcase - Wheel Dynamics Rover M1", content);
                    Assert.Contains("vesselName = Part Showcase - Wheel Dynamics Rover S2", content);
                    Assert.Contains("vesselName = Part Showcase - Wheel Dynamics Rover XL3", content);
                    Assert.Contains("vesselName = Part Showcase - Wheel Dynamics TR-2L", content);
                    Assert.Contains("vesselName = Part Showcase - AnimateHeat Airplane Tail", content);
                    Assert.Contains("vesselName = Part Showcase - AnimateHeat Airplane Tail B", content);
                    Assert.Contains("vesselName = Part Showcase - AnimateHeat Avionics Nose Cone", content);
                    Assert.Contains("vesselName = Part Showcase - AnimateHeat Circular Intake", content);
                    Assert.Contains("vesselName = Part Showcase - AnimateHeat Mk1 Intake Fuselage", content);
                    Assert.Contains("vesselName = Part Showcase - AnimateHeat Nacelle Body", content);
                    Assert.Contains("vesselName = Part Showcase - AnimateHeat Nose Cone Adapter", content);
                    Assert.Contains("vesselName = Part Showcase - AnimateHeat Pointy Nose Cone A", content);
                    Assert.Contains("vesselName = Part Showcase - AnimateHeat Pointy Nose Cone B", content);
                    Assert.Contains("vesselName = Part Showcase - AnimateHeat Radial Engine Body", content);
                    Assert.Contains("vesselName = Part Showcase - AnimateHeat Ram Air Intake", content);
                    Assert.Contains("vesselName = Part Showcase - AnimateHeat Shock Cone Intake", content);
                    Assert.Contains("vesselName = Part Showcase - AnimateHeat Standard Nose Cone", content);
                    Assert.Contains("vesselName = Part Showcase - Inventory Placement", content);
                    Assert.Contains("vesselName = Flea Chain", content);
                    Assert.Contains("chainId = chain-eva-board-test", content);
                    Assert.Contains("vesselName = Landing Craft", content);
                    Assert.Contains("chainId = chain-eva-walk-test", content);
                    Assert.Contains("vesselName = Kerbin Ascent", content);
                    Assert.Contains("chainId = chain-atmo-split-test", content);
                    Assert.Contains("segmentPhase = atmo", content);
                    Assert.Contains("segmentPhase = exo", content);
                    Assert.Contains("segmentBodyName = Kerbin", content);
                    Assert.Contains("vesselName = Reentry East", content);
                    Assert.Contains("vesselName = Reentry Shallow", content);
                    Assert.Contains("vesselName = Reentry South", content);
                    // Tree recording assertions (E1-E3)
                    Assert.Contains("RECORDING_TREE", content);
                    Assert.Contains("treeName = Undock Test Tree", content);
                    Assert.Contains("treeName = EVA Test Tree", content);
                    Assert.Contains("treeName = Destruction Test Tree", content);
                    Assert.Contains("vesselName = Undock Root", content);
                    Assert.Contains("vesselName = Undock Upper Stage", content);
                    Assert.Contains("vesselName = Surviving Capsule", content);
                    Assert.Contains("vesselName = Destroyed Booster", content);

                    // Real career fixture recordings: standalone RECORDING nodes are
                    // no longer loaded after T56 (sidecar files still get copied), but
                    // RECORDING_TREE nodes ARE now injected via ScenarioWriter.AddTree.
                    // #384 added the Learstar A1 mission tree from the S16 career as a
                    // far-away / map-view smoke test — assert it round-tripped into the
                    // injected save.
                    Assert.Contains("vesselName = Learstar A1", content);
                    Assert.Contains("vesselName = Learstar A1 Debris", content);
                    Assert.Contains("treeName = Learstar A1", content);

                    Assert.Contains("FLIGHTSTATE", content);

                    // v3: no inline trajectory POINT data in .sfs
                    // (BRANCH_POINT nodes in RECORDING_TREE are expected)
                    Assert.Contains(
                        "recordingFormatVersion = " +
                        RecordingStore.CurrentRecordingFormatVersion.ToString(CultureInfo.InvariantCulture),
                        content);
                    var scenarioSection = content.Substring(
                        content.IndexOf("name = ParsekScenario"),
                        content.IndexOf("FLIGHTSTATE") - content.IndexOf("name = ParsekScenario"));
                    Assert.DoesNotContain("\tPOINT\r\n", scenarioSection);
                    Assert.DoesNotContain("\tPOINT\n", scenarioSection);

                    // Game action milestone data in .sfs
                    Assert.Contains("milestoneEpoch", content);
                    Assert.Contains("MILESTONE_STATE", content);

                    File.Copy(tempPath, savePath, overwrite: true);
                }
                finally
                {
                    if (File.Exists(tempPath))
                        File.Delete(tempPath);
                }
            }

            // Verify sidecar files were written alongside first target
            string firstSavePath = Path.Combine(saveDir, targets[0]);
            if (File.Exists(firstSavePath))
            {
                string recordingsDir = Path.Combine(saveDir, "Parsek", "Recordings");
                Assert.True(Directory.Exists(recordingsDir),
                    $"Expected Parsek/Recordings directory at {recordingsDir}");

                // Expect exactly the synthetic recordings plus the real career
                // recordings whose sidecars CopyRealRecordingFiles forwards — no
                // orphan .prec files from previous inject runs. Each recording
                // has one .prec, and vessel/ghost snapshots produce at least one
                // _vessel.craft OR _ghost.craft file.
                string[] precFiles = Directory.GetFiles(recordingsDir, "*.prec");
                string[] vesselFiles = Directory.GetFiles(recordingsDir, "*_vessel.craft");
                string[] ghostFiles = Directory.GetFiles(recordingsDir, "*_ghost.craft");
                int expected = writer.V3BuilderCount + realRecordingNodes.Length;
                Assert.True(precFiles.Length == expected,
                    $"Expected exactly {expected} .prec files ({writer.V3BuilderCount} synthetic + {realRecordingNodes.Length} real), found {precFiles.Length}. " +
                    "Extra files indicate orphan sidecars from a previous inject run — PurgeRecordingSidecars should have removed them.");
                Assert.True(vesselFiles.Length + ghostFiles.Length >= expected,
                    $"Expected at least {expected} vessel/ghost snapshot files ({writer.V3BuilderCount} synthetic + {realRecordingNodes.Length} real), " +
                    $"found {vesselFiles.Length} _vessel.craft + {ghostFiles.Length} _ghost.craft.");

                // Verify game state sidecar files
                string gameStateDir = Path.Combine(saveDir, "Parsek", "GameState");
                Assert.True(Directory.Exists(gameStateDir),
                    $"Expected Parsek/GameState directory at {gameStateDir}");
                Assert.True(File.Exists(Path.Combine(gameStateDir, "milestones.pgsm")),
                    "Expected milestones.pgsm file");
                Assert.True(File.Exists(Path.Combine(gameStateDir, "events.pgse")),
                    "Expected events.pgse file");
            }
        }

        private static void AddSyntheticGameActions(ScenarioWriter writer, double baseUT)
        {
            var ic = System.Globalization.CultureInfo.InvariantCulture;

            // Create a milestone that looks like a typical first flight commit.
            // Events represent career-mode actions: research tech, purchase parts,
            // accept/complete contracts, hire crew, upgrade facility, resource changes.

            string milestoneId = "synthetic-milestone-001";
            double milestoneStart = 0;
            double milestoneEnd = baseUT + 25; // just before first recording

            var events = new List<GameStateEvent>();

            // Tech research
            events.Add(new GameStateEvent
            {
                ut = 100,
                eventType = GameStateEventType.TechResearched,
                key = "basicRocketry",
                detail = "cost=5;parts=solidBooster.sm.v2,solidBooster",
                valueBefore = 5, valueAfter = 0
            });

            // Part purchase
            events.Add(new GameStateEvent
            {
                ut = 110,
                eventType = GameStateEventType.PartPurchased,
                key = "solidBooster.sm.v2",
                detail = "cost=200",
                valueBefore = 24800, valueAfter = 24600
            });

            // Contract accepted
            events.Add(new GameStateEvent
            {
                ut = 200,
                eventType = GameStateEventType.ContractAccepted,
                key = "contract-guid-survey-001",
                detail = "title=Survey Kerbin Shores"
            });

            // Contract completed
            events.Add(new GameStateEvent
            {
                ut = baseUT + 10,
                eventType = GameStateEventType.ContractCompleted,
                key = "contract-guid-survey-001",
                detail = "title=Survey Kerbin Shores"
            });

            // Crew hired
            events.Add(new GameStateEvent
            {
                ut = 300,
                eventType = GameStateEventType.CrewHired,
                key = "Luzor Kerman",
                detail = "trait=Pilot"
            });

            // Facility upgraded
            events.Add(new GameStateEvent
            {
                ut = 500,
                eventType = GameStateEventType.FacilityUpgraded,
                key = "SpaceCenter/LaunchPad",
                detail = "from=0;to=1",
                valueBefore = 0, valueAfter = 1
            });

            // Contract offered (just noise — shows up as "offered")
            events.Add(new GameStateEvent
            {
                ut = 600,
                eventType = GameStateEventType.ContractOffered,
                key = "contract-guid-orbit-001",
                detail = "title=Orbit Kerbin"
            });

            // Another tech
            events.Add(new GameStateEvent
            {
                ut = baseUT + 5,
                eventType = GameStateEventType.TechResearched,
                key = "engineering101",
                detail = "cost=5;parts=structuralPylon",
                valueBefore = 10, valueAfter = 5
            });

            // Resource events (NOT included in milestone — filtered by CreateMilestone,
            // but included in the events file for budget computation)
            var fundsSpent = new GameStateEvent
            {
                ut = 110,
                eventType = GameStateEventType.FundsChanged,
                key = "",
                valueBefore = 25000, valueAfter = 24600
            };
            var fundsEarned = new GameStateEvent
            {
                ut = baseUT + 10,
                eventType = GameStateEventType.FundsChanged,
                key = "",
                valueBefore = 24600, valueAfter = 30000
            };
            var scienceSpent = new GameStateEvent
            {
                ut = 100,
                eventType = GameStateEventType.ScienceChanged,
                key = "",
                valueBefore = 10, valueAfter = 5
            };
            var repGained = new GameStateEvent
            {
                ut = baseUT + 10,
                eventType = GameStateEventType.ReputationChanged,
                key = "",
                valueBefore = 0, valueAfter = 5
            };

            // Build the milestone (only non-resource, non-noise events)
            var milestone = new Milestone
            {
                MilestoneId = milestoneId,
                StartUT = milestoneStart,
                EndUT = milestoneEnd,
                RecordingId = "",
                Epoch = 0,
                Events = events,
                Committed = true,
                LastReplayedEventIndex = 3 // first 4 events replayed (tech, part, accept, complete)
            };

            writer.WithMilestoneEpoch(0);
            writer.AddMilestone(milestone);

            // Add ALL events to the events file (including resource events)
            foreach (var e in events)
                writer.AddGameStateEvent(e);
            writer.AddGameStateEvent(fundsSpent);
            writer.AddGameStateEvent(fundsEarned);
            writer.AddGameStateEvent(scienceSpent);
            writer.AddGameStateEvent(repGained);
        }

        #endregion

        #region Real Career Recordings

        /// <summary>
        /// Parses real recordings from the default career's persistent.sfs and adds them
        /// to the writer. Returns the array of RECORDING ConfigNodes that were added
        /// (empty array if the default career is absent).
        /// </summary>
        /// <summary>
        /// Returns the path to the frozen default career fixture directory
        /// (Source/Parsek.Tests/Fixtures/DefaultCareer).
        /// </summary>
        private static string ResolveDefaultCareerFixtureDir()
        {
            // Test working dir is bin/Debug/net472/ — walk up to project root
            string dir = Directory.GetCurrentDirectory();
            for (int i = 0; i < 6; i++)
            {
                string candidate = Path.Combine(dir, "Source", "Parsek.Tests", "Fixtures", "DefaultCareer");
                if (Directory.Exists(candidate))
                    return candidate;
                dir = Path.GetDirectoryName(dir);
                if (dir == null) break;
            }
            return null;
        }

        private static ConfigNode[] AddRealCareerRecordings(ScenarioWriter writer, string kspRoot)
        {
            // Use frozen fixture copy instead of live default career
            string fixtureDir = ResolveDefaultCareerFixtureDir();
            string defaultPersistent = fixtureDir != null
                ? Path.Combine(fixtureDir, "persistent.sfs")
                : Path.Combine(kspRoot, "saves", "default", "persistent.sfs");
            if (!File.Exists(defaultPersistent))
                return new ConfigNode[0];

            var root = ConfigNode.Load(defaultPersistent);
            if (root == null)
                return new ConfigNode[0];

            // persistent.sfs has GAME as the root node wrapping everything
            var gameNode = root.HasNode("GAME") ? root.GetNode("GAME") : root;

            // Find ParsekScenario
            ConfigNode scenarioNode = null;
            foreach (ConfigNode sn in gameNode.GetNodes("SCENARIO"))
            {
                if (sn.GetValue("name") == "ParsekScenario")
                {
                    scenarioNode = sn;
                    break;
                }
            }

            if (scenarioNode == null)
                return new ConfigNode[0];

            // Standalone RECORDING nodes are no longer loaded after T56 — they are
            // collected only so CopyRealRecordingFiles can copy their sidecar files.
            // RECORDING_TREE nodes, however, ARE injected into the target save via
            // ScenarioWriter.AddTree so tree-inner recordings appear live in the
            // injected test career (#384 Learstar A1 is the first such tree).
            var recNodes = scenarioNode.GetNodes("RECORDING");
            var treeNodes = scenarioNode.GetNodes("RECORDING_TREE");

            var allRecordings = new List<ConfigNode>(recNodes);
            for (int i = 0; i < treeNodes.Length; i++)
            {
                writer.AddTree(treeNodes[i]);
                allRecordings.AddRange(treeNodes[i].GetNodes("RECORDING"));
            }

            // Forward group hierarchy entries from the real career (e.g.,
            // "Learstar A1 / Debris" nested under "Learstar A1" — #384).
            var hierarchyNodes = scenarioNode.GetNodes("GROUP_HIERARCHY");
            for (int i = 0; i < hierarchyNodes.Length; i++)
            {
                var entries = hierarchyNodes[i].GetNodes("ENTRY");
                for (int j = 0; j < entries.Length; j++)
                {
                    string child = entries[j].GetValue("child");
                    string parent = entries[j].GetValue("parent");
                    writer.AddGroupHierarchyEntry(child, parent);
                }
            }

            // Add milestone states from the real career
            var milestoneStates = scenarioNode.GetNodes("MILESTONE_STATE");
            for (int i = 0; i < milestoneStates.Length; i++)
                writer.AddRawMilestoneState(milestoneStates[i]);

            // Propagate milestone epoch (take the max of existing and parsed)
            string epochStr = scenarioNode.GetValue("milestoneEpoch");
            if (epochStr != null)
            {
                uint epoch;
                if (uint.TryParse(epochStr, System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out epoch))
                {
                    writer.WithMilestoneEpoch(epoch);
                }
            }

            return allRecordings.ToArray();
        }

        /// <summary>
        /// Copies recording sidecar files (authoritative + readable mirrors) and rewind
        /// save files from the default career to the target save directory.
        /// </summary>
        private static void CopyRealRecordingFiles(
            string sourceCareerDir, string targetSaveDir, ConfigNode[] recordings)
        {
            // Copy recording sidecar files
            string srcRecDir = Path.Combine(sourceCareerDir, "Parsek", "Recordings");
            string dstRecDir = Path.Combine(targetSaveDir, "Parsek", "Recordings");
            if (Directory.Exists(srcRecDir))
            {
                if (!Directory.Exists(dstRecDir))
                    Directory.CreateDirectory(dstRecDir);

                for (int i = 0; i < recordings.Length; i++)
                {
                    string id = recordings[i].GetValue("recordingId");
                    if (string.IsNullOrEmpty(id)) continue;

                    string[] suffixes = { ".prec", "_vessel.craft", "_ghost.craft", ".prec.txt", "_vessel.craft.txt", "_ghost.craft.txt" };
                    for (int s = 0; s < suffixes.Length; s++)
                    {
                        string fileName = id + suffixes[s];
                        string src = Path.Combine(srcRecDir, fileName);
                        if (File.Exists(src))
                            File.Copy(src, Path.Combine(dstRecDir, fileName), true);
                    }
                }
            }

            // Copy rewind save files
            string srcSavesDir = Path.Combine(sourceCareerDir, "Parsek", "Saves");
            string dstSavesDir = Path.Combine(targetSaveDir, "Parsek", "Saves");
            if (Directory.Exists(srcSavesDir))
            {
                for (int i = 0; i < recordings.Length; i++)
                {
                    string rewindSave = recordings[i].GetValue("rewindSave");
                    if (string.IsNullOrEmpty(rewindSave)) continue;

                    string src = Path.Combine(srcSavesDir, rewindSave + ".sfs");
                    if (File.Exists(src))
                    {
                        if (!Directory.Exists(dstSavesDir))
                            Directory.CreateDirectory(dstSavesDir);
                        File.Copy(src, Path.Combine(dstSavesDir, rewindSave + ".sfs"), true);
                    }
                }
            }
        }

        #endregion

        #region Tree Recording Builders (E1-E3)

        /// <summary>
        /// E1: Simple Undock Tree — root recording splits into two children.
        /// Root is a composite vessel (pod + SRB), active child continues in orbit,
        /// background child has orbit segment data only.
        /// Time: baseUT+270 to baseUT+390.
        /// </summary>
        internal static ConfigNode SimpleUndockTree(double baseUT = 0)
        {
            double t0 = baseUT + 270;
            double tSplit = baseUT + 330;
            double tEnd = baseUT + 390;
            string treeId = "tree-undock-e1";
            string rootId = "e1-root";
            string childActiveId = "e1-child-active";
            string childBgId = "e1-child-bg";

            var tree = new RecordingTree
            {
                Id = treeId,
                TreeName = "Undock Test Tree",
                RootRecordingId = rootId,
                ActiveRecordingId = childActiveId,
                // Phase F: PreTreeFunds / DeltaFunds / ResourcesApplied removed —
                // ledger drives funds/science/reputation; tree-level delta gone.
            };

            // Root: composite vessel on pad, runs from t0 to tSplit
            tree.Recordings[rootId] = new Recording
            {
                RecordingId = rootId,
                TreeId = treeId,
                VesselName = "Undock Root",
                VesselPersistentId = 5001,
                ChildBranchPointId = "e1-bp1",
                ExplicitStartUT = t0,
                ExplicitEndUT = tSplit,
                TerminalStateValue = null
            };

            // Active child: upper stage, continues orbiting
            tree.Recordings[childActiveId] = new Recording
            {
                RecordingId = childActiveId,
                TreeId = treeId,
                VesselName = "Undock Upper Stage",
                VesselPersistentId = 5002,
                ParentBranchPointId = "e1-bp1",
                ExplicitStartUT = tSplit,
                ExplicitEndUT = tEnd,
                TerminalStateValue = TerminalState.Orbiting,
                TerminalOrbitBody = "Kerbin",
                TerminalOrbitSemiMajorAxis = 700000,
                TerminalOrbitEccentricity = 0.001,
                TerminalOrbitInclination = 28.5,
                TerminalOrbitEpoch = tEnd,
                VesselSnapshot = VesselSnapshotBuilder.CrewedShip("Undock Upper Stage", "Valentina Kerman", 5002)
                    .AsOrbiting(700000, 0.001, 28.5, epoch: tEnd)
                    .Build()
            };

            // Background child: lower stage (orbit segment only, no snapshot, destroyed)
            tree.Recordings[childBgId] = new Recording
            {
                RecordingId = childBgId,
                TreeId = treeId,
                VesselName = "Undock Lower Stage",
                VesselPersistentId = 5003,
                ParentBranchPointId = "e1-bp1",
                ExplicitStartUT = tSplit,
                ExplicitEndUT = tEnd,
                TerminalStateValue = TerminalState.Destroyed,
                VesselSnapshot = null
            };

            tree.BranchPoints.Add(new BranchPoint
            {
                Id = "e1-bp1",
                UT = tSplit,
                Type = BranchPointType.Undock,
                ParentRecordingIds = new List<string> { rootId },
                ChildRecordingIds = new List<string> { childActiveId, childBgId }
            });

            var node = new ConfigNode("RECORDING_TREE");
            tree.Save(node);
            return node;
        }

        /// <summary>
        /// E2: EVA Tree — vessel on pad, EVA kerbal walks, vessel continues recording.
        /// Time: baseUT+390 to baseUT+480.
        /// </summary>
        internal static ConfigNode EvaTree(double baseUT = 0)
        {
            double t0 = baseUT + 390;
            double tEva = baseUT + 420;
            double tEnd = baseUT + 480;
            string treeId = "tree-eva-e2";
            string rootId = "e2-root";
            string vesselContinueId = "e2-vessel";
            string evaChildId = "e2-eva";

            var tree = new RecordingTree
            {
                Id = treeId,
                TreeName = "EVA Test Tree",
                RootRecordingId = rootId,
                ActiveRecordingId = evaChildId,
                // Phase F: legacy resource fields removed.
            };

            // Root: vessel on pad, runs from t0 to tEva
            tree.Recordings[rootId] = new Recording
            {
                RecordingId = rootId,
                TreeId = treeId,
                VesselName = "EVA Root Vessel",
                VesselPersistentId = 6001,
                ChildBranchPointId = "e2-bp1",
                ExplicitStartUT = t0,
                ExplicitEndUT = tEva,
                TerminalStateValue = null
            };

            // Vessel continues after EVA
            tree.Recordings[vesselContinueId] = new Recording
            {
                RecordingId = vesselContinueId,
                TreeId = treeId,
                VesselName = "EVA Root Vessel",
                VesselPersistentId = 6001,
                ParentBranchPointId = "e2-bp1",
                ExplicitStartUT = tEva,
                ExplicitEndUT = tEnd,
                TerminalStateValue = TerminalState.Landed,
                TerminalPosition = new SurfacePosition
                {
                    body = "Kerbin",
                    latitude = -0.0972,
                    longitude = -74.5575,
                    altitude = 75.0,
                    situation = SurfaceSituation.Landed
                },
                VesselSnapshot = VesselSnapshotBuilder.CrewedShip("EVA Root Vessel", "Bill Kerman", 6001)
                    .AsLanded(-0.0972, -74.5575, 75.0)
                    .Build()
            };

            // EVA kerbal walks around
            tree.Recordings[evaChildId] = new Recording
            {
                RecordingId = evaChildId,
                TreeId = treeId,
                VesselName = "Jebediah Kerman",
                VesselPersistentId = 6002,
                ParentBranchPointId = "e2-bp1",
                EvaCrewName = "Jebediah Kerman",
                ExplicitStartUT = tEva,
                ExplicitEndUT = tEnd,
                TerminalStateValue = TerminalState.Landed,
                TerminalPosition = new SurfacePosition
                {
                    body = "Kerbin",
                    latitude = -0.0975,
                    longitude = -74.5570,
                    altitude = 75.0,
                    situation = SurfaceSituation.Landed
                }
            };

            tree.BranchPoints.Add(new BranchPoint
            {
                Id = "e2-bp1",
                UT = tEva,
                Type = BranchPointType.EVA,
                ParentRecordingIds = new List<string> { rootId },
                ChildRecordingIds = new List<string> { vesselContinueId, evaChildId }
            });

            var node = new ConfigNode("RECORDING_TREE");
            tree.Save(node);
            return node;
        }

        /// <summary>
        /// E3: Destruction Tree — root splits into two children, one orbiting (spawnable)
        /// and one destroyed (not spawnable).
        /// Time: baseUT+480 to baseUT+570.
        /// </summary>
        internal static ConfigNode DestructionTree(double baseUT = 0)
        {
            double t0 = baseUT + 480;
            double tSplit = baseUT + 510;
            double tDestroyed = baseUT + 540;
            double tEnd = baseUT + 570;
            string treeId = "tree-dest-e3";
            string rootId = "e3-root";
            string childOrbitId = "e3-child-orbit";
            string childDestroyedId = "e3-child-destroyed";

            var tree = new RecordingTree
            {
                Id = treeId,
                TreeName = "Destruction Test Tree",
                RootRecordingId = rootId,
                ActiveRecordingId = childOrbitId,
                // Phase F: legacy resource fields removed.
            };

            // Root: vessel flies, splits at tSplit
            tree.Recordings[rootId] = new Recording
            {
                RecordingId = rootId,
                TreeId = treeId,
                VesselName = "Destruction Root",
                VesselPersistentId = 7001,
                ChildBranchPointId = "e3-bp1",
                ExplicitStartUT = t0,
                ExplicitEndUT = tSplit,
                TerminalStateValue = null
            };

            // Child A: continues orbiting (spawnable)
            tree.Recordings[childOrbitId] = new Recording
            {
                RecordingId = childOrbitId,
                TreeId = treeId,
                VesselName = "Surviving Capsule",
                VesselPersistentId = 7002,
                ParentBranchPointId = "e3-bp1",
                ExplicitStartUT = tSplit,
                ExplicitEndUT = tEnd,
                TerminalStateValue = TerminalState.Orbiting,
                TerminalOrbitBody = "Kerbin",
                TerminalOrbitSemiMajorAxis = 700000,
                TerminalOrbitEccentricity = 0.005,
                TerminalOrbitInclination = 15.0,
                TerminalOrbitEpoch = tEnd,
                VesselSnapshot = VesselSnapshotBuilder.CrewedShip("Surviving Capsule", "Bob Kerman", 7002)
                    .AsOrbiting(700000, 0.005, 15.0, epoch: tEnd)
                    .Build()
            };

            // Child B: destroyed at tDestroyed (not spawnable)
            tree.Recordings[childDestroyedId] = new Recording
            {
                RecordingId = childDestroyedId,
                TreeId = treeId,
                VesselName = "Destroyed Booster",
                VesselPersistentId = 7003,
                ParentBranchPointId = "e3-bp1",
                ExplicitStartUT = tSplit,
                ExplicitEndUT = tDestroyed,
                TerminalStateValue = TerminalState.Destroyed,
                VesselSnapshot = null,
                VesselDestroyed = true
            };

            tree.BranchPoints.Add(new BranchPoint
            {
                Id = "e3-bp1",
                UT = tSplit,
                Type = BranchPointType.Undock,
                ParentRecordingIds = new List<string> { rootId },
                ChildRecordingIds = new List<string> { childOrbitId, childDestroyedId }
            });

            var node = new ConfigNode("RECORDING_TREE");
            tree.Save(node);
            return node;
        }

        #endregion

        #region Ghost Chain Tree Builders (Phase 6)

        /// <summary>
        /// Scenario A (R0): Station Alpha — pre-existing orbiting station (PID=8001).
        /// Provides the target vessel for docking chain tests.
        /// Time: baseUT+2 to baseUT+4 (early, so station exists before docking recordings start).
        /// </summary>
        internal static ConfigNode StationR0Tree(double baseUT = 0)
        {
            double t0 = baseUT + 2;
            double tEnd = baseUT + 4;
            string treeId = "tree-station-r0";
            string rootId = "gc-station-r0";

            var tree = new RecordingTree
            {
                Id = treeId,
                TreeName = "Station Alpha",
                RootRecordingId = rootId
            };

            tree.Recordings[rootId] = new Recording
            {
                RecordingId = rootId,
                TreeId = treeId,
                VesselName = "Station Alpha",
                VesselPersistentId = 8001,
                ExplicitStartUT = t0,
                ExplicitEndUT = tEnd,
                TerminalStateValue = TerminalState.Orbiting,
                TerminalOrbitBody = "Kerbin",
                TerminalOrbitSemiMajorAxis = 700000,
                TerminalOrbitEccentricity = 0.001,
                TerminalOrbitInclination = 0,
                TerminalOrbitLAN = 0,
                TerminalOrbitArgumentOfPeriapsis = 0,
                TerminalOrbitMeanAnomalyAtEpoch = 0,
                TerminalOrbitEpoch = tEnd,
                RecordingGroups = new List<string> { "Ghost Chain Tests" },
                VesselSnapshot = VesselSnapshotBuilder.ProbeShip("Station Alpha", pid: 8001)
                    .AsOrbiting(700000, 0.001, 0, epoch: tEnd)
                    .Build()
            };

            var node = new ConfigNode("RECORDING_TREE");
            tree.Save(node);
            return node;
        }

        /// <summary>
        /// Scenario A (R1): Docking Vessel A (PID=8002) launches and docks to Station Alpha (PID=8001).
        /// The Dock branch point with TargetVesselPersistentId=8001 triggers the ghost chain walker.
        /// Time: baseUT+5 to baseUT+15 (starts shortly after save load).
        /// </summary>
        internal static ConfigNode StationDockingChainTree(double baseUT = 0)
        {
            double t0 = baseUT + 5;
            double tDock = baseUT + 10;
            double tEnd = baseUT + 15;
            string treeId = "tree-dock-r1";
            string rootId = "dock-r1-root";
            string mergedId = "dock-r1-merged";

            var tree = new RecordingTree
            {
                Id = treeId,
                TreeName = "Station Docking Chain",
                RootRecordingId = rootId
            };

            // Root: docking vessel approaches station
            tree.Recordings[rootId] = new Recording
            {
                RecordingId = rootId,
                TreeId = treeId,
                VesselName = "Docking Vessel A",
                VesselPersistentId = 8002,
                ChildBranchPointId = "dock-r1-bp1",
                ExplicitStartUT = t0,
                ExplicitEndUT = tDock,
                RecordingGroups = new List<string> { "Ghost Chain Tests" }
            };

            // Merged: combined vessel continues orbiting as Station Alpha (PID=8001)
            tree.Recordings[mergedId] = new Recording
            {
                RecordingId = mergedId,
                TreeId = treeId,
                VesselName = "Station Alpha",
                VesselPersistentId = 8001,
                ParentBranchPointId = "dock-r1-bp1",
                ExplicitStartUT = tDock,
                ExplicitEndUT = tEnd,
                TerminalStateValue = TerminalState.Orbiting,
                TerminalOrbitBody = "Kerbin",
                TerminalOrbitSemiMajorAxis = 700000,
                TerminalOrbitEccentricity = 0.001,
                TerminalOrbitInclination = 0,
                TerminalOrbitLAN = 0,
                TerminalOrbitArgumentOfPeriapsis = 0,
                TerminalOrbitMeanAnomalyAtEpoch = 0,
                TerminalOrbitEpoch = tEnd,
                RecordingGroups = new List<string> { "Ghost Chain Tests" },
                VesselSnapshot = VesselSnapshotBuilder.ProbeShip("Station Alpha", pid: 8001)
                    .AsOrbiting(700000, 0.001, 0, epoch: tEnd)
                    .Build()
            };

            tree.BranchPoints.Add(new BranchPoint
            {
                Id = "dock-r1-bp1",
                UT = tDock,
                Type = BranchPointType.Dock,
                TargetVesselPersistentId = 8001,
                MergeCause = "DOCK",
                ParentRecordingIds = new List<string> { rootId },
                ChildRecordingIds = new List<string> { mergedId }
            });

            var node = new ConfigNode("RECORDING_TREE");
            tree.Save(node);
            return node;
        }

        /// <summary>
        /// Scenario B: Docking Vessel B (PID=8003) docks to the R1 merged product (PID=8001).
        /// Cross-tree chain: both R1's merged and R2's target share PID=8001.
        /// Time: baseUT+16 to baseUT+25 (follows Scenario A).
        /// </summary>
        internal static ConfigNode CrossTreeDockingChainTree(double baseUT = 0)
        {
            double t0 = baseUT + 16;
            double tDock = baseUT + 20;
            double tEnd = baseUT + 25;
            string treeId = "tree-dock-r2";
            string rootId = "dock-r2-root";
            string mergedId = "dock-r2-merged";

            var tree = new RecordingTree
            {
                Id = treeId,
                TreeName = "Cross-Tree Docking Chain",
                RootRecordingId = rootId
            };

            tree.Recordings[rootId] = new Recording
            {
                RecordingId = rootId,
                TreeId = treeId,
                VesselName = "Docking Vessel B",
                VesselPersistentId = 8003,
                ChildBranchPointId = "dock-r2-bp1",
                ExplicitStartUT = t0,
                ExplicitEndUT = tDock,
                RecordingGroups = new List<string> { "Ghost Chain Tests" }
            };

            tree.Recordings[mergedId] = new Recording
            {
                RecordingId = mergedId,
                TreeId = treeId,
                VesselName = "Station Alpha",
                VesselPersistentId = 8001,
                ParentBranchPointId = "dock-r2-bp1",
                ExplicitStartUT = tDock,
                ExplicitEndUT = tEnd,
                TerminalStateValue = TerminalState.Orbiting,
                TerminalOrbitBody = "Kerbin",
                TerminalOrbitSemiMajorAxis = 700000,
                TerminalOrbitEccentricity = 0.001,
                TerminalOrbitInclination = 0,
                TerminalOrbitLAN = 0,
                TerminalOrbitArgumentOfPeriapsis = 0,
                TerminalOrbitMeanAnomalyAtEpoch = 0,
                TerminalOrbitEpoch = tEnd,
                RecordingGroups = new List<string> { "Ghost Chain Tests" },
                VesselSnapshot = VesselSnapshotBuilder.ProbeShip("Station Alpha", pid: 8001)
                    .AsOrbiting(700000, 0.001, 0, epoch: tEnd)
                    .Build()
            };

            tree.BranchPoints.Add(new BranchPoint
            {
                Id = "dock-r2-bp1",
                UT = tDock,
                Type = BranchPointType.Dock,
                TargetVesselPersistentId = 8001,
                MergeCause = "DOCK",
                ParentRecordingIds = new List<string> { rootId },
                ChildRecordingIds = new List<string> { mergedId }
            });

            var node = new ConfigNode("RECORDING_TREE");
            tree.Save(node);
            return node;
        }

        /// <summary>
        /// Scenario C (R0): Pad Service Rig — pre-existing vessel landed at KSC pad area (PID=8005).
        /// Time: baseUT+2 to baseUT+4 (early, so pad rig exists before collision recording).
        /// </summary>
        internal static ConfigNode PadRigR0Tree(double baseUT = 0)
        {
            double t0 = baseUT + 2;
            double tEnd = baseUT + 4;
            string treeId = "tree-pad-rig-r0";
            string rootId = "gc-pad-rig-r0";

            var tree = new RecordingTree
            {
                Id = treeId,
                TreeName = "Pad Service Rig",
                RootRecordingId = rootId
            };

            tree.Recordings[rootId] = new Recording
            {
                RecordingId = rootId,
                TreeId = treeId,
                VesselName = "Pad Service Rig",
                VesselPersistentId = 8005,
                ExplicitStartUT = t0,
                ExplicitEndUT = tEnd,
                TerminalStateValue = TerminalState.Landed,
                TerminalPosition = new SurfacePosition
                {
                    body = "Kerbin",
                    latitude = -0.0972,
                    longitude = -74.5576,
                    altitude = 70.0,
                    situation = SurfaceSituation.Landed
                },
                TerrainHeightAtEnd = 65.0,
                RecordingGroups = new List<string> { "Ghost Chain Tests" },
                VesselSnapshot = VesselSnapshotBuilder.ProbeShip("Pad Service Rig", pid: 8005)
                    .AsLanded(-0.0972, -74.5576, 70.0)
                    .Build()
            };

            var node = new ConfigNode("RECORDING_TREE");
            tree.Save(node);
            return node;
        }

        /// <summary>
        /// Scenario C (R1): Collision Probe (PID=8004) docks to Pad Service Rig (PID=8005).
        /// Tests ghost chain spawn near the KSC pad with potential collision.
        /// Time: baseUT+26 to baseUT+35 (follows Scenario B).
        /// </summary>
        internal static ConfigNode SpawnCollisionChainTree(double baseUT = 0)
        {
            double t0 = baseUT + 26;
            double tDock = baseUT + 30;
            double tEnd = baseUT + 35;
            string treeId = "tree-collision-r1";
            string rootId = "collision-r1-root";
            string mergedId = "collision-r1-merged";

            var tree = new RecordingTree
            {
                Id = treeId,
                TreeName = "Collision Dock Chain",
                RootRecordingId = rootId
            };

            tree.Recordings[rootId] = new Recording
            {
                RecordingId = rootId,
                TreeId = treeId,
                VesselName = "Collision Probe",
                VesselPersistentId = 8004,
                ChildBranchPointId = "collision-r1-bp1",
                ExplicitStartUT = t0,
                ExplicitEndUT = tDock,
                RecordingGroups = new List<string> { "Ghost Chain Tests" }
            };

            tree.Recordings[mergedId] = new Recording
            {
                RecordingId = mergedId,
                TreeId = treeId,
                VesselName = "Pad Service Rig",
                VesselPersistentId = 8005,
                ParentBranchPointId = "collision-r1-bp1",
                ExplicitStartUT = tDock,
                ExplicitEndUT = tEnd,
                TerminalStateValue = TerminalState.Landed,
                TerminalPosition = new SurfacePosition
                {
                    body = "Kerbin",
                    latitude = -0.0972,
                    longitude = -74.5576,
                    altitude = 70.0,
                    situation = SurfaceSituation.Landed
                },
                TerrainHeightAtEnd = 65.0,
                RecordingGroups = new List<string> { "Ghost Chain Tests" },
                VesselSnapshot = VesselSnapshotBuilder.ProbeShip("Pad Service Rig", pid: 8005)
                    .AsLanded(-0.0972, -74.5576, 70.0)
                    .Build()
            };

            tree.BranchPoints.Add(new BranchPoint
            {
                Id = "collision-r1-bp1",
                UT = tDock,
                Type = BranchPointType.Dock,
                TargetVesselPersistentId = 8005,
                MergeCause = "DOCK",
                ParentRecordingIds = new List<string> { rootId },
                ChildRecordingIds = new List<string> { mergedId }
            });

            var node = new ConfigNode("RECORDING_TREE");
            tree.Save(node);
            return node;
        }

        /// <summary>
        /// Scenario D (R0): KSC Surface Base — pre-existing vessel landed at flat area near VAB (PID=8006).
        /// Time: baseUT+2 to baseUT+4 (early, so base exists before rover recording).
        /// </summary>
        internal static ConfigNode SurfaceBaseR0Tree(double baseUT = 0)
        {
            double t0 = baseUT + 2;
            double tEnd = baseUT + 4;
            string treeId = "tree-surface-base-r0";
            string rootId = "gc-surface-base-r0";

            var tree = new RecordingTree
            {
                Id = treeId,
                TreeName = "KSC Surface Base",
                RootRecordingId = rootId
            };

            tree.Recordings[rootId] = new Recording
            {
                RecordingId = rootId,
                TreeId = treeId,
                VesselName = "KSC Surface Base",
                VesselPersistentId = 8006,
                ExplicitStartUT = t0,
                ExplicitEndUT = tEnd,
                TerminalStateValue = TerminalState.Landed,
                TerminalPosition = new SurfacePosition
                {
                    body = "Kerbin",
                    latitude = -0.0465,
                    longitude = -74.6100,
                    altitude = 69.0,
                    situation = SurfaceSituation.Landed
                },
                TerrainHeightAtEnd = 65.0,
                RecordingGroups = new List<string> { "Ghost Chain Tests" },
                VesselSnapshot = VesselSnapshotBuilder.ProbeShip("KSC Surface Base", pid: 8006)
                    .AsLanded(-0.0465, -74.6100, 69.0)
                    .Build()
            };

            var node = new ConfigNode("RECORDING_TREE");
            tree.Save(node);
            return node;
        }

        /// <summary>
        /// Scenario D (R1): Service Rover (PID=8007) docks to KSC Surface Base (PID=8006).
        /// Tests surface ghost chain with terrain height correction.
        /// Time: baseUT+36 to baseUT+45 (follows Scenario C).
        /// </summary>
        internal static ConfigNode SurfaceGhostChainTree(double baseUT = 0)
        {
            double t0 = baseUT + 36;
            double tDock = baseUT + 40;
            double tEnd = baseUT + 45;
            string treeId = "tree-surface-dock-r1";
            string rootId = "surface-dock-r1-root";
            string mergedId = "surface-dock-r1-merged";

            var tree = new RecordingTree
            {
                Id = treeId,
                TreeName = "Surface Ghost Chain",
                RootRecordingId = rootId
            };

            tree.Recordings[rootId] = new Recording
            {
                RecordingId = rootId,
                TreeId = treeId,
                VesselName = "Service Rover",
                VesselPersistentId = 8007,
                ChildBranchPointId = "surface-dock-r1-bp1",
                ExplicitStartUT = t0,
                ExplicitEndUT = tDock,
                RecordingGroups = new List<string> { "Ghost Chain Tests" }
            };

            tree.Recordings[mergedId] = new Recording
            {
                RecordingId = mergedId,
                TreeId = treeId,
                VesselName = "KSC Surface Base",
                VesselPersistentId = 8006,
                ParentBranchPointId = "surface-dock-r1-bp1",
                ExplicitStartUT = tDock,
                ExplicitEndUT = tEnd,
                TerminalStateValue = TerminalState.Landed,
                TerminalPosition = new SurfacePosition
                {
                    body = "Kerbin",
                    latitude = -0.0465,
                    longitude = -74.6100,
                    altitude = 69.0,
                    situation = SurfaceSituation.Landed
                },
                TerrainHeightAtEnd = 65.0,
                RecordingGroups = new List<string> { "Ghost Chain Tests" },
                VesselSnapshot = VesselSnapshotBuilder.ProbeShip("KSC Surface Base", pid: 8006)
                    .AsLanded(-0.0465, -74.6100, 69.0)
                    .Build()
            };

            tree.BranchPoints.Add(new BranchPoint
            {
                Id = "surface-dock-r1-bp1",
                UT = tDock,
                Type = BranchPointType.Dock,
                TargetVesselPersistentId = 8006,
                MergeCause = "DOCK",
                ParentRecordingIds = new List<string> { rootId },
                ChildRecordingIds = new List<string> { mergedId }
            });

            var node = new ConfigNode("RECORDING_TREE");
            tree.Save(node);
            return node;
        }

        #endregion
    }
}
