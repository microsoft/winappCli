namespace Ledgerline.Stock;

public partial class StockForm : Form
{
    private readonly DataGridView _grid = new() { Dock = DockStyle.Fill };

    public StockForm()
    {
        Text = "Ledgerline Stock";
        Controls.Add(_grid);
        Load += (_, _) => ShowCostColumns(false);
    }

    private void ShowCostColumns(bool visible)
    {
        foreach (var name in new[] { "UnitCost", "Margin" })
        {
            if (_grid.Columns[name] is { } column) column.Visible = visible;
        }
    }
}
