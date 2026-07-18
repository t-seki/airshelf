#Requires -RunAsAdministrator
<#
.SYNOPSIS
  Radirec(トレイ常駐)を初回インストールする(冪等)。

.DESCRIPTION
  トレイ常駐移行(ADR-0012)後の配置スクリプト。取り込みワーカーはトレイ(Radirec.Gui)が
  ユーザーセッションでホストするため、**Windows サービス登録・EventLog ソース登録・サービス実行
  アカウントの資格情報は行わない**(以前は必要だった。ADR-0007 は本 ADR で supersede)。

  行うこと:
    - 実行体(Radirec.Gui.exe + 単発 CLI radirec.exe)を Program Files へ配置
    - データ(設定・台帳・マスタ)を %ProgramData%\radirec\ へ配置(ADR-0006)。設定は既存なら温存
    - トレイをログオン自動起動へ登録し、今すぐ起動
    - 旧 Windows サービス(移行前の登録)が残っていれば退役(defunct: service verb は撤去済み)

  更新(exe 差し替え)は update.ps1、削除は uninstall.ps1。
  ※ per-user・非 admin 化(%LocalAppData%)と Velopack 自己更新は S5 / #67 で被せる。

.PARAMETER InstallDir
  実行体の配置先。既定 'C:\Program Files\radirec'。

.PARAMETER DataDir
  データ(radirec.json / ledger.db / stations.json / programs.json)の配置先。既定 '%ProgramData%\radirec'。
#>
[CmdletBinding()]
param(
    [string]$InstallDir = "$env:ProgramFiles\radirec",
    [string]$DataDir = "$env:ProgramData\radirec"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# 取込通知トレイ(Radirec.Gui)の定数・ヘルパ。
. (Join-Path $PSScriptRoot '_TrayCommon.ps1')

$ServiceName = 'Radirec'
$SourceRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$ExeSource = Join-Path $SourceRoot 'radirec.exe'
$GuiExeSource = Join-Path $SourceRoot $GuiExeName
$ConfigSampleDir = Join-Path $SourceRoot 'config'
$ExeTarget = Join-Path $InstallDir 'radirec.exe'
$GuiExeTarget = Join-Path $InstallDir $GuiExeName
$ConfigPath = Join-Path $DataDir 'radirec.json'

if (-not (Test-Path $GuiExeSource)) {
    throw "$GuiExeName が見つかりません: $GuiExeSource(Release zip 内の deploy/ から実行してください)"
}

Write-Host "== Radirec インストール(トレイ常駐)==" -ForegroundColor Cyan
Write-Host "  実行体: $InstallDir"
Write-Host "  データ: $DataDir"

# --- 旧 Windows サービスの退役(移行前の登録が残っていれば)---
# service verb は撤去済み(ADR-0012)。旧サービスは起動できず、放置すると同じ config を読んで
# 競合しうるため停止・削除する。未登録なら何もしない(冪等)。
$oldSvc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($oldSvc) {
    Write-Host "[migrate] 旧 Windows サービス '$ServiceName' を退役します(トレイ常駐へ移行)"
    if ($oldSvc.Status -ne 'Stopped') { Stop-Service -Name $ServiceName -Force }
    sc.exe delete $ServiceName | Out-Null
    $deadline = (Get-Date).AddSeconds(15)
    while ((Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) -and (Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 500
    }
    Write-Host "[OK] 旧サービスを削除しました"
}
# 旧 EventLog ソースが残っていれば解除(通知は in-process 化。無ければスキップ)。
if ([System.Diagnostics.EventLog]::SourceExists($ServiceName)) {
    [System.Diagnostics.EventLog]::DeleteEventSource($ServiceName)
    Write-Host "[OK] 旧 EventLog ソース '$ServiceName' を解除しました"
}

# --- データ・設定 ---
New-Item -ItemType Directory -Path $DataDir, $InstallDir -Force | Out-Null

$configMap = @{
    'radirec.sample.json'  = 'radirec.json'
    'stations.sample.json' = 'stations.json'
    'programs.sample.json' = 'programs.json'
}
foreach ($sample in $configMap.Keys) {
    $src = Join-Path $ConfigSampleDir $sample
    $dst = Join-Path $DataDir $configMap[$sample]
    if (Test-Path $dst) {
        Write-Host "[skip] $($configMap[$sample]) は既存(温存)"
    } elseif (Test-Path $src) {
        Copy-Item -Path $src -Destination $dst
        Write-Host "[OK] $($configMap[$sample]) を sample から生成しました"
    }
}

# --- dev/batch 用 CLI(任意。常駐はしない)---
if (Test-Path $ExeSource) {
    Copy-Item -Path $ExeSource -Destination $ExeTarget -Force
    Write-Host "[OK] radirec.exe(単発 CLI:ingest/refresh)を配置しました"
}

# --- 取込ワーカーをホストする常駐トレイ(Radirec.Gui)---
# 起動中の健全なトレイは触らない(冪等な再実行で常駐を止めないため)。exe 差し替えは update.ps1。
if (Test-TrayRunning) {
    Write-Host "[skip] トレイは起動中。exe 差し替えは update.ps1 を使ってください"
} else {
    Copy-Item -Path $GuiExeSource -Destination $GuiExeTarget -Force
    Write-Host "[OK] $GuiExeName を配置しました"
}

# ログオン自動起動を操作者の HKCU Run へ冪等登録(アプリ側の自己登録と等価。単一ユーザー前提)。
Register-TrayAutostart -ExePath $GuiExeTarget
Write-Host "[OK] ログオン自動起動を登録しました"

# 停止中なら今すぐ起動(次回ログオンからは非昇格で自動起動)。
if (-not (Test-TrayRunning)) {
    Start-Process -FilePath $GuiExeTarget
    Write-Host "[OK] トレイを起動しました" -NoNewline
    Write-Host "(※今回は管理者起動のため昇格実行。定常はログオン時に非昇格で自動起動)" -ForegroundColor DarkGray
}

Write-Host ""
Write-Host "設定を確認・編集してください: $ConfigPath" -ForegroundColor Cyan
Write-Host "編集を反映するにはトレイメニューの「設定リロード」、またはトレイを再起動してください" -ForegroundColor Cyan
