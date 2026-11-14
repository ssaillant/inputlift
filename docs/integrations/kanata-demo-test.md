# Focused Kanata synthetic demo

This optional test asks whether a physical `a` key produces `a` normally, `b`
during one synthetic virtual-key hold, then `a` again. It uses real Kanata
keyboard remapping. It does not require Bluetooth capture, administrator
privileges, an InputLift installation or a particular keyboard.

Use a fixed InputLift commit or tag and Kanata v1.12.0. Save work, stop other
remappers, and use a blank disposable text editor. Do not run this against a
configuration with unrelated actions. Actual blank-editor effects and independent
manual testing remain unvalidated for this timed demo.

## Build and validate without remapping

From normal-user PowerShell in the checkout:

```powershell
./Build.ps1 -IncludeKanataAdapter
kanata --check --cfg ./integrations/kanata/held-layer.kbd
```

The build uses the existing compiler and performs pure checks; it does not
download or launch Kanata. The second command assumes your separately obtained
Kanata v1.12.0 executable is available as `kanata`, and validates configuration
without starting remapping. Stop at an error and preserve the exact bounded
diagnostic. Do not repeat a failed interactive test without reviewing it.

## Separately opt in to the interactive test

1. In one normal-user PowerShell window, start the reviewed sample configuration:

   ```powershell
   kanata --cfg ./integrations/kanata/held-layer.kbd --port 127.0.0.1:7070
   ```

2. In the blank editor, press lowercase `a` and confirm the baseline is `a`.
3. In a second normal-user PowerShell window in the checkout, run:

   ```powershell
   ./artifacts/kanata/InputLift.Kanata.exe --demo --port 7070 --virtual-key inputlift-fn `
     --demo-countdown-ms 5000 --demo-hold-ms 5000
   ```

4. Immediately focus the blank editor. Press lowercase `a` occasionally. After
   connection validation there are five seconds of neutral countdown, then five
   seconds holding the sample layer. Confirm `a` changes to `b`, then returns to
   `a` after automatic release. Startup and OS scheduling can shift the wall-clock
   timing; do not interpret the countdown as a latency measurement.
5. Return to the second PowerShell window and retain its JSON result. An `ok`
   report and layer-notification booleans describe process/protocol observations;
   they do not replace your editor observation. `finalReleaseSent` reports a
   best-effort cleanup write, not an application acknowledgement.
6. Stop Kanata in the first window with Ctrl+C. If the layer appears stuck or
   either program reports an error, stop Kanata before any further test. This
   focused test does not exercise real-server crashes or source failures.

The optional countdown accepts 0–10000 ms and the hold accepts 100–10000 ms.
Defaults remain an immediate press and a 1000 ms hold. The supervisor keeps its
existing liveness thresholds and limits this ordinary demo to the configured
window plus five seconds of startup/completion allowance before cleanup. To
cancel, focus the companion's supervisor window and press Ctrl+C; simultaneous
process/OS failure or an unavailable Kanata endpoint can still prevent cleanup.

## Minimal report

Report the InputLift commit/tag, Kanata version and Windows version, then:

```text
Build/pure checks: pass / exact bounded error
Kanata --check: pass / exact bounded error
Interactive test performed: yes / no
Baseline a -> a: pass / fail / not tested
Timed hold a -> b: pass / fail / not tested
After release a -> a: pass / fail / not tested
Companion JSON status and observed-layer booleans:
Companion and Kanata stopped: yes / no / uncertain
Other stages: not tested
```

Do not attach editor content, raw captures, addresses, serial numbers, account
identifiers, machine paths or personal workflows. Share only bounded error
tokens after removing private data. No physical Bluetooth compatibility or
installed event-only path is established by this test. See the broader
[tester checklist](kanata-testing.md) before considering any other stage.
