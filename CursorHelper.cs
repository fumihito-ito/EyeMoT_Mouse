using System;
using System.Runtime.InteropServices;

namespace TobiiEyeMouse;

/// <summary>
/// カーソル生成を完全にWin32 API (CreateDIBSection + CreateIconIndirect) で実装。
/// System.Drawing (GDI+) を使わないため、Debug/Release 両構成で安定動作する。
/// GetHbitmap の強制リサイズやプリマルチプライドアルファの問題も回避。
/// </summary>
public static class CursorHelper
{
    private const int OCR_NORMAL = 32512;

    [DllImport("user32.dll")] private static extern bool SetSystemCursor(IntPtr hcur, uint id);
    [DllImport("user32.dll")] private static extern IntPtr CopyIcon(IntPtr hIcon);
    [DllImport("user32.dll")] private static extern bool SystemParametersInfo(uint uiAction, uint uiParam, IntPtr pvParam, uint fWinIni);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr hIcon);
    [DllImport("user32.dll")] private static extern IntPtr CreateIconIndirect(ref ICONINFO info);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr hObject);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFO bmi, uint usage, out IntPtr bits, IntPtr hSection, uint offset);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateBitmap(int w, int h, uint planes, uint bpp, IntPtr bits);

    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO { public bool fIcon; public int xHotspot; public int yHotspot; public IntPtr hbmMask; public IntPtr hbmColor; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize; public int biWidth; public int biHeight;
        public ushort biPlanes; public ushort biBitCount; public uint biCompression;
        public uint biSizeImage; public int biXPelsPerMeter; public int biYPelsPerMeter;
        public uint biClrUsed; public uint biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO { public BITMAPINFOHEADER bmiHeader; }

    private const uint SPI_SETCURSORS = 0x0057;
    private const uint SPIF_SENDCHANGE = 0x0002;

    public static void Apply(CursorStyle style, int size)
    {
        if (style == CursorStyle.None) return;
        Restore();
        size = Math.Clamp(size, 16, 256);

        var hCursor = BuildCursor(style, size);
        if (hCursor == IntPtr.Zero) return;

        var copy = CopyIcon(hCursor);
        SetSystemCursor(copy, OCR_NORMAL);
        DestroyIcon(hCursor);
    }

    public static void Hide()
    {
        Restore();
        var hCursor = BuildCursor(CursorStyle.None, 16); // 透明カーソル
        if (hCursor == IntPtr.Zero) return;

        var copy = CopyIcon(hCursor);
        SetSystemCursor(copy, OCR_NORMAL);
        DestroyIcon(hCursor);
    }

    public static void Restore()
    {
        // _changed フラグに関わらず強制的にリセット（異常終了後の復帰のため）
        SystemParametersInfo(SPI_SETCURSORS, 0, IntPtr.Zero, SPIF_SENDCHANGE);
    }

    private static IntPtr BuildCursor(CursorStyle style, int size)
    {
        // 32bpp BGRA DIBSection を作成（ボトムアップ）
        var bmi = new BITMAPINFO();
        bmi.bmiHeader.biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>();
        bmi.bmiHeader.biWidth = size;
        bmi.bmiHeader.biHeight = -size;  // トップダウン（負の値）
        bmi.bmiHeader.biPlanes = 1;
        bmi.bmiHeader.biBitCount = 32;
        bmi.bmiHeader.biCompression = 0; // BI_RGB

        IntPtr hbmColor = CreateDIBSection(IntPtr.Zero, ref bmi, 0, out IntPtr colorBits, IntPtr.Zero, 0);
        if (hbmColor == IntPtr.Zero || colorBits == IntPtr.Zero) return IntPtr.Zero;

        // ピクセルバッファに描画
        int stride = size * 4;
        byte[] pixels = new byte[size * size * 4];
        DrawCursorPixels(pixels, style, size);
        Marshal.Copy(pixels, 0, colorBits, pixels.Length);

        // モノクロマスク（全0 = 全表示。アルファはカラービットマップで制御）
        int maskStride = ((size + 31) / 32) * 4;
        byte[] maskBits = new byte[maskStride * size]; // 全0 → AND=0 → カラーを表示

        // style が None の場合（Hide関数で使用される透明カーソル）は、
        // マスクを全 1 (0xFF) にして背景を透過させる (Screen & 1 = Screen)。
        // これをしないと、透過ピクセルが黒い四角として表示される場合がある。
        if (style == CursorStyle.None)
        {
            for (int i = 0; i < maskBits.Length; i++) maskBits[i] = 0xFF;
        }

        IntPtr maskPtr = Marshal.AllocHGlobal(maskBits.Length);
        Marshal.Copy(maskBits, 0, maskPtr, maskBits.Length);
        IntPtr hbmMask = CreateBitmap(size, size, 1, 1, maskPtr);
        Marshal.FreeHGlobal(maskPtr);

        var ii = new ICONINFO
        {
            fIcon = false,
            xHotspot = size / 2,
            yHotspot = size / 2,
            hbmMask = hbmMask,
            hbmColor = hbmColor
        };

        IntPtr hCursor = CreateIconIndirect(ref ii);
        DeleteObject(hbmColor);
        DeleteObject(hbmMask);
        return hCursor;
    }

    // ── ピクセル単位の描画関数群 ──

    private static void DrawCursorPixels(byte[] px, CursorStyle style, int size)
    {
        switch (style)
        {
            case CursorStyle.Crosshair: DrawCrosshair(px, size); break;
            case CursorStyle.Circle:    DrawCircle(px, size); break;
            case CursorStyle.Diamond:   DrawDiamond(px, size); break;
            case CursorStyle.Ring:       DrawRing(px, size); break;
            case CursorStyle.Dot:        DrawDot(px, size); break;
        }
    }

    private static void SetPx(byte[] px, int size, int x, int y, byte r, byte g, byte b, byte a)
    {
        if (x < 0 || x >= size || y < 0 || y >= size) return;
        int i = (y * size + x) * 4;
        // プリマルチプライドアルファ
        px[i + 0] = (byte)(b * a / 255); // B
        px[i + 1] = (byte)(g * a / 255); // G
        px[i + 2] = (byte)(r * a / 255); // R
        px[i + 3] = a;
    }

    private static void FillCircle(byte[] px, int size, double cx, double cy, double radius,
                                    byte r, byte g, byte b, byte a)
    {
        int x0 = Math.Max(0, (int)(cx - radius - 1));
        int x1 = Math.Min(size - 1, (int)(cx + radius + 1));
        int y0 = Math.Max(0, (int)(cy - radius - 1));
        int y1 = Math.Min(size - 1, (int)(cy + radius + 1));

        for (int y = y0; y <= y1; y++)
            for (int x = x0; x <= x1; x++)
            {
                double dx = x - cx, dy = y - cy;
                double dist = Math.Sqrt(dx * dx + dy * dy);
                if (dist <= radius)
                {
                    // アンチエイリアス（端を少しぼかす）
                    double edge = Math.Clamp(radius - dist, 0, 1);
                    byte aa = (byte)(a * edge);
                    BlendPx(px, size, x, y, r, g, b, aa);
                }
            }
    }

    private static void DrawRingShape(byte[] px, int size, double cx, double cy,
                                       double outerR, double innerR,
                                       byte r, byte g, byte b, byte a)
    {
        int x0 = Math.Max(0, (int)(cx - outerR - 1));
        int x1 = Math.Min(size - 1, (int)(cx + outerR + 1));
        int y0 = Math.Max(0, (int)(cy - outerR - 1));
        int y1 = Math.Min(size - 1, (int)(cy + outerR + 1));

        for (int y = y0; y <= y1; y++)
            for (int x = x0; x <= x1; x++)
            {
                double dx = x - cx, dy = y - cy;
                double dist = Math.Sqrt(dx * dx + dy * dy);
                if (dist <= outerR && dist >= innerR)
                {
                    double edgeOuter = Math.Clamp(outerR - dist, 0, 1);
                    double edgeInner = Math.Clamp(dist - innerR, 0, 1);
                    byte aa = (byte)(a * Math.Min(edgeOuter, edgeInner));
                    BlendPx(px, size, x, y, r, g, b, aa);
                }
            }
    }

    private static void BlendPx(byte[] px, int size, int x, int y, byte r, byte g, byte b, byte a)
    {
        if (x < 0 || x >= size || y < 0 || y >= size || a == 0) return;
        int i = (y * size + x) * 4;
        // プリマルチプライドアルファで over 合成
        int srcB = b * a / 255, srcG = g * a / 255, srcR = r * a / 255, srcA = a;
        int dstB = px[i], dstG = px[i + 1], dstR = px[i + 2], dstA = px[i + 3];
        int outA = srcA + dstA * (255 - srcA) / 255;
        if (outA > 0)
        {
            px[i + 0] = (byte)Math.Min(255, srcB + dstB * (255 - srcA) / 255);
            px[i + 1] = (byte)Math.Min(255, srcG + dstG * (255 - srcA) / 255);
            px[i + 2] = (byte)Math.Min(255, srcR + dstR * (255 - srcA) / 255);
            px[i + 3] = (byte)Math.Min(255, outA);
        }
    }

    private static void DrawLine(byte[] px, int size, int x0, int y0, int x1, int y1,
                                  int thickness, byte r, byte g, byte b, byte a)
    {
        int dx = Math.Abs(x1 - x0), dy = Math.Abs(y1 - y0);
        int sx = x0 < x1 ? 1 : -1, sy = y0 < y1 ? 1 : -1;
        int err = dx - dy;
        int half = thickness / 2;

        while (true)
        {
            for (int ty = -half; ty <= half; ty++)
                for (int tx = -half; tx <= half; tx++)
                    BlendPx(px, size, x0 + tx, y0 + ty, r, g, b, a);

            if (x0 == x1 && y0 == y1) break;
            int e2 = 2 * err;
            if (e2 > -dy) { err -= dy; x0 += sx; }
            if (e2 < dx) { err += dx; y0 += sy; }
        }
    }

    // ── 5種類の描画 ──

    private static void DrawCrosshair(byte[] px, int s)
    {
        int h = s / 2;
        int gap = s / 6;
        int m = Math.Max(3, s / 12);
        int thick = Math.Max(1, s / 18);

        // 黒縁
        DrawLine(px, s, h, m, h, h - gap, thick + 2, 0, 0, 0, 200);
        DrawLine(px, s, h, h + gap, h, s - m, thick + 2, 0, 0, 0, 200);
        DrawLine(px, s, m, h, h - gap, h, thick + 2, 0, 0, 0, 200);
        DrawLine(px, s, h + gap, h, s - m, h, thick + 2, 0, 0, 0, 200);
        // 白線
        DrawLine(px, s, h, m, h, h - gap, thick, 255, 255, 255, 255);
        DrawLine(px, s, h, h + gap, h, s - m, thick, 255, 255, 255, 255);
        DrawLine(px, s, m, h, h - gap, h, thick, 255, 255, 255, 255);
        DrawLine(px, s, h + gap, h, s - m, h, thick, 255, 255, 255, 255);
        // 中心赤ドット
        FillCircle(px, s, h, h, Math.Max(2, s / 10.0), 255, 60, 60, 255);
    }

    private static void DrawCircle(byte[] px, int s)
    {
        int h = s / 2;
        double r = h - Math.Max(2, s / 12);
        FillCircle(px, s, h, h, r, 60, 130, 246, 140);
        DrawRingShape(px, s, h, h, r + 1, r - 1, 255, 255, 255, 220);
        FillCircle(px, s, h, h, Math.Max(2, s / 12.0), 255, 255, 255, 255);
    }

    private static void DrawDiamond(byte[] px, int s)
    {
        int h = s / 2;
        int m = Math.Max(3, s / 10);
        // 塗りつぶしひし形
        for (int y = 0; y < s; y++)
            for (int x = 0; x < s; x++)
            {
                double dx = Math.Abs(x - h), dy = Math.Abs(y - h);
                double d = dx / (h - m) + dy / (h - m);
                if (d <= 1.0)
                {
                    double edge = Math.Clamp((1.0 - d) * (h - m), 0, 1);
                    BlendPx(px, s, x, y, 16, 185, 129, (byte)(140 * edge));
                }
                // 縁
                if (Math.Abs(d - 1.0) < 2.0 / (h - m))
                    BlendPx(px, s, x, y, 255, 255, 255, 200);
            }
        FillCircle(px, s, h, h, Math.Max(2, s / 12.0), 255, 255, 255, 255);
    }

    private static void DrawRing(byte[] px, int s)
    {
        int h = s / 2;
        double outerR = h - Math.Max(3, s / 10);
        double ringW = Math.Max(3, s / 8.0);
        DrawRingShape(px, s, h, h, outerR, outerR - ringW, 0, 0, 0, 180);
        DrawRingShape(px, s, h, h, outerR - 1, outerR - ringW + 1, 255, 255, 255, 240);
        FillCircle(px, s, h, h, Math.Max(1.5, s / 16.0), 255, 60, 60, 255);
    }

    private static void DrawDot(byte[] px, int s)
    {
        int h = s / 2;
        double r = Math.Max(4, s / 4.0);
        FillCircle(px, s, h, h, r + 2, 0, 0, 0, 180);
        FillCircle(px, s, h, h, r, 245, 158, 11, 255);
    }
}
