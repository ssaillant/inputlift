# Ephemeral status publication checks

Run on Windows with the existing x64 .NET Framework compiler:

    ./tests/ephemeral-status/Invoke-Cases.ps1

The fixture compiles the production publisher and calls its actual retry,
cancellation, cleanup and sequence-commit methods. Injected failures verify that
only replacement errors 32/33 are deferred, retry delays stop within 200 ms, and
other errors retain their exact exception/HRESULT and remain fatal.
An overscheduled retry delay cannot admit a new replacement after the window.

Four real Windows file cases use synthetic status JSON: a reader denying delete
sharing, a reader released during retry, a reader permitting delete sharing, and
cancellation during a sharing conflict. They verify atomic last-good retention,
one owned temporary file, exact cleanup, and preservation of an unrelated file.
Release and cancellation cases use a controlled clock with real Windows file
operations so cold filesystem latency cannot obscure those branch assertions.
Published progress advances only after replacement succeeds; the existing guard
suite separately checks its unchanged fifteen-second progress lease.

All files stay under ignored artifacts/ephemeral-status/. No administrator
rights, installation, Bluetooth, input, registry, network or real helper state
are used. The 200 ms window bounds retries and deliberate delay; a synchronous
Windows filesystem call itself can still block.
