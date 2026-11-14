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
./tests/Test-BuildFailure.ps1
```

The first build exercises 72 core checks and 22 synthetic-example checks. The optional build also exercises 394 runtime checks and 21 setup-validation checks. Some decoder checks run in both assemblies; these counts must not be described as distinct hardware tests.

The failure regression intentionally runs source-incomplete fixtures and verifies 24 conditions: generated success reports are invalidated, an optional rebuild cannot leave an old installable manifest after failing early, a default build preserves an unrelated optional package, and payload/unrelated sentinels remain unchanged. Its small diagnostic fixtures are retained under ignored `artifacts/readiness/` paths.

All of these checks are inert with respect to hardware: no live tracing, input injection, setup entry point, logging setting or startup task is activated. The demos only print synthetic events or update virtual state.

Compiler CS0649 warnings in JSON-deserialized runtime state classes are expected with the existing compiler. They are not suppressed globally. Builds are not asserted to be bit-for-bit reproducible: executable identities/hashes can change between builds, and the installer verifies the manifest from the same local build.

## CI and limits

The GitHub workflow targets the Windows Server 2025 runner generation, uses a verified full checkout commit SHA, a read-only token and no persisted checkout credentials. It runs the same pure build/test modes and the failed-build regression. Runner-image updates still occur within that generation. Dependabot is configured to propose monthly action updates; updates are not automatically merged.

The workflow can only be verified on GitHub after publication. Until then, a local clean-archive run is evidence of source completeness, not proof of hosted CI success.

The renamed/generalized live capture package, a fresh privileged installation, reconnect behavior and additional device profiles remain unvalidated in this public version. Keep these limits in compatibility and release notes; do not publish stable end-user binaries on the strength of pure checks alone.
