using System;
using System.Collections.Generic;
using Microsoft.Win32;

namespace AppMover.Core
{
    /// <summary>
    /// 云端同步目录识别。
    ///
    /// 为什么需要这个独立机制，而不是靠「重解析点」属性判断：
    ///
    /// Windows 会对「云文件占位符」（OneDrive 的按需文件等）做**进程级属性屏蔽**。
    /// 实测：同一个目录 C:\Users\<用户名>\OneDrive
    ///   · PowerShell 5.1 读到 FileAttributes = 1073（含 ReparsePoint）
    ///   · 本程序（即使已嵌入声明 Windows 10 兼容性的应用清单）读到 49（不含）
    ///   · FSCTL_GET_REPARSE_POINT 在本进程中直接返回 ERROR_NOT_A_REPARSE_POINT(4390)
    ///
    /// 也就是说：**在本进程里，我们无法把云占位符识别成重解析点。**
    /// 如果不管它，程序会去搬一个云同步目录 —— 复制占位文件会触发**全量下载**，
    /// 可能瞬间吃掉几十 GB 磁盘和大量带宽，甚至撑爆目标盘。
    ///
    /// 因此这里改用确定性的证据：环境变量、OneDrive 注册表登记、以及已知同步根名称。
    /// 宁可多报「禁止搬移」，也不能漏掉一个同步根。
    /// </summary>
    public static class CloudSync
    {
        /// <summary>已知同步根的名称模式（目录名，不含路径）。</summary>
        private static readonly string[] NamePatterns = new string[]
        {
            "onedrive",              // OneDrive
            "onedrive - ",           // OneDrive - Contoso（企业版按前缀匹配）
            "dropbox",
            "google drive",
            "my drive",
            "icloud drive",
            "iclouddrive",
            "nutstore",
            "坚果云",
            "百度网盘",
            "baidunetdisk",
            "box sync",
            "box drive",
            "pcloud",
            "megasync",
            "seafile",
            "nextcloud",
            "owncloud",
            "syncthing",
            "verysync",
            "微力同步",
            "weiyun",
            "腾讯微云",
            "aliyundrive",
            "阿里云盘",
        };

        private static List<string> _roots;

        /// <summary>已知云同步根目录（规范化绝对路径）。首次调用时从环境变量与注册表收集。</summary>
        public static List<string> Roots
        {
            get
            {
                if (_roots == null) _roots = Collect();
                return _roots;
            }
        }

        private static List<string> Collect()
        {
            List<string> list = new List<string>();

            // 1) 环境变量（OneDrive 自己就会设置）
            AddEnv(list, "OneDrive");
            AddEnv(list, "OneDriveConsumer");
            AddEnv(list, "OneDriveCommercial");
            AddEnv(list, "Dropbox");
            AddEnv(list, "GoogleDrive");

            // 2) OneDrive 账户登记表：这是最权威的来源
            AddOneDriveAccounts(list);

            // 3) 通用同步引擎登记（Windows 的 SyncEngines 提供程序）
            AddSyncEngines(list);

            // 去重
            List<string> result = new List<string>();
            for (int i = 0; i < list.Count; i++)
            {
                string n = PathUtil.Normalize(list[i]);
                if (n.Length == 0) continue;
                if (!result.Contains(n)) result.Add(n);
            }
            return result;
        }

        private static void AddEnv(List<string> list, string name)
        {
            try
            {
                string v = Environment.GetEnvironmentVariable(name);
                if (!string.IsNullOrEmpty(v)) list.Add(v);
            }
            catch { }
        }

        private static void AddOneDriveAccounts(List<string> list)
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\OneDrive\Accounts"))
                {
                    if (k == null) return;
                    foreach (string sub in k.GetSubKeyNames())
                    {
                        try
                        {
                            using (RegistryKey sk = k.OpenSubKey(sub))
                            {
                                if (sk == null) continue;
                                string folder = sk.GetValue("UserFolder") as string;
                                if (!string.IsNullOrEmpty(folder)) list.Add(folder);
                            }
                        }
                        catch { }
                    }
                }
            }
            catch { }
        }

        private static void AddSyncEngines(List<string> list)
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(@"Software\SyncEngines\Providers"))
                {
                    if (k == null) return;
                    foreach (string provider in k.GetSubKeyNames())
                    {
                        try
                        {
                            using (RegistryKey pk = k.OpenSubKey(provider))
                            {
                                if (pk == null) continue;
                                foreach (string id in pk.GetSubKeyNames())
                                {
                                    try
                                    {
                                        using (RegistryKey sk = pk.OpenSubKey(id))
                                        {
                                            if (sk == null) continue;
                                            string mount = sk.GetValue("MountPoint") as string;
                                            if (!string.IsNullOrEmpty(mount)) list.Add(mount);
                                        }
                                    }
                                    catch { }
                                }
                            }
                        }
                        catch { }
                    }
                }
            }
            catch { }
        }

        /// <summary>该目录名是否像已知的云同步根。</summary>
        public static bool LooksLikeSyncRootName(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            string n = name.Trim().ToLowerInvariant();
            for (int i = 0; i < NamePatterns.Length; i++)
            {
                string pat = NamePatterns[i];
                if (n == pat) return true;
                if (pat.EndsWith(" - ", StringComparison.Ordinal) && n.StartsWith(pat, StringComparison.Ordinal)) return true;
            }
            return false;
        }

        /// <summary>
        /// 判断目录是否属于云同步范围。返回命中的同步根（用于说明依据），否则 null。
        /// </summary>
        public static string MatchSyncRoot(string path)
        {
            string norm = PathUtil.Normalize(path);
            List<string> roots = Roots;
            for (int i = 0; i < roots.Count; i++)
            {
                if (PathUtil.IsUnder(norm, roots[i])) return roots[i];
            }
            return null;
        }

        /// <summary>
        /// 该目录**内部**是否装着某个云同步根（例如 C:\Users\me 里装着 OneDrive）。
        /// 搬这种父目录等于连同步根一起搬，同样要拦。
        /// </summary>
        public static string ContainsSyncRoot(string path)
        {
            string norm = PathUtil.Normalize(path);
            List<string> roots = Roots;
            for (int i = 0; i < roots.Count; i++)
            {
                if (PathUtil.IsUnder(roots[i], norm)) return roots[i];
            }
            return null;
        }

        /// <summary>
        /// 按**路径分段**判断是否身处云同步目录。
        ///
        /// 为什么不只依赖环境变量/注册表：同步根可能被移动、环境变量可能缺失，
        /// 或者用户的同步根根本不在本机登记（例如从别的机器拷来的目录结构）。
        /// 只要路径里任何一段是已知同步目录名，就按同步目录处理。
        ///
        /// 代价是可能出现误报（某个无关文件夹恰好叫这些名字），但误报的后果
        /// 只是「这个目录被标为禁止搬移」，用户看得到理由；漏报的后果是
        /// 触发云端全量下载。两者不对称，所以宁可误报。
        /// </summary>
        public static string MatchSyncRootSegment(string path)
        {
            string norm = PathUtil.Normalize(path);
            string[] parts = norm.Split('\\');
            for (int i = 0; i < parts.Length; i++)
            {
                if (parts[i].Length == 0) continue;
                if (LooksLikeSyncRootName(parts[i])) return parts[i];
            }
            return null;
        }
    }
}
