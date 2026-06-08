param(
    [int]$IntervalMinutes = 30,
    [string[]]$Countries = @("Canada", "USA", "United States"),
    [string[]]$Locations = @()
)

$ErrorActionPreference = "Stop"

function Get-ExpressVpnCommand {
    $commands = @("expressvpnctl", "expressvpn")

    foreach ($name in $commands) {
        $cmd = Get-Command $name -ErrorAction SilentlyContinue
        if ($cmd) {
            return $cmd.Source
        }
    }

    throw "Could not find the ExpressVPN CLI. Make sure ExpressVPN is installed and available in PATH."
}

function Invoke-ExpressVpn {
    param(
        [string]$Executable,
        [string[]]$Arguments
    )

    Write-Host "[$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')] $([System.IO.Path]::GetFileName($Executable)) $($Arguments -join ' ')"
    & $Executable @Arguments
}

function Get-RegionCommandArguments {
    param([string]$Executable)

    $name = [System.IO.Path]::GetFileNameWithoutExtension($Executable).ToLowerInvariant()
    if ($name -eq "expressvpnctl") {
        return @("get", "regions")
    }

    return @("list", "all")
}

function Get-ConnectCommandArguments {
    param(
        [string]$Executable,
        [string]$Location
    )

    return @("connect", $Location)
}

function Get-DisconnectCommandArguments {
    param([string]$Executable)

    return @("disconnect")
}

function Get-AvailableLocations {
    param(
        [string]$Executable,
        [string[]]$CountryFilters
    )

    $arguments = Get-RegionCommandArguments -Executable $Executable
    $rawOutput = (& $Executable @arguments 2>&1 | Out-String)
    $lines = $rawOutput -split "`r?`n"

    $escapedCountries = $CountryFilters | ForEach-Object { [Regex]::Escape($_) }
    $countryPattern = '^(' + ($escapedCountries -join '|') + ')\s*-\s*.+$'

    $locations = $lines |
        ForEach-Object { $_.Trim() } |
        Where-Object { $_ -match $countryPattern } |
        Select-Object -Unique

    if ($locations.Count -eq 0) {
        throw "No ExpressVPN locations matched: $($CountryFilters -join ', '). Raw output from '$($arguments -join ' ')':`n$rawOutput"
    }

    return [string[]]$locations
}

function Get-NextLocation {
    param(
        [string[]]$Pool,
        [string]$Current
    )

    if ($Pool.Count -eq 1) {
        return $Pool[0]
    }

    $choices = $Pool | Where-Object { $_ -ne $Current }
    return Get-Random -InputObject $choices
}

if ($IntervalMinutes -lt 1) {
    throw "IntervalMinutes must be at least 1."
}

$expressVpn = Get-ExpressVpnCommand

if ($Locations.Count -gt 0) {
    $locationPool = $Locations
}
else {
    $locationPool = Get-AvailableLocations -Executable $expressVpn -CountryFilters $Countries
}

$currentLocation = $null

Write-Host "Starting ExpressVPN rotation. Interval: $IntervalMinutes minute(s)."
Write-Host "Location pool ($($locationPool.Count)): $($locationPool -join '; ')"
Write-Host "Run this script from an elevated PowerShell window if ExpressVPN requires admin access."
Write-Host "Press Ctrl+C to stop."

while ($true) {
    $nextLocation = Get-NextLocation -Pool $locationPool -Current $currentLocation

    try {
        Invoke-ExpressVpn -Executable $expressVpn -Arguments (Get-DisconnectCommandArguments -Executable $expressVpn)
    }
    catch {
        Write-Warning "Disconnect failed or no active connection was present. Continuing."
    }

    Invoke-ExpressVpn -Executable $expressVpn -Arguments (Get-ConnectCommandArguments -Executable $expressVpn -Location $nextLocation)
    $currentLocation = $nextLocation

    Write-Host "Connected to '$currentLocation'. Sleeping for $IntervalMinutes minute(s)..."
    Start-Sleep -Seconds ($IntervalMinutes * 60)
}
