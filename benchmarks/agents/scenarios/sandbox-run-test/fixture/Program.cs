using Microsoft.Data.Sqlite;

namespace ContosoClock;

static class Program
{
    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();
        using var db = new SqliteConnection("Data Source=clock.db");
        db.Open();
        Application.Run(new Form { Text = "Contoso Clock" });
    }
}
