using System.Drawing;

namespace SplitwiseUploader;

/// <summary>
/// Finestra di revisione dei duplicati LOCALI. Due modalità commutabili:
///  - ESATTI: stessa data + importo + DESCRIZIONE normalizzata → sicuramente duplicati;
///  - LARGHI: stessa data + importo (la descrizione può variare tra fonti, es. .xls vs testo web).
/// In ogni gruppo è pre-spuntata da eliminare ogni riga TRANNE la più recente (quella appena importata).
/// "Ignora gruppo" tiene tutte le righe del gruppo e non le ripropone più (anche ai controlli futuri).
/// </summary>
public class DuplicatesDialog : Form
{
    private readonly List<ExpenseRecord> _all;
    private readonly DataGridView _grid;
    private readonly RadioButton _rbExact;
    private readonly RadioButton _rbLoose;
    private readonly RadioButton _rbPrefix;
    private readonly Label _summary;
    private readonly Dictionary<long, List<long>> _groupOf = new();   // id riga → ids del suo gruppo
    private readonly List<ExpenseRecord> _ignored;                    // già marcati "ignora"
    private readonly Button _btnIgnoredView;
    private readonly Dictionary<long, DateTime> _batchWhen;           // id registro → data/ora import

    /// <summary>Id dei movimenti che l'utente ha scelto di eliminare.</summary>
    public List<long> IdsToDelete { get; } = new();

    /// <summary>Id dei movimenti da marcare "ignora nel controllo duplicati" (tieni e non riproporre).</summary>
    public List<long> IdsToIgnore { get; } = new();

    /// <summary>Id dei movimenti da riportare nel controllo duplicati (tolti dagli ignorati).</summary>
    public List<long> IdsToReenable { get; } = new();

    public DuplicatesDialog(List<ExpenseRecord> liveRecords, List<ExpenseRecord>? ignored = null,
        Dictionary<long, DateTime>? batchWhen = null)
    {
        _all = liveRecords.Where(e => e.Date.HasValue && !e.DupIgnore).ToList();
        _ignored = (ignored ?? liveRecords.Where(e => e.DupIgnore)).ToList();
        _batchWhen = batchWhen ?? new Dictionary<long, DateTime>();

        Text = "Controlla duplicati";
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false; MaximizeBox = true;
        ClientSize = new Size(900, 560);

        var top = new Panel { Dock = DockStyle.Top, Height = 96 };
        _rbExact = new RadioButton { Text = "Esatti: stessa data + importo + descrizione (duplicati certi)",
            Left = 10, Top = 6, Width = 560, Checked = true };
        _rbPrefix = new RadioButton { Text = "Simili: stessa data + importo + inizio descrizione uguale (≥ 50% della più corta)",
            Left = 10, Top = 28, Width = 620 };
        _rbLoose = new RadioButton { Text = "Larghi: stessa data + importo (la descrizione può variare del tutto)",
            Left = 10, Top = 50, Width = 560 };
        _rbExact.CheckedChanged += (_, _) => { if (_rbExact.Checked) Rebuild(); };
        _rbPrefix.CheckedChanged += (_, _) => { if (_rbPrefix.Checked) Rebuild(); };
        _rbLoose.CheckedChanged += (_, _) => { if (_rbLoose.Checked) Rebuild(); };
        _summary = new Label { Left = 10, Top = 74, Width = 860, Height = 18, Text = "" };
        top.Controls.AddRange(new Control[] { _rbExact, _rbPrefix, _rbLoose, _summary });

        _grid = new DataGridView
        {
            Dock = DockStyle.Fill, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
            RowHeadersVisible = false, SelectionMode = DataGridViewSelectionMode.CellSelect,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None
        };
        _grid.Columns.Add(new DataGridViewCheckBoxColumn { HeaderText = "Elimina", Width = 60, Name = "colDel" });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Data", Width = 90, Name = "colDate" });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Tipo", Width = 70, Name = "colKind" });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Importo €", Width = 90, Name = "colAmt" });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Fonte", Width = 80, Name = "colSrc" });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Stato", Width = 90, Name = "colStat" });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Registro (cod. — data/ora)", Width = 170, Name = "colReg" });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Descrizione", Width = 360, Name = "colDesc" });
        foreach (DataGridViewColumn c in _grid.Columns)
            if (c.Name != "colDel") c.ReadOnly = true;

        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 48 };
        var ok = new Button { Text = "Elimina selezionate", Left = 8, Top = 8, Width = 160, Height = 32, DialogResult = DialogResult.OK };
        var ignore = new Button { Text = "Ignora gruppo (tieni tutto)", Left = 176, Top = 8, Width = 200, Height = 32 };
        _btnIgnoredView = new Button { Text = $"Ignorati ({_ignored.Count})…", Left = 384, Top = 8, Width = 130, Height = 32 };
        var cancel = new Button { Text = "Chiudi", Left = 522, Top = 8, Width = 100, Height = 32, DialogResult = DialogResult.Cancel };
        _btnIgnoredView.Click += (_, _) =>
        {
            if (_ignored.Count == 0) { MessageBox.Show("Nessun duplicato ignorato.", "Duplicati ignorati"); return; }
            using var d = new IgnoredDuplicatesDialog(_ignored);
            if (d.ShowDialog(this) == DialogResult.OK && d.IdsToReenable.Count > 0)
            {
                foreach (var i in d.IdsToReenable) if (!IdsToReenable.Contains(i)) IdsToReenable.Add(i);
                // i riattivati tornano a essere valutati: rientrano in _all ed escono da _ignored
                var back = _ignored.Where(e => d.IdsToReenable.Contains(e.Id)).ToList();
                _all.AddRange(back);
                _ignored.RemoveAll(e => d.IdsToReenable.Contains(e.Id));
                _btnIgnoredView.Text = $"Ignorati ({_ignored.Count})…";
                Rebuild();
            }
        };
        ok.Click += (_, _) =>
        {
            _grid.EndEdit();
            IdsToDelete.Clear();
            foreach (DataGridViewRow r in _grid.Rows)
                if (r.Tag is long id && r.Cells["colDel"].Value is bool b && b)
                    IdsToDelete.Add(id);
            if (IdsToDelete.Count == 0)
            {
                MessageBox.Show("Nessuna riga spuntata da eliminare.", "Controlla duplicati");
                DialogResult = DialogResult.None;
            }
        };
        ignore.Click += (_, _) =>
        {
            if (_grid.CurrentRow?.Tag is not long id || !_groupOf.TryGetValue(id, out var ids))
            { MessageBox.Show("Seleziona una riga del gruppo da ignorare.", "Controlla duplicati"); return; }
            foreach (var i in ids) if (!IdsToIgnore.Contains(i)) IdsToIgnore.Add(i);
            _all.RemoveAll(e => ids.Contains(e.Id));   // sparisce da questa vista e dai controlli futuri
            Rebuild();
        };
        AcceptButton = ok; CancelButton = cancel;
        bottom.Controls.AddRange(new Control[] { ok, ignore, _btnIgnoredView, cancel });

        Controls.Add(_grid);
        Controls.Add(bottom);
        Controls.Add(top);

        Rebuild();
    }

    private void Rebuild()
    {
        // tutti i criteri partono da data + importo + direzione; poi si suddivide secondo la modalità
        var groups = new List<List<ExpenseRecord>>();
        foreach (var bucket in _all.GroupBy(e => $"{e.Date!.Value.Date.Ticks}|{Math.Round(e.Amount, 2)}|{(int)e.Direction}"))
        {
            var items = bucket.ToList();
            if (items.Count < 2) continue;

            if (_rbExact.Checked)
                groups.AddRange(items.GroupBy(e => MainForm.NormDesc(e.Description)).Where(g => g.Count() > 1).Select(g => g.ToList()));
            else if (_rbLoose.Checked)
                groups.Add(items);
            else   // Simili: prefisso comune ≥ 50% della descrizione più corta (clustering nel bucket)
                groups.AddRange(ClusterBySimilarity(items).Where(c => c.Count > 1));
        }
        groups = groups.OrderBy(g => g.First().Date).ToList();

        _groupOf.Clear();
        foreach (var g in groups)
        {
            var ids = g.Select(e => e.Id).ToList();
            foreach (var id in ids) _groupOf[id] = ids;
        }

        int total = groups.Sum(g => g.Count);
        int extra = groups.Sum(g => g.Count - 1);
        _summary.Text = groups.Count == 0
            ? "Nessun duplicato con questo criterio."
            : $"{groups.Count} gruppi · {total} righe · {extra} doppioni. Pre-spuntata da eliminare la versione MENO recente; tieni la più nuova.";

        _grid.SuspendLayout();
        _grid.Rows.Clear();
        int gi = 0;
        foreach (var g in groups)
        {
            // tieni la versione INVIATA se c'è (protegge il link Splitwise), altrimenti la PIÙ RECENTE (Id più alto)
            var keep = g.OrderByDescending(e => e.Status == ExpenseStatus.Inviata).ThenByDescending(e => e.Id).First();
            var band = (gi++ % 2 == 0) ? Color.White : Color.FromArgb(238, 244, 255);
            foreach (var e in g.OrderByDescending(x => x.Status == ExpenseStatus.Inviata).ThenByDescending(x => x.Id))
            {
                bool del = e.Id != keep.Id;
                string reg = e.ImportBatch == 0 ? "—"
                    : _batchWhen.TryGetValue(e.ImportBatch, out var w) ? $"#{e.ImportBatch}  {w:dd/MM/yyyy HH:mm}"
                    : $"#{e.ImportBatch}";
                int idx = _grid.Rows.Add(
                    del,
                    e.Date?.ToString("dd/MM/yyyy") ?? "",
                    e.Direction == ExpenseDirection.Entrata ? "Entrata" : "Uscita",
                    e.Amount.ToString("0.00"),
                    e.Source.ToString(),
                    e.Status switch
                    {
                        ExpenseStatus.Inviata => "Inviata",
                        ExpenseStatus.Archiviata => "Archiviata",
                        _ => "Da inviare"
                    },
                    reg,
                    e.Description);
                _grid.Rows[idx].Tag = e.Id;
                _grid.Rows[idx].DefaultCellStyle.BackColor = band;
            }
        }
        _grid.ResumeLayout();
    }

    // Raggruppa per similarità di descrizione dentro un bucket (stessa data+importo+direzione).
    // Due righe sono "simili" se il prefisso comune normalizzato è ≥ 50% della descrizione più corta (min 4 caratteri).
    private static List<List<ExpenseRecord>> ClusterBySimilarity(List<ExpenseRecord> items)
    {
        var clusters = new List<List<ExpenseRecord>>();
        foreach (var e in items)
        {
            var nd = MainForm.NormDesc(e.Description);
            var target = clusters.FirstOrDefault(c => c.Any(x => Similar(MainForm.NormDesc(x.Description), nd)));
            if (target != null) target.Add(e);
            else clusters.Add(new List<ExpenseRecord> { e });
        }
        return clusters;
    }

    private static bool Similar(string a, string b)
    {
        if (a.Length == 0 || b.Length == 0) return a.Length == b.Length;
        int shorter = Math.Min(a.Length, b.Length);
        int cp = 0;
        while (cp < a.Length && cp < b.Length && a[cp] == b[cp]) cp++;
        return cp >= Math.Max(4, (shorter + 1) / 2);   // ≥ 50% della più corta, almeno 4 caratteri
    }
}
