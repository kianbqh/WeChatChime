using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace WeChatChime
{
    // Explicit opt-in adapter for one verified Weixin build. No scanning, injection,
    // executable writes, on-disk modification, or automatic version fallback.
    public sealed class CompatibilityAccess : IDisposable
    {
        internal const string SupportedVersion = "4.1.13.63";
        internal const string ExpectedHash = "8bf9ae1edf52676bbfa2e0c4024af207c0f9bba589758f99de467515c90635e1";
        internal const long TargetRva = 0xAE2B0C8;
        internal const long InstructionRva = 0x839902;
        readonly object gate = new object();
        ProcessHandle process;
        int processId, moduleSize;
        long creationTime, moduleBase;
        byte original;
        bool changed, enabled, disposed;
        string restoreError;

        public string LastRestoreError { get { lock(gate) return restoreError; } }
        public bool IsOwned { get { lock(gate) return changed; } }
        public bool IsEnabled { get { lock(gate) return enabled; } }

        // The caller must require the user's EnableCompatibility setting before calling.
        // Null means the runtime flag was verified as enabled, not that UIA or alerts work.
        public string EnsureEnabled(IntPtr window)
        {
            lock(gate)
            {
                if(disposed) return "兼容模式已关闭。";
                uint windowPid;
                if(window == IntPtr.Zero || GetWindowThreadProcessId(window,out windowPid)==0 || windowPid==0)
                    return "微信窗口已失效，请等待重新检测。";
                try
                {
                    if(process != null)
                    {
                        if(!IsRunning() || windowPid != processId)
                        {
                            string failure = ReleaseCore();
                            if(failure != null) return failure;
                        }
                        else
                        {
                            ValidateIdentity();
                            ValidateRuntime();
                            if(Read(moduleBase+TargetRva,1)[0] != 1)
                            {
                                enabled=false;
                                return "微信兼容状态已变化，请关闭后重新启用兼容模式。";
                            }
                            enabled=true;
                            return null;
                        }
                    }
                    Acquire(window,windowPid);
                    ValidateIdentity();
                    ValidateRuntime();
                    original=Read(moduleBase+TargetRva,1)[0];
                    if(original>1) throw new InvalidOperationException("微信辅助访问状态异常，已停止启用。" );
                    if(original==0)
                    {
                        // Mark ownership BEFORE attempting the write: failed verification
                        // must still cause rollback of a possibly completed single-byte write.
                        changed=true;
                        WriteByte(1);
                    }
                    enabled=true;
                    restoreError=null;
                    return null;
                }
                catch(Exception ex)
                {
                    enabled=false;
                    string failure=ReleaseCore();
                    return Describe(ex)+(failure==null ? "" : " "+failure);
                }
            }
        }

        void Acquire(IntPtr window,uint windowPid)
        {
            if(IntPtr.Size!=8) throw new InvalidOperationException("兼容模式需要 64 位程序。" );
            int foundId=0, foundSize=0;
            long foundBase=0, foundCreated=0;
            string foundPath=null;
            var candidates=Process.GetProcessesByName("Weixin");
            try
            {
                foreach(var candidate in candidates)
                {
                    foreach(ProcessModule module in candidate.Modules)
                    {
                        if(!String.Equals(module.ModuleName,"Weixin.dll",StringComparison.OrdinalIgnoreCase)) continue;
                        if(foundId!=0) throw new InvalidOperationException("检测到多个微信主进程，兼容模式暂不支持。" );
                        foundId=candidate.Id; foundSize=module.ModuleMemorySize;
                        foundBase=module.BaseAddress.ToInt64(); foundPath=module.FileName;
                        foundCreated=candidate.StartTime.ToUniversalTime().ToFileTimeUtc();
                    }
                }
            }
            finally { foreach(var candidate in candidates) candidate.Dispose(); }
            if(foundId==0 || foundId!=windowPid)
                throw new InvalidOperationException("微信窗口与受支持的主进程不匹配。" );
            if(!LocationsInModule(foundSize)) throw new InvalidOperationException("微信模块范围不匹配。" );
            if(FileVersionInfo.GetVersionInfo(foundPath).FileVersion!=SupportedVersion)
                throw new InvalidOperationException("当前微信版本不支持兼容模式（仅支持 "+SupportedVersion+"）。" );
            string hash;
            using(var sha=SHA256.Create())
            using(var stream=File.Open(foundPath,FileMode.Open,FileAccess.Read,FileShare.Read))
                hash=BitConverter.ToString(sha.ComputeHash(stream)).Replace("-","").ToLowerInvariant();
            if(!String.Equals(hash,ExpectedHash,StringComparison.Ordinal))
                throw new InvalidOperationException("微信文件校验不匹配，已停止启用兼容模式。" );

            // SYNCHRONIZE + QUERY_INFORMATION + VM_OPERATION + VM_READ + VM_WRITE.
            process=OpenProcess(0x100438u,false,foundId);
            if(process==null || process.IsInvalid)
                throw new InvalidOperationException("无法访问微信进程（"+Marshal.GetLastWin32Error()+"），请保持相同运行权限。" );
            processId=foundId; moduleBase=foundBase; moduleSize=foundSize; creationTime=foundCreated;
            ValidateIdentity();
            uint currentPid;
            if(GetWindowThreadProcessId(window,out currentPid)==0 || currentPid!=processId)
                throw new InvalidOperationException("微信窗口所属进程已变化。" );
        }

        bool IsRunning()
        {
            uint state=WaitForSingleObject(process,0);
            if(state==0) return false;
            if(state==0x102) return true;
            throw new InvalidOperationException("无法确认微信进程状态（"+Marshal.GetLastWin32Error()+"）。" );
        }

        void ValidateIdentity()
        {
            long created,exited,kernel,user;
            if(!IsRunning() || !GetProcessTimes(process,out created,out exited,out kernel,out user) || created!=creationTime)
                throw new InvalidOperationException("微信进程已退出或身份已变化。" );
        }

        void ValidateRuntime()
        {
            if(!LocationsInModule(moduleSize)) throw new InvalidOperationException("微信模块范围不匹配。" );
            var target=Query(moduleBase+TargetRva);
            if(!RegionMatches(target.BaseAddress.ToInt64(),target.AllocationBase.ToInt64(),target.RegionSize.ToUInt64(),
                target.State,target.Protect,target.Type,moduleBase,moduleBase+TargetRva,1,false))
                throw new InvalidOperationException("微信状态内存页校验失败，已停止访问。" );
            var code=Query(moduleBase+InstructionRva);
            if(!RegionMatches(code.BaseAddress.ToInt64(),code.AllocationBase.ToInt64(),code.RegionSize.ToUInt64(),
                code.State,code.Protect,code.Type,moduleBase,moduleBase+InstructionRva,18,true))
                throw new InvalidOperationException("微信指令内存页校验失败，已停止访问。" );
            if(!RuntimeSignatureMatches(Read(moduleBase+InstructionRva,18)))
                throw new InvalidOperationException("微信运行时指令校验失败，已停止访问。" );
        }

        internal static bool LocationsInModule(long size)
        {
            return TargetRva>=0 && TargetRva<size && InstructionRva>=0 && InstructionRva<=size-18;
        }

        internal static bool RuntimeSignatureMatches(byte[] code)
        {
            if(code==null || code.Length!=18) return false;
            if(code[0]!=0x48 || code[1]!=0x85 || code[2]!=0xc9 || code[3]!=0x0f || code[4]!=0x84
                || code[9]!=0x80 || code[10]!=0x3d || code[15]!=0 || code[16]!=0x0f || code[17]!=0x84) return false;
            return InstructionRva+16+BitConverter.ToInt32(code,11)==TargetRva;
        }

        internal static bool RegionMatches(long baseAddress,long allocationBase,ulong size,uint state,uint protect,uint type,
            long expectedModule,long address,int count,bool executable)
        {
            if(expectedModule<=0 || allocationBase!=expectedModule || baseAddress<expectedModule || address<baseAddress || count<=0
                || state!=0x1000 || type!=0x1000000) return false;
            // Exact flags reject guard/no-access/cache modifiers and writable executable pages.
            if(executable ? protect!=0x20 : (protect!=0x04 && protect!=0x08)) return false;
            ulong offset=(ulong)(address-baseAddress);
            return offset<size && (ulong)count<=size-offset;
        }

        MemoryRegion Query(long address)
        {
            MemoryRegion value;
            var length=new UIntPtr((uint)Marshal.SizeOf(typeof(MemoryRegion)));
            if(VirtualQueryEx(process,new IntPtr(address),out value,length).ToUInt64()!=length.ToUInt64())
                throw new InvalidOperationException("无法校验微信内存页（"+Marshal.GetLastWin32Error()+"）。" );
            return value;
        }

        byte[] Read(long address,int count)
        {
            var bytes=new byte[count]; IntPtr read;
            if(!ReadProcessMemory(process,new IntPtr(address),bytes,new IntPtr(count),out read) || read.ToInt64()!=count)
                throw new InvalidOperationException("微信状态读取失败（"+Marshal.GetLastWin32Error()+"）。" );
            return bytes;
        }

        void WriteByte(byte value)
        {
            IntPtr written;
            if(!WriteProcessMemory(process,new IntPtr(moduleBase+TargetRva),new[]{value},new IntPtr(1),out written) || written.ToInt64()!=1)
                throw new InvalidOperationException("微信兼容状态写入失败（"+Marshal.GetLastWin32Error()+"）。" );
            if(Read(moduleBase+TargetRva,1)[0]!=value)
                throw new InvalidOperationException("微信兼容状态回读校验失败。" );
        }

        // On restore failure retain the real handle for a later Release retry. Never
        // reopen by PID, overwrite an unexpected value, or restore a pre-existing 1.
        public string Release() { lock(gate) return disposed ? restoreError : ReleaseCore(); }

        string ReleaseCore()
        {
            enabled=false;
            if(process==null || process.IsInvalid) { ClearHandle(); restoreError=null; return null; }
            try
            {
                if(changed && IsRunning())
                {
                    ValidateIdentity();
                    ValidateRuntime();
                    byte current=Read(moduleBase+TargetRva,1)[0];
                    if(current!=original && current!=1)
                        throw new InvalidOperationException("微信状态被其他程序修改，未覆盖该状态。" );
                    if(current!=original) WriteByte(original);
                    if(Read(moduleBase+TargetRva,1)[0]!=original)
                        throw new InvalidOperationException("微信原始状态还原校验失败。" );
                }
                ClearHandle(); restoreError=null; return null;
            }
            catch(Exception ex)
            {
                // Exiting while a read/write was in flight needs no restoration.
                if(WaitForSingleObject(process,0)==0) { ClearHandle(); restoreError=null; return null; }
                restoreError="兼容状态未能还原："+Describe(ex)+" 请退出并重新打开微信。";
                return restoreError;
            }
        }

        static string Describe(Exception ex)
        {
            if(ex is InvalidOperationException) return ex.Message;
            return "兼容模式操作失败（"+ex.GetType().Name+"）。";
        }

        void ClearHandle()
        {
            if(process!=null) process.Dispose();
            process=null; processId=0; moduleSize=0; moduleBase=0; creationTime=0; changed=false; enabled=false;
        }

        public void Dispose()
        {
            lock(gate)
            {
                if(disposed) return;
                ReleaseCore();
                ClearHandle(); // LastRestoreError remains available to the owner.
                disposed=true;
            }
        }

        sealed class ProcessHandle : SafeHandleZeroOrMinusOneIsInvalid
        {
            public ProcessHandle() : base(true) { }
            protected override bool ReleaseHandle() { return CloseHandle(handle); }
        }
        [StructLayout(LayoutKind.Sequential)] struct MemoryRegion
        {
            public IntPtr BaseAddress,AllocationBase;
            public uint AllocationProtect;
            public UIntPtr RegionSize;
            public uint State,Protect,Type;
        }
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window,out uint pid);
        [DllImport("kernel32.dll",SetLastError=true)] static extern ProcessHandle OpenProcess(uint access,bool inherit,int pid);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
        [DllImport("kernel32.dll",SetLastError=true)] static extern uint WaitForSingleObject(ProcessHandle process,uint milliseconds);
        [DllImport("kernel32.dll",SetLastError=true)] static extern bool GetProcessTimes(ProcessHandle process,out long created,out long exited,out long kernel,out long user);
        [DllImport("kernel32.dll",SetLastError=true)] static extern UIntPtr VirtualQueryEx(ProcessHandle process,IntPtr address,out MemoryRegion info,UIntPtr length);
        [DllImport("kernel32.dll",SetLastError=true)] static extern bool ReadProcessMemory(ProcessHandle process,IntPtr address,byte[] data,IntPtr size,out IntPtr read);
        [DllImport("kernel32.dll",SetLastError=true)] static extern bool WriteProcessMemory(ProcessHandle process,IntPtr address,byte[] data,IntPtr size,out IntPtr written);
    }
}
