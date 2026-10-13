using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace AppMover.Core
{
    public enum Category
    {
        Safe,       // 可安全搬移
        Caution,    // 可搬但需小心
        Forbidden,  // 禁止搬移
        Junk        // 疑似残留，建议清理（只建议，绝不自动删）
    }

    public class Classification
    {
        public Category Category = Category.Safe;
        public string Confidence = "medium";
        public List<string> Reasons = new List<string>();

        public string CategoryName
        {
            get
            {
                switch (Category)
                {
                    case Category.Safe: return "可安全搬移";
                    case Category.Caution: return "可搬但需小心";
                    case Category.Forbidden: return "禁止搬移";
                    default: return "疑似残留可清理";
                }
            }
        }
    }

    /// <summary>位置类别，决定同一份证据怎么解读。</summary>
    public enum PathKind
    {
        ProgramFiles, ProgramFilesX86, ProgramData,
        AppDataLocal, AppDataRoaming, AppDataLocalLow,
        UserProfile, DriveRoot, Other
    }

    internal class Rule
    {
        public Regex Pattern;
        public string Reason;
        public Rule(string pattern, string reason)
        {
            Pattern = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            Reason = reason;
        }
        public bool Matches(string s) { return Pattern.IsMatch(s); }
    }

    /// <summary>
    /// 判定引擎。
    ///
    /// 这里的规则表不是拍脑袋写的：先在真实机器上扫描、分类，再逐条人工审计输出，
    /// 修掉了三类系统性误判（共享组件被当垃圾、活跃软件数据被当垃圾、
    /// 32 位系统目录因路径假设错误漏网）。改动规则后必须重跑回归。
    ///
    /// **范围约定：只支持 Windows 装在 C 盘的情况。**
    /// 所以下面的规则直接以 c:\ 开头，不做盘符推导。这是**有意为之，不是疏漏**：
    /// 为一个不会出现的场景增加一层盘符间接，只会让规则表更难读。
    /// 若将来真要支持系统在别的盘，需要一并改动的地方是：
    /// 本文件的两张规则表与 GetKind、Security.NeedsElevation、
    /// Scanner 的扫描根目录、SoftwareIndex 的服务路径归一。
    /// </summary>
    public class Classifier
    {
        private readonly SoftwareIndex _index;

        public Classifier(SoftwareIndex index) { _index = index; }

        // ---------------- 规则表 ----------------

        private static readonly Rule[] Forbidden = new Rule[]
        {
            new Rule(@"^c:\\windows(\\|$)",                    "Windows 操作系统本体"),
            new Rule(@"^c:\\windows\\winsxs",                  "组件存储 WinSxS（含硬链接，动它会破坏系统）"),
            new Rule(@"^c:\\program files\\windowsapps",       "商店应用(MSIX)，ACL 受保护"),
            new Rule(@"^c:\\program files\\windows defender",  "Windows Defender 自身"),
            new Rule(@"^c:\\program files \(x86\)\\windows defender", "Windows Defender 自身"),
            new Rule(@"^c:\\programdata\\microsoft\\windows defender", "Windows Defender 数据"),
            new Rule(@"^c:\\program files\\windows nt",        "系统组件"),
            new Rule(@"^c:\\program files\\common files\\microsoft shared", "COM/Office 基础设施"),
            new Rule(@"^c:\\program files\\dotnet",            ".NET 运行时，被其他软件依赖"),
            new Rule(@"^c:\\program files \(x86\)\\microsoft\.net", ".NET Framework 组件"),
            new Rule(@"^c:\\program files\\windowspowershell", "系统 PowerShell"),
            new Rule(@"^c:\\program files\\windows kits",      "Windows SDK 组件"),
            new Rule(@"^c:\\programdata\\microsoft\\windows",  "系统外壳数据"),
            new Rule(@"^c:\\programdata\\microsoft\\edge",     "Edge 更新器目录，更新会重建"),
            new Rule(@"^c:\\programdata\\package cache",       "安装器包缓存，被卸载程序引用"),
            new Rule(@"^c:\\users\\[^\\]+\\appdata\\local\\packages", "UWP 应用数据，ACL 受保护"),
            new Rule(@"^c:\\users\\[^\\]+\\appdata\\local\\microsoft\\windows($|\\)", "系统外壳数据"),
            new Rule(@"^c:\\users\\[^\\]+\\appdata\\local\\temp", "系统临时目录"),
            new Rule(@"^c:\\\[\$]recycle\.bin",                "回收站"),
            new Rule(@"^c:\\system volume information",        "系统卷信息"),
            new Rule(@"^c:\\recovery",                         "恢复分区数据"),
            new Rule(@"^c:\\perflogs",                         "性能日志"),
            new Rule(@"^c:\\msocache",                         "Office 安装缓存"),
            new Rule(@"^c:\\config\.msi",                      "安装事务目录"),
            new Rule(@"^c:\\\[\$]winreagent",                  "Windows 恢复代理"),
            new Rule(@"^c:\\\[\$]sysreset",                    "系统重置数据"),
        };

        private static readonly Rule[] SharedNeverMove = new Rule[]
        {
            new Rule(@"\\common files$",          "共享组件目录（Common Files），被多个软件引用"),
            new Rule(@"\\nvidia( corporation)?$", "NVIDIA 驱动组件，搬动会影响驱动更新与卸载"),
            new Rule(@"\\amd$",                   "AMD 驱动组件"),
            new Rule(@"\\intel( corporation)?$",  "Intel 驱动组件"),
            new Rule(@"\\realtek",                "Realtek 驱动组件"),
            new Rule(@"\\modifiablewindowsapps$", "系统可修改应用目录"),
            new Rule(@"\\reference assemblies$",  ".NET 引用程序集，被编译工具依赖"),
            new Rule(@"\\uninstall information$", "安装/卸载信息"),
            new Rule(@"\\internet explorer$",     "系统组件 IE"),
            new Rule(@"\\windows mail$|\\windows media player$|\\windows multimedia platform$|\\windows portable devices$|\\windows photo viewer$|\\windows sidebar$|\\windows security$", "Windows 自带组件"),
            new Rule(@"^c:\\program files( \(x86\))?\\windows ?(nt|powershell|defender|mail|sidebar|photo viewer|portable devices|multimedia platform)$", "Windows 自带组件（32 位变体）"),
            new Rule(@"^c:\\program files( \(x86\))?\\microsoft$", "Microsoft 共享组件（含 Edge / WebView2）"),
            new Rule(@"^c:\\program files( \(x86\))?\\msbuild$",   "MSBuild 构建组件"),
            new Rule(@"\\installshield installation information$", "InstallShield 卸载信息，删除会导致无法卸载"),
            new Rule(@"^c:\\programdata\\(usoshared|usoprivate|microsoft|huorong|windows security health)$", "系统/安全软件数据目录"),
            // 以下条目来自真实反馈（feedback --unknown 收集到的「规则拿不准」目录）：
            // SoftwareDistribution 是 Windows 更新缓存、boost_interprocess 是运行中程序的共享内存、
            // Packages 与 Windows Master Store 属于系统商店数据 —— 都不该被当成普通应用数据。
            new Rule(@"^c:\\programdata\\(softwaredistribution|packages|ssh|boost_interprocess|windows master store|usoshared|usoprivate|windows update)$", "Windows 系统/更新/共享数据目录"),
            new Rule(@"^c:\\programdata\\regid\.1991-06\.com\.microsoft$", "Microsoft 组件的注册信息（被安装程序引用）"),
            new Rule(@"^c:\\users\\[^\\]+\\appdata\\(local|roaming|locallow)\\microsoft$", "Microsoft 共享数据，含系统组件与多软件运行时"),
            new Rule(@"^c:\\programdata\\microsoft$", "Microsoft 共享数据（系统外壳/组件）"),
        };

        private static readonly Regex CacheName = new Regex(
            @"^(pip|npm|npm-cache|yarn|pnpm|nuget|\.gradle|\.m2|\.cargo|go-build|temp|tmp|cache|caches|code cache|gpucache|shadercache|d3dscache|grshadercache|cachestorage|logs?|dxcache|glcache)$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly Regex OrphanName = new Regex(
            @"\((\d+)\)$|_old$|\.old$|\.bak$|^backup|^temp$|新建文件夹",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        // ---------------- 位置分类 ----------------

        public static PathKind GetKind(string normalizedPath)
        {
            string p = normalizedPath;
            if (p.StartsWith(@"c:\program files (x86)\", StringComparison.Ordinal)) return PathKind.ProgramFilesX86;
            if (p.StartsWith(@"c:\program files\", StringComparison.Ordinal)) return PathKind.ProgramFiles;
            if (p.StartsWith(@"c:\programdata\", StringComparison.Ordinal)) return PathKind.ProgramData;
            if (p.StartsWith(@"c:\users\", StringComparison.Ordinal))
            {
                if (p.Contains(@"\appdata\local\") || p.EndsWith(@"\appdata\local", StringComparison.Ordinal)) return PathKind.AppDataLocal;
                if (p.Contains(@"\appdata\roaming\") || p.EndsWith(@"\appdata\roaming", StringComparison.Ordinal)) return PathKind.AppDataRoaming;
                if (p.Contains(@"\appdata\locallow\") || p.EndsWith(@"\appdata\locallow", StringComparison.Ordinal)) return PathKind.AppDataLocalLow;
                // 去掉 c:\users\<user>\ 之后还有一层以上才算用户目录内容
                string rest = p.Substring(@"c:\users\".Length);
                int slash = rest.IndexOf('\\');
                if (slash > 0 && rest.Length > slash + 1) return PathKind.UserProfile;
                return PathKind.Other;
            }
            if (p.Length > 3 && p[1] == ':' && p[2] == '\\' && p.IndexOf('\\', 3) < 0) return PathKind.DriveRoot;
            return PathKind.Other;
        }

        // ---------------- 主判定 ----------------

        public Classification Classify(string path, string name, TreeStats stats, bool isReparsePoint)
        {
            Classification c = new Classification();
            string norm = PathUtil.Normalize(path);
            PathKind kind = GetKind(norm);
            string nameLower = (name ?? string.Empty).ToLowerInvariant();

            // 1) 硬黑名单 + 共享组件
            foreach (Rule r in Forbidden)
                if (r.Matches(norm)) { c.Category = Category.Forbidden; c.Confidence = "high"; c.Reasons.Add(r.Reason); return Finish(c); }
            foreach (Rule r in SharedNeverMove)
                if (r.Matches(norm)) { c.Category = Category.Forbidden; c.Confidence = "high"; c.Reasons.Add(r.Reason); return Finish(c); }

            // 1.5) 云同步目录（OneDrive 等）
            // 必须按「同步根」显式判断，不能依赖重解析点属性：Windows 对本进程屏蔽了
            // 云文件占位符的 ReparsePoint 位（实测属性 49 vs PowerShell 的 1073）。
            // 漏判的后果是把云目录当普通目录搬 —— 复制占位文件会触发全量下载。
            if (CloudSync.LooksLikeSyncRootName(nameLower))
            {
                c.Category = Category.Forbidden; c.Confidence = "high";
                c.Reasons.Add("名称符合已知云同步目录：搬移会触发云端文件的【全量下载】，可能瞬间占满磁盘与带宽");
                return Finish(c);
            }
            string syncRoot = CloudSync.MatchSyncRoot(norm);
            if (syncRoot != null)
            {
                c.Category = Category.Forbidden; c.Confidence = "high";
                c.Reasons.Add("位于云同步目录之内（同步根 " + syncRoot + "）：搬移会触发云端文件全量下载");
                return Finish(c);
            }
            string innerSync = CloudSync.ContainsSyncRoot(norm);
            if (innerSync != null)
            {
                c.Category = Category.Caution; c.Confidence = "high";
                c.Reasons.Add("该目录内部包含云同步根 " + innerSync + "：搬移会连带搬动它，需先排除");
            }
            // 按路径分段兜底：同步根被移动或未登记时，环境变量/注册表都指望不上
            string segSync = CloudSync.MatchSyncRootSegment(norm);
            if (segSync != null)
            {
                c.Category = Category.Forbidden; c.Confidence = "high";
                c.Reasons.Add("路径中包含云同步目录名「" + segSync + "」：搬移会触发云端文件全量下载");
                return Finish(c);
            }

            // 2) 本身就是重解析点 —— 禁止二次搬运
            if (isReparsePoint)
            {
                c.Category = Category.Forbidden; c.Confidence = "high";
                c.Reasons.Add("该目录本身已经是联接/符号链接，禁止二次搬运");
                return Finish(c);
            }

            // 3) 目录内含链接项
            if (stats != null && stats.ReparsePoints > 0)
            {
                c.Reasons.Add("目录内含 " + stats.ReparsePoints + " 个联接/符号链接项，复制会打断这些关系");
                if (stats.ReparsePoints > 20) { c.Category = Category.Caution; c.Confidence = "medium"; }
            }

            // 4) 统计不完整（有跳过的条目）→ 不敢下结论
            if (stats != null && !stats.IsComplete)
            {
                c.Reasons.Add("有 " + stats.Denied + " 个条目因权限不足未能读取，统计不完整");
                if (c.Category == Category.Safe) { c.Category = Category.Caution; c.Confidence = "low"; }
            }

            bool byLoc = _index.MatchesInstallLocation(norm);
            bool byName = _index.MatchesDisplayName(nameLower);
            bool hasService = _index.HasServiceUnder(norm);

            if (hasService)
            {
                c.Category = Category.Caution;
                c.Reasons.Add("该目录内含 Windows 服务程序，搬移前需停止服务");
            }

            switch (kind)
            {
                case PathKind.ProgramFiles:
                case PathKind.ProgramFilesX86:
                    if (byLoc || byName)
                    {
                        c.Category = Category.Caution;
                        c.Reasons.Add("Program Files 下的已安装软件（需管理员权限；自带更新器可能破坏联接）");
                    }
                    else if (stats != null && stats.Files == 0)
                    {
                        c.Category = Category.Junk; c.Confidence = "high";
                        c.Reasons.Add("空目录（不含任何文件）");
                    }
                    else if (OrphanName.IsMatch(nameLower))
                    {
                        c.Category = Category.Junk; c.Confidence = "medium";
                        c.Reasons.Add("无卸载登记且命名符合卸载残留特征");
                    }
                    else
                    {
                        // 关键：没有卸载登记 ≠ 是垃圾。共享组件、绿色软件都可能没有登记项。
                        c.Category = Category.Caution; c.Confidence = "low";
                        c.Reasons.Add("Program Files 下但注册表无对应卸载项：可能是共享组件或绿色软件，也可能是卸载残留，需人工判断");
                    }
                    break;

                case PathKind.AppDataLocal:
                case PathKind.AppDataRoaming:
                case PathKind.AppDataLocalLow:
                    // 审计结论：AppData 目录绝大多数不登记到卸载表，
                    // 「无卸载项」绝不能当作残留证据（初版因此把剪映、微信、游戏存档误判成垃圾）。
                    if (CacheName.IsMatch(nameLower))
                    {
                        c.Category = Category.Junk; c.Confidence = "medium";
                        c.Reasons.Add("缓存类目录，软件会自动重建：可安全清理，也可搬移到其他盘");
                    }
                    else if (byLoc || byName)
                    {
                        c.Reasons.Add("已登记软件的应用数据目录（搬移前请先退出该软件）");
                    }
                    else
                    {
                        c.Reasons.Add("应用数据目录（多数软件的数据目录不登记到卸载表，无登记不代表是残留）");
                    }
                    break;

                case PathKind.ProgramData:
                    if (CacheName.IsMatch(nameLower) || nameLower.Contains("package cache") || nameLower.Contains("installer cache"))
                    {
                        c.Category = Category.Junk; c.Confidence = "medium";
                        c.Reasons.Add("安装器包缓存，卸载程序可能引用，清理前建议确认");
                    }
                    else if (byLoc || byName)
                    {
                        c.Reasons.Add("已登记软件的数据目录");
                    }
                    else
                    {
                        c.Category = Category.Caution; c.Confidence = "low";
                        c.Reasons.Add("ProgramData 下无卸载登记：可能是驱动/共享组件或服务数据，搬移前需确认");
                    }
                    break;

                case PathKind.UserProfile:
                    c.Category = Category.Caution;
                    c.Reasons.Add("用户文档类目录，建议优先使用 Windows 自带「属性→位置」迁移功能");
                    break;
            }

            return Finish(c);
        }

        private static Classification Finish(Classification c)
        {
            if (c.Reasons.Count == 0) c.Reasons.Add("位于可搬位置，未发现禁止特征");
            return c;
        }
    }
}
