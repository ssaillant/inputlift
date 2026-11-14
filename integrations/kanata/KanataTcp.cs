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

namespace InputLift.Kanata
{
    /// <summary>
    /// Loopback-only Kanata protocol-1 transport. Verifies Hello/capabilities and
    /// the configured virtual key before allowing actions. Kanata has no action
    /// success acknowledgement and does not release virtual keys on disconnect.
    /// </summary>
    public sealed class KanataTcp : IKeyTransport
    {
        public const int MaximumLineBytes = 16384;
        private readonly TcpClient client;
        private readonly NetworkStream stream;
        private readonly string key;
        private readonly object sendGate = new object(), stateGate = new object();
        private Thread monitor;
        private bool disposed, failed, sawBase, sawLifted, sawBaseAfterLifted;
        public string ServerVersion { get; private set; }
        public bool IsHealthy { get { lock (stateGate) return !disposed && !failed; } }
        /// <summary>Observed notifications for the fixed names in held-layer.kbd, not key-action acknowledgements.</summary>
        public bool SawBase { get { lock (stateGate) return sawBase; } }
        public bool SawLifted { get { lock (stateGate) return sawLifted; } }
        public bool SawBaseAfterLifted { get { lock (stateGate) return sawBaseAfterLifted; } }

        public static void ValidateKey(string value)
        {
            if (value == null || value.Length < 1 || value.Length > 64)
                throw new ArgumentException("Use a dedicated virtual key of 1-64 ASCII letters/digits/dot/underscore/hyphen.");
            foreach (char c in value)
                if (!((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') ||
                    (c >= '0' && c <= '9') || c == '.' || c == '_' || c == '-'))
                    throw new ArgumentException("Invalid virtual-key label.");
        }

        public static KanataTcp Connect(int port, string key)
        {
            ValidateKey(key);
            if (port < 1024 || port > 65535) throw new ArgumentOutOfRangeException("port", "Use an unprivileged loopback port, 1024-65535.");
            TcpClient client = new TcpClient(AddressFamily.InterNetwork);
            try
            {
                IAsyncResult pending = client.BeginConnect(IPAddress.Loopback, port, null, null);
                using (pending.AsyncWaitHandle)
                    if (!pending.AsyncWaitHandle.WaitOne(1500)) throw new TimeoutException("Kanata loopback connect timed out.");
                client.EndConnect(pending);
                client.NoDelay = true; client.SendTimeout = 500; client.ReceiveTimeout = 1000;
                KanataTcp result = new KanataTcp(client, key);
                try { result.Handshake(); result.StartMonitor(); return result; }
                catch { result.Dispose(); throw; }
            }
            catch { client.Close(); throw; }
        }

        private KanataTcp(TcpClient client, string key)
        { this.client = client; this.key = key; stream = client.GetStream(); }

        private void Handshake()
        {
            Write("{\"Hello\":{}}\n");
            IDictionary<string, object> hello = Await("HelloOk");
            object protocol, capabilities, version;
            if (!hello.TryGetValue("protocol", out protocol) || Convert.ToInt32(protocol) != 1 ||
                !hello.TryGetValue("capabilities", out capabilities) || !HasString(capabilities, "fake-key") ||
                !HasString(capabilities, "fake-key-names") || !hello.TryGetValue("version", out version) || !(version is string))
                throw new IOException("Kanata did not advertise protocol 1 and required fake-key capabilities.");
            string text = (string)version;
            if (text.Length == 0 || text.Length > 64) throw new IOException("Invalid Kanata version response.");
            ServerVersion = text;
            Write("{\"RequestFakeKeyNames\":{}}\n");
            IDictionary<string, object> names = Await("FakeKeyNames");
            object values;
            if (!names.TryGetValue("names", out values) || !HasString(values, key))
                throw new IOException("The dedicated virtual key is absent from Kanata's configured names.");
        }

        private static bool HasString(object collection, string wanted)
        {
            IEnumerable sequence = collection as IEnumerable;
            if (sequence == null || collection is string) return false;
            int count = 0;
            foreach (object item in sequence)
            { if (++count > 2048) throw new IOException("Kanata response collection exceeds its bound."); if (item is string && (string)item == wanted) return true; }
            return false;
        }

        private IDictionary<string, object> Await(string name)
        {
            Stopwatch deadline = Stopwatch.StartNew();
            for (int lines = 0; lines < 64; lines++)
            {
                int remaining = 1500 - (int)deadline.ElapsedMilliseconds;
                if (remaining <= 0) throw new TimeoutException("Kanata handshake timed out.");
                IDictionary<string, object> message = Parse(ReadLine(remaining));
                RefuseError(message);
                ObserveLayer(message);
                object value;
                if (message.TryGetValue(name, out value))
                {
                    IDictionary<string, object> body = value as IDictionary<string, object>;
                    if (body == null) throw new IOException("Kanata handshake body is not an object.");
                    return body;
                }
            }
            throw new IOException("Too many unsolicited messages during Kanata handshake.");
        }

        internal static IDictionary<string, object> Parse(string line)
        {
            if (line == null || Encoding.UTF8.GetByteCount(line) > MaximumLineBytes)
                throw new IOException("Kanata response exceeds its bound.");
            JavaScriptSerializer parser = new JavaScriptSerializer { MaxJsonLength = MaximumLineBytes, RecursionLimit = 8 };
            IDictionary<string, object> message = parser.DeserializeObject(line) as IDictionary<string, object>;
            if (message == null || message.Count != 1) throw new IOException("Invalid Kanata message envelope.");
            return message;
        }

        private static void RefuseError(IDictionary<string, object> message)
        {
            // Do not echo arbitrary server messages or application configuration.
            if (message.ContainsKey("Error")) throw new IOException("Kanata reported a protocol/action error; output is stopped.");
        }

        private void ObserveLayer(IDictionary<string, object> message)
        {
            object value, name;
            if (!message.TryGetValue("LayerChange", out value)) return;
            IDictionary<string, object> body = value as IDictionary<string, object>;
            if (body == null || !body.TryGetValue("new", out name) || !(name is string)) return;
            // Retain only booleans for the two public example labels; never log
            // arbitrary names from another application's configuration.
            lock (stateGate)
            {
                if ((string)name == "lifted") sawLifted = true;
                if ((string)name == "base") { sawBase = true; if (sawLifted) sawBaseAfterLifted = true; }
            }
        }

        private string ReadLine(int milliseconds)
        {
            Stopwatch timer = Stopwatch.StartNew(); List<byte> bytes = new List<byte>();
            while (bytes.Count <= MaximumLineBytes)
            {
                int remaining = milliseconds - (int)timer.ElapsedMilliseconds;
                if (remaining <= 0) throw new TimeoutException("Kanata response line timed out.");
                stream.ReadTimeout = remaining;
                int next = stream.ReadByte();
                if (next < 0) throw new EndOfStreamException("Kanata closed the connection.");
                if (next == '\n') return new UTF8Encoding(false, true).GetString(bytes.ToArray());
                bytes.Add((byte)next);
            }
            throw new IOException("Kanata response line exceeds its byte bound.");
        }

        private void Write(string line)
        {
            lock (sendGate)
            {
                if (!IsHealthy) throw new IOException("Kanata transport is closed or faulted.");
                byte[] bytes = Encoding.UTF8.GetBytes(line);
                try { stream.WriteTimeout = 500; stream.Write(bytes, 0, bytes.Length); }
                catch { Fail(); throw; }
            }
        }

        public void Send(KeyAction action)
        {
            if (action != KeyAction.Press && action != KeyAction.Release) throw new ArgumentOutOfRangeException("action");
            Write("{\"ActOnFakeKey\":{\"name\":\"" + key + "\",\"action\":\"" + action.ToString() + "\"}}\n");
        }

        private void StartMonitor()
        {
            monitor = new Thread(delegate()
            {
                try
                {
                    while (IsHealthy)
                        if (client.Client.Poll(100000, SelectMode.SelectRead))
                        {
                            IDictionary<string, object> message = Parse(ReadLine(1000));
                            RefuseError(message); ObserveLayer(message);
                        }
                }
                catch { if (!disposed) Fail(); }
            });
            monitor.IsBackground = true; monitor.Name = "InputLift Kanata status"; monitor.Start();
        }

        private void Fail()
        { lock (stateGate) failed = true; try { client.Close(); } catch { } }

        public void Dispose()
        {
            lock (stateGate) { if (disposed) return; disposed = true; }
            client.Close();
            if (monitor != null && monitor != Thread.CurrentThread) monitor.Join(1200);
        }
    }
}
