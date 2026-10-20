using System.Text.Json;

namespace AccessLoop.AuditFixture;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Length is < 2 or > 3 || args[0] != "--connection-file"
            || (args.Length == 3 && args[2] != "--with-owned-window"))
            throw new ArgumentException("Use --connection-file <new-json-path> [--with-owned-window].");
        var connectionFile = Path.GetFullPath(args[1]);
        if (File.Exists(connectionFile))
            throw new IOException("The fixture connection file must be new.");
        ApplicationConfiguration.Initialize();
        using var form = new AuditForm(connectionFile, args.Length == 3);
        Application.Run(form);
    }
}

internal sealed class AuditForm : Form
{
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 1000 };
    private readonly Form excluded = new()
    {
        Text = "AccessLoop excluded window",
        Width = 330,
        Height = 170,
        StartPosition = FormStartPosition.Manual,
        Location = new Point(880, 100)
    };
    private readonly Label status = new() { Name = "StatusLabel", AutoSize = true, Text = "Waiting for automatic update" };
    private readonly Bitmap icon = new(28, 28);
    private int ticks;

    public AuditForm(string connectionFile, bool withOwnedWindow)
    {
        Text = "AccessLoop audit fixture";
        Name = "AuditFixture";
        Width = 800;
        Height = 570;
        StartPosition = FormStartPosition.Manual;
        Location = new Point(70, 90);

        using (var graphics = Graphics.FromImage(icon))
        {
            graphics.Clear(Color.Transparent);
            graphics.FillRectangle(Brushes.Navy, 3, 3, 22, 22);
            graphics.FillRectangle(Brushes.White, 8, 6, 12, 7);
        }

        var panel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            Padding = new Padding(20),
            AutoScroll = true
        };
        panel.Controls.Add(new Label
        {
            AutoSize = true,
            Text = "Owned test data: missing names, text attributes, events and exact-window scope."
        });
        panel.Controls.Add(new TextBox
        {
            Name = "",
            AccessibleName = "Editor without an application-assigned AutomationId",
            Width = 670,
            Text = "Non-secret test text"
        });
        panel.Controls.Add(new Button
        {
            Name = "MissingNameButton",
            Text = "",
            AccessibleName = null,
            Image = icon,
            Width = 90,
            Height = 42
        });
        panel.Controls.Add(new RichTextBox
        {
            Name = "LowContrastEditor",
            AccessibleName = "Low contrast editor",
            Width = 670,
            Height = 60,
            Text = "Deliberately low-contrast editable text",
            Font = new Font("Segoe UI", 12, FontStyle.Regular, GraphicsUnit.Point),
            ForeColor = Color.FromArgb(170, 170, 170),
            BackColor = Color.FromArgb(190, 190, 190)
        });
        var mixed = new RichTextBox
        {
            Name = "MixedFormattingEditor",
            AccessibleName = "Mixed formatting editor",
            Width = 670,
            Height = 60,
            Text = "Dark text and light text",
            BackColor = Color.White,
            Font = new Font("Segoe UI", 12, FontStyle.Regular, GraphicsUnit.Point)
        };
        mixed.Select(0, 9);
        mixed.SelectionColor = Color.Black;
        mixed.Select(10, mixed.TextLength - 10);
        mixed.SelectionColor = Color.LightGray;
        mixed.Select(0, 0);
        panel.Controls.Add(mixed);
        panel.Controls.Add(new TextBox
        {
            Name = "FixturePassword",
            AccessibleName = "Test password",
            UseSystemPasswordChar = true,
            Text = "fixture-only",
            Width = 670
        });
        var checkbox = new CheckBox { Name = "FixtureCheckbox", AutoSize = true, Text = "A named checkbox" };
        checkbox.CheckedChanged += (_, _) => status.Text = checkbox.Checked ? "Checked" : "Unchecked";
        panel.Controls.Add(checkbox);
        panel.Controls.Add(status);
        Controls.Add(panel);

        excluded.Controls.Add(new Button
        {
            Name = "ExcludedMissingNameButton",
            Text = "",
            AccessibleName = null,
            Image = icon,
            Location = new Point(30, 30),
            Width = 90,
            Height = 42
        });
        Shown += (_, _) =>
        {
            if (withOwnedWindow)
                excluded.Show(this);
            Activate();
            Directory.CreateDirectory(Path.GetDirectoryName(connectionFile)!);
            var temporary = connectionFile + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    JsonSerializer.Serialize(stream, new
                    {
                        processId = Environment.ProcessId,
                        windowHandle = (long)Handle,
                        excludedWindowHandle = withOwnedWindow ? (long)excluded.Handle : 0
                    });
                }
                File.Move(temporary, connectionFile);
            }
            finally
            {
                File.Delete(temporary);
            }
            timer.Start();
        };
        timer.Tick += (_, _) =>
        {
            ticks++;
            var notified = status.AccessibilityObject.RaiseAutomationNotification(
                System.Windows.Forms.Automation.AutomationNotificationKind.Other,
                System.Windows.Forms.Automation.AutomationNotificationProcessing.ImportantMostRecent,
                $"Fixture notification {ticks}");
            status.Text = $"Automatic status update {ticks}; notification accepted: {notified}";
            status.Enabled = ticks % 2 == 0;
            if (ticks >= 600)
                Close();
        };
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            timer.Dispose();
            excluded.Dispose();
            icon.Dispose();
        }
        base.Dispose(disposing);
    }
}
