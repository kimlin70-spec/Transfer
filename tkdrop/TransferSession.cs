using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace TkDrop
{
    /// <summary>Progress snapshot for the UI. Bytes are cumulative over the whole session.</summary>
    public sealed class TransferProgress
    {
        public string CurrentItem;
        public int ItemIndex;
        public int ItemCount;
        public long BytesDone;
        public long BytesTotal;
        /// <summary>Items that could not be opened locally and were skipped (display name + reason).</summary>
        public readonly List<string> Skipped = new List<string>();
        public int Percent { get { return BytesTotal <= 0 ? 100 : (int)Math.Min(100, BytesDone * 100 / BytesTotal); } }
    }

    /// <summary>What a receiving session produced; raised once per item so the tray can notify.</summary>
    public sealed class ReceivedItem
    {
        public bool IsText;
        public string Text;
        public string SavedPath;
        public long Size;
        public string SenderName;
    }

    /// <summary>
    /// Stream-level protocol logic, independent of Bluetooth so it can be exercised over any Stream
    /// (the self-test runs it over an in-process pipe).
    /// </summary>
    public static class TransferSession
    {
        /// <summary>
        /// Sends all items over an already-connected stream. Files that cannot be opened locally are skipped
        /// (listed in the returned progress) so the rest of the batch still arrives. Throws on transport failure.
        /// </summary>
        public static TransferProgress Send(Stream stream, string senderName, IList<SendItem> items, Action<TransferProgress> onProgress, CancellationToken cancel)
        {
            var progress = new TransferProgress { ItemCount = items.Count, BytesTotal = SendItem.TotalSize(items) };
            var w = new BinaryWriter(stream, Protocol.Utf8, leaveOpen: true);
            var r = new BinaryReader(stream, Protocol.Utf8, leaveOpen: true);

            Protocol.WriteHello(w, senderName);

            var buffer = new byte[Protocol.BufferSize];
            for (int i = 0; i < items.Count; i++)
            {
                cancel.ThrowIfCancellationRequested();
                var item = items[i];
                progress.ItemIndex = i + 1;
                progress.CurrentItem = item.Display;
                Report(onProgress, progress);

                if (item.IsText)
                {
                    w.Write(Protocol.KindText);
                    w.Write(item.Text);
                    w.Flush();
                    progress.BytesDone += item.Size;
                }
                else
                {
                    FileStream file;
                    try
                    {
                        // ReadWrite|Delete sharing so documents currently open in Word/Excel/etc. can still be sent.
                        file = new FileStream(item.LocalPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, Protocol.BufferSize);
                    }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is NotSupportedException)
                    {
                        // Local problem with this one file: skip it, keep the session alive for the others.
                        progress.Skipped.Add(item.Display + " - " + DescribeOpenError(ex));
                        progress.BytesTotal -= item.Size;
                        Report(onProgress, progress);
                        continue;
                    }

                    using (file)
                    using (var hash = Protocol.NewHash())
                    {
                        long size = file.Length;
                        w.Write(Protocol.KindFile);
                        w.Write(item.RelativeName);
                        w.Write(size);
                        w.Flush();

                        long remaining = size;
                        while (remaining > 0)
                        {
                            cancel.ThrowIfCancellationRequested();
                            int want = (int)Math.Min(buffer.Length, remaining);
                            int n = file.Read(buffer, 0, want);
                            if (n <= 0) throw new IOException("파일을 읽는 중 끝에 도달했습니다 (파일이 전송 중 바뀌었을 수 있음): " + item.LocalPath);
                            stream.Write(buffer, 0, n);
                            hash.AppendData(buffer, 0, n);
                            remaining -= n;
                            progress.BytesDone += n;
                            Report(onProgress, progress);
                        }
                        stream.Write(hash.GetHashAndReset(), 0, 32);
                        stream.Flush();
                    }
                }

                ExpectAck(r, item.Display);
                Report(onProgress, progress);
            }

            w.Write(Protocol.KindEnd);
            w.Flush();
            ExpectAck(r, "종료");
            return progress;
        }

        private static string DescribeOpenError(Exception ex)
        {
            if (ex is FileNotFoundException || ex is DirectoryNotFoundException) return "파일을 찾을 수 없음";
            if (ex is UnauthorizedAccessException) return "접근 권한 없음";
            var io = ex as IOException;
            if (io != null && (io.HResult & 0xFFFF) == 32) return "다른 프로그램이 사용 중"; // ERROR_SHARING_VIOLATION
            return ex.Message;
        }

        private static void ExpectAck(BinaryReader r, string what)
        {
            byte ack;
            try { ack = r.ReadByte(); }
            catch (EndOfStreamException) { throw new IOException("상대가 응답하지 않고 연결을 끊었습니다: " + what); }

            switch (ack)
            {
                case Protocol.AckOk: return;
                case Protocol.AckChecksum: throw new IOException("전송 데이터 검증(SHA-256)에 실패했습니다: " + what);
                default: throw new IOException("상대가 전송을 거부했습니다: " + what);
            }
        }

        /// <summary>
        /// Receives a whole session from an already-connected stream and saves into <paramref name="saveFolder"/>.
        /// <paramref name="onItem"/> is called after each item completes. Returns the sender's name.
        /// </summary>
        public static string Receive(Stream stream, string saveFolder, Action<ReceivedItem> onItem, Action<TransferProgress> onProgress, CancellationToken cancel)
        {
            var r = new BinaryReader(stream, Protocol.Utf8, leaveOpen: true);
            var w = new BinaryWriter(stream, Protocol.Utf8, leaveOpen: true);

            var sender = Protocol.ReadHello(r);
            Directory.CreateDirectory(saveFolder);

            var buffer = new byte[Protocol.BufferSize];
            var progress = new TransferProgress { ItemCount = 0 };
            int index = 0;

            while (true)
            {
                cancel.ThrowIfCancellationRequested();
                byte kind;
                try { kind = r.ReadByte(); }
                catch (EndOfStreamException) { throw new IOException("상대가 전송을 끝내지 않고 연결을 끊었습니다."); }

                if (kind == Protocol.KindEnd)
                {
                    w.Write(Protocol.AckOk);
                    w.Flush();
                    return sender;
                }

                index++;
                if (kind == Protocol.KindText)
                {
                    var text = Protocol.ReadBoundedString(r, Protocol.MaxTextBytes);
                    w.Write(Protocol.AckOk);
                    w.Flush();
                    onItem(new ReceivedItem { IsText = true, Text = text, Size = Protocol.Utf8.GetByteCount(text), SenderName = sender });
                    continue;
                }

                if (kind != Protocol.KindFile)
                    throw new InvalidDataException("알 수 없는 항목 종류: " + kind);

                var rawName = Protocol.ReadBoundedString(r, Protocol.MaxNameBytes);
                long size = r.ReadInt64();
                if (size < 0) throw new InvalidDataException("잘못된 파일 크기: " + size);

                var relative = Protocol.SanitizeRelativePath(rawName);
                var finalPath = Protocol.UniquePath(saveFolder, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(finalPath));
                var partPath = finalPath + ".tkdrop-part";

                progress.ItemIndex = index;
                progress.CurrentItem = relative;
                progress.BytesTotal = size;
                progress.BytesDone = 0;
                Report(onProgress, progress);

                byte[] expected;
                bool ok;
                try
                {
                    using (var file = new FileStream(partPath, FileMode.Create, FileAccess.Write, FileShare.None, Protocol.BufferSize))
                    using (var hash = Protocol.NewHash())
                    {
                        long remaining = size;
                        while (remaining > 0)
                        {
                            cancel.ThrowIfCancellationRequested();
                            int want = (int)Math.Min(buffer.Length, remaining);
                            int n = stream.Read(buffer, 0, want);
                            if (n <= 0) throw new IOException("파일 수신 중 연결이 끊겼습니다: " + relative);
                            file.Write(buffer, 0, n);
                            hash.AppendData(buffer, 0, n);
                            remaining -= n;
                            progress.BytesDone += n;
                            Report(onProgress, progress);
                        }
                        expected = ReadExactly(stream, 32);
                        ok = Protocol.HashEquals(expected, hash.GetHashAndReset());
                    }
                }
                catch
                {
                    TryDelete(partPath);
                    throw;
                }

                if (!ok)
                {
                    TryDelete(partPath);
                    w.Write(Protocol.AckChecksum);
                    w.Flush();
                    throw new IOException("수신 데이터 검증(SHA-256)에 실패했습니다: " + relative);
                }

                // Another file may have appeared meanwhile; re-check uniqueness right before the rename.
                if (File.Exists(finalPath)) finalPath = Protocol.UniquePath(saveFolder, relative);
                File.Move(partPath, finalPath);

                w.Write(Protocol.AckOk);
                w.Flush();
                onItem(new ReceivedItem { SavedPath = finalPath, Size = size, SenderName = sender });
            }
        }

        private static byte[] ReadExactly(Stream s, int count)
        {
            var buf = new byte[count];
            int read = 0;
            while (read < count)
            {
                int n = s.Read(buf, read, count - read);
                if (n <= 0) throw new IOException("체크섬 수신 중 연결이 끊겼습니다.");
                read += n;
            }
            return buf;
        }

        private static void Report(Action<TransferProgress> cb, TransferProgress p)
        {
            if (cb != null) cb(p);
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }
    }
}
