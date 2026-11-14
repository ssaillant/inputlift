# Supported protocol profile and limits

The generic event API and synthetic examples do not imply additional device support. These constraints apply to the implemented Bluetooth Fn decoder and optional live backend.

| Component | Current constraint |
|---|---|
| Decoder library | AnyCPU .NET Framework 4.x assembly; supplied build checked on Windows. No Deskflow or administrator dependency for pure decoding. |
| Reference HID profile | Vendor ID `05AC`, product ID `0321`; recognized vendor Fn-report layout. IDs do not imply support for an entire product family. |
| Packet records | BTHPORT-interpreted HCI event and ACL records, including connection/channel setup. Not arbitrary H4-framed data. |
| Transport/profile | Bluetooth classic HID. Wired input and other report variants are unvalidated. |
| Live acquisition backend | Optional Windows x64 diagnostics backend, with privileged logging/refresh requirements. |
| Live backend radio | One present Intel USB Bluetooth radio, VID `8087` / PID `0AAA`. Deliberate allowlist. |
| Included outputs | Marked Mouse 5 via Deskflow; experimental event-only Fn pipe and Kanata companion. Other KVM adapters are not implemented. |
| Kanata target | Protocol 1, initial reference v1.12.0; official configuration check and synthetic base/lifted/base layer lifecycle pass locally. Physical typing, installed event-only source and independent validation remain pending. |
| Deskflow assumptions | Version 1.27 integration conventions; daemon enabled and Always run as system off. |
| Release validation | Compilation and pure tests checked. Generalized runtime installation/live execution remain unvalidated. |

Consult `src/GlobePacketDecoder.cs` for framing and vendor-field checks. Do not extrapolate offsets or hardware support from a similar device. New support needs synthetic fixtures and reviewed opt-in hardware validation.

The Windows backend may require one keyboard off/on cycle after login/reconnect, followed by neutral-state confirmation. End any latched client action before switching screens. These are integration policies, not work performed by the pure decoder library.

Unknown devices and consumers are untested, not implicitly supported. This source alpha is not a production keyboard driver, compatibility certification or completed security audit.
