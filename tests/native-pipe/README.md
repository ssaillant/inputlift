# Native Fn-pipe fixture

This fixture is for the existing **disposable GitHub-hosted Windows CI** account
harness. It must not run on the working workstation. Building is inert:

```powershell
./Build.ps1 -IncludeKanataAdapter
./tests/native-pipe/Build-Fixture.ps1
```

The build compiles the exact `src/FnEventPipe.cs` with a test-only `DailyPaths`
shim. The shim selects the temporary standard reader's SID. Production uses the
same Windows account at elevated/medium integrity; CI uses two different
accounts. Consequently these checks prove native pipe implementation/ACL and
consumer parsing behavior, **not** production installation or owner binding.
There are no Bluetooth, logging, registry, input or real application operations.
Its generated `build-result.json` is invalidated before validation or compilation;
the coordinator matches the staged EXE and current production source against that
successful build manifest. Failed builds cannot supply a success manifest.

The existing hosted account harness calls:

```powershell
./tests/native-pipe/Invoke-Cases.ps1 -ReaderCredential $credential `
  -ReaderSid $ownedUser.SID.Value -FixtureExecutable $stagedNativeFixture `
  -ReportDirectory $nativeReports
```

This script owns only its five server/client process pairs and report files. It
does not create/remove the account; the outer harness retains that responsibility.

## Orchestrator contract

Stage the fixture EXE beside hash-verified `InputLift.Kanata.exe` and
`InputLift.Core.dll`, with administrator write and selected-user read/execute
rights. The existing CI harness owns the temporary standard account and all
process handles. Start the server as the runner administrator with redirected
stdin/stdout, then the reader using that standard account's credential:

```text
InputLift.NativePipeTests.exe --server --reader-sid SID --nonce NONCE --scenario SCENARIO
InputLift.NativePipeTests.exe --client --reader-sid SID --nonce NONCE --scenario SCENARIO --server-pid PID --server-start TICKS
```

`NONCE` is a new lower-case 32-digit hex value per scenario. The server first
prints exactly one JSON ready line with `phase=ready`, `scenario`, `pid`,
`startUtcTicks`, and `pipe`. Pass that metadata to the client. Reading clients
print one `phase=connected` line only after authenticating the still-open server;
the orchestrator then writes `begin` to server stdin. After the client exits,
write exactly `finish` plus newline to server stdin, unless it has already
exited. Denied-write clients print only a final result and never use `begin`.
Then wait for both exact owned handles. No caller-supplied PID may be terminated.
Server/client results use `phase=result`, `status=ok|error` and
scenario-specific evidence flags. On any failure, preserve the exact error and
stop; no automatic retry.

| Scenario | Reader behavior | Native proof |
|---|---|---|
| `ordered` | Read frames into the actual public capture-frame consumer with an in-memory output. | Kernel ACL, duplicate-producer rejection, selected-user read, initial reset/no preconnection replay, ordered neutral/down/up/reset, disconnect stop. |
| `denied-write` | Attempt an outbound/write connection; must receive access denied. | Selected read-only principal cannot send data to the outbound producer. |
| `idle-dispose` | Do not start a client; write `finish` after ready. | Cancellation and bounded disposal before a reader connects, then 32 pending-accept shutdowns alternating immediate Stop and an observed waiting writer. Clean process exit and empty stderr remain required. |
| `unarmed-down` | Read initial reset until server EOF. | Actual wire writer rejects a down before neutral and emits no press. |
| `blocked-reader` | Connect and intentionally do not read for 6.5 seconds. | Actual buffer/write or bounded queue overload stops the producer, followed by bounded disposal. |

Every server inspects the kernel-returned DACL, requires a duplicate producer to
fail with `ERROR_ACCESS_DENIED` (5) or `ERROR_PIPE_BUSY` (231), and confirms that
the original producer stays healthy while awaiting its reader. It records the
actual rejection code as `duplicateInstanceErrorCode`, then recreates the same
pipe after disposal to prove handle closure. No raw payload or ordinary-key data
exists in this fixture.

The source has both the first-instance flag and `maxInstances=1`. The hosted
diagnostic observed code 231, which establishes the combined one-producer
contract but does not isolate the first-instance flag's behavior. The fixture
separately checks that the compiled source retains that flag policy;
`firstInstanceFlagBehaviorValidated=false` deliberately remains in every report.
Other exception types/codes, successful duplicate creation or damage to the
original producer fail the case. No assertion accepts an arbitrary error.

The reader uses the production server-PID P/Invoke and limited-process-query
helper. If the different-account default process DACL denies query access, the
result explicitly records that limitation rather than pretending it proves the
same-account production path. When the query succeeds, mismatched start time and
image are also rejected. The actual production `PipeFrames` fixed Program Files
image check remains unvalidated: the fixture is intentionally not installed there.

`remoteSmbBehaviorValidated=false` is deliberate. The production remote-client
rejection flag is source/pure-policy checked; a localhost SMB failure could occur
in authentication, service or firewall layers before reaching the pipe. No
firewall/service/network change or second network machine is part of this test.
