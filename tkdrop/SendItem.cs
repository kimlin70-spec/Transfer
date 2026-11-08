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
            if (File.Exists(path))
            {
                yield return FromFile(path, Path.GetFileName(path));
                yield break;
            }
            if (!Directory.Exists(path)) yield break;

            var root = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var rootName = Path.GetFileName(root);
            if (string.IsNullOrEmpty(rootName)) rootName = "folder";

            var found = new List<SendItem>();
            try
            {
                foreach (var f in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                {
                    var rel = f.Substring(root.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    found.Add(FromFile(f, rootName + "/" + rel.Replace('\\', '/')));
                }
            }
            catch (UnauthorizedAccessException) { }
            catch (IOException) { }

            foreach (var item in found) yield return item;
        }

        public static long TotalSize(IEnumerable<SendItem> items)
        {
            long total = 0;
            foreach (var i in items) total += i.Size;
            return total;
        }
    }
}
