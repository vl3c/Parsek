using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Parsek;
using UnityEngine;

namespace Parsek.Tests.Harness
{
    /// <summary>
    /// Resolver-level regression harness primitive (Step 1 of the recording &amp;
    /// ghost policies refactor plan).
    ///
    /// Hashes the deterministic outputs of
    /// <see cref="RelativeAnchorResolver.TryResolveRecordingPose"/> sampled at
    /// <c>N</c> evenly-spaced UTs over a configurable range. The hash exposes
    /// regressions in the resolver chain (Absolute path, Relative path,
    /// loop-anchor rejection, Re-Fly walk-back, same-chain continuation)
    /// without needing a Unity body or any positioner state.
    ///
    /// Per the plan's Step 1 §"Scope reduction": this DOES NOT cover
    /// positioner-level world-space placement, terrain clamping, or visual
    /// rendering. It catches "the resolver returns the wrong (pos, rot) for
    /// this UT" but not "the positioner snaps the ghost into the ground."
    ///
    /// Hash contract: SHA-256 over the concatenation of
    /// <c>(x, y, z, qx, qy, qz, qw)</c> doubles per sample, formatted with
    /// <c>R</c> on <see cref="CultureInfo.InvariantCulture"/>, separated by
    /// '|'. Unresolved samples emit <c>NaN|NaN|NaN|NaN|NaN|NaN|NaN</c> so that
    /// "resolver returns false" is part of the contract — flipping a sample
    /// from resolved to unresolved (or vice versa) changes the hash.
    /// </summary>
    internal static class ResolverPoseHasher
    {
        public const int DefaultSampleCount = 32;

        /// <summary>
        /// Sample <paramref name="target"/>'s pose at <paramref name="sampleCount"/>
        /// evenly-spaced UTs in <c>[startUT, endUT]</c> and return a stable
        /// SHA-256 hex digest of the resolver outputs. Both endpoints are
        /// included; with <c>sampleCount == 1</c> only <paramref name="startUT"/>
        /// is sampled.
        /// </summary>
        internal static string HashResolverPoses(
            RelativeAnchorResolverContext context,
            Recording target,
            double startUT,
            double endUT,
            int sampleCount = DefaultSampleCount)
        {
            if (target == null)
                throw new ArgumentNullException(nameof(target));
            if (sampleCount < 1)
                throw new ArgumentOutOfRangeException(nameof(sampleCount), "sampleCount must be >= 1");
            if (double.IsNaN(startUT) || double.IsInfinity(startUT))
                throw new ArgumentException("startUT must be finite", nameof(startUT));
            if (double.IsNaN(endUT) || double.IsInfinity(endUT))
                throw new ArgumentException("endUT must be finite", nameof(endUT));

            var ic = CultureInfo.InvariantCulture;
            var sb = new StringBuilder();
            for (int i = 0; i < sampleCount; i++)
            {
                double ut = sampleCount == 1
                    ? startUT
                    : startUT + (endUT - startUT) * ((double)i / (double)(sampleCount - 1));

                bool resolved = RelativeAnchorResolver.TryResolveRecordingPose(
                    context,
                    target,
                    ut,
                    new HashSet<string>(StringComparer.Ordinal),
                    out AnchorPose pose);

                if (i > 0)
                    sb.Append('\n');

                if (!resolved)
                {
                    sb.Append("NaN|NaN|NaN|NaN|NaN|NaN|NaN");
                    continue;
                }

                sb.Append(pose.WorldPos.x.ToString("R", ic));
                sb.Append('|');
                sb.Append(pose.WorldPos.y.ToString("R", ic));
                sb.Append('|');
                sb.Append(pose.WorldPos.z.ToString("R", ic));
                sb.Append('|');
                sb.Append(pose.WorldRotation.x.ToString("R", ic));
                sb.Append('|');
                sb.Append(pose.WorldRotation.y.ToString("R", ic));
                sb.Append('|');
                sb.Append(pose.WorldRotation.z.ToString("R", ic));
                sb.Append('|');
                sb.Append(pose.WorldRotation.w.ToString("R", ic));
            }

            return Sha256Hex(sb.ToString());
        }

        /// <summary>
        /// Returns the human-readable sample line dump used to compute the hash.
        /// Useful when a test fails: print the dump and diff it against the
        /// previous baseline to see which sample drifted, instead of
        /// regenerating the baseline blind.
        /// </summary>
        internal static string DumpResolverPoses(
            RelativeAnchorResolverContext context,
            Recording target,
            double startUT,
            double endUT,
            int sampleCount = DefaultSampleCount)
        {
            if (target == null)
                throw new ArgumentNullException(nameof(target));

            var ic = CultureInfo.InvariantCulture;
            var sb = new StringBuilder();
            sb.Append("# target=").Append(target.RecordingId ?? "(none)")
                .Append(" startUT=").Append(startUT.ToString("R", ic))
                .Append(" endUT=").Append(endUT.ToString("R", ic))
                .Append(" samples=").Append(sampleCount.ToString(ic))
                .Append('\n');

            for (int i = 0; i < sampleCount; i++)
            {
                double ut = sampleCount == 1
                    ? startUT
                    : startUT + (endUT - startUT) * ((double)i / (double)(sampleCount - 1));

                bool resolved = RelativeAnchorResolver.TryResolveRecordingPose(
                    context,
                    target,
                    ut,
                    new HashSet<string>(StringComparer.Ordinal),
                    out AnchorPose pose);

                sb.Append(i.ToString(ic)).Append(' ');
                sb.Append("ut=").Append(ut.ToString("R", ic)).Append(' ');
                if (!resolved)
                {
                    sb.Append("UNRESOLVED\n");
                    continue;
                }

                sb.Append("pos=(").Append(pose.WorldPos.x.ToString("R", ic))
                    .Append(',').Append(pose.WorldPos.y.ToString("R", ic))
                    .Append(',').Append(pose.WorldPos.z.ToString("R", ic))
                    .Append(") rot=(").Append(pose.WorldRotation.x.ToString("R", ic))
                    .Append(',').Append(pose.WorldRotation.y.ToString("R", ic))
                    .Append(',').Append(pose.WorldRotation.z.ToString("R", ic))
                    .Append(',').Append(pose.WorldRotation.w.ToString("R", ic))
                    .Append(") sectionIdx=").Append(pose.ResolvedSectionIndex.ToString(ic))
                    .Append(" resolvedRecId=").Append(pose.ResolvedRecordingId ?? "(none)")
                    .Append('\n');
            }

            return sb.ToString();
        }

        private static string Sha256Hex(string s)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(s);
            using (var sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(bytes);
                var hex = new StringBuilder(hash.Length * 2);
                for (int i = 0; i < hash.Length; i++)
                    hex.Append(hash[i].ToString("x2", CultureInfo.InvariantCulture));
                return hex.ToString();
            }
        }
    }
}
