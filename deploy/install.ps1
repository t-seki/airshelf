#Requires -RunAsAdministrator
<#
.SYNOPSIS
  Radirec を初回インストールする(冪等)。

.DESCRIPTION
  バイナリを Program Files へ、データ(設定・台帳・マスタ)を %ProgramData%\radirec\ へ分離配置する
  (ADR-0006)。EventLog ソース登録・設定の初回生成・Windows サービス登録(run-as 自分のアカウント)
  までを一括で行う。再実行しても壊れない:
    - EventLog ソースは存在すればスキップ
    - 設定は存在すれば温存(上書きしない)
    - サービスは存在すればスキップ('-Force' で再登録)

  更新(バイナリ差し替え)は update.ps1、削除は uninstall.ps1 を使う。

.PARAMETER InstallDir
  実行体の配置先。既定 'C:\Program Files\radirec'。

.PARAMETER DataDir
  データ(radirec.json / ledger.db / stations.json / programs.json)の配置先。
  既定 '%ProgramData%\radirec'。

.PARAMETER ServiceAccount
  サービスの実行アカウント。NAS(UNC)へ書くため自分の Windows アカウントを指定する。
  既定 '.\<現在のユーザー>'。パスワードは登録時に対話入力する。

.PARAMETER Force
  既存サービスがあっても停止・削除して再登録する(パスワードを再入力)。
#>
[CmdletBinding()]
param(
    [string]$InstallDir = "$env:ProgramFiles\radirec",
    [string]$DataDir = "$env:ProgramData\radirec",
    [string]$ServiceAccount = ".\$env:USERNAME",
    [switch]$Force
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$ServiceName = 'Radirec'
$SourceRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$ExeSource = Join-Path $SourceRoot 'radirec.exe'
$ConfigSampleDir = Join-Path $SourceRoot 'config'
$ExeTarget = Join-Path $InstallDir 'radirec.exe'
$ConfigPath = Join-Path $DataDir 'radirec.json'

if (-not (Test-Path $ExeSource)) {
    throw "radirec.exe が見つかりません: $ExeSource(Release zip 内の deploy/ から実行してください)"
}

Write-Host "== Radirec インストール ==" -ForegroundColor Cyan
Write-Host "  実行体: $InstallDir"
Write-Host "  データ: $DataDir"
Write-Host "  実行アカウント: $ServiceAccount"

# 1. バイナリ配置
New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null
Copy-Item -Path $ExeSource -Destination $ExeTarget -Force
Write-Host "[OK] radirec.exe を配置しました"

# 2. データディレクトリ
New-Item -ItemType Directory -Path $DataDir -Force | Out-Null

# 3. EventLog ソース登録(冪等)
if ([System.Diagnostics.EventLog]::SourceExists($ServiceName)) {
    Write-Host "[skip] EventLog ソース '$ServiceName' は登録済み"
} else {
    New-EventLog -LogName Application -Source $ServiceName
    Write-Host "[OK] EventLog ソース '$ServiceName' を登録しました"
}

# 4. 設定の初回生成(存在すれば温存)
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

# 5. サービス登録
$existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($existing -and -not $Force) {
    Write-Host "[skip] サービス '$ServiceName' は登録済み。バイナリ更新は update.ps1 を使ってください" -ForegroundColor Yellow
    Write-Host "       (再登録が必要なら -Force を付けて再実行)"
    Write-Host ""
    Write-Host "設定を編集してください: $ConfigPath" -ForegroundColor Cyan
    return
}

if ($existing -and $Force) {
    Write-Host "[force] 既存サービスを停止・削除して再登録します"
    if ($existing.Status -ne 'Stopped') { Stop-Service -Name $ServiceName -Force }
    sc.exe delete $ServiceName | Out-Null
    Start-Sleep -Seconds 1
}

# NAS(UNC)へ書くため run-as = 自分のアカウント。マップ済み Z: はサービスから不可視なので UNC 直参照。
$cred = Get-Credential -UserName $ServiceAccount -Message "サービス実行アカウントのパスワード(NAS アクセス用)"
$binPath = '"{0}" service --config "{1}"' -f $ExeTarget, $ConfigPath
New-Service -Name $ServiceName -BinaryPathName $binPath -StartupType Automatic -Credential $cred | Out-Null
Write-Host "[OK] サービス '$ServiceName' を登録しました"

Write-Host ""
Write-Host "設定を確認・編集してください: $ConfigPath" -ForegroundColor Cyan
Write-Host "編集後、サービスを開始: Start-Service $ServiceName" -ForegroundColor Cyan
