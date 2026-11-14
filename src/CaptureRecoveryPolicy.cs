// Copyright (c) 2026 InputLift contributors
// SPDX-License-Identifier: MIT
// Pure one-shot supervision decisions. No process, registry, capture or input APIs.
using System;

internal enum RecoveryCause { Unknown,WorkerProgressTimeout,UserStop,HostShutdown,OutputFailure,TraceFailure }
internal sealed class RecoveryTermination
{
    internal readonly long Generation;
    internal readonly RecoveryCause Cause;
    internal readonly bool IdentityValid,Clean,RegistryAbsent,TraceStopped,CaptureQuiescent,
        ShortcutReleased,GuardExited,GuardianExited,OnlyClassifiedError;
    internal RecoveryTermination(long generation,RecoveryCause cause,bool identityValid,bool clean,
        bool registryAbsent,bool traceStopped,bool captureQuiescent,bool shortcutReleased,
        bool guardExited,bool guardianExited,bool onlyClassifiedError)
    {
        Generation=generation;Cause=cause;IdentityValid=identityValid;Clean=clean;
        RegistryAbsent=registryAbsent;TraceStopped=traceStopped;CaptureQuiescent=captureQuiescent;
        ShortcutReleased=shortcutReleased;GuardExited=guardExited;GuardianExited=guardianExited;
        OnlyClassifiedError=onlyClassifiedError;
    }
    internal bool Eligible
    {
        get { return Cause==RecoveryCause.WorkerProgressTimeout && CleanupVerified && OnlyClassifiedError; }
    }
    internal bool CleanupVerified
    {
        get { return IdentityValid && Clean && RegistryAbsent && TraceStopped && CaptureQuiescent &&
            ShortcutReleased && GuardExited && GuardianExited; }
    }
    internal bool SameEvidence(RecoveryTermination other)
    {
        return other!=null && Generation==other.Generation && Cause==other.Cause &&
            IdentityValid==other.IdentityValid && Clean==other.Clean && RegistryAbsent==other.RegistryAbsent &&
            TraceStopped==other.TraceStopped && CaptureQuiescent==other.CaptureQuiescent &&
            ShortcutReleased==other.ShortcutReleased && GuardExited==other.GuardExited &&
            GuardianExited==other.GuardianExited && OnlyClassifiedError==other.OnlyClassifiedError;
    }
}

internal enum RecoveryAction { None,StartFreshCapture,NeedsAttention }
internal sealed class RecoveryDecision
{
    internal readonly RecoveryAction Action;
    internal readonly string State;
    internal readonly bool IdentificationRequired;
    internal RecoveryDecision(RecoveryAction action,string state,bool identification)
    { Action=action;State=state;IdentificationRequired=identification; }
}

// Tray-side one-shot supervisor. Call only from its owner/UI thread.
// An existing guard and its independent guardian still perform all cleanup.
internal sealed class CaptureRecoveryPolicy
{
    internal const long RestartDelayMilliseconds=5000,DeskflowStableMilliseconds=2000,MaximumProbeAgeMilliseconds=500;
    private bool enabled,running,attemptUsed,startReserved,attention,initialStartAvailable,cleanupUnconfirmed;
    private long generation,terminalGeneration,stoppedAt,serverSince=-1,serverObservedAt=-1,lastNow=-1;
    private RecoveryTermination termination;
    internal bool AttemptUsed { get { return attemptUsed; } }
    internal bool CleanupUnconfirmed { get { return cleanupUnconfirmed; } }
    internal bool GuardRunning { get { return running; } }
    internal bool RecoveryQueued
    {
        get { return enabled && !running && !attention && !cleanupUnconfirmed && !startReserved && termination!=null &&
            termination.Eligible && !attemptUsed; }
    }

    // Only an explicit user Start or the already-approved logon start calls this.
    // A healthy interval, disconnect/reconnect or timer tick must not reset it.
    internal void BeginEnabledSession()
    {
        if(running) throw new InvalidOperationException("Cannot reset recovery budget during a live owned run.");
        if(cleanupUnconfirmed) throw new InvalidOperationException("Cleanup of the previous owned capture is unconfirmed. Restart Windows before starting another capture.");
        enabled=true;attemptUsed=startReserved=attention=false;initialStartAvailable=true;
        termination=null;serverSince=serverObservedAt=-1;
    }
    internal void StopByUser()
    { enabled=false;startReserved=initialStartAvailable=false;termination=null;serverSince=serverObservedAt=-1; }
    internal void CaptureStarted(long newGeneration)
    {
        if(!enabled || running || attention || cleanupUnconfirmed || newGeneration<=generation || (!initialStartAvailable && !startReserved))
            throw new InvalidOperationException("Fresh owned capture generation and an authorized start are required.");
        generation=newGeneration;running=true;startReserved=initialStartAvailable=false;
        termination=null;serverSince=serverObservedAt=-1;
    }
    internal void CaptureTerminated(RecoveryTermination evidence,long now)
    {
        if(evidence==null) throw new ArgumentNullException("evidence");
        if(evidence.Generation<=0 || evidence.Generation>generation)
        { attention=true;startReserved=false;return; }
        // An old run cannot terminate a newer live run. Its completion was
        // already handled before CaptureStarted authorized that new generation.
        if(evidence.Generation<generation) return;
        bool clockValid=now>=0 && (lastNow<0 || now>=lastNow);
        if(!clockValid) attention=true;
        if(evidence.Generation==terminalGeneration)
        {
            if(termination==null || !termination.SameEvidence(evidence))
            {
                attention=true;startReserved=false;
                if(!evidence.CleanupVerified) cleanupUnconfirmed=true;
            }
            return; // A repeated completion never renews backoff or budget.
        }
        if(!running) { attention=true;startReserved=false;return; }
        if(!evidence.GuardExited)
        { attention=true;startReserved=false;return; }
        // Identity and confirmed guard exit determine terminal ownership, not
        // the retry clock. A clock fault still forbids automatic recovery.
        if(clockValid) lastNow=now;
        terminalGeneration=evidence.Generation;running=false;startReserved=false;
        termination=evidence;stoppedAt=clockValid?now:Math.Max(0,lastNow);serverSince=serverObservedAt=-1;
        if(!evidence.CleanupVerified) cleanupUnconfirmed=true;
        if(!evidence.Eligible || attemptUsed) attention=true;
    }
    internal void AutomaticStartFailed()
    {
        if(running) throw new InvalidOperationException("An owned run must finish cleanup before launch failure is finalized.");
        startReserved=false;attention=true;
    }

    internal RecoveryDecision Tick(long now,bool verifiedResponsiveDeskflow,long observedAt)
    {
        if(now<0 || (lastNow>=0 && now<lastNow)) { attention=true;serverSince=-1; }
        lastNow=now;
        if(!enabled) return new RecoveryDecision(RecoveryAction.None,"DisabledByUser",false);
        if(attention) return new RecoveryDecision(RecoveryAction.NeedsAttention,"ManualActionRequired",false);
        if(running) return new RecoveryDecision(RecoveryAction.None,"KeepCurrentCapture",false);
        if(startReserved) return new RecoveryDecision(RecoveryAction.None,"FreshStartAlreadyRequested",true);
        if(termination==null) return new RecoveryDecision(RecoveryAction.None,"WaitingForOwnedStart",false);
        if(!termination.Eligible || attemptUsed)
            return new RecoveryDecision(RecoveryAction.NeedsAttention,"ManualActionRequired",false);
        bool fresh=verifiedResponsiveDeskflow && observedAt>=0 && now>=observedAt &&
            now-observedAt<=MaximumProbeAgeMilliseconds;
        if(!fresh) serverSince=serverObservedAt=-1;
        else
        {
            // A pair of widely spaced successful probes is not evidence of
            // continuously responsive Deskflow. Require fresh observations at
            // least every 500 ms throughout the stabilization interval.
            if(serverSince<0 || serverObservedAt<0 || observedAt<serverObservedAt ||
                observedAt-serverObservedAt>MaximumProbeAgeMilliseconds)
                serverSince=now;
            serverObservedAt=observedAt;
        }
        if(!fresh || now-serverSince<DeskflowStableMilliseconds)
            return new RecoveryDecision(RecoveryAction.None,"WaitingForResponsiveDeskflow",false);
        if(now-stoppedAt<RestartDelayMilliseconds)
            return new RecoveryDecision(RecoveryAction.None,"RecoveryBackoff",false);
        // Reserve before notifying the caller, so repeated ticks cannot start a
        // second guard. A failed launch consumes this one attempt too.
        attemptUsed=true;startReserved=true;
        return new RecoveryDecision(RecoveryAction.StartFreshCapture,"StartOnceAfterVerifiedCleanup",true);
    }
}
