# 镜像失效演练：验证"BMCLAPI/HIT 镜像挂了还能不能装"
#
# 用法：powershell -ExecutionPolicy Bypass -File tools\test-fallback.ps1 [-Version 1.19.2]
param([string]$Version = "1.19.2")

$ErrorActionPreference = 'Stop'
$exe = "legacy\winforms\out\mcroute.exe"
$data = "mcdata"
if (-not (Test-Path $exe)) { throw "先 dotnet build csharp\mcroute.csproj -c Release -o csharp\out" }

Write-Host "== 1) 找一个该版本用到的库，删掉，模拟需要重新下载 ==" -ForegroundColor Cyan
$jar = Get-ChildItem "$data\libraries" -Recurse -Filter "*.jar" -ErrorAction SilentlyContinue |
       Where-Object { $_.Length -gt 100000 } | Select-Object -First 1
if (-not $jar) { throw "libraries 目录里没有可测试的 jar" }
Write-Host ("   目标: " + $jar.Name)
Copy-Item $jar.FullName ($jar.FullName + ".bak") -Force

Write-Host "== 2) 镜像模式 ==" -ForegroundColor Cyan
Remove-Item $jar.FullName -Force
$sw = [System.Diagnostics.Stopwatch]::StartNew()
& $exe fetch $Version --dir $data --no-assets | Out-Null
$sw.Stop()
$okMirror = Test-Path $jar.FullName
Write-Host ("   耗时 " + [math]::Round($sw.Elapsed.TotalSeconds,2) + "s  复原=" + $okMirror)

Write-Host "== 3) 禁用镜像（模拟镜像失效，只走官方）==" -ForegroundColor Cyan
Remove-Item $jar.FullName -Force
$sw2 = [System.Diagnostics.Stopwatch]::StartNew()
& $exe fetch $Version --dir $data --no-assets --no-mirror | Out-Null
$sw2.Stop()
$okOfficial = Test-Path $jar.FullName
Write-Host ("   耗时 " + [math]::Round($sw2.Elapsed.TotalSeconds,2) + "s  复原=" + $okOfficial)

Write-Host ""
if ($okMirror -and $okOfficial) {
    Write-Host "结论：两种模式都能下载 —— 镜像失效时官方兜底有效 ✅" -ForegroundColor Green
} else {
    Write-Host "结论：有模式失败 ❌  镜像=$okMirror  官方=$okOfficial" -ForegroundColor Red
    exit 1
}
