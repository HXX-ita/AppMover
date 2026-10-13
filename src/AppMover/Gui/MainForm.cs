using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using AppMover.Core;

namespace AppMover.Gui
{
    internal static class GuiEntry
    {
        [STAThread]
        private static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // 无头冒烟测试：界面代码的问题（布局重叠、控件跑到可视区外、构造抛异常）
            // 在没有人工看画面的情况下也必须能被自动发现。
            if (args.Length >= 2 && args[0] == "--smoke")
            {
                string report = UiSmoke.Run();
                try { System.IO.File.WriteAllText(args[1], report, new System.Text.UTF8Encoding(true)); }
                catch { }
                return;
            }

            Application.Run(new MainForm());
        }
    }

    /// <summary>图形界面主窗口：扫描 / 历史记录 / 关于与反馈。</summary>
    public class MainForm : Form
    {
        private TabControl _tabs;

        // 扫描页
        private ComboBox _driveBox;
        private Button _scanBtn, _stopBtn, _moveBtn;
        private CheckBox _dryRun;
        private TextBox _filter;
        private ComboBox _catFilter;
        private ListView _list;
        private TextBox _log;
        private Label _status;

        // 历史页
        private ListView _journalList;
        private Button _undoBtn, _refreshBtn;

        // 状态
        private List<ScanRow> _allRows = new List<ScanRow>();
        private volatile bool _cancel;
        private bool _busy;
        private int _sortColumn = 1;
        private bool _sortAsc = false;
        private string _targetRoot;

        public MainForm()
        {
            Text = "软件搬家 · AppMover";
            ClientSize = new Size(1120, 700);
            MinimumSize = new Size(900, 560);
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Microsoft YaHei UI", 9F);

            BuildUi();
            LoadVolumes();
            RefreshJournal();
            Log("就绪。选择目标盘后点击「开始扫描」。");
            Log("提示：搬移 Program Files / ProgramData 下的项目需要管理员权限。当前" +
                (Security.IsElevated() ? "已提权。" : "未提权。"));

            // 启动时检查上次是否有搬移没走完（断电/被强杀）。
            // 放在 Shown 而不是构造函数里：窗口先显示出来，再进行可能耗时或弹窗的检查。
            Shown += delegate { CheckInterrupted(false); };
        }

        // ================= 界面构建 =================

        private void BuildUi()
        {
            _tabs = new TabControl();
            _tabs.Dock = DockStyle.Fill;
            _tabs.Name = "tabs";
            Controls.Add(_tabs);

            _tabs.TabPages.Add(BuildScanPage());
            _tabs.TabPages.Add(BuildHistoryPage());
            _tabs.TabPages.Add(BuildAboutPage());
        }

        private TabPage BuildScanPage()
        {
            TabPage page = new TabPage("扫描与搬移");

            Panel top = new Panel();
            top.Dock = DockStyle.Top;
            top.Height = 76;

            Label l1 = new Label();
            l1.Text = "目标盘：";
            l1.Left = 12; l1.Top = 14; l1.Width = 62;
            top.Controls.Add(l1);

            _driveBox = new ComboBox();
            _driveBox.Left = 76; _driveBox.Top = 11; _driveBox.Width = 210;
            _driveBox.DropDownStyle = ComboBoxStyle.DropDownList;
            top.Controls.Add(_driveBox);

            _scanBtn = new Button();
            _scanBtn.Text = "开始扫描";
            _scanBtn.Left = 300; _scanBtn.Top = 10; _scanBtn.Width = 100; _scanBtn.Height = 27;
            _scanBtn.Click += delegate { StartScan(); };
            top.Controls.Add(_scanBtn);

            _stopBtn = new Button();
            _stopBtn.Text = "停止";
            _stopBtn.Left = 406; _stopBtn.Top = 10; _stopBtn.Width = 66; _stopBtn.Height = 27;
            _stopBtn.Enabled = false;
            _stopBtn.Click += delegate { _cancel = true; Log("已请求停止，等待当前步骤结束…"); };
            top.Controls.Add(_stopBtn);

            _dryRun = new CheckBox();
            _dryRun.Text = "干跑（只出计划，不改动文件）";
            _dryRun.Left = 490; _dryRun.Top = 14; _dryRun.Width = 220;
            top.Controls.Add(_dryRun);

            Label l2 = new Label();
            l2.Text = "搜索：";
            l2.Left = 12; l2.Top = 47; l2.Width = 48;
            top.Controls.Add(l2);

            _filter = new TextBox();
            _filter.Left = 60; _filter.Top = 44; _filter.Width = 250;
            _filter.TextChanged += delegate { ApplyFilter(); };
            top.Controls.Add(_filter);

            Label l3 = new Label();
            l3.Text = "分类：";
            l3.Left = 322; l3.Top = 47; l3.Width = 44;
            top.Controls.Add(l3);

            _catFilter = new ComboBox();
            _catFilter.Left = 368; _catFilter.Top = 44; _catFilter.Width = 170;
            _catFilter.DropDownStyle = ComboBoxStyle.DropDownList;
            _catFilter.Items.AddRange(new object[] { "全部分类", "可安全搬移", "可搬但需小心", "禁止搬移", "疑似残留可清理" });
            _catFilter.SelectedIndex = 0;
            _catFilter.SelectedIndexChanged += delegate { ApplyFilter(); };
            top.Controls.Add(_catFilter);

            _moveBtn = new Button();
            _moveBtn.Text = "搬移选中项";
            _moveBtn.Left = 552; _moveBtn.Top = 42; _moveBtn.Width = 120; _moveBtn.Height = 27;
            _moveBtn.Enabled = false;
            _moveBtn.Click += delegate { OnMove(); };
            top.Controls.Add(_moveBtn);

            _status = new Label();
            _status.Left = 684; _status.Top = 47; _status.Width = 420;
            _status.ForeColor = Color.FromArgb(90, 100, 115);
            top.Controls.Add(_status);

            // 中间：结果列表 + 底部日志
            SplitContainer split = new SplitContainer();
            split.Dock = DockStyle.Fill;
            split.Orientation = Orientation.Horizontal;
            split.SplitterDistance = 330;
            // 停靠顺序的坑：必须先添加 Fill 控件，再添加 Top 控件。
            // WinForms 按 z 序反向停靠 —— 后添加的先停靠；若先添加 Top 再添加 Fill，
            // Fill 会占满整个客户区，再叠加的 Top 会把它顶部盖住。
            page.Controls.Add(split);
            page.Controls.Add(top);

            _list = new ListView();
            _list.Dock = DockStyle.Fill;
            _list.Name = "resultList";
            _list.View = View.Details;
            _list.FullRowSelect = true;
            _list.MultiSelect = false;
            _list.HideSelection = false;
            _list.Columns.Add("名称", 200);
            _list.Columns.Add("大小", 90, HorizontalAlignment.Right);
            _list.Columns.Add("文件数", 70, HorizontalAlignment.Right);
            _list.Columns.Add("分类", 110);
            _list.Columns.Add("置信度", 66);
            _list.Columns.Add("路径", 340);
            _list.Columns.Add("判定依据", 420);
            _list.ColumnClick += delegate (object s, ColumnClickEventArgs e)
            {
                if (_sortColumn == e.Column) _sortAsc = !_sortAsc;
                else { _sortColumn = e.Column; _sortAsc = (e.Column == 0); }
                ApplyFilter();
            };
            _list.SelectedIndexChanged += delegate
            {
                _moveBtn.Enabled = _list.SelectedItems.Count > 0 && !_busy;
            };
            _list.DoubleClick += delegate { OnMove(); };
            split.Panel1.Controls.Add(_list);

            _log = new TextBox();
            _log.Dock = DockStyle.Fill;
            _log.Multiline = true;
            _log.ReadOnly = true;
            _log.ScrollBars = ScrollBars.Both;
            _log.WordWrap = false;
            _log.BackColor = Color.FromArgb(30, 34, 40);
            _log.ForeColor = Color.FromArgb(220, 226, 232);
            _log.Font = new Font("Consolas", 9F);
            split.Panel2.Controls.Add(_log);

            return page;
        }

        private TabPage BuildHistoryPage()
        {
            TabPage page = new TabPage("历史记录与撤销");

            Panel top = new Panel();
            top.Dock = DockStyle.Top;
            top.Height = 46;

            _refreshBtn = new Button();
            _refreshBtn.Text = "刷新";
            _refreshBtn.Left = 12; _refreshBtn.Top = 9; _refreshBtn.Width = 70; _refreshBtn.Height = 27;
            _refreshBtn.Click += delegate { RefreshJournal(); };
            top.Controls.Add(_refreshBtn);

            _undoBtn = new Button();
            _undoBtn.Text = "撤销选中项";
            _undoBtn.Left = 88; _undoBtn.Top = 9; _undoBtn.Width = 110; _undoBtn.Height = 27;
            _undoBtn.Enabled = false;
            _undoBtn.Click += delegate { OnUndo(); };
            top.Controls.Add(_undoBtn);

            Button recoverBtn = new Button();
            recoverBtn.Text = "处理未完成的搬移";
            recoverBtn.Left = 204; recoverBtn.Top = 9; recoverBtn.Width = 140; recoverBtn.Height = 27;
            recoverBtn.Click += delegate { CheckInterrupted(true); };
            top.Controls.Add(recoverBtn);

            Label hint = new Label();
            hint.Text = "撤销 = 拆掉联接 → 把数据搬回原位置 → 校验 → 删除目标盘副本。只有状态为 completed 的记录可以撤销。";
            hint.Left = 356; hint.Top = 14; hint.Width = 620;
            hint.ForeColor = Color.FromArgb(90, 100, 115);
            top.Controls.Add(hint);

            _journalList = new ListView();
            _journalList.Dock = DockStyle.Fill;
            _journalList.View = View.Details;
            _journalList.FullRowSelect = true;
            _journalList.MultiSelect = false;
            _journalList.HideSelection = false;
            _journalList.Columns.Add("记录号", 190);
            _journalList.Columns.Add("时间", 150);
            _journalList.Columns.Add("状态", 90);
            _journalList.Columns.Add("大小", 90, HorizontalAlignment.Right);
            _journalList.Columns.Add("原路径", 380);
            _journalList.Columns.Add("目标路径", 380);
            _journalList.SelectedIndexChanged += delegate
            {
                _undoBtn.Enabled = _journalList.SelectedItems.Count > 0 && !_busy
                    && _journalList.SelectedItems[0].SubItems[2].Text == "completed";
            };
            page.Controls.Add(_journalList);
            page.Controls.Add(top);

            return page;
        }

        private TabPage BuildAboutPage()
        {
            TabPage page = new TabPage("关于与反馈");

            TextBox info = new TextBox();
            info.Dock = DockStyle.Fill;
            info.Multiline = true;
            info.ReadOnly = true;
            info.ScrollBars = ScrollBars.Vertical;
            info.BackColor = Color.White;
            info.Font = new Font("Microsoft YaHei UI", 9.5F);
            info.Text =
                "软件搬家 · AppMover" + Environment.NewLine +
                "版本 " + VersionText() + "    仅支持 Windows" + Environment.NewLine + Environment.NewLine +
                "把 C 盘上的软件与数据文件夹安全搬到其他磁盘：" + Environment.NewLine +
                "复制 → 校验 → 改名让位 → 在原位置建立目录联接 → 验证 → 删除旧副本。" + Environment.NewLine +
                "目录联接对软件完全透明 —— 路径字符串不变，所以软件照常可用。" + Environment.NewLine + Environment.NewLine +
                "安全底线：" + Environment.NewLine +
                "  · 校验未通过前绝不删除源目录" + Environment.NewLine +
                "  · 全程不存在「两份数据都没了」的时刻，任何一步失败都会回滚" + Environment.NewLine +
                "  · 每次搬移都写记录，可一键撤销" + Environment.NewLine +
                "  · 云同步目录（OneDrive 等）一律禁止搬移 —— 复制云占位文件会触发全量下载" + Environment.NewLine + Environment.NewLine +
                "权限说明：" + Environment.NewLine +
                "  · 扫描、搬移用户目录（AppData 等）不需要管理员" + Environment.NewLine +
                "  · 搬移 Program Files / ProgramData 需要管理员：请用下面的按钮以管理员身份重启" + Environment.NewLine + Environment.NewLine +
                "反馈：" + Environment.NewLine +
                "  遇到判定明显不对、或程序报错，请点下面的「生成反馈报告」。" + Environment.NewLine +
                "  报告只包含目录名/路径/判定依据/环境版本，不含任何文件内容；" + Environment.NewLine +
                "  路径中的用户名与计算机名会被替换为占位符。" + Environment.NewLine +
                "  生成后浏览器会打开一个已预填内容的提交页面，你可以先过目再提交。";

            Panel bottom = new Panel();
            bottom.Dock = DockStyle.Bottom;
            bottom.Height = 52;

            Button fb = new Button();
            fb.Text = "生成反馈报告并打开提交页";
            fb.Left = 12; fb.Top = 12; fb.Width = 210; fb.Height = 30;
            fb.Click += delegate { OnFeedback(); };
            bottom.Controls.Add(fb);

            Button fbUnknown = new Button();
            fbUnknown.Text = "收集「规则拿不准」的目录并反馈";
            fbUnknown.Left = 232; fbUnknown.Top = 12; fbUnknown.Width = 240; fbUnknown.Height = 30;
            fbUnknown.Click += delegate { OnFeedbackUnknown(); };
            bottom.Controls.Add(fbUnknown);

            Button elevate = new Button();
            elevate.Text = "以管理员身份重启";
            elevate.Left = 482; elevate.Top = 12; elevate.Width = 150; elevate.Height = 30;
            elevate.Click += delegate { RelaunchElevated(); };
            bottom.Controls.Add(elevate);

            Button openDir = new Button();
            openDir.Text = "打开程序所在目录";
            openDir.Left = 642; openDir.Top = 12; openDir.Width = 150; openDir.Height = 30;
            openDir.Click += delegate
            {
                try { System.Diagnostics.Process.Start("explorer.exe", "\"" + AppDomain.CurrentDomain.BaseDirectory + "\""); }
                catch (Exception ex) { Log("打开目录失败：" + ex.Message); }
            };
            bottom.Controls.Add(openDir);

            page.Controls.Add(info);
            page.Controls.Add(bottom);

            return page;
        }

        private static string VersionText()
        {
            try { return typeof(MainForm).Assembly.GetName().Version.ToString(); }
            catch { return "0.1.0.0"; }
        }

        // ================= 卷与目标盘 =================

        private void LoadVolumes()
        {
            _driveBox.Items.Clear();
            List<VolumeInfo> vols = Volumes.Enumerate();
            VolumeInfo def = Volumes.PickDefaultTarget(vols);
            foreach (VolumeInfo v in vols)
            {
                string label = v.Letter + "\\  (" + v.FileSystem + ", 可用 " +
                    (v.FreeBytes / 1073741824.0).ToString("F1") + " GB)" +
                    (v.CanHostJunction ? "" : "  [不可作为目标]")
                    + (v.IsSystem ? "  [系统盘]" : "");
                _driveBox.Items.Add(new VolumeItem(v, label));
            }
            for (int i = 0; i < _driveBox.Items.Count; i++)
            {
                VolumeItem it = (VolumeItem)_driveBox.Items[i];
                if (def != null && it.Volume.Letter == def.Letter) { _driveBox.SelectedIndex = i; break; }
            }
            if (_driveBox.SelectedIndex < 0 && _driveBox.Items.Count > 0) _driveBox.SelectedIndex = 0;

            if (def == null)
            {
                _scanBtn.Enabled = false;
                Log("警告：没有可作为目标的盘（需要另一块 NTFS 固定磁盘）。扫描仍可进行，但无法搬移。");
            }
        }

        private class VolumeItem
        {
            public VolumeInfo Volume;
            private readonly string _text;
            public VolumeItem(VolumeInfo v, string text) { Volume = v; _text = text; }
            public override string ToString() { return _text; }
        }

        private string SelectedTargetRoot()
        {
            VolumeItem it = _driveBox.SelectedItem as VolumeItem;
            if (it == null) return null;
            return it.Volume.Letter + "\\";
        }

        // ================= 日志 =================

        private void Log(string s)
        {
            if (InvokeRequired) { BeginInvoke((MethodInvoker)delegate { Log(s); }); return; }
            _log.AppendText(s + Environment.NewLine);
        }

        private void SetBusy(bool busy)
        {
            _busy = busy;
            _scanBtn.Enabled = !busy;
            _stopBtn.Enabled = busy;
            _driveBox.Enabled = !busy;
            _refreshBtn.Enabled = !busy;
            _moveBtn.Enabled = !busy && _list.SelectedItems.Count > 0;
            _undoBtn.Enabled = !busy && _journalList.SelectedItems.Count > 0
                && _journalList.SelectedItems[0].SubItems[2].Text == "completed";
            Cursor = busy ? Cursors.WaitCursor : Cursors.Default;
        }

        private void RunBusy(string what, Action work)
        {
            if (_busy) { Log("有任务正在进行，请等待完成。"); return; }
            SetBusy(true);
            Log("— " + what);
            Thread t = new Thread(delegate ()
            {
                try { work(); }
                catch (Exception ex) { Log("出错：" + ex.GetType().Name + ": " + ex.Message); }
                finally { BeginInvoke((MethodInvoker)delegate { SetBusy(false); }); }
            });
            t.IsBackground = true;
            t.Start();
        }

        // ================= 扫描 =================

        private void StartScan()
        {
            _targetRoot = SelectedTargetRoot();
            if (_targetRoot != null)
            {
                VolumeItem it = _driveBox.SelectedItem as VolumeItem;
                if (it != null && !it.Volume.CanHostJunction)
                    Log("注意：所选盘不能作为搬移目标（" + it.Volume.WhyNotHost() + "），扫描结果仅供参考。");
            }

            _cancel = false;
            _allRows = new List<ScanRow>();
            _list.Items.Clear();
            _status.Text = "";

            RunBusy("扫描中…", delegate ()
            {
                SoftwareIndex idx = SoftwareIndex.Load();
                Log("软件索引：卸载登记项 " + idx.UninstallEntryCount + " 项，服务 " + idx.ServiceCount + " 项");
                Classifier cls = new Classifier(idx);
                List<ScanRow> rows = Scanner.ScanAll(cls, this.Log);
                if (_cancel) { Log("扫描已取消（结果不完整，未采用）。"); return; }
                _allRows = rows;
                BeginInvoke((MethodInvoker)delegate { ApplyFilter(); });
                Log("扫描完成，共 " + rows.Count + " 个目录。");
            });
        }

        private void ApplyFilter()
        {
            string q = _filter.Text.Trim().ToLowerInvariant();
            string cat = _catFilter.SelectedIndex <= 0 ? null : (string)_catFilter.SelectedItem;

            List<ScanRow> rows = new List<ScanRow>();
            for (int i = 0; i < _allRows.Count; i++)
            {
                ScanRow r = _allRows[i];
                if (cat != null && r.Category != cat) continue;
                if (q.Length > 0)
                {
                    if (r.Name.ToLowerInvariant().IndexOf(q, StringComparison.Ordinal) < 0 &&
                        r.Path.ToLowerInvariant().IndexOf(q, StringComparison.Ordinal) < 0) continue;
                }
                rows.Add(r);
            }
            rows.Sort(CompareRows);

            _list.BeginUpdate();
            _list.Items.Clear();
            long totalBytes = 0;
            for (int i = 0; i < rows.Count; i++)
            {
                ScanRow r = rows[i];
                totalBytes += r.Bytes;
                ListViewItem it = new ListViewItem(r.Name);
                it.SubItems.Add(Human(r.Bytes));
                it.SubItems.Add(r.Files.ToString("N0"));
                it.SubItems.Add(r.Category);
                it.SubItems.Add(r.Confidence);
                it.SubItems.Add(r.Path);
                it.SubItems.Add(r.Reasons);
                it.Tag = r;
                it.ForeColor = ColorFor(r.CategoryKey);
                _list.Items.Add(it);
            }
            _list.EndUpdate();

            _status.Text = string.Format("显示 {0} 项，合计 {1}", rows.Count, Human(totalBytes));
            SetBusy(_busy);
        }

        private int CompareRows(ScanRow a, ScanRow b)
        {
            int r;
            switch (_sortColumn)
            {
                case 1: r = a.Bytes.CompareTo(b.Bytes); break;
                case 2: r = a.Files.CompareTo(b.Files); break;
                case 3: r = string.Compare(a.Category, b.Category, StringComparison.CurrentCulture); break;
                case 4: r = string.Compare(a.Confidence, b.Confidence, StringComparison.Ordinal); break;
                case 5: r = string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase); break;
                case 6: r = string.Compare(a.Reasons, b.Reasons, StringComparison.CurrentCulture); break;
                default: r = string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase); break;
            }
            return _sortAsc ? r : -r;
        }

        private static Color ColorFor(string categoryKey)
        {
            switch (categoryKey)
            {
                case "safe": return Color.FromArgb(0x12, 0x60, 0x43);
                case "caution": return Color.FromArgb(0x8a, 0x52, 0x00);
                case "forbidden": return Color.FromArgb(0xa3, 0x22, 0x1c);
                default: return Color.FromArgb(0x53, 0x39, 0x96);
            }
        }

        private static string Human(long bytes)
        {
            if (bytes >= 1073741824L) return (bytes / 1073741824.0).ToString("N2") + " GB";
            if (bytes >= 1048576L) return (bytes / 1048576.0).ToString("N1") + " MB";
            if (bytes >= 1024L) return (bytes / 1024.0).ToString("N0") + " KB";
            return bytes + " B";
        }

        // ================= 搬移 =================

        private void OnMove()
        {
            if (_list.SelectedItems.Count == 0) return;
            ScanRow row = (ScanRow)_list.SelectedItems[0].Tag;
            string targetRoot = SelectedTargetRoot();
            if (targetRoot == null)
            {
                MessageBox.Show(this, "没有选择目标盘。", "无法搬移", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            bool dry = _dryRun.Checked;

            RunBusy("准备搬移：" + row.Path, delegate ()
            {
                SoftwareIndex idx = SoftwareIndex.Load();
                Classifier cls = new Classifier(idx);
                Log("预检中（判定规则、占用检测、硬链接检查、空间校验）…");
                MovePlan plan = Relocator.BuildPlan(row.Path, targetRoot, cls, idx, false, false);

                DialogResult res = DialogResult.Cancel;
                Invoke((MethodInvoker)delegate
                {
                    using (ConfirmDialog dlg = new ConfirmDialog(plan, dry))
                    {
                        res = dlg.ShowDialog(this);
                    }
                });

                if (res != DialogResult.OK) { Log("已取消。"); return; }

                MoveResult r = Relocator.Execute(plan, dry, this.Log);
                Log("结果：" + r.Status + " — " + r.Message);
                if (r.Status == "failed" || r.Status == "blocked")
                    BeginInvoke((MethodInvoker)delegate
                    {
                        MessageBox.Show(this, r.Message, "搬移未完成", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    });
                BeginInvoke((MethodInvoker)delegate { RefreshJournal(); });
            });
        }

        /// <summary>
        /// 处理被中断的搬移（断电 / 被强杀）。
        ///
        /// 为什么界面必须管这件事：搬移中途死掉时，数据其实没丢（用的是改名让位而不是删除），
        /// 但原路径会空着、软件打不开。用户只会看到"软件坏了"，根本不知道有一条恢复命令。
        /// 所以启动时主动提示，并把交互入口放在历史页。
        /// 结论无歧义的（只是日志没更新、或只剩半截副本）直接自动处理，不打扰用户。
        /// </summary>
        private void CheckInterrupted(bool interactive)
        {
            List<Relocator.RecoveryItem> items = Relocator.FindInterrupted();
            if (items.Count == 0)
            {
                if (interactive)
                    MessageBox.Show(this, "没有未完成的搬移操作。", "检查完成",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            Log("检测到 " + items.Count + " 次未完成的搬移操作。");
            for (int i = 0; i < items.Count; i++)
            {
                Relocator.RecoveryItem it = items[i];
                Log("  记录 " + it.JournalId + "：" + it.Situation);

                if (it.AutoFixable)
                {
                    MoveResult rr = Relocator.Recover(it, false, this.Log);
                    Log("    自动处理：" + rr.Message);
                    continue;
                }

                if (!it.CanRestore && !it.CanComplete)
                {
                    Log("    需要人工检查（数据可能在 " + it.Target + "）");
                    if (interactive)
                        MessageBox.Show(this,
                            "记录 " + it.JournalId + " 需要人工检查：\r\n\r\n" + it.Situation
                            + "\r\n\r\n数据可能在：" + it.Target,
                            "需要人工处理", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    continue;
                }

                if (!interactive)
                {
                    Log("    请到「历史记录与撤销」页点「处理未完成的搬移」来选择处理方式");
                    continue;
                }

                string msg = "上次的搬移没有完成（可能是断电，或程序被强制结束）。\r\n\r\n"
                    + "原路径：" + it.Original + "\r\n"
                    + "目标路径：" + it.Target + "\r\n\r\n"
                    + "现场：" + it.Situation + "\r\n\r\n"
                    + "数据没有丢失。请选择处理方式：\r\n\r\n"
                    + "「是」= 把搬移做完（原路径变成指向目标盘的联接）\r\n"
                    + "「否」= 还原到操作前的状态（数据搬回原路径）\r\n"
                    + "「取消」= 先不动，稍后处理";
                DialogResult r = MessageBox.Show(this, msg, "未完成的搬移 " + it.JournalId,
                    MessageBoxButtons.YesNoCancel, MessageBoxIcon.Warning);
                if (r == DialogResult.Cancel) { Log("    已跳过，稍后可再次处理"); continue; }

                MoveResult res = Relocator.Recover(it, r == DialogResult.Yes, this.Log);
                Log("    处理结果：" + res.Message);
            }
            RefreshJournal();
        }

        private void OnUndo()
        {
            if (_journalList.SelectedItems.Count == 0) return;
            JournalEntry e = (JournalEntry)_journalList.SelectedItems[0].Tag;
            if (e == null) return;

            DialogResult ask = MessageBox.Show(this,
                "确定要撤销这次搬移吗？\r\n\r\n原路径：" + e.Original + "\r\n目标路径：" + e.Target +
                "\r\n\r\n将把数据搬回原位置并删除目标盘上的副本。",
                "确认撤销", MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
            if (ask != DialogResult.OK) return;

            RunBusy("撤销：" + e.Id, delegate ()
            {
                MoveResult r = Relocator.Undo(e, this.Log);
                Log("结果：" + r.Status + " — " + r.Message);
                if (!r.Success)
                    BeginInvoke((MethodInvoker)delegate
                    {
                        MessageBox.Show(this, r.Message, "撤销未完成", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    });
                BeginInvoke((MethodInvoker)delegate { RefreshJournal(); });
            });
        }

        private void RefreshJournal()
        {
            _journalList.BeginUpdate();
            _journalList.Items.Clear();
            List<JournalEntry> all = Journal.ReadAll();
            for (int i = all.Count - 1; i >= 0; i--)
            {
                JournalEntry e = all[i];
                ListViewItem it = new ListViewItem(e.Id);
                it.SubItems.Add(SafeTime(e.Time));
                it.SubItems.Add(e.Status);
                it.SubItems.Add(Human(e.Bytes));
                it.SubItems.Add(e.Original);
                it.SubItems.Add(e.Target);
                it.Tag = e;
                if (e.Status == "completed") it.ForeColor = Color.FromArgb(0x12, 0x60, 0x43);
                else if (e.Status == "reverted") it.ForeColor = Color.FromArgb(0x53, 0x39, 0x96);
                else it.ForeColor = Color.FromArgb(0xa3, 0x22, 0x1c);
                _journalList.Items.Add(it);
            }
            _journalList.EndUpdate();
            SetBusy(_busy);
        }

        private static string SafeTime(string t)
        {
            try { return DateTime.Parse(t).ToString("yyyy-MM-dd HH:mm:ss"); }
            catch { return t; }
        }

        // ================= 反馈与提权 =================

        private void OnFeedback()
        {
            RunBusy("生成反馈报告", delegate ()
            {
                string selected = null;
                BeginInvoke((MethodInvoker)delegate
                {
                    if (_list.SelectedItems.Count > 0)
                    {
                        ScanRow r = (ScanRow)_list.SelectedItems[0].Tag;
                        selected = r.Path;
                    }
                });
                Thread.Sleep(120);   // 让上面的 BeginInvoke 先执行完
                DoFeedback(selected, false);
            });
        }

        private void OnFeedbackUnknown()
        {
            RunBusy("收集规则拿不准的目录（需要重新扫描一次）", delegate ()
            {
                DoFeedback(null, true);
            });
        }

        private void DoFeedback(string path, bool scanUnknown)
        {
            FeedbackReport rep = Feedback.Collect(path, null, scanUnknown, this.Log);
            string jsonPath;
            string dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "feedback");
            string md = Feedback.WriteReport(rep, dir, out jsonPath);
            Log("报告已保存：" + md);
            Log("            " + jsonPath);

            string url = Feedback.BuildIssueUrl(
                path != null ? "[反馈] 目录判定：" + rep.PathInQuestion : "[反馈] 判定/兼容性反馈",
                Feedback.ToMarkdown(rep));

            if (!Feedback.IsRepoConfigured)
                Log("注意：反馈仓库尚未配置（链接中的 REPLACE-ME 需替换）。配置方法见 docs/FEEDBACK.md。");

            Invoke((MethodInvoker)delegate
            {
                DialogResult r = MessageBox.Show(this,
                    "报告已生成：\r\n" + md + "\r\n\r\n" +
                    "只包含目录名/路径/判定依据/环境版本，不含任何文件内容，\r\n" +
                    "用户名与计算机名已替换为占位符。\r\n\r\n" +
                    "现在打开浏览器提交吗？",
                    "反馈报告", MessageBoxButtons.OKCancel, MessageBoxIcon.Information);
                if (r == DialogResult.OK)
                {
                    if (!Feedback.OpenUrl(url)) Log("无法自动打开浏览器，链接：\r\n" + url);
                }
            });
        }

        private void RelaunchElevated()
        {
            try
            {
                System.Diagnostics.ProcessStartInfo psi =
                    new System.Diagnostics.ProcessStartInfo(Application.ExecutablePath);
                psi.UseShellExecute = true;
                psi.Verb = "runas";
                System.Diagnostics.Process.Start(psi);
                Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "提权启动失败：" + ex.Message, "无法提权",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
    }
}
