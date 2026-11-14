# Kanata companion adapter

This optional Windows/.NET Framework companion maps one selected InputLift button
to one dedicated Kanata virtual key. It is separate from privileged acquisition.
The configured virtual key must be reserved exclusively for this companion.

The protocol target is Kanata v1.12.0, commit
`02242ef761ef43b860eac30067ce86904be7d466`. The client requires protocol 1, the
`fake-key` and `fake-key-names` capabilities, and the configured virtual key in the
server's advertised name list. It connects only to IPv4 `127.0.0.1`.

## Inert checks

`InputLift.Kanata.exe --self-test` uses in-memory fakes only: no sockets, capture,
keyboard remapping, driver calls, installation, or settings changes.

`InputLift.Kanata.exe --test-transport` is an explicit integration check against
its own temporary loopback stub. It tests actual sockets and owned subprocesses,
including abrupt consumer exit, a stalled consumer, and loss of the supervisor's
heartbeat. It does not connect to Kanata or send native keyboard input.

## Controlled application demo

Validate [held-layer.kbd](held-layer.kbd) using Kanata's `--check` first. Running
Kanata without `--check` starts keyboard remapping and must be a separate opt-in.
Use a blank disposable document and stop other remappers for the test.

```powershell
kanata --check --cfg held-layer.kbd
# Separate opt-in: bind explicitly to loopback, not a bare port number.
kanata --cfg held-layer.kbd --port 127.0.0.1:7070
InputLift.Kanata.exe --demo --port 7070 --virtual-key inputlift-fn
# Optional manual test: time to focus a blank editor, then a five-second hold.
InputLift.Kanata.exe --demo --port 7070 --virtual-key inputlift-fn `
  --demo-countdown-ms 5000 --demo-hold-ms 5000
```

`--demo` supplies synthetic reset/neutral/press/hold/release frames. It sends real
commands to the local Kanata instance; it does not recover a hardware event.
The sample maps A to A normally and A to B while the virtual key is held. The
demo report retains only booleans for observed `base`, `lifted`, then `base`
layer notifications. These are separate observations, not acknowledgements of
the virtual-key commands or proof of physical keyboard behavior.

The optional timing flags are accepted only by `--demo`. Countdown is 0–10000 ms
(default 0); hold is 100–10000 ms (default 1000). Countdown begins after the
worker connects and verifies Kanata, and sends neutral heartbeats before the
single press. The held phase sends non-neutral heartbeats, then releases. Both
waits check the existing supervisor link at intervals of at most 100 ms, subject
to OS scheduling and I/O. Ctrl+C in the supervisor requests graceful release;
the existing failure and cleanup limits below still apply. The ordinary demo's
supervisor run window is the requested countdown plus hold plus five seconds of
startup/completion allowance, followed by the existing bounded cleanup attempts.
This does not widen bridge or deliberate fault-fixture timeouts. Defaults retain
the original immediate one-second hold. See the [focused manual test](../../docs/integrations/kanata-demo-test.md).

## Live recovered-control input

For an explicitly reviewed fresh physical-source installation, follow the
[event-only setup guide](../../docs/integrations/kanata.md#fresh-physical-source-setup-for-developers).
Do not use the Deskflow/default installation mode for this path.

From normal-user PowerShell in the checkout, inspect the current protected state
without starting anything, then explicitly connect if ready:

```powershell
./integrations/kanata/Start-Bridge.ps1
./integrations/kanata/Start-Bridge.ps1 -Connect
```

The launcher rejects stale/wrong-mode state and prints no session or device
identifiers. Inspection opens no socket, starts no process, and changes no
settings. It does not start or install the privileged source. The native
companion still authenticates the pipe server after connection.

`--bridge` connects read-only to the event-only capture session's named pipe:

```powershell
InputLift.Kanata.exe --bridge --port 7070 --virtual-key inputlift-fn `
  --capture-pipe InputLift.Fn.<session-nonce> `
  --capture-pid <worker-pid> --capture-start <worker-start-UTC-ticks>
```

The launcher must obtain these values from the protected current-session state,
never a public issue or shared configuration. The reader authenticates the pipe
server PID, process creation time, and fixed installed image
`Program Files\InputLift\InputLiftCapture.exe`, using limited-information process
queries that do not require reading the elevated process's memory. It never
accepts a remote pipe path or arbitrary executable path.

Capture frames are at most 96 ASCII characters plus newline:

```text
ILFN1 1 reset 0
ILFN1 2 heartbeat 1
ILFN1 3 down 0
ILFN1 4 heartbeat 0
ILFN1 5 up 1
```

Sequences must be positive, contiguous, and increasing. The first frame must be
reset. Reset/down require neutral=0, and up requires neutral=1. A neutral heartbeat
can arm the consumer or recover a held release. A non-neutral heartbeat preserves
the current state; it never synthesizes a press. Unknown, malformed, lost, stale,
or contradictory frames stop the session. A new press requires observed neutral
after every reset. There is no reconnect or buffered-press replay.

The consumer bounds its own frame-processing time and heartbeat gaps. Frames do
not carry an acquisition timestamp, so these checks do not establish an end-to-end
hardware-event latency guarantee or measure time already spent in OS buffers.

## Process and cleanup limits

The normal-user supervisor starts the same executable as an owned output-consumer
child. The child owns the normal TCP connection and capture pipe. They exchange
short liveness messages through inherited anonymous pipes. If source data is
missing for 1.5 seconds, or guardian heartbeats for 2 seconds, the child stops new
presses and attempts release. If child progress stops for 2.5 seconds, the
supervisor terminates only its exact owned child handle, confirms quiescence,
then attempts release using a new loopback connection. It never accepts a PID
to terminate. Normal teardown also attempts release.

Kanata does not automatically release virtual keys when TCP disconnects and has
no action-success acknowledgement. Cleanup writes are therefore best effort.
Simultaneous process termination, OS failure, server failure, or a delayed command
queue can prevent or delay recovery. Stop/restart Kanata to clear an uncertain
virtual-key/layer state before resuming; do not interpret `finalReleaseSent` as
proof that the application applied a release. A local loopback connection is not
authenticated against other applications running as the same user.

The current Bluetooth acquisition backend's sensitive-logging and restart
requirements still apply. This consumer neither enables nor clears that logging.
Its build and stub tests establish no hardware or live Bluetooth compatibility.

Protocol references: [configuration and TCP commands](https://github.com/jtroo/kanata/blob/02242ef761ef43b860eac30067ce86904be7d466/docs/config.adoc),
[server implementation](https://github.com/jtroo/kanata/blob/02242ef761ef43b860eac30067ce86904be7d466/src/tcp_server.rs).
