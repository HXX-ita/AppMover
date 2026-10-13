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

        /// <summary>把路径变成 \\?\ 扩展形式，绕过 260 字符限制。必须是已规范化的绝对路径。</summary>
        public static string ToExtended(string fullPath)
        {
            if (string.IsNullOrEmpty(fullPath)) return fullPath;
            if (fullPath.StartsWith(@"\\?\", StringComparison.Ordinal)) return fullPath;
            if (fullPath.StartsWith(@"\\", StringComparison.Ordinal))
                return @"\\?\UNC\" + fullPath.Substring(2);
            return @"\\?\" + fullPath;
        }

        /// <summary>超过这个长度才需要扩展前缀。</summary>
        private const int ExtendedThreshold = 240;

        /// <summary>
        /// 交给文件系统 API 的实际路径。
        ///
        /// 为什么不无脑加 \\?\：.NET Framework 在默认（legacy path handling）配置下
        /// **会拒绝** \\?\ 前缀，直接抛异常 —— 实测中它让整棵树的枚举全部失败、
        /// 大小统计全变 0。所以短路径走普通形式，只有确实可能超长时才加前缀
        /// （超长场景由随产物发布的 AppMover.exe.config 打开长路径支持来兜底）。
        /// </summary>
        public static string IoPath(string fullPath)
        {
            if (string.IsNullOrEmpty(fullPath)) return fullPath;
            if (fullPath.Length < ExtendedThreshold) return fullPath;
            return ToExtended(fullPath);
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
                h = CreateFileW(path, FILE_READ_ATTRIBUTES,
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
                FileAttributes a = File.GetAttributes(path);
                return (a & FileAttributes.Directory) == FileAttributes.Directory;
            }
            catch { return false; }
        }

        /// <summary>本身是否为重解析点（联接/符号链接/挂载点）。</summary>
        public static bool IsReparsePoint(string path)
        {
            try
            {
                FileAttributes a = File.GetAttributes(path);
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
