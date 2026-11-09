using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace TkDrop
{
    /// <summary>
    /// Wire format (both ends are .NET on Windows, so BinaryWriter little-endian framing is used as-is):
    ///
    ///   Hello   : "TKDP" (4 bytes) | version (byte) | sender name (BinaryWriter string)
    ///   Item    : kind (byte)  1 = file, 2 = text, 0 = end of session
    ///     file  : relative name (string) | size (int64) | data | SHA-256 (32 bytes)   -> ack (byte)
    ///     text  : text (string)                                                      -> ack (byte)
    ///     end   : (nothing)                                                          -> ack (byte)
    ///   Ack     : 1 = ok, 2 = checksum mismatch, 3 = rejected / error
    /// </summary>
    public static class Protocol
    {
        /// <summary>RFCOMM service UUID both laptops listen on. Changing it makes old builds incompatible.</summary>
        public static readonly Guid ServiceId = new Guid("a4f1c0de-7d0b-4c6a-9e2f-3b5d8a1c0f42");
        public const string ServiceName = "tkdrop";

        public static readonly byte[] Magic = { (byte)'T', (byte)'K', (byte)'D', (byte)'P' };
        public const byte Version = 1;

        public const byte KindEnd = 0;
        public const byte KindFile = 1;
        public const byte KindText = 2;

        public const byte AckOk = 1;
        public const byte AckChecksum = 2;
        public const byte AckRejected = 3;

        /// <summary>RFCOMM frames are small; 32 KiB keeps syscall count low without hurting progress updates.</summary>
        public const int BufferSize = 32 * 1024;

        /// <summary>Upper bound for a single text message (1 MiB of UTF-8).</summary>
        public const int MaxTextBytes = 1024 * 1024;

        /// <summary>Upper bound for a relative file name on the wire.</summary>
        public const int MaxNameBytes = 4096;

        /// <summary>Longest single path segment we create (keeps Desktop\tkdrop\... under MAX_PATH with room to spare).</summary>
        public const int MaxSegmentChars = 120;

        public static readonly Encoding Utf8 = new UTF8Encoding(false);

        public static void WriteHello(BinaryWriter w, string senderName)
        {
            w.Write(Magic);
            w.Write(Version);
            w.Write(senderName ?? "");
            w.Flush();
        }

        /// <summary>Reads and validates the hello header; returns the sender's name.</summary>
        public static string ReadHello(BinaryReader r)
        {
            var magic = r.ReadBytes(4);
            if (magic.Length != 4 || magic[0] != Magic[0] || magic[1] != Magic[1] || magic[2] != Magic[2] || magic[3] != Magic[3])
                throw new InvalidDataException("tkdrop 프로토콜이 아닌 연결입니다.");
            var version = r.ReadByte();
            if (version != Version)
                throw new InvalidDataException("상대 tkdrop 버전이 달라 통신할 수 없습니다. (상대 v" + version + ", 나 v" + Version + ")");
            return r.ReadString();
        }

        /// <summary>
        /// Turns a relative name received from the peer into a safe relative path:
        /// each segment is cleaned of invalid characters, "." / ".." / empty segments are dropped,
        /// and the result is never rooted.
        /// </summary>
        public static string SanitizeRelativePath(string name)
        {
            if (name == null) name = "";
            var parts = name.Replace('\\', '/').Split('/');
            // Explicit Windows set (not Path.GetInvalidFileNameChars) so behaviour is identical everywhere.
            var invalid = new[] { '<', '>', ':', '"', '/', '\\', '|', '?', '*' };
            var sb = new StringBuilder();
            foreach (var raw in parts)
            {
                var seg = raw.Trim();
                if (seg.Length == 0 || seg == "." || seg == "..") continue;
                var cleaned = new StringBuilder(seg.Length);
                foreach (var ch in seg)
                    cleaned.Append(Array.IndexOf(invalid, ch) >= 0 || ch < 32 ? '_' : ch);
                var final = cleaned.ToString().TrimEnd('.', ' ');
                if (final.Length == 0) final = "_";
                if (IsReservedDeviceName(final)) final = "_" + final;
                final = Shorten(final, MaxSegmentChars);
                if (sb.Length > 0) sb.Append(Path.DirectorySeparatorChar);
                sb.Append(final);
            }
            return sb.Length == 0 ? "file" : sb.ToString();
        }

        /// <summary>Trims an over-long name while keeping its extension (up to 20 chars of it).</summary>
        private static string Shorten(string name, int max)
        {
            if (name.Length <= max) return name;
            var ext = Path.GetExtension(name);
            if (ext.Length > 20) ext = "";
            var stem = name.Substring(0, name.Length - ext.Length);
            int keep = Math.Max(1, max - ext.Length);
            if (stem.Length > keep) stem = stem.Substring(0, keep);
            // Do not cut in the middle of a surrogate pair.
            if (stem.Length > 0 && char.IsHighSurrogate(stem[stem.Length - 1])) stem = stem.Substring(0, stem.Length - 1);
            return stem.TrimEnd('.', ' ') + ext;
        }

        private static bool IsReservedDeviceName(string name)
        {
            var stem = name;
            int dot = stem.IndexOf('.');
            if (dot >= 0) stem = stem.Substring(0, dot);
            switch (stem.ToUpperInvariant())
            {
                case "CON": case "PRN": case "AUX": case "NUL":
                case "COM1": case "COM2": case "COM3": case "COM4": case "COM5": case "COM6": case "COM7": case "COM8": case "COM9":
                case "LPT1": case "LPT2": case "LPT3": case "LPT4": case "LPT5": case "LPT6": case "LPT7": case "LPT8": case "LPT9":
                    return true;
            }
            return false;
        }

        /// <summary>Returns a path in <paramref name="folder"/> that does not exist yet: name.ext, name (1).ext, ...</summary>
        public static string UniquePath(string folder, string relativePath)
        {
            var candidate = Path.Combine(folder, relativePath);
            if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;

            var dir = Path.GetDirectoryName(candidate);
            var stem = Path.GetFileNameWithoutExtension(candidate);
            var ext = Path.GetExtension(candidate);
            for (int i = 1; i < 10000; i++)
            {
                var p = Path.Combine(dir, stem + " (" + i + ")" + ext);
                if (!File.Exists(p) && !Directory.Exists(p)) return p;
            }
            return Path.Combine(dir, stem + " (" + Guid.NewGuid().ToString("N") + ")" + ext);
        }

        public static string FormatSize(long bytes)
        {
            if (bytes < 1024) return bytes + " B";
            if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("0.#") + " KB";
            if (bytes < 1024L * 1024 * 1024) return (bytes / (1024.0 * 1024)).ToString("0.#") + " MB";
            return (bytes / (1024.0 * 1024 * 1024)).ToString("0.##") + " GB";
        }

        public static IncrementalHash NewHash()
        {
            return IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        }

        public static bool HashEquals(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            int diff = 0;
            for (int i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
            return diff == 0;
        }
    }
}
