# Deskflow disconnect and worker recovery

These behaviors belong to the optional Windows `deskflow` output mode. The
independent decoder/event library and the `events-only` producer do not depend
on Deskflow or use its availability probe.

## Keep acquisition separate from output availability

| Condition | Runtime behavior |
|---|---|
| Deskflow or its remote route is unavailable | Pause new Mouse 5 presses while Bluetooth acquisition and device identification continue. |
| An owned press spans route loss | Release owned output through the existing journal and acknowledgement path. An uncertain release remains an error. |
| The route returns while Fn is held | Wait for an actual release; never replay the held press. |
| A fresh route and known neutral input are available | Accept a new physical press without re-identifying the Bluetooth device. |
| The Bluetooth connection/channel changes or the Windows host suspends | Reset identification/neutral state through the existing source-reset path. |

The route probe uses one read-only background worker. Capture/status callbacks
read immutable observations rather than enumerating processes or querying
another process's window. Slow, failed, paused or stale observations refuse new
presses. Observation age and query duration together must fit the 500 ms
freshness limit. The output path rechecks the same route epoch after hook
preparation, so a selection/process change during preparation suppresses DOWN.

A response from the verified local Deskflow window proves only that window
responded. Window selection remains a compatibility heuristic; it does not
authenticate remote focus, prove client connectivity or acknowledge a remote
application action. Native hook health, local suppression and release safeguards
remain necessary. A disconnect at the final input boundary can still leave the
remote application's state uncertain.

## One retry for a fully cleaned worker timeout

The tray supervisor can reserve one fresh capture attempt per explicit Start or
logon session, and only for the exact 15-second worker-progress timeout. Before
queuing it, the matching protected run/result/guardian records must confirm all
of the following:

- Owned run identity and normal worker exit, with complete output draining.
- Registry-flag absence, owned trace stopped, worker quiescence and shortcut release.
- Guard and independent guardian exit, with only the exact classified timeout errors.

The attempt waits at least five seconds and requires two seconds of fresh local
Deskflow-window responses. It validates the protected installation, consent,
configured keyboard/output mode and enabled owned task again before launch.
An unsuccessful launch consumes the attempt. Healthy time does not replenish it.

Stop, Exit, disabling startup or host shutdown cancels pending recovery. Extra
errors, malformed records, identity changes, incomplete cleanup, uncertain input
release and a second timeout remain stopped for review. An exited guard completes
its generation even when cleanup cannot be established; a separate cleanup latch
blocks a new capture and requests a Windows Restart. A later UI/probe failure
cannot overwrite already-verified clean proof. The existing watchdog
limit is unchanged; progress is never invented to keep a stalled worker alive.

## Startup and mode boundaries

The existing logon task launches the tray supervisor in the background. Hiding
the status window leaves it running. A new capture still needs trustworthy
connection/channel setup and an actual neutral keyboard report. If the keyboard
connected before capture began, physical off/on identification may be required;
Shift is a predictable way to produce the neutral report. Automatic connection
bootstrap or a software reconnect is not implemented by this change.

`events-only` retains its existing single-session producer/consumer behavior and
does not create a Deskflow route probe or queue this automatic retry. Restarting
that producer would create a new nonce and pipe identity; authenticated consumer
rediscovery and reset handling require a separate design. No Kanata reconnect
behavior is implied.

## Validation scope

`./Build.ps1 -IncludeDeskflowAdapter` runs the inert runtime checks and
`tests/recovery/Test-Recovery.ps1` before writing an installable manifest. Tests
cover blocked/stale probes, pause/resume generations, epoch changes, held-key
release/rearming, mode separation, one-attempt budgeting, Stop races and actual
JSON deserialization into the production cleanup classifier. They start no
Bluetooth capture, native input, installed task or device reconnect.

The local suite passes 581 recovery checks (358 policy checks across 21 scenarios,
214 production JSON cases and nine completed-guard boundary cases), plus 29 synthetic probe/admission and
22 capture-status mode checks. Counts are software checks, not devices or live
disconnect tests. A fresh hosted run is still required for this source revision.

These checks establish software behavior, not a complete public-runtime
deployment or independent hardware validation. See [validation](../validation.md)
and [the integration guide](deskflow.md) for acquisition and security limits.
