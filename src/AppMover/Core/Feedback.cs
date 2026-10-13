using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace AppMover.Core
{
    /// <summary>一份反馈报告的全部内容。</summary>
    public class FeedbackReport
    {
        public string AppVersion = "";
        public string Os = "";
        public string Arch = "";
        public string Elevated = "";
        public string Time = "";
        public string PathInQuestion = "";
        public string Category = "";
        public string Reasons = "";
        public string LastError = "";
        public List<string> UnknownFolders = new List<string>();
        public List<string> Volumes = new List<string>();

        public string FileBaseName
        {
            get { return "feedback-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"); }
        }
    }

    /// <summary>
    /// 反馈收集与发送。
    ///
    /// 设计目标：**不依赖自建服务器也能把反馈收回来**。
    /// 因此后端直接用 GitHub Issues：
    ///   · 程序生成一份**已脱敏**的诊断报告（本地留档）
    ///   · 同时生成一个预先填好标题/正文/标签的 GitHub Issue 链接
    ///   · 用户点一下、看一眼、提交 —— 反馈就进了仓库的 issue 列表
    /// 这样维护者不需要运维任何服务，用户也不需要注册额外账号。
    ///
    /// 隐私：只收集「目录名、路径、判定结果、环境版本」这类结构化信息，
    /// **绝不读取或上传任何文件内容**；路径中的用户名与计算机名会被替换成占位符。
    /// </summary>
    public static class Feedback
    {
        /// <summary>
        /// TODO(维护者): 改成你自己的 GitHub 仓库（owner/repo），
        /// 或者在同目录放一个 feedback.json，内容如 {"repo":"owner/AppMover"} 来覆盖。
        /// </summary>
        public const string DefaultRepo = "REPLACE-ME/AppMover";

        public static string RepoSlug
        {
            get
            {
                try
                {
                    string cfg = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "feedback.json");
                    if (File.Exists(cfg))
                    {
                        Dictionary<string, string> m = JsonLite.ParseFlatObject(File.ReadAllText(cfg, Encoding.UTF8));
                        string r;
                        if (m.TryGetValue("repo", out r) && !string.IsNullOrEmpty(r)) return r;
                    }
                }
                catch { }
                return DefaultRepo;
            }
        }

        /// <summary>仓库地址还没配置时，提示维护者去改。</summary>
        public static bool IsRepoConfigured
        {
            get { return RepoSlug.IndexOf("REPLACE-ME", StringComparison.OrdinalIgnoreCase) < 0; }
        }

        // ---------------- 脱敏 ----------------

        /// <summary>
        /// 把路径里的人名/机器名替换成占位符。
        /// 保留目录名本身（那是改进规则真正需要的信息）。
        /// </summary>
        public static string Anonymize(string path)
        {
            if (string.IsNullOrEmpty(path)) return path;
            string s = path;

            string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrEmpty(profile))
                s = Replace(s, profile, @"C:\Users\<user>", StringComparison.OrdinalIgnoreCase);

            string user = Environment.UserName;
            if (!string.IsNullOrEmpty(user))
                s = Replace(s, user, "<user>", StringComparison.OrdinalIgnoreCase);

            string comp = Environment.MachineName;
            if (!string.IsNullOrEmpty(comp))
                s = Replace(s, comp, "<PC>", StringComparison.OrdinalIgnoreCase);

            return s;
        }

        private static string Replace(string s, string from, string to, StringComparison cmp)
        {
            if (string.IsNullOrEmpty(from)) return s;
            int i = s.IndexOf(from, cmp);
            if (i < 0) return s;
            StringBuilder sb = new StringBuilder();
            int pos = 0;
            while (i >= 0)
            {
                sb.Append(s, pos, i - pos);
                sb.Append(to);
                pos = i + from.Length;
                i = s.IndexOf(from, pos, cmp);
            }
            sb.Append(s, pos, s.Length - pos);
            return sb.ToString();
        }

        // ---------------- 采集 ----------------

        public static FeedbackReport Collect(string pathInQuestion, string lastError, bool scanUnknown, Action<string> log)
        {
            FeedbackReport r = new FeedbackReport();
            r.AppVersion = typeof(Feedback).Assembly.GetName().Version == null
                ? "0.1.0" : typeof(Feedback).Assembly.GetName().Version.ToString();
            r.Time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss zzz");
            r.Os = SafeOs();
            r.Arch = Environment.Is64BitOperatingSystem ? "x64" : "x86";
            r.Elevated = Security.IsElevated() ? "是" : "否";
            r.LastError = lastError;

            List<VolumeInfo> vols = Volumes.Enumerate();
            for (int i = 0; i < vols.Count; i++)
            {
                VolumeInfo v = vols[i];
                r.Volumes.Add(string.Format("{0} {1} {2} {3} 可用{4:F1}GB",
                    v.Letter, v.FileSystem, v.DriveType, v.CanHostJunction ? "可作目标" : "不可作目标",
                    v.FreeBytes / 1073741824.0));
            }

            if (!string.IsNullOrEmpty(pathInQuestion))
            {
                r.PathInQuestion = Anonymize(pathInQuestion);
                try
                {
                    SoftwareIndex idx = SoftwareIndex.Load();
                    Classifier cls = new Classifier(idx);
                    TreeStats st = TreeWalker.Measure(pathInQuestion);
                    Classification c = cls.Classify(pathInQuestion, Path.GetFileName(pathInQuestion), st,
                        PathUtil.IsReparsePoint(pathInQuestion));
                    r.Category = c.CategoryName + "（置信度 " + c.Confidence + "）";
                    r.Reasons = string.Join("；", c.Reasons.ToArray());
                }
                catch (Exception ex)
                {
                    r.Reasons = "分类时出错: " + ex.Message;
                }
            }

            if (scanUnknown)
            {
                if (log != null) log("扫描以收集判定把握不大的目录名 ...");
                try
                {
                    SoftwareIndex idx = SoftwareIndex.Load();
                    Classifier cls = new Classifier(idx);
                    List<ScanRow> rows = Scanner.ScanAll(cls, log);
                    for (int i = 0; i < rows.Count; i++)
                    {
                        ScanRow row = rows[i];
                        if (row.IsLowConfidence || row.CategoryKey == "junk")
                        {
                            r.UnknownFolders.Add(string.Format("{0} ｜ {1} ｜ {2} ｜ {3}",
                                Anonymize(row.Path), row.CategoryKey, row.Confidence, row.Reasons));
                        }
                    }
                    if (log != null) log("收集到 " + r.UnknownFolders.Count + " 条需要人工复核的目录。");
                }
                catch (Exception ex) { if (log != null) log("扫描失败: " + ex.Message); }
            }

            return r;
        }

        private static string SafeOs()
        {
            try
            {
                string v = Environment.OSVersion.VersionString;
                return v + " (NT " + Environment.OSVersion.Version.ToString() + ")";
            }
            catch { return "未知"; }
        }

        // ---------------- 输出 ----------------

        public static string ToMarkdown(FeedbackReport r)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("## 环境");
            sb.AppendLine();
            sb.AppendLine("| 项 | 值 |");
            sb.AppendLine("|---|---|");
            sb.AppendLine("| 程序版本 | " + r.AppVersion + " |");
            sb.AppendLine("| 系统 | " + r.Os + " |");
            sb.AppendLine("| 架构 | " + r.Arch + " |");
            sb.AppendLine("| 管理员权限 | " + r.Elevated + " |");
            sb.AppendLine("| 时间 | " + r.Time + " |");
            sb.AppendLine();
            sb.AppendLine("## 磁盘");
            sb.AppendLine();
            for (int i = 0; i < r.Volumes.Count; i++) sb.AppendLine("- " + r.Volumes[i]);
            sb.AppendLine();

            if (!string.IsNullOrEmpty(r.PathInQuestion))
            {
                sb.AppendLine("## 出问题的路径");
                sb.AppendLine();
                sb.AppendLine("```");
                sb.AppendLine(r.PathInQuestion);
                sb.AppendLine("```");
                sb.AppendLine();
                sb.AppendLine("- 判定: " + r.Category);
                sb.AppendLine("- 依据: " + r.Reasons);
                sb.AppendLine();
            }

            if (!string.IsNullOrEmpty(r.LastError))
            {
                sb.AppendLine("## 报错信息");
                sb.AppendLine();
                sb.AppendLine("```");
                sb.AppendLine(r.LastError);
                sb.AppendLine("```");
                sb.AppendLine();
            }

            if (r.UnknownFolders.Count > 0)
            {
                sb.AppendLine("## 判定把握不大的目录（" + r.UnknownFolders.Count + " 条）");
                sb.AppendLine();
                sb.AppendLine("路径 ｜ 判定 ｜ 置信度 ｜ 依据");
                sb.AppendLine();
                for (int i = 0; i < r.UnknownFolders.Count; i++) sb.AppendLine("- " + r.UnknownFolders[i]);
                sb.AppendLine();
            }

            sb.AppendLine("---");
            sb.AppendLine();
            sb.AppendLine("> 本报告由程序自动生成，只包含目录名/路径/判定结果/环境版本，**不含任何文件内容**；");
            sb.AppendLine("> 路径中的用户名与计算机名已替换为占位符。");
            return sb.ToString();
        }

        /// <summary>把报告写到磁盘，返回 (md 路径, json 路径)。</summary>
        public static string WriteReport(FeedbackReport r, string directory, out string jsonPath)
        {
            if (!Directory.Exists(directory)) Directory.CreateDirectory(directory);
            string md = Path.Combine(directory, r.FileBaseName + ".md");
            File.WriteAllText(md, ToMarkdown(r), new UTF8Encoding(true));

            jsonPath = Path.Combine(directory, r.FileBaseName + ".json");
            StringBuilder sb = new StringBuilder();
            sb.Append("{");
            JsonLite.AppendString(sb, "appVersion", r.AppVersion, true);
            JsonLite.AppendString(sb, "os", r.Os, false);
            JsonLite.AppendString(sb, "arch", r.Arch, false);
            JsonLite.AppendString(sb, "elevated", r.Elevated, false);
            JsonLite.AppendString(sb, "time", r.Time, false);
            JsonLite.AppendString(sb, "path", r.PathInQuestion, false);
            JsonLite.AppendString(sb, "category", r.Category, false);
            JsonLite.AppendString(sb, "reasons", r.Reasons, false);
            JsonLite.AppendString(sb, "lastError", r.LastError, false);
            JsonLite.AppendString(sb, "volumes", string.Join(" | ", r.Volumes.ToArray()), false);
            JsonLite.AppendString(sb, "unknownFolders", string.Join("\n", r.UnknownFolders.ToArray()), false);
            sb.Append("}");
            File.WriteAllText(jsonPath, sb.ToString(), new UTF8Encoding(false));

            return md;
        }

        /// <summary>
        /// 生成预先填好的 GitHub Issue 链接。
        /// 正文长度有上限（GitHub 对 URL 有限制），因此只放摘要，完整报告在本地文件里。
        /// </summary>
        public static string BuildIssueUrl(string title, string body)
        {
            string repo = RepoSlug;
            const int maxBody = 6000;
            if (body.Length > maxBody)
                body = body.Substring(0, maxBody) + "\n\n...（正文过长已截断，完整报告见本地生成的 feedback-*.md）";

            return "https://github.com/" + repo + "/issues/new"
                + "?title=" + Uri.EscapeDataString(title)
                + "&body=" + Uri.EscapeDataString(body)
                + "&labels=" + Uri.EscapeDataString("feedback");
        }

        /// <summary>用默认浏览器打开链接。失败返回 false。</summary>
        public static bool OpenUrl(string url)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(url);
                psi.UseShellExecute = true;
                Process.Start(psi);
                return true;
            }
            catch { return false; }
        }
    }
}
