using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Collections.Generic;

namespace TobiiEyeMouse;

public sealed class TobiiStreamEngine : IDisposable
{
    private IntPtr _hDll;
    private IntPtr _api;
    private IntPtr _device;
    private bool _subscribed;
    private bool _disposed;

    public string Model { get; private set; } = "";
    public string Serial { get; private set; } = "";
    public bool IsConnected => _device != IntPtr.Zero;
    public bool IsSubscribed => _subscribed;

    private const int TOBII_ERROR_NO_ERROR = 0;
    private const int TOBII_ERROR_TIMED_OUT = 11;
    private const int TOBII_ERROR_CONNECTION_FAILED = 3;
    private const int TOBII_VALIDITY_VALID = 1;
    private const int TOBII_FIELD_OF_USE_INTERACTIVE = 1;

    [StructLayout(LayoutKind.Sequential)]
    public struct TobiiGazePoint
    {
        public long TimestampUs;
        public int Validity;
        public float X;
        public float Y;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    private struct TobiiDeviceInfo
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string SerialNumber;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string ModelName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Generation;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string FirmwareVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string IntegrationId;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string HwCalibrationVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string HwCalibrationDate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string LotId;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string IntegrationType;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string RuntimeBuildVersion;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void UrlReceiverDelegate(IntPtr url, IntPtr userData);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void GazePointCallbackDelegate(ref TobiiGazePoint gazePoint, IntPtr userData);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int D_api_create(out IntPtr api, IntPtr a, IntPtr b);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int D_api_destroy(IntPtr api);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int D_enumerate(IntPtr api, UrlReceiverDelegate r, IntPtr ud);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int D_device_create(IntPtr api, IntPtr url, int f, out IntPtr dev);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int D_device_destroy(IntPtr dev);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int D_device_reconnect(IntPtr dev);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int D_get_info(IntPtr dev, out TobiiDeviceInfo info);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int D_gaze_sub(IntPtr dev, GazePointCallbackDelegate cb, IntPtr ud);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int D_gaze_unsub(IntPtr dev);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int D_wait(int c, ref IntPtr dev);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int D_process(IntPtr dev);

    private D_api_create? _apiCreate; private D_api_destroy? _apiDestroy;
    private D_enumerate? _enumerate; private D_device_create? _devCreate;
    private D_device_destroy? _devDestroy; private D_device_reconnect? _devReconnect;
    private D_get_info? _getInfo; private D_gaze_sub? _gazeSub;
    private D_gaze_unsub? _gazeUnsub; private D_wait? _wait; private D_process? _process;
    private GazePointCallbackDelegate? _gazeCallbackDelegate;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern IntPtr LoadLibraryW(string p);
    [DllImport("kernel32.dll")] private static extern IntPtr GetProcAddress(IntPtr h, string n);
    [DllImport("kernel32.dll")] private static extern bool FreeLibrary(IntPtr h);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool SetDllDirectoryW(string p);

    public event Action<float, float>? GazePointReceived;

    public bool LoadDll()
    {
        foreach (var p in GetSearchPaths())
        {
            if (File.Exists(p))
            {
                var dir = Path.GetDirectoryName(p);
                if (!string.IsNullOrEmpty(dir)) SetDllDirectoryW(dir);
                _hDll = LoadLibraryW(p);
                if (_hDll != IntPtr.Zero) break;
            }
        }
        if (_hDll == IntPtr.Zero)
        {
            var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var found = FindDll(Path.Combine(pf, "Tobii"), 0);
            if (found != null) { SetDllDirectoryW(Path.GetDirectoryName(found)!); _hDll = LoadLibraryW(found); }
        }
        if (_hDll == IntPtr.Zero)
        {
            var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            var found = FindDll(Path.Combine(pf86, "Tobii"), 0);
            if (found != null) { SetDllDirectoryW(Path.GetDirectoryName(found)!); _hDll = LoadLibraryW(found); }
        }
        return _hDll != IntPtr.Zero && LoadFunctions();
    }

    private List<string> GetSearchPaths()
    {
        var l = new List<string>();
        var d = AppDomain.CurrentDomain.BaseDirectory;
        l.Add(Path.Combine(d, "tobii_stream_engine.dll"));
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        l.Add(Path.Combine(pf, "Tobii", "Tobii EyeX", "tobii_stream_engine.dll"));
        l.Add(Path.Combine(pf, "Tobii", "Tobii Experience", "tobii_stream_engine.dll"));
        l.Add(Path.Combine(pf, "Tobii", "Service", "tobii_stream_engine.dll"));
        var pd = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        l.Add(Path.Combine(pd, "Tobii", "tobii_stream_engine.dll"));
        return l;
    }

    private static string? FindDll(string root, int depth)
    {
        if (depth > 4 || !Directory.Exists(root)) return null;
        try
        {
            foreach (var f in Directory.GetFiles(root, "tobii_stream_engine.dll", SearchOption.TopDirectoryOnly)) return f;
            foreach (var sub in Directory.GetDirectories(root)) { var r = FindDll(sub, depth + 1); if (r != null) return r; }
        }
        catch { }
        return null;
    }

    private T? G<T>(string name) where T : Delegate
    { var p = GetProcAddress(_hDll, name); return p == IntPtr.Zero ? null : Marshal.GetDelegateForFunctionPointer<T>(p); }

    private bool LoadFunctions()
    {
        _apiCreate = G<D_api_create>("tobii_api_create"); _apiDestroy = G<D_api_destroy>("tobii_api_destroy");
        _enumerate = G<D_enumerate>("tobii_enumerate_local_device_urls");
        _devCreate = G<D_device_create>("tobii_device_create"); _devDestroy = G<D_device_destroy>("tobii_device_destroy");
        _devReconnect = G<D_device_reconnect>("tobii_device_reconnect");
        _getInfo = G<D_get_info>("tobii_get_device_info");
        _gazeSub = G<D_gaze_sub>("tobii_gaze_point_subscribe"); _gazeUnsub = G<D_gaze_unsub>("tobii_gaze_point_unsubscribe");
        _wait = G<D_wait>("tobii_wait_for_callbacks"); _process = G<D_process>("tobii_device_process_callbacks");
        return _apiCreate != null && _apiDestroy != null && _enumerate != null && _devCreate != null
            && _devDestroy != null && _gazeSub != null && _gazeUnsub != null && _wait != null && _process != null;
    }

    public bool Connect()
    {
        if (_apiCreate == null) return false;
        if (_apiCreate(out _api, IntPtr.Zero, IntPtr.Zero) != 0) return false;
        string? url = null;
        UrlReceiverDelegate recv = (p, _) => { if (url == null) url = Marshal.PtrToStringAnsi(p); };
        _enumerate!(_api, recv, IntPtr.Zero);
        if (url == null) { _apiDestroy!(_api); _api = IntPtr.Zero; return false; }
        var nUrl = Marshal.StringToHGlobalAnsi(url);
        try { if (_devCreate!(_api, nUrl, TOBII_FIELD_OF_USE_INTERACTIVE, out _device) != 0) { _apiDestroy!(_api); _api = IntPtr.Zero; return false; } }
        finally { Marshal.FreeHGlobal(nUrl); }
        if (_getInfo != null && _getInfo(_device, out var info) == 0) { Model = info.ModelName ?? "Tobii"; Serial = info.SerialNumber ?? "?"; }
        return true;
    }

    public bool Subscribe()
    {
        if (_device == IntPtr.Zero || _gazeSub == null) return false;
        _gazeCallbackDelegate = (ref TobiiGazePoint gp, IntPtr _) =>
        { if (gp.Validity == TOBII_VALIDITY_VALID && gp.X >= 0f && gp.X <= 1f && gp.Y >= 0f && gp.Y <= 1f) GazePointReceived?.Invoke(gp.X, gp.Y); };
        _subscribed = _gazeSub(_device, _gazeCallbackDelegate, IntPtr.Zero) == 0;
        return _subscribed;
    }

    public void Unsubscribe()
    { if (_subscribed && _gazeUnsub != null && _device != IntPtr.Zero) { _gazeUnsub(_device); _subscribed = false; } }

    public void ProcessLoop(Func<bool> run)
    {
        while (run())
        {
            var d = _device;
            int e = _wait!(1, ref d);
            if (e == TOBII_ERROR_TIMED_OUT) continue;
            if (e == TOBII_ERROR_CONNECTION_FAILED) { Reconn(run); continue; }
            e = _process!(d);
            if (e == TOBII_ERROR_CONNECTION_FAILED) Reconn(run);
        }
    }

    private void Reconn(Func<bool> run)
    { if (_devReconnect == null) return; while (run()) { if (_devReconnect(_device) == 0) break; System.Threading.Thread.Sleep(300); } }

    public void Dispose()
    {
        if (_disposed) return; _disposed = true;
        Unsubscribe();
        if (_device != IntPtr.Zero && _devDestroy != null) { _devDestroy(_device); _device = IntPtr.Zero; }
        if (_api != IntPtr.Zero && _apiDestroy != null) { _apiDestroy(_api); _api = IntPtr.Zero; }
        if (_hDll != IntPtr.Zero) { FreeLibrary(_hDll); _hDll = IntPtr.Zero; }
    }
}
