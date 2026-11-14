// Copyright (c) 2026 InputLift contributors
// SPDX-License-Identifier: MIT
// Narrow previous-boot recovery of this helper's proven DWORD1 only.
// Live entry is opt-in installed code; self-tests never access files/registry.
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

internal static class OwnedBootRecovery
{
    private const string RegistryPath=@"SYSTEM\CurrentControlSet\Services\BTHPORT\Parameters";
    private const string ValueName="EtwLogSensitiveData";
    private const string MutexName=@"Global\GlobeRelay.TemporaryFullHidProbe";
    private const string SessionPrefix="InputLift.Daily.";
    private const string Administrators="S-1-5-32-544",SystemAccount="S-1-5-18";
    private const uint ForbiddenUserRights=0x500D0156;

    [DllImport("advapi32.dll",CharSet=CharSet.Unicode,ExactSpelling=true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint RegQueryInfoKeyW(SafeRegistryHandle key,IntPtr name,IntPtr nameLength,IntPtr reserved,
        IntPtr subkeys,IntPtr maximumSubkeyName,IntPtr maximumClass,IntPtr values,IntPtr maximumValueName,
        IntPtr maximumValueData,IntPtr securityDescriptorBytes,out long lastWriteFileTime);

    private sealed class Identity
    {
        internal long Pid,Start;
        internal Identity(long pid,long start) { Pid=pid;Start=start; }
        internal Identity Copy() { return new Identity(Pid,Start); }
    }
    private sealed class Proof
    {
        internal int Version;
        internal string Nonce,StateNonce,SessionName,GuardianNonce;
        internal long Boot,StateBoot;
        internal Identity Tray,Guard,Worker,Guardian,StateTray,StateGuard,StateWorker;
        internal bool RegistryInitiallyAbsent,TraceInitiallyAbsent,WriteMayHaveOccurred,ValueEnabled,RestoreConfirmed;
        internal bool IsDword,AlreadyRecovered;
        internal long RegistryValue,LastWrite;
        internal Proof Copy()
        {
            Proof p=(Proof)MemberwiseClone();
            p.Tray=Tray.Copy();p.Guard=Guard.Copy();p.Worker=Worker.Copy();p.Guardian=Guardian.Copy();
            p.StateTray=StateTray.Copy();p.StateGuard=StateGuard.Copy();p.StateWorker=StateWorker.Copy();return p;
        }
    }
    private sealed class Window
    {
        internal string Nonce;
        internal long Boot;
        internal bool Removed;
    }
    private static JavaScriptSerializer Serializer() { return new JavaScriptSerializer { MaxJsonLength=65536 }; }
    private static object Field(IDictionary<string,object> data,string key)
    {
        object value;
        if(data==null || !data.TryGetValue(key,out value)) throw new InvalidOperationException("Owned recovery proof is missing field "+key+".");
        return value;
    }
    private static long Number(IDictionary<string,object> data,string key)
    {
        object value=Field(data,key);
        if(!(value is int) && !(value is long)) throw new InvalidOperationException("Owned recovery proof field "+key+" must be an integer.");
        return Convert.ToInt64(value);
    }
    private static string Text(IDictionary<string,object> data,string key)
    {
        object value=Field(data,key);
        if(!(value is string)) throw new InvalidOperationException("Owned recovery proof field "+key+" must be a string.");
        return (string)value;
    }
    private static bool Boolean(IDictionary<string,object> data,string key)
    {
        object value=Field(data,key);
        if(!(value is bool)) throw new InvalidOperationException("Owned recovery proof field "+key+" must be a boolean.");
        return (bool)value;
    }
    private static Identity ReadIdentity(IDictionary<string,object> data,string pid,string start)
    { return new Identity(Number(data,pid),Number(data,start)); }
    private static bool Same(Identity first,Identity second)
    { return first!=null && second!=null && first.Pid==second.Pid && first.Start==second.Start; }
    private static bool OldIdentity(Identity identity,long oldBoot,long currentBoot,bool zeroAllowed)
    {
        if(identity==null) return false;
        if(zeroAllowed && identity.Pid==0 && identity.Start==0) return true;
        return identity.Pid>0 && identity.Pid<=int.MaxValue && identity.Start>=oldBoot && identity.Start<currentBoot;
    }
    private static string Validate(Proof p,long currentBoot)
    {
        if(p==null) return "Prior-run ownership proof is missing.";
        if(currentBoot<=0 || p.Boot<=0 || currentBoot<=p.Boot) return "Recovery requires a strictly later Windows boot.";
        if(p.Version!=1 || !DailyPaths.ValidNonce(p.Nonce)) return "Prior active-run version or nonce is invalid.";
        if(p.StateNonce!=p.Nonce || p.GuardianNonce!=p.Nonce || p.SessionName!=SessionPrefix+p.Nonce)
            return "Prior active/state/guardian nonce or session identity differs.";
        if(p.StateBoot!=p.Boot) return "Prior state and active boot identities differ.";
        if(!p.RegistryInitiallyAbsent || !p.TraceInitiallyAbsent || !p.WriteMayHaveOccurred || !p.ValueEnabled || p.RestoreConfirmed)
            return "Prior guardian does not prove an un-restored helper-owned DWORD1 write.";
        if(!OldIdentity(p.Tray,p.Boot,currentBoot,false) || !OldIdentity(p.Guard,p.Boot,currentBoot,false) ||
            !OldIdentity(p.Worker,p.Boot,currentBoot,true) || !OldIdentity(p.Guardian,p.Boot,currentBoot,false))
            return "Prior process identities are missing, inconsistent or not from the prior boot.";
        if(!Same(p.Tray,p.StateTray) || !Same(p.Guard,p.StateGuard) || !Same(p.Worker,p.StateWorker))
            return "Prior active and state process identities differ.";
        if(!p.IsDword || p.RegistryValue!=1) return "Existing logging value is not exactly DWORD1; it was left unchanged.";
        if(p.LastWrite<p.Boot || p.LastWrite>=currentBoot) return "Logging registry key was edited outside the proven prior boot; ownership is ambiguous.";
        if(p.AlreadyRecovered) return "That prior run was already recovered; a present logging value cannot be claimed again.";
        return null;
    }

    private static void AssertProtected(string path,bool directory)
    {
        if((File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0)
            throw new InvalidOperationException("Owned recovery refuses reparse-point state: "+Path.GetFileName(path));
        FileSystemSecurity security=directory ? (FileSystemSecurity)Directory.GetAccessControl(path) : File.GetAccessControl(path);
        string owner=((SecurityIdentifier)security.GetOwner(typeof(SecurityIdentifier))).Value;
        if(owner!=Administrators && owner!=SystemAccount) throw new InvalidOperationException("Owned recovery state is not administrator/SYSTEM owned: "+Path.GetFileName(path));
        if(directory && !security.AreAccessRulesProtected) throw new InvalidOperationException("Owned recovery state directory does not have a protected ACL.");
        RawSecurityDescriptor raw=new RawSecurityDescriptor(security.GetSecurityDescriptorBinaryForm(),0);
        bool admin=false,system=false;
        foreach(GenericAce entry in raw.DiscretionaryAcl)
        {
            CommonAce ace=entry as CommonAce;
            if(ace==null || ace.AceQualifier!=AceQualifier.AccessAllowed) throw new InvalidOperationException("Owned recovery state has an unexpected ACL entry.");
            string sid=ace.SecurityIdentifier.Value;uint mask=unchecked((uint)ace.AccessMask);
            if(sid==DailyPaths.OwnerSid)
            { if((mask&ForbiddenUserRights)!=0) throw new InvalidOperationException("Unelevated account can modify owned recovery proof."); }
            else if(sid==Administrators || sid==SystemAccount)
            {
                if((mask&0x10000000)==0 && (mask&0x001F01FF)!=0x001F01FF) throw new InvalidOperationException("Administrator/SYSTEM full control is missing on recovery state.");
                if(sid==Administrators) admin=true;else system=true;
            }
            else throw new InvalidOperationException("Unexpected principal can access owned recovery proof.");
        }
        if(!admin || !system) throw new InvalidOperationException("Owned recovery proof lacks required administrator/SYSTEM ACL entries.");
    }
    private static Dictionary<string,object> Read(string path)
    {
        AssertProtected(path,false);
        var result=Serializer().Deserialize<Dictionary<string,object>>(DailyPaths.ReadTextShared(path,65536));
        if(result==null) throw new InvalidOperationException("Owned recovery JSON object is missing: "+Path.GetFileName(path));
        return result;
    }
    private static Proof Load(long currentBoot,RegistryKey key)
    {
        AssertProtected(DailyPaths.StateDirectory,true);
        var active=Read(Path.Combine(DailyPaths.StateDirectory,"active.json"));
        string nonce=Text(active,"RunNonce");
        if(!DailyPaths.ValidNonce(nonce)) throw new InvalidOperationException("Prior active run has an invalid nonce.");
        var state=Read(DailyPaths.FileFor(nonce,".state.json"));
        var guardian=Read(DailyPaths.FileFor(nonce,".guardian-state.json"));
        Proof proof=new Proof {
            Version=checked((int)Number(active,"Version")),Nonce=nonce,StateNonce=Text(state,"Nonce"),SessionName=Text(state,"SessionName"),
            GuardianNonce=Text(guardian,"Nonce"),Boot=Number(active,"BootUtcTicks"),StateBoot=Number(state,"BootUtcTicks"),
            Tray=ReadIdentity(active,"TrayPid","TrayStartUtcTicks"),Guard=ReadIdentity(active,"GuardPid","GuardStartUtcTicks"),
            Worker=ReadIdentity(active,"WorkerPid","WorkerStartUtcTicks"),Guardian=ReadIdentity(active,"GuardianPid","GuardianStartUtcTicks"),
            StateTray=ReadIdentity(state,"TrayPid","TrayStartUtcTicks"),StateGuard=ReadIdentity(state,"ParentPid","ParentStartUtcTicks"),
            StateWorker=ReadIdentity(state,"ChildPid","ChildStartUtcTicks"),RegistryInitiallyAbsent=Boolean(state,"RegistryInitiallyAbsent"),
            TraceInitiallyAbsent=Boolean(state,"TraceInitiallyAbsent"),WriteMayHaveOccurred=Boolean(guardian,"WriteMayHaveOccurred"),
            ValueEnabled=Boolean(guardian,"ValueEnabled"),RestoreConfirmed=Boolean(guardian,"RegistryRestoreConfirmed"),
            AlreadyRecovered=File.Exists(Path.Combine(DailyPaths.StateDirectory,"recovery-"+nonce+".json")) };
        Snapshot(key,proof);
        string invalid=Validate(proof,currentBoot);
        if(invalid!=null) throw new InvalidOperationException(invalid);
        return proof;
    }
    private static bool Present(RegistryKey key)
    {
        if(key==null) return false;
        foreach(string name in key.GetValueNames()) if(string.Equals(name,ValueName,StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
    private static void Snapshot(RegistryKey key,Proof proof)
    {
        if(!Present(key)) throw new InvalidOperationException("Logging value disappeared while ownership was being checked; no deletion was attempted.");
        proof.IsDword=key.GetValueKind(ValueName)==RegistryValueKind.DWord;
        proof.RegistryValue=proof.IsDword ? Convert.ToInt64(key.GetValue(ValueName,null,RegistryValueOptions.DoNotExpandEnvironmentNames)) : 0;
        long fileTime;
        uint status=RegQueryInfoKeyW(key.Handle,IntPtr.Zero,IntPtr.Zero,IntPtr.Zero,IntPtr.Zero,IntPtr.Zero,IntPtr.Zero,
            IntPtr.Zero,IntPtr.Zero,IntPtr.Zero,IntPtr.Zero,out fileTime);
        if(status!=0) throw new Win32Exception(unchecked((int)status),"RegQueryInfoKeyW recovery last-write query failed with Win32 "+status+".");
        proof.LastWrite=DateTime.FromFileTimeUtc(fileTime).Ticks;
    }
    private static Window ReadWindow(long currentBoot,string nonce)
    {
        string path=Path.Combine(DailyPaths.StateDirectory,"recovery-window.json");
        if(!File.Exists(path)) return null;
        var data=Read(path);
        Window window=new Window { Nonce=Text(data,"OldRunNonce"),Boot=Number(data,"CurrentBootUtcTicks") };
        if(Number(data,"Version")!=1 || !DailyPaths.ValidNonce(window.Nonce) || window.Boot<=0 || window.Boot>currentBoot || !Boolean(data,"MayHaveOccurred"))
            throw new InvalidOperationException("Existing owned recovery window is invalid or from an unverified boot.");
        object removed;
        if(data.TryGetValue("Removed",out removed)) { if(!(removed is bool)) throw new InvalidOperationException("Recovery Removed field must be a boolean.");window.Removed=(bool)removed; }
        if(window.Nonce==nonce && window.Removed) throw new InvalidOperationException("That run's logging value was already removed; a present value is ambiguous.");
        if(window.Boot==currentBoot && window.Nonce!=nonce) throw new InvalidOperationException("A different recovery window already exists in this Windows boot.");
        return window;
    }
    private static void WriteProtected(string path,object value)
    {
        if(File.Exists(path)) AssertProtected(path,false);
        string temporary=path+"."+Guid.NewGuid().ToString("N")+".tmp";
        FileSecurity security=new FileSecurity();
        security.SetSecurityDescriptorSddlForm("O:BAG:BAD:P(A;;FA;;;SY)(A;;FA;;;BA)(A;;FRFX;;;"+DailyPaths.OwnerSid+")");
        byte[] bytes=new UTF8Encoding(false).GetBytes(Serializer().Serialize(value));
        using(FileStream file=new FileStream(temporary,FileMode.CreateNew,FileSystemRights.Write,FileShare.None,4096,FileOptions.WriteThrough,security))
        { file.Write(bytes,0,bytes.Length);file.Flush(true); }
        if(File.Exists(path)) File.Replace(temporary,path,null);else File.Move(temporary,path);
        AssertProtected(path,false);
    }

    internal static bool TryRecover(long currentBootUtcTicks,Action recoveryWindowEntered)
    {
        if(recoveryWindowEntered==null) throw new ArgumentNullException("recoveryWindowEntered");
        DailyPaths.RequireInstalledCodePaths();
        using(RegistryKey baseline=Registry.LocalMachine.OpenSubKey(RegistryPath,false)) { if(!Present(baseline)) return false; }
        using(Mutex mutex=new Mutex(false,MutexName))
        {
            bool owns=false;
            try
            {
                try { owns=mutex.WaitOne(0); } catch(AbandonedMutexException) { owns=true; }
                if(!owns) throw new InvalidOperationException("Another capture owns the global logging mutex; prior-boot recovery was refused.");
                using(RegistryKey key=Registry.LocalMachine.OpenSubKey(RegistryPath,true))
                {
                    if(!Present(key)) return false;
                    Proof proof=Load(currentBootUtcTicks,key);
                    ReadWindow(currentBootUtcTicks,proof.Nonce);
                    // Callback first, then durable protected marker, before ANY
                    // possible delete. Keep the marker even if later work fails.
                    recoveryWindowEntered();
                    WriteProtected(Path.Combine(DailyPaths.StateDirectory,"recovery-window.json"),new {
                        Version=1,OldRunNonce=proof.Nonce,CurrentBootUtcTicks=currentBootUtcTicks,MayHaveOccurred=true,Removed=false });
                    Snapshot(key,proof);
                    string changed=Validate(proof,currentBootUtcTicks);
                    if(changed!=null) throw new InvalidOperationException("Recovery pre-delete recheck: "+changed);
                    key.DeleteValue(ValueName,true);
                    if(Present(key)) throw new InvalidOperationException("Owned recovery deletion did not leave the logging value absent; no retry was attempted.");
                    try
                    {
                        WriteProtected(Path.Combine(DailyPaths.StateDirectory,"recovery-"+proof.Nonce+".json"),new {
                            Version=1,OldRunNonce=proof.Nonce,OldBootUtcTicks=proof.Boot,CurrentBootUtcTicks=currentBootUtcTicks,
                            RegistryAbsenceConfirmed=true,RecoveredAtUtc=DateTime.UtcNow.ToString("o") });
                        WriteProtected(Path.Combine(DailyPaths.StateDirectory,"recovery-window.json"),new {
                            Version=1,OldRunNonce=proof.Nonce,CurrentBootUtcTicks=currentBootUtcTicks,MayHaveOccurred=true,Removed=true });
                    }
                    catch(Exception error) { throw new InvalidOperationException("Owned DWORD1 was removed and verified, but recovery completion journal failed. Restart remains required: "+error.Message,error); }
                    return true;
                }
            }
            finally { if(owns) mutex.ReleaseMutex(); }
        }
    }

    private static Proof Fixture()
    {
        string nonce="0123456789abcdef0123456789abcdef";
        return new Proof { Version=1,Nonce=nonce,StateNonce=nonce,GuardianNonce=nonce,SessionName=SessionPrefix+nonce,
            Boot=1000,StateBoot=1000,Tray=new Identity(1,1100),Guard=new Identity(2,1200),Worker=new Identity(3,1300),
            Guardian=new Identity(4,1250),StateTray=new Identity(1,1100),StateGuard=new Identity(2,1200),StateWorker=new Identity(3,1300),
            RegistryInitiallyAbsent=true,TraceInitiallyAbsent=true,WriteMayHaveOccurred=true,ValueEnabled=true,RestoreConfirmed=false,
            IsDword=true,RegistryValue=1,LastWrite=1400,AlreadyRecovered=false };
    }
    internal static string[] RunSelfTests(out int count)
    {
        int tests=0;List<string> errors=new List<string>();
        Action<bool,string> check=delegate(bool okay,string name){tests++;if(!okay)errors.Add(name);};
        Proof good=Fixture();check(Validate(good,2000)==null,"Complete prior-boot proof permits only owned DWORD1 recovery");
        check(Validate(good,1000)!=null && Validate(good,999)!=null && Validate(good,0)!=null,"Same/older/unknown boot is refused");
        Action<Proof>[] mutations={
            delegate(Proof p){p.Version=0;},delegate(Proof p){p.Nonce="bad";},delegate(Proof p){p.StateNonce="bad";},
            delegate(Proof p){p.GuardianNonce="bad";},delegate(Proof p){p.SessionName="Other.Session."+p.Nonce;},
            delegate(Proof p){p.SessionName="GlobeRelay.FnProbe."+p.Nonce;},
            delegate(Proof p){p.Boot=0;},delegate(Proof p){p.StateBoot++;},delegate(Proof p){p.RegistryInitiallyAbsent=false;},
            delegate(Proof p){p.TraceInitiallyAbsent=false;},delegate(Proof p){p.WriteMayHaveOccurred=false;},
            delegate(Proof p){p.ValueEnabled=false;},delegate(Proof p){p.RestoreConfirmed=true;},
            delegate(Proof p){p.Tray.Pid=0;},delegate(Proof p){p.Guard.Start=2000;},delegate(Proof p){p.Worker.Start=999;},
            delegate(Proof p){p.Guardian.Pid=0;},delegate(Proof p){p.StateTray.Pid++;},delegate(Proof p){p.StateGuard.Start++;},
            delegate(Proof p){p.StateWorker.Pid++;},delegate(Proof p){p.IsDword=false;},delegate(Proof p){p.RegistryValue=0;},
            delegate(Proof p){p.RegistryValue=2;},delegate(Proof p){p.LastWrite=999;},delegate(Proof p){p.LastWrite=2000;},
            delegate(Proof p){p.AlreadyRecovered=true;} };
        foreach(Action<Proof> mutation in mutations) { Proof p=good.Copy();mutation(p);check(Validate(p,2000)!=null,"Each ownership/value/boot condition is independently necessary"); }
        Proof emptyWorker=good.Copy();emptyWorker.Worker=new Identity(0,0);emptyWorker.StateWorker=new Identity(0,0);
        check(Validate(emptyWorker,2000)==null,"A startup crash before worker registration is recoverable with matching zero tuple");
        emptyWorker.Worker.Start=1;check(Validate(emptyWorker,2000)!=null,"Half-present zero worker tuple is refused");
        var fields=new Dictionary<string,object>();fields["RegistryRestoreConfirmed"]=false;
        check(!Boolean(fields,"RegistryRestoreConfirmed"),"Explicit false restoration proof is retained");
        fields["RegistryRestoreConfirmed"]="false";bool refused=false;try{Boolean(fields,"RegistryRestoreConfirmed");}catch(InvalidOperationException){refused=true;}
        check(refused,"String false cannot impersonate the required boolean false");
        fields.Clear();refused=false;try{Boolean(fields,"RegistryRestoreConfirmed");}catch(InvalidOperationException){refused=true;}
        check(refused,"Missing restoration proof cannot default to false");
        fields["Boot"]="1000";refused=false;try{Number(fields,"Boot");}catch(InvalidOperationException){refused=true;}
        check(refused,"String process/boot identities are refused");
        count=tests;return errors.ToArray();
    }
}
