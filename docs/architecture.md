# Architecture

InputLift separates a common event model and device decoders from consumers, a privileged Windows acquisition backend and a specific output integration.

```text
Acquisition backend → independent Fn decoder → transition consumer / output adapter
                                               ├─ included Deskflow integration
                                               └─ experimental local Kanata companion
```

## Independent core

`InputLift.Core.dll` contains the immutable `InputEvent` model, the bounded device/connection/channel Fn decoder and a `FnInputAdapter` mapping to common events. The event vocabulary covers press/release, normalized values, relative steps and source reset. These types do not imply additional physical device support. The core has no dependency on Deskflow, Windows tracing, hooks, deployment metadata or application bindings.

The core does not capture Bluetooth traffic by itself. The default build compiles this library, its pure tests and the synthetic examples. Hold/toggle/virtual-value consumers and JSON stdout demonstrate extension boundaries without controlling a device or application. The Windows runtime currently reuses the underlying decoder source directly; the common-event adapter is available to other consumers. No third-party plugins are dynamically loaded into the privileged runtime.

## Optional Windows runtime

The live acquisition backend remains a guarded Windows x64 implementation. It verifies protected installation metadata, explicit sensitive-logging consent, the selected device and the narrow radio profile before observing Bluetooth ETW records. It requires fresh connection identification and a neutral state before output can start.

The tray supervisor, guard and independent guardian supervise acquisition and cleanup using per-run nonces, process start times and protected journals. New connection/channel epochs invalidate queued state. Disconnect, suspend, stale progress and unexpected send results trigger release/stop handling. Skipping an unavailable status snapshot never renews a watchdog lease.

This backend retains the sensitive-logging/private-refresh tradeoffs described in SECURITY.md. The pure core does not activate those mechanisms.

## Included Deskflow adapter

`DeskflowRouteGate.cs` contains the fixed Deskflow executable/window checks. `OwnedMouseOutput.cs` retains the native marked Mouse 5 output, hook acknowledgement and local suppression. Its new-press permission combines hook health with the Deskflow gate; releases remain possible for cleanup.

The adapter lets Deskflow observe marked events before suppressing local window delivery. Its route check is a compatibility heuristic, not authenticated focus. Removing its gate is not a supported way to create a generic local injector.

## Experimental event-only source and Kanata consumer

An explicit protected installation mode replaces Mouse 5 output with a fixed,
outbound local Fn-state pipe. It has a selected-owner read ACL, rejects remote
clients, requires a first pipe instance, bounds queued edges/write times, starts
with reset, and requires fresh neutral input. Only reset/down/up/heartbeat frames
are emitted. No consumer-supplied action or plugin runs inside elevated capture.

The unprivileged Kanata companion authenticates the selected capture process,
accepts the ordered stream, and maps one recovered control to a dedicated virtual
key on Kanata's loopback TCP interface. Output has separate supervision and
release handling. The acquisition guardian proves source cleanup, not remote
application state. Kanata provides no successful-action acknowledgement and does
not release virtual keys on client disconnect. See the
[integration and validation stages](integrations/kanata.md).

This implementation is experimental. A generic event bus, arbitrary elevated
commands, and other KVM adapters are not provided.

## Deployment and validation boundary

The optional runtime installs to fixed administrator-protected Program Files/ProgramData paths and binds its account, selected device, task and executable hashes in protected metadata. One runtime installation per machine is supported. This is not a boundary against a hostile administrator.

The generalized runtime installation and renamed executables have not been validated as a complete live deployment. Builds/CI compile the core and optional integration and run pure checks only. Passing them does not establish hardware support or prove native input delivery.
