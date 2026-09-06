using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace WeChatChime.Tests
{
    internal static class UiTestsMain
    {
        private static int _checks;
        private const BindingFlags PrivateInstance = BindingFlags.NonPublic | BindingFlags.Instance;

        [STAThread]
        private static int Main(string[] args)
        {
            if (args.Length != 1 || !Path.IsPathRooted(args[0]))
            {
                Console.Error.WriteLine("Usage: UiTests.exe <absolute temporary root>");
                return 2;
            }
            string root = Path.Combine(Path.GetFullPath(args[0]), "ui-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            try
            {
                CheckNativeFlows(root);
                CheckScaledLayout(root, 1.25F);
                CheckScaledLayout(root, 1.5F);
                Console.WriteLine("PASS " + _checks + " UI checks; fixtures: " + root);
                Console.WriteLine("Scale checks simulate geometry/font scaling; actual Windows DPI/device acceptance remains separate.");
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("FAIL: " + ex);
                Console.Error.WriteLine("Fixtures retained: " + root);
                return 1;
            }
        }

        private static void CheckNativeFlows(string root)
        {
            string folder = Path.Combine(root, "native-flows");
            SettingsStore store = new SettingsStore(folder);
            AppSettings original = store.Load();
            using (SoundService sound = new SoundService())
            using (WeChatMonitor monitor = new WeChatMonitor())
            using (MainForm form = MakeForm(original, store, sound, monitor))
            {
                ListView list = Find<ListView>(form, "ContactRules");
                Check(list.Items.Count == 0, "fresh UI has no sample contacts");
                Check(!Find<Button>(form, "PreviewSoundButton").Enabled, "empty UI disables playback");
                Check(!Find<Button>(form, "RemoveContactButton").Enabled, "empty UI disables deletion");
                CheckBox compatibility = Find<CheckBox>(form, "CompatibilityMode");
                Check(!compatibility.Checked, "fresh UI does not enable compatibility implicitly");
                compatibility.Checked = true;
                Check(store.Load().CompatibilityMode, "compatibility switch persists immediately");
                Check(!original.CompatibilityMode, "compatibility switch does not mutate supplied settings");
                Check(!Find<Label>(form, "MonitorStatus").Text.Contains("支持最小化"), "switch alone does not claim minimized monitoring works");
                Invoke(form, "HandleSnapshot", new MonitorSnapshot { Connected = true, CompatibilityActive = false, Status = "已连接微信" });
                Check(!Find<Label>(form, "MonitorStatus").Text.Contains("支持最小化"), "ordinary readable connection does not prove compatibility activation");
                Invoke(form, "HandleSnapshot", new MonitorSnapshot { Connected = false, CompatibilityActive = true, Status = "正在检测微信" });
                Check(!Find<Label>(form, "MonitorStatus").Text.Contains("支持最小化"), "activation alone does not prove readable connection");
                Invoke(form, "HandleSnapshot", new MonitorSnapshot { Connected = true, CompatibilityActive = true, Minimized = true, Status = "已连接微信" });
                Check(Find<Label>(form, "MonitorStatus").Text.Contains("支持最小化"), "active readable compatibility snapshot reports minimized support");
                compatibility.Checked = false;
                Check(!store.Load().CompatibilityMode && !Find<Label>(form, "MonitorStatus").Text.Contains("支持最小化"), "disabling mode saves and removes compatibility success status");

                OpenAddDialog(form, delegate(Form dialog)
                {
                    Find<ComboBox>(dialog, "ContactNameInput").Text = "  测试联系人  ";
                    Find<Button>(dialog, "ConfirmAddContactButton").PerformClick();
                });
                Check(store.Load().Contacts.Count == 1 && store.Load().Contacts[0].Name == "测试联系人", "native add dialog saves trimmed contact");
                Check(original.Contacts.Count == 0, "UI does not mutate supplied settings");
                Check(list.Items.Count == 1 && list.Items[0].Selected, "new contact is selected");
                Check(Find<Button>(form, "PreviewSoundButton").Enabled, "selection enables preview");

                OpenAddDialog(form, delegate(Form dialog)
                {
                    Find<ComboBox>(dialog, "ContactNameInput").Text = "测试联系人";
                    Find<Button>(dialog, "ConfirmAddContactButton").PerformClick();
                    Check(dialog.Visible && dialog.DialogResult != DialogResult.OK, "duplicate keeps add dialog open");
                    Check(Find<Label>(dialog, "ContactNameValidation").Text.Contains("已经添加"), "duplicate gives inline validation");
                    dialog.DialogResult = DialogResult.Cancel;
                    dialog.Close();
                });
                Check(store.Load().Contacts.Count == 1, "duplicate does not alter saved rules");

                ComboBox sounds = Find<ComboBox>(form, "SoundSelection");
                Check(sounds.Items.Count == SoundService.BuiltInSounds.Length, "all built-in sounds offered");
                sounds.SelectedIndex = 1;
                Check(store.Load().Contacts[0].Sound == SoundService.BuiltInSounds[1], "sound selection saves immediately without playback");
                list.Items[0].Checked = false;
                Check(!store.Load().Contacts[0].Enabled && !Find<CheckBox>(form, "ContactEnabled").Checked, "list checkbox updates persisted rule and editor");
                Find<CheckBox>(form, "ContactEnabled").Checked = true;
                Check(store.Load().Contacts[0].Enabled && list.Items[0].Checked, "editor checkbox updates persisted rule and list");

                Find<NumericUpDown>(form, "CooldownSeconds").Value = 7;
                Check(store.Load().CooldownSeconds == 7, "cooldown saves immediately");
                Find<Button>(form, "PauseButton").PerformClick();
                Check(!store.Load().Enabled, "pause saves global state");
                string pausedFeedback = Find<Label>(form, "ActivityStatus").Text;
                Invoke(form, "HandleAlert", "测试联系人");
                Check(Find<Label>(form, "ActivityStatus").Text == pausedFeedback, "paused alert does not play or update last message");
                Find<Button>(form, "PauseButton").PerformClick();
                Check(store.Load().Enabled, "resume saves global state");

                CheckSaveFailureRollback(form, folder);
                MonitorSnapshot unsupported = new MonitorSnapshot { Status = "当前微信未开放消息读取", Detail = "自动提醒尚未生效", Connected = false, CheckedAt = DateTime.UtcNow };
                Thread worker = new Thread(delegate() { Invoke(form, "HandleSnapshot", unsupported); });
                worker.Start();
                Check(worker.Join(1000), "background snapshot handler returns promptly");
                Application.DoEvents();
                Check(Find<Label>(form, "MonitorStatus").Text.Contains(unsupported.Status), "background snapshot reaches native status label");
                NotifyIcon tray = (NotifyIcon)typeof(MainForm).GetField("_tray", PrivateInstance).GetValue(form);
                Check(!tray.Text.Contains("正在监听"), "enabled toggle never claims connection while unsupported");
                Check(Find<Label>(form, "MonitorDetail").Text.Contains("尚未生效"), "unsupported state preserves meaningful diagnostic");

                SavePreview(form, Path.Combine(root, "ui-native-flow.png"));
                CheckControlBounds(form);
                form.Close();
                Check(!form.Visible && !form.IsDisposed, "window close hides to tray");
                form.ShowFromTray();
                Application.DoEvents();
                Check(form.Visible && form.WindowState != FormWindowState.Minimized, "tray restore reopens same window");
                Find<Button>(form, "RemoveContactButton").PerformClick();
                Check(store.Load().Contacts.Count == 0 && list.Items.Count == 0, "remove persists and clears list");
                Check(!Find<Button>(form, "PreviewSoundButton").Enabled, "remove resets selected-contact actions");
            }
        }

        private static void CheckSaveFailureRollback(MainForm form, string folder)
        {
            string path = Path.Combine(folder, "data", "settings.json");
            byte[] original = File.ReadAllBytes(path);
            File.WriteAllText(path, "invalid JSON fixture");
            try
            {
                CheckSaveFailureMessage(delegate { Find<Button>(form, "PauseButton").PerformClick(); });
                AppSettings live = (AppSettings)typeof(MainForm).GetField("_settings", PrivateInstance).GetValue(form);
                Check(live.Enabled && Find<Button>(form, "PauseButton").Text == "暂停提醒", "failed save preserves in-memory and visible state");
                CheckSaveFailureMessage(delegate { Find<CheckBox>(form, "CompatibilityMode").Checked = true; });
                live = (AppSettings)typeof(MainForm).GetField("_settings", PrivateInstance).GetValue(form);
                Check(!live.CompatibilityMode && !Find<CheckBox>(form, "CompatibilityMode").Checked, "failed compatibility save restores original checkbox and setting");
                Check(live.Enabled && live.CooldownSeconds == 7 && live.Contacts.Count == 1 && live.Contacts[0].Enabled,
                    "failed compatibility save preserves all other settings");
                Check(File.ReadAllText(path) == "invalid JSON fixture", "failed save preserves existing file");
            }
            finally { File.WriteAllBytes(path, original); }
        }

        private static void CheckSaveFailureMessage(Action action)
        {
            bool closedMessage = false;
            using (System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer())
            {
                timer.Interval = 25;
                timer.Tick += delegate
                {
                    IntPtr window = FindWindow(null, "无法保存设置");
                    uint pid;
                    GetWindowThreadProcessId(window, out pid);
                    if (window != IntPtr.Zero && pid == (uint)Process.GetCurrentProcess().Id)
                    {
                        closedMessage = true;
                        timer.Stop();
                        PostMessage(window, 0x0010, IntPtr.Zero, IntPtr.Zero);
                    }
                };
                timer.Start();
                action();
                timer.Stop();
            }
            Check(closedMessage, "save failure displays readable native error");
        }

        private static void CheckScaledLayout(string root, float factor)
        {
            string folder = Path.Combine(root, "scale-" + factor.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture));
            SettingsStore store = new SettingsStore(folder);
            AppSettings settings = new AppSettings();
            settings.Contacts.Add(new ContactRule { Name = "测试联系人（很长的备注名用来验证省略显示）" });
            using (SoundService sound = new SoundService())
            using (WeChatMonitor monitor = new WeChatMonitor())
            using (MainForm form = MakeForm(settings, store, sound, monitor))
            {
                Dictionary<Control, Font> originalFonts = new Dictionary<Control, Font>();
                CaptureFonts(form, originalFonts);
                form.SuspendLayout();
                form.Scale(new SizeF(factor, factor));
                foreach (KeyValuePair<Control, Font> pair in originalFonts)
                    pair.Key.Font = new Font(pair.Value.FontFamily, pair.Value.SizeInPoints * factor, pair.Value.Style, GraphicsUnit.Point);
                form.ResumeLayout(true);
                Application.DoEvents();
                CheckControlBounds(form);
                Check(!String.IsNullOrEmpty(Find<Label>(form, "SelectedContactName").Text), "scaled view preserves selected contact");
                Check(Find<ComboBox>(form, "SoundSelection").Width > 150 * factor, "scaled sound selector remains usable");
                CheckBox compatibility = Find<CheckBox>(form, "CompatibilityMode");
                Size compatibilityText = TextRenderer.MeasureText(compatibility.Text, compatibility.Font);
                Check(compatibility.Width >= compatibilityText.Width + (int)(22 * factor) && compatibility.Height >= compatibilityText.Height,
                    "scaled compatibility checkbox text is not clipped");
                SavePreview(form, Path.Combine(root, "ui-scale-" + ((int)(factor * 100)).ToString() + ".png"));
            }
        }

        private static MainForm MakeForm(AppSettings settings, SettingsStore store, SoundService sound, WeChatMonitor monitor)
        {
            MainForm form = new MainForm(settings, store, sound, monitor);
            // Exercise real native controls without observing WeChat or producing messages/audio.
            typeof(MainForm).GetField("_started", PrivateInstance).SetValue(form, true);
            form.Show();
            Application.DoEvents();
            return form;
        }

        private static void OpenAddDialog(MainForm owner, Action<Form> action)
        {
            bool seen = false;
            Exception failure = null;
            using (System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer())
            {
                timer.Interval = 25;
                timer.Tick += delegate
                {
                    Form dialog = null;
                    foreach (Form open in Application.OpenForms) if (open.Name == "AddContactDialog") { dialog = open; break; }
                    if (dialog == null) return;
                    seen = true;
                    timer.Stop();
                    try { action(dialog); }
                    catch (Exception ex) { failure = ex; }
                    finally
                    {
                        if (!dialog.IsDisposed && dialog.Visible && dialog.DialogResult == DialogResult.None)
                        {
                            dialog.DialogResult = DialogResult.Cancel;
                            dialog.Close();
                        }
                    }
                };
                timer.Start();
                Find<Button>(owner, "AddContactButton").PerformClick();
                timer.Stop();
            }
            if (failure != null) throw failure;
            Check(seen, "add button opens real modal dialog");
        }

        private static T Find<T>(Control root, string name) where T : Control
        {
            Control[] found = root.Controls.Find(name, true);
            if (found.Length != 1 || !(found[0] is T)) throw new Exception("Missing/ambiguous control: " + name);
            return (T)found[0];
        }

        private static void Invoke(MainForm form, string method, object argument)
        {
            typeof(MainForm).GetMethod(method, PrivateInstance).Invoke(form, new object[] { argument });
        }

        private static void CheckControlBounds(Control root)
        {
            foreach (Control child in root.Controls)
            {
                if (!child.Visible) continue;
                Rectangle available = root.ClientRectangle;
                // Native controls can overhang by one pixel owing to integer DPI rounding.
                available.Inflate(1, 1);
                Check(available.Contains(child.Bounds), "control within parent bounds: " + (child.Name.Length > 0 ? child.Name : child.GetType().Name)
                    + " " + child.Bounds.ToString() + " in " + root.GetType().Name + " " + available.ToString());
                if (child is Button || child is ComboBox || child is NumericUpDown)
                    Check(child.Height >= child.Font.Height + 2, "interactive control text fits height: " + child.Name);
                CheckControlBounds(child);
            }
        }

        private static void CaptureFonts(Control root, Dictionary<Control, Font> fonts)
        {
            fonts.Add(root, root.Font);
            foreach (Control child in root.Controls) CaptureFonts(child, fonts);
        }

        private static void SavePreview(Form form, string path)
        {
            using (Bitmap image = new Bitmap(form.Width, form.Height))
            {
                form.DrawToBitmap(image, new Rectangle(Point.Empty, image.Size));
                image.Save(path);
            }
        }

        private static void Check(bool condition, string message)
        {
            if (!condition) throw new Exception(message);
            _checks++;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr FindWindow(string className, string title);
        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
        [DllImport("user32.dll")]
        private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    }
}
