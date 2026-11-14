// Copyright (c) 2026 InputLift contributors
// SPDX-License-Identifier: MIT
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using InputLift.Core;

namespace InputLift.Kanata
{
    internal static class Tests
    {
        private static int checks;
        private static readonly List<string> errors = new List<string>();
        private static void Check(string name, Action test)
        { checks++; try { test(); } catch (Exception error) { errors.Add(name + ": " + error.GetType().Name + ": " + error.Message); } }
        private static void Assert(bool condition, string reason) { if (!condition) throw new Exception(reason); }
        private static void Refused<T>(Action action) where T : Exception
        { try { action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name + "."); }
        private static int Report(string mode, bool sockets)
        {
            Console.WriteLine(new JavaScriptSerializer().Serialize(new { status = errors.Count == 0 ? "ok" : "error",
                mode = mode, tests = checks, socketsOpened = sockets, realApplicationConnected = false,
                captureStarted = false, nativeInputSent = false, errors = errors.ToArray() }));
            return errors.Count == 0 ? 0 : 1;
        }
        private sealed class Fake : IKeyTransport
        {
            internal readonly List<KeyAction> Actions = new List<KeyAction>();
            internal bool FailPress, FailRelease, Closed;
            public bool IsHealthy { get { return !Closed; } }
            public void Send(KeyAction action)
            { Actions.Add(action); if ((action == KeyAction.Press && FailPress) || (action == KeyAction.Release && FailRelease)) throw new IOException("simulated send failure"); }
            public void Dispose() { Closed = true; }
        }
        private static InputEvent Down() { return InputEvent.Button("source", "fn", true, 1); }
        private static InputEvent Up() { return InputEvent.Button("source", "fn", false, 2); }
        private static KanataConsumer Consumer(Fake fake) { return new KanataConsumer(fake, "source", "fn"); }

        internal static int Pure()
        {
            checks = 0; errors.Clear();
            Check("constructor requires transport", delegate { Refused<ArgumentNullException>(delegate { new KanataConsumer(null, "source", "fn"); }); });
            Check("logical identifier validation", delegate { Refused<ArgumentException>(delegate { ConsumerWithInvalidSource(); }); });
            Check("session mandatory", delegate { var c = Consumer(new Fake()); Refused<InvalidOperationException>(delegate { c.Accept(Down()); }); });
            Check("session begins with release and waits neutral", delegate {
                var f = new Fake(); var c = Consumer(f); c.BeginSession(); c.Accept(Down());
                Assert(f.Actions.Count == 1 && f.Actions[0] == KeyAction.Release && !c.IsHeld && !c.IsArmed, "Premature press was sent."); });
            Check("press release after neutral", delegate {
                var f = new Fake(); var c = Consumer(f); c.BeginSession(); c.Accept(Up()); c.Accept(Down()); c.Accept(Up());
                Assert(f.Actions.Count == 3 && f.Actions[1] == KeyAction.Press && f.Actions[2] == KeyAction.Release && !c.IsHeld, "Incorrect action sequence."); });
            Check("duplicate states suppressed", delegate {
                var f = new Fake(); var c = Consumer(f); c.BeginSession(); c.Accept(Up()); c.Accept(Up()); c.Accept(Down()); c.Accept(Down()); c.Accept(Up()); c.Accept(Up());
                Assert(f.Actions.Count == 3, "Duplicates sent actions."); });
            Check("selected source only", delegate {
                var f = new Fake(); var c = Consumer(f); c.BeginSession(); c.Accept(InputEvent.Button("other", "fn", false, 0)); c.Accept(Down());
                Assert(f.Actions.Count == 1, "Different source armed input."); });
            Check("selected control only", delegate {
                var f = new Fake(); var c = Consumer(f); c.BeginSession(); c.Accept(InputEvent.Button("source", "other", false, 0)); c.Accept(Down());
                Assert(f.Actions.Count == 1, "Different control armed input."); });
            Check("non-button event ignored", delegate {
                var f = new Fake(); var c = Consumer(f); c.BeginSession(); c.Accept(InputEvent.Absolute("source", "fn", 1, 0)); c.Accept(Down());
                Assert(f.Actions.Count == 1, "Value event armed input."); });
            Check("reset releases and disarms", delegate {
                var f = new Fake(); var c = Consumer(f); c.BeginSession(); c.ObserveNeutral(); c.Accept(Down()); c.Accept(InputEvent.Reset("source", 2)); c.Accept(Down());
                Assert(f.Actions.Count == 3 && !c.IsHeld && !c.IsArmed, "Reset failed to neutralize/disarm."); });
            Check("different source reset ignored", delegate {
                var f = new Fake(); var c = Consumer(f); c.BeginSession(); c.ObserveNeutral(); c.Accept(Down()); c.Accept(InputEvent.Reset("other", 2));
                Assert(c.IsHeld && f.Actions.Count == 2, "Unrelated reset released selected input."); });
            Check("reset can rearm on fresh neutral", delegate {
                var f = new Fake(); var c = Consumer(f); c.BeginSession(); c.ObserveNeutral(); c.Accept(Down()); c.Accept(InputEvent.Reset("source", 2)); c.ObserveNeutral(); c.Accept(Down());
                Assert(c.IsHeld && f.Actions.Count == 4, "Fresh neutral did not rearm."); });
            Check("neutral heartbeat recovers held release", delegate {
                var f = new Fake(); var c = Consumer(f); c.BeginSession(); c.ObserveNeutral(); c.Accept(Down()); c.ObserveNeutral();
                Assert(!c.IsHeld && f.Actions.Count == 3, "Neutral state did not recover release."); });
            Check("press failure terminal with cleanup attempt", delegate {
                var f = new Fake(); var c = Consumer(f); c.BeginSession(); c.ObserveNeutral(); f.FailPress = true;
                Refused<IOException>(delegate { c.Accept(Down()); });
                Assert(c.IsFaulted && f.Actions.Count == 3 && f.Actions[2] == KeyAction.Release, "Failed press did not attempt release.");
                Refused<InvalidOperationException>(delegate { c.Accept(Down()); }); });
            Check("release failure remains uncertain", delegate {
                var f = new Fake(); var c = Consumer(f); c.BeginSession(); c.ObserveNeutral(); c.Accept(Down()); f.FailRelease = true;
                Refused<IOException>(delegate { c.Accept(Up()); }); Assert(c.IsFaulted && c.ReleaseUnconfirmed, "Failed release falsely confirmed neutral."); });
            Check("dispose releases held and closes", delegate {
                var f = new Fake(); var c = Consumer(f); c.BeginSession(); c.ObserveNeutral(); c.Accept(Down()); c.Dispose(); c.Dispose();
                Assert(f.Closed && f.Actions.Count == 3 && !c.IsHeld, "Dispose did not release once."); });
            Check("session cannot restart", delegate { var c = Consumer(new Fake()); c.BeginSession(); Refused<InvalidOperationException>(c.BeginSession); });
            Check("disposed consumer rejects input", delegate { var c = Consumer(new Fake()); c.BeginSession(); c.Dispose(); Refused<InvalidOperationException>(delegate { c.Accept(Up()); }); });
            Check("virtual key validated", delegate { KanataTcp.ValidateKey("inputlift-fn"); Refused<ArgumentException>(delegate { KanataTcp.ValidateKey("a\"b"); }); Refused<ArgumentException>(delegate { KanataTcp.ValidateKey(new string('x', 65)); }); });
            Check("frame kinds parse", delegate { Assert(CaptureFrame.Parse("ILFN1 1 reset 0").Sequence == 1 && CaptureFrame.Parse("ILFN1 2 up 1").Neutral, "Valid frames rejected."); });
            Check("frame state contradictions refused", delegate {
                foreach (string text in new[] { "ILFN1 1 reset 1", "ILFN1 1 down 1", "ILFN1 1 up 0" }) Refused<FormatException>(delegate { CaptureFrame.Parse(text); }); });
            Check("frame malformed fields refused", delegate {
                foreach (string text in new[] { "ILFN2 1 up 1", "ILFN1 -1 up 1", "ILFN1 0 up 1", "ILFN1 1 other 0", "ILFN1 1 up 2", "ILFN1 1  up 1", "ILFN1 1 up 1 extra", new string('x', 97) }) Refused<FormatException>(delegate { CaptureFrame.Parse(text); }); });
            Check("stream begins with reset", delegate {
                var c = Consumer(new Fake()); c.BeginSession(); var frames = new CaptureFrameConsumer(c, "source", "fn"); Refused<FormatException>(delegate { frames.Accept("ILFN1 1 heartbeat 1"); }); });
            Check("stream sequence contiguous", delegate {
                var c = Consumer(new Fake()); c.BeginSession(); var frames = new CaptureFrameConsumer(c, "source", "fn"); frames.Accept("ILFN1 1 reset 0"); Refused<FormatException>(delegate { frames.Accept("ILFN1 3 heartbeat 1"); }); });
            Check("stream duplicate refused", delegate {
                var c = Consumer(new Fake()); c.BeginSession(); var frames = new CaptureFrameConsumer(c, "source", "fn"); frames.Accept("ILFN1 1 reset 0"); Refused<FormatException>(delegate { frames.Accept("ILFN1 1 reset 0"); }); });
            Check("stream neutral arm and held heartbeat", delegate {
                var f = new Fake(); var c = Consumer(f); c.BeginSession(); var frames = new CaptureFrameConsumer(c, "source", "fn");
                frames.Accept("ILFN1 1 reset 0"); frames.Accept("ILFN1 2 heartbeat 1"); frames.Accept("ILFN1 3 down 0"); frames.Accept("ILFN1 4 heartbeat 0");
                Assert(c.IsHeld && f.Actions.Count == 2, "Held heartbeat synthesized release."); frames.Accept("ILFN1 5 up 1"); Assert(!c.IsHeld, "Up did not release."); });
            Check("Kanata envelope validation", delegate {
                Assert(KanataTcp.Parse("{\"LayerChange\":{\"new\":\"base\"}}").Count == 1, "Valid envelope rejected.");
                Refused<IOException>(delegate { KanataTcp.Parse("[]"); }); Refused<IOException>(delegate { KanataTcp.Parse("{\"a\":1,\"b\":2}"); }); });
            Check("Kanata line bound", delegate { Refused<IOException>(delegate { KanataTcp.Parse(new string('x', KanataTcp.MaximumLineBytes + 1)); }); });
            return Report("pure-self-test", false);
        }
        private static void ConsumerWithInvalidSource() { new KanataConsumer(new Fake(), "private address", "fn"); }

        private sealed class Stub : IDisposable
        {
            private readonly TcpListener listener;
            private readonly Thread server;
            private readonly List<TcpClient> connections = new List<TcpClient>();
            private readonly List<Thread> handlers = new List<Thread>();
            internal readonly List<string> Actions = new List<string>();
            private readonly List<string> firstConnectionActions = new List<string>();
            internal int Port;
            internal string Failure;
            private readonly bool includeKey, errorOnPress;
            internal Stub(bool includeKey, bool errorOnPress)
            {
                this.includeKey = includeKey; this.errorOnPress = errorOnPress;
                listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(1);
                Port = ((IPEndPoint)listener.LocalEndpoint).Port;
                server = new Thread(Run); server.IsBackground = true; server.Start();
            }
            private void Run()
            {
                try
                {
                    while (true)
                    {
                        TcpClient connected = listener.AcceptTcpClient();
                        connected.ReceiveTimeout = 6000; connected.SendTimeout = 1000;
                        bool firstConnection;
                        lock (connections) firstConnection = connections.Count == 0;
                        Thread handler = new Thread(delegate() { Serve(connected, firstConnection); }); handler.IsBackground = true;
                        lock (connections) { connections.Add(connected); handlers.Add(handler); }
                        handler.Start();
                    }
                }
                catch (SocketException) { }
                catch (ObjectDisposedException) { }
                catch (Exception error) { Failure = error.GetType().Name + ": " + error.Message; }
            }
            private void Serve(TcpClient connected, bool firstConnection)
            {
                try
                {
                    using (NetworkStream stream = connected.GetStream())
                    using (StreamReader reader = new StreamReader(stream, new UTF8Encoding(false)))
                    using (StreamWriter writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" })
                    {
                        writer.WriteLine("{\"LayerChange\":{\"new\":\"base\"}}");
                        string line;
                        while ((line = reader.ReadLine()) != null)
                        {
                            IDictionary<string, object> msg = KanataTcp.Parse(line);
                            if (msg.ContainsKey("Hello")) writer.WriteLine("{\"HelloOk\":{\"version\":\"1.12.0\",\"protocol\":1,\"capabilities\":[\"fake-key\",\"fake-key-names\"]}}");
                            else if (msg.ContainsKey("RequestFakeKeyNames")) writer.WriteLine(includeKey ? "{\"FakeKeyNames\":{\"names\":[\"inputlift-fn\"]}}" : "{\"FakeKeyNames\":{\"names\":[]}}");
                            else
                            {
                                IDictionary<string, object> body = (IDictionary<string, object>)msg["ActOnFakeKey"];
                                Assert((string)body["name"] == "inputlift-fn", "Wrong virtual key on wire.");
                                string action = (string)body["action"];
                                Assert(action == "Press" || action == "Release", "Wrong action on wire.");
                                lock (Actions) { Actions.Add(action); if (firstConnection) firstConnectionActions.Add(action); }
                                if (errorOnPress && action == "Press") writer.WriteLine("{\"Error\":{\"msg\":\"simulated action error\"}}");
                            }
                        }
                    }
                }
                catch (Exception error) { if (!(error is SocketException) && !(error is IOException)) Failure = error.GetType().Name + ": " + error.Message; }
            }
            internal string[] ActionSnapshot() { lock (Actions) return Actions.ToArray(); }
            internal string[] FirstConnectionSnapshot() { lock (Actions) return firstConnectionActions.ToArray(); }
            public void Dispose()
            {
                listener.Stop(); server.Join(1500);
                lock (connections)
                {
                    foreach (TcpClient connected in connections) connected.Close();
                    foreach (Thread handler in handlers) handler.Join(1500);
                }
            }
        }
        private static void WaitUntil(Func<bool> condition)
        { Stopwatch timer = Stopwatch.StartNew(); while (!condition() && timer.ElapsedMilliseconds < 2000) Thread.Sleep(10); Assert(condition(), "Condition timed out."); }
        internal static int Transport()
        {
            checks = 0; errors.Clear();
            Check("loopback handshake and ordered exact actions", delegate {
                using (Stub stub = new Stub(true, false))
                {
                    using (KanataTcp client = KanataTcp.Connect(stub.Port, "inputlift-fn"))
                    { Assert(client.ServerVersion == "1.12.0", "Version not observed."); client.Send(KeyAction.Release); client.Send(KeyAction.Press); client.Send(KeyAction.Release); WaitUntil(delegate { return stub.ActionSnapshot().Length == 3; }); }
                    Assert(string.Join(",", stub.ActionSnapshot()) == "Release,Press,Release" && stub.Failure == null, "Incorrect wire sequence or server failure.");
                }
            });
            Check("missing configured key refused before output", delegate {
                using (Stub stub = new Stub(false, false))
                { Refused<IOException>(delegate { using (KanataTcp client = KanataTcp.Connect(stub.Port, "inputlift-fn")) { } }); Assert(stub.ActionSnapshot().Length == 0, "Missing-key handshake sent an action."); }
            });
            Check("asynchronous Kanata error faults transport", delegate {
                using (Stub stub = new Stub(true, true))
                using (KanataTcp client = KanataTcp.Connect(stub.Port, "inputlift-fn"))
                { client.Send(KeyAction.Press); WaitUntil(delegate { return !client.IsHealthy; }); Refused<IOException>(delegate { client.Send(KeyAction.Press); }); }
            });
            Check("guarded synthetic demo has normal release", delegate {
                using (Stub stub = new Stub(true, false))
                {
                    Program.GuardReport result = Program.GuardFixture(stub.Port, "demo");
                    WaitUntil(delegate { return stub.ActionSnapshot().Length >= 5; });
                    string[] actions = stub.ActionSnapshot();
                    Assert(result.status == "ok" && result.ownedChildStopped && !result.ownedChildTerminated && result.finalReleaseSent,
                        "Normal guarded demo failed: " + result.error);
                    Assert(Array.IndexOf(actions, "Press") >= 0 && actions[actions.Length - 1] == "Release", "Demo did not end in release.");
                }
            });
            Check("independent guard recovers abrupt consumer death", delegate {
                using (Stub stub = new Stub(true, false))
                {
                    Program.GuardReport result = Program.GuardFixture(stub.Port, "test-crash");
                    WaitUntil(delegate { return stub.ActionSnapshot().Length >= 5; });
                    string[] actions = stub.ActionSnapshot();
                    Assert(result.status == "error" && result.ownedChildStopped && result.finalReleaseSent &&
                        Array.IndexOf(actions, "Press") >= 0 && actions[actions.Length - 1] == "Release", "Abrupt-exit recovery not observed.");
                }
            });
            Check("guard terminates only owned stalled consumer before release", delegate {
                using (Stub stub = new Stub(true, false))
                {
                    Program.GuardReport result = Program.GuardFixture(stub.Port, "test-hang");
                    WaitUntil(delegate { return stub.ActionSnapshot().Length >= 5; });
                    string[] actions = stub.ActionSnapshot();
                    Assert(result.status == "error" && result.ownedChildStopped && result.ownedChildTerminated && result.finalReleaseSent &&
                        Array.IndexOf(actions, "Press") >= 0 && actions[actions.Length - 1] == "Release", "Hung owned-consumer recovery not observed.");
                }
            });
            Check("consumer releases if guardian heartbeat stops", delegate {
                using (Stub stub = new Stub(true, false))
                {
                    Program.GuardReport result = Program.GuardFixture(stub.Port, "test-parent-timeout");
                    WaitUntil(delegate { return stub.ActionSnapshot().Length >= 5; });
                    string[] actions = stub.ActionSnapshot();
                    Assert(result.status == "error" && result.ownedChildStopped && !result.ownedChildTerminated && result.finalReleaseSent &&
                        Array.IndexOf(actions, "Press") >= 0 && actions[actions.Length - 1] == "Release", "Consumer did not self-release after guardian heartbeat expiry.");
                }
            });
            Check("intentional guardian cancellation releases held input and succeeds", delegate {
                using (Stub stub = new Stub(true, false))
                {
                    Program.GuardReport result = Program.GuardCancellationFixture(stub.Port,
                        delegate { return Array.IndexOf(stub.ActionSnapshot(), "Press") >= 0; });
                    WaitUntil(delegate { return stub.ActionSnapshot().Length >= 5; });
                    string[] workerActions = stub.FirstConnectionSnapshot();
                    int press = Array.IndexOf(workerActions, "Press");
                    Assert(result.status == "ok" && result.error == null && result.ownedChildStopped &&
                        !result.ownedChildTerminated && result.finalReleaseSent,
                        "Intentional cancellation was reported as failure: " + result.error);
                    Assert(press >= 0 && Array.IndexOf(workerActions, "Release", press + 1) > press,
                        "Worker did not release on its original connection before graceful exit.");
                }
            });
            return Report("explicit-owned-loopback-stub", true);
        }
    }
}
