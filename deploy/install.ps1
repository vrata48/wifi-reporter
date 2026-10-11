<#
.SYNOPSIS
    Installs or updates the Wi-Fi Reporter service on this PC (or removes it with -Uninstall).

.DESCRIPTION
    Run from the unzipped release folder (install.cmd does that with the right execution policy).
    - Copies WifiReporter.exe (and iperf3.exe + cygwin1.dll if found) to C:\Program Files\WifiReporter.
    - Creates %ProgramData%\WifiReporter\config.json if it does not exist yet: from a config.json placed
      next to this script, otherwise from config.example.json plus the broker host and the MQTT
      credentials (asked for).
      An existing config is kept, only the values passed as parameters are changed.
    - Installs and starts the service, or restarts it with the new exe if it is already installed.
    - Waits for the first published state and shows whether Windows hides Wi-Fi details (restricted),
      plus a short hardware summary.

    Kept ASCII-only: Windows PowerShell 5.1 reads BOM-less scripts in the ANSI code page.

.EXAMPLE
    .\install.cmd
.EXAMPLE
    .\install.cmd -MqttHost 192.168.1.10 -MqttUsername wifireporter -MqttPassword secret -DeviceName NOTEBOOK
.EXAMPLE
    .\install.cmd -Uninstall
#>
param(
    [string]$MqttHost,
    [string]$MqttUsername,
    [string]$MqttPassword,
    [string]$DeviceName,
    [switch]$Uninstall,
    [switch]$NoPause
)

$ErrorActionPreference = 'Stop'
$ServiceName = 'WifiReporter'
$InstallDir = Join-Path $env:ProgramFiles 'WifiReporter'
$DataDir = Join-Path $env:ProgramData 'WifiReporter'
$ConfigPath = Join-Path $DataDir 'config.json'
$Source = $PSScriptRoot

function Test-Admin {
    $principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Quote([string]$s) { '"' + $s.Replace('"', '\"') + '"' }

function Wait-Exit {
    if (-not $NoPause) { Read-Host 'Press Enter to close' | Out-Null }
}

# Self-elevate: re-run this script as administrator in a new window, passing the parameters along.
if (-not (Test-Admin)) {
    $argList = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Quote $PSCommandPath))
    foreach ($p in $PSBoundParameters.GetEnumerator()) {
        if ($p.Value -is [switch]) { if ($p.Value) { $argList += "-$($p.Key)" } }
        else { $argList += "-$($p.Key)"; $argList += (Quote ([string]$p.Value)) }
    }
    Start-Process powershell.exe -Verb RunAs -ArgumentList $argList
    exit
}

function Get-Service-Safe { Get-Service -Name $ServiceName -ErrorAction SilentlyContinue }

function Stop-Reporter {
    $svc = Get-Service-Safe
    if ($svc -and $svc.Status -ne 'Stopped') {
        Write-Host "Stopping service $ServiceName..."
        Stop-Service -Name $ServiceName -Force
    }
    # A console / logon-task instance would lock the exe and fight over the MQTT client id.
    Get-Process -Name WifiReporter -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Seconds 1
}

function Find-Iperf {
    # 1. shipped next to this script, 2. winget portable install (user or machine scope), 3. PATH
    if (Test-Path (Join-Path $Source 'iperf3.exe')) { return $Source }
    $roots = @(
        (Join-Path $env:LOCALAPPDATA 'Microsoft\WinGet\Packages'),
        (Join-Path $env:ProgramFiles 'WinGet\Packages')
    )
    foreach ($root in $roots) {
        if (-not (Test-Path $root)) { continue }
        $exe = Get-ChildItem -Path $root -Filter 'iperf3.exe' -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($exe) { return $exe.DirectoryName }
    }
    $cmd = Get-Command iperf3.exe -ErrorAction SilentlyContinue
    if ($cmd) { return (Split-Path $cmd.Source) }
    return $null
}

function Read-Plain([string]$prompt) {
    $secure = Read-Host $prompt -AsSecureString
    $bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
    try { return [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr) }
    finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr) }
}

function Save-Json($obj, [string]$path) {
    $json = $obj | ConvertTo-Json
    [IO.File]::WriteAllText($path, $json, (New-Object Text.UTF8Encoding($false)))
}

try {
    if ($Uninstall) {
        $exe = Join-Path $InstallDir 'WifiReporter.exe'
        Stop-Reporter
        if (Get-Service-Safe) {
            if (Test-Path $exe) { & $exe uninstall | Out-Host } else { & sc.exe delete $ServiceName | Out-Host }
        }
        # via cmd: in PowerShell 5.1 a redirected native stderr would throw with ErrorActionPreference=Stop
        & cmd.exe /c "schtasks.exe /Query /TN $ServiceName >nul 2>&1"
        if ($LASTEXITCODE -eq 0) { & $exe uninstall-task | Out-Host }
        if (Test-Path $InstallDir) { Remove-Item -Recurse -Force $InstallDir }
        Write-Host ''
        Write-Host "Removed. Config and logs are kept in $DataDir (delete the folder to remove them too)."
        Wait-Exit
        exit 0
    }

    if (-not (Test-Path (Join-Path $Source 'WifiReporter.exe'))) {
        throw "WifiReporter.exe not found next to this script ($Source). Run install from the unzipped release folder."
    }

    # --- files ------------------------------------------------------------------------------------------
    Stop-Reporter
    New-Item -ItemType Directory -Force $InstallDir | Out-Null
    Copy-Item (Join-Path $Source 'WifiReporter.exe') $InstallDir -Force
    $example = Join-Path $Source 'config.example.json'
    if (Test-Path $example) { Copy-Item $example $InstallDir -Force }
    Write-Host "Installed WifiReporter.exe to $InstallDir"

    $iperfDir = Find-Iperf
    if (-not $iperfDir -and (Get-Command winget.exe -ErrorAction SilentlyContinue)) {
        Write-Host 'iperf3 not found, installing it with winget (ar51an.iPerf3)...'
        & winget.exe install --id ar51an.iPerf3 -e --accept-source-agreements --accept-package-agreements --silent | Out-Host
        $iperfDir = Find-Iperf
    }
    $iperfInstalled = $false
    if ($iperfDir) {
        Copy-Item (Join-Path $iperfDir 'iperf3.exe') $InstallDir -Force
        $dll = Join-Path $iperfDir 'cygwin1.dll'
        if (Test-Path $dll) { Copy-Item $dll $InstallDir -Force }
        $iperfInstalled = $true
        Write-Host "Copied iperf3 from $iperfDir"
    }
    else {
        Write-Warning 'iperf3 not found: the throughput test will be skipped (put iperf3.exe + cygwin1.dll next to install.ps1).'
    }

    # --- config -----------------------------------------------------------------------------------------
    New-Item -ItemType Directory -Force $DataDir | Out-Null
    if (Test-Path $ConfigPath) {
        Write-Host "Keeping existing config $ConfigPath"
        $cfg = Get-Content $ConfigPath -Raw | ConvertFrom-Json
    }
    else {
        $bundled = Join-Path $Source 'config.json'
        $template = if (Test-Path $bundled) { $bundled } else { $example }
        if (-not (Test-Path $template)) { throw "Neither config.json nor config.example.json found in $Source" }
        Write-Host "Creating $ConfigPath from $(Split-Path $template -Leaf)"
        $cfg = Get-Content $template -Raw | ConvertFrom-Json
        if ($template -eq $example -and -not $MqttHost) {
            $MqttHost = Read-Host "MQTT broker (Home Assistant) host or IP [$($cfg.mqttHost)]"
        }
    }

    if ($MqttHost) {
        # iperf3 usually runs on the same machine as the broker (the Home Assistant host).
        if ($cfg.iperfServer -eq $cfg.mqttHost) { $cfg.iperfServer = $MqttHost }
        $cfg.mqttHost = $MqttHost
    }

    if ($MqttUsername) { $cfg.mqttUsername = $MqttUsername }
    if ($MqttPassword) { $cfg.mqttPassword = $MqttPassword }
    if ($PSBoundParameters.ContainsKey('DeviceName')) { $cfg.deviceName = $DeviceName }
    if (-not $cfg.mqttUsername) { $cfg.mqttUsername = Read-Host 'MQTT username' }
    if (-not $cfg.mqttPassword) { $cfg.mqttPassword = Read-Plain 'MQTT password' }
    # iperf3 now sits next to the exe; a path from another PC would not exist here.
    if ($iperfInstalled) { $cfg.iperfPath = 'iperf3.exe' }
    Save-Json $cfg $ConfigPath

    # --- service ----------------------------------------------------------------------------------------
    $exe = Join-Path $InstallDir 'WifiReporter.exe'
    $startedAt = Get-Date
    if (Get-Service-Safe) {
        Write-Host "Service exists, starting the new version..."
        Start-Service -Name $ServiceName
    }
    else {
        & $exe install | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "WifiReporter.exe install failed (exit $LASTEXITCODE)" }
    }

    # --- verify -----------------------------------------------------------------------------------------
    Write-Host 'Waiting for the first report...'
    $logDir = Join-Path $DataDir 'logs'
    $state = $null
    $system = $null
    $problem = $null
    $deadline = $startedAt.AddSeconds(45)
    while ((Get-Date) -lt $deadline -and -not $state) {
        Start-Sleep -Seconds 3
        $log = Get-ChildItem $logDir -Filter '*.log' -ErrorAction SilentlyContinue | Sort-Object LastWriteTime | Select-Object -Last 1
        if (-not $log) { continue }
        foreach ($line in Get-Content $log.FullName -Tail 30) {
            $stamp = [datetime]::MinValue
            if ($line.Length -lt 19 -or -not [datetime]::TryParseExact($line.Substring(0, 19), 'yyyy-MM-dd HH:mm:ss',
                    [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::None, [ref]$stamp)) { continue }
            if ($stamp -lt $startedAt.AddSeconds(-1)) { continue }
            if ($line -match ' (WARN|ERROR) ') { $problem = $line }
            if ($line -match 'INFO  state (\{.*\})') { $state = $Matches[1] | ConvertFrom-Json }
            if ($line -match 'INFO  system (\{.*\})') { $system = $Matches[1] | ConvertFrom-Json }
        }
    }

    Write-Host ''
    if ($state) {
        $name = if ($cfg.deviceName) { $cfg.deviceName } else { $env:COMPUTERNAME }
        Write-Host "OK: '$name' is reporting to MQTT $($cfg.mqttHost)" -ForegroundColor Green
        Write-Host ("    SSID {0}, AP {1}, {2}, signal {3} dBm, link {4}/{5} Mbit/s" -f `
            $state.ssid, $state.bssid, $state.band, $state.rssi, $state.rx_rate, $state.tx_rate)
        if ($state.restricted) {
            Write-Warning ('Windows hides SSID/BSSID/rates from the service (location privacy). ' +
                'Turn on Settings > Privacy & security > Location > "Let desktop apps access your location", ' +
                'or switch to the logon task: "' + $exe + '" uninstall; "' + $exe + '" install-task')
        }
        if ($system) {
            Write-Host ("    {0}, {1}, {2}" -f $system.cpu, $system.ram, $system.gpu)
        }
        Write-Host "    The throughput test (iperf3) runs right after start, then every $($cfg.iperfIntervalSeconds / 60) minutes (to $($cfg.iperfServer))."
    }
    else {
        Write-Warning "No state published within 45 s. Last problem in the log: $problem"
        Write-Host "    Log folder: $logDir"
    }
    if ($problem -and $state) { Write-Warning "Log: $problem" }
    Write-Host "Config: $ConfigPath   Logs: $logDir"
}
catch {
    Write-Host ''
    Write-Host "FAILED: $($_.Exception.Message)" -ForegroundColor Red
    Wait-Exit
    exit 1
}

Wait-Exit
