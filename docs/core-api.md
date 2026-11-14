# Bluetooth Fn decoder API

This page covers the first implemented device profile. See [events and extensions](extensions.md) for the generic `InputEvent` vocabulary and synthetic consumer examples.

The `InputLift.Core.dll` assembly exposes `InputLift.Core.FnDecoder`. It is a stateful protocol decoder, not a Bluetooth capture API or a Windows keyboard driver.

## Input contract

Construct an instance with the selected device's canonical 12-hex-digit Bluetooth address. Zero/broadcast addresses and malformed values are refused. Keep real addresses out of diagnostics, examples and issues.

Feed `BluetoothPacketKind` records to `Process` using a byte array, an array segment, or a bounded byte-reader delegate. Packet kinds use the BTHPORT mapping: `HciEvent = 2`, `ReceivedAcl = 3`, `SentAcl = 4`. These values are **not** standard H4 type bytes. Remove transport framing and normalize records in the acquisition layer rather than assuming an arbitrary Bluetooth capture can be passed in unchanged.

The decoder needs matching connection and L2CAP channel-setup records before accepting a keyboard report. It supports only explicit report shapes; unknown or malformed protocol data does not establish compatibility. Maximum record bounds and byte-reader contracts are enforced by the API.

Timestamp values are opaque and retained on transitions. Use one consistent clock/time unit and serialize calls to each decoder instance. The core does not schedule actions or interpret double presses.

## Output contract

`Process` returns typed `FnTransition` values: `Pressed`, `Released` and `Reset`, with timestamp and a protocol reason token. Read `IsBound`, `IsConnectionBound` and `IsPressed` for current decoder state.

Call `Reset(timestamp)` after capture loss, a session change or another external discontinuity. It clears learned connection/channel state and returns an explicit reset notification, preceded by a release if the primary Fn state was held. A consumer must handle the reset even if it has not observed a press.

This library does not promise balanced native output events: it emits protocol state. A consumer that injects input must separately implement activation/neutral-state checks, focus or routing policy, release ownership, exception handling and cleanup. The included Deskflow integration implements those policies separately.

## Data and side effects

The core reads protocol fields, a configured device address and recognized vendor Fn fields. Ordinary keyboard key slots are skipped. It performs no native calls, live capture, file writes, networking, input injection, driver refresh or logging-configuration changes. Supplying a packet buffer does not erase its contents or stop another component from reading it; acquisition and buffer lifetime belong to the caller.

Use synthetic fixtures in tests. Do not commit real Bluetooth captures, encryption keys, typed content or unique device identifiers.
