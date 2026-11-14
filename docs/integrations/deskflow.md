# Deskflow integration

This guide is only for the optional Windows capture/runtime and Deskflow adapter. The independent decoder library does not require this setup. Run all commands below from the repository root.

This optional Windows runtime exposes **Fn-key press and release transitions from a supported Bluetooth HID profile** as standard Mouse 5 events through Deskflow.

```text
Supported Bluetooth HID input → Fn transitions → Mouse 5 press/release → Deskflow client
```

The project addresses an input gap: a vendor-specific Fn state can be present in Bluetooth reports without appearing as an ordinary Windows key event. The current implementation extracts that state for one explicitly configured keyboard and translates it into a conventional input signal. It does not emit a native Fn event on the receiving endpoint.

**Source-only alpha.** Support is intentionally narrow. The public installer and live runtime have not yet been validated as a complete generalized release. There are no published binaries and no claim of universal Bluetooth Fn support.

## Security tradeoff

The current backend requires administrator privileges, **sensitive Windows Bluetooth diagnostic logging**, and an **unsupported private driver-refresh request**. Other local tracing tools could access keyboard reports and Bluetooth encryption keys while that logging is enabled. This program skips ordinary key fields, saves no raw packet logs and makes no direct network connection; it cannot restrict what unrelated tracers observe.

Stopping removes the owned logging flag and trace, but the driver may retain its logging state until a true **Windows Restart**. Disable automatic startup before restarting if logging must remain off. Read [SECURITY.md](../../SECURITY.md) before considering a live run.

## Current scope

- Bluetooth input from one explicitly configured device and recognized HID report profile.
- Paired Fn transitions translated into marked Mouse 5 press/release events.
- New presses gated on a conservative Deskflow remote-screen check; releases allowed for cleanup.
- Local suppression of the marked output events.
- Background tray controls, optional status window, logon startup and independent cleanup supervision.
- Re-identification and neutral-state requirements after connection or channel changes.

Ordinary Deskflow route loss now pauses output while retaining Bluetooth
identification. A separate, bounded retry handles only a fully cleaned worker
timeout. See [disconnect and worker recovery](deskflow-recovery.md) for exact
conditions, held-key behavior and startup limitations. Cold-start connection
bootstrap remains separate; unattended keyboard identification is not promised.

The output is an ordinary mouse-button signal. Any downstream action depends on the receiving software's bindings and behavior; application-specific shortcuts and gesture semantics are outside this project's scope. The Deskflow gate is a compatibility heuristic, not an authenticated remote-focus signal.

See [compatibility](../compatibility.md) for the required profile and radio IDs. Other profiles, radios, architectures and integration versions are unverified. Wired input is outside the current scope.

## Build and pure checks

Requirements: Windows x64, the existing .NET Framework 4.x x64 C# compiler, and 64-bit PowerShell. The build downloads no SDK or packages.

```powershell
./Build.ps1 -IncludeDeskflowAdapter
```

This compiles three executables into `artifacts/package`, runs pure self-tests and setup-validation checks, and writes a package manifest with executable SHA-256 hashes. It does not install, start Bluetooth capture, inject input, change logging settings or register a task. Generated artifacts are ignored by Git.

Automated checks do not establish hardware compatibility or validate a live installation.

## Experimental setup

Review the source and security notes first. Use the intended Windows account, which must be able to approve administrator elevation; do not use another administrator's credentials. Only one installation per computer is supported. Setup is create-only and refuses its existing application/state paths.

1. Build locally and obtain the selected device's Bluetooth address from Windows device information. Keep that unique identifier out of commits, screenshots and issues.
2. In 64-bit administrator PowerShell under the same account, explicitly opt in to ongoing sensitive logging:

```powershell
$keyboardAddress = Read-Host 'Bluetooth address (12 hex digits without separators; do not share)'
$manifestHash = (Get-FileHash ./artifacts/package/package.json -Algorithm SHA256).Hash
./Setup.ps1 -Mode Install -KeyboardAddress $keyboardAddress `
  -ExpectedManifestSha256 $manifestHash -EnablePersistentLogging `
  -CreateStartMenuShortcut -Start
```

Setup creates administrator-protected files in `%ProgramFiles%/InputLift` and `%ProgramData%/InputLift`, records the current account and selected device in protected metadata, and creates the elevated interactive logon task `InputLift-<owner SID>`. Setup does not itself activate logging; the runtime verifies explicit consent first. Stop on an error and preserve its exact wording.

3. Configure Deskflow separately with Windows as the server. The documented integration configuration keeps **Use background service (daemon)** enabled and **Always run as system** off. Use local controls for login/UAC secure desktops. The precise reason for the privilege-dependent input-routing behavior is not established.
4. Follow the identification notifications: switch the selected keyboard off, wait for the instruction to switch it on, then tap/release left Shift when requested. Release all keys. A power cycle may be needed after login or reconnect.
5. Assign Mouse 5 to a harmless test action on the intended client and verify press/release behavior. Check for unintended local actions before using a real binding. End any latched client action before changing screens.

Setup does not install or configure Deskflow, other applications, network settings, firewall rules or a keyboard driver.

## Background controls

Closing the status window hides it; capture continues. Reopen it through the tray icon or Start menu shortcut.

| Tray action | Effect |
|---|---|
| Start / Stop | Resume or pause capture for this session; logon startup remains enabled. |
| Stop and exit | Stop capture and exit; startup remains enabled for the next logon. |
| Stop and disable automatic startup | Stop capture and leave the task disabled. The current UI has no re-enable command. |

To clear the driver's cached logging state and keep it off: end any active client action, choose **Stop and disable automatic startup**, wait for cleanup, then use **Start → Power → Restart**. Leaving startup enabled causes logging to be enabled again at the next logon.

For removal, run under the installation account in administrator PowerShell after stopping:

```powershell
./Setup.ps1 -Mode Uninstall
```

Follow the reported restart requirement. If cleanup cannot be verified, files/state are retained for diagnosis. Running `Setup.ps1` without arguments performs read-only inspection.

## Development

See [architecture](../architecture.md), [contributing](../../CONTRIBUTING.md) and [provenance](../provenance.md). Development used AI assistance; contributors should disclose assistance and report only validation they actually performed.

Licensed under [MIT](../../LICENSE). No third-party application, installer or driver binaries are bundled. This project is independent of Deskflow and the relevant hardware/platform vendors.
