# Provenance

InputLift provides a typed input-event model, a supported Bluetooth Fn decoder, synthetic consumer examples and an optional Windows runtime. The core uses managed .NET Framework libraries; the optional runtime also uses Windows APIs through P/Invoke. The source is MIT-licensed, and protocol/state test fixtures are synthetic. Development used AI assistance.

Deskflow is an external dependency of the included output integration, not of the core library. Its source is neither linked nor vendored. The repository contains no downstream-application bindings, personal deployment narrative, unique device identifiers or third-party executables.

The optional Windows backend's private Bluetooth logging-refresh control and mask were identified through offline inspection of Microsoft's Bluetooth Test Platform tool. `BluetoothLoggingRefresh.cs` records that technical provenance. This is an unsupported interface, not a documented guarantee from Microsoft. No Microsoft installer, tool, binary code or driver is redistributed.

A future stable release needs additional review and preferably a documented backend with a smaller privacy tradeoff. Preserve source notices and check the license of any future imported material. The MIT license applies to this source, not to external software, platform interfaces or vendor documentation.

Technical dependency: [Deskflow](https://github.com/deskflow/deskflow). Follow upstream licenses and contribution policies when working on separate upstream projects.
