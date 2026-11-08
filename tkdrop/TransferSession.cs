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
        /// <summary>Sends all items over an already-connected stream. Throws on any failure.</summary>
        public static void Send(Stream stream, string senderName, IList<SendItem> items, Action<TransferProgress> onProgress, CancellationToken cancel)
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
                    using (var file = new FileStream(item.LocalPath, FileMode.Open, FileAccess.Read, FileShare.Read, Protocol.BufferSize))
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
                    var text = ReadBoundedString(r, Protocol.MaxTextBytes);
                    w.Write(Protocol.AckOk);
                    w.Flush();
                    onItem(new ReceivedItem { IsText = true, Text = text, Size = Protocol.Utf8.GetByteCount(text), SenderName = sender });
                    continue;
                }

                if (kind != Protocol.KindFile)
                    throw new InvalidDataException("알 수 없는 항목 종류: " + kind);

                var rawName = ReadBoundedString(r, Protocol.MaxNameBytes);
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

        private static string ReadBoundedString(BinaryReader r, int maxBytes)
        {
            // BinaryReader.ReadString uses a 7-bit encoded byte length prefix; peek it to enforce the bound.
            int length = Read7BitEncodedInt(r);
            if (length < 0 || length > maxBytes) throw new InvalidDataException("문자열 길이가 허용 범위를 벗어났습니다: " + length);
            var bytes = r.ReadBytes(length);
            if (bytes.Length != length) throw new EndOfStreamException();
            return Protocol.Utf8.GetString(bytes);
        }

        private static int Read7BitEncodedInt(BinaryReader r)
        {
            int count = 0, shift = 0;
            byte b;
            do
            {
                if (shift == 5 * 7) throw new FormatException("잘못된 7비트 길이 접두사");
                b = r.ReadByte();
                count |= (b & 0x7F) << shift;
                shift += 7;
            } while ((b & 0x80) != 0);
            return count;
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
