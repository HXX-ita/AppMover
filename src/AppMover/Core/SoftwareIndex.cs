using System;
using System.Collections.Generic;
using Microsoft.Win32;

namespace AppMover.Core
{
    /// <summary>
    /// 系统「已安装软件」与「服务」索引。
    ///
    /// 这是判定「一个目录是否对应真实已安装软件」的证据来源。
    /// 注意：注册表读取会因权限/位数差异而失败，失败时索引为空，
    /// 判定会退化为「无法证明它是已安装软件」——这比误判成垃圾要安全。
    /// </summary>
    public class SoftwareIndex
    {
        private readonly List<string> _installLocations = new List<string>();
        private readonly List<string> _displayNames = new List<string>();
        private readonly List<string> _serviceExePaths = new List<string>();

        public int UninstallEntryCount { get { return _displayNames.Count; } }
        public int ServiceCount { get { return _serviceExePaths.Count; } }

        public static SoftwareIndex Load()
        {
            SoftwareIndex idx = new SoftwareIndex();
            idx.LoadUninstall();
            idx.LoadServices();
            return idx;
        }

        private void LoadUninstall()
        {
            string[] paths = new string[]
            {
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
                @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
            };

            foreach (string p in paths) AddUninstallFrom(Registry.LocalMachine, p);
            AddUninstallFrom(Registry.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
        }

        private void AddUninstallFrom(RegistryKey root, string subPath)
        {
            try
            {
                using (RegistryKey k = root.OpenSubKey(subPath))
                {
                    if (k == null) return;
                    string[] names = k.GetSubKeyNames();
                    for (int i = 0; i < names.Length; i++)
                    {
                        try
                        {
                            using (RegistryKey sk = k.OpenSubKey(names[i]))
                            {
                                if (sk == null) continue;
                                string display = sk.GetValue("DisplayName") as string;
                                if (string.IsNullOrEmpty(display)) continue;
                                string loc = sk.GetValue("InstallLocation") as string;
                                _displayNames.Add(display.ToLowerInvariant());
                                if (!string.IsNullOrEmpty(loc)) _installLocations.Add(PathUtil.Normalize(loc));
                            }
                        }
                        catch { }
                    }
                }
            }
            catch { }
        }

        private void LoadServices()
        {
            try
            {
                using (RegistryKey k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services"))
                {
                    if (k == null) return;
                    string[] names = k.GetSubKeyNames();
                    for (int i = 0; i < names.Length; i++)
                    {
                        try
                        {
                            using (RegistryKey sk = k.OpenSubKey(names[i]))
                            {
                                if (sk == null) continue;
                                string img = sk.GetValue("ImagePath") as string;
                                if (string.IsNullOrEmpty(img)) continue;
                                string norm = NormalizeImagePath(img);
                                if (norm.Length > 0) _serviceExePaths.Add(norm);
                            }
                        }
                        catch { }
                    }
                }
            }
            catch { }
        }

        /// <summary>把服务 ImagePath 归一成一个绝对路径，供前缀比较。</summary>
        internal static string NormalizeImagePath(string imagePath)
        {
            string s = imagePath.Trim();
            if (s.StartsWith("\"", StringComparison.Ordinal))
            {
                int end = s.IndexOf('"', 1);
                if (end > 0) s = s.Substring(1, end - 1);
            }
            else
            {
                // 无引号：截断到第一个 " -" 形式的参数
                int cut = s.IndexOf(" -", StringComparison.Ordinal);
                if (cut > 0) s = s.Substring(0, cut);
            }

            // 内核路径前缀归一
            if (s.StartsWith(@"\SystemRoot\", StringComparison.OrdinalIgnoreCase))
                s = @"C:\Windows\" + s.Substring(@"\SystemRoot\".Length);
            else if (s.StartsWith(@"\??\", StringComparison.Ordinal))
                s = s.Substring(4);
            else if (s.StartsWith(@"system32\", StringComparison.OrdinalIgnoreCase))
                s = @"C:\Windows\" + s;

            if (s.Length > 1 && s[1] != ':') return string.Empty;   // 非绝对路径，放弃
            return PathUtil.Normalize(s);
        }

        /// <summary>是否存在一个卸载项的 InstallLocation 与本目录互相包含。</summary>
        public bool MatchesInstallLocation(string normalizedPath)
        {
            for (int i = 0; i < _installLocations.Count; i++)
            {
                string l = _installLocations[i];
                if (l.Length == 0) continue;
                if (string.Equals(l, normalizedPath, StringComparison.Ordinal)) return true;
                if (l.StartsWith(normalizedPath + "\\", StringComparison.Ordinal)) return true;
                if (normalizedPath.StartsWith(l + "\\", StringComparison.Ordinal)) return true;
            }
            return false;
        }

        /// <summary>卸载项名称与目录名互为前缀（模糊匹配）。</summary>
        public bool MatchesDisplayName(string nameLower)
        {
            if (string.IsNullOrEmpty(nameLower)) return false;
            for (int i = 0; i < _displayNames.Count; i++)
            {
                string n = _displayNames[i];
                if (string.Equals(n, nameLower, StringComparison.Ordinal)) return true;
                if (n.StartsWith(nameLower, StringComparison.Ordinal)) return true;
                if (nameLower.StartsWith(n, StringComparison.Ordinal)) return true;
            }
            return false;
        }

        /// <summary>是否有服务的可执行文件位于该目录之下。</summary>
        public bool HasServiceUnder(string normalizedPath)
        {
            for (int i = 0; i < _serviceExePaths.Count; i++)
            {
                if (_serviceExePaths[i].StartsWith(normalizedPath + "\\", StringComparison.Ordinal)) return true;
            }
            return false;
        }
    }
}
