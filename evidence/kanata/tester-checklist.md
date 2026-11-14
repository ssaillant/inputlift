# Independent test protocol

**Status: recruitment/preparation checklist. No independent test is recorded here.** A useful tester is a separate person who runs the published revision and reports their own observations. Automated tests, maintainers merely reading the source, and another AI review do not satisfy that condition.

## Stage 1: source and consumer checks

Record the InputLift commit/tag, Kanata version, Windows version and the exact tests run. Build the source and run the synthetic fixture verifier first. These checks need no Bluetooth capture, administrator privileges or keyboard changes.

For a real Kanata test, use an official binary obtained independently with the tester's approval, or a binary they built. Do not redistribute an unreviewed executable in this evidence package. Validate the supplied example with `--check` before starting Kanata. Use only `--port 127.0.0.1:7070`; do not open a network/firewall exception.

The interactive demonstration changes keyboard behavior while Kanata is running. Use a blank local editor, save work first and keep an independent way to stop the programs. Start with synthetic input to the real consumer before enabling hardware acquisition. Follow the integration's current commands; a mock server alone cannot establish Kanata compatibility.

Report whether:

- The dedicated virtual key is discovered and a normal `a` stays `a` when inactive.
- A press activates the demonstration layer; `a` becomes `b` while held.
- A release restores `a` without a stuck state.
- Duplicate presses do not latch extra state.
- Reset, stream termination and transport loss end activity or produce a visible recovery requirement.
- Restart/reconnect requires neutral input before a fresh activation.
- Stopping the companion does not leave a held action; if it does, record exactly how recovery worked.

Do not infer success from a clean socket write. Record the editor's output and layer behavior. If a failure occurs, stop and report its exact wording and reproduction sequence rather than repeatedly relaunching.

## Stage 2: supported hardware acquisition

This stage is separate and opt-in. The existing backend has narrow profile/radio restrictions and privileged sensitive-logging requirements. Read `SECURITY.md` and the current integration instructions before agreeing. Do not enable capture during ordinary or sensitive typing. Do not change allowlists just because a model appears similar.

Record product-level profile/radio IDs only, whether connection identification/neutral arming succeeded, physical press/release behavior and cleanup verification. Include restart requirements and whether they were followed. Never attach raw Bluetooth captures or encryption keys.

## Minimal report

```text
InputLift revision/tag:
Kanata version and binary source:
Windows version:
Stage tested (pure / synthetic-to-real-Kanata / physical input):
Product/profile IDs, if hardware tested (no unique IDs):
Commands and exact observations:
Press / release / duplicate / reset / disconnect / restart results:
Cleanup and any recovery needed:
Unresolved failures or untested items:
```

Do not include Bluetooth addresses, serial numbers, usernames, machine names, local paths, typed private text, personal applications or workplace details. Synthetic fixture values must remain labeled synthetic. Publish the tester's report only with their agreement and retain its actual limits.
