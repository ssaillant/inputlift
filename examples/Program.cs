// Copyright (c) 2026 InputLift contributors
// SPDX-License-Identifier: MIT
// Console demonstrations and pure tests. No capture, native APIs, network or file writes.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Web.Script.Serialization;
using InputLift.Core;

namespace InputLift.Examples
{
    internal static class Program
    {
        private static int checks;
        private static void Assert(bool condition, string message)
        { if (!condition) throw new InvalidOperationException(message); }
        private static void Refused<T>(Action operation) where T : Exception
        {
            try { operation(); } catch (T) { return; }
            throw new InvalidOperationException("Expected " + typeof(T).Name + ".");
        }
        private static void Test(List<string> errors, string name, Action operation)
        {
            checks++;
            try { operation(); } catch (Exception error) { errors.Add(name + ": " + error.GetType().Name + ": " + error.Message); }
        }
        private static void Feed(IEnumerable<InputEvent> events, Action<InputEvent> consumer)
        { foreach (InputEvent input in events) consumer(input); }
        private static InputEvent Press(long time) { return InputEvent.Button("source", "button", true, time); }
        private static InputEvent Release(long time) { return InputEvent.Button("source", "button", false, time); }

        private static string[] SelfTests()
        {
            List<string> errors = new List<string>(); checks = 0;
            Test(errors, "hold press/release", delegate {
                List<bool> applied = new List<bool>(); var consumer = new HoldActionConsumer("source", "button", applied.Add);
                consumer.Accept(Press(0)); Assert(consumer.IsActive, "Press did not activate.");
                consumer.Accept(Release(1)); Assert(!consumer.IsActive && applied.Count == 2 && applied[0] && !applied[1], "Release did not deactivate.");
            });
            Test(errors, "hold duplicate states", delegate {
                int calls = 0; var consumer = new HoldActionConsumer("source", "button", delegate(bool next) { calls++; });
                consumer.Accept(Release(0)); consumer.Accept(Press(1)); consumer.Accept(Press(2)); consumer.Accept(Release(3)); consumer.Accept(Release(4));
                Assert(calls == 2 && !consumer.IsActive, "Duplicate states repeated an action.");
            });
            Test(errors, "hold source isolation", delegate {
                int calls = 0; var consumer = new HoldActionConsumer("source", "button", delegate(bool next) { calls++; });
                consumer.Accept(InputEvent.Button("other", "button", true, 0)); consumer.Accept(Press(1)); consumer.Accept(InputEvent.Reset("other", 2));
                Assert(calls == 1 && consumer.IsActive, "A different source affected the hold.");
            });
            Test(errors, "hold control and kind isolation", delegate {
                int calls = 0; var consumer = new HoldActionConsumer("source", "button", delegate(bool next) { calls++; });
                consumer.Accept(InputEvent.Button("source", "other", true, 0)); consumer.Accept(InputEvent.Absolute("source", "button", 1, 1));
                Assert(calls == 0 && !consumer.IsActive, "Unselected control/type affected the hold.");
            });
            Test(errors, "hold source reset clears active state", delegate {
                List<bool> applied = new List<bool>(); var consumer = new HoldActionConsumer("source", "button", applied.Add);
                consumer.Accept(Press(0)); consumer.Accept(InputEvent.Reset("source", 1)); consumer.Accept(InputEvent.Reset("source", 2));
                Assert(applied.Count == 2 && !applied[1] && !consumer.IsActive, "Reset did not clear exactly once.");
            });
            Test(errors, "hold can rearm after reset", delegate {
                int calls = 0; var consumer = new HoldActionConsumer("source", "button", delegate(bool next) { calls++; });
                consumer.Accept(Press(0)); consumer.Accept(InputEvent.Reset("source", 1)); consumer.Accept(Press(2));
                Assert(calls == 3 && consumer.IsActive, "Reset prevented a new explicit press.");
            });
            Test(errors, "toggle once per physical press", delegate {
                List<bool> applied = new List<bool>(); var consumer = new ToggleActionConsumer("source", "button", applied.Add);
                consumer.Accept(Press(0)); consumer.Accept(Press(1)); consumer.Accept(Release(2)); consumer.Accept(Release(3)); consumer.Accept(Press(4));
                Assert(applied.Count == 2 && applied[0] && !applied[1] && !consumer.IsActive, "Toggle did not debounce held presses.");
            });
            Test(errors, "toggle release does not toggle", delegate {
                int calls = 0; var consumer = new ToggleActionConsumer("source", "button", delegate(bool next) { calls++; });
                consumer.Accept(Release(0)); consumer.Accept(Press(1)); consumer.Accept(Release(2));
                Assert(calls == 1 && consumer.IsActive, "Release changed latched state.");
            });
            Test(errors, "toggle reset fails closed", delegate {
                List<bool> applied = new List<bool>(); var consumer = new ToggleActionConsumer("source", "button", applied.Add);
                consumer.Accept(Press(0)); consumer.Accept(InputEvent.Reset("source", 1));
                Assert(!consumer.IsActive && applied.Count == 2 && !applied[1], "Reset left toggle active.");
                consumer.Accept(Press(2)); Assert(consumer.IsActive && applied.Count == 3, "Reset retained held-edge suppression.");
            });
            Test(errors, "toggle reset while already off", delegate {
                int calls = 0; var consumer = new ToggleActionConsumer("source", "button", delegate(bool next) { calls++; });
                consumer.Accept(InputEvent.Reset("source", 0)); Assert(calls == 0 && !consumer.IsActive, "Idle reset emitted an action.");
            });
            Test(errors, "toggle source/control/type isolation", delegate {
                int calls = 0; var consumer = new ToggleActionConsumer("source", "button", delegate(bool next) { calls++; });
                consumer.Accept(InputEvent.Button("other", "button", true, 0)); consumer.Accept(InputEvent.Button("source", "other", true, 1));
                consumer.Accept(InputEvent.Relative("source", "button", 1, 2)); consumer.Accept(Press(3)); consumer.Accept(InputEvent.Reset("other", 4));
                Assert(calls == 1 && consumer.IsActive, "Unselected input affected toggle.");
            });
            Test(errors, "absolute value", delegate {
                List<double> applied = new List<double>(); var consumer = new VirtualValueConsumer("source", "level", 0.25, 0.1, applied.Add);
                consumer.Accept(InputEvent.Absolute("source", "level", 0.8, 0));
                Assert(consumer.Value == 0.8 && applied.Count == 1 && applied[0] == 0.8, "Normalized absolute value was not applied.");
            });
            Test(errors, "relative signed steps", delegate {
                var consumer = new VirtualValueConsumer("source", "level", 0.5, 0.125, delegate(double next) { });
                consumer.Accept(InputEvent.Relative("source", "level", 2, 0)); Assert(consumer.Value == 0.75, "Positive steps wrong.");
                consumer.Accept(InputEvent.Relative("source", "level", -1, 1)); Assert(consumer.Value == 0.625, "Negative steps wrong.");
            });
            Test(errors, "relative Int32 extrema clamp without overflow", delegate {
                var consumer = new VirtualValueConsumer("source", "level", 0.5, 0.1, delegate(double next) { });
                consumer.Accept(InputEvent.Relative("source", "level", int.MaxValue, 0)); Assert(consumer.Value == 1, "Maximum steps did not clamp high.");
                consumer.Accept(InputEvent.Relative("source", "level", int.MinValue, 1)); Assert(consumer.Value == 0, "Minimum steps overflowed or did not clamp low.");
            });
            Test(errors, "value duplicate and clamped states", delegate {
                int calls = 0; var consumer = new VirtualValueConsumer("source", "level", 0, 0.1, delegate(double next) { calls++; });
                consumer.Accept(InputEvent.Absolute("source", "level", 0, 0)); consumer.Accept(InputEvent.Relative("source", "level", -10, 1));
                consumer.Accept(InputEvent.Absolute("source", "level", 1, 2)); consumer.Accept(InputEvent.Relative("source", "level", 10, 3));
                Assert(calls == 1 && consumer.Value == 1, "Unchanged value repeated action.");
            });
            Test(errors, "value reset to configured default", delegate {
                int calls = 0; var consumer = new VirtualValueConsumer("source", "level", 0.25, 0.1, delegate(double next) { calls++; });
                consumer.Accept(InputEvent.Absolute("source", "level", 0.8, 0)); consumer.Accept(InputEvent.Reset("source", 1)); consumer.Accept(InputEvent.Reset("source", 2));
                Assert(consumer.Value == 0.25 && calls == 2, "Reset did not restore default exactly once.");
            });
            Test(errors, "value source/control/type isolation", delegate {
                int calls = 0; var consumer = new VirtualValueConsumer("source", "level", 0.25, 0.1, delegate(double next) { calls++; });
                consumer.Accept(InputEvent.Absolute("other", "level", 1, 0)); consumer.Accept(InputEvent.Relative("source", "other", 1, 1));
                consumer.Accept(InputEvent.Button("source", "level", true, 2)); consumer.Accept(InputEvent.Reset("other", 3));
                Assert(calls == 0 && consumer.Value == 0.25, "Unselected input affected value.");
            });
            Test(errors, "consumer invalid configuration and null input", delegate {
                Refused<ArgumentException>(delegate { new HoldActionConsumer("bad source", "button", delegate(bool next) { }); });
                Refused<ArgumentNullException>(delegate { new HoldActionConsumer("source", "button", null); });
                Refused<ArgumentNullException>(delegate { new ToggleActionConsumer("source", "button", null); });
                Refused<ArgumentOutOfRangeException>(delegate { new VirtualValueConsumer("source", "level", double.NaN, 0.1, delegate(double next) { }); });
                Refused<ArgumentOutOfRangeException>(delegate { new VirtualValueConsumer("source", "level", 0.5, double.PositiveInfinity, delegate(double next) { }); });
                Refused<ArgumentOutOfRangeException>(delegate { new VirtualValueConsumer("source", "level", 0.5, 0, delegate(double next) { }); });
                var consumer = new HoldActionConsumer("source", "button", delegate(bool next) { });
                Refused<ArgumentNullException>(delegate { consumer.Accept(null); });
            });
            Test(errors, "stream preserves typed fields and reset scope", delegate {
                StringWriter writer = new StringWriter(CultureInfo.InvariantCulture); EventStream.Write(writer, SyntheticProfiles.All());
                string[] lines = writer.ToString().Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                Assert(lines.Length == 5, "Wrong stream record count."); var serializer = new JavaScriptSerializer();
                var down = serializer.Deserialize<Dictionary<string, object>>(lines[0]); var delta = serializer.Deserialize<Dictionary<string, object>>(lines[3]);
                var reset = serializer.Deserialize<Dictionary<string, object>>(lines[4]);
                Assert((string)down["kind"] == "Pressed" && down["value"] == null && down["steps"] == null, "Button stream fields incorrect.");
                Assert((string)delta["kind"] == "Delta" && Convert.ToInt32(delta["steps"], CultureInfo.InvariantCulture) == -1, "Delta payload lost.");
                Assert((string)reset["kind"] == "Reset" && reset["controlId"] == null && (string)reset["sourceId"] == SyntheticProfiles.Source, "Reset scope lost.");
            });
            Test(errors, "stream rejects null boundaries", delegate {
                Refused<ArgumentNullException>(delegate { EventStream.Write(null, SyntheticProfiles.All()); });
                Refused<ArgumentNullException>(delegate { EventStream.Write(new StringWriter(), null); });
                Refused<ArgumentException>(delegate { EventStream.Write(new StringWriter(), new InputEvent[] { null }); });
            });
            Test(errors, "stream preserves full opaque timestamp precision", delegate {
                StringWriter writer = new StringWriter(CultureInfo.InvariantCulture);
                EventStream.Write(writer, new[] { InputEvent.Button("source", "button", true, long.MaxValue), InputEvent.Reset("source", long.MinValue) });
                string[] lines = writer.ToString().Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                var serializer = new JavaScriptSerializer();
                Assert((string)serializer.Deserialize<Dictionary<string, object>>(lines[0])["timestamp"] == long.MaxValue.ToString(CultureInfo.InvariantCulture), "Maximum timestamp lost JSON precision.");
                Assert((string)serializer.Deserialize<Dictionary<string, object>>(lines[1])["timestamp"] == long.MinValue.ToString(CultureInfo.InvariantCulture), "Minimum timestamp lost JSON precision.");
            });
            Test(errors, "all synthetic demos finish in safe state", delegate {
                var hold = new HoldActionConsumer(SyntheticProfiles.Source, SyntheticProfiles.Button, delegate(bool next) { });
                var toggle = new ToggleActionConsumer(SyntheticProfiles.Source, SyntheticProfiles.Button, delegate(bool next) { });
                var value = new VirtualValueConsumer(SyntheticProfiles.Source, SyntheticProfiles.Value, 0.25, 0.125, delegate(double next) { });
                Feed(SyntheticProfiles.Hold(), hold.Accept); Feed(SyntheticProfiles.Toggle(), toggle.Accept); Feed(SyntheticProfiles.Values(), value.Accept);
                Assert(!hold.IsActive && !toggle.IsActive && value.Value == 0.25, "A demonstration retained active state.");
            });
            return errors.ToArray();
        }

        private static void RunDemo(string name)
        {
            if (name == "hold" || name == "all") {
                Console.WriteLine("Synthetic hold-to-act callback (no real action):");
                var hold = new HoldActionConsumer(SyntheticProfiles.Source, SyntheticProfiles.Button,
                    delegate(bool active) { Console.WriteLine(active ? "active" : "inactive"); });
                Feed(SyntheticProfiles.Hold(), hold.Accept);
            }
            if (name == "toggle" || name == "all") {
                Console.WriteLine("Synthetic toggle callback (no real action):");
                var toggle = new ToggleActionConsumer(SyntheticProfiles.Source, SyntheticProfiles.Button,
                    delegate(bool active) { Console.WriteLine(active ? "on" : "off"); });
                Feed(SyntheticProfiles.Toggle(), toggle.Accept);
            }
            if (name == "dial" || name == "all") {
                Console.WriteLine("Synthetic normalized value and dial steps (virtual value only):");
                var value = new VirtualValueConsumer(SyntheticProfiles.Source, SyntheticProfiles.Value, 0.25, 0.125,
                    delegate(double next) { Console.WriteLine(next.ToString("0.###", CultureInfo.InvariantCulture)); });
                Feed(SyntheticProfiles.Values(), value.Accept);
            }
            if (name == "stream" || name == "all") EventStream.Write(Console.Out, SyntheticProfiles.All());
        }
        private static int Main(string[] args)
        {
            if (args.Length == 1 && args[0] == "--self-test") {
                string[] errors = SelfTests(); Console.WriteLine(new JavaScriptSerializer().Serialize(new {
                    status = errors.Length == 0 ? "ok" : "error", checks = checks, errors = errors,
                    liveActionsPerformed = false, syntheticEventsOnly = true
                })); return errors.Length == 0 ? 0 : 1;
            }
            if (args.Length == 2 && args[0] == "--demo" &&
                (args[1] == "all" || args[1] == "hold" || args[1] == "toggle" || args[1] == "dial" || args[1] == "stream"))
            { RunDemo(args[1]); return 0; }
            Console.WriteLine("Use --demo all|hold|toggle|dial|stream, or --self-test. All events are synthetic; no hardware or applications are controlled.");
            return args.Length == 1 && args[0] == "--help" ? 0 : 64;
        }
    }
}
