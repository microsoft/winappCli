using System;
using System.IO;
using System.Windows.Forms;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        // Records each launch so checkers can confirm the packaged app really started.
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "BenchSimpleApp");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "launches.log"), $"{DateTime.UtcNow:o}|{Environment.ProcessPath}|{string.Join(' ', args)}{Environment.NewLine}");
        }
        catch { }
        ApplicationConfiguration.Initialize();
        var form = new Form { Text = "Simple App", Width = 400, Height = 200 };
        form.Controls.Add(new Label { Text = "Hello from Simple App", AutoSize = true, Left = 20, Top = 20 });
        Application.Run(form);
    }
}
