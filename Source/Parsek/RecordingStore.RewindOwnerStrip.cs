using System;
using System.Collections.Generic;

namespace Parsek
{
    public static partial class RecordingStore
    {
        /// <summary>
        /// What the plain rewind's pre-load strip removes by owner identity: the rewind owner's
        /// own vessel (the one the rewind quicksave was captured on) plus the EVA kerbals of the
        /// owner's chain.
        ///
        /// <para>The owner's vessel is matched by name AND by its launch guid (the quicksave
        /// VESSEL <c>pid</c> value against <see cref="Recording.RecordedVesselGuid"/>, both taken
        /// from the same live vessel at the recording's start). A relaunch of the same craft
        /// shares the craft name, so a name alone would also strip an EARLIER launch's vessel
        /// that is still standing in the quicksave; nothing re-spawns that vessel after the
        /// rewind (it is committed history), so it was lost.</para>
        ///
        /// <para>EVA child names stay name-only: a kerbal's EVA vessel is named after the
        /// kerbal, and a kerbal is unique in the roster, so the name is the kerbal's identity. A
        /// chain EVA kerbal present in the quicksave boards the owner's vessel later in the
        /// flight (his EVA vessel then gets a new guid), and the replay re-produces him, so a
        /// guid gate there would leave a duplicate kerbal.</para>
        /// </summary>
        internal sealed class RewindOwnerStrip
        {
            /// <summary>The owner's vessel name (null when the owner is unknown).</summary>
            internal string OwnerName;

            /// <summary>The owner's launch guid, normalized; null when unknown (name-only).</summary>
            internal string OwnerGuid;

            /// <summary>Names stripped by name alone (the chain's EVA kerbals; or every name for
            /// a legacy caller that passes a plain name set).</summary>
            internal readonly HashSet<string> NameOnly = new HashSet<string>(StringComparer.Ordinal);

            /// <summary>Every name the strip considers, for the summary log.</summary>
            internal IEnumerable<string> AllNames()
            {
                if (!string.IsNullOrEmpty(OwnerName) && !NameOnly.Contains(OwnerName))
                    yield return OwnerName;
                foreach (var n in NameOnly)
                    yield return n;
            }

            /// <summary>A name-only strip over <paramref name="names"/> (the legacy contract).</summary>
            internal static RewindOwnerStrip FromNames(IEnumerable<string> names)
            {
                var strip = new RewindOwnerStrip();
                if (names != null)
                {
                    foreach (var n in names)
                    {
                        if (n != null)
                            strip.NameOnly.Add(n);
                    }
                }
                return strip;
            }
        }

        /// <summary>Per-VESSEL answer of the owner strip.</summary>
        internal enum RewindOwnerStripDecision
        {
            /// <summary>Not an owner-strip candidate (the PID strip may still apply).</summary>
            NotMatched,

            /// <summary>The owner's vessel, identified by its launch guid.</summary>
            StripOwnerGuid,

            /// <summary>Stripped by name (owner name with no conclusive guid disagreement, or an
            /// EVA child name).</summary>
            StripName,

            /// <summary>Carries the owner's name but is a different launch of the same craft
            /// (its guid conclusively differs from the owner's while the owner's own vessel is
            /// identified in the same save): committed history, kept.</summary>
            KeepOtherLaunch,
        }

        /// <summary>
        /// Builds the owner strip for a plain rewind: the owner's name and launch guid, and the
        /// EVA kerbal names of the owner's chain.
        /// </summary>
        internal static RewindOwnerStrip BuildRewindOwnerStrip(Recording owner)
        {
            var strip = new RewindOwnerStrip();
            if (owner == null)
                return strip;

            strip.OwnerName = owner.VesselName;
            strip.OwnerGuid = VesselLaunchIdentity.NormalizeGuid(owner.RecordedVesselGuid);
            if (!string.IsNullOrEmpty(owner.ChainId))
            {
                foreach (var committed in committedRecordings)
                {
                    if (committed.ChainId == owner.ChainId &&
                        !string.IsNullOrEmpty(committed.EvaCrewName) &&
                        committed.VesselName != owner.VesselName &&
                        committed.VesselName != null)
                    {
                        strip.NameOnly.Add(committed.VesselName);
                    }
                }
                if (strip.NameOnly.Count > 0 && !SuppressLogging)
                    ParsekLog.Info("Rewind",
                        $"Rewind strip includes {strip.NameOnly.Count} EVA child vessel name(s) from chain '{owner.ChainId}'");
            }

            if (!SuppressLogging)
                ParsekLog.Verbose("Rewind",
                    $"Rewind owner strip: owner='{strip.OwnerName}' guid={strip.OwnerGuid ?? "(none, name-only)"} " +
                    $"evaChildNames={strip.NameOnly.Count}");
            return strip;
        }

        /// <summary>
        /// Whether some VESSEL in the quicksave carries the owner's launch guid. Only then does a
        /// conclusive guid mismatch mean "a different launch": when no vessel carries the owner's
        /// guid, the guid cannot anchor the owner in this save and the name decides, as before.
        /// </summary>
        internal static bool RewindOwnerStripIsAnchored(RewindOwnerStrip strip, IEnumerable<string> vesselGuids)
        {
            if (strip == null || strip.OwnerGuid == null || vesselGuids == null)
                return false;
            foreach (var g in vesselGuids)
            {
                string n = VesselLaunchIdentity.NormalizeGuid(g);
                if (n != null && string.Equals(n, strip.OwnerGuid, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Pure per-VESSEL decision of the owner strip.
        ///
        /// <list type="number">
        /// <item>An EVA child name is stripped by name (kerbal names are roster-unique).</item>
        /// <item>The owner's name is stripped unless the owner is anchored in this save
        /// (<paramref name="ownerAnchored"/>) and this vessel's guid conclusively differs: then it
        /// is another launch of the same craft and is kept. An unknown guid on either side, or
        /// an unanchored owner, falls back to the name (the pre-identity behavior).</item>
        /// <item>A vessel under another name that carries the owner's launch guid is still the
        /// owner's vessel (a recording renamed after the flight): stripped by guid.</item>
        /// </list>
        /// </summary>
        internal static RewindOwnerStripDecision ClassifyRewindOwnerStripVessel(
            string vesselName, string vesselGuid, RewindOwnerStrip strip, bool ownerAnchored)
        {
            if (strip == null)
                return RewindOwnerStripDecision.NotMatched;

            string guid = VesselLaunchIdentity.NormalizeGuid(vesselGuid);
            if (vesselName != null && strip.NameOnly.Contains(vesselName))
                return RewindOwnerStripDecision.StripName;

            if (vesselName != null && !string.IsNullOrEmpty(strip.OwnerName)
                && string.Equals(vesselName, strip.OwnerName, StringComparison.Ordinal))
            {
                if (ownerAnchored && VesselLaunchIdentity.GuidsConclusivelyDiffer(strip.OwnerGuid, guid))
                    return RewindOwnerStripDecision.KeepOtherLaunch;
                return RewindOwnerStripDecision.StripName;
            }

            if (strip.OwnerGuid != null && guid != null
                && string.Equals(guid, strip.OwnerGuid, StringComparison.OrdinalIgnoreCase))
                return RewindOwnerStripDecision.StripOwnerGuid;

            return RewindOwnerStripDecision.NotMatched;
        }
    }
}
