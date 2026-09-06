using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
using WeChatChime;

// Render the real UI with fictional data. Does not start WeChat monitoring or play audio.
class RenderPreview
{
    [STAThread] static void Main(string[] args)
    {
        if(args.Length!=2) throw new ArgumentException("Expected fixture directory and output PNG.");
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        var settings=new AppSettings { CompatibilityMode=true };
        settings.Contacts.Add(new ContactRule {Name="家人",Sound="builtin:soft"});
        settings.Contacts.Add(new ContactRule {Name="项目搭档",Sound="builtin:bell"});
        settings.Contacts.Add(new ContactRule {Name="小林",Sound="builtin:wood"});
        var store=new SettingsStore(Path.GetFullPath(args[0]));
        using(var sound=new SoundService())
        using(var monitor=new WeChatMonitor())
        using(var form=new MainForm(settings,store,sound,monitor))
        {
            var flags=BindingFlags.Instance|BindingFlags.NonPublic;
            typeof(MainForm).GetField("_started",flags).SetValue(form,true);
            form.Show();
            Application.DoEvents();
            var list=(ListView)form.Controls.Find("ContactRules",true)[0];
            list.Items[0].Selected=true;
            typeof(MainForm).GetMethod("HandleSnapshot",flags).Invoke(form,new object[]{new MonitorSnapshot {
                Status="界面预览 · 演示数据",Detail="给重要的人，设置专属提示音。",SessionCount=3,
                CheckedAt=new DateTime(2026,9,7,9,41,0,DateTimeKind.Local),Contacts=new List<string>{"家人","项目搭档","小林"}
            }});
            Application.DoEvents();
            var content=form.Controls[0];
            using(var bitmap=new Bitmap(content.Width,content.Height))
            {
                content.DrawToBitmap(bitmap,new Rectangle(Point.Empty,bitmap.Size));
                string output=Path.GetFullPath(args[1]);
                Directory.CreateDirectory(Path.GetDirectoryName(output));
                bitmap.Save(output,System.Drawing.Imaging.ImageFormat.Png);
            }
        }
    }
}
