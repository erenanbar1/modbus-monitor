using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO.Ports;
using System.Linq;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using STS.Core;

namespace STS.WinForms;

public sealed class MainForm : Form
{
    private const int DefaultBusIntervalMs = 50;

    private readonly DataGridView _grid = new()
    {
        Dock = DockStyle.Fill,
        ReadOnly = true,
        AutoGenerateColumns = false,
        AllowUserToAddRows = false,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect
    };

    private readonly BindingList<DeviceRow> _rows = new();
    private readonly System.Windows.Forms.Timer _uiRefreshTimer = new() { Interval = 200 };
    private readonly Label _lblElapsed = new()
    {
        Dock = DockStyle.Top,
        Height = 22,
        Text = "Elapsed: 00:00:00",
        TextAlign = ContentAlignment.MiddleLeft,
        Padding = new Padding(6, 0, 0, 0)
    };
    private DateTime? _startedAt;

    // Per COM port: independent polling task (no UI-thread blocking)
    private sealed class BusContext
    {
        public SerialSettings Settings;
        public SerialModbusClient Client;
        public Poller Poller;
        private readonly CancellationTokenSource _cts = new();
        private Task? _task;
        public BusContext(SerialSettings s, SerialModbusClient c, Poller p)
        { Settings = s; Client = c; Poller = p; }

        public void Start(Control ui, int intervalMs)
        {
            _task = Task.Run(async () =>
            {
                while (!_cts.IsCancellationRequested)
                {
                    try
                    {
                        Poller.Poll(Client);
                    }
                    catch (Exception ex)
                    {
                        // marshal error to UI and stop this bus
                        try
                        {
                            ui.BeginInvoke(new Action(() =>
                                MessageBox.Show(ui, $"Polling stopped on {Settings.Port}: {ex.Message}", "Error",
                                    MessageBoxButtons.OK, MessageBoxIcon.Error)));
                        }
                        catch { /* ignore */ }
                        break;
                    }
                    try { await Task.Delay(intervalMs, _cts.Token); }
                    catch (TaskCanceledException) { break; }
                }
            }, _cts.Token);
        }

        public void Stop()
        {
            try { _cts.Cancel(); } catch { }
            try { _task?.Wait(500); } catch { }
            try { Client.Dispose(); } catch { }
        }
    }

    private readonly List<BusContext> _buses = new();

    private record SerialSettings(string Port, int Baud, Parity Parity, int DataBits, StopBits StopBits, int TimeoutMs);

    private sealed class DeviceRow
    {
        public string Port { get; set; } = "";
        public byte Id { get; set; }
        public bool Online { get; set; }
        public string RawStatus { get; set; } = "";
        public DateTime? OfflineSince { get; set; }
        public string Status
        {
            get
            {
                if (Online) return "ONLINE";
                if (OfflineSince is null) return "OFFLINE";
                var el = DateTime.UtcNow - OfflineSince.Value;
                return $"OFFLINE {Fmt(el)}";
            }
            set { RawStatus = value; }
        }
        public double Vout { get; set; }
        public double Iout { get; set; }
        public double Src1 { get; set; }
        public double Src2 { get; set; }
        public double Diff { get; set; }
        public double Freq { get; set; }
        public string Active { get; set; } = "";
        public int ReadMs { get; set; }
        public int Cycle { get; set; }

        private static string Fmt(TimeSpan ts)
        {
            if (ts.TotalSeconds < 60) return $"{(int)ts.TotalSeconds}s";
            if (ts.TotalMinutes < 60) return $"{(int)ts.TotalMinutes}m {ts.Seconds:D2}s";
            if (ts.TotalHours < 24) return $"{(int)ts.TotalHours}h {ts.Minutes:D2}m";
            return $"{(int)ts.TotalDays}d {(int)(ts.Hours)}h";
        }
    }

    public MainForm()
    {
        Text = "STS Monitor";
        Width = 1000;
        Height = 400;

        Controls.Add(_grid);
        Controls.Add(_lblElapsed);

        InitGrid();

        var cms = new ContextMenuStrip();
        cms.Items.Add(new ToolStripMenuItem("Add Device", null, (_, _) => AddDevice()));
        cms.Items.Add(new ToolStripSeparator());
        cms.Items.Add(new ToolStripMenuItem("Remove Selected Device", null, (_, _) => RemoveSelectedDevice())
        {
            ShortcutKeys = Keys.Delete
        });
        ContextMenuStrip = cms;
        _grid.ContextMenuStrip = cms;
        _grid.KeyDown += (_, e) => { if (e.KeyCode == Keys.Delete) RemoveSelectedDevice(); };

        _uiRefreshTimer.Tick += (_, _) =>
        {
            if (_startedAt.HasValue)
            {
                var el = DateTime.UtcNow - _startedAt.Value;
                _lblElapsed.Text = $"Elapsed: {el:hh\\:mm\\:ss}";
            }
            for (int i = 0; i < _rows.Count; i++)
                if (!_rows[i].Online) _rows.ResetItem(i);
        };
        _uiRefreshTimer.Start();
    }

    private void InitGrid()
    {
        void Col(string name, string prop, int w = 70)
            => _grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                DataPropertyName = prop,
                HeaderText = name,
                Width = w
            });

        Col("Port", nameof(DeviceRow.Port), 70);
        Col("ID", nameof(DeviceRow.Id), 40);
        Col("Online", nameof(DeviceRow.Online), 55);
        Col("Status", nameof(DeviceRow.Status), 130);
        Col("Vout", nameof(DeviceRow.Vout));
        Col("Iout", nameof(DeviceRow.Iout));
        Col("Src1", nameof(DeviceRow.Src1));
        Col("Src2", nameof(DeviceRow.Src2));
        Col("Diff", nameof(DeviceRow.Diff));
        Col("Freq", nameof(DeviceRow.Freq));
        Col("Active", nameof(DeviceRow.Active), 55);
        Col("Read ms", nameof(DeviceRow.ReadMs), 65);
        Col("Cycle", nameof(DeviceRow.Cycle), 55);
        _grid.DataSource = _rows;
    }

    private void AddDevice()
    {
        using var dlg = new AddDeviceForm();
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        var settings = new SerialSettings(dlg.PortName, dlg.Baud, dlg.Parity, dlg.DataBits, dlg.StopBits, dlg.TimeoutMs);
        //check if a bus with the same settings already exists
        var bus = _buses.FirstOrDefault(b => Same(settings, b.Settings));

        if (bus == null)
        {
            // No existing bus with same settings, create a new one
            var conflict = _buses.FirstOrDefault(b => b.Settings.Port.Equals(settings.Port, StringComparison.OrdinalIgnoreCase));
            if (conflict != null)
            {
                MessageBox.Show(this, $"Port {settings.Port} already opened with different settings.", "Mismatch",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            try
            {
                var client = new SerialModbusClient(settings.Port, settings.Baud, settings.Parity,
                    settings.DataBits, settings.StopBits, settings.TimeoutMs);
                var poller = new Poller(Enumerable.Empty<STSDevice>());
                bus = new BusContext(settings, client, poller);
                poller.OnDeviceUpdate += u => OnDeviceUpdate(bus.Settings.Port, u);
                bus.Start(this, DefaultBusIntervalMs); // background loop per COM
                _buses.Add(bus);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Failed to open serial port {settings.Port}: {ex.Message}", "Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
        }

        // Start elapsed tracking when first device added
        if (!_startedAt.HasValue)
        {
            _startedAt = DateTime.UtcNow;
            _lblElapsed.Text = "Elapsed: 00:00:00";
        }

        var dev = new STSDevice(dlg.SlaveId);
        bus.Poller.AddDevice(dev);
        if (_rows.All(r => r.Port != settings.Port || r.Id != dev.Id))
            _rows.Add(new DeviceRow { Port = settings.Port, Id = dev.Id, RawStatus = "NEW" });
    }

    private void RemoveSelectedDevice()
    {
        if (_grid.SelectedRows.Count == 0) return;
        var row = _grid.SelectedRows[0].DataBoundItem as DeviceRow;
        if (row == null) return;

        var bus = _buses.FirstOrDefault(b => b.Settings.Port.Equals(row.Port, StringComparison.OrdinalIgnoreCase));
        if (bus == null) return;

        if (MessageBox.Show(this, $"Remove device {row.Id} on {row.Port}?",
                "Confirm", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return;

        if (bus.Poller.RemoveDevice(row.Id))
        {
            _rows.Remove(row);
            bool busEmpty = !_rows.Any(r => r.Port.Equals(bus.Settings.Port, StringComparison.OrdinalIgnoreCase));
            if (busEmpty)
            {
                bus.Stop();
                _buses.Remove(bus);
            }
        }
    }

    private static bool Same(SerialSettings a, SerialSettings b)
        => a.Port == b.Port && a.Baud == b.Baud && a.Parity == b.Parity
           && a.DataBits == b.DataBits && a.StopBits == b.StopBits && a.TimeoutMs == b.TimeoutMs;

    private void OnDeviceUpdate(string port, Poller.DeviceUpdate u)
    {
        if (InvokeRequired) { BeginInvoke(new Action(() => OnDeviceUpdate(port, u))); return; }
        var row = _rows.FirstOrDefault(r => r.Port == port && r.Id == u.Device.Id)
                  ?? AddRow(port, u.Device.Id);

        bool prevOnline = row.Online;
        row.Online = u.Device.Online;
        if (!row.Online && prevOnline) row.OfflineSince ??= DateTime.UtcNow;
        else if (row.Online && !prevOnline) row.OfflineSince = null;

        row.RawStatus = u.Status;
        row.Vout = u.Reading.OutputVoltageV;
        row.Iout = u.Reading.OutputCurrentA;
        row.Src1 = u.Reading.Source1VoltageV;
        row.Src2 = u.Reading.Source2VoltageV;
        row.Diff = u.Reading.DiffVoltageV;
        row.Freq = u.Reading.FrequencyHz;
        row.Active = u.Reading.ActiveSourceName;
        row.ReadMs = u.ReadTimeMs;
        row.Cycle = u.CycleNo;
        int idx = _rows.IndexOf(row);
        if (idx >= 0) _rows.ResetItem(idx);
    }

    private DeviceRow AddRow(string port, byte id)
    {
        var r = new DeviceRow { Port = port, Id = id, RawStatus = "NEW" };
        _rows.Add(r);
        return r;
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        base.OnFormClosing(e);
        _uiRefreshTimer.Stop();
        foreach (var b in _buses.ToList())
            b.Stop();
        _buses.Clear();
    }
}