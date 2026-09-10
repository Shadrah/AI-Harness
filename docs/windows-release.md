# Windows release build

Harness ships as a self-contained x64 MSI. The installed application does not
require a separately installed .NET runtime. Git and GitHub CLI remain optional
external tools: the GitHub module detects each one independently and links to its
official installer when it is unavailable.

## Build an unsigned certification installer

```powershell
.\build\Build-Release.ps1
```

Output is written under `.artifacts/release/<version>/`. The build rejects PDBs,
local databases, logs, and files whose names indicate credentials or tokens.

## Build the signed installer

Copy `build/artifact-signing.sample.json` outside the repository, enter the Azure
Artifact Signing endpoint, account name, certificate profile, and owning directory
tenant ID, then run:

```powershell
.\build\Build-Release.ps1 -SigningMetadataPath C:\secure\harness-signing.json
```

The release script restores pinned Microsoft signing tools, signs and verifies
`Harness.exe`, packages it, signs and verifies the MSI, and writes SHA-256
checksums. It also inspects the MSI database and rejects incorrect product,
upgrade, payload, shortcut, feature, or embedded-browser bootstrap metadata.
Authentication is handled by the Microsoft Artifact Signing client via
`DefaultAzureCredential`; no Azure credential is stored in the repository.

The signing identity must have the **Artifact Signing Certificate Profile Signer**
role on the selected profile. The endpoint must match the profile's Azure region.

## Installer behavior

- Installs application files to Program Files and registers Windows repair,
  upgrade, and uninstall metadata.
- Creates a Start menu shortcut. A desktop shortcut is an optional installer
  feature and is off by default.
- Major upgrades replace application files in place and reject downgrades.
- User state remains under `%LOCALAPPDATA%\Harness` and is not removed by repair,
  upgrade, or uninstall.
