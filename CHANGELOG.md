# Changelog

## 0.1.0-alpha.2

- Add default read-only protected-state inspection and an explicit `-Connect`
  bridge launcher; session identifiers no longer need to be copied manually.
- Add 111 pure launcher checks and an explicit event-only installation/recovery
  recipe for opt-in developer testing.
- Accept UTC `DateTime` values produced by PowerShell JSON parsing while
  retaining strict UTC-kind and freshness checks.
- Treat an intentional Ctrl+C as graceful cancellation, with release through the
  worker's existing connection; unexpected EOF/heartbeat loss remains an error.
- Correct the event-only pipe's native direction from inbound to outbound. The
  [Windows API contract](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-createnamedpipea)
  requires outbound access for a read-only client; alpha.1's flag was wrong.
- Add an eighth owned-stub check for stopping while held, plus hosted-only native
  pipe fixtures. All five native scenarios passed in
  [hosted CI](https://github.com/ssaillant/inputlift/actions/runs/43625183677).
  These use a test-only reader-identity shim, not a production installation.

The hosted result at `02543d4` predates the UTC timestamp fix. The updated
111-check launcher suite passes locally; the current candidate needs a fresh
hosted run.

The installed event-only and physical Bluetooth path still needs validation.
There is no independent tester or upstream acceptance claim.

## 0.1.0-alpha.1

Source-only experimental release. No compiled executables, installers, drivers or
diagnostic bundles are distributed with this release.

- Independent typed event API and one bounded Bluetooth Fn profile decoder.
- Language-neutral synthetic packet fixtures and a public-API verifier.
- Optional normal-user Kanata companion using loopback virtual-key commands,
  neutral arming, duplicate suppression and supervised release handling.
- Explicit experimental Windows event-only capture mode with a protected local
  Fn-state pipe. The existing Deskflow output remains the default mode.
- Example held-layer configuration and opt-in real Kanata test harness.

Validation: 72 core checks, 22 example checks, 28 Kanata pure checks, seven
owned-loopback protocol/process checks, three synthetic packet scenarios with
28 steps, runtime/setup checks and 45 failed-build regression checks. Counts are
software checks, not independent devices. The official Kanata v1.12.0 binary
validates the example and a local synthetic real-server test observed
base → lifted → base with owned-process shutdown.

The [hosted candidate checks](https://github.com/ssaillant/inputlift/actions/runs/43622329855)
passed, including all seven process checks under a temporary standard account.
The [validation record](docs/integrations/kanata-status.md) explains scope.

Installed event-only capture, cross-integrity native pipe behavior, physical
typing and independent hardware validation remain unverified. The optional
Bluetooth backend requires sensitive diagnostic logging and an unsupported
driver request. Kanata cleanup is best effort; hard failures can require stopping
Kanata. Read [SECURITY.md](SECURITY.md) before considering live acquisition.

The [Kanata design proposal](https://github.com/jtroo/kanata/discussions/2247)
and [independent tester request](https://github.com/ssaillant/inputlift/issues/1)
are open. Posting a proposal does not establish acceptance or adoption.
