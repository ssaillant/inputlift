# Changelog

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
