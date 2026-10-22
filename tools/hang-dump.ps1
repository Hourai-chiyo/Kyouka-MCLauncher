# Dump Java thread stacks when Minecraft 1.12.2 hangs at
# "Shutting down internal server" / "Saving worlds".
#
# Usage:
#   1. When the game is stuck (do NOT close the game window)
#   2. Run tools\hang-dump.cmd  (double-click is fine)
#   3. Send the dump file to the developer
#
# It finds the Minecraft java process, uses jstack from the same JDK,
# and saves all thread stacks to %TEMP%\kyouka-hang-dumps\hang-<time>.txt

$ErrorActionPreference = 'SilentlyContinue'

Write-Host "=== looking for java processes ===" -ForegroundColor Cyan
$procs = Get-CimInstance Win32_Process -Filter "Name='javaw.exe' OR Name='java.exe'" |
         Where-Object { $_.CommandLine -match 'minecraft|forge|cleanroom|Foundation|net\.minecraft' }

if (-not $procs) {
    Write-Host "No Minecraft java process found. Run this while the game is still stuck." -ForegroundColor Yellow
    exit 1
}

foreach ($p in $procs) {
    Write-Host ("  PID " + $p.ProcessId + "  " + $p.ExecutablePath) -ForegroundColor Gray
}

$target = $procs | Where-Object { $_.CommandLine -match 'top\.outlands\.foundation\.boot\.Foundation|net\.minecraft\.client\.main\.Main' } | Select-Object -First 1
if (-not $target) { $target = $procs | Where-Object { $_.Name -eq 'javaw.exe' } | Select-Object -First 1 }
if (-not $target) { $target = $procs | Select-Object -First 1 }
Write-Host ("  -> target PID " + $target.ProcessId + "  (" + $target.Name + ")") -ForegroundColor Cyan
$exe = $target.ExecutablePath
if (-not $exe -or -not (Test-Path $exe)) {
    Write-Host "Cannot resolve process exe path." -ForegroundColor Red
    exit 1
}

$bin = Split-Path $exe -Parent
$jstack = Join-Path $bin 'jstack.exe'
if (-not (Test-Path $jstack)) {
    Write-Host ("jstack.exe not found: " + $jstack) -ForegroundColor Red
    Write-Host "This java may be a JRE without jstack. Launch with a JDK, or install a JDK." -ForegroundColor Yellow
    exit 1
}

$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$outDir = Join-Path $env:TEMP 'kyouka-hang-dumps'
New-Item -ItemType Directory -Force -Path $outDir | Out-Null
$outFile = Join-Path $outDir ("hang-" + $stamp + ".txt")

Write-Host ("=== dumping to: " + $outFile) -ForegroundColor Cyan
$header = @(
    "time: " + (Get-Date).ToString("yyyy-MM-dd HH:mm:ss"),
    "pid : " + $target.ProcessId,
    "exe : " + $exe,
    "cmd : " + $target.CommandLine,
    ""
) -join "`r`n"
Set-Content -Path $outFile -Value $header -Encoding UTF8

& $jstack $target.ProcessId 2>&1 | Out-File -FilePath $outFile -Append -Encoding UTF8

Write-Host ""
Write-Host "=== interesting threads ===" -ForegroundColor Cyan
$lines = Get-Content $outFile
$lines | Select-String -Pattern '^"Server thread"|^"Client thread"|^"main"|Stopping|Saving|WAITING|BLOCKED' |
    Select-Object -First 40 | ForEach-Object { Write-Host ("  " + $_.Line) }

Write-Host ""
Write-Host ("Full dump saved: " + $outFile) -ForegroundColor Green
Write-Host "Send that file (or the Server/Client thread sections) to the developer." -ForegroundColor Green
