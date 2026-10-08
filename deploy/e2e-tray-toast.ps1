#Requires -RunAsAdministrator
<#
.SYNOPSIS
  トレイ常駐(ADR-0012)の E2E スモークテストを、実録音なし・本番資産に触れず行う。

.DESCRIPTION
  本番 airshelf.json を退避し、隔離テスト config(RadioRoot/OutputRoot/LedgerPath を %TEMP% の絶対パスに、
  Jellyfin 空)へ差し替える。管理画面の設定 gui-settings.json(新規取込のトーストの切り替え。#152・ADR-0019)も
  退避し、新規取込のトーストを出す設定で試す(本番に gui-settings.json が無ければ、その旨を記録しておき、
  -Cleanup で試験が作ったものを消す)。ffmpeg があれば有効な無音 M4A を らじれこ命名で生成し、トレイ(Airshelf.Gui)を
  起動して「新規取込 1件」トーストが 1 回出ることを確認する。本番の NAS・Jellyfin・台帳には一切触れない。
  旧 Windows サービスが在れば停止する(同じ config を読む競合防止)。

  検証後は -Cleanup で本番 config(airshelf.json・gui-settings.json)を復元し、テスト作業ディレクトリを削除する。

  ※ これは検証用ハーネスであり、通常の配置(Velopack Setup.exe)とは別。データルートは
     %LOCALAPPDATA%\AirshelfData(ADR-0013 / S5。Velopack 導入先 %LOCALAPPDATA%\Airshelf とは別)。

.PARAMETER GuiExe
  トレイ実行体のパス。既定は deploy/ の 1 つ上の Airshelf.Gui.exe。Velopack 導入版を試すなら
  %LOCALAPPDATA%\Airshelf\current\Airshelf.Gui.exe を -GuiExe で指す。

.PARAMETER Cleanup
  テスト config を本番へ復元し(airshelf.json・gui-settings.json)、作業ディレクトリを削除する。
#>
[CmdletBinding()]
param(
    [string]$GuiExe,
    [switch]$Cleanup
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# トレイ定数・ヘルパ(GuiExeName / Stop-Tray / Test-TrayRunning)。
. (Join-Path $PSScriptRoot '_TrayCommon.ps1')

$SourceRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
if (-not $GuiExe) { $GuiExe = Join-Path $SourceRoot $GuiExeName }

$ServiceName = 'Radirec'
$dataDir = Join-Path $env:LOCALAPPDATA 'AirshelfData'  # ADR-0013 / S5: データは %LOCALAPPDATA%\AirshelfData
$realCfg = Join-Path $dataDir 'airshelf.json'
$bakCfg  = Join-Path $dataDir 'airshelf.json.e2e-bak'
# 管理画面の設定(#152・ADR-0019)。本番に無かったときは「無かった」印を置き、-Cleanup で試験が作ったものを消す。
$realGui   = Join-Path $dataDir 'gui-settings.json'
$bakGui    = Join-Path $dataDir 'gui-settings.json.e2e-bak'
$absentGui = Join-Path $dataDir 'gui-settings.json.e2e-absent'
$base    = Join-Path $env:TEMP 'airshelf-e2e'
$inRoot  = Join-Path $base 'in'
$outRoot = Join-Path $base 'out'
$ledger  = Join-Path $base 'ledger.db'
$guiLog  = Join-Path $env:LOCALAPPDATA 'AirshelfData\gui.log'

if ($Cleanup) {
    Write-Host '== E2E クリーンアップ ==' -ForegroundColor Cyan
    Stop-Tray
    if (Test-Path $bakCfg) {
        Move-Item -Force $bakCfg $realCfg
        Write-Host '[OK] 本番 airshelf.json を復元しました'
    } else {
        Write-Warning "バックアップ($bakCfg)が無いため config は変更しません"
    }
    if (Test-Path $bakGui) {
        Move-Item -Force $bakGui $realGui
        Write-Host '[OK] 本番 gui-settings.json を復元しました'
    } elseif (Test-Path $absentGui) {
        # 本番には無かった。試験(トレイの起動)が作ったものを消して、無い状態に戻す。
        if (Test-Path $realGui) { Remove-Item -Force $realGui }
        Remove-Item -Force $absentGui
        Write-Host '[OK] 試験で作られた gui-settings.json を削除しました(本番には無かった)'
    } else {
        Write-Warning "gui-settings.json のバックアップ($bakGui)も無かった印も無いため、gui-settings.json は変更しません"
    }
    if (Test-Path $base) { Remove-Item -Recurse -Force $base; Write-Host '[OK] テスト作業ディレクトリを削除' }
    Write-Host '本番運用に戻すには通常の Velopack 導入トレイを起動してください(%LOCALAPPDATA%\Airshelf\current)' -ForegroundColor Yellow
    return
}

Write-Host '== E2E セットアップ(隔離テスト)==' -ForegroundColor Cyan
if (-not (Test-Path $GuiExe)) { throw "$GuiExeName が見つかりません: $GuiExe(-GuiExe で指定)" }

# 旧 Windows サービスが在れば停止(同じ config を読んで競合するため)。登録は消さない。
$svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($svc -and $svc.Status -ne 'Stopped') {
    Stop-Service -Name $ServiceName -Force
    Write-Host '[OK] 旧 Radirec サービスを停止しました(テスト中の競合防止)'
}

# 本番 config を退避(初回のみ)。
if ((Test-Path $realCfg) -and -not (Test-Path $bakCfg)) {
    Copy-Item $realCfg $bakCfg
    Write-Host "[OK] 本番 airshelf.json を退避: $bakCfg"
}

# 本番 gui-settings.json を退避(初回のみ)。無ければ「無かった」印を置く(-Cleanup で試験が作ったものを消すため)。
if (-not (Test-Path $bakGui) -and -not (Test-Path $absentGui)) {
    if (Test-Path $realGui) {
        Copy-Item $realGui $bakGui
        Write-Host "[OK] 本番 gui-settings.json を退避: $bakGui"
    } else {
        New-Item -ItemType File -Path $absentGui | Out-Null
        Write-Host '[OK] 本番に gui-settings.json は無い(-Cleanup で試験が作ったものを消す)'
    }
}
# 新規取込のトーストを出す設定で試す(本番で止めていても、成功パスのトーストを確かめられるように)。
[ordered]@{ IngestToastsEnabled = $true } | ConvertTo-Json | Set-Content -Path $realGui -Encoding UTF8
Write-Host '[OK] 試験用 gui-settings.json を配置(新規取込のトーストを出す)'

# 隔離テスト config(本番の NAS/Jellyfin/台帳に触れない絶対パス)。フィールドは sample と同一集合
# (未知プロパティは Disallow で弾かれるため過不足なく)。stations/programs 無し=未登録局は生名で取込。
New-Item -ItemType Directory -Force -Path $inRoot, $outRoot | Out-Null
$testCfg = [ordered]@{
    RadioRoot            = $inRoot
    OutputRoot           = $outRoot
    LedgerPath           = $ledger
    StationsFile         = (Join-Path $base 'stations.json')
    ProgramsFile         = (Join-Path $base 'programs.json')
    StabilitySeconds     = 2
    Genre                = 'ラジオ'
    SkipPatterns         = @()
    KnownRecordingKinds  = @('TimeFree')
    JellyfinUrl          = ''
    JellyfinApiKey       = ''
    ScanIntervalSeconds  = 30
    WatchDebounceSeconds = 2
    # 旧設定(#152・ADR-0019)。新規取込のトーストは同じフォルダの gui-settings.json の値が優先で、ここは初期値にしかならない
    # (試験では上で gui-settings.json を「出す」にしてある)。
    NotificationsEnabled = $true
}
# Windows PowerShell 5.x の既定は UTF-8 BOM。ConfigLoader は BOM 付き UTF-8 を読める。
$testCfg | ConvertTo-Json | Set-Content -Path $realCfg -Encoding UTF8
Write-Host "[OK] 隔離テスト config を配置(in=$inRoot / out=$outRoot / ledger=$ledger)"

# 合成 M4A を らじれこ命名で生成: [{局}]{番組}(TimeFree)_{YYYYMMDDHHMMSS}.m4a
$stamp = (Get-Date).ToString('yyyyMMddHHmmss')
$progDir = Join-Path $inRoot 'E2Eテスト局\E2Eテスト番組'
New-Item -ItemType Directory -Force -Path $progDir | Out-Null
$file = Join-Path $progDir "[E2Eテスト局]E2Eテスト番組(TimeFree)_$stamp.m4a"

if (Get-Command ffmpeg -ErrorAction SilentlyContinue) {
    & ffmpeg -y -loglevel error -f lavfi -i anullsrc=r=44100:cl=stereo -t 1 -c:a aac -f mp4 "$file"
    Write-Host '[OK] 有効な無音 M4A を生成 → 期待トースト:「新規取込 1件」(成功パス)' -ForegroundColor Green
} else {
    Set-Content -Path $file -Value 'not a real m4a' -Encoding Ascii
    Write-Warning 'ffmpeg 未検出 → 無効ファイルを生成。期待トースト:「取込失敗 1件」(失敗パス)。成功パスは ffmpeg 導入後に再実行'
}

Stop-Tray
Start-Process -FilePath $GuiExe
Write-Host ''
Write-Host '== 観察ポイント ==' -ForegroundColor Cyan
Write-Host ' 1) タスクトレイに Airshelf アイコンが出る(NAS 未接続でも数十秒待たされない)'
Write-Host ' 2) トーストが「ちょうど 1 回」出る(成功=新規取込1件 / ffmpeg無=取込失敗1件)'
Write-Host " 3) gui.log に unhandled 例外が無い: $guiLog"
Write-Host " 4) 成功時、出力先に整形済みファイル: $outRoot"
Write-Host ' 5) トレイ右クリック → 「状態: 稼働中(監視中)」・「最終スキャン」に件数。'
Write-Host '    「今すぐ再スキャン」「設定リロード」も試す'
Write-Host ''
Write-Host '終わったら復元: 同じスクリプトを  -Cleanup  で再実行' -ForegroundColor Yellow
