# Kanata integration evidence

This package makes the first supported decoder profile independently inspectable. It contains **invented protocol records**, not Bluetooth captures. It does not establish new hardware compatibility or prove live capture, transport or application behavior.

## Files and verification

- `fn-profile.synthetic.json`: language-neutral BTHPORT-classified records, explicit public transitions and state expectations.
- `VerifyFixture.cs`: a bounded reader harness using the public `InputLift.Core.FnDecoder` and `FnInputAdapter` APIs.
- `Verify-Fixture.ps1`: compiles and runs that harness using an already built core DLL and the existing Windows .NET Framework compiler.
- `protocol-reference.md`: pinned Kanata interface, limitations and source links.
- `proposal-notes.md`: scope for a maintainer design discussion.
- `tester-checklist.md`: independent-test protocol and reporting limits.

From the repository root, using PowerShell 7:

```powershell
./Build.ps1
./evidence/kanata/Verify-Fixture.ps1
```

The verifier writes generated binaries and `verification.json` only under the ignored `artifacts/evidence/kanata` directory. It neither installs anything nor opens a device/socket, injects input, changes logging or contacts another computer. A compile or verification error stops the script; it does not retry automatically. The verifier is distinct from the Kanata transport tests.

## Fixture contract

Schema version 1 has one `setup` sequence and three `scenarios`. Start each scenario with a new decoder using the fixture's synthetic `selector`, replay `setup`, then replay that scenario's `steps` in order. The selector `102030405060` is an invented test value, not a device to discover or configure.

Each step supplies an operation, opaque timestamp, expected transition kinds/reasons and expected public decoder state. A `packet` step supplies a packet-kind name and space-delimited hexadecimal bytes. A `reset` step calls the decoder's explicit reset method; it is not a fabricated Bluetooth packet.

The classifications are **BTHPORT conventions**: `HciEvent=2`, `ReceivedAcl=3`, `SentAcl=4`. They are not H4 packet-type bytes and must not be passed to a reader as transport framing. An acquisition adapter must remove/normalize its own framing. The records contain complete HCI event or ACL payloads, including the ACL/L2CAP headers expected by this decoder.

The shared setup exercises connection completion, a host-initiated HID interrupt-channel request/response and an all-released baseline. Scenarios then exercise:

1. One press and release, suppressing duplicate held/released reports.
2. Explicit event-loss reset while held, followed by rejection of a stale report.
3. Disconnect while held, rejection of stale traffic, and reacquisition through a fresh connection/channel setup.

Every 19-byte report uses zeros for ordinary modifier/key fields. The harness refuses reads of indices `10,12,13,14,15,16,17`; it checks only the public decoder results and adapter metadata. This checks this decoder's access pattern on these fixtures. It does not protect a separately implemented acquisition component or erase supplied packet buffers.

## Provenance and observed result

Packet shapes and state expectations were derived from the repository's MIT-licensed synthetic builders/tests in `core/CoreTests.cs` and `src/GlobePacketDecoderTests.cs`. Ordinary-key placeholders were replaced with zero bytes. No captured reports, raw traces, encryption keys or individual device identifiers are included.

The local fixture check returned `status: ok`: **3 scenarios, 28 checked steps, 27 packet records, 1 explicit reset, 10 decoded transitions and 10 generic events**, with zero forbidden ordinary-key reads. Fixture SHA-256:

```text
8056c09958b5cb53831b1252ab3d94537cfad269e1cd64105d37c3e4ee96be84
```

The report explicitly marks hardware validation, live transport testing and native actions as false. Re-run verification against the exact source revision being evaluated. These counts describe fixture checks, not independent devices or testers.
