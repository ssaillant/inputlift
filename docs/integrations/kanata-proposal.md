# Kanata proposal: optional recovered-control companion

Design notes supporting [Kanata Ideas #2247](https://github.com/jtroo/kanata/discussions/2247).
The posted proposal links fixed evidence and a tester request. No maintainer
acceptance or independent validation is implied. The [validation record](kanata-status.md)
tracks observed results and outstanding stages.

## Problem and scope

Some device-specific Bluetooth control fields do not appear as ordinary Windows
keyboard events, so remapping an existing scan code cannot reach them. InputLift
contains a bounded decoder for one such Fn profile and a separate typed event
model. A companion could translate its press/release/reset state to one dedicated
Kanata virtual key through Kanata's existing TCP API.

This builds on the companion/documentation approach discussed in
[Kanata #1984](https://github.com/jtroo/kanata/discussions/1984). The initial example
is a held layer, with no application-specific use case. The proposal does not ask
Kanata to adopt the privileged Windows acquisition backend or a .NET dependency.

## Proposed boundary

- An optional unprivileged companion targets protocol 1 on loopback only.
- Handshake verifies capabilities and the dedicated virtual-key name.
- New presses require a healthy source and observed neutral state.
- Duplicate input is suppressed; reset/teardown requests release.
- A separate supervisor handles worker failure. TCP disconnect itself does not
  clear Kanata's virtual-key state, and socket writes are not action acknowledgements.
- Acquisition remains separate, with explicit limits and security documentation.

The current acquisition option requires sensitive Windows Bluetooth logging and
an unsupported driver-refresh request. Other local tracing tools can access
keyboard reports and encryption keys. A documented acquisition alternative is
desirable; this proposal does not present the workaround as suitable for general
automatic inclusion in Kanata.

## Evidence to attach

Link a fixed InputLift commit, the versioned protocol references, synthetic packet
fixtures, pure and explicit local transport results, actual configuration checks,
and any real Kanata/physical-device results. Mark missing stages as pending. Do
not attach raw packet captures, private device addresses, or personal workflows.

## Design question

Would a separately maintained companion and a small configuration/documentation
recipe be a useful fit for Kanata's community integrations? Are there preferred
virtual-key lifecycle or recovery conventions to follow before proposing a link?

Development uses AI assistance; all compatibility and validation claims must be
reviewed against actual recorded results.
