using System.Reflection;

namespace Parsek
{
    /// <summary>
    /// Reads whether stock is inside <c>Vessel.Unload()</c> for a vessel. Stock snapshots the
    /// protoVessel, destroys and CLEARS the part list, and only then calls <c>GoOnRails()</c>
    /// (for a vessel it had not packed yet) and <c>SetLoaded(false)</c>. So inside that
    /// <c>onVesselGoOnRails</c> the vessel still reads loaded, with no parts: the same shape as
    /// a vessel whose last part just died, except that it is alive and its protoVessel is
    /// current. Every vessel unloaded before it was packed passes through it: a flying one
    /// (stock unload 22.5 km, pack 25 km) and one that just touched down past 2.5 km.
    /// <para>
    /// Inside the window nothing may call <c>Vessel.BackupVessel()</c>: the vessel reads
    /// loaded, so stock would build a fresh ProtoVessel from the empty part list and install it
    /// over the real one, and the craft would lose every part.
    /// </para>
    /// </summary>
    internal static class VesselUnloadState
    {
        // Internal in stock; set at the top of Unload() and cleared after SetLoaded(false).
        private static readonly FieldInfo IsUnloadingField = typeof(Vessel).GetField(
            "isUnloading", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

        /// <summary>
        /// Pure: the mid-unload shape. <paramref name="isUnloading"/> is stock's own flag; the
        /// rest keeps the answer false once the part list is gone for another reason.
        /// </summary>
        internal static bool IsMidUnloadCore(
            bool isUnloading, bool stateDead, bool loaded, int loadedPartCount, int protoPartCount)
        {
            return isUnloading
                && !stateDead
                && loaded
                && loadedPartCount == 0
                && protoPartCount > 0;
        }

        /// <summary>Live: true while stock is unloading <paramref name="vessel"/> and its parts
        /// are already gone; its protoVessel is then the only, and current, part list.</summary>
        internal static bool IsMidUnload(Vessel vessel)
        {
            if (ReferenceEquals(vessel, null))
                return false;
            if (IsUnloadingField == null)
            {
                ParsekLog.WarnRateLimited("Recording", "vessel-unload-flag-missing",
                    "Stock Vessel.isUnloading not found: a vessel mid-unload reads as destroyed",
                    minIntervalSeconds: 600.0);
                return false;
            }
            return IsMidUnloadCore(
                (bool)IsUnloadingField.GetValue(vessel),
                vessel.state == Vessel.State.DEAD,
                vessel.loaded,
                vessel.parts != null ? vessel.parts.Count : 0,
                vessel.protoVessel != null && vessel.protoVessel.protoPartSnapshots != null
                    ? vessel.protoVessel.protoPartSnapshots.Count
                    : 0);
        }
    }
}
