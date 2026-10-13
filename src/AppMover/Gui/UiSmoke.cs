using System;
using System.Drawing;
using System.Text;
using System.Windows.Forms;
using AppMover.Core;

namespace AppMover.Gui
{
    /// <summary>
    /// 界面冒烟测试（无头运行，把结论写成文本）。
    ///
    /// 为什么需要它：这个项目里没人能"看一眼"界面就判断对错，而 WinForms 有一类
    /// 典型错误只在真正布局时才暴露 —— 比如停靠顺序写反导致 Fill 控件盖住 Top 面板、
    /// 或者用 Anchor 在父容器尺寸为 0 时算偏移导致按钮跑到窗口外。
    /// 这些都没法靠编译发现，只能在 Show() 之后用控件的实际边界去断言。
    ///
    /// 断言的是**几何不变量**，不是像素级外观：
    ///   · 关键控件必须完全落在父容器的客户区内
    ///   · 顶部工具条不能与结果列表重叠
    ///   · 对话框按钮不能超出对话框边界
    /// </summary>
    internal static class UiSmoke
    {
        private static StringBuilder _sb;
        private static int _pass;
        private static int _fail;

        public static string Run()
        {
            _sb = new StringBuilder();
            _pass = 0; _fail = 0;

            _sb.AppendLine("AppMover 界面冒烟测试  " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            _sb.AppendLine(new string('=', 60));

            try { TestMainForm(); }
            catch (Exception ex) { Fail("MainForm 测试抛异常: " + ex.GetType().Name + ": " + ex.Message); }

            try { TestConfirmDialog(); }
            catch (Exception ex) { Fail("ConfirmDialog 测试抛异常: " + ex.GetType().Name + ": " + ex.Message); }

            try { TestRecoverySurface(); }
            catch (Exception ex) { Fail("恢复入口测试抛异常: " + ex.GetType().Name + ": " + ex.Message); }

            _sb.AppendLine();
            _sb.AppendLine(new string('=', 60));
            _sb.AppendLine(string.Format("通过 {0}   失败 {1}", _pass, _fail));
            return _sb.ToString();
        }

        private static void TestMainForm()
        {
            Section("主窗口 MainForm");
            MainForm f = new MainForm();
            f.Show();
            Application.DoEvents();

            Ok("构造并显示成功，客户区 " + f.ClientSize.Width + "x" + f.ClientSize.Height);
            Ok("标题：" + f.Text);

            TabControl tabs = Find(f, "tabs") as TabControl;
            Check(tabs != null, "找到选项卡控件");
            if (tabs != null)
            {
                Check(tabs.TabCount == 3, "选项卡数量为 3（实际 " + tabs.TabCount + "）");
                string[] want = new string[] { "扫描与搬移", "历史记录与撤销", "关于与反馈" };
                for (int i = 0; i < tabs.TabPages.Count && i < want.Length; i++)
                    Check(tabs.TabPages[i].Text == want[i], "第 " + (i + 1) + " 页标题 = " + tabs.TabPages[i].Text);
                Check(tabs.Dock == DockStyle.Fill, "选项卡铺满窗口（Dock=Fill）");
            }

            ListView list = Find(f, "resultList") as ListView;
            Check(list != null, "找到结果列表");
            if (list != null)
            {
                Check(list.Columns.Count == 7, "结果列表 7 列（实际 " + list.Columns.Count + "）");
                Check(list.View == View.Details, "结果列表为详细视图");
                Check(list.FullRowSelect, "结果列表整行选中");
                Check(Inside(list), "结果列表完全落在父容器客户区内 " + Rect(list));

                // 停靠顺序不变式：顶部工具条与结果区域不能重叠。
                // 注意：结果列表的直接父容器是 SplitContainer.Panel1 而不是 TabPage，
                // 所以要先往上找到"直接挂在 TabPage 下的那个 Fill 子控件"，再与 TabPage 里
                // Dock=Top 的面板比较。这正是停靠顺序写反时会被破坏的几何关系。
                TabPage scanPage = tabs != null && tabs.TabPages.Count > 0 ? tabs.TabPages[0] : null;
                Control fillChild = scanPage == null ? null : NearestChildOf(scanPage, list);
                Control top = scanPage == null ? null : FindTopPanel(scanPage);
                if (fillChild != null && top != null)
                {
                    Check(fillChild.Top >= top.Bottom,
                        "结果区域未被顶部工具条覆盖（工具条底部 " + top.Bottom + "，结果区域顶部 " + fillChild.Top + "）");
                    Check(fillChild.Height > 100, "结果区域高度合理（" + fillChild.Height + " px）");
                    Check(top.Height > 40, "顶部工具条高度合理（" + top.Height + " px）");
                }
                else
                {
                    Fail("未能定位顶部工具条或 Fill 子控件，停靠顺序未经验证");
                }
            }

            Check(!f.IsDisposed, "窗口在测试期间未被销毁");
            f.Close();
            f.Dispose();
        }

        private static void TestConfirmDialog()
        {
            Section("确认窗口 ConfirmDialog");

            // 场景一：可搬移
            MovePlan ok = new MovePlan();
            ok.Source = @"C:\Program Files\Foo";
            ok.Target = @"D:\Foo";
            ok.Bytes = 1234567890;
            ok.Files = 4321;
            ok.Category = "可搬但需小心";
            ok.Confidence = "medium";
            ok.Reasons = "Program Files 下的已安装软件（需管理员权限）";
            ok.TargetFreeBytes = 500L * 1024 * 1024 * 1024;
            ok.Warnings.Add("自动更新器可能破坏联接");
            ok.Occupying.Add("foo.exe [PID 1234]");

            ConfirmDialog d1 = new ConfirmDialog(ok, false);
            d1.Show();
            Application.DoEvents();
            Ok("可搬移场景构造并显示成功");
            CheckButtonsInside(d1, "可搬移场景");
            d1.Close(); d1.Dispose();

            // 场景二：被阻止 —— 必须不能出现"继续"按钮可点
            MovePlan bad = new MovePlan();
            bad.Source = @"C:\Windows";
            bad.Target = @"D:\Windows";
            bad.Category = "禁止搬移";
            bad.Confidence = "high";
            bad.Reasons = "Windows 操作系统本体";
            bad.Blockers.Add("判定为「禁止搬移」：Windows 操作系统本体");

            ConfirmDialog d2 = new ConfirmDialog(bad, false);
            d2.Show();
            Application.DoEvents();
            Ok("被阻止场景构造并显示成功");
            Check(!bad.CanProceed, "计划本身标记为不可继续");
            CheckButtonsInside(d2, "被阻止场景");

            // 关键安全断言：有阻止项时，"开始搬移"按钮必须不可点
            Button start = null;
            foreach (Control c in All(d2)) { Button b = c as Button; if (b != null && !b.Enabled && b.Text.IndexOf("搬移") >= 0) start = b; }
            Check(start != null, "存在被禁用的搬移按钮（阻止项生效）");
            d2.Close(); d2.Dispose();

            // 场景三：干跑
            ConfirmDialog d3 = new ConfirmDialog(ok, true);
            d3.Show();
            Application.DoEvents();
            Ok("干跑场景构造并显示成功");
            CheckButtonsInside(d3, "干跑场景");
            d3.Close(); d3.Dispose();
        }

        /// <summary>
        /// 界面的启动恢复流程。
        ///
        /// 为什么必须测：崩溃恢复只在命令行里验证过，而**普通用户永远不会去用命令行**。
        /// 界面在 Shown 时会自动检查并处理无歧义的中断现场，这条链路之前完全没被覆盖过。
        /// 这里构造一个真实的「源完好、目标有半截副本」现场，然后真的显示窗口，
        /// 看它是否自己把现场收拾干净且不抛异常。
        /// </summary>
        private static void TestRecoverySurface()
        {
            Section("未完成搬移的界面入口");
            string baseDir = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "_selftest_ui_recover");
            string journalPath = System.IO.Path.Combine(baseDir, "journal.jsonl");
            string oldJournal = Environment.GetEnvironmentVariable("APPMOVER_JOURNAL");
            try
            {
                if (System.IO.Directory.Exists(baseDir))
                {
                    System.Collections.Generic.List<string> j = new System.Collections.Generic.List<string>();
                    SafeDelete.DeleteTree(baseDir, j);
                }
                System.IO.Directory.CreateDirectory(baseDir);
                Environment.SetEnvironmentVariable("APPMOVER_JOURNAL", journalPath);

                string src = System.IO.Path.Combine(baseDir, "App");
                string tgt = System.IO.Path.Combine(baseDir, "dst", "App");
                System.IO.Directory.CreateDirectory(src);
                System.IO.Directory.CreateDirectory(tgt);
                System.IO.File.WriteAllText(System.IO.Path.Combine(src, "a.txt"), "a");
                System.IO.File.WriteAllText(System.IO.Path.Combine(tgt, "half.txt"), "half");

                JournalEntry e = new JournalEntry();
                e.Id = "uitest-0001";
                e.Time = DateTime.Now.ToString("o");
                e.Original = src;
                e.Target = tgt;
                e.Status = "planned";
                e.Method = "test";
                Journal.Append(e);

                Check(Relocator.FindInterrupted().Count == 1, "构造出 1 项被中断的搬移（测试前提）");

                MainForm f = new MainForm();
                f.Show();                 // Shown 事件里会自动检查并处理
                Application.DoEvents();

                Ok("带未完成搬移的窗口能正常显示");
                Check(Relocator.FindInterrupted().Count == 0, "界面在启动时自动处理了无歧义的中断现场");
                Check(!System.IO.Directory.Exists(tgt), "目标盘的半截副本已被清理");
                Check(System.IO.Directory.Exists(src), "源目录保持完好");
                Check(System.IO.File.Exists(System.IO.Path.Combine(src, "a.txt")), "源文件未被破坏");

                f.Close();
                f.Dispose();
            }
            catch (Exception ex)
            {
                Fail("恢复入口测试异常: " + ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                Environment.SetEnvironmentVariable("APPMOVER_JOURNAL", oldJournal);
                try
                {
                    System.Collections.Generic.List<string> j = new System.Collections.Generic.List<string>();
                    SafeDelete.DeleteTree(baseDir, j);
                }
                catch { }
            }
        }

        private static void CheckButtonsInside(Form f, string tag)
        {
            int checkedCount = 0;
            foreach (Control c in All(f))
            {
                Button b = c as Button;
                if (b == null) continue;
                checkedCount++;
                Check(Inside(b), tag + "：按钮「" + b.Text + "」在窗口内 " + Rect(b));
                Check(b.Width > 20 && b.Height > 10, tag + "：按钮「" + b.Text + "」尺寸合理");
            }
            Check(checkedCount >= 2, tag + "：至少检查到 2 个按钮（实际 " + checkedCount + "）");
        }

        // ---------------- 工具 ----------------

        private static Control Find(Control root, string name)
        {
            if (root.Name == name) return root;
            foreach (Control c in root.Controls)
            {
                Control r = Find(c, name);
                if (r != null) return r;
            }
            return null;
        }

        /// <summary>从后代往上找，返回"直接挂在指定祖先下的那个子控件"。</summary>
        private static Control NearestChildOf(Control ancestor, Control descendant)
        {
            Control cur = descendant;
            while (cur != null && cur.Parent != null && cur.Parent != ancestor) cur = cur.Parent;
            if (cur != null && cur.Parent == ancestor) return cur;
            return null;
        }

        /// <summary>在容器里找位于最下方的 Dock=Top 面板。</summary>
        private static Control FindTopPanel(Control container)
        {
            Control best = null;
            foreach (Control c in container.Controls)
            {
                if (!(c is Panel)) continue;
                if (c.Dock != DockStyle.Top) continue;
                if (best == null || c.Bottom > best.Bottom) best = c;
            }
            return best;
        }

        /// <summary>找到与目标控件同父、且位于其上方的停靠面板。</summary>
        private static Control FindPanelAbove(Control target)
        {
            if (target.Parent == null) return null;
            Control best = null;
            foreach (Control c in target.Parent.Controls)
            {
                if (c == target) continue;
                if (c is Panel && c.Dock == DockStyle.Top)
                    if (best == null || c.Bottom > best.Bottom) best = c;
            }
            return best;
        }

        private static System.Collections.Generic.IEnumerable<Control> All(Control root)
        {
            foreach (Control c in root.Controls)
            {
                yield return c;
                foreach (Control g in All(c)) yield return g;
            }
        }

        private static bool Inside(Control c)
        {
            if (c.Parent == null) return false;
            Rectangle p = c.Parent.ClientRectangle;
            return c.Left >= 0 && c.Top >= 0 && c.Right <= p.Width && c.Bottom <= p.Height;
        }

        private static string Rect(Control c)
        {
            return string.Format("(L{0} T{1} {2}x{3})", c.Left, c.Top, c.Width, c.Height);
        }

        private static void Section(string s)
        {
            _sb.AppendLine();
            _sb.AppendLine("[" + s + "]");
        }

        private static void Ok(string s) { _pass++; _sb.AppendLine("  ok   " + s); }

        private static void Check(bool cond, string what)
        {
            if (cond) { _pass++; _sb.AppendLine("  ok   " + what); }
            else { _fail++; _sb.AppendLine("  FAIL " + what); }
        }

        private static void Fail(string s) { _fail++; _sb.AppendLine("  FAIL " + s); }
    }
}
