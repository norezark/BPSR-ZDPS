# このフォークの管理方針

[norezark/BPSR-ZDPS](https://github.com/norezark/BPSR-ZDPS) は、
[Blue-Protocol-Source/BPSR-ZDPS](https://github.com/Blue-Protocol-Source/BPSR-ZDPS) の個人用フォークです。
上流のGit履歴を保持し、共通の土台と個別機能をブランチで管理します。

## ブランチ

| ブランチ | 役割 |
|---|---|
| `main` | このフォークの既定ブランチ。上流ZDPSを基に、共通の説明・開発設定を管理する土台です。新しい作業はここから分岐します。 |
| `master` | フォーク作成時の上流ブランチを保持しています。最新の上流は `upstream/master` から取得します。 |
| `feature/illusion-energy` | 虚妄エネルギー表示を追加した既存の機能ブランチです。機能のコード・日本語説明・ライセンスはこのブランチにあります。 |

`main` の開始点は上流 v0.1.7.5、コミット
[`cfeb58c0acc85bc17181b413b9e50b0b26c15c5d`](https://github.com/Blue-Protocol-Source/BPSR-ZDPS/commit/cfeb58c0acc85bc17181b413b9e50b0b26c15c5d) です。
`main` 作成時の追加は、このフォークの説明とCI設定です。
虚妄エネルギーの実装は `feature/illusion-energy` で管理し、公開済みタグ `v0.1.7.5-illusion.1` を保持します。

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

共通の更新が `main` に入った後は、各機能ブランチに必要なタイミングでマージします。
例えば既存の虚妄エネルギー機能へ取り込む場合は、`feature/illusion-energy` に切り替えて `git merge main` を実行し、機能側のREADMEに沿って検証します。

## 新しい個人用機能

最新の `main` から `feature/<機能名>` を作成します。
機能の説明と配布物はそのブランチで管理し、`main` のREADMEには概要とリンクを追加します。
共通の土台へ統合するときは、`main` 向けのPull Requestなどで差分を確認して取り込みます。

## ビルドとライセンス

`main` へのpushと、`main` を対象にしたPull Requestで、GitHub ActionsがWindows x64向けのビルドを確認します。
成果物はActionsの `BPSR-ZDPS-main-win-x64` に保存します。上流と同様に、実行には.NET 9ランタイムとNpcapが必要です。

`main` は上流ZDPSのMIT本文をそのまま保持しています。
個別機能では移植元に応じて配布条件が異なる場合があるため、該当ブランチの `LICENSE` と説明を参照してください。
