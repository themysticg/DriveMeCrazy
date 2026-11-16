# Security policy

DriveMeCrazy runs with administrator rights and installs drivers, so we take security seriously.

## Design rules

- Updates are only obtained through Microsoft's Windows Update Agent API, the Microsoft Store install APIs, and
  the Microsoft Update Catalog. The app never downloads executables from any other source.
- Catalog driver packages are only downloaded over HTTPS from `*.windowsupdate.com` / `*.microsoft.com`. They must carry a
  valid Microsoft signature (WinVerifyTrust), and their INF must list the device's exact hardware ID. They're installed with
  Windows' `pnputil`, never by running a vendor installer.
- The app never accepts licence terms on the user's behalf. Updates that need them are skipped.
- No network calls other than those made by Windows Update and the Microsoft Store. No telemetry.
- "Search online" only opens a web search in your browser. It never downloads anything.

## Reporting a vulnerability

Please **do not** open a public issue. Use GitHub's
[private vulnerability reporting](../../security/advisories/new) for this repository instead.
We aim to respond within 7 days.

## Verifying a download

Every release lists the SHA-256 hash of `DriveMeCrazy.exe`. Check it with:

```powershell
Get-FileHash .\DriveMeCrazy.exe -Algorithm SHA256
```
