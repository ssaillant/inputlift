// Copyright (c) 2026 InputLift contributors
// SPDX-License-Identifier: MIT
// Synthetic fixtures only. No native APIs, capture, files, input injection or configuration.
using System;
using System.Collections.Generic;
using System.Web.Script.Serialization;
using InputLift.Core;

internal static class CoreTests
{
    private const string Address = "102030405060";
    private const ushort Handle = 0x0123, Local = 0x0040, Remote = 0x0041;
    private static int apiChecks;
    private static void Assert(bool okay, string message) { if (!okay) throw new InvalidOperationException(message); }
    private static void Refused<T>(Action action) where T : Exception
    {
        try { action(); } catch (T) { return; }
        throw new InvalidOperationException("Expected " + typeof(T).Name + ".");
    }
    private static void Test(List<string> failures, string name, Action action)
    {
        apiChecks++;
        try { action(); } catch (Exception error) { failures.Add("Public API " + name + ": " + error.GetType().Name + ": " + error.Message); }
    }
    private static void Word(byte[] bytes, int at, ushort value) { bytes[at] = (byte)value; bytes[at + 1] = (byte)(value >> 8); }
    private static byte[] Connection()
    {
        return new byte[] { 3, 11, 0, 0x23, 1, 0x60, 0x50, 0x40, 0x30, 0x20, 0x10, 1, 0 };
    }
    private static byte[] Acl(bool inbound, ushort cid, byte[] payload)
    {
        byte[] packet = new byte[8 + payload.Length];
        Word(packet, 0, (ushort)(Handle | (inbound ? 0x2000 : 0)));
        Word(packet, 2, (ushort)(4 + payload.Length)); Word(packet, 4, (ushort)payload.Length); Word(packet, 6, cid);
        Array.Copy(payload, 0, packet, 8, payload.Length); return packet;
    }
    private static byte[] Report(bool down)
    { return Acl(true, Local, new byte[] { 0xA1, 1, 0xF0, 0, 4, 5, 6, 7, 8, 9, (byte)(down ? 2 : 0) }); }
    private static FnDecoder Ready()
    {
        FnDecoder decoder = new FnDecoder(Address);
        decoder.Process(BluetoothPacketKind.HciEvent, Connection(), 1);
        decoder.Process(BluetoothPacketKind.SentAcl, Acl(false, 1, new byte[] { 2, 9, 4, 0, 0x13, 0, 0x40, 0 }), 2);
        decoder.Process(BluetoothPacketKind.ReceivedAcl, Acl(true, 1, new byte[] { 3, 9, 8, 0, 0x41, 0, 0x40, 0, 0, 0, 0, 0 }), 3);
        Assert(decoder.IsBound, "Synthetic interrupt-channel setup failed."); return decoder;
    }
    private static void One(IList<FnTransition> events, FnTransitionKind kind, long timestamp)
    { Assert(events.Count == 1 && events[0].Kind == kind && events[0].Timestamp == timestamp, "Wrong public transition or opaque timestamp."); }

    private static string[] RunApiTests()
    {
        List<string> failures = new List<string>(); apiChecks = 0;
        Test(failures, "constructor selectors", delegate
        {
            Refused<ArgumentNullException>(delegate { new FnDecoder(null); });
            foreach (string invalid in new[] { "", "10203040506", "1020304050600", "10:20:30:40:50:60", "10203040506G", "000000000000", "FFFFFFFFFFFF", "10203040506\n" })
                Refused<ArgumentException>(delegate { new FnDecoder(invalid); });
            Assert(!new FnDecoder("aAbBcCdDeEfF").IsBound, "Canonical mixed-case selector rejected or unexpectedly bound.");
        });
        Test(failures, "BTHPORT kinds are explicit", delegate
        {
            Assert((byte)BluetoothPacketKind.HciEvent == 2 && (byte)BluetoothPacketKind.ReceivedAcl == 3 && (byte)BluetoothPacketKind.SentAcl == 4, "Packet classification mapping changed.");
            Refused<ArgumentOutOfRangeException>(delegate { new FnDecoder(Address).Process((BluetoothPacketKind)1, new byte[0], 0); });
        });
        Test(failures, "buffer and reader bounds", delegate
        {
            FnDecoder decoder = new FnDecoder(Address); int reads = 0;
            Refused<ArgumentNullException>(delegate { decoder.Process(BluetoothPacketKind.HciEvent, (byte[])null, 0); });
            Refused<ArgumentNullException>(delegate { decoder.Process(BluetoothPacketKind.HciEvent, 1, null, 0); });
            Refused<ArgumentOutOfRangeException>(delegate { decoder.Process(BluetoothPacketKind.HciEvent, -1, delegate(int i) { reads++; return 0; }, 0); });
            Refused<ArgumentOutOfRangeException>(delegate { decoder.Process(BluetoothPacketKind.HciEvent, FnDecoder.MaximumPacketLength + 1, delegate(int i) { reads++; return 0; }, 0); });
            Refused<ArgumentOutOfRangeException>(delegate { decoder.Process(BluetoothPacketKind.HciEvent, new byte[1], int.MaxValue, 1, 0); });
            Assert(reads == 0, "Invalid length reached the reader.");
        });
        Test(failures, "malformed record cannot bind", delegate
        {
            FnDecoder decoder = new FnDecoder(Address);
            Assert(decoder.Process(BluetoothPacketKind.HciEvent, new byte[] { 3, 11 }, 1).Count == 0 && !decoder.IsBound && !decoder.IsConnectionBound, "Truncated connection created state.");
        });
        Test(failures, "bounded slice connection", delegate
        {
            byte[] packet = Connection(), padded = new byte[packet.Length + 8]; Array.Copy(packet, 0, padded, 4, packet.Length);
            FnDecoder decoder = new FnDecoder(Address);
            Assert(decoder.Process(BluetoothPacketKind.HciEvent, padded, 4, packet.Length, -10).Count == 0 && decoder.IsConnectionBound, "Bounded slice was not decoded.");
        });
        Test(failures, "primary press release and timestamps", delegate
        {
            FnDecoder decoder = Ready(); One(decoder.Process(BluetoothPacketKind.ReceivedAcl, Report(true), long.MaxValue), FnTransitionKind.Pressed, long.MaxValue);
            Assert(decoder.IsPressed, "Primary press state missing.");
            Assert(decoder.Process(BluetoothPacketKind.ReceivedAcl, Report(true), 5).Count == 0, "Held state repeated a press.");
            One(decoder.Process(BluetoothPacketKind.ReceivedAcl, Report(false), -7), FnTransitionKind.Released, -7);
            Assert(!decoder.IsPressed, "Primary release state missing.");
        });
        Test(failures, "ordinary key slots skipped", delegate
        {
            FnDecoder decoder = Ready(); byte[] packet = Report(true);
            IList<FnTransition> events = decoder.Process(BluetoothPacketKind.ReceivedAcl, packet.Length, delegate(int index)
            {
                Assert(index >= 0 && index < packet.Length, "Reader escaped its bound.");
                Assert(index != 10 && (index < 12 || index > 17), "Ordinary key field was read."); return packet[index];
            }, 4);
            One(events, FnTransitionKind.Pressed, 4);
        });
        Test(failures, "legacy source not combined", delegate
        {
            FnDecoder decoder = Ready(); byte[] packet = Report(false); packet[11] = 1;
            Assert(decoder.Process(BluetoothPacketKind.ReceivedAcl, packet, 4).Count == 0 && !decoder.IsPressed, "Legacy vendor field became primary Fn.");
        });
        Test(failures, "event list is read-only", delegate
        {
            IList<FnTransition> events = Ready().Process(BluetoothPacketKind.ReceivedAcl, Report(true), 4);
            Assert(events.IsReadOnly, "Returned list is mutable."); Refused<NotSupportedException>(delegate { events.Clear(); });
        });
        Test(failures, "explicit idle reset remains observable", delegate
        {
            FnDecoder decoder = new FnDecoder(Address); One(decoder.Reset(4), FnTransitionKind.Reset, 4);
            One(decoder.Reset(5), FnTransitionKind.Reset, 5);
        });
        Test(failures, "held reset balances before invalidation", delegate
        {
            FnDecoder decoder = Ready(); decoder.Process(BluetoothPacketKind.ReceivedAcl, Report(true), 4); IList<FnTransition> events = decoder.Reset(5);
            Assert(events.Count == 2 && events[0].Kind == FnTransitionKind.Released && events[1].Kind == FnTransitionKind.Reset && !decoder.IsBound && !decoder.IsPressed, "Reset failed to release/invalidate primary state.");
        });
        Test(failures, "disconnect emits release and reset", delegate
        {
            FnDecoder decoder = Ready(); decoder.Process(BluetoothPacketKind.ReceivedAcl, Report(true), 4);
            IList<FnTransition> events = decoder.Process(BluetoothPacketKind.HciEvent, new byte[] { 5, 4, 0, 0x23, 1, 0x13 }, 5);
            Assert(events.Count == 2 && events[0].Kind == FnTransitionKind.Released && events[1].Kind == FnTransitionKind.Reset && !decoder.IsConnectionBound, "Disconnect was not visible to an output consumer.");
        });
        Test(failures, "reader fault invalidates and propagates", delegate
        {
            FnDecoder decoder = Ready(); decoder.Process(BluetoothPacketKind.ReceivedAcl, Report(true), 4);
            Refused<InvalidOperationException>(delegate { decoder.Process(BluetoothPacketKind.HciEvent, 13, delegate(int i) { throw new InvalidOperationException("Synthetic unavailable input"); }, 5); });
            Assert(!decoder.IsBound && !decoder.IsPressed && !decoder.IsConnectionBound, "Reader fault retained learned state.");
        });
        Test(failures, "reader reentry is refused", delegate
        {
            FnDecoder decoder = Ready();
            Refused<InvalidOperationException>(delegate { decoder.Process(BluetoothPacketKind.HciEvent, 13, delegate(int i) { decoder.Reset(5); return 0; }, 5); });
            Assert(!decoder.IsBound, "Reentry failure retained a partial binding.");
        });
        Test(failures, "wrong address emits nothing", delegate
        {
            FnDecoder decoder = new FnDecoder("102030405061"); decoder.Process(BluetoothPacketKind.HciEvent, Connection(), 1);
            Assert(!decoder.IsConnectionBound && decoder.Process(BluetoothPacketKind.ReceivedAcl, Report(true), 4).Count == 0, "Unselected device emitted primary input.");
        });
        return failures.ToArray();
    }

    private static int Main(string[] args)
    {
        if (args.Length != 1 || args[0] != "--self-test")
        { Console.Error.WriteLine("Use --self-test; this executable only runs synthetic pure tests."); return 64; }
        List<string> failures = new List<string>(GlobePacketDecoderTests.Run()); failures.AddRange(RunApiTests());
        int eventChecks; failures.AddRange(EventTests.Run(out eventChecks));
        Console.WriteLine(new JavaScriptSerializer().Serialize(new { status = failures.Count == 0 ? "ok" : "error",
            checks = GlobePacketDecoderTests.TestCount + apiChecks + eventChecks, decoderChecks = GlobePacketDecoderTests.TestCount,
            apiChecks = apiChecks, eventChecks = eventChecks, errors = failures.ToArray(), liveActionsPerformed = false }));
        return failures.Count == 0 ? 0 : 1;
    }
}
