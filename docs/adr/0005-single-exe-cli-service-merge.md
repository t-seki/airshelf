# CLI と Service を単一 exe に統合する

Status: accepted / amended by [ADR-0012](0012-retire-service-for-tray-resident-per-user.md)(#16 配布・インストール整備。従来の 2 exe 構成 `Radirec.Cli`(net10.0) + `Radirec.Service`(net10.0-windows)を 1 つの `radirec.exe` に集約)。**ADR-0012 で `service` verb を廃止**し、`radirec.exe` は dev/batch 単発 CLI 専用に縮小。常駐はトレイ(`Radirec.Gui`)へ移設。**[ADR-0022](0022-rename-product-to-airshelf.md)(2026-10-08)で製品名を Airshelf に改め、CLI は `airshelf.exe`、トレイは `Airshelf.Gui` になった(ここと本文の名前は当時のもの)**。

配布・インストールを Release + スクリプトに閉じ込めるにあたり、成果物が 2 つ(CLI と Service)あることが構造的な事故源だった。両者は共有ライブラリ `Radirec.Hosting` / `Radirec.Core` を参照するが、**別々に publish される**ため、片方だけ差し替えると Core のバージョンがズレて取り込みロジックとサービスの挙動が食い違いうる。install/update スクリプトも 2 バイナリぶんの差し替えを冪等に扱う必要があり複雑になる。

よって CLI と Service を **1 つの自己完結 exe `radirec.exe`** に統合し、verb で分岐する:

- `radirec.exe service` — Windows サービス常駐(`Microsoft.Extensions.Hosting.WindowsServices`)
- `radirec.exe ingest` / `radirec.exe refresh` — 対話 CLI

`Microsoft.Extensions.Hosting.WindowsServices` は同一バイナリでサービス常駐と対話実行を両立でき(`WindowsServiceLifetime` はサービス起動時のみ有効化)、トップレベルのコマンドルータは `Spectre.Console.Cli`、`service` verb のみ Host を起動する。TFM は `net10.0-windows` に寄せる(`Radirec.Core` は `net10.0` のまま)。

## Considered Options

- **(採用) 単一 exe + verb 集約** — 配布物が 1 つになり Core バージョン不一致事故が構造的に消える。update は 1 ファイル差し替えで冪等化が容易。CLI 実行時も `net10.0-windows` 自己完結物になりサイズは増えるが、本ツールは Windows ネイティブ実行が前提(Z: が WSL 不可視のため Windows 側で走る)なので実害なし。
- **2 exe のまま publish/CI だけ整える** — 変更は小さいが、バージョン不一致の温床と 2 バイナリぶんの install/update 冪等処理が残る。統合で得られる「配布物 1 つ」の単純さを捨てることになる。

## なぜ WSL 開発ループを壊さないか(検証済み)

懸念は「CLI(現行 `net10.0`・クロスプラットフォーム)を `net10.0-windows` に寄せると WSL の build/test ループが壊れないか」だった。この環境は WSL で開発し Windows で実行する制約を持つ。実測で確認済み:

- `net10.0-windows` の `Radirec.Service` は **WSL 上で 0 error ビルド**(Windows Desktop targeting pack は NuGet ref pack として復元されるため Linux でもビルド可)。
- `win-x64 --self-contained --PublishSingleFile` は WSL からクロス publish でき、**単一 exe(素の .dll ゼロ)**が生成される。

`Radirec.Core`(テストのターゲット)は `net10.0` を維持するため、テストループも不変。

## Consequences

- プロジェクト構成の再編(`Radirec.Cli` / `Radirec.Service` を統合、`Radirec.Hosting` は共有のまま)。
- `Directory.Build.props` の共通 TFM か、統合 exe の TFM を `net10.0-windows` に。`Radirec.Core` は `net10.0` を明示維持。
- サービス登録は `radirec.exe service --config <絶対パス>` の形になる([ADR-0006](0006-binary-data-separation-and-path-resolution.md))。
- 配布物・Release アセットが `radirec.exe` 1 本に集約される。
