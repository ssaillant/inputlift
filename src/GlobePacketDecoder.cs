// Copyright (c) 2026 GlobeRelay contributors
// SPDX-License-Identifier: MIT
// Pure, conservative protocol decoder. No I/O, tracing, injection or packet logging.
// The reader is accessed only for protocol fields, a connection address and two
// descriptor-verified vendor Fn fields. Ordinary modifiers/key slots are skipped.
using System;
using System.Collections.Generic;
using System.Globalization;

internal sealed class GlobeEdge
{
    internal bool Down;
    internal long Timestamp;
    internal string Reason;
    internal string Source;
}

internal sealed class GlobeHidShapeCount
{
    internal int PacketLength;
    internal int AclDataLength;
    internal int L2capLength;
    internal int HidpTag;
    internal string Reason;
    internal long Count;
}

internal sealed class GlobeRejectionCount
{
    internal string Reason;
    internal long Count;
}

// Thread-confined: call Process/Reset from one consumer, or serialize externally.
// Numeric packet kinds follow the current BTHPORT interpretation: 2=HCI event,
// 3=received ACL, 4=sent ACL. They are not H4 packet type values.
internal sealed class HciGlobeDecoder
{
    private const ushort SignalingCid = 0x0001;
    private const ushort HidInterruptPsm = 0x0013;
    private const ushort FirstDynamicCid = 0x0040;
    private static readonly IList<GlobeEdge> NoEdges = new GlobeEdge[0];
    private const int MaximumHidShapeBuckets = 64;
    private const string PrimaryFnSource = "vendor-ff01-usage3";
    private const string LegacyFnSource = "vendor-ff00-usage3";
    private readonly byte[] targetAddress;
    private readonly Dictionary<byte, ushort> hostRequests = new Dictionary<byte, ushort>();
    private readonly Dictionary<byte, ushort> peerRequests = new Dictionary<byte, ushort>();
    private readonly SortedDictionary<string, GlobeHidShapeCount> hidShapes = new SortedDictionary<string, GlobeHidShapeCount>(StringComparer.Ordinal);
    private bool connectionBound;
    private ushort connectionHandle;
    private bool channelBound;
    private ushort localInterruptCid;
    private ushort remoteInterruptCid;
    private bool fnDown;
    private bool legacyFnDown;
    private bool rejectedThisPacket;
    private long connectionCount;
    private long disconnectionCount;
    private long rejectedPackets;
    private long fragmentedPackets;
    private long signalingPackets;
    private long boundChannelCount;
    private long acceptedKeyboardReports;
    private long hidShapeEvents;
    private long hidShapeBucketsDropped;

    internal HciGlobeDecoder(byte[] targetAddressBigEndian)
    {
        if (targetAddressBigEndian == null) throw new ArgumentNullException("targetAddressBigEndian");
        if (targetAddressBigEndian.Length != 6) throw new ArgumentException("A Bluetooth address must contain six bytes.", "targetAddressBigEndian");
        targetAddress = (byte[])targetAddressBigEndian.Clone();
    }

    internal bool Bound { get { return connectionBound && channelBound; } }
    internal bool ConnectionBound { get { return connectionBound; } }
    internal bool FnDown { get { return fnDown; } }
    internal bool LegacyFnDown { get { return legacyFnDown; } }
    internal long ConnectionCount { get { return connectionCount; } }
    internal long DisconnectionCount { get { return disconnectionCount; } }
    internal long RejectedPackets { get { return rejectedPackets; } }
    internal long FragmentedPackets { get { return fragmentedPackets; } }
    internal long SignalingPackets { get { return signalingPackets; } }
    internal long BoundChannelCount { get { return boundChannelCount; } }
    internal long AcceptedKeyboardReports { get { return acceptedKeyboardReports; } }
    internal long HidShapeEvents { get { return hidShapeEvents; } }
    internal long HidShapeBucketsDropped { get { return hidShapeBucketsDropped; } }

    // Snapshots contain only bounded aggregates from the exact target's bound
    // inbound interrupt channel. No handle, CID, address or key values are output.
    internal IList<GlobeHidShapeCount> GetHidShapeCounts()
    {
        List<GlobeHidShapeCount> result = new List<GlobeHidShapeCount>();
        foreach (GlobeHidShapeCount shape in hidShapes.Values)
            result.Add(new GlobeHidShapeCount { PacketLength = shape.PacketLength,
                AclDataLength = shape.AclDataLength, L2capLength = shape.L2capLength,
                HidpTag = shape.HidpTag, Reason = shape.Reason, Count = shape.Count });
        return result;
    }

    internal IList<GlobeRejectionCount> GetHidRejectionCounts()
    {
        SortedDictionary<string, long> totals = new SortedDictionary<string, long>(StringComparer.Ordinal);
        foreach (GlobeHidShapeCount shape in hidShapes.Values)
        {
            if (shape.Reason == "accepted-report01") continue;
            long count;
            totals.TryGetValue(shape.Reason, out count);
            totals[shape.Reason] = count + shape.Count;
        }
        List<GlobeRejectionCount> result = new List<GlobeRejectionCount>();
        foreach (KeyValuePair<string, long> item in totals)
            result.Add(new GlobeRejectionCount { Reason = item.Key, Count = item.Value });
        return result;
    }

    internal IList<GlobeEdge> Process(byte type, int length, Func<int, byte> read, long timestamp)
    {
        if (read == null) throw new ArgumentNullException("read");
        rejectedThisPacket = false;
        if (length < 0) { Reject(); return NoEdges; }
        if (type == 2) return ProcessEvent(length, read, timestamp);
        if (type == 3 || type == 4) return ProcessAcl(type, length, read, timestamp);
        // Commands and unrelated numeric packet kinds are outside this decoder.
        return NoEdges;
    }

    // An external event-loss/session-stop handler can explicitly invalidate all
    // learned state. Timestamp is opaque and preserved exactly on emitted edges.
    internal IList<GlobeEdge> Reset(long timestamp, string reason)
    {
        IList<GlobeEdge> edges = Release(timestamp, string.IsNullOrEmpty(reason) ? "source-reset" : reason);
        ClearConnection();
        return edges;
    }

    private IList<GlobeEdge> ProcessEvent(int length, Func<int, byte> read, long timestamp)
    {
        if (length < 2) { Reject(); return NoEdges; }
        byte eventCode = read(0);
        if (length != 2 + read(1)) { Reject(); return NoEdges; }
        if (eventCode == 0x03)
        {
            if (length != 13) { Reject(); return NoEdges; }
            if (read(2) != 0 || read(11) != 1) { Reject(); return NoEdges; }
            ushort handleWord = Word(read, 3);
            if (handleWord > 0x0EFF) { Reject(); return NoEdges; }
            ushort handle = (ushort)(handleWord & 0x0FFF);
            bool matches = true;
            for (int i = 0; i < 6; i++)
            {
                if (read(5 + i) != targetAddress[5 - i]) matches = false;
            }
            if (!matches)
            {
                Reject();
                // A handle can be reused after a missed disconnection event.
                if (connectionBound && handle == connectionHandle)
                    return Reset(timestamp, "handle-reused");
                return NoEdges;
            }
            IList<GlobeEdge> prior = Release(timestamp, "connection-replaced");
            ClearConnection();
            connectionBound = true;
            connectionHandle = handle;
            connectionCount++;
            return prior;
        }
        if (eventCode == 0x05)
        {
            if (length != 6) { Reject(); return NoEdges; }
            ushort handleWord = Word(read, 3);
            if (read(2) != 0 || (handleWord & 0xF000) != 0) { Reject(); return NoEdges; }
            if (!connectionBound || handleWord != connectionHandle) { Reject(); return NoEdges; }
            disconnectionCount++;
            return Reset(timestamp, "disconnect");
        }
        return NoEdges;
    }

    private IList<GlobeEdge> ProcessAcl(byte type, int length, Func<int, byte> read, long timestamp)
    {
        if (length < 8 || length > 65539) { Reject(); return NoEdges; }
        ushort handleAndFlags = Word(read, 0);
        ushort handle = (ushort)(handleAndFlags & 0x0FFF);
        if (!connectionBound || handle != connectionHandle) { Reject(); return NoEdges; }
        int boundary = (handleAndFlags >> 12) & 3;
        int broadcast = (handleAndFlags >> 14) & 3;
        if (broadcast != 0) { Reject(); return NoEdges; }
        // Controller-to-host starts use PB=2. Host-to-controller starts may use
        // PB=0 or PB=2. Continuations and the formerly used PB=3 are not decoded.
        if (boundary == 1 || boundary == 3 || (type == 3 && boundary != 2))
        {
            fragmentedPackets++;
            Reject();
            return NoEdges;
        }
        int aclLength = Word(read, 2);
        int l2capLength = Word(read, 4);
        ushort cid = Word(read, 6);
        bool targetHidInput = type == 3 && channelBound && cid == localInterruptCid;
        // Offset 8 is the HIDP transaction tag, not keyboard data. In an unknown
        // shape no later byte (including a possible boot-mode modifier at 9) is read.
        int hidpTag = targetHidInput && l2capLength >= 1 && length >= 9 ? read(8) : -1;
        if (aclLength != length - 4)
        {
            if (targetHidInput) ObserveHidShape(length, aclLength, l2capLength, hidpTag, "inconsistent-acl-length");
            Reject();
            return NoEdges;
        }
        if (l2capLength != length - 8)
        {
            if (l2capLength > length - 8) fragmentedPackets++;
            if (targetHidInput) ObserveHidShape(length, aclLength, l2capLength, hidpTag, "inconsistent-l2cap-length");
            Reject();
            return NoEdges;
        }
        if (cid == SignalingCid)
        {
            // A 19-byte packet could be the keyboard-shaped fixture. No
            // connection/disconnection signaling command we need has this size.
            // Refuse it before reading positions 10..17, even if mislabeled CID1.
            if (length == 19) { Reject(); return NoEdges; }
            signalingPackets++;
            return ProcessSignaling(type, length, read, timestamp);
        }
        if (!targetHidInput)
        {
            Reject();
            return NoEdges;
        }
        if (length != 19 || aclLength != 15 || l2capLength != 11)
        {
            ObserveHidShape(length, aclLength, l2capLength, hidpTag, "unsupported-packet-length");
            Reject();
            return NoEdges;
        }
        if (hidpTag != 0xA1)
        {
            ObserveHidShape(length, aclLength, l2capLength, hidpTag, "unsupported-hidp-tag");
            Reject();
            return NoEdges;
        }
        if (read(9) != 0x01)
        {
            ObserveHidShape(length, aclLength, l2capLength, hidpTag, "unsupported-report-id");
            Reject();
            return NoEdges;
        }
        // Actual device descriptor metadata verified two independent Fn usages:
        // FF01/usage3 is report byte9 bit02 (packet18); FF00/usage3 is report
        // byte2 bit01 (packet11). Read those fields only, never ordinary modifier
        // byte10 or ordinary key slots12..17. Do not combine the Fn sources.
        bool currentPrimary = (read(18) & 0x02) != 0;
        bool currentLegacy = (read(11) & 0x01) != 0;
        acceptedKeyboardReports++;
        ObserveHidShape(length, aclLength, l2capLength, hidpTag, "accepted-report01");
        List<GlobeEdge> sourceEdges = null;
        if (currentPrimary != fnDown)
        {
            fnDown = currentPrimary;
            Append(ref sourceEdges, OneEdge(currentPrimary, timestamp,
                currentPrimary ? "globe-down" : "globe-up", PrimaryFnSource));
        }
        if (currentLegacy != legacyFnDown)
        {
            legacyFnDown = currentLegacy;
            Append(ref sourceEdges, OneEdge(currentLegacy, timestamp,
                currentLegacy ? "globe-down" : "globe-up", LegacyFnSource));
        }
        return sourceEdges == null ? NoEdges : sourceEdges;
    }

    private IList<GlobeEdge> ProcessSignaling(byte type, int length, Func<int, byte> read, long timestamp)
    {
        // Reject a structurally malformed signaling PDU before changing any
        // transaction/channel state, including when its final command is bad.
        if (!SignalingIsWellFormed(length, read)) { Reject(); return NoEdges; }
        List<GlobeEdge> edges = null;
        int position = 8;
        while (position < length)
        {
            if (length - position < 4) { Reject(); break; }
            byte code = read(position);
            byte identifier = read(position + 1);
            int commandLength = Word(read, position + 2);
            int body = position + 4;
            if (identifier == 0 || commandLength > length - body) { Reject(); break; }
            if (code == 0x02)
            {
                if (commandLength != 4) { Reject(); break; }
                // Identifiers can be reused for a different PSM. Do not leave
                // an older HID request behind when the replacement is ignored.
                Dictionary<byte, ushort> pending = type == 4 ? hostRequests : peerRequests;
                pending.Remove(identifier);
                ushort psm = Word(read, body);
                ushort sourceCid = Word(read, body + 2);
                if (psm != HidInterruptPsm || !DynamicCid(sourceCid))
                {
                    Reject();
                }
                else pending[identifier] = sourceCid;
            }
            else if (code == 0x03)
            {
                if (commandLength != 8) { Reject(); break; }
                ushort destinationCid = Word(read, body);
                ushort sourceCid = Word(read, body + 2);
                ushort result = Word(read, body + 4);
                Dictionary<byte, ushort> pending = type == 3 ? hostRequests : peerRequests;
                ushort requestedSource;
                if (!pending.TryGetValue(identifier, out requestedSource) || requestedSource != sourceCid)
                {
                    Reject();
                }
                else if (result == 1)
                {
                    // Pending authorization/authentication can precede success.
                }
                else
                {
                    pending.Remove(identifier);
                    if (result != 0 || Word(read, body + 6) != 0 || !DynamicCid(sourceCid) || !DynamicCid(destinationCid))
                    {
                        Reject();
                    }
                    else
                    {
                        Append(ref edges, Release(timestamp, "interrupt-channel-rebound"));
                        channelBound = true;
                        localInterruptCid = type == 3 ? sourceCid : destinationCid;
                        remoteInterruptCid = type == 3 ? destinationCid : sourceCid;
                        boundChannelCount++;
                    }
                }
            }
            else if (code == 0x01)
            {
                // Command Reject terminates an earlier request sent in the
                // opposite direction. Its reason/body is not needed here.
                Dictionary<byte, ushort> pending = type == 3 ? hostRequests : peerRequests;
                pending.Remove(identifier);
            }
            else if (code == 0x06 || code == 0x07)
            {
                if (commandLength != 4) { Reject(); break; }
                ushort destinationCid = Word(read, body);
                ushort sourceCid = Word(read, body + 2);
                // Responses echo the request's CID pair, so their orientation
                // differs from a request arriving in the same direction.
                bool localIsDestination = (code == 0x06 && type == 3) || (code == 0x07 && type == 4);
                bool matches = channelBound && (localIsDestination ?
                    destinationCid == localInterruptCid && sourceCid == remoteInterruptCid :
                    destinationCid == remoteInterruptCid && sourceCid == localInterruptCid);
                if (matches)
                {
                    Append(ref edges, Release(timestamp, "interrupt-channel-closed"));
                    ClearChannel();
                }
            }
            // Configuration, echo and other signaling commands are skipped;
            // no bytes in their command bodies are examined.
            position = body + commandLength;
        }
        return edges == null ? NoEdges : edges;
    }

    private static bool SignalingIsWellFormed(int length, Func<int, byte> read)
    {
        int position = 8;
        while (position < length)
        {
            if (length - position < 4) return false;
            byte code = read(position);
            if (read(position + 1) == 0) return false;
            int commandLength = Word(read, position + 2);
            int body = position + 4;
            if (commandLength > length - body) return false;
            if (code == 0x01 && commandLength < 2) return false;
            if ((code == 0x02 || code == 0x06 || code == 0x07) && commandLength != 4) return false;
            if (code == 0x03 && commandLength != 8) return false;
            position = body + commandLength;
        }
        return true;
    }

    private static bool DynamicCid(ushort cid) { return cid >= FirstDynamicCid; }
    private void ObserveHidShape(int packetLength, int aclLength, int l2capLength, int hidpTag, string reason)
    {
        hidShapeEvents++;
        string key = string.Format(CultureInfo.InvariantCulture, "{0}:{1}:{2}:{3}:{4}",
            packetLength, aclLength, l2capLength, hidpTag, reason);
        GlobeHidShapeCount shape;
        if (hidShapes.TryGetValue(key, out shape)) { shape.Count++; return; }
        if (hidShapes.Count >= MaximumHidShapeBuckets) { hidShapeBucketsDropped++; return; }
        hidShapes.Add(key, new GlobeHidShapeCount { PacketLength = packetLength,
            AclDataLength = aclLength, L2capLength = l2capLength, HidpTag = hidpTag,
            Reason = reason, Count = 1 });
    }
    private static ushort Word(Func<int, byte> read, int position)
    {
        return (ushort)(read(position) | (read(position + 1) << 8));
    }
    private void Reject()
    {
        if (!rejectedThisPacket) { rejectedPackets++; rejectedThisPacket = true; }
    }
    private IList<GlobeEdge> Release(long timestamp, string reason)
    {
        List<GlobeEdge> edges = null;
        if (fnDown)
        {
            fnDown = false;
            Append(ref edges, OneEdge(false, timestamp, reason, PrimaryFnSource));
        }
        if (legacyFnDown)
        {
            legacyFnDown = false;
            Append(ref edges, OneEdge(false, timestamp, reason, LegacyFnSource));
        }
        return edges == null ? NoEdges : edges;
    }
    private static IList<GlobeEdge> OneEdge(bool down, long timestamp, string reason, string source)
    {
        return new GlobeEdge[] { new GlobeEdge { Down = down, Timestamp = timestamp, Reason = reason, Source = source } };
    }
    private static void Append(ref List<GlobeEdge> output, IList<GlobeEdge> input)
    {
        if (input.Count == 0) return;
        if (output == null) output = new List<GlobeEdge>();
        for (int i = 0; i < input.Count; i++) output.Add(input[i]);
    }
    private void ClearChannel()
    {
        channelBound = false;
        localInterruptCid = 0;
        remoteInterruptCid = 0;
        hostRequests.Clear();
        peerRequests.Clear();
    }
    private void ClearConnection()
    {
        ClearChannel();
        connectionBound = false;
        connectionHandle = 0;
    }
}
