// Copyright (c) 2026 InputLift contributors
// SPDX-License-Identifier: MIT
// One read-only discovery worker. Capture/status callers never enumerate processes
// or query another window. A stale/slow/failed observation cannot admit a press.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

internal sealed class DeskflowRouteProbe : IDisposable
{
    internal const int FreshMilliseconds=500, PollMilliseconds=100, StopJoinMilliseconds=500;
    internal enum RouteReason { Starting, CoreUnavailable, NoRemoteWindow, Available, Unresponsive, Stale, QueryFailed, SlowQuery, Paused, Stopped }
    internal enum StageKind { Starting, ProcessDiscovery, ImageVerification, WindowDiscovery, WindowResponsiveness, Waiting, Paused, Stopped }
    internal sealed class Observation
    {
        internal readonly bool ServerPresent, CoreWindowResponsive, RemoteSelected;
        internal readonly uint ProcessId;
        internal readonly long ProcessStart;
        internal readonly IntPtr Window;
        internal readonly RouteReason Reason;
        internal Observation(bool server,bool responsive,bool selected,uint pid,long start,IntPtr window,RouteReason reason)
        { ServerPresent=server;CoreWindowResponsive=responsive;RemoteSelected=selected;ProcessId=pid;ProcessStart=start;Window=window;Reason=reason; }
    }
    internal sealed class Sample
    {
        internal readonly Observation Value;
        internal readonly long Epoch,Completed,Duration,Generation;
        internal Sample(Observation value,long epoch,long completed,long duration,long generation=1)
        { Value=value;Epoch=epoch;Completed=completed;Duration=duration;Generation=generation; }
    }
    internal sealed class Snapshot
    {
        internal readonly bool Fresh,ServerPresent,CoreWindowResponsive,RemoteSelected,CanStartPress;
        internal readonly long Epoch,ObservedMonotonicMilliseconds,AgeMilliseconds,QueryDurationMilliseconds;
        internal readonly StageKind Stage;
        internal readonly RouteReason Reason;
        internal Snapshot(bool fresh,Sample sample,long age,StageKind stage,RouteReason reason)
        {
            Fresh=fresh;ServerPresent=fresh&&sample.Value.ServerPresent;
            CoreWindowResponsive=fresh&&sample.Value.CoreWindowResponsive;
            RemoteSelected=fresh&&sample.Value.RemoteSelected;
            CanStartPress=ServerPresent&&CoreWindowResponsive&&RemoteSelected;
            Epoch=sample.Epoch;ObservedMonotonicMilliseconds=sample.Completed;
            AgeMilliseconds=age;QueryDurationMilliseconds=sample.Duration;Stage=stage;Reason=reason;
        }
    }
    private readonly Func<Action<StageKind>,Observation> query;
    private readonly Func<long> clock;
    private readonly int poll;
    private readonly ManualResetEvent stop=new ManualResetEvent(false);
    private readonly object pollingGate=new object();
    private int pollingEnabled=1;
    private long pollingGeneration=1;
    private readonly Thread worker;
    private Sample sample;
    private int stage=(int)StageKind.Starting;
    private volatile bool stopping;
    private bool disposed;
    private DeskflowRouteProbe(Func<Action<StageKind>,Observation> query,Func<long> clock,int poll)
    {
        this.query=query;this.clock=clock;this.poll=poll;
        sample=new Sample(new Observation(false,false,false,0,0,IntPtr.Zero,RouteReason.Starting),1,clock(),0);
        worker=new Thread(Run) { IsBackground=true,Name="InputLift read-only Deskflow route probe" };
        worker.Start();
    }
    internal static DeskflowRouteProbe Start()
    { return new DeskflowRouteProbe(QueryNative,MonotonicMilliseconds,PollMilliseconds); }
    internal static Snapshot NotStarted()
    { return new Snapshot(false,new Sample(new Observation(false,false,false,0,0,IntPtr.Zero,RouteReason.Starting),1,MonotonicMilliseconds(),0),0,StageKind.Starting,RouteReason.Starting); }
    internal static long MonotonicMilliseconds()
    {
        long ticks=Stopwatch.GetTimestamp(),frequency=Stopwatch.Frequency;
        return checked((ticks/frequency)*1000+(ticks%frequency)*1000/frequency);
    }
    internal Snapshot Read()
    {
        Sample observed=Interlocked.CompareExchange(ref sample,null,null);
        long now=clock(),age=now>=observed.Completed?now-observed.Completed:long.MaxValue;
        bool enabled=Interlocked.CompareExchange(ref pollingEnabled,0,0)==1;
        long generation=Interlocked.Read(ref pollingGeneration);
        bool fresh=!stopping && enabled && observed.Generation==generation && age<=FreshMilliseconds && observed.Duration<=FreshMilliseconds-age &&
            observed.Value.Reason!=RouteReason.Starting && observed.Value.Reason!=RouteReason.QueryFailed && observed.Value.Reason!=RouteReason.SlowQuery;
        RouteReason reason=stopping?RouteReason.Stopped:!enabled?RouteReason.Paused:!fresh && observed.Value.Reason!=RouteReason.Starting &&
            observed.Value.Reason!=RouteReason.QueryFailed && observed.Value.Reason!=RouteReason.SlowQuery?RouteReason.Stale:observed.Value.Reason;
        return new Snapshot(fresh,observed,age,(StageKind)Interlocked.CompareExchange(ref stage,0,0),reason);
    }
    internal void SetPollingEnabled(bool enabled)
    {
        lock(pollingGate)
        {
            if(stopping)return;
            int next=enabled?1:0;
            if(pollingEnabled==next)return;
            // Invalidate cached and in-flight results before a new generation
            // can admit input. Resume reuses this exact worker, never a clone.
            if(pollingGeneration==long.MaxValue)throw new InvalidOperationException("Polling generation exhausted.");
            Interlocked.Increment(ref pollingGeneration);
            Interlocked.Exchange(ref pollingEnabled,next);
        }
    }
    internal bool WorkerStopped { get { return !worker.IsAlive; } }
    private static bool IdentityChanged(Observation first,Observation next)
    { return first.ServerPresent!=next.ServerPresent || first.CoreWindowResponsive!=next.CoreWindowResponsive ||
        first.RemoteSelected!=next.RemoteSelected || first.ProcessId!=next.ProcessId || first.ProcessStart!=next.ProcessStart || first.Window!=next.Window; }
    private void Run()
    {
        try
        {
            while(!stopping)
            {
                long generation;
                lock(pollingGate) { generation=pollingEnabled==1?pollingGeneration:0; }
                if(generation==0)
                { Interlocked.Exchange(ref stage,(int)StageKind.Paused);if(stop.WaitOne(poll))break;continue; }
                long started=clock();Observation result;
                try { result=query(delegate(StageKind next){Interlocked.Exchange(ref stage,(int)next);});
                    if(result==null)throw new InvalidOperationException("Missing route observation."); }
                catch { result=new Observation(false,false,false,0,0,IntPtr.Zero,RouteReason.QueryFailed); }
                long completed=clock(),duration=completed>=started?completed-started:long.MaxValue;
                if(duration>FreshMilliseconds)result=new Observation(false,false,false,0,0,IntPtr.Zero,RouteReason.SlowQuery);
                if(stopping)break;
                if(Interlocked.CompareExchange(ref pollingEnabled,0,0)!=1 || generation!=Interlocked.Read(ref pollingGeneration))continue;
                Sample prior=Interlocked.CompareExchange(ref sample,null,null);
                long epoch=prior.Epoch;
                if(IdentityChanged(prior.Value,result) || prior.Value.Reason!=result.Reason)
                { if(epoch==long.MaxValue)throw new InvalidOperationException("Route epoch exhausted.");epoch++; }
                Interlocked.Exchange(ref sample,new Sample(result,epoch,completed,duration,generation));
                Interlocked.Exchange(ref stage,(int)StageKind.Waiting);
                if(stop.WaitOne(poll))break;
            }
        }
        finally { stopping=true;Interlocked.Exchange(ref stage,(int)StageKind.Stopped); }
    }
    public void Dispose()
    {
        if(disposed)return;disposed=true;stopping=true;stop.Set();
        // An in-flight Windows query is read-only and cannot be force-aborted.
        // Never spawn replacement workers. The capture process ultimately exits.
        if(worker.Join(StopJoinMilliseconds))stop.Dispose();
    }

    [DllImport("user32.dll",CharSet=CharSet.Unicode,ExactSpelling=true)] private static extern IntPtr FindWindowExW(IntPtr parent,IntPtr after,string name,string title);
    [DllImport("user32.dll",ExactSpelling=true)] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll",ExactSpelling=true)] private static extern uint GetWindowThreadProcessId(IntPtr window,out uint pid);
    [DllImport("user32.dll",CharSet=CharSet.Unicode,ExactSpelling=true,SetLastError=true)] private static extern IntPtr SendMessageTimeoutW(IntPtr window,uint message,UIntPtr wparam,IntPtr lparam,uint flags,uint timeout,out UIntPtr result);
    [DllImport("kernel32.dll",ExactSpelling=true,SetLastError=true)] private static extern IntPtr OpenProcess(uint access,[MarshalAs(UnmanagedType.Bool)] bool inherit,uint pid);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,ExactSpelling=true,SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool QueryFullProcessImageNameW(IntPtr process,uint flags,StringBuilder path,ref uint size);
    [DllImport("kernel32.dll",ExactSpelling=true,SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool GetProcessTimes(IntPtr process,out long creation,out long exit,out long kernel,out long user);
    [DllImport("kernel32.dll",ExactSpelling=true,SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(IntPtr handle);
    private static bool ExactCore(uint pid,out long started)
    {
        started=0;IntPtr process=OpenProcess(0x1000,false,pid);if(process==IntPtr.Zero)return false;
        bool matched=false,closed;
        try
        {
            StringBuilder image=new StringBuilder(1024);uint size=1024;long exit,kernel,user;
            matched=QueryFullProcessImageNameW(process,0,image,ref size) && GetProcessTimes(process,out started,out exit,out kernel,out user) &&
                string.Equals(image.ToString(),Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),"Deskflow","deskflow-core.exe"),StringComparison.OrdinalIgnoreCase);
        }
        finally { closed=CloseHandle(process); }
        return matched&&closed;
    }
    private static Observation QueryNative(Action<StageKind> setStage)
    {
        setStage(StageKind.ProcessDiscovery);uint pid=0;
        Process[] cores=Process.GetProcessesByName("deskflow-core");
        try { if(cores.Length==1)pid=unchecked((uint)cores[0].Id); }
        finally { foreach(Process core in cores)core.Dispose(); }
        if(pid==0)return new Observation(false,false,false,0,0,IntPtr.Zero,RouteReason.CoreUnavailable);
        setStage(StageKind.ImageVerification);long started;
        if(!ExactCore(pid,out started))return new Observation(false,false,false,0,0,IntPtr.Zero,RouteReason.CoreUnavailable);
        IntPtr window=IntPtr.Zero,observedWindow=IntPtr.Zero;bool responsive=false;
        for(int index=0;index<64;index++)
        {
            setStage(StageKind.WindowDiscovery);window=FindWindowExW(IntPtr.Zero,window,"DeskflowDesk","DeskflowDesk");
            if(window==IntPtr.Zero)break;
            uint owner;GetWindowThreadProcessId(window,out owner);if(owner!=pid)continue;
            observedWindow=window;setStage(StageKind.WindowResponsiveness);UIntPtr result;
            // WM_NULL has no action payload. This proves a reply by this local
            // window, not a healthy remote connection or authenticated focus.
            bool replied=SendMessageTimeoutW(window,0,UIntPtr.Zero,IntPtr.Zero,3,100,out result)!=IntPtr.Zero;
            responsive|=replied;
            if(replied&&IsWindowVisible(window))return new Observation(true,true,true,pid,started,window,RouteReason.Available);
        }
        return new Observation(true,responsive,false,pid,started,observedWindow,responsive?RouteReason.NoRemoteWindow:RouteReason.Unresponsive);
    }

    internal static List<string> RunSelfTests(out int count)
    {
        var errors=new List<string>();int checks=0;
        Action<bool,string> check=delegate(bool ok,string text){checks++;if(!ok)errors.Add(text);};
        long now=100;int calls=0;var entered=new ManualResetEvent(false);var release=new ManualResetEvent(false);
        DeskflowRouteProbe probe=new DeskflowRouteProbe(delegate(Action<StageKind> phase){
            phase(StageKind.ProcessDiscovery);
            if(Interlocked.Increment(ref calls)>1) { entered.Set();release.WaitOne(2000); }
            else Interlocked.Add(ref now,200);
            return new Observation(true,true,true,1,10,new IntPtr(20),RouteReason.Available);
        },delegate{return Interlocked.Read(ref now);},10);
        try
        {
            Stopwatch wait=Stopwatch.StartNew();while(!probe.Read().CanStartPress&&wait.ElapsedMilliseconds<1000)Thread.Sleep(5);
            check(probe.Read().CanStartPress,"A fresh verified route can admit a press.");
            check(entered.WaitOne(1000),"The synthetic probe entered its blocked read.");
            Interlocked.Exchange(ref now,299);check(!probe.Read().Fresh&&!probe.Read().CanStartPress,"A backward monotonic clock cannot make a future observation fresh.");
            Interlocked.Exchange(ref now,550);check(probe.Read().Fresh,"Observation freshness includes its discovery duration within the total bound.");
            Interlocked.Exchange(ref now,601);check(!probe.Read().Fresh,"Discovery duration plus cached age cannot exceed the freshness bound.");
            Interlocked.Exchange(ref now,1000);
            Stopwatch readTime=Stopwatch.StartNew();Snapshot stale=probe.Read();
            check(!stale.Fresh&&!stale.CanStartPress&&stale.Reason==RouteReason.Stale,"A blocked probe expires fail-closed.");
            check(readTime.ElapsedMilliseconds<1000,"A blocked discovery worker never blocks its snapshot reader.");
        }
        finally { release.Set();probe.Dispose();entered.Dispose();release.Dispose(); }
        check(probe.WorkerStopped&&!probe.Read().CanStartPress,"Bounded stopped probe cannot admit presses.");
        Sample sample=new Sample(new Observation(true,true,true,1,10,new IntPtr(20),RouteReason.Available),3,100,1);
        check(!IdentityChanged(sample.Value,new Observation(true,true,true,1,10,new IntPtr(20),RouteReason.Available)),"Stable identity keeps its route epoch.");
        check(IdentityChanged(sample.Value,new Observation(true,true,true,1,11,new IntPtr(20),RouteReason.Available)),"Process restart invalidates an otherwise similar route.");
        check(IdentityChanged(sample.Value,new Observation(true,true,false,1,10,new IntPtr(20),RouteReason.NoRemoteWindow)),"Remote selection changes its admission epoch.");
        int queries=0,queryThread=0,threadChanged=0;var blocked=new ManualResetEvent(false);var resume=new ManualResetEvent(false);
        var reusable=new DeskflowRouteProbe(delegate(Action<StageKind> phase) {
            int previous=Interlocked.CompareExchange(ref queryThread,Thread.CurrentThread.ManagedThreadId,0);
            if(previous!=0&&previous!=Thread.CurrentThread.ManagedThreadId)Interlocked.Exchange(ref threadChanged,1);
            if(Interlocked.Increment(ref queries)==2) { blocked.Set();resume.WaitOne(2000); }
            return new Observation(true,true,true,1,10,new IntPtr(20),RouteReason.Available);
        },MonotonicMilliseconds,10);
        try
        {
            Stopwatch wait=Stopwatch.StartNew();while(!reusable.Read().CanStartPress&&wait.ElapsedMilliseconds<1000)Thread.Sleep(5);
            check(reusable.Read().CanStartPress&&blocked.WaitOne(1000),"Reusable probe obtains an initial observation before its synthetic blocked query.");
            reusable.SetPollingEnabled(false);reusable.SetPollingEnabled(false);
            check(!reusable.Read().Fresh&&reusable.Read().Reason==RouteReason.Paused,"Pause immediately invalidates the last successful observation.");
            reusable.SetPollingEnabled(true);reusable.SetPollingEnabled(false);reusable.SetPollingEnabled(true);
            check(!reusable.Read().CanStartPress,"Repeated pause/resume cannot reuse a previous generation's cached or in-flight observation.");
            resume.Set();wait.Restart();while(!reusable.Read().CanStartPress&&wait.ElapsedMilliseconds<1000)Thread.Sleep(5);
            check(reusable.Read().CanStartPress&&queries>=3&&threadChanged==0,"Resume obtains a fresh generation on the same one worker.");
            reusable.SetPollingEnabled(false);int pausedQueries=Interlocked.CompareExchange(ref queries,0,0);Thread.Sleep(50);
            check(!reusable.Read().CanStartPress&&queries==pausedQueries,"A paused reusable probe performs no further native-query work.");
        }
        finally { resume.Set();reusable.Dispose();blocked.Dispose();resume.Dispose(); }
        check(reusable.WorkerStopped,"A paused reusable probe stops within its bounded join.");
        errors.AddRange(RelayRoutePolicy.RunSelfTests(out count));count+=checks;return errors;
    }
}

// Pure admission policy shared by the live capture controller and tests.
internal sealed class RelayRoutePolicy
{
    private bool seen,previousAvailable,neutralKnown;
    private long epoch;
    internal void ResetPhysical() { neutralKnown=false; }
    internal void ObservePhysical(bool down) { neutralKnown=!down; }
    internal bool ObserveRoute(bool available,long currentEpoch,bool relayArmed)
    {
        bool pause=relayArmed&&(!available||(seen&&epoch!=currentEpoch));
        seen=true;previousAvailable=available;epoch=currentEpoch;return pause;
    }
    internal bool CanArm(bool keyboardBound,bool physicalDown)
    { return previousAvailable&&keyboardBound&&neutralKnown&&!physicalDown; }
    internal static List<string> RunSelfTests(out int count)
    {
        var errors=new List<string>();int checks=0;Action<bool,string> check=delegate(bool ok,string text){checks++;if(!ok)errors.Add(text);};
        var policy=new RelayRoutePolicy();policy.ObservePhysical(false);policy.ObserveRoute(true,1,false);
        check(policy.CanArm(true,false),"Known neutral permits an ergonomic first press on a fresh route.");
        policy.ObservePhysical(true);check(policy.ObserveRoute(false,2,true),"Route loss requests an armed relay's pause.");
        check(!policy.ObserveRoute(false,2,false),"Repeated unavailable state does not request duplicate abort.");
        policy.ObserveRoute(true,3,false);check(!policy.CanArm(true,true),"A held key on route return cannot rearm or replay.");
        policy.ObservePhysical(false);check(policy.CanArm(true,false),"A physical release rearms without Bluetooth reidentification.");
        check(policy.ObserveRoute(true,4,true),"A different route epoch pauses an armed relay even if selected again.");
        policy.ResetPhysical();check(!policy.CanArm(true,false),"Keyboard/channel reset requires another real neutral report.");
        policy.ObservePhysical(false);check(!policy.CanArm(false,false),"An unbound keyboard cannot arm from route presence alone.");
        var lease=new RouteAdmissionLease();check(!lease.Allows(false,1),"Unavailable route cannot establish an admission lease.");
        check(lease.Allows(true,2)&&lease.Allows(true,2),"A fresh same-epoch route remains valid across output preparation.");
        check(!lease.Allows(true,3),"Route epoch change during preparation rejects DOWN.");
        check(!lease.Allows(false,2),"An expired route rejects DOWN even with the same epoch.");
        count=checks;return errors;
    }
}

internal sealed class RouteAdmissionLease
{
    private bool seen;private long epoch;
    internal bool Allows(bool available,long next)
    { if(!available)return false;if(!seen){seen=true;epoch=next;}return epoch==next; }
}
