# Reviewed Kanata protocol boundary

Initial reference: **Kanata v1.12.0**, commit `02242ef761ef43b860eac30067ce86904be7d466`. This is a source/protocol review, not a binary compatibility certificate.

| Fact | Pinned primary source |
|---|---|
| TCP messages use JSON; server serialization appends newline. | [Protocol definitions](https://github.com/jtroo/kanata/blob/02242ef761ef43b860eac30067ce86904be7d466/tcp_protocol/src/lib.rs) |
| Virtual-key commands accept `Press`, `Release`, `Tap` and `Toggle`. | [Protocol definitions](https://github.com/jtroo/kanata/blob/02242ef761ef43b860eac30067ce86904be7d466/tcp_protocol/src/lib.rs) |
| Connection begins with a `LayerChange` notification. | [TCP server](https://github.com/jtroo/kanata/blob/02242ef761ef43b860eac30067ce86904be7d466/src/tcp_server.rs) |
| `Hello` reports version, protocol `1` and capabilities including `fake-key`. | [TCP server](https://github.com/jtroo/kanata/blob/02242ef761ef43b860eac30067ce86904be7d466/src/tcp_server.rs) |
| `RequestFakeKeyNames` returns configured virtual-key names. | [TCP server](https://github.com/jtroo/kanata/blob/02242ef761ef43b860eac30067ce86904be7d466/src/tcp_server.rs) |
| A successful `ActOnFakeKey` handler sends no per-action success response; unknown keys emit `Error`. | [TCP handler](https://github.com/jtroo/kanata/blob/02242ef761ef43b860eac30067ce86904be7d466/src/tcp_server.rs#L213) |
| The handler does not associate virtual-key ownership with a TCP client or release that client's held keys on EOF. | [TCP client loop](https://github.com/jtroo/kanata/blob/02242ef761ef43b860eac30067ce86904be7d466/src/tcp_server.rs#L172) |
| A bare `--port` binds all IP addresses; an explicit `IP:PORT` limits binding. `--check` validates configuration and exits. | [Versioned guide](https://github.com/jtroo/kanata/blob/v1.12.0/docs/config.adoc#args-tcp) |

Example outgoing action, terminated with a newline:

```json
{"ActOnFakeKey":{"name":"inputlift-fn","action":"Press"}}
```

Release uses the same virtual-key name with `"action":"Release"`. The integration should use a dedicated name, not one shared with physical bindings or other command clients. A successful socket write proves neither physical output nor activation of the intended layer.

## Security and lifecycle constraints

Use an explicit loopback address such as `127.0.0.1:7070` on both ends. The protocol is plaintext and has no authentication handshake. Loopback limits remote exposure; it does not authenticate another local process. Do not add firewall allowances or expose the server on a home/VPN interface for this integration.

Keep the consumer outside elevated Bluetooth acquisition. It should accept only the selected recovered control, require neutral arming after a reset, suppress duplicate presses and attempt an owned-key release during teardown. TCP loss or a process crash can prevent release; socket closure alone does not clear Kanata state. Recovery/guardian behavior must be independently tested before recommending unattended use.

The current InputLift Bluetooth backend requires sensitive diagnostic logging and a private driver-refresh request. Other local tracing tools may then access ordinary keyboard reports and encryption keys, even though InputLift's decoder skips ordinary key slots. The source evidence above does not remove this acquisition risk. Live testers must receive the repository's security explanation and explicitly opt in before that backend is enabled.

## Upstream fit and policy record

The maintainer welcomed contributions or TCP integration with README documentation in [discussion #1984](https://github.com/jtroo/kanata/discussions/1984). [The README](https://github.com/jtroo/kanata#contributing) welcomes discussions and generally licenses upstream contributions under LGPL-3.0-only. InputLift's independently written companion remains in its own MIT-licensed repository; this package does not copy Kanata implementation code.

No AI-specific restriction was identified in the inspected README/root listing. This is not proof that no policy exists: recheck current contribution files, templates and any instructions shown when posting. Disclose AI assistance accurately. The companion proposal and alpha.2 follow-up are recorded in [discussion #2247](https://github.com/jtroo/kanata/discussions/2247). No upstream acceptance or independent validation is claimed.
