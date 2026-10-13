using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;

namespace AppMover.Core
{
    /// <summary>复制与校验。</summary>
    public class CopyReport
    {
        public long SourceFiles, SourceBytes;
        public long TargetFiles, TargetBytes;
        public bool CountsMatch;
        public bool BytesMatch;
        public List<string> HashChecked = new List<string>();
        public List<string> HashMismatch = new List<string>();
        public List<string> MissingInTarget = new List<string>();
        public bool Verified;

        /// <summary>校验结论：数量与字节必须一致，且抽样哈希全部一致。</summary>
        public bool Ok
        {
            get { return CountsMatch && BytesMatch && HashMismatch.Count == 0 && MissingInTarget.Count == 0; }
        }
    }

    public static class Copier
    {
        /// <summary>
        /// 用 robocopy 复制目录树。
        ///
        /// 为什么用 robocopy 而不是自己写复制：它原生处理长路径、保留 ACL/时间戳、
        /// 有重试机制，而且是 Windows 上搬移大目录的标准工具。
        ///
        /// 退出码语义：&lt;8 表示成功（0=无变化，1=有文件复制，2=有多余文件…），
        /// &gt;=8 表示至少有一个文件未复制成功。
        /// </summary>
        public static int RunRobocopy(string source, string target, bool preserveAcl, Action<string> log)
        {
            Directory.CreateDirectory(target);

            string copyFlags = preserveAcl ? "/COPY:DATS" : "/COPY:DAT";
            string args = string.Format(
                "\"{0}\" \"{1}\" /E {2} /DCOPY:DAT /R:1 /W:1 /NFL /NDL /NP /NJH /NJS",
                source.TrimEnd('\\'), target.TrimEnd('\\'), copyFlags);

            int code = Proc.Run("robocopy", args, log, 0);

            // 保留 ACL 需要在目标上写安全描述符，非管理员时可能整体失败；降级重试
            if (code >= 8 && preserveAcl)
            {
                if (log != null) log("robocopy 保留 ACL 失败（退出码 " + code + "），降级为仅复制数据与时间戳重试 ...");
                args = string.Format(
                    "\"{0}\" \"{1}\" /E /COPY:DAT /DCOPY:DAT /R:1 /W:1 /NFL /NDL /NP /NJH /NJS",
                    source.TrimEnd('\\'), target.TrimEnd('\\'));
                code = Proc.Run("robocopy", args, log, 0);
            }
            return code;
        }

        /// <summary>逐项校验副本：数量、字节、以及若干大文件的 SHA256。</summary>
        public static CopyReport Verify(string source, string target, int hashSampleCount, long hashSampleMaxBytes, Action<string> log)
        {
            CopyReport r = new CopyReport();

            TreeStats s = TreeWalker.Measure(source);
            TreeStats t = TreeWalker.Measure(target);
            r.SourceFiles = s.Files; r.SourceBytes = s.Bytes;
            r.TargetFiles = t.Files; r.TargetBytes = t.Bytes;
            r.CountsMatch = s.Files == t.Files;
            r.BytesMatch = s.Bytes == t.Bytes;

            if (log != null)
                log(string.Format("  校验：源 {0} 文件/{1} 字节，目标 {2} 文件/{3} 字节",
                    s.Files, s.Bytes, t.Files, t.Bytes));

            if (!r.Ok) return r;      // 数量或字节已经不一致，无需继续

            // 抽样哈希：挑最大的若干文件（最容易在复制中出问题的也是大文件）
            List<FileEntry> biggest = new List<FileEntry>();
            CollectFiles(source, biggest, 4000);
            biggest.Sort(delegate (FileEntry a, FileEntry b) { return b.Size.CompareTo(a.Size); });

            long budget = hashSampleMaxBytes;
            int taken = 0;
            for (int i = 0; i < biggest.Count && taken < hashSampleCount; i++)
            {
                if (budget <= 0) break;
                FileEntry fe = biggest[i];
                string rel = fe.Path.Substring(source.TrimEnd('\\').Length).TrimStart('\\');
                string other = Path.Combine(target.TrimEnd('\\'), rel);
                if (!File.Exists(other))
                {
                    r.MissingInTarget.Add(rel);
                    continue;
                }
                string h1 = Sha256(fe.Path);
                string h2 = Sha256(other);
                if (h1 == null || h2 == null || h1 != h2) r.HashMismatch.Add(rel);
                else r.HashChecked.Add(rel);
                budget -= fe.Size;
                taken++;
            }

            if (log != null)
                log(string.Format("  抽样哈希：{0} 个一致，{1} 个不一致，{2} 个目标缺失",
                    r.HashChecked.Count, r.HashMismatch.Count, r.MissingInTarget.Count));

            return r;
        }

        private class FileEntry
        {
            public string Path;
            public long Size;
        }

        private static void CollectFiles(string root, List<FileEntry> list, int cap)
        {
            Stack<string> stack = new Stack<string>();
            stack.Push(root);
            while (stack.Count > 0 && list.Count < cap)
            {
                string cur = stack.Pop();
                string[] entries;
                try { entries = Directory.GetFileSystemEntries(PathUtil.IoPath(cur)); }
                catch { continue; }
                for (int i = 0; i < entries.Length; i++)
                {
                    try
                    {
                        FileAttributes a = File.GetAttributes(entries[i]);
                        if ((a & FileAttributes.ReparsePoint) == FileAttributes.ReparsePoint) continue;
                        if ((a & FileAttributes.Directory) == FileAttributes.Directory) stack.Push(entries[i]);
                        else
                        {
                            FileInfo fi = new FileInfo(entries[i]);
                            FileEntry fe = new FileEntry();
                            fe.Path = entries[i];
                            fe.Size = fi.Length;
                            list.Add(fe);
                            if (list.Count >= cap) return;
                        }
                    }
                    catch { }
                }
            }
        }

        internal static string Sha256(string path)
        {
            try
            {
                using (FileStream fs = new FileStream(PathUtil.IoPath(path), FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (SHA256 sha = SHA256.Create())
                {
                    byte[] hash = sha.ComputeHash(fs);
                    return BitConverter.ToString(hash).Replace("-", "");
                }
            }
            catch { return null; }
        }
    }
}
