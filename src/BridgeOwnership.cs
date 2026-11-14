// Copyright (c) 2026 GlobeRelay contributors
// SPDX-License-Identifier: MIT
// Durable proof for this controlled test's fixed shortcut only; no key log.
using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

internal sealed class BridgeOwnership : IDisposable
{
    public sealed class GuardRegistration
    {
        public string Nonce;
        public string SessionName;
        public int ChildPid;
        public long ChildStartUtcTicks;
    }
    public sealed class GuardPhase
    {
        public string Nonce;
        public string Phase;
        public bool ValueEnabled;
    }
    private readonly string nonce, directory, sessionName;
    private readonly int childPid;
    private readonly long childStart;
    private readonly EventWaitHandle done;
    private static JavaScriptSerializer Serializer() { return new JavaScriptSerializer { MaxJsonLength=65536 }; }

    internal BridgeOwnership(string expectedSessionName)
    {
        const string prefix="InputLift.Daily.";
        Guid parsed;
        if(expectedSessionName==null || !expectedSessionName.StartsWith(prefix,StringComparison.Ordinal) ||
            !Guid.TryParseExact(expectedSessionName.Substring(prefix.Length),"N",out parsed))
            throw new InvalidOperationException("A valid guard-owned session nonce is required for shortcut output.");
        sessionName=expectedSessionName; nonce=parsed.ToString("N");
        directory=DailyPaths.StateDirectory;
        using(Process self=Process.GetCurrentProcess()) { childPid=self.Id; childStart=self.StartTime.ToUniversalTime().Ticks; }
        done=EventWaitHandle.OpenExisting(@"Local\GlobeRelay.FullHid."+nonce+".done");
        try
        {
            // Parent writes its child PID immediately after child.Start. This is
            // a bounded registration handshake, never a hardware/input retry.
            Stopwatch wait=Stopwatch.StartNew();
            while(true)
            {
                CheckLeasePhase();
                GuardRegistration state=Read<GuardRegistration>(".state.json");
                if(state==null || state.Nonce!=nonce || state.SessionName!=sessionName)
                    throw new InvalidOperationException("Guard registration nonce/session mismatch; no shortcut output allowed.");
                if(state.ChildPid==childPid && state.ChildStartUtcTicks==childStart) break;
                if(state.ChildPid!=0 || state.ChildStartUtcTicks!=0 || wait.ElapsedMilliseconds>=5000)
                    throw new InvalidOperationException("Guard has not durably registered this child PID/start identity; no shortcut output allowed.");
                Thread.Sleep(25);
            }
        }
        catch { done.Dispose(); throw; }
    }

    private string FileFor(string suffix) { return DailyPaths.FileFor(nonce,suffix); }
    private T Read<T>(string suffix)
    {
        string path=FileFor(suffix);
        FileInfo info=new FileInfo(path);
        if(!info.Exists || info.Length>65536) throw new InvalidOperationException("Missing or oversized guard ownership file: "+Path.GetFileName(path));
        return Serializer().Deserialize<T>(DailyPaths.ReadTextShared(path,65536));
    }
    private void CheckLeasePhase()
    {
        if(done.WaitOne(0)) throw new InvalidOperationException("Guard has ended the test lease; no new shortcut press allowed.");
        GuardPhase phase=Read<GuardPhase>(".guardian-state.json");
        if(phase==null || phase.Nonce!=nonce || phase.Phase!="enabled" || !phase.ValueEnabled)
            throw new InvalidOperationException("Guardian is not in the enabled phase; no new shortcut press allowed.");
    }
    internal bool LeaseEnded { get { return done.WaitOne(0); } }

    internal void Persist(bool armed, int mouseButton)
    {
        if(armed)
        {
            CheckLeasePhase();
            GuardRegistration registration=Read<GuardRegistration>(".state.json");
            if(registration==null || registration.Nonce!=nonce || registration.SessionName!=sessionName ||
                registration.ChildPid!=childPid || registration.ChildStartUtcTicks!=childStart)
                throw new InvalidOperationException("Guard child ownership changed; no shortcut press allowed.");
        }
        ShortcutJournalState state=new ShortcutJournalState { Version=2,OutputKind="mouse5",MouseButton=mouseButton,
            Nonce=nonce,ChildPid=childPid,ChildStartUtcTicks=childStart,PossiblyDown=armed,Released=!armed };
        string validation=GlobeShortcutRelay.ValidateJournalIdentity(state,nonce,childPid,childStart);
        if(validation!=null) throw new InvalidOperationException("Shortcut journal validation: "+validation);
        DailyPaths.WriteTextProtected(FileFor(".shortcut.json"),Serializer().Serialize(state),true);
    }

    public void Dispose() { done.Dispose(); }
}
