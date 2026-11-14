// Copyright (c) 2026 InputLift contributors
// SPDX-License-Identifier: MIT
// Native named-pipe fixture, intended ONLY for disposable hosted Windows CI.
// No Bluetooth, registry, input injection, application connection or installation.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using InputLift.Kanata;

internal static class NativePipeTests
{
    private sealed class Options
    {
        internal string Mode, ReaderSid, Nonce, Scenario;
        internal int ServerPid;
        internal long ServerStart;
    }
    private sealed class FakeTransport : IKeyTransport
    {
        internal readonly List<KeyAction> Actions = new List<KeyAction>();
        public bool IsHealthy { get { return true; } }
        public void Send(KeyAction action) { Actions.Add(action); }
        public void Dispose() { }
    }
    private static readonly JavaScriptSerializer Json = new JavaScriptSerializer();
    private static bool Admin()
    { return new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator); }
    private static void Check(bool value, string message)
    { if (!value) throw new InvalidOperationException(message); }
    private static string Need(Dictionary<string, string> values, string key)
    { string value; if (!values.TryGetValue(key, out value)) throw new ArgumentException("Missing " + key + "."); return value; }
    private static Options Parse(string[] args)
    {
        if (args.Length < 1 || (args[0] != "--server" && args[0] != "--client"))
            throw new ArgumentException("Use only the documented CI server/client fixture modes.");
        Dictionary<string, string> values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 1; i < args.Length; i += 2)
        {
            if (i + 1 == args.Length || values.ContainsKey(args[i])) throw new ArgumentException("Fixture arguments require unique pairs.");
            if (args[i] != "--reader-sid" && args[i] != "--nonce" && args[i] != "--scenario" && args[i] != "--server-pid" && args[i] != "--server-start")
                throw new ArgumentException("Unknown fixture argument.");
            values.Add(args[i], args[i + 1]);
        }
        Options options = new Options { Mode = args[0], ReaderSid = Need(values, "--reader-sid"), Nonce = Need(values, "--nonce"), Scenario = Need(values, "--scenario") };
        if (new SecurityIdentifier(options.ReaderSid).Value != options.ReaderSid) throw new ArgumentException("Canonical temporary-reader SID required.");
        if (!DailyPaths.ValidNonce(options.Nonce) || options.Nonce != options.Nonce.ToLowerInvariant()) throw new ArgumentException("Canonical lower-case fixture nonce required.");
        if (options.Scenario != "ordered" && options.Scenario != "denied-write" && options.Scenario != "idle-dispose" &&
            options.Scenario != "unarmed-down" && options.Scenario != "blocked-reader") throw new ArgumentException("Unknown fixed fixture scenario.");
        if (options.Mode == "--client")
        {
            if (!int.TryParse(Need(values, "--server-pid"), out options.ServerPid) || options.ServerPid <= 0 ||
                !long.TryParse(Need(values, "--server-start"), out options.ServerStart) || options.ServerStart <= 0)
                throw new ArgumentException("Positive server identity required.");
        }
        return options;
    }
    private static int Main(string[] args)
    {
        try
        {
            Check(Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true" &&
                Environment.GetEnvironmentVariable("RUNNER_ENVIRONMENT") == "github-hosted" &&
                Environment.GetEnvironmentVariable("RUNNER_OS") == "Windows", "Native fixture is restricted to disposable GitHub-hosted Windows CI.");
            Options options = Parse(args);
            return options.Mode == "--server" ? Server(options) : Client(options);
        }
        catch (Exception error)
        {
            Console.WriteLine(Json.Serialize(new { phase = "result", status = "error", error = error.GetType().Name + ": " + error.Message,
                bluetoothCaptureStarted = false, nativeInputSent = false, installedDeploymentValidated = false, remoteSmbBehaviorValidated = false }));
            return 1;
        }
    }
    private static void WaitUntil(Func<bool> condition, int timeout, string failure)
    {
        Stopwatch timer = Stopwatch.StartNew();
        while (!condition() && timer.ElapsedMilliseconds < timeout) Thread.Sleep(10);
        Check(condition(), failure);
    }
    private static void Control(string expected)
    {
        Task<string> line = Console.In.ReadLineAsync();
        Check(line.Wait(15000) && line.Result == expected, "Owning CI orchestrator did not provide its fixed lifecycle signal.");
    }
    private static void Finish() { Control("finish"); }
    private static NamedPipeServerStream Stream(FnEventPipe producer)
    {
        FieldInfo field = typeof(FnEventPipe).GetField("pipe", BindingFlags.NonPublic | BindingFlags.Instance);
        Check(field != null, "Production pipe implementation no longer exposes the fixture's inspected field.");
        return (NamedPipeServerStream)field.GetValue(producer);
    }
    private static void ActualAcl(FnEventPipe producer, string readerSid)
    {
        PipeSecurity security = Stream(producer).GetAccessControl();
        Check(security.AreAccessRulesProtected, "Native pipe DACL must not inherit additional principals.");
        Check(((SecurityIdentifier)security.GetOwner(typeof(SecurityIdentifier))).Value == "S-1-5-32-544", "Native pipe owner must be Administrators.");
        int selected = 0, system = 0, administrators = 0;
        foreach (PipeAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            Check(rule.AccessControlType == AccessControlType.Allow && !rule.IsInherited, "Unexpected deny/inherited native pipe rule.");
            string sid = ((SecurityIdentifier)rule.IdentityReference).Value;
            uint rights = unchecked((uint)rule.PipeAccessRights);
            if (sid == readerSid)
            {
                selected++;
                // FILE_WRITE_DATA/CREATE_PIPE_INSTANCE/WRITE_EA/WRITE_ATTRIBUTES/
                // DELETE/WRITE_DAC/WRITE_OWNER must not be granted to the reader.
                const uint forbidden = 2 | 4 | 16 | 256 | 65536 | 262144 | 524288;
                Check(((rights & 1) != 0 || (rights & 0x80000000U) != 0) && (rights & forbidden) == 0 &&
                    (rights & (0x40000000U | 0x10000000U)) == 0, "Selected reader must have read-data and no write/create/ownership rights.");
            }
            else if (sid == "S-1-5-18") system++;
            else if (sid == "S-1-5-32-544") administrators++;
            else throw new InvalidOperationException("Unexpected principal in the actual native pipe DACL.");
        }
        Check(selected == 1 && system == 1 && administrators == 1, "Expected exactly selected-reader, SYSTEM and administrator grants.");
    }
    private static void FirstInstance(string nonce)
    {
        bool denied = false;
        try { using (FnEventPipe duplicate = new FnEventPipe(nonce)) { } }
        catch (System.ComponentModel.Win32Exception error) { denied = error.NativeErrorCode == 5; }
        Check(denied, "A second production first-instance pipe must be rejected with access denied.");
    }
    private static int Server(Options options)
    {
        Check(Admin(), "Only the disposable runner administrator may create the fixture producer.");
        Check(Console.IsInputRedirected && Console.IsOutputRedirected, "The fixture server requires its owning CI process pipes.");
        DailyPaths.ReaderSid = options.ReaderSid;
        bool disconnected = false, timeoutStopped = false, noReplay = false, disposalBounded = false;
        using (FnEventPipe producer = new FnEventPipe(options.Nonce))
        {
            ActualAcl(producer, options.ReaderSid);
            FirstInstance(options.Nonce);
            // Disconnected publication is deliberately not queued for a later reader.
            producer.Publish("heartbeat", true); producer.Publish("down", false); producer.Publish("up", true);
            using (Process process = Process.GetCurrentProcess())
                Console.WriteLine(Json.Serialize(new { phase = "ready", scenario = options.Scenario, pid = process.Id,
                    startUtcTicks = process.StartTime.ToUniversalTime().Ticks, pipe = producer.Name }));
            Console.Out.Flush();
            if (options.Scenario == "idle-dispose" || options.Scenario == "denied-write")
            {
                Finish();
                Check(!producer.Connected, "A non-reading/denied client unexpectedly became the selected read consumer.");
            }
            else
            {
                WaitUntil(delegate { return producer.Connected; }, 5000, "Selected CI client did not connect within five seconds.");
                WaitUntil(delegate { return producer.TakeConnectionReset(); }, 1000, "Fresh connection reset was not delivered.");
                Check(!producer.TakeConnectionReset(), "Exactly one fresh connection reset must be delivered.");
                noReplay = true; // Client separately verifies first reset/unarmed state.
                // The CI orchestrator waits until the reader has authenticated
                // this still-open server, then sends begin on THIS private test
                // process stdin. Nothing is accepted through the outbound Fn pipe.
                Control("begin");
                if (options.Scenario == "ordered")
                {
                    producer.Publish("heartbeat", true); producer.Publish("down", false); producer.Publish("up", true);
                    producer.Publish("reset", false); producer.Publish("heartbeat", true); producer.Publish("down", false); producer.Publish("up", true);
                    Finish();
                    WaitUntil(delegate { return producer.Failure != null && !producer.Connected; }, 2000, "Disconnected consumer did not stop the producer.");
                    disconnected = true;
                }
                else if (options.Scenario == "unarmed-down")
                {
                    producer.Publish("down", false);
                    WaitUntil(delegate { return producer.Failure != null && !producer.Connected; }, 2000, "Unarmed down was not rejected by the wire writer.");
                    Check(producer.Failure.Contains("ordered neutral baseline"), "Unarmed down failed at an unexpected stage.");
                }
                else
                {
                    producer.Publish("heartbeat", true);
                    Stopwatch fill = Stopwatch.StartNew();
                    bool down = false;
                    while (producer.Failure == null && fill.ElapsedMilliseconds < 5500)
                    {
                        down = !down; producer.Publish(down ? "down" : "up", !down); Thread.Sleep(10);
                    }
                    Check(producer.Failure != null, "A blocked native reader did not produce a bounded stop.");
                    Check(producer.Failure.Contains("500 milliseconds") || producer.Failure.Contains("queue overflow"), "Blocked reader failed at an unexpected stage.");
                    timeoutStopped = true;
                }
            }
            Stopwatch stop = Stopwatch.StartNew(); producer.Dispose();
            Check(stop.ElapsedMilliseconds < 2000, "Native producer disposal exceeded its bounded shutdown allowance.");
            disposalBounded = true;
        }
        if (options.Scenario == "unarmed-down" || options.Scenario == "blocked-reader") Finish();
        // Recreating after disposal proves that the owned named handle was closed.
        using (FnEventPipe recreated = new FnEventPipe(options.Nonce)) { }
        Console.WriteLine(Json.Serialize(new { phase = "result", status = "ok", scenario = options.Scenario,
            actualKernelAclVerified = true, firstInstanceDenied = true, nativeHandleClosed = true,
            disposalBounded = disposalBounded, freshConnectionReset = noReplay, disconnectedConsumerStopped = disconnected,
            blockedReaderStopped = timeoutStopped, bluetoothCaptureStarted = false, nativeInputSent = false,
            installedDeploymentValidated = false, productionOwnerBindingValidated = false, remoteSmbBehaviorValidated = false }));
        return 0;
    }
    private static string ReadLine(NamedPipeClientStream pipe, int timeout)
    {
        List<byte> bytes = new List<byte>(); Stopwatch timer = Stopwatch.StartNew();
        byte[] one = new byte[1];
        while (timer.ElapsedMilliseconds < timeout)
        {
            Task<int> read = pipe.ReadAsync(one, 0, 1);
            if (!read.Wait(Math.Max(1, timeout - (int)timer.ElapsedMilliseconds))) throw new TimeoutException("Native fixture frame read exceeded its deadline.");
            if (read.Result == 0) { Check(bytes.Count == 0, "Truncated native frame."); return null; }
            byte value = one[0];
            if (value == 10) return Encoding.ASCII.GetString(bytes.ToArray());
            Check(value >= 32 && value <= 126 && bytes.Count < 96, "Native producer emitted a non-ASCII or oversized frame.");
            bytes.Add(value);
        }
        throw new TimeoutException("Native fixture frame read exceeded its deadline.");
    }
    private static bool IdentityPrimitive(NamedPipeClientStream pipe, Options options)
    {
        Type program = typeof(KanataConsumer).Assembly.GetType("InputLift.Kanata.Program", true);
        MethodInfo pidQuery = program.GetMethod("GetNamedPipeServerProcessId", BindingFlags.NonPublic | BindingFlags.Static);
        object[] query = new object[] { pipe.SafePipeHandle, (uint)0 };
        Check(pidQuery != null && (bool)pidQuery.Invoke(null, query) && (uint)query[1] == options.ServerPid, "Actual pipe server PID differs from the selected fixture process.");
        MethodInfo identity = program.GetMethod("RequireProcessIdentity", BindingFlags.NonPublic | BindingFlags.Static);
        Check(identity != null, "Production limited-process-query helper was not found.");
        string image = Process.GetCurrentProcess().MainModule.FileName;
        try { identity.Invoke(null, new object[] { options.ServerPid, options.ServerStart, image }); }
        catch (TargetInvocationException error)
        {
            // CI uses a different standard-account SID. Production uses the same
            // account at different integrity levels, so a cross-account default
            // process-DACL denial is reported, not mislabelled as installation proof.
            if (error.InnerException is IOException && error.InnerException.Message.Contains("Win32 5.")) return false;
            throw;
        }
        bool wrongStart = false, wrongImage = false;
        try { identity.Invoke(null, new object[] { options.ServerPid, options.ServerStart + 1, image }); }
        catch (TargetInvocationException error) { wrongStart = error.InnerException is IOException; }
        try { identity.Invoke(null, new object[] { options.ServerPid, options.ServerStart, image + ".wrong" }); }
        catch (TargetInvocationException error) { wrongImage = error.InnerException is IOException; }
        Check(wrongStart && wrongImage, "Identity helper did not reject mismatched process epoch/image.");
        return true;
    }
    private static int Client(Options options)
    {
        Check(!Admin(), "Native fixture reader must be a real standard account.");
        using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
            Check(identity.User != null && identity.User.Value == options.ReaderSid, "Reader identity differs from the selected temporary account.");
        string name = FnEventPipe.PipeName(options.Nonce);
        bool writeDenied = false, identityValidated = false, ordered = false, unarmedRejected = false;
        int frames = 0;
        if (options.Scenario == "denied-write")
        {
            try { using (NamedPipeClientStream attempt = new NamedPipeClientStream(".", name, PipeDirection.Out, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification)) { attempt.Connect(1500); } }
            catch (UnauthorizedAccessException) { writeDenied = true; }
            Check(writeDenied, "Selected reader's write connection was not denied.");
        }
        else
        {
            using (NamedPipeClientStream pipe = new NamedPipeClientStream(".", name, PipeDirection.In, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification))
            {
                pipe.Connect(1500); identityValidated = IdentityPrimitive(pipe, options);
                Console.WriteLine(Json.Serialize(new { phase = "connected", scenario = options.Scenario,
                    standardAccountConfirmed = true, pipeServerPidVerified = true }));
                Console.Out.Flush();
                if (options.Scenario == "blocked-reader") Thread.Sleep(6500);
                else
                {
                    FakeTransport output = new FakeTransport();
                    using (KanataConsumer consumer = new KanataConsumer(output, "pipe-fixture", "fn"))
                    {
                        consumer.BeginSession(); CaptureFrameConsumer accepted = new CaptureFrameConsumer(consumer, "pipe-fixture", "fn");
                        bool initial = true; int downs = 0, ups = 0;
                        Stopwatch all = Stopwatch.StartNew();
                        while (all.ElapsedMilliseconds < 8000)
                        {
                            string line = ReadLine(pipe, 2500);
                            if (line == null) { Check(options.Scenario == "unarmed-down", "Unexpected native producer EOF."); break; }
                            CaptureFrame frame = CaptureFrame.Parse(line);
                            if (initial) Check(frame.Sequence == 1 && frame.Kind == "reset" && !frame.Neutral, "A new native consumer inherited buffered preconnection state.");
                            initial = false; accepted.Accept(line); frames++;
                            if (frame.Kind == "down") downs++;
                            if (frame.Kind == "up") ups++;
                            if (options.Scenario == "ordered" && ups == 2) break;
                        }
                        if (options.Scenario == "unarmed-down")
                        {
                            Check(downs == 0 && !consumer.IsHeld && output.Actions.Count == 1, "Unarmed down escaped onto the native pipe.");
                            unarmedRejected = true;
                        }
                        else
                        {
                            Check(downs == 2 && ups == 2 && !consumer.IsHeld && output.Actions.Count == 5,
                                "Native reset/baseline/press/release order did not reach the actual consumer correctly.");
                            Check(output.Actions[0] == KeyAction.Release && output.Actions[1] == KeyAction.Press &&
                                output.Actions[2] == KeyAction.Release && output.Actions[3] == KeyAction.Press && output.Actions[4] == KeyAction.Release,
                                "Native frame sequence produced unexpected consumer actions.");
                            ordered = true;
                        }
                    }
                }
            }
        }
        Console.WriteLine(Json.Serialize(new { phase = "result", status = "ok", scenario = options.Scenario,
            standardAccountConfirmed = true, selectedReaderConnected = options.Scenario != "denied-write", writeConnectionDenied = writeDenied,
            pipeServerPidVerified = options.Scenario != "denied-write", limitedQueryIdentityPrimitiveValidated = identityValidated,
            identityPrimitiveCrossAccountDenied = options.Scenario != "denied-write" && !identityValidated,
            orderedFramesReachedActualConsumer = ordered, unarmedDownRejected = unarmedRejected, frames = frames,
            bluetoothCaptureStarted = false, nativeInputSent = false, installedDeploymentValidated = false,
            productionOwnerBindingValidated = false, remoteSmbBehaviorValidated = false }));
        return 0;
    }
}
