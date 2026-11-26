// Copyright (c) 2026 InputLift contributors
// SPDX-License-Identifier: MIT
// Fixed, outbound-only Fn state. No packet payload, command, target key or network endpoint.
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

internal sealed class FnEventPipe : IDisposable
{
    // PIPE_ACCESS_OUTBOUND=0x2; 0x1 is INBOUND and cannot feed a read-only client.
    private const uint OutboundOverlappedFirst = 2U | 0x40000000U | 0x00080000U;
    private const uint RejectRemoteClients = 8;
    private const int MaximumQueuedEdges=64, WriteTimeoutMilliseconds=500, CancellationDrainMilliseconds=500;
    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes { internal int Length;internal IntPtr Descriptor;[MarshalAs(UnmanagedType.Bool)] internal bool Inherit; }
    [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true,ExactSpelling=true)]
    [return:MarshalAs(UnmanagedType.Bool)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(string text,uint revision,out IntPtr descriptor,out uint size);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true,ExactSpelling=true)]
    private static extern SafePipeHandle CreateNamedPipeW(string name,uint openMode,uint pipeMode,uint instances,uint outputSize,uint inputSize,uint timeout,ref SecurityAttributes attributes);
    [DllImport("kernel32.dll",ExactSpelling=true)] private static extern IntPtr LocalFree(IntPtr memory);
    [DllImport("kernel32.dll",SetLastError=true,ExactSpelling=true)]
    [return:MarshalAs(UnmanagedType.Bool)] private static extern bool CancelIoEx(SafePipeHandle handle,IntPtr overlapped);
    private sealed class Edge { internal string Kind;internal bool Neutral; }
    private readonly object gate=new object();
    private readonly Queue<Edge> queued=new Queue<Edge>();
    private readonly AutoResetEvent changed=new AutoResetEvent(false);
    private readonly ManualResetEvent stopped=new ManualResetEvent(false);
    private readonly NamedPipeServerStream pipe;
    private readonly Thread writer;
    private volatile bool connected,neutral;
    private int connectionPending;
    private string failure;
    private long sequence;
    private readonly EmissionOrder emissionOrder=new EmissionOrder();
    private bool disposed,disposalCompleted,ioDrainIncomplete;
    internal string Name { get;private set; }
    internal bool Connected { get { return connected; } }
    internal string Failure { get { lock(gate) return failure; } }
    internal bool TakeConnectionReset() { return Interlocked.Exchange(ref connectionPending,0)!=0; }

    internal static string PipeName(string nonce)
    {
        if(!DailyPaths.ValidNonce(nonce)) throw new ArgumentException("A canonical capture nonce is required.");
        return "InputLift.Fn."+nonce.ToLowerInvariant();
    }
    internal static string Frame(long value,string kind,bool isNeutral)
    {
        if(value<=0)throw new ArgumentOutOfRangeException("value");
        if(kind!="reset" && kind!="down" && kind!="up" && kind!="heartbeat")throw new ArgumentException("Unknown fixed Fn frame kind.");
        if((kind=="reset" || kind=="down") && isNeutral)throw new ArgumentException("Reset and down cannot advertise neutral input.");
        if(kind=="up" && !isNeutral)throw new ArgumentException("Up must advertise recognized neutral input.");
        return "ILFN1 "+value.ToString(CultureInfo.InvariantCulture)+" "+kind+" "+(isNeutral?"1":"0")+"\n";
    }
    internal static string SecurityDescriptor(string ownerSid)
    {
        SecurityIdentifier sid=new SecurityIdentifier(ownerSid);
        if(sid.Value!=ownerSid)throw new ArgumentException("Canonical owner SID required.");
        return "O:BAG:BAD:P(A;;GA;;;SY)(A;;GA;;;BA)(A;;GR;;;"+sid.Value+")";
    }
    internal FnEventPipe(string nonce)
    {
        Name=PipeName(nonce);
        IntPtr descriptor=IntPtr.Zero;uint size;
        SafePipeHandle handle=null;
        try
        {
            if(!ConvertStringSecurityDescriptorToSecurityDescriptorW(SecurityDescriptor(DailyPaths.OwnerSid),1,out descriptor,out size))
                throw new Win32Exception(Marshal.GetLastWin32Error(),"Create fixed Fn pipe security descriptor failed.");
            SecurityAttributes attributes=new SecurityAttributes { Length=Marshal.SizeOf(typeof(SecurityAttributes)),Descriptor=descriptor,Inherit=false };
            handle=CreateNamedPipeW(@"\\.\pipe\"+Name,OutboundOverlappedFirst,RejectRemoteClients,1,4096,0,0,ref attributes);
            if(handle.IsInvalid)throw new Win32Exception(Marshal.GetLastWin32Error(),"Create local outbound first-instance Fn pipe failed.");
            pipe=new NamedPipeServerStream(PipeDirection.Out,true,false,handle);
            handle=null; // Stream owns this handle.
        }
        finally { if(handle!=null)handle.Dispose();if(descriptor!=IntPtr.Zero)LocalFree(descriptor); }
        writer=new Thread(WriteLoop) { IsBackground=true,Name="InputLift fixed Fn pipe" };
        writer.Start();
    }
    // Called from the capture loop. This does not perform pipe I/O or wait for a reader.
    internal void SetNeutral(bool value) { neutral=value; }
    internal void Publish(string kind,bool isNeutral)
    {
        Frame(1,kind,isNeutral); // Validate vocabulary before state mutation.
        lock(gate)
        {
            neutral=isNeutral;
            if(disposed || failure!=null || !connected)return;
            if(queued.Count>=MaximumQueuedEdges) { failure="Fixed Fn pipe queue overflow; input continuity was lost.";stopped.Set();return; }
            queued.Enqueue(new Edge { Kind=kind,Neutral=isNeutral });
            changed.Set(); // Dispose cannot close this event before publication leaves the gate.
        }
    }
    private void Send(string kind,bool isNeutral)
    {
        if(sequence==long.MaxValue)throw new InvalidOperationException("Fixed Fn pipe sequence exhausted.");
        emissionOrder.Accept(kind,isNeutral);
        byte[] bytes=Encoding.ASCII.GetBytes(Frame(++sequence,kind,isNeutral));
        IAsyncResult pending=pipe.BeginWrite(bytes,0,bytes.Length,null,null);
        if(!WaitAndEnd(pending,stopped,CancelPendingIo,pipe.EndWrite,WriteTimeoutMilliseconds,CancellationDrainMilliseconds))
            throw new IOException("Fixed Fn pipe write stopped or exceeded 500 milliseconds.");
    }
    // This policy runs on the sole wire writer. A producer-side baseline is not
    // enough: neutral must occur in stream order before the first admitted down.
    private sealed class EmissionOrder
    {
        internal bool Armed,Held;
        internal void Accept(string kind,bool isNeutral)
        {
            Frame(1,kind,isNeutral);
            if(kind=="reset") { Armed=Held=false;return; }
            if(kind=="down")
            {
                if(!Armed)throw new InvalidOperationException("Fn press preceded its ordered neutral baseline.");
                Held=true;return;
            }
            if(isNeutral) { Armed=true;Held=false; }
        }
    }
    private void WriteLoop()
    {
        try
        {
            IAsyncResult pending=pipe.BeginWaitForConnection(null,null);
            if(!WaitAndEnd(pending,stopped,CancelPendingIo,pipe.EndWaitForConnection,Timeout.Infinite,CancellationDrainMilliseconds))return;
            // A new reader never inherits an existing held state. Capture will
            // require a fresh neutral keyboard report before admitting a down.
            neutral=false;
            Send("reset",false);
            connected=true;Interlocked.Exchange(ref connectionPending,1);
            while(!stopped.WaitOne(0))
            {
                Edge edge=null;
                lock(gate) { if(queued.Count>0)edge=queued.Dequeue(); }
                if(edge!=null) { Send(edge.Kind,edge.Neutral);continue; }
                if(WaitHandle.WaitAny(new WaitHandle[]{stopped,changed},250)==0)break;
                lock(gate) { if(queued.Count>0)continue; }
                Send("heartbeat",neutral);
            }
        }
        catch(Exception error)
        {
            lock(gate)
            {
                if(error is PendingIoNotDrainedException)ioDrainIncomplete=true;
                if(ioDrainIncomplete || !stopped.WaitOne(0))failure=error.GetType().Name+": "+error.Message;
            }
        }
        finally { connected=false;stopped.Set(); }
    }
    private sealed class PendingIoNotDrainedException : IOException
    {
        internal PendingIoNotDrainedException(string message,Exception inner) : base(message+(inner==null?String.Empty:" "+inner.GetType().Name+": "+inner.Message),inner) { }
    }
    private void CancelPendingIo() { CancelIoEx(pipe.SafePipeHandle,IntPtr.Zero); }
    // Framework End methods own the result's event. Its IsCompleted flag can be
    // set BEFORE the IOCP callback signals that event, so it is not a drain proof.
    // Never dispose AsyncWaitHandle ourselves, including on stop or timeout.
    private static bool WaitAndEnd(IAsyncResult pending,WaitHandle stop,Action cancel,Action<IAsyncResult> end,int timeout,int drainTimeout)
    {
        WaitHandle completed=pending.AsyncWaitHandle;
        int selected=WaitHandle.WaitAny(new WaitHandle[]{stop,completed},timeout);
        if(selected!=1)
        {
            Exception cancellationError=null;
            try { cancel(); } catch(Exception error) { cancellationError=error; }
            bool drained;
            try { drained=completed.WaitOne(drainTimeout); }
            catch(Exception error) { throw new PendingIoNotDrainedException("Fixed Fn pipe cancellation completion could not be observed; process exit is required.",error); }
            if(!drained)throw new PendingIoNotDrainedException("Fixed Fn pipe cancellation did not complete within its drain budget; process exit is required.",cancellationError);
        }
        try { end(pending); }
        catch(IOException) { if(selected==1)throw; } // End released the completed result before surfacing cancellation.
        catch(OperationCanceledException) { if(selected==1)throw; } // Framework maps ERROR_OPERATION_ABORTED to this type.
        catch(Exception error) { throw new PendingIoNotDrainedException("Fixed Fn pipe completion cleanup failed; process exit is required.",error); }
        return selected==1;
    }
    private static bool BeginDisposal(ref bool requested,bool completed)
    {
        if(completed)return false;
        if(requested)throw new InvalidOperationException("Fixed Fn pipe cleanup is incomplete; process exit is required.");
        requested=true;return true;
    }
    public void Dispose()
    {
        lock(gate)
        {
            if(!BeginDisposal(ref disposed,disposalCompleted))return;
            neutral=false;stopped.Set();changed.Set();
        }
        CancelPendingIo();
        // Keep the stream open until End has drained the result. Closing it first
        // can make Framework End fail before releasing its completion resources.
        if(!writer.Join(1500))throw new InvalidOperationException("Fixed Fn pipe writer did not stop; process exit is required.");
        if(ioDrainIncomplete)throw new InvalidOperationException("Fixed Fn pipe I/O did not drain; process exit is required. "+Failure);
        pipe.Dispose();
        changed.Dispose();stopped.Dispose();
        lock(gate)disposalCompleted=true;
    }
    private sealed class SyntheticPending : IAsyncResult,IDisposable
    {
        internal readonly ManualResetEvent Signal=new ManualResetEvent(false);
        internal bool EarlyComplete,Ended;
        internal int EndCalls,CompletedReads;
        public object AsyncState { get { return null; } }
        public WaitHandle AsyncWaitHandle { get { return Signal; } }
        public bool CompletedSynchronously { get { return false; } }
        public bool IsCompleted { get { Interlocked.Increment(ref CompletedReads);return EarlyComplete; } }
        internal void End(IAsyncResult result)
        {
            EndCalls++;
            if(!Object.ReferenceEquals(this,result) || !Signal.WaitOne(0))throw new InvalidOperationException("Synthetic End preceded completion.");
            Signal.Close();Ended=true;
        }
        public void Dispose() { Signal.Dispose(); }
    }
    internal static List<string> RunSelfTests(out int count)
    {
        var errors=new List<string>();int checks=0;
        Action<bool,string> check=delegate(bool ok,string text){checks++;if(!ok)errors.Add(text);};
        using(ManualResetEvent stop=new ManualResetEvent(false))
        using(SyntheticPending pending=new SyntheticPending())
        {
            pending.Signal.Set();int cancellations=0;
            bool admitted=WaitAndEnd(pending,stop,delegate { cancellations++; },pending.End,0,0);
            check(admitted && pending.EndCalls==1 && pending.Ended && cancellations==0,"Completed operations pair End exactly once without cancellation.");
        }
        using(ManualResetEvent stop=new ManualResetEvent(true))
        using(ManualResetEvent cancelObserved=new ManualResetEvent(false))
        using(SyntheticPending pending=new SyntheticPending())
        {
            // Match Framework's early IsCompleted=true, delayed event.Set window.
            pending.EarlyComplete=true;Exception operationError=null;int cancellations=0;bool admitted=false;
            Thread operation=new Thread(delegate()
            {
                try { admitted=WaitAndEnd(pending,stop,delegate { cancellations++;cancelObserved.Set(); },pending.End,Timeout.Infinite,3000); }
                catch(Exception error) { operationError=error; }
            }) { IsBackground=true };
            operation.Start();
            try
            {
                if(!cancelObserved.WaitOne(3000))throw new InvalidOperationException("Synthetic cancellation was not requested.");
                check(!operation.Join(50) && pending.EndCalls==0,"An early completion flag cannot admit End or return while its event remains unsignaled.");
            }
            finally
            {
                pending.Signal.Set();
                if(!operation.Join(3000))throw new InvalidOperationException("Synthetic completion did not finish.");
            }
            check(!admitted && pending.EndCalls==1 && pending.Ended && cancellations==1 && operationError==null && pending.CompletedReads==0,"Stop drains delayed signaling without polling IsCompleted or closing its live event.");
        }
        using(ManualResetEvent stop=new ManualResetEvent(false))
        using(SyntheticPending pending=new SyntheticPending())
        {
            int cancellations=0;
            bool admitted=WaitAndEnd(pending,stop,delegate { cancellations++;pending.Signal.Set(); },pending.End,0,1000);
            check(!admitted && cancellations==1 && pending.EndCalls==1,"Write timeout cancels after Begin, drains and pairs End exactly once.");
        }
        using(ManualResetEvent stop=new ManualResetEvent(true))
        using(SyntheticPending pending=new SyntheticPending())
        {
            bool refused=false;try { WaitAndEnd(pending,stop,delegate { },pending.End,0,0); }
            catch(PendingIoNotDrainedException) { refused=true; }
            check(refused && pending.EndCalls==0,"Incomplete cancellation refuses End and cleanup proof.");
            pending.Signal.Set(); // A late completion must still be able to signal.
            check(pending.Signal.WaitOne(0),"Failed drain leaves the pending completion event alive.");
            pending.End(pending);
        }
        using(ManualResetEvent stop=new ManualResetEvent(true))
        using(SyntheticPending pending=new SyntheticPending())
        {
            int endCalls=0;
            bool admitted=WaitAndEnd(pending,stop,delegate { pending.Signal.Set(); },delegate(IAsyncResult result)
            { endCalls++;pending.Signal.Close();throw new IOException("Synthetic operation aborted after End cleanup."); },0,0);
            check(!admitted && endCalls==1,"Expected cancellation is handled only after the completed End call.");
        }
        using(ManualResetEvent stop=new ManualResetEvent(true))
        using(SyntheticPending pending=new SyntheticPending())
        {
            int endCalls=0;
            bool admitted=WaitAndEnd(pending,stop,delegate { pending.Signal.Set(); },delegate(IAsyncResult result)
            { endCalls++;pending.Signal.Close();throw new OperationCanceledException("Synthetic native operation aborted after End cleanup."); },0,0);
            check(!admitted && endCalls==1,"Framework operation-aborted cancellation is clean only after the stopped operation's End call.");
        }
        using(ManualResetEvent stop=new ManualResetEvent(false))
        using(SyntheticPending pending=new SyntheticPending())
        {
            pending.Signal.Set();int endCalls=0;bool surfaced=false;
            try { WaitAndEnd(pending,stop,delegate { },delegate(IAsyncResult result)
            { endCalls++;pending.Signal.Close();throw new OperationCanceledException("Synthetic unsolicited cancellation."); },0,0); }
            catch(OperationCanceledException) { surfaced=true; }
            check(surfaced && endCalls==1,"Unsolicited cancellation still surfaces after End; it is not classified as an undrained operation.");
        }
        bool disposalRequested=false;
        check(BeginDisposal(ref disposalRequested,false),"First disposal owns cleanup.");
        bool repeatedIncompleteRefused=false;try { BeginDisposal(ref disposalRequested,false); }catch(InvalidOperationException) { repeatedIncompleteRefused=true; }
        check(repeatedIncompleteRefused,"A failed or unfinished first disposal cannot turn into successful repeated cleanup.");
        check(!BeginDisposal(ref disposalRequested,true),"Completed disposal is idempotent.");
        check(PipeName("0123456789abcdef0123456789abcdef")=="InputLift.Fn.0123456789abcdef0123456789abcdef","Fixed non-personal pipe namespace.");
        check(Frame(1,"reset",false)=="ILFN1 1 reset 0\n","Reset starts unarmed.");
        check(Frame(2,"down",false)=="ILFN1 2 down 0\n","Down is not neutral.");
        check(Frame(3,"up",true)=="ILFN1 3 up 1\n","Up can advertise neutral.");
        check(Frame(long.MaxValue,"heartbeat",true).Length<64,"Frames remain bounded for Int64 sequences.");
        foreach(string kind in new[]{"command","pressed","down\n1","",null})
        { bool refused=false;try{Frame(1,kind,false);}catch(ArgumentException){refused=true;}check(refused,"No arbitrary output payload or frame kind."); }
        foreach(string kind in new[]{"reset","down"})
        { bool refused=false;try{Frame(1,kind,true);}catch(ArgumentException){refused=true;}check(refused,"Non-neutral edge cannot claim neutral."); }
        bool contradictoryUp=false;try{Frame(1,"up",false);}catch(ArgumentException){contradictoryUp=true;}check(contradictoryUp,"Release cannot contradict neutral input.");
        EmissionOrder order=new EmissionOrder();order.Accept("reset",false);
        bool unarmedRefused=false;try{order.Accept("down",false);}catch(InvalidOperationException){unarmedRefused=true;}
        check(unarmedRefused && !order.Armed && !order.Held,"A down cannot overtake an omitted neutral baseline.");
        order.Accept("heartbeat",true);order.Accept("down",false);order.Accept("heartbeat",false);
        check(order.Armed && order.Held,"Ordered baseline permits an immediate press; held heartbeats preserve it.");
        order.Accept("reset",false);order.Accept("heartbeat",false);
        check(!order.Armed && !order.Held,"Reset invalidates both admission and held state.");
        order.Accept("up",true);order.Accept("down",false);
        check(order.Armed && order.Held,"An observed release establishes a new neutral baseline before the next press.");
        foreach(long value in new[]{0L,-1L})
        { bool refused=false;try{Frame(value,"heartbeat",false);}catch(ArgumentException){refused=true;}check(refused,"Nonpositive sequence refused."); }
        check(SecurityDescriptor("S-1-5-21-1-2-3-1001")=="O:BAG:BAD:P(A;;GA;;;SY)(A;;GA;;;BA)(A;;GR;;;S-1-5-21-1-2-3-1001)","Only selected owner read, administrators and SYSTEM have pipe access.");
        check((OutboundOverlappedFirst&3U)==2U,"Native pipe direction is outbound, not inbound or duplex.");
        check((OutboundOverlappedFirst&0x00080000U)!=0 && RejectRemoteClients==8,"First-instance and remote-client rejection flags fixed.");
        count=checks;return errors;
    }
}
