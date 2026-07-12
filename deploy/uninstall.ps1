#Requires -RunAsAdministrator
<#
.SYNOPSIS
  Radirec を削除する(冪等)。既定でデータは残す。

.DESCRIPTION
  サービスを停止・削除し、EventLog ソースを解除し、実行体を削除する。取込通知トレイ(Radirec.Gui。
  ADR-0009)も停止・削除し、ログオン自動起動(HKCU Run)を解除する。
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
$GuiProcessName = 'Radirec.Gui'
$GuiExePath = Join-Path $InstallDir 'Radirec.Gui.exe'
$GuiRunKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$GuiRunValue = 'RadirecTray'

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

# 3. 取込通知トレイ(Radirec.Gui)停止・削除・自動起動解除(ADR-0009)
Get-Process -Name $GuiProcessName -ErrorAction SilentlyContinue | Stop-Process -Force
if (Test-Path $GuiExePath) {
    Remove-Item -Path $GuiExePath -Force
    Write-Host "[OK] Radirec.Gui.exe を削除しました"
} else {
    Write-Host "[skip] $GuiExePath は存在しない"
}
# HKCU Run の自己登録を解除(GUI が自己登録した値。実行ユーザーのハイブから消す)。
if (Get-ItemProperty -Path $GuiRunKey -Name $GuiRunValue -ErrorAction SilentlyContinue) {
    Remove-ItemProperty -Path $GuiRunKey -Name $GuiRunValue
    Write-Host "[OK] ログオン自動起動(HKCU Run '$GuiRunValue')を解除しました"
} else {
    Write-Host "[skip] HKCU Run '$GuiRunValue' は未登録"
}
# トースト用のスタートメニューショートカット(ToastNotificationManagerCompat 生成)は
# ユーザーの Start Menu に残りうるが無害。気になる場合は手動削除。

# 4. 実行体削除(radirec.exe のみ)。
#    InstallDir を丸ごと Remove-Item -Recurse すると、旧同居レイアウト(exe と台帳が同一
#    フォルダ)や InstallDir==DataDir の指定時に、-PurgeData 無しでも台帳を巻き添え削除してしまう。
$installFull = [System.IO.Path]::GetFullPath($InstallDir)
$dataFull = [System.IO.Path]::GetFullPath($DataDir)
$exePath = Join-Path $InstallDir 'radirec.exe'
if ($installFull -eq $dataFull) {
    Write-Host "[warn] InstallDir が DataDir と同一。radirec.exe のみ削除し、データは温存します: $InstallDir" -ForegroundColor Yellow
}
if (Test-Path $exePath) {
    Remove-Item -Path $exePath -Force
    Write-Host "[OK] radirec.exe を削除しました"
    # InstallDir が(データと別で)空になったらディレクトリも片付ける。
    if ($installFull -ne $dataFull -and -not (Get-ChildItem -Path $InstallDir -Force -ErrorAction SilentlyContinue)) {
        Remove-Item -Path $InstallDir -Force
        Write-Host "[OK] 空になった $InstallDir を削除しました"
    }
} else {
    Write-Host "[skip] $exePath は存在しない"
}

# 5. データ(既定は保持)
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
