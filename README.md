# YMM4TaskManagerPlugin

YMM4のCPU・メモリ・GPU・プラグイン別使用率を確認し、操作とアプリ全体の負荷を記録できるツールプラグイン。

「ツール → ユーティリティ → タスクマネージャー」から開きます。

## 機能

- **概要**: YMM4/システムのCPU・メモリ、マネージドヒープ、GC、スレッド、ハンドル、子プロセス一覧と、プラグインごとのCPU・メモリ・オブジェクト数・スレッド・DLL数
- **GPU**: YMM4/システムのGPU使用率、専用/共有VRAM、アダプター別・エンジン別の使用率
- **記録**: クリック・ボタン・メニュー・ショートカットの操作と、CPU・メモリ・GPU・UI応答遅延を時系列で記録（文字入力は記録しません。記録中は終了ボタンのみ表示）。結果はCSVで保存できます

プラグイン別CPUはスレッドのスタック採取による推定値、プラグイン別メモリは[ClrMD](https://github.com/microsoft/clrmd)によるヒープ解析の値です。

## ビルド

```sh
export YMM4_PATH=/path/to/YMM4
dotnet build            # YMM4_PATH/user/plugin/ に配置
dotnet publish src/TaskManager/YMM4TaskManagerPlugin.csproj -c Release   # publish/*.ymme を生成
```
