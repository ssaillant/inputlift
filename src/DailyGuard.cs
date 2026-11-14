// Copyright (c) 2026 GlobeRelay contributors
// SPDX-License-Identifier: MIT
// Opt-in daily lifetime guard. No live mode is run during development/self-test.
// The only registry value this executable can change is EtwLogSensitiveData.
// Persistent Globe-to-Mouse5 worker supervision. No live action during
// development/self-test. Only after separately approved live mode can this
// executable release a validated possibly-owned Mouse5 button, and only
// after the exact child is confirmed quiescent. No automatic reboot occurs.
// Driver refresh may latch sensitive logging. Registry removal does NOT prove
// runtime cache reset. A manual Windows Restart is required after any attempt.
// The synchronous refresh may block this parent; the separate guardian still
// attempts registry cleanup. No automatic radio reset or Windows restart occurs.
using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using Microsoft.Win32;

internal static class DailyGuard
{
    private const string RegistryPath = @"SYSTEM\CurrentControlSet\Services\BTHPORT\Parameters";
    private const string ValueName = "EtwLogSensitiveData";
    private const string MutexName = @"Global\GlobeRelay.TemporaryFullHidProbe";
    private const string SessionPrefix = "InputLift.Daily.";
    private const int StartupGraceMilliseconds = 60000;
    private const int StaleProgressMilliseconds = 15000;
    private static readonly string DirectoryPath = DailyPaths.StateDirectory;
    private static JavaScriptSerializer NewSerializer() { return new JavaScriptSerializer { MaxJsonLength = 1048576 }; }

    [StructLayout(LayoutKind.Explicit, Size=120)]
    private struct TraceProperties
    {
        [FieldOffset(0)] public uint BufferSize;
        [FieldOffset(24)] public Guid SessionGuid;
        [FieldOffset(44)] public uint WnodeFlags;
        [FieldOffset(112)] public uint LogFileNameOffset;
        [FieldOffset(116)] public uint LoggerNameOffset;
    }
    [DllImport("advapi32.dll", CharSet=CharSet.Unicode, ExactSpelling=true)]
    private static extern uint ControlTraceW(ulong handle, string name, IntPtr properties, uint command);
    [DllImport("kernel32.dll",ExactSpelling=true,SetLastError=true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryUnbiasedInterruptTime(out ulong time100Nanoseconds);
    [DllImport("kernel32.dll",ExactSpelling=true)]
    private static extern void SetLastError(uint code);
    private static ulong ReadActiveTime()
    {
        SetLastError(0);
        ulong value;
        if(!QueryUnbiasedInterruptTime(out value))
        {
            int code=Marshal.GetLastWin32Error();
            if(code==0) throw new InvalidOperationException("QueryUnbiasedInterruptTime returned false; Windows supplied no extended error.");
            throw new Win32Exception(code,"QueryUnbiasedInterruptTime failed: Win32 "+code+": "+new Win32Exception(code).Message);
        }
        return value;
    }
    private sealed class ActiveClock
    {
        private readonly Func<ulong> read;
        private ulong started;
        internal ActiveClock(Func<ulong> read)
        { if(read==null)throw new ArgumentNullException("read");this.read=read;Restart(); }
        internal static ActiveClock Start() { return new ActiveClock(ReadActiveTime); }
        internal void Restart() { started=read(); }
        internal long ElapsedMilliseconds
        {
            get
            {
                ulong now=read();
                if(now<started) throw new InvalidOperationException("Unbiased active-time counter moved backwards.");
                ulong milliseconds=(now-started)/10000UL;
                if(milliseconds>long.MaxValue)throw new InvalidOperationException("Unbiased active-time interval exceeded its supported range.");
                return (long)milliseconds;
            }
        }
    }

    public sealed class Journal
    {
        public Journal() { }
        public string Nonce;
        public string SessionName;
        public string OutputMode;
        public int ParentPid;
        public long ParentStartUtcTicks;
        public int TrayPid;
        public long TrayStartUtcTicks;
        public long BootUtcTicks;
        public int ChildPid;
        public long ChildStartUtcTicks;
        public bool RegistryInitiallyAbsent;
        public bool TraceInitiallyAbsent;
        public bool TraceCreatedAcknowledged;
        public bool TraceCollisionReported;
        public bool ParentRegistryRestoreConfirmed;
        public bool ParentTraceStopConfirmed;
        public bool ParentChildQuiescenceConfirmed;
        public bool ParentShortcutReleaseConfirmed;
        public string ParentShortcutRecoveryStatus;
        public string RadioInterfacePath;
        public string RadioInstanceId;
        public bool DriverRefreshAttempted;
        public bool DriverRefreshSucceeded;
    }

    public sealed class GuardianState
    {
        public GuardianState() { }
        public string Nonce;
        public bool WriteMayHaveOccurred;
        public bool ValueEnabled;
        public bool RegistryRestoreConfirmed;
        public bool OwnedTraceStoppedOrAbsent;
        public string RestoreStatus;
        public string Phase;
        public bool DriverRefreshAttempted;
        public bool RestartRequired;
        public bool ChildQuiescenceConfirmed;
        public bool ShortcutReleaseConfirmed;
        public string ShortcutRecoveryStatus;
    }

    private sealed class LiveOptions
    {
        internal string Address;
        internal string ExpectedHash;
        internal string OutputMode;
        internal int TrayPid;
        internal long TrayStartUtcTicks;
    }
    private sealed class ExpectedStartupStop : Exception
    {
        internal ExpectedStartupStop() : base("The tray requested Stop during daily startup.") { }
    }
    private static string StartupDecision(bool done,bool trayAlive,bool guardianAlive)
    {
        if(done || !trayAlive) return "stop";
        return guardianAlive ? "continue" : "guardian-failed";
    }
    private static void CheckStartupLease(EventWaitHandle done,LiveOptions options,Process guardian)
    {
        bool ended=done.WaitOne(0);
        bool trayAlive=!ended && ProcessMatches(options.TrayPid,options.TrayStartUtcTicks,DailyPaths.CodeFile("InputLift.exe"));
        bool guardianAlive=guardian!=null && !guardian.HasExited;
        string decision=StartupDecision(ended,trayAlive,guardianAlive);
        if(decision=="stop") throw new ExpectedStartupStop();
        if(decision!="continue") throw new InvalidOperationException("Independent guardian ended during daily startup.");
    }

    public sealed class ActiveRun
    {
        public int Version=1;
        public string RunNonce;
        public int TrayPid,GuardPid,WorkerPid,GuardianPid;
        public long TrayStartUtcTicks,GuardStartUtcTicks,WorkerStartUtcTicks,GuardianStartUtcTicks,BootUtcTicks;
    }
    public sealed class WorkerStatus
    {
        public string Nonce;
        public int WorkerPid;
        public long WorkerStartUtcTicks,ProgressSequence;
        public string State,Error;
    }
    private sealed class ProgressWatch
    {
        private long sequence,progressTime;
        private bool seen;
        internal string Check(long now,WorkerStatus status,int pid,long startTicks,string nonce)
        {
            if(status!=null)
            {
                if(status.Nonce!=nonce || pid<=0 || startTicks<=0 || status.WorkerPid!=pid || status.WorkerStartUtcTicks!=startTicks)
                    return "Worker progress identity does not match its owned nonce/PID/start tuple.";
                if(status.ProgressSequence<=0) return "Worker progress sequence must be positive.";
                if(seen && status.ProgressSequence<sequence) return "Worker progress sequence moved backwards.";
                if(!seen || status.ProgressSequence>sequence)
                { sequence=status.ProgressSequence;progressTime=now;seen=true; }
            }
            if(!seen && now>=StartupGraceMilliseconds) return "Worker did not publish matching progress within the 60-second startup grace.";
            if(seen && now-progressTime>=StaleProgressMilliseconds) return "Worker progress did not advance for 15 seconds; owned capture is being stopped.";
            return null;
        }
    }
    private static void WriteActive(Journal journal,int guardianPid,long guardianTicks)
    {
        WriteJson(Path.Combine(DirectoryPath,"active.json"),new ActiveRun { RunNonce=journal.Nonce,
            TrayPid=journal.TrayPid,TrayStartUtcTicks=journal.TrayStartUtcTicks,
            GuardPid=journal.ParentPid,GuardStartUtcTicks=journal.ParentStartUtcTicks,
            WorkerPid=journal.ChildPid,WorkerStartUtcTicks=journal.ChildStartUtcTicks,
            GuardianPid=guardianPid,GuardianStartUtcTicks=guardianTicks,BootUtcTicks=journal.BootUtcTicks });
    }
    private static WorkerStatus ReadWorkerStatus(string nonce)
    {
        string path=FileFor(nonce,".status.json");
        if(!File.Exists(path)) return null;
        string text;
        if(!DailyPaths.TryReadTextShared(path,65536,out text)) return null;
        return NewSerializer().Deserialize<WorkerStatus>(text);
    }
    private static void RequireJournalContinuity(Journal known,Journal current)
    {
        if(current==null || known==null || current.Nonce!=known.Nonce || current.SessionName!=known.SessionName ||
            current.ParentPid!=known.ParentPid || current.ParentStartUtcTicks!=known.ParentStartUtcTicks ||
            current.TrayPid!=known.TrayPid || current.TrayStartUtcTicks!=known.TrayStartUtcTicks || current.BootUtcTicks!=known.BootUtcTicks ||
            DailyPaths.NormalizeOutputMode(current.OutputMode)!=DailyPaths.NormalizeOutputMode(known.OutputMode))
            throw new InvalidOperationException("Daily ownership identity changed during monitoring.");
        if(known.ChildPid>0 && (current.ChildPid!=known.ChildPid || current.ChildStartUtcTicks!=known.ChildStartUtcTicks))
            throw new InvalidOperationException("The registered worker identity changed or disappeared from its journal.");
    }

    private static string FileFor(string nonce, string suffix)
    {
        if (!ValidNonce(nonce)) throw new ArgumentException("Invalid test nonce.");
        return DailyPaths.FileFor(nonce,suffix);
    }
    private static string EventFor(string nonce, string suffix)
    {
        if (!ValidNonce(nonce)) throw new ArgumentException("Invalid test nonce.");
        return @"Local\GlobeRelay.FullHid." + nonce + "." + suffix;
    }
    private static bool ValidNonce(string nonce)
    {
        Guid parsed;
        return nonce != null && nonce.Length == 32 && Guid.TryParseExact(nonce, "N", out parsed);
    }
    private static bool ValidSession(string name)
    {
        return name != null && name.StartsWith(SessionPrefix, StringComparison.Ordinal) && ValidNonce(name.Substring(SessionPrefix.Length));
    }
    private static string Json(object value) { return NewSerializer().Serialize(value); }
    private static string ErrorText(string operation, Exception error)
    {
        return operation + ": " + error.GetType().Name + ": " + error.Message;
    }
    private static string NativeError(string operation, uint code)
    {
        return operation + ": Win32 " + code + ": " + new Win32Exception(unchecked((int)code)).Message;
    }
    private static void WriteJson(string path, object value)
    {
        DailyPaths.WriteTextProtected(path,Json(value),true);
    }
    private static Journal ReadJournal(string nonce)
    {
        Journal value = NewSerializer().Deserialize<Journal>(DailyPaths.ReadTextShared(FileFor(nonce, ".state.json"),1048576));
        if (value == null || value.Nonce != nonce || value.SessionName != SessionPrefix + nonce ||
            !value.RegistryInitiallyAbsent || !value.TraceInitiallyAbsent ||
            string.IsNullOrEmpty(value.RadioInterfacePath) || string.IsNullOrEmpty(value.RadioInstanceId) ||
            value.TrayPid<=0 || value.TrayStartUtcTicks<=0 || value.BootUtcTicks<=0)
            throw new InvalidOperationException("Test journal identity or baseline is invalid.");
        DailyPaths.NormalizeOutputMode(value.OutputMode);
        return value;
    }

    private static void RequireAdministrator()
    {
        WindowsPrincipal principal = new WindowsPrincipal(WindowsIdentity.GetCurrent());
        if (!principal.IsInRole(WindowsBuiltInRole.Administrator))
            throw new UnauthorizedAccessException("Run this expressly approved test from an administrator console.");
    }
    private static bool RegistryValueAbsent()
    {
        using (RegistryKey key = Registry.LocalMachine.OpenSubKey(RegistryPath, false))
        {
            if (key == null) throw new InvalidOperationException("BTHPORT Parameters key is missing.");
            foreach (string name in key.GetValueNames()) if (string.Equals(name, ValueName, StringComparison.OrdinalIgnoreCase)) return false;
            return true;
        }
    }
    private static void EnableOnlyExpectedValue(Action armBeforeWrite)
    {
        using (RegistryKey key = Registry.LocalMachine.OpenSubKey(RegistryPath, true))
        {
            if (key == null) throw new InvalidOperationException("BTHPORT Parameters key is missing.");
            foreach (string name in key.GetValueNames())
                if (string.Equals(name, ValueName, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("EtwLogSensitiveData appeared after preflight; left unchanged.");
            armBeforeWrite();
            key.SetValue(ValueName, 1, RegistryValueKind.DWord);
            if (key.GetValueKind(ValueName) != RegistryValueKind.DWord || Convert.ToInt64(key.GetValue(ValueName)) != 1)
                throw new InvalidOperationException("EtwLogSensitiveData read-back did not match DWORD 1.");
        }
    }

    // Pure decision function: self-tests do not open the registry.
    private static string RestoreDecision(bool armed, bool present, bool dword, long value)
    {
        if (!armed) return "not-armed";
        if (!present) return "already-absent";
        return dword && value == 1 ? "remove-created-value" : "refuse-changed-value";
    }
    private static bool MayNeedRecovery(bool enabledAck,bool writeStarted,bool journalMayWrite)
    {
        return enabledAck || writeStarted || journalMayWrite;
    }
    private static bool RequiresRestart(bool oldBootRecovery,bool driverRefresh,bool flagWrite,bool guardianRestart)
    { return oldBootRecovery || driverRefresh || flagWrite || guardianRestart; }
    private static string RestoreExpectedValue(bool armed)
    {
        if (!armed) return "not-armed";
        using (RegistryKey key = Registry.LocalMachine.OpenSubKey(RegistryPath, true))
        {
            if (key == null) throw new InvalidOperationException("BTHPORT Parameters key is missing during restoration.");
            bool present = false;
            foreach (string name in key.GetValueNames()) if (string.Equals(name, ValueName, StringComparison.OrdinalIgnoreCase)) present = true;
            bool dword = present && key.GetValueKind(ValueName) == RegistryValueKind.DWord;
            long value = dword ? Convert.ToInt64(key.GetValue(ValueName)) : 0;
            string decision = RestoreDecision(armed, present, dword, value);
            if (decision == "refuse-changed-value")
                throw new InvalidOperationException("EtwLogSensitiveData was changed by another actor; restoration refused to overwrite it.");
            if (decision == "remove-created-value") key.DeleteValue(ValueName, false);
            foreach (string name in key.GetValueNames())
                if (string.Equals(name, ValueName, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Restoration did not leave EtwLogSensitiveData absent.");
            return decision == "remove-created-value" ? "removed-created-value" : decision;
        }
    }

    private static uint TraceControl(string name, uint command, out Guid actualGuid)
    {
        if (!ValidSession(name)) throw new InvalidOperationException("Refusing an invalid trace session name.");
        int size = checked(120 + (name.Length + 1) * 2);
        IntPtr memory = Marshal.AllocHGlobal(size);
        try
        {
            for (int i = 0; i < size; i++) Marshal.WriteByte(memory, i, 0);
            TraceProperties props = new TraceProperties { BufferSize=(uint)size, WnodeFlags=0x00020000, LoggerNameOffset=120, LogFileNameOffset=0 };
            Marshal.StructureToPtr(props, memory, false);
            byte[] nameBytes = Encoding.Unicode.GetBytes(name + "\0");
            Marshal.Copy(nameBytes, 0, IntPtr.Add(memory, 120), nameBytes.Length);
            uint status=ControlTraceW(0, name, memory, command);
            actualGuid=((TraceProperties)Marshal.PtrToStructure(memory,typeof(TraceProperties))).SessionGuid;
            return status;
        }
        finally { Marshal.FreeHGlobal(memory); }
    }
    private static string TraceOwnershipDecision(bool initiallyAbsent,bool collision,bool found,Guid actual,Guid expected)
    {
        if(!initiallyAbsent) return "refuse-preexisting";
        if(collision) return "refuse-collision";
        if(!found) return "absent";
        return actual==expected ? "stop-owned" : "refuse-guid-mismatch";
    }
    private static bool StopAcknowledgedTrace(Journal state, List<string> errors)
    {
        Guid actual;
        uint query=TraceControl(state.SessionName,0,out actual);
        if(query!=0 && query!=4201) { errors.Add(NativeError("Query owned trace for recovery",query)); return false; }
        string decision=TraceOwnershipDecision(state.TraceInitiallyAbsent,state.TraceCollisionReported,query==0,actual,Guid.ParseExact(state.Nonce,"N"));
        if(decision=="absent") return true;
        if(decision!="stop-owned") { errors.Add("Trace recovery left the named session untouched: "+decision+"."); return false; }
        uint result = TraceControl(state.SessionName, 1,out actual);
        if (result == 0 || result == 4201) return true; // stopped or already absent
        errors.Add(NativeError("Stop acknowledged owned trace", result));
        return false;
    }
    private static long StartTicks(Process process) { return process.StartTime.ToUniversalTime().Ticks; }
    private static long QueryBootUtcTicks()
    {
        using(ManagementObjectSearcher searcher=new ManagementObjectSearcher(@"root\cimv2","SELECT LastBootUpTime FROM Win32_OperatingSystem"))
        {
            searcher.Options.Timeout=TimeSpan.FromSeconds(5);
            using(ManagementObjectCollection rows=searcher.Get())
            {
                long ticks=0;int count=0;
                foreach(ManagementObject row in rows)
                {
                    using(row) { ticks=ManagementDateTimeConverter.ToDateTime(Convert.ToString(row["LastBootUpTime"])).ToUniversalTime().Ticks;count++; }
                }
                if(count!=1 || ticks<=0) throw new InvalidOperationException("The current Windows boot time could not be established.");
                return ticks;
            }
        }
    }
    private static bool ProcessMatches(int pid, long ticks, string expectedImage)
    {
        if (pid <= 0 || ticks <= 0) return false;
        try
        {
            using (Process process = Process.GetProcessById(pid))
            {
                if (process.HasExited || StartTicks(process) != ticks) return false;
                if (expectedImage != null && !string.Equals(process.MainModule.FileName, expectedImage, StringComparison.OrdinalIgnoreCase)) return false;
                return true;
            }
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
    }
    private static bool EndOwnedChild(Journal state, List<string> errors)
    {
        string expected = DailyPaths.CodeFile("InputLiftCapture.exe");
        try
        {
            // Child startup is fail-closed: it cannot inject until the parent's
            // durable journal binds its PID/start tuple and the done lease is
            // still open. A missing tuple therefore never owns injected keys.
            if(state.ChildPid<=0 || state.ChildStartUtcTicks<=0) return true;
            using (Process process = Process.GetProcessById(state.ChildPid))
            {
                if(process.HasExited || StartTicks(process)!=state.ChildStartUtcTicks) return true;
                if(!string.Equals(process.MainModule.FileName,expected,StringComparison.OrdinalIgnoreCase))
                { errors.Add("Owned child image no longer matches; quiescence is not confirmed."); return false; }
                if (process.WaitForExit(3000)) return true;
                if(process.HasExited || StartTicks(process)!=state.ChildStartUtcTicks) return true;
                if(!string.Equals(process.MainModule.FileName,expected,StringComparison.OrdinalIgnoreCase))
                { errors.Add("Owned child image changed before termination; quiescence is not confirmed."); return false; }
                process.Kill();
                if (process.WaitForExit(3000)) return true;
                errors.Add("Owned bridge child did not stop within 3 seconds after termination; no emergency shortcut release is permitted.");
                return false;
            }
        }
        catch (ArgumentException) { return true; } // The exact old PID no longer exists.
        catch (Exception error) { errors.Add(ErrorText("Stop owned bridge child", error)); return false; }
    }

    private static string ShortcutRecoveryDecision(bool childQuiesced,bool journalPresent,bool identityValid,bool possiblyDown,bool released)
    {
        if(!childQuiesced) return "refuse-running-child";
        if(!journalPresent) return "missing-shortcut-proof";
        if(!identityValid) return "refuse-shortcut-identity";
        if(possiblyDown==released) return "refuse-inconsistent-shortcut-state";
        return possiblyDown ? "release-owned-shortcut" : "already-released";
    }
    private static bool RecoverOwnedShortcut(Journal state,bool childQuiesced,List<string> errors,out string status)
    {
        status="not-reviewed";
        try
        {
            if(!childQuiesced) { status="refuse-running-child"; errors.Add("Shortcut recovery refused: owned child quiescence is not confirmed."); return false; }
            if(DailyPaths.NormalizeOutputMode(state.OutputMode)=="events-only")
            {
                // This producer never creates native output. EOF/heartbeat loss
                // is handled by the unprivileged consumer's independent guard.
                // This is not proof that a third-party consumer released keys.
                status="event-only-producer-quiescent-no-native-output";
                return true;
            }
            string path=FileFor(state.Nonce,".shortcut.json");
            if(!File.Exists(path))
            {
                if(state.ChildPid<=0 || state.ChildStartUtcTicks<=0) { status="child-not-started"; return true; }
                status="missing-shortcut-proof";
                errors.Add("Shortcut cleanup cannot be confirmed: the child started but its durable shortcut journal is missing. No emergency input was sent.");
                return false;
            }
            ShortcutJournalState chord=NewSerializer().Deserialize<ShortcutJournalState>(DailyPaths.ReadTextShared(path,65536));
            string invalid=GlobeShortcutRelay.ValidateJournalIdentity(chord,state.Nonce,state.ChildPid,state.ChildStartUtcTicks);
            string decision=ShortcutRecoveryDecision(childQuiesced,true,invalid==null,
                chord!=null && chord.PossiblyDown,chord!=null && chord.Released);
            status=decision;
            if(decision=="already-released") return true;
            if(decision!="release-owned-shortcut")
            { errors.Add("Shortcut recovery refused: "+decision+". "+invalid); return false; }
            // The only guardian input is one validated fixed Mouse5-up.
            // The exact child must first be quiescent; no other input is held.
            GlobeShortcutRelay.SendResult release=GlobeShortcutRelay.NativeEmergencyReleaseValidated(chord.MouseButton);
            if(!ReleaseComplete(release.Inserted,release.Error))
            {
                status="emergency-release-incomplete";
                errors.Add(EmergencyReleaseFailure(release));
                return false;
            }
            chord.PossiblyDown=false; chord.Released=true;
            WriteJson(path,chord);
            status="released-owned-shortcut";
            return true;
        }
        catch(Exception error) { errors.Add(ErrorText("Recover owned shortcut",error)); status="shortcut-recovery-error"; return false; }
    }
    private static bool RecoverOwnedShortcutWithMutex(Journal state,bool childQuiesced,List<string> errors,out string status)
    {
        Mutex recovery=null; bool owns=false;
        status="not-reviewed";
        try
        {
            recovery=new Mutex(false,MutexName);
            try { owns=recovery.WaitOne(0); } catch(AbandonedMutexException) { owns=true; }
            if(!owns)
            { status="refuse-another-guard"; errors.Add("Another guard owns the test mutex; no parent emergency shortcut input was sent."); return false; }
            return RecoverOwnedShortcut(state,childQuiesced,errors,out status);
        }
        catch(Exception error) { errors.Add(ErrorText("Acquire shortcut recovery mutex",error)); status="shortcut-recovery-mutex-error"; return false; }
        finally { if(owns&&recovery!=null)recovery.ReleaseMutex(); if(recovery!=null)recovery.Dispose(); }
    }

    private static bool ReleaseComplete(uint inserted,int error) { return inserted==1 && error==0; }
    private static string EmergencyReleaseFailure(GlobeShortcutRelay.SendResult release)
    { return "Emergency Mouse5-up: "+GlobeShortcutRelay.DescribeOutputFailure(release)+". Shortcut journal remains armed."; }
    private static bool AppendOutputHistory(List<string> history,string line,ref long overwritten)
    {
        if(line==null || line.Length>262144) return false;
        if(history.Count>=64) { history.RemoveAt(0);overwritten++; }
        history.Add(line);return true;
    }
    private static bool DrainOutput(bool childStarted,Func<int,bool> stdout,Func<int,bool> stderr,Func<long> elapsed,
        int totalMilliseconds,out bool stdoutComplete,out bool stderrComplete)
    {
        if(totalMilliseconds<0 || totalMilliseconds>1000) throw new ArgumentOutOfRangeException("totalMilliseconds");
        if(!childStarted) { stdoutComplete=stderrComplete=true; return true; }
        stdoutComplete=stdout(totalMilliseconds);
        int remaining=(int)Math.Max(0,totalMilliseconds-Math.Min((long)totalMilliseconds,elapsed()));
        stderrComplete=stderr(remaining);
        return stdoutComplete && stderrComplete;
    }
    private static LiveOptions ParseLive(string[] args)
    {
        if((args.Length!=9 && args.Length!=11) || args[0]!="--run-daily" || args[1]!="--keyboard-address" ||
            args[3]!="--expected-probe-sha256" || args[5]!="--tray-pid" || args[7]!="--tray-start" ||
            (args.Length==11 && args[9]!="--output-mode"))
            throw new ArgumentException("Use the installed tray controller to start the approved daily helper.");
        if(!DailyPaths.ValidKeyboardAddress(args[2]) || !Regex.IsMatch(args[4],@"\A[0-9a-fA-F]{64}\z"))
            throw new ArgumentException("Keyboard address or capture SHA-256 format is invalid.");
        int pid; long ticks;
        if(!int.TryParse(args[6],out pid) || pid<=0 ||
            !long.TryParse(args[8],out ticks) || ticks<=0)
            throw new ArgumentException("Tray identity is invalid.");
        string mode=args.Length==11 ? DailyPaths.NormalizeOutputMode(args[10]) : "deskflow";
        return new LiveOptions { Address=args[2].ToUpperInvariant(),ExpectedHash=args[4].ToUpperInvariant(),OutputMode=mode,
            TrayPid=pid,TrayStartUtcTicks=ticks };
    }
    private static string Hash(Stream stream)
    {
        using (SHA256 hash = SHA256.Create())
        {
            stream.Position=0;
            return BitConverter.ToString(hash.ComputeHash(stream)).Replace("-", "");
        }
    }

    private static int RunGuardian(string nonce)
    {
        List<string> errors = new List<string>();
        bool armed = false, traceStopped = false, valueAbsent = false, ownsMutex=false;
        bool childQuiesced=false, shortcutReleased=false;
        string shortcutStatus="not-reviewed";
        bool driverRefreshMayHaveOccurred=false;
        string restoration = "not-armed", trigger = "not-started";
        Mutex mutex=null;
        GuardianState state=new GuardianState { Nonce=nonce,RestoreStatus="not-armed",Phase="starting" };
        try
        {
            RequireAdministrator();
            DailyPaths.RequireInstalledCodePaths();
            Journal initial = ReadJournal(nonce);
            using (EventWaitHandle ready = EventWaitHandle.OpenExisting(EventFor(nonce,"ready")))
            using (EventWaitHandle arm = EventWaitHandle.OpenExisting(EventFor(nonce,"armed")))
            using (EventWaitHandle writeStarted = EventWaitHandle.OpenExisting(EventFor(nonce,"write-started")))
            using (EventWaitHandle refreshStarted = EventWaitHandle.OpenExisting(EventFor(nonce,"refresh-started")))
            using (EventWaitHandle enabled = EventWaitHandle.OpenExisting(EventFor(nonce,"enabled")))
            using (EventWaitHandle done = EventWaitHandle.OpenExisting(EventFor(nonce,"done")))
            {
                if (!ProcessMatches(initial.ParentPid, initial.ParentStartUtcTicks, Process.GetCurrentProcess().MainModule.FileName))
                    throw new InvalidOperationException("The guardian's parent identity is no longer valid.");
                if(!ProcessMatches(initial.TrayPid,initial.TrayStartUtcTicks,DailyPaths.CodeFile("InputLift.exe")))
                    throw new InvalidOperationException("The owned tray identity is no longer valid.");
                mutex=new Mutex(false,MutexName);
                try { ownsMutex=mutex.WaitOne(0); } catch(AbandonedMutexException) { ownsMutex=true; }
                if(!ownsMutex) throw new InvalidOperationException("Another full-HID guard owns the global test mutex; no flag was written.");
                if (!RegistryValueAbsent()) throw new InvalidOperationException("Guardian preflight found an existing EtwLogSensitiveData value; no acknowledgement granted.");
                Guid preflightGuid;
                uint preflight=TraceControl(initial.SessionName,0,out preflightGuid);
                if(preflight!=4201) throw new InvalidOperationException(preflight==0 ? "Planned session now exists; guardian refused to write the flag." : NativeError("Guardian trace preflight",preflight));
                state.Phase="ready";
                WriteJson(FileFor(nonce,".guardian-state.json"),state);
                ready.Set();
                Journal final = initial;
                try
                {
                    // The guardian is the ONLY writer of DWORD1. A paused parent
                    // can never enable it after this process's deadline expires.
                    ActiveClock armWait=ActiveClock.Start();
                    bool requested=false;
                    while(armWait.ElapsedMilliseconds<10000)
                    {
                        if(done.WaitOne(0)) { trigger="completed-before-enable"; break; }
                        if(!ProcessMatches(initial.ParentPid,initial.ParentStartUtcTicks,null)) { trigger="parent-exited-before-enable"; break; }
                        if(arm.WaitOne(100)) { requested=true; break; }
                    }
                    if(!requested) { if(trigger=="not-started") trigger="arm-request-deadline"; }
                    else if(done.WaitOne(0) || !ProcessMatches(initial.ParentPid,initial.ParentStartUtcTicks,null)) trigger="parent-finished-before-enable";
                    else
                    {
                        EnableOnlyExpectedValue(delegate() {
                            state.WriteMayHaveOccurred=true;
                            state.Phase="write-armed";
                            WriteJson(FileFor(nonce,".guardian-state.json"),state);
                            armed=true;
                            writeStarted.Set();
                        });
                        state.ValueEnabled=true; state.Phase="enabled";
                        WriteJson(FileFor(nonce,".guardian-state.json"),state);
                        enabled.Set();
                    ActiveClock clock = ActiveClock.Start();
                    ProgressWatch progress=new ProgressWatch();
                    while (true)
                    {
                        if (done.WaitOne(250)) { trigger="parent-completed"; break; }
                        bool parentGone=false;
                        try { parentGone=!ProcessMatches(initial.ParentPid, initial.ParentStartUtcTicks, null); }
                        catch(Exception error)
                        {
                            // A transient inspection failure is not proof the
                            // parent died. Remain alive through the deadline.
                            if(errors.Count<16) errors.Add(ErrorText("Inspect guardian parent",error));
                        }
                        if (parentGone) { trigger="parent-exited"; break; }
                        if(!ProcessMatches(initial.TrayPid,initial.TrayStartUtcTicks,DailyPaths.CodeFile("InputLift.exe")))
                        { trigger="tray-exited";break; }
                        Journal current=ReadJournal(nonce);
                        RequireJournalContinuity(final,current);
                        final=current; // Preserve the latest proved worker tuple for independent recovery.
                        if(current.ChildPid>0 && !ProcessMatches(current.ChildPid,current.ChildStartUtcTicks,DailyPaths.CodeFile("InputLiftCapture.exe")))
                        { trigger="worker-exited";break; }
                        string stale=progress.Check(clock.ElapsedMilliseconds,ReadWorkerStatus(nonce),current.ChildPid,current.ChildStartUtcTicks,nonce);
                        if(stale!=null) { trigger="worker-progress-failed";errors.Add(stale);break; }
                    }
                    }
                }
                catch (Exception error) { errors.Add(ErrorText("Guardian monitor",error)); }
                finally
                {
                    // Close the child's retained injection lease before any
                    // cleanup, including when the parent is paused or exited.
                    // Failure here must not skip independent registry cleanup.
                    try { done.Set(); }
                    catch(Exception error) { errors.Add(ErrorText("Close bridge injection lease",error)); }
                    try { Journal current=ReadJournal(nonce);RequireJournalContinuity(final,current);final=current; }
                    catch(Exception error) { errors.Add(ErrorText("Read final guardian journal",error)); }
                    if (armed)
                    {
                        // Each cleanup is isolated: a trace/journal failure must
                        // never prevent the independent flag-restoration attempt.
                        try { childQuiesced=EndOwnedChild(final, errors); }
                        catch(Exception error) { errors.Add(ErrorText("Guardian stop owned child",error)); }
                        try { shortcutReleased=RecoverOwnedShortcut(final,childQuiesced,errors,out shortcutStatus); }
                        catch(Exception error) { errors.Add(ErrorText("Guardian release owned shortcut",error)); }
                        try { traceStopped=StopAcknowledgedTrace(final, errors); }
                        catch(Exception error) { errors.Add(ErrorText("Guardian stop owned trace",error)); }
                        try { restoration=RestoreExpectedValue(true); valueAbsent=RegistryValueAbsent(); }
                        catch(Exception error) { errors.Add(ErrorText("Guardian restore diagnostic flag",error)); }
                    }
                    // An armed restart-required test remains conservative if
                    // the parent exited or its final journal is unreadable.
                    // The retained event survives lost journal/acknowledgement.
                    driverRefreshMayHaveOccurred=armed || final.DriverRefreshAttempted || refreshStarted.WaitOne(0);
                    state.DriverRefreshAttempted=driverRefreshMayHaveOccurred;
                    state.RestartRequired=driverRefreshMayHaveOccurred;
                    state.ChildQuiescenceConfirmed=childQuiesced;
                    state.ShortcutReleaseConfirmed=shortcutReleased;
                    state.ShortcutRecoveryStatus=shortcutStatus;
                    state.WriteMayHaveOccurred=armed;
                    state.RegistryRestoreConfirmed=valueAbsent;
                    state.OwnedTraceStoppedOrAbsent=traceStopped;
                    state.RestoreStatus=restoration;
                    state.Phase="finished";
                    try { WriteJson(FileFor(nonce,".guardian-state.json"),state); }
                    catch(Exception error) { errors.Add(ErrorText("Write guardian state",error)); }
                }
            }
        }
        catch (Exception error) { errors.Add(ErrorText("Guardian", error)); }
        finally { if(ownsMutex&&mutex!=null) mutex.ReleaseMutex();if(mutex!=null)mutex.Dispose(); }
        object result = new { mode="daily-guardian", nonce=nonce, trigger=trigger, armed=armed,
            driverRefreshAttempted=driverRefreshMayHaveOccurred,
            restartRequired=driverRefreshMayHaveOccurred,
            runtimeCacheUnconfirmedUntilWindowsRestart=driverRefreshMayHaveOccurred,
            childQuiescenceConfirmed=childQuiesced,shortcutReleaseConfirmed=shortcutReleased,
            shortcutRecoveryStatus=shortcutStatus,
            registryRestoreStatus=restoration, registryAbsenceConfirmed=valueAbsent,
            ownedTraceStoppedOrAbsent=traceStopped, errors=errors };
        try { WriteJson(FileFor(nonce,".guardian.json"), result); }
        catch (Exception error)
        {
            using(BoundedConsoleWriter failedDisplay=new BoundedConsoleWriter("Guardian cleanup display only"))
            { failedDisplay.TryWrite(ErrorText("Write guardian cleanup result",error)); failedDisplay.Complete(1000); }
            return 1;
        }
        return errors.Count==0 ? 0 : 1;
    }

    private static int RunLive(LiveOptions options)
    {
        List<string> errors = new List<string>();
        List<string> childOutput = new List<string>();
        object gate = new object();
        BoundedConsoleWriter display = new BoundedConsoleWriter("Globe guard display only");
        string nonce=Guid.NewGuid().ToString("N");
        string ownImage=Process.GetCurrentProcess().MainModule.FileName;
        string childImage=DailyPaths.CodeFile("InputLiftCapture.exe");
        Journal journal=new Journal { Nonce=nonce, SessionName=SessionPrefix+nonce,OutputMode=options.OutputMode,
            ParentPid=Process.GetCurrentProcess().Id, ParentStartUtcTicks=StartTicks(Process.GetCurrentProcess()),
            TrayPid=options.TrayPid,TrayStartUtcTicks=options.TrayStartUtcTicks };
        Process child=null, guardian=null;
        bool armRequested=false, settingEnabled=false, registryRestored=false, childCompleted=false;
        bool workerExitErrorReported=false;
        bool guardianExited=false, guardianTerminated=false;
        bool stdoutComplete=false, stderrComplete=false, aggregateOutputComplete=false;
        bool stoppedDuringStartup=false;
        bool stdoutOverflowReported=false;
        long stdoutLinesOverwritten=0;
        bool writeWindowEntered=false;
        bool driverRefreshAttempted=false, driverRefreshSucceeded=false;
        bool recoveryMayHaveOccurred=false,ownedBootRecoveryRemoved=false;
        long guardianStartTicks=0;
        GuardianState finalGuardianState=null;
        string restoration="not-armed";
        int childExitCode=-1;
        FileStream imageLock=null;
        EventWaitHandle ready=null, arm=null, writeStarted=null, refreshStarted=null, enabled=null, done=null;
        ManualResetEvent stdoutEof=new ManualResetEvent(false), stderrEof=new ManualResetEvent(false);
        try
        {
            RequireAdministrator();
            DailyPaths.RequireInstalledCodePaths();
            DailyPaths.RequireConfiguredKeyboard(options.Address);
            DailyPaths.RequireConfiguredOutputMode(options.OutputMode);
            if(!ProcessMatches(options.TrayPid,options.TrayStartUtcTicks,DailyPaths.CodeFile("InputLift.exe")))
                throw new InvalidOperationException("The installed tray PID/start/image does not match the launch request.");
            journal.BootUtcTicks=QueryBootUtcTicks(); // Before any sensitive flag or guardian exists.
            ownedBootRecoveryRemoved=OwnedBootRecovery.TryRecover(journal.BootUtcTicks,delegate() {
                // The old DWORD may already have been consumed at this boot.
                // Mark the recovery window before its deletion can begin.
                recoveryMayHaveOccurred=true;
            });
            var expectedRadio=BluetoothLoggingRefresh.InspectSingleExpectedRadio();
            journal.RadioInterfacePath=expectedRadio.InterfacePath;
            journal.RadioInstanceId=expectedRadio.InstanceId;
            if(!RegistryValueAbsent()) throw new InvalidOperationException("EtwLogSensitiveData already exists; this test requires the verified absent baseline and left it unchanged.");
            journal.RegistryInitiallyAbsent=true;
            Guid preflightGuid;
            uint traceQuery=TraceControl(journal.SessionName,0,out preflightGuid);
            if(traceQuery!=4201)
                throw new InvalidOperationException(traceQuery==0 ? "Planned trace session already exists; no changes made." : NativeError("Query planned trace session",traceQuery));
            journal.TraceInitiallyAbsent=true;
            imageLock=new FileStream(childImage,FileMode.Open,FileAccess.Read,FileShare.Read);
            if(!string.Equals(Hash(imageLock),options.ExpectedHash,StringComparison.Ordinal))
                throw new InvalidOperationException("Probe SHA-256 differs from the expressly reviewed build; no settings changed.");
            ready=new EventWaitHandle(false,EventResetMode.ManualReset,EventFor(nonce,"ready"));
            arm=new EventWaitHandle(false,EventResetMode.ManualReset,EventFor(nonce,"armed"));
            writeStarted=new EventWaitHandle(false,EventResetMode.ManualReset,EventFor(nonce,"write-started"));
            refreshStarted=new EventWaitHandle(false,EventResetMode.ManualReset,EventFor(nonce,"refresh-started"));
            enabled=new EventWaitHandle(false,EventResetMode.ManualReset,EventFor(nonce,"enabled"));
            done=new EventWaitHandle(false,EventResetMode.ManualReset,EventFor(nonce,"done"));
            WriteJson(FileFor(nonce,".state.json"),journal);
            WriteActive(journal,0,0);
            guardian=Process.Start(new ProcessStartInfo(ownImage,"--guardian "+nonce) { UseShellExecute=false,CreateNoWindow=true });
            if(guardian!=null) guardianStartTicks=StartTicks(guardian);
            if(guardian!=null) WriteActive(journal,guardian.Id,guardianStartTicks);
            if(guardian==null) throw new InvalidOperationException("Independent guardian process did not start; no flag was enabled.");
            ActiveClock startupWait=ActiveClock.Start();
            while(!ready.WaitOne(100))
            { CheckStartupLease(done,options,guardian);if(startupWait.ElapsedMilliseconds>=5000)throw new InvalidOperationException("Independent guardian did not acknowledge readiness within 5 seconds; no flag was enabled."); }
            CheckStartupLease(done,options,guardian);
            arm.Set(); armRequested=true;
            startupWait.Restart();
            while(!enabled.WaitOne(100))
            { CheckStartupLease(done,options,guardian);if(startupWait.ElapsedMilliseconds>=5000)throw new InvalidOperationException("Guardian did not acknowledge flag enablement within 5 seconds; teardown was requested."); }
            settingEnabled=true;
            CheckStartupLease(done,options,guardian);
            display.TryWrite("RESTART REQUIRED after this test: once registry removal is verified, use Windows Start > Power > Restart. No automatic reboot occurs.");
            CheckStartupLease(done,options,guardian);
            BluetoothLoggingRefresh.ApplyRefresh(expectedRadio,delegate() {
                CheckStartupLease(done,options,guardian);
                lock(gate) {
                    driverRefreshAttempted=true;
                    journal.DriverRefreshAttempted=true;
                    WriteJson(FileFor(nonce,".state.json"),journal);
                    refreshStarted.Set();
                }
                // Durable I/O may have outlasted the lease. Do not enter the
                // private refresh after an already-observed Stop.
                CheckStartupLease(done,options,guardian);
            });
            driverRefreshSucceeded=true;
            CheckStartupLease(done,options,guardian);
            lock(gate) {
                journal.DriverRefreshSucceeded=true;
                WriteJson(FileFor(nonce,".state.json"),journal);
            }
            CheckStartupLease(done,options,guardian);
            string childArgs="--run-daily --keyboard-address "+options.Address+" --session-name "+journal.SessionName+" --output-mode "+options.OutputMode;
            child=new Process { StartInfo=new ProcessStartInfo(childImage,childArgs) {
                UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true } };
            child.OutputDataReceived += delegate(object sender,DataReceivedEventArgs e)
            {
                if(e.Data==null) { try { stdoutEof.Set(); } catch(ObjectDisposedException) { } return; }
                lock(gate)
                {
                    if(!AppendOutputHistory(childOutput,e.Data,ref stdoutLinesOverwritten))
                    {
                        if(!stdoutOverflowReported)
                        { stdoutOverflowReported=true; errors.Add("Capture worker output line exceeded262144characters; owned capture is being stopped."); }
                        try { done.Set(); } catch(ObjectDisposedException) { }
                        return;
                    }
                    try
                    {
                        if(e.Data.StartsWith("{",StringComparison.Ordinal))
                        {
                            Dictionary<string,object> item=NewSerializer().Deserialize<Dictionary<string,object>>(e.Data);
                            object state,name,created;
                            if(item.TryGetValue("state",out state) && Convert.ToString(state)=="session-created" &&
                                item.TryGetValue("ownedSessionName",out name) && Convert.ToString(name)==journal.SessionName &&
                                item.TryGetValue("ownedSessionCreated",out created) && created is bool && (bool)created)
                            { journal.TraceCreatedAcknowledged=true; WriteJson(FileFor(nonce,".state.json"),journal); }
                            object childErrors;
                            if(item.TryGetValue("errors",out childErrors) && childErrors is IEnumerable)
                            {
                                foreach(object entry in (IEnumerable)childErrors)
                                {
                                    Dictionary<string,object> detail=entry as Dictionary<string,object>;
                                    object stage,code;
                                    if(detail!=null && detail.TryGetValue("stage",out stage) && Convert.ToString(stage)=="StartTraceW" &&
                                        detail.TryGetValue("win32Code",out code) && Convert.ToInt64(code)==183)
                                    { journal.TraceCollisionReported=true; WriteJson(FileFor(nonce,".state.json"),journal); }
                                }
                            }
                        }
                    }
                    catch(Exception error) { if(errors.Count<64) errors.Add(ErrorText("Process probe acknowledgement",error)); }
                }
                display.TryWrite(e.Data);
            };
            child.ErrorDataReceived += delegate(object sender,DataReceivedEventArgs e) {
                if(e.Data==null) { try { stderrEof.Set(); } catch(ObjectDisposedException) { } return; }
                lock(gate) {
                    if(errors.Count<64 && e.Data.Length<=262144) errors.Add("Probe stderr: "+e.Data);
                    else if(errors.Count<65) errors.Add("Probe stderr exceeded the bounded output allowance.");
                }
            };
            CheckStartupLease(done,options,guardian);
            if(!child.Start()) throw new InvalidOperationException("Probe process did not start.");
            lock(gate) { journal.ChildPid=child.Id; journal.ChildStartUtcTicks=StartTicks(child); WriteJson(FileFor(nonce,".state.json"),journal); WriteActive(journal,guardian.Id,guardianStartTicks); }
            child.BeginOutputReadLine(); child.BeginErrorReadLine();
            while(true)
            {
                if(child.WaitForExit(250))
                { childCompleted=true;childExitCode=child.ExitCode; if(childExitCode!=0) lock(gate) { errors.Add("Capture worker exited with code "+childExitCode+"; inspect its exact error status."); workerExitErrorReported=true; } break; }
                if(done.WaitOne(0)) break;
                if(!ProcessMatches(options.TrayPid,options.TrayStartUtcTicks,DailyPaths.CodeFile("InputLift.exe")))
                { done.Set();break; }
                if(guardian.HasExited)
                { done.Set();lock(gate){errors.Add("Independent guardian exited while capture was running; stopping owned worker.");}break; }
            }
        }
        catch(ExpectedStartupStop) { stoppedDuringStartup=true; }
        catch(Exception error) { lock(gate) { errors.Add(ErrorText("Guard",error)); } }
        finally
        {
            // First teardown action, before locks, output draining or file I/O.
            try { if(done!=null) done.Set(); }
            catch(Exception error) { lock(gate) { errors.Add(ErrorText("Close parent injection lease",error)); } }
            try { if(ready!=null) { lock(gate) { WriteJson(FileFor(nonce,".state.json"),journal); } } }
            catch(Exception error) { lock(gate) { errors.Add(ErrorText("Write final test journal",error)); } }
            if(guardian!=null)
            {
                try
                {
                    guardianExited=guardian.WaitForExit(8000);
                    if(!guardianExited)
                    {
                        lock(gate) { errors.Add("Guardian teardown exceeded 8 seconds; terminating the verified owned guardian before fallback restoration."); }
                        if(ProcessMatches(guardian.Id,guardianStartTicks,ownImage))
                        { guardian.Kill(); guardianTerminated=true; guardianExited=guardian.WaitForExit(3000); }
                    }
                    if(!guardianExited) lock(gate) { errors.Add("Guardian exit could not be confirmed. No parent fallback write is allowed while it might resume."); }
                    else if(guardian.ExitCode!=0) lock(gate) { errors.Add("Guardian exited with code "+guardian.ExitCode+"; inspect its exact cleanup errors in the guardian journal."); }
                }
                catch(Exception error) { lock(gate) { errors.Add(ErrorText("Wait for guardian",error)); } }
            }
            // EOF drain is bounded and occurs only after cleanup was requested.
            // If the child never started, no redirected output can be pending.
            Stopwatch drain=Stopwatch.StartNew();
            aggregateOutputComplete=DrainOutput(child!=null && journal.ChildPid>0,
                delegate(int milliseconds) { return stdoutEof.WaitOne(milliseconds); },
                delegate(int milliseconds) { return stderrEof.WaitOne(milliseconds); },
                delegate() { return drain.ElapsedMilliseconds; },1000,out stdoutComplete,out stderrComplete);
            if(!aggregateOutputComplete) lock(gate) { errors.Add("Redirected output did not reach EOF within the one-second drain allowance after cleanup request; aggregate output may be incomplete."); }
            GuardianState guardianState=null;
            try
            {
                string statePath=FileFor(nonce,".guardian-state.json");
                GuardianState candidate=null;
                if(File.Exists(statePath)) candidate=NewSerializer().Deserialize<GuardianState>(DailyPaths.ReadTextShared(statePath,1048576));
                if(candidate!=null && candidate.Nonce!=nonce) throw new InvalidOperationException("Guardian-state nonce mismatch.");
                guardianState=candidate;
                finalGuardianState=guardianState;
            }
            catch(Exception error) { lock(gate) { errors.Add(ErrorText("Read guardian state",error)); } }
            writeWindowEntered=writeStarted!=null && writeStarted.WaitOne(0);
            if(guardianExited)
            {
                // A parent paused across the guardian deadline could have
                // started its child after the guardian already stopped. Recheck
                // our exact nonce/GUID trace and child independently; never rely
                // on the guardian's earlier trace-absence snapshot.
                // Stop the identified child before the final trace query so it
                // cannot create its session after an earlier absence snapshot.
                try { lock(gate) { journal.ParentChildQuiescenceConfirmed=EndOwnedChild(journal,errors); } }
                catch(Exception error) { lock(gate) { errors.Add(ErrorText("Verify owned child teardown",error)); } }
                try
                {
                    lock(gate)
                    {
                        string shortcutStatus;
                        journal.ParentShortcutReleaseConfirmed=RecoverOwnedShortcutWithMutex(journal,journal.ParentChildQuiescenceConfirmed,errors,out shortcutStatus);
                        journal.ParentShortcutRecoveryStatus=shortcutStatus;
                    }
                }
                catch(Exception error) { lock(gate) { errors.Add(ErrorText("Verify owned shortcut release",error)); } }
                try
                {
                    lock(gate)
                    {
                        // If the child never started, a guardian preflight
                        // collision cannot belong to this run. Leave it alone.
                        if(journal.ChildPid>0) journal.ParentTraceStopConfirmed=StopAcknowledgedTrace(journal,errors);
                    }
                }
                catch(Exception error) { lock(gate) { errors.Add(ErrorText("Verify owned trace teardown",error)); } }
                bool mayNeedRecovery=MayNeedRecovery(settingEnabled,writeWindowEntered,guardianState!=null && guardianState.WriteMayHaveOccurred);
                if(guardianState!=null && guardianState.RegistryRestoreConfirmed)
                {
                    restoration=guardianState.RestoreStatus;
                }
                else if(mayNeedRecovery)
                {
                    Mutex recovery=null;bool ownsRecovery=false;
                    try
                    {
                        recovery=new Mutex(false,MutexName);
                        try{ownsRecovery=recovery.WaitOne(0);}catch(AbandonedMutexException){ownsRecovery=true;}
                        if(!ownsRecovery) throw new InvalidOperationException("Another guard owns the diagnostic setting; fallback left it untouched.");
                        // Registry restoration is independent of every trace
                        // and process recovery call once ownership is held.
                        try { lock(gate) { journal.ParentChildQuiescenceConfirmed=EndOwnedChild(journal,errors); } }
                        catch(Exception error) { lock(gate) { errors.Add(ErrorText("Fallback stop owned child",error)); } }
                        try
                        {
                            lock(gate)
                            {
                                string shortcutStatus;
                                journal.ParentShortcutReleaseConfirmed=RecoverOwnedShortcut(journal,journal.ParentChildQuiescenceConfirmed,errors,out shortcutStatus);
                                journal.ParentShortcutRecoveryStatus=shortcutStatus;
                            }
                        }
                        catch(Exception error) { lock(gate) { errors.Add(ErrorText("Fallback release owned shortcut",error)); } }
                        try { lock(gate) { journal.ParentTraceStopConfirmed=StopAcknowledgedTrace(journal,errors); } }
                        catch(Exception error) { lock(gate) { errors.Add(ErrorText("Fallback stop owned trace",error)); } }
                        restoration=RestoreExpectedValue(true);
                    }
                    catch(Exception error) { lock(gate) { errors.Add(ErrorText("Parent fallback cleanup",error)); } }
                    finally { if(ownsRecovery&&recovery!=null)recovery.ReleaseMutex();if(recovery!=null)recovery.Dispose(); }
                }
                try
                {
                    registryRestored=RegistryValueAbsent();
                    if(mayNeedRecovery && !registryRestored) lock(gate) { errors.Add("EtwLogSensitiveData is not absent after teardown; restoration is not confirmed."); }
                    journal.ParentRegistryRestoreConfirmed=registryRestored;
                }
                catch(Exception error) { lock(gate) { errors.Add(ErrorText("Verify final registry absence",error)); } }
            }
            if(imageLock!=null) imageLock.Dispose();
            if(ready!=null) ready.Dispose(); if(arm!=null) arm.Dispose(); if(writeStarted!=null)writeStarted.Dispose(); if(refreshStarted!=null)refreshStarted.Dispose(); if(enabled!=null)enabled.Dispose(); if(done!=null) done.Dispose();
        }
        // The guardian may have signaled done just before the parent observed
        // worker exit. Preserve that exit code instead of silently reporting an
        // unexpected nonzero worker exit as an ordinary Stop.
        try
        {
            if(child!=null && journal.ChildPid>0 && child.HasExited)
            {
                childCompleted=true;childExitCode=child.ExitCode;
                if(childExitCode!=0 && !workerExitErrorReported)
                    lock(gate) { errors.Add("Capture worker exited with code "+childExitCode+"; inspect its exact error status."); }
            }
        }
        catch(Exception error) { lock(gate) { errors.Add(ErrorText("Read final worker exit status",error)); } }
        bool restartMayBeRequired=RequiresRestart(recoveryMayHaveOccurred || ownedBootRecoveryRemoved,driverRefreshAttempted,
            writeWindowEntered,finalGuardianState!=null && finalGuardianState.RestartRequired);
        object result;
        lock(gate)
        {
            result=new { mode="daily-globe-guard",status=errors.Count==0?"ok":"error",nonce=nonce,
                radioInstanceId=journal.RadioInstanceId,
                driverRefreshAttempted=driverRefreshAttempted,driverRefreshSucceeded=driverRefreshSucceeded,
                recoveryMayHaveOccurred=recoveryMayHaveOccurred,ownedBootRecoveryRemoved=ownedBootRecoveryRemoved,
                restartRequired=restartMayBeRequired,
                runtimeCacheUnconfirmedUntilWindowsRestart=restartMayBeRequired,
                sensitiveLoggingSettingEnabled=settingEnabled || (finalGuardianState!=null && finalGuardianState.ValueEnabled),
                flagEnablementAcknowledged=settingEnabled,
                flagWriteMayHaveOccurred=MayNeedRecovery(settingEnabled,writeWindowEntered,finalGuardianState!=null && finalGuardianState.WriteMayHaveOccurred),
                registryRestoreStatus=restoration,registryAbsenceConfirmed=registryRestored,
                armRequested=armRequested,guardianConfirmedExited=guardianExited,guardianTerminatedForRecovery=guardianTerminated,
                traceCreatedAcknowledged=journal.TraceCreatedAcknowledged,ownedTraceStoppedOrAbsent=journal.ParentTraceStopConfirmed,
                childQuiescenceConfirmed=journal.ParentChildQuiescenceConfirmed,
                shortcutReleaseConfirmed=journal.ParentShortcutReleaseConfirmed,
                shortcutRecoveryStatus=journal.ParentShortcutRecoveryStatus,
                trayPid=journal.TrayPid,trayStartUtcTicks=journal.TrayStartUtcTicks,
                guardPid=journal.ParentPid,guardStartUtcTicks=journal.ParentStartUtcTicks,
                workerPid=journal.ChildPid,workerStartUtcTicks=journal.ChildStartUtcTicks,
                stoppedDuringStartup=stoppedDuringStartup,
                bootUtcTicks=journal.BootUtcTicks,
                sessionName=journal.SessionName,stateDirectory=DirectoryPath,
                childCompleted=childCompleted,childExitCode=childExitCode,
                aggregateOutputComplete=aggregateOutputComplete,stdoutEofConfirmed=stdoutComplete,stderrEofConfirmed=stderrComplete,
                consoleDroppedLinesBeforeFinalRender=display.DroppedCount,
                childOutputLinesOverwritten=stdoutLinesOverwritten,
                childAggregateOutput=childOutput.ToArray(),guardianJournal=FileFor(nonce,".guardian.json"),errors=errors.ToArray() };
        }
        try { WriteJson(FileFor(nonce,".result.json"),result); }
        catch(Exception error) { display.TryWrite(ErrorText("Write aggregate guard result",error)); display.Complete(1000); display.Dispose(); stdoutEof.Dispose(); stderrEof.Dispose(); return 1; }
        display.TryWrite(Json(result));
        if(restartMayBeRequired) display.TryWrite("IMPORTANT: Runtime Bluetooth logging cache is unconfirmed. After verified registry removal, use Windows Start > Power > Restart. Registry cleanup alone is not cache cleanup.");
        display.Complete(1000);
        display.Dispose();
        stdoutEof.Dispose(); stderrEof.Dispose();
        if(child!=null) child.Dispose(); if(guardian!=null) guardian.Dispose();
        return errors.Count==0 ? 0 : 1;
    }

    private static int SelfTest()
    {
        List<string> errors=new List<string>();int cases=0;
        Action<bool,string> check=delegate(bool okay,string name){cases++;if(!okay)errors.Add(name);};
        check(RestoreDecision(false,true,true,1)=="not-armed","Unarmed run must not touch a present value.");
        check(RestoreDecision(false,false,false,0)=="not-armed","Unarmed absent state.");
        check(RestoreDecision(true,false,false,0)=="already-absent","Absent state restoration.");
        check(RestoreDecision(true,true,true,1)=="remove-created-value","Owned DWORD1 removal.");
        check(RestoreDecision(true,true,true,0)=="refuse-changed-value","Refuse changed DWORD0.");
        check(RestoreDecision(true,true,true,2)=="refuse-changed-value","Refuse changed DWORD2.");
        check(RestoreDecision(true,true,false,1)=="refuse-changed-value","Refuse non-DWORD value.");
        check(!MayNeedRecovery(false,false,false),"An arm request without write evidence does not own a setting.");
        check(MayNeedRecovery(false,true,false),"Write-started event survives missing journal and absent enabled acknowledgement.");
        check(MayNeedRecovery(true,false,false),"Enabled acknowledgement proves a write window.");
        check(MayNeedRecovery(false,false,true),"Durable guardian journal proves a possible write.");
        check(RequiresRestart(true,false,false,false),"Prior-boot flag recovery still requires restart if no new capture starts.");
        check(!RequiresRestart(false,false,false,false) && RequiresRestart(false,true,false,false) &&
            RequiresRestart(false,false,true,false) && RequiresRestart(false,false,false,true),"Only proven possible logging activation/recovery requests restart.");
        string n="0123456789abcdef0123456789abcdef";
        check(ValidNonce(n),"Valid nonce.");check(!ValidNonce("../bad"),"Refuse path traversal nonce.");
        check(ValidSession(SessionPrefix+n),"Valid unique session.");check(!ValidSession("Other.Session."+n),"Refuse other session prefix.");
        check(!ValidSession(SessionPrefix+n+"0"),"Refuse malformed session length.");
        check(string.Equals("etwlogsensitivedata",ValueName,StringComparison.OrdinalIgnoreCase),"Registry-name checks must ignore case.");
        Guid expected=Guid.ParseExact(n,"N");
        check(TraceOwnershipDecision(true,false,true,expected,expected)=="stop-owned","Nonce GUID proves own session without stdout acknowledgement.");
        check(TraceOwnershipDecision(true,true,true,expected,expected)=="refuse-collision","A reported collision never grants ownership.");
        check(TraceOwnershipDecision(true,false,true,Guid.Empty,expected)=="refuse-guid-mismatch","Wrong session GUID is left untouched.");
        check(TraceOwnershipDecision(false,false,true,expected,expected)=="refuse-preexisting","Pre-existing session is left untouched.");
        check(TraceOwnershipDecision(true,false,false,Guid.Empty,expected)=="absent","An absent session needs no stop.");
        check(ShortcutRecoveryDecision(false,true,true,true,false)=="refuse-running-child","A possibly live child forbids emergency input.");
        check(ShortcutRecoveryDecision(true,false,true,true,false)=="missing-shortcut-proof","Missing durable proof never grants key ownership.");
        check(ShortcutRecoveryDecision(true,true,false,true,false)=="refuse-shortcut-identity","Mismatched PID/nonce proof never grants key ownership.");
        check(ShortcutRecoveryDecision(true,true,true,true,true)=="refuse-inconsistent-shortcut-state","Contradictory armed/released proof is refused.");
        check(ShortcutRecoveryDecision(true,true,true,false,false)=="refuse-inconsistent-shortcut-state","Uninitialized state is refused.");
        check(ShortcutRecoveryDecision(true,true,true,false,true)=="already-released","Confirmed released state sends no emergency input.");
        check(ShortcutRecoveryDecision(true,true,true,true,false)=="release-owned-shortcut","Only quiesced validated armed state permits fixed key-ups.");
        check(ReleaseComplete(1,0) && !ReleaseComplete(0,0) && !ReleaseComplete(2,0) && !ReleaseComplete(1,1460),"Mouse5 recovery requires exactly one inserted and acknowledged UP; ACK failure cannot clear ownership.");
        GlobeShortcutRelay.SendResult emergencyPreflight=new GlobeShortcutRelay.SendResult(0,1460,"OwnedHookHealthAcknowledgement");
        emergencyPreflight.MouseButtonAttempted=false;
        string emergencyPreflightText=EmergencyReleaseFailure(emergencyPreflight);
        check(emergencyPreflightText.Contains("OwnedHookHealthAcknowledgement") &&
            emergencyPreflightText.Contains("SendInput not attempted") && emergencyPreflightText.Contains("journal remains armed"),
            "Emergency preflight failure preserves its exact stage and does not claim Mouse5 native insertion was attempted.");
        GlobeShortcutRelay.SendResult emergencyUnacknowledged=new GlobeShortcutRelay.SendResult(1,1460,"Mouse5HookAcknowledgement");
        string emergencyAckText=EmergencyReleaseFailure(emergencyUnacknowledged);
        check(emergencyAckText.Contains("Mouse5HookAcknowledgement") && emergencyAckText.Contains("SendInput attempted") &&
            emergencyAckText.Contains("inserted 1 of 1") && !emergencyAckText.Contains("not attempted"),
            "Emergency inserted-but-unacknowledged UP retains native-attempt evidence and the hook failure stage.");
        List<string> outputHistory=new List<string>();long overwritten=0;
        for(int index=0;index<1000;index++) AppendOutputHistory(outputHistory,"notification"+index,ref overwritten);
        check(outputHistory.Count==64 && overwritten==936 && outputHistory[0]=="notification936" && outputHistory[63]=="notification999","Daily output retains a bounded64-line ring without a lifetime failure threshold.");
        check(!AppendOutputHistory(outputHistory,new string('x',262145),ref overwritten) && outputHistory.Count==64 && overwritten==936,"Oversized output is refused without adding an unbounded line or erasing evidence.");
        check(StartupGraceMilliseconds==60000 && StaleProgressMilliseconds==15000,"Daily monitor uses startup and progress leases, not a fixed capture lifetime.");
        check(StartupDecision(true,true,true)=="stop" && StartupDecision(false,false,true)=="stop" &&
            StartupDecision(true,true,false)=="stop","Explicit Stop or tray exit is an expected startup cancellation.");
        check(StartupDecision(false,true,false)=="guardian-failed" && StartupDecision(false,true,true)=="continue","Startup continues only with live tray and guardian plus open lease.");
        ulong activeTime=12340000UL;
        long simulatedWallMilliseconds=0;
        ActiveClock activeClock=new ActiveClock(delegate(){return activeTime;});
        ProgressWatch sleepWatch=new ProgressWatch();
        WorkerStatus sleepStatus=new WorkerStatus { Nonce=n,WorkerPid=7,WorkerStartUtcTicks=8,ProgressSequence=1,State="Ready" };
        check(sleepWatch.Check(activeClock.ElapsedMilliseconds,sleepStatus,7,8,n)==null,"Active-time lease starts with matching progress.");
        simulatedWallMilliseconds+=6L*60*60*1000;
        check(simulatedWallMilliseconds>15000 && activeClock.ElapsedMilliseconds==0 &&
            sleepWatch.Check(activeClock.ElapsedMilliseconds,sleepStatus,7,8,n)==null,"Hours of simulated sleep do not consume an unchanged active-time progress lease.");
        activeTime+=14999UL*10000UL;
        check(sleepWatch.Check(activeClock.ElapsedMilliseconds,sleepStatus,7,8,n)==null,"Progress remains valid through14999active milliseconds after sleep.");
        activeTime+=10000UL;
        check(sleepWatch.Check(activeClock.ElapsedMilliseconds,sleepStatus,7,8,n)!=null,"Only15000active milliseconds without progress expires the watchdog.");
        activeClock.Restart();
        check(activeClock.ElapsedMilliseconds==0,"Active-time restart establishes a new startup deadline baseline.");
        string hash=new string('A',64);
        string[] valid={"--run-daily","--keyboard-address","001122334455","--expected-probe-sha256",hash,"--tray-pid","123","--tray-start","456"};
        try { LiveOptions p=ParseLive(valid);check(p.Address=="001122334455"&&p.ExpectedHash==hash&&p.TrayPid==123&&p.TrayStartUtcTicks==456,"Daily invocation validates its synthetic keyboard and tray identity."); }
        catch(Exception e){check(false,"Daily argument parser: "+e.Message);}
        string[] eventArgs={"--run-daily","--keyboard-address","001122334455","--expected-probe-sha256",hash,"--tray-pid","123","--tray-start","456","--output-mode","events-only"};
        check(ParseLive(valid).OutputMode=="deskflow" && ParseLive(eventArgs).OutputMode=="events-only","Event-only is an explicit launch mode; legacy invocation remains Deskflow.");
        foreach(string invalidMode in new[]{"","Events-Only","mouse5","arbitrary-action"})
        { string[] invalid=(string[])eventArgs.Clone();invalid[10]=invalidMode;bool refused=false;try{ParseLive(invalid);}catch(ArgumentException){refused=true;}check(refused,"Unknown output mode refused before any privileged operation."); }
        List<string> eventCleanupErrors=new List<string>();string eventCleanupStatus;
        check(RecoverOwnedShortcut(new Journal { OutputMode="events-only" },true,eventCleanupErrors,out eventCleanupStatus) &&
            eventCleanupErrors.Count==0 && eventCleanupStatus=="event-only-producer-quiescent-no-native-output","Quiescent event-only producer cleanup performs no native input or shortcut-journal read.");
        check(!RecoverOwnedShortcut(new Journal { OutputMode="events-only" },false,eventCleanupErrors,out eventCleanupStatus) &&
            eventCleanupStatus=="refuse-running-child","Event-only cleanup still requires authoritative producer quiescence.");
        foreach(int index in new[]{0,2,4,6,8})
        { string[] invalid=(string[])valid.Clone();invalid[index]="bad";bool refused=false;try{ParseLive(invalid);}catch(ArgumentException){refused=true;}check(refused,"Invalid daily argument refused before live work."); }
        ProgressWatch progress=new ProgressWatch();
        WorkerStatus status=new WorkerStatus { Nonce=n,WorkerPid=7,WorkerStartUtcTicks=8,ProgressSequence=1,State="WaitingForKeyboard" };
        check(progress.Check(59999,null,0,0,n)==null,"Missing worker has bounded startup grace.");
        check(progress.Check(60000,null,0,0,n)!=null,"Missing worker progress expires startup grace.");
        progress=new ProgressWatch();
        check(progress.Check(60001,status,7,8,n)==null,"Valid unbound worker progress is not a failure.");
        check(progress.Check(75000,status,7,8,n)==null && progress.Check(75001,status,7,8,n)!=null,"Unchanged progress expires after15seconds.");
        progress=new ProgressWatch();progress.Check(100,status,7,8,n);
        check(progress.Check(8000,null,7,8,n)==null && progress.Check(15099,null,7,8,n)==null &&
            progress.Check(15100,null,7,8,n)!=null,
            "Repeated unavailable status reads never renew observed progress and still expire at15seconds.");
        progress=new ProgressWatch();progress.Check(100,status,7,8,n);progress.Check(8000,null,7,8,n);
        check(progress.Check(15099,status,7,8,n)==null && progress.Check(15100,null,7,8,n)!=null,
            "Reading the same sequence after a sharing interruption does not renew the original progress lease.");
        progress=new ProgressWatch();progress.Check(100,status,7,8,n);progress.Check(8000,null,7,8,n);
        WorkerStatus recoveredStatus=new WorkerStatus { Nonce=n,WorkerPid=7,WorkerStartUtcTicks=8,ProgressSequence=2,State="Ready" };
        check(progress.Check(15099,recoveredStatus,7,8,n)==null && progress.Check(30098,null,7,8,n)==null &&
            progress.Check(30099,null,7,8,n)!=null,
            "A matching advancing sequence after a transient unavailable read renews the lease from its actual recovery time.");
        progress=new ProgressWatch();progress.Check(1,status,7,8,n);status.ProgressSequence=2;
        check(progress.Check(20000,status,7,8,n)==null,"A newly observed advancing sequence renews progress.");
        status.ProgressSequence=1;
        check(progress.Check(20001,status,7,8,n)!=null,"Regressed progress is refused.");
        progress=new ProgressWatch();status.ProgressSequence=1;
        check(progress.Check(1,status,99,8,n)!=null && progress.Check(1,status,7,99,n)!=null && progress.Check(1,status,7,8,new string('f',32))!=null,"Wrong worker/nonce identity never renews progress.");
        status.ProgressSequence=0;
        check(progress.Check(1,status,7,8,n)!=null,"Zero progress sequence is not a heartbeat.");
        Journal known=new Journal { Nonce=n,SessionName=SessionPrefix+n,ParentPid=1,ParentStartUtcTicks=2,TrayPid=3,TrayStartUtcTicks=4,BootUtcTicks=5 };
        Journal registered=new Journal { Nonce=n,SessionName=SessionPrefix+n,ParentPid=1,ParentStartUtcTicks=2,TrayPid=3,TrayStartUtcTicks=4,BootUtcTicks=5,ChildPid=7,ChildStartUtcTicks=8 };
        bool continuity=true;try{RequireJournalContinuity(known,registered);}catch{continuity=false;}
        check(continuity,"A previously unregistered journal may register exactly one worker tuple.");
        continuity=false;try{RequireJournalContinuity(registered,known);}catch(InvalidOperationException){continuity=true;}
        check(continuity,"Recovery cannot forget a worker tuple it already observed.");
        known.ChildPid=7;known.ChildStartUtcTicks=8;known.BootUtcTicks=6;
        continuity=false;try{RequireJournalContinuity(registered,known);}catch(InvalidOperationException){continuity=true;}
        check(continuity,"Changed boot identity is refused before replacing recovery proof.");
        int pathTests=0;string[] pathErrors=DailyPaths.RunSelfTests(out pathTests);errors.AddRange(pathErrors);
        int recoveryTests=0;
        try { errors.AddRange(OwnedBootRecovery.RunSelfTests(out recoveryTests)); }
        catch(Exception error) { errors.Add("Owned previous-boot recovery pure tests: "+error.Message); }
        string[] helperErrors=BluetoothLoggingRefresh.SelfTestErrors();
        check(helperErrors!=null && helperErrors.Length==0,"Pure Bluetooth refresh helper validation.");
        if(helperErrors!=null) foreach(string detail in helperErrors) errors.Add("Refresh helper: "+detail);
        bool stdoutDone,stderrDone;
        long drainElapsed=0; List<int> drainWaits=new List<int>();
        bool drained=DrainOutput(true,delegate(int milliseconds) { drainWaits.Add(milliseconds);drainElapsed=900;return false; },
            delegate(int milliseconds) { drainWaits.Add(milliseconds);return false; },delegate(){return drainElapsed;},1000,out stdoutDone,out stderrDone);
        check(!drained && !stdoutDone && !stderrDone && drainWaits.Count==2 && drainWaits[0]==1000 && drainWaits[1]==100,
            "Incomplete stdout/stderr share one total drain budget instead of independent unbounded waits");
        drainWaits.Clear(); drainElapsed=2000;
        drained=DrainOutput(true,delegate(int milliseconds){return true;},delegate(int milliseconds){drainWaits.Add(milliseconds);return false;},
            delegate(){return drainElapsed;},1000,out stdoutDone,out stderrDone);
        check(!drained && stdoutDone && !stderrDone && drainWaits[0]==0,"An exhausted drain budget permits only a zero-time stderr check");
        drained=DrainOutput(false,delegate(int milliseconds){throw new Exception("must not wait");},delegate(int milliseconds){throw new Exception("must not wait");},
            delegate(){return 0;},1000,out stdoutDone,out stderrDone);
        check(drained && stdoutDone && stderrDone,"A child that never started has no pending redirected output");
        drained=DrainOutput(true,delegate(int milliseconds){return true;},delegate(int milliseconds){return true;},delegate(){return 0;},1000,out stdoutDone,out stderrDone);
        check(drained && stdoutDone && stderrDone,"Both EOF acknowledgements confirm aggregate completeness");
        int displayTests=0;
        try { errors.AddRange(BoundedConsoleWriter.RunSelfTests(out displayTests)); }
        catch(Exception error) { errors.Add("Pure display queue tests: "+error.Message); }
        check(IntPtr.Size==8 && Marshal.SizeOf(typeof(TraceProperties))==120,"x64 trace-control ABI.");
        check(Marshal.OffsetOf(typeof(TraceProperties),"WnodeFlags").ToInt32()==44,"Trace-control WNODE flags ABI.");
        Console.WriteLine(Json(new{mode="self-test",status=errors.Count==0?"ok":"error",tests=cases,pathTests=pathTests,recoveryTests=recoveryTests,displayTests=displayTests,traceStarted=false,registryWritten=false,childStarted=false,injectionEnabled=false,nativeSendInputCalls=0,errors=errors}));
        return errors.Count==0?0:1;
    }

    private static int Main(string[] args)
    {
        if(args.Length==1 && args[0]=="--self-test") return SelfTest();
        if(args.Length==2 && args[0]=="--guardian" && ValidNonce(args[1])) return RunGuardian(args[1]);
        LiveOptions options;
        try { options=ParseLive(args); }
        catch(Exception error)
        {
            Console.WriteLine(Json(new{status="not-started",traceStarted=false,registryWritten=false,error=error.Message}));
            return 64;
        }
        return RunLive(options);
    }
}
