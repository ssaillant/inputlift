// Copyright (c) 2026 InputLift contributors
// SPDX-License-Identifier: MIT
// Fixed Deskflow integration only. This checks process/window metadata without
// reading arbitrary window text or input. It is a conservative compatibility
// gate, not an authenticated indication of the active remote client.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

internal static class DeskflowRouteGate
{
    private const uint QueryLimitedInformation = 0x1000;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern IntPtr FindWindowExW(IntPtr parent, IntPtr after, string className, string title);
    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageNameW(IntPtr process, uint flags, StringBuilder path, ref uint size);
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(IntPtr handle);

    private static bool VisibleSinkMatches(bool visible,uint windowPid,uint expectedPid,bool imageMatches,string className,string title)
    {
        // KeepForeground=true can leave the sink visible without making it the
        // foreground window. Visibility and verified ownership are the gate.
        return visible && windowPid!=0 && windowPid==expectedPid && imageMatches &&
            className=="DeskflowDesk" && title=="DeskflowDesk";
    }

    private static uint SingleCorePid()
    {
        Process[] cores=Process.GetProcessesByName("deskflow-core");
        try { return cores.Length==1 ? unchecked((uint)cores[0].Id) : 0; }
        finally { foreach(Process core in cores) core.Dispose(); }
    }

    private static bool ExactCoreImage(uint pid)
    {
        IntPtr process=OpenProcess(QueryLimitedInformation,false,pid);
        if(process==IntPtr.Zero) return false;
        bool matched=false,closed;
        try
        {
            StringBuilder image=new StringBuilder(1024); uint size=1024;
            if(QueryFullProcessImageNameW(process,0,image,ref size))
            {
                string expected=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),"Deskflow","deskflow-core.exe");
                matched=string.Equals(image.ToString(),expected,StringComparison.OrdinalIgnoreCase);
            }
        }
        finally { closed=CloseHandle(process); }
        return matched && closed;
    }

    internal static bool CanStartPress
    {
        get
        {
            try
            {
                uint expected=SingleCorePid();
                if(expected==0 || !ExactCoreImage(expected)) return false;
                IntPtr window=IntPtr.Zero;
                for(int i=0;i<64;i++)
                {
                    // Match a fixed class/title; do not retrieve foreign text.
                    window=FindWindowExW(IntPtr.Zero,window,"DeskflowDesk","DeskflowDesk");
                    if(window==IntPtr.Zero) return false;
                    uint pid; GetWindowThreadProcessId(window,out pid);
                    if(VisibleSinkMatches(IsWindowVisible(window),pid,expected,true,"DeskflowDesk","DeskflowDesk")) return true;
                }
            }
            catch { return false; }
            return false;
        }
    }

    // Pure predicate tests only: no process enumeration, window inspection,
    // native input, hooks or hardware activity.
    internal static string[] RunSelfTests(out int count)
    {
        int cases=0; List<string> errors=new List<string>();
        Action<bool,string> check=delegate(bool okay,string name){cases++;if(!okay) errors.Add(name);};
        check(VisibleSinkMatches(true,55,55,true,"DeskflowDesk","DeskflowDesk"),
            "Exact visible trusted core sink permits a press without requiring foreground");
        check(!VisibleSinkMatches(false,55,55,true,"DeskflowDesk","DeskflowDesk") &&
            !VisibleSinkMatches(true,56,55,true,"DeskflowDesk","DeskflowDesk") &&
            !VisibleSinkMatches(true,55,55,false,"DeskflowDesk","DeskflowDesk") &&
            !VisibleSinkMatches(true,55,55,true,"Other","DeskflowDesk") &&
            !VisibleSinkMatches(true,55,55,true,"DeskflowDesk","Other"),
            "Hidden, foreign PID/image/class/title gates fail closed");
        count=cases; return errors.ToArray();
    }
}
