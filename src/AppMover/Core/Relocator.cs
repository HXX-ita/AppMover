using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Principal;

namespace AppMover.Core
{
    public static class Security
    {
        /// <summary>当前进程是否以管理员身份运行。</summary>
        public static bool IsElevated()
        {
            try
            {
                WindowsIdentity id = WindowsIdentity.GetCurrent();
                WindowsPrincipal p = new WindowsPrincipal(id);
                return p.IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }

        /// <summary>该路径是否属于需要管理员才能改写的位置。</summary>
        public static bool NeedsElevation(string path)
        {
            string n = PathUtil.Normalize(path);
            return PathUtil.IsUnder(n, @"C:\Program Files")
                || PathUtil.IsUnder(n, @"C:\Program Files (x86)")
                || PathUtil.IsUnder(n, @"C:\ProgramData")
                || PathUtil.IsUnder(n, @"C:\Windows");
        }
    }

    public class MovePlan
    {
        public string Source = "";
        public string TargetRoot = "";
        public string Target = "";
        public long Bytes;
        public long Files;
        public string Category = "";
        public string Confidence = "";
        public string Reasons = "";

        public List<string> Blockers = new List<string>();
        public List<string> Warnings = new List<string>();
        public List<string> Occupying = new List<string>();
        public bool NeedsAdmin;
        public long TargetFreeBytes;

        public bool CanProceed { get { return Blockers.Count == 0; } }
    }

    public class MoveResult
    {
        public bool Success;
        public string Status = "";
        public string Message = "";
        public string JournalId = "";
        public List<string> Steps = new List<string>();
    }

    /// <summary>
    /// 搬移状态机。
    ///
    /// 顺序刻意与「复制 → 校验 → 删除源 → 建联接」不同：删除源目录是**不可逆**的，
    /// 而建联接可能失败（权限、目标被占、磁盘错误）。如果先删源再建联接，
    /// 一旦建联接失败，用户就得到一个「软件没了、位置也空了」的烂摊子。
    ///
    /// 这里改用「改名让位」换出回滚窗口：
    ///
    ///   1. 复制到目标盘
    ///   2. 校验（数量 + 字节 + 抽样哈希）
    ///   3. 把源目录**改名**（同卷改名是元数据操作，瞬间完成，且可逆）
    ///   4. 在原位置建立目录联接
    ///   5. 验证联接（解析目标 + 通过联接读哨兵文件）
    ///   6. 成功 → 删除改名后的旧目录
    ///      失败 → 拆除联接、把旧目录改名回来，磁盘回到操作前状态
    ///
    /// 全程**没有任何一步会同时失去两份数据**。
    /// </summary>
    public static class Relocator
    {
        public static MovePlan BuildPlan(string source, string targetRoot, Classifier cls, SoftwareIndex idx, bool force)
        {
            return BuildPlan(source, targetRoot, cls, idx, force, false);
        }

        public static MovePlan BuildPlan(string source, string targetRoot, Classifier cls, SoftwareIndex idx,
            bool force, bool allowSameVolume)
        {
            MovePlan plan = new MovePlan();
            // 入口统一剥掉 \\?\ 前缀：调用方可能从枚举结果里直接拿来路径，
            // 而计划里的路径会进入界面、日志与字符串比较，带前缀会污染这些地方。
            plan.Source = PathUtil.StripExtended(source).TrimEnd('\\');
            plan.TargetRoot = PathUtil.StripExtended(targetRoot).TrimEnd('\\');

            if (!Directory.Exists(PathUtil.IoPath(plan.Source)))
            {
                plan.Blockers.Add("源目录不存在");
                return plan;
            }
            if (!Directory.Exists(PathUtil.IoPath(plan.TargetRoot)))
            {
                plan.Blockers.Add("目标盘目录不存在: " + plan.TargetRoot);
                return plan;
            }

            string name = Path.GetFileName(plan.Source);
            plan.Target = Path.Combine(plan.TargetRoot, name);

            // 规则判定（与扫描用同一套引擎）
            TreeStats st = TreeWalker.Measure(plan.Source);
            plan.Bytes = st.Bytes;
            plan.Files = st.Files;

            if (PathUtil.IsReparsePoint(plan.Source))
            {
                plan.Blockers.Add("源目录本身已经是联接/符号链接，禁止二次搬运");
                return plan;
            }

            Classification c = cls.Classify(plan.Source, name, st, false);
            plan.Category = c.CategoryName;
            plan.Confidence = c.Confidence;
            plan.Reasons = string.Join("；", c.Reasons.ToArray());

            if (c.Category == Category.Forbidden)
                plan.Blockers.Add("判定为「禁止搬移」：" + plan.Reasons);
            else if (c.Category == Category.Caution && !force)
                plan.Warnings.Add("判定为「可搬但需小心」：" + plan.Reasons + "（需要 --force 才会执行）");

            if (!st.IsComplete)
                plan.Warnings.Add("有 " + st.Denied + " 个条目读取失败，大小统计可能不完整");

            // 盘符与文件系统资格
            string srcRoot = PathUtil.GetVolumeRoot(plan.Source);
            string dstRoot = PathUtil.GetVolumeRoot(plan.Target);
            if (string.Equals(srcRoot, dstRoot, StringComparison.OrdinalIgnoreCase))
            {
                if (allowSameVolume)
                    plan.Warnings.Add("源与目标在同一个卷（" + srcRoot + "），不会释放该卷空间，仅改变目录位置");
                else
                    plan.Blockers.Add("源与目标在同一个卷（" + srcRoot + "），搬移没有意义；联接也只在跨卷时才有价值"
                        + "（如需强制，加 --allow-same-volume）");
            }

            VolumeInfo vol = null;
            List<VolumeInfo> vols = Volumes.Enumerate();
            for (int i = 0; i < vols.Count; i++)
                if (string.Equals(vols[i].Letter, dstRoot, StringComparison.OrdinalIgnoreCase)) { vol = vols[i]; break; }

            if (vol == null)
            {
                plan.Blockers.Add("找不到目标卷 " + dstRoot);
            }
            else
            {
                string why = vol.WhyNotHost();
                if (why != null) plan.Blockers.Add("目标卷不合适：" + why);
                plan.TargetFreeBytes = vol.FreeBytes;
                if (vol.FreeBytes < plan.Bytes + (plan.Bytes / 20) + (64L * 1024 * 1024))
                    plan.Blockers.Add(string.Format("目标盘剩余空间不足：可用 {0:F2} GB，需要约 {1:F2} GB（含 5% 余量）",
                        vol.FreeBytes / 1073741824.0, (plan.Bytes * 1.05) / 1073741824.0));
            }

            // 目标位置冲突
            if (Directory.Exists(PathUtil.IoPath(plan.Target)) || File.Exists(plan.Target))
            {
                int n = 0;
                try { n = Directory.GetFileSystemEntries(PathUtil.IoPath(plan.Target)).Length; } catch { }
                if (n > 0) plan.Blockers.Add("目标位置已存在且非空：" + plan.Target);
                else plan.Warnings.Add("目标位置已存在空目录，将被使用：" + plan.Target);
            }

            // 硬链接（不能跨卷，复制会打断）
            int samples;
            if (TreeWalker.ContainsHardlinks(plan.Source, out samples))
                plan.Blockers.Add("目录内存在硬链接文件（已发现 " + samples + " 个），复制会打断链接关系，禁止搬移");

            // 权限
            plan.NeedsAdmin = Security.NeedsElevation(plan.Source) || Security.NeedsElevation(plan.Target);
            if (plan.NeedsAdmin && !Security.IsElevated())
                plan.Blockers.Add("该位置需要管理员权限，请以管理员身份运行本程序");

            // 占用检测
            OccupancyReport occ = Occupancy.Check(plan.Source, 3000);
            plan.Occupying = occ.Processes;
            if (occ.HasOccupancy)
            {
                string list = string.Join("、", occ.Processes.ToArray());
                if (force) plan.Warnings.Add("以下进程正在使用该目录，复制可能跳过它们占用的文件：" + list);
                else plan.Blockers.Add("以下进程正在使用该目录，请先关闭它们（或用 --force 忽略）：" + list);
            }
            if (occ.ScanTruncated) plan.Warnings.Add("占用检测只覆盖了部分文件（目录过大）");

            return plan;
        }

        public static MoveResult Execute(MovePlan plan, bool dryRun, Action<string> log)
        {
            MoveResult r = new MoveResult();
            r.JournalId = Journal.NewId();
            if (log == null) log = delegate (string s) { };

            if (!plan.CanProceed)
            {
                r.Success = false; r.Status = "blocked";
                r.Message = string.Join("；", plan.Blockers.ToArray());
                return r;
            }

            JournalEntry entry = new JournalEntry();
            entry.Id = r.JournalId;
            entry.Time = DateTime.Now.ToString("o");
            entry.Original = plan.Source;
            entry.Target = plan.Target;
            entry.Bytes = plan.Bytes;
            entry.Files = plan.Files;
            entry.Method = "robocopy+junction";
            entry.Status = "planned";
            entry.Message = plan.Category;
            Journal.Append(entry);

            string stage = "start";
            string oldPath = plan.Source + ".appmover-old-" + r.JournalId;
            bool junctionCreated = false;
            bool sourceRenamed = false;

            try
            {
                if (dryRun)
                {
                    log("[干跑] 不会改动任何文件。");
                    r.Success = true; r.Status = "dry-run"; r.Message = "干跑完成"; return r;
                }

                // 1) 复制
                stage = "copy";
                log("1/6 复制 " + plan.Source + "  →  " + plan.Target);
                int rc = Copier.RunRobocopy(plan.Source, plan.Target, true, log);
                r.Steps.Add("copy exit=" + rc);
                if (rc >= 8)
                {
                    log("  复制失败（robocopy 退出码 " + rc + "），清理目标副本，源目录未动。");
                    CleanupTarget(plan.Target, log);
                    return Fail(r, entry, "复制失败（robocopy 退出码 " + rc + "）", "failed");
                }
                log("  复制完成（退出码 " + rc + "）");

                // 2) 校验
                stage = "verify";
                log("2/6 校验副本");
                CopyReport rep = Copier.Verify(plan.Source, plan.Target, 8, 268435456, log);
                r.Steps.Add("verify ok=" + rep.Ok);
                if (!rep.Ok)
                {
                    log("  校验未通过，清理目标副本，源目录未动。");
                    CleanupTarget(plan.Target, log);
                    return Fail(r, entry, string.Format("校验未通过（源 {0}/{1} 字节，目标 {2}/{3} 字节，哈希不一致 {4}）",
                        rep.SourceFiles, rep.SourceBytes, rep.TargetFiles, rep.TargetBytes, rep.HashMismatch.Count), "failed");
                }
                log("  校验通过");

                // 3) 源目录改名让位（可回滚窗口从这里开始）
                stage = "rename";
                log("3/6 源目录改名为 " + Path.GetFileName(oldPath) + "（换出回滚窗口）");
                Directory.Move(PathUtil.IoPath(plan.Source), PathUtil.IoPath(oldPath));
                sourceRenamed = true;

                // 4) 建立联接
                stage = "junction";
                log("4/6 在原位置建立目录联接");
                Junction.Create(plan.Source, plan.Target);
                junctionCreated = true;

                // 5) 验证联接
                stage = "verify-junction";
                log("5/6 验证联接");
                string real = PathUtil.TryGetRealPath(plan.Source);
                if (real == null || PathUtil.Normalize(real) != PathUtil.Normalize(plan.Target))
                {
                    throw new IOException("联接真实路径为 " + (real == null ? "(无法解析)" : real) + "，期望 " + plan.Target);
                }
                if (!CanaryReadable(plan.Source, plan.Target))
                    throw new IOException("通过联接读取哨兵文件失败");
                log("  联接验证通过（真实路径 = " + real + "）");

                // 6) 删除改名后的旧目录
                stage = "cleanup";
                log("6/6 删除旧副本 " + Path.GetFileName(oldPath));
                List<string> failures = new List<string>();
                bool cleaned = SafeDelete.DeleteTree(oldPath, failures);
                if (!cleaned)
                {
                    // 删除失败不算搬移失败：数据在目标盘、联接已生效，只是多占一份空间
                    log("  旧副本未能完全删除（" + failures.Count + " 项），请稍后手动清理：" + oldPath);
                    entry.Message = "搬移成功，但旧副本残留：" + oldPath;
                }

                entry.JunctionCreated = true;
                entry.Status = "completed";
                Journal.UpdateStatus(entry.Id, "completed", entry.Message);

                r.Success = true; r.Status = "completed";
                r.Message = "搬移成功。原路径现在是指向 " + plan.Target + " 的目录联接。";
                log(r.Message);
                return r;
            }
            catch (Exception ex)
            {
                log("在步骤 [" + stage + "] 失败: " + ex.Message);
                log("开始回滚 ...");

                // 回滚：拆掉可能已建立的联接，把旧目录改名回来
                try
                {
                    if (junctionCreated && Directory.Exists(PathUtil.IoPath(plan.Source)) && PathUtil.IsReparsePoint(plan.Source))
                    {
                        Junction.Delete(plan.Source);
                        log("  已拆除联接");
                    }
                }
                catch (Exception e2) { log("  拆除联接失败: " + e2.Message); }

                try
                {
                    if (sourceRenamed && Directory.Exists(PathUtil.IoPath(oldPath)) && !Directory.Exists(PathUtil.IoPath(plan.Source)))
                    {
                        Directory.Move(PathUtil.IoPath(oldPath), PathUtil.IoPath(plan.Source));
                        log("  已把原目录改名还原");
                    }
                }
                catch (Exception e3) { log("  ！还原失败，原数据在: " + oldPath + " （" + e3.Message + "）"); }

                if (stage == "copy" || stage == "verify")
                {
                    CleanupTarget(plan.Target, log);
                }

                return Fail(r, entry, "在步骤 [" + stage + "] 失败: " + ex.Message + "（已尝试回滚）", "failed");
            }
        }

        private static MoveResult Fail(MoveResult r, JournalEntry entry, string message, string status)
        {
            r.Success = false; r.Status = status; r.Message = message;
            entry.Status = status; entry.Message = message;
            Journal.UpdateStatus(entry.Id, status, message);
            return r;
        }

        /// <summary>
        /// 撤销一次已完成的搬移：把数据搬回原位置，并移除目录联接。
        ///
        /// 顺序同样是「先确保有可用副本再删」：
        ///   1. 严格前置检查（联接是否存在、是否指向记录里的目标）—— 任何不符就拒绝，
        ///      绝不猜测，避免在用户已经改过现场的情况下乱动数据
        ///   2. 拆除联接（原路径空出来）
        ///   3. 把目标数据复制回原路径
        ///   4. 校验
        ///   5. 删除目标盘上的副本
        ///      失败则重建联接，恢复到撤销前的可用状态
        /// </summary>
        public static MoveResult Undo(JournalEntry e, Action<string> log)
        {
            MoveResult r = new MoveResult();
            r.JournalId = e.Id;
            if (log == null) log = delegate (string s) { };

            if (e.Status != "completed")
            {
                r.Status = "blocked"; r.Message = "只有状态为 completed 的记录可以撤销（当前 " + e.Status + "）";
                return r;
            }
            if (!Directory.Exists(PathUtil.IoPath(e.Target))) { r.Status = "blocked"; r.Message = "目标目录不存在: " + e.Target; return r; }
            if (!Directory.Exists(PathUtil.IoPath(e.Original))) { r.Status = "blocked"; r.Message = "原位置不存在: " + e.Original; return r; }
            if (!PathUtil.IsReparsePoint(e.Original))
            {
                r.Status = "blocked";
                r.Message = "原位置已经不是目录联接（可能被人为改动），为避免破坏数据，拒绝撤销";
                return r;
            }
            string real = PathUtil.TryGetRealPath(e.Original);
            if (real == null || PathUtil.Normalize(real) != PathUtil.Normalize(e.Target))
            {
                r.Status = "blocked";
                r.Message = "原位置的联接指向 " + (real == null ? "(无法解析)" : real)
                    + "，与记录中的目标 " + e.Target + " 不符，拒绝撤销";
                return r;
            }

            bool junctionRemoved = false;
            bool copiedBack = false;
            try
            {
                log("1/5 拆除联接 " + e.Original);
                Junction.Delete(e.Original);
                junctionRemoved = true;
                r.Steps.Add("junction-removed");

                log("2/5 复制回原位置 " + e.Target + "  →  " + e.Original);
                int rc = Copier.RunRobocopy(e.Target, e.Original, true, log);
                if (rc >= 8) throw new IOException("复制回原位置失败（robocopy 退出码 " + rc + "）");
                copiedBack = true;

                log("3/5 校验");
                CopyReport rep = Copier.Verify(e.Target, e.Original, 8, 268435456, log);
                if (!rep.Ok) throw new IOException("校验未通过");

                log("4/5 删除目标盘上的副本 " + e.Target);
                List<string> failures = new List<string>();
                SafeDelete.DeleteTree(e.Target, failures);
                if (failures.Count > 0) log("  副本未能完全删除（" + failures.Count + " 项）");

                log("5/5 更新记录");
                Journal.UpdateStatus(e.Id, "reverted", "已撤销，数据已回到 " + e.Original);

                r.Success = true; r.Status = "reverted";
                r.Message = "撤销完成，数据已回到 " + e.Original;
                log(r.Message);
                return r;
            }
            catch (Exception ex)
            {
                log("撤销失败: " + ex.Message);
                // 尽力恢复「撤销前」的可用状态：把联接重新建起来
                try
                {
                    if (junctionRemoved && !PathUtil.IsReparsePoint(e.Original) && Directory.Exists(PathUtil.IoPath(e.Target)))
                    {
                        if (copiedBack && Directory.Exists(PathUtil.IoPath(e.Original)))
                        {
                            // 原位置已经有复制回来的一份，先移开再重建联接，避免数据混淆
                            string stash = e.Original + ".appmover-undo-" + e.Id;
                            log("  原位置已有部分复制的数据，移到 " + stash + " 以免混淆");
                            if (!Directory.Exists(PathUtil.IoPath(stash))) Directory.Move(PathUtil.IoPath(e.Original), PathUtil.IoPath(stash));
                        }
                        Junction.Create(e.Original, e.Target);
                        log("  已重建联接，程序仍可用（指向 " + e.Target + "）");
                    }
                }
                catch (Exception e2) { log("  ！重建联接失败，请手动处理：" + e2.Message); }

                r.Success = false; r.Status = "undo-failed";
                r.Message = "撤销失败: " + ex.Message + "（已尽力恢复到可用状态，详见日志）";
                return r;
            }
        }

        private static void CleanupTarget(string target, Action<string> log)
        {
            try
            {
                if (Directory.Exists(PathUtil.IoPath(target)))
                {
                    List<string> f = new List<string>();
                    SafeDelete.DeleteTree(target, f);
                    if (log != null) log("  已清理目标副本" + (f.Count > 0 ? "（有 " + f.Count + " 项未删除）" : ""));
                }
            }
            catch (Exception ex) { if (log != null) log("  清理目标副本失败: " + ex.Message); }
        }

        /// <summary>通过联接读取一个真实文件，确认联通（不是只看联接本身）。</summary>
        private static bool CanaryReadable(string junctionPath, string targetPath)
        {
            string canary = null;
            try
            {
                Stack<string> stack = new Stack<string>();
                stack.Push(targetPath);
                while (stack.Count > 0 && canary == null)
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
                            // 剥前缀：下面要用它按目标路径长度截出相对路径，前缀会让长度对不上
                            else { canary = PathUtil.StripExtended(entries[i]); break; }
                        }
                        catch { }
                    }
                }
            }
            catch { }

            if (canary == null) return true;   // 空目录，没有可验证的文件

            string rel = canary.Substring(targetPath.TrimEnd('\\').Length).TrimStart('\\');
            string viaJunction = Path.Combine(junctionPath, rel);
            try
            {
                FileStream fs = new FileStream(PathUtil.IoPath(viaJunction), FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                fs.ReadByte();
                fs.Close();
                return true;
            }
            catch { return false; }
        }
    }
}
