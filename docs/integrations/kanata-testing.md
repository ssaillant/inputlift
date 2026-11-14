# Independent Kanata validation

No independent validation has been recorded yet. Volunteers should use a fixed
commit or prerelease tag so results identify the code actually tested.

## Start with an unprivileged test

The first useful report needs only Windows, the documented build toolchain, and
Kanata v1.12.0. It does not require a particular physical keyboard. Build the
companion and run its pure tests. Validate `integrations/kanata/held-layer.kbd`
with Kanata's `--check --cfg` before considering an interactive remapping test.

An interactive synthetic demo should establish:

- Baseline: `a` produces `a`.
- During a companion-owned virtual-key hold, `a` produces `b`.
- After release/reset, `a` produces `a` again.
- Duplicate down/up input does not toggle or repeat the held action.
- Source loss and the demonstrated worker-failure path release the layer or
  report an explicit unconfirmed release and require recovery.
- Kanata termination stops the companion; no press is replayed on a later start.

Use a blank text editor and save work first. The configuration is intentionally
limited to a visible letter mapping. A process kill, OS failure, or inaccessible
Kanata endpoint can prevent cleanup. If the layer remains active, stop Kanata;
do not keep testing against an uncertain held state.

## Optional physical-source stage

This stage is not a general invitation to enable Bluetooth tracing. First review
the exact candidate, supported radio/profile, installation and rollback steps,
and [security requirements](../../SECURITY.md). Explicitly opt in before any
privileged setup or logging change. Testing the source requires ongoing sensitive
Bluetooth logging; other local tracing programs can access keyboard reports and
encryption keys. Clearing the driver's cached state requires a true Windows
Restart after disabling startup and verifying owned cleanup.

The physical-source report should additionally establish connection binding,
neutral-before-arm, press/release, source disconnect while held, reconnect,
source/consumer termination, and no unintended action while unarmed. Use only
non-sensitive test input. Stop at the first unexpected error and record its
exact diagnostic stage; do not retry without reviewing the failure.

Use the explicit [event-only installation recipe](kanata.md#fresh-physical-source-setup-for-developers),
then the default read-only `Start-Bridge.ps1` inspection before an explicit
`-Connect`. Do not install the default Deskflow mode or manually guess session
identifiers. The launcher checks metadata; a successful inspection alone does
not prove native pipe access or Bluetooth behavior.

## Report only necessary information

Include:

- InputLift commit/tag, Kanata version, Windows version, and build/test outcomes.
- Which stages were actually performed and which remain untested.
- Product/profile IDs only when reporting hardware compatibility.
- Expected/observed behavior and bounded error/status tokens.

Exclude raw captures, typing, Bluetooth addresses, serial numbers, account SIDs,
machine names/paths, encryption keys, employer details, and personal applications
or workflows. Synthetic fixtures are enough for a protocol-only report.

An agent or automated runner is not an independent physical-device tester. Record
the tester's public handle only with their consent; an anonymous report may be
described as such without inventing attribution.
