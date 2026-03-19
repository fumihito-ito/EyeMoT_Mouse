using System;
using System.Windows;
using System.Windows.Threading;

namespace TobiiEyeMouse;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 例外ハンドラを最初に登録
        DispatcherUnhandledException += (s, args) =>
        {
            MessageBox.Show($"エラー:\n\n{args.Exception.Message}\n\n{args.Exception.StackTrace}",
                "TobiiEyeMouse - エラー", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (s, args) =>
        {
            if (args.ExceptionObject is Exception ex)
                MessageBox.Show($"致命的エラー:\n\n{ex.Message}\n\n{ex.StackTrace}",
                    "TobiiEyeMouse", MessageBoxButton.OK, MessageBoxImage.Error);
        };

        // DPIスケーリング対応: 物理解像度を取得
        try { ScreenHelper.Initialize(); } catch { }
    }
}
