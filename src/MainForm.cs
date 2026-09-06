using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace WeChatChime
{
    internal sealed class MainForm : Form
    {
        private static readonly Color Ink = Color.FromArgb(31, 47, 41);
        private static readonly Color Muted = Color.FromArgb(111, 122, 116);
        private static readonly Color Green = Color.FromArgb(35, 104, 74);
        private static readonly Color Canvas = Color.FromArgb(246, 247, 242);
        private static readonly Color Line = Color.FromArgb(223, 230, 222);

        private AppSettings _settings;
        private readonly SettingsStore _store;
        private readonly SoundService _sound;
        private readonly WeChatMonitor _monitor;
        private MonitorSnapshot _snapshot;
        private string _selectedId;
        private bool _refreshing;
        private bool _exitRequested;
        private bool _started;
        private bool _disposed;
        private readonly ToolTip _tips = new ToolTip();
        private readonly ListView _contacts = new BufferedListView();
        private readonly Label _empty = new Label();
        private readonly Label _count = new Label();
        private readonly Label _selectedName = new Label();
        private readonly Label _editorHelp = new Label();
        private readonly Label _feedback = new Label();
        private readonly Label _status = new Label();
        private readonly Label _detail = new Label();
        private readonly Label _checked = new Label();
        private readonly Button _pause = new Button();
        private readonly Button _delete = new Button();
        private readonly Button _preview = new Button();
        private readonly Button _import = new Button();
        private readonly CheckBox _ruleEnabled = new CheckBox();
        private readonly CheckBox _compatibility = new CheckBox();
        private readonly ComboBox _sounds = new ComboBox();
        private readonly NumericUpDown _cooldown = new NumericUpDown();
        private readonly NotifyIcon _tray = new NotifyIcon();
        private readonly ToolStripMenuItem _trayPause = new ToolStripMenuItem();
        private readonly Icon _appIcon;

        public MainForm(AppSettings settings, SettingsStore store, SoundService sound, WeChatMonitor monitor)
        {
            if (settings == null) throw new ArgumentNullException("settings");
            if (store == null) throw new ArgumentNullException("store");
            if (sound == null) throw new ArgumentNullException("sound");
            if (monitor == null) throw new ArgumentNullException("monitor");
            _settings = settings.Clone();
            _store = store;
            _sound = sound;
            _monitor = monitor;

            Text = "微信重点消息提醒";
            Name = "WeChatChimeMainForm";
            AccessibleName = "微信重点消息提醒主窗口";
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Regular, GraphicsUnit.Point);
            ForeColor = Ink;
            BackColor = Canvas;
            ClientSize = new Size(810, 612);
            MinimumSize = new Size(760, 652);
            StartPosition = FormStartPosition.CenterScreen;
            KeyPreview = true;
            _appIcon = MakeIcon();
            Icon = _appIcon;

            BuildWindow();
            BuildTray();
            BindEvents();
            RefreshContacts();
            RefreshGlobalState();
            RefreshConnection();
        }

        private void BuildWindow()
        {
            TableLayoutPanel root = new TableLayoutPanel();
            root.Dock = DockStyle.Fill;
            root.Padding = new Padding(24, 20, 24, 16);
            root.ColumnCount = 1;
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            root.RowCount = 5;
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 78));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 64));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 88));
            Controls.Add(root);

            TableLayoutPanel header = new TableLayoutPanel();
            header.Dock = DockStyle.Fill;
            header.Margin = Padding.Empty;
            header.ColumnCount = 3;
            header.RowCount = 1;
            header.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 52));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 114));
            BellMark mark = new BellMark();
            mark.Size = new Size(40, 40);
            mark.Margin = new Padding(0, 4, 10, 0);
            mark.AccessibleName = "提醒助手";
            header.Controls.Add(mark, 0, 0);
            TableLayoutPanel heading = new TableLayoutPanel();
            heading.Dock = DockStyle.Fill;
            heading.Margin = Padding.Empty;
            heading.RowCount = 2;
            heading.ColumnCount = 1;
            heading.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            heading.RowStyles.Add(new RowStyle(SizeType.Absolute, 33));
            heading.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            Label title = NewLabel("微信重点消息", 20F, FontStyle.Bold, Ink);
            title.Margin = Padding.Empty;
            title.Dock = DockStyle.Fill;
            heading.Controls.Add(title, 0, 0);
            Label subtitle = NewLabel("给重要的人，设置专属提示音。", 9F, FontStyle.Regular, Muted);
            subtitle.Dock = DockStyle.Fill;
            subtitle.Margin = new Padding(1, 6, 0, 0);
            heading.Controls.Add(subtitle, 0, 1);
            header.Controls.Add(heading, 1, 0);
            StyleButton(_pause, "暂停提醒", true);
            _pause.Name = "PauseButton";
            _pause.AccessibleName = "暂停或恢复全部提醒";
            _pause.Dock = DockStyle.Top;
            _pause.Height = 36;
            _pause.Margin = new Padding(4, 8, 0, 0);
            header.Controls.Add(_pause, 2, 0);
            root.Controls.Add(header, 0, 0);

            TableLayoutPanel body = new TableLayoutPanel();
            body.Dock = DockStyle.Fill;
            body.Margin = Padding.Empty;
            body.ColumnCount = 2;
            body.RowCount = 1;
            body.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 48));
            body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 52));
            body.Controls.Add(BuildContactCard(), 0, 0);
            body.Controls.Add(BuildEditorCard(), 1, 0);
            root.Controls.Add(body, 0, 1);

            FlowLayoutPanel options = new FlowLayoutPanel();
            options.Dock = DockStyle.Fill;
            options.WrapContents = false;
            options.Margin = Padding.Empty;
            options.Padding = new Padding(0, 19, 0, 0);
            Label cooldownCaption = NewLabel("连续消息间隔", 9F, FontStyle.Regular, Ink);
            cooldownCaption.AutoSize = true;
            cooldownCaption.Margin = new Padding(0, 5, 10, 0);
            options.Controls.Add(cooldownCaption);
            _cooldown.Name = "CooldownSeconds";
            _cooldown.AccessibleName = "同一联系人提醒间隔秒数";
            _cooldown.Minimum = 0;
            _cooldown.Maximum = 30;
            _cooldown.Width = 58;
            _cooldown.Margin = new Padding(0, 0, 6, 0);
            options.Controls.Add(_cooldown);
            Label seconds = NewLabel("秒", 9F, FontStyle.Regular, Muted);
            seconds.AutoSize = true;
            seconds.Margin = new Padding(0, 5, 18, 0);
            options.Controls.Add(seconds);
            Label explanation = NewLabel("同一联系人在间隔内只提醒一次", 9F, FontStyle.Regular, Muted);
            explanation.AutoSize = true;
            explanation.Margin = new Padding(0, 5, 0, 0);
            options.Controls.Add(explanation);
            root.Controls.Add(options, 0, 2);

            _compatibility.Name = "CompatibilityMode";
            _compatibility.Text = "支持微信最小化（兼容模式）";
            _compatibility.AccessibleName = "启用微信最小化提醒兼容模式";
            _compatibility.AutoSize = true;
            _compatibility.Dock = DockStyle.Fill;
            _compatibility.Margin = new Padding(0, 0, 0, 10);
            _tips.SetToolTip(_compatibility, "为已验证的微信版本启用辅助访问，退出时尝试还原。未知版本不启用。");
            root.Controls.Add(_compatibility, 0, 3);

            TableLayoutPanel footer = new TableLayoutPanel();
            footer.Dock = DockStyle.Fill;
            footer.Margin = Padding.Empty;
            footer.Padding = new Padding(0, 12, 0, 0);
            footer.ColumnCount = 2;
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190));
            footer.RowCount = 3;
            footer.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
            footer.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            footer.RowStyles.Add(new RowStyle(SizeType.Absolute, 20));
            footer.Paint += delegate(object sender, PaintEventArgs e)
            {
                using (Pen pen = new Pen(Line)) e.Graphics.DrawLine(pen, 0, 0, footer.Width, 0);
            };
            _status.Name = "MonitorStatus";
            _status.AccessibleName = "微信连接状态";
            _status.Dock = DockStyle.Fill;
            _status.Margin = Padding.Empty;
            _status.Font = new Font(Font, FontStyle.Bold);
            _status.AutoEllipsis = true;
            footer.Controls.Add(_status, 0, 0);
            _checked.Dock = DockStyle.Fill;
            _checked.Margin = Padding.Empty;
            _checked.ForeColor = Muted;
            _checked.TextAlign = ContentAlignment.TopRight;
            footer.Controls.Add(_checked, 1, 0);
            _detail.Name = "MonitorDetail";
            _detail.AccessibleName = "连接诊断";
            _detail.Dock = DockStyle.Fill;
            _detail.Margin = new Padding(0, 2, 0, 0);
            _detail.ForeColor = Muted;
            _detail.AutoEllipsis = true;
            footer.Controls.Add(_detail, 0, 1);
            footer.SetColumnSpan(_detail, 2);
            Label trayHelp = NewLabel("设置自动保存 · 关闭窗口后留在系统托盘", 8F, FontStyle.Regular, Muted);
            trayHelp.Dock = DockStyle.Fill;
            trayHelp.Margin = Padding.Empty;
            footer.Controls.Add(trayHelp, 0, 2);
            footer.SetColumnSpan(trayHelp, 2);
            root.Controls.Add(footer, 0, 4);
        }

        private Control BuildContactCard()
        {
            TableLayoutPanel card = NewCard();
            card.Margin = new Padding(0, 0, 8, 0);
            card.Padding = new Padding(14, 14, 14, 12);
            card.RowCount = 3;
            card.RowStyles.Add(new RowStyle(SizeType.Absolute, 39));
            card.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            card.RowStyles.Add(new RowStyle(SizeType.Absolute, 35));

            TableLayoutPanel top = new TableLayoutPanel();
            top.Dock = DockStyle.Fill;
            top.Margin = Padding.Empty;
            top.ColumnCount = 2;
            top.RowCount = 1;
            top.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            top.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 91));
            _count.Dock = DockStyle.Fill;
            _count.Font = new Font(Font, FontStyle.Bold);
            _count.Margin = new Padding(0, 5, 0, 0);
            top.Controls.Add(_count, 0, 0);
            Button add = new Button();
            StyleButton(add, "+ 添加", false);
            add.Name = "AddContactButton";
            add.AccessibleName = "添加重点联系人";
            add.Dock = DockStyle.Top;
            add.Height = 29;
            add.Margin = Padding.Empty;
            add.Click += delegate { AddContact(); };
            _tips.SetToolTip(add, "添加重点联系人 (Ctrl+N)");
            top.Controls.Add(add, 1, 0);
            card.Controls.Add(top, 0, 0);

            Panel listHost = new Panel();
            listHost.Dock = DockStyle.Fill;
            listHost.Margin = Padding.Empty;
            _contacts.Name = "ContactRules";
            _contacts.AccessibleName = "重点联系人列表，勾选启用提醒";
            _contacts.Dock = DockStyle.Fill;
            _contacts.View = View.Details;
            _contacts.CheckBoxes = true;
            _contacts.FullRowSelect = true;
            _contacts.MultiSelect = false;
            _contacts.HideSelection = false;
            _contacts.BorderStyle = BorderStyle.None;
            _contacts.BackColor = Color.White;
            _contacts.ForeColor = Ink;
            _contacts.HeaderStyle = ColumnHeaderStyle.Nonclickable;
            _contacts.Columns.Add("联系人", 164);
            _contacts.Columns.Add("提示音", 125);
            _contacts.Resize += delegate { ResizeContactColumns(); };
            listHost.Controls.Add(_contacts);
            _empty.Text = "还没有重点联系人\r\n\r\n点击“+ 添加”，开始设置专属提醒。";
            _empty.Name = "EmptyContactState";
            _empty.Dock = DockStyle.Fill;
            _empty.TextAlign = ContentAlignment.MiddleCenter;
            _empty.ForeColor = Muted;
            _empty.Padding = new Padding(6);
            listHost.Controls.Add(_empty);
            card.Controls.Add(listHost, 0, 1);

            TableLayoutPanel bottom = new TableLayoutPanel();
            bottom.Dock = DockStyle.Fill;
            bottom.Margin = new Padding(0, 7, 0, 0);
            bottom.ColumnCount = 2;
            bottom.RowCount = 1;
            bottom.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 60));
            Label help = NewLabel("勾选联系人即可启用提醒", 8F, FontStyle.Regular, Muted);
            help.Dock = DockStyle.Fill;
            help.Margin = new Padding(0, 6, 0, 0);
            bottom.Controls.Add(help, 0, 0);
            StyleButton(_delete, "移除", false);
            _delete.Name = "RemoveContactButton";
            _delete.AccessibleName = "移除所选联系人";
            _delete.Dock = DockStyle.Fill;
            _delete.Margin = Padding.Empty;
            _tips.SetToolTip(_delete, "移除所选联系人 (Ctrl+Delete)");
            bottom.Controls.Add(_delete, 1, 0);
            card.Controls.Add(bottom, 0, 2);
            return card;
        }

        private Control BuildEditorCard()
        {
            TableLayoutPanel card = NewCard();
            card.Margin = new Padding(8, 0, 0, 0);
            card.Padding = new Padding(20, 16, 20, 16);
            card.RowCount = 8;
            card.RowStyles.Add(new RowStyle(SizeType.Absolute, 23));
            card.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
            card.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            card.RowStyles.Add(new RowStyle(SizeType.Absolute, 25));
            card.RowStyles.Add(new RowStyle(SizeType.Absolute, 35));
            card.RowStyles.Add(new RowStyle(SizeType.Absolute, 41));
            card.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            card.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
            Label eyebrow = NewLabel("专属提示音", 9F, FontStyle.Regular, Muted);
            eyebrow.Dock = DockStyle.Fill;
            eyebrow.Margin = Padding.Empty;
            card.Controls.Add(eyebrow, 0, 0);
            _selectedName.Name = "SelectedContactName";
            _selectedName.AccessibleName = "当前所选联系人";
            _selectedName.Font = new Font("Microsoft YaHei UI", 16F, FontStyle.Bold);
            _selectedName.AutoEllipsis = true;
            _selectedName.Dock = DockStyle.Fill;
            _selectedName.Margin = Padding.Empty;
            card.Controls.Add(_selectedName, 0, 1);
            _ruleEnabled.Text = "接收这位联系人的提醒";
            _ruleEnabled.Name = "ContactEnabled";
            _ruleEnabled.AccessibleName = "启用所选联系人的提醒";
            _ruleEnabled.AutoSize = true;
            _ruleEnabled.Dock = DockStyle.Fill;
            _ruleEnabled.Margin = Padding.Empty;
            card.Controls.Add(_ruleEnabled, 0, 2);
            Label soundLabel = NewLabel("提示音", 9F, FontStyle.Regular, Ink);
            soundLabel.Dock = DockStyle.Fill;
            soundLabel.Margin = new Padding(0, 6, 0, 0);
            card.Controls.Add(soundLabel, 0, 3);
            _sounds.Name = "SoundSelection";
            _sounds.AccessibleName = "选择所选联系人的提示音";
            _sounds.DropDownStyle = ComboBoxStyle.DropDownList;
            _sounds.Dock = DockStyle.Top;
            _sounds.IntegralHeight = false;
            _sounds.DropDownHeight = 160;
            _sounds.Margin = new Padding(0, 2, 0, 0);
            card.Controls.Add(_sounds, 0, 4);

            TableLayoutPanel actions = new TableLayoutPanel();
            actions.Dock = DockStyle.Fill;
            actions.Margin = Padding.Empty;
            actions.ColumnCount = 2;
            actions.RowCount = 1;
            actions.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 62));
            actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 38));
            StyleButton(_import, "导入音频…", false);
            _import.Name = "ImportSoundButton";
            _import.AccessibleName = "导入自定义音频";
            _import.Dock = DockStyle.Top;
            _import.Height = 32;
            _import.Margin = new Padding(0, 2, 7, 0);
            actions.Controls.Add(_import, 0, 0);
            StyleButton(_preview, "试听", false);
            _preview.Name = "PreviewSoundButton";
            _preview.AccessibleName = "试听当前提示音";
            _preview.Dock = DockStyle.Top;
            _preview.Height = 32;
            _preview.Margin = new Padding(0, 2, 0, 0);
            _tips.SetToolTip(_preview, "试听当前提示音 (F5)");
            actions.Controls.Add(_preview, 1, 0);
            card.Controls.Add(actions, 0, 5);
            _editorHelp.Text = "支持 WAV、MP3、M4A、WMA，最大 20 MB\r\n建议使用简短、容易辨认的声音。";
            _editorHelp.ForeColor = Muted;
            _editorHelp.Font = new Font("Microsoft YaHei UI", 8F);
            _editorHelp.Dock = DockStyle.Fill;
            _editorHelp.Margin = new Padding(0, 4, 0, 0);
            _editorHelp.AutoEllipsis = true;
            card.Controls.Add(_editorHelp, 0, 6);
            _feedback.Name = "ActivityStatus";
            _feedback.AccessibleName = "最近提醒与操作结果";
            _feedback.Text = "尚未收到重点联系人消息";
            _feedback.ForeColor = Muted;
            _feedback.BackColor = Canvas;
            _feedback.Dock = DockStyle.Fill;
            _feedback.Margin = new Padding(0, 6, 0, 0);
            _feedback.Padding = new Padding(9, 6, 9, 0);
            _feedback.AutoEllipsis = true;
            card.Controls.Add(_feedback, 0, 7);
            return card;
        }

        private void BuildTray()
        {
            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Font = Font;
            ToolStripMenuItem show = new ToolStripMenuItem("打开提醒助手");
            show.Click += delegate { ShowFromTray(); };
            menu.Items.Add(show);
            _trayPause.Click += delegate { TogglePause(); };
            menu.Items.Add(_trayPause);
            menu.Items.Add(new ToolStripSeparator());
            ToolStripMenuItem exit = new ToolStripMenuItem("退出");
            exit.Click += delegate { ExitApplication(); };
            menu.Items.Add(exit);
            _tray.Icon = _appIcon;
            _tray.ContextMenuStrip = menu;
            _tray.Text = "微信重点消息提醒";
            _tray.MouseClick += delegate(object sender, MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Left) ShowFromTray();
            };
            _tray.Visible = true;
        }

        private void BindEvents()
        {
            Shown += delegate
            {
                if (_started) return;
                _started = true;
                try
                {
                    _monitor.Configure(_settings.Clone());
                    _monitor.Start();
                }
                catch (Exception ex) { ShowFeedback("监听启动失败：" + ex.Message, true); }
            };
            FormClosing += delegate(object sender, FormClosingEventArgs e)
            {
                if (!_exitRequested && e.CloseReason == CloseReason.UserClosing)
                {
                    e.Cancel = true;
                    Hide();
                }
            };
            Resize += delegate
            {
                if (WindowState == FormWindowState.Minimized) Hide();
            };
            KeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (e.Control && e.KeyCode == Keys.N) { AddContact(); e.SuppressKeyPress = true; }
                else if (e.Control && e.KeyCode == Keys.Delete) { RemoveContact(); e.SuppressKeyPress = true; }
                else if (e.KeyCode == Keys.F5) { PreviewSound(); e.SuppressKeyPress = true; }
            };
            _pause.Click += delegate { TogglePause(); };
            _delete.Click += delegate { RemoveContact(); };
            _preview.Click += delegate { PreviewSound(); };
            _import.Click += delegate { ImportSound(); };
            _contacts.SelectedIndexChanged += delegate
            {
                if (_refreshing) return;
                _selectedId = _contacts.SelectedItems.Count == 0 ? null : (string)_contacts.SelectedItems[0].Tag;
                RefreshEditor();
            };
            _contacts.ItemChecked += delegate(object sender, ItemCheckedEventArgs e)
            {
                if (_refreshing) return;
                string id = e.Item.Tag as string;
                ContactRule original = FindRule(_settings, id);
                if (original == null || original.Enabled == e.Item.Checked) return;
                AppSettings next = _settings.Clone();
                FindRule(next, id).Enabled = e.Item.Checked;
                if (!CommitSettings(next))
                {
                    _refreshing = true;
                    e.Item.Checked = original.Enabled;
                    _refreshing = false;
                }
                RefreshEditor();
            };
            _ruleEnabled.CheckedChanged += delegate
            {
                if (_refreshing) return;
                ContactRule current = FindRule(_settings, _selectedId);
                if (current == null || current.Enabled == _ruleEnabled.Checked) return;
                AppSettings next = _settings.Clone();
                FindRule(next, _selectedId).Enabled = _ruleEnabled.Checked;
                CommitSettings(next);
                RefreshContacts();
            };
            _sounds.SelectedIndexChanged += delegate
            {
                if (_refreshing) return;
                ContactRule current = FindRule(_settings, _selectedId);
                SoundOption option = _sounds.SelectedItem as SoundOption;
                if (current == null || option == null || current.Sound == option.Key) return;
                AppSettings next = _settings.Clone();
                FindRule(next, _selectedId).Sound = option.Key;
                if (CommitSettings(next)) ShowFeedback("已保存专属提示音", false);
                RefreshContacts();
            };
            _cooldown.ValueChanged += delegate
            {
                if (_refreshing || _settings.CooldownSeconds == (int)_cooldown.Value) return;
                AppSettings next = _settings.Clone();
                next.CooldownSeconds = (int)_cooldown.Value;
                CommitSettings(next);
                RefreshGlobalState();
            };
            _compatibility.CheckedChanged += delegate
            {
                if (_refreshing || _settings.CompatibilityMode == _compatibility.Checked) return;
                AppSettings next = _settings.Clone();
                next.CompatibilityMode = _compatibility.Checked;
                if (CommitSettings(next))
                    ShowFeedback(_settings.CompatibilityMode ? "兼容模式已保存，正在检查微信支持情况" : "兼容模式已关闭", false);
                RefreshGlobalState();
            };
            _monitor.SnapshotChanged += HandleSnapshot;
            _monitor.Alert += HandleAlert;
            _monitor.Fault += HandleFault;
            _sound.PlaybackFailed += HandlePlaybackFailed;
        }

        private bool CommitSettings(AppSettings next)
        {
            try { _store.Save(next); }
            catch (Exception ex)
            {
                ShowFeedback("设置未保存：" + ex.Message, true);
                MessageBox.Show(this, "设置未能保存，原有设置已保留。\r\n\r\n" + ex.Message,
                    "无法保存设置", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }
            _settings = next;
            try { _monitor.Configure(_settings.Clone()); }
            catch (Exception ex) { ShowFeedback("设置已保存，监听更新失败：" + ex.Message, true); }
            RefreshGlobalState();
            return true;
        }

        private void AddContact()
        {
            List<string> recent = _snapshot != null && _snapshot.Contacts != null
                ? new List<string>(_snapshot.Contacts) : new List<string>();
            using (AddContactDialog dialog = new AddContactDialog(recent, _settings.Contacts))
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                AppSettings next = _settings.Clone();
                ContactRule rule = new ContactRule();
                rule.Id = Guid.NewGuid().ToString("N");
                rule.Name = dialog.ContactName;
                rule.Enabled = true;
                rule.Sound = SoundService.BuiltInSounds.Length > 0 ? SoundService.BuiltInSounds[0] : String.Empty;
                next.Contacts.Add(rule);
                if (!CommitSettings(next)) return;
                _selectedId = rule.Id;
                RefreshContacts();
                ShowFeedback("联系人已添加，设置已保存", false);
            }
        }

        private void RemoveContact()
        {
            ContactRule current = FindRule(_settings, _selectedId);
            if (current == null) return;
            AppSettings next = _settings.Clone();
            next.Contacts.RemoveAll(delegate(ContactRule rule) { return rule.Id == current.Id; });
            if (!CommitSettings(next)) return;
            _selectedId = null;
            RefreshContacts();
            ShowFeedback("已移除联系人规则", false);
        }

        private void ImportSound()
        {
            ContactRule current = FindRule(_settings, _selectedId);
            if (current == null) return;
            using (OpenFileDialog picker = new OpenFileDialog())
            {
                picker.Title = "选择专属提示音";
                picker.Filter = "音频文件 (*.wav;*.mp3;*.m4a;*.wma)|*.wav;*.mp3;*.m4a;*.wma";
                picker.CheckFileExists = true;
                picker.Multiselect = false;
                if (picker.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    string imported = _store.ImportSound(picker.FileName);
                    AppSettings next = _settings.Clone();
                    FindRule(next, current.Id).Sound = imported;
                    if (!CommitSettings(next)) return;
                    RefreshContacts();
                    ShowFeedback("音频已导入，可点击“试听”", false);
                }
                catch (Exception ex)
                {
                    ShowFeedback("音频未导入：" + ex.Message, true);
                    MessageBox.Show(this, "无法导入此音频，原提示音已保留。\r\n\r\n" + ex.Message,
                        "导入失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
        }

        private void PreviewSound()
        {
            ContactRule current = FindRule(_settings, _selectedId);
            if (current == null) return;
            try
            {
                _sound.Play(current.Sound);
                ShowFeedback("正在试听 · " + SoundService.DisplayName(current.Sound), false);
            }
            catch (Exception ex) { ShowFeedback("播放失败：" + ex.Message, true); }
        }

        private void TogglePause()
        {
            AppSettings next = _settings.Clone();
            next.Enabled = !next.Enabled;
            if (!CommitSettings(next)) return;
            if (!_settings.Enabled) _sound.Stop();
            ShowFeedback(_settings.Enabled ? "提醒已恢复" : "提醒已暂停，仍可手动试听", false);
        }

        private void RefreshContacts()
        {
            _refreshing = true;
            _contacts.BeginUpdate();
            try
            {
                _contacts.Items.Clear();
                if (FindRule(_settings, _selectedId) == null)
                    _selectedId = _settings.Contacts.Count > 0 ? _settings.Contacts[0].Id : null;
                foreach (ContactRule rule in _settings.Contacts)
                {
                    ListViewItem item = new ListViewItem(rule.Name);
                    item.Tag = rule.Id;
                    item.Checked = rule.Enabled;
                    item.SubItems.Add(SoundService.DisplayName(rule.Sound));
                    item.ToolTipText = rule.Name + " · " + SoundService.DisplayName(rule.Sound);
                    _contacts.Items.Add(item);
                    if (rule.Id == _selectedId) { item.Selected = true; item.Focused = true; }
                }
                _count.Text = "重点联系人  " + _settings.Contacts.Count.ToString();
                _empty.Visible = _settings.Contacts.Count == 0;
                _contacts.Visible = _settings.Contacts.Count > 0;
                _contacts.ShowItemToolTips = true;
                ResizeContactColumns();
            }
            finally
            {
                _contacts.EndUpdate();
                _refreshing = false;
            }
            RefreshEditor();
        }

        private void RefreshEditor()
        {
            _refreshing = true;
            try
            {
                ContactRule current = FindRule(_settings, _selectedId);
                bool selected = current != null;
                _selectedName.Text = selected ? current.Name : "选择一位联系人";
                _tips.SetToolTip(_selectedName, selected ? current.Name : "添加或选择重点联系人后设置提示音");
                _ruleEnabled.Enabled = selected;
                _ruleEnabled.Checked = selected && current.Enabled;
                _delete.Enabled = selected;
                _sounds.Enabled = selected;
                _preview.Enabled = selected;
                _import.Enabled = selected;
                _sounds.Items.Clear();
                foreach (string key in SoundService.BuiltInSounds)
                    _sounds.Items.Add(new SoundOption(key, SoundService.DisplayName(key)));
                int chosen = -1;
                if (selected)
                {
                    for (int i = 0; i < _sounds.Items.Count; i++)
                        if (((SoundOption)_sounds.Items[i]).Key == current.Sound) chosen = i;
                    if (chosen < 0 && !String.IsNullOrEmpty(current.Sound))
                    {
                        chosen = _sounds.Items.Add(new SoundOption(current.Sound, SoundService.DisplayName(current.Sound)));
                    }
                }
                _sounds.SelectedIndex = chosen;
                _tips.SetToolTip(_sounds, selected ? SoundService.DisplayName(current.Sound) : String.Empty);
            }
            finally { _refreshing = false; }
        }

        private void RefreshGlobalState()
        {
            bool previous = _refreshing;
            _refreshing = true;
            _cooldown.Value = Math.Max(0, Math.Min(30, _settings.CooldownSeconds));
            _compatibility.Checked = _settings.CompatibilityMode;
            _pause.Text = _settings.Enabled ? "暂停提醒" : "恢复提醒";
            _pause.BackColor = _settings.Enabled ? Green : Color.White;
            _pause.ForeColor = _settings.Enabled ? Color.White : Green;
            _trayPause.Text = _settings.Enabled ? "暂停全部提醒" : "恢复全部提醒";
            _refreshing = previous;
            RefreshConnection();
        }

        private void RefreshConnection()
        {
            string status = _snapshot == null || String.IsNullOrWhiteSpace(_snapshot.Status)
                ? "等待连接微信" : _snapshot.Status;
            if (_settings.CompatibilityMode && _snapshot != null && _snapshot.CompatibilityActive && _snapshot.Connected
                && status.IndexOf("最小化", StringComparison.Ordinal) < 0)
                status += " · 支持最小化";
            _status.Text = (_settings.Enabled ? "● " : "● 提醒已暂停 · ") + status;
            _status.ForeColor = _snapshot != null && _snapshot.Connected && _settings.Enabled ? Green : Muted;
            _tray.Text = "微信重点消息提醒 · " + (!_settings.Enabled ? "提醒已暂停" :
                _snapshot != null && _snapshot.Connected ? "正在监听" : "等待可用监听");
            _detail.Text = _snapshot == null || String.IsNullOrWhiteSpace(_snapshot.Detail)
                ? "请登录电脑版微信，保持微信运行。" : _snapshot.Detail;
            _tips.SetToolTip(_status, _status.Text);
            _tips.SetToolTip(_detail, _detail.Text);
            _checked.Text = _snapshot == null || _snapshot.CheckedAt == DateTime.MinValue
                ? String.Empty : _snapshot.SessionCount.ToString() + " 个会话 · " + _snapshot.CheckedAt.ToLocalTime().ToString("HH:mm:ss");
        }

        private void ResizeContactColumns()
        {
            int available = Math.Max(180, _contacts.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - 3);
            _contacts.Columns[0].Width = (int)(available * 0.57);
            _contacts.Columns[1].Width = available - _contacts.Columns[0].Width;
        }

        private void HandleSnapshot(MonitorSnapshot snapshot)
        {
            OnUi(delegate { _snapshot = snapshot; RefreshConnection(); });
        }

        private void HandleAlert(string name)
        {
            OnUi(delegate
            {
                if (!_settings.Enabled) return;
                ContactRule matched = null;
                foreach (ContactRule rule in _settings.Contacts)
                {
                    if (rule.Enabled && String.Equals(rule.Name.Trim(), (name ?? String.Empty).Trim(), StringComparison.OrdinalIgnoreCase))
                    { matched = rule; break; }
                }
                if (matched == null) return;
                try
                {
                    _sound.Play(matched.Sound);
                    ShowFeedback(DateTime.Now.ToString("HH:mm:ss") + "  " + matched.Name + " 有新消息", false);
                }
                catch (Exception ex) { ShowFeedback("播放失败：" + ex.Message, true); }
            });
        }

        private void HandleFault(string message)
        {
            OnUi(delegate {
                _snapshot=new MonitorSnapshot {Status="监听暂不可用",Detail=message,CheckedAt=DateTime.Now};
                RefreshConnection();
                ShowFeedback("监听提示：" + message, true);
            });
        }

        private void HandlePlaybackFailed(object sender, SoundPlaybackErrorEventArgs args)
        {
            OnUi(delegate { ShowFeedback("播放失败：" + args.Message, true); });
        }

        private void OnUi(Action action)
        {
            if (_disposed || IsDisposed || Disposing || !IsHandleCreated) return;
            if (!InvokeRequired) { action(); return; }
            try
            {
                BeginInvoke(new Action(delegate { if (!_disposed && !IsDisposed) action(); }));
            }
            catch (InvalidOperationException) { }
        }

        private void ShowFeedback(string message, bool error)
        {
            _feedback.Text = message;
            _feedback.ForeColor = error ? Color.FromArgb(154, 73, 42) : Muted;
            _tips.SetToolTip(_feedback, message);
        }

        public void ShowFromTray()
        {
            OnUi(delegate
            {
                Show();
                if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
                Activate();
                BringToFront();
            });
        }

        private void ExitApplication()
        {
            _exitRequested = true;
            Close();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !_disposed)
            {
                _disposed = true;
                _monitor.SnapshotChanged -= HandleSnapshot;
                _monitor.Alert -= HandleAlert;
                _monitor.Fault -= HandleFault;
                _sound.PlaybackFailed -= HandlePlaybackFailed;
                _tray.Visible = false;
                ContextMenuStrip menu = _tray.ContextMenuStrip;
                _tray.Dispose();
                if (menu != null) menu.Dispose();
                _monitor.Dispose();
                if(!String.IsNullOrEmpty(_monitor.LastRestoreError))
                    MessageBox.Show(_monitor.LastRestoreError,"微信兼容状态",MessageBoxButtons.OK,MessageBoxIcon.Warning);
                _sound.Stop();
                _tips.Dispose();
                _appIcon.Dispose();
            }
            base.Dispose(disposing);
        }

        private static ContactRule FindRule(AppSettings settings, string id)
        {
            if (id == null) return null;
            foreach (ContactRule rule in settings.Contacts) if (rule.Id == id) return rule;
            return null;
        }

        private static Label NewLabel(string text, float size, FontStyle style, Color color)
        {
            Label label = new Label();
            label.Text = text;
            label.Font = new Font("Microsoft YaHei UI", size, style);
            label.ForeColor = color;
            return label;
        }

        private static TableLayoutPanel NewCard()
        {
            TableLayoutPanel card = new TableLayoutPanel();
            card.Dock = DockStyle.Fill;
            card.BackColor = Color.White;
            card.ColumnCount = 1;
            card.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            card.Paint += delegate(object sender, PaintEventArgs e)
            {
                using (Pen pen = new Pen(Line)) e.Graphics.DrawRectangle(pen, 0, 0, card.Width - 1, card.Height - 1);
            };
            return card;
        }

        private static void StyleButton(Button button, string text, bool primary)
        {
            button.Text = text;
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderSize = 1;
            button.FlatAppearance.BorderColor = primary ? Green : Line;
            button.FlatAppearance.MouseOverBackColor = primary ? Color.FromArgb(28, 89, 62) : Color.FromArgb(237, 243, 235);
            button.FlatAppearance.MouseDownBackColor = primary ? Color.FromArgb(21, 72, 50) : Color.FromArgb(224, 234, 221);
            button.UseVisualStyleBackColor = false;
            button.BackColor = primary ? Green : Color.White;
            button.ForeColor = primary ? Color.White : Ink;
            button.Cursor = Cursors.Hand;
            button.TabStop = true;
        }

        private static Icon MakeIcon()
        {
            using (Bitmap bitmap = new Bitmap(32, 32))
            {
                using (Graphics graphics = Graphics.FromImage(bitmap))
                {
                    graphics.SmoothingMode = SmoothingMode.AntiAlias;
                    using (Brush fill = new SolidBrush(Green)) graphics.FillEllipse(fill, 1, 1, 30, 30);
                    DrawBell(graphics, new RectangleF(7, 6, 18, 21));
                }
                IntPtr handle = bitmap.GetHicon();
                try { using (Icon borrowed = Icon.FromHandle(handle)) return (Icon)borrowed.Clone(); }
                finally { DestroyIcon(handle); }
            }
        }

        private static void DrawBell(Graphics graphics, RectangleF bounds)
        {
            GraphicsState state = graphics.Save();
            graphics.TranslateTransform(bounds.X, bounds.Y);
            graphics.ScaleTransform(bounds.Width / 18F, bounds.Height / 21F);
            using (Pen pen = new Pen(Color.White, 1.7F))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                using (GraphicsPath path = new GraphicsPath())
                {
                    path.AddBezier(3, 14, 5, 11, 2, 4, 9, 4);
                    path.AddBezier(9, 4, 16, 4, 13, 11, 15, 14);
                    path.AddLine(15, 14, 16, 16);
                    path.AddLine(16, 16, 2, 16);
                    path.AddLine(2, 16, 3, 14);
                    graphics.DrawPath(pen, path);
                }
                graphics.DrawLine(pen, 9, 2, 9, 3);
                graphics.DrawArc(pen, 6.5F, 15, 5, 5, 20, 140);
            }
            graphics.Restore(state);
        }

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DestroyIcon(IntPtr handle);

        private sealed class SoundOption
        {
            public readonly string Key;
            private readonly string _text;
            public SoundOption(string key, string text) { Key = key; _text = text; }
            public override string ToString() { return _text; }
        }

        private sealed class BufferedListView : ListView
        {
            public BufferedListView() { DoubleBuffered = true; }
        }

        private sealed class BellMark : Control
        {
            public BellMark() { SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true); }
            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e);
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                using (Brush brush = new SolidBrush(Green)) e.Graphics.FillEllipse(brush, 0, 0, Width - 1, Height - 1);
                DrawBell(e.Graphics, new RectangleF(Width * .25F, Height * .18F, Width * .5F, Height * .61F));
            }
        }

        private sealed class AddContactDialog : Form
        {
            private readonly ComboBox _name = new ComboBox();
            private readonly Label _validation = new Label();
            private readonly HashSet<string> _existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public string ContactName { get; private set; }

            public AddContactDialog(List<string> recent, List<ContactRule> existing)
            {
                Text = "添加重点联系人";
                Name = "AddContactDialog";
                AccessibleName = "添加重点联系人";
                AutoScaleDimensions = new SizeF(96F, 96F);
                AutoScaleMode = AutoScaleMode.Dpi;
                Font = new Font("Microsoft YaHei UI", 9F);
                ForeColor = Ink;
                BackColor = Canvas;
                FormBorderStyle = FormBorderStyle.FixedDialog;
                MaximizeBox = false;
                MinimizeBox = false;
                ShowInTaskbar = false;
                StartPosition = FormStartPosition.CenterParent;
                ClientSize = new Size(450, 270);
                foreach (ContactRule rule in existing) _existing.Add(rule.Name.Trim());

                TableLayoutPanel layout = new TableLayoutPanel();
                layout.Dock = DockStyle.Fill;
                layout.Padding = new Padding(24, 22, 24, 20);
                layout.ColumnCount = 1;
                layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                layout.RowCount = 6;
                layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
                layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
                layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
                layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 53));
                layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
                layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
                Controls.Add(layout);
                Label title = NewLabel("选择重要的人", 15F, FontStyle.Bold, Ink);
                title.Dock = DockStyle.Fill;
                title.Margin = Padding.Empty;
                layout.Controls.Add(title, 0, 0);
                Label label = NewLabel("微信联系人备注名 / 会话名称", 9F, FontStyle.Regular, Ink);
                label.Dock = DockStyle.Fill;
                label.Margin = new Padding(0, 7, 0, 0);
                layout.Controls.Add(label, 0, 1);
                _name.Name = "ContactNameInput";
                _name.AccessibleName = "从最近会话选择或输入完整联系人名称";
                _name.Dock = DockStyle.Top;
                _name.Margin = Padding.Empty;
                _name.DropDownStyle = ComboBoxStyle.DropDown;
                _name.MaxLength = 256;
                _name.AutoCompleteMode = AutoCompleteMode.SuggestAppend;
                _name.AutoCompleteSource = AutoCompleteSource.ListItems;
                HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (string contact in recent)
                {
                    if (!String.IsNullOrWhiteSpace(contact) && seen.Add(contact.Trim())) _name.Items.Add(contact.Trim());
                }
                layout.Controls.Add(_name, 0, 2);
                string helpText = _name.Items.Count > 0 ? "可从已识别的最近会话选择，也可直接输入。" : "暂未识别到最近会话，可直接输入完整名称。";
                Label help = NewLabel(helpText + "\r\n名称须与微信中一致；同名会话无法区分。", 8.5F, FontStyle.Regular, Muted);
                help.Dock = DockStyle.Fill;
                help.Margin = new Padding(0, 4, 0, 0);
                layout.Controls.Add(help, 0, 3);
                _validation.Name = "ContactNameValidation";
                _validation.Dock = DockStyle.Fill;
                _validation.Margin = Padding.Empty;
                _validation.ForeColor = Color.FromArgb(154, 73, 42);
                layout.Controls.Add(_validation, 0, 4);
                FlowLayoutPanel buttons = new FlowLayoutPanel();
                buttons.Dock = DockStyle.Fill;
                buttons.FlowDirection = FlowDirection.RightToLeft;
                buttons.WrapContents = false;
                buttons.Margin = Padding.Empty;
                Button add = new Button();
                StyleButton(add, "添加联系人", true);
                add.Name = "ConfirmAddContactButton";
                add.AccessibleName = "确认添加联系人";
                add.Size = new Size(112, 32);
                add.Margin = Padding.Empty;
                add.Click += delegate { Submit(); };
                buttons.Controls.Add(add);
                Button cancel = new Button();
                StyleButton(cancel, "取消", false);
                cancel.Size = new Size(78, 32);
                cancel.Margin = new Padding(0, 0, 10, 0);
                cancel.DialogResult = DialogResult.Cancel;
                buttons.Controls.Add(cancel);
                AcceptButton = add;
                CancelButton = cancel;
                layout.Controls.Add(buttons, 0, 5);
                Shown += delegate { _name.Focus(); };
                _name.TextChanged += delegate { _validation.Text = String.Empty; };
            }

            private void Submit()
            {
                string name = _name.Text.Trim();
                if (String.IsNullOrWhiteSpace(name)) { _validation.Text = "请输入联系人在微信中的完整名称。"; _name.Focus(); return; }
                if (_existing.Contains(name)) { _validation.Text = "这位联系人已经添加，无需重复设置。"; _name.Focus(); return; }
                ContactName = name;
                DialogResult = DialogResult.OK;
                Close();
            }
        }
    }
}
