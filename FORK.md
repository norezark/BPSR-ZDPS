# このフォークの管理方針

[norezark/BPSR-ZDPS](https://github.com/norezark/BPSR-ZDPS) は、
[Blue-Protocol-Source/BPSR-ZDPS](https://github.com/Blue-Protocol-Source/BPSR-ZDPS) の個人用フォークです。
上流のGit履歴を保持し、個人用機能を `main` に統合します。新しい機能は専用ブランチで開発します。

## ブランチ

| ブランチ | 役割 |
|---|---|
| `main` | このフォークの既定ブランチ。上流ZDPSに個人用機能を統合する土台です。虚妄エネルギー機能を含み、新しい作業はここから分岐します。 |
| `master` | フォーク作成時の上流ブランチを保持しています。最新の上流は `upstream/master` から取得します。 |
| `feature/illusion-energy` | 虚妄エネルギー機能の開発履歴を保持するブランチです。今回の変更は `main` に取り込み済みです。 |

`main` の開始点は上流 v0.1.7.5、コミット
[`cfeb58c0acc85bc17181b413b9e50b0b26c15c5d`](https://github.com/Blue-Protocol-Source/BPSR-ZDPS/commit/cfeb58c0acc85bc17181b413b9e50b0b26c15c5d) です。
現在は `feature/illusion-energy` の実装・定義・テストも `main` に統合しています。
公開済みタグ `v0.1.7.5-illusion.1` は、初回公開時のソースと配布物を示すものとして保持します。

## 初回の取得

```powershell
git clone https://github.com/norezark/BPSR-ZDPS.git
cd BPSR-ZDPS
git remote add upstream https://github.com/Blue-Protocol-Source/BPSR-ZDPS.git
```

既存の作業フォルダーでは `git remote -v` を確認し、`upstream` が未登録の場合だけ追加します。

## 上流の更新

作業途中の変更は先にコミットします。次の例では、更新確認用のブランチを `main` から作ります。
`maintenance/upstream-update` が既にある場合は、別のブランチ名を指定してください。

```powershell
git switch main
git pull --ff-only origin main
git fetch upstream --tags
git switch -c maintenance/upstream-update
git merge upstream/master
```

特定リリースだけを取り込む場合は、最後の `upstream/master` をそのリリースのタグに置き換えます。
README先頭のフォーク説明とCIの対象ブランチを保持して競合を解消し、ビルド・動作を確認してから `main` へ取り込みます。
マージを保留する場合は `git merge --abort` で更新前へ戻せます。

統合済みの虚妄エネルギー機能の接続箇所・検証方法は [README.FactorEnergy.ja.md](README.FactorEnergy.ja.md) を参照してください。
開発中の機能ブランチへ共通の更新を取り込む場合は、そのブランチで `git merge main` を実行します。

## 新しい個人用機能

最新の `main` から `feature/<機能名>` を作成します。
実装・説明・必要なテストをそのブランチに追加し、`main` 向けのPull Requestなどで差分を確認して取り込みます。
統合時には `main` のREADMEの機能一覧とライセンス表記も更新します。

## ビルドとライセンス

`main` へのpushと、`main` を対象にしたPull Requestで、GitHub Actionsが因子の自動テスト・ヘッドレスUIテストとWindows x64向けのビルドを確認します。
成果物はActionsの `BPSR-ZDPS-main-win-x64` に保存し、対応ソースとチェックサムも含めます。実行用の.NETランタイムはZIPに同梱します。Npcapは別途必要です。

ライセンスは、対象のソース・配布物に合わせて記載しています。

| 対象 | 配布条件 |
|---|---|
| `main` / `feature/illusion-energy` / `v0.1.7.5-illusion.1` | resonance-logs-cn由来の実装・定義データを含む改造版全体をAGPL-3.0-onlyで配布します。上流ZDPS由来部分のMIT表記を `LICENSES/BPSR-ZDPS-MIT.txt` に保持しています。 |
| `master` | フォーク作成時の上流ZDPSを保持するブランチです。元のMITライセンスが適用されます。 |

移植元とクレジットは [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)、ライセンス全文は [LICENSE](LICENSE) を参照してください。
