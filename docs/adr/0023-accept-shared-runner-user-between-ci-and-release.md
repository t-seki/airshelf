# PR の CI と release が同じ runner・同じ OS ユーザーで動くことを受け入れ、事故の汚れだけを release 側の一時領域で防ぐ

Status: accepted(#286 の grill、2026-10-09。[ADR-0013](0013-velopack-per-user-updates.md) の release の仕組み、#284 / PR #285 で PR の CI を母艦で回すことにしたのと関連)

## 背景

PR の CI(`ci.yml`)と release(`release.yml`)は、母艦 Windows PC の同じ永続 self-hosted runner
(`C:\actions-runner\radirec-tool\`)の同じ OS ユーザー(runner-svc)で動く(`docs/development.md` の「ビルドとテスト」)。
`DOTNET_INSTALL_DIR` は `runner.temp` に向けているが、NuGet のグローバルキャッシュ(`~/.nuget/packages`)と、
vpk を入れる dotnet tool の置き場(`%USERPROFILE%\.dotnet\tools`)は共有のままだった(PR #285 の 1 周目のレビュー。
構造は確かめたが、実害は確かめていない)。

PR の CI は、restore した NuGet パッケージの MSBuild ターゲットとテストのコードを動かす。そこで書き換えられた
キャッシュやツールを、次のタグ push の release(`contents: write` の `GITHUB_TOKEN` を持つ)がそのまま使いうる。

## 決定

- **`release.yml` の NuGet のグローバルパッケージフォルダと vpk を、ジョブごとの一時領域(`runner.temp`)の下に置く**。
  - `NUGET_PACKAGES` を `runner.temp` の下に向ける(`GITHUB_ENV` 経由で以降の全ステップに効かせる)。分けるのはこのフォルダだけ。
  - vpk は `dotnet tool update -g` をやめ、`dotnet tool update vpk --tool-path <runner.temp の下>` で入れて `GITHUB_PATH` に足す
    (置き場が空なら新しく入り、残っていても冪等)。
    版は `Directory.Packages.props` の Velopack の版を読んで渡す(#281 の決定のまま)。
  - `runner.temp` はジョブの初めと終わりに空になる、というのは GitHub の docs(runner コンテキストの `runner.temp` の説明)の記述で、
    この runner での実測ではない。
- **`ci.yml` は変えない**。release が CI の書いたものを読まなくなれば足りる。
- **悪意のあるコードまでは防げないことを承知で受け入れる**。両ジョブは同じ OS ユーザーで動くので、PR の CI で動くコードが
  悪意を持つなら、環境変数で場所を分けても、ユーザーのプロファイルや runner のディレクトリに書けてしまう。
  環境変数で分けて防げるのは、事故でキャッシュやツールが汚れる場合だけ。repo が private で、PR を出すのが owner と
  Renovate だけであることを理由に受け入れる。public にするときは見直す(`docs/development.md` の既存の記述と同じ条件で、
  そこでは CI を GitHub-hosted に戻す)。

## 検討した案

- **(a) `ci.yml` だけ `NUGET_PACKAGES` を分ける**:release 側は、永続の `.dotnet\tools` の vpk を使い続ける。
  release が CI の書いたものを読まないことが目的なので、読む側で分けるほうが確実。採らない。
- **(b) 何もしない(`docs/development.md` に理由だけ書く)**:同じ OS ユーザーなので悪意には効かないが、事故の汚れ
  (壊れたキャッシュ、古い vpk の居残り)は防げる。安く防げるので採らない。
- **(c) runner を増やす、または ephemeral にする**:悪意まで防げるが、この issue の非目標で、アカウント全体の
  runner の方針(t-seki/homelab#229)で扱う。採らない。

## 結果

- `runner.temp` が毎回空になるなら(GitHub の docs の記述で、この runner での実測ではない)、release は空の NuGet のグローバルパッケージフォルダで
  restore し、vpk を入れ直す。そのぶんタグ push のビルドは少し遅くなりうる(未計測)。
- 防げるのは事故の汚れだけで、同じ OS ユーザーである限り、悪意のある CI のコードは release の信頼境界に届きうる。
- `release.yml` は PR の CI では確かめられない。次のタグ push で、vpk が `runner.temp` の下から動いたことと、
  restore が新しいキャッシュに入ったことを見る。
