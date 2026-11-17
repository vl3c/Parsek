using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using Parsek.TestCommands;
using UnityEngine;

namespace Parsek.InGameTests.Spikes.S3
{
    // R0 spike branch only. Spike S3, the ghost half: builds each of the eight EVA kerbal
    // parts through Parsek's own ghost builder (GhostVisualBuilder, the timeline build the
    // playback engine uses), times it, and prototypes the A4 per-ghost suit swap:
    //
    //   the builder copies the prefab's SkinnedMeshRenderer.sharedMaterials onto the ghost
    //   (GhostVisualBuilder.PartInfoBuilders.cs, the skinned clone), so a ghost renderer
    //   SHARES the prefab material. The swap takes renderer.materials (Unity's per-renderer
    //   copies, never sharedMaterials) on the helmet / body / neckring renderers, matched to
    //   the prefab KerbalEVA's three meshes, and sets mainTexture and _BumpMap with the
    //   (1, -1) scale stock KerbalEVA.SetNewTextures uses for a GameDatabase texture. The
    //   copies are destroyed with the ghost (the spike tracks them; a leak would show in the
    //   material census line).
    //
    // The proof is the prefab's three shared materials: instance id and main texture read
    // before the first build and after the twentieth, per part.
    internal static class SpikeS3Ghosts
    {
        private static string Tag => SpikeS3Format.Tag;
        private const uint PidBase = 300000;
        private const float CaptureTimeoutSeconds = 10f;

        internal sealed class Run
        {
            internal int Parts, Found, Fallbacks, SharedChanged, TextureBad;
            internal readonly List<GameObject> Kept = new List<GameObject>();      // row A, one per part
            internal readonly List<Material> Owned = new List<Material>();         // swapped copies
            internal readonly List<GameObject> Dressed = new List<GameObject>();   // row B
            internal readonly List<string> DressedLabels = new List<string>();
            internal uint NextPid = PidBase;
        }

        private sealed class Roles
        {
            internal Mesh Helmet, Body, Neck;
            internal Material HelmetMat, BodyMat, NeckMat;

            internal string RoleOf(Mesh m)
            {
                if (m == null) return null;
                if (m == Body) return "body";
                if (m == Helmet) return "helmet";
                if (m == Neck) return "neckring";
                return null;
            }

            internal Material PrefabMaterial(string role)
                => role == "body" ? BodyMat : role == "helmet" ? HelmetMat : role == "neckring" ? NeckMat : null;

            internal string Fingerprint()
                => Fp("helmet", HelmetMat) + " " + Fp("body", BodyMat) + " " + Fp("neckring", NeckMat);

            private static string Fp(string role, Material m)
            {
                if (m == null) return role + "=-";
                return role + "=" + m.GetInstanceID().ToString(CultureInfo.InvariantCulture)
                    + "/" + (SpikeS3Looks.TextureName(m.mainTexture) ?? "-");
            }
        }

        private sealed class SwapStats
        {
            internal int Renderers, Materials, Matched;
            internal bool SharedBefore = true, TextureOk = true;
        }

        // ------------------------------------------------------------------ measurement

        /// <summary>Builds one part 10 times plain and 10 times with the swap; logs one line.</summary>
        internal static void MeasurePart(Run run, SpikeS3KerbalModel m)
        {
            run.Parts++;
            AvailablePart ap = GhostVisualBuilder.ResolveAvailablePart(m.EvaPart);
            bool found = ap != null && ap.partPrefab != null;
            string source = found ? (ap.partUrl + "|" + ap.configFileFullName) : null;
            Roles roles = found ? RolesOf(ap.partPrefab) : new Roles();
            string before = roles.Fingerprint();
            Texture swapTex, swapNrm;
            string swapPath = SwapTextures(m.Gender, "VintageLike", out swapTex, out swapNrm);

            var plain = new List<double>();
            var swap = new List<double>();
            var both = new List<double>();
            bool fallback = false;
            var stats = new SwapStats();

            for (int i = 0; i < SpikeS3Format.BuildsPerPart; i++)
            {
                var sw = Stopwatch.StartNew();
                GameObject g = Build(m.EvaPart, run.NextPid++);
                sw.Stop();
                plain.Add(sw.Elapsed.TotalMilliseconds);
                if (g == null)
                {
                    fallback = true;
                    if (i == SpikeS3Format.BuildsPerPart - 1)
                        run.Kept.Add(GhostVisualBuilder.CreateGhostSphere("ParsekSpikeS3Sphere_" + m.EvaPart, Color.magenta));
                    continue;
                }
                if (i == SpikeS3Format.BuildsPerPart - 1) run.Kept.Add(g);
                else UnityEngine.Object.Destroy(g);
            }
            for (int i = 0; i < SpikeS3Format.BuildsPerPart && !fallback; i++)
            {
                var sw = Stopwatch.StartNew();
                GameObject g = Build(m.EvaPart, run.NextPid++);
                double built = sw.Elapsed.TotalMilliseconds;
                if (g == null) { fallback = true; break; }
                var owned = new List<Material>();
                SwapStats s = Swap(g, roles, swapTex, swapNrm, owned);
                sw.Stop();
                both.Add(sw.Elapsed.TotalMilliseconds);
                swap.Add(sw.Elapsed.TotalMilliseconds - built);
                if (i == 0) stats = s;
                else
                {
                    stats.SharedBefore &= s.SharedBefore;
                    stats.TextureOk &= s.TextureOk;
                }
                UnityEngine.Object.Destroy(g);
                foreach (Material mat in owned) UnityEngine.Object.Destroy(mat);
            }

            string after = found ? RolesOf(ap.partPrefab).Fingerprint() : roles.Fingerprint();
            bool unchanged = before == after;
            bool textureOk = !found || fallback || (stats.Matched > 0 && stats.TextureOk);
            if (found) run.Found++;
            if (fallback) run.Fallbacks++;
            if (!unchanged) run.SharedChanged++;
            if (!textureOk) run.TextureBad++;

            ParsekLog.Info(Tag, SpikeS3Format.Line("ghost part",
                "part", m.EvaPart, "suit", m.Suit, "gender", m.Gender,
                "found", SpikeS3Format.B(found), "fallback", fallback ? "sphere" : "none",
                "source", source, "builds", SpikeS3Format.I(plain.Count + both.Count),
                "buildMedianMs", SpikeS3Format.Ms(SpikeS3Format.Median(plain)),
                "buildMaxMs", SpikeS3Format.Ms(SpikeS3Format.Max(plain)),
                "swapMedianMs", SpikeS3Format.Ms(SpikeS3Format.Median(swap)),
                "swapMaxMs", SpikeS3Format.Ms(SpikeS3Format.Max(swap)),
                "buildSwapMedianMs", SpikeS3Format.Ms(SpikeS3Format.Median(both)),
                "materials", SpikeS3Format.I(stats.Materials),
                "renderers", SpikeS3Format.I(stats.Renderers),
                "rolesMatched", SpikeS3Format.I(stats.Matched),
                "sharedBeforeSwap", SpikeS3Format.B(stats.SharedBefore),
                "textureOk", SpikeS3Format.B(textureOk),
                "swapTexture", swapPath));
            ParsekLog.Info(Tag, SpikeS3Format.Line("shared material",
                "part", m.EvaPart, "before", before, "after", after,
                "unchanged", SpikeS3Format.B(unchanged),
                "builds", SpikeS3Format.I(plain.Count + both.Count)));
            SpikeS3Looks.SafeCsv("ghosts", SpikeS3Format.GhostHeader, SpikeS3Looks.Session, m.EvaPart, m.Suit,
                m.Gender, found, fallback ? "sphere" : "none", source, plain.Count + both.Count,
                SpikeS3Format.Median(plain), SpikeS3Format.Max(plain), SpikeS3Format.Median(swap),
                SpikeS3Format.Max(swap), SpikeS3Format.Median(both), stats.Materials, stats.Renderers,
                stats.Matched, stats.SharedBefore, textureOk, unchanged, swapPath);
        }

        /// <summary>Row B: the two Default parts dressed in each test set, plus an undressed pair.</summary>
        internal static void BuildDressed(Run run)
        {
            foreach (string gender in new[] { "Male", "Female" })
            {
                SpikeS3KerbalModel m = SpikeS3Format.ModelFor("Default", gender);
                AvailablePart ap = GhostVisualBuilder.ResolveAvailablePart(m.EvaPart);
                if (ap == null || ap.partPrefab == null) continue;
                Roles roles = RolesOf(ap.partPrefab);
                foreach (string set in SpikeS3Format.ParsekSets)
                {
                    Texture tex, nrm;
                    string path = SwapTextures(gender, set, out tex, out nrm);
                    GameObject g = Build(m.EvaPart, run.NextPid++);
                    if (g == null) continue;
                    SwapStats s = Swap(g, roles, tex, nrm, run.Owned);
                    run.Dressed.Add(g);
                    run.DressedLabels.Add(gender + "/" + set);
                    ParsekLog.Info(Tag, SpikeS3Format.Line("ghost dressed",
                        "part", m.EvaPart, "set", set, "texture", path,
                        "rolesMatched", SpikeS3Format.I(s.Matched), "textureOk", SpikeS3Format.B(s.TextureOk)));
                }
            }
        }

        /// <summary>Count of live Material objects (the leak check around the measurement).</summary>
        internal static int MaterialCensus() => Resources.FindObjectsOfTypeAll<Material>().Length;

        // ------------------------------------------------------------------ display

        /// <summary>
        /// Shows row A (the eight parts, undressed, a magenta sphere for a missing part) and
        /// row B (the Default male and female dressed in each test set) 10 m in front of the
        /// flight camera, writes two screenshots (front, then turned 180 deg) with the UI
        /// hidden, and destroys everything it made.
        /// </summary>
        internal static IEnumerator ShowAndPhotograph(Run run)
        {
            BuildDressed(run);
            Camera cam = FlightCamera.fetch != null ? FlightCamera.fetch.mainCamera : Camera.main;
            if (cam == null)
            {
                ParsekLog.Warn(Tag, "ghost display: no flight camera; no screenshot");
                Cleanup(run);
                yield break;
            }
            bool hidden = false;
            try
            {
                GameEvents.onHideUI.Fire();
                hidden = true;
                Place(run, cam, 0f);
                yield return null;
                yield return null;
                IEnumerator a = Capture("s3-ghosts-front");
                while (a.MoveNext()) yield return a.Current;
                Place(run, cam, 180f);
                yield return null;
                yield return null;
                IEnumerator b = Capture("s3-ghosts-back");
                while (b.MoveNext()) yield return b.Current;
            }
            finally
            {
                if (hidden) GameEvents.onShowUI.Fire();
                Cleanup(run);
            }
        }

        private static void Place(Run run, Camera cam, float turnDegrees)
        {
            Transform ct = cam.transform;
            Vector3 center = ct.position + ct.forward * 10f;
            Quaternion face = Quaternion.AngleAxis(turnDegrees, ct.up) * Quaternion.LookRotation(-ct.forward, ct.up);
            for (int i = 0; i < run.Kept.Count; i++)
                Put(run.Kept[i], center + ct.up * 1.1f
                    + ct.right * (float)SpikeS3Format.RowOffset(i, run.Kept.Count, 1.2), face);
            for (int i = 0; i < run.Dressed.Count; i++)
                Put(run.Dressed[i], center - ct.up * 1.3f
                    + ct.right * (float)SpikeS3Format.RowOffset(i, run.Dressed.Count, 1.5), face);
            ParsekLog.Info(Tag, SpikeS3Format.Line("ghost display",
                "rowA", SpikeS3Format.I(run.Kept.Count), "rowB", SpikeS3Format.I(run.Dressed.Count),
                "rowBOrder", string.Join(",", run.DressedLabels.ToArray()),
                "turn", SpikeS3Format.D(turnDegrees)));
        }

        private static void Put(GameObject g, Vector3 pos, Quaternion rot)
        {
            if (g == null) return;
            g.transform.position = pos;
            g.transform.rotation = rot;
            foreach (Collider c in g.GetComponentsInChildren<Collider>(true)) c.enabled = false;
            g.SetActive(true);
        }

        private static IEnumerator Capture(string label)
        {
            string dir = Path.Combine(Path.Combine(SpikeCsv.ResolveSpikeRoot(), SpikeS3Format.SpikeId), "shots");
            string path = Path.Combine(dir, label + ".png");
            try
            {
                Directory.CreateDirectory(dir);
                if (File.Exists(path)) File.Delete(path);
                ScreenCapture.CaptureScreenshot(path, 1);
            }
            catch (Exception ex)
            {
                ParsekLog.Warn(Tag, "ghost screenshot " + label + " failed: " + ex.Message);
                yield break;
            }
            long previous = -1L;
            float deadline = Time.realtimeSinceStartup + CaptureTimeoutSeconds;
            while (true)
            {
                yield return null;
                var fi = new FileInfo(path);
                long bytes = fi.Exists ? fi.Length : -1L;
                ScreenshotPollOutcome o = TestCommandCaptureScreenshot.DecidePoll(
                    fi.Exists, bytes, previous, Time.realtimeSinceStartup >= deadline);
                if (o == ScreenshotPollOutcome.NotYet) { previous = bytes; continue; }
                ParsekLog.Info(Tag, SpikeS3Format.Line("ghost screenshot", "label", label,
                    "file", path, "written", SpikeS3Format.B(o == ScreenshotPollOutcome.Settled),
                    "bytes", bytes.ToString(CultureInfo.InvariantCulture)));
                yield break;
            }
        }

        private static void Cleanup(Run run)
        {
            foreach (GameObject g in run.Kept) if (g != null) UnityEngine.Object.Destroy(g);
            foreach (GameObject g in run.Dressed) if (g != null) UnityEngine.Object.Destroy(g);
            foreach (Material m in run.Owned) if (m != null) UnityEngine.Object.Destroy(m);
            run.Kept.Clear();
            run.Dressed.Clear();
            run.Owned.Clear();
        }

        // ------------------------------------------------------------------ build and swap

        /// <summary>One EVA kerbal ghost from a one-part snapshot, or null (zero visuals).</summary>
        internal static GameObject Build(string evaPart, uint pid)
        {
            var vessel = new ConfigNode("VESSEL");
            vessel.AddValue("name", "SpikeS3 " + evaPart);
            ConfigNode part = vessel.AddNode("PART");
            part.AddValue("name", evaPart);
            part.AddValue("persistentId", pid.ToString(CultureInfo.InvariantCulture));
            part.AddValue("pos", "0,0,0");
            part.AddValue("rot", "0,0,0,1");
            part.AddValue("attRot", "0,0,0,1");
            PendingGhostVisualBuild build = GhostVisualBuilder.TryBeginTimelineGhostBuild(
                null, vessel, "ParsekSpikeS3Ghost_" + evaPart, HeaviestSpawnBuildType.RecordingStartSnapshot);
            if (build == null) return null;
            GhostVisualBuilder.AdvanceTimelineGhostBuild(build, long.MaxValue);
            GhostBuildResult result = GhostVisualBuilder.CompleteTimelineGhostBuild(build, null);
            return result != null ? result.root : null;
        }

        private static Roles RolesOf(Part prefab)
        {
            var r = new Roles();
            KerbalEVA kev = prefab != null ? prefab.FindModuleImplementing<KerbalEVA>() : null;
            if (kev == null) return r;
            if (kev.helmetMesh != null) { r.Helmet = kev.helmetMesh.sharedMesh; r.HelmetMat = kev.helmetMesh.sharedMaterial; }
            if (kev.bodyMesh != null) { r.Body = kev.bodyMesh.sharedMesh; r.BodyMat = kev.bodyMesh.sharedMaterial; }
            if (kev.neckringMesh != null) { r.Neck = kev.neckringMesh.sharedMesh; r.NeckMat = kev.neckringMesh.sharedMaterial; }
            return r;
        }

        private static SwapStats Swap(GameObject root, Roles roles, Texture tex, Texture nrm, List<Material> owned)
        {
            var s = new SwapStats();
            foreach (Renderer r in root.GetComponentsInChildren<Renderer>(true))
            {
                s.Renderers++;
                s.Materials += r.sharedMaterials.Length;
            }
            foreach (SkinnedMeshRenderer smr in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                string role = roles.RoleOf(smr.sharedMesh);
                if (role == null) continue;
                s.Matched++;
                if (smr.sharedMaterial != roles.PrefabMaterial(role)) s.SharedBefore = false;
                Material[] copies = smr.materials; // per-renderer copies; the prefab's stay shared
                foreach (Material c in copies)
                {
                    owned.Add(c);
                    if (tex != null)
                    {
                        c.mainTexture = tex;
                        c.mainTextureScale = new Vector2(1f, -1f);
                    }
                    if (nrm != null && c.HasProperty("_BumpMap"))
                    {
                        c.SetTexture("_BumpMap", nrm);
                        c.SetTextureScale("_BumpMap", new Vector2(1f, -1f));
                    }
                    if (tex == null || c.mainTexture != tex) s.TextureOk = false;
                }
            }
            return s;
        }

        /// <summary>The suit and normal textures of a test set for a gender; the path, or "missing".</summary>
        private static string SwapTextures(string gender, string set, out Texture tex, out Texture nrm)
        {
            tex = null;
            nrm = null;
            SuitCombos combos = SpikeS3Looks.FindCombos();
            SuitCombo c = combos != null ? combos.GetCombo(SpikeS3Format.ParsekComboName(set, gender)) : null;
            if (c == null) return "missing:" + SpikeS3Format.ParsekComboName(set, gender);
            tex = GameDatabase.Instance.GetTexture(c.suitTexture, false);
            nrm = string.IsNullOrEmpty(c.normalTexture) ? null : GameDatabase.Instance.GetTexture(c.normalTexture, true);
            return tex != null ? c.suitTexture : "unloaded:" + c.suitTexture;
        }
    }
}
