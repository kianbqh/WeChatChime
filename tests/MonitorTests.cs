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
        Console.WriteLine("PASS "+count+" monitor checks");
    }
}
