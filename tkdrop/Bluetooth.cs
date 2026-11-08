using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using InTheHand.Net;
using InTheHand.Net.Bluetooth;
using InTheHand.Net.Sockets;

namespace TkDrop
{
    /// <summary>A paired device as shown in the peer picker.</summary>
    public sealed class PairedDevice
    {
        public string Name;
        public string Address;
        public bool IsComputer;
        public override string ToString()
        {
            return (string.IsNullOrEmpty(Name) ? "(이름 없음)" : Name) + "   " + Address + (IsComputer ? "" : "   (PC 아님)");
        }
    }

    public static class BtLink
    {
        public static string FormatAddress(BluetoothAddress address)
        {
            return address.ToString("C"); // AA:BB:CC:DD:EE:FF
        }

        public static bool TryParseAddress(string text, out BluetoothAddress address)
        {
            address = null;
            if (string.IsNullOrWhiteSpace(text)) return false;
            return BluetoothAddress.TryParse(text.Trim(), out address) && address != BluetoothAddress.None;
        }

        /// <summary>True when a Bluetooth radio is present and switched on (Windows stops enumerating a powered-off radio).</summary>
        public static bool RadioPresent()
        {
            return RadioAvailable();
        }

        /// <summary>Lists devices paired with this PC, computers first. Blocking; call off the UI thread.</summary>
        public static List<PairedDevice> ListPairedDevices()
        {
            var result = new List<PairedDevice>();
            using (var client = new BluetoothClient())
            {
                foreach (var d in client.PairedDevices)
                {
                    bool isComputer = false;
                    try { isComputer = d.ClassOfDevice != null && d.ClassOfDevice.MajorDevice == DeviceClass.Computer; } catch { }
                    result.Add(new PairedDevice { Name = d.DeviceName, Address = FormatAddress(d.DeviceAddress), IsComputer = isComputer });
                }
            }
            result.Sort((a, b) =>
            {
                if (a.IsComputer != b.IsComputer) return a.IsComputer ? -1 : 1;
                return string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase);
            });
            return result;
        }

        /// <summary>
        /// Connects to the peer's tkdrop service and runs the send session. Blocking; run on a worker thread.
        /// </summary>
        public const int ConnectTimeoutMs = 25000;

        public static void SendTo(string peerAddress, IList<SendItem> items, Action<string> onStatus, Action<TransferProgress> onProgress, CancellationToken cancel)
        {
            BluetoothAddress address;
            if (!TryParseAddress(peerAddress, out address))
                throw new InvalidOperationException("상대 노트북이 선택되지 않았습니다. 트레이 메뉴에서 '상대 노트북 선택'을 먼저 해주세요.");

            var client = new BluetoothClient();
            try
            {
                client.Authenticate = true;
                client.Encrypt = true;

                if (onStatus != null) onStatus("연결 중...");
                // Winsock resolves the RFCOMM channel from the service UUID (port 0 + SDP lookup), which can block
                // for a long time when the peer is off; enforce our own limit by disposing the client.
                using (var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancel))
                {
                    connectTimeout.CancelAfter(ConnectTimeoutMs);
                    using (connectTimeout.Token.Register(() => SafeClose(client)))
                    {
                        try
                        {
                            client.Connect(address, Protocol.ServiceId);
                        }
                        catch (Exception) when (connectTimeout.IsCancellationRequested && !cancel.IsCancellationRequested)
                        {
                            throw new IOException("상대 노트북에 " + (ConnectTimeoutMs / 1000) + "초 안에 연결하지 못했습니다. 상대의 블루투스가 켜져 있고 tkdrop이 실행 중인지 확인하세요.");
                        }
                    }
                }
                cancel.ThrowIfCancellationRequested();

                var stream = client.GetStream();
                if (stream == null) throw new IOException("연결이 바로 끊어졌습니다.");
                using (stream)
                {
                    stream.ReadTimeout = 30000;
                    stream.WriteTimeout = 60000;
                    if (onStatus != null) onStatus("보내는 중...");
                    using (cancel.Register(() => SafeClose(client)))
                    {
                        TransferSession.Send(stream, Environment.MachineName, items, onProgress, cancel);
                    }
                }
            }
            catch (Exception ex) when (cancel.IsCancellationRequested && !(ex is OperationCanceledException))
            {
                // User pressed cancel: the client was disposed under the blocked call, which surfaces as
                // SocketException / ObjectDisposedException / IOException. Report it as a cancellation.
                throw new OperationCanceledException(cancel);
            }
            catch (SocketException ex)
            {
                throw new IOException(DescribeSocketError(ex), ex);
            }
            catch (IOException ex) when (ex.InnerException is SocketException)
            {
                throw new IOException(DescribeSocketError((SocketException)ex.InnerException), ex);
            }
            finally
            {
                SafeClose(client);
            }
        }

        private static string DescribeSocketError(SocketException ex)
        {
            // Codes per Microsoft "Bluetooth and connect": 10060 page timeout, 10051 radio/L2CAP error,
            // 10064 RFCOMM DM (channel refused), 10049 service/channel not found, 10013 authentication failed.
            switch (ex.SocketErrorCode)
            {
                case SocketError.TimedOut:
                case SocketError.NetworkUnreachable:
                case SocketError.HostUnreachable:
                    return "상대 노트북을 찾을 수 없습니다. 상대의 블루투스가 켜져 있고 가까이 있는지 확인하세요. (" + ex.NativeErrorCode + ")";
                case SocketError.HostDown:
                case SocketError.AddressNotAvailable:
                case SocketError.ConnectionRefused:
                    return "상대 노트북에서 tkdrop이 실행 중이 아닌 것 같습니다. 상대 쪽에서 tkdrop을 먼저 실행하세요. (" + ex.NativeErrorCode + ")";
                case SocketError.AccessDenied:
                    return "인증에 실패해 연결이 거부되었습니다. 두 노트북이 Windows 설정에서 페어링되어 있는지 확인하세요. (" + ex.NativeErrorCode + ")";
                case SocketError.NetworkDown:
                case SocketError.AddressFamilyNotSupported:
                case SocketError.ProtocolNotSupported:
                    return "이 노트북의 블루투스가 꺼져 있거나 사용할 수 없습니다. (" + ex.NativeErrorCode + ")";
                case SocketError.ConnectionReset:
                case SocketError.Disconnecting:
                case SocketError.Shutdown:
                    return "전송 중 상대가 연결을 끊었습니다. (" + ex.NativeErrorCode + ")";
                default:
                    return "블루투스 연결 실패 (" + ex.SocketErrorCode + ", " + ex.NativeErrorCode + "): " + ex.Message;
            }
        }

        /// <summary>
        /// Disposes the client. Note: BluetoothClient.Close() is a no-op unless the socket is already connected,
        /// so Dispose() is the only way to abort a pending Connect().
        /// </summary>
        internal static void SafeClose(BluetoothClient client)
        {
            try { client.Dispose(); } catch { }
        }

        /// <summary>True when Windows lists <paramref name="address"/> among the paired (authenticated) devices.</summary>
        public static bool IsPaired(string address)
        {
            BluetoothAddress target;
            if (!TryParseAddress(address, out target)) return false;
            try
            {
                foreach (var d in ListPairedDevices())
                {
                    BluetoothAddress a;
                    if (TryParseAddress(d.Address, out a) && a == target) return true;
                }
            }
            catch { }
            return false;
        }

        /// <summary>"Bluetooth is off or missing" check. BluetoothRadio.Default throws PlatformNotSupportedException in that case.</summary>
        public static bool RadioAvailable()
        {
            try { var r = BluetoothRadio.Default; return r != null; }
            catch { return false; }
        }
    }

    /// <summary>Accepts incoming tkdrop connections on a background thread and saves what arrives.</summary>
    public sealed class Receiver : IDisposable
    {
        private readonly AppSettings _settings;
        private readonly Thread _thread;
        private volatile bool _stopping;
        private BluetoothListener _listener;
        private readonly object _gate = new object();

        public event Action<string> StatusChanged;          // human readable listener state
        public event Action<ReceivedItem> ItemReceived;
        public event Action<string, string> PeerAutoRegistered; // (name, address)
        public event Action<string> Blocked;                  // remote address/name that was refused
        public event Action<string> Failed;                   // session error message
        public event Action<TransferProgress> Progress;

        public string Status { get; private set; }
        public bool IsListening { get; private set; }

        public Receiver(AppSettings settings)
        {
            _settings = settings;
            _thread = new Thread(Loop) { IsBackground = true, Name = "tkdrop-listener" };
        }

        public void Start()
        {
            _thread.Start();
        }

        /// <summary>Tear down the listener; the loop re-creates it (used after resume from sleep or radio toggle).</summary>
        public void Restart()
        {
            BluetoothListener l;
            lock (_gate)
            {
                l = _listener;
                _listener = null;
            }
            StopListener(l);
        }

        /// <summary>
        /// Win32BluetoothListener.Stop() has no guards: it throws NullReferenceException when not Active, and if
        /// deleting the SDP record fails it throws SocketException while leaving the socket open (a second call then
        /// closes it). Closing the socket is what unblocks AcceptBluetoothClient().
        /// </summary>
        private static void StopListener(BluetoothListener l)
        {
            if (l == null) return;
            for (int attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    if (!l.Active) return;
                    l.Stop();
                    return;
                }
                catch { }
            }
        }

        private void Loop()
        {
            int failures = 0;
            while (!_stopping)
            {
                BluetoothListener listener = null;
                try
                {
                    listener = new BluetoothListener(Protocol.ServiceId) { ServiceName = Protocol.ServiceName };
                    listener.Start();
                    lock (_gate) { _listener = listener; }
                    failures = 0;
                    SetStatus(true, "수신 대기 중");

                    while (!_stopping)
                    {
                        BluetoothClient client = listener.AcceptBluetoothClient();
                        ThreadPool.QueueUserWorkItem(_ => HandleClient(client));
                    }
                }
                catch (Exception ex)
                {
                    if (_stopping) break;
                    bool wasRestart;
                    lock (_gate) { wasRestart = _listener == null; }
                    if (!wasRestart)
                    {
                        failures++;
                        SetStatus(false, "블루투스 수신 대기 실패: " + Describe(ex));
                    }
                }
                finally
                {
                    lock (_gate) { if (_listener == listener) _listener = null; }
                    StopListener(listener); // a listener whose Start() threw is never reused; a new one is created per attempt
                }

                if (_stopping) break;
                // Back off: 3s after the first failure, up to 30s. Covers "Bluetooth is off" until the user turns it on.
                int delayMs = Math.Min(30000, 3000 * Math.Max(1, failures));
                Thread.Sleep(delayMs);
            }
            SetStatus(false, "중지됨");
        }

        private static string Describe(Exception ex)
        {
            if (!BtLink.RadioAvailable()) return "블루투스가 꺼져 있거나 없습니다.";
            var se = ex as SocketException;
            if (se != null)
            {
                if (se.SocketErrorCode == SocketError.AddressFamilyNotSupported || se.SocketErrorCode == SocketError.NetworkDown || se.SocketErrorCode == SocketError.ProtocolNotSupported)
                    return "블루투스가 꺼져 있거나 없습니다.";
                return se.SocketErrorCode + " (" + se.NativeErrorCode + ")";
            }
            if (ex is PlatformNotSupportedException) return "이 PC에서 블루투스를 사용할 수 없습니다.";
            return ex.Message;
        }

        private void HandleClient(BluetoothClient client)
        {
            string remoteAddress = null, remoteName = null;
            try
            {
                var ep = client.Client.RemoteEndPoint as BluetoothEndPoint;
                if (ep != null) remoteAddress = BtLink.FormatAddress(ep.Address);
                try { remoteName = client.RemoteMachineName; } catch { }

                if (!IsAllowed(remoteAddress, remoteName))
                {
                    var who = remoteName ?? remoteAddress ?? "알 수 없는 장치";
                    Raise(Blocked, who);
                    return;
                }

                var stream = client.GetStream();
                if (stream == null) return; // already disconnected
                using (stream)
                {
                    stream.ReadTimeout = 30000;
                    stream.WriteTimeout = 60000;
                    TransferSession.Receive(stream, _settings.ResolvedSaveFolder, item => Raise(ItemReceived, item), p => Raise(Progress, p), CancellationToken.None);
                }
            }
            catch (Exception ex)
            {
                if (!_stopping) Raise(Failed, (remoteName ?? remoteAddress ?? "상대") + ": " + ex.Message);
            }
            finally
            {
                BtLink.SafeClose(client);
            }
        }

        /// <summary>
        /// Only the chosen peer may send. If no peer is chosen yet, the first paired device that connects
        /// becomes the peer (so the user only has to pick the peer on one of the two laptops).
        /// </summary>
        private bool IsAllowed(string remoteAddress, string remoteName)
        {
            if (remoteAddress == null) return false;

            if (!_settings.HasPeer)
            {
                // The listening socket has no authentication requirement (32feet exposes none), so make sure the
                // first device we adopt as peer is actually paired with this PC.
                if (!BtLink.IsPaired(remoteAddress)) return false;

                _settings.PeerAddress = remoteAddress;
                _settings.PeerName = remoteName;
                try { _settings.Save(); } catch { }
                Raise(PeerAutoRegistered, remoteName ?? remoteAddress, remoteAddress);
                return true;
            }

            BluetoothAddress a, b;
            if (BtLink.TryParseAddress(_settings.PeerAddress, out a) && BtLink.TryParseAddress(remoteAddress, out b))
                return a == b;
            return string.Equals(_settings.PeerAddress, remoteAddress, StringComparison.OrdinalIgnoreCase);
        }

        private void SetStatus(bool listening, string text)
        {
            IsListening = listening;
            Status = text;
            Raise(StatusChanged, text);
        }

        private void Raise<T>(Action<T> handler, T arg)
        {
            try { if (handler != null) handler(arg); } catch { }
        }

        private void Raise<T1, T2>(Action<T1, T2> handler, T1 a, T2 b)
        {
            try { if (handler != null) handler(a, b); } catch { }
        }

        public void Dispose()
        {
            _stopping = true;
            Restart();
            if (_thread.IsAlive) _thread.Join(2000);
        }
    }
}
