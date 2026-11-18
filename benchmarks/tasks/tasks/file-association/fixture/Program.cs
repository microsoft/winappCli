namespace NoteViewer;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        var form = new Form { Text = "Note Viewer", Width = 600, Height = 400 };
        var box = new TextBox { Multiline = true, ReadOnly = true, Dock = DockStyle.Fill, ScrollBars = ScrollBars.Vertical };
        if (args.Length > 0 && File.Exists(args[0]))
        {
            form.Text = $"Note Viewer - {Path.GetFileName(args[0])}";
            box.Text = File.ReadAllText(args[0]);
        }
        else
        {
            box.Text = "Open a .ctnote file to view it.";
        }
        form.Controls.Add(box);
        Application.Run(form);
    }
}
