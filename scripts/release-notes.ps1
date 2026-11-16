<#
.SYNOPSIS
  Builds the GitHub release notes for a version from CHANGELOG.md plus download info.
.EXAMPLE
  ./scripts/release-notes.ps1 -Version 1.1.0 -Sha256 3E71... -OutFile notes.md
#>
param(
    [Parameter(Mandatory)] [string]$Version,
    [string]$Sha256,
    [string]$OutFile = 'release-notes.md',
    [string]$Changelog
)

$ErrorActionPreference = 'Stop'
if (-not $Changelog) { $Changelog = Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) '..\CHANGELOG.md' }
$Version = $Version.TrimStart('v')
$lines = [IO.File]::ReadAllLines([IO.Path]::GetFullPath($Changelog), [Text.Encoding]::UTF8)

# Take everything between "## [<version>]" and the next "## [" heading.
$start = $lines.IndexOf(($lines | Where-Object { $_ -match "^## \[$([regex]::Escape($Version))\]" } | Select-Object -First 1))
if ($start -lt 0) { throw "CHANGELOG.md has no section for $Version" }
$end = $lines.Count
for ($i = $start + 1; $i -lt $lines.Count; $i++) { if ($lines[$i] -match '^## \[') { $end = $i; break } }
$section = ($lines[($start + 1)..($end - 1)] -join "`n").Trim()
# Release pages already show a title, so demote "### " headings to "## ".
$section = $section -replace '(?m)^### ', '## '

$notes = @"
$section

---

## ⬇️ Download

Download **DriveMeCrazy.exe** below. It's a single file and needs no installer and no .NET. Run it and accept the administrator prompt.

> [!NOTE]
> Windows SmartScreen may warn because the app isn't code-signed yet: click **More info → Run anyway**.

| File | SHA-256 |
|---|---|
| ``DriveMeCrazy.exe`` | ``$Sha256`` |

Requires Windows 10 version 2004 or later, or Windows 11 (x64).
"@

[IO.File]::WriteAllText([IO.Path]::GetFullPath($OutFile), $notes, (New-Object System.Text.UTF8Encoding($false)))
Write-Host "Wrote release notes for $Version to $OutFile"
