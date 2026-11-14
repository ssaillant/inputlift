# Kanata companion integration

Status: **in development; no live Bluetooth compatibility claim yet**. The initial
target is Kanata **v1.12.0**, commit
`02242ef761ef43b860eac30067ce86904be7d466`. Keep the demonstrated source,
transport checks, and physical-device validation separate when reporting results.

## Intended behavior

A supported Bluetooth Fn control holds one dedicated Kanata virtual key. The
example configuration changes `a` to `b` while that virtual key holds the
`lifted` layer; releasing the control restores `a`. Application actions and
gesture interpretation remain in Kanata configuration.

```text
Selected Bluetooth fields → InputLift event-only capture
                        → protected local Fn-state pipe
                        → unprivileged supervised companion
                        → Kanata on 127.0.0.1 → dedicated virtual key
```

The existing Deskflow output remains a separate installation mode. Another KVM
is not involved in this demonstration. The supplied physical decoder still
supports only the profile in [compatibility](../compatibility.md).

## Protocol and trust boundaries

The companion uses Kanata's protocol-1 `Hello`, `RequestFakeKeyNames`, and
`ActOnFakeKey` messages. The configured virtual key must exist and be reserved
exclusively for this companion. Kanata does not associate held virtual-key state
with an individual TCP client and does not release it merely because that client
disconnects. Startup, reset, normal teardown, and supervised failure therefore
need explicit release behavior. A successful socket write is not acknowledgement
that Kanata applied an action.

Bind Kanata with an explicit loopback address:

```text
kanata_windows_tty_winIOv2_x64.exe --cfg integrations/kanata/held-layer.kbd --port 127.0.0.1:7070
```

This is an **interactive remapping command**, not a build or validation step.
Do not run it as part of normal CI. It starts keyboard processing until stopped.
A bare port can expose the unauthenticated plaintext interface on other network
addresses. Loopback restricts network reach but does not authenticate other local
programs. Keep unrelated actions out of the demonstration configuration.

The elevated capture side may emit only fixed Fn-state/reset/heartbeat records.
It does not accept scripts, application actions, arbitrary key names, or network
destinations from consumers. Consumer actions run separately without elevation.
Capture installation retains explicit sensitive-logging consent and the existing
cleanup supervisor. See [security](../../SECURITY.md).

## Validation stages

1. Build and run pure consumer/protocol tests without hardware or sockets.
2. Verify the synthetic packet evidence through the public decoder API.
3. Run the explicit local transport fixture against an owned stub server.
4. Validate the example with the official Kanata binary using `--check --cfg`.
5. With separate approval, test synthetic control events against real Kanata.
6. With separate approval, install the event-only source and test the supported
   Bluetooth profile, including reset/disconnect and supervision failures.
7. Obtain an independent report using the [tester checklist](kanata-testing.md).

The official v1.12.0 configuration and a real-server synthetic layer lifecycle
have now passed locally. See the [candidate validation record](kanata-status.md)
for the exact scope. To repeat the interactive stage, explicitly opt in with
`tests/Test-KanataLive.ps1 -RunInteractiveDemo -KanataExecutable <reviewed-path>
-ExpectedKanataSha256 <reviewed-hash>`. This script starts a temporary keyboard
hook, requires port 7070 to be free, checks base/lifted/base notifications and
stops its owned processes. It never downloads Kanata or starts Bluetooth capture.

Only completed stages belong in a release's test results. An emulator or a
synthetic packet replay does not prove a Bluetooth device works, and a successful
configuration check does not prove remapping or cleanup works.

Do not replace an existing working helper to try this candidate. The capture
mutex deliberately prevents concurrent privileged acquisition. A migration test
must first establish a concrete stop/cleanup/restart/recovery plan.

## Upstream references

- [Versioned protocol definitions](https://github.com/jtroo/kanata/blob/02242ef761ef43b860eac30067ce86904be7d466/tcp_protocol/src/lib.rs)
- [Versioned TCP implementation](https://github.com/jtroo/kanata/blob/02242ef761ef43b860eac30067ce86904be7d466/src/tcp_server.rs)
- [Versioned configuration documentation](https://github.com/jtroo/kanata/blob/v1.12.0/docs/config.adoc)
- [Maintainer discussion of additional input methods](https://github.com/jtroo/kanata/discussions/1984)

This is an independent companion proposal. It is not endorsed by Kanata and does
not imply that its maintainers have accepted InputLift's acquisition backend.
