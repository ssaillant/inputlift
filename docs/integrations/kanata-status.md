# Kanata candidate validation record

This record separates observed results from release gates. It is not a hardware
compatibility certificate. Source version: `0.1.0-alpha.1`. GitHub's
[release page](https://github.com/ssaillant/inputlift/releases/tag/v0.1.0-alpha.1)
is the authority for publication state; a version in this file alone is not a
published release.

| Stage | Evidence | Status |
|---|---|---|
| Pinned interface | Kanata v1.12.0, `02242ef761ef43b860eac30067ce86904be7d466`; [references](../../evidence/kanata/protocol-reference.md) | Reviewed |
| Synthetic packet evidence | Three scenarios, 28 steps, 10 transitions/events; verifier and fixture committed as source | Local pass |
| Official distribution integrity | Windows x64 ZIP, 8,484,766 bytes, published checksum matches | Verified |
| Configuration validation | Official non-command-enabled winIOv2 console binary; `--check --cfg integrations/kanata/held-layer.kbd`, exit 0 | Pass; no remapping started |
| Consumer and supervision | 28 pure checks; seven owned-stub protocol/process checks, including consumer crash/hang and lost guardian heartbeats | Local pass |
| Hosted checks for candidate | [`f8166d7`](https://github.com/ssaillant/inputlift/commit/f8166d7783350eca18daea8af244755b9805dd23), [successful run](https://github.com/ssaillant/inputlift/actions/runs/43622329855) | Pass, including all seven process checks under a temporary standard account |
| Real Kanata synthetic demo | Official v1.12.0, base → lifted → base notifications; owned consumer and Kanata stopped | Local pass for layer lifecycle; physical typing and real-server crash recovery not tested |
| Event-only installation/native IPC | Protected install and cross-integrity source authentication | Pending |
| Physical Bluetooth source | Connection/neutral, press/release, disconnect/reconnect and cleanup | Pending |
| Upstream discussion | [Kanata Ideas #2247](https://github.com/jtroo/kanata/discussions/2247) | Proposal posted; no acceptance implied |
| Independent tester | [Recruitment/report issue #1](https://github.com/ssaillant/inputlift/issues/1) | Open; no tester or independent results claimed |
| Versioned release | Target `v0.1.0-alpha.1`; see the linked release page | Source only, experimental; no binaries |

Official ZIP SHA-256:

```text
13947ED78CFA3284BFEF854E3C542C74AB366236B72FD9F7E039F8638DEEAD9D
```

Validated/demo executable: `kanata_windows_tty_winIOv2_x64.exe`, SHA-256
`05DC54C30ED967730A3DF74EC98DC5D9F5176432EC78A77AD4AD5E4C90247132`.
It is the non-command-enabled console variant. The explicitly approved real
application test ran `tests/Test-KanataLive.ps1`; it observed the three sample
layer states and confirmed owned-process shutdown. It sent synthetic virtual-key
commands, not physical test typing. No Bluetooth capture, installation, or
settings changes occurred. The successful layer notifications are distinct from
per-key action acknowledgements, which Kanata does not provide.

Source fixture SHA-256:

```text
8056c09958b5cb53831b1252ab3d94537cfad269e1cd64105d37c3e4ee96be84
```

Artifacts from builds and local diagnostics remain ignored. Public results must
exclude account identity, machine paths, hardware addresses and packet captures.
An external tester must report their own observations; automated checks or a
second coding agent do not establish independent physical-device validation.
