using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Windows.Forms;

namespace MagicKeyBattery;

// 履歴 CSV の 1 行
internal readonly record struct HistoryPoint(DateTime Time, byte Level, bool Charging);

// バッテリー履歴をグラフで表示するウィンドウ (外部ライブラリ不使用、GDI+ で描画)
internal sealed class HistoryForm : Form
{
    // --- 配色 (設定ダイアログと同じダークテーマ) ---
    internal static readonly Color Surface = Color.FromArgb(0x1a, 0x1a, 0x19);
    internal static readonly Color SurfaceRaised = Color.FromArgb(0x26, 0x26, 0x24);
    internal static readonly Color TextPrimary = Color.White;
    internal static readonly Color TextSecondary = Color.FromArgb(0xc3, 0xc2, 0xb7);
    internal static readonly Color TextMuted = Color.FromArgb(0x8a, 0x89, 0x80);
    internal static readonly Color GridLine = Color.FromArgb(0x38, 0x38, 0x35);
    internal static readonly Color SeriesColor = Color.FromArgb(0x39, 0x87, 0xe5);
    internal static readonly Color ChargingColor = Color.FromArgb(0x19, 0x9e, 0x70);
    internal static readonly Color ThresholdColor = Color.FromArgb(0xd0, 0x3b, 0x3b);

    private enum RangeKind { Day, Week, Month, All }

    private readonly Func<string, string> _t;
    private readonly string _historyFile;
    private readonly int _threshold;

    private List<HistoryPoint> _points = new();
    private RangeKind _range = RangeKind.Week;

    private readonly ChartControl _chart;
    private readonly StatTile _tileCurrent;
    private readonly StatTile _tileDrain;
    private readonly StatTile _tileRemaining;
    private readonly StatTile _tileLastCharge;
    private readonly Label _lblEmpty;

    public HistoryForm(Func<string, string> translate, string historyFile, int notifyThreshold)
    {
        _t = translate;
        _historyFile = historyFile;
        _threshold = notifyThreshold;

        Text = _t("history_title");
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(760, 480);
        MinimumSize = new Size(520, 380);
        BackColor = Surface;
        ForeColor = TextPrimary;
        Font = new Font("Segoe UI", 9f);
        ShowIcon = false;

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            Padding = new Padding(16, 12, 16, 12),
            BackColor = Surface
        };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // 期間切り替え
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // 統計タイル
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));  // グラフ
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // ボタン

        // --- 期間切り替え (グラフの上に 1 行) ---
        var rangeRow = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = false, Margin = new Padding(0, 0, 0, 8) };
        rangeRow.Controls.Add(MakeRangeButton(_t("range_24h"), RangeKind.Day));
        rangeRow.Controls.Add(MakeRangeButton(_t("range_7d"), RangeKind.Week, isDefault: true));
        rangeRow.Controls.Add(MakeRangeButton(_t("range_30d"), RangeKind.Month));
        rangeRow.Controls.Add(MakeRangeButton(_t("range_all"), RangeKind.All));

        // --- 統計タイル ---
        var tiles = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 4, RowCount = 1, Margin = new Padding(0, 0, 0, 8) };
        for (int i = 0; i < 4; i++) tiles.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
        _tileCurrent = new StatTile(_t("stat_current"));
        _tileDrain = new StatTile(_t("stat_drain"));
        _tileRemaining = new StatTile(_t("stat_remaining"));
        _tileLastCharge = new StatTile(_t("stat_last_charge"));
        tiles.Controls.Add(_tileCurrent, 0, 0);
        tiles.Controls.Add(_tileDrain, 1, 0);
        tiles.Controls.Add(_tileRemaining, 2, 0);
        tiles.Controls.Add(_tileLastCharge, 3, 0);

        // --- グラフ ---
        var chartHost = new Panel { Dock = DockStyle.Fill, Margin = new Padding(0) };
        _chart = new ChartControl(_t) { Dock = DockStyle.Fill, Threshold = _threshold };
        _lblEmpty = new Label
        {
            Dock = DockStyle.Fill,
            Text = _t("no_history"),
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = TextSecondary,
            BackColor = Surface,
            Visible = false
        };
        chartHost.Controls.Add(_lblEmpty);
        chartHost.Controls.Add(_chart);

        // --- ボタン (表データとしての CSV を開く / 再読み込み) ---
        var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, Margin = new Padding(0, 8, 0, 0) };
        var btnCsv = MakeButton(_t("btn_open_csv"));
        btnCsv.Click += (s, e) => OpenCsv();
        var btnRefresh = MakeButton(_t("btn_refresh"));
        btnRefresh.Click += (s, e) => ReloadData();
        buttons.Controls.Add(btnCsv);
        buttons.Controls.Add(btnRefresh);

        layout.Controls.Add(rangeRow, 0, 0);
        layout.Controls.Add(tiles, 0, 1);
        layout.Controls.Add(chartHost, 0, 2);
        layout.Controls.Add(buttons, 0, 3);
        Controls.Add(layout);

        ReloadData();
    }

    // Program から新しい記録が追加された時にも呼ばれる
    public void ReloadData()
    {
        _points = LoadHistory(_historyFile);
        ApplyRange();
    }

    private RadioButton MakeRangeButton(string text, RangeKind kind, bool isDefault = false)
    {
        var rb = new RadioButton
        {
            Text = text,
            Appearance = Appearance.Button,
            FlatStyle = FlatStyle.Flat,
            AutoSize = true,
            TextAlign = ContentAlignment.MiddleCenter,
            Padding = new Padding(8, 2, 8, 2),
            Margin = new Padding(0, 0, 6, 0),
            ForeColor = TextSecondary,
            BackColor = SurfaceRaised,
            Checked = isDefault,
            Cursor = Cursors.Hand
        };
        rb.FlatAppearance.BorderColor = GridLine;
        rb.FlatAppearance.CheckedBackColor = SeriesColor;
        rb.CheckedChanged += (s, e) =>
        {
            rb.ForeColor = rb.Checked ? TextPrimary : TextSecondary;
            if (rb.Checked)
            {
                _range = kind;
                ApplyRange();
            }
        };
        if (isDefault) rb.ForeColor = TextPrimary;
        return rb;
    }

    private static Button MakeButton(string text)
    {
        var btn = new Button
        {
            Text = text,
            AutoSize = true,
            FlatStyle = FlatStyle.Flat,
            Padding = new Padding(8, 2, 8, 2),
            Margin = new Padding(6, 0, 0, 0),
            BackColor = SurfaceRaised,
            ForeColor = TextPrimary,
            Cursor = Cursors.Hand
        };
        btn.FlatAppearance.BorderColor = GridLine;
        return btn;
    }

    private void ApplyRange()
    {
        DateTime now = DateTime.Now;
        DateTime end = now;
        DateTime start = _range switch
        {
            RangeKind.Day => now.AddHours(-24),
            RangeKind.Week => now.AddDays(-7),
            RangeKind.Month => now.AddDays(-30),
            _ => _points.Count > 0 ? _points[0].Time : now.AddHours(-24)
        };
        if (end - start < TimeSpan.FromHours(1)) start = end.AddHours(-1);

        _chart.SetData(_points, start, end);

        bool empty = _points.Count == 0;
        _lblEmpty.Visible = empty;
        _chart.Visible = !empty;

        UpdateStats(start);
    }

    private void UpdateStats(DateTime rangeStart)
    {
        if (_points.Count == 0)
        {
            _tileCurrent.SetValue("—");
            _tileDrain.SetValue("—");
            _tileRemaining.SetValue("—");
            _tileLastCharge.SetValue("—");
            return;
        }

        HistoryPoint last = _points[^1];
        _tileCurrent.SetValue($"{last.Level}%" + (last.Charging ? " ⚡" : ""));

        // 平均消費: 表示期間内の「充電していない & 残量が減った」区間だけを集計
        double droppedPercent = 0;
        double dischargeHours = 0;
        for (int i = 1; i < _points.Count; i++)
        {
            HistoryPoint a = _points[i - 1], b = _points[i];
            if (b.Time < rangeStart) continue;
            if (a.Charging || b.Charging || b.Level > a.Level) continue;

            double hours = (b.Time - a.Time).TotalHours;
            if (hours <= 0) continue;
            droppedPercent += a.Level - b.Level;
            dischargeHours += hours;
        }

        double perDay = dischargeHours >= 1 && droppedPercent > 0 ? droppedPercent / dischargeHours * 24 : 0;
        if (perDay > 0)
        {
            _tileDrain.SetValue(string.Format(CultureInfo.CurrentCulture, _t("stat_drain_value"), perDay));
            if (last.Charging)
            {
                _tileRemaining.SetValue("—");
            }
            else
            {
                double days = last.Level / perDay;
                _tileRemaining.SetValue(days >= 1
                    ? string.Format(CultureInfo.CurrentCulture, _t("stat_days_value"), days)
                    : string.Format(CultureInfo.CurrentCulture, _t("stat_hours_value"), days * 24));
            }
        }
        else
        {
            _tileDrain.SetValue(_t("stat_not_enough"));
            _tileRemaining.SetValue("—");
        }

        // 最後の充電: 充電フラグ、または 5% 以上の上昇
        DateTime? lastCharge = null;
        for (int i = _points.Count - 1; i >= 0; i--)
        {
            bool rose = i > 0 && _points[i].Level >= _points[i - 1].Level + 5;
            if (_points[i].Charging || rose) { lastCharge = _points[i].Time; break; }
        }
        _tileLastCharge.SetValue(lastCharge == null ? "—" : FormatAgo(DateTime.Now - lastCharge.Value));
    }

    private string FormatAgo(TimeSpan span)
    {
        if (span.TotalDays >= 1) return string.Format(CultureInfo.CurrentCulture, _t("ago_days"), (int)span.TotalDays);
        if (span.TotalHours >= 1) return string.Format(CultureInfo.CurrentCulture, _t("ago_hours"), (int)span.TotalHours);
        return string.Format(CultureInfo.CurrentCulture, _t("ago_minutes"), Math.Max(1, (int)span.TotalMinutes));
    }

    private void OpenCsv()
    {
        if (!File.Exists(_historyFile)) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(_historyFile) { UseShellExecute = true });
        }
        catch
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{_historyFile}\"") { UseShellExecute = true });
        }
    }

    // 退避ファイル (.old.csv) → 現在のファイルの順に読み込む
    internal static List<HistoryPoint> LoadHistory(string historyFile)
    {
        var points = new List<HistoryPoint>();
        foreach (string file in new[] { Path.ChangeExtension(historyFile, ".old.csv"), historyFile })
        {
            if (!File.Exists(file)) continue;
            try
            {
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(stream);
                string? line;
                while ((line = reader.ReadLine()) != null)
                {
                    string[] parts = line.Split(',');
                    if (parts.Length < 3) continue;
                    if (!DateTime.TryParseExact(parts[0], "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime time)) continue;
                    if (!byte.TryParse(parts[1], out byte level) || level > 100) continue;
                    points.Add(new HistoryPoint(time, level, parts[2].Trim() == "1"));
                }
            }
            catch { }
        }
        points.Sort((a, b) => a.Time.CompareTo(b.Time));
        return points;
    }
}

// 統計タイル: 小さな見出し + 大きな数値
internal sealed class StatTile : Control
{
    private readonly string _caption;
    private string _value = "—";

    public StatTile(string caption)
    {
        _caption = caption;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        Dock = DockStyle.Fill;
        Height = 64;
        Margin = new Padding(0, 0, 8, 0);
        BackColor = HistoryForm.SurfaceRaised;
    }

    public void SetValue(string value)
    {
        _value = value;
        Invalidate();
    }

    // 高さを DPI に合わせる (見出し + 大きな数値が収まる高さ)
    private void UpdateHeightForDpi() => Height = (int)Math.Ceiling(66 * DeviceDpi / 96f);

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        UpdateHeightForDpi();
    }

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        UpdateHeightForDpi();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        float scale = DeviceDpi / 96f;

        using var captionFont = new Font("Segoe UI", 8.5f);
        using var valueFont = new Font("Segoe UI Semibold", 15f);
        using var captionBrush = new SolidBrush(HistoryForm.TextSecondary);
        using var valueBrush = new SolidBrush(HistoryForm.TextPrimary);

        float pad = 10 * scale;
        g.DrawString(_caption, captionFont, captionBrush, pad, 6 * scale);
        g.DrawString(_value, valueFont, valueBrush, pad, 24 * scale);
    }
}

// 折れ線グラフ本体
internal sealed class ChartControl : Control
{
    private readonly Func<string, string> _t;
    private List<HistoryPoint> _points = new();
    private DateTime _start, _end;
    private int _hoverIndex = -1;

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public int Threshold { get; set; }

    public ChartControl(Func<string, string> translate)
    {
        _t = translate;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = HistoryForm.Surface;
    }

    public void SetData(List<HistoryPoint> points, DateTime start, DateTime end)
    {
        _points = points;
        _start = start;
        _end = end;
        _hoverIndex = -1;
        Invalidate();
    }

    private float S(float v) => v * DeviceDpi / 96f;

    private RectangleF PlotArea => new RectangleF(S(40), S(12), Math.Max(1, Width - S(40) - S(64)), Math.Max(1, Height - S(12) - S(44)));

    private float X(DateTime t, RectangleF plot) => plot.Left + (float)((t - _start).TotalSeconds / Math.Max(1, (_end - _start).TotalSeconds)) * plot.Width;
    private static float Y(double level, RectangleF plot) => plot.Bottom - (float)(level / 100.0) * plot.Height;

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        RectangleF plot = PlotArea;

        // 十字線: マウスに最も近い X のデータ点にスナップ
        int best = -1;
        float bestDist = float.MaxValue;
        for (int i = 0; i < _points.Count; i++)
        {
            if (_points[i].Time < _start || _points[i].Time > _end) continue;
            float d = Math.Abs(X(_points[i].Time, plot) - e.X);
            if (d < bestDist) { bestDist = d; best = i; }
        }
        if (e.X < plot.Left - S(8) || e.X > plot.Right + S(8)) best = -1;

        if (best != _hoverIndex)
        {
            _hoverIndex = best;
            Invalidate();
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hoverIndex = -1;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        RectangleF plot = PlotArea;
        using var axisFont = new Font("Segoe UI", 8f);
        using var mutedBrush = new SolidBrush(HistoryForm.TextMuted);
        using var secondaryBrush = new SolidBrush(HistoryForm.TextSecondary);
        using var gridPen = new Pen(HistoryForm.GridLine, S(1));

        // --- Y 軸グリッド (0〜100%) ---
        foreach (int level in new[] { 0, 25, 50, 75, 100 })
        {
            float y = Y(level, plot);
            g.DrawLine(gridPen, plot.Left, y, plot.Right, y);
            string label = $"{level}%";
            SizeF size = g.MeasureString(label, axisFont);
            g.DrawString(label, axisFont, mutedBrush, plot.Left - size.Width - S(4), y - size.Height / 2);
        }

        // --- X 軸の目盛り ---
        DrawTimeAxis(g, plot, axisFont, mutedBrush, gridPen);

        // 表示範囲に入る点 + 範囲の直前の 1 点 (左端から線をつなぐため)
        int first = _points.FindIndex(p => p.Time >= _start);
        if (first < 0) first = _points.Count;
        int from = Math.Max(0, first - 1);

        g.SetClip(plot);

        // --- 充電中の区間 (帯) ---
        using (var chargeBrush = new SolidBrush(Color.FromArgb(56, HistoryForm.ChargingColor)))
        {
            for (int i = from; i < _points.Count; i++)
            {
                if (!_points[i].Charging) continue;
                DateTime segEnd = i + 1 < _points.Count ? _points[i + 1].Time : _end;
                float x1 = X(_points[i].Time, plot), x2 = Math.Max(x1 + S(2), X(segEnd, plot));
                g.FillRectangle(chargeBrush, x1, plot.Top, x2 - x1, plot.Height);
            }
        }

        // --- 通知しきい値 (破線) ---
        if (Threshold > 0)
        {
            using var thresholdPen = new Pen(Color.FromArgb(180, HistoryForm.ThresholdColor), S(1)) { DashStyle = DashStyle.Dash };
            float ty = Y(Threshold, plot);
            g.DrawLine(thresholdPen, plot.Left, ty, plot.Right, ty);
        }

        // --- 残量の線 + 面 ---
        var pts = new List<PointF>();
        for (int i = from; i < _points.Count; i++)
        {
            pts.Add(new PointF(X(_points[i].Time, plot), Y(_points[i].Level, plot)));
        }

        if (pts.Count >= 2)
        {
            var area = new List<PointF>(pts) { new PointF(pts[^1].X, plot.Bottom), new PointF(pts[0].X, plot.Bottom) };
            using (var areaBrush = new LinearGradientBrush(new PointF(0, plot.Top), new PointF(0, plot.Bottom), Color.FromArgb(70, HistoryForm.SeriesColor), Color.FromArgb(0, HistoryForm.SeriesColor)))
            {
                g.FillPolygon(areaBrush, area.ToArray());
            }
            using var linePen = new Pen(HistoryForm.SeriesColor, S(2)) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawLines(linePen, pts.ToArray());
        }

        g.ResetClip();

        // しきい値のラベル (右側の余白に)
        if (Threshold > 0)
        {
            string label = string.Format(CultureInfo.CurrentCulture, _t("chart_threshold"), Threshold);
            SizeF size = g.MeasureString(label, axisFont);
            g.DrawString(label, axisFont, secondaryBrush, plot.Right + S(4), Y(Threshold, plot) - size.Height / 2);
        }

        // --- 最新の点のマーカー + 直接ラベル ---
        if (pts.Count >= 1 && _points.Count > 0 && _points[^1].Time >= _start)
        {
            PointF lastPt = pts[^1];
            DrawMarker(g, lastPt);
            string label = $"{_points[^1].Level}%";
            using var labelFont = new Font("Segoe UI Semibold", 8.5f);
            using var primaryBrush = new SolidBrush(HistoryForm.TextPrimary);
            SizeF size = g.MeasureString(label, labelFont);
            // 右端付近ではしきい値ラベルと重ならないよう、点の左上に置く
            float lx = lastPt.X + S(8) + size.Width > plot.Right ? lastPt.X - size.Width - S(8) : lastPt.X + S(8);
            float ly = lastPt.Y - size.Height - S(4);
            if (ly < plot.Top) ly = lastPt.Y + S(6);
            g.DrawString(label, labelFont, primaryBrush, lx, ly);
        }

        // --- 凡例 (下部) ---
        DrawLegend(g, plot, axisFont, secondaryBrush);

        // --- ホバー: 十字線 + ツールチップ ---
        if (_hoverIndex >= 0 && _hoverIndex < _points.Count)
        {
            HistoryPoint p = _points[_hoverIndex];
            PointF pt = new PointF(X(p.Time, plot), Y(p.Level, plot));
            using (var hairPen = new Pen(HistoryForm.TextMuted, S(1)))
            {
                g.DrawLine(hairPen, pt.X, plot.Top, pt.X, plot.Bottom);
            }
            DrawMarker(g, pt);
            DrawTooltip(g, pt, p);
        }
    }

    private void DrawMarker(Graphics g, PointF pt)
    {
        float r = S(4.5f);
        using var ring = new SolidBrush(HistoryForm.Surface);
        using var fill = new SolidBrush(HistoryForm.SeriesColor);
        g.FillEllipse(ring, pt.X - r - S(2), pt.Y - r - S(2), (r + S(2)) * 2, (r + S(2)) * 2);
        g.FillEllipse(fill, pt.X - r, pt.Y - r, r * 2, r * 2);
    }

    private void DrawTooltip(Graphics g, PointF pt, HistoryPoint p)
    {
        string timeText = p.Time.ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture);
        string valueText = $"{p.Level}%" + (p.Charging ? $"  ⚡ {_t("charging")}" : "");

        using var timeFont = new Font("Segoe UI", 8f);
        using var valueFont = new Font("Segoe UI Semibold", 10f);
        SizeF timeSize = g.MeasureString(timeText, timeFont);
        SizeF valueSize = g.MeasureString(valueText, valueFont);

        float pad = S(8);
        float w = Math.Max(timeSize.Width, valueSize.Width) + pad * 2;
        float h = timeSize.Height + valueSize.Height + pad * 1.5f;
        float x = pt.X + S(12);
        if (x + w > Width - S(2)) x = pt.X - w - S(12);
        float y = Math.Clamp(pt.Y - h / 2, S(2), Height - h - S(2));

        var rect = new RectangleF(x, y, w, h);
        using (var path = RoundedRect(rect, S(4)))
        using (var bg = new SolidBrush(Color.FromArgb(0x30, 0x30, 0x2e)))
        using (var border = new Pen(HistoryForm.GridLine, S(1)))
        {
            g.FillPath(bg, path);
            g.DrawPath(border, path);
        }

        using var secondary = new SolidBrush(HistoryForm.TextSecondary);
        using var primary = new SolidBrush(HistoryForm.TextPrimary);
        g.DrawString(timeText, timeFont, secondary, x + pad, y + pad * 0.6f);
        g.DrawString(valueText, valueFont, primary, x + pad, y + pad * 0.6f + timeSize.Height);
    }

    private void DrawLegend(Graphics g, RectangleF plot, Font font, Brush textBrush)
    {
        float y = plot.Bottom + S(24);
        float x = plot.Left;
        float sw = S(14);

        // 残量 (線)
        using (var linePen = new Pen(HistoryForm.SeriesColor, S(2)))
        {
            g.DrawLine(linePen, x, y + S(7), x + sw, y + S(7));
        }
        x += sw + S(4);
        string batteryLabel = _t("chart_battery");
        g.DrawString(batteryLabel, font, textBrush, x, y);
        x += g.MeasureString(batteryLabel, font).Width + S(14);

        // 充電中 (帯)
        using (var chargeBrush = new SolidBrush(Color.FromArgb(110, HistoryForm.ChargingColor)))
        {
            g.FillRectangle(chargeBrush, x, y + S(2), sw, S(10));
        }
        x += sw + S(4);
        string chargingLabel = _t("charging");
        g.DrawString(chargingLabel, font, textBrush, x, y);
        x += g.MeasureString(chargingLabel, font).Width + S(14);

        // しきい値 (破線)
        if (Threshold > 0)
        {
            using var thresholdPen = new Pen(HistoryForm.ThresholdColor, S(1.5f)) { DashStyle = DashStyle.Dash };
            g.DrawLine(thresholdPen, x, y + S(7), x + sw, y + S(7));
            x += sw + S(4);
            g.DrawString(_t("chart_threshold_legend"), font, textBrush, x, y);
        }
    }

    private void DrawTimeAxis(Graphics g, RectangleF plot, Font font, Brush brush, Pen gridPen)
    {
        TimeSpan span = _end - _start;
        TimeSpan[] steps =
        {
            TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(30), TimeSpan.FromHours(1), TimeSpan.FromHours(3),
            TimeSpan.FromHours(6), TimeSpan.FromHours(12), TimeSpan.FromDays(1), TimeSpan.FromDays(2),
            TimeSpan.FromDays(5), TimeSpan.FromDays(7), TimeSpan.FromDays(14), TimeSpan.FromDays(30), TimeSpan.FromDays(90)
        };

        // 幅に応じて目盛りが多くなりすぎない間隔を選ぶ (1 目盛りあたり最低 ~80px)
        int maxTicks = Math.Max(2, (int)(plot.Width / S(80)));
        TimeSpan step = steps[^1];
        foreach (TimeSpan s in steps)
        {
            if (span.TotalSeconds / s.TotalSeconds <= maxTicks) { step = s; break; }
        }

        string format = step < TimeSpan.FromDays(1) ? (span > TimeSpan.FromDays(1) ? "MM/dd HH:mm" : "HH:mm") : "MM/dd";

        // 間隔の倍数に揃えた最初の目盛り
        long stepTicks = step.Ticks;
        DateTime tick = step >= TimeSpan.FromDays(1)
            ? _start.Date.AddDays(1)
            : new DateTime((_start.Ticks / stepTicks + 1) * stepTicks);

        using var tickPen = new Pen(Color.FromArgb(90, HistoryForm.GridLine), S(1)) { DashStyle = DashStyle.Dot };
        for (; tick < _end; tick = tick.Add(step))
        {
            float x = X(tick, plot);
            g.DrawLine(tickPen, x, plot.Top, x, plot.Bottom);
            string label = tick.ToString(format, CultureInfo.CurrentCulture);
            SizeF size = g.MeasureString(label, font);
            g.DrawString(label, font, brush, x - size.Width / 2, plot.Bottom + S(4));
        }
    }

    private static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        float d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(r.Left, r.Top, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
