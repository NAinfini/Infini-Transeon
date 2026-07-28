# GitHub Release and application updates

Infini-Transeon publishes only stable `vMAJOR.MINOR.PATCH` releases from the
`build-release` GitHub Actions workflow. The workflow builds and tests the
managed and native code, creates an unsigned MSI and portable archive without
package identity, generates an SBOM, signs the canonical release manifest with
Ed25519, uploads a draft GitHub Release, and publishes it only after every prior
step succeeds.

The current Windows binaries and MSI intentionally have no Authenticode
signature. Windows therefore displays **Unknown publisher**, and SmartScreen
may warn. Every GitHub Release and every in-app update prompt must state this
clearly. This is a temporary distribution policy, not equivalent to trusted
Windows code signing.

The desktop updater checks:

`https://api.github.com/repos/NAinfini/Infini-Transeon/releases/latest`

It never downloads or installs automatically. A user must approve the
download. Before the MSI is offered, the updater verifies the manifest with the
public key embedded in the application, rejects release-sequence downgrades,
checks the signed byte size and SHA-256 digest, and requires the signed manifest
to declare the MSI's `codeSigning` policy as `unsigned`. Opening the verified
MSI is a separate user action with another unsigned-publisher warning. Strict
offline mode blocks update checks before any network request.

## One-time maintainer setup

1. Authenticate GitHub CLI as a repository administrator:

   ```powershell
   gh auth login -h github.com
   ```

2. The first release key is generated once with:

   ```powershell
   ./scripts/new-release-signing-key.ps1 -KeyId release-2026-b
   ```

   The script stores the private key in Windows Credential Manager under
   `InfiniTranseon/ReleaseSigning/Ed25519` and writes only the public key to
   `ProductionReleaseTrustRoot.cs`. The current repository has already
   initialized `release-2026-b`; do not run this command again. The earlier
   `release-2026-a` public key never shipped in a production-approved release,
   and its missing private key was replaced before `v0.1.0`.

3. Upload the release-manifest key to the protected `release` GitHub
   environment:

   ```powershell
   # Local verification only; this does not contact GitHub or export the key.
   ./scripts/configure-github-release.ps1 -ValidateOnly

   # Explicit maintainer action: uploads an encrypted GitHub Actions secret.
   ./scripts/configure-github-release.ps1 -UploadSecrets
   ```

   This configures `RELEASE_ED25519_PRIVATE_KEY` and
   `RELEASE_ED25519_KEY_ID` without printing their values. Authenticode secrets
   are not required in the current unsigned mode.

4. In GitHub repository settings, protect the `release` environment with at
   least one required reviewer and restrict deployment branches/tags to the
   default branch and stable version tags. Environment protection is a
   repository-owner decision and is intentionally not changed by a local
   script.

5. Store an encrypted offline backup of the Ed25519 private key in a
   maintainer-controlled secrets vault. GitHub Actions secrets cannot be read
   back later. Losing the only Ed25519 private key would prevent existing
   installations from trusting future manifests.

## Publishing a release

1. Ensure the release commit is on `main` and all required checks pass.
2. Create one immutable stable tag:

   ```powershell
   git tag -a v1.0.0 -m 'Infini-Transeon v1.0.0'
   git push origin v1.0.0
   ```

3. Approve the protected `release` environment deployment when GitHub requests
   it.
4. Verify that the published Release contains:
   `Infini-Transeon.msi`, `Infini-Transeon-portable.zip`,
   `Infini-Transeon-source.zip`, `release-manifest.json`, `sbom.json`, and
   `THIRD-PARTY-NOTICES.json`.
   Verify that both binary packages contain `model-catalog.json` and
   `InfiniTranseon.ModelRuntime.Native.dll`, but no `models/` payload.
   Its release notes must start with the **Unsigned Windows build** warning.
5. Install the MSI on a clean supported Windows 11 x64 machine and verify an
   update check from the previous release. Confirm that the app and Windows both
   expose the unsigned-publisher warning, and that Diagnostics explicitly records
   `capture.borderless.unavailableWithoutPackageIdentity`. The system capture
   border is expected to remain in this unsigned distribution.

The workflow can also be re-run manually for an existing tag. It may replace
assets only while the GitHub Release is still a draft; a published Release is
treated as immutable.

## Key rotation

Do not overwrite `ProductionReleaseTrustRoot.cs` with a new key. Rotation must
be staged:

1. ship a release that trusts both the current and next public keys;
2. sign at least one transition release with both keys;
3. wait for the supported client population to receive the transition;
4. promote the next key and revoke the old key in a later release.

This preserves the trust path for existing installations.

## Moving to Authenticode later

Do not silently replace `codeSigning: unsigned` with an omitted field. A future
signed release must declare `codeSigning: authenticode` and bind the exact
certificate subject in `authenticodePublisher`; the updater already keeps that
verification path.

The current unsigned release does not include an external-location identity
package. Microsoft requires that package to be signed by a certificate trusted
on the target computer; shipping an unsigned package would make first launch
fail and is not a production identity path.

Moving to a CA-backed package requires an explicit migration that generates the
matching executable manifest, signs and registers the identity package, restarts
the application, and asks the user for borderless-capture access. That migration
must be tested before enabling package identity in the release workflow.
