# GetSystemInfo.ps1
# Script to collect and display system information

# System Overview
function Get-SystemOverview {
    $computerInfo = Get-ComputerInfo
    $os = $computerInfo.OsName + " " + $computerInfo.OsVersion
    $cpu = (Get-WmiObject -Class Win32_Processor).Name
    $ram = [math]::Round($computerInfo.CsTotalPhysicalMemory / 1GB, 2)
    
    return [PSCustomObject]@{
        "OS" = $os
        "CPU" = $cpu
        "RAM (GB)" = $ram
        "ComputerName" = $env:COMPUTERNAME
        "UserName" = $env:USERNAME
        "PowerShell Version" = $PSVersionTable.PSVersion.ToString()
    }
}

# Disk Information
function Get-DiskInfo {
    $disks = Get-WmiObject -Class Win32_LogicalDisk -Filter "DriveType=3"
    $diskInfo = foreach ($disk in $disks) {
        $freeSpaceGB = [math]::Round($disk.FreeSpace / 1GB, 2)
        $totalSizeGB = [math]::Round($disk.Size / 1GB, 2)
        $percentFree = [math]::Round(($disk.FreeSpace / $disk.Size) * 100, 2)
        
        [PSCustomObject]@{
            "Drive" = $disk.DeviceID
            "Total Size (GB)" = $totalSizeGB
            "Free Space (GB)" = $freeSpaceGB
            "Free Space (%)" = $percentFree
        }
    }
    return $diskInfo
}

# Network Information
function Get-NetworkInfo {
    $networkAdapters = Get-NetAdapter | Where-Object { $_.Status -eq "Up" }
    $networkInfo = foreach ($adapter in $networkAdapters) {
        $ipConfig = Get-NetIPAddress -InterfaceIndex $adapter.ifIndex -AddressFamily IPv4
        
        [PSCustomObject]@{
            "Adapter Name" = $adapter.Name
            "Connection Speed" = "{0:N2} Mbps" -f ($adapter.LinkSpeed / 1000000)
            "IP Address" = $ipConfig.IPAddress
            "MAC Address" = $adapter.MacAddress
        }
    }
    return $networkInfo
}

# Running Processes (Top 10 by CPU usage)
function Get-TopProcesses {
    $processes = Get-Process | Sort-Object -Property CPU -Descending | Select-Object -First 10
    $processInfo = foreach ($process in $processes) {
        [PSCustomObject]@{
            "Process Name" = $process.ProcessName
            "ID" = $process.Id
            "CPU (s)" = [math]::Round($process.CPU, 2)
            "Memory (MB)" = [math]::Round($process.WorkingSet64 / 1MB, 2)
        }
    }
    return $processInfo
}

# .NET Framework Versions
function Get-DotNetVersions {
    $dotNetVersions = Get-ChildItem "HKLM:\SOFTWARE\Microsoft\NET Framework Setup\NDP" -Recurse |
        Get-ItemProperty -Name Version, Release -ErrorAction SilentlyContinue |
        Where-Object { $_.PSChildName -match '^(?!S)\p{L}' } |
        Select-Object PSChildName, Version, Release
        
    return $dotNetVersions
}

# Main execution
Write-Host "=============================================" -ForegroundColor Cyan
Write-Host "           SYSTEM INFORMATION               " -ForegroundColor Cyan
Write-Host "=============================================" -ForegroundColor Cyan

Write-Host "`n[System Overview]" -ForegroundColor Yellow
Get-SystemOverview | Format-List

Write-Host "`n[Disk Information]" -ForegroundColor Yellow
Get-DiskInfo | Format-Table -AutoSize

Write-Host "`n[Network Information]" -ForegroundColor Yellow
Get-NetworkInfo | Format-Table -AutoSize

Write-Host "`n[Top 10 Processes by CPU Usage]" -ForegroundColor Yellow
Get-TopProcesses | Format-Table -AutoSize

Write-Host "`n[.NET Framework Versions]" -ForegroundColor Yellow
Get-DotNetVersions | Format-Table -AutoSize

Write-Host "`n=============================================" -ForegroundColor Cyan
Write-Host "          END OF SYSTEM INFORMATION          " -ForegroundColor Cyan
Write-Host "=============================================" -ForegroundColor Cyan 