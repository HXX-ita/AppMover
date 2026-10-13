using System;
using System.Collections.Generic;
using System.IO;

namespace AppMover.Core
{
    /// <summary>一条扫描结果。</summary>
    public class ScanRow
    {
        public string Path = "";
        public string Name = "";
        public string Kind = "";
        public long Files;
        public long Bytes;
        public long Denied;
        public string Category = "";
        public string CategoryKey = "";
        public string Confidence = "";
        public string Reasons = "";

        /// <summary>低置信度：规则拿不准，正是最需要人工复核（以及反馈回来改进规则）的那些。</summary>
        public bool IsLowConfidence
        {
            get { return Confidence == "low"; }
        }
    }

    /// <summary>
    /// 标准位置的扫描器。命令行 `scan` 与反馈用的「未知目录名收集」共用它，
    /// 避免两处逻辑各自漂移。
    /// </summary>
    public static class Scanner
    {
        /// <summary>C 盘根目录下属于系统的子目录，不作为可搬项列出。</summary>
        public static bool IsSystemRootChild(string name)
        {
            string[] skip = new string[]
            {
                "Windows", "Program Files", "Program Files (x86)", "ProgramData", "Users",
                "$Recycle.Bin", "System Volume Information", "Recovery", "PerfLogs",
                "$WinREAgent", "MSOCache", "Documents and Settings", "Config.Msi", "$SysReset",
                "Intel", "AMD", "NVIDIA", "$Windows.~BT", "$Windows.~WS", "OneDriveTemp",
            };
            for (int i = 0; i < skip.Length; i++)
                if (string.Equals(name, skip[i], StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>扫描各个标准位置，逐个判定。返回按大小降序排列的结果。</summary>
        public static List<ScanRow> ScanAll(Classifier cls, Action<string> log)
        {
            List<ScanRow> rows = new List<ScanRow>();
            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

            string[] roots = new string[]
            {
                @"C:\Program Files",
                @"C:\Program Files (x86)",
                @"C:\ProgramData",
                Path.Combine(userProfile, @"AppData\Local"),
                Path.Combine(userProfile, @"AppData\Roaming"),
                Path.Combine(userProfile, @"AppData\LocalLow"),
                userProfile,
                @"C:\",
            };

            foreach (string root in roots)
            {
                if (!Directory.Exists(PathUtil.IoPath(root))) continue;
                if (log != null) log("扫描 " + root + " ...");
                string[] children;
                try { children = Directory.GetDirectories(PathUtil.IoPath(root)); }
                catch { continue; }

                bool isDriveRoot = root.EndsWith(":\\", StringComparison.Ordinal);

                foreach (string rawChild in children)
                {
                    // 枚举是用 \\?\ 前缀做的（长路径必需），返回的路径会带上该前缀。
                    // 这里剥掉它，避免 \\?\ 泄漏到界面、日志与路径比较中。
                    string child = PathUtil.StripExtended(rawChild);
                    string name = Path.GetFileName(child);
                    if (root == userProfile && name == "AppData") continue;
                    if (isDriveRoot && IsSystemRootChild(name)) continue;

                    TreeStats st = TreeWalker.Measure(child);
                    Classification c = cls.Classify(child, name, st, PathUtil.IsReparsePoint(child));

                    ScanRow row = new ScanRow();
                    row.Path = child;
                    row.Name = name;
                    row.Kind = Classifier.GetKind(PathUtil.Normalize(child)).ToString();
                    row.Files = st.Files;
                    row.Bytes = st.Bytes;
                    row.Denied = st.Denied;
                    row.Category = c.CategoryName;
                    row.CategoryKey = c.Category.ToString().ToLowerInvariant();
                    row.Confidence = c.Confidence;
                    row.Reasons = string.Join("；", c.Reasons.ToArray());
                    rows.Add(row);
                }
            }

            rows.Sort(delegate (ScanRow a, ScanRow b) { return b.Bytes.CompareTo(a.Bytes); });
            return rows;
        }
    }
}
