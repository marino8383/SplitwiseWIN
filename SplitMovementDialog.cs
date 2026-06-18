using System.Globalization;
using System.Drawing;

namespace SplitwiseUploader;

/// <summary>
/// Suddivide un movimento in più parti (importo + tag), ognuna inviabile o no a Splitwise.
/// Le parti devono sommare all'importo del movimento. Il movimento originale verrà escluso dai totali
/// (resta in archivio come àncora anti-duplicati), e le parti contano al suo posto.
/// </summary>
public class SplitMovementDialog : Form
{
    public sealed class Part { public decimal Amount; public string Tag = ""; public bool Send; }

    private readonly decimal _total;
    private readonly DataGridView _grid;
    private readonly Label _lblSum;

    public List<Part> Parts { get; } = new();

    public SplitMovementDialog(ExpenseRecord parent, List<string> tags)
    {
        _total = Math.Round(parent.Amount, 2);

        Text = "Suddividi movimento";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimizeBox = false; MaximizeBox = false;
        ClientSize = new Size(560, 360);

        var head = new Label
        {
            Dock = DockStyle.Top, Height = 56, Padding = new Padding(10, 8, 10, 0),
            Text = $"{parent.Date:dd/MM/yyyy}  {parent.Description}\nImporto da suddividere: {_total:0.00} €"
        };

        _grid = new DataGridView
        {
            Dock = DockStyle.Fill, AllowUserToAddRows = true, RowHeadersVisible = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None,
            EditMode = DataGridViewEditMode.EditOnEnter
        };
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Importo €", Name = "colAmt", Width = 110 });
        var tagCol = new DataGridViewComboBoxColumn { HeaderText = "Tag", Name = "colTag", Width = 240,
            FlatStyle = FlatStyle.Flat };
        tagCol.Items.AddRange(tags.Cast<object>().ToArray());
        _grid.Columns.Add(tagCol);
        _grid.Columns.Add(new DataGridViewCheckBoxColumn { HeaderText = "Invia a Splitwise", Name = "colSend", Width = 130 });
        _grid.DataError += (_, e) => e.ThrowException = false;
        _grid.CellValueChanged += (_, _) => UpdateSum();
        _grid.CurrentCellDirtyStateChanged += (_, _) =>
        { if (_grid.IsCurrentCellDirty) _grid.CommitEdit(DataGridViewDataErrorContexts.Commit); };

        // due parti precompilate a metà dell'importo (caso più comune)
        var half = Math.Round(_total / 2, 2);
        _grid.Rows.Add((_total - half).ToString("0.00"), null, false);
        _grid.Rows.Add(half.ToString("0.00"), null, false);

        _lblSum = new Label { Dock = DockStyle.Bottom, Height = 26, Padding = new Padding(10, 4, 10, 0) };

        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 48 };
        var ok = new Button { Text = "Conferma", Left = 10, Top = 8, Width = 120, Height = 32, DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "Annulla", Left = 138, Top = 8, Width = 100, Height = 32, DialogResult = DialogResult.Cancel };
        ok.Click += (_, _) => OnConfirm();
        AcceptButton = null; CancelButton = cancel;   // niente conferma con Invio (si edita in griglia)
        bottom.Controls.AddRange(new Control[] { ok, cancel });

        Controls.Add(_grid);
        Controls.Add(_lblSum);
        Controls.Add(bottom);
        Controls.Add(head);
        UpdateSum();
    }

    private decimal SumParts()
    {
        decimal sum = 0;
        foreach (DataGridViewRow r in _grid.Rows)
        {
            if (r.IsNewRow) continue;
            if (TryAmount(r.Cells["colAmt"].Value, out var a)) sum += a;
        }
        return Math.Round(sum, 2);
    }

    private void UpdateSum()
    {
        var sum = SumParts();
        var diff = Math.Round(_total - sum, 2);
        _lblSum.Text = diff == 0
            ? $"Somma parti: {sum:0.00} € — OK (corrisponde al totale)"
            : $"Somma parti: {sum:0.00} € — manca {diff:0.00} € sul totale di {_total:0.00} €";
        _lblSum.ForeColor = diff == 0 ? Color.Green : Color.Firebrick;
    }

    private void OnConfirm()
    {
        Parts.Clear();
        foreach (DataGridViewRow r in _grid.Rows)
        {
            if (r.IsNewRow) continue;
            if (!TryAmount(r.Cells["colAmt"].Value, out var a) || a <= 0) continue;
            var tag = Convert.ToString(r.Cells["colTag"].Value) ?? "";
            var send = r.Cells["colSend"].Value is bool b && b;
            Parts.Add(new Part { Amount = a, Tag = tag.Trim(), Send = send });
        }
        if (Parts.Count < 2)
        {
            MessageBox.Show("Inserisci almeno due parti con importo valido.", "Suddividi");
            DialogResult = DialogResult.None; return;
        }
        if (Math.Round(Parts.Sum(p => p.Amount), 2) != _total)
        {
            MessageBox.Show($"La somma delle parti deve essere {_total:0.00} €.", "Suddividi");
            DialogResult = DialogResult.None; return;
        }
    }

    private static bool TryAmount(object? v, out decimal value)
    {
        value = 0;
        var s = (Convert.ToString(v) ?? "").Replace("€", "").Replace(" ", "").Replace(",", ".").Trim();
        return s.Length > 0 && decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out value);
    }
}
