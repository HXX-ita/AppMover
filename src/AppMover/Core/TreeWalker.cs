using System;
using System.Collections.Generic;
using System.IO;

namespace AppMover.Core
{
    /// <summary>一个目录树的统计结果。</summary>
    public class TreeStats
    {
        public long Files;
        public long Dirs;
        public long Bytes;
        public long Denied;              // 无权限而跳过的条目数（>0 表示结果不完整）
        public long ReparsePoints;       // 内含的联接/符号链接数
        public DateTime MaxWriteUtc;     // 内部最新的写入时间（比目录自身时间可靠）
        public bool MaxWriteKnown;

        public bool IsComplete { get { return Denied == 0; } }
    }

    /// <summary>
    /// 容错的目录树遍历。
    ///
    /// 为什么不用 Directory.GetFiles(..., AllDirectories)：它在遇到第一个无权限目录时
    /// 会整棵树抛异常中断，而我们需要「部分失败也能给出结果，并如实报告跳过了多少」。
    ///
    /// 关键安全点：遇到重解析点**不下钻**。否则一个指向 C:\Windows 的联接会让
    /// 大小统计和后续删除操作跑到别的树上去。
    /// </summary>
    public static class TreeWalker
    {
        public static TreeStats Measure(string root)
        {
            TreeStats st = new TreeStats();
            Stack<string> stack = new Stack<string>();
            stack.Push(root);

            while (stack.Count > 0)
            {
                string cur = stack.Pop();
                string[] entries;
                try
                {
                    entries = Directory.GetFileSystemEntries(PathUtil.IoPath(cur));
                }
                catch
                {
                    st.Denied++;
                    continue;
                }

                for (int i = 0; i < entries.Length; i++)
                {
                    string e = entries[i];
                    try
                    {
                        FileAttributes attr = File.GetAttributes(PathUtil.IoPath(e));
                        if ((attr & FileAttributes.ReparsePoint) == FileAttributes.ReparsePoint)
                        {
                            st.ReparsePoints++;
                            continue;   // 不下钻
                        }
                        if ((attr & FileAttributes.Directory) == FileAttributes.Directory)
                        {
                            st.Dirs++;
                            stack.Push(e);
                        }
                        else
                        {
                            st.Files++;
                            try
                            {
                                FileInfo fi = new FileInfo(PathUtil.IoPath(e));
                                st.Bytes += fi.Length;
                                if (!st.MaxWriteKnown || fi.LastWriteTimeUtc > st.MaxWriteUtc)
                                {
                                    st.MaxWriteUtc = fi.LastWriteTimeUtc;
                                    st.MaxWriteKnown = true;
                                }
                            }
                            catch { st.Denied++; }
                        }
                    }
                    catch { st.Denied++; }
                }
            }
            return st;
        }

        /// <summary>
        /// 深度检查：目录内是否存在硬链接文件（NumberOfLinks &gt; 1）。
        /// 硬链接不能跨卷，复制会打断链接关系，因此这类目录禁止搬移。
        /// 只在真正搬移前的预检里调用（代价高）。
        /// </summary>
        public static bool ContainsHardlinks(string root, out int samples)
        {
            samples = 0;
            Stack<string> stack = new Stack<string>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                string cur = stack.Pop();
                string[] entries;
                try { entries = Directory.GetFileSystemEntries(PathUtil.IoPath(cur)); }
                catch { continue; }

                for (int i = 0; i < entries.Length; i++)
                {
                    string e = entries[i];
                    try
                    {
                        FileAttributes attr = File.GetAttributes(PathUtil.IoPath(e));
                        if ((attr & FileAttributes.ReparsePoint) == FileAttributes.ReparsePoint) continue;
                        if ((attr & FileAttributes.Directory) == FileAttributes.Directory) { stack.Push(e); continue; }
                        if (HardlinkUtil.GetLinkCount(e) > 1)
                        {
                            samples++;
                            if (samples >= 3) return true;
                        }
                    }
                    catch { }
                }
            }
            // 这里是真 bug 的修复点：曾经写成 return false。
            // 于是只含 1~2 个硬链接的目录会被判成"没有硬链接"，
            // 搬移照常进行，跨卷复制打断链接关系 —— 而这个小样本量恰恰是最常见的情况
            // （3 个以上才提前 return 的设计让 bug 只在少量硬链接时出现）。
            return samples > 0;
        }
    }
}
