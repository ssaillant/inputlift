// Copyright (c) 2026 InputLift contributors
// SPDX-License-Identifier: MIT
// Pure synthetic event-contract checks. No capture, native input or settings access.
using System;
using System.Collections.Generic;
using System.Reflection;
using InputLift.Core;

internal static class EventTests
{
    private static void Assert(bool okay, string message)
    { if (!okay) throw new InvalidOperationException(message); }
    private static void Refused<T>(Action action) where T : Exception
    {
        try { action(); } catch (T) { return; }
        throw new InvalidOperationException("Expected " + typeof(T).Name + ".");
    }
    private static IEnumerable<FnTransition> Batch(int count)
    {
        for (int i = 0; i < count; i++)
            yield return new FnTransition(FnTransitionKind.Pressed, i, "synthetic");
    }
    private static IEnumerable<FnTransition> BrokenBatch()
    {
        yield return new FnTransition(FnTransitionKind.Pressed, 1, "synthetic");
        throw new InvalidOperationException("Synthetic enumeration failure");
    }

    internal static string[] Run(out int count)
    {
        int cases = 0; List<string> failures = new List<string>();
        Action<string, Action> test = delegate(string name, Action action)
        {
            cases++;
            try { action(); }
            catch (Exception error) { failures.Add("Event model " + name + ": " + error.GetType().Name + ": " + error.Message); }
        };
        test("button identity and payload isolation", delegate
        {
            InputEvent down = InputEvent.Button("demo-device", "button-1", true, 5);
            InputEvent up = InputEvent.Button("demo-device", "button-1", false, 9);
            Assert(down.Kind == InputEventKind.Pressed && up.Kind == InputEventKind.Released &&
                down.SourceId == "demo-device" && down.ControlId == "button-1" &&
                down.Timestamp == 5 && up.Timestamp == 9 && !down.Value.HasValue && !down.Steps.HasValue &&
                !up.Value.HasValue && !up.Steps.HasValue, "Button metadata/payload contract changed.");
        });
        test("opaque timestamps remain unchanged", delegate
        {
            Assert(InputEvent.Button("source", "control", true, long.MinValue).Timestamp == long.MinValue &&
                InputEvent.Reset("source", long.MaxValue).Timestamp == long.MaxValue,
                "An event converted or rejected the caller's opaque timestamp.");
        });
        test("absolute normalized endpoints and midpoint", delegate
        {
            foreach (double value in new[] { 0.0, 0.5, 1.0 })
            {
                InputEvent item = InputEvent.Absolute("source", "slider", value, 7);
                Assert(item.Kind == InputEventKind.ValueChanged && item.Value == value &&
                    !item.Steps.HasValue && item.ControlId == "slider", "Normalized absolute value changed.");
            }
        });
        test("nonfinite or unnormalized absolute values refused", delegate
        {
            foreach (double value in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity, -0.001, 1.001 })
                Refused<ArgumentOutOfRangeException>(delegate { InputEvent.Absolute("source", "slider", value, 0); });
        });
        test("signed relative steps keep their own payload", delegate
        {
            foreach (int steps in new[] { int.MinValue, -1, 1, int.MaxValue })
            {
                InputEvent item = InputEvent.Relative("source", "dial", steps, 1);
                Assert(item.Kind == InputEventKind.Delta && item.Steps == steps && !item.Value.HasValue,
                    "Relative steps were confused with an absolute normalized value.");
            }
        });
        test("zero relative movement refused", delegate
        { Refused<ArgumentOutOfRangeException>(delegate { InputEvent.Relative("source", "dial", 0, 1); }); });
        test("reset is source-wide without control or scalar payload", delegate
        {
            InputEvent reset = InputEvent.Reset("source-1", 6);
            Assert(reset.Kind == InputEventKind.Reset && reset.SourceId == "source-1" &&
                reset.ControlId == null && !reset.Value.HasValue && !reset.Steps.HasValue,
                "Reset no longer invalidates an entire source unambiguously.");
        });
        test("source identifiers are bounded logical tokens", delegate
        {
            Refused<ArgumentNullException>(delegate { InputEvent.Reset(null, 0); });
            foreach (string invalid in new[] { "", "space here", "line\n", "slash/path", "colon:token", "\u00e9" })
                Refused<ArgumentException>(delegate { InputEvent.Reset(invalid, 0); });
        });
        test("control identifiers use the same strict contract", delegate
        {
            Refused<ArgumentNullException>(delegate { InputEvent.Button("source", null, true, 0); });
            foreach (string invalid in new[] { "", " ", "line\r", "path\\part", "\u200b" })
                Refused<ArgumentException>(delegate { InputEvent.Absolute("source", invalid, 0.5, 0); });
        });
        test("identifier length boundary and safe punctuation", delegate
        {
            string longest = new string('a', 64);
            Assert(InputEvent.Button(longest, "Control_A.1-2", true, 0).SourceId == longest,
                "Valid boundary identifier rejected.");
            Refused<ArgumentException>(delegate { InputEvent.Reset(longest + "a", 0); });
            Refused<ArgumentException>(delegate { InputEvent.Relative("source", longest + "a", 1, 0); });
        });
        test("public events expose no mutable state", delegate
        {
            foreach (PropertyInfo property in typeof(InputEvent).GetProperties())
                Assert(property.GetSetMethod(false) == null, "Event property has a public setter: " + property.Name);
            Assert(typeof(InputEvent).GetConstructors().Length == 0, "An unchecked public constructor bypasses event validation.");
        });
        test("Fn mapping preserves order identity and timestamp", delegate
        {
            FnInputAdapter adapter = new FnInputAdapter("logical-device", "extra-key");
            IList<InputEvent> events = adapter.Convert(new[] {
                new FnTransition(FnTransitionKind.Pressed, 2, "synthetic"),
                new FnTransition(FnTransitionKind.Released, 4, "synthetic"),
                new FnTransition(FnTransitionKind.Reset, 5, "synthetic") });
            Assert(events.Count == 3 && events[0].Kind == InputEventKind.Pressed &&
                events[1].Kind == InputEventKind.Released && events[2].Kind == InputEventKind.Reset &&
                events[0].Timestamp == 2 && events[1].Timestamp == 4 && events[2].Timestamp == 5 &&
                events[0].SourceId == "logical-device" && events[0].ControlId == "extra-key" &&
                events[2].ControlId == null, "Fn mapping changed transition order or reset scope.");
        });
        test("empty mapped batch and default Fn control are read-only", delegate
        {
            FnInputAdapter adapter = new FnInputAdapter("source");
            IList<InputEvent> empty = adapter.Convert(new FnTransition[0]);
            IList<InputEvent> one = adapter.Convert(Batch(1));
            Assert(empty.Count == 0 && empty.IsReadOnly && one.IsReadOnly && one[0].ControlId == "fn",
                "Mapped collection/default-control contract changed.");
            Refused<NotSupportedException>(delegate { empty.Add(InputEvent.Reset("source", 0)); });
            Refused<NotSupportedException>(delegate { one.Clear(); });
        });
        test("invalid mapper selectors and input batches refused", delegate
        {
            Refused<ArgumentException>(delegate { new FnInputAdapter("invalid source"); });
            Refused<ArgumentNullException>(delegate { new FnInputAdapter("source", null); });
            FnInputAdapter adapter = new FnInputAdapter("source");
            Refused<ArgumentNullException>(delegate { adapter.Convert(null); });
            Refused<ArgumentException>(delegate { adapter.Convert(new FnTransition[] { null }); });
        });
        test("unknown Fn transition kind refused", delegate
        {
            Refused<ArgumentException>(delegate { new FnInputAdapter("source").Convert(new[] {
                new FnTransition((FnTransitionKind)999, 1, "synthetic") }); });
        });
        test("mapped enumeration is bounded", delegate
        {
            FnInputAdapter adapter = new FnInputAdapter("source");
            Assert(adapter.Convert(Batch(FnInputAdapter.MaximumBatchSize)).Count == FnInputAdapter.MaximumBatchSize,
                "Exact batch bound rejected.");
            Refused<ArgumentException>(delegate { adapter.Convert(Batch(FnInputAdapter.MaximumBatchSize + 1)); });
        });
        test("enumeration failures propagate without retaining partial state", delegate
        {
            FnInputAdapter adapter = new FnInputAdapter("source");
            Refused<InvalidOperationException>(delegate { adapter.Convert(BrokenBatch()); });
            IList<InputEvent> next = adapter.Convert(new[] { new FnTransition(FnTransitionKind.Released, 8, "synthetic") });
            Assert(next.Count == 1 && next[0].Kind == InputEventKind.Released,
                "A failed mapping retained partial output or changed subsequent use.");
        });
        count = cases; return failures.ToArray();
    }
}
