// Copyright (c) 2026 InputLift contributors
// SPDX-License-Identifier: MIT
using System;

namespace InputLift.Core
{
    /// <summary>Control-state primitives, independent of transport and application actions.</summary>
    public enum InputEventKind { Pressed, Released, ValueChanged, Delta, Reset }

    /// <summary>
    /// Immutable control metadata. Source/control identifiers are short logical
    /// labels chosen by the caller. Validation restricts their format; it does
    /// not anonymize them. Callers must exclude addresses, secrets and captured
    /// text. This model does not acquire a device or implement an output action.
    /// </summary>
    public sealed class InputEvent
    {
        /// <summary>Logical source label. Keep unique hardware identifiers private.</summary>
        public string SourceId { get; private set; }
        /// <summary>Logical control label; null only for a source-wide Reset.</summary>
        public string ControlId { get; private set; }
        public InputEventKind Kind { get; private set; }
        /// <summary>Opaque caller timestamp; the event model performs no clock conversion.</summary>
        public long Timestamp { get; private set; }
        /// <summary>Normalized [0,1] value for ValueChanged only; otherwise null.</summary>
        public double? Value { get; private set; }
        /// <summary>Signed, nonzero discrete steps for Delta only; otherwise null.</summary>
        public int? Steps { get; private set; }

        private InputEvent(string source, string control, InputEventKind kind, long timestamp,
            double? value, int? steps)
        {
            SourceId = source; ControlId = control; Kind = kind; Timestamp = timestamp;
            Value = value; Steps = steps;
        }

        /// <summary>Represents a control becoming pressed or released; does not infer gestures.</summary>
        public static InputEvent Button(string source, string control, bool down, long timestamp)
        {
            ValidateToken(source, "source"); ValidateToken(control, "control");
            return new InputEvent(source, control, down ? InputEventKind.Pressed : InputEventKind.Released,
                timestamp, null, null);
        }

        /// <summary>
        /// Represents a normalized absolute control value in [0,1]. Conversion
        /// from device units, calibration and sampling policy belong to a producer.
        /// </summary>
        public static InputEvent Absolute(string source, string control, double value, long timestamp)
        {
            ValidateToken(source, "source"); ValidateToken(control, "control");
            if (double.IsNaN(value) || double.IsInfinity(value) || value < 0 || value > 1)
                throw new ArgumentOutOfRangeException("value", "An absolute value must be finite and normalized to [0,1].");
            return new InputEvent(source, control, InputEventKind.ValueChanged, timestamp, value, null);
        }

        /// <summary>
        /// Represents nonzero relative movement in producer-defined discrete steps.
        /// Consumers should use a wider numeric type before scaling/accumulating.
        /// </summary>
        public static InputEvent Relative(string source, string control, int steps, long timestamp)
        {
            ValidateToken(source, "source"); ValidateToken(control, "control");
            if (steps == 0) throw new ArgumentOutOfRangeException("steps", "A relative event must contain nonzero steps.");
            return new InputEvent(source, control, InputEventKind.Delta, timestamp, null, steps);
        }

        /// <summary>
        /// Invalidates all learned/active control state for this source, not just
        /// one control. Consumers must release source-owned outputs and clear
        /// pending gestures. No physical value or automatic output is implied.
        /// </summary>
        public static InputEvent Reset(string source, long timestamp)
        {
            ValidateToken(source, "source");
            return new InputEvent(source, null, InputEventKind.Reset, timestamp, null, null);
        }

        internal static void ValidateToken(string value, string parameter)
        {
            if (value == null) throw new ArgumentNullException(parameter);
            if (value.Length < 1 || value.Length > 64)
                throw new ArgumentException("Use a logical identifier of 1 to 64 ASCII letters, digits, dots, underscores or hyphens.", parameter);
            foreach (char item in value)
                if (!((item >= 'a' && item <= 'z') || (item >= 'A' && item <= 'Z') ||
                    (item >= '0' && item <= '9') || item == '.' || item == '_' || item == '-'))
                    throw new ArgumentException("Logical identifiers may contain only ASCII letters, digits, dots, underscores or hyphens.", parameter);
        }
    }
}
