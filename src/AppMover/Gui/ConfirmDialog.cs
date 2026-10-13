using System;
using System.Drawing;
using System.Windows.Forms;
using AppMover.Core;

namespace AppMover.Gui
{
    /// <summary>
    /// 搬移确认窗口。
    ///
    /// 设计立场：搬移是高风险操作，确认窗口不是走个形式 —— 它必须把
    /// 「判定依据、风险点、前置动作（要关哪些程序）」全部摊开，
    /// 并且在有阻止项时**只给关闭按钮**，不给"强行继续"。
    /// 唯一能强行继续的情形是「可搬但需小心」，那时也必须先勾选确认。
    /// </summary>
    public class ConfirmDialog : Form
    {
        private readonly MovePlan _plan;
        private readonly bool _dryRun;
        private CheckBox _confirm;
        private Button _ok;

        public ConfirmDialog(MovePlan plan, bool dryRun)
        {
            _plan = plan;
            _dryRun = dryRun;

            Text = plan.CanProceed ? "确认搬移" : "无法搬移";
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(720, 560);
            MinimumSize = new Size(620, 480);
            Font = new Font("Microsoft YaHei UI", 9F);
            MaximizeBox = false;
            MinimizeBox = false;

            Build();
        }

        private void Build()
        {
            TableLayoutPanel root = new TableLayoutPanel();
            root.Dock = DockStyle.Fill;
            root.ColumnCount = 1;
            root.RowCount = 3;
            root.Padding = new Padding(14);
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 118));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 62));
            Controls.Add(root);

            // ---- 概要 ----
            TableLayoutPanel sum = new TableLayoutPanel();
            sum.Dock = DockStyle.Fill;
            sum.ColumnCount = 2;
            sum.RowCount = 5;
            sum.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 92));
            sum.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            for (int i = 0; i < 5; i++) sum.RowStyles.Add(new RowStyle(SizeType.Percent, 20));
            root.Controls.Add(sum, 0, 0);

            AddRow(sum, 0, "源目录", _plan.Source);
            AddRow(sum, 1, "目标位置", _plan.Target);
            AddRow(sum, 2, "大小", string.Format("{0:N2} MB ／ {1} 个文件",
                _plan.Bytes / 1048576.0, _plan.Files));
            AddRow(sum, 3, "目标盘可用", string.Format("{0:N2} GB（搬移后约剩 {1:N2} GB）",
                _plan.TargetFreeBytes / 1073741824.0,
                (_plan.TargetFreeBytes - _plan.Bytes) / 1073741824.0));
            AddRow(sum, 4, "判定", _plan.Category + "（置信度 " + _plan.Confidence + "）");

            // ---- 详情 ----
            TextBox detail = new TextBox();
            detail.Dock = DockStyle.Fill;
            detail.Multiline = true;
            detail.ReadOnly = true;
            detail.ScrollBars = ScrollBars.Vertical;
            detail.BackColor = Color.White;
            detail.Font = new Font("Microsoft YaHei UI", 9F);
            detail.Text = BuildDetail();
            root.Controls.Add(detail, 0, 1);

            // ---- 底部按钮 ----
            Panel bottom = new Panel();
            bottom.Dock = DockStyle.Fill;
            root.Controls.Add(bottom, 0, 2);

            _confirm = new CheckBox();
            _confirm.Text = _dryRun
                ? "我已了解：本次是干跑，只生成计划，不会改动任何文件"
                : "我已阅读并确认上述风险，并已按「需要先做的事」处理完毕";
            _confirm.AutoSize = false;
            _confirm.Dock = DockStyle.Top;
            _confirm.Height = 24;
            _confirm.Checked = false;
            _confirm.CheckedChanged += delegate { UpdateOkEnabled(); };
            bottom.Controls.Add(_confirm);

            // 按钮布局用 FlowLayoutPanel(RightToLeft)：
            // 之前用 Anchor=Right 配合 Left = 父容器宽度 - N 定位，但父容器此时宽度还是 0，
            // 锚定偏移会算错、按钮跑到窗口外。RightToLeft 流式布局不需要知道宽度。
            FlowLayoutPanel buttons = new FlowLayoutPanel();
            buttons.Dock = DockStyle.Bottom;
            buttons.Height = 38;
            buttons.FlowDirection = FlowDirection.RightToLeft;
            buttons.WrapContents = false;
            buttons.Padding = new Padding(0, 4, 0, 0);
            bottom.Controls.Add(buttons);

            _ok = new Button();
            _ok.Text = _dryRun ? "开始干跑" : "开始搬移";
            _ok.Width = 120;
            _ok.Height = 28;
            _ok.DialogResult = DialogResult.OK;
            buttons.Controls.Add(_ok);          // RightToLeft：先添加的在最右边

            Button cancel = new Button();
            cancel.Text = "取消";
            cancel.Width = 100;
            cancel.Height = 28;
            cancel.DialogResult = DialogResult.Cancel;
            buttons.Controls.Add(cancel);

            if (!_plan.CanProceed)
            {
                _ok.Enabled = false;
                _ok.Text = "无法搬移";
                _confirm.Text = "存在阻止项，无法继续（请先解决上方 X 标记的问题）";
                _confirm.Enabled = false;
                cancel.Text = "关闭";
                AcceptButton = cancel;
                CancelButton = cancel;
            }
            else
            {
                AcceptButton = _ok;
                CancelButton = cancel;
            }
            UpdateOkEnabled();
        }

        private void UpdateOkEnabled()
        {
            if (!_plan.CanProceed) { _ok.Enabled = false; return; }
            _ok.Enabled = _confirm.Checked;
        }

        private static void AddRow(TableLayoutPanel panel, int row, string label, string value)
        {
            Label l = new Label();
            l.Text = label;
            l.Dock = DockStyle.Fill;
            l.TextAlign = ContentAlignment.MiddleLeft;
            l.ForeColor = Color.FromArgb(90, 100, 115);
            panel.Controls.Add(l, 0, row);

            Label v = new Label();
            v.Text = value;
            v.Dock = DockStyle.Fill;
            v.TextAlign = ContentAlignment.MiddleLeft;
            v.AutoEllipsis = true;
            panel.Controls.Add(v, 1, row);
        }

        private string BuildDetail()
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder();

            sb.AppendLine("【为什么这样判定】");
            sb.AppendLine("  " + _plan.Reasons);
            sb.AppendLine();

            if (_plan.Blockers.Count > 0)
            {
                sb.AppendLine("【阻止项 — 必须解决后才能搬移】");
                for (int i = 0; i < _plan.Blockers.Count; i++)
                    sb.AppendLine("  X  " + _plan.Blockers[i]);
                sb.AppendLine();
            }

            if (_plan.Occupying.Count > 0)
            {
                sb.AppendLine("【需要先做的事：以下程序正在使用这个目录，请先关闭它们】");
                for (int i = 0; i < _plan.Occupying.Count; i++)
                    sb.AppendLine("  !  " + _plan.Occupying[i]);
                sb.AppendLine("  （如果不关闭，复制可能跳过它们占用的文件，或后续步骤失败）");
                sb.AppendLine();
            }

            if (_plan.Warnings.Count > 0)
            {
                sb.AppendLine("【风险提示】");
                for (int i = 0; i < _plan.Warnings.Count; i++)
                    sb.AppendLine("  !  " + _plan.Warnings[i]);
                sb.AppendLine();
            }

            sb.AppendLine("【将会执行的操作】");
            if (_dryRun)
            {
                sb.AppendLine("  干跑：只计算并展示计划，不改动任何文件。");
            }
            else
            {
                sb.AppendLine("  1. 复制到目标盘（robocopy，保留 ACL 与时间戳）");
                sb.AppendLine("  2. 校验：文件数 + 总字节 + 抽样 SHA256 哈希");
                sb.AppendLine("  3. 把源目录改名为 .appmover-old-*（换出可回滚的窗口）");
                sb.AppendLine("  4. 在原位置建立目录联接，让软件继续按原路径工作");
                sb.AppendLine("  5. 验证联接（解析目标 + 通过联接读取文件）");
                sb.AppendLine("  6. 删除改名后的旧副本");
                sb.AppendLine();
                sb.AppendLine("  任何一步失败都会回滚；全程不会出现「两份数据都没了」的情况。");
                sb.AppendLine("  完成后可在「历史记录」页一键撤销，把数据搬回原处。");
            }
            sb.AppendLine();
            sb.AppendLine("【搬移后请注意】");
            sb.AppendLine("  · 目标盘从此是这个软件的必需磁盘：拔掉、损坏或盘符变化，软件将无法启动。");
            sb.AppendLine("  · 自带自动更新器的软件可能重建目录，破坏联接；出问题请用「撤销」恢复。");

            return sb.ToString();
        }
    }
}
