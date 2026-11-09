using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;

namespace TkDrop
{
    /// <summary>Lets the user pick the peer laptop from the devices paired in Windows.</summary>
    public sealed class PeerForm : Form
    {
        private readonly AppSettings _settings;
        private readonly ListBox _list = new ListBox();
        private readonly Label _hint = new Label();
        private readonly Button _refresh = new Button();
        private readonly Button _ok = new Button();
        private readonly Button _cancel = new Button();
        private int _loadGeneration;

        public PeerForm(AppSettings settings)
        {
            _settings = settings;

            Text = "상대 노트북 선택";
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            ClientSize = new Size(460, 320);
            Font = new Font("Malgun Gothic", 9.5f);
            AutoScaleMode = AutoScaleMode.Dpi;

            _hint.Text = "Windows 설정 > Bluetooth 에서 먼저 두 노트북을 페어링해 두어야 목록에 나타납니다.";
            _hint.Bounds = new Rectangle(14, 12, 432, 40);

            _list.Bounds = new Rectangle(14, 56, 432, 210);
            _list.IntegralHeight = false;
            _list.DoubleClick += (s, e) => Accept();

            _refresh.Text = "새로고침";
            _refresh.Bounds = new Rectangle(14, 276, 100, 30);
            _refresh.Click += (s, e) => LoadDevices();
            _ok.Text = "선택";
            _ok.Bounds = new Rectangle(260, 276, 90, 30);
            _ok.Click += (s, e) => Accept();
            _cancel.Text = "취소";
            _cancel.Bounds = new Rectangle(356, 276, 90, 30);
            _cancel.DialogResult = DialogResult.Cancel;

            AcceptButton = _ok;
            CancelButton = _cancel;
            Controls.AddRange(new Control[] { _hint, _list, _refresh, _ok, _cancel });

            Shown += (s, e) => LoadDevices();
        }

        private void LoadDevices()
        {
            int generation = ++_loadGeneration;
            _list.Items.Clear();
            _list.Items.Add("페어링된 장치를 읽는 중...");
            _list.Enabled = false;
            _ok.Enabled = false;

            var worker = new Thread(() =>
            {
                List<PairedDevice> devices = null;
                string error = null;
                try { devices = BtLink.ListPairedDevices(); }
                catch (Exception ex) { error = ex.Message; }

                try
                {
                    BeginInvoke((Action)(() =>
                    {
                        if (generation != _loadGeneration || IsDisposed) return;
                        _list.Items.Clear();
                        if (error != null)
                        {
                            _list.Items.Add("목록을 읽지 못했습니다: " + error);
                            _list.Items.Add("이 노트북의 블루투스가 켜져 있는지 확인하세요.");
                            return;
                        }
                        if (devices.Count == 0)
                        {
                            _list.Items.Add(BtLink.RadioAvailable() ? "페어링된 장치가 없습니다." : "블루투스가 꺼져 있거나 없습니다.");
                            return;
                        }
                        _list.Enabled = true;
                        _ok.Enabled = true;
                        foreach (var d in devices)
                        {
                            _list.Items.Add(d);
                            if (_settings.HasPeer && string.Equals(d.Address, _settings.PeerAddress, StringComparison.OrdinalIgnoreCase))
                                _list.SelectedIndex = _list.Items.Count - 1;
                        }
                        if (_list.SelectedIndex < 0) _list.SelectedIndex = 0;
                    }));
                }
                catch (ObjectDisposedException) { }
                catch (InvalidOperationException) { }
            }) { IsBackground = true, Name = "tkdrop-paired" };
            worker.Start();
        }

        private void Accept()
        {
            var device = _list.SelectedItem as PairedDevice;
            if (device == null) return;

            if (!device.IsComputer)
            {
                var r = MessageBox.Show(this, "선택한 장치가 PC로 보이지 않습니다. 그래도 이 장치를 상대로 저장할까요?", Program.AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (r != DialogResult.Yes) return;
            }

            _settings.PeerAddress = device.Address;
            _settings.PeerName = device.Name;
            try
            {
                _settings.Save();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, Program.AppName, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
