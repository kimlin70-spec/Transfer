using System;
using System.Collections.Generic;
using System.IO;

namespace TkDrop
{
    /// <summary>One thing to send: a file (with the relative name the receiver should use) or a text snippet.</summary>
    public sealed class SendItem
    {
        public bool IsText { get; private set; }
        public string Text { get; private set; }
        public string LocalPath { get; private set; }
        /// <summary>Name the receiver saves under; may contain '/' separators when a folder was dropped.</summary>
        public string RelativeName { get; private set; }
        public long Size { get; private set; }

        public string Display
        {
            get { return IsText ? "텍스트 (" + Protocol.FormatSize(Size) + ")" : RelativeName.Replace('/', '\\'); }
        }

        public static SendItem FromText(string text)
        {
            return new SendItem { IsText = true, Text = text, Size = Protocol.Utf8.GetByteCount(text) };
        }

        public static SendItem FromFile(string path, string relativeName)
        {
            var info = new FileInfo(path);
            return new SendItem { LocalPath = info.FullName, RelativeName = relativeName, Size = info.Length };
        }

        /// <summary>Expands a file or folder path into send items (folders recursively, keeping relative names).</summary>
        public static IEnumerable<SendItem> FromPath(string path)
        {
            return FromPath(path, null);
        }

        /// <summary>
        /// Like <see cref="FromPath(string)"/>; folders/files that could not be read are appended to
        /// <paramref name="skipped"/> (full paths) instead of silently truncating the walk.
        /// Reparse points (junctions, symlinks) are not followed.
        /// </summary>
        public static List<SendItem> FromPath(string path, List<string> skipped)
        {
            var result = new List<SendItem>();
            if (File.Exists(path))
            {
                try { result.Add(FromFile(path, Path.GetFileName(path))); }
                catch (Exception) { if (skipped != null) skipped.Add(path); }
                return result;
            }
            if (!Directory.Exists(path)) { if (skipped != null) skipped.Add(path); return result; }

            var full = Path.GetFullPath(path);
            var root = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (root.Length == 0 || root.EndsWith(":")) root = full; // drive root such as C:\ must keep its separator
            var rootName = Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar));
            if (string.IsNullOrEmpty(rootName)) rootName = root.Replace(":", "").Replace("\\", "").Replace("/", "");
            if (string.IsNullOrEmpty(rootName)) rootName = "folder";

            var pending = new Stack<string>();
            pending.Push(root);
            while (pending.Count > 0)
            {
                var dir = pending.Pop();
                try
                {
                    foreach (var f in Directory.EnumerateFiles(dir))
                    {
                        try { result.Add(FromFile(f, rootName + "/" + Relative(root, f))); }
                        catch (Exception) { if (skipped != null) skipped.Add(f); }
                    }
                }
                catch (Exception) { if (skipped != null) skipped.Add(dir); continue; }

                try
                {
                    foreach (var d in Directory.EnumerateDirectories(dir))
                    {
                        bool reparse = false;
                        try { reparse = (new DirectoryInfo(d).Attributes & FileAttributes.ReparsePoint) != 0; } catch { }
                        if (reparse) { if (skipped != null) skipped.Add(d); continue; }
                        pending.Push(d);
                    }
                }
                catch (Exception) { if (skipped != null) skipped.Add(dir); }
            }
            result.Sort((a, b) => string.CompareOrdinal(a.RelativeName, b.RelativeName));
            return result;
        }

        private static string Relative(string root, string file)
        {
            var rel = file.Substring(root.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return rel.Replace('\\', '/');
        }

        public static long TotalSize(IEnumerable<SendItem> items)
        {
            long total = 0;
            foreach (var i in items) total += i.Size;
            return total;
        }
    }
}
