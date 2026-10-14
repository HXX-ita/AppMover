using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using AppMover.Core;

namespace AppMover
{
    /// <summary>
    /// 内置回归自测。
    ///
    /// 这里的用例不是凑数的：每一条都对应一个真实踩过的坑，
    /// 尤其是规则校准阶段发现的误判。改动规则后必须跑这个。
    /// </summary>
    public static class SelfTest
    {
        private static int _pass;
        private static int _fail;
        private static string _group = "";

        public static int Run()
        {
            _pass = 0; _fail = 0;

            TestPathNormalize();
            TestIsUnderPrefixTrap();
            TestExtendedPath();
            TestServiceImagePath();
            TestForbiddenRules();
            TestSharedRules();
            TestAppDataIsNotJunk();
            TestCacheIsJunk();
            TestProgramFilesNoRegistry();
            TestEmptyFolderIsJunk();
            TestCloudSync();
            TestJunctionRoundTrip();
            TestOccupancyDetection();
            TestHardlinkDetection();
            TestLongPathHandling();
            TestGuardBlockers();
            TestSpaceRule();
            TestCrashRecovery();
            TestElevationRequired();
            TestDriveRootTarget();
            TestUndoRefusals();
            TestFullRelocateAndUndo();
            TestRealMachinePaths();
            TestScratchLocationUsable();

            Console.WriteLine();
            Console.WriteLine("==================================");
            Console.WriteLine("  通过 " + _pass + "   失败 " + _fail);
            Console.WriteLine("==================================");
            return _fail == 0 ? 0 : 1;
        }

        // ---------------- 断言 ----------------

        private static string _scratchRoot;

        /// <summary>
        /// 自测用的临时目录根。
        ///
        /// 这个方法是**被发布包打脸之后补的**：早先所有测试都直接在可执行文件所在目录下
        /// 建临时目录。本地与 CI 都在工作区（D 盘）里跑，一切正常；但有人把发布包解压到
        /// C:\Users\...\AppData\Local\Temp\ 再跑 selftest，临时目录就落在系统临时目录里，
        /// 而**规则正确地判定「系统临时目录 → 禁止搬移」** —— 于是搬移类测试被自己的规则
        /// 拦住，报出 6 项「失败」。那不是代码坏了，是测试不是位置无关的。
        ///
        /// 做法：优先用可执行文件所在目录（构建与 CI 场景）；若该位置会被判定为禁止搬移，
        /// 就退到用户目录下的专门子目录（不在 AppData\Local\Temp 下，因此只算「用户目录」
        /// 而非「系统临时目录」）。判断复用产品自己的规则，而不是另写一套逻辑。
        /// </summary>
        private static string ScratchRoot()
        {
            if (_scratchRoot != null) return _scratchRoot;

            string exeDir = AppDomain.CurrentDomain.BaseDirectory;
            if (!IsForbiddenScratch(exeDir))
            {
                _scratchRoot = exeDir;
                return _scratchRoot;
            }

            string alt = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppMover-selftest");
            _scratchRoot = alt;
            Console.WriteLine("  （可执行文件位于被禁止搬移的位置，自测临时目录改用 " + alt + "）");
            return _scratchRoot;
        }

        /// <summary>该位置是否会被判定为「禁止搬移」—— 那样就不能拿来当搬移测试的现场。</summary>
        private static bool IsForbiddenScratch(string dir)
        {
            try
            {
                Classifier cls = new Classifier(new SoftwareIndex());
                Classification c = cls.Classify(dir, Path.GetFileName(dir.TrimEnd('\\')),
                    Stats(1, 1024), false);
                return c.Category == Category.Forbidden;
            }
            catch { return false; }
        }

        /// <summary>确认自测临时目录本身可用 —— 专治「测试不是位置无关」这个问题。</summary>
        private static void TestScratchLocationUsable()
        {
            Group("自测环境自身（防止测试不是位置无关的）");
            string root = ScratchRoot();
            Check(!IsForbiddenScratch(root), "自测临时目录不会被判定为禁止搬移：" + root);
            try
            {
                string probe = Path.Combine(root, "_selftest_scratch_probe");
                Directory.CreateDirectory(PathUtil.IoPath(probe));
                File.WriteAllText(PathUtil.IoPath(Path.Combine(probe, "a.txt")), "a");
                Check(Directory.Exists(PathUtil.IoPath(probe)), "自测临时目录可写");
                List<string> j = new List<string>();
                SafeDelete.DeleteTree(probe, j);
                Check(!Directory.Exists(PathUtil.IoPath(probe)), "自测临时目录可清理");
            }
            catch (Exception ex)
            {
                _fail++;
                Console.WriteLine("  FAIL 自测临时目录不可用: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static void Group(string name)
        {
            _group = name;
            Console.WriteLine();
            Console.WriteLine("[" + name + "]");
        }

        private static void Check(bool ok, string what)
        {
            if (ok) { _pass++; Console.WriteLine("  ok   " + what); }
            else { _fail++; Console.WriteLine("  FAIL " + what); }
        }

        private static void Eq(string actual, string expected, string what)
        {
            bool ok = string.Equals(actual, expected, StringComparison.Ordinal);
            if (ok) { _pass++; Console.WriteLine("  ok   " + what); }
            else
            {
                _fail++;
                Console.WriteLine("  FAIL " + what);
                Console.WriteLine("         期望: " + expected);
                Console.WriteLine("         实际: " + actual);
            }
        }

        // ---------------- 用例 ----------------

        private static void TestPathNormalize()
        {
            Group("路径规范化");
            Eq(PathUtil.Normalize(@"C:\Windows\"), @"c:\windows", @"去尾部反斜杠并转小写");
            Eq(PathUtil.Normalize("C:/Program Files/"), @"c:\program files", "正斜杠统一为反斜杠");
            Eq(PathUtil.Normalize(@"C:\"), @"c:\", "根路径不被削成 c:");
            Eq(PathUtil.Normalize("  C:\\Windows  "), @"c:\windows", "去首尾空白");
        }

        private static void TestIsUnderPrefixTrap()
        {
            Group("前缀陷阱（C:\\WindowsOld 不属于 C:\\Windows）");
            Check(PathUtil.IsUnder(@"C:\Windows\System32", @"C:\Windows"), "System32 在 Windows 之下");
            Check(!PathUtil.IsUnder(@"C:\WindowsOld", @"C:\Windows"), "WindowsOld 不在 Windows 之下");
            Check(!PathUtil.IsUnder(@"C:\WindowsX\a", @"C:\Windows"), "WindowsX 不在 Windows 之下");
            Check(PathUtil.IsUnder(@"C:\Windows", @"C:\Windows"), "自身算在自身之下");
        }

        private static void TestExtendedPath()
        {
            Group("长路径前缀");
            Eq(PathUtil.ToExtended(@"C:\a\b"), @"\\?\C:\a\b", "本地路径加 \\?\\ 前缀");
            Eq(PathUtil.ToExtended(@"\\server\share\a"), @"\\?\UNC\server\share\a", @"UNC 路径转 \\?\UNC 形式");
            Eq(PathUtil.ToExtended(@"\\?\C:\a"), @"\\?\C:\a", "已有前缀不重复添加");
        }

        private static void TestServiceImagePath()
        {
            Group("服务 ImagePath 归一");
            Eq(SoftwareIndex.NormalizeImagePath("\"C:\\Program Files\\Foo\\svc.exe\" -arg"),
               @"c:\program files\foo\svc.exe", "带引号并带参数");
            Eq(SoftwareIndex.NormalizeImagePath(@"C:\WINDOWS\system32\svchost.exe -k netsvcs"),
               @"c:\windows\system32\svchost.exe", "无引号带参数");
            Eq(SoftwareIndex.NormalizeImagePath(@"\SystemRoot\System32\drivers\x.sys"),
               @"c:\windows\system32\drivers\x.sys", "内核 SystemRoot 前缀");
            Eq(SoftwareIndex.NormalizeImagePath(@"\??\C:\driver.sys"),
               @"c:\driver.sys", "\\??\\ 前缀");
        }

        private static Classifier NewClassifier() { return new Classifier(new SoftwareIndex()); }

        private static TreeStats Stats(long files, long bytes)
        {
            TreeStats s = new TreeStats();
            s.Files = files; s.Bytes = bytes; s.Dirs = 1;
            return s;
        }

        private static Category CatOf(string path, long files)
        {
            return NewClassifier().Classify(path, Path.GetFileName(path), Stats(files, files * 1024), false).Category;
        }

        private static void TestForbiddenRules()
        {
            Group("禁止搬移（硬黑名单）");
            Check(CatOf(@"C:\Windows", 100) == Category.Forbidden, @"C:\Windows");
            Check(CatOf(@"C:\Windows\System32", 100) == Category.Forbidden, @"C:\Windows\System32");
            Check(CatOf(@"C:\Program Files\WindowsApps", 100) == Category.Forbidden, @"WindowsApps (MSIX)");
            Check(CatOf(@"C:\Program Files\dotnet", 100) == Category.Forbidden, @"Program Files\dotnet");
            Check(CatOf(@"C:\Users\bob\AppData\Local\Packages", 100) == Category.Forbidden, "UWP Packages");
            // 回归：初版规则 32 位变体漏网，被误判为「疑似残留」
            Check(CatOf(@"C:\Program Files (x86)\WindowsPowerShell", 147) == Category.Forbidden,
                  @"Program Files (x86)\WindowsPowerShell（无空格，曾经的漏网 bug）");
            Check(CatOf(@"C:\Program Files (x86)\Windows Defender", 100) == Category.Forbidden, "32 位 Windows Defender");
            Check(CatOf(@"C:\Program Files (x86)\Microsoft.NET", 100) == Category.Forbidden, "32 位 Microsoft.NET");
        }

        private static void TestSharedRules()
        {
            Group("共享组件 / 厂商目录（是「禁止」而不是「垃圾」）");
            // 回归：初版把这两个当成卸载残留
            Check(CatOf(@"C:\Program Files\Common Files", 394) == Category.Forbidden, "Common Files");
            Check(CatOf(@"C:\ProgramData\NVIDIA Corporation", 1065) == Category.Forbidden, "NVIDIA Corporation");
            Check(CatOf(@"C:\ProgramData\Microsoft", 100) == Category.Forbidden, @"ProgramData\Microsoft");
            Check(CatOf(@"C:\Users\bob\AppData\Local\Microsoft", 100) == Category.Forbidden, @"AppData\Local\Microsoft");
            Check(CatOf(@"C:\Program Files (x86)\InstallShield Installation Information", 81) == Category.Forbidden,
                  "InstallShield 卸载信息（删了会导致无法卸载）");
            Check(CatOf(@"C:\ProgramData\USOShared", 185) == Category.Forbidden, "USOShared（系统更新数据）");
            // 以下三条来自真实反馈闭环：用户运行 feedback --unknown 收集到这些"规则拿不准"的目录，
            // 据此补规则，并在这里固化成回归用例。
            Check(CatOf(@"C:\ProgramData\SoftwareDistribution", 100) == Category.Forbidden,
                  "SoftwareDistribution（Windows 更新缓存，反馈发现）");
            Check(CatOf(@"C:\ProgramData\boost_interprocess", 100) == Category.Forbidden,
                  "boost_interprocess（运行中程序的共享内存，反馈发现）");
            Check(CatOf(@"C:\ProgramData\Packages", 100) == Category.Forbidden,
                  @"ProgramData\Packages（商店数据，反馈发现）");
        }

        private static void TestAppDataIsNotJunk()
        {
            Group("AppData 数据目录不是垃圾（最重要的回归）");
            // 回归：初版把剪映、微信、米哈游、游戏存档全判成了「疑似残留」
            Check(CatOf(@"C:\Users\bob\AppData\Local\JianyingPro", 50212) == Category.Safe, "剪映 JianyingPro");
            Check(CatOf(@"C:\Users\bob\AppData\Roaming\Tencent", 5096) == Category.Safe, "腾讯 Tencent");
            Check(CatOf(@"C:\Users\bob\AppData\Local\miHoYo", 738) == Category.Safe, "米哈游 miHoYo");
            Check(CatOf(@"C:\Users\bob\AppData\Local\Larian Studios", 308) == Category.Safe, "博德之门存档");
            Check(CatOf(@"C:\Users\bob\AppData\Local\Quark", 10567) == Category.Safe, "夸克 Quark");
            Check(CatOf(@"C:\Users\bob\AppData\Local\Programs", 9891) == Category.Safe, "用户级程序目录");
        }

        private static void TestCacheIsJunk()
        {
            Group("缓存目录才算可清理");
            Check(CatOf(@"C:\Users\bob\AppData\Local\pip", 1022) == Category.Junk, "pip 缓存");
            Check(CatOf(@"C:\Users\bob\AppData\Local\Yarn", 17195) == Category.Junk, "Yarn 缓存");
            Check(CatOf(@"C:\Users\bob\AppData\Local\npm-cache", 14) == Category.Junk, "npm 缓存");
            Check(CatOf(@"C:\Users\bob\AppData\Local\D3DSCache", 7) == Category.Junk, "D3D 着色器缓存");

            Classification c = NewClassifier().Classify(@"C:\Users\bob\AppData\Local\pip", "pip", Stats(10, 1024), false);
            Check(c.Confidence == "medium", "缓存类判定置信度为 medium（不说满话）");
        }

        private static void TestProgramFilesNoRegistry()
        {
            Group("Program Files 下无卸载登记 ≠ 垃圾");
            // 回归：初版直接判为 junk，会把共享组件和绿色软件误报
            Category c = CatOf(@"C:\Program Files\SomeUnknownApp", 500);
            Check(c == Category.Caution, "无卸载登记且非空 → 需小心（人工判断），而不是垃圾");
            Check(c != Category.Junk, "确认没有被判成垃圾");

            Classification cls = NewClassifier().Classify(@"C:\Program Files\SomeUnknownApp", "SomeUnknownApp", Stats(500, 1048576), false);
            Check(cls.Confidence == "low", "证据不足时置信度为 low");
        }

        private static void TestEmptyFolderIsJunk()
        {
            Group("空目录");
            Check(CatOf(@"C:\Program Files\Tencent", 0) == Category.Junk, "空目录判为可清理（高置信）");
            Classification c = NewClassifier().Classify(@"C:\Program Files\Tencent", "Tencent", Stats(0, 0), false);
            Check(c.Confidence == "high", "空目录置信度为 high");
        }

        private static void TestCloudSync()
        {
            Group("云同步目录（Windows 屏蔽了它的重解析点属性，必须靠同步根判断）");
            Check(CatOf(@"C:\Users\bob\OneDrive", 10) == Category.Forbidden, "OneDrive 根目录");
            Check(CatOf(@"C:\Users\bob\OneDrive\Documents", 10) == Category.Forbidden, "OneDrive 内部子目录");
            Check(CatOf(@"C:\Users\bob\OneDrive - Contoso", 10) == Category.Forbidden, "OneDrive 企业版（带后缀）");
            Check(CatOf(@"C:\Users\bob\Dropbox", 10) == Category.Forbidden, "Dropbox");
            Check(CatOf(@"C:\Users\bob\Google Drive", 10) == Category.Forbidden, "Google Drive");

            Classification c = NewClassifier().Classify(@"C:\Users\bob\OneDrive", "OneDrive", Stats(10, 1024), false);
            Check(c.Reasons[0].Contains("全量下载"), "理由中说明了「全量下载」这一具体后果");

            // 真实机器回归：本机上 OneDrive 确实存在，且本进程**看不到**它的重解析点属性。
            // 这个用例的意义就是证明：判定不依赖那个被屏蔽的属性位。
            string real = Environment.GetEnvironmentVariable("OneDrive");
            if (!string.IsNullOrEmpty(real) && Directory.Exists(real))
            {
                Check(!PathUtil.IsReparsePoint(real),
                      "前提确认：本进程读不到 " + real + " 的重解析点属性（所以不能依赖它）");
                Classification rc = NewClassifier().Classify(real, Path.GetFileName(real),
                    Stats(0, 0), PathUtil.IsReparsePoint(real));
                Check(rc.Category == Category.Forbidden,
                      "真实 OneDrive 路径仍被正确判为禁止搬移");
            }
            else
            {
                Console.WriteLine("  skip 本机没有 OneDrive，跳过真实路径回归");
            }
        }

        private static void TestJunctionRoundTrip()
        {
            Group("目录联接 创建/读取/删除 往返（搬移引擎的核心机制）");
            string baseDir = Path.Combine(ScratchRoot(), "_selftest_junction");
            string link = Path.Combine(baseDir, "link");
            try
            {
                if (Directory.Exists(baseDir)) Directory.Delete(baseDir, true);
                string target = Path.Combine(baseDir, "target");
                Directory.CreateDirectory(target);
                File.WriteAllText(Path.Combine(target, "probe.txt"), "hello-junction");

                Junction.Create(link, target);

                Check(PathUtil.IsReparsePoint(link), "建立后：被识别为重解析点");
                string t = Junction.TryGetTarget(link);
                Check(t != null && PathUtil.Normalize(t) == PathUtil.Normalize(target),
                      "建立后：读回联接目标 = " + (t == null ? "null" : t));
                Check(File.Exists(Path.Combine(link, "probe.txt")), "建立后：通过联接能读到目标文件");
                Check(File.ReadAllText(Path.Combine(link, "probe.txt")) == "hello-junction",
                      "建立后：通过联接读到的内容正确");

                string real = PathUtil.TryGetRealPath(link);
                Check(real != null && PathUtil.Normalize(real) == PathUtil.Normalize(target),
                      "建立后：真实路径解析到目标（用于绕开联接的安全检查）");

                // 关键安全点：删除联接绝不能连带删除目标数据
                Junction.Delete(link);
                Check(!PathUtil.IsReparsePoint(link), "删除后：联接已不存在");
                Check(Directory.Exists(target), "删除后：**目标目录仍然存在**（没有被连带删除）");
                Check(File.Exists(Path.Combine(target, "probe.txt")), "删除后：目标里的文件完好");
                Check(File.ReadAllText(Path.Combine(target, "probe.txt")) == "hello-junction",
                      "删除后：目标文件内容未被破坏");
            }
            catch (Exception ex)
            {
                _fail++;
                Console.WriteLine("  FAIL 联接往返异常: " + ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                // 先摘联接，再递归删除，避免递归删到联接指向的数据
                try { if (Directory.Exists(link) && PathUtil.IsReparsePoint(link)) Junction.Delete(link); }
                catch { }
                try { if (Directory.Exists(baseDir)) Directory.Delete(baseDir, true); }
                catch { }
            }
        }

        /// <summary>
        /// 完整搬移 + 撤销的端到端回归。
        ///
        /// 这是整个软件最核心的行为：复制 → 校验 → 改名让位 → 建联接 → 验联接 → 删旧，
        /// 以及反向的撤销。用真实的假软件目录跑，不做任何桩替换。
        /// 因为本机只有 C: 与 D: 两个卷，而测试数据放在 D 盘，所以开启同卷许可
        /// （真实场景是跨卷，跨卷复制路径已由 289 MB 的真实搬迁验证过）。
        /// </summary>
        private static void TestFullRelocateAndUndo()
        {
            Group("端到端：完整搬移 + 撤销");
            string baseDir = Path.Combine(ScratchRoot(), "_selftest_move");
            string journalPath = Path.Combine(baseDir, "journal.jsonl");
            string oldJournalEnv = Environment.GetEnvironmentVariable("APPMOVER_JOURNAL");
            try
            {
                if (Directory.Exists(baseDir)) Directory.Delete(baseDir, true);
                string srcRoot = Path.Combine(baseDir, "srcvol");
                string dstRoot = Path.Combine(baseDir, "dstvol");
                string app = Path.Combine(srcRoot, "FakeApp");
                Directory.CreateDirectory(Path.Combine(app, "bin"));
                Directory.CreateDirectory(Path.Combine(app, "data"));
                Directory.CreateDirectory(dstRoot);
                File.WriteAllText(Path.Combine(app, "bin", "app.exe"), "MZ fake");
                File.WriteAllText(Path.Combine(app, "readme.txt"), "端到端验证");

                byte[] blob = new byte[512 * 1024];
                new Random(1234).NextBytes(blob);
                File.WriteAllBytes(Path.Combine(app, "data", "blob.bin"), blob);
                string originalHash = Copier.Sha256(Path.Combine(app, "data", "blob.bin"));

                Environment.SetEnvironmentVariable("APPMOVER_JOURNAL", journalPath);

                Classifier cls = new Classifier(new SoftwareIndex());
                MovePlan plan = Relocator.BuildPlan(app, dstRoot, cls, null, false, true);
                Check(plan.CanProceed, "规划通过（若有阻止项: " + string.Join("；", plan.Blockers.ToArray()) + "）");
                if (!plan.CanProceed) return;

                MoveResult mv = Relocator.Execute(plan, false, null);
                Check(mv.Success, "搬移执行成功（" + mv.Status + "）");
                Check(PathUtil.IsReparsePoint(app), "原位置变为目录联接");
                Check(File.Exists(Path.Combine(app, "readme.txt")), "通过联接可读到文件");
                Check(File.ReadAllText(Path.Combine(app, "readme.txt")) == "端到端验证", "通过联接读到的内容正确");
                Check(Copier.Sha256(Path.Combine(app, "data", "blob.bin")) == originalHash,
                      "通过联接读到的大文件哈希与搬移前一致");
                Check(string.Equals(PathUtil.Normalize(PathUtil.TryGetRealPath(app)), PathUtil.Normalize(plan.Target)),
                      "联接指向目标目录");
                Check((GetDirCount(srcRoot)) == 1, "旧副本已删除（源卷只剩这个联接）");

                // 撤销
                List<JournalEntry> all = Journal.ReadAll();
                Check(all.Count >= 1, "撤销日志已写入（" + all.Count + " 条）");
                if (all.Count == 0) return;
                JournalEntry e = all[all.Count - 1];
                Check(e.Status == "completed", "记录状态为 completed");
                // 真实软件搬移之后才发现：UpdateStatus 是从磁盘重读记录再写回，
                // 所以 Execute 在内存里设的 junctionCreated 一直没被保存，日志里永远是 false。
                Check(e.JunctionCreated, "日志记录了「已建立联接」（这个字段曾经一直是 false）");

                MoveResult un = Relocator.Undo(e, null);
                Check(un.Success, "撤销执行成功（" + un.Status + "）");
                if (!un.Success) { Console.WriteLine("         撤销失败原因: " + un.Message); return; }
                Check(!PathUtil.IsReparsePoint(app), "撤销后原位置不再是联接（是真实目录）");
                Check(File.Exists(Path.Combine(app, "readme.txt")), "撤销后文件已回到原位置");
                Check(Copier.Sha256(Path.Combine(app, "data", "blob.bin")) == originalHash,
                      "撤销后大文件哈希与最初一致（完整往返）");
                Check(!Directory.Exists(plan.Target), "撤销后目标盘副本已删除（数据只有一份）");
                Check(Journal.ReadAll()[0].Status == "reverted", "记录状态更新为 reverted");
            }
            catch (Exception ex)
            {
                _fail++;
                Console.WriteLine("  FAIL 端到端搬移异常: " + ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                Environment.SetEnvironmentVariable("APPMOVER_JOURNAL", oldJournalEnv);
                try { if (Directory.Exists(baseDir)) Directory.Delete(baseDir, true); } catch { }
            }
        }

        private static int GetDirCount(string dir)
        {
            try { return Directory.GetDirectories(dir).Length; }
            catch { return -1; }
        }

        /// <summary>
        /// 占用检测（Restart Manager）。
        ///
        /// 为什么必须测：这套 P/Invoke 接线如果写错，它不会报错，只会**永远返回"没有占用"**。
        /// 后果是用户拿不到"请先关闭某某程序"的提示，而是卡在后面改名/删除步骤上失败，
        /// 错误信息还不指向真正原因。静默失效的安全机制等于没有。
        /// </summary>
        private static void TestOccupancyDetection()
        {
            Group("占用检测（Restart Manager）");
            string dir = Path.Combine(ScratchRoot(), "_selftest_occ");
            try
            {
                if (Directory.Exists(dir)) { List<string> junk = new List<string>(); SafeDelete.DeleteTree(dir, junk); }
                Directory.CreateDirectory(dir);
                string file = Path.Combine(dir, "locked.bin");
                File.WriteAllText(file, "locked content");

                OccupancyReport idle = Occupancy.Check(dir, 100);
                Check(!idle.HasOccupancy, "未锁定时不误报占用（扫描了 " + idle.FilesScanned + " 个文件）");

                using (FileStream fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None))
                {
                    OccupancyReport busy = Occupancy.Check(dir, 100);
                    Check(busy.HasOccupancy, "独占锁定后能报告出占用");
                    if (busy.HasOccupancy)
                    {
                        string list = string.Join(" | ", busy.Processes.ToArray());
                        // 断言用 PID 而不是进程名：Restart Manager 返回的 strAppName 来自
                        // 程序版本资源里的描述（AssemblyTitle），不是进程名，拿进程名去比会误判。
                        // 真正要验证的是「它认出了持有文件的那个进程」，PID 才是精确的判据。
                        int myPid = Process.GetCurrentProcess().Id;
                        bool foundSelf = list.IndexOf("[PID " + myPid + "]", StringComparison.Ordinal) >= 0;
                        Check(foundSelf, "占用列表包含当前进程 PID " + myPid + "（实际：" + list + "）");
                    }
                    else
                    {
                        _fail++;
                        Console.WriteLine("  FAIL 占用未被检出（Restart Manager 接线可能失效）");
                    }
                }

                OccupancyReport after = Occupancy.Check(dir, 100);
                Check(!after.HasOccupancy, "释放锁后不再报告占用");
            }
            catch (Exception ex)
            {
                _fail++;
                Console.WriteLine("  FAIL 占用检测测试异常: " + ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                try { List<string> j = new List<string>(); SafeDelete.DeleteTree(dir, j); } catch { }
            }
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CreateHardLinkW(string newLink, string existing, IntPtr security);

        /// <summary>硬链接检测。硬链接不能跨卷，复制会打断链接关系，因此含硬链接的目录必须禁止搬移。</summary>
        private static void TestHardlinkDetection()
        {
            Group("硬链接检测");
            string dir = Path.Combine(ScratchRoot(), "_selftest_hardlink");
            string dst = Path.Combine(ScratchRoot(), "_selftest_hardlink_dst");
            try
            {
                if (Directory.Exists(dir)) { List<string> j = new List<string>(); SafeDelete.DeleteTree(dir, j); }
                if (Directory.Exists(dst)) { List<string> j = new List<string>(); SafeDelete.DeleteTree(dst, j); }
                Directory.CreateDirectory(dir);
                Directory.CreateDirectory(dst);

                string original = Path.Combine(dir, "original.bin");
                string linked = Path.Combine(dir, "linked.bin");
                File.WriteAllText(original, "hardlink target content");

                bool created = CreateHardLinkW(linked, original, IntPtr.Zero);
                Check(created, "测试前提：成功创建硬链接");
                if (!created)
                {
                    Console.WriteLine("  （当前文件系统不支持硬链接，跳过后续断言）");
                    return;
                }

                uint n = HardlinkUtil.GetLinkCount(original);
                Check(n == 2, "GetLinkCount 识别链接数为 2（实际 " + n + "）");

                int samples;
                Check(TreeWalker.ContainsHardlinks(dir, out samples), "ContainsHardlinks 检出目录内含硬链接（样本 " + samples + "）");

                Classifier cls = new Classifier(new SoftwareIndex());
                MovePlan plan = Relocator.BuildPlan(dir, dst, cls, null, false, true);
                bool blocked = false;
                for (int i = 0; i < plan.Blockers.Count; i++)
                    if (plan.Blockers[i].IndexOf("硬链接", StringComparison.Ordinal) >= 0) blocked = true;
                Check(blocked, "规划因含硬链接而阻止搬移");
                if (!blocked) Console.WriteLine("         阻止项: " + string.Join("；", plan.Blockers.ToArray()));
            }
            catch (Exception ex)
            {
                _fail++;
                Console.WriteLine("  FAIL 硬链接测试异常: " + ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                try { List<string> j = new List<string>(); SafeDelete.DeleteTree(dir, j); } catch { }
                try { List<string> j = new List<string>(); SafeDelete.DeleteTree(dst, j); } catch { }
            }
        }

        /// <summary>
        /// 长路径（超过 260 字符）。
        /// 这里踩过坑：早期给所有路径都加 \\?\ 前缀，结果 .NET Framework 默认配置直接拒绝该前缀，
        /// 整棵树的统计全变成 0。修法是短路径不加、长路径才加，并随产物附带 app.config 打开长路径支持。
        /// </summary>
        private static void TestLongPathHandling()
        {
            Group("长路径（超过 260 字符）");
            string root = Path.Combine(ScratchRoot(), "_selftest_longpath");
            try
            {
                if (Directory.Exists(root)) { List<string> j = new List<string>(); SafeDelete.DeleteTree(root, j); }

                string deep = root;
                for (int i = 0; i < 12; i++) deep = Path.Combine(deep, "level_" + i + "_padding_padding_padding");
                Check(deep.Length > 260, "构造出的路径确实超过 260 字符（实际 " + deep.Length + "）");

                Directory.CreateDirectory(PathUtil.IoPath(deep));
                File.WriteAllText(PathUtil.IoPath(Path.Combine(deep, "deep.txt")), "deep content");
                Check(true, "能在超长路径下创建目录与文件");

                TreeStats st = TreeWalker.Measure(root);
                Check(st.Files >= 1, "TreeWalker 统计到超长路径下的文件（实际 " + st.Files + " 个）");
                Check(st.IsComplete, "超长路径遍历没有出现权限/路径失败（跳过 " + st.Denied + " 项）");

                List<string> fails = new List<string>();
                bool deleted = SafeDelete.DeleteTree(root, fails);
                Check(deleted, "超长路径树能被安全删除"
                    + (fails.Count > 0 ? "（失败 " + fails.Count + " 项，例如 " + fails[0] + "）" : ""));
            }
            catch (Exception ex)
            {
                _fail++;
                Console.WriteLine("  FAIL 长路径测试异常: " + ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                try { List<string> j = new List<string>(); SafeDelete.DeleteTree(root, j); } catch { }
            }
        }

        /// <summary>预检守卫：这些条件必须真的拦得住，不能只是"文档里写了"。</summary>
        private static void TestGuardBlockers()
        {
            Group("预检阻止项");
            string baseDir = Path.Combine(ScratchRoot(), "_selftest_guard");
            try
            {
                if (Directory.Exists(baseDir)) { List<string> j = new List<string>(); SafeDelete.DeleteTree(baseDir, j); }
                string src = Path.Combine(baseDir, "App");
                string dst = Path.Combine(baseDir, "dst");
                Directory.CreateDirectory(src);
                Directory.CreateDirectory(dst);
                File.WriteAllText(Path.Combine(src, "a.txt"), "a");

                Classifier cls = new Classifier(new SoftwareIndex());

                // (a) 源本身就是联接
                string linkSrc = Path.Combine(baseDir, "LinkApp");
                Junction.Create(linkSrc, src);
                MovePlan p1 = Relocator.BuildPlan(linkSrc, dst, cls, null, false, true);
                Check(HasBlocker(p1, "联接"), "源本身是联接时被阻止");
                Junction.Delete(linkSrc);

                // (b) 目标位置已存在且非空
                string conflict = Path.Combine(dst, "App");
                Directory.CreateDirectory(conflict);
                File.WriteAllText(Path.Combine(conflict, "occupied.txt"), "x");
                MovePlan p2 = Relocator.BuildPlan(src, dst, cls, null, false, true);
                Check(HasBlocker(p2, "已存在且非空"), "目标位置已被非空目录占用时被阻止");

                // (c) 同卷搬移（未加开关）—— 没有意义，应当阻止
                MovePlan p3 = Relocator.BuildPlan(src, dst, cls, null, false, false);
                Check(HasBlocker(p3, "同一个卷"), "同卷搬移（未加开关）被阻止");

                // (d) 目标盘不存在
                MovePlan p4 = Relocator.BuildPlan(src, @"Z:\不存在的盘\子目录", cls, null, false, true);
                Check(p4.Blockers.Count > 0, "目标盘不存在时被阻止");

                // (e) 源目录不存在
                MovePlan p5 = Relocator.BuildPlan(Path.Combine(baseDir, "NoSuchApp"), dst, cls, null, false, true);
                Check(HasBlocker(p5, "不存在"), "源目录不存在时被阻止");
            }
            catch (Exception ex)
            {
                _fail++;
                Console.WriteLine("  FAIL 阻止项测试异常: " + ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                try { List<string> j = new List<string>(); SafeDelete.DeleteTree(baseDir, j); } catch { }
            }
        }

        private static bool HasBlocker(MovePlan p, string keyword)
        {
            for (int i = 0; i < p.Blockers.Count; i++)
                if (p.Blockers[i].IndexOf(keyword, StringComparison.Ordinal) >= 0) return true;
            return false;
        }

        /// <summary>目标盘空间规则。只有两块大盘的机器上没法把盘写满，所以规则本身必须可单测。</summary>
        private static void TestSpaceRule()
        {
            Group("目标盘空间规则");
            long mb = 1024 * 1024;
            Check(Relocator.HasEnoughSpace(0, 64 * mb), "空目录：64 MB 余量恰好够");
            Check(!Relocator.HasEnoughSpace(0, 63 * mb), "空目录：63 MB 不够（固定余量占位）");
            Check(!Relocator.HasEnoughSpace(900 * mb, 1000 * mb), "需 900 MB / 有 1000 MB：不够（要留 5% + 64 MB）");
            Check(Relocator.HasEnoughSpace(900 * mb, 1100 * mb), "需 900 MB / 有 1100 MB：够");
            Check(!Relocator.HasEnoughSpace(100 * mb, 100 * mb), "刚好等于所需：不够（余量为零）");
            Check(Relocator.HasEnoughSpace(1024 * mb, 2048 * mb), "需 1 GB / 有 2 GB：够");
            Check(!Relocator.HasEnoughSpace(10240 * mb, 10240 * mb), "需 10 GB / 恰好 10 GB：不够");
        }

        /// <summary>
        /// 崩溃恢复。
        ///
        /// 这是「声称了安全却没给恢复路径」的典型：搬移中途断电/被强杀时，
        /// 数据本身完好（改名让位而不是删除），但原路径会空着、软件起不来。
        /// 之前只有一条 planned 日志，没有任何修复手段。
        ///
        /// 验证方式：用子进程在指定阶段调用 Environment.Exit 真实猝死
        /// （绕过所有 catch/finally，不做回滚），再让恢复逻辑去修。
        /// </summary>
        private static void TestCrashRecovery()
        {
            Group("崩溃恢复（断电 / 被强杀后的现场修复）");
            string baseDir = Path.Combine(ScratchRoot(), "_selftest_crash");
            string journalPath = Path.Combine(baseDir, "journal.jsonl");
            string oldJournal = Environment.GetEnvironmentVariable("APPMOVER_JOURNAL");
            try
            {
                if (Directory.Exists(baseDir))
                {
                    List<string> j = new List<string>();
                    SafeDelete.DeleteTree(baseDir, j);
                }
                string srcRoot = Path.Combine(baseDir, "srcvol");
                string dstRoot = Path.Combine(baseDir, "dstvol");
                Directory.CreateDirectory(srcRoot);
                Directory.CreateDirectory(dstRoot);
                Environment.SetEnvironmentVariable("APPMOVER_JOURNAL", journalPath);

                // 场景 A：崩溃于「改名之后、建联接之前」——最需要修复的状态
                string app = Path.Combine(srcRoot, "CrashApp");
                Directory.CreateDirectory(app);
                File.WriteAllText(Path.Combine(app, "keep.txt"), "很重要的数据");

                int code; string childOut;
                code = SpawnCrash(app, dstRoot, journalPath, "junction", out childOut);
                Check(code == 99, "子进程在建立联接前真实猝死（退出码 " + code + "）");
                if (code != 99) Console.WriteLine("         子进程输出: " + childOut);

                Check(!Directory.Exists(PathUtil.IoPath(app)), "崩溃后原路径确实不存在（正是需要修复的状态）");
                string[] leftovers = Directory.GetDirectories(PathUtil.IoPath(srcRoot));
                Check(leftovers.Length == 1 && leftovers[0].IndexOf(".appmover-old-", StringComparison.Ordinal) >= 0,
                    "现场留下改名后的目录（共 " + leftovers.Length + " 项）");
                Check(File.Exists(Path.Combine(dstRoot, "CrashApp", "keep.txt")), "目标盘有完整副本");

                List<Relocator.RecoveryItem> items = Relocator.FindInterrupted();
                Check(items.Count == 1, "找到 1 项被中断的操作（实际 " + items.Count + "）");
                if (items.Count != 1) return;
                Check(items[0].CanRestore, "现场判定：可以还原");
                Check(items[0].CanComplete, "现场判定：可以把搬移做完");

                // A1：还原
                MoveResult r1 = Relocator.Recover(items[0], false, null);
                Check(r1.Success && r1.Status == "restored", "还原成功（" + r1.Status + "：" + r1.Message + "）");
                Check(!PathUtil.IsReparsePoint(app), "还原后原路径是真实目录");
                Check(File.ReadAllText(Path.Combine(app, "keep.txt")) == "很重要的数据", "还原后数据完好");
                Check(!Directory.Exists(Path.Combine(dstRoot, "CrashApp")), "还原后目标盘副本已清理");
                Check(Relocator.FindInterrupted().Count == 0, "还原后该记录不再是「被中断」");

                // 场景 B：再崩一次，这次选择"把搬移做完"
                string app2 = Path.Combine(srcRoot, "CrashApp2");
                Directory.CreateDirectory(app2);
                File.WriteAllText(Path.Combine(app2, "keep2.txt"), "第二份数据");
                int code2; string childOut2;
                code2 = SpawnCrash(app2, dstRoot, journalPath, "junction", out childOut2);
                Check(code2 == 99, "第二次崩溃注入成功");
                if (code2 != 99) Console.WriteLine("         子进程输出: " + childOut2);

                List<Relocator.RecoveryItem> items2 = Relocator.FindInterrupted();
                Check(items2.Count == 1, "第二次中断同样被找到（实际 " + items2.Count + "）");
                if (items2.Count != 1) return;
                MoveResult r2 = Relocator.Recover(items2[0], true, null);
                Check(r2.Success && r2.Status == "recovered", "完成中断的搬移成功（" + r2.Status + "）");
                Check(PathUtil.IsReparsePoint(app2), "完成后原路径是联接");
                Check(File.ReadAllText(Path.Combine(app2, "keep2.txt")) == "第二份数据", "通过联接读到数据");
                Check(!Directory.Exists(PathUtil.IoPath(items2[0].AsidePath)), "改名后的旧副本已清理");

                // 场景 C：崩溃于「联接已建立、旧副本未删」——必须清掉残留副本而不是只标记完成
                string app3 = Path.Combine(srcRoot, "CrashApp3");
                Directory.CreateDirectory(app3);
                File.WriteAllText(Path.Combine(app3, "keep3.txt"), "第三份数据");
                int code3; string childOut3;
                code3 = SpawnCrash(app3, dstRoot, journalPath, "verify-junction", out childOut3);
                Check(code3 == 99, "第三次崩溃注入成功（联接已建、未删旧副本）");
                if (code3 != 99) Console.WriteLine("         子进程输出: " + childOut3);

                List<Relocator.RecoveryItem> items3 = Relocator.FindInterrupted();
                Check(items3.Count == 1, "第三次中断被找到");
                if (items3.Count != 1) return;
                Check(items3[0].AutoFix == "cleanup-aside",
                    "现场被识别为「需清理残留旧副本」而不是「只标记完成」（实际 " + items3[0].AutoFix + "）");
                Check(Directory.Exists(PathUtil.IoPath(items3[0].AsidePath)), "确认旧副本确实还在（测试前提）");
                MoveResult r3 = Relocator.Recover(items3[0], false, null);
                Check(r3.Success, "自动清理成功");
                Check(!Directory.Exists(PathUtil.IoPath(items3[0].AsidePath)), "残留旧副本已被清理");
                Check(File.ReadAllText(Path.Combine(app3, "keep3.txt")) == "第三份数据", "联接仍可正常读取数据");
            }
            catch (Exception ex)
            {
                _fail++;
                Console.WriteLine("  FAIL 崩溃恢复测试异常: " + ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                Environment.SetEnvironmentVariable("APPMOVER_JOURNAL", oldJournal);
                try { List<string> j = new List<string>(); SafeDelete.DeleteTree(baseDir, j); } catch { }
            }
        }

        /// <summary>用子进程在指定阶段真实猝死，返回退出码。</summary>
        private static int SpawnCrash(string source, string targetRoot, string journalPath, string crashStage, out string output)
        {
            string exe = System.Reflection.Assembly.GetEntryAssembly().Location;
            ProcessStartInfo psi = new ProcessStartInfo(exe);
            // 注意：路径**不能**以反斜杠结尾再加右引号 —— 那个反斜杠会把引号转义掉，
            // 参数被切开、目标盘变成不存在的路径，预检直接拦下（曾经因此拿到退出码 2）。
            // PowerShell 自己拼参数时不会有这个问题，所以手工测试发现不了。
            psi.Arguments = "move \"" + source.TrimEnd('\\') + "\" --to \""
                + targetRoot.TrimEnd('\\') + "\" --allow-same-volume";
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.EnvironmentVariables["APPMOVER_JOURNAL"] = journalPath;
            psi.EnvironmentVariables["APPMOVER_TEST_CRASH_STAGE"] = crashStage;
            using (Process p = Process.Start(psi))
            {
                string so = p.StandardOutput.ReadToEnd();
                string se = p.StandardError.ReadToEnd();
                p.WaitForExit();
                output = so + se;
                return p.ExitCode;
            }
        }

        /// <summary>
        /// 需要管理员权限但没提权时的行为。
        ///
        /// 这条路径之前只有逻辑、没有实测。有两个必须验证的点：
        ///   1. 预检要能提前拦住（而不是让用户走到一半才失败）
        ///   2. 万一真去建联接，失败信息必须**可操作**（告诉用户该以管理员身份运行），
        ///      而且要收拾干净，不能把空的载体目录留在 Program Files 里
        /// </summary>
        private static void TestElevationRequired()
        {
            Group("需要管理员权限时（当前未提权）");
            if (Security.IsElevated())
            {
                Console.WriteLine("  skip 当前进程已提权，无法验证「权限不足」的路径");
                return;
            }
            Check(!Security.IsElevated(), "测试前提：当前进程未提权");
            Check(Security.NeedsElevation(@"C:\Program Files\Foo"), @"Program Files 下判定为需要提权");
            Check(Security.NeedsElevation(@"C:\Program Files (x86)\Foo"), @"Program Files (x86) 下判定为需要提权");
            Check(Security.NeedsElevation(@"C:\ProgramData\Foo"), @"ProgramData 下判定为需要提权");
            Check(Security.NeedsElevation(@"C:\Windows\Foo"), @"Windows 下判定为需要提权");
            Check(!Security.NeedsElevation(@"D:\Foo"), "普通数据盘目录不需要提权");
            Check(!Security.NeedsElevation(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)),
                "用户目录不需要提权");

            string target = Path.Combine(ScratchRoot(), "_selftest_elev_target");
            string testDir = @"C:\Program Files\_appmover_junction_probe";
            try
            {
                Directory.CreateDirectory(PathUtil.IoPath(target));
                if (Directory.Exists(PathUtil.IoPath(testDir)))
                {
                    Console.WriteLine("  skip 探针目录已存在（疑似上次残留）：" + testDir);
                    return;
                }

                // 预检：用一个真实存在的 Program Files 子目录，验证会被提权要求拦住
                string[] subs = Directory.GetDirectories(PathUtil.IoPath(@"C:\Program Files"));
                string realSub = subs.Length > 0 ? PathUtil.StripExtended(subs[0]) : null;
                if (realSub != null)
                {
                    Classifier cls = new Classifier(new SoftwareIndex());
                    MovePlan plan = Relocator.BuildPlan(realSub, target, cls, null, true, true);
                    bool adminBlocked = false;
                    for (int i = 0; i < plan.Blockers.Count; i++)
                        if (plan.Blockers[i].IndexOf("管理员", StringComparison.Ordinal) >= 0) adminBlocked = true;
                    Check(adminBlocked, "预检提前拦住需要提权的搬移（" + Path.GetFileName(realSub) + "）");
                }

                // 真去建联接：必须失败，且信息可操作、现场干净
                bool threw = false; string msg = "";
                try { Junction.Create(testDir, target); }
                catch (Exception ex) { threw = true; msg = ex.Message; }

                if (threw)
                {
                    Check(true, "在 Program Files 下建立联接会失败，而不是静默成功");
                    Check(msg.IndexOf("管理员", StringComparison.Ordinal) >= 0, "失败信息提示了需要管理员权限");
                    Console.WriteLine("         实际信息: " + msg);
                    Check(!Directory.Exists(PathUtil.IoPath(testDir)), "失败后没有把空载体目录留在 Program Files 里");
                }
                else
                {
                    _fail++;
                    Console.WriteLine("  FAIL 竟然成功在 Program Files 下建立了联接，请检查权限策略");
                    try { Junction.Delete(testDir); } catch { }
                }
            }
            catch (Exception ex)
            {
                _fail++;
                Console.WriteLine("  FAIL 提权路径测试异常: " + ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                try { List<string> j = new List<string>(); SafeDelete.DeleteTree(target, j); } catch { }
                try
                {
                    if (Directory.Exists(PathUtil.IoPath(testDir)) && !PathUtil.IsReparsePoint(testDir))
                        Directory.Delete(PathUtil.IoPath(testDir), false);
                }
                catch { }
            }
        }

        /// <summary>
        /// 盘根作为目标路径。
        ///
        /// 真实 bug：曾经用 TrimEnd('\\') 处理路径，把盘根 "D:\" 削成了 "D:"，
        /// 而 "D:" 不是有效目录，于是预检报「目标盘目录不存在: D:」。
        /// 图形界面传的目标盘恰恰就是 "D:\" 这种形式 —— 也就是说界面的搬移按钮
        /// **完全无法工作**，而界面测试只验了几何布局、从没跑过搬移流程，所以一直没暴露。
        /// 这条用例同时守住「路径规范化」和「界面传参形式」两个面。
        /// </summary>
        private static void TestDriveRootTarget()
        {
            Group("盘根作为目标（界面传的就是 D:\\ 这种形式）");
            Check(PathUtil.TrimTrailingSeparator(@"D:\") == @"D:\", @"盘根不被削成 D:");
            Check(PathUtil.TrimTrailingSeparator(@"D:") == @"D:\", "裸盘符补成盘根");
            Check(PathUtil.TrimTrailingSeparator(@"D:\Foo\") == @"D:\Foo", "普通目录去掉尾部分隔符");
            Check(PathUtil.TrimTrailingSeparator(@"D:\Foo") == @"D:\Foo", "无尾部分隔符时不变");
            Check(PathUtil.TrimTrailingSeparator(@"D:\Foo\\") == @"D:\Foo", "多重尾部分隔符一并去掉");
            Check(PathUtil.TrimTrailingSeparator(@"C:/") == @"C:\", "正斜杠形式的盘根也归一");

            string baseDir = Path.Combine(ScratchRoot(), "_selftest_driveroot");
            try
            {
                if (Directory.Exists(baseDir)) { List<string> j = new List<string>(); SafeDelete.DeleteTree(baseDir, j); }
                string src = Path.Combine(baseDir, "App");
                Directory.CreateDirectory(src);
                File.WriteAllText(Path.Combine(src, "a.txt"), "a");

                Classifier cls = new Classifier(new SoftwareIndex());
                // 用当前程序所在盘的盘根作为目标，正是界面会传的形式
                string volumeRoot = Path.GetPathRoot(ScratchRoot());
                MovePlan plan = Relocator.BuildPlan(src, volumeRoot, cls, null, false, true);

                Check(!HasBlocker(plan, "目标盘目录不存在"),
                    "以盘根为目标时不再误报「目标盘目录不存在」（实际阻止项：" +
                    (plan.Blockers.Count == 0 ? "无" : string.Join("；", plan.Blockers.ToArray())) + "）");
                Check(plan.TargetRoot.Length > 0 && plan.TargetRoot.EndsWith("\\", StringComparison.Ordinal),
                    "计划里的目标根仍是合法的盘根形式：" + plan.TargetRoot);
                Check(plan.Target.Length > 0, "目标位置能被正确拼出来：" + plan.Target);
            }
            catch (Exception ex)
            {
                _fail++;
                Console.WriteLine("  FAIL 盘根目标测试异常: " + ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                try { List<string> j = new List<string>(); SafeDelete.DeleteTree(baseDir, j); } catch { }
            }
        }

        /// <summary>
        /// 撤销的拒绝路径。
        ///
        /// Undo 的第一件事是核对现场与日志是否相符，不符就**拒绝**而不是猜。
        /// 这几条守卫如果坏了，撤销就会在用户已经改动过现场的情况下乱动数据 ——
        /// 属于「坏了会毁数据」的那一类，却一直没被测过。
        /// 每条都断言两件事：必须拒绝，且**拒绝后磁盘上什么都没变**。
        /// </summary>
        private static void TestUndoRefusals()
        {
            Group("撤销的拒绝路径（宁可拒绝，不可猜）");
            string baseDir = Path.Combine(ScratchRoot(), "_selftest_undoguard");
            string journalPath = Path.Combine(baseDir, "journal.jsonl");
            string oldJournal = Environment.GetEnvironmentVariable("APPMOVER_JOURNAL");
            try
            {
                if (Directory.Exists(baseDir)) { List<string> j = new List<string>(); SafeDelete.DeleteTree(baseDir, j); }
                Directory.CreateDirectory(baseDir);
                Environment.SetEnvironmentVariable("APPMOVER_JOURNAL", journalPath);

                string realDir = Path.Combine(baseDir, "RealApp");       // 普通目录
                string targetDir = Path.Combine(baseDir, "Target");      // 真目标
                string linkDir = Path.Combine(baseDir, "LinkApp");       // 指向 targetDir 的联接
                string otherDir = Path.Combine(baseDir, "Other");        // 另一个目录（用于「指向别处」）
                Directory.CreateDirectory(realDir);
                Directory.CreateDirectory(targetDir);
                Directory.CreateDirectory(otherDir);
                File.WriteAllText(Path.Combine(realDir, "keep.txt"), "keep");
                File.WriteAllText(Path.Combine(targetDir, "t.txt"), "t");
                File.WriteAllText(Path.Combine(otherDir, "o.txt"), "o");
                Junction.Create(linkDir, targetDir);

                // 场景一：状态不是 completed（还没搬完的记录不该被撤销）
                JournalEntry e1 = MakeEntry("guard-1", realDir, targetDir, "planned");
                MoveResult r1 = Relocator.Undo(e1, null);
                Check(!r1.Success && r1.Status == "blocked", "状态非 completed 时拒绝撤销（" + r1.Status + "）");
                Check(Directory.Exists(realDir) && File.Exists(Path.Combine(realDir, "keep.txt")),
                    "拒绝后源目录与文件未被改动");

                // 场景二：目标目录不存在（数据不在记录所说的位置）
                JournalEntry e2 = MakeEntry("guard-2", linkDir, Path.Combine(baseDir, "NoSuchTarget"), "completed");
                MoveResult r2 = Relocator.Undo(e2, null);
                Check(!r2.Success && r2.Status == "blocked", "目标目录不存在时拒绝撤销（" + r2.Status + "）");
                Check(PathUtil.IsReparsePoint(linkDir), "拒绝后联接仍在，没有被拆掉");

                // 场景三：原位置已经不是联接（可能被人为改动过）
                JournalEntry e3 = MakeEntry("guard-3", realDir, targetDir, "completed");
                MoveResult r3 = Relocator.Undo(e3, null);
                Check(!r3.Success && r3.Status == "blocked", "原位置不是联接时拒绝撤销（" + r3.Status + "）");
                Check(Directory.Exists(realDir) && !PathUtil.IsReparsePoint(realDir), "拒绝后原目录保持原样");
                Check(File.Exists(Path.Combine(realDir, "keep.txt")), "拒绝后原目录里的文件完好");

                // 场景四：联接指向别处（与日志记录不符）
                JournalEntry e4 = MakeEntry("guard-4", linkDir, otherDir, "completed");
                MoveResult r4 = Relocator.Undo(e4, null);
                Check(!r4.Success && r4.Status == "blocked", "联接指向与记录不符时拒绝撤销（" + r4.Status + "）");
                Check(PathUtil.IsReparsePoint(linkDir), "拒绝后联接仍指向原处");
                string real = PathUtil.TryGetRealPath(linkDir);
                Check(real != null && PathUtil.Normalize(real) == PathUtil.Normalize(targetDir),
                    "联接目标未被改动（仍为 " + targetDir + "）");
                Check(File.Exists(Path.Combine(otherDir, "o.txt")), "无关目录未被波及");

                // 每条拒绝信息都应说明原因，而不是空话
                Console.WriteLine("         场景一信息: " + r1.Message);
                Console.WriteLine("         场景四信息: " + r4.Message);
                Check(r1.Message.IndexOf("completed", StringComparison.OrdinalIgnoreCase) >= 0,
                    "场景一的拒绝信息说明了原因");
                Check(r4.Message.IndexOf("不符", StringComparison.Ordinal) >= 0,
                    "场景四的拒绝信息说明了原因");
            }
            catch (Exception ex)
            {
                _fail++;
                Console.WriteLine("  FAIL 撤销拒绝路径测试异常: " + ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                Environment.SetEnvironmentVariable("APPMOVER_JOURNAL", oldJournal);
                try { List<string> j = new List<string>(); SafeDelete.DeleteTree(baseDir, j); } catch { }
            }
        }

        private static JournalEntry MakeEntry(string id, string original, string target, string status)
        {
            JournalEntry e = new JournalEntry();
            e.Id = id;
            e.Time = DateTime.Now.ToString("o");
            e.Original = original;
            e.Target = target;
            e.Status = status;
            e.Method = "test";
            return e;
        }

        private static void TestRealMachinePaths()
        {
            Group("真实机器回归（用真实存在的路径，而不是构造的字符串）");
            // 教训：我曾用一个与正则同构的构造字符串去"验证"正则，结果假通过。
            // 这里强制用真实路径，把 curl 得到的真实大小差异也体现出来。
            string[] realForbidden = new string[]
            {
                @"C:\Windows",
                @"C:\Program Files\Common Files",
                @"C:\Program Files (x86)\InstallShield Installation Information",
            };
            foreach (string p in realForbidden)
            {
                if (!Directory.Exists(p)) { Console.WriteLine("  skip " + p + "（本机不存在）"); continue; }
                Classification c = NewClassifier().Classify(p, Path.GetFileName(p),
                    TreeWalker.Measure(p), PathUtil.IsReparsePoint(p));
                Check(c.Category == Category.Forbidden, p + " → 禁止搬移");
            }

            // 联接目录必须被识别为真实路径不同
            string linkTarget = null;
            foreach (string cand in new string[] { @"C:\Users\All Users", @"C:\Documents and Settings" })
            {
                if (Directory.Exists(cand)) { linkTarget = cand; break; }
            }
            if (linkTarget != null)
            {
                string real = PathUtil.TryGetRealPath(linkTarget);
                Check(real != null && PathUtil.Normalize(real) != PathUtil.Normalize(linkTarget),
                      linkTarget + " 是联接，真实路径解析为 " + (real ?? "null"));
                Check(PathUtil.IsReparsePoint(linkTarget), linkTarget + " 被识别为重解析点");
            }
            else
            {
                Console.WriteLine("  skip 未找到可用的联接样本");
            }
        }
    }
}
