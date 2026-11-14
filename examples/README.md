# Synthetic consumer examples

These runnable examples show how typed input events can feed other software. They construct **synthetic logical events**, not real device reports. They do not start Bluetooth capture, inject input, change device settings, run commands, control applications, or write files/network traffic. Callbacks only print messages or change an in-memory virtual value.

Build from the repository root with `./Build.ps1`. This compiles `artifacts/examples/InputLift.Examples.exe` and runs its pure checks.

```powershell
./artifacts/examples/InputLift.Examples.exe --demo all
```

| Demo | Pattern | Extension point |
|---|---|---|
| `--demo hold` | Press activates; release or source reset deactivates. Duplicate states are ignored. | An accessibility action or temporary tool mode through an application-owned callback. |
| `--demo toggle` | A new press toggles once; held duplicates do not toggle again. Reset clears the latch. | A persistent application action, with an explicit fail-closed reset policy. |
| `--demo dial` | Normalized absolute values and signed relative steps update a clamped virtual value. | Sliders, dials or other scalar controls after adding a real input profile and output adapter. |
| `--demo stream` | Writes one JSON event per line to standard output. | A consumer in another language or process. A transport and authentication policy would be separate work. |

`--demo stream` emits only JSON lines; `--demo all` mixes human-readable headings and demo output. Timestamps are decimal strings so consumers do not lose opaque 64-bit clock precision through JSON floating-point numbers. All identifiers such as `demo-device` are logical labels, not device addresses. To run the checks directly:

```powershell
./artifacts/examples/InputLift.Examples.exe --self-test
```

## Event handling policies

Consumers select one logical source and control. A `Reset` applies to **all controls of its source**, so a matching source reset clears selected state even though the reset has no control ID. Events from other sources or controls are ignored. Consumers assume ordered input and do not implement timing-based tap/double-tap gestures.

The hold and toggle examples clear active state on reset. The virtual value example restores its configured default. These are deliberate consumer policies; the event model does not impose the same action on every application. A real output adapter must decide its own activation, permission, failure and recovery behavior.

Callbacks may throw. These minimal examples propagate failures and record their intended state before invoking a callback; that state is not acknowledgement that an external action succeeded. A real adapter needs confirmed output ownership and cleanup. Do not use these examples as privileged plugin or arbitrary-command execution hooks.

## Adding a real source or target

1. Implement and validate acquisition/decoding for the selected device profile, or adapt an existing supported decoder's transitions.
2. Emit logical typed events with a consistent clock; keep raw reports and unique identifiers private.
3. Send a source reset on input loss or disconnect and implement a safe consumer policy.
4. Connect an explicit application API or reviewed output adapter outside the privileged capture backend.
5. Add synthetic regressions and perform separate opt-in hardware validation.

The absolute-value and relative-step demos are **not** evidence of support for physical sliders, sensors, dials, pedals, remotes or additional keyboard models. Only the separately documented Bluetooth Fn profile currently has a decoder; adding another device requires new profile and hardware work.
