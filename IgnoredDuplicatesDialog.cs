using System.Drawing;

namespace SplitwiseUploader;

/// <summary>
/// Elenco dei movimenti marcati "ignora nel controllo duplicati" (tenuti volutamente).
/// Permette di RIATTIVARLI (spunta "Riattiva") così torneranno a essere valutati dai controlli futuri.
/// </summary>
public class IgnoredDuplicatesDialog : Form
{
    private readonly DataGridView _grid;

    /// <summary>Id da riportare nel controllo duplicati (DupIgnore = 0).</summary>
    public List<long> IdsToReenable { get; } = new();

    public IgnoredDuplicatesDialog(List<ExpenseRecord> ignored)
    {
        Text = "Duplicati ignorati";
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false; MaximizeBox = true;
        ClientSize = new Size(860, 480);

        var lbl = new Label
        {
            Dock = DockStyle.Top, Height = 34, Padding = new Padding(8, 8, 8, 0),
            Text = $"{ignored.Count} movimenti ignorati nel controllo duplicati. Spunta \"Riattiva\" per rimetterli in valutazione."
        };

        _grid = new DataGridView
        {
            Dock = DockStyle.Fill, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
            RowHeadersVisible = false, SelectionMode = DataGridViewSelectionMode.CellSelect,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None
        };
        _grid.Columns.Add(new DataGridViewCheckBoxColumn { HeaderText = "Riattiva", Width = 70, Name = "colReen" });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Data", Width = 90, Name = "colDate" });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Tipo", Width = 70, Name = "colKind" });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Importo €", Width = 90, Name = "colAmt" });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Fonte", Width = 80, Name = "colSrc" });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Descrizione", Width = 430, Name = "colDesc" });
        foreach (DataGridViewColumn c in _grid.Columns)
            if (c.Name != "colReen") c.ReadOnly = true;

        foreach (var e in ignored.OrderBy(x => x.Date))
        {
            int idx = _grid.Rows.Add(
                false,
                e.Date?.ToString("dd/MM/yyyy") ?? "",
                e.Direction == ExpenseDirection.Entrata ? "Entrata" : "Uscita",
                e.Amount.ToString("0.00"),
                e.Source.ToString(),
                e.Description);
            _grid.Rows[idx].Tag = e.Id;
        }

        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 48 };
        var ok = new Button { Text = "Riattiva selezionate", Left = 8, Top = 8, Width = 170, Height = 32, DialogResult = DialogResult.OK };
        var close = new Button { Text = "Chiudi", Left = 186, Top = 8, Width = 100, Height = 32, DialogResult = DialogResult.Cancel };
        ok.Click += (_, _) =>
        {
            _grid.EndEdit();
            IdsToReenable.Clear();
            foreach (DataGridViewRow r in _grid.Rows)
                if (r.Tag is long id && r.Cells["colReen"].Value is bool b && b)
                    IdsToReenable.Add(id);
        };
        AcceptButton = ok; CancelButton = close;
        bottom.Controls.AddRange(new Control[] { ok, close });

        Controls.Add(_grid);
        Controls.Add(bottom);
        Controls.Add(lbl);
    }
}
