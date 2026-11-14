// Copyright (c) 2026 InputLift contributors
// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.Globalization;

namespace InputLift.Core
{
    /// <summary>
    /// Packet classifications used by the current Windows BTHPORT acquisition path.
    /// These values are NOT the standard Bluetooth H4 packet-type bytes. An input
    /// adapter must translate its own transport/direction classification explicitly.
    /// </summary>
    public enum BluetoothPacketKind : byte
    {
        HciEvent = 2,
        ReceivedAcl = 3,
        SentAcl = 4
    }

    /// <summary>The primary vendor Fn field changed, or learned input state was invalidated.</summary>
    public enum FnTransitionKind { Pressed, Released, Reset }

    /// <summary>Immutable decoded state metadata; contains no ordinary key data or device address.</summary>
    public sealed class FnTransition
    {
        public FnTransitionKind Kind { get; private set; }
        /// <summary>The caller's opaque timestamp, preserved without clock conversion.</summary>
        public long Timestamp { get; private set; }
        /// <summary>A decoder-defined protocol/reset token, not captured text.</summary>
        public string Reason { get; private set; }
        internal FnTransition(FnTransitionKind kind, long timestamp, string reason)
        { Kind = kind; Timestamp = timestamp; Reason = reason; }
    }

    /// <summary>
    /// Pure, stateful decoder for the existing primary FF01/usage3 Fn profile.
    /// It consumes supplied packet records; it does not acquire Bluetooth data,
    /// enable logging, load a driver, inject input, select an application or route
    /// output. Other vendor report profiles are not inferred or combined.
    /// </summary>
    /// <remarks>
    /// Calls are serialized per instance. A supplied reader must return promptly,
    /// must not re-enter this instance, and must provide stable packet bytes for
    /// the duration of Process. Argument validation does not consume a packet.
    /// Reader/decoder exceptions during processing discard learned state; callers
    /// must treat a processing failure as input loss and release any mapped output.
    /// This library supplies decoding only, not a neutral-arming/output safety policy.
    /// </remarks>
    public sealed class FnDecoder
    {
        /// <summary>Four ACL header bytes plus the largest 16-bit ACL data length.</summary>
        public const int MaximumPacketLength = 65539;
        private const string PrimarySource = "vendor-ff01-usage3";
        private readonly object gate = new object();
        private readonly HciGlobeDecoder decoder;
        private bool inOperation;

        /// <summary>
        /// Selects one device using exactly 12 hexadecimal address digits without
        /// separators. Zero and broadcast addresses are refused. No address is exposed
        /// in emitted transitions. The caller must keep its configuration private.
        /// </summary>
        public FnDecoder(string bluetoothAddress)
        {
            if (bluetoothAddress == null) throw new ArgumentNullException("bluetoothAddress");
            if (bluetoothAddress.Length != 12) throw new ArgumentException("Expected exactly 12 hexadecimal address digits without separators.", "bluetoothAddress");
            byte[] address = new byte[6];
            bool anyNonzero = false, anyNonBroadcast = false;
            for (int i = 0; i < address.Length; i++)
            {
                byte value;
                if (!byte.TryParse(bluetoothAddress.Substring(i * 2, 2), NumberStyles.AllowHexSpecifier,
                    CultureInfo.InvariantCulture, out value))
                    throw new ArgumentException("Expected exactly 12 hexadecimal address digits without separators.", "bluetoothAddress");
                address[i] = value;
                anyNonzero |= value != 0; anyNonBroadcast |= value != 255;
            }
            if (!anyNonzero || !anyNonBroadcast) throw new ArgumentException("Zero and broadcast addresses are not device selectors.", "bluetoothAddress");
            decoder = new HciGlobeDecoder(address);
        }

        public bool IsConnectionBound { get { lock (gate) return decoder.ConnectionBound; } }
        public bool IsBound { get { lock (gate) return decoder.Bound; } }
        public bool IsPressed { get { lock (gate) return decoder.FnDown; } }

        /// <summary>Processes one complete packet record; the buffer is not retained.</summary>
        public IList<FnTransition> Process(BluetoothPacketKind kind, byte[] packet, long timestamp)
        {
            if (packet == null) throw new ArgumentNullException("packet");
            return Process(kind, packet, 0, packet.Length, timestamp);
        }

        /// <summary>Processes a bounded slice without copying or reading ordinary key slots.</summary>
        public IList<FnTransition> Process(BluetoothPacketKind kind, byte[] packet, int offset, int count, long timestamp)
        {
            if (packet == null) throw new ArgumentNullException("packet");
            if (offset < 0 || count < 0 || count > packet.Length || offset > packet.Length - count)
                throw new ArgumentOutOfRangeException("offset", "The packet slice must stay inside its buffer.");
            return Process(kind, count, delegate(int index) { return packet[offset + index]; }, timestamp);
        }

        /// <summary>
        /// Processes a length-bounded packet reader. The decoder reads protocol fields,
        /// the target connection address and known vendor Fn bits; ordinary key fields
        /// are skipped. A reader exception invalidates learned state and is rethrown.
        /// </summary>
        public IList<FnTransition> Process(BluetoothPacketKind kind, int length, Func<int, byte> read, long timestamp)
        {
            if (kind != BluetoothPacketKind.HciEvent && kind != BluetoothPacketKind.ReceivedAcl && kind != BluetoothPacketKind.SentAcl)
                throw new ArgumentOutOfRangeException("kind", "Expected a supported BTHPORT packet classification, not an H4 packet-type byte.");
            if (read == null) throw new ArgumentNullException("read");
            if (length < 0 || length > MaximumPacketLength) throw new ArgumentOutOfRangeException("length");
            lock (gate)
            {
                EnterOperation();
                try
                {
                    bool wasBound = decoder.Bound, hadConnection = decoder.ConnectionBound;
                    IList<GlobeEdge> edges = decoder.Process((byte)kind, length, delegate(int index)
                    {
                        if (index < 0 || index >= length) throw new InvalidOperationException("Decoder reader access escaped the supplied packet bound.");
                        return read(index);
                    }, timestamp);
                    List<FnTransition> result = Convert(edges);
                    if ((wasBound && !decoder.Bound) || (hadConnection && !decoder.ConnectionBound))
                        result.Add(new FnTransition(FnTransitionKind.Reset, timestamp, "binding-invalidated"));
                    return result.AsReadOnly();
                }
                catch
                {
                    decoder.Reset(timestamp, "reader-or-decoder-failure");
                    throw;
                }
                finally { inOperation = false; }
            }
        }

        /// <summary>
        /// Invalidates connection/channel state after event loss or session changes.
        /// Emits Released first if the primary Fn field was held, then always emits
        /// Reset, even if already idle. Consumers must release their own output state.
        /// </summary>
        public IList<FnTransition> Reset(long timestamp)
        {
            lock (gate)
            {
                EnterOperation();
                try
                {
                    List<FnTransition> result = Convert(decoder.Reset(timestamp, "external-reset"));
                    result.Add(new FnTransition(FnTransitionKind.Reset, timestamp, "external-reset"));
                    return result.AsReadOnly();
                }
                finally { inOperation = false; }
            }
        }

        private void EnterOperation()
        {
            if (inOperation) throw new InvalidOperationException("A packet reader must not re-enter its FnDecoder.");
            inOperation = true;
        }
        private static List<FnTransition> Convert(IList<GlobeEdge> edges)
        {
            List<FnTransition> result = new List<FnTransition>();
            foreach (GlobeEdge edge in edges)
                if (edge.Source == PrimarySource)
                    result.Add(new FnTransition(edge.Down ? FnTransitionKind.Pressed : FnTransitionKind.Released,
                        edge.Timestamp, edge.Reason));
            return result;
        }
    }
}
