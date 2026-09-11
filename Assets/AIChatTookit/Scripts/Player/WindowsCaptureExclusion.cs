using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace NeEEvA.Player
{
    /// <summary>
    /// A shared lease excluding this process's visible top-level Windows windows from
    /// supported desktop capture APIs. Call on the Unity main thread. This changes
    /// capture visibility only: windows still receive input and obscure the real desktop.
    /// </summary>
    public sealed class WindowsCaptureExclusion : IDisposable
    {
        private static readonly object gate = new object();
        private static int leases;
        private static long generation;
        private static string status = "Unity window exclusion is off.";
        private static bool hasError;
        private static int excludedWindowCount;
        private readonly long leaseGeneration;
        private bool disposed;

        public string Status { get { lock (gate) return status; } }
        public bool HasError { get { lock (gate) return hasError; } }
        public int ExcludedWindowCount { get { lock (gate) return excludedWindowCount; } }
        /// <summary>RestoreAll invalidates previous leases; create a new lease to enable again.</summary>
        public bool IsDisposed { get { lock (gate) return disposed || leaseGeneration != generation; } }

        public WindowsCaptureExclusion()
        {
            lock (gate)
            {
                leaseGeneration = generation;
                leases++;
                RefreshShared();
            }
        }

        /// <summary>Periodically discover new windows and discard closed windows; 0.5 seconds is sufficient.</summary>
        public void Refresh()
        {
            lock (gate)
            {
                if (!disposed && leaseGeneration == generation) RefreshShared();
            }
        }

        public void Dispose()
        {
            lock (gate)
            {
                if (disposed) return;
                disposed = true;
                if (leaseGeneration != generation) return;
                if (--leases == 0) RestoreShared();
            }
        }

        /// <summary>
        /// Call before assembly/domain reload and on application quit, on the main thread.
        /// Invalidates all leases and restores original affinities. Returns null on success,
        /// otherwise an error for the lifecycle owner to log. Failed records remain retryable.
        /// </summary>
        public static string RestoreAll()
        {
            lock (gate)
            {
                generation++;
                leases = 0;
                RestoreShared();
                return hasError ? status : null;
            }
        }

        private static void RefreshShared()
        {
#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
            var errors = new List<string>();
            try
            {
                string supportError = CheckSupport();
                if (supportError != null)
                {
                    errors.Add(supportError);
                    RestoreTracked(errors);
                }
                else
                {
                    RemoveClosedRecords();
                    var windows = new List<IntPtr>();
                    GCHandle context = GCHandle.Alloc(windows);
                    bool enumerated;
                    int enumerationError;
                    try
                    {
                        enumerated = EnumWindows(enumCallback, GCHandle.ToIntPtr(context));
                        enumerationError = Marshal.GetLastWin32Error();
                    }
                    finally { context.Free(); }
                    if (!enumerated)
                        errors.Add("EnumWindows failed (Windows error " + enumerationError + ").");
                    else
                        foreach (IntPtr window in windows) ExcludeWindow(window, errors);
                }
            }
            catch (Exception error) { errors.Add("Unity window exclusion failed: " + error.Message); }
            UpdateStatus(errors, false);
#else
            hasError = true;
            excludedWindowCount = 0;
            status = "Excluding Unity windows from desktop capture requires Windows 10 version 2004 or newer.";
#endif
        }

        private static void RestoreShared()
        {
#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
            var errors = new List<string>();
            RestoreTracked(errors);
            UpdateStatus(errors, true);
#else
            hasError = false;
            excludedWindowCount = 0;
            status = "Unity window exclusion is off.";
#endif
        }

#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
        private const uint ExcludeFromCapture = 0x11;
        private static readonly uint processId = GetCurrentProcessId();
        // A property belongs to the native window instance, unlike the numeric HWND.
        // A destroyed/reused HWND cannot pass this marker check, even in the same process.
        // Values are identifiers, not allocated native handles or managed pointers.
        private static readonly string identityProperty = "NeEEvA.CaptureExclusion." + Guid.NewGuid().ToString("N");
        private static long nextIdentity;
        private static readonly Dictionary<IntPtr, WindowRecord> records = new Dictionary<IntPtr, WindowRecord>();
        private static readonly EnumWindowsProc enumCallback = CollectWindow;

        private sealed class WindowRecord
        {
            public IntPtr Window, Identity;
            public uint OriginalAffinity;
            public bool Excluded;
        }

        private static string CheckSupport()
        {
            var version = new OsVersionInfo { Size = (uint)Marshal.SizeOf(typeof(OsVersionInfo)), ServicePack = string.Empty };
            int result = RtlGetVersion(ref version);
            if (result != 0) return "Cannot verify Windows version (NTSTATUS 0x" + result.ToString("X8") + ").";
            if (version.Major < 10 || (version.Major == 10 && version.Build < 19041))
                return "Window exclusion requires Windows 10 version 2004 (build 19041) or newer; no black-frame fallback was applied.";
            bool enabled;
            result = DwmIsCompositionEnabled(out enabled);
            if (result != 0) return "Cannot query desktop composition (HRESULT 0x" + result.ToString("X8") + ").";
            return enabled ? null : "Desktop composition is unavailable; Unity window exclusion was not applied.";
        }

        private static bool OwnedWindow(IntPtr window)
        {
            uint owner;
            return IsWindow(window) && GetWindowThreadProcessId(window, out owner) != 0 && owner == processId;
        }

        private static bool SameWindow(WindowRecord record)
        {
            return OwnedWindow(record.Window) && GetProp(record.Window, identityProperty) == record.Identity;
        }

        private static void RemoveClosedRecords()
        {
            var obsolete = new List<IntPtr>();
            foreach (var pair in records)
                if (!SameWindow(pair.Value)) obsolete.Add(pair.Key);
            foreach (IntPtr window in obsolete) records.Remove(window);
        }

        private static void ExcludeWindow(IntPtr window, List<string> errors)
        {
            if (!OwnedWindow(window) || !IsWindowVisible(window)) return;
            WindowRecord record;
            bool added = !records.TryGetValue(window, out record);
            if (added)
            {
                uint original;
                if (!GetWindowDisplayAffinity(window, out original))
                {
                    errors.Add(NativeFailure("Read original capture affinity", window));
                    return; // Never modify a window if its previous state cannot be restored.
                }
                long identity = ++nextIdentity;
                if (IntPtr.Size == 4) identity = (identity % int.MaxValue) + 1;
                record = new WindowRecord { Window = window, Identity = new IntPtr(identity), OriginalAffinity = original };
                if (!SetProp(window, identityProperty, record.Identity))
                {
                    errors.Add(NativeFailure("Mark Unity window", window));
                    return;
                }
                records.Add(window, record);
            }

            try
            {
                if (!SameWindow(record)) return;
                uint current;
                if (!GetWindowDisplayAffinity(window, out current))
                    throw new InvalidOperationException(NativeFailure("Read capture affinity", window));
                if (current != ExcludeFromCapture)
                {
                    if (!SetWindowDisplayAffinity(window, ExcludeFromCapture))
                        throw new InvalidOperationException(NativeFailure("Exclude Unity window", window));
                    if (!GetWindowDisplayAffinity(window, out current))
                        throw new InvalidOperationException(NativeFailure("Verify capture exclusion", window));
                }
                if (current != ExcludeFromCapture)
                    throw new InvalidOperationException("Windows did not retain capture exclusion for window " + window + ".");
                record.Excluded = true;
            }
            catch (Exception error)
            {
                record.Excluded = false;
                errors.Add(error.Message);
                // Roll back a failed application instead of leaving a partially changed window.
                if (RestoreRecord(record, errors)) records.Remove(window);
            }
        }

        private static bool RestoreRecord(WindowRecord record, List<string> errors)
        {
            try
            {
                if (!SameWindow(record)) return true; // Closed or reused: never touch the replacement.
                if (!SetWindowDisplayAffinity(record.Window, record.OriginalAffinity))
                {
                    errors.Add(NativeFailure("Restore original capture affinity", record.Window));
                    return false;
                }
                uint restored;
                if (!GetWindowDisplayAffinity(record.Window, out restored))
                {
                    errors.Add(NativeFailure("Verify restored capture affinity", record.Window));
                    return false;
                }
                record.Excluded = restored == ExcludeFromCapture;
                if (restored != record.OriginalAffinity)
                {
                    errors.Add("Windows did not restore the original capture affinity for window " + record.Window + ".");
                    return false;
                }
                if (RemoveProp(record.Window, identityProperty) != record.Identity)
                {
                    if (!OwnedWindow(record.Window)) return true;
                    errors.Add("The identity marker could not be removed from Unity window " + record.Window + ".");
                    return false;
                }
                return true;
            }
            catch (Exception error)
            {
                errors.Add("Restore Unity window " + record.Window + " failed: " + error.Message);
                return false;
            }
        }

        private static void RestoreTracked(List<string> errors)
        {
            var restored = new List<IntPtr>();
            foreach (var pair in records)
                if (RestoreRecord(pair.Value, errors)) restored.Add(pair.Key);
            foreach (IntPtr window in restored) records.Remove(window);
        }

        private static void UpdateStatus(List<string> errors, bool stopping)
        {
            excludedWindowCount = 0;
            foreach (WindowRecord record in records.Values)
                if (record.Excluded && SameWindow(record)) excludedWindowCount++;
            hasError = errors.Count > 0;
            if (hasError)
                status = "Unity window exclusion: " + string.Join(" ", errors.ToArray()) +
                    " Excluded windows: " + excludedWindowCount + ".";
            else if (stopping)
                status = "Unity window exclusion is off; original window capture settings restored.";
            else if (excludedWindowCount == 0)
                status = "Waiting for a visible Unity window to exclude from capture.";
            else
                status = "Excluded " + excludedWindowCount + " Unity window(s) from supported desktop capture APIs.";
        }

        private static string NativeFailure(string operation, IntPtr window)
        {
            int error = Marshal.GetLastWin32Error();
            return operation + " failed for window " + window + " (Windows error " + error + ").";
        }

#if ENABLE_IL2CPP
        [AOT.MonoPInvokeCallback(typeof(EnumWindowsProc))]
#endif
        private static bool CollectWindow(IntPtr window, IntPtr context)
        {
            if (OwnedWindow(window) && IsWindowVisible(window))
                ((List<IntPtr>)GCHandle.FromIntPtr(context).Target).Add(window);
            return true;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct OsVersionInfo
        {
            public uint Size, Major, Minor, Build, Platform;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string ServicePack;
        }
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private delegate bool EnumWindowsProc(IntPtr window, IntPtr context);
        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentProcessId();
        [DllImport("ntdll.dll", CharSet = CharSet.Unicode)]
        private static extern int RtlGetVersion(ref OsVersionInfo version);
        [DllImport("dwmapi.dll")]
        private static extern int DwmIsCompositionEnabled([MarshalAs(UnmanagedType.Bool)] out bool enabled);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr context);
        [DllImport("user32.dll")]
        private static extern bool IsWindow(IntPtr window);
        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr window);
        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool GetWindowDisplayAffinity(IntPtr window, out uint affinity);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowDisplayAffinity(IntPtr window, uint affinity);
        [DllImport("user32.dll", EntryPoint = "SetPropW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SetProp(IntPtr window, string name, IntPtr data);
        [DllImport("user32.dll", EntryPoint = "GetPropW", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetProp(IntPtr window, string name);
        [DllImport("user32.dll", EntryPoint = "RemovePropW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr RemoveProp(IntPtr window, string name);
#endif
    }
}
