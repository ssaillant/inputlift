// Copyright (c) 2026 InputLift contributors
// SPDX-License-Identifier: MIT
// Fixed protected deployment/state paths. No installation or settings writes.
using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Text.RegularExpressions;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Web.Script.Serialization;

internal static class DailyPaths
{
    internal static string OwnerSid
    {
        get
        {
            using(WindowsIdentity identity=WindowsIdentity.GetCurrent())
            {
                if(identity.User==null) throw new UnauthorizedAccessException("A Windows user identity is required.");
                return identity.User.Value;
            }
        }
    }
    internal static string TaskName { get { return "InputLift-"+OwnerSid; } }
    internal static string StateDirectory { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),"InputLift"); } }
    internal static string CodeDirectory { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),"InputLift"); } }
    internal static bool ValidNonce(string value)
    {
        Guid parsed;
        return value!=null && value.Length==32 && Guid.TryParseExact(value,"N",out parsed);
    }
    internal static string FileFor(string nonce,string suffix)
    {
        if(!ValidNonce(nonce)) throw new ArgumentException("Invalid daily-run nonce.");
        if(suffix==null || !Regex.IsMatch(suffix,@"^\.[a-z0-9-]+\.json$",RegexOptions.CultureInvariant))
            throw new ArgumentException("Invalid daily state suffix.");
        return Path.Combine(StateDirectory,"daily-"+nonce+suffix);
    }
    internal static string CodeFile(string filename)
    {
        if(filename!="InputLift.exe" && filename!="InputLiftGuard.exe" && filename!="InputLiftCapture.exe")
            throw new ArgumentException("Only the fixed installed helper executables are permitted.");
        return Path.Combine(CodeDirectory,filename);
    }
    internal static bool PathEquals(string first,string second)
    {
        if(string.IsNullOrEmpty(first)||string.IsNullOrEmpty(second)) return false;
        return string.Equals(Path.GetFullPath(first).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(second).TrimEnd(Path.DirectorySeparatorChar),StringComparison.OrdinalIgnoreCase);
    }
    internal static void RequireInstalledCodePaths()
    {
        string image;
        using(Process self=Process.GetCurrentProcess()) image=self.MainModule.FileName;
        string filename=Path.GetFileName(image);
        if(!PathEquals(image,CodeFile(filename)))
            throw new InvalidOperationException("Live mode requires the reviewed executable in the fixed administrator-protected Program Files installation.");
        if(!Directory.Exists(CodeDirectory) || !Directory.Exists(StateDirectory))
            throw new InvalidOperationException("The protected code/state directories have not been installed.");
        if((File.GetAttributes(CodeDirectory)&FileAttributes.ReparsePoint)!=0 ||
            (File.GetAttributes(StateDirectory)&FileAttributes.ReparsePoint)!=0)
            throw new InvalidOperationException("Code and state directories must not be reparse points.");
        foreach(string installed in new[]{"InputLift.exe","InputLiftGuard.exe","InputLiftCapture.exe"})
        {
            string path=CodeFile(installed);
            if(!File.Exists(path) || (File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0)
                throw new InvalidOperationException("A fixed installed executable is missing or is a reparse point: "+installed);
        }
        string consentPath=Path.Combine(StateDirectory,"consent.json");
        if(!File.Exists(consentPath) || (File.GetAttributes(consentPath)&FileAttributes.ReparsePoint)!=0)
            throw new InvalidOperationException("Persistent Bluetooth logging has not been approved through setup.");
        RequireProtectedMetadataFile(consentPath);
        var consent=new JavaScriptSerializer().Deserialize<System.Collections.Generic.Dictionary<string,object>>(ReadTextShared(consentPath,65536));
        using(WindowsIdentity identity=WindowsIdentity.GetCurrent())
        {
            if(!ConsentValid(consent,identity.User.Value))
                throw new InvalidOperationException("Persistent logging consent is missing, false, or belongs to a different account.");
        }
        ReadConfiguredKeyboardAddress();
        // Installer owns and verifies administrator-only write ACLs. This check
        // enforces location/reparse invariants; it is not a substitute for ACLs.
    }
    private static bool ConsentValid(System.Collections.Generic.IDictionary<string,object> consent,string currentSid)
    {
        object version,approved,owner;
        return consent!=null && currentSid==OwnerSid && consent.TryGetValue("Version",out version) && version is int && (int)version==1 &&
            consent.TryGetValue("ApprovedPersistentLogging",out approved) && approved is bool && (bool)approved &&
            consent.TryGetValue("OwnerSid",out owner) && owner is string && (string)owner==OwnerSid;
    }
    internal static bool ValidKeyboardAddress(string value)
    {
        return value!=null && Regex.IsMatch(value,@"\A[0-9A-Fa-f]{12}\z",RegexOptions.CultureInvariant) &&
            value!="000000000000" && !string.Equals(value,"FFFFFFFFFFFF",StringComparison.OrdinalIgnoreCase);
    }
    private static bool ConfigurationValid(System.Collections.Generic.IDictionary<string,object> manifest,string currentSid)
    {
        object version,owner,task,address;
        return manifest!=null && manifest.TryGetValue("Version",out version) && version is int && (int)version==1 &&
            manifest.TryGetValue("OwnerSid",out owner) && owner is string && (string)owner==currentSid &&
            manifest.TryGetValue("TaskName",out task) && task is string && (string)task=="InputLift-"+currentSid &&
            manifest.TryGetValue("KeyboardAddress",out address) && address is string && ValidKeyboardAddress((string)address);
    }
    // These values influence elevated capture. Setup writes them with an
    // administrator-owned, protected DACL; a per-user writable config is refused.
    internal static void RequireProtectedMetadataFile(string path)
    {
        if(!PathEquals(Path.GetDirectoryName(path),StateDirectory) ||
            (Path.GetFileName(path)!="install-manifest.json" && Path.GetFileName(path)!="consent.json"))
            throw new ArgumentException("Only installed metadata may be inspected here.");
        string probe=Path.GetFullPath(path);
        while(!string.IsNullOrEmpty(probe))
        {
            if((File.GetAttributes(probe)&FileAttributes.ReparsePoint)!=0)
                throw new InvalidOperationException("Protected metadata ancestry contains a reparse point.");
            DirectoryInfo parent=Directory.GetParent(probe);probe=parent==null?null:parent.FullName;
        }
        FileSecurity acl=File.GetAccessControl(path);
        string owner=((SecurityIdentifier)acl.GetOwner(typeof(SecurityIdentifier))).Value;
        if(owner!="S-1-5-32-544" || !acl.AreAccessRulesProtected)
            throw new InvalidOperationException("Installed metadata must be administrator-owned with protected permissions.");
        bool user=false,admin=false,system=false;
        foreach(FileSystemAccessRule rule in acl.GetAccessRules(true,true,typeof(SecurityIdentifier)))
        {
            string sid=((SecurityIdentifier)rule.IdentityReference).Value;
            if(rule.AccessControlType!=AccessControlType.Allow ||
                (sid!="S-1-5-32-544" && sid!="S-1-5-18" && sid!=OwnerSid))
                throw new InvalidOperationException("Installed metadata contains an unexpected access rule.");
            FileSystemRights rights=rule.FileSystemRights;
            if(sid==OwnerSid)
            {
                FileSystemRights writes=FileSystemRights.Write|FileSystemRights.Delete|FileSystemRights.DeleteSubdirectoriesAndFiles|
                    FileSystemRights.ChangePermissions|FileSystemRights.TakeOwnership;
                if((rights&writes)!=0) throw new InvalidOperationException("Installed metadata is writable by the unelevated owner.");
                user=true;
            }
            else
            {
                if((rights&FileSystemRights.FullControl)!=FileSystemRights.FullControl)
                    throw new InvalidOperationException("Installed metadata lacks administrator/SYSTEM full control.");
                if(sid=="S-1-5-32-544") admin=true;else system=true;
            }
        }
        if(!user || !admin || !system) throw new InvalidOperationException("Installed metadata is missing required permissions.");
    }
    internal static string ReadConfiguredKeyboardAddress()
    {
        string path=Path.Combine(StateDirectory,"install-manifest.json");
        RequireProtectedMetadataFile(path);
        var manifest=new JavaScriptSerializer().Deserialize<System.Collections.Generic.Dictionary<string,object>>(ReadTextShared(path,65536));
        if(!ConfigurationValid(manifest,OwnerSid))
            throw new InvalidOperationException("Installed keyboard configuration is invalid or belongs to a different account.");
        return ((string)manifest["KeyboardAddress"]).ToUpperInvariant();
    }
    internal static void RequireConfiguredKeyboard(string address)
    {
        if(!ValidKeyboardAddress(address) || !string.Equals(address,ReadConfiguredKeyboardAddress(),StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The launch keyboard address differs from the protected installation configuration.");
    }
    internal static string NormalizeOutputMode(string value)
    {
        if(value==null)return "deskflow"; // Existing protected installations predate output selection.
        if(value!="deskflow" && value!="events-only")throw new ArgumentException("Only deskflow or events-only output is supported.");
        return value;
    }
    internal static string ReadConfiguredOutputMode()
    {
        string path=Path.Combine(StateDirectory,"install-manifest.json");
        RequireProtectedMetadataFile(path);
        var manifest=new JavaScriptSerializer().Deserialize<System.Collections.Generic.Dictionary<string,object>>(ReadTextShared(path,65536));
        if(!ConfigurationValid(manifest,OwnerSid))throw new InvalidOperationException("Installed output configuration identity is invalid.");
        object mode;
        if(!manifest.TryGetValue("OutputMode",out mode))return "deskflow";
        if(!(mode is string))throw new InvalidOperationException("Installed output mode must be an explicit string.");
        return NormalizeOutputMode((string)mode);
    }
    internal static void RequireConfiguredOutputMode(string mode)
    {
        if(NormalizeOutputMode(mode)!=ReadConfiguredOutputMode())
            throw new InvalidOperationException("The launch output mode differs from the protected installation configuration.");
    }
    internal static string ReadTextShared(string path,int maximumBytes)
    {
        // ReplaceFile briefly holds an incompatible handle even though readers
        // allow delete sharing. Mutable runtime journals may also briefly lose
        // their name during replacement. Bound deliberate retry delay to200ms
        // and retain the final native error; immutable missing files fail at once.
        for(int attempt=0;;attempt++)
        {
            try { return ReadTextSharedOnce(path,maximumBytes); }
            catch(IOException error)
            {
                bool replaceGap=error.HResult==unchecked((int)0x80070002) && IsStateFile(path);
                if((!TransientReadConflict(error) && !replaceGap) || attempt>=8) throw;
                Thread.Sleep(25);
            }
        }
    }
    internal static bool TryReadTextShared(string path,int maximumBytes,out string text)
    {
        // Watchdog/UI polling may skip a conflicting snapshot, never inventing
        // progress. Their existing lease still expires if no real update arrives.
        try { text=ReadTextSharedOnce(path,maximumBytes);return true; }
        catch(IOException error)
        {
            if(!SnapshotUnavailable(error)) throw;
            text=null;return false;
        }
    }
    private static bool TransientReadConflict(IOException error)
    {
        return error.HResult==unchecked((int)0x80070020) || error.HResult==unchecked((int)0x80070021);
    }
    private static bool SnapshotUnavailable(IOException error)
    {
        return TransientReadConflict(error) || error.HResult==unchecked((int)0x80070002);
    }
    private static string ReadTextSharedOnce(string path,int maximumBytes)
    {
        if(maximumBytes<1 || maximumBytes>1048576) throw new ArgumentOutOfRangeException("maximumBytes");
        using(FileStream file=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete))
        {
            if(file.Length>maximumBytes) throw new InvalidOperationException("Protected state file exceeds its size limit: "+Path.GetFileName(path));
            byte[] bytes=new byte[maximumBytes+1];int count=0;
            while(count<bytes.Length)
            { int read=file.Read(bytes,count,bytes.Length-count);if(read==0)break;count+=read; }
            if(count>maximumBytes) throw new InvalidOperationException("Protected state file grew beyond its size limit: "+Path.GetFileName(path));
            int bom=count>=3 && bytes[0]==0xEF && bytes[1]==0xBB && bytes[2]==0xBF ? 3 : 0;
            return new UTF8Encoding(false,true).GetString(bytes,bom,count-bom);
        }
    }
    private static bool IsStateFile(string path)
    {
        if(!PathEquals(Path.GetDirectoryName(Path.GetFullPath(path)),StateDirectory)) return false;
        return Regex.IsMatch(Path.GetFileName(path),@"^(active|cleanup|daily-[0-9a-f]{32}\.[a-z0-9-]+)\.json$",RegexOptions.CultureInvariant);
    }
    // Explicit owner/DACL on every runtime journal prevents an unelevated
    // object-creator owner from changing its ACL despite a read-only ACE.
    internal static void WriteTextProtected(string path,string text,bool durable)
    {
        if(!IsStateFile(path)) throw new ArgumentException("Only fixed protected helper state files may be written.");
        if((File.GetAttributes(StateDirectory)&FileAttributes.ReparsePoint)!=0)
            throw new InvalidOperationException("State directory must not be a reparse point.");
        if(File.Exists(path))
        {
            if((File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0) throw new InvalidOperationException("State file must not be a reparse point.");
            string owner=((SecurityIdentifier)File.GetAccessControl(path).GetOwner(typeof(SecurityIdentifier))).Value;
            if(owner!="S-1-5-32-544" && owner!="S-1-5-18") throw new InvalidOperationException("Existing state file is not administrator/SYSTEM owned.");
        }
        byte[] bytes=new UTF8Encoding(false).GetBytes(text);
        if(bytes.Length>1048576) throw new InvalidOperationException("Protected helper state exceeds its one-megabyte bound.");
        FileSecurity security=new FileSecurity();
        security.SetSecurityDescriptorSddlForm("O:BAG:BAD:P(A;;FA;;;SY)(A;;FA;;;BA)(A;;FRFX;;;"+OwnerSid+")");
        string temporary=path+"."+Guid.NewGuid().ToString("N")+".tmp";
        using(FileStream file=new FileStream(temporary,FileMode.CreateNew,FileSystemRights.Write,FileShare.None,4096,
            durable?FileOptions.WriteThrough:FileOptions.None,security))
        { file.Write(bytes,0,bytes.Length);file.Flush(durable); }
        if(File.Exists(path)) File.Replace(temporary,path,null);else File.Move(temporary,path);
    }
    internal static string[] RunSelfTests(out int count)
    {
        int cases=0; var errors=new System.Collections.Generic.List<string>();
        Action<bool,string> check=delegate(bool okay,string name){cases++;if(!okay) errors.Add(name);};
        check(TransientReadConflict(new IOException("sharing",unchecked((int)0x80070020))) &&
            TransientReadConflict(new IOException("locked",unchecked((int)0x80070021))),"Only Windows sharing/lock conflicts may be skipped as unavailable snapshots");
        check(!TransientReadConflict(new IOException("access denied",unchecked((int)0x80070005))) &&
            !TransientReadConflict(new IOException("missing",unchecked((int)0x80070002))) &&
            !TransientReadConflict(new IOException("other",unchecked((int)0x80004005))),"Access, missing-file and unrelated IO errors cannot masquerade as sharing conflicts");
        check(SnapshotUnavailable(new IOException("replacement name gap",unchecked((int)0x80070002))) &&
            !SnapshotUnavailable(new IOException("missing parent",unchecked((int)0x80070003))) &&
            !SnapshotUnavailable(new IOException("access denied",unchecked((int)0x80070005))),"Only a missing mutable snapshot name may be skipped; missing directories and access errors remain fatal");
        string nonce="0123456789abcdef0123456789abcdef";
        check(ValidNonce(nonce) && !ValidNonce("../bad") && !ValidNonce(nonce+"0"),"Nonce format is fixed");
        check(PathEquals(FileFor(nonce,".state.json"),Path.Combine(StateDirectory,"daily-"+nonce+".state.json")),"State files remain under fixed protected directory");
        foreach(string suffix in new[]{"../x",".a.json/../x",".x.exe",null,".json"})
        {bool refused=false;try{FileFor(nonce,suffix);}catch(ArgumentException){refused=true;}check(refused,"Invalid suffix refused");}
        bool codeRefused=false;try{CodeFile("..\\evil.exe");}catch(ArgumentException){codeRefused=true;}
        check(codeRefused,"Arbitrary executable paths refused");
        check(PathEquals(CodeFile("InputLiftCapture.exe"),Path.Combine(CodeDirectory,"InputLiftCapture.exe")),"Only fixed installed capture executable is resolved");
        check(IsStateFile(FileFor(nonce,".status.json")) && IsStateFile(Path.Combine(StateDirectory,"active.json")),"Runtime writes allow only fixed metadata files");
        check(!IsStateFile(Path.Combine(StateDirectory,"consent.json")) && !IsStateFile(Path.Combine(StateDirectory,"child","active.json")) &&
            !IsStateFile(Path.Combine(CodeDirectory,"active.json")),"Runtime journal writer cannot change consent, code or nested paths");
        var consent=new System.Collections.Generic.Dictionary<string,object>{{"Version",1},{"ApprovedPersistentLogging",true},{"OwnerSid",OwnerSid}};
        check(ConsentValid(consent,OwnerSid),"Explicit true consent for the installed current account is accepted");
        consent["ApprovedPersistentLogging"]=false;check(!ConsentValid(consent,OwnerSid),"False consent cannot enable logging");
        consent["ApprovedPersistentLogging"]="true";check(!ConsentValid(consent,OwnerSid),"String consent is not a boolean opt-in");
        consent["ApprovedPersistentLogging"]=true;
        string otherSid=OwnerSid=="S-1-5-18"?"S-1-5-19":"S-1-5-18";
        check(!ConsentValid(consent,otherSid) && !ConsentValid(null,OwnerSid),"Different process account and missing marker are refused");
        consent["OwnerSid"]=otherSid;check(!ConsentValid(consent,OwnerSid),"Forged owner field is refused");
        check(ValidKeyboardAddress("001122334455") && ValidKeyboardAddress("aabbccddeeff"),"Synthetic keyboard addresses may be configured in either case");
        foreach(string invalid in new[]{null,"", "000000000000","FFFFFFFFFFFF","ffffffffffff","00112233445","0011223344556","00:11:22:33:44:55","00112233445G","001122334455\n"})
            check(!ValidKeyboardAddress(invalid),"Malformed, zero and broadcast keyboard addresses are refused");
        var config=new System.Collections.Generic.Dictionary<string,object>{{"Version",1},{"OwnerSid",OwnerSid},{"TaskName",TaskName},{"KeyboardAddress","001122334455"}};
        check(ConfigurationValid(config,OwnerSid),"Installed configuration accepts current owner and a valid keyboard");
        check(!ConfigurationValid(config,otherSid),"Installed configuration refuses a different process owner");
        config["TaskName"]="InputLift-other";check(!ConfigurationValid(config,OwnerSid),"Task identity must match installation owner");config["TaskName"]=TaskName;
        config["KeyboardAddress"]="000000000000";check(!ConfigurationValid(config,OwnerSid),"Invalid keyboard cannot enter protected configuration");
        config.Remove("KeyboardAddress");check(!ConfigurationValid(config,OwnerSid),"Missing keyboard configuration fails closed");
        check(NormalizeOutputMode(null)=="deskflow" && NormalizeOutputMode("deskflow")=="deskflow","Legacy output defaults only to existing Deskflow behavior.");
        check(NormalizeOutputMode("events-only")=="events-only","Event-only mode is explicit.");
        foreach(string invalid in new[]{"","Events-Only","mouse5","kanata","events-only --command x","deskflow\n"})
        { bool refused=false;try{NormalizeOutputMode(invalid);}catch(ArgumentException){refused=true;}check(refused,"No arbitrary elevated output mode."); }
        count=cases;return errors.ToArray();
    }
}
