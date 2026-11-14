// Copyright (c) 2026 InputLift contributors
// SPDX-License-Identifier: MIT
// Tray/status frontend. The installed guard owns capture and cleanup. Preview
// and self-test modes never start a guard, task, hook, ETW or registry operation.
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using System.Xml;
using Microsoft.Win32.SafeHandles;
using Microsoft.Win32;

internal static class InputLift
{
    private static string TaskName { get { return DailyPaths.TaskName; } }
    private static readonly string EventPrefix=@"Local\InputLift."+DailyPaths.OwnerSid;
    private static readonly string MutexName=EventPrefix+".ui";
    private static JavaScriptSerializer Serializer() { return new JavaScriptSerializer { MaxJsonLength=1048576 }; }
    private static string Json(object value) { return Serializer().Serialize(value); }
    private static string Describe(Exception error)
    { return error.GetType().Name+": "+error.Message; }
    private static bool IsAdministrator()
    { using(WindowsIdentity id=WindowsIdentity.GetCurrent()) return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator); }
    private static void RequireOwner()
    {
        using(WindowsIdentity id=WindowsIdentity.GetCurrent())
            if(id.User==null || id.User.Value!=DailyPaths.OwnerSid) throw new UnauthorizedAccessException("This helper requires the installed Windows user account.");
    }
    private enum Mode { Invalid,Tray,Show,Stop,SelfTest,Preview }
    private static Mode Parse(string[] args)
    {
        if(args.Length==1)
        {
            if(args[0]=="--tray") return Mode.Tray;
            if(args[0]=="--show") return Mode.Show;
            if(args[0]=="--stop") return Mode.Stop;
            if(args[0]=="--self-test") return Mode.SelfTest;
        }
        if(args.Length==2 && args[0]=="--render-preview" && Path.IsPathRooted(args[1]) &&
            string.Equals(Path.GetExtension(args[1]),".png",StringComparison.OrdinalIgnoreCase)) return Mode.Preview;
        return Mode.Invalid;
    }
    public sealed class Payload { public string Name,Sha256; }
    public sealed class InstallManifest
    {
        public int Version;
        public string OwnerSid,InstanceId,TaskName,TaskMarker,TaskXmlSha256,AppDir,DataDir,KeyboardAddress;
        public string OutputMode;
        public List<Payload> Files;
    }
    public sealed class ActiveRun
    {
        public int Version;
        public string RunNonce;
        public int TrayPid,GuardPid,WorkerPid,GuardianPid;
        public long TrayStartUtcTicks,GuardStartUtcTicks,WorkerStartUtcTicks,GuardianStartUtcTicks,BootUtcTicks;
    }
    public sealed class WorkerStatus
    {
        public string Nonce,State,Error,UpdatedUtc;
        public int WorkerPid;
        public long WorkerStartUtcTicks,ProgressSequence,PhysicalGlobePresses,OutputPresses,OutputPairs;
        public bool KeyboardBound,NeutralConfirmed,RestartRequired;
    }
    public sealed class CleanupProof
    {
        public int Version=1;
        public string RunNonce,Error;
        public int TrayPid,GuardPid,WorkerPid,GuardianPid;
        public long TrayStartUtcTicks,GuardStartUtcTicks,WorkerStartUtcTicks,GuardianStartUtcTicks,BootUtcTicks;
        public bool Clean,RegistryAbsenceConfirmed,OwnedTraceStoppedOrAbsent,ChildQuiescenceConfirmed,ShortcutReleaseConfirmed,RestartRequired=true;
    }
    private static string Hash(string path)
    {
        using(FileStream stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read))
        using(SHA256 algorithm=SHA256.Create())
        {
            if(stream.Length>67108864) throw new InvalidOperationException("Installed helper exceeds its reviewed size limit: "+Path.GetFileName(path));
            return BitConverter.ToString(algorithm.ComputeHash(stream)).Replace("-","");
        }
    }
    private static bool ManifestValid(InstallManifest manifest)
    {
        if(manifest==null || manifest.Version!=1 || manifest.OwnerSid!=DailyPaths.OwnerSid || !DailyPaths.ValidKeyboardAddress(manifest.KeyboardAddress) ||
            !DailyPaths.ValidNonce(manifest.InstanceId) || manifest.TaskName!=TaskName ||
            manifest.TaskMarker!="InputLift managed instance "+manifest.InstanceId+"; owner "+DailyPaths.OwnerSid ||
            !DailyPaths.PathEquals(manifest.AppDir,DailyPaths.CodeDirectory) || !DailyPaths.PathEquals(manifest.DataDir,DailyPaths.StateDirectory) ||
            manifest.TaskXmlSha256==null || !Regex.IsMatch(manifest.TaskXmlSha256,"^[0-9A-Fa-f]{64}$") ||
            manifest.Files==null || manifest.Files.Count!=3 ||
            (manifest.OutputMode!=null && manifest.OutputMode!="deskflow" && manifest.OutputMode!="events-only")) return false;
        HashSet<string> seen=new HashSet<string>(StringComparer.Ordinal);
        foreach(Payload file in manifest.Files)
        {
            if(file==null || (file.Name!="InputLift.exe" && file.Name!="InputLiftGuard.exe" && file.Name!="InputLiftCapture.exe") ||
                !seen.Add(file.Name) || file.Sha256==null || !Regex.IsMatch(file.Sha256,"^[0-9A-Fa-f]{64}$")) return false;
        }
        return true;
    }
    private static InstallManifest ReadManifest()
    {
        string path=Path.Combine(DailyPaths.StateDirectory,"install-manifest.json");
        DailyPaths.RequireProtectedMetadataFile(path);
        InstallManifest manifest=Serializer().Deserialize<InstallManifest>(DailyPaths.ReadTextShared(path,65536));
        if(!ManifestValid(manifest)) throw new InvalidOperationException("Installed manifest identity, paths or payload list is invalid.");
        foreach(Payload file in manifest.Files)
            if(!string.Equals(Hash(DailyPaths.CodeFile(file.Name)),file.Sha256,StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Installed executable hash differs: "+file.Name+". No task or capture was started.");
        return manifest;
    }
    private static string CaptureHash(InstallManifest manifest)
    { foreach(Payload file in manifest.Files) if(file.Name=="InputLiftCapture.exe") return file.Sha256.ToUpperInvariant(); throw new InvalidOperationException("Capture hash missing."); }
    private static string Sid(string value)
    {
        if(string.IsNullOrEmpty(value)) return null;
        if(value.StartsWith("S-",StringComparison.Ordinal)) return new SecurityIdentifier(value).Value;
        return ((SecurityIdentifier)new NTAccount(value).Translate(typeof(SecurityIdentifier))).Value;
    }
    private static string NormalizedTaskHash(string xml)
    {
        XmlReaderSettings settings=new XmlReaderSettings { DtdProcessing=DtdProcessing.Prohibit,XmlResolver=null };
        XmlDocument document=new XmlDocument { PreserveWhitespace=false,XmlResolver=null };
        using(StringReader text=new StringReader(xml)) using(XmlReader reader=XmlReader.Create(text,settings)) document.Load(reader);
        XmlNode enabled=document.SelectSingleNode("/*[local-name()='Task']/*[local-name()='Settings']/*[local-name()='Enabled']");
        if(enabled==null)
        {
            // Scheduler omits default true. Keep the persisted disabled-task
            // position so existing Version 1 ownership hashes remain valid.
            XmlNode taskSettings=document.SelectSingleNode("/*[local-name()='Task']/*[local-name()='Settings']");
            if(taskSettings==null) throw new InvalidOperationException("Owned task Settings node is missing.");
            XmlNode executionLimit=taskSettings.SelectSingleNode("*[local-name()='ExecutionTimeLimit']");
            if(executionLimit==null) throw new InvalidOperationException("Owned task Settings/ExecutionTimeLimit node is missing.");
            enabled=document.CreateElement(taskSettings.Prefix,"Enabled",taskSettings.NamespaceURI);
            taskSettings.InsertBefore(enabled,executionLimit);
        }
        enabled.InnerText="true";
        using(SHA256 algorithm=SHA256.Create()) return BitConverter.ToString(algorithm.ComputeHash(new UTF8Encoding(false).GetBytes(document.OuterXml))).Replace("-","");
    }
    private static void ReleaseCom(object value)
    { if(value!=null && Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value); }
    private static void WithOwnedTask(InstallManifest manifest,Action<object> operation)
    {
        object service=null,folder=null,task=null,definition=null,principal=null,actions=null,action=null,triggers=null,trigger=null,settings=null,registration=null;
        try
        {
            service=Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service",true));
            ((dynamic)service).Connect(); folder=((dynamic)service).GetFolder("\\"); task=((dynamic)folder).GetTask(TaskName);
            definition=((dynamic)task).Definition; principal=((dynamic)definition).Principal;
            actions=((dynamic)definition).Actions; triggers=((dynamic)definition).Triggers;
            settings=((dynamic)definition).Settings; registration=((dynamic)definition).RegistrationInfo;
            if((string)((dynamic)registration).Description!=manifest.TaskMarker || Sid((string)((dynamic)principal).UserId)!=DailyPaths.OwnerSid ||
                (int)((dynamic)principal).RunLevel!=1 || (int)((dynamic)principal).LogonType!=3 ||
                (int)((dynamic)actions).Count!=1 || (int)((dynamic)triggers).Count!=1)
                throw new InvalidOperationException("Scheduled task ownership, principal or action count differs.");
            action=((dynamic)actions).Item(1); trigger=((dynamic)triggers).Item(1);
            if((int)((dynamic)action).Type!=0 || !DailyPaths.PathEquals((string)((dynamic)action).Path,DailyPaths.CodeFile("InputLift.exe")) ||
                (string)((dynamic)action).Arguments!="--tray" || !DailyPaths.PathEquals((string)((dynamic)action).WorkingDirectory,DailyPaths.CodeDirectory) ||
                (int)((dynamic)trigger).Type!=9 || Sid((string)((dynamic)trigger).UserId)!=DailyPaths.OwnerSid ||
                (string)((dynamic)settings).ExecutionTimeLimit!="PT0S" || (int)((dynamic)settings).MultipleInstances!=2 ||
                (bool)((dynamic)settings).DisallowStartIfOnBatteries || (bool)((dynamic)settings).StopIfGoingOnBatteries ||
                !string.Equals(NormalizedTaskHash((string)((dynamic)task).Xml),manifest.TaskXmlSha256,StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Scheduled task definition differs from the reviewed fixed helper.");
            operation(task);
        }
        finally
        { foreach(object value in new[]{registration,settings,trigger,triggers,action,actions,principal,definition,task,folder,service}) ReleaseCom(value); }
    }

    [StructLayout(LayoutKind.Sequential)] private struct SecurityAttributes { internal int Length;internal IntPtr Descriptor;internal int Inherit; }
    [DllImport("advapi32.dll",CharSet=CharSet.Unicode,ExactSpelling=true,SetLastError=true)]
    [return:MarshalAs(UnmanagedType.Bool)] private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(string text,uint revision,out IntPtr descriptor,out uint size);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,ExactSpelling=true,SetLastError=true)]
    private static extern IntPtr CreateEventW(ref SecurityAttributes attributes,[MarshalAs(UnmanagedType.Bool)]bool manual,[MarshalAs(UnmanagedType.Bool)]bool initial,string name);
    [DllImport("kernel32.dll",ExactSpelling=true)] private static extern IntPtr LocalFree(IntPtr memory);
    private static EventWaitHandle CreateOwnedEvent(string suffix,bool manual)
    {
        // Explicit owner-only medium-integrity event allows the installed user's
        // Start Menu process to show/stop their elevated tray. No command data.
        string sddl="D:P(A;;GA;;;SY)(A;;GA;;;BA)(A;;GA;;;"+DailyPaths.OwnerSid+")S:(ML;;NW;;;ME)";
        IntPtr descriptor;uint size;
        if(!ConvertStringSecurityDescriptorToSecurityDescriptorW(sddl,1,out descriptor,out size)) throw new Win32Exception(Marshal.GetLastWin32Error(),"Create helper event security descriptor failed.");
        try
        {
            SecurityAttributes attributes=new SecurityAttributes { Length=Marshal.SizeOf(typeof(SecurityAttributes)),Descriptor=descriptor,Inherit=0 };
            IntPtr native=CreateEventW(ref attributes,manual,false,EventPrefix+suffix);
            if(native==IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(),"Create owned helper event failed.");
            EventWaitHandle result=new EventWaitHandle(false,manual?EventResetMode.ManualReset:EventResetMode.AutoReset);
            result.SafeWaitHandle=new SafeWaitHandle(native,true); return result;
        }
        finally { LocalFree(descriptor); }
    }
    private static bool SignalExisting(string suffix)
    {
        try { using(EventWaitHandle signal=EventWaitHandle.OpenExisting(EventPrefix+suffix,System.Security.AccessControl.EventWaitHandleRights.Modify)) { signal.Set();return true; } }
        catch(WaitHandleCannotBeOpenedException) { return false; }
    }
    private static bool ActiveValid(ActiveRun active,int trayPid,long trayTicks,int guardPid,long guardTicks)
    {
        return active!=null && active.Version==1 && DailyPaths.ValidNonce(active.RunNonce) && active.TrayPid==trayPid &&
            active.TrayStartUtcTicks==trayTicks && active.GuardPid==guardPid && active.GuardStartUtcTicks==guardTicks &&
            active.BootUtcTicks>0 && active.WorkerPid>=0 && active.WorkerStartUtcTicks>=0 &&
            ((active.WorkerPid==0)==(active.WorkerStartUtcTicks==0)) && active.GuardianPid>=0 && active.GuardianStartUtcTicks>=0 &&
            ((active.GuardianPid==0)==(active.GuardianStartUtcTicks==0));
    }
    private static bool Bool(IDictionary<string,object> values,string name)
    { object value;return values!=null && values.TryGetValue(name,out value) && value is bool && (bool)value; }
    private static long Number(IDictionary<string,object> values,string name)
    { object value;if(values==null || !values.TryGetValue(name,out value) || !(value is int || value is long)) return long.MinValue;return Convert.ToInt64(value,CultureInfo.InvariantCulture); }
    private static bool ResultIdentityValid(ActiveRun active,IDictionary<string,object> result)
    {
        object nonce;
        return active!=null && result!=null && result.TryGetValue("nonce",out nonce) && nonce is string && (string)nonce==active.RunNonce &&
            Number(result,"trayPid")==active.TrayPid && Number(result,"trayStartUtcTicks")==active.TrayStartUtcTicks &&
            Number(result,"guardPid")==active.GuardPid && Number(result,"guardStartUtcTicks")==active.GuardStartUtcTicks &&
            Number(result,"workerPid")==active.WorkerPid && Number(result,"workerStartUtcTicks")==active.WorkerStartUtcTicks &&
            Number(result,"bootUtcTicks")==active.BootUtcTicks;
    }
    private static CleanupProof MakeCleanup(ActiveRun active,IDictionary<string,object> result,string error)
    {
        CleanupProof proof=new CleanupProof { RunNonce=active.RunNonce,TrayPid=active.TrayPid,TrayStartUtcTicks=active.TrayStartUtcTicks,
            GuardPid=active.GuardPid,GuardStartUtcTicks=active.GuardStartUtcTicks,WorkerPid=active.WorkerPid,WorkerStartUtcTicks=active.WorkerStartUtcTicks,
            GuardianPid=active.GuardianPid,GuardianStartUtcTicks=active.GuardianStartUtcTicks,BootUtcTicks=active.BootUtcTicks,Error=error };
        if(ResultIdentityValid(active,result))
        {
            proof.RegistryAbsenceConfirmed=Bool(result,"registryAbsenceConfirmed"); proof.OwnedTraceStoppedOrAbsent=Bool(result,"ownedTraceStoppedOrAbsent");
            proof.ChildQuiescenceConfirmed=Bool(result,"childQuiescenceConfirmed"); proof.ShortcutReleaseConfirmed=Bool(result,"shortcutReleaseConfirmed");
            proof.Clean=proof.RegistryAbsenceConfirmed && proof.OwnedTraceStoppedOrAbsent && proof.ChildQuiescenceConfirmed &&
                proof.ShortcutReleaseConfirmed && Bool(result,"guardianConfirmedExited");
        }
        return proof;
    }
    private static void WriteProtectedJson(string filename,object value)
    {
        DailyPaths.WriteTextProtected(Path.Combine(DailyPaths.StateDirectory,filename),Json(value),true);
    }
    private sealed class ViewState
    {
        internal string Title,Detail;
        internal Color Color;
        internal ViewState(string title,string detail,Color color) { Title=title;Detail=detail;Color=color; }
    }
    private static ViewState MapState(string state,string error)
    {
        Color blue=Color.FromArgb(42,92,190),green=Color.FromArgb(28,125,83),amber=Color.FromArgb(157,98,20),red=Color.FromArgb(174,48,48);
        switch(state)
        {
            case "Starting":return new ViewState("Starting","Preparing Globe support. No console window needs to stay open.",blue);
            case "WaitingForKeyboard":return new ViewState("Switch the configured keyboard off","Switch the configured keyboard OFF. Leave it OFF until the helper tells you to turn it on. Keep it paired to this Windows host.",amber);
            case "WaitingForDisconnect":return new ViewState("Keep the keyboard off","Windows is releasing the Bluetooth connection. Leave the configured keyboard OFF and wait for the turn-on instruction.",amber);
            case "TurnKeyboardOn":return new ViewState("Turn the configured keyboard on","Switch the keyboard ON and tap Shift once. Then wait for the helper to identify it.",amber);
            case "WaitingForIdentification":return new ViewState("Wake the keyboard with Shift","Tap Shift once on the configured keyboard and wait for Keyboard identified. Keep the keyboard paired to this Windows host.",amber);
            case "AwaitNeutral":return new ViewState("Release Globe","Release Globe and tap Shift once so the helper can confirm a clean starting state.",amber);
            case "Ready":return new ViewState("Fn is ready on the client","Fn presses and releases are forwarded as Mouse 5 while the pointer is on the remote client.",green);
            case "PausedOnHost":return new ViewState("Ready — pointer is on the host","Move the pointer onto the Deskflow client to forward Fn as Mouse 5. Fn is ignored on this Windows host.",blue);
            case "AwaitConsumer":return new ViewState("Waiting for the local event consumer","Start the reviewed event consumer. The producer sends only Fn state through a protected local pipe; it does not press keys.",amber);
            case "EventsReady":return new ViewState("Fn events are ready","The local consumer receives Fn press, release and reset state. Its own status must confirm any mapped action.",green);
            case "Stopping":return new ViewState("Stopping","Closing Bluetooth capture. Please wait for producer cleanup confirmation and check the consumer's mapped action separately.",amber);
            case "Stopped":return new ViewState("Stopped — restart required","Cleanup is verified. Use Windows Start → Power → Restart to fully clear Bluetooth logging.",amber);
            case "Idle":return new ViewState("Stopped","Click Start when you want Globe support. Closing this window leaves the tray controls available.",blue);
            case "Error":return new ViewState("Needs attention",string.IsNullOrEmpty(error)?"The helper could not continue. No automatic retry was attempted.":error,red);
            default:return new ViewState("Needs attention","Unknown helper state: "+state,red);
        }
    }
    private sealed class StatusForm : Form
    {
        private readonly Label statusTitle,statusDetail,counters,previewLabel;
        private readonly Panel statusCard;
        private readonly Button startButton,stopButton,moreButton;
        private readonly bool previewMode;
        private Color stateColor;
        internal StatusForm(Action start,Action stop,Action more,bool preview,string outputMode="deskflow")
        {
            previewMode=preview;
            Text="InputLift";ClientSize=new Size(720,450);MinimumSize=Size;MaximumSize=Size;FormBorderStyle=FormBorderStyle.FixedSingle;
            MaximizeBox=false;StartPosition=FormStartPosition.CenterScreen;BackColor=Color.FromArgb(244,246,250);Font=new Font("Segoe UI",10);
            Label title=new Label { Text="InputLift",Font=new Font("Segoe UI",21,FontStyle.Bold),Location=new Point(24,18),Size=new Size(650,38) };
            Label sub=new Label { Text=outputMode=="events-only"?"Bluetooth Fn → protected local event pipe":"Bluetooth Fn → Mouse 5 → Deskflow",ForeColor=Color.FromArgb(85,96,114),Location=new Point(26,60),Size=new Size(650,24) };
            previewLabel=new Label { Text=preview?"Preview — capture off":"Runs in the Windows notification area",ForeColor=Color.FromArgb(85,96,114),Location=new Point(26,88),Size=new Size(650,23) };
            statusCard=new Panel { BackColor=Color.White,Location=new Point(24,121),Size=new Size(672,121) };
            statusCard.Paint+=delegate(object sender,PaintEventArgs e) { using(SolidBrush brush=new SolidBrush(stateColor)) e.Graphics.FillEllipse(brush,17,20,12,12); };
            statusTitle=new Label { Font=new Font("Segoe UI",14,FontStyle.Bold),Location=new Point(42,12),Size=new Size(606,30) };
            statusDetail=new Label { Location=new Point(42,47),Size=new Size(606,51),ForeColor=Color.FromArgb(65,76,94) };
            counters=new Label { Location=new Point(26,251),Size=new Size(660,23),Font=new Font("Segoe UI",9),ForeColor=Color.FromArgb(85,96,114) };
            startButton=new Button { Text="Start",Location=new Point(24,281),Size=new Size(116,36),FlatStyle=FlatStyle.System };
            stopButton=new Button { Text="Stop",Location=new Point(152,281),Size=new Size(116,36),FlatStyle=FlatStyle.System };
            moreButton=new Button { Text="More…",Location=new Point(280,281),Size=new Size(116,36),FlatStyle=FlatStyle.System };
            startButton.Click+=delegate{if(start!=null) start();};stopButton.Click+=delegate{if(stop!=null) stop();};moreButton.Click+=delegate{if(more!=null) more();};
            Label help=new Label { Text=outputMode=="events-only"?"Event-only producer: no native input is sent here. Stop the local consumer\r\nand confirm its mapped action is released before closing it.":"Configure Mouse 5 in your remote application. Release Fn and finish any active\r\naction before returning the pointer to the Windows host.",Location=new Point(26,334),Size=new Size(665,47),ForeColor=Color.FromArgb(65,76,94),Font=new Font("Segoe UI",9) };
            Label privacy=new Label { Text="Bluetooth sensitive logging is enabled while this helper runs. After stopping,\r\nrestart Windows to fully clear the driver's logging state.",Location=new Point(26,389),Size=new Size(665,43),ForeColor=Color.FromArgb(105,87,55),Font=new Font("Segoe UI",9) };
            statusCard.Controls.AddRange(new Control[]{statusTitle,statusDetail});Controls.AddRange(new Control[]{title,sub,previewLabel,statusCard,counters,startButton,stopButton,moreButton,help,privacy});
            SetState(MapState("Starting",null),0,0,false,false);
            FormClosing+=delegate(object sender,FormClosingEventArgs e) { if(e.CloseReason==CloseReason.UserClosing) { e.Cancel=true;Hide(); } };
        }
        protected override bool ShowWithoutActivation { get { return previewMode; } }
        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams parameters=base.CreateParams;
                if(previewMode) parameters.ExStyle|=0x08000000|0x00000080; // NOACTIVATE and TOOLWINDOW.
                return parameters;
            }
        }
        internal void SetState(ViewState state,long physical,long output,bool running,bool stopping)
        {
            statusTitle.Text=state.Title;statusDetail.Text=state.Detail;stateColor=state.Color;statusCard.Invalidate();
            counters.Text="Fn presses detected: "+physical+"     Mapped presses completed: "+output;
            startButton.Enabled=!running && !stopping;stopButton.Enabled=running && !stopping;
        }
        internal void Present() { Show();WindowState=FormWindowState.Normal;Activate(); }
    }
    private sealed class TrayContext : ApplicationContext
    {
        private readonly int trayPid;
        private readonly long trayTicks;
        private readonly StatusForm form;
        private readonly NotifyIcon icon;
        private readonly ContextMenuStrip menu;
        private readonly System.Windows.Forms.Timer timer;
        private readonly EventWaitHandle showEvent,stopEvent,exitedEvent;
        private readonly object outputGate=new object();
        private readonly List<string> guardErrors=new List<string>();
        private readonly HashSet<string> announcedWaiting=new HashSet<string>(StringComparer.Ordinal);
        private Process guard;
        private long guardTicks;
        private volatile ActiveRun active;
        private string state="Starting",lastError;
        private long physical,output,lastSequence;
        private bool startPending=true,stopRequested,exitRequested,doneSignalled,finalized,disposed;
        private readonly Stopwatch stopClock=new Stopwatch();
        internal int ExitCode;
        internal TrayContext()
        {
            using(Process self=Process.GetCurrentProcess()) { trayPid=self.Id;trayTicks=self.StartTime.ToUniversalTime().Ticks; }
            showEvent=CreateOwnedEvent(".show",false);stopEvent=CreateOwnedEvent(".stop",false);exitedEvent=CreateOwnedEvent(".exited",true);exitedEvent.Reset();
            menu=new ContextMenuStrip();
            menu.Items.Add("Show status",null,delegate{form.Present();});
            menu.Items.Add("Start",null,delegate{StartCapture();});menu.Items.Add("Stop",null,delegate{RequestStop(false,false);});
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Stop and disable automatic startup",null,delegate{RequestStop(false,true);});
            menu.Items.Add("Stop and exit",null,delegate{RequestStop(true,false);});
            form=new StatusForm(StartCapture,delegate{RequestStop(false,false);},delegate{menu.Show(form,new Point(280,318));},false,DailyPaths.ReadConfiguredOutputMode());
            icon=new NotifyIcon { Icon=SystemIcons.Application,Text="InputLift — starting",Visible=true,ContextMenuStrip=menu };
            icon.DoubleClick+=delegate{form.Present();};
            timer=new System.Windows.Forms.Timer { Interval=500 };timer.Tick+=Tick;timer.Start();
            SystemEvents.SessionEnding+=OnSessionEnding;
        }
        private void OnSessionEnding(object sender,SessionEndingEventArgs e)
        {
            // This system callback may run off the UI thread. Close only the
            // matching backend lease; never wait for UI or cancel Windows exit.
            try { SignalDone(); } catch { /* Independent guardian also watches UI lifetime. */ }
        }
        private void Display(string next,string error)
        {
            state=next;if(error!=null) lastError=error;
            ViewState view=MapState(next,error);bool running=guard!=null && !finalized;
            form.SetState(view,physical,output,running,stopRequested && !finalized);
            string tip="Bluetooth Fn: "+view.Title;icon.Text=tip.Length>63?tip.Substring(0,63):tip;
            bool waiting=next=="WaitingForKeyboard" || next=="WaitingForDisconnect" || next=="TurnKeyboardOn" || next=="WaitingForIdentification" || next=="AwaitNeutral";
            icon.Icon=next=="Error"?SystemIcons.Error:waiting || next=="Stopping" || next=="Stopped"?SystemIcons.Warning:SystemIcons.Information;
            if(waiting && announcedWaiting.Add(next))
            {
                icon.BalloonTipTitle=view.Title;icon.BalloonTipText=view.Detail;icon.BalloonTipIcon=ToolTipIcon.Info;icon.ShowBalloonTip(7000);
            }
        }
        private void StartCapture()
        {
            if(guard!=null && !finalized) return;
            try
            {
                DailyPaths.RequireInstalledCodePaths();InstallManifest manifest=ReadManifest();
                stopRequested=exitRequested=doneSignalled=finalized=false;ExitCode=0;lastError=null;active=null;lastSequence=physical=output=0;stopClock.Reset();
                announcedWaiting.Clear();
                lock(outputGate) guardErrors.Clear();
                if(guard!=null) { guard.Dispose();guard=null; }
                string arguments="--run-daily --keyboard-address "+manifest.KeyboardAddress.ToUpperInvariant()+" --expected-probe-sha256 "+CaptureHash(manifest)+
                    " --tray-pid "+trayPid.ToString(CultureInfo.InvariantCulture)+" --tray-start "+trayTicks.ToString(CultureInfo.InvariantCulture)+
                    " --output-mode "+DailyPaths.NormalizeOutputMode(manifest.OutputMode);
                Process launched=new Process { StartInfo=new ProcessStartInfo(DailyPaths.CodeFile("InputLiftGuard.exe"),arguments) {
                    WorkingDirectory=DailyPaths.CodeDirectory,UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true } };
                launched.OutputDataReceived+=delegate(object sender,DataReceivedEventArgs e) {
                    // Output is diagnostic only; UI state comes from protected files.
                    if(e.Data!=null && e.Data.Length>262144) lock(outputGate) { if(guardErrors.Count<16) guardErrors.Add("Guard output exceeded its size allowance."); }
                };
                launched.ErrorDataReceived+=delegate(object sender,DataReceivedEventArgs e) {
                    if(e.Data!=null) lock(outputGate) { if(guardErrors.Count<16) guardErrors.Add(e.Data.Length<=4096?e.Data:e.Data.Substring(0,4096)); }
                };
                if(!launched.Start()) { launched.Dispose();throw new InvalidOperationException("The protected guard did not start."); }
                guard=launched;guardTicks=guard.StartTime.ToUniversalTime().Ticks;
                guard.BeginOutputReadLine();guard.BeginErrorReadLine();Display("Starting",null);
            }
            catch(Exception error)
            {
                Display("Error",Describe(error));
                if(guard!=null && !finalized) RequestStop(true,false);
            }
        }
        private ActiveRun ReadActive()
        {
            string path=Path.Combine(DailyPaths.StateDirectory,"active.json");
            if(!File.Exists(path)) return null;
            string text;
            if(!DailyPaths.TryReadTextShared(path,65536,out text)) return null;
            ActiveRun candidate=Serializer().Deserialize<ActiveRun>(text);
            if(!ActiveValid(candidate,trayPid,trayTicks,guard.Id,guardTicks)) return null;
            if(active!=null && candidate.RunNonce!=active.RunNonce) throw new InvalidOperationException("Active run nonce changed while its guard was running.");
            return candidate;
        }
        private void SignalDone()
        {
            if(active==null || doneSignalled) return;
            using(EventWaitHandle done=EventWaitHandle.OpenExisting(@"Local\GlobeRelay.FullHid."+active.RunNonce+".done")) done.Set();
            doneSignalled=true;
        }
        private void RequestStop(bool exit,bool disable)
        {
            try
            {
                if(disable) { InstallManifest manifest=ReadManifest();WithOwnedTask(manifest,delegate(object task){((dynamic)task).Enabled=false;}); }
                if(exit) exitRequested=true;
                if(guard==null || finalized) { if(exitRequested) ExitThread();return; }
                stopRequested=true;if(!stopClock.IsRunning) stopClock.Start();Display("Stopping",null);SignalDone();
            }
            catch(Exception error) { Display("Error",Describe(error));if(exit) { ExitCode=1;ExitThread(); } }
        }
        private void FinishGuard()
        {
            if(finalized) return;finalized=true;
            try
            {
                if(active==null) active=ReadActive();
                if(active==null) throw new InvalidOperationException("Guard exited without a matching active-run identity; cleanup proof is unavailable.");
                string resultPath=DailyPaths.FileFor(active.RunNonce,".result.json");
                if(!File.Exists(resultPath)) throw new InvalidOperationException("Guard exited without its result file; cleanup is unconfirmed.");
                var result=Serializer().Deserialize<Dictionary<string,object>>(DailyPaths.ReadTextShared(resultPath,1048576));
                if(!ResultIdentityValid(active,result)) throw new InvalidOperationException("Guard result identity differs from the active run; cleanup is unconfirmed.");
                CleanupProof proof=MakeCleanup(active,result,lastError);
                WriteProtectedJson("cleanup.json",proof);
                if(!proof.Clean) throw new InvalidOperationException("Guard did not confirm registry, trace, worker and Mouse5 cleanup. Restart is required; retain the installed files.");
                object statusValue;string guardState=result.TryGetValue("status",out statusValue)?Convert.ToString(statusValue,CultureInfo.InvariantCulture):"error";
                if(guard.ExitCode!=0 || guardState!="ok")
                {
                    string detail=lastError;
                    object messages;
                    if(string.IsNullOrEmpty(detail) && result.TryGetValue("errors",out messages)) detail=Json(messages);
                    if(string.IsNullOrEmpty(detail)) lock(outputGate) detail=string.Join("; ",guardErrors.ToArray());
                    Display("Error",string.IsNullOrEmpty(detail)?"The helper stopped with an error. Cleanup is verified; restart the host before troubleshooting.":detail+" Cleanup is verified; restart the host.");ExitCode=1;
                }
                else Display("Stopped",null);
            }
            catch(Exception error)
            {
                lastError=Describe(error);Display("Error",lastError);ExitCode=1;
                if(active!=null)
                { try { WriteProtectedJson("cleanup.json",MakeCleanup(active,null,lastError)); } catch { /* No false clean proof is written. */ } }
            }
            if(exitRequested) ExitThread();
        }
        private void Tick(object sender,EventArgs e)
        {
            try
            {
                if(showEvent.WaitOne(0)) form.Present();
                if(stopEvent.WaitOne(0)) RequestStop(true,false);
                if(startPending) { startPending=false;StartCapture(); }
                if(guard==null || finalized) return;
                ActiveRun candidate=ReadActive();if(candidate!=null) active=candidate;
                if(stopRequested) { SignalDone();if(stopClock.ElapsedMilliseconds>30000 && exitRequested) { ExitCode=1;Display("Error","Cleanup is not confirmed after 30 seconds. The guardian will observe this UI exit; verify cleanup before restarting.");ExitThread();return; } }
                if(guard.HasExited) { FinishGuard();return; }
                if(!stopRequested && active!=null && active.WorkerPid>0)
                {
                    string path=DailyPaths.FileFor(active.RunNonce,".status.json");
                    if(File.Exists(path))
                    {
                        string text;
                        if(!DailyPaths.TryReadTextShared(path,65536,out text)) return;
                        WorkerStatus status=Serializer().Deserialize<WorkerStatus>(text);
                        if(status==null || status.Nonce!=active.RunNonce || status.WorkerPid!=active.WorkerPid || status.WorkerStartUtcTicks!=active.WorkerStartUtcTicks ||
                            status.ProgressSequence<=0 || status.ProgressSequence<lastSequence) throw new InvalidOperationException("Worker status identity or sequence is invalid.");
                        lastSequence=status.ProgressSequence;physical=status.PhysicalGlobePresses;output=status.OutputPairs;
                        Display(status.State,status.Error);
                    }
                }
            }
            catch(Exception error)
            {
                lastError=Describe(error);Display("Error",lastError);
                if(guard!=null && !finalized) RequestStop(false,false);
            }
        }
        protected override void ExitThreadCore()
        {
            // No broad process termination. If proof is missing, the independent
            // guardian sees the tray process end and attempts its own cleanup.
            timer.Stop();icon.Visible=false;exitedEvent.Set();base.ExitThreadCore();
        }
        protected override void Dispose(bool disposing)
        {
            if(disposing && !disposed)
            {
                disposed=true;SystemEvents.SessionEnding-=OnSessionEnding;timer.Stop();timer.Dispose();icon.Visible=false;icon.Dispose();menu.Dispose();form.Dispose();
                showEvent.Dispose();stopEvent.Dispose();exitedEvent.Dispose();if(guard!=null) guard.Dispose();
            }
            base.Dispose(disposing);
        }
    }
    private static int ShowInstalled()
    {
        DailyPaths.RequireInstalledCodePaths();ReadManifest();
        if(SignalExisting(".show")) return 0;
        InstallManifest manifest=ReadManifest();
        WithOwnedTask(manifest,delegate(object task) {
            if(!(bool)((dynamic)task).Enabled) throw new InvalidOperationException("Automatic startup is disabled. Open the installed helper as administrator to enable its task before starting it.");
            object running=((dynamic)task).Run(null);ReleaseCom(running);
        });
        Stopwatch wait=Stopwatch.StartNew();
        while(wait.ElapsedMilliseconds<10000) { if(SignalExisting(".show")) return 0;Thread.Sleep(100); }
        throw new TimeoutException("The owned helper task did not publish its Show event within ten seconds. No automatic retry was attempted.");
    }
    private static int StopInstalled()
    {
        DailyPaths.RequireInstalledCodePaths();
        if(!SignalExisting(".stop")) return 0;
        using(EventWaitHandle exited=EventWaitHandle.OpenExisting(EventPrefix+".exited",System.Security.AccessControl.EventWaitHandleRights.Synchronize))
            if(!exited.WaitOne(30000)) throw new TimeoutException("Helper did not complete its graceful stop within 30 seconds. No process was killed.");
        string activePath=Path.Combine(DailyPaths.StateDirectory,"active.json");
        if(File.Exists(activePath))
        {
            ActiveRun active=Serializer().Deserialize<ActiveRun>(DailyPaths.ReadTextShared(activePath,65536));
            string proofPath=Path.Combine(DailyPaths.StateDirectory,"cleanup.json");
            if(!File.Exists(proofPath)) throw new InvalidOperationException("The tray exited without cleanup proof.");
            CleanupProof proof=Serializer().Deserialize<CleanupProof>(DailyPaths.ReadTextShared(proofPath,65536));
            if(active==null || proof==null || proof.Version!=1 || proof.RunNonce!=active.RunNonce || !proof.Clean)
                throw new InvalidOperationException("The tray exited without matching clean recovery proof. Verify cleanup before restarting.");
        }
        return 0;
    }
    private static int SelfTest()
    {
        int cases=0;List<string> errors=new List<string>();Action<bool,string> check=delegate(bool good,string name){cases++;if(!good) errors.Add(name);};
        foreach(string flag in new[]{"--tray","--show","--stop","--self-test"}) check(Parse(new[]{flag})!=Mode.Invalid,"Fixed CLI mode accepted");
        foreach(string[] bad in new[]{new string[0],new[]{"--tray","extra"},new[]{"--start","cmd"},new[]{"--render-preview","relative.png"},new[]{"--render-preview",@"C:\preview.exe"}})
            check(Parse(bad)==Mode.Invalid,"Unknown or arbitrary live invocation refused");
        check(Parse(new[]{"--render-preview",@"C:\preview.png"})==Mode.Preview,"Absolute PNG-only preview accepted");
        foreach(string state in new[]{"Starting","WaitingForKeyboard","WaitingForDisconnect","TurnKeyboardOn","WaitingForIdentification","AwaitNeutral","Ready","PausedOnHost","AwaitConsumer","EventsReady","Stopping","Stopped","Idle","Error"})
        { ViewState view=MapState(state,null);check(!string.IsNullOrEmpty(view.Title)&&!string.IsNullOrEmpty(view.Detail),"Known state has plain user-facing status"); }
        check(MapState("Error","Exact failure").Detail=="Exact failure","Exact errors are retained");
        check(MapState("WaitingForKeyboard",null).Detail.Contains("OFF") && MapState("WaitingForDisconnect",null).Detail.Contains("OFF"),"Initial reconnect states keep physical power OFF");
        check(MapState("TurnKeyboardOn",null).Detail.Contains("ON") && MapState("TurnKeyboardOn",null).Detail.Contains("Shift") &&
            MapState("WaitingForIdentification",null).Detail.Contains("Shift"),"Turn-on and identification phases explicitly name Shift wake step");
        string taskXml="<?xml version=\"1.0\" encoding=\"UTF-16\"?><Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\"><Settings><StopIfGoingOnBatteries>false</StopIfGoingOnBatteries><Enabled>false</Enabled><ExecutionTimeLimit>PT0S</ExecutionTimeLimit><MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy></Settings></Task>";
        string taskHash=NormalizedTaskHash(taskXml);
        check(taskHash==NormalizedTaskHash(taskXml.Replace("<Enabled>false</Enabled>","<Enabled>true</Enabled>")),"Enabled toggle preserves task ownership hash");
        check(taskHash==NormalizedTaskHash(taskXml.Replace("<Enabled>false</Enabled>","")),"Omitted default Enabled preserves legacy task ownership hash");
        check(taskHash!=NormalizedTaskHash(taskXml.Replace("PT0S","PT1H")),"Other task changes remain detectable");
        bool missingLimitRejected=false;
        try { NormalizedTaskHash(taskXml.Replace("<Enabled>false</Enabled>","").Replace("<ExecutionTimeLimit>PT0S</ExecutionTimeLimit>","")); }
        catch(InvalidOperationException) { missingLimitRejected=true; }
        check(missingLimitRejected,"Missing normalization anchor is refused");
        string nonce="0123456789abcdef0123456789abcdef";
        ActiveRun active=new ActiveRun { Version=1,RunNonce=nonce,TrayPid=1,TrayStartUtcTicks=2,GuardPid=3,GuardStartUtcTicks=4,WorkerPid=5,WorkerStartUtcTicks=6,GuardianPid=7,GuardianStartUtcTicks=8,BootUtcTicks=9 };
        check(ActiveValid(active,1,2,3,4) && !ActiveValid(active,1,99,3,4) && !ActiveValid(active,1,2,30,4),"Active journal binds tray and guard PID/start");
        var result=new Dictionary<string,object>{{"nonce",nonce},{"trayPid",1},{"trayStartUtcTicks",2},{"guardPid",3},{"guardStartUtcTicks",4},{"workerPid",5},{"workerStartUtcTicks",6},{"bootUtcTicks",9},
            {"registryAbsenceConfirmed",true},{"ownedTraceStoppedOrAbsent",true},{"childQuiescenceConfirmed",true},{"shortcutReleaseConfirmed",true},{"guardianConfirmedExited",true}};
        check(MakeCleanup(active,result,null).Clean,"All matching cleanup proofs permit Clean");
        foreach(string field in new[]{"registryAbsenceConfirmed","ownedTraceStoppedOrAbsent","childQuiescenceConfirmed","shortcutReleaseConfirmed","guardianConfirmedExited"})
        { result[field]=false;check(!MakeCleanup(active,result,null).Clean,"Missing individual cleanup proof forbids Clean");result[field]=true; }
        result["workerPid"]=99;check(!MakeCleanup(active,result,null).Clean,"Wrong worker identity cannot authorize Clean");result["workerPid"]=5;
        result["registryAbsenceConfirmed"]="true";check(!MakeCleanup(active,result,null).Clean,"Truthy strings cannot authorize cleanup");
        check(!MakeCleanup(active,null,"missing").Clean,"Missing result retains explicit unclean proof");
        Console.WriteLine(Json(new { mode="self-test",status=errors.Count==0?"ok":"error",tests=cases,nativeInput=false,captureStarted=false,taskStarted=false,settingsChanged=false,errors=errors }));
        return errors.Count==0?0:1;
    }
    [STAThread]
    private static int Main(string[] args)
    {
        Mode mode=Parse(args);
        if(mode==Mode.SelfTest) return SelfTest();
        if(mode==Mode.Preview)
        {
            Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
            using(StatusForm preview=new StatusForm(null,null,null,true))
            {
                preview.SetState(MapState("PausedOnHost",null),12,8,false,false);
                preview.StartPosition=FormStartPosition.Manual;preview.Location=new Point(-32000,-32000);preview.ShowInTaskbar=false;
                // Show only offscreen with NOACTIVATE so WinForms creates all
                // child handles. No tray, guard, event signal or capture exists.
                preview.Show();preview.PerformLayout();preview.Update();Application.DoEvents();
                using(Bitmap bitmap=new Bitmap(preview.Width,preview.Height))
                { preview.DrawToBitmap(bitmap,new Rectangle(Point.Empty,bitmap.Size));bitmap.Save(args[1],ImageFormat.Png); }
                preview.Hide();
            }
            return 0;
        }
        if(mode==Mode.Invalid) { Console.WriteLine(Json(new{status="not-started",error="Use --tray, --show, --stop, --self-test or --render-preview ABSOLUTE.PNG."}));return 64; }
        try
        {
            RequireOwner();
            if(mode==Mode.Show) return ShowInstalled();
            if(mode==Mode.Stop) return StopInstalled();
            DailyPaths.RequireInstalledCodePaths();ReadManifest();
            if(!IsAdministrator()) throw new UnauthorizedAccessException("Automatic capture must be started by the reviewed elevated helper task.");
            bool created;
            using(Mutex singleton=new Mutex(true,MutexName,out created))
            {
                if(!created) return 0;
                try
                {
                    Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
                    using(TrayContext tray=new TrayContext()) { Application.Run(tray);return tray.ExitCode; }
                }
                finally { singleton.ReleaseMutex(); }
            }
        }
        catch(Exception error)
        {
            string message=Describe(error);Console.WriteLine(Json(new{status="error",error=message}));
            MessageBox.Show(message,"InputLift",MessageBoxButtons.OK,MessageBoxIcon.Error);return 1;
        }
    }
}
