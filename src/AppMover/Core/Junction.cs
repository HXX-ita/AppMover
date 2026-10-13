using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace AppMover.Core
{
    /// <summary>
    /// 目录联接（junction）的创建、读取与删除。
    ///
    /// 为什么不调用 `cmd /c mklink /J`：
    ///   1. 参数要经过 shell，存在转义与注入风险（路径里可能有空格、引号、&amp;）；
    ///   2. 需要额外起进程，错误信息也不好处理。
    /// 这里直接走 DeviceIoControl(FSCTL_SET_REPARSE_POINT)，与系统的 mklink 底层同一套机制。
    ///
    /// 联接对应用完全透明：访问原路径会被文件系统转到目标路径，路径字符串不变 ——
    /// 这正是「搬完软件还能用」的原理。
    /// </summary>
    public static class Junction
    {
        private const uint IO_REPARSE_TAG_MOUNT_POINT = 0xA0000003;

        private const uint GENERIC_WRITE = 0x40000000;
        private const uint GENERIC_READ = 0x80000000;
        private const uint FILE_SHARE_READ = 0x1;
        private const uint FILE_SHARE_WRITE = 0x2;
        private const uint FILE_SHARE_DELETE = 0x4;
        private const uint OPEN_EXISTING = 3;
        private const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
        private const uint FILE_FLAG_OPEN_REPARSE_POINT = 0x00200000;

        private const uint FSCTL_SET_REPARSE_POINT = 0x000900A4;
        private const uint FSCTL_GET_REPARSE_POINT = 0x000900A8;

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateFileW(string name, uint access, uint share, IntPtr sec,
            uint disposition, uint flags, IntPtr template);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DeviceIoControl(IntPtr h, uint code, byte[] inBuf, int inSize,
            byte[] outBuf, int outSize, out int returned, IntPtr overlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr h);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool RemoveDirectoryW(string path);

        /// <summary>
        /// 在 junctionPath 建立指向 targetPath 的目录联接。
        /// junctionPath 必须**尚不存在**（调用方应先确认，避免覆盖已有目录）。
        /// targetPath 必须是本地绝对路径，例如 "D:\Foo"。
        /// </summary>
        public static void Create(string junctionPath, string targetPath)
        {
            if (Directory.Exists(junctionPath) || File.Exists(junctionPath))
                throw new IOException("建立联接失败：目标位置已存在同名项 " + junctionPath);
            if (!Directory.Exists(targetPath))
                throw new IOException("建立联接失败：联接指向的目录不存在 " + targetPath);

            // 联接必须先有一个空目录作为载体
            Directory.CreateDirectory(junctionPath);

            // 替换名必须用 NT 命名空间形式 \??\D:\Foo；显示名用普通形式
            string substitute = @"\??\" + targetPath.TrimEnd('\\');
            string print = targetPath.TrimEnd('\\');

            byte[] subBytes = Encoding.Unicode.GetBytes(substitute);
            byte[] printBytes = Encoding.Unicode.GetBytes(print);

            int pathBufferLen = subBytes.Length + 2 + printBytes.Length + 2;
            int reparseDataLen = 8 + pathBufferLen;         // 4 个 ushort 偏移/长度字段
            int total = 8 + reparseDataLen;                 // 再加 REPARSE_DATA_BUFFER 头

            byte[] buf = new byte[total];
            PutUInt32(buf, 0, IO_REPARSE_TAG_MOUNT_POINT);
            PutUInt16(buf, 4, (ushort)reparseDataLen);
            PutUInt16(buf, 6, 0);                                  // Reserved
            PutUInt16(buf, 8, 0);                                  // SubstituteNameOffset
            PutUInt16(buf, 10, (ushort)subBytes.Length);            // SubstituteNameLength
            PutUInt16(buf, 12, (ushort)(subBytes.Length + 2));      // PrintNameOffset
            PutUInt16(buf, 14, (ushort)printBytes.Length);          // PrintNameLength
            Array.Copy(subBytes, 0, buf, 16, subBytes.Length);
            Array.Copy(printBytes, 0, buf, 16 + subBytes.Length + 2, printBytes.Length);

            IntPtr h = CreateFileW(junctionPath, GENERIC_WRITE,
                FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                IntPtr.Zero, OPEN_EXISTING,
                FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT, IntPtr.Zero);

            if (h == new IntPtr(-1))
            {
                int err = Marshal.GetLastWin32Error();
                TryCleanupEmptyDir(junctionPath);
                throw new IOException("建立联接失败：无法打开目录句柄（Win32 错误 " + err + "）。"
                    + "移入 Program Files 需要管理员权限。");
            }

            try
            {
                int returned;
                if (!DeviceIoControl(h, FSCTL_SET_REPARSE_POINT, buf, total, null, 0, out returned, IntPtr.Zero))
                {
                    int err = Marshal.GetLastWin32Error();
                    TryCleanupEmptyDir(junctionPath);
                    throw new IOException("建立联接失败：FSCTL_SET_REPARSE_POINT 返回 Win32 错误 " + err);
                }
            }
            finally
            {
                CloseHandle(h);
            }

            // 验证：真实路径必须解析到目标
            string real = PathUtil.TryGetRealPath(junctionPath);
            if (real == null || PathUtil.Normalize(real) != PathUtil.Normalize(targetPath))
            {
                TryCleanupEmptyDir(junctionPath);
                throw new IOException("建立联接后校验失败：真实路径为 "
                    + (real == null ? "(无法解析)" : real) + "，期望 " + targetPath);
            }
        }

        /// <summary>读取联接/符号链接指向的目标；不是重解析点或读取失败时返回 null。</summary>
        public static string TryGetTarget(string path)
        {
            IntPtr h = CreateFileW(path, GENERIC_READ,
                FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                IntPtr.Zero, OPEN_EXISTING,
                FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT, IntPtr.Zero);
            if (h == new IntPtr(-1)) return null;
            try
            {
                byte[] buf = new byte[16384];
                int returned;
                if (!DeviceIoControl(h, FSCTL_GET_REPARSE_POINT, null, 0, buf, buf.Length, out returned, IntPtr.Zero))
                    return null;
                if (returned < 16) return null;

                uint tag = GetUInt32(buf, 0);
                if (tag != IO_REPARSE_TAG_MOUNT_POINT && tag != 0xA000000C /* symlink */) return null;

                ushort subOff = GetUInt16(buf, 8);
                ushort subLen = GetUInt16(buf, 10);
                if (subLen == 0) return null;
                string name = Encoding.Unicode.GetString(buf, 16 + subOff, subLen);
                // \??\D:\Foo  ->  D:\Foo
                if (name.StartsWith(@"\??\", StringComparison.Ordinal)) name = name.Substring(4);
                return name;
            }
            catch { return null; }
            finally { CloseHandle(h); }
        }

        /// <summary>
        /// 删除联接本身。用 RemoveDirectory 而不是 Directory.Delete：
        /// 前者只摘掉重解析点，绝不会递归删到联接指向的真实数据。
        /// </summary>
        public static void Delete(string junctionPath)
        {
            if (!RemoveDirectoryW(PathUtil.IoPath(junctionPath)))
            {
                int err = Marshal.GetLastWin32Error();
                throw new IOException("删除联接失败（Win32 错误 " + err + "）：" + junctionPath);
            }
        }

        private static void TryCleanupEmptyDir(string dir)
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, false); }
            catch { }
        }

        private static void PutUInt16(byte[] b, int off, ushort v)
        {
            b[off] = (byte)(v & 0xFF);
            b[off + 1] = (byte)((v >> 8) & 0xFF);
        }

        private static void PutUInt32(byte[] b, int off, uint v)
        {
            b[off] = (byte)(v & 0xFF);
            b[off + 1] = (byte)((v >> 8) & 0xFF);
            b[off + 2] = (byte)((v >> 16) & 0xFF);
            b[off + 3] = (byte)((v >> 24) & 0xFF);
        }

        private static ushort GetUInt16(byte[] b, int off)
        {
            return (ushort)(b[off] | (b[off + 1] << 8));
        }

        private static uint GetUInt32(byte[] b, int off)
        {
            return (uint)(b[off] | (b[off + 1] << 8) | (b[off + 2] << 16) | (b[off + 3] << 24));
        }
    }
}
