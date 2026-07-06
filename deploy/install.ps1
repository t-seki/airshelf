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
    - サービスが存在すればバイナリは触らずスキップ('-Force' で停止・削除・再登録)

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

# 実行アカウントに「サービスとしてログオン」(SeServiceLogonRight)を付与する。
# New-Service -Credential はこの権利を自動付与しないため、未付与だと Start-Service が 1069 で失敗する。
# secedit で USER_RIGHTS 領域のみを加算的に更新する(既存権利は保持)。失敗しても致命化しない。
function Grant-ServiceLogonRight {
    param([string]$Account)
    try {
        $sid = ([System.Security.Principal.NTAccount]$Account).Translate(
            [System.Security.Principal.SecurityIdentifier]).Value
    } catch {
        Write-Warning "アカウント '$Account' の SID を解決できません。secpol.msc で『サービスとしてログオン』を手動付与してください。"
        return
    }

    $inf = Join-Path $env:TEMP 'radirec-secpol.inf'
    $sdb = Join-Path $env:TEMP 'radirec-secpol.sdb'
    try {
        secedit /export /areas USER_RIGHTS /cfg $inf | Out-Null
        $lines = Get-Content $inf
        $current = $lines | Where-Object { $_ -match '^SeServiceLogonRight' }

        if ($current -and $current -match [regex]::Escape("*$sid")) {
            Write-Host "[skip] '$Account' は既に『サービスとしてログオン』権利を保持"
            return
        }
        if ($current) {
            $updated = $current.TrimEnd() + ",*$sid"
            $lines = $lines -replace [regex]::Escape($current), $updated
        } else {
            $lines = $lines -replace '(\[Privilege Rights\])', "`$1`r`nSeServiceLogonRight = *$sid"
        }
        Set-Content -Path $inf -Value $lines -Encoding Unicode
        secedit /configure /db $sdb /cfg $inf /areas USER_RIGHTS | Out-Null
        Write-Host "[OK] '$Account' に『サービスとしてログオン』権利を付与しました"
    } catch {
        Write-Warning "『サービスとしてログオン』権利の付与に失敗しました($_)。secpol.msc で手動付与してください。"
    } finally {
        Remove-Item $inf, $sdb -ErrorAction SilentlyContinue
    }
}

Write-Host "== Radirec インストール ==" -ForegroundColor Cyan
Write-Host "  実行体: $InstallDir"
Write-Host "  データ: $DataDir"
Write-Host "  実行アカウント: $ServiceAccount"

# --- サービス状態に依存しない冪等ステップ(EventLog / データ / 設定)---

# データディレクトリ
New-Item -ItemType Directory -Path $DataDir -Force | Out-Null

# EventLog ソース登録(冪等)
if ([System.Diagnostics.EventLog]::SourceExists($ServiceName)) {
    Write-Host "[skip] EventLog ソース '$ServiceName' は登録済み"
} else {
    New-EventLog -LogName Application -Source $ServiceName
    Write-Host "[OK] EventLog ソース '$ServiceName' を登録しました"
}

# 設定の初回生成(存在すれば温存)
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

# --- サービス登録 ---

$existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($existing -and -not $Force) {
    # バイナリは触らない(差し替えは update.ps1)。稼働中の exe をロックしないため、ここで Copy もしない。
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
    # sc.exe delete は非同期。削除完了(Get-Service が消える)までポーリングする。
    $deadline = (Get-Date).AddSeconds(15)
    while ((Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) -and (Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 500
    }
    if (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) {
        throw "サービス '$ServiceName' の削除完了を待てませんでした。少し待って再実行してください。"
    }
}

# バイナリ配置(この時点でサービスは停止・削除済み → exe ロックなし)
New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null
Copy-Item -Path $ExeSource -Destination $ExeTarget -Force
Write-Host "[OK] radirec.exe を配置しました"

# NAS(UNC)へ書くため run-as = 自分のアカウント。マップ済み Z: はサービスから不可視なので UNC 直参照。
Grant-ServiceLogonRight -Account $ServiceAccount
$cred = Get-Credential -UserName $ServiceAccount -Message "サービス実行アカウントのパスワード(NAS アクセス用)"
$binPath = '"{0}" service --config "{1}"' -f $ExeTarget, $ConfigPath
New-Service -Name $ServiceName -BinaryPathName $binPath -StartupType Automatic -Credential $cred | Out-Null
Write-Host "[OK] サービス '$ServiceName' を登録しました"

Write-Host ""
Write-Host "設定を確認・編集してください: $ConfigPath" -ForegroundColor Cyan
Write-Host "編集後、サービスを開始: Start-Service $ServiceName" -ForegroundColor Cyan
