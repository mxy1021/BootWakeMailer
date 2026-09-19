#Requires -Version 5.1
<#
.SYNOPSIS
    Installs BootWakeMailer as a Windows Service.

.DESCRIPTION
    Copies the self-contained win-x64 payload to %ProgramFiles%\BootWakeMailer,
    creates %ProgramData%\BootWakeMailer, registers the BootWakeMailer Windows
    Service with Automatic startup and starts it (requirements.md FR-09,
    architecture.md §13).

    The script is idempotent: running it again stops the service, replaces the
    installed files and reconfigures the existing service registration instead of
    creating a second one.

    Normally started through install.cmd, which handles the UAC prompt. Running
    this script directly also works: it re-launches itself elevated when needed.

.PARAMETER SourceDirectory
    Directory holding the published payload (the "app" folder of the release
    package, containing the service and configtool sub-directories).
    Defaults to "app" next to this script.

.PARAMETER InstallDirectory
    Installation directory. Defaults to %ProgramFiles%\BootWakeMailer.

.PARAMETER DataDirectory
    Application data directory. Defaults to %ProgramData%\BootWakeMailer.

.PARAMETER ServiceName
    Windows Service name. Defaults to BootWakeMailer.

.PARAMETER NoElevate
    Fails instead of re-launching elevated. Intended for automated tests that run
    without administrator privileges.

.PARAMETER SkipServiceRegistration
    Copies the files and creates the data directory but does not stop, register or
    start the Windows Service. Intended for automated tests, which must not modify
    the real service.

.EXAMPLE
    .\install.ps1
    Standard installation.

.EXAMPLE
    .\install.ps1 -WhatIf
    Shows what would be changed without touching the machine.
#>
[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'Medium')]
param(
    [string] $SourceDirectory,

    [string] $InstallDirectory = (Join-Path -Path $env:ProgramFiles -ChildPath 'BootWakeMailer'),

    [string] $DataDirectory = (Join-Path -Path $env:ProgramData -ChildPath 'BootWakeMailer'),

    [string] $ServiceName = 'BootWakeMailer',

    [string] $ServiceDisplayName = 'BootWakeMailer',

    [switch] $NoElevate,

    [switch] $SkipServiceRegistration
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

# Exit codes (install.cmd forwards these to the caller).
$script:ExitSuccess = 0
$script:ExitFailure = 1
$script:ExitElevation = 2

# Payload and installation layout.
$script:ServiceSubDirectory = 'service'
$script:ConfigToolSubDirectory = 'configtool'
$script:ServiceExeName = 'BootWakeMailer.Service.exe'
$script:ConfigToolExeName = 'BootWakeMailer.ConfigTool.exe'

# A self-contained publish always ships the runtime itself. Its presence is what
# makes the target machine independent of an installed .NET runtime.
$script:SelfContainedMarker = 'coreclr.dll'

$script:ServiceDescription = 'Sends an email notification when Windows starts or resumes from sleep.'

function Write-Log {
    param(
        [Parameter(Mandatory)][string] $Message,
        [ValidateSet('INFO', 'WARN', 'ERROR')][string] $Level = 'INFO'
    )

    Write-Host ('[{0}] {1}' -f $Level, $Message)
}

function Write-Step {
    param([Parameter(Mandatory)][string] $Message)

    Write-Host ''
    Write-Host ('==> {0}' -f $Message) -ForegroundColor Cyan
}

function Test-IsAdministrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Invoke-SelfElevation {
    <#
        Re-launches this script through UAC with the arguments the caller built
        at script scope, where $PSBoundParameters describes this script. Inside a
        function that automatic variable only describes the function itself, so
        the argument list must not be built here.
    #>
    param([Parameter(Mandatory)][AllowEmptyCollection()][string[]] $ForwardedArguments)

    if ([string]::IsNullOrEmpty($PSCommandPath)) {
        throw 'Elevation requires the script to run from a file (use install.ps1, not a piped command).'
    }

    $arguments = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', ('"{0}"' -f $PSCommandPath)) + $ForwardedArguments

    Write-Log 'Administrator privileges are required. Requesting elevation (UAC prompt).'

    # Inherited by the elevated child, which uses this marker to abort instead of
    # asking for elevation a second time.
    $env:BootWakeMailerInstallElevated = '1'

    $hostExecutable = (Get-Process -Id $PID).Path
    try {
        $process = Start-Process -FilePath $hostExecutable -ArgumentList $arguments -Verb RunAs -Wait -PassThru
    }
    catch {
        Write-Log ('Elevation was cancelled or failed: {0}' -f $_.Exception.Message) 'ERROR'
        exit $script:ExitElevation
    }

    exit $process.ExitCode
}

function Invoke-Sc {
    <#
        Runs sc.exe with a verbatim argument line and returns its exit code.

        Start-Process hands the argument line to CreateProcess unchanged, which
        avoids PowerShell re-quoting values such as binPath= "C:\Program Files\...".
    #>
    param(
        [Parameter(Mandatory)][string] $ArgumentLine,
        [Parameter(Mandatory)][string] $Description,
        [Parameter(Mandatory)][string] $TargetServiceName
    )

    $process = Start-Process -FilePath 'sc.exe' -ArgumentList $ArgumentLine -NoNewWindow -Wait -PassThru
    if ($process.ExitCode -ne 0) {
        throw ('sc.exe {0} failed for "{1}" with exit code {2}.' -f $Description, $TargetServiceName, $process.ExitCode)
    }

    return $process.ExitCode
}

function Get-BootWakeMailerService {
    return Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
}

function Stop-BootWakeMailerService {
    param([Parameter(Mandatory)] $Service)

    if ($Service.Status -eq [System.ServiceProcess.ServiceControllerStatus]::Stopped) {
        Write-Log ('Service "{0}" is already stopped.' -f $ServiceName)
        return
    }

    Write-Log ('Stopping service "{0}" (current state: {1}).' -f $ServiceName, $Service.Status)
    Stop-Service -Name $ServiceName -Force -ErrorAction Stop
    (Get-Service -Name $ServiceName).WaitForStatus(
        [System.ServiceProcess.ServiceControllerStatus]::Stopped,
        [TimeSpan]::FromSeconds(30))
    Write-Log ('Service "{0}" stopped.' -f $ServiceName)
}

function Assert-ValidPayload {
    <#
        Validates the publish output before anything is copied. A payload that is
        not self-contained would install an application that cannot start on a
        machine without the .NET runtime, so it is rejected here.
    #>
    param([Parameter(Mandatory)][string] $PayloadRoot)

    $expected = @(
        (Join-Path (Join-Path $PayloadRoot $script:ServiceSubDirectory) $script:ServiceExeName),
        (Join-Path (Join-Path $PayloadRoot $script:ConfigToolSubDirectory) $script:ConfigToolExeName)
    )

    foreach ($file in $expected) {
        if (-not (Test-Path -LiteralPath $file -PathType Leaf)) {
            throw ('The release payload is incomplete: "{0}" is missing. Publish it with: dotnet publish src\BootWakeMailer.Service\BootWakeMailer.Service.csproj -c Release -r win-x64 --self-contained true' -f $file)
        }
    }

    foreach ($sub in @($script:ServiceSubDirectory, $script:ConfigToolSubDirectory)) {
        $marker = Join-Path (Join-Path $PayloadRoot $sub) $script:SelfContainedMarker
        if (-not (Test-Path -LiteralPath $marker -PathType Leaf)) {
            throw ('"{0}" is not a self-contained publish output ("{1}" is missing). The target machine would need the .NET runtime installed.' -f $sub, $script:SelfContainedMarker)
        }
    }
}

function Assert-SafeDirectoryToDelete {
    <#
        Refuses to delete a directory that is clearly not an application
        directory. Uninstall removes whole trees, so a mistyped parameter must not
        be able to erase a system directory.
    #>
    param(
        [Parameter(Mandatory)][string] $Path,
        [Parameter(Mandatory)][string] $ParameterName
    )

    $full = [System.IO.Path]::GetFullPath($Path)

    if (-not [System.IO.Path]::IsPathRooted($full)) {
        throw ('{0} must be an absolute path: "{1}".' -f $ParameterName, $Path)
    }

    # The drive root is spelled with a trailing backslash: GetFullPath('C:')
    # resolves to the current directory on C:, not to C:\.
    $forbidden = @($env:SystemRoot, $env:ProgramFiles, $env:ProgramData, ($env:SystemDrive + '\'), $env:USERPROFILE)
    foreach ($candidate in $forbidden) {
        if ([string]::IsNullOrWhiteSpace($candidate)) { continue }
        if ($full.TrimEnd('\') -ieq ([System.IO.Path]::GetFullPath($candidate)).TrimEnd('\')) {
            throw ('{0} points at a protected directory and was refused: "{1}".' -f $ParameterName, $full)
        }
    }

    # Require at least <root>\<name>, e.g. C:\Program Files\BootWakeMailer.
    $parent = [System.IO.Directory]::GetParent($full.TrimEnd('\'))
    if ($null -eq $parent -or $null -eq $parent.Parent) {
        throw ('{0} is too close to the drive root and was refused: "{1}".' -f $ParameterName, $full)
    }

    return $full
}

# ---------------------------------------------------------------------------
# 1. Administrator privileges
# ---------------------------------------------------------------------------

Write-Step 'BootWakeMailer installation'
Write-Log ('Install directory : {0}' -f $InstallDirectory)
Write-Log ('Data directory    : {0}' -f $DataDirectory)
Write-Log ('Service name      : {0}' -f $ServiceName)

if ($PSBoundParameters.ContainsKey('SourceDirectory')) {
    Write-Log ('Payload source    : {0}' -f $SourceDirectory)
}

# $WhatIfPreference is checked together with the bound switch so that a dry run
# never asks for elevation, whether or not the preference variable is set for
# this script.
$whatIfRequested = [bool]$WhatIfPreference -or [bool]$PSBoundParameters['WhatIf']
$skipElevation = $NoElevate.IsPresent -or $whatIfRequested

if (-not (Test-IsAdministrator)) {
    if ($skipElevation) {
        Write-Log 'Not running as administrator. Continuing because elevation was skipped (this will fail for operations that require administrator rights).' 'WARN'
    }
    elseif ($env:BootWakeMailerInstallElevated -eq '1') {
        Write-Log 'Administrator privileges could not be obtained. Installation aborted.' 'ERROR'
        exit $script:ExitElevation
    }
    else {
        # Built at script scope, where $PSBoundParameters describes this script.
        $forwardable = @(
            'SourceDirectory', 'InstallDirectory', 'DataDirectory',
            'ServiceName', 'ServiceDisplayName', 'SkipServiceRegistration'
        )

        $elevatedArguments = @()
        foreach ($name in $forwardable) {
            if (-not $PSBoundParameters.ContainsKey($name)) { continue }

            $value = $PSBoundParameters[$name]
            if ($value -is [switch]) {
                if ($value.IsPresent) { $elevatedArguments += ('-{0}' -f $name) }
            }
            else {
                # Single-quoted so that the child PowerShell does not expand a
                # "$" or a backtick inside the value.
                $quotedValue = "'{0}'" -f ([string]$value).Replace("'", "''")
                $elevatedArguments += @(('-{0}' -f $name), $quotedValue)
            }
        }

        Invoke-SelfElevation -ForwardedArguments $elevatedArguments
    }
}
else {
    Write-Log 'Running with administrator privileges.'
}

try {
    # -----------------------------------------------------------------------
    # 2. Resolve and validate the payload
    # -----------------------------------------------------------------------

    Write-Step 'Validating the release payload'

    if (-not $PSBoundParameters.ContainsKey('SourceDirectory')) {
        $SourceDirectory = Join-Path -Path $PSScriptRoot -ChildPath 'app'
    }
    $SourceDirectory = [System.IO.Path]::GetFullPath($SourceDirectory)

    if (-not (Test-Path -LiteralPath $SourceDirectory -PathType Container)) {
        throw ('The payload directory "{0}" was not found. Extract the whole release package and run install.cmd from its root.' -f $SourceDirectory)
    }

    Assert-ValidPayload -PayloadRoot $SourceDirectory
    Write-Log ('Payload looks complete and self-contained: {0}' -f $SourceDirectory)

    # -----------------------------------------------------------------------
    # 3. Application data directory
    # -----------------------------------------------------------------------

    Write-Step 'Preparing the application data directory'

    if ($PSCmdlet.ShouldProcess($DataDirectory, 'Create the application data directory')) {
        New-Item -ItemType Directory -Path $DataDirectory -Force | Out-Null
    }
    Write-Log ('Application data directory ready: {0}' -f $DataDirectory)

    # -----------------------------------------------------------------------
    # 4. Service registration
    # -----------------------------------------------------------------------

    $existingService = $null
    if ($SkipServiceRegistration) {
        Write-Log 'Service registration was skipped (-SkipServiceRegistration).' 'WARN'
    }
    else {
        Write-Step 'Preparing the Windows Service'

        $existingService = Get-BootWakeMailerService
        if ($existingService) {
            # Stopping first releases the service executable so its files can be
            # replaced; it also keeps repeated installs from creating a second
            # service registration.
            Write-Log ('Service "{0}" is already registered. It will be updated.' -f $ServiceName)

            if ($PSCmdlet.ShouldProcess($ServiceName, 'Stop the Windows Service')) {
                Stop-BootWakeMailerService -Service $existingService
            }
        }
        else {
            Write-Log ('Service "{0}" is not registered yet.' -f $ServiceName)
        }
    }

    # -----------------------------------------------------------------------
    # 5. Application files
    # -----------------------------------------------------------------------

    Write-Step 'Installing application files'

    # The guard runs before ShouldProcess so that an invalid -InstallDirectory is
    # reported even for a dry run, and never after the removal was decided.
    $previousInstallationExists = Test-Path -LiteralPath $InstallDirectory -PathType Container
    if ($previousInstallationExists) {
        $InstallDirectory = Assert-SafeDirectoryToDelete -Path $InstallDirectory -ParameterName 'InstallDirectory'
    }

    if ($PSCmdlet.ShouldProcess($InstallDirectory, 'Replace the installed application files')) {
        if ($previousInstallationExists) {
            Write-Log ('Removing the previous installation in {0}.' -f $InstallDirectory)
            Remove-Item -LiteralPath $InstallDirectory -Recurse -Force
        }

        New-Item -ItemType Directory -Path $InstallDirectory -Force | Out-Null

        foreach ($sub in @($script:ServiceSubDirectory, $script:ConfigToolSubDirectory)) {
            Copy-Item -LiteralPath (Join-Path $SourceDirectory $sub) `
                      -Destination (Join-Path $InstallDirectory $sub) `
                      -Recurse -Force
        }
    }
    Write-Log ('Application files installed in {0}' -f $InstallDirectory)

    # -----------------------------------------------------------------------
    # 6. Service registration with Automatic startup
    # -----------------------------------------------------------------------

    if ($SkipServiceRegistration) {
        Write-Step 'Skipping service registration and start'
        Write-Log ('Register the service manually with "sc.exe create" if this was not intentional. The service executable is {0}.' -f (Join-Path (Join-Path $InstallDirectory $script:ServiceSubDirectory) $script:ServiceExeName)) 'WARN'
    }
    else {
        Write-Step 'Registering the Windows Service'

        $serviceExePath = Join-Path (Join-Path $InstallDirectory $script:ServiceSubDirectory) $script:ServiceExeName
        if (-not (Test-Path -LiteralPath $serviceExePath -PathType Leaf)) {
            throw ('The service executable was not installed: "{0}".' -f $serviceExePath)
        }

        # sc.exe needs the executable path quoted because it contains a space.
        $quotedExePath = '"{0}"' -f $serviceExePath

        if ($PSCmdlet.ShouldProcess($ServiceName, 'Register the Windows Service with Automatic startup')) {
            if ($existingService) {
                Invoke-Sc -ArgumentLine ('config "{0}" binPath= {1} start= auto' -f $ServiceName, $quotedExePath) -Description 'config' -TargetServiceName $ServiceName
            }
            else {
                Invoke-Sc -ArgumentLine ('create "{0}" binPath= {1} start= auto DisplayName= "{2}"' -f $ServiceName, $quotedExePath, $ServiceDisplayName) -Description 'create' -TargetServiceName $ServiceName
            }

            Invoke-Sc -ArgumentLine ('description "{0}" "{1}"' -f $ServiceName, $script:ServiceDescription) -Description 'description' -TargetServiceName $ServiceName

            # Verified inside the ShouldProcess block so that a dry run does not
            # report a registration it never performed.
            $registered = Get-CimInstance -ClassName Win32_Service -Filter ("Name='{0}'" -f $ServiceName) -ErrorAction SilentlyContinue
            if (-not $registered) {
                Write-Log ('Could not confirm the startup type of "{0}".' -f $ServiceName) 'WARN'
            }
            elseif ($registered.StartMode -ne 'Auto') {
                Write-Log ('Startup type is "{0}" instead of Automatic.' -f $registered.StartMode) 'WARN'
            }
            else {
                Write-Log ('Service "{0}" registered with Automatic startup.' -f $ServiceName)
            }
        }

        # -------------------------------------------------------------------
        # 7. Start the service
        # -------------------------------------------------------------------

        Write-Step 'Starting the Windows Service'

        if ($PSCmdlet.ShouldProcess($ServiceName, 'Start the Windows Service')) {
            Start-Service -Name $ServiceName -ErrorAction Stop

            $running = $false
            for ($attempt = 0; $attempt -lt 20; $attempt++) {
                $service = Get-Service -Name $ServiceName
                if ($service.Status -eq [System.ServiceProcess.ServiceControllerStatus]::Running) {
                    $running = $true
                    break
                }
                if ($service.Status -eq [System.ServiceProcess.ServiceControllerStatus]::Stopped) {
                    break
                }
                Start-Sleep -Milliseconds 500
            }

            if ($running) {
                Write-Log ('Service "{0}" is running.' -f $ServiceName)
            }
            else {
                $state = (Get-Service -Name $ServiceName).Status
                Write-Log ('Service "{0}" was started but is not running (state: {1}). Check the application event log for details.' -f $ServiceName, $state) 'WARN'
            }
        }
    }

    # -----------------------------------------------------------------------
    # 8. Summary
    # -----------------------------------------------------------------------

    Write-Step 'Installation completed'
    Write-Log ('Installed files : {0}' -f $InstallDirectory)
    Write-Log ('Application data: {0}' -f $DataDirectory)

    $configFilePath = Join-Path -Path $DataDirectory -ChildPath 'config.json'
    if (-not (Test-Path -LiteralPath $configFilePath -PathType Leaf)) {
        Write-Log ('No configuration found yet. Run "{0}" to configure the SMTP settings.' -f (Join-Path (Join-Path $InstallDirectory $script:ConfigToolSubDirectory) $script:ConfigToolExeName)) 'WARN'
    }

    exit $script:ExitSuccess
}
catch {
    Write-Host ''
    Write-Log ('Installation failed: {0}' -f $_.Exception.Message) 'ERROR'
    exit $script:ExitFailure
}
