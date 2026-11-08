using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using InTheHand.Net;

namespace TkDrop
{
    /// <summary>
    /// "tkdrop.exe --selftest [report.txt]" : exercises everything that does not need Bluetooth hardware
    /// (protocol round-trip over TCP loopback, name sanitising, embedded assembly loading).
    /// Exit code 0 = all passed. Used by the GitHub Actions build.
    /// </summary>
    public static class SelfTest
    {
        public static int Run(string reportPath)
        {
            var log = new StringBuilder();
            int failed = 0;
            Action<string, Func<bool>> test = (name, fn) =>
            {
                try
                {
                    bool ok = fn();
                    log.AppendLine((ok ? "PASS " : "FAIL ") + name);
                    if (!ok) failed++;
                }
                catch (Exception ex)
                {
                    log.AppendLine("FAIL " + name + " : " + ex);
                    failed++;
                }
            };

            test("embedded 32feet assembly loads", () =>
            {
                var asm = typeof(BluetoothAddress).Assembly;
                log.AppendLine("     32feet " + asm.GetName().Version + " location='" + asm.Location + "'");
                return asm.GetName().Name == "InTheHand.Net.Bluetooth";
            });

            test("BluetoothAddress parse/format round trip", () =>
            {
                BluetoothAddress a;
                if (!BtLink.TryParseAddress("00:11:22:33:44:55", out a)) return false;
                var text = BtLink.FormatAddress(a);
                log.AppendLine("     formatted='" + text + "'");
                BluetoothAddress b;
                return BtLink.TryParseAddress(text, out b) && a == b && text.ToUpperInvariant() == "00:11:22:33:44:55";
            });

            test("radio query does not crash", () =>
            {
                log.AppendLine("     radio present=" + BtLink.RadioPresent());
                return true;
            });

            var sanitizeCases = new[,]
            {
                { "../../evil.txt", "evil.txt" },
                { "a/b\\c.txt", Path.Combine("a", Path.Combine("b", "c.txt")) },
                { "con.txt", "_con.txt" },
                { "  ", "file" },
                { "x<y>:z?.txt", "x_y__z_.txt" },
                { "한글 파일.txt", "한글 파일.txt" },
                { "trailing. ", "trailing" },
                { "/abs/path.txt", Path.Combine("abs", "path.txt") },
            };
            for (int i = 0; i < sanitizeCases.GetLength(0); i++)
            {
                string input = sanitizeCases[i, 0], expected = sanitizeCases[i, 1];
                test("sanitize '" + input + "'", () =>
                {
                    var actual = Protocol.SanitizeRelativePath(input);
                    if (actual != expected) log.AppendLine("     expected '" + expected + "' got '" + actual + "'");
                    return actual == expected;
                });
            }

            test("protocol round trip over loopback", () => RoundTrip(log));

            test("checksum mismatch is detected", () => CorruptedTransfer(log));

            var report = log.ToString() + (failed == 0 ? "ALL PASSED" : failed + " FAILED") + Environment.NewLine;
            if (!string.IsNullOrEmpty(reportPath))
            {
                try { File.WriteAllText(reportPath, report, Encoding.UTF8); } catch { }
            }
            return failed == 0 ? 0 : 1;
        }

        private static bool RoundTrip(StringBuilder log)
        {
            var temp = Path.Combine(Path.GetTempPath(), "tkdrop-selftest-" + Guid.NewGuid().ToString("N"));
            var srcDir = Path.Combine(temp, "src");
            var dstDir = Path.Combine(temp, "dst");
            Directory.CreateDirectory(Path.Combine(srcDir, "folder", "sub"));
            Directory.CreateDirectory(dstDir);
            try
            {
                var rnd = new Random(1234);
                var big = new byte[700 * 1024 + 13];
                rnd.NextBytes(big);
                File.WriteAllBytes(Path.Combine(srcDir, "big.bin"), big);
                File.WriteAllBytes(Path.Combine(srcDir, "empty.bin"), new byte[0]);
                File.WriteAllText(Path.Combine(srcDir, "folder", "sub", "한글 노트.txt"), "안녕하세요", Encoding.UTF8);
                File.WriteAllText(Path.Combine(dstDir, "big.bin"), "pre-existing"); // forces "big (1).bin"

                var items = new List<SendItem>();
                items.AddRange(SendItem.FromPath(Path.Combine(srcDir, "big.bin")));
                items.AddRange(SendItem.FromPath(Path.Combine(srcDir, "empty.bin")));
                items.AddRange(SendItem.FromPath(Path.Combine(srcDir, "folder")));
                items.Add(SendItem.FromText("clipboard 텍스트 ✓"));

                var received = new List<ReceivedItem>();
                string sender = null;
                Exception receiveError = null;

                RunPair(
                    serverStream => { sender = TransferSession.Receive(serverStream, dstDir, received.Add, null, CancellationToken.None); },
                    clientStream => { TransferSession.Send(clientStream, "TESTER", items, null, CancellationToken.None); },
                    out receiveError);

                if (receiveError != null) { log.AppendLine("     receive error: " + receiveError); return false; }
                if (sender != "TESTER") { log.AppendLine("     sender name mismatch: " + sender); return false; }
                if (received.Count != 4) { log.AppendLine("     received count " + received.Count); return false; }

                var bigPath = Path.Combine(dstDir, "big (1).bin");
                if (!File.Exists(bigPath)) { log.AppendLine("     missing " + bigPath); return false; }
                var got = File.ReadAllBytes(bigPath);
                if (!Protocol.HashEquals(got, big)) { log.AppendLine("     big.bin content differs"); return false; }

                if (new FileInfo(Path.Combine(dstDir, "empty.bin")).Length != 0) return false;

                var nested = Path.Combine(dstDir, "folder", "sub", "한글 노트.txt");
                if (File.ReadAllText(nested, Encoding.UTF8) != "안녕하세요") { log.AppendLine("     nested text differs"); return false; }

                var text = received.Find(r => r.IsText);
                if (text == null || text.Text != "clipboard 텍스트 ✓") return false;

                foreach (var f in Directory.GetFiles(dstDir, "*.tkdrop-part", SearchOption.AllDirectories))
                {
                    log.AppendLine("     leftover part file " + f);
                    return false;
                }
                return true;
            }
            finally
            {
                try { Directory.Delete(temp, true); } catch { }
            }
        }

        private static bool CorruptedTransfer(StringBuilder log)
        {
            var temp = Path.Combine(Path.GetTempPath(), "tkdrop-selftest-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temp);
            try
            {
                var dst = Path.Combine(temp, "dst");
                Directory.CreateDirectory(dst);
                var payload = Encoding.UTF8.GetBytes("hello world, this is a corrupted transfer test");
                Exception receiveError = null;
                Exception sendError = null;

                RunPair(
                    serverStream => { TransferSession.Receive(serverStream, dst, _ => { }, null, CancellationToken.None); },
                    clientStream =>
                    {
                        try
                        {
                            var w = new BinaryWriter(clientStream, Protocol.Utf8, true);
                            var r = new BinaryReader(clientStream, Protocol.Utf8, true);
                            Protocol.WriteHello(w, "BAD");
                            w.Write(Protocol.KindFile);
                            w.Write("bad.txt");
                            w.Write((long)payload.Length);
                            w.Flush();
                            clientStream.Write(payload, 0, payload.Length);
                            clientStream.Write(new byte[32], 0, 32); // wrong hash
                            clientStream.Flush();
                            byte ack = r.ReadByte();
                            if (ack != Protocol.AckChecksum) throw new Exception("expected checksum ack, got " + ack);
                        }
                        catch (Exception ex) { sendError = ex; }
                    },
                    out receiveError);

                if (sendError != null) { log.AppendLine("     sender side: " + sendError.Message); return false; }
                if (receiveError == null) { log.AppendLine("     receiver accepted corrupted file"); return false; }
                if (Directory.GetFiles(dst, "*", SearchOption.AllDirectories).Length != 0) { log.AppendLine("     corrupted file left on disk"); return false; }
                return true;
            }
            finally
            {
                try { Directory.Delete(temp, true); } catch { }
            }
        }

        /// <summary>Runs server and client bodies over a TCP loopback connection, like the RFCOMM NetworkStream.</summary>
        private static void RunPair(Action<Stream> server, Action<Stream> client, out Exception serverError)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            Exception error = null;

            var serverThread = new Thread(() =>
            {
                try
                {
                    using (var sock = listener.AcceptTcpClient())
                    using (var stream = sock.GetStream())
                    {
                        stream.ReadTimeout = 10000;
                        server(stream);
                    }
                }
                catch (Exception ex) { error = ex; }
            }) { IsBackground = true };
            serverThread.Start();

            try
            {
                using (var sock = new TcpClient())
                {
                    sock.Connect(IPAddress.Loopback, port);
                    using (var stream = sock.GetStream())
                    {
                        stream.ReadTimeout = 10000;
                        client(stream);
                    }
                }
            }
            finally
            {
                serverThread.Join(15000);
                listener.Stop();
            }
            serverError = error;
        }
    }
}
