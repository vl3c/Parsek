using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using HarmonyLib;
using Parsek.Display;
using Parsek.Logistics;
using Parsek.MapRender;
using UnityEngine;
using Vectrosity;

namespace Parsek
{
    /// <summary>
    /// The Unity-side capture behind the automation-only <c>DumpMapGeometry</c> seam verb
    /// (<c>docs/dev/design-map-geometry-dump.md</c>): reads, in ONE frame after every map draw
    /// (the applier calls <see cref="Capture"/> from <c>WaitForEndOfFrame</c>), the geometry the
    /// map drew - body context, every ghost's proto icon and stock orbit line, Parsek's polyline
    /// legs and forward arcs, Parsek's IMGUI markers, the route overview lines - into a
    /// <see cref="MapGeometryDocument"/>, and writes it with the pure <see cref="MapGeometryJson"/>.
    ///
    /// <para>READ-ONLY. Every source is read through a read accessor that copies
    /// (<c>VisitCachedLegs</c>, <c>VisitCachedForwardArcs</c>, <c>VisitCachedBridgeLines</c>,
    /// <c>VisitCachedRouteLegs</c>, <c>ShadowRenderDriver.TryGetLastIntent</c>, the two taps);
    /// nothing drawn is touched and no setting is read, so the dump works with
    /// <c>mapRenderTracing</c> off. The only state it arms is two taps
    /// (<see cref="MapMarkerRenderer.SetDrawnMarkerTapArmed"/>,
    /// <c>GhostOrbitArcPatch.SetDrawnArcTapArmed</c>), which only record what the marker
    /// funnels and the orbit arc clip already compute.</para>
    ///
    /// <para>FRAMES. Every position is a difference of WORLD positions taken in the capture
    /// frame, so all of them share one set of world axes (the <c>axes</c> block names
    /// <c>Planetarium.up</c> / <c>Planetarium.right</c> in those axes). A drawn line vertex is a
    /// scaled-space point; it is mapped back to metres relative to its body as
    /// <c>(vertex - body.scaledBody.transform.position) * ScaledSpace.ScaleFactor</c>, i.e.
    /// relative to the planet AS DRAWN in the same frame (scaled space has no rotation, only a
    /// translation and a scale).</para>
    ///
    /// <para>IDENTITY. Recording ids and indices come from the render layer's own maps
    /// (<c>GhostMapPresence</c>'s pid maps, the polyline and route caches, the marker keys),
    /// never from a raw committed-list read, so the ERS / ELS gate has nothing to allowlist.</para>
    /// </summary>
    internal static class MapGeometryRecorder
    {
        /// <summary>The extension every dump is written with (the <c>.gui.json</c> pattern;
        /// pinned against <c>TestCommandDumpMapGeometry.DumpSuffix</c> and listed in hlib's
        /// <c>ARTIFACT_SHOTS_SUFFIXES</c>).</summary>
        internal const string OutputSuffix = ".map.json";

        private const string Tag = "MapGeometry";

        // Stock's protected OrbitRendererBase.lineOpacity (KSP 1.12.5: `family float32`), the
        // zoom fade DrawOrbit gates its draw on. Resolved once; null when the field is absent
        // or not a float, and every orbit line then reads opacity unknown (the older rule).
        private static readonly AccessTools.FieldRef<OrbitRendererBase, float> LineOpacityRef =
            ResolveLineOpacityRef();

        private static AccessTools.FieldRef<OrbitRendererBase, float> ResolveLineOpacityRef()
        {
            try
            {
                System.Reflection.FieldInfo f = AccessTools.Field(typeof(OrbitRendererBase), "lineOpacity");
                if (f == null || f.FieldType != typeof(float))
                    return null;
                return AccessTools.FieldRefAccess<OrbitRendererBase, float>("lineOpacity");
            }
            catch (Exception)
            {
                return null;
            }
        }

        // The renderer's fade opacity, or null when it cannot be read.
        private static double? ReadLineOpacity(OrbitRendererBase rb)
        {
            if (rb == null || LineOpacityRef == null)
                return null;
            try
            {
                return LineOpacityRef(rb);
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>The absolute path a dump with <paramref name="label"/> is written to: the
        /// KSP root's <c>Screenshots</c> folder, <c>CaptureScreenshot</c>'s directory.</summary>
        internal static string ResolveOutputPath(string label)
        {
            return Path.Combine(
                Path.Combine(KSPUtil.ApplicationRootPath, "Screenshots"),
                (label ?? string.Empty) + OutputSuffix);
        }

        /// <summary>Writes <paramref name="doc"/> to <paramref name="path"/> through the safe
        /// write (tmp + rename), creating the folder. Returns the counts the dump states.</summary>
        internal static MapGeometryCounts WriteDocument(string path, MapGeometryDocument doc)
        {
            string json = MapGeometryJson.Write(doc, out MapGeometryCounts counts);
            // UTF8Encoding(false): no BOM, so a strict JSON reader sees '{' as byte 0.
            FileIOUtils.SafeWriteBytes(new System.Text.UTF8Encoding(false).GetBytes(json), path, Tag);
            return counts;
        }

        /// <summary>
        /// Reads the current frame's map geometry. Call only from <c>WaitForEndOfFrame</c> in
        /// FLIGHT with the map open or in the Tracking Station; <paramref name="sceneToken"/> is
        /// what the dump states.
        /// </summary>
        internal static MapGeometryDocument Capture(string label, string sceneToken)
        {
            int frame = Time.frameCount;
            double ut = Planetarium.GetUniversalTime();
            double scale = ScaledSpace.ScaleFactor;

            var doc = new MapGeometryDocument
            {
                Label = label,
                Scene = sceneToken,
                Ut = ut,
                Frame = frame,
                WarpRate = TimeWarp.fetch != null ? (double?)TimeWarp.CurrentRate : null,
            };

            PlanetariumCamera cam = PlanetariumCamera.fetch;
            if (cam != null)
            {
                doc.Camera = new MapGeometryCamera
                {
                    Target = cam.target != null ? cam.target.GetName() : null,
                    PitchDeg = TestCommands.TestCommandMapCamera.RadToDeg(cam.camPitch),
                    HeadingDeg = TestCommands.TestCommandMapCamera.RadToDeg(cam.camHdg),
                    Distance = (double)cam.Distance * scale,
                };
                // The rendering camera's own frame. Scaled space only translates and scales,
                // so these directions are in the same world axes as every position.
                Camera unityCam = PlanetariumCamera.Camera;
                if (unityCam != null)
                {
                    doc.Camera.Forward = Triple((Vector3d)unityCam.transform.forward);
                    doc.Camera.Up = Triple((Vector3d)unityCam.transform.up);
                    doc.Camera.FovDeg = unityCam.fieldOfView;
                }
            }

            doc.AxisUp = Triple(Planetarium.up);
            doc.AxisRight = Triple(Planetarium.right);

            var bodiesByName = new Dictionary<string, CelestialBody>(StringComparer.Ordinal);
            CaptureBodies(doc, ut, bodiesByName);

            var ghostsByRecording = new Dictionary<string, List<MapGeometryGhost>>(StringComparer.Ordinal);
            var primaryPidByRecording = new Dictionary<string, uint>(StringComparer.Ordinal);
            var markers = new Dictionary<string, Vector3d>(StringComparer.Ordinal);
            MapMarkerRenderer.CopyDrawnMarkers(frame, markers);
            var consumedMarkers = new HashSet<string>(StringComparer.Ordinal);
            var summary = new MapGeometryDecisions.CaptureSummary { Frame = frame, Scene = sceneToken };

            summary.ProtoGhosts = CaptureProtoGhosts(
                doc, frame, ut, scale, bodiesByName, ghostsByRecording, primaryPidByRecording, summary);

            GhostTrajectoryPolylineRenderer.VisitCachedLegs((recId, index, leg) =>
            {
                MapGeometryDecisions.CachedLineReport report = MapGeometryDecisions.ClassifyCachedLine(
                    leg.vectorLine.active, leg.lastDrawnFrame, frame);
                if (report == MapGeometryDecisions.CachedLineReport.Skip)
                    return;
                var line = new MapGeometryLine
                {
                    Kind = MapGeometryDecisions.KindPolyline,
                    Body = leg.bodyName,
                    Active = report == MapGeometryDecisions.CachedLineReport.Drawn,
                    Ut0 = leg.startUT,
                    Ut1 = leg.endUT,
                };
                if (line.Active)
                    FillDrawnPoints(line, leg.vectorLine, leg.bodyName, bodiesByName, scale);
                if (!leg.wasAnchored && leg.lastDrawnFrame == frame
                    && leg.parityDrawFrame == leg.lastDrawnFrame
                    && leg.parityRecordedRel != null && leg.parityRecordedRel.Length >= 3)
                {
                    line.Reference = FromFlat(leg.parityRecordedRel);
                    line.ReferenceKind = MapGeometryDecisions.ReferenceRecorded;
                    summary.LegRefs++;
                }
                GhostFor(doc, recId, ghostsByRecording, primaryPidByRecording).Lines.Add(line);
                summary.Legs++;
            });

            GhostTrajectoryPolylineRenderer.VisitCachedForwardArcs((recId, secondary, index, arc) =>
            {
                MapGeometryDecisions.CachedLineReport report = MapGeometryDecisions.ClassifyCachedLine(
                    arc.vectorLine.active, arc.lastDrawnFrame, frame);
                if (report == MapGeometryDecisions.CachedLineReport.Skip)
                    return;
                var line = new MapGeometryLine
                {
                    Kind = MapGeometryDecisions.KindTail,
                    Body = arc.bodyName,
                    Active = report == MapGeometryDecisions.CachedLineReport.Drawn,
                    Ut0 = arc.startUT,
                    Ut1 = arc.endUT,
                };
                // The arc's drawStart / drawEnd already carry the seam-bridge clip, so the
                // points are the piece drawn; the bridge itself is read below.
                if (line.Active)
                    FillDrawnPoints(line, arc.vectorLine, arc.bodyName, bodiesByName, scale);
                if (arc.bodyRelOffsets != null && arc.bodyRelOffsets.Length > 0)
                {
                    line.Reference = new List<double[]>(arc.bodyRelOffsets.Length);
                    for (int i = 0; i < arc.bodyRelOffsets.Length; i++)
                        line.Reference.Add(Triple(arc.bodyRelOffsets[i]));
                    line.ReferenceKind = MapGeometryDecisions.ReferenceIntended;
                }
                GhostFor(doc, recId, ghostsByRecording, primaryPidByRecording).Lines.Add(line);
                summary.Arcs++;
            });

            // Seam bridges: the filler from a drawn leg's end to the adjacent conic. Attached to
            // the leg's recording; no `ut` (a bridge blends two pieces, it covers no recorded
            // span of its own).
            GhostTrajectoryPolylineRenderer.VisitCachedBridgeLines(bridge =>
            {
                MapGeometryDecisions.CachedLineReport report = MapGeometryDecisions.ClassifyCachedLine(
                    bridge.Line.active, bridge.LastDrawnFrame, frame);
                if (report == MapGeometryDecisions.CachedLineReport.Skip)
                    return;
                var line = new MapGeometryLine
                {
                    Kind = MapGeometryDecisions.KindBridge,
                    Body = bridge.BodyName,
                    Active = report == MapGeometryDecisions.CachedLineReport.Drawn,
                };
                if (line.Active)
                    FillDrawnPoints(line, bridge.Line, bridge.BodyName, bodiesByName, scale);
                GhostFor(doc, bridge.LegRecordingId, ghostsByRecording, primaryPidByRecording).Lines.Add(line);
                summary.Bridges++;
            });

            summary.MarkersDrawn = AttachMarkers(
                doc, markers, consumedMarkers, ghostsByRecording, primaryPidByRecording);

            // Ghost "body" fallback for entries with no intent / proto orbit: the first active
            // line's body, so an icon-less pid-0 entry still names its frame.
            for (int i = 0; i < doc.Ghosts.Count; i++)
            {
                MapGeometryGhost g = doc.Ghosts[i];
                if (g.Body != null) continue;
                for (int l = 0; l < g.Lines.Count; l++)
                    if (g.Lines[l].Active && g.Lines[l].Body != null) { g.Body = g.Lines[l].Body; break; }
            }

            // Icon positions, now that every entry has the body it will name: a marker-only
            // entry with no line takes the body whose SOI contains the marker.
            summary.MarkerBodiesBySoi = ResolveIconPositions(doc, bodiesByName);

            summary.RouteLegs = CaptureRoutes(doc, frame, scale, bodiesByName);
            summary.Routes = doc.Routes.Count;
            summary.Bodies = doc.Bodies.Count;
            summary.Ghosts = doc.Ghosts.Count;
            MeasureScaledSpace(bodiesByName, scale, summary);

            ParsekLog.Info(Tag, MapGeometryDecisions.FormatCaptureSummary(summary));
            return doc;
        }

        private static void CaptureBodies(
            MapGeometryDocument doc, double ut, Dictionary<string, CelestialBody> bodiesByName)
        {
            List<CelestialBody> bodies = FlightGlobals.Bodies;
            if (bodies == null) return;
            CelestialBody root = null;
            for (int i = 0; i < bodies.Count; i++)
            {
                CelestialBody b = bodies[i];
                if (b == null) continue;
                if (!bodiesByName.ContainsKey(b.bodyName))
                    bodiesByName[b.bodyName] = b;
                if (root == null && IsRoot(b))
                    root = b;
            }
            if (root == null) return;
            doc.Root = root.bodyName;
            Vector3d rootPos = root.position;

            for (int i = 0; i < bodies.Count; i++)
            {
                CelestialBody b = bodies[i];
                if (b == null) continue;
                bool isRoot = b == root;
                var entry = new MapGeometryBody
                {
                    Name = b.bodyName,
                    Parent = !isRoot && b.referenceBody != null ? b.referenceBody.bodyName : null,
                    Radius = b.Radius,
                    Soi = MapGeometryJson.IsFinite(b.sphereOfInfluence) ? (double?)b.sphereOfInfluence : null,
                    Pos = Triple(b.position - rootPos),
                };
                if (!isRoot && b.orbitDriver != null && b.orbitDriver.orbit != null)
                {
                    Orbit orbit = b.orbitDriver.orbit;
                    double period = orbit.period;
                    if (MapGeometryJson.IsFinite(period) && period > 0.0)
                    {
                        entry.Orbit = new List<double[]>(MapGeometryJson.BodyOrbitRingPoints);
                        for (int k = 0; k < MapGeometryJson.BodyOrbitRingPoints; k++)
                        {
                            double t = MapGeometryDecisions.RingSampleUT(
                                ut, period, k, MapGeometryJson.BodyOrbitRingPoints);
                            entry.Orbit.Add(Triple(orbit.getRelativePositionAtUT(t).xzy));
                        }
                    }
                }
                doc.Bodies.Add(entry);
            }
        }

        private static bool IsRoot(CelestialBody b)
            => b.orbitDriver == null || b.orbitDriver.orbit == null || b.referenceBody == null
               || b.referenceBody == b;

        private static int CaptureProtoGhosts(
            MapGeometryDocument doc, int frame, double ut, double scale,
            Dictionary<string, CelestialBody> bodiesByName,
            Dictionary<string, List<MapGeometryGhost>> ghostsByRecording,
            Dictionary<string, uint> primaryPidByRecording,
            MapGeometryDecisions.CaptureSummary summary)
        {
            var pids = new List<uint>(GhostMapPresence.ghostMapVesselPids);
            pids.Sort();
            if (pids.Count == 0) return 0;

            var vesselByPid = new Dictionary<uint, Vessel>();
            List<Vessel> vessels = FlightGlobals.Vessels;
            if (vessels != null)
            {
                for (int i = 0; i < vessels.Count; i++)
                {
                    Vessel v = vessels[i];
                    if (v != null && GhostMapPresence.IsGhostMapVessel(v.persistentId))
                        vesselByPid[v.persistentId] = v;
                }
            }

            for (int i = 0; i < pids.Count; i++)
            {
                uint pid = pids[i];
                vesselByPid.TryGetValue(pid, out Vessel vessel);
                GhostMapPresence.TryGetCommittedTrajectoryForPid(pid, out IPlaybackTrajectory traj, out int idx);
                string recId = GhostMapPresence.FindRecordingIdByVesselPid(pid);
                if (recId == null && traj != null) recId = traj.RecordingId;

                var ghost = new MapGeometryGhost
                {
                    RecordingId = recId,
                    VesselName = traj != null && traj.VesselName != null
                        ? traj.VesselName
                        : (vessel != null ? vessel.vesselName : null),
                    Pid = pid,
                    CommittedIndex = idx,
                };
                if (GhostMapPresence.TryGetOverlapCycleForPid(pid, out _, out long cycle))
                    ghost.LoopCycle = MapGeometryDecisions.CycleToInt(cycle);

                bool hasIntent = ShadowRenderDriver.TryGetLastIntent(pid, out GhostRenderIntent intent);
                bool markerDraw = GhostMapPresence.ShouldDrawNonProtoMarkerForGhost(
                    pid, out bool directorTraced, out bool polylineOwning, out bool iconSuppressed);
                ghost.MarkerDecision = new MapGeometryMarkerDecision
                {
                    Draw = markerDraw,
                    DirectorTracedPath = directorTraced,
                    PolylineOwning = polylineOwning,
                    IconSuppressed = iconSuppressed,
                };
                ghost.Treatment = MapGeometryDecisions.TreatmentToken(
                    hasPid: true, hasIntent, hasIntent && intent.Visible,
                    hasIntent ? intent.Treatment.ToString() : null, iconSuppressed);
                if (hasIntent && intent.Visible && MapGeometryJson.IsFinite(intent.DriveUT))
                    ghost.SampleUT = intent.DriveUT;

                Orbit orbit = vessel != null && vessel.orbitDriver != null ? vessel.orbitDriver.orbit : null;
                CelestialBody orbitBody = orbit != null ? orbit.referenceBody : null;
                ghost.Body = hasIntent && intent.Visible && !string.IsNullOrEmpty(intent.FrameBodyName)
                    ? intent.FrameBodyName
                    : (orbitBody != null ? orbitBody.bodyName : null);

                OrbitRendererBase rb = vessel != null ? vessel.orbitRenderer : null;
                if (rb != null && rb.OrbitLine != null)
                {
                    VectorLine vl = rb.OrbitLine;
                    // Drawn only when stock's DrawOrbit reached DrawSpline this frame: mesh
                    // active and renderer enabled (Parsek's line Postfix may force the mesh
                    // active), the mode LateUpdate passes not OFF, and the zoom fade above 0.
                    // A faded line's points3 are a stale frame's, so it carries none.
                    bool driverIdle = rb.driver != null
                        && rb.driver.updateMode == OrbitDriver.UpdateMode.IDLE;
                    string drawMode = MapGeometryDecisions.EffectiveOrbitDrawMode(
                        rb.drawMode.ToString(), driverIdle);
                    double? opacity = ReadLineOpacity(rb);
                    MapGeometryDecisions.OrbitLineReport lineReport = MapGeometryDecisions.ClassifyOrbitLine(
                        vl.active, rb.enabled, drawMode, opacity);
                    if (lineReport == MapGeometryDecisions.OrbitLineReport.Faded)
                        summary.OrbitLinesFaded++;
                    else if (lineReport == MapGeometryDecisions.OrbitLineReport.DrawnOpacityUnknown)
                        summary.OrbitOpacityUnknown++;
                    var line = new MapGeometryLine
                    {
                        Kind = MapGeometryDecisions.KindOrbit,
                        Body = orbitBody != null ? orbitBody.bodyName : null,
                        Active = MapGeometryDecisions.IsOrbitLineDrawn(lineReport),
                        Opacity = opacity,
                        DrawMode = drawMode,
                    };
                    if (line.Active)
                        FillDrawnPoints(line, vl, line.Body, bodiesByName, scale);
                    if (orbit != null)
                    {
                        line.Conic = new MapGeometryConic
                        {
                            Sma = orbit.semiMajorAxis,
                            Ecc = orbit.eccentricity,
                            IncDeg = orbit.inclination,
                            LanDeg = orbit.LAN,
                            ArgPeDeg = orbit.argumentOfPeriapsis,
                            MeanAnomalyAtEpoch = orbit.meanAnomalyAtEpoch,
                            Epoch = orbit.epoch,
                        };
                    }
                    // `ut` is the window of the arc actually drawn: the one GhostOrbitArcPatch
                    // clipped to THIS frame (its tap), else the same visible-bounds call the
                    // clip makes (stock drew the line: a full period or more). Both are
                    // live-frame; the clip maps live to recording time as effUT = live - shift
                    // in either drive mode, so the same subtraction gives the recording time.
                    bool clipped = Patches.GhostOrbitArcPatch.TryGetDrawnArcClip(
                        pid, frame, out double clipStart, out double clipEnd);
                    double visStart = 0, visEnd = 0;
                    bool hasVisible = !clipped && GhostMapPresence.TryGetVisibleOrbitBoundsForGhostVessel(
                        pid, ut, out visStart, out visEnd);
                    if (MapGeometryDecisions.TrySelectOrbitLineWindow(
                            clipped, clipStart, clipEnd, hasVisible, visStart, visEnd,
                            out double liveStart, out double liveEnd, out _)
                        && MapGeometryDecisions.TryRecordingTimeRange(
                            liveStart, liveEnd, GhostMapPresence.GetGhostOrbitEpochShift(pid),
                            out double ut0, out double ut1))
                    {
                        line.Ut0 = ut0;
                        line.Ut1 = ut1;
                    }
                    if (line.Active)
                        line.Clipped = clipped;
                    ghost.Lines.Add(line);

                    // Frame-offset check for the summary line: the proto icon against its own
                    // drawn orbit line, both relative to the orbit's body.
                    if (vessel != null && line.Active && orbitBody != null && line.Points.Count > 0)
                    {
                        double? d = MapGeometryDecisions.MinDistanceToPolyline(
                            Triple(vessel.GetWorldPos3D() - orbitBody.position), line.Points);
                        if (d.HasValue)
                        {
                            summary.IconLineMeasured++;
                            if (!summary.MaxIconToLineM.HasValue || d.Value > summary.MaxIconToLineM.Value)
                            {
                                summary.MaxIconToLineM = d.Value;
                                summary.MaxIconToLinePid = pid;
                            }
                        }
                    }
                }

                // The proto icon: stock draws it from the renderer's drawIcons while the
                // renderer is enabled. Its position is reported even when hidden (where the
                // icon sits), relative to the ghost's frame body.
                bool protoIconDrawn = vessel != null && rb != null && rb.enabled
                    && rb.drawIcons != OrbitRendererBase.DrawIcons.NONE;
                ghost.Icon = new MapGeometryIcon
                {
                    Drawn = protoIconDrawn,
                    Source = protoIconDrawn ? "proto" : null,
                    World = vessel != null ? Triple(vessel.GetWorldPos3D()) : null,
                };

                doc.Ghosts.Add(ghost);
                if (recId != null)
                {
                    if (!ghostsByRecording.TryGetValue(recId, out List<MapGeometryGhost> list))
                    {
                        list = new List<MapGeometryGhost>();
                        ghostsByRecording[recId] = list;
                    }
                    list.Add(ghost);
                    if (idx >= 0 && !primaryPidByRecording.ContainsKey(recId))
                        primaryPidByRecording[recId] = GhostMapPresence.GetGhostVesselPidForRecording(idx);
                }
            }
            return pids.Count;
        }

        // The entry a recording's cache line attaches to: the recording's primary proto ghost
        // when it has one (GhostMapPresence.GetGhostVesselPidForRecording), else its first proto
        // ghost, else a new pid-0 entry.
        private static MapGeometryGhost GhostFor(
            MapGeometryDocument doc, string recId,
            Dictionary<string, List<MapGeometryGhost>> ghostsByRecording,
            Dictionary<string, uint> primaryPidByRecording)
        {
            string key = recId ?? string.Empty;
            if (ghostsByRecording.TryGetValue(key, out List<MapGeometryGhost> list) && list.Count > 0)
            {
                if (primaryPidByRecording.TryGetValue(key, out uint primary) && primary != 0u)
                {
                    for (int i = 0; i < list.Count; i++)
                        if (list[i].Pid == primary) return list[i];
                }
                return list[0];
            }
            var ghost = NewPidlessGhost(recId, null);
            doc.Ghosts.Add(ghost);
            ghostsByRecording[key] = new List<MapGeometryGhost> { ghost };
            return ghost;
        }

        private static MapGeometryGhost NewPidlessGhost(string recId, int? cycle)
        {
            var ghost = new MapGeometryGhost
            {
                RecordingId = recId,
                Pid = 0u,
                LoopCycle = cycle,
                Treatment = MapGeometryDecisions.TreatmentToken(
                    hasPid: false, hasIntent: false, intentVisible: false, intentTreatment: null,
                    iconSuppressed: false),
                Icon = new MapGeometryIcon { Drawn = false },
            };
            if (recId != null
                && GhostMapPresence.TryGetCommittedRecordingById(recId, out int idx, out Recording rec))
            {
                ghost.CommittedIndex = idx;
                ghost.VesselName = rec != null ? rec.VesselName : null;
            }
            return ghost;
        }

        // Parsek's IMGUI markers drawn this frame: each marker key attaches to the ghost entry
        // that answers to it (recordingId#cycle for a loop instance, else the bare id, each key
        // once); a key no entry answers to becomes a new pid-0 entry. The marker replaces a
        // proto icon reading on the entry it attaches to (the marker decision is a superset of
        // the proto hide, so both are not drawn).
        private static int AttachMarkers(
            MapGeometryDocument doc, Dictionary<string, Vector3d> markers, HashSet<string> consumed,
            Dictionary<string, List<MapGeometryGhost>> ghostsByRecording,
            Dictionary<string, uint> primaryPidByRecording)
        {
            if (markers.Count == 0) return 0;
            int drawn = 0;
            int existing = doc.Ghosts.Count;
            for (int i = 0; i < existing; i++)
            {
                MapGeometryGhost g = doc.Ghosts[i];
                string key = MapGeometryDecisions.MarkerKeyFor(g.RecordingId, g.LoopCycle);
                if (key == null || consumed.Contains(key) || !markers.ContainsKey(key))
                {
                    // A loop instance falls back to the bare-id key only when it is the
                    // recording's primary pid (the one the single-icon flight marker stands for).
                    key = g.RecordingId;
                    bool bareAllowed = !g.LoopCycle.HasValue || IsPrimary(g, primaryPidByRecording);
                    if (key == null || !bareAllowed || consumed.Contains(key) || !markers.ContainsKey(key))
                        continue;
                }
                consumed.Add(key);
                SetMarkerIcon(g, markers[key]);
                drawn++;
            }

            var keys = new List<string>(markers.Keys);
            keys.Sort(StringComparer.Ordinal);
            for (int i = 0; i < keys.Count; i++)
            {
                string key = keys[i];
                if (consumed.Contains(key)) continue;
                if (!MapGeometryDecisions.TryParseMarkerKey(key, out string recId, out int? cycle))
                    continue;
                consumed.Add(key);
                MapGeometryGhost g = null;
                if (!cycle.HasValue && ghostsByRecording.TryGetValue(recId, out List<MapGeometryGhost> list))
                {
                    for (int j = 0; j < list.Count; j++)
                        if (list[j].Pid == 0u && (list[j].Icon == null || !list[j].Icon.Drawn)) { g = list[j]; break; }
                }
                if (g == null)
                {
                    g = NewPidlessGhost(recId, cycle);
                    doc.Ghosts.Add(g);
                    if (!ghostsByRecording.TryGetValue(recId, out List<MapGeometryGhost> l2))
                    {
                        l2 = new List<MapGeometryGhost>();
                        ghostsByRecording[recId] = l2;
                    }
                    l2.Add(g);
                }
                SetMarkerIcon(g, markers[key]);
                drawn++;
            }
            return drawn;
        }

        private static bool IsPrimary(MapGeometryGhost g, Dictionary<string, uint> primaryPidByRecording)
            => g.RecordingId != null
               && primaryPidByRecording.TryGetValue(g.RecordingId, out uint primary)
               && primary == g.Pid;

        // The marker's WORLD position is kept; its body-relative `pos` is resolved by
        // ResolveIconPositions once the entry's body is known (a pid-0 entry learns it from its
        // lines or the marker itself, after this runs).
        private static void SetMarkerIcon(MapGeometryGhost g, Vector3d world)
        {
            g.Icon = new MapGeometryIcon { Drawn = true, Source = "marker", World = Triple(world) };
        }

        // Every icon's `pos` relative to its ghost's body. An entry still without a body (a
        // marker-only pid-0 entry: no intent, no proto orbit, no active line) takes the body
        // whose sphere of influence contains the icon (MapGeometryDecisions.ContainingSoiBody,
        // innermost SOI wins, else the root). Returns how many entries got their body that way.
        private static int ResolveIconPositions(
            MapGeometryDocument doc, Dictionary<string, CelestialBody> bodiesByName)
        {
            List<MapGeometryDecisions.SoiCandidate> candidates = null;
            int bySoi = 0;
            for (int i = 0; i < doc.Ghosts.Count; i++)
            {
                MapGeometryGhost g = doc.Ghosts[i];
                if (g.Icon == null || g.Icon.World == null) continue;
                if (g.Body == null)
                {
                    if (candidates == null) candidates = SoiCandidates();
                    string soiBody = MapGeometryDecisions.ContainingSoiBody(g.Icon.World, candidates);
                    if (soiBody != null)
                    {
                        g.Body = soiBody;
                        bySoi++;
                    }
                }
                if (TryBody(g.Body, bodiesByName, out CelestialBody body))
                {
                    Vector3d c = body.position;
                    g.Icon.Pos = new[] { g.Icon.World[0] - c.x, g.Icon.World[1] - c.y, g.Icon.World[2] - c.z };
                }
            }
            return bySoi;
        }

        private static List<MapGeometryDecisions.SoiCandidate> SoiCandidates()
        {
            var list = new List<MapGeometryDecisions.SoiCandidate>();
            List<CelestialBody> bodies = FlightGlobals.Bodies;
            if (bodies == null) return list;
            for (int i = 0; i < bodies.Count; i++)
            {
                CelestialBody b = bodies[i];
                if (b == null) continue;
                // The root's SOI is unbounded whatever stock stores for it.
                double soi = IsRoot(b) ? double.PositiveInfinity : b.sphereOfInfluence;
                list.Add(new MapGeometryDecisions.SoiCandidate
                {
                    Name = b.bodyName, Centre = Triple(b.position), Soi = soi,
                });
            }
            return list;
        }

        // The summary line's scaled-space figures: ScaledSpace's floating offset (its private
        // totalOffset, read as -LocalToScaledSpace(0)) and the max gap, in metres, between a
        // scaled body's transform (the centre every drawn vertex is mapped against) and
        // LocalToScaledSpace(body.position). A frame-offset error under warp shows there.
        private static void MeasureScaledSpace(
            Dictionary<string, CelestialBody> bodiesByName, double scale,
            MapGeometryDecisions.CaptureSummary summary)
        {
            summary.ScaledOffset = Triple(Vector3d.zero - ScaledSpace.LocalToScaledSpace(Vector3d.zero));
            foreach (var kv in bodiesByName)
            {
                CelestialBody b = kv.Value;
                if (b == null || b.scaledBody == null) continue;
                Vector3d gap = (Vector3d)b.scaledBody.transform.position - ScaledSpace.LocalToScaledSpace(b.position);
                double m = gap.magnitude * scale;
                if (!MapGeometryJson.IsFinite(m)) continue;
                if (!summary.MaxScaledCentreSkewM.HasValue || m > summary.MaxScaledCentreSkewM.Value)
                    summary.MaxScaledCentreSkewM = m;
            }
        }

        private static int CaptureRoutes(
            MapGeometryDocument doc, int frame, double scale, Dictionary<string, CelestialBody> bodiesByName)
        {
            var routesById = new Dictionary<string, MapGeometryRoute>(StringComparer.Ordinal);
            var order = new List<string>();
            int legs = 0;
            RouteTrajectoryLineRenderer.VisitCachedRouteLegs((routeId, memberId, index, leg) =>
            {
                // A route leg is drawn only while its mesh is active; the route renderer's own
                // sweep hides a leg the ghost owns or paints this frame.
                if (!leg.vectorLine.active) return;
                if (!routesById.TryGetValue(routeId, out MapGeometryRoute route))
                {
                    route = new MapGeometryRoute { RouteId = routeId };
                    routesById[routeId] = route;
                    order.Add(routeId);
                }
                var tmp = new MapGeometryLine { Body = leg.bodyName };
                FillDrawnPoints(tmp, leg.vectorLine, leg.bodyName, bodiesByName, scale);
                route.Legs.Add(new MapGeometryRouteLeg
                {
                    Body = leg.bodyName,
                    RecordingId = memberId,
                    Points = tmp.Points,
                });
                legs++;
            });
            if (order.Count == 0) return 0;

            var names = new Dictionary<string, string>(StringComparer.Ordinal);
            var committed = RouteStore.CommittedRoutes;
            if (committed != null)
            {
                for (int i = 0; i < committed.Count; i++)
                {
                    Route r = committed[i];
                    if (r != null && r.Id != null && !names.ContainsKey(r.Id)) names[r.Id] = r.Name;
                }
            }
            order.Sort(StringComparer.Ordinal);
            for (int i = 0; i < order.Count; i++)
            {
                MapGeometryRoute route = routesById[order[i]];
                if (names.TryGetValue(route.RouteId, out string name)) route.RouteName = name;
                doc.Routes.Add(route);
            }
            return legs;
        }

        // The vertices a Vectrosity line hands the renderer, [drawStart, drawEnd], mapped back
        // to metres relative to the line's body as drawn this frame. A line whose body cannot
        // be resolved keeps no points (the body is still named).
        private static void FillDrawnPoints(
            MapGeometryLine line, VectorLine vl, string bodyName,
            Dictionary<string, CelestialBody> bodiesByName, double scale)
        {
            line.Points = new List<double[]>();
            if (vl == null || !TryBody(bodyName, bodiesByName, out CelestialBody body))
                return;
            List<Vector3> pts = vl.points3;
            if (pts == null) return;
            if (!MapGeometryDecisions.TryClampDrawRange(pts.Count, vl.drawStart, vl.drawEnd, out int start, out int end))
                return;
            Vector3d centre = body.scaledBody != null
                ? (Vector3d)body.scaledBody.transform.position
                : ScaledSpace.LocalToScaledSpace(body.position);
            line.Points.Capacity = end - start + 1;
            for (int i = start; i <= end; i++)
            {
                Vector3d rel = RenderGeometrySampler.RenderedScaledVertexToBodyRelative(pts[i], centre, scale);
                line.Points.Add(Triple(rel));
            }
        }

        private static bool TryBody(
            string name, Dictionary<string, CelestialBody> bodiesByName, out CelestialBody body)
        {
            body = null;
            return name != null && bodiesByName.TryGetValue(name, out body) && body != null;
        }

        private static List<double[]> FromFlat(double[] flat)
        {
            var list = new List<double[]>(flat.Length / 3);
            for (int i = 0; i + 2 < flat.Length; i += 3)
                list.Add(new[] { flat[i], flat[i + 1], flat[i + 2] });
            return list;
        }

        private static double[] Triple(Vector3d v) => new[] { v.x, v.y, v.z };
    }
}
