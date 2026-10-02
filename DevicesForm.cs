using System;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace MagicKeyBattery;

// Program から渡す、デバイス設定の読み書き
internal sealed class DevicesSettings
{
    public string TvIp { get; init; } = "";
    public Action<string> SaveTvIp { get; init; } = _ => { };

    public bool F15IsPc { get; init; }
    public Action<bool> SaveF15IsPc { get; init; } = _ => { };

    public string RemotePcHost { get; init; } = "";
    public Action<string, byte[], string> SaveRemotePc { get; init; } = (_, _, _) => { };

    public bool ReceiverEnabled { get; init; }
    public Func<bool, string?> SetReceiverEnabled { get; init; } = _ => null;
}

// F14 (TV) / F15 (スマホ or 別の PC) の接続先と、この PC の受信設定
internal sealed class DevicesForm : Form
{
    private readonly Func<string, string> _t;
    private readonly DevicesSettings _settings;

    public DevicesForm(Func<string, string> translate, DevicesSettings settings)
    {
        _t = translate;
        _settings = settings;

        // レイアウトを止めてから作る: 拡大率に合わせた拡大 (AutoScale) を完成した画面に対して 1 回だけ行うため
        SuspendLayout();

        Text = _t("devices_title");
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        // 96 DPI (100%) で設計したレイアウトを画面の拡大率に合わせて拡大する
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(520, 856);
        BackColor = HistoryForm.Surface;
        ForeColor = HistoryForm.TextPrimary;
        Font = new Font("Segoe UI", 9f);
        ShowIcon = false;
        AutoScroll = true; // 画面が小さい場合はスクロール

        BuildTvSection(12);
        BuildF15Section(242);
        BuildReceiverSection(646);

        var btnClose = MakeButton(_t("devices_close"), 415, 812);
        btnClose.Click += (s, e) => Close();
        Controls.Add(btnClose);

        Rtl.Apply(this); // アラビア語: 左右反転

        ResumeLayout(false);
        PerformLayout();
    }

    // --- 📺 TV (F14) ---
    private void BuildTvSection(int top)
    {
        var txtIp = TextInput(165, top + 33, 135, _settings.TvIp);
        var btnFind = MakeButton(_t("devices_find"), 310, top + 30);
        var btnPair = MakeButton(_t("devices_pair"), 415, top + 30);
        var status = Status(top + 68);

        btnFind.Click += async (s, e) =>
        {
            status.Text = _t("devices_searching");
            string? ip = await TvRemote.DiscoverAsync();
            if (ip != null) txtIp.Text = ip;
            status.Text = ip != null ? $"✔ {ip}" : _t("devices_not_found");
        };

        btnPair.Click += async (s, e) =>
        {
            string ip = txtIp.Text.Trim();
            if (ip.Length == 0) return;
            _settings.SaveTvIp(ip);
            if (DeviceSwitcher.Tv == null) return;

            btnPair.Enabled = false;
            status.Text = _t("devices_tv_accept");
            string? error = await DeviceSwitcher.Tv.PairAsync();
            status.Text = error == null ? $"✔ {_t("devices_paired")}" : $"✖ {error}";
            btnPair.Enabled = true;
        };

        Controls.AddRange(new Control[]
        {
            Header(_t("devices_tv"), top), Caption(_t("devices_tv_ip"), top + 36), txtIp, btnFind, btnPair, status,
            Hint(_t("devices_tv_hint"), top + 92, 78),
            Hint(_t("devices_tv_channels"), top + 174, 44)
        });
    }

    // --- F15: Android スマホ or 別の PC ---
    private void BuildF15Section(int top)
    {
        var rbPhone = Radio(_t("devices_f15_phone"), 16, top + 30, !_settings.F15IsPc);
        var rbPc = Radio(_t("devices_f15_pc"), 260, top + 30, _settings.F15IsPc);

        // スマホ
        var phonePanel = new Panel { Left = 0, Top = top + 64, Width = 520, Height = 194 };
        var txtAddr = TextInput(165, 107, 135, "", "192.168.1.20:37123");
        var txtCode = TextInput(165, 139, 90, "", "123456");
        var btnPairPhone = MakeButton(_t("devices_pair"), 310, 105);
        var btnTestPhone = MakeButton(_t("devices_test"), 415, 105);
        var phoneStatus = Status(172);
        phonePanel.Controls.AddRange(new Control[]
        {
            Hint(_t("devices_phone_hint"), 0, 100), Caption(_t("devices_pair_address"), 110), txtAddr,
            Caption(_t("devices_pair_code"), 142), txtCode, btnPairPhone, btnTestPhone, phoneStatus
        });

        btnPairPhone.Click += async (s, e) =>
        {
            btnPairPhone.Enabled = false;
            phoneStatus.Text = _t("devices_pairing");
            string address = txtAddr.Text, code = txtCode.Text;
            string result = await Task.Run(() => PhoneLink.Pair(address, code));
            phoneStatus.Text = result.Contains("Successfully", StringComparison.OrdinalIgnoreCase) ? $"✔ {_t("devices_paired")}" : $"✖ {result}";
            btnPairPhone.Enabled = true;
        };
        btnTestPhone.Click += async (s, e) =>
        {
            btnTestPhone.Enabled = false;
            phoneStatus.Text = _t("devices_searching");
            string? serial = await Task.Run(PhoneLink.FindDevice);
            phoneStatus.Text = serial != null ? $"✔ {_t("devices_phone_found")}: {serial}" : $"✖ {_t("devices_not_found")}";
            btnTestPhone.Enabled = true;
        };
        if (!PhoneLink.Installed)
        {
            phoneStatus.Text = $"✖ {_t("phone_not_installed")}";
            btnPairPhone.Enabled = btnTestPhone.Enabled = false;
        }

        // 別の PC
        var pcPanel = new Panel { Left = 0, Top = top + 64, Width = 520, Height = 194 };
        var txtHost = TextInput(165, 107, 135, _settings.RemotePcHost, "192.168.1.30");
        var txtPcCode = TextInput(165, 139, 90, "", "123456");
        var btnPairPc = MakeButton(_t("devices_pair"), 310, 105);
        var pcStatus = Status(172);
        pcPanel.Controls.AddRange(new Control[]
        {
            Hint(_t("devices_pc_hint"), 0, 100), Caption(_t("devices_pc_address"), 110), txtHost,
            Caption(_t("devices_pair_code"), 142), txtPcCode, btnPairPc, pcStatus
        });
        if (_settings.RemotePcHost.Length > 0) pcStatus.Text = $"✔ {_t("devices_paired")}: {_settings.RemotePcHost}";

        btnPairPc.Click += async (s, e) =>
        {
            string host = txtHost.Text.Trim(), code = txtPcCode.Text.Trim();
            if (host.Length == 0 || code.Length == 0) return;
            btnPairPc.Enabled = false;
            pcStatus.Text = _t("devices_pairing");
            (byte[]? token, string? pin, string? error) = await RemoteLink.PairAsync(host, code);
            if (token != null && pin != null)
            {
                _settings.SaveRemotePc(host, token, pin);
                pcStatus.Text = $"✔ {_t("devices_paired")}: {host}";
                txtPcCode.Text = "";
            }
            else
            {
                pcStatus.Text = $"✖ {error}";
            }
            btnPairPc.Enabled = true;
        };

        void UpdatePanels()
        {
            phonePanel.Visible = rbPhone.Checked;
            pcPanel.Visible = rbPc.Checked;
        }
        rbPhone.CheckedChanged += (s, e) => { UpdatePanels(); if (rbPhone.Checked) _settings.SaveF15IsPc(false); };
        rbPc.CheckedChanged += (s, e) => { UpdatePanels(); if (rbPc.Checked) _settings.SaveF15IsPc(true); };
        UpdatePanels();

        Controls.AddRange(new Control[] { Header(_t("devices_f15"), top), rbPhone, rbPc, phonePanel, pcPanel });
        Controls.Add(new Label { Left = 16, Top = top + 268, Width = 488, Height = 1, BackColor = HistoryForm.GridLine });
        Controls.Add(Hint(_t("devices_f15_note"), top + 278, 112));
    }

    // --- この PC を別の PC から操作できるようにする (受信) ---
    private void BuildReceiverSection(int top)
    {
        var chkReceiver = new CheckBox
        {
            Text = _t("devices_receiver_enable"), Left = 16, Top = top + 30, Width = 490, Height = 28, Checked = _settings.ReceiverEnabled,
            ForeColor = HistoryForm.TextSecondary
        };
        var lblAddress = Hint("", top + 62, 22);
        var btnCode = MakeButton(_t("devices_receiver_code"), 16, top + 88);
        btnCode.Width = 180;
        var lblCode = new Label { Left = 210, Top = top + 86, Width = 300, Height = 34, ForeColor = HistoryForm.TextPrimary, Font = new Font("Segoe UI Semibold", 14f) };
        var btnForget = MakeButton(_t("devices_receiver_forget"), 16, top + 126);
        btnForget.Width = 180;
        var status = Status(top + 130);
        status.Left = 210;
        status.Width = 300;

        void Refresh()
        {
            bool on = chkReceiver.Checked && RemoteReceiver.Running;
            lblAddress.Text = on ? $"{_t("devices_receiver_address")}: {string.Join(", ", RemoteReceiver.LocalAddresses())}" : "";
            btnCode.Enabled = on;
            int count = RemoteReceiver.PairedDeviceNames().Count;
            btnForget.Enabled = count > 0;
            if (count > 0 && status.Text.Length == 0) status.Text = $"{_t("devices_receiver_paired")}: {string.Join(", ", RemoteReceiver.PairedDeviceNames())}";
        }

        chkReceiver.CheckedChanged += (s, e) =>
        {
            string? error = _settings.SetReceiverEnabled(chkReceiver.Checked);
            status.Text = error == null ? "" : $"✖ {error}";
            lblCode.Text = "";
            Refresh();
        };

        btnCode.Click += (s, e) =>
        {
            lblCode.Text = RemoteReceiver.NewPairingCode();
            status.Text = _t("devices_receiver_code_hint");
        };

        btnForget.Click += (s, e) =>
        {
            RemoteReceiver.RemoveAllPairedDevices();
            status.Text = $"✔ {_t("devices_receiver_forgotten")}";
            Refresh();
        };

        RemoteReceiver.Status += OnReceiverStatus;
        FormClosed += (s, e) => RemoteReceiver.Status -= OnReceiverStatus;

        void OnReceiverStatus(string text)
        {
            if (IsDisposed) return;
            BeginInvoke(() =>
            {
                status.Text = text;
                if (text.StartsWith("✔")) lblCode.Text = "";
                Refresh();
            });
        }

        Refresh();
        Controls.AddRange(new Control[] { Header(_t("devices_receiver"), top), chkReceiver, lblAddress, btnCode, lblCode, btnForget, status });
    }

    // --- 部品 ---

    private static Label Header(string text, int top) => new Label
    {
        Text = text, Left = 16, Top = top, AutoSize = true, ForeColor = HistoryForm.TextPrimary,
        Font = new Font("Segoe UI Semibold", 10.5f)
    };

    private static Label Caption(string text, int top) => new Label { Text = text, Left = 16, Top = top, Width = 145, Height = 24, ForeColor = HistoryForm.TextSecondary };

    private static Label Status(int top) => new Label { Left = 16, Top = top, Width = 490, Height = 22, ForeColor = HistoryForm.TextSecondary };

    private static Label Hint(string text, int top, int height) => new Label { Text = text, Left = 16, Top = top, Width = 490, Height = height, ForeColor = HistoryForm.TextMuted };

    private static TextBox TextInput(int left, int top, int width, string text, string placeholder = "") => new TextBox
    {
        Left = left, Top = top, Width = width, Text = text, PlaceholderText = placeholder,
        BackColor = HistoryForm.SurfaceRaised, ForeColor = HistoryForm.TextPrimary, BorderStyle = BorderStyle.FixedSingle
    };

    private static RadioButton Radio(string text, int left, int top, bool isChecked) => new RadioButton
    {
        Text = text, Left = left, Top = top, Width = 230, Height = 28, Checked = isChecked, ForeColor = HistoryForm.TextSecondary
    };

    private static Button MakeButton(string text, int left, int top)
    {
        var btn = new Button
        {
            Text = text, Left = left, Top = top, Width = 95, Height = 30,
            FlatStyle = FlatStyle.Flat, BackColor = HistoryForm.SurfaceRaised, ForeColor = HistoryForm.TextPrimary, Cursor = Cursors.Hand
        };
        btn.FlatAppearance.BorderColor = HistoryForm.GridLine;
        return btn;
    }
}
