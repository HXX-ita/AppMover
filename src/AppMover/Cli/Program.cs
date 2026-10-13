using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using AppMover.Core;

namespace AppMover
{
    /// <summary>
    /// 命令行入口（M1 阶段）。
    /// 界面（M3）会复用同一个内核，因此这里刻意把逻辑都放在 Core 里，
    /// Program.cs 只做参数解析与输出。
    /// </summary>
    public static class Program
    {
        public static int Main(string[] args)
        {
            try
            {
                if (args.Length == 0) { Help(); return 0; }
                string cmd = args[0].ToLowerInvariant();

                switch (cmd)
                {
                    case "volumes": return CmdVolumes();
                    case "rules": return CmdRules();
                    case "tree": return CmdTree(args);
                    case "classify": return CmdClassify(args);
                    case "scan": return CmdScan(args);
                    case "plan": return CmdPlan(args);
                    case "move": return CmdMove(args);
                    case "journal": return CmdJournal();
                    case "undo": return CmdUndo(args);
                    case "feedback": return CmdFeedback(args);
                    case "selftest": return SelfTest.Run();
                    default: Help(); return 1;
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("错误: " + ex.Message);
                return 1;
            }
        }

        private static void Help()
        {
            Console.WriteLine("AppMover 内核 (M1)");
            Console.WriteLine();
            Console.WriteLine("  volumes              列出所有卷与其作为搬移目标的资格");
            Console.WriteLine("  rules                打印判定规则表");
            Console.WriteLine("  tree <路径>          统计目录大小/文件数（容错）");
            Console.WriteLine("  classify <路径>      对单个目录给出分类与依据");
            Console.WriteLine("  scan [--json 文件]   扫描标准位置并分类，输出摘要");
            Console.WriteLine("  plan <路径>          规划一次搬移（只读，不改动任何文件）");
            Console.WriteLine("  move <路径>          执行搬移（复制-校验-改名-建联接-删旧）");
            Console.WriteLine("  journal              查看搬移记录（撤销依据）");
            Console.WriteLine("  undo <记录号>        撤销一次已完成的搬移");
            Console.WriteLine("  feedback             生成脱敏诊断报告，并给出预先填好的反馈链接");
            Console.WriteLine("                       选项: [--path <路径>] [--error \"信息\"] [--unknown] [--open]");
            Console.WriteLine("  selftest             内置回归自测");
            Console.WriteLine();
            Console.WriteLine("  plan/move 选项: [--to D:\\] [--force] [--dry-run]");
        }

        // ---------------- 搬移规划与执行 ----------------

        private static string ParseOption(string[] args, string name, string fallback)
        {
            for (int i = 1; i < args.Length; i++)
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                    return args[i + 1];
            return fallback;
        }

        private static bool HasFlag(string[] args, string name)
        {
            for (int i = 1; i < args.Length; i++)
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static string DefaultTargetRoot()
        {
            VolumeInfo v = Volumes.PickDefaultTarget(Volumes.Enumerate());
            return v == null ? null : v.Letter + "\\";
        }

        private static MovePlan MakePlan(string[] args, out string error)
        {
            error = null;
            if (args.Length < 2 || args[1].StartsWith("--", StringComparison.Ordinal))
            {
                error = "用法: " + args[0] + " <路径> [--to D:\\] [--force]";
                return null;
            }
            string source = PathUtil.StripExtended(args[1]);
            string to = ParseOption(args, "--to", null);
            if (to == null)
            {
                to = DefaultTargetRoot();
                if (to == null) { error = "本机没有可用的目标盘（需要另一块 NTFS 固定磁盘）"; return null; }
            }

            SoftwareIndex idx = SoftwareIndex.Load();
            Classifier cls = new Classifier(idx);
            return Relocator.BuildPlan(source, to, cls, idx, HasFlag(args, "--force"),
                HasFlag(args, "--allow-same-volume"));
        }

        private static void PrintPlan(MovePlan p)
        {
            Console.WriteLine("源目录    : " + p.Source);
            Console.WriteLine("目标位置  : " + p.Target);
            Console.WriteLine("大小      : " + (p.Bytes / 1048576.0).ToString("F2") + " MB / " + p.Files + " 文件");
            Console.WriteLine("判定      : " + p.Category + "（置信度 " + p.Confidence + "）");
            Console.WriteLine("判定依据  : " + p.Reasons);
            Console.WriteLine("目标盘可用: " + (p.TargetFreeBytes / 1073741824.0).ToString("F2") + " GB");
            Console.WriteLine("需要管理员: " + (p.NeedsAdmin ? "是" : "否") + "（当前" + (Security.IsElevated() ? "已提权" : "未提权") + "）");

            if (p.Occupying.Count > 0)
            {
                Console.WriteLine("正在占用  :");
                for (int i = 0; i < p.Occupying.Count; i++) Console.WriteLine("            - " + p.Occupying[i]);
            }
            if (p.Warnings.Count > 0)
            {
                Console.WriteLine("警告      :");
                for (int i = 0; i < p.Warnings.Count; i++) Console.WriteLine("            ! " + p.Warnings[i]);
            }
            if (p.Blockers.Count > 0)
            {
                Console.WriteLine("阻止项    :");
                for (int i = 0; i < p.Blockers.Count; i++) Console.WriteLine("            X " + p.Blockers[i]);
            }
            Console.WriteLine();
            Console.WriteLine(p.CanProceed ? "结论: 可以搬移" : "结论: 不能搬移");
        }

        private static int CmdPlan(string[] args)
        {
            string err;
            MovePlan p = MakePlan(args, out err);
            if (p == null) { Console.Error.WriteLine(err); return 1; }
            PrintPlan(p);
            return p.CanProceed ? 0 : 2;
        }

        private static int CmdMove(string[] args)
        {
            string err;
            MovePlan p = MakePlan(args, out err);
            if (p == null) { Console.Error.WriteLine(err); return 1; }

            PrintPlan(p);
            if (!p.CanProceed) return 2;

            bool dryRun = HasFlag(args, "--dry-run");
            Console.WriteLine(dryRun ? "=== 干跑 ===" : "=== 开始搬移 ===");
            MoveResult r = Relocator.Execute(p, dryRun, delegate (string s) { Console.WriteLine(s); });
            Console.WriteLine();
            Console.WriteLine("结果: " + r.Status + "  记录号: " + r.JournalId);
            Console.WriteLine(r.Message);
            return r.Success ? 0 : 3;
        }

        private static int CmdJournal()
        {
            List<JournalEntry> all = Journal.ReadAll();
            Console.WriteLine("日志文件: " + Journal.Path);
            Console.WriteLine("记录数  : " + all.Count);
            Console.WriteLine();
            if (all.Count == 0) return 0;

            Console.WriteLine("记录号              时间                  状态        大小      原路径 -> 目标路径");
            Console.WriteLine("------------------------------------------------------------------------------------------------");
            for (int i = all.Count - 1; i >= 0; i--)
            {
                JournalEntry e = all[i];
                Console.WriteLine(string.Format("{0,-19} {1,-21} {2,-11} {3,8:F2}MB  {4}  ->  {5}",
                    e.Id, SafeTime(e.Time), e.Status, e.Bytes / 1048576.0, e.Original, e.Target));
                if (!string.IsNullOrEmpty(e.Message)) Console.WriteLine("                                                                  " + e.Message);
            }
            return 0;
        }

        private static int CmdUndo(string[] args)
        {
            if (args.Length < 2) { Console.Error.WriteLine("用法: undo <记录号>（用 journal 查看记录号）"); return 1; }
            string id = args[1];

            List<JournalEntry> all = Journal.ReadAll();
            JournalEntry target = null;
            for (int i = 0; i < all.Count; i++) if (all[i].Id == id) target = all[i];
            if (target == null) { Console.Error.WriteLine("找不到记录: " + id); return 1; }

            Console.WriteLine("记录号  : " + target.Id);
            Console.WriteLine("原路径  : " + target.Original);
            Console.WriteLine("目标路径: " + target.Target);
            Console.WriteLine("状态    : " + target.Status);
            Console.WriteLine();
            Console.WriteLine("=== 开始撤销 ===");
            MoveResult r = Relocator.Undo(target, delegate (string s) { Console.WriteLine(s); });
            Console.WriteLine();
            Console.WriteLine("结果: " + r.Status);
            Console.WriteLine(r.Message);
            return r.Success ? 0 : 3;
        }

        private static int CmdFeedback(string[] args)
        {
            string path = ParseOption(args, "--path", null);
            string err = ParseOption(args, "--error", null);
            bool unknown = HasFlag(args, "--unknown");
            bool open = HasFlag(args, "--open");

            Console.WriteLine("=== 收集诊断信息 ===");
            Console.WriteLine("（只包含目录名/路径/判定结果/环境版本，不含任何文件内容；");
            Console.WriteLine("  路径中的用户名与计算机名会被替换为占位符）");
            Console.WriteLine();

            FeedbackReport r = Feedback.Collect(path, err, unknown,
                delegate (string s) { Console.WriteLine(s); });

            string dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "feedback");
            string jsonPath;
            string md = Feedback.WriteReport(r, dir, out jsonPath);

            Console.WriteLine();
            Console.WriteLine("报告已保存：");
            Console.WriteLine("  " + md);
            Console.WriteLine("  " + jsonPath);

            string title;
            if (!string.IsNullOrEmpty(r.PathInQuestion)) title = "[反馈] 目录判定：" + r.PathInQuestion;
            else if (!string.IsNullOrEmpty(err)) title = "[反馈] 运行报错";
            else title = "[反馈] 判定/兼容性反馈";

            string url = Feedback.BuildIssueUrl(title, Feedback.ToMarkdown(r));
            Console.WriteLine();
            Console.WriteLine("提交链接（内容已预填，请先过目再提交）：");
            Console.WriteLine("  " + url);

            if (!Feedback.IsRepoConfigured)
            {
                Console.WriteLine();
                Console.WriteLine("注意：反馈仓库尚未配置，链接中的 REPLACE-ME 需要替换为维护者的 GitHub 仓库。");
                Console.WriteLine("      配置方法：在程序目录放一个 feedback.json，内容为 {\"repo\":\"用户名/AppMover\"}");
            }

            if (open)
            {
                Console.WriteLine();
                Console.WriteLine(Feedback.OpenUrl(url) ? "已在浏览器中打开提交页面。" : "无法自动打开浏览器，请手动复制上面的链接。");
            }
            return 0;
        }

        private static string SafeTime(string t)
        {
            try { return DateTime.Parse(t).ToString("yyyy-MM-dd HH:mm:ss"); }
            catch { return t; }
        }

        private static int CmdVolumes()
        {
            List<VolumeInfo> vs = Volumes.Enumerate();
            Console.WriteLine("盘符  卷标            文件系统   类型       总容量GB   可用GB  可作目标");
            Console.WriteLine("--------------------------------------------------------------------------------");
            foreach (VolumeInfo v in vs)
            {
                Console.WriteLine(string.Format("{0,-5} {1,-15} {2,-10} {3,-10} {4,9:F1} {5,9:F1}  {6}",
                    v.Letter, Truncate(v.Label, 14), v.FileSystem, v.DriveType,
                    v.TotalBytes / 1073741824.0, v.FreeBytes / 1073741824.0,
                    v.CanHostJunction ? "是" : "否 (" + v.WhyNotHost() + ")"));
            }
            VolumeInfo def = Volumes.PickDefaultTarget(vs);
            Console.WriteLine();
            Console.WriteLine(def == null
                ? "没有可用的目标盘（需要另一块 NTFS 固定磁盘）。"
                : "默认目标盘: " + def.Letter + " （可用 " + (def.FreeBytes / 1073741824.0).ToString("F1") + " GB）");
            return 0;
        }

        private static int CmdRules()
        {
            Console.WriteLine("规则表由 Classifier 内嵌，此处仅统计条目数。");
            Console.WriteLine("（规则必须用真实数据回归验证，见 selftest 与 tools/make-report.ps1）");
            return 0;
        }

        private static int CmdTree(string[] args)
        {
            if (args.Length < 2) { Console.Error.WriteLine("用法: tree <路径>"); return 1; }
            string path = PathUtil.StripExtended(args[1]);
            if (!Directory.Exists(PathUtil.IoPath(path))) { Console.Error.WriteLine("目录不存在: " + path); return 1; }
            DateTime t0 = DateTime.UtcNow;
            TreeStats st = TreeWalker.Measure(path);
            double ms = (DateTime.UtcNow - t0).TotalMilliseconds;
            Console.WriteLine("路径    : " + path);
            Console.WriteLine("文件数  : " + st.Files);
            Console.WriteLine("目录数  : " + st.Dirs);
            Console.WriteLine("总大小  : " + (st.Bytes / 1048576.0).ToString("F2") + " MB");
            Console.WriteLine("链接项  : " + st.ReparsePoints);
            Console.WriteLine("跳过    : " + st.Denied + (st.IsComplete ? "（统计完整）" : "（有权限不足的条目）"));
            Console.WriteLine("最新写入: " + (st.MaxWriteKnown ? st.MaxWriteUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm") : "未知"));
            Console.WriteLine("耗时    : " + ms.ToString("F0") + " ms");
            return 0;
        }

        private static int CmdClassify(string[] args)
        {
            if (args.Length < 2) { Console.Error.WriteLine("用法: classify <路径>"); return 1; }
            string path = PathUtil.StripExtended(args[1]).TrimEnd('\\');
            if (!Directory.Exists(PathUtil.IoPath(path))) { Console.Error.WriteLine("目录不存在: " + path); return 1; }

            SoftwareIndex idx = SoftwareIndex.Load();
            Classifier cls = new Classifier(idx);
            TreeStats st = TreeWalker.Measure(path);
            Classification c = cls.Classify(path, Path.GetFileName(path), st, PathUtil.IsReparsePoint(path));

            Console.WriteLine("路径    : " + path);
            string real = PathUtil.TryGetRealPath(path);
            if (real != null && PathUtil.Normalize(real) != PathUtil.Normalize(path))
                Console.WriteLine("真实路径: " + real + "   <-- 该目录是一个联接");
            Console.WriteLine("位置类别: " + Classifier.GetKind(PathUtil.Normalize(path)));
            Console.WriteLine("大小    : " + (st.Bytes / 1048576.0).ToString("F2") + " MB / " + st.Files + " 文件");
            Console.WriteLine("分类    : " + c.CategoryName + "  (置信度 " + c.Confidence + ")");
            Console.WriteLine("依据    :");
            foreach (string r in c.Reasons) Console.WriteLine("          - " + r);

            if (c.Confidence == "low")
            {
                Console.WriteLine();
                Console.WriteLine("提示: 这个目录的判定把握不大（规则没见过它）。");
                Console.WriteLine("      如果判定有误，可以把这条信息反馈回来帮助改进规则：");
                Console.WriteLine("        AppMover.exe feedback --path \"" + path + "\"");
            }
            return 0;
        }

        private static int CmdScan(string[] args)
        {
            string jsonOut = null;
            for (int i = 1; i < args.Length; i++)
            {
                if (args[i] == "--json" && i + 1 < args.Length) { jsonOut = args[i + 1]; i++; }
            }

            Console.WriteLine("载入已安装软件与服务索引 ...");
            SoftwareIndex idx = SoftwareIndex.Load();
            Console.WriteLine("  卸载登记项: " + idx.UninstallEntryCount + "   服务: " + idx.ServiceCount);

            Classifier cls = new Classifier(idx);
            List<ScanRow> rows = Scanner.ScanAll(cls, delegate (string s) { Console.WriteLine(s); });

            Console.WriteLine();
            Console.WriteLine("=== 判定汇总 ===");
            PrintGroup(rows, "safe", "可安全搬移");
            PrintGroup(rows, "caution", "可搬但需小心");
            PrintGroup(rows, "forbidden", "禁止搬移");
            PrintGroup(rows, "junk", "疑似残留可清理");

            if (jsonOut != null)
            {
                WriteJson(jsonOut, rows);
                Console.WriteLine();
                Console.WriteLine("JSON 已写出: " + jsonOut);
            }
            return 0;
        }

        private static void PrintGroup(List<ScanRow> rows, string key, string title)
        {
            long n = 0; long bytes = 0;
            foreach (ScanRow r in rows) if (r.CategoryKey == key) { n++; bytes += r.Bytes; }
            Console.WriteLine(string.Format("  {0,-14} {1,4} 项  {2,9:F2} GB", title, n, bytes / 1073741824.0));
        }

        // ---------------- 极简 JSON 输出（避免引入依赖） ----------------

        private static string J(string s)
        {
            if (s == null) return "null";
            StringBuilder sb = new StringBuilder("\"");
            foreach (char ch in s)
            {
                switch (ch)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (ch < ' ') sb.Append("\\u").Append(((int)ch).ToString("x4"));
                        else sb.Append(ch);
                        break;
                }
            }
            return sb.Append('"').ToString();
        }

        private static void WriteJson(string file, List<ScanRow> rows)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("[\n");
            for (int i = 0; i < rows.Count; i++)
            {
                ScanRow r = rows[i];
                sb.Append("  {\"name\":").Append(J(r.Name));
                sb.Append(",\"path\":").Append(J(r.Path));
                sb.Append(",\"kind\":").Append(J(r.Kind));
                sb.Append(",\"bytes\":").Append(r.Bytes);
                sb.Append(",\"files\":").Append(r.Files);
                sb.Append(",\"denied\":").Append(r.Denied);
                sb.Append(",\"category\":").Append(J(r.CategoryKey));
                sb.Append(",\"categoryName\":").Append(J(r.Category));
                sb.Append(",\"confidence\":").Append(J(r.Confidence));
                sb.Append(",\"reasons\":").Append(J(r.Reasons));
                sb.Append("}");
                if (i < rows.Count - 1) sb.Append(",");
                sb.Append("\n");
            }
            sb.Append("]\n");
            File.WriteAllText(file, sb.ToString(), new UTF8Encoding(true));
        }

        private static string Truncate(string s, int n)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Length <= n ? s : s.Substring(0, n);
        }
    }

    // ScanRow 已移至 Core/Scanner.cs，与反馈用的「未知目录名收集」共用。
}
