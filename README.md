# Tobii Eye Mouse — WPF版 (C#)

Tobii Eye Tracker 5 の視線でマウスカーソルを制御する WPF アプリ。

## 特長

- **SDK不要**: Tobii Experience のDLLを自動検出して動的ロード
- **高速追従**: EMA（指数移動平均）フィルタ＋ダイレクトモード
- **カーソル5種類**: 十字 / 円 / ひし形 / リング / ドット（サイズ可変 16〜128px）
- **設定自動保存**: JSON (`%LOCALAPPDATA%\TobiiEyeMouse\settings.json`)
- **デザイナー編集対応**: カスタムテンプレート不使用

## ビルド

```bash
dotnet build -c Release
```

または `TobiiEyeMouse.csproj` をVSで開いて Ctrl+Shift+B。

## 単体EXE

```bash
dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true
```

## ファイル構成

| ファイル | 役割 |
|---------|------|
| TobiiEyeMouse.csproj | プロジェクト |
| App.xaml / App.xaml.cs | エントリーポイント |
| MainWindow.xaml / .cs | UI + ロジック |
| TobiiStreamEngine.cs | DLL動的ロード |
| GazeFilter.cs | EMAフィルタ + 設定保存 |
| CursorHelper.cs | 5種カーソル生成 |
