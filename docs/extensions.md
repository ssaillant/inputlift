# Events and extensions

InputLift separates a source's control state from the software action it might trigger. The event model is implemented; the non-Fn hardware sources below are extension ideas demonstrated using synthetic events.

## Primitive events

| Kind | Payload | Possible source |
|---|---|---|
| Pressed / Released | Source and control IDs; no numeric payload | Button, key, switch or pedal. |
| ValueChanged | Normalized `Value` in `[0,1]` | Slider, pressure reading or analog trigger. |
| Delta | Signed nonzero integer `Steps` | Rotary encoder, wheel or step-based control. |
| Reset | Source ID, no control ID or numeric payload | Disconnect, input loss or source restart. |

Examples in the last column are not supported-device claims. Additional report decoders or acquisition backends must actually be implemented and validated.

`SourceId` and `ControlId` are logical labels of 1–64 ASCII letters, digits, dots, underscores or hyphens. A reset has no control ID and applies to all controls owned by that source. Use labels such as `demo-source`, not Bluetooth addresses, serial numbers or other unique hardware identifiers.

Timestamps are opaque values from the producer's clock. Consumers must document any ordering or timing assumptions. InputLift does not perform clock synchronization or provide a general event bus.

## Working examples

The [examples directory](../examples/README.md) contains compilable, tested consumers:

- **Hold-to-act:** emit an active/inactive callback on press/release; clear it on a matching source reset.
- **Toggle:** change virtual state on a fresh press, ignore duplicate held presses, and fail closed on reset.
- **Virtual dial/value:** set virtual position from normalized values and adjust it using relative steps, with clamping.
- **Event stream:** serialize synthetic events to stdout for another program to inspect.

These patterns could support accessibility controls, application actions, monitoring, alternate input devices or another routing tool. They do not themselves control any application or provide an accessibility certification.

## Add a source

A source adapter translates a known protocol or API into `InputEvent` values. It is responsible for device selection, framing, calibration, stale-data handling and reset notification. The event model does not infer a device's vendor report format.

```csharp
// Example boundary after a source has decoded and validated its data:
InputEvent ButtonState(bool down, long timestamp)
{
    return InputEvent.Button("example-source", "switch", down, timestamp);
}
```

For a new physical device, contribute synthetic fixtures, its non-unique product/profile identifiers and a reviewed opt-in hardware validation plan. Never commit raw captures, real device addresses, typed content or encryption keys.

A slider source must normalize its own raw range before calling `Absolute`. An encoder source must define the meaning of a step before calling `Relative`. If samples are lost, reset the source and discard invalid state instead of fabricating a release or continuing a held action indefinitely.

## Add a consumer

A consumer accepts events and applies policy. Start by printing or updating virtual state as the examples do. Test source/control isolation, duplicate input, reset, unexpected ordering and teardown before connecting an actual action.

Hold/toggle consumers are examples, not a complete gesture framework. Double presses, timed holds, chords, layers and macros would require explicit recognizers and their own timing/state tests.

Keep action consumers outside privileged capture processes. Do not add arbitrary command execution, application control, scripting or dynamic third-party plugins to the elevated backend. A future IPC/output adapter needs an explicit trust boundary and activation policy.

## Extension status

| Extension | Repository support today | Further work |
|---|---|---|
| Recovered Fn input | Real decoder + common-event adapter | Validate the generalized live deployment. |
| Button-based accessibility/action patterns | Synthetic hold/toggle consumers | Bind an approved action and validate real source/output behavior. |
| Sliders, pressure or analog controls | Normalized event API + synthetic consumer | Implement a real source profile and calibration. |
| Dials or relative controls | Step event API + synthetic consumer | Implement and test a real source. |
| Diagnostic or application event streams | Synthetic JSON stdout demo; experimental fixed Fn-only local pipe | General event transport remains separate design work. |
| Kanata virtual-key output | Experimental unprivileged loopback companion | Complete real application and independent physical-source validation. |
| Other KVMs or native input output | Core API is reusable | Implement routing, focus, ownership and release policies. |
| Multi-axis motion or touch gestures | No dedicated model yet | Define units, grouping, lifecycle and report decoders. |

Adding a wider vocabulary is not the same as adding device support. Keep both the implementation and compatibility documentation precise.
