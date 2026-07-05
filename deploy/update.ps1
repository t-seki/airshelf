#Requires -RunAsAdministrator
<#
.SYNOPSIS
  Radirec のバイナリを差し替える(冪等)。

.DESCRIPTION
  稼働中サービスを停止 → radirec.exe を差し替え → 再開する。データ(設定・台帳・マスタ)は
  %ProgramData%\radirec\ 側にあり触らない(ADR-0006)。台帳スキーマはサービス起動時に自動移行する
  (ADR-0004)。認証情報は不要(登録済みの run-as アカウントを継承)。

  稼働中に publish/コピーすると DLL ロックで失敗するため、必ず停止してから差し替える。
  サービス未登録なら install.ps1 を先に実行するようエラーにする。

.PARAMETER InstallDir
  実行体の配置先。既定 'C:\Program Files\radirec'。
#>
[CmdletBinding()]
param(
    [string]$InstallDir = "$env:ProgramFiles\radirec"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$ServiceName = 'Radirec'
$SourceRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$ExeSource = Join-Path $SourceRoot 'radirec.exe'
$ExeTarget = Join-Path $InstallDir 'radirec.exe'

if (-not (Test-Path $ExeSource)) {
    throw "radirec.exe が見つかりません: $ExeSource(Release zip 内の deploy/ から実行してください)"
}

$svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if (-not $svc) {
    throw "サービス '$ServiceName' が未登録です。先に install.ps1 を実行してください。"
}

Write-Host "== Radirec 更新 ==" -ForegroundColor Cyan

# 1. 停止(DLL ロック回避)。Stop-Service は停止完了まで待つ。
if ($svc.Status -ne 'Stopped') {
    Write-Host "サービスを停止しています..."
    Stop-Service -Name $ServiceName -Force
    (Get-Service -Name $ServiceName).WaitForStatus('Stopped', '00:00:30')
    Write-Host "[OK] 停止しました"
} else {
    Write-Host "[skip] サービスは既に停止しています"
}

# 2. バイナリ差し替え
New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null
Copy-Item -Path $ExeSource -Destination $ExeTarget -Force
Write-Host "[OK] radirec.exe を差し替えました"

# 3. 再開(起動時に台帳を自動移行)
Write-Host "サービスを開始しています..."
Start-Service -Name $ServiceName
(Get-Service -Name $ServiceName).WaitForStatus('Running', '00:00:30')
Write-Host "[OK] 開始しました" -ForegroundColor Green
