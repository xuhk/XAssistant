using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using XAssistant.Services.Interfaces;

namespace XAssistant.Services;

public class MouseClickHookService : IMouseClickHookService, IDisposable
{
    public event Action<string>? MouseClicked; // 传入按钮名称 "Left"/"Middle"/"Right"

    private const int WH_MOUSE_LL = 14;
    private const int WM_LBUTTONDOWN = 0x0201;
    private const int WM_MBUTTONDOWN = 0x0207;
    private const int WM_RBUTTONDOWN = 0x0204;

    private readonly ILogger<MouseClickHookService> _logger;
    private LowLevelMouseProc _proc;
    private IntPtr _hookId = IntPtr.Zero;

    public MouseClickHookService(ILogger<MouseClickHookService> logger)
    {
        _logger = logger;
        _proc = HookCallback;
    }

    public void Start()
    {
        // 防止重复启动，导致多个钩子并存
        if (_hookId != IntPtr.Zero)
            return; // 已有一个钩子在运行

        for (int attempt = 1; attempt <= 2; attempt++)
        {
            // 获取模块名（优化后写法，避免 using 风险）
            string? moduleName = Process.GetCurrentProcess().MainModule?.ModuleName;
            if (string.IsNullOrEmpty(moduleName))
            {
                _logger.LogError("鼠标采集未能启动：无法获取主模块名称");
                return;
            }

            _hookId = SetWindowsHookEx(WH_MOUSE_LL, _proc, GetModuleHandle(moduleName), 0);
            if (_hookId != IntPtr.Zero)
            {
                _logger.LogInformation(
                    "鼠标采集已启动（钩子句柄 0x{HookId:X}）",
                    _hookId.ToInt64()
                );
                return;
            }

            _logger.LogWarning(
                "鼠标钩子安装失败（第 {Attempt}/2 次），Win32 错误码 {Error}",
                attempt,
                Marshal.GetLastWin32Error()
            );
            if (attempt < 2)
                Thread.Sleep(200);
        }

        _logger.LogError("鼠标采集未能启动：SetWindowsHookEx 连续两次失败");
    }

    public void Stop()
    {
        if (_hookId != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hookId);
            _hookId = IntPtr.Zero;
        }
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            switch ((int)wParam)
            {
                case WM_LBUTTONDOWN:
                    MouseClicked?.Invoke("Left");
                    break;
                case WM_MBUTTONDOWN:
                    MouseClicked?.Invoke("Middle");
                    break;
                case WM_RBUTTONDOWN:
                    MouseClicked?.Invoke("Right");
                    break;
            }
        }
        return CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    // P/Invoke 声明
    private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(
        int idHook,
        LowLevelMouseProc lpfn,
        IntPtr hMod,
        uint dwThreadId
    );

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr CallNextHookEx(
        IntPtr hhk,
        int nCode,
        IntPtr wParam,
        IntPtr lParam
    );

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string lpModuleName);

    public void Dispose() => Stop();
}
