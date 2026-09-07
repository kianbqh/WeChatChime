using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;

namespace WeChatChime.Tests
{
    internal static class CoreTestsMain
    {
        private static int checks;

        private static int Main(string[] args)
        {
            if (args.Length != 1 || !Path.IsPathRooted(args[0]))
            {
                Console.Error.WriteLine("Usage: CoreTests.exe <absolute temporary root>");
                return 2;
            }
            string root = Path.Combine(Path.GetFullPath(args[0]), "core-tests-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(root);
                CheckConfiguration(root);
                CheckCorruptFile(root);
                CheckImports(root);
                CheckPortableSettings(root);
                CheckWaves();
                CheckBuiltInSettings(root);
                CheckPlaybackFailure(root);
                Console.WriteLine("PASS " + checks + " core checks; fixtures: " + root);
                return 0;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine("FAIL: " + exception);
                Console.Error.WriteLine("Fixtures retained: " + root);
                return 1;
            }
        }

        private static void CheckConfiguration(string root)
        {
            string app = Path.Combine(root, "roundtrip");
            SettingsStore store = new SettingsStore(app);
            AppSettings defaults = store.Load();
            Assert(defaults.Enabled && defaults.CooldownSeconds == 2 && defaults.Contacts.Count == 0 && !defaults.CompatibilityMode, "fresh defaults keep compatibility mode off");
            Assert(!Directory.Exists(app), "loading defaults has no write side effects");
            AppSettings settings = new AppSettings { Enabled = false, CooldownSeconds = 5, CompatibilityMode = true };
            settings.Contacts.Add(new ContactRule { Name = "  家人 👋  ", Sound = "builtin:bell", Enabled = false });
            settings.Contacts.Add(new ContactRule { Name = "同事", Sound = "builtin:wood" });
            store.Save(settings);
            Assert(settings.Contacts[0].Name == "  家人 👋  ", "Save does not mutate caller");
            AppSettings restored = store.Load();
            Assert(!restored.Enabled && restored.CooldownSeconds == 5 && restored.Contacts.Count == 2 && restored.CompatibilityMode, "settings roundtrip preserves compatibility mode");
            Assert(restored.Contacts[0].Name == "家人 👋" && !restored.Contacts[0].Enabled && restored.Contacts[0].Sound == "builtin:bell", "Unicode contact roundtrip");
            AppSettings clone = restored.Clone();
            Assert(clone.CompatibilityMode, "clone preserves compatibility mode");
            clone.CompatibilityMode = false;
            Assert(restored.CompatibilityMode, "clone compatibility changes are isolated");
            clone.Contacts[0].Name = "独立副本";
            Assert(restored.Contacts[0].Name == "家人 👋", "deep clone");
            restored.Enabled = true;
            store.Save(restored);
            Assert(store.Load().Enabled, "atomic replacement works");
            string settingsPath = Path.Combine(app, "data", "settings.json");
            byte[] before = File.ReadAllBytes(settingsPath);
            restored.Contacts.Add(new ContactRule { Name = " 家人 👋 " });
            Expect<ArgumentException>(delegate { store.Save(restored); }, "duplicate Chinese name rejected");
            Assert(Equal(before, File.ReadAllBytes(settingsPath)), "invalid Save preserves prior config");
            restored.Contacts.RemoveAt(2);
            restored.Contacts.Add(new ContactRule { Name = "ALICE" });
            restored.Contacts.Add(new ContactRule { Name = " alice " });
            Expect<ArgumentException>(delegate { store.Save(restored); }, "duplicate case-insensitive name rejected");
            restored.Contacts.RemoveAt(3);
            restored.Contacts[2].Id = restored.Contacts[0].Id;
            Expect<ArgumentException>(delegate { store.Save(restored); }, "duplicate identity rejected");
            Assert(Directory.GetFiles(Path.Combine(app, "data"), "*.tmp").Length == 0, "no temporary setting files left");

            string legacyApp = Path.Combine(root, "legacy-configuration");
            string legacyData = Path.Combine(legacyApp, "data");
            Directory.CreateDirectory(legacyData);
            File.WriteAllText(Path.Combine(legacyData, "settings.json"), "{\"Enabled\":false,\"CooldownSeconds\":6,\"Contacts\":[]}");
            SettingsStore legacyStore = new SettingsStore(legacyApp);
            AppSettings legacy = legacyStore.Load();
            Assert(!legacy.CompatibilityMode && !legacy.Enabled && legacy.CooldownSeconds == 6, "older configuration keeps compatibility off and preserves prior settings");
            legacyStore.Save(legacy);
            Assert(!legacyStore.Load().CompatibilityMode, "disabled compatibility mode survives roundtrip");
            File.WriteAllText(Path.Combine(legacyData, "settings.json"), "{}");
            AppSettings sparse = legacyStore.Load();
            Assert(!sparse.CompatibilityMode && sparse.Enabled && sparse.CooldownSeconds == 2 && sparse.Contacts.Count == 0, "deserialization initializes missing fields safely");
        }

        private static void CheckCorruptFile(string root)
        {
            string app = Path.Combine(root, "corrupt");
            string data = Path.Combine(app, "data");
            Directory.CreateDirectory(data);
            string file = Path.Combine(data, "settings.json");
            File.WriteAllText(file, "{broken-settings:\"do not overwrite\"", Encoding.UTF8);
            byte[] original = File.ReadAllBytes(file);
            SettingsStore store = new SettingsStore(app);
            Expect<InvalidDataException>(delegate { store.Load(); }, "malformed settings rejected");
            Expect<InvalidDataException>(delegate { store.Save(new AppSettings()); }, "Save cannot silently replace corruption");
            Assert(Equal(original, File.ReadAllBytes(file)), "corrupt settings retained byte-for-byte");
            File.WriteAllText(file, "{\"Enabled\":true,\"CooldownSeconds\":-1,\"Contacts\":[]}");
            Expect<InvalidDataException>(delegate { store.Load(); }, "invalid setting values rejected");
            File.WriteAllText(file, "{\"Enabled\":true,\"CooldownSeconds\":31,\"Contacts\":[]}");
            Expect<InvalidDataException>(delegate { store.Load(); }, "cooldown upper bound matches UI");
        }

        private static void CheckPortableSettings(string root)
        {
            string originalApp = Path.Combine(root, "portable-original");
            SettingsStore originalStore = new SettingsStore(originalApp);
            string source = Path.Combine(root, "portable-sound.wav");
            File.WriteAllBytes(source, SoundService.CreateBuiltInWave("builtin:wood"));
            string imported = originalStore.ImportSound(source);
            AppSettings settings = new AppSettings();
            settings.Contacts.Add(new ContactRule { Name = "家人", Sound = imported });
            settings.Contacts.Add(new ContactRule { Name = "外部音频", Sound = source });
            originalStore.Save(settings);
            string settingsPath = Path.Combine(originalApp, "data", "settings.json");
            string json = File.ReadAllText(settingsPath);
            Assert(!json.Contains("portable-original"), "imported sound saved as portable path");
            Assert(originalStore.Load().Contacts[0].Sound == imported, "portable path resolves before returning settings");
            Assert(settings.Contacts[0].Sound == imported, "portable Save leaves caller path absolute");
            string relocatedApp = Path.Combine(root, "portable-relocated");
            CopyDirectory(originalApp, relocatedApp);
            AppSettings moved = new SettingsStore(relocatedApp).Load();
            string relocatedSound = Path.Combine(relocatedApp, "data", "sounds", Path.GetFileName(imported));
            Assert(moved.Contacts[0].Sound == relocatedSound && File.Exists(relocatedSound), "copied app loads sound from new directory");
            Assert(moved.Contacts[1].Sound == source, "external absolute path remains compatible");
            string corruptedJson = json.Replace("sounds\\/", "sounds\\/..\\/");
            Assert(corruptedJson != json, "traversal fixture modifies serialized relative path");
            File.WriteAllText(settingsPath, corruptedJson);
            Expect<InvalidDataException>(delegate { originalStore.Load(); }, "portable traversal rejected");
            Expect<InvalidDataException>(delegate { originalStore.Save(settings); }, "traversal corruption cannot be overwritten");
        }

        private static void CopyDirectory(string source, string destination)
        {
            Directory.CreateDirectory(destination);
            foreach (string file in Directory.GetFiles(source)) File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
            foreach (string directory in Directory.GetDirectories(source)) CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
        }

        private static void CheckImports(string root)
        {
            string app = Path.Combine(root, "import");
            SettingsStore store = new SettingsStore(app);
            string source = Path.Combine(root, "自定义提示.WAV");
            byte[] wave = SoundService.CreateBuiltInWave("builtin:soft");
            File.WriteAllBytes(source, wave);
            string imported = store.ImportSound(source);
            Assert(Path.IsPathRooted(imported) && File.Exists(imported) && Equal(wave, File.ReadAllBytes(imported)), "sound imported intact to absolute path");
            Assert(String.Equals(Path.GetDirectoryName(imported), Path.Combine(app, "data", "sounds"), StringComparison.OrdinalIgnoreCase), "sound stored locally");
            Assert(store.ImportSound(source) == imported, "repeated import deduplicated");
            Assert(store.ImportSound(imported) == imported, "same-directory import not copied");
            File.Delete(source);
            Assert(File.Exists(imported), "import survives original deletion");
            Expect<FileNotFoundException>(delegate { store.ImportSound(source); }, "missing sound rejected");
            string unsupported = Path.Combine(root, "unsupported.txt");
            File.WriteAllText(unsupported, "not audio");
            Expect<ArgumentException>(delegate { store.ImportSound(unsupported); }, "unsupported extension rejected");
            string empty = Path.Combine(root, "empty.wav");
            File.WriteAllBytes(empty, new byte[0]);
            Expect<InvalidDataException>(delegate { store.ImportSound(empty); }, "empty sound rejected");
            string oversized = Path.Combine(root, "oversized.wav");
            using (FileStream file = File.Create(oversized)) file.SetLength(SettingsStore.MaximumSoundBytes + 1);
            Expect<InvalidDataException>(delegate { store.ImportSound(oversized); }, "20 MB limit enforced");
            File.Delete(oversized);
            Assert(Directory.GetFiles(Path.Combine(app, "data", "sounds")).Length == 1, "rejected imports leave no files");
        }

        private static void CheckPlaybackFailure(string root)
        {
            string malformed = Path.Combine(root, "invalid-audio.mp3");
            File.WriteAllText(malformed, "This is a deliberately invalid audio fixture; it cannot produce sound.");
            int callerThread = Thread.CurrentThread.ManagedThreadId;
            int callbackThread = callerThread;
            SoundPlaybackErrorEventArgs reported = null;
            using (ManualResetEvent received = new ManualResetEvent(false))
            using (SoundService player = new SoundService())
            {
                EventHandler<SoundPlaybackErrorEventArgs> handler = delegate(object sender, SoundPlaybackErrorEventArgs error)
                {
                    reported = error;
                    callbackThread = Thread.CurrentThread.ManagedThreadId;
                    received.Set();
                };
                player.PlaybackFailed += handler;
                player.Play(malformed);
                Assert(received.WaitOne(10000), "malformed supported audio reports MCI error");
                player.PlaybackFailed -= handler;
                Assert(reported != null && reported.Sound == malformed && !String.IsNullOrWhiteSpace(reported.Message), "playback error identifies audio and reason");
                Assert(callbackThread != callerThread, "codec operation runs off caller thread");
                player.Stop();
            }
        }

        private static void CheckWaves()
        {
            HashSet<string> ids = new HashSet<string>(StringComparer.Ordinal);
            HashSet<string> names = new HashSet<string>(StringComparer.Ordinal);
            Dictionary<string, double> levels = new Dictionary<string, double>();
            List<byte[]> rendered = new List<byte[]>();
            Assert(SoundService.BuiltInSounds.Length >= 8, "at least eight selectable sound styles");
            foreach (string sound in SoundService.BuiltInSounds)
            {
                Assert(ids.Add(sound) && SoundService.IsBuiltIn(sound), sound + " unique supported identifier");
                string name = SoundService.DisplayName(sound);
                Assert(names.Add(name) && !String.IsNullOrWhiteSpace(name) && name != sound, sound + " distinct readable selection label");
                byte[] wave = SoundService.CreateBuiltInWave(sound);
                Assert(Encoding.ASCII.GetString(wave, 0, 4) == "RIFF" && Encoding.ASCII.GetString(wave, 8, 8) == "WAVEfmt ", sound + " WAV header");
                Assert(BitConverter.ToInt32(wave, 4) == wave.Length - 8 && BitConverter.ToInt32(wave, 40) == wave.Length - 44, sound + " WAV lengths");
                Assert(BitConverter.ToInt16(wave, 20) == 1 && BitConverter.ToInt16(wave, 22) == 1 && BitConverter.ToInt32(wave, 24) == 22050
                    && BitConverter.ToInt32(wave, 28) == 44100 && BitConverter.ToInt16(wave, 32) == 2 && BitConverter.ToInt16(wave, 34) == 16, sound + " PCM format");
                int samples = (wave.Length - 44) / 2;
                double duration = samples / 22050.0;
                int peak = 0;
                int firstAudible = -1;
                int lastAudible = -1;
                int previous = 0;
                int maximumStep = 0;
                double sum = 0;
                for (int sample = 0; sample < samples; sample++)
                {
                    int value = BitConverter.ToInt16(wave, 44 + sample * 2);
                    peak = Math.Max(peak, Math.Abs(value));
                    maximumStep = Math.Max(maximumStep, Math.Abs(value - previous));
                    previous = value;
                    sum += value;
                    if (Math.Abs(value) > Int16.MaxValue * 0.02)
                    {
                        if (firstAudible < 0) firstAudible = sample;
                        lastAudible = sample;
                    }
                }
                double rms = WaveRms(wave, 0, duration);
                levels.Add(sound, rms);
                Assert(duration >= 1.0 && duration <= 3.0, sound + " perceptible duration within three-second resource budget");
                Assert(peak > Int16.MaxValue * 0.40 && peak < Int16.MaxValue * 0.90, sound + " clear peak with clipping headroom");
                Assert(rms >= 0.12 && rms <= 0.55, sound + " audible average level with bounded output");
                Assert((lastAudible - firstAudible) / 22050.0 >= duration * 0.65, sound + " duration contains sound rather than padded silence");
                Assert(Math.Abs(sum / samples / Int16.MaxValue) < 0.005, sound + " no DC offset");
                Assert(maximumStep < Int16.MaxValue * 0.42, sound + " no abrupt full-scale transitions");
                Assert(BitConverter.ToInt16(wave, 44) == 0 && Math.Abs((int)BitConverter.ToInt16(wave, wave.Length - 2)) < 10, sound + " click-free boundaries");
                Assert(WaveRms(wave, 0, 0.001) < 0.05 && WaveRms(wave, duration - 0.001, duration) < 0.005, sound + " faded leading and trailing edges");
                Assert(wave.Length <= 132344, sound + " no more than 130 KiB generated PCM");
                foreach (byte[] earlier in rendered) Assert(!Equal(earlier, wave), sound + " distinct audio from other styles");
                rendered.Add(wave);
                Console.WriteLine("SOUND {0}: {1:F2}s, peak={2:F3}, rms={3:F3}, {4} bytes", sound, duration, peak / (double)Int16.MaxValue, rms, wave.Length);
            }
            Assert(ids.Contains("builtin:soft") && ids.Contains("builtin:bell") && ids.Contains("builtin:wood"), "all legacy selections remain available");
            Assert(levels["builtin:urgent"] >= levels["builtin:soft"] * 1.5, "prominent alert is clearly stronger than the gentle option");
            byte[] urgent = SoundService.CreateBuiltInWave("builtin:urgent");
            byte[] radar = SoundService.CreateBuiltInWave("builtin:radar");
            for (int repeat = 0; repeat < 3; repeat++)
            {
                Assert(WaveRms(urgent, repeat * 0.90, (repeat + 1) * 0.90) >= 0.25, "prominent alert retains energy in repetition " + (repeat + 1));
                Assert(WaveRms(radar, repeat * 0.80, (repeat + 1) * 0.80) >= 0.20, "radar retains energy in repetition " + (repeat + 1));
            }
            Expect<ArgumentException>(delegate { SoundService.CreateBuiltInWave("builtin:unknown"); }, "unknown synthesized style rejected");
            using (SoundService sound = new SoundService())
            {
                sound.Stop();
                Expect<ArgumentException>(delegate { sound.Play("builtin:unknown"); }, "invalid playback rejected synchronously");
                sound.Dispose();
                Expect<ObjectDisposedException>(delegate { sound.Play("builtin:soft"); }, "disposed player rejected");
            }
        }

        private static double WaveRms(byte[] wave, double startSeconds, double endSeconds)
        {
            int start = Math.Max(0, (int)(startSeconds * 22050));
            int end = Math.Min((wave.Length - 44) / 2, (int)(endSeconds * 22050));
            double energy = 0;
            for (int sample = start; sample < end; sample++)
            {
                double value = BitConverter.ToInt16(wave, 44 + sample * 2) / (double)Int16.MaxValue;
                energy += value * value;
            }
            return Math.Sqrt(energy / Math.Max(1, end - start));
        }

        private static void CheckBuiltInSettings(string root)
        {
            SettingsStore store = new SettingsStore(Path.Combine(root, "built-in-settings"));
            AppSettings settings = new AppSettings();
            foreach (string sound in SoundService.BuiltInSounds)
                settings.Contacts.Add(new ContactRule { Name = SoundService.DisplayName(sound), Sound = sound });
            store.Save(settings);
            AppSettings loaded = store.Load();
            Assert(loaded.Contacts.Count == settings.Contacts.Count, "all sound selections persist");
            for (int i = 0; i < loaded.Contacts.Count; i++)
                Assert(loaded.Contacts[i].Sound == settings.Contacts[i].Sound, "sound choice survives settings roundtrip " + i);
        }

        private static bool Equal(byte[] first, byte[] second)
        {
            if (first.Length != second.Length) return false;
            for (int i = 0; i < first.Length; i++) if (first[i] != second[i]) return false;
            return true;
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition) throw new Exception(message);
            checks++;
        }

        private static void Expect<T>(Action action, string message) where T : Exception
        {
            try { action(); }
            catch (T) { checks++; return; }
            throw new Exception(message + ": expected " + typeof(T).Name);
        }
    }
}
