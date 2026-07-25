# Package identity spike

This directory contains the external-location package-identity template for a future
certificate-signed distribution. It is deliberately excluded from the current unsigned GitHub
MSI and portable archive.

Windows requires an external-location identity package used on end-user computers to be signed
by a certificate trusted on that computer. The executable side-by-side manifest must also use the
same certificate subject. An unsigned identity package is therefore not a production substitute
for Authenticode or trusted MSIX signing.

`prepare-release-identity.ps1` accepts the exact certificate subject through `-Publisher`, writes
that value to the identity package, and injects matching `msix` metadata into a generated
application manifest. The checked-in application manifest intentionally has no `msix` element, so
the current unsigned build starts normally and explicitly reports that borderless capture is
unavailable without package identity. It retains the Windows capture border instead of failing
startup or silently claiming borderless support.

Once trusted signing is configured, the signed release pipeline must build and sign
`InfiniTranseon.Identity.msix`, publish the generated application manifest, register the identity
package from the installer, and validate that the package `Name`, `Publisher`, and application
`Id` match the executable manifest exactly.

Release hardware acceptance must still prove all of the following on every supported Windows 11 release:

- installer registration, update, repair and uninstall;
- portable external-location or sparse-package registration on first run;
- portable update, moved-directory recovery, explicit removal and stale-registration cleanup;
- `GraphicsCaptureAccess.RequestAccessAsync(Borderless)` grant, denial and restart persistence;
- `GraphicsCaptureSession.IsBorderRequired = false` only after a granted result.

No runtime component may report borderless capture as available merely because this manifest exists. Availability requires successful package-identity detection and the runtime consent result.
