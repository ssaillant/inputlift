// Copyright (c) 2026 InputLift contributors
// SPDX-License-Identifier: MIT
// Pure example consumers. Callbacks below are application-owned decisions,
// not native input, process, network or device-control APIs.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Web.Script.Serialization;
using InputLift.Core;

namespace InputLift.Examples
{
    internal static class ConsumerSelection
    {
        internal static void Validate(string sourceId, string controlId)
        {
            // Use the same logical-ID contract as producers; this constructs
            // metadata only and neither reads nor changes any device state.
            InputEvent.Button(sourceId, controlId, false, 0);
        }
        internal static bool Matches(InputEvent input, string sourceId, string controlId)
        {
            if (input == null) throw new ArgumentNullException("input");
            return input.SourceId == sourceId &&
                (input.Kind == InputEventKind.Reset || input.ControlId == controlId);
        }
    }

    /// <summary>An ordered button stream activates while held, with reset clearing ownership.</summary>
    internal sealed class HoldActionConsumer
    {
        private readonly string sourceId, controlId;
        private readonly Action<bool> apply;
        internal bool IsActive { get; private set; }
        internal HoldActionConsumer(string sourceId, string controlId, Action<bool> apply)
        {
            ConsumerSelection.Validate(sourceId, controlId);
            if (apply == null) throw new ArgumentNullException("apply");
            this.sourceId = sourceId; this.controlId = controlId; this.apply = apply;
        }
        internal void Accept(InputEvent input)
        {
            if (!ConsumerSelection.Matches(input, sourceId, controlId)) return;
            if (input.Kind == InputEventKind.Pressed) Set(true);
            else if (input.Kind == InputEventKind.Released || input.Kind == InputEventKind.Reset) Set(false);
        }
        private void Set(bool next)
        {
            if (next == IsActive) return;
            IsActive = next; apply(next);
        }
    }

    /// <summary>Each new physical press toggles once; source reset returns to the off state.</summary>
    internal sealed class ToggleActionConsumer
    {
        private readonly string sourceId, controlId;
        private readonly Action<bool> apply;
        private bool pressed;
        internal bool IsActive { get; private set; }
        internal ToggleActionConsumer(string sourceId, string controlId, Action<bool> apply)
        {
            ConsumerSelection.Validate(sourceId, controlId);
            if (apply == null) throw new ArgumentNullException("apply");
            this.sourceId = sourceId; this.controlId = controlId; this.apply = apply;
        }
        internal void Accept(InputEvent input)
        {
            if (!ConsumerSelection.Matches(input, sourceId, controlId)) return;
            if (input.Kind == InputEventKind.Reset)
            { pressed = false; Set(false); }
            else if (input.Kind == InputEventKind.Released) pressed = false;
            else if (input.Kind == InputEventKind.Pressed && !pressed)
            { pressed = true; Set(!IsActive); }
        }
        private void Set(bool next)
        {
            if (next == IsActive) return;
            IsActive = next; apply(next);
        }
    }

    /// <summary>A virtual normalized value, not a real volume, brightness or hardware control.</summary>
    internal sealed class VirtualValueConsumer
    {
        private readonly string sourceId, controlId;
        private readonly double initial, stepSize;
        private readonly Action<double> apply;
        internal double Value { get; private set; }
        internal VirtualValueConsumer(string sourceId, string controlId, double initial, double stepSize, Action<double> apply)
        {
            ConsumerSelection.Validate(sourceId, controlId);
            if (double.IsNaN(initial) || double.IsInfinity(initial) || initial < 0 || initial > 1)
                throw new ArgumentOutOfRangeException("initial");
            if (double.IsNaN(stepSize) || double.IsInfinity(stepSize) || stepSize <= 0 || stepSize > 1)
                throw new ArgumentOutOfRangeException("stepSize");
            if (apply == null) throw new ArgumentNullException("apply");
            this.sourceId = sourceId; this.controlId = controlId; this.initial = initial;
            this.stepSize = stepSize; this.apply = apply; Value = initial;
        }
        internal void Accept(InputEvent input)
        {
            if (!ConsumerSelection.Matches(input, sourceId, controlId)) return;
            if (input.Kind == InputEventKind.Reset) Set(initial);
            else if (input.Kind == InputEventKind.ValueChanged) Set(input.Value.Value);
            else if (input.Kind == InputEventKind.Delta)
            {
                // Widen before multiplying/adding, including Int32.MinValue.
                double next = Value + (double)input.Steps.Value * stepSize;
                Set(Math.Max(0, Math.Min(1, next)));
            }
        }
        private void Set(double next)
        {
            if (next == Value) return;
            Value = next; apply(next);
        }
    }

    internal static class EventStream
    {
        internal static void Write(TextWriter output, IEnumerable<InputEvent> events)
        {
            if (output == null) throw new ArgumentNullException("output");
            if (events == null) throw new ArgumentNullException("events");
            JavaScriptSerializer serializer = new JavaScriptSerializer();
            foreach (InputEvent input in events)
            {
                if (input == null) throw new ArgumentException("An event stream cannot contain null.", "events");
                output.WriteLine(serializer.Serialize(new {
                    sourceId = input.SourceId, controlId = input.ControlId,
                    kind = input.Kind.ToString(), timestamp = input.Timestamp.ToString(CultureInfo.InvariantCulture),
                    value = input.Value, steps = input.Steps
                }));
            }
        }
    }
}
