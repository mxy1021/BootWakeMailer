#Requires -Version 5.1
<#
.SYNOPSIS
    Builds the self-contained win-x64 release package for BootWakeMailer.

.DESCRIPTION
    Publishes the Windows Service and the WinForms configuration tool as
    self-contained win-x64 applications for .NET 10 (requirements.md §2,
    architecture.md §13) and assembles the release package:

        BootWakeMailer-win-x64/
        ├─ install.cmd
        ├─ install.ps1
        ├─ uninstall.cmd
        ├─ uninstall.ps1
        ├─ README.txt
        └─ app/
           ├─ service/      (BootWakeMailer.Service.exe, ...)
           └─ configtool/   (BootWakeMailer.ConfigTool.exe, ...)

    The two programs are published into separate directories on purpose. The
    configuration tool (WinForms) resolves System.Security.Cryptography.ProtectedData
    from the WindowsDesktop shared framework, while the service resolves it from
    the NuGet package, so a single merged directory would give one of the two
    programs a dependency set that does not match its .deps.json.

    A self-contained publish always contains the .NET runtime, so the target
    Windows 11 x64 machine needs no separately installed .NET runtime.

.PARAMETER Configuration
    Build configuration. Defaults to Release.

.PARAMETER RuntimeIdentifier
    Target runtime identifier. Defaults to win-x64.

.PARAMETER OutputDirectory
    Package output directory. Defaults to artifacts\publish\BootWakeMailer-win-x64
    below the repository root. An existing directory is replaced.

.PARAMETER Zip
    Also creates BootWakeMailer-win-x64.zip next to the package directory.

.EXAMPLE
    .\publish.ps1
    Builds the release package.

.EXAMPLE
    .\publish.ps1 -Zip
    Builds the release package and compresses it for distribution.

.NOTES
    Equivalent raw commands, run from the repository root:

        dotnet publish src\BootWakeMailer.Service\BootWakeMailer.Service.csproj -c Release -r win-x64 --self-contained true -o artifacts\publish\BootWakeMailer-win-x64\app\service
        dotnet publish src\BootWakeMailer.ConfigTool\BootWakeMailer.ConfigTool.csproj -c Release -r win-x64 --self-contained true -o artifacts\publish\BootWakeMailer-win-x64\app\configtool
#>
[CmdletBinding()]
param(
    [ValidateSet('Release', 'Debug')][string] $Configuration = 'Release',

    [string] $RuntimeIdentifier = 'win-x64',

    [string] $OutputDirectory,

    [switch] $Zip
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$script:ExitSuccess = 0
$script:ExitFailure = 1

$script:PackageName = 'BootWakeMailer-{0}' -f $RuntimeIdentifier
$script:ServiceProject = 'src\BootWakeMailer.Service\BootWakeMailer.Service.csproj'
$script:ConfigToolProject = 'src\BootWakeMailer.ConfigTool\BootWakeMailer.ConfigTool.csproj'
$script:ServiceSubDirectory = 'service'
$script:ConfigToolSubDirectory = 'configtool'
$script:ServiceExeName = 'BootWakeMailer.Service.exe'
$script:ConfigToolExeName = 'BootWakeMailer.ConfigTool.exe'
$script:SelfContainedMarker = 'coreclr.dll'
$script:ScriptFiles = @('install.cmd', 'install.ps1', 'uninstall.cmd', 'uninstall.ps1')

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

function Assert-SafeOutputDirectory {
    <#
        The output directory is removed recursively before it is rebuilt, so a
        mistyped -OutputDirectory must not be able to erase the repository or a
        system directory.
    #>
    param([Parameter(Mandatory)][string] $Path)

    $full = [System.IO.Path]::GetFullPath($Path)

    if (-not [System.IO.Path]::IsPathRooted($full)) {
        throw ('-OutputDirectory must be an absolute path: "{0}".' -f $Path)
    }

    $trimmed = $full.TrimEnd('\')

    # The drive root is spelled with a trailing backslash: GetFullPath('C:')
    # resolves to the current directory on C:, not to C:\.
    $forbidden = @(
        $env:SystemRoot, $env:ProgramFiles, $env:ProgramData,
        ($env:SystemDrive + '\'), $env:USERPROFILE, $repositoryRoot
    )

    foreach ($candidate in $forbidden) {
        if ([string]::IsNullOrWhiteSpace($candidate)) { continue }
        if ($trimmed -ieq ([System.IO.Path]::GetFullPath($candidate)).TrimEnd('\')) {
            throw ('-OutputDirectory points at a protected directory and was refused: "{0}".' -f $full)
        }
    }

    # Require at least <root>\<name>, e.g. C:\build\package.
    $parent = [System.IO.Directory]::GetParent($trimmed)
    if ($null -eq $parent -or $null -eq $parent.Parent) {
        throw ('-OutputDirectory is too close to the drive root and was refused: "{0}".' -f $full)
    }

    return $full
}

function Get-DirectorySize {
    param([Parameter(Mandatory)][string] $Path)

    $bytes = (Get-ChildItem -LiteralPath $Path -Recurse -File | Measure-Object -Property Length -Sum).Sum
    if ($null -eq $bytes) { $bytes = 0 }

    return [math]::Round($bytes / 1MB, 1)
}

function Invoke-DotNetPublish {
    param(
        [Parameter(Mandatory)][string] $ProjectPath,
        [Parameter(Mandatory)][string] $Destination,
        [Parameter(Mandatory)][string] $Description
    )

    Write-Log ('Publishing {0} -> {1}' -f $Description, $Destination)

    $publishArguments = @(
        'publish', $ProjectPath,
        '-c', $Configuration,
        '-r', $RuntimeIdentifier,
        '--self-contained', 'true',
        '-o', $Destination
    )

    & dotnet @publishArguments
    if ($LASTEXITCODE -ne 0) {
        throw ('dotnet publish failed for {0} with exit code {1}.' -f $ProjectPath, $LASTEXITCODE)
    }
}

function Assert-PackagePayload {
    <#
        Fails the build when the package would not be installable, most
        importantly when the runtime is missing from a publish output.
    #>
    param([Parameter(Mandatory)][string] $PackageRoot)

    $expectedFiles = @(
        (Join-Path (Join-Path $PackageRoot $script:ServiceSubDirectory) $script:ServiceExeName),
        (Join-Path (Join-Path $PackageRoot $script:ConfigToolSubDirectory) $script:ConfigToolExeName)
    )

    foreach ($file in $expectedFiles) {
        if (-not (Test-Path -LiteralPath $file -PathType Leaf)) {
            throw ('The package is incomplete: "{0}" is missing.' -f $file)
        }
    }

    foreach ($sub in @($script:ServiceSubDirectory, $script:ConfigToolSubDirectory)) {
        $marker = Join-Path (Join-Path $PackageRoot $sub) $script:SelfContainedMarker
        if (-not (Test-Path -LiteralPath $marker -PathType Leaf)) {
            throw ('"{0}" is not self-contained ("{1}" is missing). The target machine would need the .NET runtime installed.' -f $sub, $script:SelfContainedMarker)
        }
    }

    foreach ($scriptFile in $script:ScriptFiles) {
        $scriptPath = Join-Path $PackageRoot $scriptFile
        if (-not (Test-Path -LiteralPath $scriptPath -PathType Leaf)) {
            throw ('The package is incomplete: "{0}" is missing.' -f $scriptPath)
        }
    }
}

try {
    # -----------------------------------------------------------------------
    # 1. Resolve the repository layout
    # -----------------------------------------------------------------------

    Write-Step 'Preparing the release package'

    $repositoryRoot = Split-Path -Path $PSScriptRoot -Parent
    $solutionPath = Join-Path $repositoryRoot 'BootWakeMailer.sln'
    if (-not (Test-Path -LiteralPath $solutionPath -PathType Leaf)) {
        throw ('BootWakeMailer.sln was not found in "{0}". Run this script from the repository.' -f $repositoryRoot)
    }

    if (-not $PSBoundParameters.ContainsKey('OutputDirectory')) {
        $OutputDirectory = Join-Path -Path (Join-Path -Path (Join-Path -Path $repositoryRoot 'artifacts') 'publish') -ChildPath $script:PackageName
    }
    $OutputDirectory = Assert-SafeOutputDirectory -Path $OutputDirectory

    Write-Log ('Repository      : {0}' -f $repositoryRoot)
    Write-Log ('Configuration   : {0}' -f $Configuration)
    Write-Log ('Runtime         : {0}' -f $RuntimeIdentifier)
    Write-Log ('Package output  : {0}' -f $OutputDirectory)

    if (Test-Path -LiteralPath $OutputDirectory) {
        Write-Log 'Removing the previous package output.'
        Remove-Item -LiteralPath $OutputDirectory -Recurse -Force
    }
    New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null

    # -----------------------------------------------------------------------
    # 2. Publish both programs self-contained
    # -----------------------------------------------------------------------

    Write-Step 'Publishing the applications'

    $appRoot = Join-Path $OutputDirectory 'app'

    Invoke-DotNetPublish `
        -ProjectPath (Join-Path $repositoryRoot $script:ServiceProject) `
        -Destination (Join-Path $appRoot $script:ServiceSubDirectory) `
        -Description 'the Windows Service'

    Invoke-DotNetPublish `
        -ProjectPath (Join-Path $repositoryRoot $script:ConfigToolProject) `
        -Destination (Join-Path $appRoot $script:ConfigToolSubDirectory) `
        -Description 'the configuration tool'

    # -----------------------------------------------------------------------
    # 3. Add the install/uninstall entry points
    # -----------------------------------------------------------------------

    Write-Step 'Adding the installation scripts'

    foreach ($scriptFile in $script:ScriptFiles) {
        $source = Join-Path $PSScriptRoot $scriptFile
        if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
            throw ('The installation script "{0}" was not found in "{1}".' -f $scriptFile, $PSScriptRoot)
        }

        Copy-Item -LiteralPath $source -Destination (Join-Path $OutputDirectory $scriptFile) -Force
    }

    $readmePath = Join-Path $OutputDirectory 'README.txt'
    $readmeLines = @(
        'BootWakeMailer',
        '==============',
        '',
        'Requires Windows 11 x64 and administrator privileges to install.',
        'The .NET runtime does not have to be installed: the applications are',
        'self-contained.',
        '',
        'Install',
        '-------',
        '  1. Extract this package to a local folder.',
        '  2. Double-click install.cmd and confirm the UAC prompt.',
        '',
        'Uninstall',
        '---------',
        '  Double-click uninstall.cmd and confirm the UAC prompt.',
        '  The service, the installed files and the application data under',
        '  %ProgramData%\BootWakeMailer (configuration, queue, status) are removed.',
        '  Application data is not preserved.',
        '',
        'Configure',
        '---------',
        '  Run %ProgramFiles%\BootWakeMailer\configtool\BootWakeMailer.ConfigTool.exe',
        '  to edit the SMTP settings. The service reads the configuration before',
        '  every send attempt, so no service restart is needed after saving.',
        ''
    )
    Set-Content -LiteralPath $readmePath -Value $readmeLines -Encoding ASCII

    # -----------------------------------------------------------------------
    # 4. Verify and report
    # -----------------------------------------------------------------------

    Write-Step 'Verifying the package'

    Assert-PackagePayload -PackageRoot $OutputDirectory
    Write-Log 'Every expected file is present and both applications are self-contained.'

    $serviceSize = Get-DirectorySize -Path (Join-Path $appRoot $script:ServiceSubDirectory)
    $configToolSize = Get-DirectorySize -Path (Join-Path $appRoot $script:ConfigToolSubDirectory)
    Write-Log ('Service payload         : {0} MB' -f $serviceSize)
    Write-Log ('Configuration tool      : {0} MB' -f $configToolSize)

    if ($Zip) {
        Write-Step 'Compressing the package'

        $zipPath = '{0}.zip' -f $OutputDirectory
        if (Test-Path -LiteralPath $zipPath) {
            Remove-Item -LiteralPath $zipPath -Force
        }

        Add-Type -AssemblyName System.IO.Compression.FileSystem
        [System.IO.Compression.ZipFile]::CreateFromDirectory(
            $OutputDirectory,
            $zipPath,
            [System.IO.Compression.CompressionLevel]::Fastest,
            $false)

        Write-Log ('Archive                 : {0} ({1} MB)' -f $zipPath, [math]::Round((Get-Item -LiteralPath $zipPath).Length / 1MB, 1))
    }

    Write-Step 'Release package ready'
    Write-Log ('Package : {0}' -f $OutputDirectory)
    Write-Log 'Install : double-click install.cmd in the package'
    Write-Log 'Uninstall: double-click uninstall.cmd in the package'

    exit $script:ExitSuccess
}
catch {
    Write-Host ''
    Write-Log ('Publishing failed: {0}' -f $_.Exception.Message) 'ERROR'
    exit $script:ExitFailure
}
