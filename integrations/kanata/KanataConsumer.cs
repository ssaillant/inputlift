// Copyright (c) 2026 InputLift contributors
// SPDX-License-Identifier: MIT
using System;
using InputLift.Core;

namespace InputLift.Kanata
{
    public enum KeyAction { Press, Release }

    /// <summary>A successful send confirms local transport only, not application effects.</summary>
    public interface IKeyTransport : IDisposable
    {
        bool IsHealthy { get; }
        void Send(KeyAction action);
    }

    /// <summary>
    /// Maps one selected control to one exclusively reserved Kanata virtual key.
    /// A session/reset requires observed neutral before a new press. There is no
    /// reconnect or press replay. The caller owns acquisition and a process guard.
    /// </summary>
    public sealed class KanataConsumer : IDisposable
    {
        private readonly IKeyTransport transport;
        private readonly string source, control;
        private bool started, neutral, held, faulted, disposed, uncertain;
        public bool IsHeld { get { return held; } }
        public bool IsArmed { get { return started && neutral && !faulted && !disposed; } }
        public bool IsFaulted { get { return faulted || !transport.IsHealthy; } }
        public bool ReleaseUnconfirmed { get { return uncertain; } }

        public KanataConsumer(IKeyTransport transport, string source, string control)
        {
            if (transport == null) throw new ArgumentNullException("transport");
            // Reuse core identifier validation without exposing additional internals.
            InputEvent.Button(source, control, false, 0);
            this.transport = transport; this.source = source; this.control = control;
        }

        public void BeginSession()
        {
            if (started || disposed) throw new InvalidOperationException("A consumer has one session only.");
            started = true; neutral = false;
            SendRelease(); // Dedicated virtual key: also recover a previous interrupted run.
        }

        public void ObserveNeutral()
        {
            RequireActive();
            if (held) SendRelease();
            neutral = true;
        }

        public void Accept(InputEvent input)
        {
            if (input == null) throw new ArgumentNullException("input");
            RequireActive();
            if (input.SourceId != source) return;
            if (input.Kind == InputEventKind.Reset)
            {
                if (held || uncertain) SendRelease();
                neutral = false; return;
            }
            if (input.ControlId != control) return;
            if (input.Kind == InputEventKind.Released) { ObserveNeutral(); return; }
            if (input.Kind != InputEventKind.Pressed || !neutral || held) return;
            held = true; uncertain = true; // A partial TCP write can have taken effect.
            try { transport.Send(KeyAction.Press); }
            catch { faulted = true; neutral = false; TryRelease(); throw; }
        }

        private void RequireActive()
        {
            if (!started || disposed || faulted || !transport.IsHealthy)
                throw new InvalidOperationException("Consumer is not in a healthy active session; start a new guarded process.");
        }

        private void SendRelease()
        {
            uncertain = true;
            try { transport.Send(KeyAction.Release); held = false; uncertain = false; }
            catch { faulted = true; neutral = false; throw; }
        }

        private void TryRelease()
        { try { SendRelease(); } catch { uncertain = true; } }

        public void Dispose()
        {
            if (disposed) return;
            if (started && (held || uncertain)) TryRelease();
            neutral = false; disposed = true; transport.Dispose();
        }
    }

    /// <summary>Narrow, ordered, ASCII capture frames. Contains no ordinary keys or device IDs.</summary>
    public sealed class CaptureFrame
    {
        public long Sequence { get; private set; }
        public string Kind { get; private set; }
        public bool Neutral { get; private set; }
        public static CaptureFrame Parse(string line)
        {
            if (line == null || line.Length > 96) throw new FormatException("Capture frame is missing or oversized.");
            string[] parts = line.Split(' ');
            long sequence;
            if (parts.Length != 4 || parts[0] != "ILFN1" ||
                !long.TryParse(parts[1], System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out sequence) || sequence < 1 ||
                (parts[3] != "0" && parts[3] != "1"))
                throw new FormatException("Invalid capture frame header.");
            string kind = parts[2]; bool neutral = parts[3] == "1";
            if (kind != "reset" && kind != "down" && kind != "up" && kind != "heartbeat")
                throw new FormatException("Unsupported capture frame kind.");
            if (((kind == "reset" || kind == "down") && neutral) || (kind == "up" && !neutral))
                throw new FormatException("Contradictory capture frame state.");
            return new CaptureFrame { Sequence = sequence, Kind = kind, Neutral = neutral };
        }
    }

    public sealed class CaptureFrameConsumer
    {
        private readonly KanataConsumer consumer;
        private long last;
        private readonly string source, control;
        public CaptureFrameConsumer(KanataConsumer consumer, string source, string control)
        { this.consumer = consumer; this.source = source; this.control = control; }
        public void Accept(string line)
        {
            CaptureFrame frame = CaptureFrame.Parse(line);
            if (frame.Sequence <= last || (last == 0 && frame.Kind != "reset") ||
                (last != 0 && frame.Sequence != last + 1))
                throw new FormatException("Capture stream must start with reset and have contiguous increasing sequence numbers.");
            last = frame.Sequence;
            if (frame.Kind == "reset") consumer.Accept(InputEvent.Reset(source, last));
            else if (frame.Kind == "down") consumer.Accept(InputEvent.Button(source, control, true, last));
            else if (frame.Kind == "up") consumer.Accept(InputEvent.Button(source, control, false, last));
            else if (frame.Neutral) consumer.ObserveNeutral();
        }
    }
}
