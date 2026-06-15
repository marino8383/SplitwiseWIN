using System.Globalization;
using System.Text.Json;

namespace SplitwiseUploader;

public class MainForm : Form
{
    private AppConfig _cfg = null!;
    private SplitwiseClient _client = null!;
    private HistoryStore _db = null!;
    private List<(long Id, string Name)> _members = new();
    private long _me;   // id dell'utente corrente (proprietario della API key)

    // Voci della combo "Divisione" con etichette in italiano.
    private sealed class ModeItem
    {
        public SplitMode Value { get; set; }
        public string Text { get; set; } = "";
    }

    // Stato divisione per record pending (non persistito): recordId -> (mode, shares)
    private readonly Dictionary<long, (SplitMode Mode, Dictionary<long, decimal>? Shares)> _split = new();

    // Selezione "Invia" in griglia (recordId -> bool), per la tab Da inviare
    private readonly HashSet<long> _checked = new();

    // Possibili duplicati su Splitwise (recordId -> testo + colore). Popolato dopo l'import.
    //  verde  = stessa data + importo (forte)
    //  azzurro = stesso importo + pezzi di descrizione in comune
    //  giallo = stesso importo + data vicina (entro NearbyDays)
    private readonly Dictionary<long, (string Text, Color Color)> _dupInfo = new();

    // Spese 'Inviate' non più trovate su Splitwise (per data+importo). Evidenziate in rosso/barrate.
    private readonly HashSet<long> _sentNotFound = new();
    private Font? _strikeFont;

    private TabControl _tabs = null!;
    private TabPage _tabPending = null!, _tabSent = null!, _tabArchive = null!, _tabSearch = null!;
    private DataGridView _gridSearch = null!;
    private TextBox _txtSearch = null!;
    private TextBox _txtAmtFrom = null!, _txtAmtTo = null!;
    private NumericUpDown _numMonths = null!;
    private DateTimePicker _dtpSearchFrom = null!, _dtpSearchTo = null!;
    private ComboBox _cmbPayer = null!;
    private Label _lblSearchInfo = null!;
    private TextBox _txtPendingFilter = null!;
    private DateTimePicker _dtpPendFrom = null!, _dtpPendTo = null!;
    private Label _lblPendingTotal = null!;
    private readonly HashSet<string> _excludedDescriptions = new(StringComparer.OrdinalIgnoreCase);
    private string? _ctxDescription;   // descrizione della riga su cui si è fatto clic destro
    private TextBox _pasteBox = null!;
    private DataGridView _gridPending = null!, _gridSent = null!, _gridArchive = null!;
    private Button _btnParse = null!, _btnCsv = null!, _btnCsvFolder = null!, _btnStamp = null!, _btnAddManual = null!;
    private Button _btnPasteStamp = null!;
    private Button _btnSend = null!, _btnArchiveSel = null!;
    private NumericUpDown _numNearby = null!;
    private Button _btnUnarchiveSel = null!;
    private Label _status = null!;

    private List<ExpenseRecord> _pendingView = new();

    public MainForm()
    {
        Text = "Splitwise Uploader — BPER / Satispay / Stamp";
        Width = 1060; Height = 780;
        StartPosition = FormStartPosition.CenterScreen;
        BuildUi();
        Load += async (_, _) => await InitAsync();
    }

    private void BuildUi()
    {
        _tabs = new TabControl { Dock = DockStyle.Fill };
        _tabPending = new TabPage("Da inviare");
        _tabSent = new TabPage("Inviate");
        _tabArchive = new TabPage("Archivio");
        _tabSearch = new TabPage("Cerca");
        _tabs.TabPages.AddRange(new[] { _tabPending, _tabSent, _tabArchive, _tabSearch });
        _tabs.Selecting += (_, e) =>
        {
            if (e.TabPage == _tabSent) { LoadSent(); _ = CheckSentAsync(); }
            else if (e.TabPage == _tabArchive) LoadArchive();
            else if (e.TabPage == _tabSearch) { /* la ricerca parte col bottone */ }
            else LoadPending();
        };

        BuildPendingTab();
        BuildSentTab();
        BuildArchiveTab();
        BuildSearchTab();

        Controls.Add(_tabs);
    }

    // ---------- TAB DA INVIARE ----------
    private void BuildPendingTab()
    {
        var top = new Panel { Dock = DockStyle.Top, Height = 165 };
        _pasteBox = new TextBox
        {
            Multiline = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill,
            PlaceholderText = "Incolla qui i movimenti BPER/Satispay (una spesa per riga) oppure usa i bottoni sotto..."
        };
        var topButtons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 38 };
        _btnParse = new Button { Text = "Analizza testo", Width = 115 };
        _btnCsv = new Button { Text = "Carica CSV/Satispay…", Width = 145 };
        _btnCsvFolder = new Button { Text = "Processa CSV (cartella)…", Width = 175 };
        _btnStamp = new Button { Text = "Processa STAMP (cartella)…", Width = 195 };
        _btnPasteStamp = new Button { Text = "Incolla immagine (STAMP)", Width = 185 };
        _btnAddManual = new Button { Text = "+ Riga manuale", Width = 115 };
        _btnParse.Click += (_, _) => ImportRows(ExpenseParser.ParseText(_pasteBox.Text));
        _btnCsv.Click += (_, _) => LoadCsv();
        _btnCsvFolder.Click += (_, _) => ProcessCsvFolder();
        _btnStamp.Click += (_, _) => ProcessStamps();
        _btnPasteStamp.Click += (_, _) => PasteStampFromClipboard();
        _btnAddManual.Click += (_, _) => AddManualRow();
        topButtons.Controls.AddRange(new Control[] { _btnParse, _btnCsv, _btnCsvFolder, _btnStamp, _btnPasteStamp, _btnAddManual });
        top.Controls.Add(_pasteBox);
        top.Controls.Add(topButtons);

        _gridPending = NewGrid();
        _gridPending.Columns.Add(new DataGridViewCheckBoxColumn { HeaderText = "Invia", Width = 45, Name = "colSend" });
        AddCommonColumns(_gridPending);
        var modeCol = new DataGridViewComboBoxColumn { HeaderText = "Divisione", Width = 130, Name = "colMode" };
        modeCol.DataSource = new List<ModeItem>
        {
            new() { Value = SplitMode.Equal,      Text = "Parti uguali" },
            new() { Value = SplitMode.AllToOther, Text = "Tutto all'altro" },
            new() { Value = SplitMode.AllToMe,    Text = "Tutto a me" },
        };
        modeCol.ValueMember = "Value";      // il valore della cella è un SplitMode (niente più cast da stringa)
        modeCol.DisplayMember = "Text";
        _gridPending.Columns.Add(modeCol);
        _gridPending.Columns.Add(new DataGridViewTextBoxColumn
        { HeaderText = "Già su Splitwise (stessa data+importo)", Width = 240, Name = "colDup", ReadOnly = true });
        _gridPending.CellValueChanged += PendingGrid_CellValueChanged;
        _gridPending.CurrentCellDirtyStateChanged += (_, _) =>
        { if (_gridPending.IsCurrentCellDirty) _gridPending.CommitEdit(DataGridViewDataErrorContexts.Commit); };

        // Clic destro: ricorda la descrizione della riga sotto il cursore (per "Escludi")
        _gridPending.CellMouseDown += (_, e) =>
        {
            if (e.Button == MouseButtons.Right && e.RowIndex >= 0)
                _ctxDescription = Convert.ToString(_gridPending.Rows[e.RowIndex].Cells["colDesc"].Value);
        };

        // Menu contestuale (clic destro): seleziona/deseleziona, escludi descrizione, elimina.
        var menu = new ContextMenuStrip();
        menu.Items.Add("Seleziona tutti", null, (_, _) => SetAllChecked(true));
        menu.Items.Add("Deseleziona tutti", null, (_, _) => SetAllChecked(false));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Escludi questa descrizione", null, (_, _) =>
        {
            var d = _ctxDescription?.Trim();
            if (!string.IsNullOrEmpty(d)) { _excludedDescriptions.Add(d); LoadPending(); }
        });
        menu.Items.Add("Mostra tutte (rimuovi esclusioni)", null, (_, _) =>
        { _excludedDescriptions.Clear(); LoadPending(); });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Elimina selezionate", null, (_, _) => DeleteChecked());
        _gridPending.ContextMenuStrip = menu;

        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 50 };
        _btnSend = new Button { Text = "Invia selezionate", Width = 150, Height = 34, Left = 10, Top = 8 };
        _btnArchiveSel = new Button { Text = "Archivia selezionate", Width = 160, Height = 34, Left = 170, Top = 8 };
        var btnDelete = new Button { Text = "Elimina selezionate", Width = 160, Height = 34, Left = 340, Top = 8 };
        _btnSend.Click += async (_, _) => await SendAsync();
        _btnArchiveSel.Click += (_, _) => ArchiveChecked();
        btnDelete.Click += (_, _) => DeleteChecked();
        var lblNear = new Label { Text = "Intorno giorni (giallo):", AutoSize = true, Left = 515, Top = 16 };
        _numNearby = new NumericUpDown { Left = 645, Top = 12, Width = 50, Minimum = 0, Maximum = 60, Value = 3 };
        _numNearby.ValueChanged += (_, _) => { _ = CheckPendingDuplicatesAsync(); };  // ri-controlla al cambio
        _status = new Label { AutoSize = true, Left = 710, Top = 16, Text = "" };
        bottom.Controls.AddRange(new Control[] { _btnSend, _btnArchiveSel, btnDelete, lblNear, _numNearby, _status });

        // Barra filtri (parole + range date) sopra la griglia
        var filterBar = new Panel { Dock = DockStyle.Top, Height = 32 };
        var lblF = new Label { Text = "Filtro:", AutoSize = true, Left = 10, Top = 8 };
        _txtPendingFilter = new TextBox { Left = 55, Top = 5, Width = 200,
            PlaceholderText = "parole (tutte presenti)" };
        var lblFd = new Label { Text = "Data da", AutoSize = true, Left = 265, Top = 8 };
        _dtpPendFrom = new DateTimePicker { Left = 320, Top = 5, Width = 110, Format = DateTimePickerFormat.Short, ShowCheckBox = true, Checked = false };
        var lblFa = new Label { Text = "a", AutoSize = true, Left = 437, Top = 8 };
        _dtpPendTo = new DateTimePicker { Left = 452, Top = 5, Width = 110, Format = DateTimePickerFormat.Short, ShowCheckBox = true, Checked = false };
        var btnApply = new Button { Text = "Applica", Left = 572, Top = 3, Width = 80, Height = 25 };
        var btnClearPend = new Button { Text = "Pulisci filtri", Left = 657, Top = 3, Width = 100, Height = 25 };
        _lblPendingTotal = new Label { AutoSize = true, Left = 770, Top = 8, Text = "" };
        btnApply.Click += (_, _) => LoadPending();
        btnClearPend.Click += (_, _) =>
        {
            _txtPendingFilter.Clear();
            _dtpPendFrom.Checked = false; _dtpPendTo.Checked = false;
            _excludedDescriptions.Clear();   // rimuovi anche le esclusioni per descrizione
            LoadPending();
        };
        _txtPendingFilter.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; LoadPending(); } };
        _dtpPendFrom.ValueChanged += (_, _) => LoadPending();
        _dtpPendTo.ValueChanged += (_, _) => LoadPending();
        filterBar.Controls.AddRange(new Control[] { lblF, _txtPendingFilter, lblFd, _dtpPendFrom, lblFa, _dtpPendTo, btnApply, btnClearPend, _lblPendingTotal });

        _tabPending.Controls.Add(_gridPending);
        _tabPending.Controls.Add(bottom);
        _tabPending.Controls.Add(filterBar);
        _tabPending.Controls.Add(top);
    }

    // Vero se la spesa passa il filtro di "Da inviare" (parole tutte presenti + range date).
    private bool PassesPendingFilter(ExpenseRecord e)
    {
        // esclusioni per descrizione (clic destro -> Escludi)
        if (_excludedDescriptions.Contains((e.Description ?? "").Trim())) return false;

        var f = _txtPendingFilter.Text.Trim();
        if (f.Length > 0)
        {
            var terms = f.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            var desc = e.Description ?? "";
            if (!terms.All(t => desc.Contains(t, StringComparison.OrdinalIgnoreCase))) return false;
        }
        if (_dtpPendFrom.Checked && (e.Date is null || e.Date.Value.Date < _dtpPendFrom.Value.Date)) return false;
        if (_dtpPendTo.Checked && (e.Date is null || e.Date.Value.Date > _dtpPendTo.Value.Date)) return false;
        return true;
    }

    // ---------- TAB INVIATE ----------
    private void BuildSentTab()
    {
        _gridSent = NewGrid();
        AddCommonColumns(_gridSent);
        _gridSent.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Inviata il", Width = 130, Name = "colSent" });
        _gridSent.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "ID Splitwise", Width = 100, Name = "colSid" });
        _gridSent.CellFormatting += SentGrid_Formatting;

        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 50 };
        var btnDelNotFound = new Button { Text = "Elimina inviati non trovati", Width = 200, Height = 34, Left = 10, Top = 8 };
        btnDelNotFound.Click += (_, _) => DeleteSentNotFound();
        var lbl = new Label { AutoSize = true, Left = 220, Top = 16,
            Text = "In rosso/barrate: inviate non più presenti su Splitwise (data+importo)." };
        bottom.Controls.AddRange(new Control[] { btnDelNotFound, lbl });
        _tabSent.Controls.Add(_gridSent);
        _tabSent.Controls.Add(bottom);
    }

    // ---------- TAB ARCHIVIO ----------
    private void BuildArchiveTab()
    {
        _gridArchive = NewGrid();
        _gridArchive.Columns.Add(new DataGridViewCheckBoxColumn { HeaderText = "Sel", Width = 40, Name = "colSel" });
        AddCommonColumns(_gridArchive);
        _gridArchive.CurrentCellDirtyStateChanged += (_, _) =>
        { if (_gridArchive.IsCurrentCellDirty) _gridArchive.CommitEdit(DataGridViewDataErrorContexts.Commit); };

        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 50 };
        _btnUnarchiveSel = new Button { Text = "Dearchivia selezionate", Width = 180, Height = 34, Left = 10, Top = 8 };
        var btnUnarchiveAll = new Button { Text = "Dearchivia tutte", Width = 140, Height = 34, Left = 200, Top = 8 };
        var btnDeleteArch = new Button { Text = "Elimina selezionate", Width = 160, Height = 34, Left = 350, Top = 8 };
        _btnUnarchiveSel.Click += (_, _) => UnarchiveChecked(false);
        btnUnarchiveAll.Click += (_, _) => UnarchiveChecked(true);
        btnDeleteArch.Click += (_, _) => DeleteArchiveChecked();
        bottom.Controls.AddRange(new Control[] { _btnUnarchiveSel, btnUnarchiveAll, btnDeleteArch });
        _tabArchive.Controls.Add(_gridArchive);
        _tabArchive.Controls.Add(bottom);
    }

    // ---------- TAB CERCA ----------
    private void BuildSearchTab()
    {
        _gridSearch = NewGrid();
        _gridSearch.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Data", Width = 90, Name = "colDate",
            DefaultCellStyle = new DataGridViewCellStyle { Format = "dd/MM/yyyy" } });
        _gridSearch.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Descrizione", Name = "colDesc",
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill });
        _gridSearch.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Importo €", Width = 90, Name = "colAmt",
            DefaultCellStyle = new DataGridViewCellStyle { Format = "0.00", Alignment = DataGridViewContentAlignment.MiddleRight } });
        _gridSearch.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Pagato da", Width = 110, Name = "colPayer" });
        _gridSearch.ReadOnly = true;

        var top = new Panel { Dock = DockStyle.Top, Height = 72 };
        // riga 1: parola + importo
        var lbl = new Label { Text = "Parola:", AutoSize = true, Left = 10, Top = 12 };
        _txtSearch = new TextBox { Left = 60, Top = 8, Width = 180 };
        var lblA = new Label { Text = "Importo € da", AutoSize = true, Left = 255, Top = 12 };
        _txtAmtFrom = new TextBox { Left = 332, Top = 8, Width = 70 };
        var lblAt = new Label { Text = "a", AutoSize = true, Left = 409, Top = 12 };
        _txtAmtTo = new TextBox { Left = 424, Top = 8, Width = 70 };
        var lblP = new Label { Text = "Pagato da", AutoSize = true, Left = 510, Top = 12 };
        _cmbPayer = new ComboBox { Left = 580, Top = 8, Width = 90, DropDownStyle = ComboBoxStyle.DropDownList };
        _cmbPayer.Items.AddRange(new object[] { "Tutti", "Io", "Altro" });
        _cmbPayer.SelectedIndex = 0;
        // riga 2: range date + mesi + bottoni
        var lblD = new Label { Text = "Data da", AutoSize = true, Left = 10, Top = 46 };
        _dtpSearchFrom = new DateTimePicker { Left = 65, Top = 42, Width = 110, Format = DateTimePickerFormat.Short, ShowCheckBox = true, Checked = false };
        var lblDt = new Label { Text = "a", AutoSize = true, Left = 182, Top = 46 };
        _dtpSearchTo = new DateTimePicker { Left = 197, Top = 42, Width = 110, Format = DateTimePickerFormat.Short, ShowCheckBox = true, Checked = false };
        var lblM = new Label { Text = "Mesi", AutoSize = true, Left = 320, Top = 46 };
        _numMonths = new NumericUpDown { Left = 357, Top = 42, Width = 55, Minimum = 1, Maximum = 120, Value = 6 };
        var btn = new Button { Text = "Cerca", Left = 425, Top = 40, Width = 90, Height = 26 };
        var btnClear = new Button { Text = "Pulisci filtri", Left = 520, Top = 40, Width = 100, Height = 26 };
        btn.Click += async (_, _) => await DoSearch();
        btnClear.Click += (_, _) =>
        {
            _txtSearch.Clear(); _txtAmtFrom.Clear(); _txtAmtTo.Clear();
            _dtpSearchFrom.Checked = false; _dtpSearchTo.Checked = false;
            _cmbPayer.SelectedIndex = 0;   // lascia solo i mesi
            _gridSearch.Rows.Clear();
            _lblSearchInfo.Text = "Filtri puliti (mesi mantenuti).";
        };
        async void OnEnter(object? s, KeyEventArgs e) { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; await DoSearch(); } }
        _txtSearch.KeyDown += OnEnter; _txtAmtFrom.KeyDown += OnEnter; _txtAmtTo.KeyDown += OnEnter;
        top.Controls.AddRange(new Control[] { lbl, _txtSearch, lblA, _txtAmtFrom, lblAt, _txtAmtTo, lblP, _cmbPayer,
            lblD, _dtpSearchFrom, lblDt, _dtpSearchTo, lblM, _numMonths, btn, btnClear });

        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 28 };
        _lblSearchInfo = new Label { AutoSize = true, Left = 10, Top = 6,
            Text = "Cerca le spese del gruppo su Splitwise per parola, ordinate per data (più recenti in alto)." };
        bottom.Controls.Add(_lblSearchInfo);

        _tabSearch.Controls.Add(_gridSearch);
        _tabSearch.Controls.Add(top);
        _tabSearch.Controls.Add(bottom);
    }

    private async Task DoSearch()
    {
        if (_client is null || _cfg is null) { _lblSearchInfo.Text = "App non inizializzata (controlla l'autenticazione)."; return; }

        var term = _txtSearch.Text.Trim();
        var from = TryParseDecimal(_txtAmtFrom.Text);
        var to = TryParseDecimal(_txtAmtTo.Text);

        // filtro importo: solo "da" => esatto; solo "a" => esatto; entrambi => range (riordino se invertiti)
        decimal? min = null, max = null;
        string amtDesc = "";
        if (from.HasValue && to.HasValue)
        {
            min = Math.Min(from.Value, to.Value);
            max = Math.Max(from.Value, to.Value);
            amtDesc = $", importo {min:0.00}–{max:0.00} €";
        }
        else if (from.HasValue || to.HasValue)
        {
            var v = from ?? to!.Value;
            min = v - 0.005m; max = v + 0.005m;   // esatto (tolleranza mezzo centesimo)
            amtDesc = $", importo = {v:0.00} €";
        }

        DateTime? dFrom = _dtpSearchFrom.Checked ? _dtpSearchFrom.Value.Date : null;
        DateTime? dTo = _dtpSearchTo.Checked ? _dtpSearchTo.Value.Date : null;
        var dateDesc = (dFrom, dTo) switch
        {
            (not null, not null) => $", date {dFrom:dd/MM/yyyy}–{dTo:dd/MM/yyyy}",
            (not null, null) => $", dal {dFrom:dd/MM/yyyy}",
            (null, not null) => $", fino al {dTo:dd/MM/yyyy}",
            _ => ""
        };

        var payerSel = _cmbPayer.SelectedItem?.ToString() ?? "Tutti";
        var payerDesc = payerSel == "Tutti" ? "" : $", pagato da {payerSel}";

        if (term.Length == 0 && min is null && dFrom is null && dTo is null && payerSel == "Tutti")
        { _lblSearchInfo.Text = "Inserisci almeno un criterio: parola, importo, date o pagante."; return; }

        int months = (int)_numMonths.Value;
        var since = DateTime.Now.AddMonths(-months);
        if (dFrom.HasValue && dFrom.Value < since) since = dFrom.Value;   // estendi il fetch al range richiesto
        _gridSearch.Rows.Clear();
        _lblSearchInfo.Text = "Ricerca su Splitwise…";
        try
        {
            var all = await _client.GetExpensesSinceAsync(_cfg.GroupId, since);
            var matches = all
                .Where(e => term.Length == 0 || e.Description.Contains(term, StringComparison.OrdinalIgnoreCase))
                .Where(e => min is null || (e.Cost >= min.Value && e.Cost <= max!.Value))
                .Where(e => (dFrom is null || e.Date >= dFrom.Value) && (dTo is null || e.Date <= dTo.Value))
                .Where(e => payerSel switch
                {
                    "Io" => e.PayerId == _me,
                    "Altro" => e.PayerId != 0 && e.PayerId != _me,
                    _ => true
                })
                .OrderByDescending(e => e.Date)
                .ToList();

            foreach (var e in matches)
                _gridSearch.Rows.Add(e.Date, e.Description, e.Cost, PayerLabel(e.PayerId));

            decimal tot = matches.Sum(m => m.Cost);
            var crit = (term.Length > 0 ? $"\"{term}\"" : "(qualsiasi descrizione)") + amtDesc + dateDesc + payerDesc;
            _lblSearchInfo.Text = matches.Count == 0
                ? $"Nessun risultato per {crit}."
                : $"{matches.Count} risultati per {crit}. Totale: {tot:0.00} €.";
        }
        catch (Exception ex) { _lblSearchInfo.Text = "Errore ricerca: " + ex.Message; }
    }

    // Etichetta del pagante: "Io" se sono io, altrimenti il nome del membro (o "Altro").
    private string PayerLabel(long payerId)
    {
        if (payerId == 0) return "—";
        if (payerId == _me) return "Io";
        var m = _members.FirstOrDefault(x => x.Id == payerId);
        return string.IsNullOrWhiteSpace(m.Name) ? "Altro" : m.Name;
    }

    private static decimal? TryParseDecimal(string s)
    {
        s = (s ?? "").Replace("€", "").Trim();
        if (s.Length == 0) return null;
        if (s.Contains(',') && s.Contains('.')) s = s.Replace(".", "").Replace(",", ".");
        else if (s.Contains(',')) s = s.Replace(",", ".");
        return decimal.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var d) ? d : null;
    }

    private static DataGridView NewGrid() => new()
    {
        Dock = DockStyle.Fill, AutoGenerateColumns = false, AllowUserToAddRows = false,
        SelectionMode = DataGridViewSelectionMode.CellSelect, EditMode = DataGridViewEditMode.EditOnEnter,
        RowHeadersVisible = false
    };

    private static void AddCommonColumns(DataGridView g)
    {
        g.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Data", Width = 90, Name = "colDate",
            DefaultCellStyle = new DataGridViewCellStyle { Format = "dd/MM/yyyy" } });
        g.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Descrizione", Name = "colDesc",
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill });
        g.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Importo €", Width = 90, Name = "colAmt",
            DefaultCellStyle = new DataGridViewCellStyle { Format = "0.00", Alignment = DataGridViewContentAlignment.MiddleRight } });
        g.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Fonte", Width = 80, Name = "colSrc" });
    }

    private async Task InitAsync()
    {
        try
        {
            _db = new HistoryStore();
            var json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "appsettings.json"));
            _cfg = JsonSerializer.Deserialize<AppConfig>(json)!;
            if (_cfg.GroupId == 0)
                throw new Exception("Imposta GroupId in appsettings.json (avvia con argomento 'groups').");

            _client = new SplitwiseClient(_cfg.ConsumerKey, _cfg.ConsumerSecret);
            SetStatus("Autenticazione…");
            await _client.AuthenticateAsync();
            _me = await _client.GetCurrentUserAsync();
            _members = await _client.GetGroupMembersAsync(_cfg.GroupId);
            _numNearby.Value = Math.Clamp(_cfg.NearbyDays, (int)_numNearby.Minimum, (int)_numNearby.Maximum);
            LoadPending();
            SetStatus($"Pronto. Gruppo con {_members.Count} membri.");
            await CheckPendingDuplicatesAsync();   // colora subito le spese già in 'Da inviare'
            _ = CheckSentAsync();                  // verifica all'avvio le inviate non più su Splitwise
        }
        catch (Exception ex)
        {
            _btnSend.Enabled = false;
            SetStatus("Errore init (invio disabilitato): " + ex.Message, true);
            MessageBox.Show(ex.Message, "Errore di inizializzazione",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    // ---------- IMPORT ----------
    private void ImportRows(List<ExpenseRow> rows)
    {
        int added = 0;
        foreach (var r in rows)
        {
            if (r.Amount <= 0 && string.IsNullOrWhiteSpace(r.Description)) continue;
            var id = _db.AddPending(r.Date, Desc(r), r.Amount, r.Source);
            _split[id] = (r.Mode, r.CustomShares);
            _checked.Add(id); // di default selezionate per l'invio
            added++;
        }
        LoadPending();
        SetStatus($"{added} spese importate in 'Da inviare'.");
        _ = CheckPendingDuplicatesAsync();   // verifica su Splitwise ed evidenzia in rosso
    }

    // Colori dei tre livelli di match con Splitwise
    private static readonly Color DupGreen = Color.FromArgb(200, 230, 201);  // stessa data + importo
    private static readonly Color DupBlue = Color.FromArgb(187, 222, 251);   // importo + descrizione simile
    private static readonly Color DupYellow = Color.FromArgb(255, 245, 157);  // importo + data vicina

    /// <summary>
    /// Confronta le spese 'Da inviare' con Splitwise e popola _dupInfo con un colore per tipo di match:
    /// verde = stessa data+importo; azzurro = importo + pezzi di descrizione comuni; giallo = importo + data vicina.
    /// </summary>
    private async Task CheckPendingDuplicatesAsync()
    {
        if (_client is null || _cfg is null) return;
        try
        {
            var dated = _pendingView.Where(e => e.Date.HasValue).ToList();
            if (dated.Count == 0) { _dupInfo.Clear(); LoadPending(); return; }

            SetStatus("Controllo su Splitwise dei possibili duplicati…");
            int nearby = (int)_numNearby.Value;
            var since = dated.Min(e => e.Date!.Value).AddDays(-nearby - 1);
            var existing = await _client.GetExpensesSinceAsync(_cfg.GroupId, since);

            _dupInfo.Clear();
            foreach (var e in dated)
            {
                var day = e.Date!.Value.Date;
                var sameAmount = existing.Where(x => Math.Abs(x.Cost - e.Amount) < 0.005m).ToList();
                if (sameAmount.Count == 0) continue;

                // 1) verde: stessa data + importo
                var exact = sameAmount.Where(x => x.Date == day).ToList();
                if (exact.Count > 0) { _dupInfo[e.Id] = (Format(exact), DupGreen); continue; }

                // 2) azzurro: importo uguale + pezzi di descrizione in comune
                var descMatch = sameAmount.Where(x => DescriptionsOverlap(e.Description, x.Description)).ToList();
                if (descMatch.Count > 0) { _dupInfo[e.Id] = (Format(descMatch), DupBlue); continue; }

                // 3) giallo: importo uguale + data entro NearbyDays
                var near = sameAmount.Where(x => Math.Abs((x.Date - day).TotalDays) <= nearby).ToList();
                if (near.Count > 0) _dupInfo[e.Id] = (Format(near), DupYellow);
            }

            LoadPending();   // ri-render con evidenziazione
            SetStatus(_dupInfo.Count > 0
                ? $"Attenzione: {_dupInfo.Count} righe potrebbero essere già su Splitwise (verde=data+importo, azzurro=descrizione, giallo=data vicina)."
                : "Nessun possibile duplicato su Splitwise.");
        }
        catch (Exception ex) { SetStatus("Controllo duplicati Splitwise non riuscito: " + ex.Message, true); }
    }

    private static string Format(IEnumerable<(long Id, DateTime Date, decimal Cost, string Description, long PayerId)> m) =>
        string.Join(" ; ", m.Select(x => $"{x.Date:dd/MM} {x.Description} ({x.Cost:0.00}€)"));

    // Vero se le due descrizioni condividono un token DISTINTIVO (codice/numero lungo, es. il
    // numero del mandato SDD "1103049456"). Le sole parole generiche (es. "cresciamo", "asilo")
    // NON bastano, perché sono comuni a più spese dello stesso beneficiario.
    private static bool DescriptionsOverlap(string a, string b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
        var tb = b.ToLowerInvariant();
        var tokens = System.Text.RegularExpressions.Regex.Split(a.ToLowerInvariant(), @"[^a-z0-9àèéìòù]+")
            .Where(t => t.Length >= 5 && t.Any(char.IsDigit));   // solo codici/identificativi
        return tokens.Any(t => tb.Contains(t));
    }

    private void LoadCsv()
    {
        using var ofd = new OpenFileDialog
        { Filter = "BPER/Satispay/CSV (xls, xlsx, csv, txt)|*.xls;*.xlsx;*.csv;*.txt|Tutti i file|*.*" };
        if (ofd.ShowDialog() != DialogResult.OK) return;
        try
        {
            var ext = Path.GetExtension(ofd.FileName).ToLowerInvariant();
            var rows = ext switch
            {
                ".xls" => BperXlsImporter.Import(ofd.FileName),      // export BPER (Lista Movimenti Carta)
                ".xlsx" => SatispayImporter.Import(ofd.FileName),    // export Satispay (Esporta report)
                _ => ExpenseParser.ParseText(File.ReadAllText(ofd.FileName))
            };
            ImportRows(rows);
            SetStatus($"{rows.Count} movimenti importati.");
        }
        catch (Exception ex) { SetStatus("Errore import: " + ex.Message, true); }
    }

    private void ProcessCsvFolder()
    {
        using var fbd = new FolderBrowserDialog { Description = "Cartella Drive con i CSV BPER da processare" };
        if (fbd.ShowDialog() != DialogResult.OK) return;
        SetStatus("Lettura CSV…");
        try
        {
            var res = CsvFolderProcessor.ProcessFolder(fbd.SelectedPath, archive: true);
            ImportRows(res.Rows);
            var msg = $"{res.ProcessedFiles.Count} CSV processati, archiviati {res.ArchivedFiles.Count}.";
            if (res.Errors.Count > 0) { MessageBox.Show(string.Join("\n", res.Errors), "Errori CSV"); msg += $" Errori: {res.Errors.Count}."; }
            SetStatus(msg, res.Errors.Count > 0);
        }
        catch (Exception ex) { SetStatus("Errore cartella CSV: " + ex.Message, true); }
    }

    private void ProcessStamps()
    {
        using var fbd = new FolderBrowserDialog { Description = "Cartella con gli screenshot delle spese" };
        if (fbd.ShowDialog() != DialogResult.OK) return;
        if (!TryGetTessData(out var tessData)) return;

        SetStatus("OCR in corso…");
        try
        {
            var results = StampProcessor.ProcessFolder(fbd.SelectedPath, tessData);
            ImportRows(results.Select(r => r.Row).ToList());
            var noAmount = results.Count(r => !r.Confident);
            SetStatus($"{results.Count} screenshot processati. Senza importo: {noAmount} (controllali).", noAmount > 0);
        }
        catch (Exception ex) { SetStatus("Errore OCR: " + ex.Message, true); }
    }

    // Processa un'immagine copiata negli appunti (es. screenshot ritagliato) e ne crea una riga STAMP.
    private void PasteStampFromClipboard()
    {
        if (!Clipboard.ContainsImage())
        { SetStatus("Nessuna immagine negli appunti. Copia uno screenshot e riprova.", true); return; }
        if (!TryGetTessData(out var tessData)) return;

        string? temp = null;
        SetStatus("OCR in corso (immagine incollata)…");
        try
        {
            using (var img = Clipboard.GetImage())
            {
                if (img is null) { SetStatus("Impossibile leggere l'immagine dagli appunti.", true); return; }
                temp = Path.Combine(Path.GetTempPath(), $"stamp_{Guid.NewGuid():N}.png");
                img.Save(temp, System.Drawing.Imaging.ImageFormat.Png);
            }
            var results = StampProcessor.ProcessSingleFile(temp, tessData);
            ImportRows(results.Select(r => r.Row).ToList());
            var noAmount = results.Count(r => !r.Confident);
            SetStatus(results.Count == 0
                ? "Nessuna spesa rilevata nell'immagine."
                : $"{results.Count} riga/e dall'immagine. Da rivedere: {noAmount}. Controlla data/importo/descrizione in griglia.",
                results.Count == 0 || noAmount > 0);
        }
        catch (Exception ex) { SetStatus("Errore OCR (incolla): " + ex.Message, true); }
        finally { if (temp != null && File.Exists(temp)) { try { File.Delete(temp); } catch { } } }
    }

    private bool TryGetTessData(out string tessData)
    {
        tessData = Path.Combine(AppContext.BaseDirectory, "tessdata");
        if (!File.Exists(Path.Combine(tessData, "ita.traineddata")))
        {
            MessageBox.Show("Manca ./tessdata/ita.traineddata. Vedi README.", "OCR non configurato");
            return false;
        }
        return true;
    }

    private void AddManualRow()
    {
        var id = _db.AddPending(DateTime.Today, "", 0m, ExpenseSource.MANUALE);
        _split[id] = (SplitMode.Equal, null);
        _checked.Add(id);
        LoadPending();
        _tabs.SelectedTab = _tabPending;
        SetStatus("Riga manuale aggiunta. Compilala in griglia (Data/Descrizione/Importo).");
    }

    // ---------- RENDER GRIGLIE ----------
    private void LoadPending()
    {
        _pendingView = _db.GetByStatus(ExpenseStatus.Pending); // già ordinata data DESC
        _gridPending.Rows.Clear();
        decimal total = 0; int shown = 0;
        foreach (var e in _pendingView)
        {
            if (!PassesPendingFilter(e)) continue;   // filtro parole + range date + esclusioni
            var mode = _split.TryGetValue(e.Id, out var s) ? s.Mode : SplitMode.Equal;
            int idx = _gridPending.Rows.Add(
                _checked.Contains(e.Id),
                e.Date, e.Description, e.Amount, e.Source.ToString(),
                mode);
            var grow = _gridPending.Rows[idx];
            grow.Tag = e.Id;
            total += e.Amount; shown++;

            // evidenzia le righe che sembrano già presenti su Splitwise (colore per tipo di match)
            if (_dupInfo.TryGetValue(e.Id, out var dup))
            {
                grow.Cells["colDup"].Value = dup.Text;
                grow.DefaultCellStyle.BackColor = dup.Color;
            }
        }
        var excl = _excludedDescriptions.Count > 0 ? $" — {_excludedDescriptions.Count} descrizioni escluse" : "";
        _lblPendingTotal.Text = $"Totale: {total:0.00} € ({shown} voci){excl}";
    }

    private void LoadSent()
    {
        _gridSent.Rows.Clear();
        foreach (var e in _db.GetByStatus(ExpenseStatus.Inviata))
        {
            int idx = _gridSent.Rows.Add(e.Date, e.Description, e.Amount, e.Source.ToString(),
                e.SentDisplay, e.SplitwiseExpenseId);
            var grow = _gridSent.Rows[idx];
            grow.Tag = e.Id;
            if (_sentNotFound.Contains(e.Id))
            {
                _strikeFont ??= new Font(_gridSent.Font, FontStyle.Strikeout);
                grow.DefaultCellStyle.BackColor = Color.MistyRose;
                grow.DefaultCellStyle.ForeColor = Color.Firebrick;
                grow.DefaultCellStyle.Font = _strikeFont;
            }
        }
    }

    /// <summary>
    /// Controlla le spese 'Inviate' contro Splitwise: quelle non più presenti (per data+importo,
    /// o per id Splitwise) finiscono in _sentNotFound ed evidenziate in rosso/barrate.
    /// </summary>
    private async Task CheckSentAsync()
    {
        if (_client is null || _cfg is null) return;
        var sent = _db.GetByStatus(ExpenseStatus.Inviata);
        _sentNotFound.Clear();
        if (sent.Count == 0) { LoadSent(); return; }
        try
        {
            SetStatus("Verifica delle spese inviate su Splitwise…");
            var dated = sent.Where(e => e.Date.HasValue).ToList();
            var since = dated.Count > 0 ? dated.Min(e => e.Date!.Value).AddDays(-1) : DateTime.Now.AddMonths(-24);
            var existing = await _client.GetExpensesSinceAsync(_cfg.GroupId, since);

            foreach (var e in sent)
            {
                bool found = existing.Any(x => x.Id == e.SplitwiseExpenseId)
                    || (e.Date.HasValue && existing.Any(x =>
                            x.Date == e.Date.Value.Date && Math.Abs(x.Cost - e.Amount) < 0.005m));
                if (!found) _sentNotFound.Add(e.Id);
            }
            LoadSent();
            SetStatus(_sentNotFound.Count > 0
                ? $"{_sentNotFound.Count} spese inviate NON trovate su Splitwise (in rosso/barrate)."
                : "Tutte le spese inviate risultano presenti su Splitwise.");
        }
        catch (Exception ex) { SetStatus("Verifica inviate non riuscita: " + ex.Message, true); }
    }

    private void DeleteSentNotFound()
    {
        var ids = _sentNotFound.ToList();
        if (ids.Count == 0) { SetStatus("Nessuna spesa inviata 'non trovata' da eliminare.", true); return; }
        if (MessageBox.Show(
                $"Eliminare DEFINITIVAMENTE {ids.Count} spese inviate non più presenti su Splitwise?\n" +
                "Operazione irreversibile sullo storico locale (non tocca Splitwise).",
                "Conferma eliminazione", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        _db.DeleteMany(ids);
        _sentNotFound.Clear();
        LoadSent();
        SetStatus($"{ids.Count} spese inviate eliminate dallo storico.");
    }

    private void LoadArchive()
    {
        _gridArchive.Rows.Clear();
        foreach (var e in _db.GetByStatus(ExpenseStatus.Archiviata))
        {
            int idx = _gridArchive.Rows.Add(false, e.Date, e.Description, e.Amount, e.Source.ToString());
            _gridArchive.Rows[idx].Tag = e.Id;
        }
    }

    private void SentGrid_Formatting(object? sender, DataGridViewCellFormattingEventArgs e) { }

    // ---------- EDIT IN GRIGLIA PENDING ----------
    private void PendingGrid_CellValueChanged(object? sender, DataGridViewCellEventArgs e)
    {
        if (e.RowIndex < 0) return;
        var row = _gridPending.Rows[e.RowIndex];
        if (row.Tag is not long id) return;
        var colName = _gridPending.Columns[e.ColumnIndex].Name;

        if (colName == "colSend")
        {
            var val = Convert.ToBoolean(row.Cells["colSend"].Value ?? false);
            if (val) _checked.Add(id); else _checked.Remove(id);
            return;
        }
        if (colName == "colMode")
        {
            var raw = row.Cells["colMode"].Value;
            var mode = raw is SplitMode sm ? sm
                : Enum.TryParse<SplitMode>(Convert.ToString(raw), out var parsed) ? parsed
                : SplitMode.Equal;
            _split[id] = (mode, null);
            return;
        }
        // Data / Descrizione / Importo / Fonte → persisti
        PersistEditableFromRow(id, row);
    }
    
    private void PersistEditableFromRow(long id, DataGridViewRow row)
    {
        DateTime? date = null;
        if (row.Cells["colDate"].Value is DateTime d) date = d;
        else if (DateTime.TryParse(Convert.ToString(row.Cells["colDate"].Value), out var dp)) date = dp;

        var desc = Convert.ToString(row.Cells["colDesc"].Value) ?? "";
        decimal amount = 0;
        decimal.TryParse(Convert.ToString(row.Cells["colAmt"].Value),
            NumberStyles.Any, CultureInfo.CurrentCulture, out amount);
        var src = Enum.TryParse<ExpenseSource>(Convert.ToString(row.Cells["colSrc"].Value), out var s2)
            ? s2 : ExpenseSource.MANUALE;
        _db.UpdateEditable(id, date, desc, amount, src);
    }

    // ---------- INVIO ----------
    private async Task SendAsync()
    {
        var ids = _checked.ToList();
        var toSend = _db.GetByStatus(ExpenseStatus.Pending)
            .Where(e => ids.Contains(e.Id) && e.Amount > 0)
            .ToList();
        if (toSend.Count == 0) { SetStatus("Nessuna riga selezionata (o importi a zero).", true); return; }

        // DEDUP su DATA + IMPORTO contro le INVIATE
        var dups = toSend.Where(e => _db.FindSentDuplicates(e.Date, e.Amount).Count > 0).ToList();
        if (dups.Count > 0)
        {
            var lines = string.Join("\n", dups.Select(d => $"• {d.Date:dd/MM/yyyy} — {d.Description} — {d.Amount:0.00} €"));
            var res = MessageBox.Show(
                $"Queste {dups.Count} spese hanno STESSA DATA e STESSO IMPORTO di spese già inviate:\n\n{lines}\n\n" +
                "Inviarle comunque?\n\nSì = invia tutto\nNo = salta i duplicati\nAnnulla = ferma tutto",
                "Possibili duplicati (data + importo)", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Warning);
            if (res == DialogResult.Cancel) { SetStatus("Invio annullato."); return; }
            if (res == DialogResult.No) toSend = toSend.Except(dups).ToList();
        }
        if (toSend.Count == 0) { SetStatus("Niente da inviare dopo aver saltato i duplicati."); return; }

        // DEDUP lato SPLITWISE: stessa DATA + IMPORTO già presenti nel gruppo (anche inserite altrove)
        try
        {
            SetStatus("Controllo duplicati su Splitwise…");
            var remoteDups = new List<ExpenseRecord>();
            foreach (var grp in toSend.Where(e => e.Date.HasValue).GroupBy(e => e.Date!.Value.Date))
            {
                var existing = await _client.GetExpensesOnDayAsync(_cfg.GroupId, grp.Key);
                foreach (var e in grp)
                    if (existing.Any(x => Math.Abs(x.Cost - e.Amount) < 0.005m))
                        remoteDups.Add(e);
            }
            if (remoteDups.Count > 0)
            {
                var lines = string.Join("\n", remoteDups.Select(d => $"• {d.Date:dd/MM/yyyy} — {d.Description} — {d.Amount:0.00} €"));
                var res = MessageBox.Show(
                    $"Su Splitwise risultano GIÀ {remoteDups.Count} spese con stessa DATA e IMPORTO:\n\n{lines}\n\n" +
                    "Inviarle comunque?\n\nSì = invia tutto\nNo = salta i duplicati\nAnnulla = ferma tutto",
                    "Possibili duplicati su Splitwise", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Warning);
                if (res == DialogResult.Cancel) { SetStatus("Invio annullato."); return; }
                if (res == DialogResult.No) toSend = toSend.Except(remoteDups).ToList();
            }
        }
        catch (Exception ex) { SetStatus("Controllo duplicati su Splitwise non riuscito: " + ex.Message, true); }
        if (toSend.Count == 0) { SetStatus("Niente da inviare dopo aver saltato i duplicati su Splitwise."); return; }
        if (MessageBox.Show($"Inviare {toSend.Count} spese a Splitwise?", "Conferma",
                MessageBoxButtons.YesNo) != DialogResult.Yes) return;

        _btnSend.Enabled = false;
        int ok = 0, fail = 0; var errs = new List<string>();
        foreach (var e in toSend)
        {
            try
            {
                var mode = _split.TryGetValue(e.Id, out var s) ? s.Mode : SplitMode.Equal;
                long expId = mode == SplitMode.Equal
                    ? await _client.CreateEqualExpenseAsync(_cfg.GroupId, e.Amount, e.Description, _cfg.CurrencyCode, e.Date)
                    : await _client.CreateSharedExpenseAsync(_cfg.GroupId, e.Amount, e.Description, _cfg.CurrencyCode,
                        BuildShares(e.Amount, mode), e.Date);
                _db.MarkSent(e.Id, expId);
                _checked.Remove(e.Id);
                _split.Remove(e.Id);
                ok++;
            }
            catch (Exception ex) { fail++; errs.Add($"• {e.Description}: {ex.Message}"); }
            SetStatus($"Inviate {ok}, errori {fail}…");
        }
        _btnSend.Enabled = true;
        LoadPending();
        SetStatus($"Fatto. OK: {ok}, Errori: {fail}.", fail > 0);
        if (errs.Count > 0) MessageBox.Show(string.Join("\n", errs), "Errori invio");
    }

    // ---------- ARCHIVIO ----------
    private void ArchiveChecked()
    {
        var ids = _checked.ToList();
        if (ids.Count == 0) { SetStatus("Nessuna riga selezionata da archiviare.", true); return; }
        if (MessageBox.Show($"Archiviare {ids.Count} spese? Spariranno da 'Da inviare' (recuperabili in Archivio).",
                "Conferma archiviazione", MessageBoxButtons.YesNo) != DialogResult.Yes) return;
        _db.SetStatusMany(ids, ExpenseStatus.Archiviata);
        foreach (var id in ids) { _checked.Remove(id); _split.Remove(id); }
        LoadPending();
        SetStatus($"{ids.Count} spese archiviate.");
    }

    // Spunta/deseleziona tutte le righe della tab "Da inviare".
    private void SetAllChecked(bool selected)
    {
        foreach (DataGridViewRow row in _gridPending.Rows)
        {
            if (row.Tag is not long id) continue;
            row.Cells["colSend"].Value = selected;
            if (selected) _checked.Add(id); else _checked.Remove(id);
        }
        _gridPending.Invalidate();
        SetStatus(selected ? "Tutte selezionate." : "Tutte deselezionate.");
    }

    // Elimina FISICAMENTE le spese selezionate dal DB locale (irreversibile, non tocca Splitwise).
    private void DeleteChecked()
    {
        var ids = _checked.ToList();
        if (ids.Count == 0) { SetStatus("Nessuna riga selezionata da eliminare.", true); return; }
        if (MessageBox.Show(
                $"Eliminare DEFINITIVAMENTE {ids.Count} spese dallo storico locale?\n" +
                "L'operazione non è reversibile e non tocca Splitwise.",
                "Conferma eliminazione", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        _db.DeleteMany(ids);
        foreach (var id in ids) { _checked.Remove(id); _split.Remove(id); }
        LoadPending();
        SetStatus($"{ids.Count} spese eliminate definitivamente.");
    }

    private void UnarchiveChecked(bool all)
    {
        var ids = new List<long>();
        if (all)
            ids = _db.GetByStatus(ExpenseStatus.Archiviata).Select(e => e.Id).ToList();
        else
            foreach (DataGridViewRow row in _gridArchive.Rows)
                if (Convert.ToBoolean(row.Cells["colSel"].Value ?? false) && row.Tag is long id)
                    ids.Add(id);

        if (ids.Count == 0) { SetStatus("Nessuna riga da dearchiviare.", true); return; }
        _db.SetStatusMany(ids, ExpenseStatus.Pending);
        foreach (var id in ids) _checked.Add(id);
        LoadArchive();
        SetStatus($"{ids.Count} spese riportate in 'Da inviare'.");
    }

    private void DeleteArchiveChecked()
    {
        var ids = new List<long>();
        foreach (DataGridViewRow row in _gridArchive.Rows)
            if (Convert.ToBoolean(row.Cells["colSel"].Value ?? false) && row.Tag is long id)
                ids.Add(id);

        if (ids.Count == 0) { SetStatus("Nessuna riga di archivio selezionata da eliminare.", true); return; }
        if (MessageBox.Show(
                $"Eliminare DEFINITIVAMENTE {ids.Count} spese archiviate?\n" +
                "Operazione irreversibile (non tocca Splitwise).",
                "Conferma eliminazione", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        _db.DeleteMany(ids);
        LoadArchive();
        SetStatus($"{ids.Count} spese archiviate eliminate.");
    }

    // ---------- HELPERS ----------
    private static string Desc(ExpenseRow r) =>
        string.IsNullOrWhiteSpace(r.Description) ? "Spesa" : r.Description.Trim();

    // Gruppo da 2: paghi sempre tu (paidShare = importo intero per te).
    // AllToOther = l'altro deve tutto;  AllToMe = spesa interamente tua.
    private List<(long, decimal, decimal)> BuildShares(decimal amount, SplitMode mode)
    {
        if (_me == 0)
            throw new Exception("Utente corrente non identificato (get_current_user).");
        var other = _members.FirstOrDefault(m => m.Id != _me);
        if (other.Id == 0)
            throw new Exception("Impossibile trovare l'altro membro: il gruppo deve avere 2 persone.");

        return mode switch
        {
            // (userId, paidShare, owedShare)
            SplitMode.AllToOther => new List<(long, decimal, decimal)>
                { (_me, amount, 0m), (other.Id, 0m, amount) },
            SplitMode.AllToMe => new List<(long, decimal, decimal)>
                { (_me, amount, amount), (other.Id, 0m, 0m) },
            _ => throw new Exception($"Modalità non gestita in BuildShares: {mode}")
        };
    }

    private void SetStatus(string msg, bool error = false)
    {
        _status.Text = msg;
        _status.ForeColor = error ? Color.Firebrick : Color.DarkGreen;
    }
}

public class AppConfig
{
    public string ConsumerKey { get; set; } = "";
    public string ConsumerSecret { get; set; } = "";
    public long GroupId { get; set; }
    public string CurrencyCode { get; set; } = "EUR";

    // Giorni di "intorno" per il match giallo (stesso importo, data vicina) nel controllo duplicati.
    public int NearbyDays { get; set; } = 3;
}
