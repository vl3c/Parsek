using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace Parsek.TestCommands
{
    /// <summary>
    /// Finds THIS process's game window by process id: <c>EnumWindows</c> over the top-level
    /// windows, keeping the ones whose <c>GetWindowThreadProcessId</c> is the current pid.
    ///
    /// <para>A title lookup (<c>FindWindow(null, "Kerbal Space Program")</c>) answers with
    /// whichever KSP window was created first, so with several KSP copies on one PC (parallel
    /// harness slots, the operator's own Steam game) it can name another process's window.
    /// <c>Process.MainWindowHandle</c> reads zero on a harness-launched instance (measured,
    /// LT-1 run 2026-09-08_1114), so it cannot stand in either.</para>
    ///
    /// <para>The choice among the process's windows is pure (<see cref="ChooseIndex"/>):
    /// owned windows are never chosen, a visible Unity window (<c>UnityWndClass</c>) wins,
    /// then a hidden Unity window (the boot window before it is shown), then any other
    /// visible unowned window.</para>
    ///
    /// <para>Windows only. The user32 imports bind lazily at the first call, and every
    /// caller checks <see cref="IsWindowsHost"/> first, so a mono host never binds them.</para>
    /// </summary>
    internal static class OwnProcessWindowFinder
    {
        internal const string UnityWindowClass = "UnityWndClass";

        /// <summary>One enumerated top-level window, as the pure choice reads it.</summary>
        internal struct Candidate
        {
            internal IntPtr Handle;
            internal uint ProcessId;
            internal bool Visible;
            internal bool Owned;
            internal string ClassName;
        }

        /// <summary>True when the host OS is Windows (no Unity call, safe in tests).</summary>
        internal static bool IsWindowsHost()
            => Environment.OSVersion.Platform == PlatformID.Win32NT;

        /// <summary>
        /// Pure: the index of the window to use among <paramref name="candidates"/>, or -1.
        /// Only windows of <paramref name="ownPid"/> that are not owned are eligible; rank 0
        /// is visible + Unity class, rank 1 hidden + Unity class, rank 2 visible + any class.
        /// Ties keep enumeration order (top of the z-order first).
        /// </summary>
        internal static int ChooseIndex(IList<Candidate> candidates, uint ownPid)
        {
            if (candidates == null) return -1;
            int best = -1;
            int bestRank = int.MaxValue;
            for (int i = 0; i < candidates.Count; i++)
            {
                Candidate c = candidates[i];
                if (c.Handle == IntPtr.Zero || c.ProcessId != ownPid || c.Owned) continue;
                int rank = Rank(c);
                if (rank < bestRank)
                {
                    bestRank = rank;
                    best = i;
                }
            }
            return best;
        }

        private static int Rank(Candidate c)
        {
            bool unity = string.Equals(c.ClassName, UnityWindowClass, StringComparison.Ordinal);
            if (unity && c.Visible) return 0;
            if (unity) return 1;
            if (c.Visible) return 2;
            return int.MaxValue;
        }

        /// <summary>
        /// This process's game window by pid, or zero. <paramref name="visible"/> reports
        /// whether the chosen window is visible. Never throws: a bind failure answers zero.
        /// </summary>
        internal static IntPtr FindOwnWindow(out bool visible)
        {
            visible = false;
            if (!IsWindowsHost()) return IntPtr.Zero;
            try
            {
                return FindOwnWindowCore(out visible);
            }
            catch (Exception)
            {
                visible = false;
                return IntPtr.Zero;
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static IntPtr FindOwnWindowCore(out bool visible)
        {
            visible = false;
            uint ownPid = (uint)Process.GetCurrentProcess().Id;
            var collected = new List<IntPtr>();
            GCHandle handle = GCHandle.Alloc(collected);
            try
            {
                EnumWindows(EnumCallback, GCHandle.ToIntPtr(handle));
            }
            finally
            {
                handle.Free();
            }

            var candidates = new List<Candidate>(collected.Count);
            var className = new StringBuilder(256);
            foreach (IntPtr hwnd in collected)
            {
                GetWindowThreadProcessId(hwnd, out uint pid);
                if (pid != ownPid) continue;
                className.Length = 0;
                GetClassName(hwnd, className, className.Capacity);
                candidates.Add(new Candidate
                {
                    Handle = hwnd,
                    ProcessId = pid,
                    Visible = IsWindowVisible(hwnd),
                    Owned = GetWindow(hwnd, GwOwner) != IntPtr.Zero,
                    ClassName = className.ToString(),
                });
            }

            int index = ChooseIndex(candidates, ownPid);
            if (index < 0) return IntPtr.Zero;
            visible = candidates[index].Visible;
            return candidates[index].Handle;
        }

        // A static callback (no closure): the collector rides in lParam as a GCHandle, so
        // the marshalled delegate holds no instance state and stays reachable through this
        // static field for the whole EnumWindows call.
        private static readonly EnumWindowsProc EnumCallback = CollectWindow;

        private static bool CollectWindow(IntPtr hwnd, IntPtr lParam)
        {
            var list = GCHandle.FromIntPtr(lParam).Target as List<IntPtr>;
            if (list != null) list.Add(hwnd);
            return true;
        }

        private const uint GwOwner = 4;

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern IntPtr GetWindow(IntPtr hWnd, uint cmd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder className, int maxCount);
    }
}
