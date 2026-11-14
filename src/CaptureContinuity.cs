// Copyright (c) 2026 InputLift contributors
// SPDX-License-Identifier: MIT
// Detect possible suspend gaps without input, hooks, Bluetooth, I/O or settings.
// Caller serializes Sample under the same gate as decoder/input state.
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;

internal sealed class CaptureContinuity
{
    // Interrupt times are in100ns units:250ms=2,500,000 ticks.
    private const ulong GapThreshold = 2500000UL;
    private bool initialized;
    private ulong previousActive, previousBiased;

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryUnbiasedInterruptTime(out ulong active);
    // Bind through the Windows API-set contract. Some Windows builds expose
    // this API through KernelBase but have no Kernel32 export of that name.
    [DllImport("api-ms-win-core-realtime-l1-1-0.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern void QueryInterruptTime(out ulong biased);
    [DllImport("kernel32.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern void SetLastError(uint error);

    internal bool Sample()
    {
        ulong active, biased;
        // Read active first, biased second. Clear only this thread's error slot
        // so a false return cannot be reported using an unrelated stale error.
        SetLastError(0);
        if (!QueryUnbiasedInterruptTime(out active))
            throw ClockFailure(Marshal.GetLastWin32Error());
        QueryInterruptTime(out biased);
        return Observe(active, biased);
    }

    private static Win32Exception ClockFailure(int code)
    {
        return new Win32Exception(code, "QueryUnbiasedInterruptTime returned false; Win32 " + code +
            (code == 0 ? " (Windows supplied no extended error)." : ": " + new Win32Exception(code).Message));
    }

    // Pure clock-delta policy; production memory is three scalar fields.
    private bool Observe(ulong active, ulong biased)
    {
        if (!initialized)
        {
            initialized = true;
            previousActive = active;
            previousBiased = biased;
            return false;
        }
        bool discontinuity = active < previousActive || biased < previousBiased;
        if (!discontinuity)
        {
            ulong activeDelta = active - previousActive;
            ulong biasedDelta = biased - previousBiased;
            // This is growth of (biased-active), without unsigned underflow
            // or signed conversion of a long-running system's raw counters.
            discontinuity = biasedDelta > activeDelta && biasedDelta - activeDelta > GapThreshold;
        }
        previousActive = active;
        previousBiased = biased;
        return discontinuity;
    }

    internal static string[] RunSelfTests(out int count)
    {
        int cases = 0; List<string> errors = new List<string>();
        Action<bool,string> check = delegate(bool okay,string name) { cases++; if (!okay) errors.Add(name); };
        CaptureContinuity clock = new CaptureContinuity();
        check(!clock.Observe(0,0), "Initial counters establish a baseline without a reset");
        check(!clock.Observe(10000000,10000000), "Equal awake clock advancement preserves continuity");
        check(clock.Observe(11000000,3011000000), "Five-minute sleep bias invalidates cached identity");
        check(!clock.Observe(12000000,3012000000), "The same completed gap is not reported twice");

        clock = new CaptureContinuity(); clock.Observe(0,0);
        check(!clock.Observe(100,GapThreshold+100), "Exactly250ms is below the strict greater-than threshold");
        clock = new CaptureContinuity(); clock.Observe(0,0);
        check(clock.Observe(100,GapThreshold+101), "A250ms-plus-one-tick bias increase triggers a reset");

        clock = new CaptureContinuity(); clock.Observe(1000,1000);
        check(clock.Observe(999,1001), "Active counter regression invalidates identity");
        check(!clock.Observe(1099,1101), "Active regression replaces the baseline once");
        check(clock.Observe(1199,1100), "Biased counter regression invalidates identity");
        check(!clock.Observe(1299,1200), "Biased regression replaces the baseline once");

        clock = new CaptureContinuity();
        check(!clock.Observe(1000,3000001000), "Preexisting sleep bias is accepted as the initial baseline");
        check(!clock.Observe(2000,3000002000), "Preexisting constant bias does not cause repeated resets");
        check(clock.Observe(2100,6000002100), "A later independent sleep gap triggers another reset");

        clock = new CaptureContinuity(); clock.Observe(ulong.MaxValue-100,ulong.MaxValue-100);
        check(!clock.Observe(ulong.MaxValue-10,ulong.MaxValue-10), "Large counters use safe unsigned deltas");
        check(clock.Observe(5,7), "Counter wrap is treated conservatively as regression");
        check(!clock.Observe(15,17), "Wrapped counters establish a usable new baseline");

        clock = new CaptureContinuity(); clock.Observe(100,200);
        check(!clock.Observe(1100,1000), "Negative bias growth cannot underflow into a false large sleep");
        check(!clock.Observe(2100,2000), "Equal subsequent advancement remains continuous");
        check(ClockFailure(87).NativeErrorCode==87 && ClockFailure(87).Message.Contains("Win32 87"),
            "A failed native query preserves the exact observed error code");
        check(ClockFailure(0).Message.Contains("no extended error"), "Missing extended error is explicit, not a success message");
        count = cases; return errors.ToArray();
    }
}
