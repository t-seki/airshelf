# 移行 runbook: 旧 admin 配置 → Velopack per-user(一度きり)

S5(ADR-0013)で配布が **Velopack per-user(Setup.exe・非 admin)** に変わり、データが
`%ProgramData%\radirec\` → **`%LOCALAPPDATA%\radirec\`** へ移る。既存の box を新モデルへ移す**一度きり**の
手順。**新規ユーザーはこの runbook 不要**(Setup.exe を実行するだけ。`%ProgramData%` に何も無い)。

> ⚠️ **台帳(`ledger.db`)= import-once の権威**。移行し損ねると次回スキャンで全件が「未取込」判定になり
> NAS へ全再コピーが走る。必ず台帳を運ぶこと。
>
> ⚠️ **Setup.exe は導入後アプリを自動起動する**。よって**データ移行(手順 1〜4)を Setup.exe(手順 5)より
> 先に**行うこと。順序を誤ると、空の `%LOCALAPPDATA%\radirec` にアプリが初回 seed した設定で起動する。
> ただし**出荷 sample の `RadioRoot`/`OutputRoot` は空(inert)**なので、万一先に起動しても取り込みは
> 走らない(安全網。§14)。実データを扱うのは移行済み config を置いた後だけ。

前提: v0.8.0(Velopack)以降が発行済み。

## 手順

1. **現行トレイ(と旧サービスがあれば)を停止**(SQLite を閉じる):
   ```powershell
   Get-Process Radirec.Gui -ErrorAction SilentlyContinue | Stop-Process -Force
   # 旧 Windows サービスが残っていれば(通常は S2 移行時に退役済み)
   Get-Service Radirec -ErrorAction SilentlyContinue | Stop-Service -Force
   ```

2. **データを %ProgramData% → %LOCALAPPDATA% へコピー**(停止後なら `-wal`/`-shm` 込みで安全):
   ```powershell
   $src = "$env:ProgramData\radirec"
   $dst = "$env:LOCALAPPDATA\radirec"
   New-Item -ItemType Directory -Force $dst | Out-Null
   Copy-Item "$src\*" $dst -Recurse -Force    # ledger.db(+ -wal/-shm)・radirec.json・stations.json・programs.json
   ```

3. **`radirec.json` の相対パスを確認**: `LedgerPath` / `StationsFile` / `ProgramsFile` が
   **絶対パスで旧 `%ProgramData%\radirec\...` を指している場合は、裸のファイル名(相対)に直す**
   (設定ディレクトリ = 新 DataRoot 基準で解決される。ADR-0006)。例:
   ```jsonc
   "LedgerPath": "ledger.db", "StationsFile": "stations.json", "ProgramsFile": "programs.json"
   ```
   `RadioRoot` / `OutputRoot` / Jellyfin 等の絶対パス(`I:` / NAS UNC)はそのままで良い。

4. **旧配置を撤去**(Program Files の exe・旧 HKCU 自動起動・旧サービス/EventLog。**データは既定で温存**):
   ```powershell
   .\deploy\uninstall.ps1        # 旧 Program Files 配置 + 旧 'RadirecTray' 自動起動を掃除
   ```

5. **Velopack 版を導入**: v0.8.0 の GitHub Release から **`Setup.exe`** を落として実行(**admin 不要**)。
   `%LOCALAPPDATA%\Radirec` へ導入・自動起動登録・起動する。

6. **検証**: トレイが起動し、移行した `%LOCALAPPDATA%\radirec\radirec.json` + 台帳で稼働する。
   トレイメニュー「取込履歴…」に**過去の取込が並べば台帳移行成功**(空なら台帳が運べていない → 手順 2/3 を確認)。
   起動時 catch-up スキャンで**新規0件(全て既取込スキップ)**なら import-once が効いている証拠。

## 更新フィードのトークン(private repo)

本 repo は private なので、アプリの更新チェックには **contents:read のトークンが要る**(ADR-0013)。
`radirec.json` にトークンのフィールド(S6 で追加)を設定する。CI 側の発行は `GITHUB_TOKEN` で動くため不要。
