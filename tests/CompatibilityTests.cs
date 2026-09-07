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
        CheckFlagRecovery();
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
    static void Throws(Action action,string message)
    {
        bool failed=false;
        try { action(); } catch(InvalidOperationException) { failed=true; }
        Check(failed,message);
    }
    sealed class FlagDevice
    {
        public byte Value;
        public int Validations,Reads,Writes;
        public bool RejectValidation,FailAfterWrite,IgnoreWrite;
        public void Validate() { Validations++; if(RejectValidation) throw new InvalidOperationException("identity or runtime mismatch"); }
        public byte Read() { Reads++; return Value; }
        public void Write(byte value) {
            Writes++;
            if(!IgnoreWrite) Value=value;
            if(FailAfterWrite) throw new InvalidOperationException("write verification interrupted");
        }
    }
    static void CheckFlagRecovery()
    {
        var device=new FlagDevice(); var lease=new CompatibilityFlagLease();
        Action enable=delegate { lease.EnsureEnabled(device.Validate,device.Read,device.Write); };
        Action restore=delegate { lease.Restore(device.Validate,device.Read,device.Write); };
        enable();
        Check(device.Value==1 && lease.IsOwned,"initial zero acquired");
        enable(); Check(device.Writes==1 && device.Validations==2,"healthy flag revalidated without repeated writes");
        for(int i=0;i<3;i++) {
            device.Value=0; enable();
            Check(device.Value==1 && lease.IsOwned,"known reset automatically restored "+i);
        }
        restore(); Check(device.Value==0 && !lease.IsOwned,"exit after repeated recovery restores zero");
        int writes=device.Writes; restore(); Check(device.Writes==writes,"release idempotent");

        device.Value=1; enable();
        Check(!lease.IsOwned,"pre-existing accessibility not owned");
        restore(); Check(device.Value==1 && device.Writes==writes,"external enabled state untouched on release");
        enable(); device.Value=0; enable();
        Check(lease.IsOwned && device.Value==1,"external state later reset is newly acquired");
        restore(); Check(device.Value==0,"newly acquired state restores its immediate original zero");

        device.Value=2; writes=device.Writes;
        Throws(enable,"unexpected state refuses enable");
        Check(device.Value==2 && device.Writes==writes && !lease.IsOwned,"unexpected byte preserved");
        device.Value=0; device.RejectValidation=true; int reads=device.Reads;
        Throws(enable,"identity or runtime mismatch refuses access");
        Check(device.Reads==reads && device.Writes==writes,"guard failure precedes all memory access");
        device.RejectValidation=false; device.FailAfterWrite=true;
        Throws(enable,"partial successful write reports failure");
        Check(device.Value==1 && lease.IsOwned,"partial write retains rollback ownership");
        device.FailAfterWrite=false; restore();
        Check(device.Value==0 && !lease.IsOwned,"partial write rolled back");

        device.IgnoreWrite=true; Throws(enable,"failed enable readback rejected");
        Check(lease.IsOwned,"unverified write still retains rollback ownership");
        device.IgnoreWrite=false; restore(); Check(!lease.IsOwned,"already reset zero releases ownership");
        enable(); device.Value=2; writes=device.Writes;
        Throws(restore,"unexpected external mutation refuses restore");
        Check(device.Value==2 && device.Writes==writes && lease.IsOwned,"failed restore retains retry without overwrite");
        device.Value=1; device.RejectValidation=true; reads=device.Reads;
        Throws(restore,"restore revalidates retained target");
        Check(device.Reads==reads && device.Writes==writes,"restore guard failure precedes memory access");
        device.RejectValidation=false; device.IgnoreWrite=true;
        Throws(restore,"failed restore readback rejected");
        Check(lease.IsOwned,"restore readback failure retains retry ownership");
        device.IgnoreWrite=false; restore();
        Check(device.Value==0 && !lease.IsOwned,"restore retry succeeds");
        enable(); lease.Clear(); device.Value=1; restore();
        Check(!lease.IsOwned && device.Value==1,"exited process cleanup does not restore into a new process");
    }
    static bool Region(uint flags)
    {
        return CompatibilityAccess.RegionMatches(0x2000,0x1000,0x1000,0x1000,flags,0x1000000,0x1000,0x2010,1,false);
    }
}
