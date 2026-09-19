# BootWakeMailer

BootWakeMailer is a Windows 11 x64 background service that sends an email
notification when the service starts or when Windows resumes from
sleep/hibernation.

See `requirements.md` for the MVP requirements and `architecture.md` for the
design.

## Build and test

```powershell
dotnet build BootWakeMailer.sln -c Release
dotnet test tests\BootWakeMailer.Tests\BootWakeMailer.Tests.csproj
```

Name `BootWakeMailer.sln` explicitly: the repository also contains an empty
`BootWakeMailer.slnx`, which would otherwise be picked up first.

## Publish a release

```powershell
.\scripts\publish.ps1
```

This builds the self-contained win-x64 package in
`artifacts\publish\BootWakeMailer-win-x64`. Add `-Zip` to also create
`BootWakeMailer-win-x64.zip`.

The package contains the two published applications plus the installation
entry points:

```text
BootWakeMailer-win-x64/
├─ install.cmd
├─ install.ps1
├─ uninstall.cmd
├─ uninstall.ps1
├─ README.txt
└─ app/
   ├─ service/      BootWakeMailer.Service.exe
   └─ configtool/   BootWakeMailer.ConfigTool.exe
```

The equivalent raw commands are:

```powershell
dotnet publish src\BootWakeMailer.Service\BootWakeMailer.Service.csproj -c Release -r win-x64 --self-contained true -o artifacts\publish\BootWakeMailer-win-x64\app\service
dotnet publish src\BootWakeMailer.ConfigTool\BootWakeMailer.ConfigTool.csproj -c Release -r win-x64 --self-contained true -o artifacts\publish\BootWakeMailer-win-x64\app\configtool
```

Both applications are self-contained, so the deployment machine does not need a
separately installed .NET runtime. The publish script fails the build when a
publish output does not contain the runtime.

The service and the configuration tool are published into separate directories
on purpose: the WinForms tool resolves
`System.Security.Cryptography.ProtectedData` from the WindowsDesktop shared
framework while the service resolves it from the NuGet package, so one merged
directory would give one of the two programs a dependency set that does not
match its `.deps.json`.

## Install

Extract the release package and double-click `install.cmd`. It requests
administrator privileges through UAC and then runs `install.ps1`.

`install.ps1` copies the application files to
`%ProgramFiles%\BootWakeMailer`, creates `%ProgramData%\BootWakeMailer`,
registers the `BootWakeMailer` Windows Service with Automatic startup and starts
it. Running it again updates the existing installation and service registration
instead of creating a second service.

Useful parameters:

| Parameter | Purpose |
| --- | --- |
| `-SourceDirectory` | Payload directory. Defaults to `app` next to the script. |
| `-InstallDirectory` | Defaults to `%ProgramFiles%\BootWakeMailer`. |
| `-DataDirectory` | Defaults to `%ProgramData%\BootWakeMailer`. |
| `-ServiceName` | Defaults to `BootWakeMailer`. |
| `-WhatIf` | Reports what would change without touching the machine. |

## Uninstall

Double-click `uninstall.cmd`. It stops and removes the service, deletes
`%ProgramFiles%\BootWakeMailer` and deletes `%ProgramData%\BootWakeMailer`,
including `config.json`, `queue.json` and `status.json`. Application data is not
preserved. Running it again is safe.

## Test the installation scripts

```powershell
pwsh -File tests\scripts\Test-InstallScripts.ps1
```

The suite has no dependency on Pester. It runs the installation scripts against
temporary directories with `-SkipServiceRegistration`, so it needs no
administrator privileges and never touches the real installation, the real
application data or the real Windows Service. Pass `-PackageDirectory` to also
validate a built release package:

```powershell
pwsh -File tests\scripts\Test-InstallScripts.ps1 -PackageDirectory artifacts\publish\BootWakeMailer-win-x64
```

Registering a Windows Service, starting it and installing into
`%ProgramFiles%` require administrator privileges and a real Windows machine, so
those steps are not covered by the suite and have to be verified manually.
