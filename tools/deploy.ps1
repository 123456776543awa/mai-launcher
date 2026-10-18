param(
    [string]$Source = 'D:\Desktop\app\publish3',
    [string]$Target = 'D:\MaimaiLauncher'
)

$ErrorActionPreference = 'Continue'

function Stop-All {
    for ($i = 0; $i -lt 15; $i++) {
        $procs = Get-Process MaimaiLauncher -ErrorAction SilentlyContinue
        if (-not $procs) { return $true }
        foreach ($p in $procs) {
            try { taskkill /F /PID $p.Id 2>$null | Out-Null } catch { }
        }
        Start-Sleep -Seconds 1
    }
    return -not (Get-Process MaimaiLauncher -ErrorAction SilentlyContinue)
}

function Wait-Unlocked([string]$path, [int]$seconds = 15) {
    for ($i = 0; $i -lt $seconds; $i++) {
        try {
            $fs = [IO.File]::Open($path, 'Open', 'ReadWrite', 'None')
            $fs.Close()
            return $true
        } catch {
            Start-Sleep -Seconds 1
        }
    }
    return $false
}

Write-Host '=> stop running launcher'
if (-not (Stop-All)) {
    Write-Host '!! launcher is still running, close it manually (or reboot)'
    exit 1
}

$exe = Join-Path $Target 'MaimaiLauncher.exe'
$dll = Join-Path $Target 'MaimaiLauncher.dll'

if (-not (Wait-Unlocked $exe)) {
    Write-Host '!! exe is locked, close it manually (or reboot)'
    exit 1
}

Write-Host '=> replace exe/dll'
Move-Item $exe "$exe.old" -Force -ErrorAction SilentlyContinue
Move-Item $dll "$dll.old" -Force -ErrorAction SilentlyContinue

robocopy $Source $Target /E /R:2 /W:1 /NFL /NDL /NJH /NJS | Out-Null

if (-not (Test-Path $exe)) {
    Write-Host '!! copy failed'
    exit 1
}

Write-Host ('=> deployed exe: ' + (Get-Item $exe).LastWriteTime)

Start-Process $exe
Start-Sleep -Seconds 5
Write-Host '=> started'
