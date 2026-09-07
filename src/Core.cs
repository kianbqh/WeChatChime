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
        public static readonly string[] BuiltInSounds =
        {
            "builtin:soft", "builtin:bell", "builtin:wood", "builtin:doorbell",
            "builtin:crystal", "builtin:radar", "builtin:urgent", "builtin:melody"
        };
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
                case "builtin:doorbell": return "叮咚门铃";
                case "builtin:crystal": return "水晶三连";
                case "builtin:radar": return "雷达呼叫";
                case "builtin:urgent": return "醒目连响";
                case "builtin:melody": return "上行旋律";
                default: return String.IsNullOrWhiteSpace(sound) ? "未选择提示音" : Path.GetFileName(sound);
            }
        }

        internal static bool IsBuiltIn(string sound)
        {
            switch (sound)
            {
                case "builtin:soft": case "builtin:bell": case "builtin:wood":
                case "builtin:doorbell": case "builtin:crystal": case "builtin:radar":
                case "builtin:urgent": case "builtin:melody": return true;
                default: return false;
            }
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
            double duration;
            switch (sound)
            {
                case "builtin:soft": duration = 1.20; break;
                case "builtin:bell": duration = 1.55; break;
                case "builtin:wood": duration = 1.10; break;
                case "builtin:doorbell": duration = 1.90; break;
                case "builtin:crystal": duration = 1.85; break;
                case "builtin:radar": duration = 2.40; break;
                case "builtin:urgent": duration = 2.80; break;
                default: duration = 2.40; break;
            }
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
                    double sample = BuiltInSample(sound, time);
                    // Notes are mixed below full scale; this is only a final PCM conversion guard.
                    writer.Write((short)(Math.Max(-1.0, Math.Min(1.0, sample)) * Int16.MaxValue));
                }
                return stream.ToArray();
            }
        }

        private static double BuiltInSample(string sound, double time)
        {
            switch (sound)
            {
                case "builtin:soft":
                    return SoftNote(time, 659.255, 0.65, 0.42) + SoftNote(time - 0.48, 880.0, 0.70, 0.42);
                case "builtin:bell":
                    return BellNote(time, 1046.5, 1.48, 0.78);
                case "builtin:wood":
                    return WoodNote(time, 620, 0.29) + WoodNote(time - 0.34, 720, 0.29) + WoodNote(time - 0.70, 620, 0.32);
                case "builtin:doorbell":
                    return BellNote(time, 830.61, 1.12, 0.80) + BellNote(time - 0.72, 622.25, 1.10, 0.78);
                case "builtin:crystal":
                    return BellNote(time, 880.0, 0.92, 0.68) + BellNote(time - 0.38, 1108.73, 0.96, 0.68)
                        + BellNote(time - 0.80, 1318.51, 0.96, 0.68);
                case "builtin:radar":
                    double pulse = time % 0.80;
                    if (pulse >= 0.56) return 0;
                    double envelope = Math.Sin(Math.PI * pulse / 0.56);
                    double phase = 2 * Math.PI * (650 * pulse + 450 * pulse * pulse);
                    return 0.82 * envelope * envelope * (Math.Sin(phase) + 0.16 * Math.Sin(2 * phase)) / 1.16;
                case "builtin:urgent":
                    double group = time % 0.90;
                    if (time >= 2.70) return 0;
                    return ClearNote(group, 1046.5, 0.28, 0.84) + ClearNote(group - 0.36, 1318.51, 0.28, 0.84);
                default:
                    return ClearNote(time, 523.25, 0.45, 0.62) + ClearNote(time - 0.50, 659.255, 0.45, 0.62)
                        + ClearNote(time - 1.0, 783.99, 0.45, 0.62) + ClearNote(time - 1.50, 1046.5, 0.82, 0.68);
            }
        }

        private static double SoftNote(double time, double frequency, double duration, double level)
        {
            if (time < 0 || time >= duration) return 0;
            double envelope = Math.Sin(Math.PI * time / duration);
            return level * envelope * envelope * Math.Sin(2 * Math.PI * frequency * time);
        }

        private static double BellNote(double time, double frequency, double duration, double level)
        {
            if (time < 0 || time >= duration) return 0;
            double envelope = Fade(time, duration, 0.008, 0.12) * Math.Exp(-2.5 * time / duration);
            double phase = 2 * Math.PI * frequency * time;
            return level * envelope * (Math.Sin(phase) + 0.26 * Math.Sin(phase * 2.003) + 0.10 * Math.Sin(phase * 3.99)) / 1.36;
        }

        private static double WoodNote(double time, double frequency, double duration)
        {
            if (time < 0 || time >= duration) return 0;
            double envelope = Fade(time, duration, 0.004, 0.04) * Math.Exp(-14 * time);
            double phase = 2 * Math.PI * (frequency * time - 140 * time * time);
            return 0.82 * envelope * (Math.Sin(phase) + 0.30 * Math.Sin(2 * Math.PI * frequency * 2.67 * time)) / 1.30;
        }

        private static double ClearNote(double time, double frequency, double duration, double level)
        {
            if (time < 0 || time >= duration) return 0;
            double phase = 2 * Math.PI * frequency * time;
            return level * Fade(time, duration, 0.014, 0.06) * (Math.Sin(phase) + 0.20 * Math.Sin(2 * phase)) / 1.20;
        }

        private static double Fade(double time, double duration, double attack, double release)
        {
            // A raised-cosine edge avoids clicks, including where repeated notes meet silence.
            double edge = Math.Min(1.0, Math.Min(time / attack, (duration - time) / release));
            return 0.5 - 0.5 * Math.Cos(Math.PI * edge);
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
