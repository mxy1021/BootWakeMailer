#Requires -Version 5.1
<#
.SYNOPSIS
    Tests the BootWakeMailer installation scripts.

.DESCRIPTION
    Runs the install/uninstall scripts against temporary directories so that the
    file handling, parameter validation, exit codes, path escaping and safety
    guards can be checked without administrator privileges and without touching
    the real installation or the real Windows Service.

    Every test overrides -ServiceName with a unique name, and every service
    related test passes -SkipServiceRegistration, so the real BootWakeMailer
    service is never started, stopped, changed or removed.

    What this suite cannot cover is everything that needs a real Windows Service
    registration and a real Program Files installation; see the manual
    verification list in the report. Those steps are only exercised by
    -WhatIf style dry runs here.

    The suite has no dependency on Pester.

.PARAMETER PackageDirectory
    Optional. A publish output produced by scripts\publish.ps1. When given, the
    package layout (entry points, app\service, app\configtool, README.txt) is
    verified as well.

.EXAMPLE
    .\Test-InstallScripts.ps1
    Runs the suite against the scripts in the repository.

.EXAMPLE
    .\Test-InstallScripts.ps1 -PackageDirectory ..\..\artifacts\publish\BootWakeMailer-win-x64
    Runs the suite and additionally validates a built release package.
#>
[CmdletBinding()]
param(
    [string] $PackageDirectory,

    [switch] $KeepTestDirectories
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$script:RepositoryRoot = Split-Path -Path (Split-Path -Path $PSScriptRoot -Parent) -Parent
$script:ScriptsDirectory = Join-Path -Path $script:RepositoryRoot -ChildPath 'scripts'
$script:InstallScript = Join-Path -Path $script:ScriptsDirectory -ChildPath 'install.ps1'
$script:UninstallScript = Join-Path -Path $script:ScriptsDirectory -ChildPath 'uninstall.ps1'
$script:PublishScript = Join-Path -Path $script:ScriptsDirectory -ChildPath 'publish.ps1'
$script:InstallCommand = Join-Path -Path $script:ScriptsDirectory -ChildPath 'install.cmd'
$script:UninstallCommand = Join-Path -Path $script:ScriptsDirectory -ChildPath 'uninstall.cmd'

# Deliberately contains spaces so that every test also covers path escaping.
$script:TestRoot = Join-Path -Path ([System.IO.Path]::GetTempPath()) -ChildPath ('BootWakeMailer Install Scripts Tests {0}' -f ([Guid]::NewGuid().ToString('N')))

$script:PassedCount = 0
$script:FailedCount = 0
$script:Failures = New-Object System.Collections.ArrayList

function Write-Log {
    param(
        [Parameter(Mandatory)][string] $Message,
        [ValidateSet('INFO', 'WARN', 'ERROR', 'PASS', 'FAIL')][string] $Level = 'INFO'
    )

    $color = switch ($Level) {
        'PASS' { 'Green' }
        'FAIL' { 'Red' }
        'ERROR' { 'Red' }
        'WARN' { 'Yellow' }
        default { 'Gray' }
    }

    Write-Host ('[{0}] {1}' -f $Level, $Message) -ForegroundColor $color
}

function Write-Step {
    param([Parameter(Mandatory)][string] $Message)

    Write-Host ''
    Write-Host ('==> {0}' -f $Message) -ForegroundColor Cyan
}

function Assert-True {
    param(
        [Parameter(Mandatory)] $Condition,
        [Parameter(Mandatory)][string] $Message
    )

    if (-not $Condition) {
        throw $Message
    }
}

function Assert-FileExists {
    param([Parameter(Mandatory)][string] $Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw ('Expected file does not exist: "{0}".' -f $Path)
    }
}

function Assert-PathMissing {
    param([Parameter(Mandatory)][string] $Path)

    if (Test-Path -LiteralPath $Path) {
        throw ('Expected path to be absent, but it exists: "{0}".' -f $Path)
    }
}

function New-TestDirectory {
    param([Parameter(Mandatory)][string] $Name)

    $path = Join-Path -Path $script:TestRoot -ChildPath $Name
    New-Item -ItemType Directory -Path $path -Force | Out-Null

    return $path
}

function New-FakePayload {
    <#
        Creates a minimal directory tree that looks like a self-contained publish
        output, so the tests stay fast and do not depend on a real publish.
    #>
    param(
        [Parameter(Mandatory)][string] $PayloadRoot,
        [switch] $WithoutRuntime,
        [switch] $WithoutConfigTool
    )

    $serviceDirectory = Join-Path -Path $PayloadRoot -ChildPath 'service'
    New-Item -ItemType Directory -Path $serviceDirectory -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $serviceDirectory 'BootWakeMailer.Service.exe') -Value 'stub'
    Set-Content -LiteralPath (Join-Path $serviceDirectory 'BootWakeMailer.Shared.dll') -Value 'stub'
    if (-not $WithoutRuntime) {
        Set-Content -LiteralPath (Join-Path $serviceDirectory 'coreclr.dll') -Value 'stub'
    }

    if (-not $WithoutConfigTool) {
        $configToolDirectory = Join-Path -Path $PayloadRoot -ChildPath 'configtool'
        New-Item -ItemType Directory -Path $configToolDirectory -Force | Out-Null
        Set-Content -LiteralPath (Join-Path $configToolDirectory 'BootWakeMailer.ConfigTool.exe') -Value 'stub'
        if (-not $WithoutRuntime) {
            Set-Content -LiteralPath (Join-Path $configToolDirectory 'coreclr.dll') -Value 'stub'
        }
    }

    return $PayloadRoot
}

function Invoke-Script {
    <#
        Runs a script in a child PowerShell process, the same way the .cmd entry
        points do, and returns its exit code plus captured output.
    #>
    param(
        [Parameter(Mandatory)][string] $ScriptPath,
        [string[]] $Arguments = @()
    )

    $hostExecutable = (Get-Process -Id $PID).Path

    # Redirecting a native command's stderr while $ErrorActionPreference is Stop
    # turns the child's error output into a terminating error in this process.
    # The child's exit code and output are what the tests assert on, so errors
    # must stay non-terminating here.
    $previousErrorActionPreference = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $output = & $hostExecutable -NoProfile -ExecutionPolicy Bypass -File $ScriptPath @Arguments 2>&1 | Out-String
        $exitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousErrorActionPreference
    }

    return [pscustomobject]@{
        ExitCode = $exitCode
        Output   = $output
    }
}

function Invoke-TestCase {
    param(
        [Parameter(Mandatory)][string] $Name,
        [Parameter(Mandatory)][scriptblock] $Body
    )

    try {
        & $Body
        $script:PassedCount++
        Write-Log ('PASS  {0}' -f $Name) 'PASS'
    }
    catch {
        $script:FailedCount++
        $null = $script:Failures.Add(('{0}: {1}' -f $Name, $_.Exception.Message))
        Write-Log ('FAIL  {0}' -f $Name) 'FAIL'
        Write-Log ('      {0}' -f $_.Exception.Message) 'FAIL'
    }
}

# ---------------------------------------------------------------------------
# Static checks: syntax, line endings, entry point wiring
# ---------------------------------------------------------------------------

function Test-ScriptSyntax {
    Write-Step 'Script syntax'

    foreach ($scriptPath in @($script:InstallScript, $script:UninstallScript, $script:PublishScript)) {
        Invoke-TestCase -Name ('parses without errors: {0}' -f (Split-Path -Path $scriptPath -Leaf)) -Body {
            Assert-True (Test-Path -LiteralPath $scriptPath -PathType Leaf) ('Script not found: "{0}".' -f $scriptPath)

            $tokens = $null
            $errors = $null
            $null = [System.Management.Automation.Language.Parser]::ParseFile($scriptPath, [ref]$tokens, [ref]$errors)

            if ($errors.Count -gt 0) {
                throw ('{0} has {1} parse error(s), first: {2}' -f (Split-Path -Path $scriptPath -Leaf), $errors.Count, $errors[0].Message)
            }
        }
    }
}

function Test-CommandLineEndings {
    Write-Step 'Entry point line endings'

    # cmd.exe mis-parses "goto :label" in a file that uses bare LF endings, so
    # the .cmd entry points must use CRLF.
    foreach ($command in @($script:InstallCommand, $script:UninstallCommand)) {
        Invoke-TestCase -Name ('uses CRLF line endings: {0}' -f (Split-Path -Path $command -Leaf)) -Body {
            Assert-True (Test-Path -LiteralPath $command -PathType Leaf) ('Entry point not found: "{0}".' -f $command)

            $bytes = [System.IO.File]::ReadAllBytes($command)
            for ($index = 0; $index -lt $bytes.Length; $index++) {
                if ($bytes[$index] -ne 10) { continue }
                if ($index -eq 0 -or $bytes[$index - 1] -ne 13) {
                    throw ('{0} contains a bare LF at byte offset {1}; cmd.exe needs CRLF.' -f (Split-Path -Path $command -Leaf), $index)
                }
            }
        }
    }
}

function Test-EntryPointWiring {
    Write-Step 'Entry point wiring'

    $cases = @(
        [pscustomobject]@{ Command = $script:InstallCommand; Target = 'install.ps1'; Opposite = 'uninstall.ps1' },
        [pscustomobject]@{ Command = $script:UninstallCommand; Target = 'uninstall.ps1'; Opposite = 'install.ps1' }
    )

    foreach ($case in $cases) {
        $leaf = Split-Path -Path $case.Command -Leaf

        # The wiring is checked on the exact assignment line. A plain substring
        # search would be wrong here because "install.ps1" is contained in
        # "uninstall.ps1".
        $ownAssignment = 'set "BWM_PS1=%BWM_DIR%{0}"' -f $case.Target
        $foreignAssignment = 'set "BWM_PS1=%BWM_DIR%{0}"' -f $case.Opposite

        Invoke-TestCase -Name ('{0} runs {1} from its own directory' -f $leaf, $case.Target) -Body {
            $content = Get-Content -LiteralPath $case.Command -Raw

            Assert-True ($content -like ('*{0}*' -f $ownAssignment)) ('{0} does not run {1} from its own directory.' -f $leaf, $case.Target)
            Assert-True ($content -notlike ('*{0}*' -f $foreignAssignment)) ('{0} is wired to the wrong script ({1}).' -f $leaf, $case.Opposite)
            Assert-True ($content -like '*%~dp0*') ('{0} does not resolve its own directory with %~dp0.' -f $leaf)
        }

        Invoke-TestCase -Name ('{0} checks for and requests administrator privileges' -f $leaf) -Body {
            $content = Get-Content -LiteralPath $case.Command -Raw

            Assert-True ($content -like '*net session*') ('{0} has no administrator check.' -f $leaf)
            Assert-True ($content -like '*-Verb RunAs*') ('{0} does not request elevation.' -f $leaf)
            Assert-True ($content -like '*BWM_ELEVATED_CHILD*') ('{0} has no guard against an elevation loop.' -f $leaf)
            Assert-True ($content -like '*%ERRORLEVEL%*') ('{0} does not forward the PowerShell exit code.' -f $leaf)
        }

        Invoke-TestCase -Name ('{0} forwards its arguments to the elevated instance' -f $leaf) -Body {
            $content = Get-Content -LiteralPath $case.Command -Raw

            # Without this the elevated child starts with an empty %*, and
            # "<command> -WhatIf" would perform a real install or uninstall.
            Assert-True ($content -like '*set "BWM_ARGS=%*"*') ('{0} does not capture its arguments before elevating.' -f $leaf)
            Assert-True ($content -like '*-ArgumentList*') ('{0} does not pass the captured arguments to the elevated instance.' -f $leaf)
            Assert-True ($content -like '*$env:BWM_ARGS*') ('{0} does not forward BWM_ARGS to the elevation command.' -f $leaf)
        }

        Invoke-TestCase -Name ('{0} does not hard-code installation paths' -f $leaf) -Body {
            $content = Get-Content -LiteralPath $case.Command -Raw

            Assert-True ($content -notlike '*Program Files*') ('{0} hard-codes the installation directory; it belongs in install.ps1.' -f $leaf)
            Assert-True ($content -notlike '*ProgramData*') ('{0} hard-codes the data directory; it belongs in install.ps1.' -f $leaf)
        }
    }
}

# ---------------------------------------------------------------------------
# install.ps1 / uninstall.ps1 behaviour
# ---------------------------------------------------------------------------

function Test-DryRun {
    Write-Step 'Dry run'

    Invoke-TestCase -Name 'install.ps1 -WhatIf changes nothing' -Body {
        $payload = New-FakePayload -PayloadRoot (New-TestDirectory -Name 'dryrun payload')
        $installDirectory = Join-Path -Path $script:TestRoot -ChildPath 'dryrun install\BootWakeMailer'
        $dataDirectory = Join-Path -Path $script:TestRoot -ChildPath 'dryrun data\BootWakeMailer'

        $result = Invoke-Script -ScriptPath $script:InstallScript -Arguments @(
            '-SourceDirectory', $payload,
            '-InstallDirectory', $installDirectory,
            '-DataDirectory', $dataDirectory,
            '-ServiceName', 'BootWakeMailerScriptTest-DryRun',
            '-SkipServiceRegistration',
            '-NoElevate',
            '-WhatIf'
        )

        Assert-True ($result.ExitCode -eq 0) ('Expected exit code 0, got {0}. Output: {1}' -f $result.ExitCode, $result.Output)
        # The dry run must name the directory it would install to. The wording of
        # the "What if" line is localized, so only the target path is checked.
        Assert-True ($result.Output -like ('*{0}*' -f $installDirectory)) ('The dry run did not report the install directory. Output: {0}' -f $result.Output)
        Assert-PathMissing $installDirectory
        Assert-PathMissing $dataDirectory
    }
}

function Test-Install {
    Write-Step 'Installation'

    $payload = New-FakePayload -PayloadRoot (New-TestDirectory -Name 'install payload')
    $installDirectory = Join-Path -Path $script:TestRoot -ChildPath 'install target\BootWakeMailer'
    $dataDirectory = Join-Path -Path $script:TestRoot -ChildPath 'install data\BootWakeMailer'
    $serviceName = 'BootWakeMailerScriptTest-Install'

    Invoke-TestCase -Name 'install.ps1 copies the payload into a path containing spaces' -Body {
        $result = Invoke-Script -ScriptPath $script:InstallScript -Arguments @(
            '-SourceDirectory', $payload,
            '-InstallDirectory', $installDirectory,
            '-DataDirectory', $dataDirectory,
            '-ServiceName', $serviceName,
            '-SkipServiceRegistration',
            '-NoElevate'
        )

        Assert-True ($result.ExitCode -eq 0) ('Expected exit code 0, got {0}. Output: {1}' -f $result.ExitCode, $result.Output)
        Assert-FileExists (Join-Path $installDirectory 'service\BootWakeMailer.Service.exe')
        Assert-FileExists (Join-Path $installDirectory 'service\coreclr.dll')
        Assert-FileExists (Join-Path $installDirectory 'configtool\BootWakeMailer.ConfigTool.exe')
        Assert-FileExists (Join-Path $installDirectory 'configtool\coreclr.dll')
        Assert-True (Test-Path -LiteralPath $dataDirectory -PathType Container) ('The data directory was not created: "{0}".' -f $dataDirectory)
    }

    Invoke-TestCase -Name 'install.ps1 replaces a previous installation exactly' -Body {
        # A file from an older release must not survive a repeated install, and
        # repeating the install must not fail or duplicate anything.
        $staleFilePath = Join-Path $installDirectory 'service\stale-from-older-version.dll'
        Set-Content -LiteralPath $staleFilePath -Value 'stale'

        $result = Invoke-Script -ScriptPath $script:InstallScript -Arguments @(
            '-SourceDirectory', $payload,
            '-InstallDirectory', $installDirectory,
            '-DataDirectory', $dataDirectory,
            '-ServiceName', $serviceName,
            '-SkipServiceRegistration',
            '-NoElevate'
        )

        Assert-True ($result.ExitCode -eq 0) ('Expected exit code 0 on the second run, got {0}. Output: {1}' -f $result.ExitCode, $result.Output)
        Assert-PathMissing $staleFilePath
        Assert-FileExists (Join-Path $installDirectory 'service\BootWakeMailer.Service.exe')
    }

    Invoke-TestCase -Name 'install.ps1 retries while a file in the previous installation is still open' -Body {
        # A scanner, an editor or a shell whose working directory is inside the folder can
        # hold a file open while the installation is replaced. Without the retry the removal
        # aborts part way through, which breaks the installation that was working.
        $lockedFile = Join-Path $installDirectory 'service\BootWakeMailer.Service.exe'
        Assert-FileExists $lockedFile

        # The path travels in an environment variable, so the helper needs no quoting of its
        # own and the lock is taken before the install below starts. The command is written
        # with single quotes: Start-Process passes the argument list through the Windows
        # command line, which consumes double quotes and would corrupt the call.
        $env:BWM_TEST_LOCKED_FILE = $lockedFile
        $locker = Start-Process -FilePath (Get-Process -Id $PID).Path -PassThru -WindowStyle Hidden -ArgumentList @(
            '-NoProfile', '-Command',
            '$s = [System.IO.File]::Open($env:BWM_TEST_LOCKED_FILE, ''Open'', ''Read'', ''None''); Start-Sleep -Seconds 3; $s.Dispose()'
        )

        try {
            $deadline = (Get-Date).AddSeconds(30)
            while ($true) {
                $probe = $null
                try {
                    $probe = [System.IO.File]::Open($lockedFile, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::None)
                }
                catch {
                    break
                }
                finally {
                    if ($null -ne $probe) { $probe.Dispose() }
                }

                if ((Get-Date) -gt $deadline) { throw 'The helper process never locked the file.' }
                Start-Sleep -Milliseconds 50
            }

            $result = Invoke-Script -ScriptPath $script:InstallScript -Arguments @(
                '-SourceDirectory', $payload,
                '-InstallDirectory', $installDirectory,
                '-DataDirectory', $dataDirectory,
                '-ServiceName', $serviceName,
                '-SkipServiceRegistration',
                '-NoElevate'
            )
        }
        finally {
            Remove-Item Env:\BWM_TEST_LOCKED_FILE -ErrorAction SilentlyContinue
            Wait-Process -Id $locker.Id -Timeout 30 -ErrorAction SilentlyContinue
        }

        Assert-True ($result.ExitCode -eq 0) ('Expected exit code 0 while a file was briefly locked, got {0}. Output: {1}' -f $result.ExitCode, $result.Output)
        Assert-True ($result.Output -like '*still in use*') ('The removal was not retried. Output: {0}' -f $result.Output)
        Assert-FileExists (Join-Path $installDirectory 'service\BootWakeMailer.Service.exe')
        Assert-FileExists (Join-Path $installDirectory 'configtool\BootWakeMailer.ConfigTool.exe')
    }

    Invoke-TestCase -Name 'install.ps1 rejects a payload that is not self-contained' -Body {
        $incompletePayload = New-FakePayload -PayloadRoot (New-TestDirectory -Name 'payload without runtime') -WithoutRuntime
        $target = Join-Path -Path $script:TestRoot -ChildPath 'never installed\BootWakeMailer'

        $result = Invoke-Script -ScriptPath $script:InstallScript -Arguments @(
            '-SourceDirectory', $incompletePayload,
            '-InstallDirectory', $target,
            '-DataDirectory', (Join-Path $script:TestRoot 'never installed data\BootWakeMailer'),
            '-ServiceName', $serviceName,
            '-SkipServiceRegistration',
            '-NoElevate'
        )

        Assert-True ($result.ExitCode -eq 1) ('Expected exit code 1, got {0}. Output: {1}' -f $result.ExitCode, $result.Output)
        Assert-True ($result.Output -like '*self-contained*') ('The error message does not explain the missing runtime. Output: {0}' -f $result.Output)
        Assert-PathMissing $target
    }

    Invoke-TestCase -Name 'install.ps1 rejects a missing payload directory' -Body {
        $result = Invoke-Script -ScriptPath $script:InstallScript -Arguments @(
            '-SourceDirectory', (Join-Path $script:TestRoot 'does not exist'),
            '-InstallDirectory', (Join-Path $script:TestRoot 'never installed 2\BootWakeMailer'),
            '-DataDirectory', (Join-Path $script:TestRoot 'never installed data 2\BootWakeMailer'),
            '-ServiceName', $serviceName,
            '-SkipServiceRegistration',
            '-NoElevate'
        )

        Assert-True ($result.ExitCode -eq 1) ('Expected exit code 1, got {0}. Output: {1}' -f $result.ExitCode, $result.Output)
    }

    Invoke-TestCase -Name 'install.ps1 refuses a protected -InstallDirectory' -Body {
        # -WhatIf guarantees that nothing is removed even if the guard regressed.
        $result = Invoke-Script -ScriptPath $script:InstallScript -Arguments @(
            '-SourceDirectory', $payload,
            '-InstallDirectory', $env:ProgramFiles,
            '-DataDirectory', (Join-Path $script:TestRoot 'protected target data\BootWakeMailer'),
            '-ServiceName', $serviceName,
            '-SkipServiceRegistration',
            '-NoElevate',
            '-WhatIf'
        )

        Assert-True ($result.ExitCode -eq 1) ('Expected exit code 1, got {0}. Output: {1}' -f $result.ExitCode, $result.Output)
        Assert-True ($result.Output -like '*protected directory*') ('The guard message is missing. Output: {0}' -f $result.Output)
        Assert-True (Test-Path -LiteralPath $env:ProgramFiles -PathType Container) 'Program Files is gone, which must never happen.'
    }
}

function Test-Uninstall {
    Write-Step 'Uninstallation'

    $serviceName = 'BootWakeMailerScriptTest-Uninstall'
    $installDirectory = Join-Path -Path $script:TestRoot -ChildPath 'install target\BootWakeMailer'
    $dataDirectory = Join-Path -Path $script:TestRoot -ChildPath 'install data\BootWakeMailer'

    Invoke-TestCase -Name 'uninstall.ps1 removes the application files and the application data' -Body {
        Assert-True (Test-Path -LiteralPath $installDirectory -PathType Container) 'The installation from the previous test is missing.'
        Set-Content -LiteralPath (Join-Path $dataDirectory 'config.json') -Value '{}'
        Set-Content -LiteralPath (Join-Path $dataDirectory 'queue.json') -Value '{}'
        Set-Content -LiteralPath (Join-Path $dataDirectory 'status.json') -Value '{}'

        $result = Invoke-Script -ScriptPath $script:UninstallScript -Arguments @(
            '-InstallDirectory', $installDirectory,
            '-DataDirectory', $dataDirectory,
            '-ServiceName', $serviceName,
            '-SkipServiceRegistration',
            '-NoElevate'
        )

        Assert-True ($result.ExitCode -eq 0) ('Expected exit code 0, got {0}. Output: {1}' -f $result.ExitCode, $result.Output)
        Assert-PathMissing $installDirectory
        Assert-PathMissing $dataDirectory
    }

    Invoke-TestCase -Name 'uninstall.ps1 is idempotent' -Body {
        $result = Invoke-Script -ScriptPath $script:UninstallScript -Arguments @(
            '-InstallDirectory', $installDirectory,
            '-DataDirectory', $dataDirectory,
            '-ServiceName', $serviceName,
            '-SkipServiceRegistration',
            '-NoElevate'
        )

        Assert-True ($result.ExitCode -eq 0) ('Expected exit code 0, got {0}. Output: {1}' -f $result.ExitCode, $result.Output)
        Assert-True ($result.Output -like '*is not present*') ('The already removed directories were not reported. Output: {0}' -f $result.Output)
    }

    Invoke-TestCase -Name 'uninstall.ps1 refuses a protected -DataDirectory' -Body {
        # -WhatIf guarantees that nothing is removed even if the guard regressed.
        $result = Invoke-Script -ScriptPath $script:UninstallScript -Arguments @(
            '-InstallDirectory', (Join-Path $script:TestRoot 'protected install\BootWakeMailer'),
            '-DataDirectory', $env:ProgramData,
            '-ServiceName', $serviceName,
            '-SkipServiceRegistration',
            '-NoElevate',
            '-WhatIf'
        )

        Assert-True ($result.ExitCode -eq 1) ('Expected exit code 1, got {0}. Output: {1}' -f $result.ExitCode, $result.Output)
        Assert-True ($result.Output -like '*protected directory*') ('The guard message is missing. Output: {0}' -f $result.Output)
        Assert-True (Test-Path -LiteralPath $env:ProgramData -PathType Container) 'ProgramData is gone, which must never happen.'
    }

    Invoke-TestCase -Name 'uninstall.ps1 does not touch a service it was not asked to remove' -Body {
        # The real service name is passed, but -SkipServiceRegistration and
        # -WhatIf keep both the real service and the real default directories out
        # of reach; explicit temporary directories are used for the same reason.
        $result = Invoke-Script -ScriptPath $script:UninstallScript -Arguments @(
            '-InstallDirectory', (Join-Path $script:TestRoot 'skipped service install\BootWakeMailer'),
            '-DataDirectory', (Join-Path $script:TestRoot 'skipped service data\BootWakeMailer'),
            '-ServiceName', 'BootWakeMailer',
            '-SkipServiceRegistration',
            '-NoElevate',
            '-WhatIf'
        )

        Assert-True ($result.ExitCode -eq 0) ('Expected exit code 0, got {0}. Output: {1}' -f $result.ExitCode, $result.Output)
        Assert-True ($result.Output -like '*was not touched*') ('The skip notice is missing. Output: {0}' -f $result.Output)
    }
}

# ---------------------------------------------------------------------------
# Optional: a real publish output
# ---------------------------------------------------------------------------

function Test-ReleasePackage {
    # $PSBoundParameters inside a function describes that function, so the
    # script level decision is captured in $script:VerifyReleasePackage instead.
    if (-not $script:VerifyReleasePackage) {
        Write-Step 'Release package'
        Write-Log 'Skipped: pass -PackageDirectory to verify a publish output.' 'WARN'
        return
    }

    Write-Step 'Release package'

    $packageRoot = [System.IO.Path]::GetFullPath($PackageDirectory)

    Invoke-TestCase -Name 'the package contains the entry points at its root' -Body {
        foreach ($file in @('install.cmd', 'install.ps1', 'uninstall.cmd', 'uninstall.ps1', 'README.txt')) {
            Assert-FileExists (Join-Path $packageRoot $file)
        }
    }

    Invoke-TestCase -Name 'the package contains both self-contained applications' -Body {
        Assert-FileExists (Join-Path $packageRoot 'app\service\BootWakeMailer.Service.exe')
        Assert-FileExists (Join-Path $packageRoot 'app\service\coreclr.dll')
        Assert-FileExists (Join-Path $packageRoot 'app\configtool\BootWakeMailer.ConfigTool.exe')
        Assert-FileExists (Join-Path $packageRoot 'app\configtool\coreclr.dll')
    }

    Invoke-TestCase -Name 'the package payload installs and uninstalls cleanly' -Body {
        $installDirectory = Join-Path -Path $script:TestRoot -ChildPath 'package target\BootWakeMailer'
        $dataDirectory = Join-Path -Path $script:TestRoot -ChildPath 'package data\BootWakeMailer'

        $install = Invoke-Script -ScriptPath (Join-Path $packageRoot 'install.ps1') -Arguments @(
            '-InstallDirectory', $installDirectory,
            '-DataDirectory', $dataDirectory,
            '-ServiceName', 'BootWakeMailerScriptTest-Package',
            '-SkipServiceRegistration',
            '-NoElevate'
        )
        Assert-True ($install.ExitCode -eq 0) ('Install failed with exit code {0}. Output: {1}' -f $install.ExitCode, $install.Output)
        Assert-FileExists (Join-Path $installDirectory 'service\BootWakeMailer.Service.exe')

        $uninstall = Invoke-Script -ScriptPath (Join-Path $packageRoot 'uninstall.ps1') -Arguments @(
            '-InstallDirectory', $installDirectory,
            '-DataDirectory', $dataDirectory,
            '-ServiceName', 'BootWakeMailerScriptTest-Package',
            '-SkipServiceRegistration',
            '-NoElevate'
        )
        Assert-True ($uninstall.ExitCode -eq 0) ('Uninstall failed with exit code {0}. Output: {1}' -f $uninstall.ExitCode, $uninstall.Output)
        Assert-PathMissing $installDirectory
        Assert-PathMissing $dataDirectory
    }
}

# ---------------------------------------------------------------------------
# Run
# ---------------------------------------------------------------------------

try {
    Write-Host ''
    Write-Host 'BootWakeMailer installation script tests' -ForegroundColor White
    Write-Log ('Repository : {0}' -f $script:RepositoryRoot)
    Write-Log ('Test root  : {0}' -f $script:TestRoot)

    New-Item -ItemType Directory -Path $script:TestRoot -Force | Out-Null

    # $PSBoundParameters is only reliable at script level.
    $script:VerifyReleasePackage = $PSBoundParameters.ContainsKey('PackageDirectory')

    Test-ScriptSyntax
    Test-CommandLineEndings
    Test-EntryPointWiring
    Test-DryRun
    Test-Install
    Test-Uninstall
    Test-ReleasePackage

    Write-Host ''
    Write-Host ('Tests passed: {0}, failed: {1}' -f $script:PassedCount, $script:FailedCount)

    if ($script:FailedCount -gt 0) {
        Write-Host ''
        Write-Log 'Failed tests:' 'ERROR'
        foreach ($failure in $script:Failures) {
            Write-Log ('  {0}' -f $failure) 'ERROR'
        }

        exit 1
    }

    exit 0
}
finally {
    if (-not $KeepTestDirectories) {
        if (Test-Path -LiteralPath $script:TestRoot) {
            Remove-Item -LiteralPath $script:TestRoot -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
    else {
        Write-Log ('Test directories kept: {0}' -f $script:TestRoot) 'WARN'
    }
}
