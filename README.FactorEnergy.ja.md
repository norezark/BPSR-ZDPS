# 虚妄エネルギー対応・個人利用版

ZDPS v0.1.7.5 に、装着中の実像因子の虚妄エネルギーを推定表示する機能を追加した版です。ゲームのスキル使用、ヒット、バフ、リソース消費、移動を読み取り、因子ごとに現在値・必要量・加算停止時間を表示します。

現在の配布物は **修正版2（fix2）** です。全9クラスの表示データ123項目を見直し、クラス・スキル・リソース・因子の日本語表記を修正しました。例えば「ウインドナイト」を「ゲイルランサー」、「居合斬」を「雷刃抜刀」、「真因子」を「実像因子」に変更しています。照合資料と保守方法は [日本語表記の修正記録](JAPANESE-NAMES.md) にまとめています。

fix1 の同期エラー修正も含みます。以前の版を終了し、修正版を起動してからゲームへ再ログインしてください。

## 起動と表示

1. 配布 ZIP を専用のフォルダーに展開し、同梱の `BPSR-ZDPS.exe` を起動します。既存の ZDPS と取り違えないよう、この版を使うときは既存の ZDPS を終了してください。
2. 通常の ZDPS と同じキャプチャ対象・ネットワーク設定を使います。.NET ランタイムは配布版に同梱しています。Npcap は元の ZDPS と同じく必要です。
3. **この版のキャプチャが開始した状態でゲームに再ログイン**すると、キャラクターと装着中の因子を取得できます。途中起動でまだ装着情報がない場合は、ウィンドウに「同期待ち」と表示されます。
4. 初期状態では専用ウィンドウが開きます。閉じた場合は **Features → Raid Manager → Illusion Energy** から再表示できます。メニューの日本語表記は ZDPS 本体の言語設定に従います。

ウィンドウは移動・サイズ変更ができ、最前面表示、背景の濃さ、文字サイズを変更できます。閉じても計測は続きます。「エネルギーを計測」を無効にしてから再度有効にした場合は、再ログインして情報を取得してください。

因子構成はゲームから自動取得します。クラスの手動選択は不要です。必要エネルギーは装備したアイテム ID ごとに決定するため、因子のランクによる違いも反映します。DPS の区間リセットでは因子の値を消しません。

## 対応範囲と数値の読み方

移植元にある全9クラスを対象に、84種類の第六感因子の定義（86個の計算ルール）、39種類の実像因子の定義、390個の必要量を取り込んでいます。

ストームブレイド、ゲイルランサー、ツインストライカー、ビートパフォーマー、ディバインアーチャー、フロストメイジ、シールドファイター、ヘヴィガーディアン、ヴァーダントオラクルが対象です。

- 現在値は**通信イベントからの推定値**です。直接受信するゲーム内部のゲージ値ではありません。
- 途中参加や構成変更後は「推定」と表示します。対応するリセットイベントを観測した因子から、その接頭辞が外れます。以後も通信欠落などによる差はあり得ます。
- 必要量に達しただけでは、勝手にゼロへ戻しません。移植元と同様に、対応バフのイベントでリセット・加算停止を処理します。
- 未対応のアイテムは ID を表示します。未対応の獲得因子を含む構成では、表示値が全体のエネルギーを表さない可能性があります。
- この移植は **S1〜S3 の因子方式**が対象です。S4以降を検出した場合は未対応と表示し、過去シーズンの因子を誤って使いません。移植元の別機能である S4 のノード・バフ表示は含みません。
- ゲーム実機での数値照合は未実施です。最初はゲーム内の発動タイミングと比較してください。「第六感因子・受信状況」から取得した定義とスキル要求数を確認できます。

設定は実行ファイルの隣の `FactorEnergy.settings.json` に保存します。ZDPS 本体の設定とは別ファイルです。データは `Data/FactorEnergy/` にあります。

## 上流の ZDPS を後から取り込む

ソースフォルダーは履歴を含む Git リポジトリです。公式リポジトリを `upstream`、個人用の変更を `feature/illusion-energy` ブランチとして管理しています。アプリ内に更新機能は追加していません。

PowerShell で、この README のあるソースフォルダーを開いて実行します。作業途中の変更がある場合は、先にコミットしてから更新してください。

```powershell
git switch feature/illusion-energy
git status --short
# 更新前の状態を残す。既に同名の枝がある場合は名前を変える。
git branch backup/illusion-energy-before-update
git fetch upstream --tags
git merge upstream/master
```

公開リリースだけを取り込みたい場合は、最後のコマンドで `upstream/master` の代わりに、そのリリースのタグを指定します。`git tag --sort=-version:refname` で確認できます。

競合が出た場合は、下表の接続箇所を残して解消し、`git add` と `git commit` でマージを完了してください。保留したい場合は `git merge --abort` で更新前に戻せます。Git のマージで自動解決できても、通信仕様が変わっていないことは別途確認が必要です。

| 上流ファイル | 残す接続 |
|---|---|
| `BPSR-ZDPSLib/NetCap.cs` | `NotifyObserved` / `ProxyObserved`、圧縮された Call / FrameUp の処理 |
| `BPSR-ZDPS/Managers/MessageManager.cs` | キャプチャ開始前の `FactorEnergyModule.Attach`、停止時の `Detach` |
| `BPSR-ZDPS/Windows/MainWindow.cs` | `FactorEnergyWindow.Draw` と `Illusion Energy` メニュー |
| `BPSR-ZDPS/BPSR-ZDPS.csproj` | `Data/FactorEnergy/**` の出力・発行へのコピー |

更新後はテストと発行を行います。Git と .NET 9 以降の SDK が必要です。今回のビルドでは .NET SDK 10.0.300 を使用しています。

```powershell
dotnet run --project tests/FactorEnergy.Tests -c Release
# Windows 上で、デスクトップ画面やキャプチャを起動しない表示処理の確認
dotnet run --project tests/FactorEnergy.UiSmoke -c Release
dotnet publish BPSR-ZDPS/BPSR-ZDPS.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:PublishTrimmed=false -o publish-factor
```

`publish-factor` 内の実行ファイルとデータを一緒に使います。通常の ZDPS 配布物だけで上書きすると、この追加機能は含まれない状態になるため、今後の更新もこのブランチへ取り込んでビルドしてください。

## 検証と実装の配置

123件の自動テストで、全86ルール、アイテム別の必要量、複数対象ヒットの集計、クリティカル／幸運条件、召喚者への帰属、被ダメージのチャンネル判定、継続効果の開始直後と終了境界、加算停止、リソース消費、移動、因子差分更新、破損した差分の拒否、圧縮通信、既存ハンドラーとの共存、今回の省略値による同期エラーの回帰を確認しています。テストは追加のテストフレームワークを必要としないコンソール実行形式です。失敗すると終了コードが非ゼロになります。

さらに、実際の ImGui と ZDPS のフォントを使い、同期待ち、複数因子、加算停止、未対応因子、未対応シーズン、同期エラー、無効化、非表示の8ケースで描画処理が正常に完了することを確認しました。これはヘッドレスの表示処理テストで、実際のデスクトップ上の外観・ゲームとの数値照合は含みません。

| 場所 | 役割 |
|---|---|
| `BPSR-ZDPS/Features/FactorEnergy/Core/` | UIやキャプチャに依存しない定義読み込みと計算 |
| `BPSR-ZDPS/Features/FactorEnergy/Protocol/` | ZDPS の protobuf、装着因子の差分データの変換 |
| `BPSR-ZDPS/Features/FactorEnergy/FactorEnergyModule.cs` | 受信接続、排他制御、UIから独立したタイマー、設定 |
| `BPSR-ZDPS/Features/FactorEnergy/FactorEnergyWindow.cs` | 専用ウィンドウ |
| `BPSR-ZDPS/Data/FactorEnergy/` | 移植元の定義、日本語表示名、アイテム別必要量 |
| `tests/FactorEnergy.Tests/` | 計算・通信・差分・受信接続の回帰テスト |
| `tests/FactorEnergy.UiSmoke/` | 実際の ImGui を使った、キャプチャなしの表示処理テスト |

因子の差分データは、従来の ZDPS の BlobReader を変更せず、専用のパーサーで既存のスナップショットへ反映します。破損時は不完全な構成を採用せず、因子表示を同期待ちに戻します。因子側の例外は既存の DPS ハンドラーへ流しません。

基準にしたソース:

- ZDPS: [`cfeb58c0acc85bc17181b413b9e50b0b26c15c5d`](https://github.com/Blue-Protocol-Source/BPSR-ZDPS/commit/cfeb58c0acc85bc17181b413b9e50b0b26c15c5d) / v0.1.7.5
- resonance-logs-cn: [`bd71d2dfd3c7289e6398c4cd042f4357d4f35721`](https://github.com/fudiyangjin/resonance-logs-cn/commit/bd71d2dfd3c7289e6398c4cd042f4357d4f35721) / v0.2.4

移植元の説明文と計算設定が異なる箇所は、計算設定を採用しています（例: フロストメイジの対象継続スキルは500ms間隔、ヴァーダントオラクルの対象スキル要求は342点）。継続加算は開始時刻にも1回発生し、バフの有効期限ちょうどには発生しません。
