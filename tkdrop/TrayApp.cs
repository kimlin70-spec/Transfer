using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using Microsoft.Win32;

namespace TkDrop
{
    /// <summary>Tray-resident main application: hosts the receiver and the context menu.</summary>
    public sealed class TrayApp : ApplicationContext
    {
        private readonly AppSettings _settings;
        private readonly Receiver _receiver;
        private readonly NotifyIcon _tray;
        private readonly PumpForm _pump;
        private readonly ToolStripMenuItem _peerLabel;
        private readonly ToolStripMenuItem _autoStart;
        private SendForm _sendForm;
        private Action _balloonAction;
        private readonly Stopwatch _tooltipThrottle = Stopwatch.StartNew();
        private Timer _resumeTimer;
        private readonly Timer _radioTimer = new Timer { Interval = 15000 };
        private bool? _lastRadioOn;

        public TrayApp(AppSettings settings)
        {
            _settings = settings;

            _pump = new PumpForm(this);
            var handle = _pump.Handle; // force creation so BeginInvoke works from worker threads

            var menu = new ContextMenuStrip();
            menu.Items.Add("파일 보내기...", null, (s, e) => OpenSendForm(pickFiles: true));
            menu.Items.Add("클립보드 텍스트 보내기", null, (s, e) => SendClipboard());
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("받은 폴더 열기", null, (s, e) => OpenFolder(null));
            _peerLabel = new ToolStripMenuItem { Enabled = false };
            menu.Items.Add(_peerLabel);
            menu.Items.Add("상대 노트북 선택...", null, (s, e) => ChoosePeer());
            menu.Items.Add("저장 폴더 변경...", null, (s, e) => ChooseFolder());
            _autoStart = new ToolStripMenuItem("Windows 시작 시 자동 실행") { CheckOnClick = true, Checked = AutoStart.IsEnabled() };
            _autoStart.CheckedChanged += (s, e) => AutoStart.Set(_autoStart.Checked);
            menu.Items.Add(_autoStart);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("정보", null, (s, e) => ShowAbout());
            menu.Items.Add("종료", null, (s, e) => ExitThread());
            menu.Opening += (s, e) => { _settings.Refresh(); RefreshPeerLabel(); };

            _tray = new NotifyIcon
            {
                Icon = LoadIcon(),
                Text = Program.AppName,
                ContextMenuStrip = menu,
                Visible = true,
            };
            _tray.DoubleClick += (s, e) => OpenSendForm(pickFiles: false);
            _tray.BalloonTipClicked += (s, e) => { var a = _balloonAction; _balloonAction = null; if (a != null) a(); };

            _receiver = new Receiver(_settings);
            _receiver.StatusChanged += text => Ui(() => SetTooltip(text));
            _receiver.ItemReceived += item => Ui(() => OnReceived(item));
            _receiver.PeerAutoRegistered += (name, addr) => Ui(() =>
            {
                RefreshPeerLabel();
                if (_sendForm != null && !_sendForm.IsDisposed) _sendForm.RefreshPeer();
                Balloon("상대 노트북 등록됨", name + "\n" + addr + "\n이제 이 노트북에서도 바로 보낼 수 있습니다.", ToolTipIcon.Info, null);
            });
            _receiver.Blocked += who => Ui(() => Balloon("연결 차단", who + " 에서 연결을 시도했지만, 선택된 상대 노트북이 아니거나 페어링되지 않은 장치라 거부했습니다.", ToolTipIcon.Warning, null));
            _receiver.Failed += msg => Ui(() => { SetTooltip(_receiver.Status); Balloon("수신 실패", msg, ToolTipIcon.Error, null); });
            _receiver.Progress += p => Ui(() => OnReceiveProgress(p));
            _receiver.Start();

            SystemEvents.PowerModeChanged += OnPowerModeChanged;

            // A listening RFCOMM socket is not guaranteed to survive the radio being toggled; watch the radio
            // state and recreate the listener on every off->on / on->off transition.
            _lastRadioOn = RadioWatch.IsRadioOn();
            _radioTimer.Tick += (s, e) =>
            {
                var now = RadioWatch.IsRadioOn();
                if (now.HasValue && _lastRadioOn.HasValue && now.Value != _lastRadioOn.Value)
                {
                    _receiver.Restart();
                    if (!now.Value) SetTooltip("블루투스가 꺼져 있습니다");
                }
                _lastRadioOn = now;
            };
            _radioTimer.Start();

            RefreshPeerLabel();
            if (!_settings.HasPeer)
            {
                Balloon("tkdrop 시작됨", "상대 노트북이 아직 선택되지 않았습니다. 클릭해서 선택하거나, 상대 노트북에서 먼저 보내면 자동으로 등록됩니다.", ToolTipIcon.Info, ChoosePeer);
            }
        }

        // ---- UI helpers -------------------------------------------------------------------

        private void Ui(Action action)
        {
            if (_pump.IsDisposed) return;
            try { _pump.BeginInvoke(action); } catch (ObjectDisposedException) { } catch (InvalidOperationException) { }
        }

        private void SetTooltip(string text)
        {
            var full = Program.AppName + " - " + text;
            if (full.Length > 63) full = full.Substring(0, 60) + "...";
            try { _tray.Text = full; } catch { }
        }

        private void Balloon(string title, string text, ToolTipIcon icon, Action onClick)
        {
            _balloonAction = onClick;
            try { _tray.ShowBalloonTip(5000, title, text, icon); } catch { }
        }

        private static Icon LoadIcon()
        {
            try
            {
                var icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
                if (icon != null) return icon;
            }
            catch { }
            return SystemIcons.Application;
        }

        private void RefreshPeerLabel()
        {
            _peerLabel.Text = "상대 노트북: " + _settings.PeerDisplay;
        }

        // ---- receiver events ----------------------------------------------------------------

        private void OnReceived(ReceivedItem item)
        {
            if (item.IsText)
            {
                bool copied = false;
                string savedTo = null;
                if (!string.IsNullOrEmpty(item.Text))
                {
                    try { Clipboard.SetText(item.Text); copied = true; } // retries 10x100ms internally
                    catch
                    {
                        // Another app holds the clipboard (RDP, clipboard managers...): do not lose the text.
                        try
                        {
                            var folder = _settings.ResolvedSaveFolder;
                            Directory.CreateDirectory(folder);
                            savedTo = Protocol.UniquePath(folder, "텍스트 " + DateTime.Now.ToString("yyyy-MM-dd HHmmss") + ".txt");
                            File.WriteAllText(savedTo, item.Text, Protocol.Utf8);
                        }
                        catch { savedTo = null; }
                    }
                }
                var preview = item.Text.Length > 120 ? item.Text.Substring(0, 120) + "..." : item.Text;
                if (copied)
                    Balloon("텍스트 받음 (클립보드에 복사됨)", preview, ToolTipIcon.Info, null);
                else if (savedTo != null)
                    Balloon("텍스트 받음 (클립보드 사용 불가, 파일로 저장)", Path.GetFileName(savedTo) + "\n" + preview, ToolTipIcon.Warning, () => OpenFolder(savedTo));
                else
                    Balloon("텍스트 받음", preview, ToolTipIcon.Warning, null);
            }
            else
            {
                var name = Path.GetFileName(item.SavedPath);
                Balloon("파일 받음", name + " (" + Protocol.FormatSize(item.Size) + ")\n" + item.SenderName + " 에서. 클릭하면 폴더를 엽니다.", ToolTipIcon.Info, () => OpenFolder(item.SavedPath));
            }
            SetTooltip(_receiver.Status);
        }

        private void OnReceiveProgress(TransferProgress p)
        {
            if (_tooltipThrottle.ElapsedMilliseconds < 500) return;
            _tooltipThrottle.Restart();
            SetTooltip("받는 중 " + p.Percent + "%  " + p.CurrentItem);
        }

        private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
        {
            if (e.Mode != PowerModes.Resume) return;
            // The RFCOMM listening socket is not reliable across sleep; recreate it a few seconds after resume.
            Ui(() =>
            {
                if (_resumeTimer == null)
                {
                    _resumeTimer = new Timer { Interval = 5000 };
                    _resumeTimer.Tick += (s, a) => { _resumeTimer.Stop(); _receiver.Restart(); };
                }
                _resumeTimer.Stop();
                _resumeTimer.Start();
            });
        }

        // ---- menu actions -------------------------------------------------------------------

        private void OpenSendForm(bool pickFiles)
        {
            if (_sendForm == null || _sendForm.IsDisposed)
            {
                _sendForm = new SendForm(_settings, null, closeWhenDone: false);
                _sendForm.FormClosed += (s, e) => _sendForm = null;
                _sendForm.Show();
            }
            _sendForm.Activate();
            if (pickFiles) _sendForm.PickFiles();
        }

        private void SendClipboard()
        {
            var text = Program.ClipboardText();
            if (string.IsNullOrEmpty(text))
            {
                Balloon("보낼 텍스트 없음", "클립보드에 텍스트가 없습니다.", ToolTipIcon.Warning, null);
                return;
            }
            OpenSendForm(pickFiles: false);
            _sendForm.Enqueue(new List<SendItem> { SendItem.FromText(text) });
        }

        private void OpenFolder(string selectPath)
        {
            try
            {
                var folder = _settings.ResolvedSaveFolder;
                Directory.CreateDirectory(folder);
                if (selectPath != null && File.Exists(selectPath))
                    Process.Start("explorer.exe", "/select,\"" + selectPath + "\"");
                else
                    Process.Start("explorer.exe", "\"" + folder + "\"");
            }
            catch (Exception ex)
            {
                Balloon("폴더 열기 실패", ex.Message, ToolTipIcon.Error, null);
            }
        }

        private void ChoosePeer()
        {
            using (var dlg = new PeerForm(_settings))
            {
                if (dlg.ShowDialog() == DialogResult.OK) RefreshPeerLabel();
            }
        }

        private void ChooseFolder()
        {
            using (var dlg = new FolderBrowserDialog { Description = "받은 파일을 저장할 폴더", SelectedPath = _settings.ResolvedSaveFolder, ShowNewFolderButton = true })
            {
                if (dlg.ShowDialog() != DialogResult.OK) return;
                _settings.SaveFolder = dlg.SelectedPath;
                try { _settings.Save(); }
                catch (Exception ex) { MessageBox.Show(ex.Message, Program.AppName, MessageBoxButtons.OK, MessageBoxIcon.Error); }
            }
        }

        private void ShowAbout()
        {
            var version = typeof(Program).Assembly.GetName().Version;
            var text = "tkdrop " + version.ToString(3) + "\n\n"
                + "블루투스로 노트북 두 대 사이에 파일과 텍스트를 주고받습니다.\n\n"
                + "수신 상태: " + _receiver.Status + "\n"
                + "상대 노트북: " + _settings.PeerDisplay + "\n"
                + "저장 폴더: " + _settings.ResolvedSaveFolder + "\n"
                + "설정 파일: " + (_settings.LoadedPath ?? Path.Combine(AppSettings.ExeDirectory, AppSettings.FileName) + " (아직 없음)") + "\n\n"
                + "사용법: 트레이 아이콘 더블클릭 → 파일을 끌어다 놓기.\n"
                + "탐색기 '보내기' 메뉴에 tkdrop.exe 바로가기를 넣으면 바로 보낼 수 있습니다.";
            MessageBox.Show(text, "tkdrop 정보", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        internal void ShowFromOtherInstance()
        {
            OpenSendForm(pickFiles: false);
        }

        protected override void ExitThreadCore()
        {
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            _radioTimer.Stop();
            _radioTimer.Dispose();
            if (_resumeTimer != null) _resumeTimer.Dispose();
            try { _tray.Visible = false; } catch { }
            _receiver.Dispose();
            _tray.Dispose();
            if (_sendForm != null && !_sendForm.IsDisposed) _sendForm.ForceClose();
            _pump.Dispose();
            base.ExitThreadCore();
        }

        /// <summary>Hidden window: marshals worker-thread callbacks to the UI thread and receives WM_SHOWME broadcasts.</summary>
        private sealed class PumpForm : Form
        {
            private readonly TrayApp _owner;
            public PumpForm(TrayApp owner)
            {
                _owner = owner;
                ShowInTaskbar = false;
                WindowState = FormWindowState.Minimized;
                FormBorderStyle = FormBorderStyle.FixedToolWindow;
                Opacity = 0;
                Size = new Size(1, 1);
                StartPosition = FormStartPosition.Manual;
                Location = new Point(-32000, -32000);
            }

            protected override void SetVisibleCore(bool value)
            {
                base.SetVisibleCore(false); // never actually shown
            }

            protected override void WndProc(ref Message m)
            {
                if (m.Msg == Program.WM_SHOWME)
                {
                    _owner.ShowFromOtherInstance();
                    return;
                }
                base.WndProc(ref m);
            }
        }
    }

    /// <summary>HKCU Run key handling for "start with Windows".</summary>
    public static class AutoStart
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

        public static bool IsEnabled()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(RunKey, false))
                {
                    var value = key == null ? null : key.GetValue(Program.AppName) as string;
                    return !string.IsNullOrEmpty(value) && value.IndexOf(Application.ExecutablePath, StringComparison.OrdinalIgnoreCase) >= 0;
                }
            }
            catch { return false; }
        }

        public static void Set(bool enabled)
        {
            try
            {
                using (var key = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (enabled) key.SetValue(Program.AppName, "\"" + Application.ExecutablePath + "\"");
                    else key.DeleteValue(Program.AppName, false);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("자동 실행 설정을 바꾸지 못했습니다.\n" + ex.Message, Program.AppName, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }
}
