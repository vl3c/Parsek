using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Parsek
{
    // The model of one map-geometry dump (DumpMapGeometry, docs/dev/design-map-geometry-dump.md).
    // Plain data: the Unity-side capture (MapGeometryRecorder) fills it, the pure writer below
    // serializes it. A nullable or null field is OMITTED from the JSON: the contract says a field
    // that cannot be filled honestly is left out, never invented.

    /// <summary>The <c>camera</c> block.</summary>
    internal sealed class MapGeometryCamera
    {
        internal string Target;
        internal double? PitchDeg;
        internal double? HeadingDeg;
        internal double? Distance;

        /// <summary>Additive: the camera's own world-space forward unit vector.</summary>
        internal double[] Forward;

        /// <summary>Additive: the camera's own world-space up unit vector.</summary>
        internal double[] Up;

        /// <summary>Additive: the camera's vertical field of view in degrees.</summary>
        internal double? FovDeg;
    }

    /// <summary>One <c>bodies[]</c> entry.</summary>
    internal sealed class MapGeometryBody
    {
        internal string Name;
        internal string Parent;
        internal double? Radius;
        internal double? Soi;
        internal double[] Pos;
        internal List<double[]> Orbit;
    }

    /// <summary>The <c>icon</c> block of a ghost.</summary>
    internal sealed class MapGeometryIcon
    {
        internal bool Drawn;
        internal double[] Pos;

        /// <summary>Additive key <c>source</c>: <c>proto</c> (the stock map icon of the ghost's
        /// proto vessel), <c>marker</c> (Parsek's IMGUI marker) or null (nothing drawn).</summary>
        internal string Source;
    }

    /// <summary>The additive <c>markerDecision</c> block: the Parsek marker decision for a
    /// proto ghost and its three inputs (<c>GhostMapPresence.ShouldDrawNonProtoMarkerForGhost</c>).</summary>
    internal sealed class MapGeometryMarkerDecision
    {
        internal bool Draw;
        internal bool DirectorTracedPath;
        internal bool PolylineOwning;
        internal bool IconSuppressed;
    }

    /// <summary>The <c>conic</c> block of an orbit line (KSP-native elements).</summary>
    internal sealed class MapGeometryConic
    {
        internal double Sma;
        internal double Ecc;
        internal double IncDeg;
        internal double LanDeg;
        internal double ArgPeDeg;
        internal double MeanAnomalyAtEpoch;
        internal double Epoch;
    }

    /// <summary>One <c>lines[]</c> entry of a ghost.</summary>
    internal sealed class MapGeometryLine
    {
        internal string Kind;
        internal string Body;
        internal bool Active;
        internal List<double[]> Points = new List<double[]>();
        internal double? Ut0;
        internal double? Ut1;
        internal MapGeometryConic Conic;
        internal List<double[]> Reference;
        internal string ReferenceKind;
    }

    /// <summary>One <c>ghosts[]</c> entry.</summary>
    internal sealed class MapGeometryGhost
    {
        internal string RecordingId;
        internal string VesselName;
        internal uint Pid;
        internal int CommittedIndex = -1;
        internal double? SampleUT;
        internal int? LoopCycle;
        internal string Treatment;
        internal string Body;
        internal MapGeometryIcon Icon;
        internal MapGeometryMarkerDecision MarkerDecision;
        internal List<MapGeometryLine> Lines = new List<MapGeometryLine>();
    }

    /// <summary>One leg of a route overview line.</summary>
    internal sealed class MapGeometryRouteLeg
    {
        internal string Body;
        internal string RecordingId;
        internal List<double[]> Points = new List<double[]>();
    }

    /// <summary>One <c>routeLines[]</c> entry.</summary>
    internal sealed class MapGeometryRoute
    {
        internal string RouteId;
        internal string RouteName;
        internal List<MapGeometryRouteLeg> Legs = new List<MapGeometryRouteLeg>();
    }

    /// <summary>The whole dump.</summary>
    internal sealed class MapGeometryDocument
    {
        internal string Label;
        internal string Scene;
        internal double? Ut;
        internal int? Frame;
        internal double? WarpRate;
        internal MapGeometryCamera Camera;
        internal double[] AxisUp;
        internal double[] AxisRight;
        internal string Root;
        internal List<MapGeometryBody> Bodies = new List<MapGeometryBody>();
        internal List<MapGeometryGhost> Ghosts = new List<MapGeometryGhost>();
        internal List<MapGeometryRoute> Routes = new List<MapGeometryRoute>();
    }

    /// <summary>The <c>counts</c> block, computed by <see cref="MapGeometryJson.ApplyCaps"/>.</summary>
    internal sealed class MapGeometryCounts
    {
        internal int Ghosts;
        internal int Lines;
        internal long Points;
        internal bool Truncated;
        internal int DroppedGhosts;
        internal int DroppedLines;
        internal int DroppedRoutes;
        internal int DroppedLegs;
        internal int DroppedBodies;

        /// <summary>Items skipped because the body they name is not in <c>bodies</c> (a
        /// ghost's or line's or leg's body; not a cap, so it does not set
        /// <see cref="Truncated"/>).</summary>
        internal int UnknownBodyItems;
    }

    /// <summary>
    /// Pure decisions the Unity-side capture (<c>MapGeometryRecorder</c>) makes per ghost and
    /// per line, split out so each has xUnit cells.
    /// </summary>
    internal static class MapGeometryDecisions
    {
        /// <summary>Treatment token: the ghost has a proto vessel but the Director holds no
        /// intent for it.</summary>
        internal const string NoIntentTreatment = "NoIntent";

        /// <summary>Treatment token: a pid-0 entry (the ghost exists only as Parsek polyline /
        /// forward-arc cache lines or a Parsek marker, with no proto vessel the Director
        /// could key an intent on).</summary>
        internal const string DriverOnlyTreatment = "DriverOnly";

        /// <summary>Treatment token: the icon-suppression floor drives the ghost's marker
        /// (no visible Director intent).</summary>
        internal const string SuppressedMarkerTreatment = "SuppressedMarker";

        /// <summary>Treatment token: the Director's intent is hidden this frame.</summary>
        internal const string NoneTreatment = "None";

        internal const string KindOrbit = "orbit";
        internal const string KindPolyline = "polyline";
        internal const string KindTail = "tail";

        internal const string ReferenceRecorded = "recorded";
        internal const string ReferenceIntended = "intended";

        /// <summary>
        /// The ghost's <c>treatment</c> token. A visible intent names its treatment
        /// (<c>StockConic</c> / <c>TracedPath</c>); otherwise the icon-suppression floor reads
        /// <see cref="SuppressedMarkerTreatment"/>; a hidden intent reads
        /// <see cref="NoneTreatment"/>; no intent reads <see cref="NoIntentTreatment"/> for a
        /// proto ghost and <see cref="DriverOnlyTreatment"/> for a pid-0 entry.
        /// </summary>
        internal static string TreatmentToken(
            bool hasPid, bool hasIntent, bool intentVisible, string intentTreatment, bool iconSuppressed)
        {
            if (hasIntent && intentVisible && !string.IsNullOrEmpty(intentTreatment))
                return intentTreatment;
            if (hasPid && iconSuppressed)
                return SuppressedMarkerTreatment;
            if (hasIntent)
                return NoneTreatment;
            return hasPid ? NoIntentTreatment : DriverOnlyTreatment;
        }

        /// <summary>
        /// Splits a Parsek marker key into its recording id and, for a per-instance key
        /// (<c>recordingId#cycle</c>), its loop cycle. False for a null / empty key or the
        /// flight preview marker (<c>preview</c>), which is not a recording.
        /// </summary>
        internal static bool TryParseMarkerKey(string key, out string recordingId, out int? cycle)
        {
            recordingId = null;
            cycle = null;
            if (string.IsNullOrEmpty(key) || string.Equals(key, "preview", StringComparison.Ordinal))
                return false;
            int hash = key.LastIndexOf('#');
            if (hash > 0 && hash < key.Length - 1
                && int.TryParse(key.Substring(hash + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out int c))
            {
                recordingId = key.Substring(0, hash);
                cycle = c;
                return true;
            }
            recordingId = key;
            return true;
        }

        /// <summary>The marker key a ghost entry answers to: <c>recordingId#cycle</c> for a
        /// loop instance, else the bare recording id. Null without a recording id.</summary>
        internal static string MarkerKeyFor(string recordingId, int? cycle)
        {
            if (string.IsNullOrEmpty(recordingId)) return null;
            return cycle.HasValue
                ? recordingId + "#" + cycle.Value.ToString(CultureInfo.InvariantCulture)
                : recordingId;
        }

        /// <summary>
        /// The vertex index range a Vectrosity line draws: <c>[drawStart, drawEnd]</c> clamped
        /// into <c>[0, count - 1]</c>. False when nothing is drawn (no points, or an empty
        /// range).
        /// </summary>
        internal static bool TryClampDrawRange(
            int count, int drawStart, int drawEnd, out int start, out int end)
        {
            start = drawStart < 0 ? 0 : drawStart;
            end = drawEnd > count - 1 ? count - 1 : drawEnd;
            return count > 0 && end >= start;
        }

        /// <summary>A cached Parsek line is reported when its mesh is active or it drew this
        /// frame or the frame before (the deactivation sweep's own window).</summary>
        internal static bool IsCachedLineReported(bool active, int lastDrawnFrame, int frame)
            => active || lastDrawnFrame >= frame - 1;

        /// <summary>
        /// The recording-time range of a proto ghost's orbit line: the live-frame arc bounds
        /// minus the loop epoch shift (<c>effUT = liveUT - shift</c>). Null when either bound
        /// or the shift is not finite.
        /// </summary>
        internal static bool TryRecordingTimeRange(
            double liveStartUT, double liveEndUT, double epochShift, out double ut0, out double ut1)
        {
            ut0 = liveStartUT - epochShift;
            ut1 = liveEndUT - epochShift;
            return MapGeometryJson.IsFinite(ut0) && MapGeometryJson.IsFinite(ut1);
        }

        /// <summary>The loop cycle as the contract's int, or null when it does not fit.</summary>
        internal static int? CycleToInt(long cycle)
            => cycle >= int.MinValue && cycle <= int.MaxValue ? (int?)(int)cycle : null;

        /// <summary>The UT of point <paramref name="k"/> of a body orbit ring of
        /// <paramref name="points"/> points spread evenly over one period. The ring does NOT
        /// repeat its first point at the end; a viewer closes it.</summary>
        internal static double RingSampleUT(double ut, double period, int k, int points)
        {
            if (points < 1) return ut;
            return ut + period * k / points;
        }

        /// <summary>
        /// Orders a ghost's lines in time: lines with a known <c>ut</c> range first, by start
        /// then end; then lines with no range, in capture order (orbit, then polyline legs,
        /// then tail arcs, each in its cache order). Stable: equal keys keep capture order.
        /// </summary>
        internal static List<MapGeometryLine> OrderLines(List<MapGeometryLine> lines)
        {
            var result = new List<MapGeometryLine>();
            if (lines == null) return result;
            var timed = new List<KeyValuePair<int, MapGeometryLine>>();
            var untimed = new List<MapGeometryLine>();
            for (int i = 0; i < lines.Count; i++)
            {
                MapGeometryLine l = lines[i];
                if (l == null) continue;
                if (HasTimeRange(l)) timed.Add(new KeyValuePair<int, MapGeometryLine>(i, l));
                else untimed.Add(l);
            }
            timed.Sort((a, b) =>
            {
                int c = a.Value.Ut0.Value.CompareTo(b.Value.Ut0.Value);
                if (c != 0) return c;
                c = a.Value.Ut1.Value.CompareTo(b.Value.Ut1.Value);
                return c != 0 ? c : a.Key.CompareTo(b.Key);
            });
            for (int i = 0; i < timed.Count; i++) result.Add(timed[i].Value);
            result.AddRange(untimed);
            return result;
        }

        internal static bool HasTimeRange(MapGeometryLine l)
            => l != null && l.Ut0.HasValue && l.Ut1.HasValue
               && MapGeometryJson.IsFinite(l.Ut0.Value) && MapGeometryJson.IsFinite(l.Ut1.Value);
    }

    /// <summary>
    /// The pure half of <c>DumpMapGeometry</c>: the schema id, the bounds every dump obeys
    /// (per-line decimation and the hard caps with their explicit <c>truncated</c> marker) and
    /// the hand-rolled JSON writer.
    ///
    /// <para>Numbers are written with <c>ToString("R", CultureInfo.InvariantCulture)</c>, so a
    /// dump reads the same on a ro-RO machine. NaN and Infinity are never written: a
    /// non-finite scalar omits its field, and a non-finite point is dropped before decimation
    /// (a point list keeps only finite triples).</para>
    ///
    /// <para>Bounds. Every point list (a line, a route leg, a line's reference) is decimated to
    /// at most <see cref="MaxPointsPerLine"/> points, keeping the first and last point and an
    /// even index spread between them - decimation is the documented resolution limit, not a
    /// truncation. The hard caps drop WHOLE entries (a ghost past <see cref="MaxGhosts"/>, a line
    /// past <see cref="MaxLines"/> or past <see cref="MaxPoints"/> drawn points, a route past
    /// <see cref="MaxRoutes"/>, a leg past <see cref="MaxLegsPerRoute"/>, a body past
    /// <see cref="MaxBodies"/>), and any drop sets <c>counts.truncated</c> with the dropped
    /// totals beside it. Nothing is capped silently.</para>
    /// </summary>
    internal static class MapGeometryJson
    {
        internal const string SchemaId = "parsek-map-geometry/1";

        /// <summary>The most points any one point list is written with.</summary>
        internal const int MaxPointsPerLine = 512;

        /// <summary>The points of one body's orbit ring, spread evenly over one period; the
        /// first point is NOT repeated at the end (a viewer closes the ring).</summary>
        internal const int BodyOrbitRingPoints = 64;

        internal const int MaxGhosts = 512;
        internal const int MaxLines = 4096;
        internal const long MaxPoints = 262144;
        internal const int MaxRoutes = 128;
        internal const int MaxLegsPerRoute = 256;
        internal const int MaxBodies = 512;

        private static readonly CultureInfo Ic = CultureInfo.InvariantCulture;

        internal static bool IsFinite(double v) => !double.IsNaN(v) && !double.IsInfinity(v);

        /// <summary>True when <paramref name="p"/> is a non-null triple of finite numbers.</summary>
        internal static bool IsFiniteTriple(double[] p)
            => p != null && p.Length == 3 && IsFinite(p[0]) && IsFinite(p[1]) && IsFinite(p[2]);

        /// <summary>
        /// The finite points of <paramref name="points"/>, decimated to at most
        /// <paramref name="max"/>. When more remain than fit, index <c>i</c> of the output is
        /// source index <c>round(i * (n - 1) / (max - 1))</c>, so the first and last point are
        /// always kept and the spread is even. Never returns null.
        /// </summary>
        internal static List<double[]> Decimate(IList<double[]> points, int max)
        {
            var finite = new List<double[]>(points == null ? 0 : points.Count);
            if (points != null)
            {
                for (int i = 0; i < points.Count; i++)
                    if (IsFiniteTriple(points[i]))
                        finite.Add(points[i]);
            }
            if (max <= 0)
                return new List<double[]>();
            int n = finite.Count;
            if (n <= max)
                return finite;
            if (max == 1)
                return new List<double[]> { finite[0] };
            var result = new List<double[]>(max);
            for (int i = 0; i < max; i++)
            {
                long idx = (long)Math.Round((double)i * (n - 1) / (max - 1), MidpointRounding.AwayFromZero);
                if (idx < 0) idx = 0;
                if (idx > n - 1) idx = n - 1;
                result.Add(finite[(int)idx]);
            }
            return result;
        }

        /// <summary>
        /// Skips (and counts in <c>UnknownBodyItems</c>) every ghost body, line and route leg
        /// naming a body not listed in <c>bodies</c>, orders each ghost's lines in time
        /// (<see cref="MapGeometryDecisions.OrderLines"/>), then
        /// decimates every point list of <paramref name="doc"/> and applies the hard caps in
        /// document order (bodies, then ghosts and their lines, then routes and their legs),
        /// dropping whole entries past a cap. Returns the counts the writer emits:
        /// <c>lines</c> and <c>points</c> cover ghost lines AND route legs (the drawn geometry;
        /// body rings and line references are context and are not counted).
        /// </summary>
        internal static MapGeometryCounts ApplyCaps(MapGeometryDocument doc)
        {
            var counts = new MapGeometryCounts();
            if (doc == null)
                return counts;

            if (doc.Bodies == null) doc.Bodies = new List<MapGeometryBody>();
            if (doc.Ghosts == null) doc.Ghosts = new List<MapGeometryGhost>();
            if (doc.Routes == null) doc.Routes = new List<MapGeometryRoute>();
            // A null entry is not an item: it is neither written nor counted.
            doc.Bodies.RemoveAll(b => b == null);
            doc.Ghosts.RemoveAll(g => g == null);
            doc.Routes.RemoveAll(r => r == null);

            if (doc.Bodies.Count > MaxBodies)
            {
                counts.DroppedBodies = doc.Bodies.Count - MaxBodies;
                doc.Bodies.RemoveRange(MaxBodies, doc.Bodies.Count - MaxBodies);
            }
            for (int b = 0; b < doc.Bodies.Count; b++)
            {
                MapGeometryBody body = doc.Bodies[b];
                if (body != null && body.Orbit != null)
                    body.Orbit = Decimate(body.Orbit, MaxPointsPerLine);
            }

            // Every body a ghost, line, icon or leg names must be listed in bodies: an item
            // naming an unlisted body is skipped and counted, never written with an unknown
            // name. A ghost whose own body is unknown keeps its lines but loses its body and
            // its icon position (which is relative to that body).
            var known = new HashSet<string>(StringComparer.Ordinal);
            for (int b = 0; b < doc.Bodies.Count; b++)
                if (doc.Bodies[b] != null && doc.Bodies[b].Name != null)
                    known.Add(doc.Bodies[b].Name);
            for (int g = 0; g < doc.Ghosts.Count; g++)
            {
                MapGeometryGhost ghost = doc.Ghosts[g];
                if (ghost == null) continue;
                if (ghost.Body != null && !known.Contains(ghost.Body))
                {
                    counts.UnknownBodyItems++;
                    ghost.Body = null;
                    if (ghost.Icon != null) ghost.Icon.Pos = null;
                }
                else if (ghost.Body == null && ghost.Icon != null)
                {
                    ghost.Icon.Pos = null;
                }
                if (ghost.Lines == null) continue;
                var keptKnown = new List<MapGeometryLine>(ghost.Lines.Count);
                for (int l = 0; l < ghost.Lines.Count; l++)
                {
                    MapGeometryLine line = ghost.Lines[l];
                    if (line == null) continue;
                    if (line.Body == null || !known.Contains(line.Body))
                    {
                        counts.UnknownBodyItems++;
                        continue;
                    }
                    keptKnown.Add(line);
                }
                ghost.Lines = MapGeometryDecisions.OrderLines(keptKnown);
            }
            for (int r = 0; r < doc.Routes.Count; r++)
            {
                MapGeometryRoute route = doc.Routes[r];
                if (route == null || route.Legs == null) continue;
                for (int l = route.Legs.Count - 1; l >= 0; l--)
                {
                    MapGeometryRouteLeg leg = route.Legs[l];
                    if (leg == null) continue;
                    if (leg.Body == null || !known.Contains(leg.Body))
                    {
                        counts.UnknownBodyItems++;
                        route.Legs.RemoveAt(l);
                    }
                }
            }

            if (doc.Ghosts.Count > MaxGhosts)
            {
                for (int g = MaxGhosts; g < doc.Ghosts.Count; g++)
                {
                    counts.DroppedGhosts++;
                    MapGeometryGhost dropped = doc.Ghosts[g];
                    if (dropped != null && dropped.Lines != null)
                        counts.DroppedLines += dropped.Lines.Count;
                }
                doc.Ghosts.RemoveRange(MaxGhosts, doc.Ghosts.Count - MaxGhosts);
            }

            int lines = 0;
            long points = 0;
            for (int g = 0; g < doc.Ghosts.Count; g++)
            {
                MapGeometryGhost ghost = doc.Ghosts[g];
                if (ghost == null) continue;
                if (ghost.Lines == null) ghost.Lines = new List<MapGeometryLine>();
                var kept = new List<MapGeometryLine>(ghost.Lines.Count);
                for (int l = 0; l < ghost.Lines.Count; l++)
                {
                    MapGeometryLine line = ghost.Lines[l];
                    if (line == null) continue;
                    line.Points = Decimate(line.Points, MaxPointsPerLine);
                    if (line.Reference != null)
                        line.Reference = Decimate(line.Reference, MaxPointsPerLine);
                    if (lines >= MaxLines || points + line.Points.Count > MaxPoints)
                    {
                        counts.DroppedLines++;
                        continue;
                    }
                    lines++;
                    points += line.Points.Count;
                    kept.Add(line);
                }
                ghost.Lines = kept;
            }

            if (doc.Routes.Count > MaxRoutes)
            {
                for (int r = MaxRoutes; r < doc.Routes.Count; r++)
                {
                    counts.DroppedRoutes++;
                    MapGeometryRoute dropped = doc.Routes[r];
                    if (dropped != null && dropped.Legs != null)
                        counts.DroppedLegs += dropped.Legs.Count;
                }
                doc.Routes.RemoveRange(MaxRoutes, doc.Routes.Count - MaxRoutes);
            }
            for (int r = 0; r < doc.Routes.Count; r++)
            {
                MapGeometryRoute route = doc.Routes[r];
                if (route == null) continue;
                if (route.Legs == null) route.Legs = new List<MapGeometryRouteLeg>();
                var kept = new List<MapGeometryRouteLeg>(route.Legs.Count);
                for (int l = 0; l < route.Legs.Count; l++)
                {
                    MapGeometryRouteLeg leg = route.Legs[l];
                    if (leg == null) continue;
                    leg.Points = Decimate(leg.Points, MaxPointsPerLine);
                    if (kept.Count >= MaxLegsPerRoute || lines >= MaxLines
                        || points + leg.Points.Count > MaxPoints)
                    {
                        counts.DroppedLegs++;
                        continue;
                    }
                    lines++;
                    points += leg.Points.Count;
                    kept.Add(leg);
                }
                route.Legs = kept;
            }

            counts.Ghosts = doc.Ghosts.Count;
            counts.Lines = lines;
            counts.Points = points;
            counts.Truncated = counts.DroppedGhosts > 0 || counts.DroppedLines > 0
                || counts.DroppedRoutes > 0 || counts.DroppedLegs > 0 || counts.DroppedBodies > 0;
            return counts;
        }

        /// <summary>
        /// Serializes <paramref name="doc"/> after <see cref="ApplyCaps"/>. Returns the JSON text;
        /// <paramref name="counts"/> carries what the <c>counts</c> block says, for the verb's
        /// payload and log line.
        /// </summary>
        internal static string Write(MapGeometryDocument doc, out MapGeometryCounts counts)
        {
            if (doc == null) doc = new MapGeometryDocument();
            counts = ApplyCaps(doc);

            var sb = new StringBuilder(16384);
            sb.Append("{\n");
            sb.Append("  \"schema\": ").Append(Str(SchemaId));
            Field(sb, ",\n  ", "label", doc.Label);
            Field(sb, ",\n  ", "scene", doc.Scene);
            Field(sb, ",\n  ", "ut", doc.Ut);
            if (doc.Frame.HasValue)
                sb.Append(",\n  \"frame\": ").Append(doc.Frame.Value.ToString(Ic));
            Field(sb, ",\n  ", "warpRate", doc.WarpRate);

            if (doc.Camera != null)
            {
                sb.Append(",\n  \"camera\": {");
                bool first = true;
                FieldFirst(sb, ref first, "target", doc.Camera.Target);
                FieldFirst(sb, ref first, "pitchDeg", doc.Camera.PitchDeg);
                FieldFirst(sb, ref first, "headingDeg", doc.Camera.HeadingDeg);
                FieldFirst(sb, ref first, "distance", doc.Camera.Distance);
                if (IsFiniteTriple(doc.Camera.Forward))
                {
                    Sep(sb, ref first);
                    sb.Append("\"forward\": ");
                    Vec(sb, doc.Camera.Forward);
                }
                if (IsFiniteTriple(doc.Camera.Up))
                {
                    Sep(sb, ref first);
                    sb.Append("\"up\": ");
                    Vec(sb, doc.Camera.Up);
                }
                FieldFirst(sb, ref first, "fovDeg", doc.Camera.FovDeg);
                sb.Append('}');
            }

            bool up = IsFiniteTriple(doc.AxisUp);
            bool right = IsFiniteTriple(doc.AxisRight);
            if (up || right)
            {
                sb.Append(",\n  \"axes\": {");
                bool first = true;
                if (up) { Sep(sb, ref first); sb.Append("\"up\": "); Vec(sb, doc.AxisUp); }
                if (right) { Sep(sb, ref first); sb.Append("\"right\": "); Vec(sb, doc.AxisRight); }
                sb.Append('}');
            }

            Field(sb, ",\n  ", "root", doc.Root);

            sb.Append(",\n  \"bodies\": [");
            for (int i = 0; i < doc.Bodies.Count; i++)
            {
                MapGeometryBody b = doc.Bodies[i];
                if (b == null) continue;
                sb.Append(i == 0 ? "\n    {" : ",\n    {");
                bool first = true;
                FieldFirst(sb, ref first, "name", b.Name);
                FieldFirst(sb, ref first, "parent", b.Parent);
                FieldFirst(sb, ref first, "radius", b.Radius);
                FieldFirst(sb, ref first, "soi", b.Soi);
                if (IsFiniteTriple(b.Pos)) { Sep(sb, ref first); sb.Append("\"pos\": "); Vec(sb, b.Pos); }
                if (b.Orbit != null) { Sep(sb, ref first); sb.Append("\"orbit\": "); Points(sb, b.Orbit); }
                sb.Append('}');
            }
            sb.Append(doc.Bodies.Count > 0 ? "\n  ]" : "]");

            sb.Append(",\n  \"ghosts\": [");
            bool firstGhost = true;
            for (int i = 0; i < doc.Ghosts.Count; i++)
            {
                MapGeometryGhost g = doc.Ghosts[i];
                if (g == null) continue;
                sb.Append(firstGhost ? "\n    {" : ",\n    {");
                firstGhost = false;
                WriteGhost(sb, g);
                sb.Append('}');
            }
            sb.Append(firstGhost ? "]" : "\n  ]");

            sb.Append(",\n  \"routeLines\": [");
            bool firstRoute = true;
            for (int i = 0; i < doc.Routes.Count; i++)
            {
                MapGeometryRoute r = doc.Routes[i];
                if (r == null) continue;
                sb.Append(firstRoute ? "\n    {" : ",\n    {");
                firstRoute = false;
                bool first = true;
                FieldFirst(sb, ref first, "routeId", r.RouteId);
                FieldFirst(sb, ref first, "routeName", r.RouteName);
                Sep(sb, ref first);
                sb.Append("\"legs\": [");
                for (int l = 0; l < r.Legs.Count; l++)
                {
                    MapGeometryRouteLeg leg = r.Legs[l];
                    sb.Append(l == 0 ? "{" : ", {");
                    bool lf = true;
                    FieldFirst(sb, ref lf, "body", leg.Body);
                    FieldFirst(sb, ref lf, "recordingId", leg.RecordingId);
                    Sep(sb, ref lf);
                    sb.Append("\"points\": ");
                    Points(sb, leg.Points);
                    sb.Append('}');
                }
                sb.Append("]}");
            }
            sb.Append(firstRoute ? "]" : "\n  ]");

            sb.Append(",\n  \"counts\": {\"ghosts\": ").Append(counts.Ghosts.ToString(Ic));
            sb.Append(", \"lines\": ").Append(counts.Lines.ToString(Ic));
            sb.Append(", \"points\": ").Append(counts.Points.ToString(Ic));
            sb.Append(", \"truncated\": ").Append(counts.Truncated ? "true" : "false");
            sb.Append(", \"unknownBodyItems\": ").Append(counts.UnknownBodyItems.ToString(Ic));
            if (counts.Truncated)
            {
                sb.Append(", \"dropped\": {\"ghosts\": ").Append(counts.DroppedGhosts.ToString(Ic));
                sb.Append(", \"lines\": ").Append(counts.DroppedLines.ToString(Ic));
                sb.Append(", \"routes\": ").Append(counts.DroppedRoutes.ToString(Ic));
                sb.Append(", \"legs\": ").Append(counts.DroppedLegs.ToString(Ic));
                sb.Append(", \"bodies\": ").Append(counts.DroppedBodies.ToString(Ic));
                sb.Append('}');
            }
            sb.Append("}\n}\n");
            return sb.ToString();
        }

        private static void WriteGhost(StringBuilder sb, MapGeometryGhost g)
        {
            bool first = true;
            FieldFirst(sb, ref first, "recordingId", g.RecordingId);
            FieldFirst(sb, ref first, "vesselName", g.VesselName);
            Sep(sb, ref first);
            sb.Append("\"pid\": ").Append(g.Pid.ToString(Ic));
            Sep(sb, ref first);
            sb.Append("\"committedIndex\": ").Append(g.CommittedIndex.ToString(Ic));
            FieldFirst(sb, ref first, "sampleUT", g.SampleUT);
            if (g.LoopCycle.HasValue)
            {
                Sep(sb, ref first);
                sb.Append("\"loopCycle\": ").Append(g.LoopCycle.Value.ToString(Ic));
            }
            FieldFirst(sb, ref first, "treatment", g.Treatment);
            FieldFirst(sb, ref first, "body", g.Body);
            if (g.Icon != null)
            {
                Sep(sb, ref first);
                sb.Append("\"icon\": {\"drawn\": ").Append(g.Icon.Drawn ? "true" : "false");
                if (IsFiniteTriple(g.Icon.Pos))
                {
                    sb.Append(", \"pos\": ");
                    Vec(sb, g.Icon.Pos);
                }
                if (g.Icon.Source != null)
                    sb.Append(", \"source\": ").Append(Str(g.Icon.Source));
                sb.Append('}');
            }
            if (g.MarkerDecision != null)
            {
                Sep(sb, ref first);
                sb.Append("\"markerDecision\": {\"draw\": ").Append(g.MarkerDecision.Draw ? "true" : "false");
                sb.Append(", \"directorTracedPath\": ").Append(g.MarkerDecision.DirectorTracedPath ? "true" : "false");
                sb.Append(", \"polylineOwning\": ").Append(g.MarkerDecision.PolylineOwning ? "true" : "false");
                sb.Append(", \"iconSuppressed\": ").Append(g.MarkerDecision.IconSuppressed ? "true" : "false");
                sb.Append('}');
            }
            Sep(sb, ref first);
            sb.Append("\"lines\": [");
            for (int l = 0; l < g.Lines.Count; l++)
            {
                MapGeometryLine line = g.Lines[l];
                sb.Append(l == 0 ? "\n      {" : ",\n      {");
                bool lf = true;
                FieldFirst(sb, ref lf, "kind", line.Kind);
                FieldFirst(sb, ref lf, "body", line.Body);
                Sep(sb, ref lf);
                sb.Append("\"active\": ").Append(line.Active ? "true" : "false");
                Sep(sb, ref lf);
                sb.Append("\"points\": ");
                Points(sb, line.Points);
                if (line.Ut0.HasValue && line.Ut1.HasValue && IsFinite(line.Ut0.Value) && IsFinite(line.Ut1.Value))
                {
                    Sep(sb, ref lf);
                    sb.Append("\"ut\": [").Append(Num(line.Ut0.Value)).Append(',')
                      .Append(Num(line.Ut1.Value)).Append(']');
                }
                if (line.Conic != null)
                {
                    Sep(sb, ref lf);
                    sb.Append("\"conic\": {");
                    bool cf = true;
                    FieldFirst(sb, ref cf, "sma", line.Conic.Sma);
                    FieldFirst(sb, ref cf, "ecc", line.Conic.Ecc);
                    FieldFirst(sb, ref cf, "incDeg", line.Conic.IncDeg);
                    FieldFirst(sb, ref cf, "lanDeg", line.Conic.LanDeg);
                    FieldFirst(sb, ref cf, "argPeDeg", line.Conic.ArgPeDeg);
                    FieldFirst(sb, ref cf, "meanAnomalyAtEpoch", line.Conic.MeanAnomalyAtEpoch);
                    FieldFirst(sb, ref cf, "epoch", line.Conic.Epoch);
                    sb.Append('}');
                }
                if (line.Reference != null && line.Reference.Count > 0)
                {
                    Sep(sb, ref lf);
                    sb.Append("\"reference\": ");
                    Points(sb, line.Reference);
                    FieldFirst(sb, ref lf, "referenceKind", line.ReferenceKind);
                }
                sb.Append('}');
            }
            sb.Append(g.Lines.Count > 0 ? "\n    ]" : "]");
        }

        /// <summary>The invariant round-trip form of a finite double.</summary>
        internal static string Num(double v) => v.ToString("R", Ic);

        internal static string Str(string s) => GuiTreeJson.Str(s);

        private static void Sep(StringBuilder sb, ref bool first)
        {
            if (!first) sb.Append(", ");
            first = false;
        }

        private static void Field(StringBuilder sb, string sep, string key, string value)
        {
            if (value == null) return;
            sb.Append(sep).Append(Str(key)).Append(": ").Append(Str(value));
        }

        private static void Field(StringBuilder sb, string sep, string key, double? value)
        {
            if (!value.HasValue || !IsFinite(value.Value)) return;
            sb.Append(sep).Append(Str(key)).Append(": ").Append(Num(value.Value));
        }

        private static void FieldFirst(StringBuilder sb, ref bool first, string key, string value)
        {
            if (value == null) return;
            Sep(sb, ref first);
            sb.Append(Str(key)).Append(": ").Append(Str(value));
        }

        private static void FieldFirst(StringBuilder sb, ref bool first, string key, double? value)
        {
            if (!value.HasValue || !IsFinite(value.Value)) return;
            Sep(sb, ref first);
            sb.Append(Str(key)).Append(": ").Append(Num(value.Value));
        }

        private static void Vec(StringBuilder sb, double[] p)
        {
            sb.Append('[').Append(Num(p[0])).Append(',').Append(Num(p[1])).Append(',')
              .Append(Num(p[2])).Append(']');
        }

        private static void Points(StringBuilder sb, List<double[]> points)
        {
            sb.Append('[');
            bool first = true;
            if (points != null)
            {
                for (int i = 0; i < points.Count; i++)
                {
                    if (!IsFiniteTriple(points[i])) continue;
                    if (!first) sb.Append(',');
                    first = false;
                    Vec(sb, points[i]);
                }
            }
            sb.Append(']');
        }
    }
}
