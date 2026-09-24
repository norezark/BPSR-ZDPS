# このフォークの管理方針

[norezark/BPSR-ZDPS](https://github.com/norezark/BPSR-ZDPS) は、
[Blue-Protocol-Source/BPSR-ZDPS](https://github.com/Blue-Protocol-Source/BPSR-ZDPS) の個人用フォークです。
通常の利用・ソースの取得・開発・更新は **`main`** を基準にします。
上流のGit履歴を保持しているため、必要なタイミングで上流の変更をマージできます。

## 初回の取得

```powershell
git clone --branch main https://github.com/norezark/BPSR-ZDPS.git
cd BPSR-ZDPS
git remote add upstream https://github.com/Blue-Protocol-Source/BPSR-ZDPS.git
```

既存の作業フォルダーでは `git remote -v` を確認し、`upstream` が未登録の場合だけ追加します。

## 上流の更新

作業途中の変更は先にコミットし、`main` に上流の更新を取り込みます。

```powershell
git switch main
git pull --ff-only origin main
git fetch upstream --tags
git merge upstream/master
```

特定リリースだけを取り込む場合は、最後の `upstream/master` をそのリリースのタグに置き換えます。
README先頭のフォーク説明、追加機能、ライセンス、CI設定を保持して競合を解消します。
競合したマージを中止する場合は `git merge --abort` で更新前へ戻せます。

虚妄エネルギー機能の接続箇所とテスト・ビルド手順は [README.FactorEnergy.ja.md](README.FactorEnergy.ja.md) を参照してください。
検証が済んだ変更を `git push origin main` で公開します。

## ビルドとライセンス

`main` へのpushと、`main` を対象にしたPull Requestで、GitHub Actionsが因子の自動テスト・ヘッドレスUIテストとWindows x64向けのビルドを確認します。
成果物はActionsの `BPSR-ZDPS-main-win-x64` に保存し、対応ソースとチェックサムも含めます。実行用の.NETランタイムはZIPに同梱します。Npcapは別途必要です。

このフォークはresonance-logs-cn由来の実装・定義データを含むため、改造版全体をAGPL-3.0-onlyで配布します。
上流ZDPS由来部分のMIT表記は `LICENSES/BPSR-ZDPS-MIT.txt` に保持しています。

移植元とクレジットは [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)、ライセンス全文は [LICENSE](LICENSE) を参照してください。
