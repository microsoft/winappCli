using System;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows.Forms;

static class Program
{
    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }
}

sealed class MainForm : Form
{
    // Picked per launch so the total can only be learned from the running app.
    readonly decimal _taxRate = new Random().Next(5, 13) / 100m;
    readonly decimal _shipping = new Random().Next(3, 10);
    readonly TextBox _qty = new() { Name = "QuantityBox", AccessibleName = "Quantity", Left = 130, Top = 20, Width = 120 };
    readonly TextBox _price = new() { Name = "UnitPriceBox", AccessibleName = "Unit price", Left = 130, Top = 55, Width = 120 };
    readonly Button _calc = new() { Name = "CalculateButton", Text = "Calculate", AccessibleName = "Calculate", Left = 130, Top = 90, Width = 120 };
    readonly Label _total = new() { Name = "TotalValue", Text = "-", Left = 130, Top = 130, Width = 200 };
    static readonly string LogPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "OrderCalculator", "events.jsonl");

    public MainForm()
    {
        Text = "Order Calculator";
        ClientSize = new Size(360, 180);
        Controls.Add(new Label { Text = "Quantity", Left = 20, Top = 23, AutoSize = true });
        Controls.Add(new Label { Text = "Unit price", Left = 20, Top = 58, AutoSize = true });
        Controls.Add(new Label { Text = "Total", Left = 20, Top = 130, AutoSize = true });
        Controls.AddRange(new Control[] { _qty, _price, _calc, _total });
        _calc.Click += (_, _) => Calculate();
        Log(new { evt = "start" });
    }

    void Calculate()
    {
        if (!int.TryParse(_qty.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var q) ||
            !decimal.TryParse(_price.Text.Trim().TrimStart('$'), NumberStyles.Number, CultureInfo.InvariantCulture, out var p))
        {
            _total.Text = "Invalid input";
            Log(new { evt = "calculate", qty = _qty.Text, price = _price.Text, total = (string?)null });
            return;
        }
        var total = Math.Round(q * p * (1 + _taxRate) + _shipping, 2);
        _total.Text = total.ToString("0.00", CultureInfo.InvariantCulture);
        Log(new { evt = "calculate", qty = q.ToString(CultureInfo.InvariantCulture), price = p.ToString(CultureInfo.InvariantCulture), total = _total.Text });
    }

    static void Log(object o)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.AppendAllText(LogPath, JsonSerializer.Serialize(o) + Environment.NewLine);
        }
        catch { }
    }
}
