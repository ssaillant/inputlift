// Copyright (c) 2026 InputLift contributors
// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using Microsoft.Win32.SafeHandles;

namespace InputLift.Kanata
{
    internal static class Program
    {
        private const string Source = "recovered-control", Control = "fn";
        private static volatile bool cancelled;
        private sealed class Options
        {
            internal string Mode, Key, Pipe, Token, WorkerKind;
            internal int Port, CapturePid, GuardianPid;
            internal long CaptureStart, GuardianStart;
            internal bool TestWithholdGuardianHeartbeat;
        }
        internal sealed class GuardReport
        {
            public string status, mode, error;
            public bool ownedChildStopped, ownedChildTerminated, finalReleaseSent;
            public bool exampleBaseObserved, exampleLiftedObserved, exampleReturnedToBaseObserved;
            public bool applicationEffectsAcknowledged = false, automaticRetry = false;
        }
        private static string Json(object value) { return new JavaScriptSerializer().Serialize(value); }
        private static int Main(string[] args)
        {
            try
            {
                if (args.Length == 1 && args[0] == "--self-test") return Tests.Pure();
                if (args.Length == 1 && args[0] == "--test-transport") return Tests.Transport();
                Options options = Parse(args);
                if (options.Mode == "--worker") return Worker(options);
                RequireNormalUser();
                GuardReport report = Guard(options); Console.WriteLine(Json(report));
                return report.status == "ok" ? 0 : 1;
            }
            catch (Exception error)
            {
                TextWriter output = args.Length != 0 && args[0] == "--worker" ? Console.Error : Console.Out;
                output.WriteLine(Json(new { status = "error", error = error.GetType().Name + ": " + error.Message,
                    automaticRetry = false, applicationEffectsAcknowledged = false }));
                return 1;
            }
        }
        private static void RequireNormalUser()
        {
            if (new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
                throw new InvalidOperationException("Run the Kanata consumer as your normal user, outside administrator PowerShell.");
        }
        private static Options Parse(string[] args)
        {
            if (args.Length == 0 || (args[0] != "--demo" && args[0] != "--bridge" && args[0] != "--worker"))
                throw new ArgumentException("Use --self-test, --test-transport, --demo --port N --virtual-key NAME, or --bridge with authenticated capture parameters.");
            Options result = new Options { Mode = args[0] };
            Dictionary<string, string> values = new Dictionary<string, string>(StringComparer.Ordinal);
            for (int i = 1; i < args.Length; i += 2)
            {
                if (i + 1 == args.Length || values.ContainsKey(args[i])) throw new ArgumentException("Arguments require unique name/value pairs.");
                string name = args[i];
                if (name != "--port" && name != "--virtual-key" && name != "--capture-pipe" && name != "--capture-pid" &&
                    name != "--capture-start" && name != "--guard-token" && name != "--guard-pid" && name != "--guard-start" && name != "--worker-kind")
                    throw new ArgumentException("Unknown argument.");
                values.Add(name, args[i + 1]);
            }
            result.Port = IntValue(values, "--port");
            result.Key = Value(values, "--virtual-key"); KanataTcp.ValidateKey(result.Key);
            if (result.Port < 1024 || result.Port > 65535) throw new ArgumentOutOfRangeException("port");
            bool live = result.Mode == "--bridge";
            if (result.Mode == "--worker")
            {
                result.Token = Value(values, "--guard-token"); RequireHex(result.Token);
                result.GuardianPid = IntValue(values, "--guard-pid"); result.GuardianStart = LongValue(values, "--guard-start");
                result.WorkerKind = Value(values, "--worker-kind");
                if (result.WorkerKind != "demo" && result.WorkerKind != "bridge" && result.WorkerKind != "test-crash" &&
                    result.WorkerKind != "test-hang" && result.WorkerKind != "test-parent-timeout") throw new ArgumentException("Invalid worker kind.");
                live = result.WorkerKind == "bridge";
                if (!Console.IsInputRedirected || !Console.IsOutputRedirected) throw new ArgumentException("Worker mode requires the owning supervisor's inherited pipes.");
            }
            else foreach (string name in values.Keys)
                if (name.StartsWith("--guard-", StringComparison.Ordinal) || name == "--worker-kind") throw new ArgumentException("Worker handoff options are not accepted by a supervisor.");
            if (live)
            {
                result.Pipe = Value(values, "--capture-pipe");
                if (!result.Pipe.StartsWith("InputLift.Fn.", StringComparison.Ordinal)) throw new ArgumentException("Invalid capture pipe name.");
                RequireHex(result.Pipe.Substring("InputLift.Fn.".Length));
                result.CapturePid = IntValue(values, "--capture-pid"); result.CaptureStart = LongValue(values, "--capture-start");
            }
            else if (values.ContainsKey("--capture-pipe") || values.ContainsKey("--capture-pid") || values.ContainsKey("--capture-start"))
                throw new ArgumentException("Synthetic demo does not accept capture parameters.");
            return result;
        }
        private static string Value(Dictionary<string, string> values, string name)
        { string value; if (!values.TryGetValue(name, out value)) throw new ArgumentException("Missing " + name + "."); return value; }
        private static long LongValue(Dictionary<string, string> values, string name)
        { long value; if (!long.TryParse(Value(values, name), NumberStyles.None, CultureInfo.InvariantCulture, out value) || value <= 0) throw new ArgumentException("Invalid " + name + "."); return value; }
        private static int IntValue(Dictionary<string, string> values, string name)
        { long value = LongValue(values, name); if (value > int.MaxValue) throw new ArgumentException("Invalid " + name + "."); return (int)value; }
        private static void RequireHex(string token)
        { if (token.Length != 32) throw new ArgumentException("Expected a 32-digit handoff identifier."); foreach (char c in token) if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))) throw new ArgumentException("Invalid handoff identifier."); }
        private static string Quote(string value)
        { foreach (char c in value) if (!(char.IsLetterOrDigit(c) || c == '-' || c == '.' || c == '_')) throw new ArgumentException("Unsafe child argument."); return value; }
        private static string WorkerArguments(Options options, string token, Process guardian)
        {
            string kind = options.WorkerKind ?? (options.Mode == "--demo" ? "demo" : "bridge");
            string result = "--worker --port " + options.Port.ToString(CultureInfo.InvariantCulture) + " --virtual-key " + Quote(options.Key) +
                " --guard-token " + token + " --guard-pid " + guardian.Id.ToString(CultureInfo.InvariantCulture) +
                " --guard-start " + guardian.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture) + " --worker-kind " + kind;
            if (options.Mode == "--bridge") result += " --capture-pipe " + Quote(options.Pipe) +
                " --capture-pid " + options.CapturePid.ToString(CultureInfo.InvariantCulture) + " --capture-start " + options.CaptureStart.ToString(CultureInfo.InvariantCulture);
            return result;
        }
        private static string ReadBoundedLine(TextReader reader, int bound)
        {
            StringBuilder line = new StringBuilder();
            while (true)
            {
                int c = reader.Read();
                if (c < 0) { if (line.Length != 0) throw new IOException("Truncated child frame."); return null; }
                if (c == '\n') return line.ToString();
                if (c == '\r') continue;
                if (c < 32 || c > 126 || line.Length == bound) throw new IOException("Invalid or oversized child frame.");
                line.Append((char)c);
            }
        }
        private sealed class ChildStatus
        {
            internal readonly object Gate = new object();
            internal bool Ended;
            internal string Failure, ErrorLine;
            internal long LastProgress;
        }
        internal static GuardReport GuardFixture(int port, string scenario)
        {
            // This internal entry point is called only by --test-transport,
            // after it creates an owned stub on loopback. Hosted Windows runners
            // may be administrators. Production --demo/--bridge still pass
            // RequireNormalUser in Main; no real application is used here.
            return Guard(new Options { Mode = "--demo", Port = port, Key = "inputlift-fn", WorkerKind = scenario,
                TestWithholdGuardianHeartbeat = scenario == "test-parent-timeout" });
        }
        private static GuardReport Guard(Options options)
        {
            cancelled = false;
            Console.CancelKeyPress += delegate(object sender, ConsoleCancelEventArgs e) { cancelled = true; e.Cancel = true; };
            GuardReport report = new GuardReport { status = "ok", mode = options.Mode.Substring(2) };
            Process child = null; Thread reader = null, errorReader = null; Exception failure = null;
            ChildStatus incoming = new ChildStatus();
            try
            {
                string token = Guid.NewGuid().ToString("N");
                using (Process guardian = Process.GetCurrentProcess())
                    child = new Process { StartInfo = new ProcessStartInfo(guardian.MainModule.FileName, WorkerArguments(options, token, guardian))
                    { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true } };
                if (!child.Start()) throw new IOException("Could not start the owned output-consumer child.");
                child.StandardInput.AutoFlush = true; child.StandardInput.WriteLine("ILGUARD1 " + token);
                incoming.LastProgress = Stopwatch.GetTimestamp();
                reader = new Thread(delegate()
                {
                    try
                    {
                        string line; long previous = 0;
                        while ((line = ReadBoundedLine(child.StandardOutput, 64)) != null)
                        {
                            if (line.StartsWith("ILRESULT1 ", StringComparison.Ordinal))
                            {
                                string[] flags = line.Split(' ');
                                if (flags.Length != 4) throw new IOException("Invalid consumer observation frame.");
                                for (int i = 1; i < flags.Length; i++) if (flags[i] != "0" && flags[i] != "1") throw new IOException("Invalid consumer observation flag.");
                                report.exampleBaseObserved = flags[1] == "1";
                                report.exampleLiftedObserved = flags[2] == "1";
                                report.exampleReturnedToBaseObserved = flags[3] == "1";
                                continue;
                            }
                            long sequence;
                            if (!line.StartsWith("ILWATCH1 ", StringComparison.Ordinal) ||
                                !long.TryParse(line.Substring(9), NumberStyles.None, CultureInfo.InvariantCulture, out sequence) || sequence != previous + 1)
                                throw new IOException("Invalid owned-consumer progress frame.");
                            previous = sequence; Interlocked.Exchange(ref incoming.LastProgress, Stopwatch.GetTimestamp());
                        }
                    }
                    catch (Exception error) { lock (incoming.Gate) incoming.Failure = error.GetType().Name + ": " + error.Message; }
                    finally { lock (incoming.Gate) incoming.Ended = true; }
                });
                reader.IsBackground = true; reader.Name = "InputLift consumer progress"; reader.Start();
                errorReader = new Thread(delegate()
                {
                    try
                    {
                        string line = ReadBoundedLine(child.StandardError, 2048);
                        lock (incoming.Gate) incoming.ErrorLine = line;
                        if (ReadBoundedLine(child.StandardError, 2048) != null) lock (incoming.Gate) incoming.Failure = "Unexpected additional child error output.";
                    }
                    catch (Exception error) { lock (incoming.Gate) incoming.ErrorLine = error.GetType().Name + ": " + error.Message; }
                });
                errorReader.IsBackground = true; errorReader.Name = "InputLift bounded child error"; errorReader.Start();
                Stopwatch time = Stopwatch.StartNew(); long lastHeartbeat = -250;
                while (!cancelled && !child.HasExited)
                {
                    lock (incoming.Gate) if (incoming.Failure != null) throw new IOException(incoming.Failure);
                    double age = (Stopwatch.GetTimestamp() - Interlocked.Read(ref incoming.LastProgress)) * 1000.0 / Stopwatch.Frequency;
                    if (age > 2500) throw new TimeoutException("Owned output-consumer heartbeat expired.");
                    if (time.ElapsedMilliseconds - lastHeartbeat >= 250 &&
                        (!options.TestWithholdGuardianHeartbeat || time.ElapsedMilliseconds < 800))
                    {
                        // At most ten short writes precede a liveness timeout. The
                        // consumer's dedicated input thread drains this private pipe.
                        child.StandardInput.WriteLine("ILGUARD1 heartbeat"); lastHeartbeat = time.ElapsedMilliseconds;
                    }
                    Thread.Sleep(20);
                }
            }
            catch (Exception error) { failure = error; }
            finally
            {
                if (child != null)
                {
                    try { child.StandardInput.Close(); } catch { }
                    try
                    {
                        if (!child.WaitForExit(700))
                        {
                            // This exact handle comes from this supervisor's Start;
                            // no caller-supplied PID can be selected for termination.
                            child.Kill(); report.ownedChildTerminated = true;
                            if (!child.WaitForExit(1500)) throw new IOException("Owned output-consumer did not stop.");
                        }
                        report.ownedChildStopped = true;
                    }
                    catch (Exception error) { if (failure == null) failure = error; }
                    if (reader != null) reader.Join(500);
                    if (errorReader != null) errorReader.Join(500);
                    if (report.ownedChildStopped && child.ExitCode != 0 && failure == null)
                        failure = new IOException("Output-consumer exited with code " + child.ExitCode + ": " + incoming.ErrorLine);
                }
                if (report.ownedChildStopped)
                {
                    try
                    {
                        using (KanataTcp recovery = KanataTcp.Connect(options.Port, options.Key))
                        {
                            // These are best-effort cleanup writes after child
                            // quiescence, not an acknowledgement of remote state.
                            for (int i = 0; i < 3; i++) { recovery.Send(KeyAction.Release); if (i != 2) Thread.Sleep(50); }
                        }
                        report.finalReleaseSent = true;
                    }
                    catch (Exception error) { if (failure == null) failure = error; }
                }
                if (child != null) child.Dispose();
            }
            if (failure != null) { report.status = "error"; report.error = failure.GetType().Name + ": " + failure.Message; }
            return report;
        }
        private sealed class GuardianLink
        { internal volatile bool Lost; internal long LastHeartbeat; }
        private sealed class WorkerWatch
        {
            private readonly GuardianLink link; private long sequence, last;
            internal WorkerWatch(GuardianLink link) { this.link = link; }
            internal void Check()
            {
                CheckGuardian(link);
                long now = Stopwatch.GetTimestamp();
                if (last == 0 || (now - last) * 1000.0 / Stopwatch.Frequency >= 100)
                { Console.WriteLine("ILWATCH1 " + (++sequence).ToString(CultureInfo.InvariantCulture)); last = now; }
            }
        }
        private static int Worker(Options options)
        {
            RequireNormalUser();
            GuardianLink link = new GuardianLink();
            RequireProcessIdentity(options.GuardianPid, options.GuardianStart, Process.GetCurrentProcess().MainModule.FileName);
            Thread input = new Thread(delegate()
            {
                try
                {
                    if (ReadBoundedLine(Console.In, 64) != "ILGUARD1 " + options.Token) { link.Lost = true; return; }
                    Interlocked.Exchange(ref link.LastHeartbeat, Stopwatch.GetTimestamp());
                    string line;
                    while ((line = ReadBoundedLine(Console.In, 64)) == "ILGUARD1 heartbeat") Interlocked.Exchange(ref link.LastHeartbeat, Stopwatch.GetTimestamp());
                }
                catch { }
                finally { link.Lost = true; }
            });
            input.IsBackground = true; input.Name = "InputLift guardian liveness"; input.Start();
            Stopwatch ready = Stopwatch.StartNew();
            while (Interlocked.Read(ref link.LastHeartbeat) == 0 && !link.Lost && ready.ElapsedMilliseconds < 1500) Thread.Sleep(10);
            WorkerWatch watch = new WorkerWatch(link); watch.Check();
            using (KanataTcp transport = KanataTcp.Connect(options.Port, options.Key))
            using (KanataConsumer consumer = new KanataConsumer(transport, Source, Control))
            {
                consumer.BeginSession();
                CaptureFrameConsumer frames = new CaptureFrameConsumer(consumer, Source, Control);
                Action<string> apply = delegate(string line) { watch.Check(); if (consumer.IsFaulted) throw new IOException("Kanata transport stopped; no reconnect is attempted."); frames.Accept(line); };
                try
                {
                    if (options.WorkerKind == "bridge") PipeFrames(options, watch, apply);
                    else DemoFrames(options.WorkerKind, watch, apply);
                }
                finally
                {
                    // Cleanup does not depend on the guardian input thread being
                    // live. Dispose retries if an output was still owned/uncertain.
                    try { transport.Send(KeyAction.Release); } catch { }
                    Stopwatch observe = Stopwatch.StartNew();
                    while (transport.IsHealthy && !transport.SawBaseAfterLifted && observe.ElapsedMilliseconds < 300) Thread.Sleep(10);
                    Console.WriteLine("ILRESULT1 " + (transport.SawBase ? "1" : "0") + " " +
                        (transport.SawLifted ? "1" : "0") + " " + (transport.SawBaseAfterLifted ? "1" : "0"));
                }
            }
            return 0;
        }
        private static void CheckGuardian(GuardianLink link)
        {
            long last = Interlocked.Read(ref link.LastHeartbeat);
            double age = last == 0 ? double.MaxValue : (Stopwatch.GetTimestamp() - last) * 1000.0 / Stopwatch.Frequency;
            if (link.Lost || age > 2000) throw new IOException("Guardian heartbeat ended; output consumer is stopping.");
        }
        private static void DemoFrames(string kind, WorkerWatch watch, Action<string> apply)
        {
            apply("ILFN1 1 reset 0"); apply("ILFN1 2 heartbeat 1"); apply("ILFN1 3 down 0");
            if (kind == "test-crash") Environment.Exit(23); // Intentional owned-stub fixture only: skips finally.
            if (kind == "test-hang") Thread.Sleep(10000); // Guardian must terminate its exact owned child.
            if (kind == "test-parent-timeout") while (true) { Thread.Sleep(100); watch.Check(); }
            for (int i = 0; i < 4; i++) { Thread.Sleep(250); apply("ILFN1 " + (4 + i) + " heartbeat 0"); }
            apply("ILFN1 8 up 1");
        }
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint serverProcessId);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder path, ref int length);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetProcessTimes(IntPtr process, out long creation, out long exit, out long kernel, out long user);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);
        private static void RequireProcessIdentity(int pid, long start, string expectedImage)
        {
            IntPtr handle = OpenProcess(0x1000, false, pid); // PROCESS_QUERY_LIMITED_INFORMATION, not VM_READ.
            if (handle == IntPtr.Zero) throw new IOException("Cannot query selected process identity; Win32 " + Marshal.GetLastWin32Error() + ".");
            try
            {
                StringBuilder image = new StringBuilder(32768); int length = image.Capacity; long creation, exit, kernel, user;
                if (!QueryFullProcessImageName(handle, 0, image, ref length) || !GetProcessTimes(handle, out creation, out exit, out kernel, out user))
                    throw new IOException("Cannot read selected process identity; Win32 " + Marshal.GetLastWin32Error() + ".");
                if (DateTime.FromFileTimeUtc(creation).Ticks != start || !string.Equals(Path.GetFullPath(image.ToString()), Path.GetFullPath(expectedImage), StringComparison.OrdinalIgnoreCase))
                    throw new IOException("Selected process start time or fixed image does not match.");
            }
            finally { CloseHandle(handle); }
        }
        private static void PipeFrames(Options options, WorkerWatch watch, Action<string> apply)
        {
            using (NamedPipeClientStream pipe = new NamedPipeClientStream(".", options.Pipe, PipeDirection.In, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification))
            {
                pipe.Connect(1500); uint pid;
                if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out pid) || pid != options.CapturePid) throw new IOException("Capture pipe server PID does not match selected session.");
                string expected = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "InputLift", "InputLiftCapture.exe");
                RequireProcessIdentity(options.CapturePid, options.CaptureStart, expected);
                byte[] buffer = new byte[256]; StringBuilder line = new StringBuilder(); Stopwatch freshness = Stopwatch.StartNew();
                while (true)
                {
                    Task<int> read = pipe.ReadAsync(buffer, 0, buffer.Length);
                    while (!read.Wait(100))
                    {
                        watch.Check();
                        if (freshness.ElapsedMilliseconds > 1500) throw new TimeoutException("Capture pipe stopped providing fresh frames.");
                    }
                    int count = read.Result;
                    if (count == 0) { if (line.Length != 0) throw new IOException("Truncated capture frame."); return; }
                    // A resumed process must not apply old frames already buffered
                    // before its last timely heartbeat. Stop rather than replay.
                    if (freshness.ElapsedMilliseconds > 1500) throw new TimeoutException("Capture continuity expired before buffered frames were applied.");
                    long received = Stopwatch.GetTimestamp();
                    for (int i = 0; i < count; i++)
                    {
                        byte value = buffer[i];
                        if (value == 10)
                        {
                            if ((Stopwatch.GetTimestamp() - received) * 1000.0 / Stopwatch.Frequency > 1000)
                                throw new TimeoutException("Capture buffer processing expired; remaining frames are discarded.");
                            apply(line.ToString()); line.Length = 0; freshness.Restart();
                        }
                        else { if (value < 32 || value > 126 || line.Length == 96) throw new IOException("Invalid capture pipe framing."); line.Append((char)value); }
                    }
                    watch.Check();
                }
            }
        }
    }
}
