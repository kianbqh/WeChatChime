using System;
using System.Collections.Generic;
using WeChatChime;
class MonitorTestsMain
{
    static int count;
    static void Check(bool success,string message) { if(!success) throw new Exception(message); count++; }
    static SessionReading Row(string name,int unread) { return new SessionReading {Name=name,Unread=unread}; }
    static void Main()
    {
        var r=SessionParser.Parse("session_item_联系人A","mmui::ChatSessionCell","置顶\n联系人A\n[3条]\n你好");
        Check(r.Name=="联系人A" && r.Unread==3,"4.x id and unread");
        Check(SessionParser.Parse("session_item_A","","A\n[2則]\n正文").Unread==2,"traditional");
        Check(SessionParser.Parse("session_item_A","","A\n[2]\nhello").Unread==2,"english");
        Check(SessionParser.Parse("session_item_A","","A\n[99+条]\nhello").Capped,"cap");
        Check(SessionParser.Parse("","Text","联系人A\n[3条]")==null,"reject non-session");
        Check(SessionParser.Parse("session_item_A","mmui::ChatSessionCell","A\n普通内容\n[9条]").Unread==0,"body newline is not unread metadata");
        Check(SessionParser.Parse("","mmui::ChatSessionCell","已置顶\nA\n[3条]")==null,"reject ambiguous legacy fallback");
        Check(SessionParser.Parse("session_item_A","mmui::ChatSessionCell","已置顶\nA\n[3条]消息\n09:05\n").Unread==3,"pinned session summary");
        Check(SessionParser.Parse("session_item_A","mmui::ChatSessionCell","A\n[4条]消息\n09:05\n消息免打扰\n").Unread==4,"real 4.1.13 muted row shape");
        Check(SessionParser.Parse("session_item_A","mmui::ChatSessionCell","Other\n[3条]")==null,"session id and title must agree");
        var config=new AppSettings {Enabled=true,CooldownSeconds=2,Contacts=new List<ContactRule>{new ContactRule{Id="a",Name="A",Enabled=true,Sound="builtin:soft"}}};
        var engine=new AlertEngine(); var time=new DateTime(2026,1,1,0,0,0,DateTimeKind.Utc);
        Check(engine.Observe(new[]{Row("A",4)},config,time).Count==0,"startup backlog silent");
        Check(engine.Observe(new[]{Row("A",5)},config,time.AddSeconds(3)).Count==1,"new unread");
        Check(engine.Observe(new[]{Row("A",5)},config,time.AddSeconds(6)).Count==0,"duplicate snapshot");
        Check(engine.Observe(new[]{Row("A",0)},config,time.AddSeconds(7)).Count==0,"read reset");
        Check(engine.Observe(new[]{Row("A",1)},config,time.AddSeconds(8)).Count==1,"new after reading");
        Check(engine.Observe(new[]{Row("A",2)},config,time.AddSeconds(9)).Count==0,"burst cooldown");
        engine.Observe(new SessionReading[0],config,time.AddSeconds(10));
        Check(engine.Observe(new[]{Row("A",2)},config,time.AddSeconds(11)).Count==0,"scroll back silent");
        config.Enabled=false; engine.Observe(new[]{Row("A",3)},config,time.AddSeconds(12)); config.Enabled=true;
        Check(engine.Observe(new[]{Row("A",3)},config,time.AddSeconds(15)).Count==0,"pause observes without replay");
        Check(engine.Observe(new[]{Row("A",4),Row("A",6)},config,time.AddSeconds(18)).Count==0,"ambiguous duplicate blocked");
        Check(engine.Observe(new[]{Row("A",6)},config,time.AddSeconds(19)).Count==0,"ambiguity cannot clear by scrolling");
        Check(engine.IsAmbiguous("A"),"ambiguous state reported");
        engine.Reset(); Check(engine.Observe(new[]{Row("A",7)},config,time.AddSeconds(19)).Count==0,"reconnect silent baseline");
        Check(engine.Observe(new[]{Row("AB",8)},config,time.AddSeconds(22)).Count==0,"exact contact match");
        engine.Reset(); engine.Observe(new[]{Row("B",0)},config,time);
        Check(engine.Observe(new[]{Row("A",9)},config,time.AddSeconds(3)).Count==0,"first unseen old unread establishes baseline");
        config.Contacts[0].Sound="builtin:bell";
        Check(engine.Observe(new[]{Row("A",10)},config,time.AddSeconds(6)).Count==1,"sound setting changes preserve next arrival");
        Check(engine.Observe(new[]{Row("A",11)},config,time.AddSeconds(9)).Count==1,"same content still alerts on each count increment");
        // A transient source read failure does not call Observe or Reset.
        Check(engine.Observe(new[]{Row("A",12)},config,time.AddSeconds(20)).Count==1,"same source resumes after a read gap");
        CheckRecoverySchedule(time);
        CheckConnectionLifecycle(config,time);
        Console.WriteLine("PASS "+count+" monitor checks");
    }

    static void CheckRecoverySchedule(DateTime time)
    {
        var retry=new CompatibilityRetrySchedule();
        const string first="uia:100:1000:10",other="uia:100:1000:20",restarted="uia:200:2000:10";
        int firstCalls=0,otherCalls=0,restartedCalls=0;
        Func<string> firstAttempt=delegate { firstCalls++; return firstCalls==1?"temporary failure":null; };
        Func<string> otherAttempt=delegate { otherCalls++; return null; };
        Check(retry.TryEnsure(first,time,true,firstAttempt)=="temporary failure" && firstCalls==1,"first recovery failure is recorded");
        Check(retry.TryEnsure(other,time,true,otherAttempt)==null && otherCalls==1,"failing candidate does not delay another window");
        Check(retry.TryEnsure(first,time.AddSeconds(29),true,firstAttempt)=="temporary failure" && firstCalls==1,"failure respects retry interval");
        Check(retry.TryEnsure(other,time.AddSeconds(4),true,otherAttempt)==null && otherCalls==1,"healthy state checks are rate limited");
        Check(retry.TryEnsure(other,time.AddSeconds(5),true,otherAttempt)==null && otherCalls==2,"healthy provider is checked again even without UIA failure");
        Check(retry.TryEnsure(restarted,time.AddSeconds(6),true,delegate { restartedCalls++; return null; })==null && restartedCalls==1,"new process identity retries immediately despite old failure");
        Check(retry.TryEnsure(first,time.AddSeconds(30),true,firstAttempt)==null && firstCalls==2,"failed target can recover automatically");
        Check(retry.TryEnsure(first,time.AddSeconds(34),true,firstAttempt)==null && firstCalls==2,"recovered target uses healthy check interval");
        Check(retry.TryEnsure(first,time.AddSeconds(35),true,firstAttempt)==null && firstCalls==3,"recovered target is rechecked after five seconds");
        Check(retry.TryEnsure(first,time.AddSeconds(70),false,delegate { throw new Exception("disabled adapter must never be called"); })==null,"disabled mode never invokes recovery");
        retry.Retain(new[]{other});
        Check(retry.Count==1,"disappeared windows are removed from retry state");
        Check(retry.TryEnsure(first,time.AddSeconds(36),true,firstAttempt)==null && firstCalls==4,"a window that reappears has no stale retry deadline");
        retry.Clear();
        Check(retry.Count==0,"turning compatibility off clears prior errors and retry clocks");
        Check(retry.TryEnsure(first,time.AddSeconds(37),true,firstAttempt)==null && firstCalls==5,"reenabling can recover immediately");
        retry.Retain(new string[0]);
        Check(retry.Count==0,"no windows leaves no retained retry entries");
    }

    static void CheckConnectionLifecycle(AppSettings config,DateTime time)
    {
        var connected=new MonitorSnapshot { Connected=true };
        var unavailable=new MonitorSnapshot();
        const string first="uia:100:1000:10",restarted="uia:200:2000:20";
        using(var monitor=new WeChatMonitor())
        {
            Check(monitor.ObserveSnapshot(connected,new[]{Row("A",4)},first,config,time).Count==0,"first connected source establishes silent baseline");
            Check(monitor.ObserveSnapshot(unavailable,new[]{Row("A",99)},null,config,time.AddSeconds(3)).Count==0,"failed read cannot update counts or alert");
            Check(monitor.ObserveSnapshot(connected,new[]{Row("A",4)},first,config,time.AddSeconds(6)).Count==0,"recovery with unchanged unread does not replay backlog");
            monitor.ObserveSnapshot(unavailable,new SessionReading[0],null,config,time.AddSeconds(9));
            Check(monitor.ObserveSnapshot(connected,new[]{Row("A",5)},first,config,time.AddSeconds(12)).Count==1,"same source alerts once for increase during recovery gap");
            Check(monitor.ObserveSnapshot(connected,new[]{Row("A",5)},first,config,time.AddSeconds(15)).Count==0,"recovery snapshot is not repeated");
            Check(monitor.ObserveSnapshot(connected,new SessionReading[0],first,config,time.AddSeconds(18)).Count==0,"readable empty list remains a connected observation");
            Check(monitor.ObserveSnapshot(connected,new[]{Row("A",5)},first,config,time.AddSeconds(21)).Count==0,"rows returning from an empty list keep their prior baseline");
            Check(monitor.ObserveSnapshot(connected,new[]{Row("A",6)},first,config,time.AddSeconds(24)).Count==1,"new unread after an empty list is still detected");
            Check(monitor.ObserveSnapshot(connected,new[]{Row("A",10)},restarted,config,time.AddSeconds(27)).Count==0,"a restarted process does not replay its existing unread messages");
            Check(monitor.ObserveSnapshot(connected,new[]{Row("A",11)},restarted,config,time.AddSeconds(30)).Count==1,"a restarted process detects subsequent arrivals");
            Check(monitor.ObserveSnapshot(unavailable,new SessionReading[0],"absent",config,time.AddSeconds(33)).Count==0,"confirmed absence clears source without alerting");
            Check(monitor.ObserveSnapshot(connected,new[]{Row("A",12)},restarted,config,time.AddSeconds(36)).Count==0,"return after confirmed absence establishes a fresh baseline");
        }
    }
}
