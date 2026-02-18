using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using Parsek.Tests.Generators;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    public class SyntheticRecordingTests
    {
        private static string ProjectRoot => Path.GetFullPath(
            Path.Combine(Directory.GetCurrentDirectory(), "..", "..", "..", "..", ".."));

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

        // Approximate "upright on surface" rotation at KSC for UT ~17000.
        // From real recording data at UT=17285. Close enough for synthetic visuals
        // (Kerbin rotates ~4° in the difference, negligible for ghost appearance).
        private const float KscRotX = 0.33f, KscRotY = -0.63f, KscRotZ = -0.63f, KscRotW = -0.33f;

        internal static RecordingBuilder PadWalk(double baseUT = 0)
        {
            // EVA: Jeb walks ~200m east from launchpad at ground level
            double t = baseUT + 30;
            var b = new RecordingBuilder("Pad Walk");
            b.WithDefaultRotation(KscRotX, KscRotY, KscRotZ, KscRotW);
            double baseLat = -0.0972;
            double baseLon = -74.5575;

            b.AddPoint(t,     baseLat, baseLon,          66);
            b.AddPoint(t+3,   baseLat, baseLon + 0.0002, 66);
            b.AddPoint(t+6,   baseLat, baseLon + 0.0004, 66);
            b.AddPoint(t+9,   baseLat, baseLon + 0.0006, 66.5);
            b.AddPoint(t+12,  baseLat, baseLon + 0.0008, 66.5);
            b.AddPoint(t+15,  baseLat, baseLon + 0.0010, 67);
            b.AddPoint(t+18,  baseLat, baseLon + 0.0012, 67);
            b.AddPoint(t+21,  baseLat, baseLon + 0.0014, 66.5);
            b.AddPoint(t+24,  baseLat, baseLon + 0.0016, 66.5);
            b.AddPoint(t+30,  baseLat, baseLon + 0.0018, 66);

            // Ghost-only EVA snapshot (no vessel spawn, no crew reservation)
            b.WithGhostVisualSnapshot(
                VesselSnapshotBuilder.CrewedShip("Jebediah Kerman", "Jebediah Kerman", pid: 55555555)
                    .WithType("EVA")
                    .AsLanded(baseLat, baseLon + 0.0018, 66));

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
            b.WithVesselSnapshot(
                VesselSnapshotBuilder.FleaRocket("Flea Flight", "Bob Kerman", pid: 22222222)
                    .AsLanded(baseLat, baseLon + 0.0175, 77));

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
                body: "Kerbin");

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

            return b;
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
                VesselSnapshotBuilder.CrewedShip("Jebediah Kerman", "Jebediah Kerman", pid: 77777777)
                    .WithType("EVA")
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
            // Parachute descent — slow final approach
            seg0.AddPoint(t + 55, baseLat - 0.0008, baseLon + 0.0091, 70);
            seg0.AddPoint(t + 60, baseLat - 0.0012, baseLon + 0.0093, 62);
            seg0.AddPoint(t + 65, baseLat - 0.0014, baseLon + 0.0094, 57);
            seg0.AddPoint(t + 70, baseLat - 0.0016, baseLon + 0.0095, 55);
            seg0.AddPoint(t + 75, baseLat - 0.0016, baseLon + 0.0095, 55); // landed

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
                    .AsLanded(landLat, landLon, 55));
            seg0.WithGhostVisualSnapshot(
                VesselSnapshotBuilder.FleaRocket("Landing Craft", "Bill Kerman", pid: 88888888)
                    .AsLanded(landLat, landLon, 55));

            // Segment 1: EVA walk — Bill walks ~25m from landing point (25s)
            var seg1 = new RecordingBuilder("Bill Kerman")
                .WithDefaultRotation(KscRotX, KscRotY, KscRotZ, KscRotW)
                .WithRecordingId("chain-walk-seg1")
                .WithChainId(chainId)
                .WithChainIndex(1)
                .WithParentRecordingId("chain-walk-seg0")
                .WithEvaCrewName("Bill Kerman");

            // Boundary anchor — same position as vessel's final point
            seg1.AddPoint(t + 75, landLat,           landLon,            55);
            seg1.AddPoint(t + 80, landLat - 0.0001,  landLon + 0.0001,  55);
            seg1.AddPoint(t + 85, landLat - 0.0002,  landLon + 0.0002,  55);
            seg1.AddPoint(t + 90, landLat - 0.0003,  landLon + 0.0002,  55);
            seg1.AddPoint(t + 95, landLat - 0.0003,  landLon + 0.0003,  55);
            seg1.AddPoint(t + 100, landLat - 0.0004, landLon + 0.0003,  55);

            // Ghost-only EVA snapshot
            seg1.WithGhostVisualSnapshot(
                VesselSnapshotBuilder.CrewedShip("Bill Kerman", "Bill Kerman", pid: 99999999)
                    .WithType("EVA")
                    .AsLanded(landLat - 0.0004, landLon + 0.0003, 55));

            return new[] { seg0, seg1 };
        }

        /// <summary>
        /// Generalized helper for building a static-trajectory, looping showcase recording
        /// that toggles a pair of part events every 3 seconds. Used for lights, deployables,
        /// gear, cargo bays, and engines.
        /// </summary>
        private static RecordingBuilder BuildPartShowcaseRecording(
            double baseUT, string vesselName, string partName, int rowIndex,
            double distanceFromPadMeters, PartEventType onEvent, PartEventType offEvent,
            uint pidBase, uint evtPid, float eventValue = 0f, int moduleIndex = 0)
        {
            const double metersPerDegree = (2.0 * Math.PI * 600000.0) / 360.0;
            const double spacingMeters = 5.0;

            double t = baseUT + 30;
            double baseLat = -0.0972;
            double baseLon = -74.5575;
            // Center 20-item row on the launchpad: shift south by half the row length
            double rowCenterOffsetMeters = -50.0;
            double lat = baseLat + ((rowIndex * spacingMeters + rowCenterOffsetMeters) / metersPerDegree);
            double lon = baseLon + (distanceFromPadMeters / metersPerDegree);
            double alt = 66.0;

            var b = new RecordingBuilder(vesselName)
                .WithDefaultRotation(KscRotX, KscRotY, KscRotZ, KscRotW)
                .WithLoopPlayback(loop: true, pauseSeconds: 0.0);

            // Static trajectory (24s) so the visual focus is part event playback.
            for (int i = 0; i <= 8; i++)
                b.AddPoint(t + (i * 3), lat, lon, alt);

            // Toggle events every 3 seconds.
            bool on = true;
            for (int sec = 3; sec <= 24; sec += 3)
            {
                b.AddPartEvent(
                    t + sec,
                    pid: evtPid,
                    type: on ? (int)onEvent : (int)offEvent,
                    partName: partName,
                    value: on ? eventValue : 0f,
                    moduleIndex: moduleIndex);
                on = !on;
            }

            // rotY(-90°): upright fixture facing east (away from pad).
            var snap = new VesselSnapshotBuilder()
                .WithName(vesselName)
                .WithPersistentId((uint)(pidBase + rowIndex))
                .AddPart(partName, rotation: "0,-0.7071068,0,0.7071068")
                .AsLanded(lat, lon, alt);
            b.WithGhostVisualSnapshot(snap);

            return b;
        }

        // The first part added by VesselSnapshotBuilder gets persistentId = 100000.
        // Event PIDs must match this so the ghost visual builder can find the part.
        private const uint SinglePartPid = 100000;

        private static RecordingBuilder BuildLightShowcaseRecording(
            double baseUT, string vesselName, string lightPartName, int rowIndex)
        {
            return BuildPartShowcaseRecording(baseUT, vesselName, lightPartName, rowIndex,
                distanceFromPadMeters: 200.0, onEvent: PartEventType.LightOn,
                offEvent: PartEventType.LightOff, pidBase: 88000000, evtPid: SinglePartPid);
        }

        internal static RecordingBuilder[] LightShowcaseRecordings(double baseUT = 0)
        {
            return new[]
            {
                BuildLightShowcaseRecording(baseUT, "Part Showcase - Lights v1", "domeLight1", rowIndex: 0),
                BuildLightShowcaseRecording(baseUT, "Part Showcase - Light - Nav v1", "navLight1", rowIndex: 1),
                BuildLightShowcaseRecording(baseUT, "Part Showcase - Light - Strip v1", "stripLight1", rowIndex: 2),
                BuildLightShowcaseRecording(baseUT, "Part Showcase - Light - Spot v1", "spotLight3", rowIndex: 3),
                BuildLightShowcaseRecording(baseUT, "Part Showcase - Light - Ground Small v1", "groundLight1", rowIndex: 4),
                BuildLightShowcaseRecording(baseUT, "Part Showcase - Light - Ground Stand v1", "groundLight2", rowIndex: 5)
            };
        }

        // Row indices continue from lights (0-5) so all showcases form one line at 200m east.
        // Lights: 0-5, Deployables: 6-10, Landing Legs: 11-13, Cargo: 14-16, Engines: 17-19.

        internal static RecordingBuilder[] DeployableShowcaseRecordings(double baseUT = 0)
        {
            return new[]
            {
                BuildPartShowcaseRecording(baseUT, "Part Showcase - Solar Tracking", "solarPanels4", 6,
                    200.0, PartEventType.DeployableExtended, PartEventType.DeployableRetracted, 89000000, SinglePartPid),
                BuildPartShowcaseRecording(baseUT, "Part Showcase - Solar Large", "largeSolarPanel", 7,
                    200.0, PartEventType.DeployableExtended, PartEventType.DeployableRetracted, 89000000, SinglePartPid),
                BuildPartShowcaseRecording(baseUT, "Part Showcase - Antenna Comm", "longAntenna", 8,
                    200.0, PartEventType.DeployableExtended, PartEventType.DeployableRetracted, 89000000, SinglePartPid),
                BuildPartShowcaseRecording(baseUT, "Part Showcase - Antenna Dish", "commDish", 9,
                    200.0, PartEventType.DeployableExtended, PartEventType.DeployableRetracted, 89000000, SinglePartPid),
                BuildPartShowcaseRecording(baseUT, "Part Showcase - Radiator", "foldingRadSmall", 10,
                    200.0, PartEventType.DeployableExtended, PartEventType.DeployableRetracted, 89000000, SinglePartPid)
            };
        }

        internal static RecordingBuilder[] GearShowcaseRecordings(double baseUT = 0)
        {
            return new[]
            {
                BuildPartShowcaseRecording(baseUT, "Part Showcase - Landing Leg LT-1", "landingLeg1", 11,
                    200.0, PartEventType.GearDeployed, PartEventType.GearRetracted, 90000000, SinglePartPid),
                BuildPartShowcaseRecording(baseUT, "Part Showcase - Landing Leg LT-2", "landingLeg1-2", 12,
                    200.0, PartEventType.GearDeployed, PartEventType.GearRetracted, 90000000, SinglePartPid),
                BuildPartShowcaseRecording(baseUT, "Part Showcase - Landing Leg LT-05", "miniLandingLeg", 13,
                    200.0, PartEventType.GearDeployed, PartEventType.GearRetracted, 90000000, SinglePartPid)
            };
        }

        internal static RecordingBuilder[] CargoBayShowcaseRecordings(double baseUT = 0)
        {
            return new[]
            {
                BuildPartShowcaseRecording(baseUT, "Part Showcase - Service Bay", "ServiceBay.125.v2", 14,
                    200.0, PartEventType.CargoBayOpened, PartEventType.CargoBayClosed, 91000000, SinglePartPid),
                BuildPartShowcaseRecording(baseUT, "Part Showcase - Cargo Mk2", "mk2CargoBayS", 15,
                    200.0, PartEventType.CargoBayOpened, PartEventType.CargoBayClosed, 91000000, SinglePartPid),
                BuildPartShowcaseRecording(baseUT, "Part Showcase - Cargo Mk3", "mk3CargoBayS", 16,
                    200.0, PartEventType.CargoBayOpened, PartEventType.CargoBayClosed, 91000000, SinglePartPid)
            };
        }

        internal static RecordingBuilder[] EngineShowcaseRecordings(double baseUT = 0)
        {
            return new[]
            {
                BuildPartShowcaseRecording(baseUT, "Part Showcase - Mainsail", "liquidEngineMainsail.v2", 17,
                    200.0, PartEventType.EngineIgnited, PartEventType.EngineShutdown, 92000000, SinglePartPid,
                    eventValue: 1.0f),
                BuildPartShowcaseRecording(baseUT, "Part Showcase - Skipper", "engineLargeSkipper.v2", 18,
                    200.0, PartEventType.EngineIgnited, PartEventType.EngineShutdown, 92000000, SinglePartPid,
                    eventValue: 1.0f),
                BuildPartShowcaseRecording(baseUT, "Part Showcase - SSME", "SSME", 19,
                    200.0, PartEventType.EngineIgnited, PartEventType.EngineShutdown, 92000000, SinglePartPid,
                    eventValue: 1.0f)
            };
        }

        #endregion

        #region Unit Tests

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
            writer.AddRecording(KscHopper());

            var scenarioNode = writer.BuildScenarioNode();
            Assert.Equal("ParsekScenario", scenarioNode.GetValue("name"));
            Assert.Single(scenarioNode.GetNodes("RECORDING"));

            string text = writer.SerializeConfigNode(scenarioNode, "SCENARIO", 1);
            Assert.Contains("name = ParsekScenario", text);
            Assert.Contains("RECORDING", text);
            Assert.Contains("POINT", text);
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
            writer.AddRecording(KscHopper());

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
            writer.AddRecording(KscHopper());

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
            writer.AddRecording(KscHopper());

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
            writer.AddRecording(KscHopper());

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
            try
            {
                // Build a recording with points, orbit segments, and part events
                var rec = new RecordingStore.Recording();
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
                node.AddValue("version", "4");
                node.AddValue("recordingId", rec.RecordingId);
                RecordingStore.SerializeTrajectoryInto(node, rec);

                // Deserialize into a fresh recording
                var rec2 = new RecordingStore.Recording();
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
            string tempDir = Path.Combine(Path.GetTempPath(), "parsek_test_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                // Build recording with data
                var rec = new RecordingStore.Recording { RecordingId = "filetest" };
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
                precNode.AddValue("version", "4");
                precNode.AddValue("recordingId", "filetest");
                RecordingStore.SerializeTrajectoryInto(precNode, rec);

                string precPath = Path.Combine(tempDir, "filetest.prec");
                precNode.Save(precPath);

                // Verify file exists and contains expected content
                Assert.True(File.Exists(precPath), "Expected .prec file to be written");
                string content = File.ReadAllText(precPath);
                Assert.Contains("recordingId = filetest", content);
                Assert.Contains("version = 4", content);
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
            Assert.Equal("4", v3Node.GetValue("recordingFormatVersion"));
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
                .WithLoopPlayback(loop: true, pauseSeconds: 0.0)
                .AddPoint(100, 0, 0, 0)
                .AddPoint(103, 0, 0, 0)
                .BuildV3Metadata();

            Assert.Equal("True", node.GetValue("loopPlayback"));
            Assert.Equal("0", node.GetValue("loopPauseSeconds"));
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
            Assert.Equal("4", trajNode.GetValue("version"));
            Assert.Equal("abc123", trajNode.GetValue("recordingId"));
            Assert.Equal(2, trajNode.GetNodes("POINT").Length);
            Assert.Single(trajNode.GetNodes("ORBIT_SEGMENT"));
            Assert.Single(trajNode.GetNodes("PART_EVENT"));
        }

        [Fact]
        public void ScenarioWriter_V3Format_ProducesMetadataOnlyNodes()
        {
            var writer = new ScenarioWriter().WithV3Format();
            writer.AddRecording(KscHopper());

            var scenarioNode = writer.BuildScenarioNode();
            var recNode = scenarioNode.GetNodes("RECORDING")[0];

            Assert.Equal("4", recNode.GetValue("recordingFormatVersion"));
            Assert.Equal("KSC Hopper", recNode.GetValue("vesselName"));
            Assert.Empty(recNode.GetNodes("POINT"));
            Assert.Null(recNode.GetNode("VESSEL_SNAPSHOT"));
            Assert.Null(recNode.GetNode("GHOST_VISUAL_SNAPSHOT"));
        }

        [Fact]
        public void LightShowcaseRecordings_BuildExpectedShape()
        {
            var recordings = LightShowcaseRecordings(baseUT: 17000);
            Assert.Equal(6, recordings.Length);

            var first = recordings[0].Build();
            Assert.Equal("Part Showcase - Lights v1", first.GetValue("vesselName"));
            Assert.Equal("9", first.GetValue("pointCount"));
            Assert.Equal("True", first.GetValue("loopPlayback"));
            Assert.Equal("0", first.GetValue("loopPauseSeconds"));
            Assert.Equal(8, first.GetNodes("PART_EVENT").Length);

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
            Assert.Equal(5, recordings.Length);

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

            // All 5 part names present
            var names = new[] { "solarPanels4", "largeSolarPanel", "longAntenna", "commDish", "foldingRadSmall" };
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
            Assert.Equal(3, recordings.Length);

            var first = recordings[0].Build();
            Assert.Equal("Part Showcase - Landing Leg LT-1", first.GetValue("vesselName"));
            Assert.Equal("True", first.GetValue("loopPlayback"));
            Assert.Equal(8, first.GetNodes("PART_EVENT").Length);

            var events = first.GetNodes("PART_EVENT");
            Assert.Equal(((int)PartEventType.GearDeployed).ToString(), events[0].GetValue("type"));
            Assert.Equal(((int)PartEventType.GearRetracted).ToString(), events[1].GetValue("type"));

            var ghost = first.GetNode("GHOST_VISUAL_SNAPSHOT");
            Assert.NotNull(ghost);
            Assert.Equal("landingLeg1", ghost.GetNodes("PART")[0].GetValue("name"));
            Assert.Equal(ghost.GetNodes("PART")[0].GetValue("persistentId"), events[0].GetValue("pid"));

            var names = new[] { "landingLeg1", "landingLeg1-2", "miniLandingLeg" };
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
            Assert.Equal(3, recordings.Length);

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

            var names = new[] { "ServiceBay.125.v2", "mk2CargoBayS", "mk3CargoBayS" };
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
            Assert.Equal(3, recordings.Length);

            var first = recordings[0].Build();
            Assert.Equal("Part Showcase - Mainsail", first.GetValue("vesselName"));
            Assert.Equal("True", first.GetValue("loopPlayback"));
            Assert.Equal(8, first.GetNodes("PART_EVENT").Length);

            // Verify engine events have value=1 for ignition, 0 for shutdown
            var events = first.GetNodes("PART_EVENT");
            Assert.Equal(((int)PartEventType.EngineIgnited).ToString(), events[0].GetValue("type"));
            Assert.Equal("1", events[0].GetValue("value"));
            Assert.Equal(((int)PartEventType.EngineShutdown).ToString(), events[1].GetValue("type"));
            Assert.Equal("0", events[1].GetValue("value"));

            // Uses dot-form part name
            var ghost = first.GetNode("GHOST_VISUAL_SNAPSHOT");
            Assert.NotNull(ghost);
            Assert.Equal("liquidEngineMainsail.v2", ghost.GetNodes("PART")[0].GetValue("name"));
            Assert.Equal(ghost.GetNodes("PART")[0].GetValue("persistentId"), events[0].GetValue("pid"));

            var names = new[] { "liquidEngineMainsail.v2", "engineLargeSkipper.v2", "SSME" };
            for (int i = 0; i < recordings.Length; i++)
            {
                var g = recordings[i].Build().GetNode("GHOST_VISUAL_SNAPSHOT");
                Assert.Equal(names[i], g.GetNodes("PART")[0].GetValue("name"));
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
                EngineShowcaseRecordings(17000)
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
                EngineShowcaseRecordings(17000)
            };

            var positions = new HashSet<string>();
            foreach (var category in allShowcases)
            {
                foreach (var rb in category)
                {
                    var rec = rb.Build();
                    var pts = rec.GetNodes("POINT");
                    string key = pts[0].GetValue("lat") + "," + pts[0].GetValue("lon");
                    Assert.True(positions.Add(key),
                        $"Duplicate position in '{rec.GetValue("vesselName")}': {key}");
                }
            }
            Assert.Equal(20, positions.Count); // 6 + 5 + 3 + 3 + 3
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
                writer.AddRecording(flight);

                writer.WriteSidecarFiles(tempDir);

                string id = flight.GetRecordingId();
                string recDir = Path.Combine(tempDir, "Parsek", "Recordings");

                // .prec file exists and has correct content
                string precPath = Path.Combine(recDir, $"{id}.prec");
                Assert.True(File.Exists(precPath), $"Expected .prec file at {precPath}");
                string precContent = File.ReadAllText(precPath);
                Assert.Contains($"recordingId = {id}", precContent);
                Assert.Contains("POINT", precContent);

                // _vessel.craft file exists and has vessel data
                string vesselPath = Path.Combine(recDir, $"{id}_vessel.craft");
                Assert.True(File.Exists(vesselPath), $"Expected _vessel.craft at {vesselPath}");
                string vesselContent = File.ReadAllText(vesselPath);
                Assert.Contains("name = Flea Flight", vesselContent);

                // _ghost.craft file exists (falls back to vessel snapshot)
                string ghostPath = Path.Combine(recDir, $"{id}_ghost.craft");
                Assert.True(File.Exists(ghostPath), $"Expected _ghost.craft at {ghostPath}");
            }
            finally
            {
                Directory.Delete(tempDir, true);
            }
        }

        [Fact]
        public void RecordingPaths_ValidateRecordingId_RejectsInvalidIds()
        {
            Assert.False(RecordingPaths.ValidateRecordingId(null));
            Assert.False(RecordingPaths.ValidateRecordingId(""));
            Assert.False(RecordingPaths.ValidateRecordingId("abc/def"));
            Assert.False(RecordingPaths.ValidateRecordingId("abc\\def"));
            Assert.False(RecordingPaths.ValidateRecordingId("abc..def"));
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
            Assert.Contains("testid123.pcrf", RecordingPaths.BuildGhostGeometryRelativePath(id));

            // All paths are under Parsek/Recordings/
            Assert.StartsWith("Parsek", RecordingPaths.BuildTrajectoryRelativePath(id));
            Assert.StartsWith("Parsek", RecordingPaths.BuildVesselSnapshotRelativePath(id));
            Assert.StartsWith("Parsek", RecordingPaths.BuildGhostSnapshotRelativePath(id));
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
            File.WriteAllText(savePath, content);

            // Clean stale recording sidecar files
            string saveDir = Path.GetDirectoryName(savePath);
            string recordingsDir = Path.Combine(saveDir, "Parsek", "Recordings");
            if (Directory.Exists(recordingsDir))
                Directory.Delete(recordingsDir, recursive: true);
        }

        [Trait("Category", "Manual")]
        [Fact]
        public void InjectAllRecordings()
        {
            string saveName = System.Environment.GetEnvironmentVariable("PARSEK_INJECT_SAVE_NAME")
                ?? "test career";
            string targetSave = System.Environment.GetEnvironmentVariable("PARSEK_INJECT_TARGET_SAVE")
                ?? "1.sfs";
            // Default to clean start (strip stale vessels from FLIGHTSTATE).
            // Set PARSEK_INJECT_CLEAN_START=0 to keep existing vessels.
            string cleanEnv = System.Environment.GetEnvironmentVariable("PARSEK_INJECT_CLEAN_START");
            bool cleanStart = cleanEnv == null || IsTruthy(cleanEnv);

            string saveDir = Path.Combine(ProjectRoot,
                "Kerbal Space Program", "saves", saveName);

            // Inject into both persistent.sfs and the target save — KSP loads
            // persistent first (sets initialLoadDone), so it must have the recordings too.
            string[] targets = { "persistent.sfs", targetSave };

            string targetPath = Path.Combine(saveDir, targetSave);
            if (!File.Exists(targetPath))
                return;

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
            writer.AddRecording(PadWalk(baseUT).WithLoopPlayback());
            writer.AddRecording(KscHopper(baseUT).WithLoopPlayback());
            writer.AddRecording(FleaFlight(baseUT).WithLoopPlayback());
            writer.AddRecording(SuborbitalArc(baseUT).WithLoopPlayback());
            writer.AddRecording(KscPadDestroyed(baseUT).WithLoopPlayback());
            writer.AddRecording(Orbit1(baseUT).WithLoopPlayback());
            writer.AddRecording(CloseSpawnConflict(baseUT).WithLoopPlayback());
            writer.AddRecording(IslandProbe(baseUT).WithLoopPlayback());

            var lightShowcases = LightShowcaseRecordings(baseUT);
            for (int i = 0; i < lightShowcases.Length; i++)
                writer.AddRecording(lightShowcases[i]);

            var deployableShowcases = DeployableShowcaseRecordings(baseUT);
            for (int i = 0; i < deployableShowcases.Length; i++)
                writer.AddRecording(deployableShowcases[i]);

            var gearShowcases = GearShowcaseRecordings(baseUT);
            for (int i = 0; i < gearShowcases.Length; i++)
                writer.AddRecording(gearShowcases[i]);

            var cargoBayShowcases = CargoBayShowcaseRecordings(baseUT);
            for (int i = 0; i < cargoBayShowcases.Length; i++)
                writer.AddRecording(cargoBayShowcases[i]);

            var engineShowcases = EngineShowcaseRecordings(baseUT);
            for (int i = 0; i < engineShowcases.Length; i++)
                writer.AddRecording(engineShowcases[i]);

            var chainSegments = EvaBoardChain(baseUT);
            for (int i = 0; i < chainSegments.Length; i++)
                writer.AddRecording(chainSegments[i].WithLoopPlayback());
            var walkChainSegments = EvaWalkChain(baseUT);
            for (int i = 0; i < walkChainSegments.Length; i++)
                writer.AddRecording(walkChainSegments[i].WithLoopPlayback());

            foreach (string file in targets)
            {
                string savePath = Path.Combine(saveDir, file);
                if (!File.Exists(savePath))
                    continue;

                string tempPath = savePath + ".tmp";
                try
                {
                    writer.InjectIntoSaveFile(savePath, tempPath);

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
                    Assert.Contains("vesselName = Part Showcase - Lights v1", content);
                    Assert.Contains("vesselName = Part Showcase - Light - Nav v1", content);
                    Assert.Contains("vesselName = Part Showcase - Light - Strip v1", content);
                    Assert.Contains("vesselName = Part Showcase - Light - Spot v1", content);
                    Assert.Contains("vesselName = Part Showcase - Light - Ground Small v1", content);
                    Assert.Contains("vesselName = Part Showcase - Light - Ground Stand v1", content);
                    Assert.Contains("vesselName = Part Showcase - Solar Tracking", content);
                    Assert.Contains("vesselName = Part Showcase - Solar Large", content);
                    Assert.Contains("vesselName = Part Showcase - Antenna Comm", content);
                    Assert.Contains("vesselName = Part Showcase - Antenna Dish", content);
                    Assert.Contains("vesselName = Part Showcase - Radiator", content);
                    Assert.Contains("vesselName = Part Showcase - Landing Leg LT-1", content);
                    Assert.Contains("vesselName = Part Showcase - Landing Leg LT-2", content);
                    Assert.Contains("vesselName = Part Showcase - Landing Leg LT-05", content);
                    Assert.Contains("vesselName = Part Showcase - Service Bay", content);
                    Assert.Contains("vesselName = Part Showcase - Cargo Mk2", content);
                    Assert.Contains("vesselName = Part Showcase - Cargo Mk3", content);
                    Assert.Contains("vesselName = Part Showcase - Mainsail", content);
                    Assert.Contains("vesselName = Part Showcase - Skipper", content);
                    Assert.Contains("vesselName = Part Showcase - SSME", content);
                    Assert.Contains("vesselName = Flea Chain", content);
                    Assert.Contains("chainId = chain-eva-board-test", content);
                    Assert.Contains("vesselName = Landing Craft", content);
                    Assert.Contains("chainId = chain-eva-walk-test", content);
                    Assert.Contains("FLIGHTSTATE", content);

                    // v3: no inline POINT data in .sfs
                    Assert.Contains("recordingFormatVersion = 4", content);
                    Assert.DoesNotContain("POINT", content.Substring(
                        content.IndexOf("name = ParsekScenario"),
                        content.IndexOf("FLIGHTSTATE") - content.IndexOf("name = ParsekScenario")));

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

                string[] precFiles = Directory.GetFiles(recordingsDir, "*.prec");
                Assert.True(precFiles.Length >= 33,
                    $"Expected at least 33 .prec files (8 baseline + 6 lights + 5 deployables + 3 gear + 3 cargo + 3 engines + 3 board-chain + 2 walk-chain), found {precFiles.Length}");
            }
        }

        #endregion
    }
}
