# Companion proposal notes

These are technical preparation notes. They are not a posted maintainer conversation or an endorsement.

## Problem and proposed contribution

A narrow Bluetooth HID vendor control can carry a real Fn state bit without producing a normal Windows keyboard event. InputLift already decodes that supported profile from supplied BTHPORT-classified connection/ACL records. A companion could forward just its validated control state to Kanata's configured virtual key, allowing Kanata to own the layer or action policy.

The proposed first demonstration is deliberately observable: while the recovered control is held, a one-key Kanata configuration maps `a` to `b`; release restores `a`. It does not require a dictation application, another computer, personal-use history or additional hardware support.

## Precise questions for maintainers

- Is a separately maintained loopback companion plus a short integration recipe an appropriate fit for the README's community-project list?
- Is the reviewed `Hello` / `RequestFakeKeyNames` / `ActOnFakeKey` interface the right supported contract for this use?
- Would a future owned/leased virtual-key activation be useful, given that client disconnect does not release state today?
- What minimum validation would maintainers want before linking an integration whose optional acquisition backend has privileged logging requirements?

## Evidence to provide

- Link the exact InputLift source revision and supported-profile limits.
- Link `fn-profile.synthetic.json`, its public-API verifier and reproducible commands.
- Link the narrowly scoped adapter/configuration and identify its actual test level.
- State independently confirmed Kanata versions and operating systems only after a real tester supplies evidence.
- Disclose the current acquisition/security requirements before proposing live hardware testing.

Do not claim that ordinary Kanata hooks are broken, that all Fn keys can be recovered, that the hardware backend is validated by pure tests, or that a maintainer has accepted the approach. Keep a companion integration and any requested upstream contribution separately reviewable.

## Publication gate

An initial version may be a clearly labeled prerelease. A stable or verified-integration claim needs the real consumer, acquisition, output and cleanup behaviors tested on the declared configuration. Record feedback and unresolved failures; do not replace a missing independent test with another automated or AI review.
