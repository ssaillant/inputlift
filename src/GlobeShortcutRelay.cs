// Copyright (c) 2026 GlobeRelay contributors
// SPDX-License-Identifier: MIT
// Controlled-test output only: fixed Mouse5 DOWN/UP from filtered Globe edges.
// Caller keeps the cursor on the remote client and serializes edge/abort/snapshot access.
// No hooks, ordinary-input capture, text conversion, packet reader, or network.
// GetAsyncKeyState cannot distinguish a physical Mouse5 from our injected one;
// no other input should be held during this bounded test.
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;

public sealed class ShortcutJournalState
{
    public int Version;
    public string OutputKind;
    public int MouseButton;
    public string Nonce;
    public int ChildPid;
    public long ChildStartUtcTicks;
    public bool PossiblyDown;
    public bool Released;
}

internal sealed class GlobeShortcutRelay
{
    internal enum Stroke { Mouse5Down, Mouse5Up }
    internal delegate SendResult BatchSender(Stroke[] strokes);
    internal struct SendResult
    {
        public uint Inserted;
        public int Error;
        public bool Suppressed;
        public string Stage;
        public bool MouseButtonAttempted;
        // Existing injected senders model a native button call. The owned
        // output adapter below always supplies its authoritative attempt flag.
        internal SendResult(uint inserted, int error) : this(inserted,error,null) { }
        internal SendResult(uint inserted, int error,string stage)
        { Inserted = inserted; Error = error; Suppressed=false; Stage=stage; MouseButtonAttempted=true; }
    }
    internal sealed class RelaySnapshot
    {
        // Chord names are retained for compatibility with the capture controller.
        public bool Initialized, PhysicalPressDown, ChordMayBeDown, JournalArmed, Stopped, Faulted;
        public long DownEdges, UpEdges, DuplicateEdges, SuppressedPresses, ChordsStarted, ChordsReleased;
        public long DownCalls, UpCalls, AbortCalls;
        public uint LastFailedInserted;
        public int LastFailedWin32Error;
        public string LastFailedOutputStage;
        public bool LastFailedMouseButtonAttempted;
        public string LastFailure;
        public int MouseButton;
    }
    internal sealed class RelayFailureException : Exception
    {
        internal RelayFailureException(string message) : base(message) { }
        internal RelayFailureException(string message, Exception inner) : base(message, inner) { }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        internal int X, Y;
        internal uint MouseData, Flags, Time;
        internal UIntPtr ExtraInfo;
    }
    [StructLayout(LayoutKind.Explicit, Size = 40)]
    private struct Input
    {
        [FieldOffset(0)] internal uint Type;
        [FieldOffset(8)] internal MouseInput Mouse;
    }
    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern short GetAsyncKeyState(int key);

    private const int SupportedMouseButton = 5, VkMouse5 = 0x06;
    private const uint XButton2 = 2, XDownFlag = 0x0080, XUpFlag = 0x0100;
    private const ulong InputMarker = OwnedMouseOutput.InputMarker;
    private static readonly Stroke[] DownBatch = { Stroke.Mouse5Down };
    private static readonly Stroke[] UpBatch = { Stroke.Mouse5Up };
    private readonly Func<bool> forbiddenKeyHeld;
    private readonly BatchSender sender;
    private readonly Action<bool> persistArmed;
    private bool initialized, physicalDown, possibleDown, journalArmed, stopped, faulted;
    private bool suppressed, failedReleaseAttempted;
    private long downEdges, upEdges, duplicates, suppressedPresses, chordsStarted, chordsReleased;
    private long downCalls, upCalls, abortCalls;
    private string lastFailure;
    private uint lastFailedInserted;
    private int lastFailedError;
    private string lastFailedOutputStage;
    private bool lastFailedMouseButtonAttempted;

    // Pure dependencies permit self-tests without Windows calls or input reads.
    internal GlobeShortcutRelay(Func<bool> forbiddenKeyHeld, BatchSender sender, Action<bool> persistArmed)
    {
        if (forbiddenKeyHeld == null || sender == null || persistArmed == null)
            throw new ArgumentNullException("Relay dependencies");
        this.forbiddenKeyHeld = forbiddenKeyHeld;
        this.sender = sender;
        this.persistArmed = persistArmed;
    }
    internal int MouseButton { get { return SupportedMouseButton; } }
    internal bool ChordMayBeDown { get { return possibleDown; } }
    internal bool JournalArmed { get { return journalArmed; } }
    internal bool Faulted { get { return faulted; } }

    private static bool CorrectNativeAbi()
    {
        return IntPtr.Size == 8 && Marshal.SizeOf(typeof(Input)) == 40 &&
            Marshal.SizeOf(typeof(MouseInput)) == 32 &&
            Marshal.OffsetOf(typeof(Input), "Mouse").ToInt32() == 8 &&
            Marshal.OffsetOf(typeof(MouseInput), "ExtraInfo").ToInt32() == 24;
    }
    private static void RequireNativeAbi()
    {
        if (!CorrectNativeAbi())
            throw new InvalidOperationException("The Mouse5 injector requires its reviewed x64 INPUT/MOUSEINPUT ABI.");
    }
    private static bool NativeForbiddenKeyHeld()
    {
        // High bits only, limited to known modifiers and the fixed mouse button.
        // Never queries an ordinary text key or captures a stream of input.
        int[] keys = { 0x10, 0x11, 0x12, 0xA0, 0xA1, 0xA2, 0xA3, 0xA4, 0xA5, 0x5B, 0x5C, VkMouse5 };
        foreach (int key in keys) if ((GetAsyncKeyState(key) & 0x8000) != 0) return true;
        return false;
    }
    internal static GlobeShortcutRelay CreateNative(Action<bool> persistArmed,OwnedMouseOutput output)
    {
        RequireNativeAbi();
        if(output==null) throw new ArgumentNullException("output");
        return new GlobeShortcutRelay(delegate() { return NativeForbiddenKeyHeld() || !output.CanStartPress; },
            delegate(Stroke[] strokes) {
                if(strokes==null || strokes.Length!=1 || (strokes[0]!=Stroke.Mouse5Down && strokes[0]!=Stroke.Mouse5Up))
                    throw new ArgumentException("Fixed Mouse5 event required.");
                var sent=output.Send(strokes[0]==Stroke.Mouse5Down);
                SendResult result=AdaptOwnedResult(sent);
                result.Suppressed=strokes[0]==Stroke.Mouse5Down && sent.Inserted==0 && sent.Error==21 &&
                    (sent.Stage=="DeskflowDeskNotVisible" || sent.Stage=="DeskflowDeskChangedBeforeDown");
                return result;
            },persistArmed);
    }
    private static Input[] BuildInputs(Stroke[] strokes)
    {
        if (strokes == null || strokes.Length != 1)
            throw new ArgumentException("Only fixed singleton Mouse5 batches are supported.");
        bool down = strokes[0] == Stroke.Mouse5Down;
        if (!down && strokes[0] != Stroke.Mouse5Up)
            throw new ArgumentException("Refusing an unrelated input batch.");
        return new[] { new Input { Type = 0, Mouse = new MouseInput {
            X = 0, Y = 0, MouseData = XButton2, Flags = down ? XDownFlag : XUpFlag,
            Time = 0, ExtraInfo = new UIntPtr(InputMarker)
        } } };
    }
    // Guard must first validate the full v2 mouse5 journal identity and stop the
    // owned child. This method cannot emit any other button or any DOWN.
    internal static SendResult NativeEmergencyReleaseValidated(int mouseButton)
    {
        if (mouseButton != SupportedMouseButton)
            throw new InvalidOperationException("Emergency release does not match the fixed Mouse5 output; no input sent.");
        RequireNativeAbi();
        using(OwnedMouseOutput output=OwnedMouseOutput.Start())
        {
            var sent=output.Send(false);
            return AdaptOwnedResult(sent);
        }
    }
    private static SendResult AdaptOwnedResult(OwnedMouseOutput.SendResult sent)
    {
        SendResult result=new SendResult(sent.Inserted,sent.Error,sent.Stage);
        result.MouseButtonAttempted=sent.MouseButtonAttempted;
        return result;
    }
    internal static string DescribeOutputFailure(SendResult result)
    {
        // A failed hook refresh/health check has not attempted Mouse5 input.
        // Report the native layer's exact stage rather than attributing every
        // zero count to the button SendInput call.
        return (result.MouseButtonAttempted ? "Mouse5 SendInput attempted" : "Mouse5 output preparation failed; Mouse5 SendInput not attempted")+
            "; "+(result.Stage==null ? "output stage unavailable" : "output stage "+result.Stage)+
            "; Mouse5 events inserted "+result.Inserted+" of 1; Win32 "+result.Error+
            (result.Error==0 ? " (Windows supplied no extended error)" : " ("+new Win32Exception(result.Error).Message+")");
    }
    internal static string ValidateJournalIdentity(ShortcutJournalState state, string nonce, int pid, long startTicks)
    {
        if (state == null) return "Shortcut journal is missing.";
        if (state.Version != 2) return "Shortcut journal version must be 2.";
        if (!string.Equals(state.OutputKind, "mouse5", StringComparison.Ordinal))
            return "Shortcut journal output kind must be mouse5.";
        if (state.MouseButton != SupportedMouseButton) return "Shortcut journal mouse button must be 5.";
        if (!ValidNonce(nonce) || !string.Equals(state.Nonce, nonce, StringComparison.Ordinal))
            return "Shortcut journal nonce mismatch.";
        if (pid <= 0 || startTicks <= 0 || state.ChildPid != pid || state.ChildStartUtcTicks != startTicks)
            return "Shortcut journal process identity mismatch.";
        if (state.PossiblyDown == state.Released) return "Shortcut journal has inconsistent ownership flags.";
        return null;
    }
    private static bool ValidNonce(string nonce)
    {
        if (nonce == null || nonce.Length != 32) return false;
        foreach (char c in nonce)
            if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F'))) return false;
        return true;
    }
    internal void InitializeJournal()
    {
        if (initialized) throw new InvalidOperationException("Shortcut ownership journal was already initialized.");
        persistArmed(false);
        initialized = true;
    }
    private RelayFailureException Failure(string stage, SendResult result, string extra)
    {
        faulted = true;
        lastFailedInserted = result.Inserted;
        lastFailedError = result.Error;
        lastFailedOutputStage = result.Stage;
        lastFailedMouseButtonAttempted = result.MouseButtonAttempted;
        lastFailure = stage + ": " + DescribeOutputFailure(result) +
            (extra == null ? "" : "; " + extra);
        return new RelayFailureException(lastFailure);
    }
    internal void OnGlobeDown()
    {
        if (!initialized) throw new InvalidOperationException("Initialize the durable shortcut journal before enabling the relay.");
        downEdges++;
        if (physicalDown) { duplicates++; return; }
        physicalDown = true;
        if (stopped || faulted) { suppressed = true; suppressedPresses++; return; }
        bool held;
        try { held = forbiddenKeyHeld(); }
        catch (Exception error)
        {
            faulted = true; suppressed = true;
            lastFailure = "Modifier/Mouse5-state check failed: " + error.Message;
            throw new RelayFailureException(lastFailure, error);
        }
        if (held) { suppressed = true; suppressedPresses++; return; }
        // Durable acknowledgement must precede DOWN. An interrupted arm write
        // may conservatively cause the independent guard to issue an extra UP.
        try { persistArmed(true); journalArmed = true; }
        catch (Exception error)
        {
            faulted = true; suppressed = true;
            lastFailure = "Arm shortcut journal failed before input: " + error.Message;
            throw new RelayFailureException(lastFailure, error);
        }
        possibleDown = true;
        downCalls++;
        SendResult result;
        try { result = sender((Stroke[])DownBatch.Clone()); }
        catch (Exception error)
        {
            faulted = true;
            string original = "SendInput DOWN threw: " + error.Message;
            string cleanup = TryReleaseAfterDownFailure();
            lastFailure = original + (cleanup == null ? "" : "; " + cleanup);
            throw new RelayFailureException(lastFailure, error);
        }
        if(result.Suppressed && result.Inserted==0)
        {
            // A normal pointer transition can invalidate the conservative gate
            // before native DOWN. Nothing was inserted; suppress this whole
            // physical press, clear durable ownership, and keep listening.
            possibleDown=false; suppressed=true; suppressedPresses++;
            try { persistArmed(false); journalArmed=false; }
            catch(Exception error)
            { faulted=true; lastFailure="Clear suppressed output journal: "+error.Message; throw new RelayFailureException(lastFailure,error); }
            return;
        }
        if (result.Inserted != 1 || result.Error != 0 || result.Suppressed)
        {
            RelayFailureException failure = Failure("Mouse5 DOWN", result, null);
            string original = lastFailure;
            string cleanup = TryReleaseAfterDownFailure();
            lastFailure = original + (cleanup == null ? "" : "; " + cleanup);
            throw new RelayFailureException(lastFailure, failure);
        }
        chordsStarted++;
    }
    private string TryReleaseAfterDownFailure()
    {
        try { ReleasePossibleChord(); return null; }
        catch (Exception error) { return "Cleanup after failed DOWN: " + error.Message; }
    }
    private void ReleasePossibleChord()
    {
        if (!possibleDown) return;
        // No repeated in-process UP after failure. The armed durable journal
        // permits the independent guard's one separately owned cleanup attempt.
        if (failedReleaseAttempted) return;
        upCalls++;
        SendResult result;
        try { result = sender((Stroke[])UpBatch.Clone()); }
        catch (Exception error)
        {
            failedReleaseAttempted = true; faulted = true;
            lastFailure = "SendInput UP threw: " + error.Message;
            throw new RelayFailureException(lastFailure, error);
        }
        if (result.Inserted != 1 || result.Error != 0)
        {
            failedReleaseAttempted = true;
            throw Failure("Mouse5 UP", result, "ownership journal remains armed for independent cleanup");
        }
        possibleDown = false; chordsReleased++;
        try { persistArmed(false); journalArmed = false; }
        catch (Exception error)
        {
            faulted = true;
            lastFailure = "Mouse5 UP succeeded but clearing the journal failed: " + error.Message;
            throw new RelayFailureException(lastFailure, error);
        }
    }
    internal void OnGlobeUp()
    {
        upEdges++;
        if (!physicalDown) { duplicates++; return; }
        physicalDown = false;
        bool wasSuppressed = suppressed; suppressed = false;
        if (!wasSuppressed) ReleasePossibleChord();
    }
    internal void Abort(string reason)
    {
        abortCalls++; stopped = true; suppressed = true;
        ReleasePossibleChord();
    }
    internal RelaySnapshot Snapshot()
    {
        return new RelaySnapshot { Initialized = initialized, PhysicalPressDown = physicalDown,
            ChordMayBeDown = possibleDown, JournalArmed = journalArmed, Stopped = stopped, Faulted = faulted,
            DownEdges = downEdges, UpEdges = upEdges, DuplicateEdges = duplicates, SuppressedPresses = suppressedPresses,
            ChordsStarted = chordsStarted, ChordsReleased = chordsReleased, DownCalls = downCalls, UpCalls = upCalls,
            AbortCalls = abortCalls, LastFailedInserted = lastFailedInserted, LastFailedWin32Error = lastFailedError,
            LastFailedOutputStage = lastFailedOutputStage, LastFailedMouseButtonAttempted = lastFailedMouseButtonAttempted,
            LastFailure = lastFailure, MouseButton = SupportedMouseButton };
    }

    internal static string[] RunSelfTests(out int count)
    {
        List<string> errors = new List<string>(); int cases = 0;
        Action<bool, string> check = delegate(bool okay, string name) { cases++; if (!okay) errors.Add(name); };
        List<string> order = new List<string>();
        bool blocked = false;
        BatchSender good = delegate(Stroke[] strokes) {
            order.Add(strokes[0] == Stroke.Mouse5Down ? "down" : "up"); return new SendResult(1, 0);
        };
        Action<bool> journal = delegate(bool armed) { order.Add(armed ? "armed" : "clear"); };
        GlobeShortcutRelay relay = new GlobeShortcutRelay(delegate { return blocked; }, good, journal);
        bool refused = false; try { relay.OnGlobeDown(); } catch (InvalidOperationException) { refused = true; }
        check(refused && order.Count == 0, "Uninitialized relay refuses input before any native dependency");
        relay.InitializeJournal(); relay.OnGlobeDown(); relay.OnGlobeDown(); relay.OnGlobeUp(); relay.OnGlobeUp();
        check(string.Join(",", order.ToArray()) == "clear,armed,down,up,clear", "Durable arm precedes DOWN; confirmed UP precedes clear");
        check(relay.Snapshot().DuplicateEdges == 2 && relay.Snapshot().ChordsStarted == 1 && relay.Snapshot().ChordsReleased == 1,
            "Duplicate edges do not replay input");
        check(!relay.ChordMayBeDown && !relay.JournalArmed, "Balanced completion clears ownership");
        check(relay.MouseButton == 5 && relay.Snapshot().MouseButton == 5, "Fixed Mouse5 API and snapshot identity");
        order.Clear(); blocked = true;
        relay.OnGlobeDown(); blocked = false; relay.OnGlobeDown(); relay.OnGlobeUp();
        check(order.Count == 0 && relay.Snapshot().SuppressedPresses == 1, "Held modifier/Mouse5 suppresses the whole physical press");
        relay.OnGlobeDown(); relay.OnGlobeUp();
        check(order.Count == 4, "A released/new press can be accepted after suppression");
        order.Clear(); relay.OnGlobeDown(); relay.Abort("stop"); relay.Abort("duplicate stop"); relay.OnGlobeUp();
        check(string.Join(",", order.ToArray()) == "armed,down,up,clear", "Abort releases once; later UP never replays cleanup");
        order.Clear(); relay.OnGlobeDown(); relay.OnGlobeUp();
        check(order.Count == 0, "Stopped relay never injects new output");
        int gatedCalls=0;
        GlobeShortcutRelay gateRace=new GlobeShortcutRelay(delegate { return false; },delegate(Stroke[] s) {
            gatedCalls++;
            SendResult r=new SendResult(0,21); r.Suppressed=true; return r;
        },delegate(bool a) { });
        gateRace.InitializeJournal(); gateRace.OnGlobeDown(); gateRace.OnGlobeUp();
        check(gatedCalls==1 && !gateRace.Faulted && !gateRace.ChordMayBeDown && !gateRace.JournalArmed && gateRace.Snapshot().SuppressedPresses==1,
            "Focus change before native DOWN suppresses one press without UP or lifetime failure");
        gateRace.OnGlobeDown(); gateRace.OnGlobeUp();
        check(gatedCalls==2 && !gateRace.Faulted,"A later Globe press remains available after a normal focus transition");
        refused = false; try { relay.InitializeJournal(); } catch (InvalidOperationException) { refused = true; }
        check(refused && order.Count == 0, "Repeated journal initialization is refused");

        int sends = 0;
        GlobeShortcutRelay badJournal = new GlobeShortcutRelay(delegate { return false; },
            delegate(Stroke[] s) { sends++; return new SendResult(1, 0); },
            delegate(bool armed) { if (armed) throw new InvalidOperationException("arm test"); });
        badJournal.InitializeJournal(); refused = false;
        try { badJournal.OnGlobeDown(); } catch (RelayFailureException) { refused = true; }
        check(refused && sends == 0 && badJournal.Faulted && !badJournal.ChordMayBeDown,
            "Failed durable arming prevents all DOWN input");
        GlobeShortcutRelay stateFailure = new GlobeShortcutRelay(delegate { throw new InvalidOperationException("state test"); },
            delegate(Stroke[] s) { sends++; return new SendResult(1, 0); }, delegate(bool a) { });
        stateFailure.InitializeJournal(); refused = false;
        try { stateFailure.OnGlobeDown(); } catch (RelayFailureException) { refused = true; }
        check(refused && sends == 0 && stateFailure.Faulted, "State-check failure stops without input");

        foreach (uint invalidInserted in new uint[] { 0, 2 })
        {
            int calls = 0, downs = 0;
            GlobeShortcutRelay failedDown = new GlobeShortcutRelay(delegate { return false; }, delegate(Stroke[] s) {
                calls++; if (s[0] == Stroke.Mouse5Down) downs++;
                return calls == 1 ? new SendResult(invalidInserted, 5) : new SendResult(1, 0);
            }, delegate(bool a) { });
            failedDown.InitializeJournal(); refused = false;
            try { failedDown.OnGlobeDown(); } catch (RelayFailureException) { refused = true; }
            failedDown.OnGlobeUp(); failedDown.OnGlobeDown(); failedDown.OnGlobeUp(); failedDown.Abort("stop");
            check(refused && calls == 2 && downs == 1 && !failedDown.ChordMayBeDown && !failedDown.JournalArmed,
                "Failed DOWN is followed only by one fixed UP, with no later DOWN retry");
            check(failedDown.Snapshot().LastFailedInserted == invalidInserted && failedDown.Snapshot().LastFailedWin32Error == 5 &&
                failedDown.Snapshot().LastFailure.Contains("of 1"), "Failed singleton DOWN preserves count and exact Win32 error");
        }
        int releaseCalls = 0;
        GlobeShortcutRelay badUp = new GlobeShortcutRelay(delegate { return false; }, delegate(Stroke[] s) {
            releaseCalls++; return releaseCalls == 1 ? new SendResult(1, 0) : new SendResult(0, 87);
        }, delegate(bool a) { });
        badUp.InitializeJournal(); badUp.OnGlobeDown(); refused = false;
        try { badUp.OnGlobeUp(); } catch (RelayFailureException) { refused = true; }
        badUp.Abort("stop"); badUp.Abort("again"); badUp.OnGlobeDown(); badUp.OnGlobeUp();
        check(refused && releaseCalls == 2 && badUp.ChordMayBeDown && badUp.JournalArmed && badUp.Faulted,
            "Failed UP stays armed and is not blindly repeated in process");
        check(badUp.Snapshot().LastFailedWin32Error == 87 && badUp.Snapshot().LastFailedInserted == 0,
            "Failed UP native count/error preserved");
        int ackDownCalls=0;
        GlobeShortcutRelay unackedDown=new GlobeShortcutRelay(delegate{return false;},delegate(Stroke[] s) {
            ackDownCalls++; return new SendResult(1,s[0]==Stroke.Mouse5Down?1460:0);
        },delegate(bool a){});
        unackedDown.InitializeJournal();refused=false;
        try { unackedDown.OnGlobeDown(); } catch(RelayFailureException) { refused=true; }
        check(refused && ackDownCalls==2 && unackedDown.Faulted && !unackedDown.ChordMayBeDown && !unackedDown.JournalArmed,
            "Inserted-but-unacknowledged DOWN is a failure followed by one confirmed UP");
        int ackUpCalls=0;
        GlobeShortcutRelay unackedUp=new GlobeShortcutRelay(delegate{return false;},delegate(Stroke[] s) {
            ackUpCalls++;return new SendResult(1,s[0]==Stroke.Mouse5Up?1460:0);
        },delegate(bool a){});
        unackedUp.InitializeJournal();unackedUp.OnGlobeDown();refused=false;
        try { unackedUp.OnGlobeUp(); } catch(RelayFailureException) { refused=true; }
        unackedUp.Abort("test-stop");
        check(refused && ackUpCalls==2 && unackedUp.ChordMayBeDown && unackedUp.JournalArmed,
            "Inserted-but-unacknowledged UP retains ownership for independent recovery");
        OwnedMouseOutput.SendResult healthFailure=new OwnedMouseOutput.SendResult(0,1460,false,"OwnedHookHealthAcknowledgement");
        SendResult adaptedHealthFailure=AdaptOwnedResult(healthFailure);
        check(adaptedHealthFailure.Stage=="OwnedHookHealthAcknowledgement" && !adaptedHealthFailure.MouseButtonAttempted &&
            adaptedHealthFailure.Inserted==0 && adaptedHealthFailure.Error==1460,
            "Shared normal/emergency adapter preserves preflight stage, count, error and absence of native Mouse5 attempt");
        OwnedMouseOutput.SendResult buttonAckFailure=new OwnedMouseOutput.SendResult(1,1460,false,"Mouse5HookAcknowledgement");
        buttonAckFailure.MouseButtonAttempted=true;
        SendResult adaptedAckFailure=AdaptOwnedResult(buttonAckFailure);
        check(adaptedAckFailure.Stage=="Mouse5HookAcknowledgement" && adaptedAckFailure.MouseButtonAttempted &&
            adaptedAckFailure.Inserted==1 && DescribeOutputFailure(adaptedAckFailure).Contains("SendInput attempted"),
            "Inserted-button acknowledgement failure retains explicit native-attempt evidence and exact stage");
        GlobeShortcutRelay failedPreflight=new GlobeShortcutRelay(delegate{return false;},delegate(Stroke[] s) {
            return s[0]==Stroke.Mouse5Down ? adaptedHealthFailure : new SendResult(1,0,"SendInput(Mouse5)");
        },delegate(bool a){});
        failedPreflight.InitializeJournal();string preflightException=null;
        try { failedPreflight.OnGlobeDown(); } catch(RelayFailureException error) { preflightException=error.Message; }
        RelaySnapshot preflightSnapshot=failedPreflight.Snapshot();
        check(preflightException!=null && preflightException.Contains("OwnedHookHealthAcknowledgement") &&
            preflightException.Contains("SendInput not attempted") && preflightSnapshot.LastFailedOutputStage=="OwnedHookHealthAcknowledgement" &&
            !preflightSnapshot.LastFailedMouseButtonAttempted && preflightSnapshot.LastFailure==preflightException,
            "Relay exception and serialized snapshot retain preflight diagnosis without claiming a Mouse5 native call");
        int throwingDownCalls = 0;
        GlobeShortcutRelay throwingDown = new GlobeShortcutRelay(delegate { return false; }, delegate(Stroke[] s) {
            throwingDownCalls++; if (s[0] == Stroke.Mouse5Down) throw new InvalidOperationException("DOWN exception test");
            return new SendResult(1, 0);
        }, delegate(bool a) { });
        throwingDown.InitializeJournal(); refused = false;
        try { throwingDown.OnGlobeDown(); } catch (RelayFailureException) { refused = true; }
        check(refused && throwingDownCalls == 2 && !throwingDown.ChordMayBeDown && !throwingDown.JournalArmed && throwingDown.Faulted,
            "DOWN exception causes one UP cleanup and preserves fault");
        int throwingUpCalls = 0;
        GlobeShortcutRelay throwingUp = new GlobeShortcutRelay(delegate { return false; }, delegate(Stroke[] s) {
            throwingUpCalls++; if (s[0] == Stroke.Mouse5Up) throw new InvalidOperationException("UP exception test");
            return new SendResult(1, 0);
        }, delegate(bool a) { });
        throwingUp.InitializeJournal(); throwingUp.OnGlobeDown(); refused = false;
        try { throwingUp.OnGlobeUp(); } catch (RelayFailureException) { refused = true; }
        throwingUp.Abort("stop");
        check(refused && throwingUpCalls == 2 && throwingUp.ChordMayBeDown && throwingUp.JournalArmed,
            "UP exception stays conservatively armed without in-process replay");
        int bothFailedCalls = 0;
        GlobeShortcutRelay bothFailed = new GlobeShortcutRelay(delegate { return false; }, delegate(Stroke[] s) {
            bothFailedCalls++; return new SendResult(0, 0);
        }, delegate(bool a) { });
        bothFailed.InitializeJournal(); refused = false;
        try { bothFailed.OnGlobeDown(); } catch (RelayFailureException) { refused = true; }
        bothFailed.Abort("stop");
        check(refused && bothFailedCalls == 2 && bothFailed.ChordMayBeDown && bothFailed.JournalArmed &&
            bothFailed.Snapshot().LastFailure.Contains("Cleanup after failed DOWN") &&
            bothFailed.Snapshot().LastFailure.Contains("no extended error"), "Double failure retains both diagnoses and conservative ownership");
        int clearCalls = 0;
        GlobeShortcutRelay badClear = new GlobeShortcutRelay(delegate { return false; }, good, delegate(bool a) {
            if (!a && ++clearCalls > 1) throw new InvalidOperationException("clear test");
        });
        badClear.InitializeJournal(); badClear.OnGlobeDown(); refused = false;
        try { badClear.OnGlobeUp(); } catch (RelayFailureException) { refused = true; }
        check(refused && !badClear.ChordMayBeDown && badClear.JournalArmed && badClear.Faulted,
            "Failed journal clear retains emergency proof after confirmed UP");

        check(CorrectNativeAbi(), "Reviewed x64 INPUT40/MOUSEINPUT32 ABI and pointer offsets");
        check(Marshal.OffsetOf(typeof(MouseInput), "MouseData").ToInt32() == 8 &&
            Marshal.OffsetOf(typeof(MouseInput), "Flags").ToInt32() == 12 &&
            Marshal.OffsetOf(typeof(MouseInput), "Time").ToInt32() == 16, "MOUSEINPUT data/flags/time offsets");
        Input[] down = BuildInputs(DownBatch), up = BuildInputs(UpBatch);
        check(down.Length == 1 && up.Length == 1 && down[0].Type == 0 && up[0].Type == 0 &&
            down[0].Mouse.MouseData == 2 && up[0].Mouse.MouseData == 2, "Singleton mouse-only XBUTTON2 output");
        check(down[0].Mouse.Flags == 0x0080 && up[0].Mouse.Flags == 0x0100,
            "Exact proven XDOWN/XUP path; no move or other buttons");
        check(down[0].Mouse.X == 0 && down[0].Mouse.Y == 0 && up[0].Mouse.X == 0 && up[0].Mouse.Y == 0 &&
            down[0].Mouse.Time == 0 && up[0].Mouse.Time == 0, "No coordinates, movement or fabricated timestamps");
        check(down[0].Mouse.ExtraInfo.ToUInt64() == InputMarker && up[0].Mouse.ExtraInfo.ToUInt64() == InputMarker,
            "Fixed new own marker on both events");
        foreach (Stroke[] invalid in new Stroke[][] { null, new Stroke[0], new[] { Stroke.Mouse5Down, Stroke.Mouse5Up }, new[] { (Stroke)99 } })
        {
            refused = false; try { BuildInputs(invalid); } catch (ArgumentException) { refused = true; }
            check(refused, "Unrelated, empty, multiple and unknown batches rejected");
        }

        string nonce = "0123456789abcdef0123456789abcdef";
        ShortcutJournalState state = new ShortcutJournalState { Version = 2, OutputKind = "mouse5", MouseButton = 5,
            Nonce = nonce, ChildPid = 12, ChildStartUtcTicks = 34, PossiblyDown = true, Released = false };
        check(ValidateJournalIdentity(state, nonce, 12, 34) == null, "Owned armed v2 Mouse5 journal accepted");
        state.PossiblyDown = false; state.Released = true;
        check(ValidateJournalIdentity(state, nonce, 12, 34) == null, "Owned released v2 Mouse5 journal accepted");
        state.Version = 0; check(ValidateJournalIdentity(state, nonce, 12, 34) != null, "Legacy/missing journal version rejected");
        state.Version = 1; check(ValidateJournalIdentity(state, nonce, 12, 34) != null, "Keyboard v1 journal rejected");
        state.Version = 3; check(ValidateJournalIdentity(state, nonce, 12, 34) != null, "Unknown future journal version rejected");
        state.Version = 2;
        foreach (string kind in new[] { null, "", "alt-f13", "Mouse5" })
        {
            state.OutputKind = kind; check(ValidateJournalIdentity(state, nonce, 12, 34) != null, "Wrong or missing exact output kind rejected");
        }
        state.OutputKind = "mouse5";
        foreach (int button in new[] { 0, 4, 6 })
        {
            state.MouseButton = button; check(ValidateJournalIdentity(state, nonce, 12, 34) != null, "Non-Mouse5 journal output rejected");
        }
        state.MouseButton = 5;
        check(ValidateJournalIdentity(null, nonce, 12, 34) != null, "Missing journal rejected");
        check(ValidateJournalIdentity(state, nonce, 13, 34) != null && ValidateJournalIdentity(state, nonce, 12, 35) != null &&
            ValidateJournalIdentity(state, nonce, 0, 34) != null && ValidateJournalIdentity(state, nonce, 12, 0) != null,
            "PID/start-time substitution or missing process identity rejected");
        check(ValidateJournalIdentity(state, "fedcba9876543210fedcba9876543210", 12, 34) != null &&
            ValidateJournalIdentity(state, "bad", 12, 34) != null, "Nonce substitution or malformed nonce rejected");
        state.Released = false; check(ValidateJournalIdentity(state, nonce, 12, 34) != null, "Both ownership flags false rejected");
        state.PossiblyDown = true; state.Released = true;
        check(ValidateJournalIdentity(state, nonce, 12, 34) != null, "Both ownership flags true rejected");
        // Invalid button is rejected before ABI checks or native emission.
        refused = false; try { NativeEmergencyReleaseValidated(4); } catch (InvalidOperationException) { refused = true; }
        check(refused, "Emergency release rejects any non-Mouse5 value before native calls");
        count = cases; return errors.ToArray();
    }
}
