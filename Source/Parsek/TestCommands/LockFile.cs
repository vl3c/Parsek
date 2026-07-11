using System;
using System.Globalization;

namespace Parsek.TestCommands
{
    /// <summary>Tri-state liveness of a probed process id.</summary>
    internal enum PidLiveness
    {
        /// <summary>The pid is a live process (and, where cheaply checkable, a KSP instance).</summary>
        Alive,

        /// <summary>The pid is not a live process (the previous owner crashed / exited).</summary>
        Dead,

        /// <summary>The pid could not be probed; fall back to the <c>t</c> tie-break.</summary>
        Unknown,
    }

    /// <summary>
    /// Seam for probing whether a lock's recorded pid is still alive. The real addon
    /// implements this over <c>System.Diagnostics.Process</c>; tests inject a fake so
    /// <see cref="LockFile.DecideLockOwnership"/> stays pure and deterministic.
    /// </summary>
    internal interface IPidLivenessProbe
    {
        PidLiveness Probe(int pid);
    }

    /// <summary>The ownership decision for a KSP-root automation lock.</summary>
    internal enum LockOwnership
    {
        /// <summary>The lock is ours (session matches): reclaim it silently.</summary>
        Reclaim,

        /// <summary>A foreign lock with a LIVE pid: another instance owns the channel;
        /// stand down (do not consume commands).</summary>
        StandDown,

        /// <summary>A foreign lock with a DEAD pid (previous owner crashed): reclaim,
        /// logging a Warn.</summary>
        ReclaimWithWarn,

        /// <summary>No usable existing lock: acquire it.</summary>
        Acquire,
    }

    /// <summary>Pure result of parsing a lock line.</summary>
    internal struct LockInfo
    {
        public bool Ok;
        public string Session;
        public int Pid;
        public double T;
        public string Root;
    }

    /// <summary>
    /// Pure lock-file grammar and ownership decision for the ParsekTestCommands seam
    /// (M-A2). The lock detects two automation instances pointed at the same KSP root.
    /// Ownership is decided by PID LIVENESS, not by <c>t</c>: <c>t</c> is written once
    /// at startup and never refreshed, so a long-lived healthy instance would look
    /// "stale" by <c>t</c> while a just-crashed one would look "fresh". <c>t</c> is a
    /// last-resort tie-break only, used when a pid cannot be probed.
    /// </summary>
    internal static class LockFile
    {
        private const string RootKey = "root=";

        /// <summary>
        /// Formats the single lock line. <paramref name="root"/> is written last and
        /// raw (it may contain spaces, e.g. "Kerbal Space Program"); the parser reads
        /// it as the trailing remainder after <c>root=</c>.
        /// </summary>
        internal static string FormatLockLine(string session, int pid, double t, string root)
        {
            return "session=" + session
                + " pid=" + pid.ToString(CultureInfo.InvariantCulture)
                + " t=" + t.ToString("R", CultureInfo.InvariantCulture)
                + " " + RootKey + (root ?? string.Empty);
        }

        /// <summary>
        /// Parses a lock line. Requires <c>session</c> and a parseable <c>pid</c>;
        /// <c>t</c> defaults to 0 when absent, and <c>root</c> is the raw trailing
        /// remainder after <c>root=</c> (may contain spaces). Returns false (Ok=false)
        /// when session or pid is missing.
        /// </summary>
        internal static bool TryParseLockLine(string line, out LockInfo info)
        {
            info = new LockInfo();
            if (string.IsNullOrEmpty(line)) return false;

            // Split off the root remainder first so a spaced path does not corrupt the
            // whitespace tokenization of the leading fields.
            string prefix = line;
            int rootIdx = line.IndexOf(RootKey, StringComparison.Ordinal);
            if (rootIdx >= 0)
            {
                info.Root = line.Substring(rootIdx + RootKey.Length);
                prefix = line.Substring(0, rootIdx);
            }

            bool sawSession = false;
            bool sawPid = false;
            string[] tokens = prefix.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (string token in tokens)
            {
                if (!TestCommandProtocol.TrySplitToken(token, out string key, out string value))
                    continue;
                switch (key)
                {
                    case "session":
                        info.Session = value;
                        sawSession = true;
                        break;
                    case "pid":
                        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int pid))
                        {
                            info.Pid = pid;
                            sawPid = true;
                        }
                        break;
                    case "t":
                        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double t))
                            info.T = t;
                        break;
                }
            }

            if (!sawSession || !sawPid) return false;
            info.Ok = true;
            return true;
        }

        /// <summary>
        /// Decides ownership of an existing lock. Order: no usable lock -> Acquire;
        /// our own session -> Reclaim; otherwise probe the foreign pid -> Alive =
        /// StandDown, Dead = ReclaimWithWarn, Unknown = the <c>t</c> tie-break (reclaim
        /// with warn when the existing lock's t is not newer than ours, else stand
        /// down). PID liveness is the primary signal; <c>t</c> is only the last resort.
        /// </summary>
        internal static LockOwnership DecideLockOwnership(
            LockInfo existing,
            bool existingPresent,
            string ownSession,
            double ownT,
            IPidLivenessProbe probe)
        {
            if (!existingPresent || !existing.Ok)
                return LockOwnership.Acquire;

            if (existing.Session == ownSession)
                return LockOwnership.Reclaim;

            PidLiveness liveness = probe != null ? probe.Probe(existing.Pid) : PidLiveness.Unknown;
            switch (liveness)
            {
                case PidLiveness.Alive:
                    return LockOwnership.StandDown;
                case PidLiveness.Dead:
                    return LockOwnership.ReclaimWithWarn;
                default:
                    // Cannot probe the pid: last-resort t tie-break. A stale lock (t not
                    // newer than ours) is reclaimed so a crashed owner does not wedge a
                    // fresh run; a lock that looks newer than ours we defer to.
                    return existing.T <= ownT ? LockOwnership.ReclaimWithWarn : LockOwnership.StandDown;
            }
        }
    }
}
