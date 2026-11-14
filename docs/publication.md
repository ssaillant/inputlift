# Publishing a source alpha

Publish the source tree as a public alpha only after reviewing the README, security model, compatibility limits and the checks in [validation](validation.md). There is no stable binary release or new device-support claim.

## Source boundary

Push the reviewed `main` branch, or create an archive from its tracked Git tree. Do not upload a ZIP of the whole workspace: `.git` can retain old local history, and ignored directories can contain builds or diagnostics.

```powershell
git status --short
git log --all --oneline
git archive --format=zip --output=artifacts/inputlift-source.zip HEAD
```

The archive command assumes the ignored `artifacts` directory exists. An archive created from `HEAD` includes only committed source; review the file list before distributing it. Never add raw captures, unique device/account identifiers, private configuration, credentials or prebuilt executables to an initial source release.

## GitHub follow-through

After authenticated repository creation and push:

1. Confirm the intended owner/name, public visibility, `main` default branch and MIT license detection. Keep the About description focused on input events and supported protocol profiles, without personal-use details.
2. Observe the first hosted workflow and resolve failures before creating a release tag. Local success is not a hosted CI result.
3. Enable private vulnerability reporting. Review available secret-scanning/push-protection settings. A SECURITY.md file documents reporting expectations but does not enable GitHub settings by itself.
4. Review Dependabot action-update proposals and require the relevant checks before merging contributions. Do not add repository secrets or deployment permissions to these pure build jobs.
5. If tagging the initial source alpha, label it as a prerelease and repeat the unvalidated live-backend/hardware limits. Do not attach local development binaries or unreviewed diagnostic bundles.

Useful official references: [workflow security](https://docs.github.com/en/actions/reference/security/secure-use), [private vulnerability reporting](https://docs.github.com/en/code-security/how-tos/report-and-fix-vulnerabilities/configure-vulnerability-reporting/configure-for-a-repository), and [Dependabot action updates](https://docs.github.com/en/code-security/how-tos/secure-your-supply-chain/secure-your-dependencies/auto-update-actions).
