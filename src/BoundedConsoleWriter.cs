// Copyright (c) 2026 GlobeRelay contributors
// SPDX-License-Identifier: MIT
// Bounded display-only queue. A blocked console never owns capture/cleanup work.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

internal sealed class BoundedConsoleWriter : IDisposable
{
    private const int MaximumLines = 64, MaximumLineCharacters = 262144;
    private readonly object gate = new object();
    private readonly Queue<string> pending = new Queue<string>();
    private readonly AutoResetEvent available;
    private readonly Action<string> sink;
    private readonly int capacity;
    private readonly Thread worker;
    private bool completing, exited;
    private int dropped;

    internal BoundedConsoleWriter(string threadName)
        : this(threadName, delegate(string line) { Console.WriteLine(line); }, MaximumLines) { }

    // Injected display sink is used only by pure threading tests.
    internal BoundedConsoleWriter(string threadName, Action<string> sink, int capacity)
    {
        if (sink == null) throw new ArgumentNullException("sink");
        if (capacity < 1 || capacity > MaximumLines) throw new ArgumentOutOfRangeException("capacity");
        this.sink = sink;
        this.capacity = capacity;
        available = new AutoResetEvent(false);
        worker = new Thread(WriteLoop) { IsBackground = true, Name = threadName };
        try { worker.Start(); }
        catch { available.Dispose(); throw; }
    }

    private void Drop() { if (dropped < int.MaxValue) dropped++; }
    internal int DroppedCount { get { lock (gate) { return dropped; } } }

    internal bool TryWrite(string line)
    {
        lock (gate)
        {
            if (line == null || line.Length > MaximumLineCharacters || completing || exited || pending.Count >= capacity)
            { Drop(); return false; }
            pending.Enqueue(line);
            available.Set();
            return true;
        }
    }

    private void WriteLoop()
    {
        try
        {
            while (true)
            {
                string line = null;
                lock (gate)
                {
                    if (pending.Count != 0) line = pending.Dequeue();
                    else if (completing) return;
                }
                if (line == null) { available.WaitOne(); continue; }
                // No queue/capture/cleanup lock is held while the sink may block.
                try { sink(line); }
                catch { lock (gate) { Drop(); } }
            }
        }
        finally
        {
            lock (gate)
            {
                exited = true;
                available.Dispose();
            }
        }
    }

    // Closing the queue never waits for a blocked display sink without a bound.
    // A blocked background writer may remain until process exit. No abort is used.
    internal bool Complete(int milliseconds)
    {
        if (milliseconds < 0 || milliseconds > 1000) throw new ArgumentOutOfRangeException("milliseconds");
        lock (gate)
        {
            completing = true;
            if (!exited) available.Set();
        }
        if (Thread.CurrentThread == worker) return false;
        return worker.Join(milliseconds);
    }

    public void Dispose() { Complete(0); }

    internal static string[] RunSelfTests(out int count)
    {
        int cases = 0;
        List<string> errors = new List<string>();
        Action<bool, string> check = delegate(bool okay, string name) { cases++; if (!okay) errors.Add(name); };
        List<string> rendered = new List<string>();
        using (ManualResetEvent entered = new ManualResetEvent(false))
        using (ManualResetEvent release = new ManualResetEvent(false))
        {
            BoundedConsoleWriter writer = new BoundedConsoleWriter("Blocked synthetic display", delegate(string line) {
                if (line == "first") { entered.Set(); release.WaitOne(); }
                lock (rendered) { rendered.Add(line); }
            }, 2);
            try
            {
                check(writer.TryWrite("first"), "Initial display message accepted");
                bool sinkBlocked = entered.WaitOne(1000);
                check(sinkBlocked, "Synthetic display sink is held before queue test");
                Stopwatch enqueue = Stopwatch.StartNew();
                bool second = writer.TryWrite("second"), third = writer.TryWrite("third"), overflow = writer.TryWrite("fourth");
                enqueue.Stop();
                check(second && third && !overflow && writer.DroppedCount == 1,
                    "Bounded queue accepts available slots and drops overflow while sink blocks");
                check(enqueue.ElapsedMilliseconds < 1000, "TryWrite does not wait on a blocked display sink");
                Stopwatch finish = Stopwatch.StartNew();
                bool complete = writer.Complete(25);
                finish.Stop();
                check(!complete && finish.ElapsedMilliseconds < 1000, "Complete returns within its bound with a blocked sink");
                check(!writer.TryWrite("after-close") && writer.DroppedCount == 2, "Closed queue refuses later output without waiting");
                release.Set();
                check(writer.Complete(1000), "Released background writer drains and exits");
                lock (rendered) { check(string.Join(",", rendered.ToArray()) == "first,second,third", "A single writer preserves accepted-message order"); }
                check(writer.Complete(0), "Completion is idempotent");
            }
            finally { release.Set(); writer.Complete(1000); writer.Dispose(); }
        }
        BoundedConsoleWriter failing = new BoundedConsoleWriter("Synthetic failing display", delegate(string line) {
            throw new InvalidOperationException("synthetic display failure");
        }, 1);
        try
        {
            check(failing.TryWrite("message") && failing.Complete(1000) && failing.DroppedCount == 1,
                "Display failure is contained on the background writer");
        }
        finally { failing.Dispose(); }
        bool refused = false;
        try { new BoundedConsoleWriter("invalid", delegate(string line) { }, 0); }
        catch (ArgumentOutOfRangeException) { refused = true; }
        check(refused, "Invalid queue capacity is refused before worker startup");
        count = cases;
        return errors.ToArray();
    }
}
