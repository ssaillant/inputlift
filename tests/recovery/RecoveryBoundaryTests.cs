// Copyright (c) 2026 InputLift contributors
// SPDX-License-Identifier: MIT
// Calls the production recovery classifier through the actual JSON boundary.
// Synthetic journals only: no installed reads, native calls, sockets or input.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Web.Script.Serialization;

internal static class RecoveryBoundaryTests
{
    private static int checks;
    private static readonly List<string> errors=new List<string>();
    private static JavaScriptSerializer Serializer()
    { return new JavaScriptSerializer { MaxJsonLength=1048576 }; }
    private const string Fixture=@"{
      ""active"":{""Version"":1,""RunNonce"":""0123456789abcdef0123456789abcdef"",
        ""TrayPid"":11,""TrayStartUtcTicks"":101,""GuardPid"":12,""GuardStartUtcTicks"":102,
        ""WorkerPid"":13,""WorkerStartUtcTicks"":103,""GuardianPid"":14,""GuardianStartUtcTicks"":104,""BootUtcTicks"":100},
      ""result"":{""mode"":""daily-globe-guard"",""status"":""error"",""nonce"":""0123456789abcdef0123456789abcdef"",
        ""trayPid"":11,""trayStartUtcTicks"":101,""guardPid"":12,""guardStartUtcTicks"":102,
        ""workerPid"":13,""workerStartUtcTicks"":103,""bootUtcTicks"":100,
        ""registryAbsenceConfirmed"":true,""ownedTraceStoppedOrAbsent"":true,""childQuiescenceConfirmed"":true,
        ""shortcutReleaseConfirmed"":true,""guardianConfirmedExited"":true,""guardianTerminatedForRecovery"":false,
        ""childCompleted"":true,""childExitCode"":0,""aggregateOutputComplete"":true,""stdoutEofConfirmed"":true,""stderrEofConfirmed"":true,
        ""errors"":[""Guardian exited with code 1; inspect its exact cleanup errors in the guardian journal.""]},
      ""guardian"":{""mode"":""daily-guardian"",""nonce"":""0123456789abcdef0123456789abcdef"",""trigger"":""worker-progress-failed"",
        ""registryAbsenceConfirmed"":true,""ownedTraceStoppedOrAbsent"":true,""childQuiescenceConfirmed"":true,""shortcutReleaseConfirmed"":true,
        ""errors"":[""Worker progress did not advance for 15 seconds; owned capture is being stopped.""]}
    }";
    private static Dictionary<string,object> Root(string json)
    { return Serializer().Deserialize<Dictionary<string,object>>(json); }
    private static Dictionary<string,object> Section(Dictionary<string,object> root,string section)
    { return (Dictionary<string,object>)root[section]; }
    private static bool Eligible(string json,string mode)
    {
        try
        {
            var serializer=Serializer();var root=Root(json);
            var active=serializer.ConvertToType<InputLift.ActiveRun>(root["active"]);
            return InputLift.IsCleanProgressTimeout(mode,active,
                (Dictionary<string,object>)root["result"],(Dictionary<string,object>)root["guardian"]);
        }
        catch(ArgumentException) { return false; }
        catch(InvalidOperationException) { return false; }
        catch(OverflowException) { return false; }
    }
    private static string Change(string section,string key,object value)
    {
        var root=Root(Fixture);Section(root,section)[key]=value;return Serializer().Serialize(root);
    }
    private static string Remove(string section,string key)
    {
        var root=Root(Fixture);Section(root,section).Remove(key);return Serializer().Serialize(root);
    }
    private static void Check(bool condition,string name)
    { checks++;if(!condition) errors.Add(name); }
    private static void Refused(string json,string name)
    { Check(!Eligible(json,"deskflow"),name); }

    private static void RunJsonChecks()
    {
        Check(Eligible(Fixture,"deskflow"),"Actual JSON arrays and Boolean proofs permit only the canonical cleaned Deskflow timeout.");
        foreach(string mode in new string[] {null,"events-only","Deskflow","deskflow\n",""})
            Check(!Eligible(Fixture,mode),"No automatic producer restart for an unsupported or event-only output mode.");
        foreach(string section in new[] {"active","result","guardian"})
        {
            var root=Root(Fixture);root[section]=null;Refused(Serializer().Serialize(root),"Missing "+section+" cannot authorize recovery.");
        }
        foreach(object value in new object[] {0,2,null})
            Refused(Change("active","Version",value),"Only the supported active-run version is accepted.");
        foreach(string key in new[] {"TrayPid","TrayStartUtcTicks","GuardPid","GuardStartUtcTicks","WorkerPid","WorkerStartUtcTicks","GuardianPid","GuardianStartUtcTicks","BootUtcTicks"})
        {
            Refused(Change("active",key,0),"Zero active identity field is refused: "+key);
            Refused(Change("active",key,-1),"Negative active identity field is refused: "+key);
            Refused(Remove("active",key),"Missing active identity field is refused: "+key);
        }
        foreach(string key in new[] {"trayPid","trayStartUtcTicks","guardPid","guardStartUtcTicks","workerPid","workerStartUtcTicks","bootUtcTicks"})
        {
            long expected=Convert.ToInt64(Section(Root(Fixture),"result")[key],CultureInfo.InvariantCulture);
            foreach(object value in new object[] {0,-1,expected+1,expected.ToString(CultureInfo.InvariantCulture),(double)expected+0.5,null})
                Refused(Change("result",key,value),"Mismatched or nonintegral result identity is refused: "+key);
            Refused(Remove("result",key),"Missing result identity is refused: "+key);
        }
        var longRoot=Root(Fixture);
        Section(longRoot,"active")["WorkerStartUtcTicks"]=281474976710655L;
        Section(longRoot,"result")["workerStartUtcTicks"]=281474976710655L;
        Check(Eligible(Serializer().Serialize(longRoot),"deskflow"),"Matching 64-bit JSON identity survives the production numeric boundary.");
        foreach(string key in new[] {"registryAbsenceConfirmed","ownedTraceStoppedOrAbsent","childQuiescenceConfirmed","shortcutReleaseConfirmed",
            "guardianConfirmedExited","childCompleted","aggregateOutputComplete","stdoutEofConfirmed","stderrEofConfirmed"})
        {
            foreach(object value in new object[] {false,"true",1,null})
                Refused(Change("result",key,value),"Result cleanup/drain proof must be a true Boolean: "+key);
            Refused(Remove("result",key),"Missing result proof is refused: "+key);
        }
        foreach(string key in new[] {"registryAbsenceConfirmed","ownedTraceStoppedOrAbsent","childQuiescenceConfirmed","shortcutReleaseConfirmed"})
        {
            foreach(object value in new object[] {false,"true",1,null})
                Refused(Change("guardian",key,value),"Guardian cleanup proof must be a true Boolean: "+key);
            Refused(Remove("guardian",key),"Missing guardian proof is refused: "+key);
        }
        foreach(object value in new object[] {true,"false",0,null})
            Refused(Change("result","guardianTerminatedForRecovery",value),"Forced/ambiguous guardian teardown is refused.");
        Refused(Remove("result","guardianTerminatedForRecovery"),"Missing false guardian-termination proof is refused.");
        foreach(object value in new object[] {1,-1,"0",null})
            Refused(Change("result","childExitCode",value),"Worker exit must be the integer zero.");
        Refused(Remove("result","childExitCode"),"Missing worker exit status is refused.");
        Refused(Fixture.Replace("\"childExitCode\":0,","\"childExitCode\":0.0,"),"A floating-point JSON zero does not broaden the integer exit schema.");
        Refused(Fixture.Replace("\"childExitCode\":0,","\"childExitCode\":0e0,"),"An exponent JSON zero does not broaden the integer exit schema.");
        foreach(string section in new[] {"result","guardian"})
        {
            foreach(object value in new object[] {null,"not-the-journal-mode",true})
                Refused(Change(section,"mode",value),"Journal mode must match its exact producer: "+section);
            Refused(Remove(section,"mode"),"Missing journal mode is refused: "+section);
            foreach(object value in new object[] {null,true,"0123456789abcdef0123456789abcdee","../bad"})
                Refused(Change(section,"nonce",value),"Journal nonce must match the owned run: "+section);
            Refused(Remove(section,"nonce"),"Missing journal nonce is refused: "+section);
        }
        foreach(object value in new object[] {null,"ok","ERROR",true})
            Refused(Change("result","status",value),"Only the exact classified error status is accepted.");
        foreach(object value in new object[] {null,true,"worker-progress-failed-extra","worker-progress-failed ","child-exited"})
            Refused(Change("guardian","trigger",value),"No loose timeout trigger matching.");
        foreach(string section in new[] {"result","guardian"})
        {
            string expected=(string)((System.Collections.IList)Section(Root(Fixture),section)["errors"])[0];
            object[] badValues={expected,new object[0],new object[] {expected,expected},new object[] {expected,"Additional native output failure"},
                new object[] {expected+" "},new object[] {null},new object[] {true},new object[] {new object[] {expected}},null};
            foreach(object value in badValues)
                Refused(Change(section,"errors",value),"Exactly one canonical JSON-array error is required: "+section);
            Refused(Remove(section,"errors"),"Missing errors array is refused: "+section);
        }
        var extended=Root(Fixture);Section(extended,"result")["unrelatedDiagnosticCounter"]=17;
        Check(Eligible(Serializer().Serialize(extended),"deskflow"),"Unrelated diagnostic fields do not weaken or replace required proofs.");
    }
    private static void RunCompletionChecks()
    {
        var active=Serializer().ConvertToType<InputLift.ActiveRun>(Root(Fixture)["active"]);
        var unknown=InputLift.FinishedGuardTermination(1,RecoveryCause.Unknown,false,null,false,false);
        Check(unknown.GuardExited && !unknown.IdentityValid && !unknown.CleanupVerified,"Missing journal identity retains the known guard exit without inventing child cleanup.");
        var policy=new CaptureRecoveryPolicy();policy.BeginEnabledSession();policy.CaptureStarted(1);
        policy.CaptureTerminated(unknown,0);policy.StopByUser();
        Check(!policy.GuardRunning && policy.CleanupUnconfirmed,"The production completion mapper did not separate exited guard ownership from unknown cleanup.");
        bool refused=false;
        try { policy.BeginEnabledSession(); }
        catch(InvalidOperationException error) { refused=error.Message.Contains("Restart Windows"); }
        Check(refused,"A missing-result completion did not expose the required restart safety block.");
        Check(InputLift.FailureCleanupProof(null,null,"Missing identity")==null,"Missing active identity cannot produce a fabricated cleanup file.");
        var failed=InputLift.FailureCleanupProof(active,null,"Result could not be read");
        Check(failed!=null && !failed.Clean && failed.RunNonce==active.RunNonce && failed.Error=="Result could not be read",
            "Unknown-result failure preserves the owned identity with explicitly unconfirmed cleanup.");
        var clean=new InputLift.CleanupProof { RunNonce=active.RunNonce,Clean=true,RegistryAbsenceConfirmed=true,
            OwnedTraceStoppedOrAbsent=true,ChildQuiescenceConfirmed=true,ShortcutReleaseConfirmed=true,Error="Prior diagnostic" };
        Check(InputLift.FailureCleanupProof(active,clean,"Later route-probe setup failure")==null && clean.Clean && clean.Error=="Prior diagnostic",
            "A later UI/probe failure must not overwrite or mutate verified clean proof.");
        policy=new CaptureRecoveryPolicy();policy.BeginEnabledSession();policy.CaptureStarted(1);
        policy.CaptureTerminated(InputLift.FinishedGuardTermination(1,RecoveryCause.Unknown,true,clean,true,false),0);
        policy.StopByUser();
        Check(!policy.GuardRunning && !policy.CleanupUnconfirmed && !policy.RecoveryQueued,"Verified cleanup plus an unrelated UI error was treated as unknown cleanup or an automatic retry.");
        policy.BeginEnabledSession();policy.CaptureStarted(2);
        Check(policy.GuardRunning,"A new explicit Start after verified cleanup remained incorrectly blocked as a live prior generation.");
        clean.Clean=false;
        var partial=InputLift.FailureCleanupProof(active,clean,"Child quiescence missing");
        Check(object.ReferenceEquals(partial,clean) && !partial.Clean && partial.Error=="Child quiescence missing",
            "Incomplete proof stays incomplete while retaining the actual failure detail.");
    }

    internal static int Main()
    {
        int policyChecks=0,jsonChecks=0;
        try { policyChecks=RecoveryPolicyChecks.Run(); }
        catch(Exception error) { errors.Add("Pure recovery policy: "+error.Message); }
        try { RunJsonChecks(); }
        catch(Exception error) { errors.Add("JSON recovery boundary: "+error.GetType().Name+": "+error.Message); }
        jsonChecks=checks;
        try { RunCompletionChecks(); }
        catch(Exception error) { errors.Add("Finished-guard completion boundary: "+error.GetType().Name+": "+error.Message); }
        Console.WriteLine(Serializer().Serialize(new {
            mode="recovery-tests",status=errors.Count==0?"ok":"error",tests=policyChecks+checks,
            policyScenarios=RecoveryPolicyChecks.ScenarioCount,policyChecks=policyChecks,jsonBoundaryChecks=jsonChecks,
            completionBoundaryChecks=checks-jsonChecks,
            captureStarted=false,installedStateRead=false,taskStarted=false,pipeStarted=false,nativeInput=false,settingsChanged=false,
            errors=errors.ToArray() }));
        return errors.Count==0?0:1;
    }
}
