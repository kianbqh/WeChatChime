using System;
using System.Collections.Generic;
using System.IO;
using System.Media;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace WeChatChime
{
    [DataContract]
    public sealed class ContactRule
    {
        [DataMember(Order = 0)] public string Id = Guid.NewGuid().ToString("N");
        [DataMember(Order = 1)] public string Name = "";
        [DataMember(Order = 2)] public string Sound = "builtin:soft";
        [DataMember(Order = 3)] public bool Enabled = true;

        [OnDeserializing]
        private void BeforeDeserialize(StreamingContext context)
        {
            Id = Guid.NewGuid().ToString("N");
            Name = "";
            Sound = "builtin:soft";
            Enabled = true;
        }

        public ContactRule Clone()
        {
            return new ContactRule { Id = Id, Name = Name, Sound = Sound, Enabled = Enabled };
        }
    }

    [DataContract]
    public sealed class AppSettings
    {
        [DataMember(Order = 0)] public bool Enabled = true;
        [DataMember(Order = 1)] public int CooldownSeconds = 2;
        [DataMember(Order = 2)] public List<ContactRule> Contacts = new List<ContactRule>();
        [DataMember(Order = 3)] public bool CompatibilityMode = false;

        [OnDeserializing]
        private void BeforeDeserialize(StreamingContext context)
        {
            Enabled = true;
            CooldownSeconds = 2;
            Contacts = new List<ContactRule>();
            CompatibilityMode = false;
        }

        public AppSettings Clone()
        {
            AppSettings copy = new AppSettings { Enabled = Enabled, CooldownSeconds = CooldownSeconds, CompatibilityMode = CompatibilityMode };
            if (Contacts == null) { copy.Contacts = null; return copy; }
            foreach (ContactRule rule in Contacts) copy.Contacts.Add(rule == null ? null : rule.Clone());
            return copy;
        }
    }

    public sealed class SettingsStore
    {
        public const long MaximumSoundBytes = 20L * 1024 * 1024;
        private readonly string dataDirectory;
        private readonly string settingsPath;
        private readonly string soundsDirectory;
        private readonly object gate = new object();

        public SettingsStore(string baseDirectory)
        {
            if (String.IsNullOrWhiteSpace(baseDirectory)) throw new ArgumentException("软件目录不能为空。", "baseDirectory");
            dataDirectory = Path.Combine(Path.GetFullPath(baseDirectory), "data");
            settingsPath = Path.Combine(dataDirectory, "settings.json");
            soundsDirectory = Path.Combine(dataDirectory, "sounds");
        }

        public AppSettings Load()
        {
            lock (gate)
            {
                if (!File.Exists(settingsPath)) return new AppSettings();
                try
                {
                    if (new FileInfo(settingsPath).Length > 1024 * 1024)
                        throw new InvalidDataException("设置文件过大。");
                    using (FileStream stream = File.OpenRead(settingsPath))
                    {
                        AppSettings settings = (AppSettings)new DataContractJsonSerializer(typeof(AppSettings)).ReadObject(stream);
                        ResolvePortableSounds(settings);
                        Validate(settings);
                        return settings;
                    }
                }
                catch (Exception ex)
                {
                    if (!(ex is SerializationException) && !(ex is ArgumentException) && !(ex is InvalidDataException) && !(ex is System.Xml.XmlException)) throw;
                    throw new InvalidDataException("设置文件损坏，已保留原文件：" + settingsPath + "。请移走或修复该文件后重新打开软件。", ex);
                }
            }
        }

        public void Save(AppSettings settings)
        {
            if (settings == null) throw new ArgumentNullException("settings");
            AppSettings snapshot = settings.Clone();
            Validate(snapshot);
            lock (gate)
            {
                // An unreadable existing file must never be replaced by defaults.
                if (File.Exists(settingsPath)) Load();
                MakeSoundsPortable(snapshot);
                Directory.CreateDirectory(Path.GetDirectoryName(settingsPath));
                string temporary = settingsPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    using (FileStream stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        new DataContractJsonSerializer(typeof(AppSettings)).WriteObject(stream, snapshot);
                        stream.Flush(true);
                    }
                    if (File.Exists(settingsPath)) File.Replace(temporary, settingsPath, null, true);
                    else File.Move(temporary, settingsPath);
                }
                finally
                {
                    if (File.Exists(temporary)) File.Delete(temporary);
                }
            }
        }

        private void ResolvePortableSounds(AppSettings settings)
        {
            if (settings == null || settings.Contacts == null) return;
            foreach (ContactRule rule in settings.Contacts)
            {
                if (rule == null || String.IsNullOrWhiteSpace(rule.Sound) || rule.Sound.StartsWith("builtin:", StringComparison.Ordinal) || Path.IsPathRooted(rule.Sound)) continue;
                string relative = rule.Sound.Replace('/', '\\');
                if (!relative.StartsWith("sounds\\", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("提示音相对路径必须位于 sounds 目录。");
                foreach (string part in relative.Split('\\'))
                    if (part == "." || part == ".." || part.Length == 0) throw new ArgumentException("提示音相对路径包含无效目录。");
                string resolved = Path.GetFullPath(Path.Combine(dataDirectory, relative));
                if (!resolved.StartsWith(soundsDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("提示音路径超出了 sounds 目录。");
                rule.Sound = resolved;
            }
        }

        private void MakeSoundsPortable(AppSettings settings)
        {
            string prefix = soundsDirectory + Path.DirectorySeparatorChar;
            foreach (ContactRule rule in settings.Contacts)
            {
                if (SoundService.IsBuiltIn(rule.Sound)) continue;
                string absolute = Path.GetFullPath(rule.Sound);
                if (absolute.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    rule.Sound = "sounds/" + absolute.Substring(prefix.Length).Replace('\\', '/');
            }
        }

        public string ImportSound(string path)
        {
            string source = ValidateSoundFile(path);
            lock (gate)
            {
                if (String.Equals(Path.GetDirectoryName(source), soundsDirectory, StringComparison.OrdinalIgnoreCase)) return source;
                Directory.CreateDirectory(soundsDirectory);
                string hash;
                using (SHA256 sha = SHA256.Create())
                using (FileStream stream = File.OpenRead(source)) hash = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
                string name = Path.GetFileNameWithoutExtension(source);
                if (name.Length > 60) name = name.Substring(0, 60);
                string destination = Path.Combine(soundsDirectory, name + "-" + hash.Substring(0, 12) + Path.GetExtension(source).ToLowerInvariant());
                if (File.Exists(destination))
                {
                    string existingHash;
                    using (SHA256 sha = SHA256.Create())
                    using (FileStream stream = File.OpenRead(destination)) existingHash = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
                    if (existingHash == hash) return destination;
                    destination = Path.Combine(soundsDirectory, name + "-" + hash + "-" + Guid.NewGuid().ToString("N").Substring(0, 8) + Path.GetExtension(source).ToLowerInvariant());
                }
                string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    // Copy from a read-locked source, and recheck the size in case it changed after validation.
                    using (FileStream input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
                    {
                        if (input.Length == 0 || input.Length > MaximumSoundBytes) throw new InvalidDataException("请选择 20 MB 以内的非空音频文件。");
                        using (FileStream output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                        {
                            input.CopyTo(output);
                            output.Flush(true);
                        }
                    }
                    File.Move(temporary, destination);
                    return destination;
                }
                finally
                {
                    if (File.Exists(temporary)) File.Delete(temporary);
                }
            }
        }

        internal static string ValidateSoundFile(string path)
        {
            if (String.IsNullOrWhiteSpace(path)) throw new ArgumentException("请先选择提示音文件。", "path");
            if (path.IndexOf('"') >= 0 || path.IndexOf('\r') >= 0 || path.IndexOf('\n') >= 0)
                throw new ArgumentException("音频文件路径无效。", "path");
            string fullPath = Path.GetFullPath(path);
            string extension = Path.GetExtension(fullPath).ToLowerInvariant();
            if (extension != ".wav" && extension != ".mp3" && extension != ".m4a" && extension != ".wma")
                throw new ArgumentException("支持 WAV、MP3、M4A 和 WMA 音频。", "path");
            FileInfo info = new FileInfo(fullPath);
            if (!info.Exists) throw new FileNotFoundException("提示音文件不存在，请重新选择。", fullPath);
            if (info.Length == 0 || info.Length > MaximumSoundBytes) throw new InvalidDataException("请选择 20 MB 以内的非空音频文件。");
            return fullPath;
        }

        private static void Validate(AppSettings settings)
        {
            if (settings == null || settings.Contacts == null) throw new ArgumentException("设置缺少联系人列表。");
            if (settings.CooldownSeconds < 0 || settings.CooldownSeconds > 30) throw new ArgumentException("提示间隔需为 0 到 30 秒。");
            HashSet<string> names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            HashSet<string> ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (ContactRule rule in settings.Contacts)
            {
                if (rule == null || String.IsNullOrWhiteSpace(rule.Name)) throw new ArgumentException("联系人名称不能为空。");
                rule.Name = rule.Name.Trim();
                if (rule.Name.Length > 128) throw new ArgumentException("联系人名称不能超过 128 个字符。");
                if (!names.Add(rule.Name)) throw new ArgumentException("已添加同名联系人：" + rule.Name);
                if (String.IsNullOrWhiteSpace(rule.Id) || !ids.Add(rule.Id)) throw new ArgumentException("联系人标识缺失或重复。");
                if (String.IsNullOrWhiteSpace(rule.Sound)) throw new ArgumentException("请为联系人选择提示音。");
                if (rule.Sound.StartsWith("builtin:", StringComparison.Ordinal))
                {
                    if (!SoundService.IsBuiltIn(rule.Sound)) throw new ArgumentException("未知的内置提示音。");
                }
                else if (!Path.IsPathRooted(rule.Sound) || rule.Sound.IndexOf('"') >= 0 || rule.Sound.IndexOf('\r') >= 0 || rule.Sound.IndexOf('\n') >= 0)
                    throw new ArgumentException("提示音文件需使用有效的绝对路径。");
            }
        }
    }

    public sealed class SoundPlaybackErrorEventArgs : EventArgs
    {
        public Exception Exception { get; private set; }
        public string Sound { get; private set; }
        public string Message { get { return Exception.Message; } }
        public SoundPlaybackErrorEventArgs(string sound, Exception exception) { Sound = sound; Exception = exception; }
    }

    public sealed class SoundService : IDisposable
    {
        public static readonly string[] BuiltInSounds = { "builtin:soft", "builtin:bell", "builtin:wood" };
        public event EventHandler<SoundPlaybackErrorEventArgs> PlaybackFailed;
        private readonly object gate = new object();
        private readonly Queue<Action> commands = new Queue<Action>();
        private Thread audioThread;
        private PumpWindow pump;
        private bool disposed;
        private bool threadExited;
        private int version;
        private SoundPlayer player;
        private MemoryStream waveStream;
        private string mciAlias;
        private NotifyWindow notification;
        private const int DispatchMessage = 0x8000 + 75;
        private const int MciNotifyMessage = 0x3B9;

        public static string DisplayName(string sound)
        {
            switch (sound)
            {
                case "builtin:soft": return "轻柔双音";
                case "builtin:bell": return "清脆铃音";
                case "builtin:wood": return "轻敲木音";
                default: return String.IsNullOrWhiteSpace(sound) ? "未选择提示音" : Path.GetFileName(sound);
            }
        }

        internal static bool IsBuiltIn(string sound)
        {
            return sound == "builtin:soft" || sound == "builtin:bell" || sound == "builtin:wood";
        }

        public void Play(string sound)
        {
            lock (gate) { if (disposed) throw new ObjectDisposedException("SoundService"); }
            if (sound != null && sound.StartsWith("builtin:", StringComparison.Ordinal) && !IsBuiltIn(sound))
                throw new ArgumentException("未知的内置提示音。", "sound");
            if (!IsBuiltIn(sound)) sound = SettingsStore.ValidateSoundFile(sound);
            string selected = sound;
            lock (gate)
            {
                if (disposed) throw new ObjectDisposedException("SoundService");
                int requested = ++version;
                EnqueueLocked(delegate
                {
                    lock (gate) { if (disposed || requested != version) return; }
                    try { PlayOnAudioThread(selected, requested); }
                    catch (Exception ex)
                    {
                        StopOnAudioThread();
                        RaisePlaybackFailed(selected, ex, requested);
                    }
                });
            }
        }

        public void Stop()
        {
            lock (gate)
            {
                if (disposed || audioThread == null || threadExited) return;
                ++version;
                EnqueueLocked(StopOnAudioThread);
            }
        }

        public void Dispose()
        {
            lock (gate)
            {
                if (disposed) return;
                disposed = true;
                ++version;
                if (audioThread == null || threadExited) return;
                EnqueueLocked(delegate { StopOnAudioThread(); Application.ExitThread(); });
            }
        }

        private void EnqueueLocked(Action action)
        {
            if (threadExited) throw new InvalidOperationException("音频服务已停止，请重新打开软件。");
            commands.Enqueue(action);
            if (audioThread == null)
            {
                audioThread = new Thread(AudioThreadMain);
                audioThread.Name = "WeChatChime.Audio";
                audioThread.IsBackground = true;
                audioThread.SetApartmentState(ApartmentState.STA);
                audioThread.Start();
            }
            else if (pump != null) PostMessage(pump.Handle, DispatchMessage, IntPtr.Zero, IntPtr.Zero);
        }

        private void AudioThreadMain()
        {
            PumpWindow window = null;
            try
            {
                window = new PumpWindow(this);
                lock (gate) { pump = window; }
                // Dispatch only once the message loop exists, including an immediate Dispose.
                PostMessage(window.Handle, DispatchMessage, IntPtr.Zero, IntPtr.Zero);
                Application.Run();
            }
            catch (Exception ex)
            {
                int current;
                lock (gate) { current = version; }
                RaisePlaybackFailed("", new InvalidOperationException("无法启动音频服务：" + ex.Message, ex), current);
            }
            finally
            {
                StopOnAudioThread();
                lock (gate) { pump = null; threadExited = true; commands.Clear(); }
                if (window != null) window.DestroyHandle();
            }
        }

        private void DrainCommands()
        {
            while (true)
            {
                Action next;
                lock (gate)
                {
                    if (commands.Count == 0) return;
                    next = commands.Dequeue();
                }
                next();
            }
        }

        private void PlayOnAudioThread(string sound, int requested)
        {
            StopOnAudioThread();
            if (IsBuiltIn(sound))
            {
                waveStream = new MemoryStream(CreateBuiltInWave(sound), false);
                player = new SoundPlayer(waveStream);
                player.Load();
                lock (gate) { if (disposed || requested != version) { StopOnAudioThread(); return; } }
                player.Play();
                return;
            }
            mciAlias = "wchime" + Guid.NewGuid().ToString("N");
            notification = new NotifyWindow(this, sound, requested);
            string type = String.Equals(Path.GetExtension(sound), ".wav", StringComparison.OrdinalIgnoreCase) ? "waveaudio" : "mpegvideo";
            Mci("open \"" + sound + "\" type " + type + " alias " + mciAlias, IntPtr.Zero);
            lock (gate) { if (disposed || requested != version) { StopOnAudioThread(); return; } }
            Mci("play " + mciAlias + " from 0 notify", notification.Handle);
        }

        private void StopOnAudioThread()
        {
            if (player != null) { player.Stop(); player.Dispose(); player = null; }
            if (waveStream != null) { waveStream.Dispose(); waveStream = null; }
            if (mciAlias != null)
            {
                mciSendString("close " + mciAlias, null, 0, IntPtr.Zero);
                mciAlias = null;
            }
            if (notification != null) { notification.DestroyHandle(); notification = null; }
        }

        private void OnMciNotify(NotifyWindow sender, int result)
        {
            if (!Object.ReferenceEquals(notification, sender)) return;
            if (result == 1 || result == 8)
            {
                StopOnAudioThread();
                if (result == 8) RaisePlaybackFailed(sender.Sound, new InvalidOperationException("系统无法播放此音频，请尝试另一个文件或 WAV 格式。"), sender.Version);
            }
        }

        private void RaisePlaybackFailed(string sound, Exception exception, int requested)
        {
            lock (gate) { if (disposed || version != requested) return; }
            EventHandler<SoundPlaybackErrorEventArgs> handler = PlaybackFailed;
            if (handler == null) return;
            // Subscribers may marshal to their UI thread; a closing UI must not crash this worker.
            foreach (EventHandler<SoundPlaybackErrorEventArgs> subscriber in handler.GetInvocationList())
            {
                try { subscriber(this, new SoundPlaybackErrorEventArgs(sound, exception)); }
                catch (ObjectDisposedException) { }
                catch (InvalidOperationException) { }
            }
        }

        private static void Mci(string command, IntPtr callback)
        {
            uint error = mciSendString(command, null, 0, callback);
            if (error == 0) return;
            StringBuilder message = new StringBuilder(512);
            mciGetErrorString(error, message, message.Capacity);
            throw new InvalidOperationException("无法播放提示音：" + message + "（可尝试 WAV 格式）");
        }

        internal static byte[] CreateBuiltInWave(string sound)
        {
            if (!IsBuiltIn(sound)) throw new ArgumentException("未知的内置提示音。", "sound");
            const int rate = 22050;
            double duration = sound == "builtin:bell" ? 0.72 : (sound == "builtin:wood" ? 0.22 : 0.48);
            int count = (int)(rate * duration);
            using (MemoryStream stream = new MemoryStream(44 + count * 2))
            using (BinaryWriter writer = new BinaryWriter(stream))
            {
                writer.Write(Encoding.ASCII.GetBytes("RIFF")); writer.Write(36 + count * 2);
                writer.Write(Encoding.ASCII.GetBytes("WAVEfmt ")); writer.Write(16);
                writer.Write((short)1); writer.Write((short)1); writer.Write(rate); writer.Write(rate * 2);
                writer.Write((short)2); writer.Write((short)16);
                writer.Write(Encoding.ASCII.GetBytes("data")); writer.Write(count * 2);
                for (int i = 0; i < count; i++)
                {
                    double time = (double)i / rate;
                    double sample;
                    if (sound == "builtin:soft")
                    {
                        sample = SoftNote(time, 659.255, 0.21) + SoftNote(time - 0.18, 880.0, 0.28);
                    }
                    else if (sound == "builtin:bell")
                    {
                        double envelope = Math.Min(time / 0.007, 1.0) * Math.Exp(-time * 7.5) * Math.Min((duration - time) / 0.035, 1.0);
                        sample = 0.18 * envelope * (Math.Sin(2 * Math.PI * 1046.5 * time) + 0.22 * Math.Sin(2 * Math.PI * 2096 * time));
                    }
                    else
                    {
                        double envelope = Math.Min(time / 0.003, 1.0) * Math.Exp(-time * 27) * Math.Min((duration - time) / 0.02, 1.0);
                        sample = 0.23 * envelope * (Math.Sin(2 * Math.PI * (620 * time - 260 * time * time)) + 0.12 * Math.Sin(2 * Math.PI * 1580 * time));
                    }
                    writer.Write((short)(Math.Max(-0.30, Math.Min(0.30, sample)) * Int16.MaxValue));
                }
                return stream.ToArray();
            }
        }

        private static double SoftNote(double time, double frequency, double duration)
        {
            if (time < 0 || time >= duration) return 0;
            double envelope = Math.Sin(Math.PI * time / duration);
            return 0.16 * envelope * envelope * Math.Sin(2 * Math.PI * frequency * time);
        }

        private sealed class PumpWindow : NativeWindow
        {
            private readonly SoundService owner;
            internal PumpWindow(SoundService owner)
            {
                this.owner = owner;
                CreateHandle(new CreateParams { Caption = "WeChatChime.Audio", Parent = new IntPtr(-3) });
            }
            protected override void WndProc(ref Message message)
            {
                if (message.Msg == DispatchMessage) owner.DrainCommands();
                base.WndProc(ref message);
            }
        }

        private sealed class NotifyWindow : NativeWindow
        {
            private readonly SoundService owner;
            internal readonly string Sound;
            internal readonly int Version;
            internal NotifyWindow(SoundService owner, string sound, int version)
            {
                this.owner = owner; Sound = sound; Version = version;
                CreateHandle(new CreateParams { Caption = "WeChatChime.Sound", Parent = new IntPtr(-3) });
            }
            protected override void WndProc(ref Message message)
            {
                if (message.Msg == MciNotifyMessage) owner.OnMciNotify(this, message.WParam.ToInt32());
                base.WndProc(ref message);
            }
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool PostMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
        [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
        private static extern uint mciSendString(string command, StringBuilder result, int resultLength, IntPtr callback);
        [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
        private static extern bool mciGetErrorString(uint error, StringBuilder text, int textLength);
    }
}
