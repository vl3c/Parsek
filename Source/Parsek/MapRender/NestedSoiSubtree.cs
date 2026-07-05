using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Parsek.MapRender
{
    /// <summary>
    /// Phase 7 (migration plan §9 / design §10): the typed model of a MOON-RICH (nested-SOI) body tree —
    /// Jool -> {Laythe, Vall, Tylo, Bop, Pol} -> sub-SOIs. The successor home for the cross-SOI body
    /// vocabulary when the recorded trajectory crosses MORE THAN ONE level of the SOI hierarchy (a moon
    /// tour inside a planet's SOI), distinct from the single-level <see cref="SoiCrossing"/> (Kerbin->Mun,
    /// ->Sun) the model already renders.
    ///
    /// <para><b>DEFINE-ONLY, v1 fail-closed-to-faithful (design §4 / §10).</b> Recorded Jool tours already
    /// render faithfully through the all-orbital chain + per-crossing <c>FlexibleSoi</c> G0 seams; the only
    /// thing fail-closed disables is a SYNTHETIC moon-to-moon re-aim, which DOES NOT EXIST yet (the
    /// recursive nested-SOI producer + a joint configuration-period alignment are deferred, design §17).
    /// This type is therefore a pure DATA + DECISION model: it names the body tree and the per-leg
    /// crossings so the <see cref="FailClosedClassifier"/> can say "this is a nested-SOI mission ->
    /// FaithfulFallback" with a real, test-assertable identity, and so the future recursive producer has a
    /// typed home. It produces NO synthetic geometry.</para>
    ///
    /// <para><b>Pure + headless.</b> No Unity / KSP-API reads. The body parent chain is supplied as a
    /// delegate (mirroring <see cref="Parsek.IBodyInfo.ReferenceBodyName"/>) so the nesting
    /// decision is directly unit-testable; the live caller passes the real
    /// <c>CelestialBody.referenceBody.bodyName</c> walk. <c>UvLambert</c> is body-agnostic (mu is a
    /// parameter), so a future recursive per-leg solver reuses the existing pure kernels — but that is
    /// deferred, not built here.</para>
    /// </summary>
    internal sealed class NestedSoiSubtree
    {
        /// <summary>The root body of the subtree (the planet whose moons the mission toured, e.g. Jool).</summary>
        internal string RootBody { get; }

        /// <summary>
        /// The ordered distinct bodies the recorded trajectory visited inside the root's SOI hierarchy,
        /// in first-visit order (e.g. Jool, Laythe, Tylo). FILTERED to the root's hierarchy (S15): the
        /// departure body and the interplanetary legs of a Kerbin-departure tour are NOT part of the
        /// subtree payload. When the tour touched the root body itself it leads the list; a moons-only
        /// tour starts at the first-visited moon.
        /// </summary>
        internal IReadOnlyList<string> VisitedBodies { get; }

        /// <summary>
        /// The per-leg body changes inside the subtree (a moon-to-moon / moon-to-planet hop), each a
        /// <see cref="SoiCrossing"/>. v1 RECORDS these (so the fail-closed decision + the future producer
        /// have the crossing list); it does NOT re-aim across them.
        /// </summary>
        internal IReadOnlyList<SoiCrossing> Crossings { get; }

        internal NestedSoiSubtree(
            string rootBody,
            IReadOnlyList<string> visitedBodies,
            IReadOnlyList<SoiCrossing> crossings)
        {
            RootBody = rootBody;
            VisitedBodies = visitedBodies ?? Array.Empty<string>();
            Crossings = crossings ?? Array.Empty<SoiCrossing>();
        }

        /// <summary>
        /// True iff the tour visited at least one moon BELOW the root (more than just the root body) — the
        /// defining "nested" property. A subtree with a single visited body (the root only) is NOT nested
        /// (it is an ordinary single-body presence, not a moon tour).
        /// </summary>
        internal bool IsNested => VisitedBodies.Count >= 2;

        /// <summary>The number of intra-subtree SOI crossings (moon hops) the tour made.</summary>
        internal int CrossingCount => Crossings.Count;

        /// <summary>
        /// design §10: DECIDE whether a recorded trajectory's body sequence is a nested-SOI (moon-rich)
        /// tour and, if so, build the typed subtree. Returns null when the trajectory is NOT nested (zero
        /// or one body, or every body change is a SINGLE-LEVEL crossing that the ordinary
        /// <see cref="SoiCrossing"/> / <c>FlexibleSoi</c> path already handles).
        ///
        /// <para>The nesting test: walk the ordered distinct bodies the recorded
        /// <see cref="OrbitSegment"/>s visited; if two or more of them share a COMMON ANCESTOR that is
        /// itself NOT the system root (the Sun) — i.e. the tour stayed under a planet and visited that
        /// planet's moons — it is nested. A direct Kerbin->Mun->Kerbin is single-level (Mun's parent IS
        /// the visited Kerbin, but the chain has depth 1 under Kerbin and never crosses a SIBLING moon),
        /// so this returns a nested subtree only when at least one crossing is a SIBLING / cross-moon hop
        /// under a shared non-root ancestor (the real Jool-tour signature).</para>
        ///
        /// <para>Pure: <paramref name="referenceBodyName"/> supplies the parent of a body (null for the
        /// system root / unknown), exactly the <see cref="Parsek.IBodyInfo.ReferenceBodyName"/>
        /// contract. A null delegate yields null (cannot decide -> not nested, the safe default).</para>
        /// </summary>
        internal static NestedSoiSubtree TryBuildFromBodySequence(
            IReadOnlyList<string> orderedBodies,
            Func<string, string> referenceBodyName)
        {
            if (orderedBodies == null || orderedBodies.Count < 2 || referenceBodyName == null)
                return null;

            // Ordered distinct bodies in first-visit order.
            var distinct = new List<string>(orderedBodies.Count);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (string b in orderedBodies)
            {
                if (string.IsNullOrEmpty(b) || !seen.Add(b))
                    continue;
                distinct.Add(b);
            }
            if (distinct.Count < 2)
                return null;

            // Find a shared NON-ROOT ancestor under which two distinct visited bodies are SIBLINGS (or a
            // parent + its child moon AND a sibling moon). The simplest robust signature: any pair of
            // visited bodies whose immediate reference body is the SAME non-null body, AND that shared
            // parent is itself not the system root (it has its own parent). That is exactly a moon-to-moon
            // hop under a planet — the nested-SOI tour the model fail-closes; a Kerbin<->Mun pair has no
            // two siblings (only Mun under Kerbin), so it stays single-level.
            string rootBody = FindNestedRoot(distinct, referenceBodyName);
            if (string.IsNullOrEmpty(rootBody))
                return null;

            // S15: the payload is scoped to the ROOT'S SOI HIERARCHY (the type contract above), so a real
            // Kerbin-departure tour ([Kerbin, Sun, Jool, Laythe, Tylo]) must NOT list the departure body /
            // the interplanetary transfer in VisitedBodies or carry Kerbin->Sun / Sun->Jool as
            // "intra-subtree" crossings. Filter to bodies whose parent chain reaches the root (the root
            // itself included).
            var inSubtree = new HashSet<string>(StringComparer.Ordinal);
            var visited = new List<string>(distinct.Count);
            foreach (string b in distinct)
            {
                if (!IsInRootHierarchy(b, rootBody, referenceBodyName))
                    continue;
                inSubtree.Add(b);
                visited.Add(b);
            }

            // Build the per-leg crossings (every adjacent body change in the ORIGINAL order, so a back-
            // and-forth moon tour records each hop), restricted to hops WITHIN the subtree set. The
            // exit/entry state vectors are left default (v1 RECORDS the crossing identity, does not
            // enforce continuity - design §10 / SoiCrossing).
            var crossings = new List<SoiCrossing>();
            for (int i = 1; i < orderedBodies.Count; i++)
            {
                string from = orderedBodies[i - 1];
                string to = orderedBodies[i];
                if (string.IsNullOrEmpty(from) || string.IsNullOrEmpty(to)
                    || string.Equals(from, to, StringComparison.Ordinal))
                    continue;
                if (!inSubtree.Contains(from) || !inSubtree.Contains(to))
                    continue;
                crossings.Add(new SoiCrossing(from, to, crossingUt: double.NaN, soiRadius: double.NaN));
            }

            return new NestedSoiSubtree(rootBody, visited, crossings);
        }

        /// <summary>
        /// True iff <paramref name="body"/> is the root itself or its parent chain reaches
        /// <paramref name="rootBody"/>. Walks at most 16 levels (a real SOI tree is ~3 deep; the cap
        /// guards a cyclic / self-referential delegate). Pure.
        /// </summary>
        private static bool IsInRootHierarchy(
            string body, string rootBody, Func<string, string> referenceBodyName)
        {
            string cursor = body;
            for (int depth = 0; depth < 16 && !string.IsNullOrEmpty(cursor); depth++)
            {
                if (string.Equals(cursor, rootBody, StringComparison.Ordinal))
                    return true;
                string parent = SafeReference(referenceBodyName, cursor);
                if (string.Equals(parent, cursor, StringComparison.Ordinal))
                    return false; // self-referential system root (live KSP Sun convention)
                cursor = parent;
            }
            return false;
        }

        /// <summary>
        /// Find a non-root ancestor under which at least two distinct visited bodies are SIBLINGS (the
        /// moon-tour signature). Returns the shared parent body name, or null when no such ancestor exists
        /// (a single-level mission). A "non-root" ancestor is one that has a PROPER parent — the system root
        /// (the Sun) never counts, whether the parent-chain delegate reports its parent as null (the headless
        /// fake convention) or as ITSELF (live KSP: <c>Sun.referenceBody == Sun</c>, self-referential). Two
        /// planets sharing the Sun is interplanetary, not a nested-SOI tour. Pure.
        /// </summary>
        private static string FindNestedRoot(
            IReadOnlyList<string> distinctBodies, Func<string, string> referenceBodyName)
        {
            // Group visited bodies by their immediate parent; a parent with >= 2 visited children that is
            // itself non-root is the nested-SOI root.
            var childrenByParent = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < distinctBodies.Count; i++)
            {
                string parent = SafeReference(referenceBodyName, distinctBodies[i]);
                if (string.IsNullOrEmpty(parent))
                    continue;
                childrenByParent.TryGetValue(parent, out int c);
                childrenByParent[parent] = c + 1;
            }

            // DETERMINISTIC candidate order (review N11): walk the visited bodies in FIRST-VISIT order
            // and return the first qualifying shared parent, never Dictionary iteration order (which is
            // insertion-order-ish today but not contractual - two qualifying parents in one tour must
            // resolve to the same root on every run and every runtime).
            for (int i = 0; i < distinctBodies.Count; i++)
            {
                string parent = SafeReference(referenceBodyName, distinctBodies[i]);
                if (string.IsNullOrEmpty(parent))
                    continue;
                if (!childrenByParent.TryGetValue(parent, out int count) || count < 2)
                    continue;
                // The shared parent must itself have a PROPER parent (be non-root): a planet's moons share the
                // planet (non-root); the Sun's planets share the Sun (root) -> interplanetary, skip. The root
                // is detected whether the delegate reports its parent as null (headless fake) OR as ITSELF
                // (live KSP: Sun.referenceBody == Sun). Without the self-reference guard, live
                // ["Kerbin","Sun","Duna"] (Sun has >= 2 visited children, grandparent("Sun") == "Sun" != null)
                // was wrongly flagged nested -> fail-closed every ordinary interplanetary transfer.
                string grandparent = SafeReference(referenceBodyName, parent);
                if (!string.IsNullOrEmpty(grandparent)
                    && !string.Equals(grandparent, parent, StringComparison.Ordinal))
                    return parent;
            }
            return null;
        }

        private static string SafeReference(Func<string, string> referenceBodyName, string body)
        {
            if (string.IsNullOrEmpty(body))
                return null;
            try { return referenceBodyName(body); }
            catch { return null; }
        }

        /// <summary>
        /// Grep-stable summary token for the Tier-A <c>fail-closed-to-faithful</c> detail line (design
        /// §14). Pure; InvariantCulture.
        /// </summary>
        internal string ToSummaryToken()
        {
            var sb = new StringBuilder();
            sb.Append("root=").Append(RootBody ?? "?")
              .Append(" visited=").Append(VisitedBodies.Count)
              .Append(" crossings=").Append(CrossingCount)
              .Append(" bodies=");
            for (int i = 0; i < VisitedBodies.Count; i++)
            {
                if (i > 0) sb.Append('/');
                sb.Append(VisitedBodies[i] ?? "?");
            }
            return sb.ToString();
        }

        public override string ToString()
            => string.Format(CultureInfo.InvariantCulture, "NestedSoiSubtree[{0}]", ToSummaryToken());
    }
}
