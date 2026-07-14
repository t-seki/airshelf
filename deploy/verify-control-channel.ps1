#Requires -Version 5.1
<#
.SYNOPSIS
  制御チャネル(ADR-0010 / #33)を Windows 実機で検証する。

.DESCRIPTION
  WSL では named pipe の ACL・実サービスの listener+worker 疎通・SQLite 再オープンが構造的に検証できない
  (PipeSecurity は PlatformNotSupported)。本スクリプトは稼働中の Radirec サービスに対し、ADR-0010 の
  Consequences ①〜⑥ のうち自動化できる項目を検査し、PASS/FAIL/SKIP を集計する。**操作者本人のセッション**
  (= 既定インストールではサービス実行アカウント)で実行すること。

  検査項目:
    ① status/rescan/reload が稼働サービスに繋がり応答する
    ② named pipe の ACL に広いアクセス(Everyone/Users/Authenticated Users)が無い(ACL 外拒否の代理検査)
    ③ reload で RadioRoot 変更が反映され、不正 config は拒否され旧構成が維持される(-SkipDestructive で省略)
    ⑤ listener が status 連打(pipe の dispose→再生成 churn)で永久停止しない
    ⑥ 監視ルートへのファイル着信 in-flight 中の reload でサービスが落ちない(-SkipDestructive で省略)

  自動化できない項目は runbook(docs/control-channel-verification.md)の手動手順で確認する:
    ② 別ユーザーからの接続拒否(第 2 アカウントが要る)
    ④ split-privilege(-ServiceAccount 専用アカウント)構成での操作者接続(#40)

  非破壊: radirec.json は生バイト列でバックアップし、必ず finally で原状復帰する。

.PARAMETER ExeTarget
  radirec.exe のパス。既定 'C:\Program Files\radirec\radirec.exe'。

.PARAMETER DataDir
  データディレクトリ。既定 '%ProgramData%\radirec'(radirec.json の場所)。

.PARAMETER ServiceName
  Windows サービス名。既定 'Radirec'。

.PARAMETER PipeName
  制御チャネルの pipe 名。既定 'radirec-control'。

.PARAMETER SkipDestructive
  radirec.json の一時変更やファイル投入を伴う ③⑥ を省略する。
#>
[CmdletBinding()]
param(
    [string]$ExeTarget = "$env:ProgramFiles\radirec\radirec.exe",
    [string]$DataDir = "$env:ProgramData\radirec",
    [string]$ServiceName = 'Radirec',
    [string]$PipeName = 'radirec-control',
    [switch]$SkipDestructive
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$ConfigPath = Join-Path $DataDir 'radirec.json'
$script:Results = [System.Collections.Generic.List[object]]::new()

function Add-Result([string]$Id, [string]$Name, [string]$Status, [string]$Detail = '') {
    $color = switch ($Status) { 'PASS' { 'Green' } 'FAIL' { 'Red' } 'SKIP' { 'Yellow' } default { 'Gray' } }
    Write-Host ("[{0}] {1} {2}" -f $Status, $Id, $Name) -ForegroundColor $color
    if ($Detail) { Write-Host ("       {0}" -f $Detail) -ForegroundColor DarkGray }
    $script:Results.Add([pscustomobject]@{ Id = $Id; Name = $Name; Status = $Status; Detail = $Detail })
}

# radirec.exe の verb を実行し、終了コードと標準出力(+エラー)をまとめて返す。
function Invoke-Radirec([string[]]$RadirecArgs) {
    $output = (& $ExeTarget @RadirecArgs 2>&1 | Out-String)
    return [pscustomobject]@{ ExitCode = $LASTEXITCODE; Output = $output }
}

# status の出力から表示行(監視ルート等)を取り出す簡易パーサ(表描画の値部分を拾う)。
function Get-StatusText() {
    $r = Invoke-Radirec @('status')
    return [pscustomobject]@{ Ok = ($r.ExitCode -eq 0); Text = $r.Output }
}

# Spectre の表は長い値を折り返す(改行 + 縦罫線 │ + 余白を挿入)。空白と縦罫線を除去して折返しで
# 分断されたトークンを連結し直してから照合する(トークン自体は空白を含まない前提)。
function Test-StatusContains([string]$Text, [string]$Token) {
    $flat = ($Text -replace '[\s│┃｜|]', '')
    return $flat -match [regex]::Escape($Token)
}

Write-Host "=== 制御チャネル検証(ADR-0010 / #33)===" -ForegroundColor Cyan
Write-Host "exe: $ExeTarget"
Write-Host "config: $ConfigPath"
Write-Host ""

# ---- 事前チェック --------------------------------------------------------------------------------
if (-not (Test-Path $ExeTarget)) {
    Add-Result 'PRE' 'radirec.exe が存在する' 'FAIL' "見つかりません: $ExeTarget"
    Write-Host "`n事前チェック失敗。中止します。" -ForegroundColor Red
    exit 1
}
$svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($null -eq $svc) {
    Add-Result 'PRE' "サービス '$ServiceName' が登録済み" 'FAIL' 'install.ps1 でインストールしてください'
    exit 1
}
if ($svc.Status -ne 'Running') {
    Add-Result 'PRE' "サービス '$ServiceName' が Running" 'FAIL' "現在: $($svc.Status)。Start-Service $ServiceName で起動してください"
    exit 1
}
Add-Result 'PRE' "サービス '$ServiceName' が Running" 'PASS'

$pipePath = "\\.\pipe\$PipeName"
# named pipe の存在確認は情報提供のみ(FAIL にしない)。実接続の可否は下記 ① status が確証する。
# 列挙 API はランタイム差で throw/空を返しうるため、判定不能は SKIP に倒す(誤 FAIL→exit 1 を避ける)。
$pipeExists = $null
try {
    $pipeExists = [bool](@([System.IO.Directory]::GetFiles('\\.\pipe\')) |
        Where-Object { [System.IO.Path]::GetFileName($_) -eq $PipeName })
} catch { $pipeExists = $null }
if ($pipeExists -eq $true) {
    Add-Result 'PRE' "pipe '$PipeName' が存在する" 'PASS'
} else {
    Add-Result 'PRE' "pipe '$PipeName' の存在" 'SKIP' "列挙で確認できず(① status の実接続で確証する)"
}

# ---- ① status/rescan/reload が応答する ----------------------------------------------------------
$st = Invoke-Radirec @('status')
if ($st.ExitCode -eq 0 -and $st.Output -match '監視ルート') {
    Add-Result '1' 'status が応答する' 'PASS'
} else {
    Add-Result '1' 'status が応答する' 'FAIL' "exit=$($st.ExitCode) out=$($st.Output.Trim())"
}

$rs = Invoke-Radirec @('rescan')
if ($rs.ExitCode -eq 0 -and $rs.Output -match '再スキャン') {
    Add-Result '1' 'rescan が応答する' 'PASS'
} else {
    Add-Result '1' 'rescan が応答する' 'FAIL' "exit=$($rs.ExitCode) out=$($rs.Output.Trim())"
}

$rl = Invoke-Radirec @('reload')
if ($rl.ExitCode -eq 0 -and $rl.Output -match '受理') {
    Add-Result '1' 'reload が応答する(現行 config の再読込)' 'PASS'
} else {
    Add-Result '1' 'reload が応答する' 'FAIL' "exit=$($rl.ExitCode) out=$($rl.Output.Trim())"
}

# ---- ② 認可(ローカル同一ユーザー限定)------------------------------------------------------------
# named pipe の DACL は PowerShell から確実に読めない(Get-Acl は pipe パス非対応で常に失敗し、
# 成功しても SID/日本語グループ名の取りこぼしで誤 PASS になりうる)。よって自動判定はせず、
# **別ユーザーからの接続拒否**を runbook の手動手順で確認する(②の本体)。
Add-Result '2' '認可(別ユーザーからの接続拒否)' 'SKIP' 'docs/control-channel-verification.md の手動手順 ② で確認(自動 ACL 検査は不可)'

# ---- ③ reload の反映 / 不正 config の拒否+旧構成維持 --------------------------------------------
if ($SkipDestructive) {
    Add-Result '3' 'reload 反映 / 不正 config 拒否' 'SKIP' '-SkipDestructive 指定'
} elseif (-not (Test-Path $ConfigPath)) {
    Add-Result '3' 'reload 反映 / 不正 config 拒否' 'SKIP' "config が既定パスに無い: $ConfigPath"
} else {
    $backup = [System.IO.File]::ReadAllBytes($ConfigPath)  # 生バイトでバックアップ(コメント等を壊さない)
    # leaf は短い一意トークン。status(Spectre の表)は長いパスを折り返すため、フルパスでなく leaf で照合する。
    $leaf = 'radirec-vfy-' + [guid]::NewGuid().ToString('N').Substring(0, 6)
    $tempRoot = Join-Path $env:TEMP $leaf
    try {
        $parsed = $null
        try { $parsed = Get-Content -Raw -Path $ConfigPath | ConvertFrom-Json -ErrorAction Stop } catch { $parsed = $null }

        if ($null -eq $parsed) {
            Add-Result '3a' 'reload で RadioRoot 変更が反映される' 'SKIP' 'radirec.json がコメント等で自動パース不可。runbook の手動手順で確認'
        } else {
            New-Item -ItemType Directory -Path $tempRoot -Force | Out-Null
            $parsed.RadioRoot = $tempRoot
            ($parsed | ConvertTo-Json -Depth 20) | Set-Content -Path $ConfigPath -Encoding UTF8

            $applied = $false
            $r = Invoke-Radirec @('reload')
            if ($r.ExitCode -eq 0) {
                for ($i = 0; $i -lt 20; $i++) {   # 反映は在中スキャン完了後(後追い)。最大 ~10 秒ポーリング。
                    Start-Sleep -Milliseconds 500
                    if (Test-StatusContains (Invoke-Radirec @('status')).Output $leaf) { $applied = $true; break }
                }
            }
            if ($applied) {
                Add-Result '3a' 'reload で RadioRoot 変更が status に反映される' 'PASS'
            } else {
                Add-Result '3a' 'reload で RadioRoot 変更が反映される' 'FAIL' "status が新ルート($tempRoot)を反映しない"
            }

            # 不正 config → 拒否 + サービス生存 + 旧構成(この時点の tempRoot)維持
            "{ this is not valid json " | Set-Content -Path $ConfigPath -Encoding UTF8
            $bad = Invoke-Radirec @('reload')
            $svcNow = Get-Service -Name $ServiceName
            $stAfter = Invoke-Radirec @('status')
            $rejected = ($bad.ExitCode -ne 0 -and $bad.Output -match '拒否')
            $alive = ($svcNow.Status -eq 'Running' -and $stAfter.ExitCode -eq 0)
            $kept = (Test-StatusContains $stAfter.Output $leaf)
            if ($rejected -and $alive -and $kept) {
                Add-Result '3b' '不正 config は拒否・サービス生存・旧構成維持' 'PASS'
            } else {
                Add-Result '3b' '不正 config は拒否・サービス生存・旧構成維持' 'FAIL' "rejected=$rejected alive=$alive kept=$kept"
            }
        }
    } finally {
        [System.IO.File]::WriteAllBytes($ConfigPath, $backup)   # 原状復帰(生バイト)
        Invoke-Radirec @('reload') | Out-Null                    # 元 config を再適用
        if (Test-Path $tempRoot) { Remove-Item -Recurse -Force $tempRoot -ErrorAction SilentlyContinue }
        Write-Host "       radirec.json を原状復帰し reload しました。" -ForegroundColor DarkGray
    }
}

# ---- ⑤ listener が status 連打(pipe churn)で永久停止しない -------------------------------------
$churnFail = 0
for ($i = 0; $i -lt 30; $i++) {
    if ((Invoke-Radirec @('status')).ExitCode -ne 0) { $churnFail++ }
}
if ($churnFail -eq 0) {
    Add-Result '5' 'listener が status 連打(pipe 再生成 churn)で生存' 'PASS' '30/30 応答'
} else {
    Add-Result '5' 'listener が status 連打で生存' 'FAIL' "$churnFail/30 が失敗(listener 停止の疑い)"
}

# ---- ⑥ ファイル着信 in-flight 中の reload でサービスが落ちない -----------------------------------
if ($SkipDestructive) {
    Add-Result '6' 'ファイルイベント in-flight 中の reload で生存' 'SKIP' '-SkipDestructive 指定'
} else {
    $status = Get-StatusText
    # 監視ルートは status 出力(折返しうる表)ではなく config から取得する。
    $dropRoot = $null
    try { $dropRoot = (Get-Content -Raw -Path $ConfigPath | ConvertFrom-Json -ErrorAction Stop).RadioRoot } catch { $dropRoot = $null }
    # Watching 中でなければ watcher コールバックを誘発できないため SKIP。
    if ($status.Text -notmatch '監視中') {
        Add-Result '6' 'ファイルイベント in-flight 中の reload で生存' 'SKIP' '監視ルート不在(定期スキャンのみ)で watcher を誘発できない'
    } else {
        $job = $null
        $probeDir = if ($dropRoot) { Join-Path $dropRoot '.radirec-verify' } else { $null }
        try {
            if (-not $dropRoot -or -not (Test-Path $dropRoot)) {
                Add-Result '6' 'ファイルイベント in-flight 中の reload で生存' 'SKIP' "監視ルートを特定/アクセスできない: $dropRoot"
            } else {
                # 監視ルートへ junk ファイルを生成しつづける背景ジョブ(watcher を継続発火させる)。
                # per-iteration の削除はせず、warm-up が生成開始を確実に観測できるようにする。
                $job = Start-Job -ScriptBlock {
                    param($d)
                    New-Item -ItemType Directory -Path $d -Force | Out-Null
                    for ($k = 0; $k -lt 200; $k++) {   # ~12s。warm-up + reload を十分に上回る。
                        Set-Content -Path (Join-Path $d "probe-$k.tmp") -Value 'x' -ErrorAction SilentlyContinue
                        Start-Sleep -Milliseconds 60
                    }
                } -ArgumentList $probeDir

                # warm-up: ジョブが実際にファイルを生成し始める(= watcher イベントが流れる)まで待つ。
                # 生成が始まらないまま reload を回すと in-flight 条件を作れず false PASS になるため、
                # 開始を確認できなければ PASS ではなく SKIP に倒す。
                $active = $false
                for ($w = 0; $w -lt 40; $w++) {   # 最大 ~4s
                    Start-Sleep -Milliseconds 100
                    if ((Test-Path $probeDir) -and
                        @(Get-ChildItem -Path $probeDir -Filter 'probe-*.tmp' -ErrorAction SilentlyContinue).Count -gt 0) {
                        $active = $true; break
                    }
                }

                if (-not $active) {
                    Add-Result '6' 'ファイルイベント in-flight 中の reload で生存' 'SKIP' '背景ファイル生成が始まらず in-flight 条件を作れなかった(false PASS 回避)'
                } else {
                    # ファイル着信が継続している最中に reload を複数回。
                    for ($i = 0; $i -lt 4; $i++) { Invoke-Radirec @('reload') | Out-Null; Start-Sleep -Milliseconds 500 }

                    $svcNow = Get-Service -Name $ServiceName
                    $stAfter = Invoke-Radirec @('status')
                    if ($svcNow.Status -eq 'Running' -and $stAfter.ExitCode -eq 0) {
                        Add-Result '6' 'ファイルイベント in-flight 中の reload で生存' 'PASS'
                    } else {
                        Add-Result '6' 'ファイルイベント in-flight 中の reload で生存' 'FAIL' "svc=$($svcNow.Status) status.exit=$($stAfter.ExitCode)"
                    }
                }
            }
        } finally {
            if ($job) { Stop-Job $job -ErrorAction SilentlyContinue; Remove-Job $job -Force -ErrorAction SilentlyContinue }
            if ($probeDir -and (Test-Path $probeDir)) { Remove-Item -Recurse -Force $probeDir -ErrorAction SilentlyContinue }
        }
    }
}

# ---- 集計 --------------------------------------------------------------------------------------
Write-Host ""
Write-Host "=== 集計 ===" -ForegroundColor Cyan
$pass = @($script:Results | Where-Object Status -eq 'PASS').Count
$fail = @($script:Results | Where-Object Status -eq 'FAIL').Count
$skip = @($script:Results | Where-Object Status -eq 'SKIP').Count
Write-Host ("PASS={0}  FAIL={1}  SKIP={2}" -f $pass, $fail, $skip)
Write-Host "手動確認(自動化不可): ② 別ユーザー接続拒否 / ④ split-privilege(#40)。docs/control-channel-verification.md 参照。" -ForegroundColor DarkGray

if ($fail -gt 0) { exit 1 } else { exit 0 }
