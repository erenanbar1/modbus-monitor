using System;
using System.IO.Ports;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace STS.WinForms;

public sealed class AddDeviceForm : Form
{
    private readonly NumericUpDown _numSlave = new() { Minimum = 1, Maximum = 247, Value = 1, Width = 80 };
    private readonly NumericUpDown _numBaud = new() { Minimum = 1200, Maximum = 921600, Increment = 1200, Value = 19200, Width = 100 };
    private readonly NumericUpDown _numDataBits = new() { Minimum = 5, Maximum = 8, Value = 8, Width = 60 };
    private readonly ComboBox _cmbStopBits = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 80 };
    private readonly ComboBox _cmbParity = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 80 };
    // Allow free text entry for COM port (user can type e.g. "COM2")
    private readonly ComboBox _cmbPort = new() { DropDownStyle = ComboBoxStyle.DropDown, Width = 100 };
    private readonly NumericUpDown _numTimeout = new() { Minimum = 50, Maximum = 5000, Increment = 50, Value = 100, Width = 80 };

    public int SlaveId => (int)_numSlave.Value;
    public int Baud => (int)_numBaud.Value;
    public int DataBits => (int)_numDataBits.Value;
    public StopBits StopBits => (StopBits)_cmbStopBits.SelectedItem!;
    public Parity Parity => (Parity)_cmbParity.SelectedItem!;
    public string PortName => _cmbPort.Text.Trim();
    public int TimeoutMs => (int)_numTimeout.Value;

    public AddDeviceForm()
    {
        Text = "Add Device";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = MinimizeBox = false;
        AcceptButton = new Button();
        Width = 320;
        Height = 340;

        _cmbStopBits.Items.AddRange(new object[] { StopBits.One, StopBits.Two });
        _cmbParity.Items.AddRange(Enum.GetValues(typeof(Parity)).Cast<object>().ToArray());
        _cmbPort.Items.AddRange(SerialPort.GetPortNames().DefaultIfEmpty("COM4").Cast<object>().ToArray());

        _cmbPort.SelectedItem = _cmbPort.Items.Cast<object>().FirstOrDefault(i => (string)i == "COM4")
                                ?? (_cmbPort.Items.Count > 0 ? _cmbPort.Items[0] : null);
        _cmbParity.SelectedItem = Parity.Even;
        _cmbStopBits.SelectedItem = StopBits.One;

        var btnOk = new Button { Text = "OK", DialogResult = DialogResult.OK, Left = 70, Top = 240, Width = 80, Height = 30 };
        var btnCancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Left = 160, Top = 240, Width = 80, Height = 30 };
        AcceptButton = btnOk;
        CancelButton = btnCancel;

        Controls.AddRange(new Control[]
        {
            L("Slave Id", 10, 10), SetLocation(_numSlave, 120, 8),
            L("Baud", 10, 40), SetLocation(_numBaud, 120, 38),
            L("Data Bits", 10, 70), SetLocation(_numDataBits, 120, 68),
            L("Stop Bits", 10, 100), SetLocation(_cmbStopBits, 120, 98),
            L("Parity", 10, 130), SetLocation(_cmbParity, 120, 128),
            L("COM Port", 10, 160), SetLocation(_cmbPort, 120, 158),
            L("Timeout ms", 10, 190), SetLocation(_numTimeout, 120, 188),
            btnOk, btnCancel
        });

        btnOk.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(PortName))
            {
                MessageBox.Show(this, "Enter a COM port (e.g. COM2).", "Validation",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                DialogResult = DialogResult.None;
                return;
            }
            // Optional light validation: COM + digits
            if (!Regex.IsMatch(PortName, @"^COM\d+$", RegexOptions.IgnoreCase))
            {
                if (MessageBox.Show(this, "Port name not in standard form (COMn). Continue?",
                        "Confirm", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.No)
                {
                    DialogResult = DialogResult.None;
                }
            }
        };
    }

    private static Label L(string text, int x, int y) => new() { Text = text, Left = x, Top = y + 3, AutoSize = true };

    private static T SetLocation<T>(T control, int left, int top) where T : Control
    {
        control.Left = left;
        control.Top = top;
        return control;
    }
}