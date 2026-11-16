# Changelog

All notable changes to DriveMeCrazy. Each section below is also used as the notes on the matching
[GitHub release](https://github.com/themysticg/DriveMeCrazy/releases).

## [1.1.0] - 2026-11-16

### ✨ New: update your old drivers
- **Newer drivers for old devices.** After every scan, DriveMeCrazy looks up each old driver in the
  [Microsoft Update Catalog](https://www.catalog.update.microsoft.com), which has many more Microsoft-signed drivers than Windows Update offers on its own.
- **One-click updates.** Open **Drivers → Old drivers**, tick what you want (the newest version for each device is pre-selected) and click **Update selected drivers**.
- **New on the Overview:** an **Update old drivers** button, and the counts now include Catalog drivers.
- **New setting** to turn the Catalog search off. It's on by default.

### 🛡️ Safety checks for every Catalog driver
- Downloaded only over HTTPS from Microsoft's own servers.
- Must carry a valid **Microsoft signature**.
- Must list **your exact device**. Packages for other models of the same chip are rejected.
- Must be newer than your current driver and built for your Windows version.
- Installed with Windows' own `pnputil`, never by running a vendor installer. Windows still decides whether the new driver is a better match.

### 🔧 Improvements
- Everything is downloaded and checked **before** anything is installed, and **network adapter drivers go last**, so your internet connection dropping during an install can't break the other updates.
- After installing, the app reads the **live** driver version from Windows, so results and the Old drivers list are correct right away instead of after a restart.
- If Windows keeps your current driver because it ranks it higher, the app says so in plain words and stops offering that version.
- Packages that turn out to be for a different device model aren't offered again.
- Downloads are retried automatically if the connection blips.

### 🐛 Fixes
- Store apps that turn out to be **already up to date** no longer stay ticked or keep being counted.
- The Overview tiles and menu badges now update right after installing.
- When **System Protection is turned off**, the app now correctly offers to turn it on and create a restore point. Before, it only asked whether to continue without one.

## [1.0.0] - 2026-11-16

First release. 🎉

### ✨ Features
- **Drivers & firmware** from Windows Update, matched to the exact hardware in your PC.
- **Windows updates**: security, quality and optional updates.
- **Microsoft Store app** updates.
- **Old drivers report** with a quick web search for the manufacturer's page.
- **Safety net:** restore point before installing, install history, and logs.
- **Fluent design** that follows your light/dark theme, in a single `.exe` with no installer.

### 🛡️ Safety
- Only official Microsoft sources. Nothing is downloaded from third-party sites.
- Only the newest offer per device is pre-selected, and drivers older than what you already have are flagged.
- Licence terms are never accepted for you.
