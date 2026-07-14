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
# named pipe の存在は Test-Path が不安定なため、pipe 名前空間を列挙して確認する。
$pipeExists = $false
try {
    $pipeExists = [bool](@([System.IO.Directory]::GetFiles('\\.\pipe\')) |
        Where-Object { [System.IO.Path]::GetFileName($_) -eq $PipeName })
} catch { $pipeExists = $false }
if ($pipeExists) {
    Add-Result 'PRE' "pipe '$PipeName' が存在する" 'PASS'
} else {
    Add-Result 'PRE' "pipe '$PipeName' が存在する" 'FAIL' "listener が listen していない可能性: $pipePath"
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

# ---- ② pipe ACL に広いアクセスが無い ------------------------------------------------------------
try {
    $acl = Get-Acl -Path $pipePath -ErrorAction Stop
    $broad = $acl.Access | Where-Object {
        $_.AccessControlType -eq 'Allow' -and
        ($_.IdentityReference.Value -in @('Everyone', 'BUILTIN\Users', 'NT AUTHORITY\Authenticated Users'))
    }
    if ($broad) {
        Add-Result '2' 'pipe ACL に広いアクセスが無い' 'FAIL' ("許可: " + (($broad | ForEach-Object { $_.IdentityReference.Value }) -join ', '))
    } else {
        $ids = ($acl.Access | ForEach-Object { $_.IdentityReference.Value }) -join ', '
        Add-Result '2' 'pipe ACL に広いアクセスが無い(自 SID 限定の代理検査)' 'PASS' "許可対象: $ids"
    }
    Write-Host "       別ユーザーからの実接続拒否(②の本体)は runbook の手動手順で確認。" -ForegroundColor DarkGray
} catch {
    Add-Result '2' 'pipe ACL を検査' 'SKIP' "Get-Acl 失敗: $($_.Exception.Message)"
}

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
                    if ((Invoke-Radirec @('status')).Output -match [regex]::Escape($leaf)) { $applied = $true; break }
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
            $kept = ($stAfter.Output -match [regex]::Escape($leaf))
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
        try {
            if (-not $dropRoot -or -not (Test-Path $dropRoot)) {
                Add-Result '6' 'ファイルイベント in-flight 中の reload で生存' 'SKIP' "監視ルートを特定/アクセスできない: $dropRoot"
            } else {
                # 監視ルートへ junk ファイルを高速に生成/削除しつづける背景ジョブ(watcher を継続発火させる)。
                $job = Start-Job -ScriptBlock {
                    param($root)
                    $dir = Join-Path $root '.radirec-verify'
                    New-Item -ItemType Directory -Path $dir -Force | Out-Null
                    for ($k = 0; $k -lt 60; $k++) {
                        $f = Join-Path $dir "probe-$k.tmp"
                        Set-Content -Path $f -Value 'x' -ErrorAction SilentlyContinue
                        Start-Sleep -Milliseconds 80
                        Remove-Item -Path $f -Force -ErrorAction SilentlyContinue
                    }
                    Remove-Item -Recurse -Force $dir -ErrorAction SilentlyContinue
                } -ArgumentList $dropRoot

                for ($i = 0; $i -lt 4; $i++) { Invoke-Radirec @('reload') | Out-Null; Start-Sleep -Milliseconds 700 }

                $svcNow = Get-Service -Name $ServiceName
                $stAfter = Invoke-Radirec @('status')
                if ($svcNow.Status -eq 'Running' -and $stAfter.ExitCode -eq 0) {
                    Add-Result '6' 'ファイルイベント in-flight 中の reload で生存' 'PASS'
                } else {
                    Add-Result '6' 'ファイルイベント in-flight 中の reload で生存' 'FAIL' "svc=$($svcNow.Status) status.exit=$($stAfter.ExitCode)"
                }
            }
        } finally {
            if ($job) { Stop-Job $job -ErrorAction SilentlyContinue; Remove-Job $job -Force -ErrorAction SilentlyContinue }
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
