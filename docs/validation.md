# Validation and build details

InputLift is prepared as a **source-only alpha**. The default library/examples are separate from the experimental privileged Windows integration. No hosted CI result, generalized live-runtime validation or formal security audit is implied by a successful local build.

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
The Kanata build runs 28 pure checks. Its explicit transport mode additionally
runs eight checks against an owned loopback stub, including supervised failure
paths. That mode opens local sockets but does not connect to a real application
or send native input. The synthetic packet verifier checks 28 steps across three
scenarios through the public decoder and event adapter.

The normal-user bridge launcher's 98 pure metadata/policy checks are also part
of the optional build. They extract validation functions without invoking the
entrypoint, reading protected state, connecting to a source, or starting a process.
Actual installed metadata/native authentication still needs deployment testing.

The failure regression intentionally runs source-incomplete fixtures and verifies
45 conditions: generated success reports are invalidated, an optional rebuild
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

The original public source commit passed its hosted workflow. Changes must have
their own hosted run; local success does not establish a candidate's CI result.

The renamed/generalized live capture package, a fresh privileged installation, reconnect behavior and additional device profiles remain unvalidated in this public version. Keep these limits in compatibility and release notes; do not publish stable end-user binaries on the strength of pure checks alone.
