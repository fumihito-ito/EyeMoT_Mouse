using System;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace TobiiEyeMouse;

/// <summary>
/// 視線位置にWPFオーバーレイでカーソル＋注視進捗円を描画する。
/// システムカーソルを一切使わず、全画面透明ウィンドウ上にレンダリング。
/// クリック透過 (WS_EX_TRANSPARENT) により、下のウィンドウをそのまま操作可能。
/// </summary>
public class GazeOverlay : Window
{
    private const double FixedDwellIndicatorRadius = 42;

    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_TOOLWINDOW  = 0x00000080;
    private const int WS_EX_NOACTIVATE  = 0x08000000;

    // 視線座標（スクリーンピクセル）
    private double _gazeX, _gazeY;
    private bool _gazeValid;

    // カーソル設定
    private CursorStyle _cursorStyle = CursorStyle.Crosshair;
    private int _cursorSize = 32;

    // 注視進捗
    private double _dwellProgress;
    private bool _dwellEnabled;

    // スクロールエリア表示
    private bool _scrollEnabled;
    private int _scrollEdgeSize;

    // ドラッグ状態
    private bool _dragActive;
    private double _dragStartX, _dragStartY;
    private readonly System.Diagnostics.Stopwatch _dragPulseSw = System.Diagnostics.Stopwatch.StartNew();

    // 一時停止状態
    private bool _isPaused;
    private double _cornerProgress;
    private int _cornerSizePx = 150;

    // 表示制御
    private bool _active;
    private bool _dimMode;   // 停止中のかすかな視線カーソルモード
    private readonly DispatcherTimer _renderTimer;

    // DPIスケール
    private double _dpiScaleX = 1, _dpiScaleY = 1;

    public GazeOverlay()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        IsHitTestVisible = false;
        WindowState = WindowState.Normal;
        Opacity = 0;  // 初期非表示（StartOverlayで表示）

        _renderTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _renderTimer.Tick += (_, _) => { if (_active || _dimMode) InvalidateVisual(); };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        int exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
        SetWindowLong(hwnd, GWL_EXSTYLE,
            exStyle | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);

        // DPIスケールを取得
        var source = PresentationSource.FromVisual(this);
        if (source?.CompositionTarget != null)
        {
            _dpiScaleX = source.CompositionTarget.TransformFromDevice.M11;
            _dpiScaleY = source.CompositionTarget.TransformFromDevice.M22;
        }

        // 全画面配置（物理解像度を論理座標に変換してセット）
        Left = 0; Top = 0;
        Width = ScreenHelper.PhysicalWidth * _dpiScaleX;
        Height = ScreenHelper.PhysicalHeight * _dpiScaleY;
    }

    /// <summary>視線座標を更新（スクリーンピクセル座標）</summary>
    public void UpdateGaze(double screenX, double screenY)
    {
        _gazeX = screenX * _dpiScaleX;
        _gazeY = screenY * _dpiScaleY;
        _gazeValid = true;
    }

    public void SetCursorAppearance(CursorStyle style, int size)
    {
        _cursorStyle = style;
        _cursorSize = Math.Clamp(size, 16, 256);
    }

    public void SetDwellState(bool enabled, double progress, double radiusPx)
    {
        _dwellEnabled = enabled;
        _dwellProgress = Math.Clamp(progress, 0, 1);
    }

    public void SetScrollState(bool enabled, int edgeSize)
    {
        _scrollEnabled = enabled;
        _scrollEdgeSize = edgeSize;
    }

    public void SetPauseState(bool paused, int cornerSizePx, double cornerProgress)
    {
        _isPaused = paused;
        _cornerSizePx = cornerSizePx;
        _cornerProgress = Math.Clamp(cornerProgress, 0, 1);
    }

    /// <summary>ドラッグ状態を設定（スクリーンピクセル座標）</summary>
    public void SetDragState(bool isDragging, double startScreenX, double startScreenY)
    {
        _dragActive = isDragging;
        _dragStartX = startScreenX * _dpiScaleX;
        _dragStartY = startScreenY * _dpiScaleY;
    }

    public void StartOverlay()
    {
        _dimMode = false;
        _active = true;
        Opacity = 1;
        _renderTimer.Start();
    }

    public void StopOverlay()
    {
        _active = false;
        _gazeValid = false;
        _renderTimer.Stop();
        Opacity = 0;
        InvalidateVisual();
    }

    /// <summary>停止中のかすかな視線カーソル表示を開始</summary>
    public void StartDimMode()
    {
        _active = false;
        _dimMode = true;
        Opacity = 1;
        _renderTimer.Start();
    }

    /// <summary>かすかな視線カーソル表示を終了</summary>
    public void StopDimMode()
    {
        _dimMode = false;
        _renderTimer.Stop();
        Opacity = 0;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);

        // 停止中のかすかな視線カーソル（通常描画より優先して単独で描く）
        if (_dimMode)
        {
            if (_gazeValid) DrawDimCursor(dc, _gazeX, _gazeY);
            return;
        }

        if (!_active) return;

        // 0. スクロール領域の表示
        if (_scrollEnabled && _scrollEdgeSize > 0)
        {
            double scaleEdge = _scrollEdgeSize * _dpiScaleY;
            var overlayBrush = new SolidColorBrush(Color.FromArgb(40, 245, 158, 11));

            dc.DrawRectangle(overlayBrush, null, new Rect(0, 0, Width, scaleEdge));
            dc.DrawRectangle(overlayBrush, null, new Rect(0, Math.Max(0, Height - scaleEdge), Width, scaleEdge));
        }

        // 一時停止中のコーナーインジケーター
        if (_isPaused)
            DrawPauseCornerIndicator(dc);

        if (!_gazeValid) return;

        double cx = _gazeX, cy = _gazeY;

        // 1. ドラッグ中の視覚フィードバック
        if (_dragActive)
            DrawDragFeedback(dc, cx, cy);

        // 2. 注視進捗円（有効かつ進捗 > 0 の場合）
        if (_dwellEnabled && _dwellProgress > 0.01)
        {
            double r = FixedDwellIndicatorRadius * _dpiScaleX;

            // 半透明背景円
            dc.DrawEllipse(
                new SolidColorBrush(Color.FromArgb(30, 255, 255, 255)),
                new Pen(new SolidColorBrush(Color.FromArgb(50, 255, 255, 255)), 1.5),
                new Point(cx, cy), r, r);

            // 進捗アーク（ドラッグ中はオレンジ、通常はブルー）
            double sweep = _dwellProgress * 360;
            var arc = CreateArc(cx, cy, r - 2, -90, sweep);
            var arcColor = _dragActive
                ? Color.FromArgb(220, 245, 158, 11)    // オレンジ
                : Color.FromArgb(220, 59, 130, 246);   // ブルー
            var arcFill = _dragActive
                ? Color.FromArgb(160, 245, 158, 11)
                : Color.FromArgb(160, 59, 130, 246);
            dc.DrawGeometry(
                new SolidColorBrush(arcFill),
                new Pen(new SolidColorBrush(arcColor), 3),
                arc);
        }

        // 3. 視線カーソル
        if (_cursorStyle != CursorStyle.None)
            DrawGazeCursor(dc, cx, cy);
    }

    /// <summary>停止中のかすかな視線カーソル（小さな半透明の灰色ドット）</summary>
    private void DrawDimCursor(DrawingContext dc, double cx, double cy)
    {
        double r = 7 * _dpiScaleX;

        // 外側のソフトグロー
        dc.DrawEllipse(
            new SolidColorBrush(Color.FromArgb(20, 200, 200, 200)),
            null,
            new Point(cx, cy), r * 2.8, r * 2.8);

        // 外側リング
        dc.DrawEllipse(null,
            new Pen(new SolidColorBrush(Color.FromArgb(60, 200, 200, 200)), _dpiScaleX),
            new Point(cx, cy), r + 4 * _dpiScaleX, r + 4 * _dpiScaleX);

        // 中心ドット
        dc.DrawEllipse(
            new SolidColorBrush(Color.FromArgb(85, 220, 220, 220)),
            new Pen(new SolidColorBrush(Color.FromArgb(40, 0, 0, 0)), 0.75 * _dpiScaleX),
            new Point(cx, cy), r, r);
    }

    /// <summary>一時停止中の左上コーナーインジケーターを描画</summary>
    private void DrawPauseCornerIndicator(DrawingContext dc)
    {
        double size = _cornerSizePx * _dpiScaleX;
        byte bgAlpha = (byte)(120 + (int)(80 * _cornerProgress));

        // コーナー領域背景
        dc.DrawRectangle(
            new SolidColorBrush(Color.FromArgb(bgAlpha, 245, 158, 11)),
            new Pen(new SolidColorBrush(Color.FromArgb(160, 245, 158, 11)), 1.5),
            new Rect(0, 0, size, size));

        // 進捗アーク
        if (_cornerProgress > 0.01)
        {
            double cx = size / 2, cy = size / 2;
            double r = size / 2 - 12 * _dpiScaleX;
            var arc = CreateArc(cx, cy, r, -90, _cornerProgress * 360);
            dc.DrawGeometry(
                new SolidColorBrush(Color.FromArgb(60, 245, 158, 11)),
                new Pen(new SolidColorBrush(Color.FromArgb(220, 245, 158, 11)), 3 * _dpiScaleX),
                arc);
        }

        // テキスト
        var text = new FormattedText(
            "注視で再開",
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal),
            12 * _dpiScaleX,
            new SolidColorBrush(Color.FromArgb(230, 255, 255, 255)),
            VisualTreeHelper.GetDpi(this).PixelsPerDip);
        dc.DrawText(text, new Point((size - text.Width) / 2, size - text.Height - 8 * _dpiScaleY));
    }

    /// <summary>ドラッグ中の視覚フィードバックを描画</summary>
    private void DrawDragFeedback(DrawingContext dc, double cx, double cy)
    {
        double sx = _dragStartX, sy = _dragStartY;

        // パルスアニメーション（開始点マーカー用）
        double pulseT = (_dragPulseSw.ElapsedMilliseconds % 1500) / 1500.0;
        double pulseAlpha = 0.5 + 0.5 * Math.Sin(pulseT * Math.PI * 2);
        byte markerAlpha = (byte)(120 + 100 * pulseAlpha);

        // --- A. 接続線（開始点 → 現在位置）---
        double lineLen = Math.Sqrt((cx - sx) * (cx - sx) + (cy - sy) * (cy - sy));
        if (lineLen > 5)
        {
            var linePen = new Pen(new SolidColorBrush(Color.FromArgb(140, 245, 158, 11)), 2.5)
            {
                DashStyle = new DashStyle(new double[] { 6, 4 }, 0)
            };
            dc.DrawLine(linePen, new Point(sx, sy), new Point(cx, cy));
        }

        // --- B. 開始点マーカー（パルスするリング）---
        double markerR = 14 * _dpiScaleX;
        double pulseR = markerR + 6 * pulseAlpha * _dpiScaleX;
        // 外側パルスリング
        dc.DrawEllipse(null,
            new Pen(new SolidColorBrush(Color.FromArgb((byte)(60 * pulseAlpha), 245, 158, 11)), 2.5),
            new Point(sx, sy), pulseR, pulseR);
        // 内側マーカー
        dc.DrawEllipse(
            new SolidColorBrush(Color.FromArgb(markerAlpha, 245, 158, 11)),
            new Pen(new SolidColorBrush(Color.FromArgb(200, 255, 255, 255)), 1.5),
            new Point(sx, sy), markerR, markerR);
        // 内側ドット
        dc.DrawEllipse(
            new SolidColorBrush(Color.FromArgb(255, 255, 255, 255)),
            null,
            new Point(sx, sy), 3 * _dpiScaleX, 3 * _dpiScaleX);

        // --- C. 「ドラッグ中」テキストラベル ---
        var labelText = new FormattedText(
            "◆ ドラッグ中",
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal),
            15 * _dpiScaleX,
            new SolidColorBrush(Color.FromArgb(240, 255, 255, 255)),
            VisualTreeHelper.GetDpi(this).PixelsPerDip);

        double labelX = cx - labelText.Width / 2;
        double labelY = cy - _cursorSize * _dpiScaleX / 2 - labelText.Height - 10 * _dpiScaleY;
        // ラベル背景
        double pad = 4 * _dpiScaleX;
        dc.DrawRoundedRectangle(
            new SolidColorBrush(Color.FromArgb(180, 180, 100, 0)),
            null,
            new Rect(labelX - pad, labelY - pad / 2, labelText.Width + pad * 2, labelText.Height + pad),
            4, 4);
        dc.DrawText(labelText, new Point(labelX, labelY));
    }

    private void DrawGazeCursor(DrawingContext dc, double cx, double cy)
    {
        double s = _cursorSize * _dpiScaleX;
        double h = s / 2;
        double lineW = Math.Max(1.5, s / 16);
        double gap = s / 6;
        double m = Math.Max(2, s / 12);

        // ドラッグ中はオレンジ系のカーソル色に変更
        var cursorWhite = _dragActive ? new SolidColorBrush(Color.FromArgb(255, 245, 200, 100)) : Brushes.White;
        var penWhite = new Pen(cursorWhite, lineW) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        var penShadow = new Pen(new SolidColorBrush(Color.FromArgb(150, 0, 0, 0)), lineW + 2)
            { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };

        switch (_cursorStyle)
        {
            case CursorStyle.Crosshair:
            {
                // 黒影 → 白線（十字）
                void Line(Pen p, double x1, double y1, double x2, double y2)
                    => dc.DrawLine(p, new Point(cx + x1, cy + y1), new Point(cx + x2, cy + y2));

                Line(penShadow, 0, -h + m, 0, -gap);
                Line(penShadow, 0, gap, 0, h - m);
                Line(penShadow, -h + m, 0, -gap, 0);
                Line(penShadow, gap, 0, h - m, 0);
                Line(penWhite, 0, -h + m, 0, -gap);
                Line(penWhite, 0, gap, 0, h - m);
                Line(penWhite, -h + m, 0, -gap, 0);
                Line(penWhite, gap, 0, h - m, 0);

                double dotR = Math.Max(2, s / 10);
                dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(230, 255, 70, 70)),
                    null, new Point(cx, cy), dotR, dotR);
                break;
            }

            case CursorStyle.Circle:
            {
                double r = h - m;
                dc.DrawEllipse(null,
                    new Pen(new SolidColorBrush(Color.FromArgb(150, 0, 0, 0)), lineW + 2), new Point(cx, cy), r, r);
                dc.DrawEllipse(Brushes.Transparent,
                    new Pen(cursorWhite, lineW), new Point(cx, cy), r, r);
                double cd = Math.Max(2, s / 12);
                dc.DrawEllipse(cursorWhite, null, new Point(cx, cy), cd, cd);
                break;
            }

            case CursorStyle.Diamond:
            {
                double d = h - m;
                var geo = new StreamGeometry();
                using (var ctx = geo.Open())
                {
                    ctx.BeginFigure(new Point(cx, cy - d), true, true);
                    ctx.LineTo(new Point(cx + d, cy), true, false);
                    ctx.LineTo(new Point(cx, cy + d), true, false);
                    ctx.LineTo(new Point(cx - d, cy), true, false);
                }
                geo.Freeze();
                dc.DrawGeometry(null,
                    new Pen(new SolidColorBrush(Color.FromArgb(150, 0, 0, 0)), lineW + 2), geo);
                dc.DrawGeometry(Brushes.Transparent,
                    new Pen(cursorWhite, lineW), geo);
                double dd = Math.Max(2, s / 12);
                dc.DrawEllipse(cursorWhite, null, new Point(cx, cy), dd, dd);
                break;
            }

            case CursorStyle.Ring:
            {
                double r = h - m;
                double ringW = Math.Max(2.5, s / 8);
                dc.DrawEllipse(null,
                    new Pen(new SolidColorBrush(Color.FromArgb(140, 0, 0, 0)), ringW + 2),
                    new Point(cx, cy), r, r);
                dc.DrawEllipse(null,
                    new Pen(Brushes.White, ringW),
                    new Point(cx, cy), r, r);
                double rd = Math.Max(1.5, s / 16);
                dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(230, 255, 70, 70)),
                    null, new Point(cx, cy), rd, rd);
                break;
            }

            case CursorStyle.Dot:
            {
                double r = Math.Max(4, s / 4);
                dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(160, 0, 0, 0)),
                    null, new Point(cx, cy), r + 2, r + 2);
                dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(240, 245, 158, 11)),
                    null, new Point(cx, cy), r, r);
                break;
            }
        }
    }

    private static StreamGeometry CreateArc(double cx, double cy, double r,
                                             double startDeg, double sweepDeg)
    {
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            double sr = startDeg * Math.PI / 180;
            double er = (startDeg + sweepDeg) * Math.PI / 180;
            var center = new Point(cx, cy);
            var start = new Point(cx + r * Math.Cos(sr), cy + r * Math.Sin(sr));
            var end = new Point(cx + r * Math.Cos(er), cy + r * Math.Sin(er));
            ctx.BeginFigure(center, true, true);
            ctx.LineTo(start, false, false);
            ctx.ArcTo(end, new Size(r, r), 0, sweepDeg > 180,
                       SweepDirection.Clockwise, true, false);
        }
        geo.Freeze();
        return geo;
    }
}
