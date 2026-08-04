# 運用者向け配布は PowerShell スクリプト + GitHub Release で行う(MSI/Inno は採らない)

Status: accepted / superseded by [ADR-0012](0012-retire-service-for-tray-resident-per-user.md) / [ADR-0013](0013-velopack-per-user-updates.md)(#16 配布・インストール整備。GUI インストーラ(Inno Setup)は M5 / #5 へ先送り)。サービス撤去で `New-Service -Credential` / `SeServiceLogonRight` / admin install は廃止、更新は Velopack per-user 自己更新(ADR-0013)へ。**#75 まで `deploy/install.ps1` / `update.ps1` + admin install が現行の配布手段だった**(両スクリプトは #75 で削除。現行の配布は Velopack の Setup.exe)。

導入・更新・削除を「Release を落として `install.ps1` を管理者実行」で完結させるにあたり、インストーラ技術として PowerShell スクリプト・WiX(MSI)・Inno Setup を比較した。#16 の対象者は**運用者本人(管理者)・1〜数台**であり、非技術者向けのダブルクリック配布ではない。

よって #16 は **`deploy/*.ps1`(install / update / uninstall)+ GitHub Release アセット**で配布する。GUI インストーラ(Inno Setup)は対象が非技術者に変わる M5(#5)で、本 issue の publish/Release を土台に**被せる**。WiX(MSI)は企業/GPO による無人配布が要件化した場合のみ検討する。

## Considered Options

- **(採用) PowerShell スクリプト + Release** — 決め手は 3 点:
  1. **run-as self の対話認証**: サービスは NAS(UNC)へ書くため自分のアカウント(`.\abada`)で実行する必要があり、`New-Service -Credential (Get-Credential)` で対話的にパスワードを取得できる。MSI/Inno はアカウント/パスワードの受け渡しが不格好で秘密情報の扱いも悪化する。
  2. **命令的な条件ロジック**: config を無ければ生成・あれば温存、WAL 込みの台帳退避、EventLog ソースの冪等登録 — いずれも条件分岐の命令処理で、宣言的 MSI ではカスタムアクション送りになり冗長。
  3. **CI が Windows 専用ツールチェーン不要**: 成果物は素の `.ps1` で、Linux/WSL からでも zip に同梱するだけ。Inno コンパイラは Windows 専用 exe で CI を制約する。
- **WiX(MSI)** — 多数台への GPO 無人配布・Add/Remove Programs への正式登録が要件なら真価を出すが、#16 の要件はほぼ全て MSI ネイティブの外(カスタムアクション)に出るため、命令処理と宣言儀式の**両方のコストを払う worst-of-both** になる。
- **Inno Setup** — 非技術者向けウィザード UX に優れ M5 の狙いに合うが、コンパイラが Windows 専用で CI を制約し、対象が運用者本人の #16 ではオーバーキル。

## Consequences

- `install.ps1` / `update.ps1` / `uninstall.ps1` を冪等に実装する。install=初回 or `-Force` のみ `-Credential` 対話(既存なら登録スキップ)、update=無認証で 1 ファイル差し替え・停止起動(未登録ならエラー)、uninstall=停止・削除・EventLog ソース解除(データは既定で保持・`-PurgeData` で削除)。
- コード署名(Authenticode)/ SmartScreen 対策は #16 では非目標(広く配る M5 段階で)。
- M5 で Inno を被せる際、#16 の単一 exe publish([ADR-0005](0005-single-exe-cli-service-merge.md))と Release がそのまま土台になる。
