# Validation and build details

InputLift [v0.1.0-alpha.2](https://github.com/ssaillant/inputlift/releases/tag/v0.1.0-alpha.2) is published as an **experimental source-only prerelease**, with no executable assets. The default library/examples are separate from the experimental privileged Windows integration. A successful local build alone establishes no hosted CI result, generalized live-runtime validation or formal security audit.

## Build toolchain

The provided scripts target Windows x64 and PowerShell 7. They use the installed .NET Framework compiler at the standard Windows Framework64 location; no SDK, NuGet package or driver download is performed. The managed core/examples are compiled as AnyCPU; optional runtime executables are x64. Other build hosts/toolchains are unverified.

The local readiness checks used PowerShell **7.6.9** and the .NET Framework **4.8.1** compiler, file version **4.8.9221.0**. These are software-version details only; no machine or unique device identifiers belong in validation reports.

## Checks

Run from a clean source checkout or an archive created from Git:

```powershell
./Build.ps1
./artifacts/examples/InputLift.Examples.exe --demo all
./Build.ps1 -IncludeDeskflowAdapter
./Build.ps1 -IncludeKanataAdapter
./evidence/kanata/Verify-Fixture.ps1
./artifacts/kanata/InputLift.Kanata.exe --test-transport
./tests/Test-BuildFailure.ps1
```

The default build exercises 72 core checks and 22 synthetic-example checks. The
optional runtime also runs capture, guard, tray and 30 setup-boundary checks;
their generated reports give per-component counts. Some decoder checks run in
both assemblies; these counts must not be described as distinct hardware tests.
The recovery port adds 29 synthetic probe/admission checks and 22 capture-status
mode checks. The optional runtime build also runs 581 recovery checks: 358 pure
policy checks across 21 scenarios, 214 actual JSON cases and nine completed-guard
boundary cases against production code. These read no installed state and start no task,
pipe, Bluetooth capture or native input. See [recovery limits](integrations/deskflow-recovery.md).
The Kanata build runs 73 pure checks, including fake-clock manual-demo timing,
authenticated handoff and cancellation/release. All eleven explicit transport
checks pass locally and in the current hosted runs against an owned loopback stub,
including supervised failure
paths and three new timed-demo cases: normal completion, countdown cancellation
without a press, and held cancellation with original-connection release before
the scheduled up. An initial restricted-context loopback denial was resolved by
rechecking the final binary in an authorized execution context, without changing
Windows network settings. That mode opens local sockets but does not connect to
a real application, start capture or send native input. The synthetic packet verifier checks 28 steps across three
scenarios through the public decoder and event adapter.

The normal-user bridge launcher's 111 pure metadata/policy checks are also part
of the optional build. They extract validation functions without invoking the
entrypoint, reading protected state, connecting to a source, or starting a process.
The expanded suite passes locally and in the current hosted runs, covering the
actual JSON timestamp boundary,
including UTC `DateTime` values, strict UTC-kind checks and freshness limits.
Actual installed metadata/native authentication still needs deployment testing.

The optional runtime also runs 113 injected checks against its production cleanup
coordinator and 62 status-publication checks, including four Windows file-sharing
fixtures in ignored workspace artifacts. Registry restoration precedes entry into
owned child/output/trace teardown when setting ownership is held. Status retries
are limited to replacement errors 32/33, nine attempts and a 200 ms retry window;
an individual operating-system call can still block beyond that window. Deferred
publication does not advance the watchdog's published progress. Durable ownership
and consent journals retain their original fail-closed writer.

The failure regression intentionally runs source-incomplete fixtures and verifies
51 conditions: generated success reports are invalidated, an optional rebuild
cannot leave an old installable manifest after failing early, unrelated optional
outputs are preserved, and payload/unrelated sentinels remain unchanged. Its
small diagnostic fixtures are retained under ignored `artifacts/readiness/` paths.

These checks do not start live tracing, inject input, invoke setup or alter
logging/startup. The default examples only print synthetic events or update
virtual state. The separately approved real Kanata demo is interactive and is
never a default build/CI step. Its [recorded result](integrations/kanata-status.md)
proves a synthetic layer lifecycle, not physical Bluetooth compatibility.

Compiler CS0649 warnings in JSON-deserialized runtime state classes are expected with the existing compiler. They are not suppressed globally. Builds are not asserted to be bit-for-bit reproducible: executable identities/hashes can change between builds, and the installer verifies the manifest from the same local build.

## CI and limits

For the published alpha.2 source at
[`7e8b54b`](https://github.com/ssaillant/inputlift/commit/7e8b54b7564cb3066fd1e7a55dba8550b89debee),
[main CI](https://github.com/ssaillant/inputlift/actions/runs/43691300895) and
[tag CI](https://github.com/ssaillant/inputlift/actions/runs/43691523576) passed all
seven required validation steps. This includes the current build/pure suites,
synthetic packet evidence, eleven owned-stub/process checks, five executed native
pipe cases and failed-build regression. Installed deployment, physical Bluetooth
behavior and independent manual validation remain separate, outstanding stages.

The GitHub workflow targets the Windows Server 2025 runner generation, uses a verified full checkout commit SHA, a read-only token and no persisted checkout credentials. It runs the same pure build/test modes and the failed-build regression. Runner-image updates still occur within that generation. Dependabot is configured to propose monthly action updates; updates are not automatically merged.

Hosted Windows jobs start with an administrator token. The explicitly separate
`tests/ci/Invoke-TransportChecks.ps1` stages hash-verified files and runs the
stub/process checks under a temporary standard account in the disposable
GitHub-hosted VM. It removes the owned account/processes/staging afterward. It
refuses ordinary workstations and self-hosted runners; never run it on a personal
computer. Production companion and worker modes keep their normal-user guard.

The same hosted-only account harness runs the separately built
[native-pipe fixture](../tests/native-pipe/README.md). It compiles the exact
production pipe source with a test-only selected-reader identity shim. This can
exercise real Windows ACLs and pipe behavior without Bluetooth/logging/input,
but differs from production's same-account elevation and fixed installed image.
Treat the per-case observed result as evidence; compiling the fixture is not a
native test pass. Remote SMB rejection and physical hardware are not exercised.

The historical [hosted run 43625183677](https://github.com/ssaillant/inputlift/actions/runs/43625183677)
passed at [`02543d4`](https://github.com/ssaillant/inputlift/commit/02543d493fff80cf21e5f42d45c35f83a7e87a4e),
before the UTC timestamp fix and expanded 111-check launcher suite. The current
main/tag runs cover those changes as well as the newer Deskflow recovery port's
build/policy/JSON checks.

The public package's fresh installation, reboot path and forced live timeout
recovery remain unvalidated.
The earlier eight-case transport attempt was blocked by Windows loopback socket
access errors, with dependent cases timing out. After execution-network
permission was granted, the unchanged source passed all eight owned-loopback
checks locally. That historical result is separate from the current eleven-case
local and hosted results above. No real application or native input was involved.

The renamed/generalized live capture package, a fresh privileged installation, reconnect behavior and additional device profiles remain unvalidated in this public version. Keep these limits in compatibility and release notes; do not publish stable end-user binaries on the strength of pure checks alone.
