using System;
using WeChatChime;

class CompatibilityTestsMain
{
    static int count;
    static void Check(bool success,string message) { if(!success) throw new Exception(message); count++; }
    static byte[] ValidCode()
    {
        byte[] code={0x48,0x85,0xc9,0x0f,0x84,0,0,0,0,0x80,0x3d,0,0,0,0,0,0x0f,0x84};
        Array.Copy(BitConverter.GetBytes((int)(CompatibilityAccess.TargetRva-CompatibilityAccess.InstructionRva-16)),0,code,11,4);
        return code;
    }
    static void Main()
    {
        Check(CompatibilityAccess.LocationsInModule(CompatibilityAccess.TargetRva+1),"last byte inside module");
        Check(!CompatibilityAccess.LocationsInModule(CompatibilityAccess.TargetRva),"target at module end rejected");
        Check(!CompatibilityAccess.LocationsInModule(0),"empty module rejected");
        Check(!CompatibilityAccess.LocationsInModule(-1),"negative module size rejected");
        Check(CompatibilityAccess.RuntimeSignatureMatches(ValidCode()),"verified RIP-relative comparison");
        foreach(int index in new[]{0,1,2,3,4,9,10,15,16,17})
        {
            byte[] code=ValidCode(); code[index]^=1;
            Check(!CompatibilityAccess.RuntimeSignatureMatches(code),"changed opcode rejected "+index);
        }
        byte[] wrongTarget=ValidCode(); wrongTarget[11]^=1;
        Check(!CompatibilityAccess.RuntimeSignatureMatches(wrongTarget),"changed RIP target rejected");
        Check(!CompatibilityAccess.RuntimeSignatureMatches(null),"missing code rejected");
        Check(!CompatibilityAccess.RuntimeSignatureMatches(new byte[17]),"short code rejected");
        Check(!CompatibilityAccess.RuntimeSignatureMatches(new byte[19]),"oversized code rejected");
        Check(Region(0x04),"writable image target accepted");
        Check(Region(0x08),"write-copy image target accepted");
        foreach(uint flags in new uint[]{0,1,2,0x10,0x20,0x40,0x80,0x104,0x204,0x404})
            Check(!Region(flags),"unsafe target protection rejected "+flags);
        Check(CompatibilityAccess.RegionMatches(0x2000,0x1000,0x1000,0x1000,0x20,0x1000000,0x1000,0x2010,18,true),"read-execute code accepted");
        Check(!CompatibilityAccess.RegionMatches(0x2000,0x1000,0x1000,0x1000,0x40,0x1000000,0x1000,0x2010,18,true),"writable code rejected");
        Check(!CompatibilityAccess.RegionMatches(0x2000,0x1100,0x1000,0x1000,4,0x1000000,0x1000,0x2010,1,false),"foreign allocation rejected");
        Check(!CompatibilityAccess.RegionMatches(0x2000,0x1000,0x1000,0x2000,4,0x1000000,0x1000,0x2010,1,false),"reserved target rejected");
        Check(!CompatibilityAccess.RegionMatches(0x2000,0x1000,0x1000,0x1000,4,0x20000,0x1000,0x2010,1,false),"private target rejected");
        Check(!CompatibilityAccess.RegionMatches(0x2000,0x1000,0x1000,0x1000,4,0x1000000,0x1000,0x1fff,1,false),"address before page rejected");
        Check(!CompatibilityAccess.RegionMatches(0x2000,0x1000,0x1000,0x1000,4,0x1000000,0x1000,0x3000,1,false),"address after page rejected");
        Check(!CompatibilityAccess.RegionMatches(0x2000,0x1000,0x1000,0x1000,0x20,0x1000000,0x1000,0x2fff,18,true),"code crossing region rejected");
        Check(!CompatibilityAccess.RegionMatches(0x2000,0x1000,0x1000,0x1000,4,0x1000000,0x1000,0x2010,0,false),"zero byte access rejected");
        // These paths never resolve or open a Weixin process and perform no remote reads or writes.
        using(var access=new CompatibilityAccess())
        {
            Check(access.EnsureEnabled(IntPtr.Zero)!=null,"invalid window rejected without access");
            Check(!access.IsOwned && !access.IsEnabled,"failed input creates no ownership");
            Check(access.Release()==null && access.LastRestoreError==null,"empty release succeeds");
            access.Dispose();
            Check(access.EnsureEnabled(IntPtr.Zero)!=null,"disposed adapter rejects enable");
        }
        Console.WriteLine("PASS "+count+" compatibility guard checks (no WeChat access)");
    }
    static bool Region(uint flags)
    {
        return CompatibilityAccess.RegionMatches(0x2000,0x1000,0x1000,0x1000,flags,0x1000000,0x1000,0x2010,1,false);
    }
}
