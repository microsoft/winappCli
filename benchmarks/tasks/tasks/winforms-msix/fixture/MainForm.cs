namespace TaskBoard;

public class MainForm : Form
{
    private readonly ListBox _tasks = new() { Dock = DockStyle.Fill };
    private readonly TextBox _input = new() { Dock = DockStyle.Fill, PlaceholderText = "New task" };
    private readonly Button _add = new() { Text = "Add", Dock = DockStyle.Right, Width = 80 };

    public MainForm()
    {
        Text = "TaskBoard";
        Width = 420;
        Height = 360;
        var top = new Panel { Dock = DockStyle.Top, Height = 30 };
        top.Controls.Add(_input);
        top.Controls.Add(_add);
        Controls.Add(_tasks);
        Controls.Add(top);
        _add.Click += (_, _) => AddTask();
        _input.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) AddTask(); };
    }

    private void AddTask()
    {
        if (string.IsNullOrWhiteSpace(_input.Text)) return;
        _tasks.Items.Add(_input.Text.Trim());
        _input.Clear();
    }
}
