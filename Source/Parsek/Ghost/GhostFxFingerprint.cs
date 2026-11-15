using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace Parsek
{
    /// <summary>
    /// Canonical per-part fingerprints of built ghost engine/RCS FX, logged after every
    /// ghost FX build in BOTH stock and Waterfall installs. Purpose: mechanical A/B
    /// equivalence checking between the stock EFFECTS/legacy path and the Waterfall
    /// pristine fallback. Two runs of the same save (config pack disabled vs enabled)
    /// must produce identical fingerprint sets per part; any differing line is a visual
    /// divergence finding. Entries capture what determines the look: asset name, parent
    /// transform, local placement, scale, and the per-system size/speed multipliers the
    /// special-case tuning adjusts.
    /// </summary>
    internal static class GhostFxFingerprint
    {
        private const double LogIntervalSeconds = 5.0;

        /// <summary>
        /// Canonical object name for fingerprinting: strips ALL trailing instance suffixes
        /// ("(Clone)" repeats from clone-of-clone chains, KSP's "(Keep Pos)"), so the same
        /// asset fingerprints identically whether it was cloned from a compiled child
        /// (stock path) or instantiated from the raw builtin asset (Waterfall fallback).
        /// </summary>
        internal static string CanonicalObjectName(string rawName)
        {
            if (string.IsNullOrEmpty(rawName))
                return "?";

            string name = rawName.Trim();
            bool stripped = true;
            while (stripped)
            {
                stripped = false;
                if (name.EndsWith("(Clone)", System.StringComparison.Ordinal))
                {
                    name = name.Substring(0, name.Length - 7).TrimEnd();
                    stripped = true;
                }
                if (name.EndsWith("(Keep Pos)", System.StringComparison.Ordinal))
                {
                    name = name.Substring(0, name.Length - 10).TrimEnd();
                    stripped = true;
                }
            }
            return name.Length > 0 ? name : "?";
        }

        /// <summary>
        /// Formats one particle system's visual identity. Pure; values rounded
        /// (InvariantCulture) so float noise never produces false diffs. Orientation is
        /// captured as the EMISSION DIRECTION (the local +Y image, the axis particles
        /// emit along), not raw Euler angles: the stock path and the Waterfall fallback
        /// produce rotations that differ only by roll around the emission axis, which is
        /// invisible for axially symmetric FX and must not diff.
        /// </summary>
        internal static string FormatEntry(
            string objectName, string parentName,
            Vector3 localPos, Vector3 emissionDir, Vector3 localScale,
            float startSizeMultiplier, float startSpeedMultiplier)
        {
            string name = CanonicalObjectName(objectName);
            string parent = CanonicalObjectName(parentName);
            return string.Format(CultureInfo.InvariantCulture,
                "{0}<{1} pos=({2:F2},{3:F2},{4:F2}) dir=({5:F1},{6:F1},{7:F1}) " +
                "scale=({8:F2},{9:F2},{10:F2}) size={11:F2} speed={12:F2}",
                name, parent,
                localPos.x, localPos.y, localPos.z,
                emissionDir.x, emissionDir.y, emissionDir.z,
                localScale.x, localScale.y, localScale.z,
                startSizeMultiplier, startSpeedMultiplier);
        }

        /// <summary>Order-independent canonical form: sorted entries joined with '|'.</summary>
        internal static string BuildFingerprint(List<string> entries)
        {
            if (entries == null || entries.Count == 0)
                return "(none)";
            entries.Sort(System.StringComparer.Ordinal);
            return string.Join("|", entries.ToArray());
        }

        /// <summary>Pure formatting; key counts extracted by the runtime caller
        /// (FloatCurve/AnimationCurve members are Unity ECalls, untestable headless).</summary>
        internal static string DescribeCurves(int emissionKeys, int speedKeys)
        {
            return string.Format(CultureInfo.InvariantCulture, "em{0}/sp{1}", emissionKeys, speedKeys);
        }

        private static int CountCurveKeys(FloatCurve curve)
        {
            return curve != null && curve.Curve != null ? curve.Curve.length : 0;
        }

        internal static void LogEngineInfos(string partName, List<EngineGhostInfo> infos)
        {
            if (infos == null)
                return;
            for (int i = 0; i < infos.Count; i++)
            {
                LogInfo(partName, "engine", infos[i].moduleIndex,
                    infos[i].particleSystems, infos[i].emissionCurve, infos[i].speedCurve);
            }
        }

        internal static void LogRcsInfos(string partName, List<RcsGhostInfo> infos)
        {
            if (infos == null)
                return;
            for (int i = 0; i < infos.Count; i++)
            {
                LogInfo(partName, "rcs", infos[i].moduleIndex,
                    infos[i].particleSystems, infos[i].emissionCurve, infos[i].speedCurve);
            }
        }

        private static void LogInfo(
            string partName, string kind, int moduleIndex,
            List<ParticleSystem> particleSystems,
            FloatCurve emissionCurve, FloatCurve speedCurve)
        {
            var entries = new List<string>();
            int nullSystems = 0;
            if (particleSystems != null)
            {
                for (int p = 0; p < particleSystems.Count; p++)
                {
                    ParticleSystem ps = particleSystems[p];
                    if (ps == null)
                    {
                        nullSystems++;
                        continue;
                    }
                    Transform t = ps.transform;
                    var main = ps.main;
                    entries.Add(FormatEntry(
                        t.gameObject.name,
                        t.parent != null ? t.parent.name : "(root)",
                        t.localPosition,
                        t.localRotation * Vector3.up,
                        t.localScale,
                        main.startSizeMultiplier,
                        main.startSpeedMultiplier));
                }
            }

            ParsekLog.VerboseRateLimited("FxFingerprint",
                $"fp-{kind}-{partName}-{moduleIndex}",
                $"part='{partName}' kind={kind} midx={moduleIndex} systems={entries.Count} " +
                $"nullSystems={nullSystems} " +
                $"curves={DescribeCurves(CountCurveKeys(emissionCurve), CountCurveKeys(speedCurve))} " +
                $"fp=[{BuildFingerprint(entries)}]",
                LogIntervalSeconds);
        }
    }
}
