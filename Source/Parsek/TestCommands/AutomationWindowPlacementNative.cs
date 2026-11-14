using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Parsek.TestCommands
{
    /// <summary>
    /// The live <see cref="IWindowPlacementPlatform"/>: user32 and the documented
    /// <c>IVirtualDesktopManager</c> (shobjidl_core.h; CLSID
    /// aa509086-5ca9-4c25-8f95-589d3c07b48a, IID a5cd92ff-29be-454c-8d04-d82879fb3f1b).
    ///
    /// <para>The COM object is called through its vtable (<c>CoCreateInstance</c> plus
    /// <c>Marshal.GetDelegateForFunctionPointer</c>), not through a <c>[ComImport]</c>
    /// interface: Unity's mono has partial COM interop, and a raw vtable call needs none of
    /// it. IVirtualDesktopManager's slots after IUnknown's three are
    /// IsWindowOnCurrentVirtualDesktop (3), GetWindowDesktopId (4) and
    /// MoveWindowToDesktop (5).</para>
    ///
    /// <para>Every member answers without throwing; each OS call sits in a NoInlining core so
    /// a bind or interop failure surfaces as a caught exception at the call, never at the
    /// JIT of a caller.</para>
    /// </summary>
    internal sealed class AutomationWindowPlacementNative : IWindowPlacementPlatform
    {
        private static readonly Guid VirtualDesktopManagerClsid = new Guid("aa509086-5ca9-4c25-8f95-589d3c07b48a");
        private static readonly Guid VirtualDesktopManagerIid = new Guid("a5cd92ff-29be-454c-8d04-d82879fb3f1b");

        private const uint ClsctxAll = 0x17;
        private const uint CoinitApartmentThreaded = 0x2;
        private const int CoENotInitialized = unchecked((int)0x800401F0);
        private const int EFail = unchecked((int)0x80004005);
        private const int VtblSlotRelease = 2;
        private const int VtblSlotMoveWindowToDesktop = 5;

        public bool IsWindows => OwnProcessWindowFinder.IsWindowsHost();

        public IntPtr FindOwnWindow(out bool visible) => OwnProcessWindowFinder.FindOwnWindow(out visible);

        public int MoveWindowToDesktop(IntPtr hwnd, Guid desktopId, out string error)
        {
            error = null;
            try
            {
                return MoveWindowToDesktopCore(hwnd, desktopId);
            }
            catch (Exception ex)
            {
                error = ex.GetType().Name;
                int hr = 0;
                try { hr = Marshal.GetHRForException(ex); } catch (Exception) { hr = 0; }
                return hr != 0 ? hr : EFail;
            }
        }

        public IntPtr GetForegroundWindow()
        {
            try { return GetForegroundWindowCore(); }
            catch (Exception) { return IntPtr.Zero; }
        }

        public bool IsWindow(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return false;
            try { return IsWindowCore(hwnd); }
            catch (Exception) { return false; }
        }

        public bool SetForegroundWindow(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return false;
            try { return SetForegroundWindowCore(hwnd); }
            catch (Exception) { return false; }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static int MoveWindowToDesktopCore(IntPtr hwnd, Guid desktopId)
        {
            Guid clsid = VirtualDesktopManagerClsid;
            Guid iid = VirtualDesktopManagerIid;
            IntPtr manager;
            bool uninitialize = false;
            int hr = CoCreateInstance(ref clsid, IntPtr.Zero, ClsctxAll, ref iid, out manager);
            if (hr == CoENotInitialized)
            {
                int init = CoInitializeEx(IntPtr.Zero, CoinitApartmentThreaded);
                uninitialize = init == 0 || init == 1;
                hr = CoCreateInstance(ref clsid, IntPtr.Zero, ClsctxAll, ref iid, out manager);
            }
            try
            {
                if (hr != 0) return hr;
                if (manager == IntPtr.Zero) return EFail;
                try
                {
                    IntPtr vtable = Marshal.ReadIntPtr(manager);
                    IntPtr moveSlot = Marshal.ReadIntPtr(vtable, VtblSlotMoveWindowToDesktop * IntPtr.Size);
                    var move = (MoveWindowToDesktopFn)Marshal.GetDelegateForFunctionPointer(
                        moveSlot, typeof(MoveWindowToDesktopFn));
                    return move(manager, hwnd, ref desktopId);
                }
                finally
                {
                    IntPtr vtable = Marshal.ReadIntPtr(manager);
                    IntPtr releaseSlot = Marshal.ReadIntPtr(vtable, VtblSlotRelease * IntPtr.Size);
                    var release = (ReleaseFn)Marshal.GetDelegateForFunctionPointer(
                        releaseSlot, typeof(ReleaseFn));
                    release(manager);
                }
            }
            finally
            {
                if (uninitialize) CoUninitialize();
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static IntPtr GetForegroundWindowCore() => NativeGetForegroundWindow();

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool IsWindowCore(IntPtr hwnd) => NativeIsWindow(hwnd);

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool SetForegroundWindowCore(IntPtr hwnd) => NativeSetForegroundWindow(hwnd);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int MoveWindowToDesktopFn(IntPtr self, IntPtr topLevelWindow, ref Guid desktopId);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate uint ReleaseFn(IntPtr self);

        [DllImport("ole32.dll")]
        private static extern int CoCreateInstance(ref Guid clsid, IntPtr outer, uint context,
            ref Guid iid, out IntPtr instance);

        [DllImport("ole32.dll")]
        private static extern int CoInitializeEx(IntPtr reserved, uint coInit);

        [DllImport("ole32.dll")]
        private static extern void CoUninitialize();

        [DllImport("user32.dll", EntryPoint = "GetForegroundWindow")]
        private static extern IntPtr NativeGetForegroundWindow();

        [DllImport("user32.dll", EntryPoint = "IsWindow")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool NativeIsWindow(IntPtr hWnd);

        [DllImport("user32.dll", EntryPoint = "SetForegroundWindow")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool NativeSetForegroundWindow(IntPtr hWnd);
    }
}
