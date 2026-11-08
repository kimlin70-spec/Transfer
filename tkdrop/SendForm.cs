using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace TkDrop
{
    /// <summary>Drag-and-drop send window with progress. Also used as the whole UI in "send mode".</summary>
    public sealed class SendForm : Form
    {
        private readonly AppSettings _settings;
        private readonly bool _closeWhenDone;
        private readonly Queue<SendItem> _queue = new Queue<SendItem>();

        private readonly Label _peerLabel = new Label();
        private readonly LinkLabel _peerLink = new LinkLabel();
        private readonly Panel _dropZone = new Panel();
        private readonly Label _dropLabel = new Label();
        private readonly Button _pickButton = new Button();
        private readonly Button _clipButton = new Button();
        private readonly Button _cancelButton = new Button();
        private readonly ProgressBar _progress = new ProgressBar();
        private readonly Label _status = new Label();
        private readonly System.Windows.Forms.Timer _closeTimer = new System.Windows.Forms.Timer { Interval = 1500 };

        private CancellationTokenSource _cts;
        private readonly Stopwatch _uiThrottle = Stopwatch.StartNew();
        private bool _sending;

        public SendForm(AppSettings settings, IList<SendItem> initialItems, bool closeWhenDone)
        {
            _settings = settings;
            _closeWhenDone = closeWhenDone;

            Text = "tkdrop - 보내기";
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ClientSize = new Size(440, 330);
            Font = new Font("Malgun Gothic", 9.5f);
            AutoScaleMode = AutoScaleMode.Dpi;
            Icon = TryLoadIcon();
            TopMost = closeWhenDone;
            AllowDrop = true;

            _peerLabel.AutoSize = true;
            _peerLabel.Location = new Point(14, 14);
            _peerLink.Text = "변경";
            _peerLink.AutoSize = true;
            _peerLink.LinkClicked += (s, e) => ChoosePeer();

            _dropZone.Bounds = new Rectangle(14, 44, 412, 150);
            _dropZone.BorderStyle = BorderStyle.None;
            _dropZone.BackColor = Color.FromArgb(245, 247, 250);
            _dropZone.AllowDrop = true;
            _dropZone.Paint += (s, e) =>
            {
                using (var pen = new Pen(Color.FromArgb(120, 140, 170), 2) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dash })
                    e.Graphics.DrawRectangle(pen, 2, 2, _dropZone.Width - 5, _dropZone.Height - 5);
            };
            _dropLabel.Text = "파일이나 폴더를 여기에 끌어다 놓으세요";
            _dropLabel.Dock = DockStyle.Fill;
            _dropLabel.TextAlign = ContentAlignment.MiddleCenter;
            _dropLabel.ForeColor = Color.FromArgb(70, 80, 100);
            _dropLabel.AllowDrop = true;
            _dropZone.Controls.Add(_dropLabel);

            foreach (Control c in new Control[] { this, _dropZone, _dropLabel })
            {
                c.DragEnter += OnDragEnter;
                c.DragDrop += OnDragDrop;
            }

            _pickButton.Text = "파일 선택...";
            _pickButton.Bounds = new Rectangle(14, 206, 150, 32);
            _pickButton.Click += (s, e) => PickFiles();
            _clipButton.Text = "클립보드 텍스트 보내기";
            _clipButton.Bounds = new Rectangle(172, 206, 180, 32);
            _clipButton.Click += (s, e) => SendClipboard();
            _cancelButton.Text = "취소";
            _cancelButton.Bounds = new Rectangle(360, 206, 66, 32);
            _cancelButton.Enabled = false;
            _cancelButton.Click += (s, e) => { if (_cts != null) _cts.Cancel(); };

            _progress.Bounds = new Rectangle(14, 252, 412, 18);
            _status.Bounds = new Rectangle(14, 278, 412, 44);
            _status.Text = _settings.HasPeer ? "대기 중" : "먼저 '변경'을 눌러 상대 노트북을 선택하세요.";

            Controls.AddRange(new Control[] { _peerLabel, _peerLink, _dropZone, _pickButton, _clipButton, _cancelButton, _progress, _status });
            _closeTimer.Tick += (s, e) => { _closeTimer.Stop(); Close(); };

            RefreshPeer();

            if (initialItems != null && initialItems.Count > 0)
                Shown += (s, e) => Enqueue(initialItems);
        }

        private static Icon TryLoadIcon()
        {
            try { return Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { return null; }
        }

        private void RefreshPeer()
        {
            _peerLabel.Text = "받는 쪽: " + _settings.PeerDisplay;
            _peerLink.Location = new Point(_peerLabel.Right + 6, 14);
        }

        private void ChoosePeer()
        {
            using (var dlg = new PeerForm(_settings))
            {
                if (dlg.ShowDialog(this) == DialogResult.OK)
                {
                    RefreshPeer();
                    if (!_sending) _status.Text = "대기 중";
                }
            }
        }

        public void PickFiles()
        {
            using (var dlg = new OpenFileDialog { Multiselect = true, Title = "보낼 파일 선택" })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                var items = new List<SendItem>();
                foreach (var f in dlg.FileNames) items.AddRange(SendItem.FromPath(f));
                Enqueue(items);
            }
        }

        private void SendClipboard()
        {
            var text = Program.ClipboardText();
            if (string.IsNullOrEmpty(text))
            {
                _status.Text = "클립보드에 텍스트가 없습니다.";
                return;
            }
            Enqueue(new List<SendItem> { SendItem.FromText(text) });
        }

        private void OnDragEnter(object sender, DragEventArgs e)
        {
            e.Effect = !_sending && e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy
                     : !_sending && e.Data.GetDataPresent(DataFormats.UnicodeText) ? DragDropEffects.Copy
                     : DragDropEffects.None;
        }

        private void OnDragDrop(object sender, DragEventArgs e)
        {
            if (_sending) return;
            var items = new List<SendItem>();
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                foreach (var p in (string[])e.Data.GetData(DataFormats.FileDrop)) items.AddRange(SendItem.FromPath(p));
            }
            else if (e.Data.GetDataPresent(DataFormats.UnicodeText))
            {
                var text = e.Data.GetData(DataFormats.UnicodeText) as string;
                if (!string.IsNullOrEmpty(text)) items.Add(SendItem.FromText(text));
            }
            Enqueue(items);
        }

        /// <summary>Adds items to the outgoing queue and starts sending if idle.</summary>
        public void Enqueue(IList<SendItem> items)
        {
            if (items == null) return;
            foreach (var i in items) _queue.Enqueue(i);
            if (_queue.Count == 0)
            {
                _status.Text = "보낼 파일이 없습니다.";
                return;
            }
            if (!_sending) StartNext();
        }

        private void StartNext()
        {
            if (_queue.Count == 0) return;

            if (!_settings.HasPeer)
            {
                ChoosePeer();
                if (!_settings.HasPeer)
                {
                    _queue.Clear();
                    _status.Text = "상대 노트북이 선택되지 않아 보내지 않았습니다.";
                    return;
                }
            }

            var batch = new List<SendItem>(_queue);
            _queue.Clear();

            _sending = true;
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            SetBusy(true);
            _progress.Value = 0;
            _status.Text = "연결 중... (" + batch.Count + "개, " + Protocol.FormatSize(SendItem.TotalSize(batch)) + ")";

            var peer = _settings.PeerAddress;
            var worker = new Thread(() =>
            {
                Exception error = null;
                try
                {
                    BtLink.SendTo(peer, batch, status => Ui(() => _status.Text = status), p => OnProgress(p), token);
                }
                catch (OperationCanceledException) { error = new OperationCanceledException(); }
                catch (Exception ex) { error = ex; }
                Ui(() => OnFinished(batch, error));
            }) { IsBackground = true, Name = "tkdrop-sender" };
            worker.Start();
        }

        private void OnProgress(TransferProgress p)
        {
            if (_uiThrottle.ElapsedMilliseconds < 80 && p.Percent < 100) return;
            _uiThrottle.Restart();
            int percent = p.Percent;
            string text = "보내는 중 " + percent + "%  (" + p.ItemIndex + "/" + p.ItemCount + ")  " + p.CurrentItem;
            Ui(() =>
            {
                _progress.Value = Math.Max(0, Math.Min(100, percent));
                _status.Text = text;
            });
        }

        private void OnFinished(List<SendItem> batch, Exception error)
        {
            _sending = false;
            _cts.Dispose();
            _cts = null;
            SetBusy(false);

            if (error == null)
            {
                _progress.Value = 100;
                _status.ForeColor = Color.FromArgb(20, 120, 60);
                _status.Text = "완료: " + batch.Count + "개 보냄 (" + Protocol.FormatSize(SendItem.TotalSize(batch)) + ")";
                if (_closeWhenDone && _queue.Count == 0) _closeTimer.Start();
            }
            else if (error is OperationCanceledException)
            {
                _progress.Value = 0;
                _status.ForeColor = SystemColors.ControlText;
                _status.Text = "취소됨";
                _queue.Clear();
            }
            else
            {
                _progress.Value = 0;
                _status.ForeColor = Color.FromArgb(180, 30, 30);
                _status.Text = "실패: " + error.Message;
                _queue.Clear();
            }

            if (_queue.Count > 0) StartNext();
        }

        private void SetBusy(bool busy)
        {
            _pickButton.Enabled = !busy;
            _clipButton.Enabled = !busy;
            _peerLink.Enabled = !busy;
            _cancelButton.Enabled = busy;
            _dropLabel.Text = busy ? "전송 중..." : "파일이나 폴더를 여기에 끌어다 놓으세요";
            if (!busy) _status.ForeColor = SystemColors.ControlText;
            UseWaitCursor = false;
        }

        private void Ui(Action action)
        {
            if (IsDisposed) return;
            try { BeginInvoke(action); } catch (ObjectDisposedException) { } catch (InvalidOperationException) { }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (_sending && e.CloseReason == CloseReason.UserClosing)
            {
                var r = MessageBox.Show(this, "전송 중입니다. 취소하고 닫을까요?", Program.AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (r != DialogResult.Yes) { e.Cancel = true; return; }
                if (_cts != null) _cts.Cancel();
            }
            base.OnFormClosing(e);
        }
    }
}
