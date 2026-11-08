using System;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;

namespace TkDrop
{
    /// <summary>
    /// Portable settings stored as JSON next to the exe (falls back to %APPDATA%\tkdrop when
    /// the exe folder is read-only, e.g. Program Files).
    /// </summary>
    [DataContract]
    public sealed class AppSettings
    {
        /// <summary>Peer laptop Bluetooth address, "AA:BB:CC:DD:EE:FF". Empty = not chosen yet.</summary>
        [DataMember(Order = 1)] public string PeerAddress { get; set; }
        [DataMember(Order = 2)] public string PeerName { get; set; }
        /// <summary>Folder for received files. Empty = Desktop\tkdrop.</summary>
        [DataMember(Order = 3)] public string SaveFolder { get; set; }

        public static readonly string FileName = "tkdrop.settings.json";

        public string ResolvedSaveFolder
        {
            get
            {
                if (!string.IsNullOrWhiteSpace(SaveFolder)) return SaveFolder;
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "tkdrop");
            }
        }

        public bool HasPeer { get { return !string.IsNullOrWhiteSpace(PeerAddress); } }

        public string PeerDisplay
        {
            get
            {
                if (!HasPeer) return "(선택 안 됨)";
                return string.IsNullOrWhiteSpace(PeerName) ? PeerAddress : PeerName + "  " + PeerAddress;
            }
        }

        public static string ExeDirectory
        {
            get { return AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar); }
        }

        private static string AppDataPath
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), Program.AppName, FileName); }
        }

        private static string ExePath
        {
            get { return Path.Combine(ExeDirectory, FileName); }
        }

        public static AppSettings Load()
        {
            foreach (var path in new[] { ExePath, AppDataPath })
            {
                try
                {
                    if (!File.Exists(path)) continue;
                    using (var fs = File.OpenRead(path))
                    {
                        var ser = new DataContractJsonSerializer(typeof(AppSettings));
                        var s = (AppSettings)ser.ReadObject(fs);
                        if (s != null) return s;
                    }
                }
                catch
                {
                    // Corrupt or unreadable file: fall through to defaults.
                }
            }
            return new AppSettings();
        }

        public void Save()
        {
            var ser = new DataContractJsonSerializer(typeof(AppSettings));
            byte[] json;
            using (var ms = new MemoryStream())
            {
                ser.WriteObject(ms, this);
                json = ms.ToArray();
            }

            Exception last = null;
            foreach (var path in new[] { ExePath, AppDataPath })
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    var tmp = path + ".tmp";
                    File.WriteAllBytes(tmp, json);
                    if (File.Exists(path)) File.Replace(tmp, path, null); else File.Move(tmp, path);
                    return;
                }
                catch (Exception ex)
                {
                    last = ex;
                }
            }
            throw new IOException("설정을 저장할 수 없습니다.", last);
        }
    }
}
