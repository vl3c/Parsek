using System;
using System.Collections.Generic;
using System.Text;

namespace Parsek.TestCommands
{
    /// <summary>One renderer of one side, as the live capture reads it (design 4.6). The
    /// transform is the renderer's PART-LOCAL pose: position in metres, rotation as a
    /// quaternion [x, y, z, w], scale per axis.</summary>
    internal sealed class PartParityRendererRecord
    {
        /// <summary>The transform path relative to the part, already normalised by
        /// <see cref="PartParityStructure.NormalizePath"/>.</summary>
        internal string Path;

        /// <summary><c>MeshRenderer</c> or <c>SkinnedMeshRenderer</c>.</summary>
        internal string Kind;

        internal string Mesh;
        internal int VertexCount;
        internal string[] Materials;
        internal string[] Shaders;
        internal string[] MainTextures;

        /// <summary>Whether the renderer draws: active in hierarchy, enabled, not
        /// <c>forceRenderingOff</c>.</summary>
        internal bool Rendered;

        /// <summary>True when the ghost builder's depth-mask rule
        /// (<c>GhostVisualBuilder.IsDepthMaskRenderer</c>) skips this renderer; set on the real
        /// side only.</summary>
        internal bool DepthMask;

        internal double[] LocalPosition;
        internal double[] LocalRotation;
        internal double[] LocalScale;

        /// <summary>The renderer's PART-LOCAL axis-aligned bounds (metres), min and max
        /// corner; null when the capture could not read a mesh bound. Read by the framing
        /// rule (<see cref="PartParityFramingRule"/>), never by the diff.</summary>
        internal double[] BoundsMin;
        internal double[] BoundsMax;
    }

    /// <summary>One renderer named by a diff list.</summary>
    internal sealed class PartParityRendererRef
    {
        /// <summary>The pairing key: the path for the first renderer at that path, the path
        /// plus <c>#k</c> for the k-th further one (k from 1), so a side with one renderer at a
        /// path still pairs with the first of several on the other side.</summary>
        internal string Key;
        internal string Mesh;
        internal bool Rendered;

        /// <summary>Null for a visible renderer; else why it draws nothing a picture can
        /// show and the ghost leaves it out by design: <c>depth-mask</c> (a depth-only
        /// shader) or <c>fx</c> (a runtime effect mesh such as a Waterfall plume). See
        /// <see cref="PartParityStructure.NonVisualReason"/>.</summary>
        internal string NonVisual;
    }

    /// <summary>A paired renderer whose part-local pose differs past a tolerance.</summary>
    internal sealed class PartParityTransformDiff
    {
        internal string Key;
        internal double PositionDeltaM;
        internal double RotationDeltaDeg;

        /// <summary>The largest per-axis relative scale difference, in percent.</summary>
        internal double ScaleDeltaPct;

        /// <summary>On an air case, for a renderer under the canopy transform only: the real
        /// renderer's distance from the canopy transform's pivot (metres, part-local), the
        /// lever arm the canopy cap turns into a position allowance (design D.4). NaN
        /// elsewhere (written null).</summary>
        internal double CanopyPivotDistanceM = double.NaN;
    }

    /// <summary>A paired renderer whose field <see cref="Field"/> differs.</summary>
    internal sealed class PartParityFieldDiff
    {
        internal string Key;
        internal string Field;
        internal string Real;
        internal string Ghost;
    }

    /// <summary>The structural diff of one case (the sidecar's <c>structure</c> block).</summary>
    internal sealed class PartParityStructureDiff
    {
        internal int RealRenderers;
        internal int GhostRenderers;
        internal int Pairs;
        internal List<PartParityRendererRef> MissingOnGhost = new List<PartParityRendererRef>();
        internal List<PartParityRendererRef> ExtraOnGhost = new List<PartParityRendererRef>();
        internal List<PartParityTransformDiff> TransformDiffers = new List<PartParityTransformDiff>();
        internal List<PartParityFieldDiff> MaterialDiffers = new List<PartParityFieldDiff>();
        internal List<PartParityFieldDiff> MeshDiffers = new List<PartParityFieldDiff>();
        internal List<PartParityFieldDiff> RenderedDiffers = new List<PartParityFieldDiff>();
    }

    /// <summary>
    /// The renderer-level structural diff (design-part-ghost-parity.md section 4.6). Pure:
    /// the live capture fills one <see cref="PartParityRendererRecord"/> per renderer per
    /// side; this pairs them by path and lists what differs.
    ///
    /// <para>Naming. <c>GhostVisualBuilder</c> builds a ghost part as
    /// <c>&lt;ghostRoot&gt;/ghost_visuals/ghost_part_&lt;pid&gt;/&lt;modelRoot&gt;/...</c>,
    /// where the model node and every mirrored transform below it carry the source
    /// GameObject's own name (<c>MirrorTransformChain</c>). A real part's renderers sit at
    /// <c>&lt;part&gt;/&lt;modelRoot&gt;/...</c>. So the ghost path maps back by dropping
    /// every segment up to and including the <c>ghost_part_</c> one; both sides then strip
    /// Unity's <c>(Clone)</c> suffixes (KSP instantiates MODEL nodes as clones, and the
    /// mirror copies those names). Ghost-only objects (<c>fake_canopy_&lt;pid&gt;</c>, FX
    /// holders) keep their own names and so read as extra on the ghost, which they are. The
    /// one exception is <c>fairing_panels</c>, the container the ghost builds a procedural
    /// fairing's stock <c>FairingPanel</c>s under, which a live fairing holds directly under
    /// its model: a ghost path drops that segment, so the panels pair (an FGV context's
    /// intact fairing, design D.10 S8).</para>
    ///
    /// <para>Pairing. Paths are compared ordinally. Several renderers may share a path
    /// (sibling GameObjects of one name are common in part models); within a path the k-th
    /// renderer of each side, in input (hierarchy) order, are paired, and from the second
    /// one on the key carries <c>#k</c>. Material, shader and texture names drop a trailing <c> (Instance)</c>,
    /// because a live part may hold instantiated materials while the ghost shares the
    /// prefab's.</para>
    /// </summary>
    internal static class PartParityStructure
    {
        internal const string GhostVisualsSegment = "ghost_visuals";
        internal const string GhostPartSegmentPrefix = "ghost_part_";
        internal const string GhostFairingPanelsSegment = "fairing_panels";
        internal const string CloneSuffix = "(Clone)";
        internal const string InstanceSuffix = " (Instance)";

        internal const double PositionToleranceM = 0.001;
        internal const double RotationToleranceDeg = 0.5;
        internal const double ScaleTolerancePct = 1.0;

        internal const string FieldMaterials = "materials";
        internal const string FieldShaders = "shaders";
        internal const string FieldMainTextures = "mainTextures";
        internal const string FieldMesh = "mesh";
        internal const string FieldVertexCount = "vertexCount";
        internal const string FieldKind = "kind";
        internal const string FieldRendered = "rendered";

        internal const string NonVisualDepthMask = "depth-mask";
        internal const string NonVisualFx = "fx";

        /// <summary>
        /// Why a renderer is non-visual, from its shader names: a depth-mask shader (the
        /// same test as <c>GhostVisualBuilder.IsDepthMaskShaderName</c>: the name contains
        /// <c>DepthMask</c>, case ignored) is <c>depth-mask</c>; a Waterfall shader (name
        /// starts with <c>Waterfall/</c>) is <c>fx</c>; else null. The ghost builder skips
        /// depth masks and builds engine effects its own way, so such a renderer missing
        /// on the ghost is not a missing mesh.
        /// </summary>
        internal static string NonVisualReason(string[] shaders)
        {
            if (shaders == null || shaders.Length == 0) return null;
            bool fx = false;
            foreach (string s in shaders)
            {
                if (string.IsNullOrEmpty(s)) continue;
                if (s.IndexOf("DepthMask", StringComparison.OrdinalIgnoreCase) >= 0)
                    return NonVisualDepthMask;
                if (s.StartsWith("Waterfall/", StringComparison.Ordinal)) fx = true;
            }
            return fx ? NonVisualFx : null;
        }

        /// <summary>Strips every trailing <c>(Clone)</c> (with or without a space before it)
        /// from one name.</summary>
        internal static string StripClone(string name)
        {
            if (name == null) return string.Empty;
            string s = name;
            while (true)
            {
                string t = s.TrimEnd();
                if (!t.EndsWith(CloneSuffix, StringComparison.Ordinal)) return t;
                s = t.Substring(0, t.Length - CloneSuffix.Length);
            }
        }

        /// <summary>Strips trailing <c> (Instance)</c> and <c>(Clone)</c> suffixes from a
        /// material, shader or texture name.</summary>
        internal static string NormalizeAssetName(string name)
        {
            if (name == null) return string.Empty;
            string s = name;
            while (true)
            {
                string before = s;
                s = StripClone(s);
                if (s.EndsWith(InstanceSuffix, StringComparison.Ordinal))
                    s = s.Substring(0, s.Length - InstanceSuffix.Length);
                if (s == before) return s;
            }
        }

        /// <summary>True when a raw path (from the real part's transform, slash-separated)
        /// runs through a ghost's hierarchy: the ghost is parented to the real part, so the
        /// real renderer list must leave these out.</summary>
        internal static bool IsGhostSubtreePath(string rawPath)
        {
            if (string.IsNullOrEmpty(rawPath)) return false;
            string[] segs = rawPath.Split('/');
            for (int i = 0; i < segs.Length; i++)
            {
                if (segs[i] == GhostVisualsSegment) return true;
                if (segs[i].StartsWith(GhostPartSegmentPrefix, StringComparison.Ordinal)) return true;
            }
            return false;
        }

        /// <summary>
        /// The part-relative path both sides are paired by. Drops empty segments, every
        /// segment up to and including the LAST <c>ghost_part_*</c> one (a ghost path), a ghost
        /// path's <c>fairing_panels</c> container, and the <c>(Clone)</c> suffixes of what
        /// remains. A real path (no ghost segment) only loses its suffixes.
        /// </summary>
        internal static string NormalizePath(string rawPath)
        {
            if (string.IsNullOrEmpty(rawPath)) return string.Empty;
            string[] segs = rawPath.Split('/');
            int start = 0;
            for (int i = 0; i < segs.Length; i++)
            {
                if (segs[i].StartsWith(GhostPartSegmentPrefix, StringComparison.Ordinal))
                    start = i + 1;
            }
            var sb = new StringBuilder(rawPath.Length);
            for (int i = start; i < segs.Length; i++)
            {
                string s = StripClone(segs[i]);
                if (s.Length == 0) continue;
                if (start > 0 && s == GhostFairingPanelsSegment) continue;
                if (sb.Length > 0) sb.Append('/');
                sb.Append(s);
            }
            return sb.ToString();
        }

        /// <summary>Angle in degrees between two unit quaternions [x, y, z, w]
        /// (<c>2 acos |dot|</c>, so q and -q read as the same rotation).</summary>
        internal static double QuaternionAngleDeg(double[] a, double[] b)
        {
            if (a == null || b == null || a.Length != 4 || b.Length != 4) return double.NaN;
            double na = Math.Sqrt(a[0] * a[0] + a[1] * a[1] + a[2] * a[2] + a[3] * a[3]);
            double nb = Math.Sqrt(b[0] * b[0] + b[1] * b[1] + b[2] * b[2] + b[3] * b[3]);
            if (na <= 0.0 || nb <= 0.0) return double.NaN;
            double dot = (a[0] * b[0] + a[1] * b[1] + a[2] * b[2] + a[3] * b[3]) / (na * nb);
            dot = Math.Abs(dot);
            if (dot > 1.0) dot = 1.0;
            return 2.0 * Math.Acos(dot) * 180.0 / Math.PI;
        }

        /// <summary>
        /// True when a structure key (a part-relative <c>/</c> path, maybe <c>#k</c>-suffixed)
        /// runs through the canopy transform: one of its segments is the canopy transform's
        /// name, so the renderer is the canopy itself or under it. Mirrored by
        /// <c>partparity.is_canopy_renderer_key</c>.
        /// </summary>
        internal static bool IsCanopyRendererKey(string key, string canopyTransform)
        {
            if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(canopyTransform)) return false;
            int hash = key.IndexOf('#');
            string path = hash >= 0 ? key.Substring(0, hash) : key;
            foreach (string seg in path.Split('/'))
                if (string.Equals(seg, canopyTransform, StringComparison.Ordinal)) return true;
            return false;
        }

        /// <summary>
        /// Writes <see cref="PartParityTransformDiff.CanopyPivotDistanceM"/> on every
        /// transform difference of a canopy renderer (<see cref="IsCanopyRendererKey"/>): the
        /// distance from that real renderer's part-local position to the canopy pivot's
        /// (<paramref name="pivotLocal"/>, read on the real part in the capture's frame). A
        /// rigid turn of the canopy subtree by an angle t about the pivot moves a renderer at
        /// distance d by at most <c>2 d sin(t / 2)</c>, which the judge allows. Returns how
        /// many entries it wrote. Pure.
        /// </summary>
        internal static int StampCanopyPivotDistances(PartParityStructureDiff diff,
            IList<PartParityRendererRecord> real, string canopyTransform, double[] pivotLocal)
        {
            if (diff == null || string.IsNullOrEmpty(canopyTransform) || pivotLocal == null || pivotLocal.Length != 3)
                return 0;
            var byKey = new Dictionary<string, PartParityRendererRecord>(StringComparer.Ordinal);
            foreach (var kv in Keyed(real)) byKey[kv.Key] = kv.Value;
            int written = 0;
            foreach (PartParityTransformDiff t in diff.TransformDiffers)
            {
                if (!IsCanopyRendererKey(t.Key, canopyTransform)) continue;
                if (!byKey.TryGetValue(t.Key, out PartParityRendererRecord r)) continue;
                double d = Distance3(r.LocalPosition, pivotLocal);
                if (double.IsNaN(d)) continue;
                t.CanopyPivotDistanceM = d;
                written++;
            }
            return written;
        }

        /// <summary>Euclidean distance between two 3-vectors.</summary>
        internal static double Distance3(double[] a, double[] b)
        {
            if (a == null || b == null || a.Length != 3 || b.Length != 3) return double.NaN;
            double dx = a[0] - b[0], dy = a[1] - b[1], dz = a[2] - b[2];
            return Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        /// <summary>Largest per-axis relative scale difference in percent; an axis whose real
        /// scale is 0 compares the absolute difference (as a fraction of 1).</summary>
        internal static double ScaleDeltaPct(double[] real, double[] ghost)
        {
            if (real == null || ghost == null || real.Length != 3 || ghost.Length != 3) return double.NaN;
            double worst = 0.0;
            for (int i = 0; i < 3; i++)
            {
                double d = real[i] != 0.0
                    ? Math.Abs(ghost[i] / real[i] - 1.0)
                    : Math.Abs(ghost[i]);
                if (d > worst) worst = d;
            }
            return worst * 100.0;
        }

        /// <summary>True when a measured delta exceeds its tolerance; a NaN (a missing pose)
        /// counts as differing, so a record the capture could not read is never a silent
        /// match.</summary>
        internal static bool Exceeds(double delta, double tolerance)
            => double.IsNaN(delta) || delta > tolerance;

        private static string JoinNames(string[] names)
        {
            if (names == null || names.Length == 0) return string.Empty;
            var sb = new StringBuilder();
            for (int i = 0; i < names.Length; i++)
            {
                if (i > 0) sb.Append('|');
                sb.Append(NormalizeAssetName(names[i]));
            }
            return sb.ToString();
        }

        /// <summary>
        /// The records of one side keyed in order: the first record at a path keys by the
        /// bare path, the k-th further one by <c>path#k</c>.
        /// </summary>
        internal static List<KeyValuePair<string, PartParityRendererRecord>> Keyed(
            IList<PartParityRendererRecord> records)
        {
            var result = new List<KeyValuePair<string, PartParityRendererRecord>>();
            if (records == null) return result;
            var seen = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < records.Count; i++)
            {
                var r = records[i];
                if (r == null) continue;
                string path = r.Path ?? string.Empty;
                seen.TryGetValue(path, out int k);
                seen[path] = k + 1;
                string key = k > 0
                    ? path + "#" + k.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    : path;
                result.Add(new KeyValuePair<string, PartParityRendererRecord>(key, r));
            }
            return result;
        }

        /// <summary>
        /// Pairs the two sides and lists every difference. Records at the same path pair
        /// first on (mesh, rendered), then on mesh, then in hierarchy order, so a real part
        /// with an inactive same-named sibling the ghost builder skips pairs the ghost
        /// renderer with the rendered real one, not the inactive one. A pair is keyed by
        /// the real record's key, an extra by the ghost record's. Lists follow the real
        /// side's order (extras: the ghost side's order), so the output is deterministic
        /// for a deterministic capture.
        /// </summary>
        internal static PartParityStructureDiff Diff(
            IList<PartParityRendererRecord> real, IList<PartParityRendererRecord> ghost)
        {
            var diff = new PartParityStructureDiff();
            var realKeyed = Keyed(real);
            var ghostKeyed = Keyed(ghost);
            diff.RealRenderers = realKeyed.Count;
            diff.GhostRenderers = ghostKeyed.Count;

            // ghost indexes per path, in hierarchy order
            var ghostByPath = new Dictionary<string, List<int>>(StringComparer.Ordinal);
            for (int i = 0; i < ghostKeyed.Count; i++)
            {
                string path = ghostKeyed[i].Value.Path ?? string.Empty;
                if (!ghostByPath.TryGetValue(path, out var list))
                    ghostByPath[path] = list = new List<int>();
                list.Add(i);
            }
            var ghostUsed = new bool[ghostKeyed.Count];
            var match = new int[realKeyed.Count];
            for (int i = 0; i < match.Length; i++) match[i] = -1;

            for (int pass = 0; pass < 3; pass++)
            {
                for (int i = 0; i < realKeyed.Count; i++)
                {
                    if (match[i] >= 0) continue;
                    var r = realKeyed[i].Value;
                    if (!ghostByPath.TryGetValue(r.Path ?? string.Empty, out var candidates)) continue;
                    for (int c = 0; c < candidates.Count; c++)
                    {
                        int gi = candidates[c];
                        if (ghostUsed[gi]) continue;
                        var g = ghostKeyed[gi].Value;
                        bool sameMesh = string.Equals(
                            NormalizeAssetName(r.Mesh), NormalizeAssetName(g.Mesh), StringComparison.Ordinal);
                        bool ok = pass == 0 ? sameMesh && r.Rendered == g.Rendered
                            : pass == 1 ? sameMesh
                            : true;
                        if (!ok) continue;
                        match[i] = gi;
                        ghostUsed[gi] = true;
                        break;
                    }
                }
            }

            for (int i = 0; i < realKeyed.Count; i++)
            {
                string key = realKeyed[i].Key;
                var r = realKeyed[i].Value;
                if (match[i] < 0)
                {
                    diff.MissingOnGhost.Add(Ref(key, r));
                    continue;
                }
                diff.Pairs++;
                ComparePair(key, r, ghostKeyed[match[i]].Value, diff);
            }
            for (int i = 0; i < ghostKeyed.Count; i++)
            {
                if (!ghostUsed[i])
                    diff.ExtraOnGhost.Add(Ref(ghostKeyed[i].Key, ghostKeyed[i].Value));
            }
            return diff;
        }

        private static PartParityRendererRef Ref(string key, PartParityRendererRecord r)
            => new PartParityRendererRef
            {
                Key = key, Mesh = r.Mesh, Rendered = r.Rendered,
                NonVisual = r.DepthMask ? NonVisualDepthMask : NonVisualReason(r.Shaders),
            };

        private static void ComparePair(
            string key, PartParityRendererRecord r, PartParityRendererRecord g, PartParityStructureDiff diff)
        {
            double pos = Distance3(r.LocalPosition, g.LocalPosition);
            double rot = QuaternionAngleDeg(r.LocalRotation, g.LocalRotation);
            double scl = ScaleDeltaPct(r.LocalScale, g.LocalScale);
            if (Exceeds(pos, PositionToleranceM) || Exceeds(rot, RotationToleranceDeg)
                || Exceeds(scl, ScaleTolerancePct))
            {
                diff.TransformDiffers.Add(new PartParityTransformDiff
                {
                    Key = key, PositionDeltaM = pos, RotationDeltaDeg = rot, ScaleDeltaPct = scl,
                });
            }

            AddIfDiffers(diff.MaterialDiffers, key, FieldMaterials, JoinNames(r.Materials), JoinNames(g.Materials));
            AddIfDiffers(diff.MaterialDiffers, key, FieldShaders, JoinNames(r.Shaders), JoinNames(g.Shaders));
            AddIfDiffers(diff.MaterialDiffers, key, FieldMainTextures, JoinNames(r.MainTextures), JoinNames(g.MainTextures));

            AddIfDiffers(diff.MeshDiffers, key, FieldKind, r.Kind ?? string.Empty, g.Kind ?? string.Empty);
            AddIfDiffers(diff.MeshDiffers, key, FieldMesh,
                NormalizeAssetName(r.Mesh), NormalizeAssetName(g.Mesh));
            AddIfDiffers(diff.MeshDiffers, key, FieldVertexCount,
                r.VertexCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
                g.VertexCount.ToString(System.Globalization.CultureInfo.InvariantCulture));

            AddIfDiffers(diff.RenderedDiffers, key, FieldRendered,
                r.Rendered ? "true" : "false", g.Rendered ? "true" : "false");
        }

        private static void AddIfDiffers(
            List<PartParityFieldDiff> list, string key, string field, string real, string ghost)
        {
            if (string.Equals(real, ghost, StringComparison.Ordinal)) return;
            list.Add(new PartParityFieldDiff { Key = key, Field = field, Real = real, Ghost = ghost });
        }
    }
}
