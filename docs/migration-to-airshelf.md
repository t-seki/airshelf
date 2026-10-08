# 移行の手順書: Radirec → Airshelf(一度きり)

製品名を Radirec から Airshelf に改めた([ADR-0022](adr/0022-rename-product-to-airshelf.md)、#256)。Velopack の packId も
`Radirec` → `Airshelf` に変わったので、Velopack 上は別のアプリになり、Radirec から Airshelf へは**更新では移れない**。
データのフォルダも `%LOCALAPPDATA%\RadirecData\` → **`%LOCALAPPDATA%\AirshelfData\`** に変わる。既存の環境を移す
**一度きり**の手順。自動で移すコードは無い(ADR-0022)。**新しく入れる人はこの手順書は要らない**(`Airshelf-win-Setup.exe` を
実行するだけ)。

> ⚠️ **台帳(`ledger.db`)= import-once の権威**。移し損ねると次の走査で全件が「未取込」になり、
> NAS へ全部コピーし直す。必ず台帳を運ぶこと。
>
> ⚠️ **データを移してから Setup を入れる**(手順の順序を入れ替えない)。Setup は導入後にアプリを起動し、
> `AirshelfData` に設定が無ければ sample から空の設定を置く(`ConfigSeeder`。既存のファイルは上書きしない)。
> 先に Setup を入れると、空の設定で起動する(sample の `RadioRoot` / `OutputRoot` は空なので取り込みは走らないが、
> そのあと移したファイルとの区別が面倒になる)。

前提: Airshelf の最初の版が [Releases](https://github.com/t-seki/airshelf/releases) に出ている。
旧 admin 配置(`%ProgramData%\radirec` + Windows サービス)のままなら、先に
[migration-to-velopack.md](migration-to-velopack.md) で `%LOCALAPPDATA%\RadirecData\` へ移しておく。

## 手順

1. **トレイ(旧 `Radirec.Gui.exe`)を止める**(SQLite を閉じる)。トレイメニューの「終了」か:
   ```powershell
   Get-Process Radirec.Gui -ErrorAction SilentlyContinue | Stop-Process -Force
   ```

2. **`%LOCALAPPDATA%\RadirecData` を `%LOCALAPPDATA%\AirshelfData` にコピーする**(停止後なら `-wal` / `-shm` 込みで安全)。
   旧フォルダは控えとして**残す**(移動ではなくコピー):
   ```powershell
   $src = "$env:LOCALAPPDATA\RadirecData"
   $dst = "$env:LOCALAPPDATA\AirshelfData"
   New-Item -ItemType Directory -Force $dst | Out-Null
   Copy-Item "$src\*" $dst -Recurse -Force    # ledger.db(+ -wal/-shm)・radirec.json・stations.json・programs.json・gui-settings.json・gui.log
   ```

3. **`radirec.json` を `airshelf.json` に改名し、中の絶対パスを直す**:
   ```powershell
   Rename-Item "$dst\radirec.json" airshelf.json
   Select-String -Path "$dst\airshelf.json" -Pattern 'RadirecData'   # 旧フォルダを指す行が残っていないか
   ```
   `LedgerPath` / `StationsFile` / `ProgramsFile` などが絶対パスで `RadirecData` を指していれば、
   `AirshelfData` に直すか、裸のファイル名(相対。`airshelf.json` のあるフォルダ基準で解決される。ADR-0006)にする。
   直さないと、Airshelf が旧フォルダの台帳を使い続ける。`RadioRoot` / `OutputRoot` / Jellyfin などの絶対パス
   (`I:` / NAS の UNC)はそのままでよい。

4. **旧 Radirec をアンインストールする**: 設定 → アプリ →「Radirec」→ アンインストール
   (または `%LOCALAPPDATA%\Radirec\Update.exe --uninstall`)。データのフォルダ(`RadirecData`)は消えない。
   旧版はアンインストールの前に自分でログオン自動起動の Run 値 `RadirecTray` を消す(`StartupRegistrar.Unregister`
   を Velopack の `OnBeforeUninstallFastCallback` から呼ぶ)ので、ふつうは残らない。念のため確かめ、残っていれば手で消す:
   ```powershell
   $run = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
   Get-ItemProperty -Path $run -Name RadirecTray -ErrorAction SilentlyContinue      # 何も出なければ消えている
   Remove-ItemProperty -Path $run -Name RadirecTray -ErrorAction SilentlyContinue   # 残っていたら消す
   ```

5. **`Airshelf-win-Setup.exe` を入れる**: [Releases](https://github.com/t-seki/airshelf/releases) から落として実行
   (**管理者不要**)。`%LOCALAPPDATA%\Airshelf` へ導入し、ログオン自動起動(Run 値 `AirshelfTray`)を登録して起動する。

6. **確かめる**:
   - トレイが起動する(アイコンのツールチップとメニューの版の行が `Airshelf`)。
   - 取り込みが動く。管理画面(トレイの「Airshelf を開く」)の取込履歴ページに**過去の取込が並べば台帳を運べている**
     (空なら手順 2 / 3 を見直す)。起動時の走査で新規 0 件(全部「済」)なら import-once が効いている。
   - 更新の確認が `t-seki/airshelf` の Release を見に行く(管理画面のバージョン情報ページの「今すぐ更新を確認」が
     エラーにならない)。更新フィードを読むトークン(`UpdateToken`)は `airshelf.json` に引き継がれている。
   - ログが `%LOCALAPPDATA%\AirshelfData\gui.log` に出る(`RadirecData\gui.log` には増えない)。

7. **(任意)旧フォルダを消す**: 上の確かめが済み、しばらく運用して問題が無ければ、控えの
   `%LOCALAPPDATA%\RadirecData` を人が消す。急がない(消すまでは手順 2 からやり直せる)。
