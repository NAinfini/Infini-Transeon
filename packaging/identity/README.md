# Package identity spike

This directory contains the shared package-identity manifest prototype for the installer and portable distribution paths. It is not yet a release package.

The identity and publisher values are development placeholders. Release packaging must replace them with the stable signed identity without changing the capability contract.

M0 remains blocked until an ordinary non-admin account proves all of the following on every supported Windows 11 release:

- installer registration, update, repair and uninstall;
- portable external-location or sparse-package registration on first run;
- portable update, moved-directory recovery, explicit removal and stale-registration cleanup;
- `GraphicsCaptureAccess.RequestAccessAsync(Borderless)` grant, denial and restart persistence;
- `GraphicsCaptureSession.IsBorderRequired = false` only after a granted result.

No runtime component may report borderless capture as available merely because this manifest exists. Availability requires successful package-identity detection and the runtime consent result.
