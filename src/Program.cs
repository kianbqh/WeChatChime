using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

[assembly: AssemblyTitle("轻响 · 微信消息提示助手")]
[assembly: AssemblyDescription("本地重点联系人提示音")]
[assembly: AssemblyCompany("Local tools")]
[assembly: AssemblyProduct("轻响")]
[assembly: AssemblyVersion("0.2.0.0")]

namespace WeChatChime
{
    internal static class Program
    {
        const string ShowSignal="Local\\WeChatChime.Show";
        const string ExitSignal="Local\\WeChatChime.Exit";
        static bool Signal(string name)
        {
            try { using(var signal=EventWaitHandle.OpenExisting(name)) return signal.Set(); }
            catch(WaitHandleCannotBeOpenedException) { return false; }
        }
        [DllImport("user32.dll")] static extern bool SetProcessDPIAware();
        [STAThread] static void Main(string[] args)
        {
            if(Array.IndexOf(args,"--exit")>=0) { Environment.ExitCode=Signal(ExitSignal)?0:1; return; }
            try { SetProcessDPIAware(); } catch(EntryPointNotFoundException) { }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            bool smoke = args.Length > 0 && args[0] == "--smoke-ui";
            bool created;
            using(var mutex=new Mutex(true, smoke ? "Local\\WeChatChime.Smoke" : "Local\\WeChatChime.App", out created))
            {
                if(!created) { if(!Signal(smoke?ShowSignal+".Smoke":ShowSignal)) MessageBox.Show("轻响已在运行。请从系统托盘打开。","轻响",MessageBoxButtons.OK,MessageBoxIcon.Information); return; }
                try
                {
                    string folder=smoke && args.Length>1 ? Path.GetFullPath(args[1]) : AppDomain.CurrentDomain.BaseDirectory;
                    var store=new SettingsStore(folder);
                    var settings=store.Load();
                    if(Array.IndexOf(args,"--enable-compatibility")>=0) {
                        settings.CompatibilityMode=true;
                        store.Save(settings);
                    }
                    using(var sound=new SoundService())
                    using(var monitor=new WeChatMonitor())
                    using(var form=new MainForm(settings,store,sound,monitor))
                    using(var showSignal=new EventWaitHandle(false,EventResetMode.AutoReset,smoke?ShowSignal+".Smoke":ShowSignal))
                    using(var exitSignal=new EventWaitHandle(false,EventResetMode.AutoReset,smoke?ExitSignal+".Smoke":ExitSignal))
                    {
                        // Queue startup signals on the UI thread even before Application.Run begins.
                        IntPtr formHandle=form.Handle;
                        var showWait=ThreadPool.RegisterWaitForSingleObject(showSignal,delegate { form.ShowFromTray(); },null,Timeout.Infinite,false);
                        var exitWait=ThreadPool.RegisterWaitForSingleObject(exitSignal,delegate {
                            try { if(!form.IsDisposed && form.IsHandleCreated) form.BeginInvoke(new Action(Application.Exit)); }
                            catch(InvalidOperationException) { }
                        },null,Timeout.Infinite,false);
                        try
                        {
                        if(smoke)
                        {
                            form.Shown+=delegate {
                                var timer=new System.Windows.Forms.Timer {Interval=1200};
                                timer.Tick+=delegate {
                                    timer.Stop(); timer.Dispose();
                                    using(var bmp=new Bitmap(form.Width,form.Height)) { form.DrawToBitmap(bmp,new Rectangle(Point.Empty,bmp.Size)); bmp.Save(Path.Combine(folder,"ui-preview.png")); }
                                    File.WriteAllText(Path.Combine(folder,"ui-smoke.txt"),"Rendered "+form.Width+" x "+form.Height+" at "+DateTime.Now.ToString("s"));
                                    Application.Exit();
                                };
                                timer.Start();
                            };
                        }
                        Application.Run(form);
                        }
                        finally { showWait.Unregister(null); exitWait.Unregister(null); }
                    }
                }
                catch(Exception ex)
                {
                    MessageBox.Show("轻响未能启动：\n"+ex.Message+"\n\n如配置文件损坏，请先备份程序旁的 data 文件夹后再处理。", "启动失败",MessageBoxButtons.OK,MessageBoxIcon.Error);
                    Environment.ExitCode=1;
                }
                finally { mutex.ReleaseMutex(); }
            }
        }
    }
}
