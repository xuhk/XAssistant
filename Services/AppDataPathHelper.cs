using System;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Threading;
using Microsoft.Win32;

namespace XAssistant.Services
{
    public static class AppDataPathHelper
    {
        public static string GetAppDataFolder()
        {
            string folderName;
#if DEBUG
            folderName = "XAssistant_Dev"; // 开发版专用文件夹
#else
            folderName = "XAssistant"; // 生产版专用文件夹
#endif
            string path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                folderName
            );
            Directory.CreateDirectory(path);
            return path;
        }

        /// <summary>
        /// 登录后计划任务会立即拉起进程；此时用户配置单元可能还没挂载完，
        /// <c>%APPDATA%</c> 可能指向临时目录。等到真实的 ApplicationData 路径出现再开始建库 / 写日志，
        /// 替代原来在计划任务上写死的 8 秒延时。
        /// </summary>
        public static bool WaitForApplicationDataReady(
            TimeSpan timeout,
            out long waitedMilliseconds
        )
        {
            var stopwatch = Stopwatch.StartNew();
            while (stopwatch.Elapsed < timeout)
            {
                if (IsApplicationDataReady())
                {
                    waitedMilliseconds = stopwatch.ElapsedMilliseconds;
                    return true;
                }
                Thread.Sleep(100);
            }

            waitedMilliseconds = stopwatch.ElapsedMilliseconds;
            return false;
        }

        /// <summary>当前进程看到的 ApplicationData 是否已经是真实用户目录。</summary>
        public static bool IsApplicationDataReady()
        {
            try
            {
                string current = Environment.GetFolderPath(
                    Environment.SpecialFolder.ApplicationData
                );
                if (string.IsNullOrWhiteSpace(current) || !Directory.Exists(current))
                    return false;

                string normalized = Path.GetFullPath(current).TrimEnd(Path.DirectorySeparatorChar);
                if (IsUnderTemp(normalized))
                    return false;

                // HKCU 的 User Shell Folders 是配置单元已经挂载的直接证据，
                // 也能正确处理被重定向到其它磁盘的 AppData。
                string? registered = GetRegisteredApplicationData();
                if (registered is not null)
                {
                    return string.Equals(
                        normalized,
                        registered.TrimEnd(Path.DirectorySeparatorChar),
                        StringComparison.OrdinalIgnoreCase
                    );
                }

                // 读不到 User Shell Folders 时退回 ProfileList（本地用户）
                string? profile = GetProfileApplicationData();
                if (profile is not null)
                {
                    return string.Equals(
                        normalized,
                        profile.TrimEnd(Path.DirectorySeparatorChar),
                        StringComparison.OrdinalIgnoreCase
                    );
                }

                return false;
            }
            catch
            {
                return false;
            }
        }

        private static bool IsUnderTemp(string normalizedPath)
        {
            string temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
            return string.Equals(normalizedPath, temp, StringComparison.OrdinalIgnoreCase)
                || normalizedPath.StartsWith(
                    temp + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase
                );
        }

        private static string? GetRegisteredApplicationData()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders"
                );
                if (
                    key?.GetValue("AppData") is not string value
                    || string.IsNullOrWhiteSpace(value)
                )
                    return null;

                return Path.GetFullPath(Environment.ExpandEnvironmentVariables(value));
            }
            catch
            {
                return null;
            }
        }

        private static string? GetProfileApplicationData()
        {
            try
            {
                string? sid = WindowsIdentity.GetCurrent().User?.Value;
                if (string.IsNullOrEmpty(sid))
                    return null;

                using var key = Registry.LocalMachine.OpenSubKey(
                    $@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList\{sid}"
                );
                if (key?.GetValue("ProfileImagePath") is not string profilePath)
                    return null;

                return Path.GetFullPath(
                    Path.Combine(
                        Environment.ExpandEnvironmentVariables(profilePath),
                        "AppData",
                        "Roaming"
                    )
                );
            }
            catch
            {
                return null;
            }
        }
    }
}
