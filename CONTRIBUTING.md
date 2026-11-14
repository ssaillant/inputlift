# Contributing

Useful contributions include hardware compatibility reports, pure decoder tests, safer alternatives to sensitive Bluetooth logging, and improvements to cleanup/reconnect behavior. Keep changes narrow and explain the failure or user behavior they address.

Run `./Build.ps1` to build and test the independent core. Run `./Build.ps1 -IncludeDeskflowAdapter` for changes to the optional Windows x64 runtime or integration. Both run pure checks without starting capture or injecting input. Do not add hardware tests, driver calls or privileged settings changes to normal builds or CI. Mark interactive tests explicitly and explain their side effects before anyone runs them.

For Kanata changes, run `./Build.ps1 -IncludeKanataAdapter` and the synthetic
packet evidence verifier. The separately invoked `--test-transport` mode talks
only to an owned loopback stub; normal self-tests create no sockets. Real Kanata
and physical-source tests require explicit opt-in and distinct result labels.

Preserve the installation ownership checks, administrator-only write boundary, explicit logging consent, radio restrictions, bounded parsing and independent cleanup supervision. Broadening supported hardware requires reviewed evidence and an opt-in test plan, not just a changed VID/PID constant.

Use synthetic protocol fixtures. Never commit captured Bluetooth traffic, typed content, real keyboard addresses, user SIDs, machine paths, certificates, secrets or personal configuration. Share product IDs rather than individual identifiers.

State what you tested, including what remains untested. A successful pure test does not establish a working keyboard/radio pair. Keep the compatibility document limited to validation actually performed for the submitted version.

AI-assisted work is welcome when disclosed and reviewed. The submitter remains responsible for understanding the change, its licensing/provenance and its test results. Do not claim human-only authorship or hardware validation that did not occur. Follow each upstream project's own contribution rules when interacting elsewhere.

By contributing code, you agree to provide it under this repository's MIT license. Retain existing notices and identify any third-party material and its license. Do not bundle proprietary drivers, installers or reverse-engineered binary fragments.
