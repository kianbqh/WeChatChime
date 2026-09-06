using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Automation;

namespace WeChatChime
{
    public sealed class MonitorSnapshot
    {
        public string Status = "正在检测微信";
        public string Detail = "请登录电脑版微信，并保持会话列表可读取。";
        public bool Connected;
        public bool Minimized;
        public bool CompatibilityActive;
        public List<string> Contacts = new List<string>();
        public int SessionCount;
        public DateTime CheckedAt;
    }

    public sealed class SessionReading
    {
        public string Name;
        public int Unread;
        public bool Capped;
    }

    // Only known 4.x session IDs and the summary line immediately after the contact name are parsed.
    public static class SessionParser
    {
        static readonly Regex Count = new Regex(@"^\[(\d{1,6})(\+?)(?:条|則)?\]", RegexOptions.Compiled);
        public static SessionReading Parse(string automationId, string className, string name)
        {
            name = (name ?? "").Replace("\r\n", "\n");
            if (!(automationId ?? "").StartsWith("session_item_", StringComparison.Ordinal)) return null;
            string contact = automationId.Substring("session_item_".Length);
            if (String.IsNullOrWhiteSpace(contact)) return null;
            string[] lines=name.Split('\n');
            int title=-1;
            for(int i=0;i<Math.Min(lines.Length,4);i++) if(lines[i].Trim()==contact.Trim()) { title=i; break; }
            if(title<0 || title+1>=lines.Length) return null;
            var match = Count.Match(lines[title+1]);
            int unread = 0;
            if (match.Success) Int32.TryParse(match.Groups[1].Value, out unread);
            return new SessionReading { Name = contact.Trim(), Unread = unread, Capped = match.Success && match.Groups[2].Value == "+" };
        }
    }

    public sealed class AlertEngine
    {
        readonly Dictionary<string, int> previous = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, DateTime> lastPlayed = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        readonly HashSet<string> ambiguous = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool baseline;
        public void Reset() { previous.Clear(); lastPlayed.Clear(); ambiguous.Clear(); baseline = false; }
        public bool IsAmbiguous(string name) { return ambiguous.Contains(name); }
        public List<string> Observe(IList<SessionReading> rows, AppSettings settings, DateTime utcNow)
        {
            var alerts = new List<string>();
            var current = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in rows)
            {
                if (current.ContainsKey(row.Name)) ambiguous.Add(row.Name);
                else current[row.Name] = row.Unread;
            }
            foreach (var rule in settings.Contacts)
            {
                int count, oldCount;
                if (!current.TryGetValue(rule.Name, out count) || ambiguous.Contains(rule.Name)) continue;
                bool known = previous.TryGetValue(rule.Name, out oldCount);
                if (baseline && known && settings.Enabled && rule.Enabled && count > 0 && count > oldCount)
                {
                    DateTime last;
                    if (!lastPlayed.TryGetValue(rule.Name, out last) || (utcNow-last).TotalSeconds >= settings.CooldownSeconds)
                    {
                        alerts.Add(rule.Name);
                        lastPlayed[rule.Name] = utcNow;
                    }
                }
            }
            // Keep absent rows so scrolling away and back cannot replay old unread messages.
            foreach (var pair in current) previous[pair.Key] = pair.Value;
            baseline = true;
            return alerts;
        }
    }

    public sealed class WeChatMonitor : IDisposable
    {
        public event Action<MonitorSnapshot> SnapshotChanged;
        public event Action<string> Alert;
        public event Action<string> Fault;
        readonly object gate = new object();
        readonly AutoResetEvent wake = new AutoResetEvent(false);
        readonly AlertEngine engine = new AlertEngine();
        readonly CompatibilityAccess compatibility = new CompatibilityAccess();
        readonly object compatibilityGate = new object();
        AppSettings settings = new AppSettings();
        volatile bool disposed;
        Thread worker;
        System.Threading.Timer watchdog;
        long scanStarted;
        string lastSource;
        DateTime nextCompatibilityAttempt;
        string compatibilityError;
        public string LastRestoreError { get { return compatibility.LastRestoreError; } }
        public void Configure(AppSettings value) {
            lock(gate) { settings=value.Clone(); }
            if(!value.CompatibilityMode) lock(compatibilityGate) {
                string failure=compatibility.Release(); nextCompatibilityAttempt=DateTime.MinValue;
                if(failure!=null) { var fault=Fault; if(fault!=null) fault(failure); }
            }
            wake.Set();
        }
        public void Start()
        {
            if (worker != null || disposed) return;
            worker = new Thread(Run) { IsBackground = true, Name = "WeChat signal reader" };
            worker.SetApartmentState(ApartmentState.MTA);
            watchdog = new System.Threading.Timer(delegate {
                long started = Interlocked.Read(ref scanStarted);
                if(!disposed && started > 0 && (DateTime.UtcNow - new DateTime(started,DateTimeKind.Utc)).TotalSeconds > 12)
                {
                    var update=SnapshotChanged;
                    if(update!=null) update(new MonitorSnapshot { Status="微信读取超时", Detail="微信暂未响应读取，提醒尚不可用。界面仍可操作；可尝试重新打开本工具。", CheckedAt=DateTime.Now });
                }
            }, null, 5000, 5000);
            worker.Start();
        }
        void Run()
        {
            while (!disposed)
            {
                int delay = 1800;
                try
                {
                    AppSettings config;
                    lock(gate) { config=settings.Clone(); }
                    List<SessionReading> rows;
                    string source;
                    Interlocked.Exchange(ref scanStarted,DateTime.UtcNow.Ticks);
                    var snapshot = Read(config,out rows, out source);
                    Interlocked.Exchange(ref scanStarted,0);
                    if (source != null && source != lastSource) { engine.Reset(); lastSource = source; }
                    if(snapshot.Connected)
                    {
                        lock(gate) config=settings.Clone();
                        var alerts = engine.Observe(rows, config, DateTime.UtcNow);
                        var available=new HashSet<string>(snapshot.Contacts,StringComparer.OrdinalIgnoreCase);
                        int missing=0,ambiguousCount=0;
                        foreach(var rule in config.Contacts) if(rule.Enabled) {
                            if(!available.Contains(rule.Name)) missing++;
                            if(engine.IsAmbiguous(rule.Name)) ambiguousCount++;
                        }
                        if(missing>0) snapshot.Detail="有 "+missing+" 位重点联系人当前不可读取，请先在微信中置顶并让会话出现在列表。";
                        if(ambiguousCount>0) snapshot.Detail="检测到同名会话，已停止该名称的提醒。请使用不同备注名后重新打开本工具。";
                        if (!config.Enabled) snapshot.Status = "提醒已暂停";
                        foreach(string name in alerts) { var handler=Alert; if(handler!=null && !disposed) handler(name); }
                    }
                    else { if(source=="absent") { engine.Reset(); lastSource=null; } delay=5000; }
                    var update=SnapshotChanged; if(update!=null && !disposed) update(snapshot);
                }
                catch(Exception ex)
                {
                    Interlocked.Exchange(ref scanStarted,0);
                    var fault=Fault; if(fault!=null && !disposed) fault("微信检测暂不可用（"+ex.GetType().Name+"），稍后自动重试。");
                    delay=5000;
                }
                if(!disposed) wake.WaitOne(delay);
            }
        }
        internal MonitorSnapshot Read(AppSettings config,out List<SessionReading> rows, out string source)
        {
            rows = new List<SessionReading>(); source = null;
            var result = new MonitorSnapshot { CheckedAt=DateTime.Now };
            var windows=FindWeChatWindows();
            if(disposed) return result;
            lock(compatibilityGate) if(!config.CompatibilityMode || windows.Count==0) {
                compatibilityError=compatibility.Release();
                nextCompatibilityAttempt=DateTime.MinValue;
            }
            if(windows.Count==0) { source="absent"; result.Status="未找到微信窗口"; result.Detail="请打开并登录电脑版微信。微信与本工具需以相同用户权限运行。"; return result; }
            foreach(var window in windows)
            {
                var root=AutomationElement.FromHandle(window);
                if(root==null) continue;
                var list = FindSessions(root);
                if(list==null && config.CompatibilityMode && DateTime.UtcNow>=nextCompatibilityAttempt)
                {
                    lock(compatibilityGate) {
                        bool allowed; lock(gate) allowed=settings.CompatibilityMode;
                        if(!disposed && allowed) {
                            compatibilityError=compatibility.EnsureEnabled(window);
                            nextCompatibilityAttempt=DateTime.UtcNow.AddSeconds(compatibilityError==null?5:30);
                        }
                    }
                    if(compatibilityError==null && !disposed) { root=AutomationElement.FromHandle(window); list=FindSessions(root); }
                }
                if(list==null) continue;
                var cache=new CacheRequest();
                {
                    cache.Add(AutomationElement.NameProperty); cache.Add(AutomationElement.AutomationIdProperty); cache.Add(AutomationElement.ClassNameProperty);
                    cache.TreeScope=TreeScope.Element;
                    cache.AutomationElementMode=AutomationElementMode.None;
                    using(cache.Activate())
                    {
                        var items=list.FindAll(TreeScope.Children,new PropertyCondition(AutomationElement.ControlTypeProperty,ControlType.ListItem));
                        for(int i=0;i<items.Count && i<300;i++)
                        {
                            var c=items[i].Cached;
                            var row=SessionParser.Parse(c.AutomationId,c.ClassName,c.Name);
                            if(row!=null) rows.Add(row);
                        }
                    }
                }
                if(rows.Count==0) continue;
                uint pid; Native.GetWindowThreadProcessId(window,out pid);
                using(var process=Process.GetProcessById((int)pid)) source="uia:"+pid+":"+process.StartTime.ToUniversalTime().Ticks+":"+window.ToInt64();
                result.Minimized=Native.IsIconic(window);
                result.CompatibilityActive=compatibility.IsEnabled;
                result.Connected=true; result.Status=result.Minimized?"微信已最小化 · 正在监听":"正在监听微信消息";
                result.Detail="请先置顶重点联系人。按未读数增加提醒；首次连接不补响旧消息。";
                var names=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach(var row in rows) if(names.Add(row.Name)) result.Contacts.Add(row.Name);
                result.Contacts.Sort(StringComparer.CurrentCulture);
                result.SessionCount=rows.Count;
                return result;
            }
            result.Status="当前微信未开放消息读取";
            result.Detail=compatibilityError ?? (config.CompatibilityMode?"兼容模式已启用，但当前会话不可读取。请确认微信已登录并停留在会话页。":"请启用“支持微信最小化（兼容模式）”。仅支持校验通过的微信版本。");
            return result;
        }
        static AutomationElement FindSessions(AutomationElement root)
        {
            if(root==null) return null;
            var list=root.FindFirst(TreeScope.Descendants,new PropertyCondition(AutomationElement.AutomationIdProperty,"session_list"));
            if(list!=null) return list;
            return root.FindFirst(TreeScope.Descendants,new AndCondition(new PropertyCondition(AutomationElement.ControlTypeProperty,ControlType.List),new OrCondition(new PropertyCondition(AutomationElement.NameProperty,"会话"),new PropertyCondition(AutomationElement.NameProperty,"Chats"),new PropertyCondition(AutomationElement.NameProperty,"對話"))));
        }
        internal static List<IntPtr> FindWeChatWindows()
        {
            var ids=new HashSet<int>();
            foreach(string name in new[]{"Weixin","WeChat"}) foreach(var p in Process.GetProcessesByName(name)) using(p) ids.Add(p.Id);
            var windows=new List<IntPtr>();
            Native.EnumWindows(delegate(IntPtr h,IntPtr unused) {
                uint pid; Native.GetWindowThreadProcessId(h,out pid);
                if(ids.Contains((int)pid)) {
                    var b=new StringBuilder(128); Native.GetClassName(h,b,b.Capacity);
                    string cls=b.ToString();
                    if(cls=="WeChatMainWndForPC") windows.Add(h);
                    else if(cls.StartsWith("Qt",StringComparison.Ordinal)) {
                        var title=new StringBuilder(128); Native.GetWindowText(h,title,title.Capacity);
                        if(title.ToString()=="微信" || title.ToString()=="Weixin" || title.ToString()=="WeChat") windows.Add(h);
                    }
                }
                return true;
            },IntPtr.Zero);
            return windows;
        }
        public void Dispose() {
            disposed=true; if(watchdog!=null) watchdog.Dispose(); wake.Set();
            lock(compatibilityGate) compatibility.Dispose();
        }
    }
    internal static class Native
    {
        internal delegate bool EnumProc(IntPtr h, IntPtr p);
        [DllImport("user32.dll")] internal static extern bool EnumWindows(EnumProc cb, IntPtr p);
        [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(IntPtr h, out uint p);
        [DllImport("user32.dll",CharSet=CharSet.Unicode)] internal static extern int GetClassName(IntPtr h,StringBuilder b,int n);
        [DllImport("user32.dll",CharSet=CharSet.Unicode)] internal static extern int GetWindowText(IntPtr h,StringBuilder b,int n);
        [DllImport("user32.dll")] internal static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] internal static extern bool IsIconic(IntPtr h);
    }
}
