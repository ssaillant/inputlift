// Copyright (c) 2026 InputLift contributors
// SPDX-License-Identifier: MIT
// Pure policy scenarios: no native APIs, capture, process control, or input.
using System;

internal static class RecoveryPolicyChecks
{
    private static int assertions,cases;
    private static void Check(bool condition,string message)
    { assertions++; if(!condition) throw new Exception(message); }
    private static void Case(string name,Action body)
    {
        try { body();cases++; } catch(Exception error) { throw new Exception(name+": "+error.Message,error); }
    }
    private static void Throws(Action body,string message)
    {
        bool threw=false;
        try { body(); } catch(InvalidOperationException) { threw=true; }
        Check(threw,message);
    }
    private static RecoveryTermination Clean(long generation,RecoveryCause cause,int falseField)
    {
        bool[] b={true,true,true,true,true,true,true,true,true};
        if(falseField>=0) b[falseField]=false;
        return new RecoveryTermination(generation,cause,b[0],b[1],b[2],b[3],b[4],b[5],b[6],b[7],b[8]);
    }
    private static RecoveryTermination Clean(long generation)
    { return Clean(generation,RecoveryCause.WorkerProgressTimeout,-1); }
    private static CaptureRecoveryPolicy Running()
    {
        CaptureRecoveryPolicy p=new CaptureRecoveryPolicy();
        p.BeginEnabledSession();p.CaptureStarted(1);return p;
    }
    private static RecoveryDecision Advance(CaptureRecoveryPolicy p,long first,long last)
    {
        RecoveryDecision d=null;
        for(long now=first;now<=last;now+=500)
        {
            d=p.Tick(now,true,now);
            if(now<last) Check(d.Action==RecoveryAction.None,"Started before requested interval ended.");
        }
        return d;
    }
    private static CaptureRecoveryPolicy Reserved()
    {
        CaptureRecoveryPolicy p=Running();p.CaptureTerminated(Clean(1),1000);
        Check(Advance(p,1000,6000).Action==RecoveryAction.StartFreshCapture,"Eligible timeout did not reserve start.");
        return p;
    }
    internal static int ScenarioCount { get { return cases; } }
    internal static int Run()
    {
        assertions=cases=0;
            Case("one clean timeout starts once after delay and stable Deskflow",delegate {
                CaptureRecoveryPolicy p=Running();p.CaptureTerminated(Clean(1),1000);
                Check(p.RecoveryQueued,"Verified timeout was not queued.");
                RecoveryDecision d=Advance(p,1000,6000);
                Check(d.Action==RecoveryAction.StartFreshCapture && d.IdentificationRequired && p.AttemptUsed,"Recovery missing requirements.");
                Check(!p.RecoveryQueued,"Reserved launch remained queued.");
                for(long now=6500;now<10000;now+=500)
                    Check(p.Tick(now,true,now).Action==RecoveryAction.None,"Repeated tick duplicated launch.");
                p.CaptureStarted(2);
                Check(p.Tick(10000,true,10000).State=="KeepCurrentCapture","Fresh run did not stay running.");
            });
            Case("each missing cleanup proof blocks recovery",delegate {
                for(int field=0;field<9;field++)
                {
                    CaptureRecoveryPolicy p=Running();p.CaptureTerminated(Clean(1,RecoveryCause.WorkerProgressTimeout,field),0);
                    RecoveryDecision d=p.Tick(10000,true,10000);
                    Check(d.Action==RecoveryAction.NeedsAttention,"Missing proof field "+field+" allowed recovery.");
                    Check(!p.AttemptUsed,"Rejected cleanup consumed a launch reservation.");
                    Check(!p.RecoveryQueued,"Rejected cleanup remained queued.");
                }
            });
            Case("an exited guard with unknown cleanup completes ownership and blocks manual restart",delegate {
                CaptureRecoveryPolicy p=Running();
                p.CaptureTerminated(new RecoveryTermination(1,RecoveryCause.Unknown,false,false,false,false,false,false,true,false,false),0);
                Check(!p.GuardRunning,"Exited guard was still described as a live owned run.");
                Check(p.CleanupUnconfirmed && !p.RecoveryQueued,"Unknown child cleanup did not latch the restart block.");
                p.StopByUser();
                bool refused=false;
                try { p.BeginEnabledSession(); }
                catch(InvalidOperationException error)
                { refused=error.Message.Contains("Restart Windows") && !error.Message.Contains("live owned run"); }
                Check(refused,"Manual Start did not explain the actual unconfirmed-cleanup block.");
                Throws(delegate { p.CaptureStarted(2); },"Unknown cleanup permitted another capture.");
                Check(!p.GuardRunning && p.CleanupUnconfirmed,"A blocked Start changed terminal ownership or cleanup state.");
                p=Running();p.StopByUser();
                p.CaptureTerminated(new RecoveryTermination(1,RecoveryCause.UserStop,false,false,false,false,false,false,true,false,false),0);
                Check(!p.GuardRunning && p.CleanupUnconfirmed,"Stop before a missing-result completion lost the terminal cleanup block.");
                Throws(delegate { p.BeginEnabledSession(); },"Prior user Stop allowed unconfirmed child cleanup to reset.");
            });
            Case("each missing terminal cleanup condition blocks a new explicit session",delegate {
                for(int field=0;field<8;field++)
                {
                    CaptureRecoveryPolicy p=Running();p.CaptureTerminated(Clean(1,RecoveryCause.Unknown,field),0);
                    if(field==6)
                        Check(p.GuardRunning,"An unconfirmed guard exit discarded live ownership.");
                    else
                        Check(!p.GuardRunning && p.CleanupUnconfirmed,"Incomplete terminal proof did not block manual restart: "+field);
                    Throws(delegate { p.BeginEnabledSession(); },"Incomplete cleanup reset through explicit Start: "+field);
                }
            });
            Case("verified clean errors allow a new explicit start without an automatic retry",delegate {
                foreach(RecoveryCause cause in new[] {RecoveryCause.Unknown,RecoveryCause.OutputFailure,RecoveryCause.TraceFailure})
                {
                    CaptureRecoveryPolicy p=Running();p.CaptureTerminated(Clean(1,cause,8),0);
                    Check(!p.GuardRunning && !p.CleanupUnconfirmed,"Clean error was mislabeled as live or unconfirmed cleanup.");
                    Check(!p.RecoveryQueued && p.Tick(1,true,1).Action==RecoveryAction.NeedsAttention,"Clean fatal/unclassified error was automatically retried.");
                    p.StopByUser();p.BeginEnabledSession();p.CaptureStarted(2);
                    Check(p.GuardRunning && !p.CleanupUnconfirmed,"A new explicit session after verified cleanup was refused.");
                }
            });
            Case("unclassified and fatal errors are never retried",delegate {
                RecoveryCause[] causes={RecoveryCause.Unknown,RecoveryCause.UserStop,RecoveryCause.HostShutdown,
                    RecoveryCause.OutputFailure,RecoveryCause.TraceFailure};
                foreach(RecoveryCause cause in causes)
                {
                    CaptureRecoveryPolicy p=Running();p.CaptureTerminated(Clean(1,cause,-1),0);
                    Check(p.Tick(10000,true,10000).Action==RecoveryAction.NeedsAttention,"Retried "+cause+".");
                    Check(!p.AttemptUsed,"Fatal cause reserved a start.");
                }
            });
            Case("user stop cancels pending recovery",delegate {
                CaptureRecoveryPolicy p=Running();p.CaptureTerminated(Clean(1),0);p.StopByUser();
                for(long now=0;now<=10000;now+=500)
                    Check(p.Tick(now,true,now).State=="DisabledByUser","Recovery overrode Stop.");
                Throws(delegate { p.CaptureStarted(2); },"Disabled policy accepted new start.");
            });
            Case("user stop cancels even a reserved start",delegate {
                CaptureRecoveryPolicy p=Reserved();p.StopByUser();
                Check(p.Tick(6500,true,6500).Action==RecoveryAction.None,"Stop retained launch request.");
                Throws(delegate { p.CaptureStarted(2); },"Stopped reservation could launch.");
            });
            Case("failed automatic launch is terminal for this enabled session",delegate {
                CaptureRecoveryPolicy p=Reserved();p.AutomaticStartFailed();
                Check(p.AttemptUsed,"Failed launch refunded retry budget.");
                for(long now=6500;now<=10000;now+=500)
                    Check(p.Tick(now,true,now).Action==RecoveryAction.NeedsAttention,"Failed launch retried.");
                Throws(delegate { p.CaptureStarted(2); },"Unreserved failed launch was accepted.");
            });
            Case("healthy intervals never replenish recovery budget",delegate {
                CaptureRecoveryPolicy p=Reserved();p.CaptureStarted(2);
                for(long now=6500;now<=60000;now+=500)
                    Check(p.Tick(now,true,now).State=="KeepCurrentCapture","Healthy interval changed run.");
                p.CaptureTerminated(Clean(2),60500);
                Check(p.Tick(70000,true,70000).Action==RecoveryAction.NeedsAttention,"Second timeout received a retry.");
                Check(p.AttemptUsed,"Second failure cleared budget.");
            });
            Case("explicit manual start resets budget only after the run ends",delegate {
                CaptureRecoveryPolicy p=Reserved();p.CaptureStarted(2);
                Throws(delegate { p.BeginEnabledSession(); },"Live run budget was reset.");
                p.StopByUser();p.CaptureTerminated(Clean(2,RecoveryCause.UserStop,-1),6500);
                p.BeginEnabledSession();Check(!p.AttemptUsed,"Explicit new session did not reset budget.");p.CaptureStarted(3);
                Check(p.Tick(7000,true,7000).State=="KeepCurrentCapture","Explicit restart did not run.");
            });
            Case("duplicate completion neither extends backoff nor authorizes extra launch",delegate {
                CaptureRecoveryPolicy p=Running();p.CaptureTerminated(Clean(1),1000);
                p.CaptureTerminated(Clean(1),2000);
                Check(Advance(p,2000,6000).Action==RecoveryAction.StartFreshCapture,"Duplicate changed original backoff.");
                p.CaptureTerminated(Clean(1),6500);
                Check(p.Tick(6500,true,6500).Action==RecoveryAction.None,"Duplicate completion reserved second launch.");
            });
            Case("conflicting proof for the same run fails closed",delegate {
                CaptureRecoveryPolicy p=Running();p.CaptureTerminated(Clean(1),1000);
                p.CaptureTerminated(Clean(1,RecoveryCause.WorkerProgressTimeout,1),2000);
                Check(p.Tick(6000,true,6000).Action==RecoveryAction.NeedsAttention,"Conflicting proof was ignored.");
                Check(p.CleanupUnconfirmed && !p.GuardRunning,"Conflicting cleanup evidence did not latch the terminal safety block.");
                p.CaptureTerminated(Clean(1),6500);p.StopByUser();
                Throws(delegate { p.BeginEnabledSession(); },"A repeated clean completion or Stop cleared the unconfirmed-cleanup latch.");
            });
            Case("old run completion cannot terminate the newer live run",delegate {
                CaptureRecoveryPolicy p=Reserved();p.CaptureStarted(2);
                p.CaptureTerminated(Clean(1),6500);
                Check(p.Tick(7000,true,7000).State=="KeepCurrentCapture","Stale completion killed current run.");
            });
            Case("invalid generation fails closed without declaring live capture quiescent",delegate {
                foreach(long bad in new long[] {0,2,-1})
                {
                    CaptureRecoveryPolicy p=Running();p.CaptureTerminated(Clean(bad),0);
                    Check(p.Tick(10000,true,10000).Action==RecoveryAction.NeedsAttention,"Bad generation allowed recovery.");
                    Check(p.GuardRunning,"Bad generation discarded the current live guard.");
                    Throws(delegate { p.BeginEnabledSession(); },"Invalid proof declared existing run quiescent.");
                }
            });
            Case("fresh source starts require authorization and increasing identity",delegate {
                CaptureRecoveryPolicy p=new CaptureRecoveryPolicy();
                Throws(delegate { p.CaptureStarted(1); },"Disabled initial start accepted.");
                p.BeginEnabledSession();p.CaptureStarted(1);
                Throws(delegate { p.CaptureStarted(2); },"Parallel capture accepted.");
                p.CaptureTerminated(Clean(1),1000);
                Throws(delegate { p.CaptureStarted(2); },"Backoff was bypassed by an unreserved start.");
                Check(Advance(p,1000,6000).Action==RecoveryAction.StartFreshCapture,"Start was not reserved.");
                Throws(delegate { p.CaptureStarted(1); },"Reused generation accepted.");p.CaptureStarted(2);
            });
            Case("initial launch failure before capture identity allows only a new explicit session",delegate {
                CaptureRecoveryPolicy p=new CaptureRecoveryPolicy();p.BeginEnabledSession();
                Check(p.Tick(0,true,0).State=="WaitingForOwnedStart","Initial session started capture itself.");
                p.AutomaticStartFailed();
                Check(p.Tick(5000,true,5000).Action==RecoveryAction.NeedsAttention,"Initial failed launch retried.");
                Check(!p.RecoveryQueued && !p.AttemptUsed,"Initial failed launch was treated as owned timeout.");
                p.BeginEnabledSession();p.CaptureStarted(1);
                Check(p.Tick(5500,true,5500).State=="KeepCurrentCapture","Explicit start after launch failure failed.");
                Throws(delegate { p.AutomaticStartFailed(); },"Launch failure discarded a live capture identity.");
            });
            Case("Deskflow disconnect resets the stabilization interval",delegate {
                CaptureRecoveryPolicy p=Running();p.CaptureTerminated(Clean(1),0);
                Check(p.Tick(5000,true,5000).Action==RecoveryAction.None,"First responsive probe started capture.");
                p.Tick(5500,true,5500);p.Tick(6000,false,6000);
                Check(Advance(p,6500,8500).Action==RecoveryAction.StartFreshCapture,"Stable route after disconnect did not recover.");
            });
            Case("sparse probes cannot claim continuously responsive Deskflow",delegate {
                CaptureRecoveryPolicy p=Running();p.CaptureTerminated(Clean(1),0);
                p.Tick(5000,true,5000);
                Check(p.Tick(10000,true,10000).Action==RecoveryAction.None,"Two sparse probes were treated as stable.");
                Check(Advance(p,10500,12000).Action==RecoveryAction.StartFreshCapture,"Continuous fresh probes did not stabilize.");
            });
            Case("stale and future responsive probes never authorize restart",delegate {
                CaptureRecoveryPolicy p=Running();p.CaptureTerminated(Clean(1),0);
                Check(p.Tick(5000,true,4499).Action==RecoveryAction.None,"Stale response restarted capture.");
                Check(p.Tick(5500,true,5501).Action==RecoveryAction.None,"Future response restarted capture.");
                Check(Advance(p,6000,8000).Action==RecoveryAction.StartFreshCapture,"Valid fresh sequence did not recover.");
            });
            Case("backward recovery clocks latch manual attention",delegate {
                CaptureRecoveryPolicy p=Running();p.CaptureTerminated(Clean(1),1000);p.Tick(2000,true,2000);
                Check(p.Tick(1999,true,1999).Action==RecoveryAction.NeedsAttention,"Backward Tick recovered.");
                Check(p.Tick(10000,true,10000).Action==RecoveryAction.NeedsAttention,"Clock fault unlatches itself.");
                p=Running();p.Tick(1000,true,1000);p.CaptureTerminated(Clean(1),999);
                Check(p.Tick(2000,true,2000).Action==RecoveryAction.NeedsAttention,"Backward termination recovered.");
                Check(!p.GuardRunning && !p.CleanupUnconfirmed,"A retry-clock fault hid the known clean guard exit.");
                p.BeginEnabledSession();p.CaptureStarted(2);
                Check(p.GuardRunning,"Clock-fault recovery bypassed the explicit new-session requirement.");
            });
        return assertions;
    }
}
