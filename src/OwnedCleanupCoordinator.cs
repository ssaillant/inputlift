// Copyright (c) 2026 InputLift contributors
// SPDX-License-Identifier: MIT
// Production cleanup ordering with injected operations. This class performs no
// registry, process, trace, input, file or synchronization API calls itself.
using System;
using System.Collections.Generic;

internal enum OwnedCleanupStep
{
    CloseLease,RestoreFlag,VerifyFlag,PublishRegistryProof,ReadFinalJournal,
    StopChild,ReleaseOutput,StopTrace
}

internal static class OwnedCleanupCoordinator
{
    private static void Attempt(OwnedCleanupStep step,Action operation,Action<OwnedCleanupStep,Exception> failed)
    {
        try { operation(); }
        catch(Exception error) { failed(step,error); }
    }

    internal static void Guardian(bool armed,Action closeLease,Action restoreFlag,Action verifyFlag,
        Action publishRegistryProof,Action readFinalJournal,Action stopChild,Action releaseOutput,
        Action stopTrace,Action<OwnedCleanupStep,Exception> failed)
    {
        Attempt(OwnedCleanupStep.CloseLease,closeLease,failed);
        // The guardian already owns the diagnostic mutex. Its retained armed
        // acknowledgement authorizes restoration without another journal read.
        // Do this before any file, child/image, input or trace cleanup can block.
        if(armed)
        {
            Attempt(OwnedCleanupStep.RestoreFlag,restoreFlag,failed);
            Attempt(OwnedCleanupStep.VerifyFlag,verifyFlag,failed);
            Attempt(OwnedCleanupStep.PublishRegistryProof,publishRegistryProof,failed);
        }
        Attempt(OwnedCleanupStep.ReadFinalJournal,readFinalJournal,failed);
        if(armed)
        {
            Attempt(OwnedCleanupStep.StopChild,stopChild,failed);
            Attempt(OwnedCleanupStep.ReleaseOutput,releaseOutput,failed);
            Attempt(OwnedCleanupStep.StopTrace,stopTrace,failed);
        }
    }

    internal static bool Parent(bool guardianExited,bool ownsRecovery,Action restoreFlag,Action verifyFlag,
        Action publishRegistryProof,Action stopChild,Action releaseOutput,Action releaseOutputWithSeparateMutex,Action stopTrace,
        Action<OwnedCleanupStep,Exception> failed)
    {
        if(!guardianExited) return false;
        if(ownsRecovery)
        {
            Attempt(OwnedCleanupStep.RestoreFlag,restoreFlag,failed);
            Attempt(OwnedCleanupStep.VerifyFlag,verifyFlag,failed);
            Attempt(OwnedCleanupStep.PublishRegistryProof,publishRegistryProof,failed);
        }
        // The old nonce's PID/start/image and trace GUID remain independent
        // cleanup authority after guardian exit. Another setting owner does not
        // authorize leaving those old resources running. Output retains its
        // original separate mutex check when this parent does not own it.
        Attempt(OwnedCleanupStep.StopChild,stopChild,failed);
        Attempt(OwnedCleanupStep.ReleaseOutput,ownsRecovery?releaseOutput:releaseOutputWithSeparateMutex,failed);
        Attempt(OwnedCleanupStep.StopTrace,stopTrace,failed);
        if(!ownsRecovery)
        {
            // Read-only absence verification does not restore a foreign flag.
            Attempt(OwnedCleanupStep.VerifyFlag,verifyFlag,failed);
            Attempt(OwnedCleanupStep.PublishRegistryProof,publishRegistryProof,failed);
        }
        return true;
    }

    internal static string[] RunSelfTests(out int count)
    {
        var errors=new List<string>();int checks=0;
        Action<bool,string> check=delegate(bool okay,string name){checks++;if(!okay)errors.Add(name);};
        var order=new List<string>();var failures=new List<OwnedCleanupStep>();
        Action<OwnedCleanupStep,Exception> failed=delegate(OwnedCleanupStep step,Exception error) {
            failures.Add(step);check(error.Message=="exact injected failure","The original cleanup exception reaches its stage reporter unchanged.");
        };
        Action close=delegate{order.Add("lease");},restore=delegate{order.Add("restore");},verify=delegate{order.Add("verify");};
        Action checkpoint=delegate{order.Add("proof");},journal=delegate{order.Add("journal");};
        Action child=delegate{order.Add("child");},output=delegate{order.Add("output");},trace=delegate{order.Add("trace");};
        Action gatedOutput=delegate{order.Add("output-gated");};
        Guardian(true,close,restore,verify,checkpoint,journal,child,output,trace,failed);
        check(string.Join(",",order.ToArray())=="lease,restore,verify,proof,journal,child,output,trace","Armed guardian restores and verifies the flag before journal/native teardown.");
        check(failures.Count==0,"Successful injected cleanup has no invented error.");
        order.Clear();Guardian(false,close,restore,verify,checkpoint,journal,child,output,trace,failed);
        check(string.Join(",",order.ToArray())=="lease,journal","An unarmed guardian never restores the flag or dispatches native cleanup.");
        foreach(OwnedCleanupStep fault in new[] {OwnedCleanupStep.CloseLease,OwnedCleanupStep.RestoreFlag,OwnedCleanupStep.VerifyFlag,
            OwnedCleanupStep.PublishRegistryProof,OwnedCleanupStep.ReadFinalJournal,OwnedCleanupStep.StopChild,OwnedCleanupStep.ReleaseOutput,OwnedCleanupStep.StopTrace})
        {
            order.Clear();failures.Clear();int restores=0;bool verificationAttempted=false;
            Func<OwnedCleanupStep,string,Action> operation=delegate(OwnedCleanupStep step,string label) { return delegate {
                order.Add(label);
                if(step==OwnedCleanupStep.RestoreFlag)restores++;
                if(step==OwnedCleanupStep.VerifyFlag)verificationAttempted=true;
                // A potentially nonreturning dependency observes the safety
                // attempt at ENTRY, not merely after it returns or throws.
                if(step==OwnedCleanupStep.ReadFinalJournal || step==OwnedCleanupStep.StopChild || step==OwnedCleanupStep.ReleaseOutput || step==OwnedCleanupStep.StopTrace)
                    check(restores==1&&verificationAttempted,"Flag restoration/verification attempts precede every possibly blocking teardown entry: "+step);
                if(step==fault)throw new InvalidOperationException("exact injected failure");
            }; };
            Guardian(true,operation(OwnedCleanupStep.CloseLease,"lease"),operation(OwnedCleanupStep.RestoreFlag,"restore"),
                operation(OwnedCleanupStep.VerifyFlag,"verify"),operation(OwnedCleanupStep.PublishRegistryProof,"proof"),
                operation(OwnedCleanupStep.ReadFinalJournal,"journal"),operation(OwnedCleanupStep.StopChild,"child"),
                operation(OwnedCleanupStep.ReleaseOutput,"output"),operation(OwnedCleanupStep.StopTrace,"trace"),failed);
            check(restores==1,"An injected failure never repeats flag restoration.");
            check(failures.Count==1&&failures[0]==fault,"Only the actual failing guardian stage is reported.");
            check(order.Count==8,"A returned exception at one cleanup stage cannot skip another stage.");
        }
        foreach(bool guardianExited in new[] {false,true}) foreach(bool owns in new[] {false,true})
        {
            order.Clear();failures.Clear();bool admitted=Parent(guardianExited,owns,restore,verify,checkpoint,child,output,gatedOutput,trace,failed);
            check(admitted==guardianExited,"Only confirmed guardian exit admits parent cleanup.");
            check(order.Count==(!admitted?0:owns?6:5),"Guardian/setting ownership boundaries select only their authorized cleanup operations.");
            if(admitted&&!owns)
                check(string.Join(",",order.ToArray())=="child,output-gated,trace,verify,proof","A foreign setting owner blocks restoration/direct output, while exact owned child/trace cleanup remains available.");
        }
        order.Clear();Parent(true,true,restore,verify,checkpoint,child,output,gatedOutput,trace,failed);
        check(string.Join(",",order.ToArray())=="restore,verify,proof,child,output,trace","Owned parent fallback restores before potentially blocking teardown.");
        int foreignValue=1,foreignRestoreCalls=0,directOutputCalls=0,ownedChildCalls=0,ownedTraceCalls=0,gatedOutputCalls=0;
        bool foreignRegistryAbsent=false,outputKnownReleased=false;
        Parent(true,false,delegate{foreignRestoreCalls++;foreignValue=0;},delegate{foreignRegistryAbsent=foreignValue==0;},delegate{},
            delegate{ownedChildCalls++;},delegate{directOutputCalls++;outputKnownReleased=true;},
            delegate{gatedOutputCalls++;/* Existing output mutex refuses this foreign-owner case. */},delegate{ownedTraceCalls++;},failed);
        check(foreignRestoreCalls==0&&foreignValue==1&&directOutputCalls==0,"No setting ownership permits neither a foreign registry write nor direct native output.");
        check(ownedChildCalls==1&&ownedTraceCalls==1&&gatedOutputCalls==1,"A foreign setting owner does not leak the old identity-checked child or trace.");
        check(!foreignRegistryAbsent&&!outputKnownReleased,"Read-only registry/unknown output cannot invent complete cleanup under a foreign owner.");
        foreach(OwnedCleanupStep fault in new[] {OwnedCleanupStep.RestoreFlag,OwnedCleanupStep.VerifyFlag,
            OwnedCleanupStep.PublishRegistryProof,OwnedCleanupStep.StopChild,OwnedCleanupStep.ReleaseOutput,OwnedCleanupStep.StopTrace})
        {
            int restores=0;bool restorationAttempted=false;bool registryAbsent=false;bool childQuiet=false,outputReleased=false,traceStopped=false;
            failures.Clear();
            Func<OwnedCleanupStep,Action> operation=delegate(OwnedCleanupStep step) { return delegate {
                if(step==OwnedCleanupStep.RestoreFlag){restores++;restorationAttempted=true;}
                if(step==OwnedCleanupStep.StopChild || step==OwnedCleanupStep.ReleaseOutput || step==OwnedCleanupStep.StopTrace)
                    check(restorationAttempted,"Parent restoration precedes a failing/blocked native dependency at entry.");
                if(step==fault)throw new InvalidOperationException("exact injected failure");
                if(step==OwnedCleanupStep.VerifyFlag)registryAbsent=true;
                if(step==OwnedCleanupStep.StopChild)childQuiet=true;
                if(step==OwnedCleanupStep.ReleaseOutput)outputReleased=childQuiet;
                if(step==OwnedCleanupStep.StopTrace)traceStopped=true;
            }; };
            Parent(true,true,operation(OwnedCleanupStep.RestoreFlag),operation(OwnedCleanupStep.VerifyFlag),
                operation(OwnedCleanupStep.PublishRegistryProof),operation(OwnedCleanupStep.StopChild),
                operation(OwnedCleanupStep.ReleaseOutput),delegate{throw new InvalidOperationException("Unowned output branch must not run.");},operation(OwnedCleanupStep.StopTrace),failed);
            check(restores==1&&failures.Count==1&&failures[0]==fault,"Parent fallback reports the exact failure without a second restoration attempt.");
            if(fault==OwnedCleanupStep.StopChild || fault==OwnedCleanupStep.ReleaseOutput || fault==OwnedCleanupStep.StopTrace)
                check(registryAbsent&&!(registryAbsent&&childQuiet&&outputReleased&&traceStopped),"A partial registry proof does not become complete cleanup after an unknown native result.");
        }
        count=checks;return errors.ToArray();
    }
}
