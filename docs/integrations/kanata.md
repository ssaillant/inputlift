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

Start with the [focused synthetic demo](kanata-demo-test.md), which provides an
optional countdown and longer hold for the blank-editor test. It uses no
Bluetooth acquisition or privileged installation.

```text
Selected Bluetooth fields → InputLift event-only capture
                        → protected local Fn-state pipe
                        → unprivileged supervised companion
                        → Kanata on 127.0.0.1 → dedicated virtual key
```

The existing Deskflow output remains a separate installation mode. Another KVM
is not involved in this demonstration. The supplied physical decoder still
supports only the profile in [compatibility](../compatibility.md).

## Fresh physical-source setup for developers

This is an **unvalidated, opt-in physical test path**, not a migration procedure
for a working helper. The synthetic demonstration above does not need it. Use a
fresh supported machine only after reviewing the source, narrow profile/radio
requirements, security model and cleanup plan. Do not disable an existing helper
or overwrite its files just to follow this guide. Setup is create-only and
refuses existing application/state directories or an existing sensitive-logging
flag. Stop on an error and inspect its exact wording.

Build both optional components without starting them:

```powershell
./Build.ps1 -IncludeWindowsCapture -IncludeKanataAdapter
```

After separate explicit approval for ongoing sensitive Bluetooth logging and
automatic logon startup, use administrator PowerShell under the **same Windows
account** that will run the normal-user companion:

```powershell
$keyboardAddress = Read-Host 'Private Bluetooth address, 12 hex digits; do not share'
$packageHash = (Get-FileHash ./artifacts/package/package.json -Algorithm SHA256).Hash
./Setup.ps1 -Mode Install -OutputMode events-only `
  -KeyboardAddress $keyboardAddress -ExpectedManifestSha256 $packageHash `
  -EnablePersistentLogging -CreateStartMenuShortcut -Start
```

The hash binds setup to that exact locally built package; it is not a substitute
for source review. The explicit `events-only` mode is required. Omitting it
selects the legacy Deskflow/Mouse 5 output, which does not provide the Fn pipe.
Setup creates a logon-startup task and protected Program Files/ProgramData state.
Do not share the device address or installation journals publicly.

Start the separately reviewed Kanata example on loopback, then open **normal,
non-administrator PowerShell** in this checkout. Inspect the current source:

```powershell
./integrations/kanata/Start-Bridge.ps1
```

Inspection reads protected metadata and prints only readiness/status information.
It does not connect, start capture, change settings, or launch a process. To
explicitly connect the companion:

```powershell
./integrations/kanata/Start-Bridge.ps1 -Connect
```

The launcher obtains the nonce and worker identity from the protected current
session, checks ownership/mode/freshness and refuses an already connected source.
The companion still authenticates the actual pipe server before accepting data.
Do not copy session identifiers from an issue or another machine. The source's
`AwaitConsumer` state is expected before this connection.

There is one reader per capture session. After the bridge has connected, another
inspection/connection attempt deliberately refuses that session. If the bridge
disconnects or fails after opening the pipe, do not reconnect using old metadata.
Stop acquisition, review its owned cleanup, and start a fresh source session only
after that cleanup is confirmed. Inspect the new session and repeat identification.
If cleanup is uncertain, use the startup-disable and Windows Restart path below.

Once connected, follow the source's identification instructions: turn the
keyboard off, wait for the instruction to turn it on, then turn it on and tap /
release left Shift. Release all keys. In a blank editor, test A → A normally,
hold the recovered control and test A → B, then release and test A → A. Exercise
reset/disconnect only after the baseline works, using the tester checklist.

For cleanup, stop the companion with Ctrl+C and stop Kanata to clear any uncertain
layer. Confirm the harmless sample returns to its baseline before continuing.
Use **Stop and disable automatic startup** in the InputLift tray for acquisition.
Verify the helper's owned cleanup result before using Windows **Restart** to
clear the driver's cached sensitive-logging state. Stopping the companion alone
does not stop capture or clear logging. Preserve exact errors and review them
before retrying. Do not restore another helper until its own startup/capture
state has been reviewed.

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
