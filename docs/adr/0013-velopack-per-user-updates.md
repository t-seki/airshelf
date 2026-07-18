# 更新機構は Velopack(GitHub Releases フィード)による per-user 自己更新にする

Status: accepted([ADR-0012](0012-retire-service-for-tray-resident-per-user.md) の per-user 化で解錠された更新機構の選定)

トレイ常駐 per-user モデル([ADR-0012](0012-retire-service-for-tray-resident-per-user.md))への移行で install が非 admin になり、デスクトップ自己更新技術が使えるようになった。更新機構に **Velopack**(旧 Squirrel / Clowd.Squirrel の後継)を採用し、**既存の GitHub Releases をフィード源**として使う。トレイが起動時・定期にフィードを照会し、新版があれば delta を取得して**稼働中 exe を差し替え → 再起動**する。

## 背景

サービス時代の更新は `deploy/update.ps1`(admin・手元展開した Release zip から 2 exe を停止/差し替え/再起動)だった。[ADR-0012](0012-retire-service-for-tray-resident-per-user.md) で per-user 化した結果、「トレイから online 更新」「installer / winget」といった当初要望が本命の答えを得られるようになった。決定的な難所は **稼働中の exe を安全に差し替えて再起動する**ことで、これは自作すると `rename-self` 等の細工を要する。

## Considered Options

- **(採用) Velopack** — 稼働中 exe の差し替え・delta・ロールバックという難所を既製で解決。per-user・非 admin。**GitHub Releases をフィード源に直接使える**ため既存の配布(タグ駆動 Release)を流用できる。install/update/データ位置を framework が一貫して面倒見るので、[ADR-0012](0012-retire-service-for-tray-resident-per-user.md) で必要になる install 全面書き直しと重なる。代償は依存追加と、`release.yml` を素の zip 生成から Velopack パッケージ生成(`vpk`)へ書き換えること。
- **自作(GitHub Releases API 直叩き)** — 依存ゼロで既存 Release 資産を流用できるが、「稼働中 exe 差し替え」を自前で背負う。Velopack が解く面倒を再発明するため、本移行の通奏低音「難所は自作しない/偶発的複雑性を増やさない」に反する。
- **winget** — per-user で技術的には可だが、winget-pkgs への manifest 公開は**他人配布(C3)寄り**。単一ユーザー機ではオーバーで、C3 ゲート内。将来 Velopack の上に「発見(`winget install`)」導線として被せられる。
- **ClickOnce** — per-user 自己更新は満たすが、マシンワイドのサービス管理はそもそも不能で、ツールチェーン(Mage)も古い。Velopack の方が本用途に素直。
- **MSIX / App Installer** — 宣言的自動更新を持つが、run-as-user の古典的 Windows サービスと相性が悪かった。サービス撤去後も、per-user デスクトップ自己更新としては Velopack がより軽量で GitHub Releases 親和。

## Consequences

- `release.yml` を Velopack 成果物(`vpk pack` / full + delta + フィード用メタ)生成へ書き換える。GitHub Releases アセットとして発行し、Velopack のフィードにする。
- トレイに更新チェック UI(現在版表示 = 元 item 1、「更新があります」通知、適用ボタン)を持たせる。適用は再起動を伴う。
- prerelease タグ(`-rc` 等)の扱い(#45 相当)を Velopack のチャネル機能で維持する。
- 依存として Velopack ランタイム/ツール(`vpk`)を導入する。
- 他人配布(C3)着手時は、本 Velopack を土台に winget 発見導線やコード署名を被せる。
