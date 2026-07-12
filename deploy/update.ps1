#Requires -RunAsAdministrator
<#
.SYNOPSIS
  Radirec のバイナリを差し替える(冪等)。

.DESCRIPTION
  稼働中サービスを停止 → radirec.exe を差し替え → 再開する。データ(設定・台帳・マスタ)は
  %ProgramData%\radirec\ 側にあり触らない(ADR-0006)。台帳スキーマはサービス起動時に自動移行する
  (ADR-0004)。認証情報は不要(登録済みの run-as アカウントを継承)。取込通知トレイ(Radirec.Gui。
  ADR-0009)も、稼働中なら停止 → 差し替え → 再起動する(サービスとは独立)。

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

# 取込通知トレイ(Radirec.Gui)の定数・ヘルパ(ADR-0009)。
. (Join-Path $PSScriptRoot '_TrayCommon.ps1')

$ServiceName = 'Radirec'
$SourceRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$ExeSource = Join-Path $SourceRoot 'radirec.exe'
$ExeTarget = Join-Path $InstallDir 'radirec.exe'

# 取込通知トレイ。サービスとは独立に差し替える(ADR-0009)。
$GuiExeSource = Join-Path $SourceRoot $GuiExeName
$GuiExeTarget = Join-Path $InstallDir $GuiExeName

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

# 3. サービス再開(起動時に台帳を自動移行)。ここまでを critical path として先に完結させる。
Write-Host "サービスを開始しています..."
Start-Service -Name $ServiceName
(Get-Service -Name $ServiceName).WaitForStatus('Running', '00:00:30')
Write-Host "[OK] 開始しました" -ForegroundColor Green

# 4. 取込通知トレイ(Radirec.Gui)差し替え(サービスとは独立。ADR-0009)。
#    トレイは cosmetic なので、ここでの失敗(exe ロック等)がサービス更新を巻き添えにしないよう
#    try/catch で握る(サービスを止めたまま abort しない)。稼働中トレイは停止しロック解放を待つ(F4)。
if (Test-Path $GuiExeSource) {
    try {
        $guiWasRunning = Test-TrayRunning
        $guiWasInstalled = Test-Path $GuiExeTarget
        Stop-Tray
        Copy-Item -Path $GuiExeSource -Destination $GuiExeTarget -Force
        Write-Host "[OK] $GuiExeName を差し替えました"
        Register-TrayAutostart -ExePath $GuiExeTarget
        # 稼働中だった、または今回初めて GUI が入った(未登録)なら起動して常駐を回復/初期化する。
        if ($guiWasRunning -or -not $guiWasInstalled) {
            Start-Process -FilePath $GuiExeTarget
            Write-Host "[OK] 取込通知トレイを起動しました"
        } else {
            Write-Host "[skip] トレイは停止中だったため起動しません(次回ログオンで自動起動)"
        }
    } catch {
        Write-Warning "通知トレイの更新に失敗しました(サービスは更新済み)。次回ログオンで回復します: $_"
    }
} else {
    Write-Host "[skip] $GuiExeName が zip に無いため通知トレイは更新しません"
}
