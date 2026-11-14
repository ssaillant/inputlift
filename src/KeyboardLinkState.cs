// Copyright (c) 2026 GlobeRelay contributors
// SPDX-License-Identifier: MIT
// Read-only configured-keyboard connection preflight. No inquiry, connection/reset,
// ETW, registry, input, packet capture, network, elevation, or file output.
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

internal static class KeyboardConnectionState
{
    private static ulong TargetAddress
    {
        get { return ulong.Parse(DailyPaths.ReadConfiguredKeyboardAddress(),System.Globalization.NumberStyles.HexNumber,System.Globalization.CultureInfo.InvariantCulture); }
    }
    private const int PollMilliseconds = 200, WaitLimitMilliseconds = 120000, StableMilliseconds = 1000;
    private const int NoMoreItems = 259;

    [StructLayout(LayoutKind.Sequential)]
    private struct RadioParameters { internal uint Size; }
    [StructLayout(LayoutKind.Sequential)]
    private struct SystemTime { internal ushort Year, Month, DayOfWeek, Day, Hour, Minute, Second, Milliseconds; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DeviceInfo
    {
        internal uint Size;
        internal ulong Address;
        internal uint ClassOfDevice;
        internal int Connected, Remembered, Authenticated;
        internal SystemTime LastSeen, LastUsed;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 248)] internal string Name;
    }
    [DllImport("bthprops.cpl", ExactSpelling = true, SetLastError = true)]
    private static extern IntPtr BluetoothFindFirstRadio(ref RadioParameters parameters, out IntPtr radio);
    [DllImport("bthprops.cpl", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BluetoothFindNextRadio(IntPtr search, out IntPtr radio);
    [DllImport("bthprops.cpl", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BluetoothFindRadioClose(IntPtr search);
    [DllImport("bthprops.cpl", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint BluetoothGetDeviceInfo(IntPtr radio, ref DeviceInfo info);
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    private static string NativeError(string operation, int error)
    { return operation + " returned Win32 " + error + ": " + new Win32Exception(error).Message; }
    private static string Describe(Exception error)
    {
        Win32Exception native = error as Win32Exception;
        return native == null ? error.GetType().Name + ": " + error.Message :
            "Win32 " + native.NativeErrorCode + ": " + error.Message;
    }
    private static bool CorrectAbi()
    {
        return IntPtr.Size == 8 && Marshal.SizeOf(typeof(RadioParameters)) == 4 &&
            Marshal.SizeOf(typeof(SystemTime)) == 16 && Marshal.SizeOf(typeof(DeviceInfo)) == 560 &&
            Marshal.OffsetOf(typeof(DeviceInfo), "Size").ToInt32() == 0 &&
            Marshal.OffsetOf(typeof(DeviceInfo), "Address").ToInt32() == 8 &&
            Marshal.OffsetOf(typeof(DeviceInfo), "ClassOfDevice").ToInt32() == 16 &&
            Marshal.OffsetOf(typeof(DeviceInfo), "Connected").ToInt32() == 20 &&
            Marshal.OffsetOf(typeof(DeviceInfo), "Remembered").ToInt32() == 24 &&
            Marshal.OffsetOf(typeof(DeviceInfo), "Authenticated").ToInt32() == 28 &&
            Marshal.OffsetOf(typeof(DeviceInfo), "LastSeen").ToInt32() == 32 &&
            Marshal.OffsetOf(typeof(DeviceInfo), "LastUsed").ToInt32() == 48 &&
            Marshal.OffsetOf(typeof(DeviceInfo), "Name").ToInt32() == 64;
    }
    private sealed class RadioScope
    {
        internal IntPtr Radio, Search, ExtraRadio;
        internal List<string> Close()
        {
            List<string> errors = new List<string>();
            IntPtr extra = ExtraRadio; ExtraRadio = IntPtr.Zero;
            if (extra != IntPtr.Zero && !CloseHandle(extra))
                errors.Add(NativeError("CloseHandle(second radio)", Marshal.GetLastWin32Error()));
            IntPtr radio = Radio; Radio = IntPtr.Zero;
            if (radio != IntPtr.Zero && !CloseHandle(radio))
                errors.Add(NativeError("CloseHandle(radio)", Marshal.GetLastWin32Error()));
            IntPtr search = Search; Search = IntPtr.Zero;
            if (search != IntPtr.Zero && !BluetoothFindRadioClose(search))
                errors.Add(NativeError("BluetoothFindRadioClose", Marshal.GetLastWin32Error()));
            return errors;
        }
        internal static RadioScope OpenSingleton()
        {
            if (!CorrectAbi()) throw new InvalidOperationException("Reviewed x64 BLUETOOTH_DEVICE_INFO560 ABI is required. No native query performed.");
            RadioScope scope = new RadioScope();
            try
            {
                RadioParameters parameters = new RadioParameters { Size = 4 };
                IntPtr firstRadio;
                scope.Search = BluetoothFindFirstRadio(ref parameters, out firstRadio);
                int firstError = Marshal.GetLastWin32Error();
                if (scope.Search == IntPtr.Zero)
                    throw new Win32Exception(firstError, NativeError("BluetoothFindFirstRadio", firstError));
                scope.Radio = firstRadio;
                if (scope.Radio == IntPtr.Zero) throw new InvalidOperationException("BluetoothFindFirstRadio returned a null radio handle.");
                bool another = BluetoothFindNextRadio(scope.Search, out scope.ExtraRadio);
                int nextError = Marshal.GetLastWin32Error();
                if (another) throw new InvalidOperationException("Expected exactly one Bluetooth radio; at least two were found. No device query performed.");
                scope.ExtraRadio = IntPtr.Zero; // A failed enumeration owns no returned radio.
                if (nextError != NoMoreItems)
                    throw new Win32Exception(nextError, NativeError("BluetoothFindNextRadio", nextError));
                IntPtr search = scope.Search; scope.Search = IntPtr.Zero;
                if (!BluetoothFindRadioClose(search))
                {
                    int closeError = Marshal.GetLastWin32Error();
                    throw new Win32Exception(closeError, NativeError("BluetoothFindRadioClose", closeError));
                }
                return scope;
            }
            catch (Exception error)
            {
                List<string> cleanup = scope.Close();
                if (cleanup.Count == 0) throw;
                throw new InvalidOperationException(Describe(error) + "; cleanup: " + string.Join("; ", cleanup.ToArray()), error);
            }
        }
        internal bool ReadConnected()
        {
            DeviceInfo info = new DeviceInfo { Size = 560, Address = TargetAddress };
            uint status = BluetoothGetDeviceInfo(Radio, ref info);
            if (status != 0)
                throw new Win32Exception(unchecked((int)status), NativeError("BluetoothGetDeviceInfo", unchecked((int)status)));
            return info.Connected != 0; // Never emit or retain name, timestamps, class or address.
        }
    }

    private static void WithRadio(Action<RadioScope> operation)
    {
        RadioScope scope = RadioScope.OpenSingleton();
        Exception failure = null;
        List<string> cleanup = null;
        try { operation(scope); }
        catch (Exception error) { failure = error; }
        finally { cleanup = scope.Close(); }
        if (failure != null)
            throw new InvalidOperationException(Describe(failure) + (cleanup.Count == 0 ? "" : "; cleanup: " + string.Join("; ", cleanup.ToArray())), failure);
        if (cleanup.Count != 0) throw new InvalidOperationException("Connection-query handle cleanup: " + string.Join("; ", cleanup.ToArray()));
    }
    private static void WaitCore(Func<bool> readConnected, Func<long> elapsed, Action<int> wait, Action<string> announce)
    {
        long falseSince = -1;
        bool first = true;
        while (true)
        {
            if (elapsed() >= WaitLimitMilliseconds)
                throw new TimeoutException("Keyboard disconnect preflight timed out after 120 seconds; Windows did not report it disconnected for a full second. Keep it OFF; do not enable the relay.");
            bool connected = readConnected(); // An API failure aborts; never treat it as disconnected.
            long now = elapsed();
            if (now >= WaitLimitMilliseconds)
                throw new TimeoutException("Keyboard disconnect preflight exceeded 120 seconds while querying Windows; no disconnected-state confirmation was accepted.");
            if (first)
            {
                announce(connected ? "Windows still reports configured keyboard connected. Keep its switch OFF until READY." :
                    "Windows reports configured keyboard disconnected; checking that state for one second. Keep it OFF until READY.");
                first = false;
            }
            if (connected) falseSince = -1;
            else
            {
                if (falseSince < 0) falseSince = now;
                if (now - falseSince >= StableMilliseconds)
                {
                    announce("Keyboard disconnected confirmed by Windows. KEEP IT OFF until READY.");
                    return;
                }
            }
            long remaining = WaitLimitMilliseconds - elapsed();
            if (remaining > 0) wait((int)Math.Min(PollMilliseconds, remaining));
        }
    }
    internal static void WaitForDisconnected(Action<string> announce)
    {
        if (announce == null) throw new ArgumentNullException("announce");
        announce("Turn configured keyboard OFF and KEEP IT OFF until READY.");
        WithRadio(delegate(RadioScope scope) {
            Stopwatch clock = Stopwatch.StartNew();
            WaitCore(scope.ReadConnected, delegate { return clock.ElapsedMilliseconds; }, Thread.Sleep, announce);
        });
        // Polling requires consecutive false samples spanning >=1s. Native API
        // calls can block; this is not a hard whole-program duration guarantee.
    }
    private static void RequireCore(Func<bool> readConnected)
    {
        if (readConnected())
            throw new InvalidOperationException("configured keyboard is connected. Keep its physical switch OFF until READY; the disconnected-state precondition no longer holds.");
    }
    internal static void RequireDisconnected()
    { WithRadio(delegate(RadioScope scope) { RequireCore(scope.ReadConnected); }); }
    internal static bool ReadConnected()
    {
        bool connected=false;
        WithRadio(delegate(RadioScope scope) { connected=scope.ReadConnected(); });
        return connected;
    }

    internal static string[] RunSelfTests(out int count)
    {
        int tests = 0; List<string> errors = new List<string>();
        Action<bool, string> check = delegate(bool ok, string name) { tests++; if (!ok) errors.Add(name); };
        check(CorrectAbi(), "Reviewed x64 DeviceInfo560/RadioParams4/SystemTime16 ABI and all documented field offsets");
        check(PollMilliseconds == 200 && WaitLimitMilliseconds == 120000 && StableMilliseconds == 1000,
            "Fixed polling, timeout and stable-disconnect interval without reading installed identity");
        long now = 0; int reads = 0; List<string> notices = new List<string>();
        WaitCore(delegate { reads++; return false; }, delegate { return now; }, delegate(int ms) { now += ms; }, notices.Add);
        check(reads == 6 && now == 1000 && notices.Count == 2, "Requires six false samples spanning a full second before success");
        check(notices[1].Contains("KEEP IT OFF until READY"), "Success keeps keyboard OFF until caller's capture READY");
        now = 0; reads = 0; notices.Clear();
        WaitCore(delegate { reads++; return now < 2000; }, delegate { return now; }, delegate(int ms) { now += ms; }, notices.Add);
        check(now == 3000 && reads == 16, "Waits for actual Windows disconnection then the full stable interval");
        now = 0; reads = 0;
        WaitCore(delegate { reads++; return now == 600; }, delegate { return now; }, delegate(int ms) { now += ms; }, delegate(string s) { });
        check(now == 1800 && reads == 10, "A connected sample resets a partially observed false interval");
        now = 0; reads = 0; bool timedOut = false;
        try { WaitCore(delegate { reads++; return true; }, delegate { return now; }, delegate(int ms) { now += ms; }, delegate(string s) { }); }
        catch (TimeoutException) { timedOut = true; }
        check(timedOut && now == 120000 && reads == 600, "Connected state times out without another query at deadline");
        now = 0; reads = 0; timedOut = false;
        try { WaitCore(delegate { reads++; now = 120001; return false; }, delegate { return now; }, delegate(int ms) { now += ms; }, delegate(string s) { }); }
        catch (TimeoutException) { timedOut = true; }
        check(timedOut && reads == 1, "A blocking query that returns past deadline cannot pass preflight");
        reads = 0; bool nativeFailed = false;
        try { WaitCore(delegate { reads++; throw new Win32Exception(87, "Fixed test error"); }, delegate { return 0; },
            delegate(int ms) { throw new Exception("No wait allowed after error"); }, delegate(string s) { }); }
        catch (Win32Exception error) { nativeFailed = error.NativeErrorCode == 87; }
        check(nativeFailed && reads == 1, "Exact native query error stops immediately, without retry or false disconnect");
        reads = 0; bool refused = false;
        try { RequireCore(delegate { reads++; return true; }); } catch (InvalidOperationException) { refused = true; }
        check(refused && reads == 1, "One-shot recheck rejects connected keyboard without retries");
        reads = 0; RequireCore(delegate { reads++; return false; });
        check(reads == 1, "One-shot recheck accepts exactly one disconnected-state query");
        reads = 0; nativeFailed = false;
        try { RequireCore(delegate { reads++; throw new Win32Exception(1168, "Fixed missing device"); }); }
        catch (Win32Exception error) { nativeFailed = error.NativeErrorCode == 1168; }
        check(nativeFailed && reads == 1, "One-shot error is not accepted as disconnected");
        count = tests; return errors.ToArray();
    }
}
