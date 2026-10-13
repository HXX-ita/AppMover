using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace AppMover.Core
{
    /// <summary>
    /// 路径规范化与「真实路径」解析。
    ///
    /// 安全要点：所有黑名单判定必须作用在**解析过联接之后的真实路径**上，
    /// 否则用户可以（或恶意软件可以）在 C:\Windows 上套一层联接来绕过检查。
    /// </summary>
    public static class PathUtil
    {
        /// <summary>规范化用于比较：去首尾空白、统一分隔符、去尾部反斜杠、转小写。</summary>
        public static string Normalize(string path)
        {
            if (string.IsNullOrEmpty(path)) return string.Empty;
            string s = path.Trim();
            s = s.Replace('/', '\\');
            // 保留 "C:\" 这种根形式
            while (s.Length > 3 && s.EndsWith("\\", StringComparison.Ordinal))
                s = s.Substring(0, s.Length - 1);
            return s.ToLowerInvariant();
        }

        /// <summary>candidate 是否等于 root 或位于 root 之下（按规范化路径比较）。</summary>
        public static bool IsUnder(string candidate, string root)
        {
            string c = Normalize(candidate);
            string r = Normalize(root);
            if (c.Length == 0 || r.Length == 0) return false;
            if (string.Equals(c, r, StringComparison.Ordinal)) return true;
            if (!r.EndsWith("\\", StringComparison.Ordinal)) r = r + "\\";
            return c.StartsWith(r, StringComparison.Ordinal);
        }

        /// <summary>
        /// 去掉尾部分隔符，但**保留盘根**。
        ///
        /// 这里有个真实踩过的坑：直接用 TrimEnd('\\') 会把盘根 "D:\" 削成 "D:"，
        /// 而 "D:" 不是有效目录（它表示"当前目录在该盘上"），后续 Directory.Exists 判定为 false，
        /// 于是报出「目标盘目录不存在: D:」。
        ///
        /// 图形界面传的目标盘恰好就是 "D:\" 这种形式（盘符 + 分隔符），
        /// 所以这个 bug 让界面的搬移按钮**完全无法工作** —— 而界面测试只验了几何布局，
        /// 从没跑过搬移流程，因此一直没被发现。
        /// </summary>
        public static string TrimTrailingSeparator(string path)
        {
            if (string.IsNullOrEmpty(path)) return path;
            string s = path.Replace('/', '\\');
            // 长度必须大于 3 才削：盘根 "D:\" 正好是 3，保持原样
            while (s.Length > 3 && s.EndsWith("\\", StringComparison.Ordinal))
                s = s.Substring(0, s.Length - 1);
            // 裸盘符 "D:" 补成盘根 "D:\"
            if (s.Length == 2 && s[1] == ':') s = s + "\\";
            return s;
        }

        /// <summary>把路径变成 \\?\ 扩展形式，绕过 260 字符限制。必须是已规范化的绝对路径。</summary>
        public static string ToExtended(string fullPath)
        {
            if (string.IsNullOrEmpty(fullPath)) return fullPath;
            if (fullPath.StartsWith(@"\\?\", StringComparison.Ordinal)) return fullPath;
            string s = fullPath.Replace('/', '\\');
            if (s.StartsWith(@"\\", StringComparison.Ordinal))
                return @"\\?\UNC\" + s.Substring(2);
            return @"\\?\" + s;
        }

        /// <summary>去掉 \\?\ / \\?\UNC\ 前缀，用于展示给用户或做路径比较。</summary>
        public static string StripExtended(string path)
        {
            if (string.IsNullOrEmpty(path)) return path;
            if (path.StartsWith(@"\\?\UNC\", StringComparison.Ordinal))
                return @"\\" + path.Substring(8);
            if (path.StartsWith(@"\\?\", StringComparison.Ordinal))
                return path.Substring(4);
            return path;
        }

        /// <summary>
        /// 交给文件系统 API 的实际路径 —— **一律加 \\?\ 前缀**。
        ///
        /// 这里踩过两次坑，值得写清楚：
        ///
        /// 第一次：给所有路径都加前缀，结果整棵树的枚举全部失败、大小统计全变成 0。
        ///   原因不是前缀本身有问题，而是**没有配套的 app.config**：
        ///   .NET Framework 默认的 legacy path handling 会直接拒绝 \\?\ 前缀。
        ///   当时的"修法"是短路径不加、长路径才加，这是个错误的折中。
        ///
        /// 第二次：实测发现那个折中根本不够 —— 即使 app.config 里
        ///   Switch.System.IO.UseLegacyPathHandling=false 已生效，
        ///   无前缀路径仍然**恰好在 260 字符处开始失败**（DirectoryNotFoundException）。
        ///   而加前缀访问长短路径都成功，包括 Exists / GetAttributes / FileInfo / Delete / FileStream。
        ///
        /// 所以结论是：配置只解决了"前缀被拒绝"，并不能让无前缀长路径工作。
        /// 正确做法就是统一加前缀（配置已在 build.ps1 里随产物生成）。
        /// </summary>
        public static string IoPath(string fullPath)
        {
            if (string.IsNullOrEmpty(fullPath)) return fullPath;
            if (fullPath.StartsWith(@"\\?\", StringComparison.Ordinal)) return fullPath;

            string s = fullPath.IndexOf('/') >= 0 ? fullPath.Replace('/', '\\') : fullPath;

            // \\?\ 只接受完全限定的路径，相对路径要先转绝对
            bool rooted = (s.Length >= 2 && s[1] == ':') || s.StartsWith(@"\\", StringComparison.Ordinal);
            if (!rooted)
            {
                try { s = Path.GetFullPath(s); }
                catch { return fullPath; }
            }
            return ToExtended(s);
        }

        // ---------------- 真实路径解析 ----------------

        private const uint FILE_READ_ATTRIBUTES = 0x0080;
        private const uint FILE_SHARE_READ = 0x00000001;
        private const uint FILE_SHARE_WRITE = 0x00000002;
        private const uint FILE_SHARE_DELETE = 0x00000004;
        private const uint OPEN_EXISTING = 3;
        private const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
        private const uint FILE_FLAG_OPEN_REPARSE_POINT = 0x00200000;
        private const uint VOLUME_NAME_DOS = 0x0;

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateFileW(
            string lpFileName, uint dwDesiredAccess, uint dwShareMode, IntPtr lpSecurityAttributes,
            uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern uint GetFinalPathNameByHandleW(IntPtr hFile, StringBuilder lpszFilePath,
            uint cchFilePath, uint dwFlags);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        /// <summary>
        /// 解析路径上的联接/符号链接，返回文件系统真实路径。
        /// 解析失败（路径不存在、无权限）时返回 null —— 调用方必须把 null 当作「无法验证」，
        /// 在安全判定中按最坏情况处理。
        /// </summary>
        public static string TryGetRealPath(string path)
        {
            IntPtr h = IntPtr.Zero;
            try
            {
                h = CreateFileW(IoPath(path), FILE_READ_ATTRIBUTES,
                    FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                    IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS, IntPtr.Zero);
                if (h == new IntPtr(-1)) return null;

                StringBuilder sb = new StringBuilder(1024);
                uint n = GetFinalPathNameByHandleW(h, sb, (uint)sb.Capacity, VOLUME_NAME_DOS);
                if (n == 0) return null;
                if (n >= sb.Capacity)
                {
                    sb = new StringBuilder((int)n + 2);
                    n = GetFinalPathNameByHandleW(h, sb, (uint)sb.Capacity, VOLUME_NAME_DOS);
                    if (n == 0) return null;
                }
                string result = sb.ToString();
                // 去掉 \\?\ 前缀
                if (result.StartsWith(@"\\?\UNC\", StringComparison.Ordinal))
                    result = @"\\" + result.Substring(8);
                else if (result.StartsWith(@"\\?\", StringComparison.Ordinal))
                    result = result.Substring(4);
                return result;
            }
            catch
            {
                return null;
            }
            finally
            {
                if (h != IntPtr.Zero && h != new IntPtr(-1)) CloseHandle(h);
            }
        }

        /// <summary>是否为目录（与重解析点无关）。</summary>
        public static bool IsDirectory(string path)
        {
            try
            {
                FileAttributes a = File.GetAttributes(IoPath(path));
                return (a & FileAttributes.Directory) == FileAttributes.Directory;
            }
            catch { return false; }
        }

        /// <summary>本身是否为重解析点（联接/符号链接/挂载点）。</summary>
        public static bool IsReparsePoint(string path)
        {
            try
            {
                FileAttributes a = File.GetAttributes(IoPath(path));
                return (a & FileAttributes.ReparsePoint) == FileAttributes.ReparsePoint;
            }
            catch { return false; }
        }

        /// <summary>取所在卷的盘符，如 "C:"。失败返回空串。</summary>
        public static string GetVolumeRoot(string path)
        {
            try
            {
                string full = Path.GetFullPath(path);
                string root = Path.GetPathRoot(full);
                return root == null ? string.Empty : root.TrimEnd('\\');
            }
            catch { return string.Empty; }
        }
    }
}
