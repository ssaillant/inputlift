// Copyright (c) 2026 GlobeRelay contributors
// SPDX-License-Identifier: MIT
// Persistent selected-field Globe capture. Owned lease/heartbeat, neutral-before-
// rearm, protected status and tagged Mouse5-only output. No ordinary key fields.
// Input output is fixed to that shortcut only. No ordinary key fields, network
// sender, registry writes, packet files, or full-payload copies. Guard required.
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32;

internal static class GlobeEtwFnProbe
{
    private const int FlushIntervalMilliseconds = 100;
    private const uint TraceControlQuery = 0, TraceControlFlush = 3;
    private const uint RealTimeMode = 0x00000100;
    private const uint EventRecordMode = 0x10000000;
    private const ulong PacketKeyword = 0x8000000000000000UL;
    private const string SessionPrefix = "InputLift.Daily.";
    private static readonly Guid Provider = new Guid("8A1F9517-3A8C-4A9E-A018-4F17A200F277");

    [StructLayout(LayoutKind.Explicit, Size = 120)]
    private struct TraceProperties
    {
        [FieldOffset(0)] internal uint WnodeBufferSize;
        [FieldOffset(24)] internal Guid WnodeGuid;
        [FieldOffset(40)] internal uint WnodeClientContext;
        [FieldOffset(44)] internal uint WnodeFlags;
        [FieldOffset(48)] internal uint BufferSize;
        [FieldOffset(52)] internal uint MinimumBuffers;
        [FieldOffset(56)] internal uint MaximumBuffers;
        [FieldOffset(64)] internal uint LogFileMode;
        [FieldOffset(68)] internal uint FlushTimer;
        [FieldOffset(88)] internal uint EventsLost;
        [FieldOffset(100)] internal uint RealTimeBuffersLost;
        [FieldOffset(112)] internal uint LogFileNameOffset;
        [FieldOffset(116)] internal uint LoggerNameOffset;
    }

    [StructLayout(LayoutKind.Explicit, Size = 448)]
    private struct TraceLogFile
    {
        [FieldOffset(0)] internal IntPtr LogFileName;
        [FieldOffset(8)] internal IntPtr LoggerName;
        [FieldOffset(28)] internal uint ProcessTraceMode;
        [FieldOffset(424)] internal IntPtr EventRecordCallback;
        [FieldOffset(440)] internal IntPtr Context;
    }

    [StructLayout(LayoutKind.Explicit, Size = 112)]
    private struct EventRecordHeader
    {
        [FieldOffset(16)] internal long EventTimeFileTime;
        [FieldOffset(24)] internal Guid ProviderId;
        [FieldOffset(40)] internal ushort EventId;
        [FieldOffset(42)] internal byte Version;
        [FieldOffset(48)] internal ulong Keyword;
        [FieldOffset(86)] internal ushort UserDataLength;
        [FieldOffset(96)] internal IntPtr UserData;
    }

    // Explicit packed BIP schema verified against the installed provider manifest.
    // The callback reads only Type and DataLength from this header, never a payload array.
    [StructLayout(LayoutKind.Explicit, Pack = 1, Size = 8)]
    private struct PackedBipHeader
    {
        [FieldOffset(0)] internal byte CommandPacketsAllowed;
        [FieldOffset(1)] internal ushort AclWriteCredits;
        [FieldOffset(3)] internal byte Type;
        [FieldOffset(4)] internal uint DataLength;
    }

    [StructLayout(LayoutKind.Explicit, Size = 16)]
    private struct PropertyDescriptor
    {
        [FieldOffset(0)] internal ulong PropertyName;
        [FieldOffset(8)] internal uint ArrayIndex;
        [FieldOffset(12)] internal uint Reserved;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void EventCallback(IntPtr record);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint StartTraceW(out ulong session, string name, IntPtr properties);
    [DllImport("advapi32.dll", ExactSpelling = true)]
    private static extern uint EnableTraceEx2(ulong session, ref Guid provider, uint control,
        byte level, ulong anyKeyword, ulong allKeyword, uint timeout, IntPtr parameters);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint ControlTraceW(ulong session, string name, IntPtr properties, uint control);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern ulong OpenTraceW(ref TraceLogFile logfile);
    [DllImport("advapi32.dll", ExactSpelling = true)]
    private static extern uint ProcessTrace([In] ulong[] traces, uint count, IntPtr start, IntPtr end);
    [DllImport("advapi32.dll", ExactSpelling = true)]
    private static extern uint CloseTrace(ulong trace);
    [DllImport("tdh.dll", ExactSpelling = true, EntryPoint = "TdhGetProperty")]
    private static extern uint ReadByteProperty(IntPtr record, uint contextCount, IntPtr context,
        uint descriptorCount, ref PropertyDescriptor descriptor, uint size, out byte value);
    [DllImport("tdh.dll", ExactSpelling = true, EntryPoint = "TdhGetProperty")]
    private static extern uint ReadUInt32Property(IntPtr record, uint contextCount, IntPtr context,
        uint descriptorCount, ref PropertyDescriptor descriptor, uint size, out uint value);

    private sealed class Failure
    {
        internal string Stage;
        internal uint Code;
        internal string Message;
    }

    private sealed class RecordedEdge
    {
        internal bool Down;
        internal long EventTime;
        internal long DeliveredTime;
        internal string Reason;
        internal string Source;
        internal bool DiagnosticReset;
    }

    // This pure orchestration function is also used by the live controller.
    // Its injected operation is synthetic in --self-test; no native APIs run.
    private delegate uint SessionOperation(ulong ownedHandle, uint control, out Guid actualGuid);
    private sealed class FlushOutcome
    {
        internal bool QueryAttempted, FlushAttempted, Succeeded;
        internal string Stage, Message;
        internal uint Code;
    }
    private static FlushOutcome AttemptOwnedFlush(bool owns, bool consumerReady, bool stopping,
        ulong ownedHandle, Guid expectedGuid, Func<bool> stopRequested, SessionOperation operation)
    {
        FlushOutcome result=new FlushOutcome();
        if(!owns || !consumerReady || stopping || stopRequested()) return result;
        if(ownedHandle==0 || ownedHandle==ulong.MaxValue || expectedGuid==Guid.Empty)
        { result.Stage="FlushOwnership"; result.Message="Refusing an invalid owned session handle or GUID."; return result; }
        Guid actual;
        result.QueryAttempted=true;
        result.Code=operation(ownedHandle,TraceControlQuery,out actual);
        if(result.Code!=0)
        { result.Stage="ControlTraceW:query-owned-session-before-flush"; return result; }
        if(actual!=expectedGuid)
        { result.Stage="FlushOwnership"; result.Message="Session GUID no longer matches this probe's nonce; no flush sent."; return result; }
        if(stopRequested()) return result;
        result.FlushAttempted=true;
        result.Code=operation(ownedHandle,TraceControlFlush,out actual);
        if(result.Code!=0)
        { result.Stage="ControlTraceW:flush-owned-session"; return result; }
        result.Succeeded=true;
        return result;
    }

    private sealed class Probe : IDisposable
    {
        private readonly object gate = new object();
        private readonly ManualResetEvent stopRequested = new ManualResetEvent(false);
        private readonly List<Failure> failures = new List<Failure>();
        private readonly List<RecordedEdge> edges = new List<RecordedEdge>();
        private readonly HciGlobeDecoder decoder;
        private readonly ConnectionHeaderDiagnostics connectionDiagnostics;
        private readonly BoundedConsoleWriter notifications;
        private readonly CaptureContinuity continuity=new CaptureContinuity();
        private readonly ReconnectGuide reconnectGuide=new ReconnectGuide();
        private int suspendResetPending;
        private bool powerSubscribed;
        private long minimumEventFileTime,preResetEventsDropped;
        private readonly EventCallback callback;
        private readonly string sessionName;
        private readonly Stopwatch elapsed = new Stopwatch();
        private GlobeShortcutRelay relay;
        private OwnedMouseOutput mouseOutput;
        private FnEventPipe eventPipe;
        private readonly string outputMode;
        private BridgeOwnership ownership;
        private bool neutralReady;
        private long retiredDowns,retiredPairs,physicalDownCount,progressSequence,lastStatusMilliseconds=-1000;
        private readonly int workerPid=Process.GetCurrentProcess().Id;
        private readonly long workerStart=Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks;
        private long staleDownsIgnored;
        private bool keyboardAnnounced;
        private bool notificationDrainComplete;
        private ulong session;
        private ulong trace = ulong.MaxValue;
        private IntPtr properties, flushProperties, loggerName, typeName, lengthName;
        private int propertiesAllocation;
        private PropertyDescriptor typeDescriptor, lengthDescriptor;
        private Thread consumer;
        private bool consumerStarted, sessionCreated, ownsSession, providerEnabled;
        private bool stopAttempted, sessionStopped, cleaned, cancelled, failed, consumerJoined;
        private bool everTargetBound, targetBoundBeforeStop, connectionBoundBeforeStop, fnDownBeforeStop, legacyFnDownBeforeStop;
        private long startedTimeFileTime;
        private long providerEvents, packetEvents, schemaRejectedEvents, filteredEvents, edgesDropped;
        private uint processTraceResult, eventsLost, buffersLost;
        private long flushQueries, flushAttempts, flushSuccesses, flushFailures, flushMaxControlMilliseconds;

        internal Probe(byte[] target, string requestedSessionName,string requestedOutputMode)
        {
            decoder = new HciGlobeDecoder(target);
            connectionDiagnostics = new ConnectionHeaderDiagnostics(target);
            notifications = new BoundedConsoleWriter("GlobeRelay keyboard notification");
            callback = OnEvent;
            sessionName = requestedSessionName ?? (SessionPrefix + Guid.NewGuid().ToString("N"));
            outputMode=DailyPaths.NormalizeOutputMode(requestedOutputMode);
        }

        private void Fail(string stage, uint code)
        {
            Fail(stage, code, new Win32Exception(unchecked((int)code)).Message);
        }

        private void Fail(string stage, uint code, string message)
        {
            stopRequested.Set();
            lock (gate)
            {
                failed = true;
                if (failures.Count < 16) failures.Add(new Failure { Stage = stage, Code = code, Message = message });
                AbortShortcut("capture-error");
            }
        }

        // Every caller holds gate; Fail can re-enter this lock on the same thread.
        private void AbortShortcut(string reason)
        {
            if(eventPipe!=null) { eventPipe.SetNeutral(false);eventPipe.Publish("reset",false); }
            if(relay==null) return;
            try { relay.Abort(reason); }
            catch(Exception error)
            {
                failed=true;
                stopRequested.Set();
                if(failures.Count<16) failures.Add(new Failure { Stage="ShortcutRelease",Code=0,
                    Message=error.GetType().Name+": "+error.Message });
            }
        }

        private void ResetRelayAfterNeutral()
        {
            if(outputMode=="events-only")
            {
                // Publish the baseline before a fast following down. Merely
                // setting the periodic-heartbeat snapshot can lose this order.
                if(eventPipe!=null)eventPipe.Publish("heartbeat",true);
                return;
            }
            if(relay!=null)
            {
                if(relay.ChordMayBeDown || relay.JournalArmed)
                    throw new InvalidOperationException("Previous owned Mouse5 output is not fully released; refusing rearm.");
                var old=relay.Snapshot(); retiredDowns+=old.ChordsStarted; retiredPairs+=old.ChordsReleased;
            }
            relay=GlobeShortcutRelay.CreateNative(delegate(bool armed) {
                ownership.Persist(armed,5);
                if(armed && (stopRequested.WaitOne(0) || cleaned || failed || ownership.LeaseEnded))
                    throw new InvalidOperationException("Capture stopped during output ownership persistence.");
            },mouseOutput);
            relay.InitializeJournal();
        }

        private void PublishStatus(bool force)
        {
            long now=elapsed.ElapsedMilliseconds;
            if(!force && now-lastStatusMilliseconds<1000) return;
            object status;
            lock(gate)
            {
                string reconnectState=null;
                if(!failed && !cleaned)
                    reconnectState=reconnectGuide.Observe(decoder.Bound || KeyboardConnectionState.ReadConnected(),now,decoder.Bound);
                string state=failed ? "Error" : cleaned ? "Stopped" : outputMode=="events-only" && (eventPipe==null || !eventPipe.Connected) ? "AwaitConsumer" : !decoder.Bound ? reconnectState :
                    !neutralReady ? "AwaitNeutral" : outputMode=="events-only" ? "EventsReady" : mouseOutput!=null && mouseOutput.CanStartPress ? "Ready" : "PausedOnHost";
                string error=failures.Count==0 ? null : failures[failures.Count-1].Stage+": "+failures[failures.Count-1].Message;
                var current=relay==null ? null : relay.Snapshot();
                status=new { Nonce=SessionGuid(sessionName).ToString("N"),WorkerPid=workerPid,WorkerStartUtcTicks=workerStart,
                    OutputMode=outputMode,PipeName=eventPipe==null?null:eventPipe.Name,
                    ProgressSequence=++progressSequence,State=state,Error=error,
                    PhysicalGlobePresses=physicalDownCount,OutputPresses=retiredDowns+(current==null?0:current.ChordsStarted),
                    OutputPairs=retiredPairs+(current==null?0:current.ChordsReleased),KeyboardBound=decoder.Bound,
                    NeutralConfirmed=neutralReady,RestartRequired=true,UpdatedUtc=DateTime.UtcNow.ToString("o") };
            }
            string path=DailyPaths.FileFor(SessionGuid(sessionName).ToString("N"),".status.json");
            DailyPaths.WriteTextProtected(path,new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(status),false);
            lastStatusMilliseconds=now;
        }

        private void OnPowerModeChanged(object sender,PowerModeChangedEventArgs args)
        { if(args.Mode==PowerModes.Suspend) Interlocked.Exchange(ref suspendResetPending,1); }

        // Caller holds gate. The native active/biased clocks detect a suspend
        // even if the Windows power notification is delayed. Old queued HCI
        // connection headers cannot resurrect a pre-suspend identity binding.
        private void CheckContinuity()
        {
            if(eventPipe!=null)
            {
                if(eventPipe.Failure!=null) { Fail("FnEventPipe",0,eventPipe.Failure);return; }
                if(eventPipe.TakeConnectionReset()) { neutralReady=false;eventPipe.SetNeutral(false); }
            }
            bool clockGap=continuity.Sample();
            bool notified=Interlocked.Exchange(ref suspendResetPending,0)!=0;
            if(!clockGap && !notified) return;
            long now=DateTime.UtcNow.ToFileTimeUtc();
            minimumEventFileTime=now;
            AbortShortcut("system-suspend-or-clock-gap");
            neutralReady=false; keyboardAnnounced=false;
            reconnectGuide.Reset();
            RecordEdges(decoder.Reset(now,"system-suspend-or-clock-gap"),now,true);
        }

        private void RecordEdges(IList<GlobeEdge> incoming, long delivered, bool reset)
        {
            foreach (GlobeEdge edge in incoming)
            {
                if (edges.Count >= 128) { edges.RemoveAt(0); edgesDropped++; }
                edges.Add(new RecordedEdge { Down = edge.Down, EventTime = edge.Timestamp,
                    DeliveredTime = delivered, Reason = edge.Reason, Source = edge.Source, DiagnosticReset = reset });
                if(edge.Source!="vendor-ff01-usage3") continue;
                if(reset || (edge.Reason!="globe-down" && edge.Reason!="globe-up"))
                { AbortShortcut("target-reset-or-disconnect"); neutralReady=false; continue; }
                if(edge.Down) physicalDownCount++;
                if((relay==null && eventPipe==null) || !neutralReady) continue;
                if(edge.Down)
                {
                    if(stopRequested.WaitOne(0) || failed || cleaned) { AbortShortcut("capture-stopping"); continue; }
                    if(ownership.LeaseEnded) { AbortShortcut("lease-ended"); stopRequested.Set(); return; }
                    if(delivered-edge.Timestamp>5000000L) { staleDownsIgnored++; continue; }
                    if(eventPipe!=null) { ownership.RequireActiveLease();eventPipe.Publish("down",false); }
                    else relay.OnGlobeDown();
                }
                else if(eventPipe!=null)eventPipe.Publish("up",true);
                else relay.OnGlobeUp();
            }
        }

        private void OnEvent(IntPtr record)
        {
            try
            {
                EventRecordHeader header = (EventRecordHeader)Marshal.PtrToStructure(record, typeof(EventRecordHeader));
                if (header.ProviderId != Provider) return;
                Interlocked.Increment(ref providerEvents);
                if (header.EventId != 402 || header.Version != 0 || (header.Keyword & PacketKeyword) == 0)
                { Interlocked.Increment(ref filteredEvents); return; }

                byte type;
                uint length;
                uint status = ReadByteProperty(record, 0, IntPtr.Zero, 1, ref typeDescriptor, 1, out type);
                if (status != 0) { Fail("TdhGetProperty:BIP_Type", status); return; }
                status = ReadUInt32Property(record, 0, IntPtr.Zero, 1, ref lengthDescriptor, 4, out length);
                if (status != 0) { Fail("TdhGetProperty:BIP_DataLen", status); return; }

                // Validate the packed schema against TDH before accessing selected packet offsets.
                // Never request BIP_Data or copy the event payload into a managed byte array.
                if (header.UserData == IntPtr.Zero || header.UserDataLength < 8 ||
                    length > (uint)(header.UserDataLength - 8) ||
                    Marshal.ReadByte(header.UserData, 3) != type ||
                    unchecked((uint)Marshal.ReadInt32(header.UserData, 4)) != length)
                { Interlocked.Increment(ref schemaRejectedEvents); Fail("BridgeSchemaRejected",0,"Unexpected ETW schema; shortcut output stopped."); return; }

                Interlocked.Increment(ref packetEvents);
                int packetLength = checked((int)length);
                IntPtr payload = IntPtr.Add(header.UserData, 8);
                Func<int, byte> selectedByteReader = delegate(int offset)
                {
                    if (offset < 0 || offset >= packetLength)
                        throw new ArgumentOutOfRangeException("offset", "Selected packet offset is outside the validated event.");
                    return Marshal.ReadByte(payload, offset);
                };
                long delivered = DateTime.UtcNow.ToFileTimeUtc();
                lock (gate)
                {
                    CheckContinuity();
                    if(header.EventTimeFileTime<minimumEventFileTime) { preResetEventsDropped++; return; }
                    connectionDiagnostics.Observe(type,packetLength,selectedByteReader);
                    bool previouslyBound=decoder.Bound;
                    long previousChannelEpoch=decoder.BoundChannelCount;
                    long acceptedBefore=decoder.AcceptedKeyboardReports;
                    var sourceEdges=decoder.Process(type, packetLength, selectedByteReader, header.EventTimeFileTime);
                    if(SourceEpochChanged(previouslyBound,decoder.Bound,previousChannelEpoch,decoder.BoundChannelCount))
                    { AbortShortcut("keyboard-channel-changed"); neutralReady=false; keyboardAnnounced=false; }
                    if(decoder.Bound && !neutralReady && decoder.AcceptedKeyboardReports>acceptedBefore && !decoder.FnDown && !failed && !cleaned && !ownership.LeaseEnded)
                    {
                        if(outputMode!="events-only" || (eventPipe!=null && eventPipe.Connected))
                        { ResetRelayAfterNeutral(); neutralReady=true; }
                    }
                    RecordEdges(sourceEdges, delivered, false);
                    if (decoder.Bound) everTargetBound = true;
                    if(decoder.Bound && !keyboardAnnounced)
                    {
                        keyboardAnnounced=true;
                        if(!notifications.TryWrite("Keyboard identified. Release Fn to enable mapped input on the remote client."))
                            Fail("KeyboardNotificationOverflow",0,"Keyboard notification queue is unavailable; relay stopped.");
                    }
                }
            }
            catch (Exception error)
            {
                Fail("EventRecordCallback", 0, error.GetType().Name + ": " + error.Message);
            }
        }

        private void Consume(ulong consumerTrace)
        {
            try
            {
                processTraceResult = ProcessTrace(new ulong[] { consumerTrace }, 1, IntPtr.Zero, IntPtr.Zero);
                if (processTraceResult != 0 && !(processTraceResult == 1223 && stopRequested.WaitOne(0)))
                    Fail("ProcessTrace", processTraceResult);
                if (!stopRequested.WaitOne(0))
                    Fail("ProcessTrace", processTraceResult, "Consumer ended before the bounded probe requested stop.");
            }
            catch (Exception error) { Fail("ProcessTrace", 0, error.GetType().Name + ": " + error.Message); }
        }

        internal void Cancel() { cancelled = true; stopRequested.Set(); }

        private uint ControlOwnedSessionForFlush(ulong ownedHandle, uint control, out Guid actualGuid)
        {
            if(control!=TraceControlQuery && control!=TraceControlFlush)
                throw new InvalidOperationException("Flush controller refused an unrelated control operation.");
            // A separate buffer prevents query/flush output from mutating the
            // original StartTrace/Stop properties. No event payload enters it.
            TraceProperties config=CreateControlProperties(sessionName,propertiesAllocation);
            Marshal.StructureToPtr(config,flushProperties,false);
            // Passing NULL name chooses the successfully created handle, not
            // a name that a different session could later reuse.
            uint status=ControlTraceW(ownedHandle,null,flushProperties,control);
            actualGuid=Guid.Empty;
            if(status==0)
            {
                TraceProperties observed=(TraceProperties)Marshal.PtrToStructure(flushProperties,typeof(TraceProperties));
                actualGuid=observed.WnodeGuid;
                if(observed.EventsLost!=0 || observed.RealTimeBuffersLost!=0)
                    Fail("BridgeTraceLoss",0,"ETW reported lost events="+observed.EventsLost+", lost real-time buffers="+observed.RealTimeBuffersLost+".");
            }
            return status;
        }

        private void FlushOnce()
        {
            Stopwatch controlTime=Stopwatch.StartNew();
            FlushOutcome result=AttemptOwnedFlush(ownsSession,consumerStarted,cleaned || stopAttempted || sessionStopped,
                session,SessionGuid(sessionName),delegate() { return stopRequested.WaitOne(0); },ControlOwnedSessionForFlush);
            controlTime.Stop();
            if(result.QueryAttempted) flushQueries++;
            if(result.FlushAttempted) flushAttempts++;
            if(result.Succeeded) flushSuccesses++;
            flushMaxControlMilliseconds=Math.Max(flushMaxControlMilliseconds,controlTime.ElapsedMilliseconds);
            if(result.Stage!=null)
            {
                flushFailures++;
                if(result.Message==null) Fail(result.Stage,result.Code);
                else Fail(result.Stage,result.Code,result.Message);
            }
        }

        internal void Run()
        {
            try
            {
                string[] abi = ValidateAbi();
                if (abi.Length != 0) throw new InvalidOperationException(string.Join("; ", abi));
                DailyPaths.RequireInstalledCodePaths();
                DailyPaths.RequireConfiguredOutputMode(outputMode);
                ownership=new BridgeOwnership(sessionName,outputMode);
                if(outputMode=="events-only")eventPipe=new FnEventPipe(SessionGuid(sessionName).ToString("N"));
                else mouseOutput=OwnedMouseOutput.Start();
                if(outputMode!="events-only")ResetRelayAfterNeutral();
                lock(gate) { continuity.Sample(); }
                SystemEvents.PowerModeChanged+=OnPowerModeChanged; powerSubscribed=true;
                int allocation = checked(120 + (sessionName.Length + 1) * 2);
                propertiesAllocation=allocation;
                properties = Marshal.AllocHGlobal(allocation);
                flushProperties = Marshal.AllocHGlobal(allocation);
                for (int i = 0; i < allocation; i++) { Marshal.WriteByte(properties, i, 0); Marshal.WriteByte(flushProperties,i,0); }
                TraceProperties config = CreateTraceProperties(sessionName, allocation);
                Marshal.StructureToPtr(config, properties, false);
                // This copy is only our own session-name configuration, never event data.
                byte[] ownName = Encoding.Unicode.GetBytes(sessionName + "\0");
                Marshal.Copy(ownName, 0, IntPtr.Add(properties, 120), ownName.Length);
                Marshal.Copy(ownName,0,IntPtr.Add(flushProperties,120),ownName.Length);
                if(ownership.LeaseEnded) { stopRequested.Set(); return; }
                uint status = StartTraceW(out session, sessionName, properties);
                if (status != 0) { Fail("StartTraceW", status); return; }
                ownsSession = sessionCreated = true;
                // Ownership acknowledgement precedes all provider configuration.
                Console.WriteLine("{\"state\":\"session-created\",\"ownedSessionCreated\":true,\"ownedSessionName\":" + Json(sessionName) + "}");
                Guid provider = Provider;
                status = EnableTraceEx2(session, ref provider, 1, 5, PacketKeyword, 0, 1000, IntPtr.Zero);
                if (status != 0) { Fail("EnableTraceEx2:enable", status); return; }
                providerEnabled = true;

                typeName = Marshal.StringToHGlobalUni("BIP_Type");
                lengthName = Marshal.StringToHGlobalUni("BIP_DataLen");
                typeDescriptor = new PropertyDescriptor { PropertyName = unchecked((ulong)typeName.ToInt64()), ArrayIndex = uint.MaxValue };
                lengthDescriptor = new PropertyDescriptor { PropertyName = unchecked((ulong)lengthName.ToInt64()), ArrayIndex = uint.MaxValue };
                loggerName = Marshal.StringToHGlobalUni(sessionName);
                TraceLogFile logfile = new TraceLogFile {
                    LoggerName = loggerName, LogFileName = IntPtr.Zero,
                    ProcessTraceMode = RealTimeMode | EventRecordMode,
                    EventRecordCallback = Marshal.GetFunctionPointerForDelegate(callback)
                };
                trace = OpenTraceW(ref logfile);
                if (trace == ulong.MaxValue) { Fail("OpenTraceW", unchecked((uint)Marshal.GetLastWin32Error())); return; }
                ulong consumerTrace = trace;
                consumer = new Thread(delegate() { Consume(consumerTrace); });
                consumer.IsBackground = true;
                consumer.Name = "GlobeRelay selected-field Fn diagnostic";
                startedTimeFileTime = DateTime.UtcNow.ToFileTimeUtc();
                elapsed.Start();
                consumer.Start();
                consumerStarted = true;
                Console.WriteLine("{\"state\":\"ready\",\"ownedSessionName\":" + Json(sessionName) + "}");
                Console.WriteLine("Listening continuously. If the keyboard is already connected, switch it OFF, wait for Windows to disconnect it, then ON and tap Shift. The tray shows actual readiness.");
                PublishStatus(true);
                // Only this controller thread flushes. Cleanup is sequential in
                // finally, after this loop: there is no Timer callback that can
                // race session stop or access freed properties.
                while(!stopRequested.WaitOne(0))
                {
                    if(ownership.LeaseEnded) { stopRequested.Set(); break; }
                    if(stopRequested.WaitOne(FlushIntervalMilliseconds)) break;
                    lock(gate) { CheckContinuity(); }
                    FlushOnce();
                    PublishStatus(false);
                }
            }
            catch (Exception error) { Fail("Probe", 0, error.GetType().Name + ": " + error.Message); }
            finally
            {
                elapsed.Stop(); Cleanup();
                notificationDrainComplete=notifications.Complete(1000);
                try { PublishStatus(true); }
                catch(Exception error) { Fail("WriteFinalStatus",0,error.GetType().Name+": "+error.Message); }
            }
        }

        private void Cleanup()
        {
            if (cleaned) return;
            cleaned = true;
            stopRequested.Set();
            if(powerSubscribed)
            { try { SystemEvents.PowerModeChanged-=OnPowerModeChanged; } catch(Exception error) { Fail("PowerNotificationCleanup",0,error.Message); } powerSubscribed=false; }
            lock(gate) { AbortShortcut("capture-cleanup"); }
            if (ownsSession)
            {
                if (providerEnabled)
                {
                    Guid provider = Provider;
                    uint disabled = EnableTraceEx2(session, ref provider, 0, 0, 0, 0, 1000, IntPtr.Zero);
                    if (disabled != 0) Fail("EnableTraceEx2:disable", disabled);
                    providerEnabled = false;
                }
                stopAttempted = true;
                uint stopped = ControlTraceW(session, sessionName, properties, 1);
                if (stopped != 0) Fail("ControlTraceW:stop-owned-session", stopped);
                else
                {
                    sessionStopped = true;
                    TraceProperties final = (TraceProperties)Marshal.PtrToStructure(properties, typeof(TraceProperties));
                    eventsLost = final.EventsLost;
                    buffersLost = final.RealTimeBuffersLost;
                }
                ownsSession = false;
            }
            if (trace != ulong.MaxValue)
            {
                uint closed = CloseTrace(trace);
                if (closed != 0 && closed != 7007) Fail("CloseTrace", closed);
                trace = ulong.MaxValue;
            }
            consumerJoined = !consumerStarted || consumer.Join(5000);
            if (!consumerJoined)
            {
                Fail("Cleanup", 0, "Consumer did not exit within 5 seconds; process exit is required.");
                return; // Keep callback property names alive until process exit.
            }
            lock (gate)
            {
                AbortShortcut("consumer-stopped");
                targetBoundBeforeStop = decoder.Bound;
                connectionBoundBeforeStop = decoder.ConnectionBound;
                fnDownBeforeStop = decoder.FnDown;
                legacyFnDownBeforeStop = decoder.LegacyFnDown;
                long now = DateTime.UtcNow.ToFileTimeUtc();
                RecordEdges(decoder.Reset(now, "ProbeStopped"), now, true);
            }
            Free(ref typeName); Free(ref lengthName); Free(ref loggerName); Free(ref flushProperties); Free(ref properties);
            if(ownership!=null)
            { try { ownership.Dispose(); } catch(Exception error) { Fail("CloseOwnershipLease",0,error.Message); } }
            if(mouseOutput!=null)
            { try { mouseOutput.Dispose(); } catch(Exception error) { Fail("CloseOwnedMouseHook",0,error.Message); } }
            if(eventPipe!=null)
            { try { eventPipe.Dispose(); } catch(Exception error) { Fail("CloseFnEventPipe",0,error.Message); } }
            GC.KeepAlive(callback);
        }

        private static void Free(ref IntPtr memory)
        {
            if (memory == IntPtr.Zero) return;
            Marshal.FreeHGlobal(memory);
            memory = IntPtr.Zero;
        }

        internal int ExitCode { get { return failed ? 1 : (cancelled ? 130 : 0); } }

        internal string Summary()
        {
            lock (gate)
            {
                StringBuilder json = new StringBuilder("{\"mode\":\"daily-globe-capture\",\"status\":");
                json.Append(Json(failed ? "error" : (cancelled ? "cancelled" : "ok")));
                json.Append(",\"outputMode\":").Append(Json(outputMode));
                GlobeShortcutRelay.RelaySnapshot relayState=relay==null ? null : relay.Snapshot();
                json.Append(",\"phase\":3,\"relayConfigured\":").Append(Bool(relay!=null));
                json.Append(",\"transportReady\":").Append(Bool(consumerStarted));
                json.Append(",\"exerciseStatus\":").Append(Json(relayState!=null && relayState.ChordsReleased>0 ? "mouse5-pairs-emitted" : "not-exercised"));
                json.Append(",\"packetPayloadCopied\":false,\"textKeySlotsRead\":false");
                json.Append(",\"shortcutState\":").Append(relay==null ? "null" : new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(relay.Snapshot()));
                json.Append(",\"staleDownsIgnored\":").Append(staleDownsIgnored);
                json.Append(",\"preResetEventsDropped\":").Append(preResetEventsDropped);
                json.Append(",\"selectedPacketFieldsRead\":").Append(packetEvents > 0 ? "true" : "false");
                json.Append(",\"sessionCreated\":").Append(Bool(sessionCreated));
                json.Append(",\"traceStarted\":").Append(Bool(consumerStarted));
                json.Append(",\"sessionStopAttempted\":").Append(Bool(stopAttempted));
                json.Append(",\"ownedSessionStopped\":").Append(Bool(sessionStopped));
                json.Append(",\"consumerJoined\":").Append(Bool(consumerJoined));
                json.Append(",\"sessionName\":").Append(Json(sessionName));
                json.Append(",\"variant\":\"daily\",\"flushIntervalMilliseconds\":").Append(FlushIntervalMilliseconds);
                json.Append(",\"notificationDrainComplete\":").Append(Bool(notificationDrainComplete));
                json.Append(",\"notificationDrops\":").Append(notifications.DroppedCount);
                json.Append(",\"connectionHeaderDiagnostics\":").Append(new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(connectionDiagnostics.Snapshot()));
                json.Append(",\"flushOwnershipQueries\":").Append(flushQueries).Append(",\"flushAttempts\":").Append(flushAttempts);
                json.Append(",\"flushSuccesses\":").Append(flushSuccesses).Append(",\"flushFailures\":").Append(flushFailures);
                json.Append(",\"flushMaxControlMilliseconds\":").Append(flushMaxControlMilliseconds);
                json.Append(",\"durationLimitSeconds\":null,\"elapsedMilliseconds\":").Append(elapsed.ElapsedMilliseconds);
                json.Append(",\"physicalGlobePresses\":").Append(physicalDownCount).Append(",\"outputPairs\":").Append(retiredPairs+(relayState==null?0:relayState.ChordsReleased));
                json.Append(",\"providerEvents\":").Append(providerEvents).Append(",\"packetEvents\":").Append(packetEvents);
                json.Append(",\"schemaRejectedEvents\":").Append(schemaRejectedEvents).Append(",\"filteredEvents\":").Append(filteredEvents);
                json.Append(",\"eventsLost\":").Append(eventsLost).Append(",\"realTimeBuffersLost\":").Append(buffersLost);
                json.Append(",\"processTraceResult\":").Append(processTraceResult);
                json.Append(",\"targetBound\":").Append(Bool(targetBoundBeforeStop));
                json.Append(",\"targetConnectionBound\":").Append(Bool(connectionBoundBeforeStop));
                json.Append(",\"targetEverBound\":").Append(Bool(everTargetBound));
                json.Append(",\"fnDownBeforeStop\":").Append(Bool(fnDownBeforeStop));
                json.Append(",\"legacyFnDownBeforeStop\":").Append(Bool(legacyFnDownBeforeStop));
                json.Append(",\"connectionCount\":").Append(decoder.ConnectionCount);
                json.Append(",\"disconnectionCount\":").Append(decoder.DisconnectionCount);
                json.Append(",\"rejectedPackets\":").Append(decoder.RejectedPackets);
                json.Append(",\"fragmentedPackets\":").Append(decoder.FragmentedPackets);
                json.Append(",\"signalingPackets\":").Append(decoder.SignalingPackets);
                json.Append(",\"boundChannelCount\":").Append(decoder.BoundChannelCount);
                json.Append(",\"acceptedKeyboardReports\":").Append(decoder.AcceptedKeyboardReports);
                json.Append(",\"unsupportedCount\":").Append(schemaRejectedEvents + decoder.RejectedPackets);
                json.Append(",\"boundTargetHidShapeEvents\":").Append(decoder.HidShapeEvents);
                json.Append(",\"hidShapeBucketsDropped\":").Append(decoder.HidShapeBucketsDropped);
                json.Append(",\"boundTargetHidShapes\":[");
                IList<GlobeHidShapeCount> shapes = decoder.GetHidShapeCounts();
                for (int i = 0; i < shapes.Count; i++)
                {
                    if (i != 0) json.Append(',');
                    GlobeHidShapeCount shape = shapes[i];
                    json.Append("{\"packetLength\":").Append(shape.PacketLength);
                    json.Append(",\"aclDataLength\":").Append(shape.AclDataLength);
                    json.Append(",\"l2capLength\":").Append(shape.L2capLength);
                    json.Append(",\"hidpTag\":").Append(shape.HidpTag);
                    json.Append(",\"reason\":").Append(Json(shape.Reason));
                    json.Append(",\"count\":").Append(shape.Count).Append('}');
                }
                json.Append("],\"boundTargetHidRejections\":[");
                IList<GlobeRejectionCount> rejections = decoder.GetHidRejectionCounts();
                for (int i = 0; i < rejections.Count; i++)
                {
                    if (i != 0) json.Append(',');
                    GlobeRejectionCount rejection = rejections[i];
                    json.Append("{\"reason\":").Append(Json(rejection.Reason));
                    json.Append(",\"count\":").Append(rejection.Count).Append('}');
                }
                json.Append("],\"edgesDropped\":").Append(edgesDropped).Append(",\"fnEdges\":[");
                for (int i = 0; i < edges.Count; i++)
                {
                    if (i != 0) json.Append(',');
                    RecordedEdge edge = edges[i];
                    json.Append("{\"down\":").Append(Bool(edge.Down));
                    json.Append(",\"eventTimeFileTime\":").Append(edge.EventTime);
                    json.Append(",\"eventElapsedMilliseconds\":").Append(((edge.EventTime - startedTimeFileTime) / 10000.0).ToString("F3", CultureInfo.InvariantCulture));
                    json.Append(",\"deliveryDelayMilliseconds\":").Append(((edge.DeliveredTime - edge.EventTime) / 10000.0).ToString("F3", CultureInfo.InvariantCulture));
                    json.Append(",\"diagnosticReset\":").Append(Bool(edge.DiagnosticReset));
                    json.Append(",\"source\":").Append(Json(edge.Source));
                    json.Append(",\"reason\":").Append(Json(edge.Reason)).Append('}');
                }
                json.Append("],\"errors\":[");
                for (int i = 0; i < failures.Count; i++)
                {
                    if (i != 0) json.Append(',');
                    Failure error = failures[i];
                    json.Append("{\"stage\":").Append(Json(error.Stage)).Append(",\"win32Code\":").Append(error.Code);
                    json.Append(",\"message\":").Append(Json(error.Message)).Append('}');
                }
                return json.Append("]}").ToString();
            }
        }

        public void Dispose() { Cleanup(); notifications.Complete(100); }
    }

    private static string Bool(bool value) { return value ? "true" : "false"; }
    private static bool SourceEpochChanged(bool wasBound,bool nowBound,long before,long after)
    { return (wasBound && !nowBound) || before!=after; }
    private static string Json(string value)
    {
        StringBuilder result = new StringBuilder("\"");
        foreach (char c in value)
        {
            if (c == '"') result.Append("\\\"");
            else if (c == '\\') result.Append("\\\\");
            else if (c < 32) result.Append("\\u").Append(((int)c).ToString("x4"));
            else result.Append(c);
        }
        return result.Append('"').ToString();
    }

    private static void Check(List<string> failures, string name, int actual, int expected)
    {
        if (actual != expected) failures.Add(name + ": expected " + expected + ", actual " + actual + ".");
    }
    private static int Offset(Type type, string field) { return Marshal.OffsetOf(type, field).ToInt32(); }

    private static string[] ValidateAbi()
    {
        List<string> failures = new List<string>();
        if (IntPtr.Size != 8) failures.Add("Requires a 64-bit process.");
        Check(failures, "TraceProperties size", Marshal.SizeOf(typeof(TraceProperties)), 120);
        Check(failures, "LoggerNameOffset", Offset(typeof(TraceProperties), "LoggerNameOffset"), 116);
        Check(failures, "TraceLogFile size", Marshal.SizeOf(typeof(TraceLogFile)), 448);
        Check(failures, "LoggerName", Offset(typeof(TraceLogFile), "LoggerName"), 8);
        Check(failures, "ProcessTraceMode", Offset(typeof(TraceLogFile), "ProcessTraceMode"), 28);
        Check(failures, "EventRecordCallback", Offset(typeof(TraceLogFile), "EventRecordCallback"), 424);
        Check(failures, "EventRecordHeader size", Marshal.SizeOf(typeof(EventRecordHeader)), 112);
        Check(failures, "EventTimeFileTime", Offset(typeof(EventRecordHeader), "EventTimeFileTime"), 16);
        Check(failures, "ProviderId", Offset(typeof(EventRecordHeader), "ProviderId"), 24);
        Check(failures, "UserDataLength", Offset(typeof(EventRecordHeader), "UserDataLength"), 86);
        Check(failures, "UserData", Offset(typeof(EventRecordHeader), "UserData"), 96);
        Check(failures, "PropertyDescriptor size", Marshal.SizeOf(typeof(PropertyDescriptor)), 16);
        Check(failures, "PackedBipHeader size", Marshal.SizeOf(typeof(PackedBipHeader)), 8);
        Check(failures, "BIP_Type", Offset(typeof(PackedBipHeader), "Type"), 3);
        Check(failures, "BIP_DataLen", Offset(typeof(PackedBipHeader), "DataLength"), 4);
        if (Json("\"\\\r\n") != "\"\\\"\\\\\\u000d\\u000a\"") failures.Add("JSON escaping failed.");
        return failures.ToArray();
    }

    private static byte[] ParseAddress(string text)
    {
        // A normalized explicit address is required. It is never included in output.
        if (text == null || text.Length != 12) throw new ArgumentException("Use exactly 12 hexadecimal digits for --keyboard-address.");
        byte[] result = new byte[6];
        for (int i = 0; i < result.Length; i++)
            if (!byte.TryParse(text.Substring(i * 2, 2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out result[i]))
                throw new ArgumentException("Use exactly 12 hexadecimal digits for --keyboard-address.");
        return result;
    }

    private static string ParseSessionName(string text)
    {
        Guid nonce;
        if (text == null || !text.StartsWith(SessionPrefix, StringComparison.Ordinal) ||
            text.Length != SessionPrefix.Length + 32 ||
            !Guid.TryParseExact(text.Substring(SessionPrefix.Length), "N", out nonce))
            throw new ArgumentException("Session name must be GlobeRelay.FnProbe. followed by exactly 32 hexadecimal digits.");
        return text;
    }

    private static Guid SessionGuid(string name)
    {
        ParseSessionName(name);
        return Guid.ParseExact(name.Substring(SessionPrefix.Length), "N");
    }

    private static TraceProperties CreateTraceProperties(string name, int allocation)
    {
        return new TraceProperties {
            WnodeBufferSize = checked((uint)allocation), WnodeGuid = SessionGuid(name), WnodeClientContext = 2,
            WnodeFlags = 0x00020000, BufferSize = 32, MinimumBuffers = 2, MaximumBuffers = 8,
            LogFileMode = RealTimeMode, FlushTimer = 1, LoggerNameOffset = 120, LogFileNameOffset = 0
        };
    }

    private static TraceProperties CreateControlProperties(string name, int allocation)
    {
        return new TraceProperties {
            WnodeBufferSize=checked((uint)allocation), WnodeGuid=SessionGuid(name), WnodeFlags=0x00020000,
            LoggerNameOffset=120, LogFileNameOffset=0
        };
    }

    private static string[] RunLowLatencyTests(out int count)
    {
        List<string> failures=new List<string>();
        int cases=0;
        Action<bool,string> check=delegate(bool okay,string testName) { cases++;if(!okay)failures.Add("Low-latency: "+testName); };
        Guid expected=Guid.ParseExact("0123456789abcdef0123456789abcdef","N");
        const ulong handle=12345;
        Func<bool> notStopped=delegate() { return false; };
        int calls=0;
        List<uint> controls=new List<uint>();
        SessionOperation success=delegate(ulong selected,uint code,out Guid actual) {
            calls++;controls.Add(code);actual=expected;
            if(selected!=handle) throw new InvalidOperationException("Wrong session handle reached synthetic operation.");
            return 0;
        };
        FlushOutcome result=AttemptOwnedFlush(false,true,false,handle,expected,notStopped,success);
        check(calls==0 && !result.QueryAttempted && !result.FlushAttempted,"A pre-existing/unowned session is never queried or flushed");
        result=AttemptOwnedFlush(true,false,false,handle,expected,notStopped,success);
        check(calls==0 && !result.FlushAttempted,"No flush before the consumer starts");
        result=AttemptOwnedFlush(true,true,true,handle,expected,notStopped,success);
        check(calls==0 && !result.FlushAttempted,"No query/flush after cleanup/stop begins");
        result=AttemptOwnedFlush(true,true,false,handle,expected,delegate(){return true;},success);
        check(calls==0 && !result.FlushAttempted,"Cancellation skips query and flush");
        result=AttemptOwnedFlush(true,true,false,0,expected,notStopped,success);
        check(calls==0 && result.Stage=="FlushOwnership","Zero handle is rejected without a native call");
        result=AttemptOwnedFlush(true,true,false,ulong.MaxValue,expected,notStopped,success);
        check(calls==0 && result.Stage=="FlushOwnership","Invalid handle is rejected without a native call");
        result=AttemptOwnedFlush(true,true,false,handle,Guid.Empty,notStopped,success);
        check(calls==0 && result.Stage=="FlushOwnership","Empty ownership GUID is rejected without a native call");
        result=AttemptOwnedFlush(true,true,false,handle,expected,notStopped,success);
        check(result.Succeeded && calls==2 && controls[0]==TraceControlQuery && controls[1]==TraceControlFlush,
            "Valid ownership queries then flushes exactly once using the owned handle");
        calls=0;
        result=AttemptOwnedFlush(true,true,false,handle,expected,notStopped,
            delegate(ulong selected,uint code,out Guid actual) { calls++;actual=Guid.NewGuid();return 0; });
        check(calls==1 && result.Stage=="FlushOwnership" && !result.FlushAttempted,"A reused/wrong GUID is never flushed");
        calls=0;
        result=AttemptOwnedFlush(true,true,false,handle,expected,notStopped,
            delegate(ulong selected,uint code,out Guid actual) { calls++;actual=Guid.Empty;return 4201; });
        check(calls==1 && result.Code==4201 && result.Stage=="ControlTraceW:query-owned-session-before-flush" && !result.FlushAttempted,
            "Guardian/external session stop returns its exact query error without a flush");
        bool cancelledBetween=false;
        calls=0;
        result=AttemptOwnedFlush(true,true,false,handle,expected,delegate(){return cancelledBetween;},
            delegate(ulong selected,uint code,out Guid actual) { calls++;actual=expected;cancelledBetween=true;return 0; });
        check(calls==1 && result.QueryAttempted && !result.FlushAttempted,"Cancellation after query prevents flush");
        calls=0;
        result=AttemptOwnedFlush(true,true,false,handle,expected,notStopped,
            delegate(ulong selected,uint code,out Guid actual) { calls++;actual=expected;return code==TraceControlQuery?0u:87u; });
        check(calls==2 && result.FlushAttempted && !result.Succeeded && result.Code==87 && result.Stage=="ControlTraceW:flush-owned-session",
            "A failed flush preserves its exact native error");
        calls=0;
        result=AttemptOwnedFlush(true,true,false,handle,expected,delegate(){return true;},success);
        check(calls==0,"A stopped controller cannot issue another query/flush after an earlier failure");
        string name=SessionPrefix+expected.ToString("N");
        TraceProperties config=CreateControlProperties(name,256);
        check(config.WnodeGuid==expected && config.WnodeBufferSize==256 && config.WnodeFlags==0x20000 && config.LoggerNameOffset==120,
            "Query/flush configuration identifies only the owned nonce session");
        check(config.LogFileNameOffset==0 && config.LogFileMode==0 && config.FlushTimer==0 && config.BufferSize==0,
            "Control properties cannot create a packet file or change a session's configuration");
        check(FlushIntervalMilliseconds>=50 && FlushIntervalMilliseconds<=100 && TraceControlFlush==3,
            "Requested flush interval and documented flush code remain fixed");
        check(CreateTraceProperties(name,256).BufferSize==32 && CreateTraceProperties(name,256).FlushTimer==1,
            "Buffer sizing/default timer stay unchanged to isolate explicit flushing");
        count=cases;
        return failures.ToArray();
    }

    private static int Main(string[] args)
    {
        if (args.Length == 1 && args[0] == "--self-test")
        {
            List<string> failures = new List<string>(ValidateAbi());
            if(!SourceEpochChanged(true,true,1,2) || !SourceEpochChanged(true,false,1,1) ||
                !SourceEpochChanged(false,true,0,1) || SourceEpochChanged(true,true,2,2))
                failures.Add("Every HID channel epoch change requires a new physical neutral report, including an empty-edge rebound.");
            int latencyTests=0, relayTests=0, headerTests=0, consoleTests=0, outputTests=0, continuityTests=0, reconnectTests=0, linkTests=0, eventPipeTests=0;
            try { failures.AddRange(FnEventPipe.RunSelfTests(out eventPipeTests)); }
            catch(Exception error) { failures.Add("Fixed Fn event pipe pure tests: "+error.Message); }
            try { failures.AddRange(ReconnectGuide.RunSelfTests(out reconnectTests)); failures.AddRange(KeyboardConnectionState.RunSelfTests(out linkTests)); }
            catch(Exception error) { failures.Add("Reconnect guide pure tests: "+error.Message); }
            try { failures.AddRange(CaptureContinuity.RunSelfTests(out continuityTests)); }
            catch(Exception error) { failures.Add("Capture continuity pure tests: "+error.Message); }
            try { failures.AddRange(ConnectionHeaderDiagnostics.RunSelfTests(out headerTests)); }
            catch(Exception error) { failures.Add("Header diagnostics pure tests: "+error.Message); }
            try { failures.AddRange(OwnedMouseOutput.RunSelfTests(out outputTests)); }
            catch(Exception error) { failures.Add("Owned output pure tests: "+error.Message); }
            try { failures.AddRange(BoundedConsoleWriter.RunSelfTests(out consoleTests)); }
            catch(Exception error) { failures.Add("Bounded output pure tests: "+error.Message); }
            try { failures.AddRange(GlobeShortcutRelay.RunSelfTests(out relayTests)); }
            catch(Exception error) { failures.Add("Relay pure tests: "+error.GetType().Name+": "+error.Message); }
            try { failures.AddRange(RunLowLatencyTests(out latencyTests)); }
            catch(Exception error) { failures.Add("Low-latency synthetic tests failed: "+error.GetType().Name+": "+error.Message); }
            try { failures.AddRange(GlobePacketDecoderTests.Run()); }
            catch (Exception error) { failures.Add("Synthetic decoder tests failed: " + error.GetType().Name + ": " + error.Message); }
            try
            {
                byte[] parsed = ParseAddress("010203040506");
                if (parsed[0] != 1 || parsed[5] != 6) failures.Add("Address parsing order failed.");
            }
            catch (Exception error) { failures.Add("Address parsing failed: " + error.GetType().Name); }
            try
            {
                string valid = SessionPrefix + "0123456789abcdef0123456789abcdef";
                if (ParseSessionName(valid) != valid) failures.Add("Session name validation changed the owned name.");
                Guid expectedGuid = Guid.ParseExact("0123456789abcdef0123456789abcdef", "N");
                TraceProperties planned = CreateTraceProperties(valid, 256);
                if (SessionGuid(valid) != expectedGuid || planned.WnodeGuid != expectedGuid)
                    failures.Add("Trace configuration GUID does not match the validated session nonce.");
                if (planned.WnodeBufferSize != 256 || planned.WnodeFlags != 0x00020000 || planned.LogFileNameOffset != 0)
                    failures.Add("Trace configuration violated the owned real-time/no-file setup.");
                string[] invalid = new string[] { "Other.FnProbe.0123456789abcdef0123456789abcdef",
                    SessionPrefix + "0123456789abcdef0123456789abcdeg", SessionPrefix + "{0123456789abcdef0123456789abcdef}" };
                foreach (string candidate in invalid)
                {
                    bool refused = false;
                    try { ParseSessionName(candidate); } catch (ArgumentException) { refused = true; }
                    if (!refused) failures.Add("Invalid session name was accepted.");
                }
            }
            catch (Exception error) { failures.Add("Session name validation failed: " + error.GetType().Name); }
            StringBuilder json = new StringBuilder("{\"mode\":\"self-test\",\"status\":");
            json.Append(Json(failures.Count == 0 ? "ok" : "error"));
            json.Append(",\"phase\":2,\"traceStarted\":false,\"injectionEnabled\":false,\"livePacketDataRead\":false,\"pointerBytes\":").Append(IntPtr.Size);
            json.Append(",\"decoderTestCount\":").Append(GlobePacketDecoderTests.TestCount);
            json.Append(",\"headerTestCount\":").Append(headerTests).Append(",\"ownedOutputTestCount\":").Append(outputTests).Append(",\"consoleTestCount\":").Append(consoleTests);
            json.Append(",\"continuityTestCount\":").Append(continuityTests);
            json.Append(",\"eventPipeTestCount\":").Append(eventPipeTests);
            json.Append(",\"reconnectGuideTestCount\":").Append(reconnectTests).Append(",\"linkStateTestCount\":").Append(linkTests);
            json.Append(",\"lowLatencyTestCount\":").Append(latencyTests).Append(",\"relayTestCount\":").Append(relayTests).Append(",\"nativeFlushCalls\":0,\"nativeInputCalls\":0");
            json.Append(",\"errors\":[");
            for (int i = 0; i < failures.Count; i++) { if (i != 0) json.Append(','); json.Append(Json(failures[i])); }
            Console.WriteLine(json.Append("]}").ToString());
            return failures.Count == 0 ? 0 : 1;
        }
        if ((args.Length != 5 && args.Length != 7) || args[0] != "--run-daily" || args[1] != "--keyboard-address" ||
            args[3] != "--session-name" || (args.Length==7 && args[5]!="--output-mode"))
        {
            Console.WriteLine("{\"status\":\"not-started\",\"traceStarted\":false,\"injectionEnabled\":false,\"error\":\"Use --self-test. Live mode requires the installed daily supervisor and persistent-logging approval.\"}");
            return 64;
        }
        byte[] target;
        string requestedSessionName = null;
        string requestedOutputMode="deskflow";
        try
        {
            DailyPaths.RequireConfiguredKeyboard(args[2]);
            target = ParseAddress(args[2]);
            requestedSessionName = ParseSessionName(args[4]);
            if(args.Length==7)requestedOutputMode=DailyPaths.NormalizeOutputMode(args[6]);
            DailyPaths.RequireConfiguredOutputMode(requestedOutputMode);
        }
        catch (Exception error)
        {
            Console.WriteLine("{\"status\":\"not-started\",\"traceStarted\":false,\"injectionEnabled\":false,\"error\":" + Json(error.Message) + "}");
            return 64;
        }
        using (Probe probe = new Probe(target, requestedSessionName,requestedOutputMode))
        {
            ConsoleCancelEventHandler cancel = delegate(object sender, ConsoleCancelEventArgs e) { e.Cancel = true; probe.Cancel(); };
            Console.CancelKeyPress += cancel;
            try { probe.Run(); }
            finally { Console.CancelKeyPress -= cancel; }
            Console.WriteLine("Mapped input stopped. Finish any active remote action; the tray reports cleanup and restart requirements.");
            Console.WriteLine(probe.Summary());
            return probe.ExitCode;
        }
    }
}
