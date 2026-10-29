# Installs Tally on Windows: downloads the app's source, builds it on this PC with the .NET SDK (installed for this
# user if it isn't here, with nothing system-wide and no admin rights), and puts it in this user's programs, with a
# Start menu entry. Building here, as on the Mac, means no unsigned download for SmartScreen to warn about. Run it again
# to update.
#   irm @SITE@/install.ps1 | iex
# Everything it does goes to %LOCALAPPDATA%\Tally\Logs\install.log.
#
# `make site` fills in SITE, VERSION and SHA256 (the source tarball's checksum).
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'  # Invoke-WebRequest's progress bar slows downloads to a crawl

$Version = '@VERSION@'
$Sha256 = '@SHA256@'
$Site = '@SITE@'
$Data = Join-Path $env:LOCALAPPDATA 'Tally'
$Programs = Join-Path $env:LOCALAPPDATA 'Programs\Tally'
$Log = Join-Path $Data 'Logs\install.log'
$exe = Join-Path $Programs 'Tally.exe'
$closed = $false
New-Item (Split-Path $Log) -ItemType Directory -Force | Out-Null
Add-Content $Log "--- $(Get-Date -Format o) installing $Version"

function Say($text) { Write-Host "==> $text" -ForegroundColor White; Add-Content $Log "==> $text" }
# Caught at the end and said plainly: run as `irm … | iex`, exit would close the person's own window before they read why
function Fail($text) {
    Add-Content $Log "FAILED: $text"
    throw "Tally was not installed: $text"
}
# A path as an MSBuild value: an account name may hold ' or $, which MSBuild reads as its own syntax
function Escaped($path) { $path.Replace('%', '%25').Replace("'", '%27').Replace('$', '%24').Replace('@', '%40') }
function HasSdk($dotnet) { $dotnet -and (Test-Path $dotnet) -and ((& $dotnet --list-sdks 2>$null) -match '^10\.') }
# A step's output goes to the log, and on failure its last lines to the screen too
function Run($what, [scriptblock]$step) {
    $ErrorActionPreference = 'Continue'  # a tool's warnings on stderr aren't failures: its exit code says
    $output = & $step 2>&1
    $output | Out-String | Add-Content $Log
    if ($LASTEXITCODE -ne 0) { $output | Select-Object -Last 20 | Out-String | Write-Host; Fail "$what failed." }
}

try {
    # An organisation's app control (AppLocker, App Control for Business) blocks apps built on the PC, as Smart App Control
    # does. Where it covers scripts, it also puts PowerShell in a constrained language the rest of this can't run in:
    # checked first
    if ($ExecutionContext.SessionState.LanguageMode -ne 'FullLanguage') {
        Fail "This PC's organisation runs app control (AppLocker or App Control for Business), which blocks apps built on the PC, as Tally is. Ask whoever manages it."
    }
    $build = [Environment]::OSVersion.Version.Build
    if ($build -lt 22000) { Fail "Tally needs Windows 11; this PC has build $build." }
    # Smart App Control runs only signed code, or code Microsoft's cloud already knows: a Tally built here is neither, so
    # with it on, Windows would block Tally from opening (no "run anyway"). 1 is on, 2 evaluating (it may turn on later).
    $sac = (Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\CI\Policy' -ErrorAction SilentlyContinue).VerifiedAndReputablePolicyState
    if ($sac -eq 1) { Fail "Smart App Control is on, and it blocks apps built on this PC, as Tally is (Windows Security, App & browser control, Smart App Control settings)." }
    if ($sac -eq 2) { Say "Smart App Control is evaluating this PC: if it turns on, it will block Tally from opening" }
    # The runtime and the project's platform for this PC's processor
    $arch, $platform = if ($env:PROCESSOR_ARCHITECTURE -eq 'ARM64') { 'arm64', 'ARM64' } else { 'x64', 'x64' }

    # Updating closes Tally, which would cut a recording short: checked now, and again just before closing it
    function Installed { Get-Process Tally -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$Programs*" } }
    function CheckNotRecording {
        if (-not (Installed)) { return }
        $recording = Get-ChildItem (Join-Path $Data 'recordings\*\local.json') -ErrorAction SilentlyContinue |
            Where-Object { (Get-Content -Raw $_.FullName) -match '"state"\s*:\s*"recording"' }
        if ($recording) { Fail "Tally is recording. Run this again when the recording is done." }
    }
    CheckNotRecording

    # In Tally's data folder, not TEMP: the build is moved into place, and a move can't cross drives (TEMP may be on D:)
    $work = Join-Path $Data "install-$PID"
    # Folders left by installs cut short (a closed window, a power cut): Windows cleans TEMP, but not these
    Get-ChildItem $Data -Directory -Filter 'install-*' -ErrorAction SilentlyContinue |
        Where-Object { -not (Get-Process -Id ($_.Name -replace '^install-') -ErrorAction SilentlyContinue) } |
        Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
    New-Item $work -ItemType Directory -Force | Out-Null
    # Run from TEMP, never from Tally's folder, which is moved aside below and can't be while a process is in it.
    # Pop-Location puts the person's window back where it was
    Push-Location $env:TEMP
    [Environment]::CurrentDirectory = $env:TEMP  # Push-Location doesn't change the process's own directory
    try {
        Say "Downloading Tally $Version"
        $tarball = Join-Path $work 'tally.tar.gz'
        try { Invoke-WebRequest "$Site/tally-windows-$Version.tar.gz" -OutFile $tarball -UseBasicParsing }
        catch { Fail "Downloading failed. Check your internet connection, then run this command again." }
        if ((Get-FileHash $tarball -Algorithm SHA256).Hash -ne $Sha256) { Fail "The download didn't match its checksum. Try again." }
        # From inside it, with no path of the user's on tar's command line: Windows' tar mangles names outside the PC's
        # code page (a local account called Michał)
        Run 'Unpacking' { Push-Location $work; try { tar -xzf 'tally.tar.gz' } finally { Pop-Location } }

        $dotnet = Get-Command dotnet -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source
        if (-not (HasSdk $dotnet)) {
            $sdk = Join-Path $Data 'dotnet'
            $dotnet = Join-Path $sdk 'dotnet.exe'
            if (-not (HasSdk $dotnet)) {
                Say "Installing the .NET SDK for this user (once, a few minutes)"
                $installer = Join-Path $work 'dotnet-install.ps1'
                try { Invoke-WebRequest 'https://dot.net/v1/dotnet-install.ps1' -OutFile $installer -UseBasicParsing }
                catch { Fail "Downloading the .NET SDK's installer failed. Check your internet connection, then run this command again." }
                # Into a folder of its own, moved into place only once whole: an install cut short (a closed window, a
                # power cut) left a half SDK that dotnet-install takes for installed, and every build after it failed
                $fresh = Join-Path $Data 'dotnet.new'
                if (Test-Path $fresh) { Remove-Item $fresh -Recurse -Force }
                # Through a script block, not a script file: Group Policy's execution policy applies to files.
                $env:TALLY_DOTNET_INSTALL, $env:TALLY_SDK = $installer, $fresh
                Run 'Installing the .NET SDK' { & powershell -NoProfile -Command '& ([scriptblock]::Create([IO.File]::ReadAllText($env:TALLY_DOTNET_INSTALL))) -Channel 10.0 -InstallDir $env:TALLY_SDK' }
                if (Test-Path $sdk) { Remove-Item $sdk -Recurse -Force }
                Move-Item $fresh $sdk
            }
        }
        $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
        $env:DOTNET_NOLOGO = '1'

        Say "Building Tally (a few minutes the first time)"
        $staging = Join-Path $work 'Tally'
        $project = Join-Path $work 'apps\windows\Tally\Tally.csproj'
        $settings = Join-Path $work 'apps\windows\tally.json'
        Run 'The build' { & $dotnet publish $project -c Release -r "win-$arch" "-p:Platform=$platform" "-p:TallySettings=$(Escaped $settings)" -p:TreatWarningsAsErrors=false -o (Escaped $staging) -nologo }

        CheckNotRecording  # again: a recording may have started during the build
        if (Installed) {
            Say "Closing Tally"
            Installed | Stop-Process -Force
            Start-Sleep 1
            $closed = $true
        }
        Say "Installing in $Programs"
        # The old version moves aside first, and comes back if the new one can't go in, so a PC is never left without Tally
        $old = "$Programs.old"
        if (Test-Path $old) { Remove-Item $old -Recurse -Force }
        New-Item (Split-Path $Programs) -ItemType Directory -Force | Out-Null
        try {
            if (Test-Path $Programs) { Move-Item $Programs $old }
            Move-Item $staging $Programs
        }
        catch {
            if (Test-Path $old) { Move-Item $old $Programs }
            Fail "Tally couldn't be put in place ($($_.Exception.Message)). The version you had is still there."
        }
        if (Test-Path $old) { Remove-Item $old -Recurse -Force -ErrorAction SilentlyContinue }

        # The Start menu, and Settings' Installed apps, where it's removed as any app is. The shortcut through Windows'
        # own Unicode interface: WScript.Shell's saves through the ANSI one, which can't name a folder like C:\Users\Michał
        Add-Type -TypeDefinition @"
    using System.Runtime.InteropServices;
    using System.Text;
    public static class TallyShortcut {
        [ComImport, Guid("00021401-0000-0000-C000-000000000046")] class ShellLink { }
        [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IShellLinkW {
            void GetPath(StringBuilder file, int size, System.IntPtr data, int flags);
            void GetIDList(out System.IntPtr list);
            void SetIDList(System.IntPtr list);
            void GetDescription(StringBuilder name, int size);
            void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
            void GetWorkingDirectory(StringBuilder dir, int size);
            void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);
            void GetArguments(StringBuilder args, int size);
            void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);
            void GetHotkey(out short key);
            void SetHotkey(short key);
            void GetShowCmd(out int show);
            void SetShowCmd(int show);
            void GetIconLocation(StringBuilder path, int size, out int index);
            void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string path, int index);
            void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, int reserved);
            void Resolve(System.IntPtr window, int flags);
            void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
        }
        public static void Save(string shortcut, string target) {
            var link = (IShellLinkW)new ShellLink();
            link.SetPath(target);
            ((System.Runtime.InteropServices.ComTypes.IPersistFile)link).Save(shortcut, true);
        }
    }
"@
        [TallyShortcut]::Save((Join-Path ([Environment]::GetFolderPath('Programs')) 'Tally.lnk'), $exe)
        Copy-Item (Join-Path $work 'apps\windows\uninstall.ps1') (Join-Path $Programs 'uninstall.ps1')
        $key = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\Tally'
        New-Item $key -Force | Out-Null
        Set-ItemProperty $key DisplayName 'Tally'
        Set-ItemProperty $key DisplayVersion $Version
        Set-ItemProperty $key Publisher 'Tally'
        Set-ItemProperty $key DisplayIcon $exe
        Set-ItemProperty $key InstallLocation $Programs
        # Through iex, not a script file: Group Policy's execution policy applies to files.
        Set-ItemProperty $key UninstallString "powershell.exe -NoProfile -Command `"iex ([IO.File]::ReadAllText([Environment]::GetFolderPath('LocalApplicationData') + '\Programs\Tally\uninstall.ps1'))`""
        Set-ItemProperty $key NoModify 1
        Set-ItemProperty $key NoRepair 1

        Say "Opening Tally: it's in the notification area, by the clock"
        Start-Process explorer.exe "`"$exe`""  # by Explorer: at the user's own level even from an admin terminal, as Tally opens things
        Add-Content $Log "Installed $Version"
    }
    finally {
        Pop-Location
        Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
        # Closed for an update that then failed: the version that's there opens again, so calls are still caught
        if ($closed -and -not (Installed) -and (Test-Path $exe)) { Start-Process explorer.exe "`"$exe`"" }
    }
}
catch {
    if ($_.Exception.Message -notlike 'Tally was not installed:*') { throw }  # not one of ours: everything there is to see
    Write-Host $_.Exception.Message -ForegroundColor Red
    Write-Host "The log is in $Log"
    if ($env:TALLY_INSTALLER) { exit 1 }  # Tally's own Update runs this in a PowerShell of its own, and reads the exit code
}
