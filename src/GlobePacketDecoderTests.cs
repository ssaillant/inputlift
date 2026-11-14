// Copyright (c) 2026 GlobeRelay contributors
// SPDX-License-Identifier: MIT
// Synthetic fixtures only. No native APIs, traces, device access or key injection.
using System;
using System.Collections.Generic;

internal static class GlobePacketDecoderTests
{
    private static readonly byte[] Target = { 0x10, 0x20, 0x30, 0x40, 0x50, 0x60 };
    private const ushort Handle = 0x0123;
    private const ushort Local = 0x0040;
    private const ushort Remote = 0x0041;
    internal static int TestCount { get; private set; }

    internal static string[] Run()
    {
        List<string> failures = new List<string>();
        TestCount = 0;
        Test(failures, "constructor validation and address clone", TestConstructor);
        Test(failures, "wrong device cannot bind", TestWrongDevice);
        Test(failures, "failed/non-ACL connection cannot bind", TestInvalidConnection);
        Test(failures, "reserved HCI handles cannot bind", TestReservedHandle);
        Test(failures, "connection alone cannot bind a keyboard channel", TestNoShapeBinding);
        Test(failures, "host-initiated interrupt channel", TestHostInitiated);
        Test(failures, "keyboard-initiated interrupt channel", TestPeerInitiated);
        Test(failures, "pending response followed by success", TestPendingResponse);
        Test(failures, "success response requires zero status", TestSuccessStatus);
        Test(failures, "response identifier and SCID matching", TestWrongResponse);
        Test(failures, "reused/rejected signaling IDs cannot bind stale requests", TestStaleRequest);
        Test(failures, "HID control PSM is not interrupt PSM", TestControlPsm);
        Test(failures, "invalid dynamic CIDs cannot bind", TestInvalidCid);
        Test(failures, "wrong handle and CID do not expose Fn", TestWrongHandleAndCid);
        Test(failures, "false baseline then exact down/up transitions", TestTransitions);
        Test(failures, "ordinary key bytes never read", TestIgnoredKeyBytes);
        Test(failures, "unrelated special bits are ignored", TestOtherSpecialBits);
        Test(failures, "wrong HID transaction/report rejected before Fn", TestWrongReport);
        Test(failures, "invalid lengths and bounded readers", TestInvalidLengths);
        Test(failures, "continuation/reserved boundary flags rejected", TestFragments);
        Test(failures, "broadcast and incoming PB0 rejected", TestInvalidAclFlags);
        Test(failures, "matching disconnect releases and invalidates", TestDisconnect);
        Test(failures, "unrelated disconnect leaves binding intact", TestOtherDisconnect);
        Test(failures, "handle reuse releases and invalidates", TestHandleReuse);
        Test(failures, "reconnect clears pending transactions", TestReconnect);
        Test(failures, "multiple signaling commands parsed", TestMultipleCommands);
        Test(failures, "keyboard-shaped CID1 never reads key slots", TestMislabeledKeyboard);
        Test(failures, "explicit event-loss reset releases", TestReset);
        Test(failures, "channel disconnection releases", TestChannelDisconnect);
        Test(failures, "truncated signaling command rejected", TestTruncatedSignaling);
        Test(failures, "malformed trailing command cannot partially bind", TestAtomicSignaling);
        Test(failures, "reader guard is active", TestGuard);
        Test(failures, "unknown HID shapes never read byte9 or later", TestUnknownHidShapes);
        Test(failures, "shape counters exclude unrelated channels and directions", TestShapeScope);
        Test(failures, "shape counters retain only bounded aggregate snapshots", TestShapeBoundsAndSnapshots);
        Test(failures, "shape diagnostics identify structural failures", TestShapeReasons);
        Test(failures, "vendor Fn fields emit independent source-specific transitions", TestIndependentFnSources);
        Test(failures, "both held Fn sources reset with balanced releases", TestBothSourcesReset);
        Test(failures, "legacy Fn field ignores every other bit", TestLegacyFnMask);
        Test(failures, "disconnect balances both held Fn sources", TestBothSourcesDisconnect);
        return failures.ToArray();
    }

    private static void Test(List<string> failures, string name, Action action)
    {
        TestCount++;
        try { action(); }
        catch (Exception error) { failures.Add(name + ": " + error.GetType().Name + ": " + error.Message); }
    }
    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
    private static HciGlobeDecoder Connected()
    {
        HciGlobeDecoder decoder = new HciGlobeDecoder(Target);
        Feed(decoder, 2, ConnectedEvent(Target, Handle), 1);
        Assert(decoder.ConnectionBound && !decoder.Bound, "Expected only the target HCI connection to be bound.");
        return decoder;
    }
    private static HciGlobeDecoder Ready()
    {
        HciGlobeDecoder decoder = Connected();
        HostBind(decoder);
        Assert(decoder.Bound, "Expected an interrupt channel binding.");
        return decoder;
    }
    private static void HostBind(HciGlobeDecoder decoder)
    {
        Feed(decoder, 4, Signal(4, Handle, Request(9, 0x0013, Local)), 2);
        Feed(decoder, 3, Signal(3, Handle, Response(9, Remote, Local, 0)), 3);
    }
    private static IList<GlobeEdge> Feed(HciGlobeDecoder decoder, byte type, byte[] packet, long timestamp)
    {
        Func<int, byte> read = delegate(int index) {
            if (index < 0 || index >= packet.Length) throw new InvalidOperationException("Reader was accessed outside the fixture.");
            // Device metadata verifies packet11 bit01 as FF00/usage3 Fn. The
            // ordinary modifier10 and key slots12..17 remain forbidden.
            if (packet.Length == 19 && (index == 10 || (index >= 12 && index <= 17)))
                throw new InvalidOperationException("Decoder accessed forbidden ordinary-key byte " + index + ".");
            return packet[index];
        };
        return decoder.Process(type, packet.Length, read, timestamp);
    }
    private static void Edge(IList<GlobeEdge> edges, bool down, long timestamp, string reason,
        string source = "vendor-ff01-usage3")
    {
        Assert(edges.Count == 1, "Expected exactly one edge.");
        Assert(edges[0].Down == down && edges[0].Timestamp == timestamp, "Wrong direction or timestamp.");
        Assert(edges[0].Source == source, "Wrong Fn source.");
        if (reason != null) Assert(edges[0].Reason == reason, "Wrong edge reason.");
    }
    private static void Word(byte[] packet, int index, ushort value)
    {
        packet[index] = (byte)value;
        packet[index + 1] = (byte)(value >> 8);
    }
    private static byte[] ConnectedEvent(byte[] address, ushort handle)
    {
        byte[] packet = new byte[13];
        packet[0] = 0x03; packet[1] = 11;
        Word(packet, 3, handle);
        for (int i = 0; i < 6; i++) packet[5 + i] = address[5 - i];
        packet[11] = 1;
        return packet;
    }
    private static byte[] DisconnectedEvent(ushort handle)
    {
        byte[] packet = { 0x05, 4, 0, 0, 0, 0x13 };
        Word(packet, 3, handle);
        return packet;
    }
    private static byte[] Acl(byte type, ushort handle, ushort cid, byte[] payload)
    {
        byte[] packet = new byte[8 + payload.Length];
        Word(packet, 0, (ushort)(handle | (type == 3 ? 0x2000 : 0)));
        Word(packet, 2, (ushort)(4 + payload.Length));
        Word(packet, 4, (ushort)payload.Length);
        Word(packet, 6, cid);
        for (int i = 0; i < payload.Length; i++) packet[8 + i] = payload[i];
        return packet;
    }
    private static byte[] Signal(byte type, ushort handle, params byte[][] commands)
    {
        int length = 0;
        for (int i = 0; i < commands.Length; i++) length += commands[i].Length;
        byte[] payload = new byte[length];
        int position = 0;
        for (int i = 0; i < commands.Length; i++)
            for (int j = 0; j < commands[i].Length; j++) payload[position++] = commands[i][j];
        return Acl(type, handle, 1, payload);
    }
    private static byte[] Request(byte id, ushort psm, ushort scid)
    {
        byte[] command = { 2, id, 4, 0, 0, 0, 0, 0 };
        Word(command, 4, psm); Word(command, 6, scid);
        return command;
    }
    private static byte[] Response(byte id, ushort dcid, ushort scid, ushort result)
    {
        byte[] command = { 3, id, 8, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
        Word(command, 4, dcid); Word(command, 6, scid); Word(command, 8, result);
        return command;
    }
    private static byte[] Keyboard(byte special)
    {
        byte[] body = { 0xA1, 0x01, 0xF0, 0, 4, 5, 6, 7, 8, 9, special };
        return Acl(3, Handle, Local, body);
    }

    private static void TestConstructor()
    {
        bool nullRejected = false, wrongLengthRejected = false;
        try { new HciGlobeDecoder(null); } catch (ArgumentNullException) { nullRejected = true; }
        try { new HciGlobeDecoder(new byte[5]); } catch (ArgumentException) { wrongLengthRejected = true; }
        Assert(nullRejected && wrongLengthRejected, "Invalid addresses were accepted.");
        byte[] address = (byte[])Target.Clone();
        HciGlobeDecoder decoder = new HciGlobeDecoder(address);
        address[0] ^= 0xFF;
        Feed(decoder, 2, ConnectedEvent(Target, Handle), 1);
        Assert(decoder.ConnectionBound, "Constructor did not retain its own address copy.");
    }
    private static void TestWrongDevice()
    {
        HciGlobeDecoder decoder = new HciGlobeDecoder(Target);
        byte[] other = (byte[])Target.Clone(); other[0] ^= 0xFF;
        Feed(decoder, 2, ConnectedEvent(other, Handle), 1);
        Assert(!decoder.ConnectionBound && decoder.ConnectionCount == 0, "An unrelated device bound.");
    }
    private static void TestInvalidConnection()
    {
        HciGlobeDecoder decoder = new HciGlobeDecoder(Target);
        byte[] failed = ConnectedEvent(Target, Handle); failed[2] = 4;
        Feed(decoder, 2, failed, 1);
        byte[] sco = ConnectedEvent(Target, Handle); sco[11] = 0;
        Feed(decoder, 2, sco, 2);
        Assert(!decoder.ConnectionBound, "Failed/SCO connection bound.");
    }
    private static void TestNoShapeBinding()
    {
        HciGlobeDecoder decoder = Connected();
        Assert(Feed(decoder, 3, Keyboard(2), 2).Count == 0 && !decoder.Bound, "Report shape alone created a binding.");
    }
    private static void TestReservedHandle()
    {
        HciGlobeDecoder decoder = new HciGlobeDecoder(Target);
        Feed(decoder, 2, ConnectedEvent(Target, 0x0F00), 1);
        Feed(decoder, 2, ConnectedEvent(Target, 0x0FFF), 2);
        Assert(!decoder.ConnectionBound, "A reserved connection handle was accepted.");
        Feed(decoder, 2, ConnectedEvent(Target, 0x0EFF), 3);
        Assert(decoder.ConnectionBound, "Highest valid connection handle was rejected.");
    }
    private static void TestHostInitiated()
    {
        HciGlobeDecoder decoder = Ready();
        Edge(Feed(decoder, 3, Keyboard(2), 4), true, 4, "globe-down");
        Assert(decoder.BoundChannelCount == 1, "Channel success was not counted once.");
    }
    private static void TestPeerInitiated()
    {
        HciGlobeDecoder decoder = Connected();
        Feed(decoder, 3, Signal(3, Handle, Request(6, 0x0013, Remote)), 2);
        Feed(decoder, 4, Signal(4, Handle, Response(6, Local, Remote, 0)), 3);
        Assert(decoder.Bound, "Keyboard-initiated channel did not bind.");
        Edge(Feed(decoder, 3, Keyboard(2), 4), true, 4, null);
    }
    private static void TestPendingResponse()
    {
        HciGlobeDecoder decoder = Connected();
        Feed(decoder, 4, Signal(4, Handle, Request(9, 0x0013, Local)), 2);
        Feed(decoder, 3, Signal(3, Handle, Response(9, 0, Local, 1)), 3);
        Assert(!decoder.Bound, "Pending result bound a channel.");
        Feed(decoder, 3, Signal(3, Handle, Response(9, Remote, Local, 0)), 4);
        Assert(decoder.Bound, "Final success after pending was lost.");
    }
    private static void TestWrongResponse()
    {
        HciGlobeDecoder decoder = Connected();
        Feed(decoder, 4, Signal(4, Handle, Request(9, 0x0013, Local)), 2);
        Feed(decoder, 3, Signal(3, Handle, Response(8, Remote, Local, 0)), 3);
        Feed(decoder, 3, Signal(3, Handle, Response(9, Remote, 0x0050, 0)), 4);
        Assert(!decoder.Bound, "Wrong identifier/SCID response bound.");
        Feed(decoder, 3, Signal(3, Handle, Response(9, Remote, Local, 0)), 5);
        Assert(decoder.Bound, "Correct response no longer matched.");
    }
    private static void TestSuccessStatus()
    {
        HciGlobeDecoder decoder = Connected();
        Feed(decoder, 4, Signal(4, Handle, Request(9, 0x0013, Local)), 2);
        byte[] response = Response(9, Remote, Local, 0);
        Word(response, 10, 1);
        Feed(decoder, 3, Signal(3, Handle, response), 3);
        Assert(!decoder.Bound, "Success with an inconsistent nonzero status bound a channel.");
    }
    private static void TestControlPsm()
    {
        HciGlobeDecoder decoder = Connected();
        Feed(decoder, 4, Signal(4, Handle, Request(9, 0x0011, Local)), 2);
        Feed(decoder, 3, Signal(3, Handle, Response(9, Remote, Local, 0)), 3);
        Assert(!decoder.Bound, "HID control channel was treated as interrupt.");
    }
    private static void TestStaleRequest()
    {
        HciGlobeDecoder decoder = Connected();
        Feed(decoder, 4, Signal(4, Handle, Request(9, 0x0013, Local)), 2);
        Feed(decoder, 4, Signal(4, Handle, Request(9, 0x0011, Local)), 3);
        Feed(decoder, 3, Signal(3, Handle, Response(9, Remote, Local, 0)), 4);
        Assert(!decoder.Bound, "Reused host request ID kept a stale interrupt request.");
        Feed(decoder, 3, Signal(3, Handle, Request(6, 0x0013, Remote)), 5);
        Feed(decoder, 3, Signal(3, Handle, Request(6, 0x0011, Remote)), 6);
        Feed(decoder, 4, Signal(4, Handle, Response(6, Local, Remote, 0)), 7);
        Assert(!decoder.Bound, "Reused peer request ID kept a stale interrupt request.");
        byte[] rejectHost = { 1, 9, 2, 0, 0, 0 };
        Feed(decoder, 4, Signal(4, Handle, Request(9, 0x0013, Local)), 8);
        Feed(decoder, 3, Signal(3, Handle, rejectHost), 9);
        Feed(decoder, 3, Signal(3, Handle, Response(9, Remote, Local, 0)), 10);
        Assert(!decoder.Bound, "Rejected host request remained pending.");
        byte[] rejectPeer = { 1, 6, 2, 0, 0, 0 };
        Feed(decoder, 3, Signal(3, Handle, Request(6, 0x0013, Remote)), 11);
        Feed(decoder, 4, Signal(4, Handle, rejectPeer), 12);
        Feed(decoder, 4, Signal(4, Handle, Response(6, Local, Remote, 0)), 13);
        Assert(!decoder.Bound, "Rejected peer request remained pending.");
    }
    private static void TestInvalidCid()
    {
        HciGlobeDecoder decoder = Connected();
        Feed(decoder, 4, Signal(4, Handle, Request(9, 0x0013, 1)), 2);
        Feed(decoder, 3, Signal(3, Handle, Response(9, Remote, 1, 0)), 3);
        Assert(!decoder.Bound, "Reserved source CID bound.");
        Feed(decoder, 4, Signal(4, Handle, Request(9, 0x0013, Local)), 4);
        Feed(decoder, 3, Signal(3, Handle, Response(9, 2, Local, 0)), 5);
        Assert(!decoder.Bound, "Reserved destination CID bound.");
    }
    private static void TestWrongHandleAndCid()
    {
        HciGlobeDecoder decoder = Ready();
        byte[] wrongHandle = Keyboard(2); Word(wrongHandle, 0, 0x2456);
        byte[] wrongCid = Keyboard(2); Word(wrongCid, 6, 0x0050);
        Assert(Feed(decoder, 3, wrongHandle, 4).Count == 0, "Wrong handle yielded an edge.");
        Assert(Feed(decoder, 3, wrongCid, 5).Count == 0 && !decoder.FnDown, "Wrong CID yielded an edge.");
        Assert(decoder.AcceptedKeyboardReports == 0, "Unrelated report was accepted.");
    }
    private static void TestTransitions()
    {
        HciGlobeDecoder decoder = Ready();
        Assert(Feed(decoder, 3, Keyboard(0), 100).Count == 0, "False baseline emitted an edge.");
        Edge(Feed(decoder, 3, Keyboard(2), 101), true, 101, "globe-down");
        Assert(Feed(decoder, 3, Keyboard(2), 102).Count == 0, "Held Fn repeated down.");
        Edge(Feed(decoder, 3, Keyboard(0), 103), false, 103, "globe-up");
        Assert(Feed(decoder, 3, Keyboard(0), 104).Count == 0, "Released Fn repeated up.");
        Assert(decoder.AcceptedKeyboardReports == 5, "Report count is incorrect.");
    }
    private static void TestIgnoredKeyBytes()
    {
        HciGlobeDecoder decoder = Ready();
        for (int i = 0; i < 10; i++)
        {
            byte[] packet = Keyboard((byte)(i % 2 == 0 ? 2 : 0));
            for (int j = 10; j <= 17; j++)
                if (j != 11) packet[j] = (byte)(i * 19 + j);
            Edge(Feed(decoder, 3, packet, i + 50), i % 2 == 0, i + 50, null);
        }
    }
    private static void TestOtherSpecialBits()
    {
        HciGlobeDecoder decoder = Ready();
        Assert(Feed(decoder, 3, Keyboard(0x04), 4).Count == 0, "Lock bit was mistaken for Fn.");
        Edge(Feed(decoder, 3, Keyboard(0xFE), 5), true, 5, null);
        Edge(Feed(decoder, 3, Keyboard(0xFC), 6), false, 6, null);
    }
    private static void TestWrongReport()
    {
        HciGlobeDecoder decoder = Ready();
        byte[] wrongTransaction = Keyboard(2); wrongTransaction[8] = 0xA2;
        byte[] wrongId = Keyboard(2); wrongId[9] = 3;
        Assert(Feed(decoder, 3, wrongTransaction, 4).Count == 0, "Wrong HIDP transaction accepted.");
        Assert(Feed(decoder, 3, wrongId, 5).Count == 0, "Wrong report ID accepted.");
        Assert(decoder.AcceptedKeyboardReports == 0, "Rejected report was counted.");
    }
    private static void TestInvalidLengths()
    {
        HciGlobeDecoder decoder = Ready();
        byte[] hci = Keyboard(2); Word(hci, 2, 16);
        byte[] l2cap = Keyboard(2); Word(l2cap, 4, 12);
        Assert(Feed(decoder, 3, hci, 4).Count == 0, "Bad HCI length accepted.");
        Assert(Feed(decoder, 3, l2cap, 5).Count == 0, "Bad L2CAP length accepted.");
        for (int length = 0; length < 8; length++)
            Assert(Feed(decoder, 3, new byte[length], 6).Count == 0, "Truncated ACL accepted.");
        Assert(Feed(decoder, 2, new byte[] { 3 }, 7).Count == 0, "Truncated event accepted.");
        Assert(decoder.Process(3, -1, delegate(int index) { throw new InvalidOperationException("Negative-length fixture was read."); }, 8).Count == 0, "Negative length accepted.");
    }
    private static void TestFragments()
    {
        HciGlobeDecoder decoder = Ready();
        byte[] continuation = Keyboard(2); Word(continuation, 0, (ushort)(Handle | 0x1000));
        byte[] reserved = Keyboard(2); Word(reserved, 0, (ushort)(Handle | 0x3000));
        byte[] incomplete = Keyboard(2); Word(incomplete, 4, 12);
        Assert(Feed(decoder, 3, continuation, 4).Count == 0, "Continuation accepted.");
        Assert(Feed(decoder, 3, reserved, 5).Count == 0, "Reserved PB accepted.");
        Assert(Feed(decoder, 3, incomplete, 6).Count == 0, "Incomplete L2CAP accepted.");
        Assert(decoder.FragmentedPackets == 3 && !decoder.FnDown, "Fragments not counted or changed state.");
    }
    private static void TestInvalidAclFlags()
    {
        HciGlobeDecoder decoder = Ready();
        byte[] broadcast = Keyboard(2); Word(broadcast, 0, (ushort)(Handle | 0x6000));
        byte[] incomingPb0 = Keyboard(2); Word(incomingPb0, 0, Handle);
        Assert(Feed(decoder, 3, broadcast, 4).Count == 0, "Broadcast keyboard data accepted.");
        Assert(Feed(decoder, 3, incomingPb0, 5).Count == 0, "Reserved incoming boundary accepted.");
    }
    private static void TestDisconnect()
    {
        HciGlobeDecoder decoder = Ready();
        Feed(decoder, 3, Keyboard(2), 4);
        Edge(Feed(decoder, 2, DisconnectedEvent(Handle), 5), false, 5, "disconnect");
        Assert(!decoder.ConnectionBound && !decoder.Bound && !decoder.FnDown, "Disconnect left stale state.");
        Assert(decoder.DisconnectionCount == 1, "Disconnect count is incorrect.");
        Assert(Feed(decoder, 3, Keyboard(2), 6).Count == 0, "Old connection accepted after disconnect.");
    }
    private static void TestOtherDisconnect()
    {
        HciGlobeDecoder decoder = Ready();
        Feed(decoder, 2, DisconnectedEvent(0x0456), 4);
        Assert(decoder.Bound && decoder.DisconnectionCount == 0, "Unrelated disconnect removed binding.");
    }
    private static void TestHandleReuse()
    {
        HciGlobeDecoder decoder = Ready();
        Feed(decoder, 3, Keyboard(2), 4);
        byte[] other = (byte[])Target.Clone(); other[5] ^= 1;
        Edge(Feed(decoder, 2, ConnectedEvent(other, Handle), 5), false, 5, "handle-reused");
        Assert(!decoder.ConnectionBound && !decoder.Bound, "Reused handle retained target binding.");
    }
    private static void TestReconnect()
    {
        HciGlobeDecoder decoder = Connected();
        Feed(decoder, 4, Signal(4, Handle, Request(9, 0x0013, Local)), 2);
        Feed(decoder, 2, ConnectedEvent(Target, Handle), 3);
        Feed(decoder, 3, Signal(3, Handle, Response(9, Remote, Local, 0)), 4);
        Assert(decoder.ConnectionBound && !decoder.Bound && decoder.ConnectionCount == 2, "Reconnect retained a stale request.");
    }
    private static void TestMultipleCommands()
    {
        HciGlobeDecoder decoder = Connected();
        byte[] echo = { 8, 1, 0, 0 };
        Feed(decoder, 4, Signal(4, Handle, echo, Request(9, 0x0013, Local)), 2);
        Feed(decoder, 3, Signal(3, Handle, Response(9, Remote, Local, 0), echo), 3);
        Assert(decoder.Bound && decoder.SignalingPackets == 2, "Multiple commands were not parsed correctly.");
    }
    private static void TestMislabeledKeyboard()
    {
        HciGlobeDecoder decoder = Ready();
        byte[] packet = Keyboard(2); Word(packet, 6, 1);
        Assert(Feed(decoder, 3, packet, 4).Count == 0 && !decoder.FnDown, "Mislabeled keyboard packet accepted.");
    }
    private static void TestReset()
    {
        HciGlobeDecoder decoder = Ready();
        Feed(decoder, 3, Keyboard(2), 4);
        Edge(decoder.Reset(5, "event-loss"), false, 5, "event-loss");
        Assert(!decoder.Bound && !decoder.ConnectionBound && !decoder.FnDown, "Reset retained stale state.");
        Assert(decoder.Reset(6, null).Count == 0, "Reset repeated an up edge.");
    }
    private static void TestChannelDisconnect()
    {
        HciGlobeDecoder decoder = Ready();
        Feed(decoder, 3, Keyboard(2), 4);
        byte[] command = { 6, 12, 4, 0, 0, 0, 0, 0 };
        Word(command, 4, Local); Word(command, 6, Remote);
        Edge(Feed(decoder, 3, Signal(3, Handle, command), 5), false, 5, "interrupt-channel-closed");
        Assert(decoder.ConnectionBound && !decoder.Bound, "Channel close did not preserve only the HCI connection.");
    }
    private static void TestTruncatedSignaling()
    {
        HciGlobeDecoder decoder = Connected();
        byte[] command = { 2, 9, 0xFF, 0, 0x13, 0 };
        Feed(decoder, 4, Signal(4, Handle, command), 2);
        Assert(!decoder.Bound && decoder.RejectedPackets > 0, "Truncated command was not rejected.");
    }
    private static void TestGuard()
    {
        byte[] fixture = Keyboard(2);
        bool rejected = false;
        Func<int, byte> guarded = delegate(int index) {
            if (index == 10 || (index >= 12 && index <= 17)) throw new InvalidOperationException("Forbidden key slot.");
            return fixture[index];
        };
        try { guarded(10); } catch (InvalidOperationException) { rejected = true; }
        Assert(rejected, "The forbidden-byte test guard is inactive.");
        Assert(guarded(11) == 0, "The verified legacy Fn field should be readable.");
    }
    private static void TestAtomicSignaling()
    {
        HciGlobeDecoder decoder = Connected();
        byte[] truncated = { 0xFF };
        Feed(decoder, 4, Signal(4, Handle, Request(9, 0x0013, Local), truncated), 2);
        Feed(decoder, 3, Signal(3, Handle, Response(9, Remote, Local, 0)), 3);
        Assert(!decoder.Bound, "Malformed request PDU left a pending transaction.");
        Feed(decoder, 4, Signal(4, Handle, Request(9, 0x0013, Local)), 4);
        Feed(decoder, 3, Signal(3, Handle, Response(9, Remote, Local, 0), truncated), 5);
        Assert(!decoder.Bound, "Malformed response PDU partially bound a channel.");
        Feed(decoder, 3, Signal(3, Handle, Response(9, Remote, Local, 0)), 6);
        Assert(decoder.Bound, "A rejected malformed PDU corrupted a valid pending transaction.");
    }
    private static void TestUnknownHidShapes()
    {
        HciGlobeDecoder decoder = Ready();
        foreach (int payloadLength in new int[] { 0, 1, 3, 9 })
        {
            byte[] body = new byte[payloadLength];
            if (body.Length > 0) body[0] = 0xA1;
            byte[] packet = Acl(3, Handle, Local, body);
            Func<int, byte> guarded = delegate(int index) {
                if (index < 0 || index >= packet.Length) throw new InvalidOperationException("Unknown shape read outside its packet.");
                if (index >= 9) throw new InvalidOperationException("Unknown shape exposed a possible boot modifier/key byte.");
                return packet[index];
            };
            Assert(decoder.Process(3, packet.Length, guarded, payloadLength + 100).Count == 0,
                "An unsupported shape produced an edge.");
        }
        Assert(decoder.HidShapeEvents == 4 && decoder.GetHidShapeCounts().Count == 4,
            "Unsupported shape aggregates were not recorded.");
        Assert(decoder.AcceptedKeyboardReports == 0 && !decoder.FnDown, "Unsupported shapes changed Fn state.");
        foreach (GlobeHidShapeCount shape in decoder.GetHidShapeCounts())
        {
            Assert(shape.Reason == "unsupported-packet-length", "Unknown shape has the wrong reason.");
            Assert(shape.HidpTag == (shape.L2capLength == 0 ? -1 : 0xA1), "Unknown shape tag is incorrect.");
        }
    }
    private static void TestShapeScope()
    {
        HciGlobeDecoder decoder = Ready();
        byte[] otherHandle = Keyboard(2); Word(otherHandle, 0, 0x2456);
        byte[] otherCid = Keyboard(2); Word(otherCid, 6, 0x0050);
        Feed(decoder, 3, otherHandle, 4);
        Feed(decoder, 3, otherCid, 5);
        byte[] outbound = Keyboard(2); Word(outbound, 0, Handle);
        Feed(decoder, 4, outbound, 6);
        Assert(decoder.HidShapeEvents == 0 && decoder.GetHidShapeCounts().Count == 0,
            "Unrelated traffic entered target HID shape aggregates.");
        Feed(decoder, 3, Keyboard(0), 7);
        Assert(decoder.HidShapeEvents == 1 && decoder.GetHidShapeCounts().Count == 1,
            "Bound target HID input was not counted.");
        Assert(decoder.GetHidRejectionCounts().Count == 0, "An accepted report was counted as a rejection.");
    }
    private static void TestShapeBoundsAndSnapshots()
    {
        HciGlobeDecoder decoder = Ready();
        for (int payloadLength = 1; payloadLength <= 70; payloadLength++)
            Feed(decoder, 3, Acl(3, Handle, Local, new byte[payloadLength]), payloadLength + 100);
        Assert(decoder.HidShapeEvents == 70 && decoder.GetHidShapeCounts().Count == 64 && decoder.HidShapeBucketsDropped == 6,
            "Shape aggregate bucket limit is incorrect.");
        IList<GlobeHidShapeCount> snapshot = decoder.GetHidShapeCounts();
        snapshot[0].Count = -1;
        snapshot[0].Reason = "not-a-decoder-reason";
        foreach (GlobeHidShapeCount shape in decoder.GetHidShapeCounts())
            Assert(shape.Count > 0 && shape.Reason != "not-a-decoder-reason", "Snapshot mutation affected internal aggregates.");
        long before = decoder.HidShapeEvents;
        decoder.Reset(1000, "test-cleanup");
        Assert(decoder.HidShapeEvents == before && decoder.GetHidShapeCounts().Count == 64,
            "Cleanup reset lost the diagnostic aggregates.");
    }
    private static void TestShapeReasons()
    {
        HciGlobeDecoder decoder = Ready();
        byte[] aclMismatch = Keyboard(0); Word(aclMismatch, 2, 16);
        byte[] l2Mismatch = Keyboard(0); Word(l2Mismatch, 4, 12);
        byte[] wrongTag = Keyboard(0); wrongTag[8] = 0xA2;
        byte[] wrongReport = Keyboard(0); wrongReport[9] = 2;
        Feed(decoder, 3, aclMismatch, 4);
        Feed(decoder, 3, l2Mismatch, 5);
        Feed(decoder, 3, wrongTag, 6);
        Feed(decoder, 3, wrongReport, 7);
        IList<GlobeRejectionCount> reasons = decoder.GetHidRejectionCounts();
        Assert(reasons.Count == 4 && decoder.HidShapeEvents == 4, "Structural shape reasons were not preserved.");
        foreach (GlobeRejectionCount reason in reasons)
            Assert(reason.Count == 1 && reason.Reason != "accepted-report01", "Incorrect target HID rejection aggregate.");
    }
    private static void TestIndependentFnSources()
    {
        HciGlobeDecoder decoder = Ready();
        byte[] legacy = Keyboard(0); legacy[11] = 1;
        Edge(Feed(decoder, 3, legacy, 4), true, 4, "globe-down", "vendor-ff00-usage3");
        Assert(!decoder.FnDown && decoder.LegacyFnDown, "Legacy Fn changed primary state.");
        Assert(Feed(decoder, 3, legacy, 5).Count == 0, "Held legacy Fn repeated an edge.");
        byte[] both = Keyboard(2); both[11] = 1;
        Edge(Feed(decoder, 3, both, 6), true, 6, "globe-down");
        Assert(decoder.FnDown && decoder.LegacyFnDown, "Both independent states should be held.");
        Edge(Feed(decoder, 3, legacy, 7), false, 7, "globe-up");
        Assert(!decoder.FnDown && decoder.LegacyFnDown, "Always-on legacy Fn masked primary release.");
        Edge(Feed(decoder, 3, Keyboard(0), 8), false, 8, "globe-up", "vendor-ff00-usage3");
        Assert(!decoder.FnDown && !decoder.LegacyFnDown, "Both source states should be released.");
    }
    private static void BothEdges(IList<GlobeEdge> edges, bool down, long timestamp, string reason)
    {
        Assert(edges.Count == 2, "Expected two independent source edges.");
        Assert(edges[0].Source == "vendor-ff01-usage3" && edges[1].Source == "vendor-ff00-usage3",
            "Both-source edges have missing/combined source identities.");
        for (int i = 0; i < edges.Count; i++)
        {
            Assert(edges[i].Down == down && edges[i].Timestamp == timestamp, "Wrong independent edge state/timestamp.");
            if (reason != null) Assert(edges[i].Reason == reason, "Wrong independent edge reason.");
        }
    }
    private static void TestBothSourcesReset()
    {
        HciGlobeDecoder decoder = Ready();
        byte[] both = Keyboard(2); both[11] = 1;
        BothEdges(Feed(decoder, 3, both, 4), true, 4, "globe-down");
        BothEdges(decoder.Reset(5, "source-loss"), false, 5, "source-loss");
        Assert(!decoder.FnDown && !decoder.LegacyFnDown && !decoder.ConnectionBound,
            "Reset retained one vendor Fn state.");
        Assert(decoder.Reset(6, "source-loss").Count == 0, "Reset repeated released vendor Fn edges.");
    }
    private static void TestLegacyFnMask()
    {
        HciGlobeDecoder decoder = Ready();
        byte[] otherBits = Keyboard(0); otherBits[11] = 0xFE;
        Assert(Feed(decoder, 3, otherBits, 4).Count == 0, "Unrelated legacy-byte bits triggered Fn.");
        byte[] legacy = Keyboard(0); legacy[11] = 0xFF;
        Edge(Feed(decoder, 3, legacy, 5), true, 5, null, "vendor-ff00-usage3");
        Edge(Feed(decoder, 3, otherBits, 6), false, 6, null, "vendor-ff00-usage3");
        Assert(!decoder.FnDown && !decoder.LegacyFnDown, "Legacy mask affected the wrong stream.");
    }
    private static void TestBothSourcesDisconnect()
    {
        HciGlobeDecoder decoder = Ready();
        byte[] both = Keyboard(2); both[11] = 1;
        Feed(decoder, 3, both, 4);
        BothEdges(Feed(decoder, 2, DisconnectedEvent(Handle), 5), false, 5, "disconnect");
        Assert(!decoder.FnDown && !decoder.LegacyFnDown && !decoder.Bound,
            "Disconnection retained a held vendor Fn source.");
    }
}
