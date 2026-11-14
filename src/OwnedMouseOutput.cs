// Copyright (c) 2026 GlobeRelay contributors
// SPDX-License-Identifier: MIT
// Only fixed, marked Mouse5 output. Foreign hooks read ExtraInfo only, then
// pass through unchanged. No keyboard hook, text/position capture, files or I/O.
// The next routing hook observes owned button events before our nonzero return
// blocks local window delivery. The fixed integration gate lives separately in
// DeskflowRouteProbe; this class retains hook health and release safeguards.
// Windows can silently remove timed-out low-level hooks;
// refresh plus swallowed one-pixel health acknowledgement reduces, not
// eliminates, races. Windows may omit zero-move hook callbacks and truncate
// extra-info markers to32bits, so distinct exact32bit markers are deliberate.
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

internal sealed class OwnedMouseOutput : IDisposable
{
    internal const ulong InputMarker = 0x474C4D35UL;
    private const ulong HealthMarker = 0x474C4854UL;
    private const uint MouseMove = 0x0200, Mouse5Down = 0x020B, Mouse5Up = 0x020C;
    private const uint RefreshMessage = 0x8001, QuitMessage = 0x0012;
    private const int WhMouseLl = 14, TimeoutError = 1460, InvalidData = 13, NotReady = 21;
    private const int RefreshWaitMilliseconds = 2000, AcknowledgementMilliseconds = 500;

    internal struct SendResult
    {
        public uint Inserted;
        public int Error;
        public bool Acknowledged;
        public bool MouseButtonAttempted;
        public string Stage;
        internal SendResult(uint inserted, int error, bool ack, string stage)
        { Inserted = inserted; Error = error; Acknowledged = ack; Stage = stage; MouseButtonAttempted=false; }
    }
    private struct Preparation
    {
        internal bool Ready;
        internal int Error;
        internal string Stage;
        internal Preparation(bool ready, int error, string stage) { Ready = ready; Error = error; Stage = stage; }
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct Point { internal int X, Y; }
    [StructLayout(LayoutKind.Sequential)]
    private struct Message
    {
        internal IntPtr Window;
        internal uint Id;
        internal UIntPtr WParam;
        internal IntPtr LParam;
        internal uint Time;
        internal Point Position;
        internal uint Private;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        internal int X, Y;
        internal uint Data, Flags, Time;
        internal UIntPtr ExtraInfo;
    }
    [StructLayout(LayoutKind.Explicit, Size = 40)]
    private struct Input
    {
        [FieldOffset(0)] internal uint Type;
        [FieldOffset(8)] internal MouseInput Mouse;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct LowLevelMouse
    {
        internal Point Position;
        internal uint Data, Flags, Time;
        internal UIntPtr ExtraInfo;
    }
    private delegate IntPtr HookProcedure(int code, IntPtr message, IntPtr data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern IntPtr SetWindowsHookExW(int id, HookProcedure callback, IntPtr module, uint thread);
    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern int GetMessageW(out Message message, IntPtr window, uint first, uint last);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool PeekMessageW(out Message message, IntPtr window, uint first, uint last, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool PostThreadMessageW(uint thread, uint message, UIntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", ExactSpelling = true)] private static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern IntPtr GetModuleHandleW(string name);
    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern uint SendInput(uint count, [In] Input[] input, int size);
    [DllImport("kernel32.dll", ExactSpelling = true)] private static extern void SetLastError(uint error);
    private readonly object sendGate = new object();
    private readonly object routeGate = new object();
    private DeskflowRouteProbe routeProbe;
    private readonly ManualResetEvent ready = new ManualResetEvent(false), refreshed = new ManualResetEvent(false);
    private readonly ManualResetEvent healthAck = new ManualResetEvent(false), mouseAck = new ManualResetEvent(false), stopped = new ManualResetEvent(false);
    private readonly HookProcedure callback;
    private readonly Thread hookThread;
    private IntPtr hook;
    private uint threadId;
    private int threadFailure, callbackFailure, pendingMessage, healthPending;
    private volatile bool disposed;
    private string failureStage;

    private OwnedMouseOutput()
    {
        callback = Hook;
        hookThread = new Thread(Pump) { IsBackground = true, Name = "Globe owned Mouse5 suppression" };
    }
    internal static OwnedMouseOutput Start()
    {
        if(!CorrectAbi()) throw new InvalidOperationException("Reviewed x64 mouse INPUT/hook/message ABI is required.");
        OwnedMouseOutput output = new OwnedMouseOutput();
        output.hookThread.Start();
        if(!output.ready.WaitOne(RefreshWaitMilliseconds))
        { output.Dispose(); throw new TimeoutException("Owned Mouse5 hook did not acknowledge startup within two seconds."); }
        int error=Interlocked.CompareExchange(ref output.threadFailure,0,0);
        if(error!=0)
        { string stage=output.failureStage; output.Dispose(); throw new Win32Exception(error,stage+": "+new Win32Exception(error).Message); }
        return output;
    }
    private static bool CorrectAbi()
    {
        return IntPtr.Size==8 && Marshal.SizeOf(typeof(Input))==40 && Marshal.SizeOf(typeof(MouseInput))==32 &&
            Marshal.OffsetOf(typeof(MouseInput),"ExtraInfo").ToInt32()==24 && Marshal.SizeOf(typeof(LowLevelMouse))==32 &&
            Marshal.OffsetOf(typeof(LowLevelMouse),"ExtraInfo").ToInt32()==24 && Marshal.SizeOf(typeof(Message))==48;
    }
    private void ThreadFailure(string stage,int error)
    {
        if(error==0) error=31;
        Interlocked.CompareExchange(ref threadFailure,error,0);
        Interlocked.CompareExchange(ref failureStage,stage,null);
    }
    private void InstallFreshHook()
    {
        if(hook!=IntPtr.Zero)
        {
            IntPtr prior=hook; hook=IntPtr.Zero;
            if(!UnhookWindowsHookEx(prior))
            {
                int error=Marshal.GetLastWin32Error();
                // A silently removed timed-out hook has no valid handle to undo.
                if(error!=1404) { ThreadFailure("UnhookWindowsHookEx(refresh)",error); return; }
            }
        }
        IntPtr module=GetModuleHandleW(null);
        if(module==IntPtr.Zero) { ThreadFailure("GetModuleHandleW",Marshal.GetLastWin32Error()); return; }
        hook=SetWindowsHookExW(WhMouseLl,callback,module,0);
        if(hook==IntPtr.Zero) ThreadFailure("SetWindowsHookExW",Marshal.GetLastWin32Error());
    }
    private void Pump()
    {
        try
        {
            threadId=GetCurrentThreadId();
            Message message;
            PeekMessageW(out message,IntPtr.Zero,0,0,0); // Create this thread's message queue.
            InstallFreshHook();
            ready.Set();
            if(Interlocked.CompareExchange(ref threadFailure,0,0)!=0) return;
            while(true)
            {
                int status=GetMessageW(out message,IntPtr.Zero,0,0);
                if(status==0) break;
                if(status<0) { ThreadFailure("GetMessageW",Marshal.GetLastWin32Error()); break; }
                if(message.Id==RefreshMessage)
                {
                    try { InstallFreshHook(); }
                    finally { refreshed.Set(); }
                    if(Interlocked.CompareExchange(ref threadFailure,0,0)!=0) break;
                }
            }
        }
        catch(Exception error) { ThreadFailure("Owned mouse hook thread: "+error.GetType().Name,31); }
        finally
        {
            if(hook!=IntPtr.Zero)
            {
                IntPtr prior=hook; hook=IntPtr.Zero;
                if(!UnhookWindowsHookEx(prior))
                { int error=Marshal.GetLastWin32Error(); if(error!=1404) ThreadFailure("UnhookWindowsHookEx(stop)",error); }
            }
            ready.Set(); refreshed.Set(); stopped.Set();
            GC.KeepAlive(callback);
        }
    }
    private enum Route { Pass, Health, Button, MalformedOwned }
    private static Route ClassifyMarker(int code,uint message,ulong marker)
    {
        if(code<0) return Route.Pass;
        if(marker==HealthMarker) return message==MouseMove ? Route.Health : Route.MalformedOwned;
        if(marker!=InputMarker) return Route.Pass;
        return message==Mouse5Down || message==Mouse5Up ? Route.Button : Route.MalformedOwned;
    }
    private static Route Classify(int code,uint message,Func<ulong> readMarker,Func<uint> readData)
    {
        if(code<0) return Route.Pass;
        Route route=ClassifyMarker(code,message,readMarker());
        return route==Route.Button && (readData()>>16)!=2 ? Route.MalformedOwned : route;
    }
    private static IntPtr RouteEvent(Route route,Func<IntPtr> next,Action health,Action button,Action malformed)
    {
        if(route==Route.Pass) return next();
        if(route==Route.Health) health();
        else if(route==Route.Button) { next(); button(); }
        else malformed();
        return new IntPtr(1); // Never deliver our marked events to a host window.
    }
    private IntPtr Hook(int code,IntPtr message,IntPtr data)
    {
        bool owned=false;
        try
        {
            if(code<0) return CallNextHookEx(hook,code,message,data);
            uint kind=unchecked((uint)message.ToInt64());
            ulong marker=unchecked((ulong)Marshal.ReadInt64(data,24));
            Route route=ClassifyMarker(code,kind,marker);
            // The common foreign path allocates no delegates and reads no
            // positions, flags, timestamps, button fields or ordinary key data.
            if(route==Route.Pass) return CallNextHookEx(hook,code,message,data);
            owned=true;
            if(route==Route.Button && (unchecked((uint)Marshal.ReadInt32(data,8))>>16)!=2) route=Route.MalformedOwned;
            if(route==Route.Health)
            { if(Interlocked.Exchange(ref healthPending,0)==1) healthAck.Set(); }
            else if(route==Route.Button)
            {
                CallNextHookEx(hook,code,message,data);
                if(Interlocked.CompareExchange(ref pendingMessage,0,unchecked((int)kind))==unchecked((int)kind)) mouseAck.Set();
            }
            else Interlocked.Exchange(ref callbackFailure,InvalidData);
            return new IntPtr(1);
        }
        catch
        {
            Interlocked.Exchange(ref callbackFailure,InvalidData);
            return owned ? new IntPtr(1) : CallNextHookEx(hook,code,message,data);
        }
    }
    private static Input BuildInput(bool down,bool health)
    {
        return new Input { Type=0,Mouse=new MouseInput { X=health?1:0,Y=0,Data=health?0u:2u,
            Flags=health?0x2001u:(down?0x80u:0x100u),Time=0,ExtraInfo=new UIntPtr(health?HealthMarker:InputMarker) } };
    }
    private static SendResult NativeInput(bool down,bool health)
    {
        Input[] inputs={BuildInput(down,health)};
        SetLastError(0);
        uint inserted=SendInput(1,inputs,40);
        int error=Marshal.GetLastWin32Error();
        SendResult result=new SendResult(inserted,inserted==1?0:error,false,health?"SendInput(health)":"SendInput(Mouse5)");
        result.MouseButtonAttempted=!health;
        return result;
    }
    private Preparation RefreshAndVerify()
    {
        int failed=Interlocked.CompareExchange(ref threadFailure,0,0);
        if(failed!=0 || stopped.WaitOne(0)) return new Preparation(false,failed==0?NotReady:failed,"OwnedHookThreadUnavailable");
        refreshed.Reset();
        if(!PostThreadMessageW(threadId,RefreshMessage,UIntPtr.Zero,IntPtr.Zero))
            return new Preparation(false,Marshal.GetLastWin32Error(),"PostThreadMessageW(refresh)");
        if(!refreshed.WaitOne(RefreshWaitMilliseconds)) return new Preparation(false,TimeoutError,"OwnedHookRefreshTimeout");
        failed=Interlocked.CompareExchange(ref threadFailure,0,0);
        if(failed!=0 || stopped.WaitOne(0)) return new Preparation(false,failed==0?NotReady:failed,"OwnedHookRefreshFailed");
        healthAck.Reset(); Interlocked.Exchange(ref healthPending,1);
        SendResult health=NativeInput(false,true);
        if(health.Inserted!=1)
        { Interlocked.Exchange(ref healthPending,0); return new Preparation(false,health.Error,"SendInput(health) inserted "+health.Inserted+" of1"); }
        if(!healthAck.WaitOne(AcknowledgementMilliseconds))
        { Interlocked.Exchange(ref healthPending,0); return new Preparation(false,TimeoutError,"OwnedHookHealthAcknowledgement"); }
        int bad=Interlocked.CompareExchange(ref callbackFailure,0,0);
        return bad==0 ? new Preparation(true,0,"ready") : new Preparation(false,bad,"MalformedOwnedHookEvent");
    }
    private static SendResult SendCore(bool down,Func<bool> canStart,Func<Preparation> prepare,Func<bool,SendResult> send,Func<bool> ack)
    {
        if(down && !canStart()) return new SendResult(0,NotReady,false,"DeskflowDeskNotVisible");
        Preparation prepared=prepare();
        if(!prepared.Ready) return new SendResult(0,prepared.Error,false,prepared.Stage);
        if(down && !canStart()) return new SendResult(0,NotReady,false,"DeskflowDeskChangedBeforeDown");
        SendResult result=send(down); // Never retry DOWN, even on an ambiguous failure.
        if(result.Inserted!=1 || result.Error!=0) return result;
        result.Acknowledged=ack();
        if(!result.Acknowledged) { result.Error=TimeoutError; result.Stage="Mouse5HookAcknowledgement"; }
        return result;
    }
    internal SendResult Send(bool down)
    {
        lock(sendGate)
        {
            if(disposed || Thread.CurrentThread==hookThread) return new SendResult(0,NotReady,false,"OwnedMouseOutputUnavailable");
            RouteAdmissionLease lease=new RouteAdmissionLease();
            SendResult result=SendCore(down,delegate {
                var route=RouteSnapshot;
                return lease.Allows(HookHealthy&&route.CanStartPress,route.Epoch);
            },RefreshAndVerify,
                delegate(bool pressed) {
                    mouseAck.Reset(); Interlocked.Exchange(ref pendingMessage,unchecked((int)(pressed?Mouse5Down:Mouse5Up)));
                    SendResult sent=NativeInput(pressed,false);
                    if(sent.Inserted!=1) Interlocked.Exchange(ref pendingMessage,0);
                    return sent;
                },delegate {
                    bool seen=mouseAck.WaitOne(AcknowledgementMilliseconds);
                    Interlocked.Exchange(ref pendingMessage,0);
                    return seen && Interlocked.CompareExchange(ref callbackFailure,0,0)==0;
                });
            int malformed=Interlocked.CompareExchange(ref callbackFailure,0,0);
            if(malformed!=0 && result.Error==TimeoutError)
            { result.Error=malformed; result.Stage="MalformedOwnedHookEvent"; }
            return result;
        }
    }
    internal bool CanStartPress
    { get { return HookHealthy&&RouteSnapshot.CanStartPress; } }
    internal bool HookHealthy
    { get { return !disposed&&hookThread.IsAlive&&Interlocked.CompareExchange(ref threadFailure,0,0)==0&&Interlocked.CompareExchange(ref callbackFailure,0,0)==0; } }
    internal DeskflowRouteProbe.Snapshot RouteSnapshot
    {
        get
        {
            // UP-only emergency cleanup never reads this property, so it does
            // not start a discovery worker. At most one worker per output owner.
            DeskflowRouteProbe current=Interlocked.CompareExchange(ref routeProbe,null,null);
            if(current==null&&!disposed)lock(routeGate)
            { if(routeProbe==null&&!disposed)Interlocked.Exchange(ref routeProbe,DeskflowRouteProbe.Start());current=routeProbe; }
            return current==null?DeskflowRouteProbe.NotStarted():current.Read();
        }
    }
    public void Dispose()
    {
        lock(sendGate)
        {
            if(disposed) return;
            disposed=true;
            DeskflowRouteProbe probe;
            lock(routeGate) { probe=routeProbe; }
            if(probe!=null)probe.Dispose();
            Stopwatch stopTime=Stopwatch.StartNew();
            if(threadId!=0 && !stopped.WaitOne(0))
            {
                if(!PostThreadMessageW(threadId,QuitMessage,UIntPtr.Zero,IntPtr.Zero))
                    ThreadFailure("PostThreadMessageW(stop)",Marshal.GetLastWin32Error());
            }
            if(!stopped.WaitOne(RefreshWaitMilliseconds))
                throw new TimeoutException("Owned Mouse5 hook thread did not stop within two seconds; process exit is required. No further input sent.");
            int remaining=(int)Math.Max(0,RefreshWaitMilliseconds-stopTime.ElapsedMilliseconds);
            if(!hookThread.Join(remaining))
                throw new TimeoutException("Owned Mouse5 hook thread exit is not confirmed; no further input sent.");
            ready.Dispose(); refreshed.Dispose(); healthAck.Dispose(); mouseAck.Dispose(); stopped.Dispose();
        }
    }

    internal static string[] RunSelfTests(out int count)
    {
        int cases=0; List<string> errors=new List<string>();
        Action<bool,string> check=delegate(bool okay,string name){cases++;if(!okay) errors.Add(name);};
        check(CorrectAbi(),"Reviewed x64 INPUT40/MOUSEINPUT32/MSLLHOOKSTRUCT32/MSG48 ABI");
        check(InputMarker!=HealthMarker,"Mouse5 and health markers are distinct");
        check(InputMarker<=uint.MaxValue && HealthMarker<=uint.MaxValue,"Exact markers survive Windows32bit extra-info truncation");
        check(ClassifyMarker(0,MouseMove,HealthMarker+0x100000000UL)==Route.Pass,"A foreign64bit value with matching low marker bits is not accepted");
        Input down=BuildInput(true,false),up=BuildInput(false,false),health=BuildInput(false,true);
        check(down.Type==0 && down.Mouse.Data==2 && down.Mouse.Flags==0x80 && down.Mouse.ExtraInfo.ToUInt64()==InputMarker,"Fixed Mouse5 DOWN encoding");
        check(up.Type==0 && up.Mouse.Data==2 && up.Mouse.Flags==0x100 && up.Mouse.ExtraInfo.ToUInt64()==InputMarker,"Fixed Mouse5 UP encoding");
        check(health.Mouse.Data==0 && health.Mouse.Flags==0x2001 && health.Mouse.ExtraInfo.ToUInt64()==HealthMarker &&
            health.Mouse.X==1 && health.Mouse.Y==0,"Swallowed health input has nonzero movement and no button flags");
        int markerReads=0,dataReads=0,nextCalls=0,healthCalls=0,buttonCalls=0,badCalls=0;
        Func<ulong> foreign=delegate{markerReads++;return 17;};
        Func<uint> noData=delegate{dataReads++;throw new Exception("Foreign button/position data read is prohibited.");};
        Route route=Classify(0,Mouse5Down,foreign,noData);
        check(route==Route.Pass && markerReads==1 && dataReads==0,"Foreign input reads only marker");
        route=Classify(-1,Mouse5Down,delegate{throw new Exception("Negative code must not read data.");},noData);
        check(route==Route.Pass,"Negative hook codes pass without reading any field");
        Func<IntPtr> next=delegate{nextCalls++;return new IntPtr(73);};
        Action onHealth=delegate{healthCalls++;},onButton=delegate{buttonCalls++;},onBad=delegate{badCalls++;};
        check(RouteEvent(Route.Pass,next,onHealth,onButton,onBad)==new IntPtr(73) && nextCalls==1,"Foreign hook return is unchanged");
        route=Classify(0,MouseMove,delegate{return HealthMarker;},noData);
        check(RouteEvent(route,next,onHealth,onButton,onBad)==new IntPtr(1) && nextCalls==1 && healthCalls==1 && dataReads==0,
            "Own health event is acknowledged and swallowed before Deskflow or window delivery");
        route=Classify(0,Mouse5Down,delegate{return InputMarker;},delegate{dataReads++;return 2u<<16;});
        check(RouteEvent(route,next,onHealth,onButton,onBad)==new IntPtr(1) && nextCalls==2 && buttonCalls==1,
            "Own Mouse5 calls next hook then blocks host window delivery");
        route=Classify(0,Mouse5Up,delegate{return InputMarker;},delegate{return 2u<<16;});
        check(route==Route.Button,"Own Mouse5 UP uses the same suppression route");
        route=Classify(0,Mouse5Down,delegate{return InputMarker;},delegate{return 1u<<16;});
        check(RouteEvent(route,next,onHealth,onButton,onBad)==new IntPtr(1) && nextCalls==2 && badCalls==1,"Wrong owned button is blocked without routing");
        check(Classify(0,MouseMove,delegate{return InputMarker;},noData)==Route.MalformedOwned,"Unexpected owned message is rejected before reading button fields");
        int routeCases;
        errors.AddRange(DeskflowRouteGate.RunSelfTests(out routeCases));
        cases+=routeCases;
        int prepared=0,sent=0,acked=0,gated=0;
        Func<Preparation> prep=delegate{prepared++;return new Preparation(true,0,"ready");};
        Func<bool,SendResult> sender=delegate(bool pressed){sent++;return new SendResult(1,0,false,"SendInput(Mouse5)");};
        Func<bool> acknowledge=delegate{acked++;return true;};
        SendResult result=SendCore(true,delegate{gated++;return false;},prep,sender,acknowledge);
        check(result.Inserted==0 && result.Error==NotReady && prepared==0 && sent==0,"Inactive gate refuses before health or button input");
        result=SendCore(false,delegate{throw new Exception("UP must ignore active-target gate.");},prep,sender,acknowledge);
        check(result.Inserted==1 && result.Error==0 && result.Acknowledged && prepared==1 && sent==1 && acked==1,"UP can clean ownership while host is active, with health required");
        prepared=sent=acked=0; gated=0;
        result=SendCore(true,delegate{gated++;return gated==1;},prep,sender,acknowledge);
        check(result.Inserted==0 && result.Stage=="DeskflowDeskChangedBeforeDown" && prepared==1 && sent==0,"Gate is checked again after health and before DOWN");
        prepared=sent=acked=0;
        result=SendCore(true,delegate{return true;},delegate{prepared++;return new Preparation(false,TimeoutError,"health");},sender,acknowledge);
        check(result.Inserted==0 && result.Error==TimeoutError && !result.MouseButtonAttempted && sent==0 && prepared==1,"Health failure sends no Mouse5 input and retains explicit non-attempt evidence");
        result=SendCore(true,delegate{return true;},prep,delegate(bool pressed){sent++;return new SendResult(0,5,false,"SendInput(Mouse5)");},acknowledge);
        check(result.Inserted==0 && result.Error==5 && sent==1 && acked==0,"Native insertion failure preserves exact count/error, no retry or acknowledgement wait");
        sent=0;
        result=SendCore(true,delegate{return true;},prep,sender,delegate{return false;});
        check(result.Inserted==1 && result.Error==TimeoutError && !result.Acknowledged && sent==1,
            "Unacknowledged inserted DOWN keeps actual count and explicit failure; no DOWN replay");
        sent=0;
        result=SendCore(true,delegate{return true;},prep,sender,acknowledge);
        check(result.Inserted==1 && result.Error==0 && result.Acknowledged && sent==1,"Healthy gated DOWN is sent once and acknowledged");
        var lease=new RouteAdmissionLease();long routeEpoch=10;
        result=SendCore(true,delegate{return lease.Allows(true,routeEpoch);},delegate { routeEpoch++;return new Preparation(true,0,"ready"); },
            delegate(bool pressed){throw new Exception("A changed admission epoch must send no DOWN.");},acknowledge);
        check(result.Stage=="DeskflowDeskChangedBeforeDown" && !result.MouseButtonAttempted,"A route epoch change during hook preparation refuses DOWN without replay");
        bool fresh=true;lease=new RouteAdmissionLease();
        result=SendCore(true,delegate{return lease.Allows(fresh,20);},delegate { fresh=false;return new Preparation(true,0,"ready"); },
            delegate(bool pressed){throw new Exception("An expired observation must send no DOWN.");},acknowledge);
        check(result.Stage=="DeskflowDeskChangedBeforeDown" && !result.MouseButtonAttempted,"A route snapshot that expires during hook preparation refuses DOWN");
        var policy=new RelayRoutePolicy();int outputsDown=0,outputsUp=0,journalClears=0;bool journal=false;
        GlobeShortcutRelay.BatchSender relaySender=delegate(GlobeShortcutRelay.Stroke[] strokes) {
            if(strokes[0]==GlobeShortcutRelay.Stroke.Mouse5Down)outputsDown++;else outputsUp++;
            return new GlobeShortcutRelay.SendResult(1,0);
        };
        Action<bool> persist=delegate(bool armed){journal=armed;if(!armed)journalClears++;};
        var relay=new GlobeShortcutRelay(delegate{return false;},relaySender,persist);relay.InitializeJournal();
        policy.ObservePhysical(false);policy.ObserveRoute(true,1,false);
        check(policy.CanArm(true,false),"A trusted neutral report arms on the selected route");
        policy.ObservePhysical(true);relay.OnGlobeDown();
        if(policy.ObserveRoute(false,2,true))relay.Abort("test-route-loss");
        int clearedAtLoss=journalClears;
        if(policy.ObserveRoute(false,2,false))relay.Abort("test-repeated-loss");
        check(outputsDown==1&&outputsUp==1&&!journal&&!relay.JournalArmed&&!relay.ChordMayBeDown&&journalClears==clearedAtLoss,
            "Disconnect while held releases owned Mouse5 once and clears its durable journal once");
        policy.ObserveRoute(true,3,false);
        check(!policy.CanArm(true,true)&&outputsDown==1,"Held Fn on route return cannot rearm or replay a press");
        policy.ObservePhysical(false);
        if(policy.CanArm(true,false)) { relay=new GlobeShortcutRelay(delegate{return false;},relaySender,persist);relay.InitializeJournal(); }
        policy.ObservePhysical(true);relay.OnGlobeDown();policy.ObservePhysical(false);relay.OnGlobeUp();
        check(outputsDown==2&&outputsUp==2&&!journal,"A later real neutral and fresh physical press recover without keyboard reidentification");
        count=cases; return errors.ToArray();
    }
}
