# 本地自签名（只对本机有效，用来验证签名流程 / 消掉自己机器上的提示）
#
# 用法：
#   powershell -ExecutionPolicy Bypass -File tools\sign-local.ps1
#   powershell -ExecutionPolicy Bypass -File tools\sign-local.ps1 -Trust
#
# 说明：
#   · 不传参数 = 只签名（其他机器下载仍然提示未知发布者）
#   · -Trust    = 顺便把证书加进「当前用户 -> 受信任的根证书颁发机构」，本机不再提示
#   · 证书有效期 3 年，存在 Cert:\CurrentUser\My 里，脚本会复用

param(
    [string]$Path = "csharp\out\mcroute.exe",
    [switch]$Trust,
    [string]$Subject = "CN=mcroute dev (self-signed)"
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path $Path)) { throw "找不到文件：$Path（先 dotnet build 一下）" }
Write-Host "目标文件：$Path" -ForegroundColor Cyan

$cert = Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert -ErrorAction SilentlyContinue |
        Where-Object { $_.Subject -eq $Subject } |
        Sort-Object NotAfter -Descending |
        Select-Object -First 1

if ($cert) {
    Write-Host "复用已有证书：$($cert.Thumbprint)  到期 $($cert.NotAfter.ToString('yyyy-MM-dd'))" -ForegroundColor Green
} else {
    Write-Host "创建自签名证书 ..." -ForegroundColor Yellow
    $cert = New-SelfSignedCertificate -Type CodeSigningCert -Subject $Subject -KeyExportPolicy Exportable -KeyUsage DigitalSignature -NotAfter (Get-Date).AddYears(3) -CertStoreLocation "Cert:\CurrentUser\My"
    Write-Host "已创建：$($cert.Thumbprint)" -ForegroundColor Green
}

if ($Trust) {
    $cerPath = Join-Path $env:TEMP "mcroute-dev-signing.cer"
    Export-Certificate -Cert $cert -FilePath $cerPath -Force | Out-Null
    Import-Certificate -FilePath $cerPath -CertStoreLocation "Cert:\CurrentUser\Root" | Out-Null
    Remove-Item $cerPath -Force -ErrorAction SilentlyContinue
    Write-Host "已加入当前用户受信任根证书（只对本机有效）" -ForegroundColor Green
}

$sig = Set-AuthenticodeSignature -FilePath $Path -Certificate $cert -TimestampServer "http://timestamp.digicert.com" -ErrorAction SilentlyContinue

Write-Host ""
Write-Host "签名结果：$($sig.Status)" -ForegroundColor Cyan
if ($sig.SignerCertificate) { Write-Host "签名者  ：$($sig.SignerCertificate.Subject)" }
if ($sig.Status -ne 'Valid') {
    Write-Host "状态信息：$($sig.StatusMessage)" -ForegroundColor Yellow
    Write-Host "提示：如果只是时间戳拿不到，签名本身仍然有效" -ForegroundColor Yellow
}

Write-Host ""
Write-Host "验证命令：" -ForegroundColor Cyan
Write-Host ('  Get-AuthenticodeSignature "{0}" | Format-List Status, SignerCertificate' -f $Path)
