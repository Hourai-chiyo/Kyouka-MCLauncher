# 抓 1.12.2「正在关闭内置服务器」卡死的线程栈
#
# 用法：
#   1. 游戏卡在"正在关闭内置服务器"时（别关进程！）
#   2. 在 PowerShell 里跑：.\tools\hang-dump.ps1
#   3. 把生成的 dump 文件发给开发者
#
# 原理：用进程自带的 jstack（同目录 bin）导出所有线程栈，
# 卡住时能看到 Server thread / Client thread 到底停在哪个 mod 的方法里。

$ErrorActionPreference = 'SilentlyContinue'

Write-Host "=== 查找 java 进程 ===" -ForegroundColor Cyan
$procs = Get-CimInstance Win32_Process -Filter "Name='javaw.exe' OR Name='java.exe'" |
         Where-Object { $_.CommandLine -match 'minecraft|forge|cleanroom|Foundation|mainClass' -or $_.CommandLine -match 'net\.minecraft' }

if (-not $procs) {
    Write-Host "没找到 Minecraft 的 java 进程。卡死时再跑一次（进程应该还在）。" -ForegroundColor Yellow
    exit 1
}

foreach ($p in $procs) {
    Write-Host ("  PID " + $p.ProcessId + "  " + $p.ExecutablePath) -ForegroundColor Gray
}

# 选第一个（通常是 javaw）
$target = $procs | Select-Object -First 1
$exe = $target.ExecutablePath
if (-not $exe -or -not (Test-Path $exe)) {
    Write-Host "拿不到进程 exe 路径，退出。" -ForegroundColor Red
    exit 1
}

# jstack 和 java 在同一个 bin 目录
$bin = Split-Path $exe -Parent
$jstack = Join-Path $bin 'jstack.exe'
if (-not (Test-Path $jstack)) {
    Write-Host ("找不到 jstack.exe：" + $jstack) -ForegroundColor Red
    Write-Host "提示：这个 java 可能是 JRE（没有 jstack）。用 JDK 的 javaw 启动，或在环境里装一个 JDK 再试。" -ForegroundColor Yellow
    exit 1
}

$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$outDir = Join-Path $env:TEMP 'kyouka-hang-dumps'
New-Item -ItemType Directory -Force -Path $outDir | Out-Null
$outFile = Join-Path $outDir ("hang-" + $stamp + ".txt")

Write-Host ("=== 导出线程栈：" + $outFile) -ForegroundColor Cyan
$header = @(
    "时间: " + (Get-Date).ToString("yyyy-MM-dd HH:mm:ss"),
    "PID : " + $target.ProcessId,
    "exe : " + $exe,
    "命令行: " + $target.CommandLine,
    ""
) -join "`r`n"
Set-Content -Path $outFile -Value $header -Encoding UTF8

& $jstack $target.ProcessId 2>&1 | Out-File -FilePath $outFile -Append -Encoding UTF8

Write-Host ""
Write-Host "=== 关键线程（看这里最省事）===" -ForegroundColor Cyan
$lines = Get-Content $outFile
$interesting = $lines | Select-String -Pattern '^"Server thread"|^"Client thread"|^"main"|shutdown|Stopping|WAITING|BLOCKED' |
               Select-Object -First 40
$interesting | ForEach-Object { Write-Host ("  " + $_.Line) }

Write-Host ""
Write-Host ("完整栈已保存：" + $outFile) -ForegroundColor Green
Write-Host "把这个文件（或里面的 Server thread / Client thread 段）发给开发者即可定位到具体 mod。" -ForegroundColor Green
