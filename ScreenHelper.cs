using System;
using System.Runtime.InteropServices;

namespace TobiiEyeMouse;

/// <summary>
/// Windowsのスケーリング（DPI）に対応した物理スクリーン解像度とDPIスケール取得。
/// 
/// DPI Awareness はアプリマニフェスト (app.manifest) で PerMonitorV2 を宣言済み。
/// SetProcessDPIAware() はWPFと競合するため使用しない。
/// </summary>
public static class ScreenHelper
{
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
    [DllImport("gdi32.dll")]  private static extern int GetDeviceCaps(IntPtr hdc, int nIndex);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int nIndex);

    private const int DESKTOPHORZRES = 118;
    private const int DESKTOPVERTRES = 117;
    private const int LOGPIXELSX = 88;
    private const int LOGPIXELSY = 90;
    private const int SM_CXSCREEN = 0;
    private const int SM_CYSCREEN = 1;

    private static int _physW, _physH;
    private static double _dpiScaleX = 1.0, _dpiScaleY = 1.0;
    private static bool _initialized;

    /// <summary>初期化（アプリ起動時に1回呼ぶ）</summary>
    public static void Initialize()
    {
        if (_initialized) return;
        _initialized = true;

        // GetDeviceCaps で物理解像度を取得
        try
        {
            IntPtr hdc = GetDC(IntPtr.Zero);
            if (hdc != IntPtr.Zero)
            {
                _physW = GetDeviceCaps(hdc, DESKTOPHORZRES);
                _physH = GetDeviceCaps(hdc, DESKTOPVERTRES);
                int logDpiX = GetDeviceCaps(hdc, LOGPIXELSX);
                int logDpiY = GetDeviceCaps(hdc, LOGPIXELSY);
                ReleaseDC(IntPtr.Zero, hdc);

                if (_physW > 0 && _physH > 0 && logDpiX > 0)
                {
                    _dpiScaleX = logDpiX / 96.0;
                    _dpiScaleY = logDpiY / 96.0;
                    return;
                }
            }
        }
        catch { }

        // フォールバック: GetSystemMetrics
        try
        {
            _physW = GetSystemMetrics(SM_CXSCREEN);
            _physH = GetSystemMetrics(SM_CYSCREEN);
        }
        catch
        {
            // 最終フォールバック
            _physW = 1920;
            _physH = 1080;
        }
        _dpiScaleX = 1.0;
        _dpiScaleY = 1.0;
    }

    public static int PhysicalWidth  { get { if (!_initialized) Initialize(); return _physW; } }
    public static int PhysicalHeight { get { if (!_initialized) Initialize(); return _physH; } }
    public static double DpiScaleX   { get { if (!_initialized) Initialize(); return _dpiScaleX; } }
    public static double DpiScaleY   { get { if (!_initialized) Initialize(); return _dpiScaleY; } }

    public static string ScalePercent
    {
        get
        {
            if (!_initialized) Initialize();
            return $"{(int)Math.Round(_dpiScaleX * 100)}%";
        }
    }
}
