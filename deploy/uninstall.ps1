#Requires -RunAsAdministrator
<#
.SYNOPSIS
  Radirec を削除する(冪等)。既定でデータは残す。

.DESCRIPTION
  サービスを停止・削除し、EventLog ソースを解除し、実行体を削除する。
  データ(設定・台帳・マスタ)は既定で残す(誤削除防止)。'-PurgeData' で明示削除する。
  各手順は対象が既に無くてもエラーにしない(冪等)。

.PARAMETER InstallDir
  実行体の配置先。既定 'C:\Program Files\radirec'。

.PARAMETER DataDir
  データの配置先。既定 '%ProgramData%\radirec'。

.PARAMETER PurgeData
  データ(台帳・設定含む)も削除する。台帳を消すと取り込み冪等の記録が失われる点に注意。
#>
[CmdletBinding()]
param(
    [string]$InstallDir = "$env:ProgramFiles\radirec",
    [string]$DataDir = "$env:ProgramData\radirec",
    [switch]$PurgeData
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$ServiceName = 'Radirec'

Write-Host "== Radirec アンインストール ==" -ForegroundColor Cyan

# 1. サービス停止・削除
$svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($svc) {
    if ($svc.Status -ne 'Stopped') { Stop-Service -Name $ServiceName -Force }
    sc.exe delete $ServiceName | Out-Null
    Write-Host "[OK] サービス '$ServiceName' を削除しました"
} else {
    Write-Host "[skip] サービス '$ServiceName' は未登録"
}

# 2. EventLog ソース解除
if ([System.Diagnostics.EventLog]::SourceExists($ServiceName)) {
    [System.Diagnostics.EventLog]::DeleteEventSource($ServiceName)
    Write-Host "[OK] EventLog ソース '$ServiceName' を解除しました"
} else {
    Write-Host "[skip] EventLog ソース '$ServiceName' は未登録"
}

# 3. 実行体削除
if (Test-Path $InstallDir) {
    Remove-Item -Path $InstallDir -Recurse -Force
    Write-Host "[OK] 実行体 $InstallDir を削除しました"
} else {
    Write-Host "[skip] 実行体 $InstallDir は存在しない"
}

# 4. データ(既定は保持)
if ($PurgeData) {
    if (Test-Path $DataDir) {
        Remove-Item -Path $DataDir -Recurse -Force
        Write-Host "[OK] データ $DataDir を削除しました" -ForegroundColor Yellow
    } else {
        Write-Host "[skip] データ $DataDir は存在しない"
    }
} else {
    Write-Host "[keep] データは残しました: $DataDir(削除するには -PurgeData)" -ForegroundColor Cyan
}
