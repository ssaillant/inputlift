# InputLift

**Turn device-specific controls into usable input events.**

InputLift is a small .NET toolkit for recovering control state from supported device reports and exposing it through a consistent event API. Producers describe input; consumers decide what that input does.

```text
Device/source adapter → typed input events → consumer or output adapter
```

The first device decoder handles a narrow Bluetooth Fn profile. The event model also supports buttons, normalized values, relative steps and source resets. Synthetic examples demonstrate those extensions without claiming additional hardware support.

## What is included

| Component | Status |
|---|---|
| Typed event API | Implemented: button press/release, value change, relative delta and source-wide reset. |
| Bluetooth Fn decoder | Implemented for one supported report profile, with bounded parsing and synthetic tests. |
| Fn-to-event adapter | Implemented: turns decoder transitions into the common event model. |
| Hold, toggle and virtual dial consumers | Runnable synthetic examples; no real application or device control. |
| JSON event stream | Runnable synthetic stdout example; no network service or capture. |
| Windows capture + Deskflow output | Optional guarded integration with privileged logging requirements. |
| Kanata companion | Experimental loopback virtual-key adapter and event-only capture path; independent/live validation pending. |
| Other device profiles, KVMs or native output backends | Extension work; not implemented or claimed as supported. |

**Source-only alpha.** The generalized Windows runtime's installation/live behavior remain unvalidated. There are no published binaries or claims of universal device support.

## Build and explore

Requirements for the provided build: Windows x64, PowerShell 7 and the existing .NET Framework 4.8.x C# compiler. No SDK or package download is performed. See [validation and build details](docs/validation.md) for the tested toolchain and current limits.

```powershell
./Build.ps1
./artifacts/examples/InputLift.Examples.exe --demo all
```

For the experimental Kanata companion, see the [integration guide](docs/integrations/kanata.md),
[synthetic protocol evidence](evidence/kanata/README.md), and
[independent tester checklist](docs/integrations/kanata-testing.md).
`./Build.ps1 -IncludeKanataAdapter` builds its unprivileged code and runs pure
checks; it does not start Kanata or keyboard processing.

The default build creates `InputLift.Core.dll`, compiles the examples and runs pure tests. It requires no Deskflow installation or administrator privileges and does not capture hardware, inject input, configure logging or register startup tasks.

The examples operate only on synthetic data and virtual state. See [examples](examples/README.md) for the individual demos and [extension guide](docs/extensions.md) for how to connect another source or consumer.

## Event model

```csharp
using InputLift.Core;

// Logical labels, not a device address or unique hardware identifier.
var pressed = InputEvent.Button("demo-source", "button", true, 100);
var released = InputEvent.Button("demo-source", "button", false, 200);
var position = InputEvent.Absolute("demo-source", "slider", 0.75, 300);
var rotation = InputEvent.Relative("demo-source", "dial", -2, 400);
var reset = InputEvent.Reset("demo-source", 500);
```

Events are immutable and include a logical source, control and caller-defined timestamp. Absolute values are normalized to `[0,1]`; relative values are signed integer steps. A source-wide reset tells consumers to clear held or latched state for that source.

The model does not discover hardware or interpret gestures automatically. A producer must supply valid events; a consumer implements its own action, activation and reset policies. See [event and extension contracts](docs/extensions.md).

## First protocol adapter: Bluetooth Fn

```csharp
var decoder = new FnDecoder("001122334455"); // Synthetic example selector.
var events = new FnInputAdapter("keyboard", "fn").Convert(
    decoder.Process(BluetoothPacketKind.ReceivedAcl, packetBytes, timestamp));
```

This decoder consumes supplied packet records; it does not open a Bluetooth device or start a trace. Connection and channel-setup records are required before reports can bind to the selected device. Packet kinds follow the BTHPORT interpretation, not standard H4 type bytes.

The core exposes press/release/reset transitions. Tap, hold, toggle and multi-press interpretations belong to consumers. See [decoder API](docs/core-api.md) and [profile compatibility](docs/compatibility.md).

## Optional Windows / Deskflow integration

```powershell
./Build.ps1 -IncludeDeskflowAdapter
```

This also builds the Windows x64 capture/runtime package and runs its pure checks. It does not install or start live capture.

The included live backend requires **administrator privileges, sensitive Windows Bluetooth logging and an unsupported private driver-refresh request**. Other local tracers could access keyboard reports and Bluetooth encryption keys. Clearing the driver's cached logging state requires a true Windows Restart.

These requirements apply to the optional backend, not to the pure event model, decoder or examples. Read [SECURITY.md](SECURITY.md) and the [Deskflow integration guide](docs/integrations/deskflow.md) before considering a live run.
An explicit experimental `events-only` installation mode emits fixed Fn state
through a protected local pipe instead of native Mouse 5 input. The separate
Kanata consumer runs without elevation. This is not a general scripting service;
its installed/live behavior still needs validation. `-IncludeWindowsCapture`
builds the same optional runtime without implying a Deskflow-only output mode.

## Extend InputLift

A useful contribution can be small: a source adapter producing the existing events, a pure consumer, a synthetic fixture or a compatibility improvement. Extensions do not have to become full desktop applications.

Start with [the extension guide](docs/extensions.md) and the working examples. Keep device decoding, action policy and privileged acquisition separate. The examples are not dynamically loaded into the elevated runtime.

See [architecture](docs/architecture.md), [contributing](CONTRIBUTING.md) and [provenance](docs/provenance.md). Development used AI assistance; report only validation actually performed. Automated checks do not establish hardware compatibility or validate live input delivery.

Use repository Issues for reviewed bug reports and extension proposals. For vulnerabilities, follow [SECURITY.md](SECURITY.md) instead of posting sensitive details publicly. Maintainers should follow the [publication notes](docs/publication.md) before creating a release.

Licensed under [MIT](LICENSE). No third-party application, installer or driver binaries are bundled.
