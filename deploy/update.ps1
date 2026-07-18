#Requires -RunAsAdministrator
<#
.SYNOPSIS
  Radirec(トレイ常駐)のバイナリを差し替える(冪等)。

.DESCRIPTION
  トレイ常駐移行(ADR-0012)後の更新スクリプト。取り込みワーカーはトレイ(Radirec.Gui)が
  ホストするため、**Windows サービスの停止/起動は行わない**(以前は必要だった)。稼働中のトレイを
  停止 → exe(Radirec.Gui.exe + 単発 CLI radirec.exe)を差し替え → トレイを再起動する。データ
  (設定・台帳・マスタ)は %ProgramData%\radirec\ 側にあり触らない。台帳スキーマは起動時に自動移行する
  (ADR-0004)。認証情報は不要。

  稼働中に publish/コピーすると exe/DLL ロックで失敗するため、必ずトレイを停止してから差し替える。
  ※ Velopack 自己更新(トレイからの online 更新)は S5 / #67 で置き換える予定。

.PARAMETER InstallDir
  実行体の配置先。既定 'C:\Program Files\radirec'。
#>
[CmdletBinding()]
param(
    [string]$InstallDir = "$env:ProgramFiles\radirec"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# 取込通知トレイ(Radirec.Gui)の定数・ヘルパ。
. (Join-Path $PSScriptRoot '_TrayCommon.ps1')

$SourceRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$ExeSource = Join-Path $SourceRoot 'radirec.exe'
$ExeTarget = Join-Path $InstallDir 'radirec.exe'
$GuiExeSource = Join-Path $SourceRoot $GuiExeName
$GuiExeTarget = Join-Path $InstallDir $GuiExeName

if (-not (Test-Path $GuiExeSource)) {
    throw "$GuiExeName が見つかりません: $GuiExeSource(Release zip 内の deploy/ から実行してください)"
}
if (-not (Test-Path $GuiExeTarget)) {
    throw "トレイが未インストールです($GuiExeTarget)。先に install.ps1 を実行してください。"
}

Write-Host "== Radirec 更新(トレイ常駐)==" -ForegroundColor Cyan

New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null

# 稼働状態を控えてトレイを停止(exe/DLL ロック解放を待つ)。
$guiWasRunning = Test-TrayRunning
Stop-Tray
if ($guiWasRunning) {
    Write-Host "[OK] トレイを停止しました"
} else {
    Write-Host "[skip] トレイは起動していませんでした"
}

# バイナリ差し替え(トレイ本体 + 単発 CLI)。
Copy-Item -Path $GuiExeSource -Destination $GuiExeTarget -Force
Write-Host "[OK] $GuiExeName を差し替えました"
if (Test-Path $ExeSource) {
    Copy-Item -Path $ExeSource -Destination $ExeTarget -Force
    Write-Host "[OK] radirec.exe(単発 CLI)を差し替えました"
}

# 自動起動を冪等に確認。
Register-TrayAutostart -ExePath $GuiExeTarget

# 元々起動していた、または初回配置なら起動する。停止中だったなら次回ログオンで自動起動に任せる。
if ($guiWasRunning) {
    Start-Process -FilePath $GuiExeTarget
    Write-Host "[OK] トレイを再起動しました" -ForegroundColor Green
} else {
    Write-Host "[skip] トレイは停止中だったため起動しません(次回ログオンで自動起動)"
}
