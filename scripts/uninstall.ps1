#Requires -Version 5.1
<#
.SYNOPSIS
    Uninstalls BootWakeMailer.

.DESCRIPTION
    Stops and removes the BootWakeMailer Windows Service, deletes
    %ProgramFiles%\BootWakeMailer and deletes %ProgramData%\BootWakeMailer,
    including config.json, queue.json and status.json (requirements.md FR-10,
    architecture.md §13).

    The script is idempotent: a service, directory or file that is already gone is
    reported and skipped. Application data is not preserved.

    Normally started through uninstall.cmd, which handles the UAC prompt. Running
    this script directly also works: it re-launches itself elevated when needed.

.PARAMETER InstallDirectory
    Installation directory to remove. Defaults to %ProgramFiles%\BootWakeMailer.

.PARAMETER DataDirectory
    Application data directory to remove. Defaults to %ProgramData%\BootWakeMailer.

.PARAMETER ServiceName
    Windows Service name. Defaults to BootWakeMailer.

.PARAMETER NoElevate
    Fails instead of re-launching elevated. Intended for automated tests that run
    without administrator privileges.

.PARAMETER SkipServiceRegistration
    Does not stop or remove the Windows Service and only deletes the directories.
    Intended for automated tests, which must not modify the real service.

.EXAMPLE
    .\uninstall.ps1
    Standard uninstallation.

.EXAMPLE
    .\uninstall.ps1 -WhatIf
    Shows what would be removed without touching the machine.
#>
[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'Medium')]
param(
    [string] $InstallDirectory = (Join-Path -Path $env:ProgramFiles -ChildPath 'BootWakeMailer'),

    [string] $DataDirectory = (Join-Path -Path $env:ProgramData -ChildPath 'BootWakeMailer'),

    [string] $ServiceName = 'BootWakeMailer',

    [switch] $NoElevate,

    [switch] $SkipServiceRegistration
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

# Exit codes (uninstall.cmd forwards these to the caller).
$script:ExitSuccess = 0
$script:ExitFailure = 1
$script:ExitElevation = 2

# A file in the installation can stay open for a moment after the service was stopped: a
# scanner, an editor, or a shell whose working directory is inside the folder. Removing
# part of the tree and then failing would leave the application half removed, so the
# removal is retried briefly before it is reported as failed.
$script:RemoveAttempts = 10
$script:RemoveRetryDelayMilliseconds = 400

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
        throw 'Elevation requires the script to run from a file (use uninstall.ps1, not a piped command).'
    }

    $arguments = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', ('"{0}"' -f $PSCommandPath)) + $ForwardedArguments

    Write-Log 'Administrator privileges are required. Requesting elevation (UAC prompt).'

    # Inherited by the elevated child, which uses this marker to abort instead of
    # asking for elevation a second time.
    $env:BootWakeMailerUninstallElevated = '1'

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

function Remove-BootWakeMailerServiceRegistration {
    if (-not (Get-BootWakeMailerService)) {
        Write-Log ('Service "{0}" is not registered.' -f $ServiceName)
        return
    }

    Write-Log ('Removing service registration "{0}".' -f $ServiceName)
    Invoke-Sc -ArgumentLine ('delete "{0}"' -f $ServiceName) -Description 'delete' -TargetServiceName $ServiceName

    # A stopped service is deleted immediately, but the service manager keeps
    # reporting the registration as "marked for deletion" for a short moment.
    for ($attempt = 0; $attempt -lt 15; $attempt++) {
        if (-not (Get-BootWakeMailerService)) { return }
        Start-Sleep -Milliseconds 400
    }

    throw ('Service "{0}" is still registered after deletion was requested.' -f $ServiceName)
}

function Remove-DirectoryIfPresent {
    param(
        [Parameter(Mandatory)][string] $Path,
        [Parameter(Mandatory)][string] $ParameterName,
        [Parameter(Mandatory)][string] $Description,
        [Parameter(Mandatory)] $Cmdlet
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Container)) {
        Write-Log ('{0} is not present: {1}' -f $Description, $Path)
        return
    }

    $safePath = Assert-SafeDirectoryToDelete -Path $Path -ParameterName $ParameterName

    if ($Cmdlet.ShouldProcess($safePath, ('Remove {0}' -f $Description))) {
        Remove-DirectoryWithRetry -Path $safePath
        Write-Log ('Removed {0}: {1}' -f $Description, $safePath)
    }
}

function Remove-DirectoryWithRetry {
    <#
        Removes a directory tree, retrying briefly while a file inside it is still open.

        Remove-Item aborts at the first file it cannot delete, which can leave the tree
        partially removed. The retry covers the transient case; a directory that stays
        locked still fails after the attempts are used up, with the same error the caller
        would otherwise have seen.
    #>
    param([Parameter(Mandatory)][string] $Path)

    for ($attempt = 1; ; $attempt++) {
        try {
            Remove-Item -LiteralPath $Path -Recurse -Force -ErrorAction Stop
            return
        }
        catch [System.IO.IOException], [System.UnauthorizedAccessException] {
            if ($attempt -ge $script:RemoveAttempts) {
                throw
            }

            Write-Log ('"{0}" is still in use; retrying the removal ({1} of {2}).' -f $Path, $attempt, $script:RemoveAttempts) 'WARN'
            Start-Sleep -Milliseconds $script:RemoveRetryDelayMilliseconds
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

Write-Step 'BootWakeMailer uninstallation'
Write-Log ('Install directory : {0}' -f $InstallDirectory)
Write-Log ('Data directory    : {0}' -f $DataDirectory)
Write-Log ('Service name      : {0}' -f $ServiceName)

# $WhatIfPreference is checked together with the bound switch so that a dry run
# never asks for elevation, whether or not the preference variable is set for
# this script.
$whatIfRequested = [bool]$WhatIfPreference -or [bool]$PSBoundParameters['WhatIf']
$skipElevation = $NoElevate.IsPresent -or $whatIfRequested

if (-not (Test-IsAdministrator)) {
    if ($skipElevation) {
        Write-Log 'Not running as administrator. Continuing because elevation was skipped (this will fail for operations that require administrator rights).' 'WARN'
    }
    elseif ($env:BootWakeMailerUninstallElevated -eq '1') {
        Write-Log 'Administrator privileges could not be obtained. Uninstallation aborted.' 'ERROR'
        exit $script:ExitElevation
    }
    else {
        # Built at script scope, where $PSBoundParameters describes this script.
        $forwardable = @('InstallDirectory', 'DataDirectory', 'ServiceName', 'SkipServiceRegistration')

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
    # 2. Service registration
    # -----------------------------------------------------------------------

    if ($SkipServiceRegistration) {
        Write-Step 'Skipping the Windows Service'
        Write-Log ('Service "{0}" was not touched (-SkipServiceRegistration).' -f $ServiceName) 'WARN'
    }
    else {
        Write-Step 'Removing the Windows Service'

        $service = Get-BootWakeMailerService
        if ($service) {
            if ($PSCmdlet.ShouldProcess($ServiceName, 'Stop the Windows Service')) {
                Stop-BootWakeMailerService -Service $service
            }

            if ($PSCmdlet.ShouldProcess($ServiceName, 'Remove the Windows Service registration')) {
                Remove-BootWakeMailerServiceRegistration
            }
        }
        else {
            Write-Log ('Service "{0}" is not registered.' -f $ServiceName)
        }
    }

    # -----------------------------------------------------------------------
    # 3. Installed application files and application data
    # -----------------------------------------------------------------------

    Write-Step 'Removing installed files'

    Remove-DirectoryIfPresent -Path $InstallDirectory -ParameterName 'InstallDirectory' -Description 'installation directory' -Cmdlet $PSCmdlet

    Write-Step 'Removing application data'

    Remove-DirectoryIfPresent -Path $DataDirectory -ParameterName 'DataDirectory' -Description 'application data directory' -Cmdlet $PSCmdlet

    # -----------------------------------------------------------------------
    # 4. Summary
    # -----------------------------------------------------------------------

    Write-Step 'Uninstallation completed'

    if (Get-BootWakeMailerService) {
        Write-Log ('Service "{0}" is still registered.' -f $ServiceName) 'WARN'
    }

    exit $script:ExitSuccess
}
catch {
    Write-Host ''
    Write-Log ('Uninstallation failed: {0}' -f $_.Exception.Message) 'ERROR'
    exit $script:ExitFailure
}
