# Security and privacy

The default InputLift core and examples are pure event/decoder code: they do not start capture, inject input or change settings and do not require administrator privileges. The following privileged-operation risks apply to the optional Windows live-capture/runtime integration, which is not appropriate for every security policy or computer.

## Sensitive logging

The runtime temporarily owns `HKLM\SYSTEM\CurrentControlSet\Services\BTHPORT\Parameters\EtwLogSensitiveData` and enables it while running. It then sends a private Bluetooth logging-refresh request. **Other local tracing tools may be able to observe keyboard reports and Bluetooth encryption keys.** Skipping ordinary key fields inside this program does not prevent the operating system from exposing that data to other tracers.

The observer processes Bluetooth protocol headers and a narrow Globe-state field. It does not save raw packets or typed text, and it has no direct networking code. This is a statement about the intended implementation, not a guarantee against defects or against unrelated software. Deskflow separately handles the user's existing input and clipboard sharing.

Removing the registry flag does not reliably disable logging already cached by the Bluetooth driver. A true Windows **Restart** is required. To keep it disabled afterward, disable the helper's automatic startup before restarting. Power loss, process termination or driver failures can interrupt cleanup.

## Unsupported driver request

The logging-refresh request is undocumented and may change or block inside the driver. The implementation intentionally permits one tested Intel radio family and refuses ambiguous radio enumeration. Do not remove this restriction merely to try another device. A driver call cannot be guaranteed to complete within a bounded time; independent cleanup supervision reduces some failure consequences but cannot guarantee recovery from every kernel or power failure.

During owned shutdown, the guardian signals the worker's injection lease, then
attempts registry restoration and absence verification before reading the final
journal or stopping the child, output and trace. It records a partial registry
checkpoint when possible. Parent registry fallback still requires confirmed
guardian exit, its existing state-file preflight and ownership of the diagnostic
mutex. If another guard owns that mutex, registry restoration is skipped while
the parent still attempts cleanup of the old identity-checked child/trace; output retains its
separate mutex safeguard. Those preflight/file/registry calls can also block.
Registry absence alone does not
prove worker, output or trace cleanup, and does not clear the driver's cached
logging state. Incomplete proof still prevents another capture from starting.

## Installation boundary

Live executables run only from the fixed protected Program Files directory. Installation metadata, executable hashes, explicit logging consent, task identity and administrator-owned ACLs bind the deployment to one account and configured keyboard. Setup is create-only and never takes over an existing installation. The privileged task must not run code from a user-writable source/build directory.

Synthetic Mouse 5 input is routed through Deskflow. The window/process gate is conservative and is not a cryptographic remote-focus guarantee. End any latched client action before changing screens. The helper does not provide isolation from the software already installed on either endpoint.

## Experimental local event consumer

The alternative `events-only` mode emits only fixed Fn-state/reset/heartbeat
records through a local outbound pipe. It retains the same privileged capture
and sensitive-logging risks. Its selected-owner ACL, first-instance flag and
consumer-side process identity checks limit accidental or unrelated connections;
they are not protection against a hostile administrator.

The separate unprivileged Kanata companion must connect only to loopback and use
one exclusively reserved virtual-key name. Kanata's TCP protocol is plaintext
and unauthenticated. Loopback blocks remote reach but does not authenticate local
callers. Never bind the demo endpoint to a home-network or VPN address.

Socket disconnection does not clear Kanata's held virtual keys. Explicit release
and independent process supervision reduce stuck-state risk, but a server/OS
failure or simultaneous process termination can defeat cleanup. A successful
socket write does not acknowledge application effects. Stop Kanata if state is
uncertain; do not promise unattended recovery from the experimental path.

## Reporting problems

For ordinary failures, share a minimal description and reviewed error wording. Include only product-level device IDs, Windows/Deskflow versions and whether a real restart occurred. Never post Bluetooth addresses, serial numbers, private keys, raw traces, typed content, clipboard content, complete installation journals or personal Deskflow configuration.

For vulnerabilities, use GitHub private vulnerability reporting if it is enabled. Otherwise request a private contact channel without publishing exploit details or sensitive diagnostics. Do not assume this repository has a security response SLA.

There are no supported stable binaries yet. Review changes before building and treat all live testing as opt-in. Never disable Secure Boot, Defender, Smart App Control or other system-wide protections to run this project.
