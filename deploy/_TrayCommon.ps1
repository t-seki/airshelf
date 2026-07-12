<#
.SYNOPSIS
  install/update/uninstall.ps1 が共有する取込通知トレイ(Radirec.Gui)の定数とヘルパ(ADR-0009)。

.DESCRIPTION
  各スクリプトの先頭で `. (Join-Path $PSScriptRoot '_TrayCommon.ps1')` として dot-source する。

  【単一ユーザー homelab 前提】これらのスクリプトは #Requires -RunAsAdministrator で昇格実行される。
  昇格しても HKCU / 対話セッションは「スクリプトを起動したユーザー」のものになる。よって
  「操作者 = 通知を受けるユーザー」を前提とする(既定の ServiceAccount = .\<現在のユーザー> と同じ想定)。
  管理者専用アカウントで runas する split-privilege 構成では、トレイの自動起動登録/解除は
  通知ユーザー自身のセッションから行う必要がある(その場合は各ユーザーで一度トレイを起動する)。
#>

$GuiProcessName = 'Radirec.Gui'
$GuiExeName = 'Radirec.Gui.exe'
$GuiRunKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$GuiRunValue = 'RadirecTray'

# 操作者と同一セッションのトレイプロセスのみを対象にする(昇格実行が他ユーザーの
# セッションのトレイを巻き添えに kill しないため。F2)。
function Get-TrayProcess {
    $sid = (Get-Process -Id $PID).SessionId
    Get-Process -Name $GuiProcessName -ErrorAction SilentlyContinue |
        Where-Object { $_.SessionId -eq $sid }
}

function Test-TrayRunning {
    return [bool]@(Get-TrayProcess).Count
}

# 同一セッションのトレイを停止し、exe ロック解放まで待つ(直後の Copy-Item 競合を防ぐ。F4)。
function Stop-Tray {
    $procs = @(Get-TrayProcess)
    foreach ($p in $procs) {
        try {
            $p | Stop-Process -Force
            $p.WaitForExit(5000) | Out-Null
        } catch {
            Write-Warning "トレイの停止に失敗しました($_)"
        }
    }
}

# ログオン自動起動を操作者の HKCU Run へ冪等登録する(アプリ側の自己登録 ADR-0009 と等価)。
function Register-TrayAutostart {
    param([Parameter(Mandatory)][string]$ExePath)
    Set-ItemProperty -Path $GuiRunKey -Name $GuiRunValue -Value ('"{0}"' -f $ExePath)
}

function Unregister-TrayAutostart {
    if (Get-ItemProperty -Path $GuiRunKey -Name $GuiRunValue -ErrorAction SilentlyContinue) {
        Remove-ItemProperty -Path $GuiRunKey -Name $GuiRunValue
        return $true
    }
    return $false
}
