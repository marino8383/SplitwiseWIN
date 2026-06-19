using System.Data;
using System.Globalization;
using System.Text;
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
    private Font? _italicFont;

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
    private TextBox _txtPendAmtFrom = null!, _txtPendAmtTo = null!;
    private DateTimePicker _dtpPendFrom = null!, _dtpPendTo = null!;
    private Label _lblPendingTotal = null!;
    private string _pendingTotalsBase = "";
    private ComboBox _cmbShow = null!;
    private Button _btnTagFilter = null!;
    private CheckedListBox _clbTags = null!;
    private ToolStripDropDown _tagDropdown = null!;
    private ComboBox _cmbOverlap = null!;
    private ComboBox _cmbSource = null!;
    private CheckBox _chkMultiTag = null!;
    private CheckBox _chkOnlyExcluded = null!;
    private CheckBox _chkOnlySentApp = null!;
    private List<(string Keyword, string Tag)> _tagRules = new();   // cache regole per i tag in griglia
    private readonly HashSet<string> _excludedDescriptions = new(StringComparer.OrdinalIgnoreCase);
    private string? _ctxDescription;   // descrizione della riga su cui si è fatto clic destro
    private long _ctxRowId;            // id della riga su cui si è fatto clic destro
    private TextBox _pasteBox = null!;
    private DataGridView _gridPending = null!, _gridSent = null!, _gridArchive = null!;
    private Button _btnParse = null!, _btnCsv = null!, _btnAddManual = null!;
    private Button _btnPasteStamp = null!;
    private Button _btnSend = null!, _btnArchiveSel = null!;
    private NumericUpDown _numNearby = null!;
    private NumericUpDown _numAmtTol = null!;
    private Button _btnUnarchiveSel = null!;
    private Label _status = null!;
    private ProgressBar _progress = null!;
    private Label _envBanner = null!;
    private GroupBox _grpSplitwise = null!;
    private bool _splitwiseReady;   // true solo se autenticazione Splitwise riuscita (API key configurate)
    private TabPage _tabRules = null!, _tabStats = null!, _tabPaste = null!, _tabLog = null!, _tabOptions = null!, _tabSwRules = null!;
    private ListBox _lstSwRules = null!;
    private TextBox _txtSwRule = null!;
    private List<string> _swRules = new();                 // frasi-regola per l'invio automatico a Splitwise
    private readonly List<long> _autoCandidateIds = new();  // id appena importati, da valutare per l'auto-invio
    // controlli scheda Opzioni
    private TextBox _optDbCurrent = null!;
    private TextBox _optDbPath = null!, _optKey = null!, _optSecret = null!, _optGroup = null!,
        _optCurrency = null!, _optInbox = null!, _optResetConfirm = null!;
    private NumericUpDown _optNearby = null!, _optAmtTol = null!;
    private DataGridView _gridLog = null!;
    private Label _lblLogInfo = null!;
    private long _importBatchFilter = 0;   // se !=0, Movimenti mostra solo le righe di quel registro (batch)
    private DataGridView _gridRules = null!, _gridStats = null!;
    private DataGridViewComboBoxColumn _colTagRule = null!;
    private ListBox _lstTags = null!;
    private TextBox _txtNewTag = null!;
    private DateTimePicker _dtpStatsFrom = null!, _dtpStatsTo = null!;
    private ComboBox _cmbStatsView = null!;
    private Label _lblStatsInfo = null!;

    private List<ExpenseRecord> _pendingView = new();
    private bool _pendingNeedsReload = true;   // ricarica la griglia 'Da inviare' solo se i dati sono cambiati

    public MainForm()
    {
        var ver = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        Text = $"Splitwise Uploader — BPER / Satispay / Stamp   (v{ver?.Major}.{ver?.Minor}.{ver?.Build})";
        Width = 1060; Height = 780;
        StartPosition = FormStartPosition.CenterScreen;
        BuildUi();
        Load += async (_, _) => await InitAsync();
    }

    private void BuildUi()
    {
        _tabs = new TabControl { Dock = DockStyle.Fill };
        _tabPending = new TabPage("Movimenti");
        _tabSent = new TabPage("Inviate");
        _tabArchive = new TabPage("Archivio");
        _tabSearch = new TabPage("Cerca");
        _tabPaste = new TabPage("Incolla testo");
        _tabRules = new TabPage("Regole tag");
        _tabStats = new TabPage("Statistiche");
        _tabLog = new TabPage("Importazioni");
        _tabOptions = new TabPage("Opzioni");
        _tabSwRules = new TabPage("Regole Splitwise");
        _tabs.TabPages.AddRange(new[] { _tabPending, _tabSent, _tabArchive, _tabSearch, _tabPaste, _tabRules, _tabStats, _tabLog, _tabSwRules, _tabOptions });
        _tabs.Selecting += (_, e) =>
        {
            if (e.TabPage == _tabSent) LoadSent();   // verifica Splitwise solo col bottone
            else if (e.TabPage == _tabArchive) LoadArchive();
            else if (e.TabPage == _tabSearch) { /* la ricerca parte col bottone */ }
            else if (e.TabPage == _tabPaste) { /* niente */ }
            else if (e.TabPage == _tabRules) LoadRules();
            else if (e.TabPage == _tabStats) ComputeStats();
            else if (e.TabPage == _tabLog) LoadLog();
            else if (_pendingNeedsReload) LoadPending();   // 'Da inviare': ricarica solo se serve
        };

        BuildPendingTab();
        BuildPasteTab();
        BuildSentTab();
        BuildArchiveTab();
        BuildSearchTab();
        BuildRulesTab();
        BuildStatsTab();
        BuildLogTab();
        BuildOptionsTab();
        BuildSwRulesTab();

        Controls.Add(_tabs);

        // barra di avanzamento (a livello finestra) visibile durante le operazioni lente
        var strip = new Panel { Dock = DockStyle.Bottom, Height = 16 };
        _progress = new ProgressBar { Dock = DockStyle.Fill, Style = ProgressBarStyle.Marquee, MarqueeAnimationSpeed = 0, Visible = false };
        strip.Controls.Add(_progress);
        Controls.Add(strip);

        // banner di avviso (debug / DB di debug): visibile solo se serve, impostato in InitAsync
        _envBanner = new Label
        {
            Dock = DockStyle.Top, Height = 26, Visible = false, TextAlign = ContentAlignment.MiddleCenter,
            BackColor = Color.FromArgb(180, 30, 30), ForeColor = Color.White, Font = new Font(Font, FontStyle.Bold)
        };
        Controls.Add(_envBanner);   // aggiunto dopo _tabs: si aggancia in alto, le schede riempiono sotto
    }

    // Mostra/anima la progress bar durante un'operazione asincrona.
    private void BeginBusy(string? msg = null)
    {
        if (msg != null) SetStatus(msg);
        _progress.Visible = true;
        _progress.MarqueeAnimationSpeed = 30;
    }
    private void EndBusy()
    {
        _progress.MarqueeAnimationSpeed = 0;
        _progress.Visible = false;
    }

    // ---------- TAB DA INVIARE ----------
    private void BuildPendingTab()
    {
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
        { HeaderText = "Tipo", Width = 70, Name = "colKind", ReadOnly = true });
        _gridPending.Columns.Add(new DataGridViewTextBoxColumn
        { HeaderText = "Tag", Width = 140, Name = "colTags", ReadOnly = true });
        _gridPending.Columns.Add(new DataGridViewTextBoxColumn
        { HeaderText = "Già su Splitwise", Width = 150, Name = "colDup", ReadOnly = true });
        _gridPending.Columns.Add(new DataGridViewTextBoxColumn
        { HeaderText = "Note", Width = 160, Name = "colNote" });   // testo libero modificabile
        _gridPending.CellValueChanged += PendingGrid_CellValueChanged;
        _gridPending.CurrentCellDirtyStateChanged += (_, _) =>
        { if (_gridPending.IsCurrentCellDirty) _gridPending.CommitEdit(DataGridViewDataErrorContexts.Commit); };

        // clic sulla cella "Tag" → apre il selettore tag manuali della riga
        _gridPending.CellClick += (_, e) =>
        {
            if (e.RowIndex >= 0 && _gridPending.Columns[e.ColumnIndex].Name == "colTags"
                && _gridPending.Rows[e.RowIndex].Tag is long id)
            { _ctxRowId = id; SetManualTagForRow(); }
        };

        // Clic destro: ricorda la descrizione della riga sotto il cursore (per "Escludi")
        _gridPending.CellMouseDown += (_, e) =>
        {
            if (e.Button == MouseButtons.Right && e.RowIndex >= 0)
            {
                _ctxDescription = Convert.ToString(_gridPending.Rows[e.RowIndex].Cells["colDesc"].Value);
                _ctxRowId = _gridPending.Rows[e.RowIndex].Tag is long lid ? lid : 0;
            }
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
        menu.Items.Add("Crea regola tag…", null, (_, _) => QuickAddRuleFromRow());
        menu.Items.Add("Tag manuale (questa riga + le spuntate)…", null, (_, _) => SetManualTagForRow());
        var miAddSwRule = new ToolStripMenuItem("Aggiungi regola Splitwise…", null, (_, _) => AddSplitwiseRuleForRow());
        menu.Items.Add(miAddSwRule);
        menu.Opening += (_, _) => { miAddSwRule.Visible = _splitwiseReady; };   // solo se Splitwise attivo
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Suddividi movimento…", null, (_, _) => SplitMovementForRow());
        menu.Items.Add("Escludi/includi nei totali", null, (_, _) => ToggleExcludeTotalsForRow());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Elimina questa riga…", null, async (_, _) => await DeleteRowUnderCursor());
        menu.Items.Add("Elimina selezionate", null, (_, _) => DeleteChecked());
        _gridPending.ContextMenuStrip = menu;

        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 96 };

        // --- riga 1: azioni LOCALI (sul DB, non Splitwise) ---
        _btnArchiveSel = new Button { Text = "Archivia selezionate", Width = 160, Height = 32, Left = 10, Top = 8 };
        var btnDelete = new Button { Text = "Elimina selezionate", Width = 160, Height = 32, Left = 175, Top = 8 };
        var btnDedup = new Button { Text = "Controlla duplicati", Width = 150, Height = 32, Left = 340, Top = 8 };
        var btnRule = new Button { Text = "Crea regola tag…", Width = 150, Height = 32, Left = 495, Top = 8 };
        _btnArchiveSel.Click += (_, _) => ArchiveChecked();
        btnDelete.Click += (_, _) => DeleteChecked();
        btnDedup.Click += (_, _) => CheckLocalDuplicates();
        btnRule.Click += (_, _) =>
        {
            var row = _gridPending.CurrentRow;
            if (row?.Tag is not long) { SetStatus("Seleziona prima una riga (clic su una cella).", true); return; }
            _ctxDescription = Convert.ToString(row.Cells["colDesc"].Value);
            QuickAddRuleFromRow();
        };
        var lblHint = new Label { AutoSize = true, Left = 655, Top = 15,
            Text = "Clic sulla colonna \"Tag\" per assegnare i tag.\nSe spunti più righe, lo stesso tag va anche su quelle (in accodamento)." };

        // --- riga 2: azioni SPLITWISE raggruppate (abilitate solo se API key configurate) ---
        var grpSw = new GroupBox { Text = "Integrazione Splitwise", Left = 10, Top = 44, Width = 700, Height = 48, Enabled = false };
        _grpSplitwise = grpSw;
        _btnSend = new Button { Text = "Invia selezionate", Width = 140, Height = 30, Left = 8, Top = 14 };
        var btnVerifyDup = new Button { Text = "Confronta su Splitwise", Width = 150, Height = 30, Left = 152, Top = 14 };
        var lblNear = new Label { Text = "± gg:", AutoSize = true, Left = 308, Top = 20 };
        _numNearby = new NumericUpDown { Left = 346, Top = 16, Width = 46, Minimum = 0, Maximum = 60, Value = 3 };
        var lblTol = new Label { Text = "± €:", AutoSize = true, Left = 400, Top = 20 };
        _numAmtTol = new NumericUpDown { Left = 432, Top = 16, Width = 60, Minimum = 0, Maximum = 100, DecimalPlaces = 2, Increment = 0.10M, Value = 0.50M };
        var btnClearDup = new Button { Text = "Pulisci evidenziazioni", Width = 150, Height = 30, Left = 500, Top = 14 };
        _btnSend.Click += async (_, _) => await SendAsync();
        btnVerifyDup.Click += async (_, _) => await CheckPendingDuplicatesAsync();
        _numNearby.ValueChanged += (_, _) => { _ = CheckPendingDuplicatesAsync(); };  // ri-controlla al cambio
        _numAmtTol.ValueChanged += (_, _) => { _ = CheckPendingDuplicatesAsync(); };  // ri-controlla al cambio tolleranza
        btnClearDup.Click += (_, _) => { _dupInfo.Clear(); LoadPending(); SetStatus("Evidenziazioni Splitwise pulite."); };
        grpSw.Controls.AddRange(new Control[] { _btnSend, btnVerifyDup, lblNear, _numNearby, lblTol, _numAmtTol, btnClearDup });

        _status = new Label { AutoSize = true, Left = 725, Top = 60, Text = "" };
        bottom.Controls.AddRange(new Control[] { _btnArchiveSel, btnDelete, btnDedup, btnRule, lblHint, grpSw, _status });

        // Barra filtri (due righe) sopra la griglia — riga 1: PERIODO (date) per primo, poi Mostra/Fonte, Applica/Pulisci
        var monthStart = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
        var filterBar = new Panel { Dock = DockStyle.Top, Height = 94, BorderStyle = BorderStyle.FixedSingle };

        var lblDate = new Label { Text = "Periodo:", AutoSize = true, Left = 10, Top = 11 };
        _dtpPendFrom = new DateTimePicker { Left = 70, Top = 7, Width = 110, Format = DateTimePickerFormat.Short, ShowCheckBox = true, Value = monthStart, Checked = true };
        var lblFa = new Label { Text = "→", AutoSize = true, Left = 186, Top = 11 };
        _dtpPendTo = new DateTimePicker { Left = 206, Top = 7, Width = 110, Format = DateTimePickerFormat.Short, ShowCheckBox = true, Value = DateTime.Today, Checked = true };
        var lblShow = new Label { Text = "Mostra", AutoSize = true, Left = 336, Top = 11 };
        _cmbShow = new ComboBox { Left = 392, Top = 7, Width = 85, DropDownStyle = ComboBoxStyle.DropDownList };
        _cmbShow.Items.AddRange(new object[] { "Uscite", "Entrate", "Tutte" });
        _cmbShow.SelectedIndex = 0;   // default: uscite (le inviabili)
        var lblSrc = new Label { Text = "Fonte", AutoSize = true, Left = 492, Top = 11 };
        _cmbSource = new ComboBox { Left = 534, Top = 7, Width = 110, DropDownStyle = ComboBoxStyle.DropDownList };
        _cmbSource.Items.Add("(tutte)");
        _cmbSource.Items.AddRange(Enum.GetNames(typeof(ExpenseSource)));
        _cmbSource.SelectedIndex = 0;
        var btnApply = new Button { Text = "Applica", Left = 664, Top = 6, Width = 85, Height = 26 };
        var btnClearPend = new Button { Text = "Pulisci filtri", Left = 754, Top = 6, Width = 100, Height = 26 };

        // riga 2: Parole + Tag + Sovrapposizioni + totale
        var lblF = new Label { Text = "Parole", AutoSize = true, Left = 10, Top = 39 };
        _txtPendingFilter = new TextBox { Left = 70, Top = 36, Width = 200, PlaceholderText = "parole (tutte presenti)" };
        var lblTag = new Label { Text = "Tag", AutoSize = true, Left = 285, Top = 39 };
        _btnTagFilter = new Button { Text = "Tag ▾", Left = 320, Top = 36, Width = 160, Height = 23, TextAlign = ContentAlignment.MiddleLeft };
        // checklist multi-selezione in un menu a discesa
        _clbTags = new CheckedListBox { Width = 220, Height = 180, CheckOnClick = true, BorderStyle = BorderStyle.None };
        _tagDropdown = new ToolStripDropDown { AutoClose = true, Padding = Padding.Empty };
        _tagDropdown.Items.Add(new ToolStripControlHost(_clbTags) { Padding = Padding.Empty, Margin = Padding.Empty });
        _btnTagFilter.Click += (_, _) => _tagDropdown.Show(_btnTagFilter, 0, _btnTagFilter.Height);
        _tagDropdown.Closed += (_, _) => UpdateTagFilterButton();
        var lblOv = new Label { Text = "Sovrapp.", AutoSize = true, Left = 495, Top = 39 };
        _cmbOverlap = new ComboBox { Left = 560, Top = 36, Width = 130, DropDownStyle = ComboBoxStyle.DropDownList };
        _cmbOverlap.Items.AddRange(new object[] { "Escludi", "Includi tutto", "Solo sovrapposte" });
        _cmbOverlap.SelectedIndex = 0;   // default: escludi
        _chkMultiTag = new CheckBox { Text = "Solo + tag", AutoSize = true, Left = 700, Top = 39 };
        _chkOnlyExcluded = new CheckBox { Text = "Solo esclusi", AutoSize = true, Left = 793, Top = 39 };
        _chkOnlySentApp = new CheckBox { Text = "Solo inviate (app)", AutoSize = true, Left = 893, Top = 39 };

        // riga 3: range importo (>= da, <= a; vuoto = nessun limite) + totale
        var lblAmt = new Label { Text = "Importo da", AutoSize = true, Left = 10, Top = 69 };
        _txtPendAmtFrom = new TextBox { Left = 82, Top = 66, Width = 80, PlaceholderText = "≥ €" };
        var lblAmtTo = new Label { Text = "a", AutoSize = true, Left = 170, Top = 69 };
        _txtPendAmtTo = new TextBox { Left = 186, Top = 66, Width = 80, PlaceholderText = "≤ €" };
        _txtPendAmtFrom.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; ApplyPendingFilters(); } };
        _txtPendAmtTo.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; ApplyPendingFilters(); } };
        _lblPendingTotal = new Label { AutoSize = true, Left = 290, Top = 69, Text = "" };
        btnApply.Click += (_, _) => ApplyPendingFilters();
        btnClearPend.Click += (_, _) =>
        {
            _txtPendingFilter.Clear();
            _txtPendAmtFrom.Clear(); _txtPendAmtTo.Clear();
            _dtpPendFrom.Value = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1); _dtpPendFrom.Checked = true;
            _dtpPendTo.Value = DateTime.Today; _dtpPendTo.Checked = true;   // torna a "inizio mese → oggi"
            for (int i = 0; i < _clbTags.Items.Count; i++) _clbTags.SetItemChecked(i, false);
            UpdateTagFilterButton();
            _cmbShow.SelectedIndex = 0;
            _cmbOverlap.SelectedIndex = 0;
            _cmbSource.SelectedIndex = 0;
            _chkMultiTag.Checked = false;
            _chkOnlyExcluded.Checked = false;
            _chkOnlySentApp.Checked = false;
            _excludedDescriptions.Clear();
            _importBatchFilter = 0;   // rimuove il filtro "movimenti di un registro"
            _checked.Clear();   // reset completo: azzera anche le selezioni (anche su righe non visibili)
            LoadPending();
        };
        // tutti i filtri si applicano SOLO con "Applica" (o Invio nel campo testo)
        _txtPendingFilter.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; ApplyPendingFilters(); } };
        filterBar.Controls.AddRange(new Control[] { lblDate, _dtpPendFrom, lblFa, _dtpPendTo, lblShow, _cmbShow,
            lblSrc, _cmbSource, btnApply, btnClearPend,
            lblF, _txtPendingFilter, lblTag, _btnTagFilter, lblOv, _cmbOverlap, _chkMultiTag, _chkOnlyExcluded, _chkOnlySentApp,
            lblAmt, _txtPendAmtFrom, lblAmtTo, _txtPendAmtTo, _lblPendingTotal });

        // Legenda colori "Già su Splitwise" (fissa, sopra la griglia)
        var legend = new Panel { Dock = DockStyle.Top, Height = 26, BackColor = Color.White };
        var legTitle = new Label { Text = "Già su Splitwise:", AutoSize = true, Left = 8, Top = 5 };
        Label Swatch(string t, Color c, int left) => new()
        { Text = t, Left = left, Top = 3, Height = 20, AutoSize = true, BackColor = c, Padding = new Padding(4, 2, 4, 2) };
        var legG = Swatch("Verde = stessa data + importo esatto", DupGreen, 120);
        var legB = Swatch("Azzurro = importo esatto + descrizione simile", DupBlue, 340);
        var legY = Swatch("Giallo = importo ± € + stessa data o vicina (± gg)", DupYellow, 620);
        var legNote = new Label { AutoSize = true, Top = 5, Left = 950, ForeColor = Color.DimGray,
            Text = "(solo spese pagate da te; la tolleranza ± € vale solo per il Giallo)" };
        legend.Controls.AddRange(new Control[] { legTitle, legG, legB, legY, legNote });

        _tabPending.Controls.Add(_gridPending);
        _tabPending.Controls.Add(bottom);
        _tabPending.Controls.Add(legend);
        _tabPending.Controls.Add(filterBar);
    }

    // ---------- TAB INCOLLA TESTO ----------
    private void BuildPasteTab()
    {
        _pasteBox = new TextBox
        {
            Multiline = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill,
            PlaceholderText = "Incolla qui i movimenti (lista web BPER, anche multi-mese, o righe singole) e premi 'Analizza testo'."
        };
        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 46 };
        _btnParse = new Button { Text = "Analizza testo", Width = 140, Height = 30, Left = 10, Top = 8 };
        _btnParse.Click += (_, _) =>
        {
            ImportRows(ExpenseParser.ParseText(_pasteBox.Text), origin: "Incolla testo");
            _pasteBox.Clear();
            _tabs.SelectedTab = _tabPending;   // mostra il risultato in 'Da inviare'
            _ = RunSplitwiseAutoFlush();
        };
        var lbl = new Label { AutoSize = true, Left = 160, Top = 14,
            Text = "Riconosce la lista movimenti BPER (deduce anno e direzione) e i formati testo/CSV." };
        bottom.Controls.AddRange(new Control[] { _btnParse, lbl });
        _tabPaste.Controls.Add(_pasteBox);
        _tabPaste.Controls.Add(bottom);
    }

    // Vero se la spesa passa il filtro di "Da inviare" (tipo, sovrapposizioni, parole, range date).
    private bool PassesPendingFilter(ExpenseRecord e)
    {
        // Mostra: Uscite / Entrate / Tutte
        var show = _cmbShow.SelectedItem?.ToString() ?? "Uscite";
        if (show == "Uscite" && e.Direction != ExpenseDirection.Uscita) return false;
        if (show == "Entrate" && e.Direction != ExpenseDirection.Entrata) return false;

        // sovrapposizioni (carta di credito / ricarica Satispay): Escludi / Includi tutto / Solo sovrapposte
        var ov = _cmbOverlap.SelectedItem?.ToString() ?? "Escludi";
        bool isOverlap = ExpenseParser.IsOverlapDescription(e.Description);
        if (ov == "Escludi" && isOverlap) return false;
        if (ov == "Solo sovrapposte" && !isOverlap) return false;

        // filtro per fonte
        var srcSel = _cmbSource.SelectedItem?.ToString() ?? "(tutte)";
        if (srcSel != "(tutte)" && e.Source.ToString() != srcSel) return false;

        // filtro per tag (multi-selezione: mostra se ha ALMENO UNO dei tag spuntati)
        var selTags = _clbTags.CheckedItems.Cast<string>().ToList();
        if (selTags.Count > 0)
        {
            var rowTags = EffectiveTags(e);
            if (!selTags.Any(st => rowTags.Contains(st, StringComparer.OrdinalIgnoreCase))) return false;
        }

        // esclusioni per descrizione (clic destro -> Escludi)
        if (_excludedDescriptions.Contains((e.Description ?? "").Trim())) return false;

        // solo righe con PIÙ di un tag effettivo (per monitorare i tag duplicati/sbagliati)
        if (_chkMultiTag.Checked && EffectiveTags(e).Count(t => t != TagEngine.Untagged) <= 1) return false;

        // solo righe escluse dai totali (per rivederle/gestirle)
        if (_chkOnlyExcluded.Checked && !e.ExcludeTotals) return false;

        // solo righe inviate a Splitwise DA QUESTA APP (Inviata + id Splitwise)
        if (_chkOnlySentApp.Checked && !(e.Status == ExpenseStatus.Inviata && e.SplitwiseExpenseId > 0)) return false;

        // filtro per registro di import (impostato da "Mostra movimenti del registro" nella scheda Importazioni)
        if (_importBatchFilter != 0 && e.ImportBatch != _importBatchFilter) return false;

        var f = _txtPendingFilter.Text.Trim();
        if (f.Length > 0)
        {
            var terms = f.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            var desc = e.Description ?? "";
            if (!terms.All(t => desc.Contains(t, StringComparison.OrdinalIgnoreCase))) return false;
        }
        if (_dtpPendFrom.Checked && (e.Date is null || e.Date.Value.Date < _dtpPendFrom.Value.Date)) return false;
        if (_dtpPendTo.Checked && (e.Date is null || e.Date.Value.Date > _dtpPendTo.Value.Date)) return false;

        // range importo (>= da, <= a); confronto a 2 decimali; campo vuoto = nessun limite; accetta virgola o punto
        var amt = Math.Round(e.Amount, 2);
        if (TryParseAmount(_txtPendAmtFrom.Text, out var amtFrom) && amt < amtFrom) return false;
        if (TryParseAmount(_txtPendAmtTo.Text, out var amtTo) && amt > amtTo) return false;
        return true;
    }

    // Interpreta un importo digitato (virgola o punto, eventuale simbolo €); vuoto/non valido = nessun limite.
    private static bool TryParseAmount(string? s, out decimal value)
    {
        value = 0m;
        s = (s ?? "").Replace("€", "").Replace(" ", "").Replace(",", ".").Trim();
        if (s.Length == 0) return false;
        return decimal.TryParse(s, System.Globalization.NumberStyles.Number,
            System.Globalization.CultureInfo.InvariantCulture, out value);
    }

    // ---------- TAB INVIATE ----------
    private void BuildSentTab()
    {
        _gridSent = NewGrid();
        AddCommonColumns(_gridSent);
        _gridSent.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Inviata il", Width = 130, Name = "colSent" });
        _gridSent.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "ID Splitwise", Width = 100, Name = "colSid" });
        _gridSent.Columns.Add(new DataGridViewButtonColumn { HeaderText = "", Text = "Ripristina",
            UseColumnTextForButtonValue = true, Width = 90, Name = "colRestore" });
        _gridSent.Columns.Add(new DataGridViewButtonColumn { HeaderText = "", Text = "Elimina",
            UseColumnTextForButtonValue = true, Width = 80, Name = "colDelSent" });
        _gridSent.CellFormatting += SentGrid_Formatting;
        _gridSent.CellContentClick += (_, e) =>
        {
            if (e.RowIndex < 0 || _gridSent.Rows[e.RowIndex].Tag is not long id) return;
            var col = _gridSent.Columns[e.ColumnIndex].Name;
            if (col == "colRestore") RestoreSent(id);
            else if (col == "colDelSent") DeleteSentRow(id);
        };

        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 50 };
        var btnVerifySent = new Button { Text = "Verifica su Splitwise", Width = 170, Height = 34, Left = 10, Top = 8 };
        btnVerifySent.Click += async (_, _) => await CheckSentAsync();
        var btnDelNotFound = new Button { Text = "Elimina inviati non trovati", Width = 200, Height = 34, Left = 190, Top = 8 };
        btnDelNotFound.Click += (_, _) => DeleteSentNotFound();
        var lbl = new Label { AutoSize = true, Left = 400, Top = 16,
            Text = "Premi 'Verifica' per controllare; in rosso/barrate = non più su Splitwise." };
        bottom.Controls.AddRange(new Control[] { btnVerifySent, btnDelNotFound, lbl });
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
        BeginBusy();
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

            _gridSearch.SuspendLayout();
            try { foreach (var e in matches) _gridSearch.Rows.Add(e.Date, e.Description, e.Cost, PayerLabel(e.PayerId)); }
            finally { _gridSearch.ResumeLayout(); }

            decimal tot = matches.Sum(m => m.Cost);
            var crit = (term.Length > 0 ? $"\"{term}\"" : "(qualsiasi descrizione)") + amtDesc + dateDesc + payerDesc;
            _lblSearchInfo.Text = matches.Count == 0
                ? $"Nessun risultato per {crit}."
                : $"{matches.Count} risultati per {crit}. Totale: {tot:0.00} €.";
        }
        catch (Exception ex) { _lblSearchInfo.Text = "Errore ricerca: " + ex.Message; }
        finally { EndBusy(); }
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

    // ---------- TAB REGOLE TAG ----------
    private void BuildRulesTab()
    {
        _gridRules = NewGrid();
        _gridRules.AllowUserToAddRows = true;
        _gridRules.ReadOnly = false;
        _gridRules.DataError += (_, e) => e.ThrowException = false;   // tag non in lista: nessun crash
        _gridRules.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Parola (nella descrizione)", Name = "colKw", Width = 320 });
        _colTagRule = new DataGridViewComboBoxColumn
        { HeaderText = "Tag", Name = "colTag", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, FlatStyle = FlatStyle.Flat };
        _gridRules.Columns.Add(_colTagRule);

        // pannello sinistro: gestione lista tag codificati
        var left = new Panel { Dock = DockStyle.Left, Width = 220 };
        var lblTags = new Label { Text = "Tag disponibili:", AutoSize = true, Left = 8, Top = 8 };
        _lstTags = new ListBox { Left = 8, Top = 30, Width = 200, Height = 300 };
        _txtNewTag = new TextBox { Left = 8, Top = 338, Width = 130 };
        var btnAddTag = new Button { Text = "Aggiungi", Left = 142, Top = 336, Width = 66, Height = 26 };
        var btnDelTag = new Button { Text = "Elimina tag", Left = 8, Top = 368, Width = 120, Height = 26 };
        btnAddTag.Click += (_, _) => { _db.AddTag(_txtNewTag.Text); _txtNewTag.Clear(); RefreshTagsUi(); };
        _txtNewTag.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; _db.AddTag(_txtNewTag.Text); _txtNewTag.Clear(); RefreshTagsUi(); } };
        btnDelTag.Click += (_, _) => { if (_lstTags.SelectedItem is string t) { _db.DeleteTag(t); RefreshTagsUi(); } };
        left.Controls.AddRange(new Control[] { lblTags, _lstTags, _txtNewTag, btnAddTag, btnDelTag });

        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 50 };
        var btnSave = new Button { Text = "Salva regole", Width = 130, Height = 34, Left = 10, Top = 8 };
        btnSave.Click += (_, _) => SaveRules();
        var lbl = new Label { AutoSize = true, Left = 150, Top = 16,
            Text = "A sinistra crei i tag; qui associ Parola → Tag (scegli dal menu). Riga vuota in fondo per aggiungere; Canc per eliminare." };
        bottom.Controls.AddRange(new Control[] { btnSave, lbl });

        _tabRules.Controls.Add(_gridRules);
        _tabRules.Controls.Add(left);
        _tabRules.Controls.Add(bottom);
    }

    // Elimina la riga sotto il cursore. Se è stata inviata DALL'APP a Splitwise (Inviata + id) e Splitwise è attivo,
    // chiede COSA eliminare: solo locale / entrambe / solo Splitwise. Altrimenti elimina solo in locale.
    private async Task DeleteRowUnderCursor()
    {
        if (_ctxRowId == 0) { SetStatus("Clic destro su una riga, poi 'Elimina questa riga'.", true); return; }
        var rec = _pendingView.FirstOrDefault(x => x.Id == _ctxRowId);
        var info = rec != null ? $"{rec.Date:dd/MM/yyyy} - {rec.Description} - {rec.Amount:0.00} €" : "questa riga";
        bool sentApp = rec != null && _splitwiseReady && rec.Status == ExpenseStatus.Inviata && rec.SplitwiseExpenseId > 0;

        if (sentApp)
        {
            var choice = AskSentDeleteChoice(info, rec!.SplitwiseExpenseId);
            switch (choice)
            {
                case SentDeleteChoice.Annulla:
                    return;

                case SentDeleteChoice.SoloLocale:
                    _db.DeleteMany(new List<long> { rec.Id });
                    CleanupRowRefs(rec.Id);
                    _pendingNeedsReload = true; LoadPending();
                    SetStatus("Riga eliminata in locale. Il pagamento resta su Splitwise.");
                    return;

                case SentDeleteChoice.SoloSplitwise:
                    try
                    {
                        BeginBusy("Eliminazione su Splitwise…");
                        await _client!.DeleteExpenseAsync(rec.SplitwiseExpenseId);
                        _db.UnmarkSent(rec.Id); _dupInfo.Remove(rec.Id);
                        _pendingNeedsReload = true; LoadPending();
                        SetStatus("Pagamento eliminato su Splitwise; riga riportata tra i 'da inviare'.");
                    }
                    catch (Exception ex) { SetStatus("Eliminazione su Splitwise non riuscita: " + ex.Message, true); }
                    finally { EndBusy(); }
                    return;

                case SentDeleteChoice.Entrambe:
                    try
                    {
                        BeginBusy("Eliminazione su Splitwise…");
                        await _client!.DeleteExpenseAsync(rec.SplitwiseExpenseId);   // prima Splitwise; se fallisce non cancello in locale
                        _db.DeleteMany(new List<long> { rec.Id });
                        CleanupRowRefs(rec.Id);
                        _pendingNeedsReload = true; LoadPending();
                        SetStatus("Eliminata su Splitwise e in locale.");
                    }
                    catch (Exception ex) { SetStatus("Splitwise non eliminato (riga locale mantenuta): " + ex.Message, true); }
                    finally { EndBusy(); }
                    return;
            }
        }

        // riga non inviata-dall'app: semplice eliminazione locale
        if (MessageBox.Show($"Eliminare definitivamente questa riga (solo locale)?\n\n{info}", "Elimina riga",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        _db.DeleteMany(new List<long> { rec?.Id ?? _ctxRowId });
        CleanupRowRefs(_ctxRowId);
        _pendingNeedsReload = true; LoadPending();
        SetStatus("Riga eliminata.");
    }

    private void CleanupRowRefs(long id)
    { _checked.Remove(id); _split.Remove(id); _dupInfo.Remove(id); _sentNotFound.Remove(id); }

    private enum SentDeleteChoice { Annulla, SoloLocale, Entrambe, SoloSplitwise }

    // Dialog di scelta per le righe inviate dall'app: cosa eliminare.
    private SentDeleteChoice AskSentDeleteChoice(string info, long swId)
    {
        var choice = SentDeleteChoice.Annulla;
        using var f = new Form
        {
            Text = "Elimina spesa inviata a Splitwise", FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent, MinimizeBox = false, MaximizeBox = false,
            ClientSize = new Size(480, 170)
        };
        f.Controls.Add(new Label { Left = 12, Top = 12, Width = 456, Height = 64, AutoSize = false,
            Text = $"{info}\nInviata a Splitwise (ID {swId}).\nCosa vuoi eliminare?" });
        var bLocal = new Button { Text = "Solo locale", Left = 12, Top = 90, Width = 145, Height = 34 };
        var bBoth = new Button { Text = "Entrambe", Left = 165, Top = 90, Width = 145, Height = 34 };
        var bSw = new Button { Text = "Solo Splitwise", Left = 318, Top = 90, Width = 145, Height = 34 };
        var bCancel = new Button { Text = "Annulla", Left = 318, Top = 130, Width = 145, Height = 28, DialogResult = DialogResult.Cancel };
        bLocal.Click += (_, _) => { choice = SentDeleteChoice.SoloLocale; f.DialogResult = DialogResult.OK; };
        bBoth.Click += (_, _) => { choice = SentDeleteChoice.Entrambe; f.DialogResult = DialogResult.OK; };
        bSw.Click += (_, _) => { choice = SentDeleteChoice.SoloSplitwise; f.DialogResult = DialogResult.OK; };
        f.CancelButton = bCancel;
        f.Controls.AddRange(new Control[] { bLocal, bBoth, bSw, bCancel });
        f.ShowDialog(this);
        return choice;
    }

    // Attiva/disattiva l'esclusione dai totali per la riga sotto il cursore.
    private void ToggleExcludeTotalsForRow()
    {
        if (_ctxRowId == 0) { SetStatus("Seleziona prima una riga.", true); return; }
        var rec = _pendingView.FirstOrDefault(x => x.Id == _ctxRowId);
        if (rec == null) return;
        bool newVal = !rec.ExcludeTotals;
        _db.SetExcludeTotals(_ctxRowId, newVal);
        rec.ExcludeTotals = newVal;
        LoadPending();
        SetStatus(newVal ? "Movimento escluso dai totali/statistiche." : "Movimento reincluso nei totali.");
    }

    // Suddivide il movimento sotto il cursore in più parti (importo+tag); l'originale resta ma escluso dai totali.
    private void SplitMovementForRow()
    {
        if (_ctxRowId == 0) { SetStatus("Seleziona prima una riga da suddividere.", true); return; }
        var rec = _pendingView.FirstOrDefault(x => x.Id == _ctxRowId);
        if (rec == null) return;
        if (rec.Amount <= 0) { MessageBox.Show("Il movimento non ha un importo valido.", "Suddividi"); return; }
        var tags = _db.GetTags();
        if (tags.Count == 0) { MessageBox.Show("Crea prima dei tag nella scheda 'Regole tag'.", "Nessun tag"); return; }

        using var dlg = new SplitMovementDialog(rec, tags);
        if (dlg.ShowDialog(this) != DialogResult.OK || dlg.Parts.Count == 0) return;

        // crea le parti come nuovi movimenti (stessa data/fonte/direzione del padre), con il tag manuale scelto
        foreach (var p in dlg.Parts)
        {
            var desc = string.IsNullOrWhiteSpace(p.Tag) ? rec.Description : $"{rec.Description} [{p.Tag}]";
            var id = _db.AddPending(rec.Date, desc, p.Amount, rec.Source, rec.Direction);
            _split[id] = (SplitMode.Equal, null);
            if (!string.IsNullOrWhiteSpace(p.Tag)) _db.SetManualTags(id, p.Tag);
            if (p.Send && rec.Direction == ExpenseDirection.Uscita) _checked.Add(id);  // pronta da inviare a Splitwise
        }
        // il padre resta in archivio (àncora per il dedup ai reimport) ma non conta più nei totali
        _db.SetExcludeTotals(_ctxRowId, true);
        rec.ExcludeTotals = true;

        _pendingNeedsReload = true;
        LoadPending();
        int daInviare = dlg.Parts.Count(p => p.Send && rec.Direction == ExpenseDirection.Uscita);
        SetStatus($"Movimento suddiviso in {dlg.Parts.Count} parti. Originale escluso dai totali." +
                  (daInviare > 0 ? $" {daInviare} parte/i spuntata/e per Splitwise." : ""));
    }

    // Tag manuali (solo per quella riga, senza creare regole) dal movimento su cui si è fatto clic destro.
    private void SetManualTagForRow()
    {
        if (_ctxRowId == 0) { SetStatus("Seleziona una riga (clic destro su una colonna non modificabile).", true); return; }
        var tags = _db.GetTags();
        if (tags.Count == 0)
        { MessageBox.Show("Crea prima dei tag nella scheda 'Regole tag'.", "Nessun tag"); return; }

        var rec = _pendingView.FirstOrDefault(x => x.Id == _ctxRowId);
        var current = (rec?.ManualTags ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        using var dlg = new ManualTagDialog(tags, current);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        var selected = dlg.SelectedTags.ToList();

        // riga cliccata: imposta esattamente i tag scelti (puoi anche toglierne)
        ApplyManualTags(_ctxRowId, selected, append: false);

        // se ci sono altre righe spuntate, accoda gli stessi tag anche a quelle (senza togliere i loro)
        var others = _checked.Where(id => id != _ctxRowId && _pendingView.Any(x => x.Id == id)).ToList();
        foreach (var id in others) ApplyManualTags(id, selected, append: true);

        // aggiorno i totali (i tag possono spostare righe dentro/fuori "Investimenti") senza ricostruire tutta la griglia
        RefreshPendingTotalsOnly();
        SetStatus(others.Count > 0
            ? $"Tag applicati a {others.Count + 1} righe (accodati su quelle spuntate)."
            : "Tag manuali della riga aggiornati.");
    }

    // Imposta/accoda i tag manuali di una riga e aggiorna SOLO la sua cella "Tag" (niente reload completo = veloce).
    private void ApplyManualTags(long id, List<string> tagsToSet, bool append)
    {
        var rec = _pendingView.FirstOrDefault(x => x.Id == id);
        if (rec == null) return;

        List<string> final;
        if (append)
        {
            final = (rec.ManualTags ?? "")
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            foreach (var t in tagsToSet)
                if (!final.Contains(t, StringComparer.OrdinalIgnoreCase)) final.Add(t);
        }
        else final = tagsToSet.ToList();

        var csv = string.Join("\n", final);   // separatore newline (i nomi tag possono contenere virgole)
        _db.SetManualTags(id, csv);
        rec.ManualTags = csv;   // aggiorna in memoria così EffectiveTags è coerente senza rileggere il DB

        foreach (DataGridViewRow r in _gridPending.Rows)
            if (r.Tag is long rid && rid == id)
            { r.Cells["colTags"].Value = string.Join(", ", EffectiveTags(rec)); break; }
    }

    // Regola veloce dal movimento su cui si è fatto clic destro.
    private void QuickAddRuleFromRow()
    {
        var desc = _ctxDescription ?? "";
        if (desc.Trim().Length == 0) { SetStatus("Nessuna descrizione su cui creare la regola.", true); return; }
        var tags = _db.GetTags();
        if (tags.Count == 0)
        { MessageBox.Show("Crea prima dei tag nella scheda 'Regole tag'.", "Nessun tag"); return; }

        using var dlg = new QuickRuleDialog(desc, tags);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        _db.AddTagRule(dlg.Keyword, dlg.Tag);
        _tagRules = _db.GetTagRules();
        RefreshTagFilter();
        LoadPending();   // ricalcola subito i tag in griglia
        SetStatus($"Regola aggiunta: \"{dlg.Keyword}\" → {dlg.Tag}.");
    }

    // Ricarica la lista tag (listbox sinistra) e le voci della colonna combo delle regole.
    private void RefreshTagsUi()
    {
        var tags = _db.GetTags();
        _lstTags.BeginUpdate();
        _lstTags.Items.Clear();
        foreach (var t in tags) _lstTags.Items.Add(t);
        _lstTags.EndUpdate();
        _colTagRule.Items.Clear();
        _colTagRule.Items.AddRange(tags.Cast<object>().ToArray());
    }

    private void LoadRules()
    {
        RefreshTagsUi();
        // assicura che i tag già usati nelle regole siano selezionabili nella combo
        foreach (var t in _db.GetTagRules().Select(r => r.Tag).Distinct())
            if (!_colTagRule.Items.Contains(t)) _colTagRule.Items.Add(t);

        _gridRules.Rows.Clear();
        foreach (var (kw, tag) in _db.GetTagRules())
            _gridRules.Rows.Add(kw, tag);
    }

    private void SaveRules()
    {
        var rules = new List<(string, string)>();
        foreach (DataGridViewRow row in _gridRules.Rows)
        {
            if (row.IsNewRow) continue;
            var kw = Convert.ToString(row.Cells["colKw"].Value)?.Trim() ?? "";
            var tag = Convert.ToString(row.Cells["colTag"].Value)?.Trim() ?? "";
            if (kw.Length > 0 && tag.Length > 0) rules.Add((kw, tag));
        }
        _db.SaveTagRules(rules);
        _tagRules = _db.GetTagRules();   // aggiorna la cache
        RefreshTagFilter();
        _pendingNeedsReload = true;      // i tag in griglia vanno ricalcolati
        SetStatus($"{rules.Count} regole tag salvate.");
    }

    // Ripopola il filtro tag in 'Da inviare' con i tag definiti dalle regole.
    // Ripopola la checklist del filtro tag (multi-selezione), mantenendo le spunte esistenti.
    private void RefreshTagFilter()
    {
        var chk = _clbTags.CheckedItems.Cast<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);

        // include TUTTI i tag che possono comparire su un movimento: definiti + da regole + manuali
        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in _db.GetTags()) names.Add(t);
        foreach (var r in _tagRules) if (!string.IsNullOrWhiteSpace(r.Tag)) names.Add(r.Tag);
        foreach (var e in _db.GetAll())
            foreach (var m in (e.ManualTags ?? "")
                         .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                names.Add(m);

        _clbTags.Items.Clear();
        foreach (var t in names) _clbTags.Items.Add(t, chk.Contains(t));
        _clbTags.Items.Add(TagEngine.Untagged, chk.Contains(TagEngine.Untagged));
        UpdateTagFilterButton();
    }

    private void UpdateTagFilterButton()
    {
        var sel = _clbTags.CheckedItems.Cast<string>().ToList();
        _btnTagFilter.Text = sel.Count switch
        {
            0 => "Tag ▾",
            1 => sel[0] + " ▾",
            _ => $"{sel.Count} tag ▾"
        };
    }

    // ---------- TAB STATISTICHE ----------
    private void BuildStatsTab()
    {
        _gridStats = NewGrid();
        _gridStats.AutoGenerateColumns = true;
        _gridStats.ReadOnly = true;

        var top = new Panel { Dock = DockStyle.Top, Height = 40 };
        var lblD = new Label { Text = "Da", AutoSize = true, Left = 10, Top = 12 };
        _dtpStatsFrom = new DateTimePicker { Left = 40, Top = 8, Width = 110, Format = DateTimePickerFormat.Short, ShowCheckBox = true, Checked = false };
        var lblA = new Label { Text = "a", AutoSize = true, Left = 158, Top = 12 };
        _dtpStatsTo = new DateTimePicker { Left = 175, Top = 8, Width = 110, Format = DateTimePickerFormat.Short, ShowCheckBox = true, Checked = false };
        var lblV = new Label { Text = "Vista", AutoSize = true, Left = 300, Top = 12 };
        _cmbStatsView = new ComboBox { Left = 340, Top = 8, Width = 160, DropDownStyle = ComboBoxStyle.DropDownList };
        _cmbStatsView.Items.AddRange(new object[] { "Totale per tag", "Per tag e mese" });
        _cmbStatsView.SelectedIndex = 0;
        var btnCalc = new Button { Text = "Calcola", Left = 510, Top = 6, Width = 100, Height = 26 };
        btnCalc.Click += (_, _) => ComputeStats();
        _cmbStatsView.SelectedIndexChanged += (_, _) => ComputeStats();
        top.Controls.AddRange(new Control[] { lblD, _dtpStatsFrom, lblA, _dtpStatsTo, lblV, _cmbStatsView, btnCalc });

        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 28 };
        _lblStatsInfo = new Label { AutoSize = true, Left = 10, Top = 6,
            Text = "Spesa (uscite, escluse sovrapposizioni) classificata per tag secondo le regole." };
        bottom.Controls.Add(_lblStatsInfo);

        _tabStats.Controls.Add(_gridStats);
        _tabStats.Controls.Add(top);
        _tabStats.Controls.Add(bottom);
    }

    private void ComputeStats()
    {
        if (_db is null) return;
        _tagRules = _db.GetTagRules();   // assicura tag aggiornati per il calcolo
        DateTime? from = _dtpStatsFrom.Checked ? _dtpStatsFrom.Value.Date : null;
        DateTime? to = _dtpStatsTo.Checked ? _dtpStatsTo.Value.Date : null;

        // uscite reali (escluse sovrapposizioni), su tutti i record
        var recs = _db.GetAll()
            .Where(e => e.Direction == ExpenseDirection.Uscita && !ExpenseParser.IsOverlapDescription(e.Description) && !e.ExcludeTotals)
            .Where(e => (from is null || (e.Date.HasValue && e.Date.Value.Date >= from.Value))
                        && (to is null || (e.Date.HasValue && e.Date.Value.Date <= to.Value)))
            .ToList();

        var view = _cmbStatsView.SelectedItem?.ToString() ?? "Totale per tag";
        var dt = new DataTable();

        if (view == "Totale per tag")
        {
            dt.Columns.Add("Tag", typeof(string));
            dt.Columns.Add("Totale €", typeof(decimal));
            dt.Columns.Add("N movimenti", typeof(int));
            var agg = new Dictionary<string, (decimal sum, int n)>();
            foreach (var e in recs)
                foreach (var tag in EffectiveTags(e))
                {
                    var cur = agg.GetValueOrDefault(tag);
                    agg[tag] = (cur.sum + e.Amount, cur.n + 1);
                }
            foreach (var kv in agg.OrderByDescending(k => k.Value.sum))
                dt.Rows.Add(kv.Key, kv.Value.sum, kv.Value.n);
        }
        else // Per tag e mese
        {
            var months = recs.Where(e => e.Date.HasValue)
                .Select(e => e.Date!.Value.ToString("yyyy-MM"))
                .Distinct().OrderBy(m => m).ToList();
            dt.Columns.Add("Tag", typeof(string));
            foreach (var m in months) dt.Columns.Add(m, typeof(decimal));
            dt.Columns.Add("Totale", typeof(decimal));

            var agg = new Dictionary<string, Dictionary<string, decimal>>();
            foreach (var e in recs.Where(e => e.Date.HasValue))
            {
                var m = e.Date!.Value.ToString("yyyy-MM");
                foreach (var tag in EffectiveTags(e))
                {
                    if (!agg.TryGetValue(tag, out var byMonth)) agg[tag] = byMonth = new();
                    byMonth[m] = byMonth.GetValueOrDefault(m) + e.Amount;
                }
            }
            foreach (var kv in agg.OrderByDescending(k => k.Value.Values.Sum()))
            {
                var row = dt.NewRow();
                row["Tag"] = kv.Key;
                decimal tot = 0;
                foreach (var m in months) { var v = kv.Value.GetValueOrDefault(m); row[m] = v; tot += v; }
                row["Totale"] = tot;
                dt.Rows.Add(row);
            }
        }

        _gridStats.DataSource = dt;
        foreach (DataGridViewColumn col in _gridStats.Columns)
            if (col.ValueType == typeof(decimal))
            {
                col.DefaultCellStyle.Format = "0.00";
                col.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;
            }

        decimal grand = recs.Sum(e => e.Amount);
        _lblStatsInfo.Text = $"{recs.Count} uscite, totale {grand:0.00} € (i multi-tag contano in ogni tag).";
    }

    // ---------- TAB IMPORTAZIONI (caricamento + registro) ----------
    private void BuildLogTab()
    {
        // pulsanti di caricamento documenti
        var topButtons = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 38 };
        _btnCsv = new Button { Text = "Carica file (BPER/Satispay/CSV)…", Width = 215 };
        _btnPasteStamp = new Button { Text = "Incolla immagine (STAMP)", Width = 185 };
        _btnAddManual = new Button { Text = "+ Riga manuale", Width = 115 };
        _btnCsv.Click += (_, _) => LoadCsv();
        _btnPasteStamp.Click += (_, _) => PasteStampFromClipboard();
        _btnAddManual.Click += (_, _) => AddManualRow();
        var btnInbox = new Button { Text = "Processa inbox", Width = 120 };
        btnInbox.Click += async (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(_cfg?.InboxFolder))
            { SetStatus("Cartella inbox non configurata (InboxFolder in appsettings.json).", true); return; }
            if (ImportFolder(_cfg.InboxFolder) > 0) { await RunSplitwiseAutoFlush(); await CheckPendingDuplicatesAsync(); }
        };
        var btnFolder = new Button { Text = "Processa cartella…", Width = 150 };
        btnFolder.Click += async (_, _) =>
        {
            using var fbd = new FolderBrowserDialog { Description = "Cartella con file BPER/Satispay/CSV o screenshot" };
            if (fbd.ShowDialog() != DialogResult.OK) return;
            if (ImportFolder(fbd.SelectedPath) > 0) { await RunSplitwiseAutoFlush(); await CheckPendingDuplicatesAsync(); }
        };
        topButtons.Controls.AddRange(new Control[] { _btnCsv, _btnPasteStamp, btnInbox, btnFolder, _btnAddManual });

        _gridLog = NewGrid();
        _gridLog.ReadOnly = true;
        _gridLog.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "ID", Name = "colId", Width = 60,
            DefaultCellStyle = new DataGridViewCellStyle { Alignment = DataGridViewContentAlignment.MiddleRight } });
        _gridLog.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Data/ora", Name = "colWhen", Width = 140,
            DefaultCellStyle = new DataGridViewCellStyle { Format = "dd/MM/yyyy HH:mm" } });
        _gridLog.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Origine (file/sorgente)", Name = "colOrigin",
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill });
        _gridLog.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Importati", Name = "colImp", Width = 90,
            DefaultCellStyle = new DataGridViewCellStyle { Alignment = DataGridViewContentAlignment.MiddleRight } });
        _gridLog.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Scartati", Name = "colSkip", Width = 90,
            DefaultCellStyle = new DataGridViewCellStyle { Alignment = DataGridViewContentAlignment.MiddleRight } });

        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 40 };
        var btnDelBatch = new Button { Text = "Elimina registro (e le sue righe)", Left = 10, Top = 7, Width = 230, Height = 26 };
        btnDelBatch.Click += (_, _) => DeleteSelectedImportBatch();
        var btnShowMov = new Button { Text = "Mostra movimenti del registro", Left = 250, Top = 7, Width = 220, Height = 26 };
        btnShowMov.Click += (_, _) =>
        {
            if (_gridLog.CurrentRow?.Tag is not long bid)
            { SetStatus("Seleziona prima un registro.", true); return; }
            ShowBatchInMovimenti(bid);
        };
        var btnClear = new Button { Text = "Svuota registro", Left = 480, Top = 7, Width = 130, Height = 26 };
        btnClear.Click += (_, _) =>
        {
            if (MessageBox.Show("Svuotare il registro degli import? (NON elimina i movimenti, solo lo storico)", "Conferma",
                MessageBoxButtons.YesNo) == DialogResult.Yes) { _db.ClearImportLog(); LoadLog(); }
        };
        _lblLogInfo = new Label { AutoSize = true, Left = 620, Top = 12, Text = "" };
        bottom.Controls.AddRange(new Control[] { btnDelBatch, btnShowMov, btnClear, _lblLogInfo });

        // doppio clic su un registro → mostra i suoi movimenti in Movimenti
        _gridLog.CellDoubleClick += (_, e) =>
        { if (e.RowIndex >= 0 && _gridLog.Rows[e.RowIndex].Tag is long bid) ShowBatchInMovimenti(bid); };

        _tabLog.Controls.Add(_gridLog);
        _tabLog.Controls.Add(bottom);
        _tabLog.Controls.Add(topButtons);
    }

    // Mostra in 'Movimenti' solo le righe importate con un certo registro (batch), azzerando gli altri filtri di disturbo.
    private void ShowBatchInMovimenti(long batchId)
    {
        _importBatchFilter = batchId;
        _cmbShow.SelectedIndex = 2;          // "Tutte" (un registro può avere entrate e uscite)
        _cmbOverlap.SelectedIndex = 1;       // "Includi tutto"
        _dtpPendFrom.Checked = false;        // niente vincolo di data
        _dtpPendTo.Checked = false;
        _checked.Clear();
        _tabs.SelectedTab = _tabPending;
        LoadPending();
        SetStatus($"Mostro i movimenti del registro #{batchId}. (Pulisci filtri per tornare alla vista normale.)");
    }

    // ---------- TAB OPZIONI ----------
    private void BuildOptionsTab()
    {
        var p = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(16) };
        int y = 10;
        const int FW = 460;

        // Titolo sezione (grassetto)
        void Head(string t)
        { p.Controls.Add(new Label { Text = t, AutoSize = true, Left = 0, Top = y, Font = new Font(Font, FontStyle.Bold) }); y += 28; }

        // Riga di nota (testo grigio che va a capo)
        void Note(string t, Color? color = null)
        {
            var l = new Label { Text = t, AutoSize = true, Left = 0, Top = y, MaximumSize = new Size(740, 0),
                ForeColor = color ?? SystemColors.GrayText };
            p.Controls.Add(l); y += l.PreferredHeight + 6;
        }

        // Etichetta SOPRA + campo SOTTO; eventuale pulsante a fianco del campo
        void Field(string label, Control field, Button? side = null)
        {
            p.Controls.Add(new Label { Text = label, AutoSize = true, Left = 0, Top = y });
            y += 20;
            field.Left = 0; field.Top = y;
            if (field.Width < 10) field.Width = FW;
            p.Controls.Add(field);
            if (side != null) { side.Left = field.Left + field.Width + 8; side.Top = y - 1; p.Controls.Add(side); }
            y += Math.Max(field.Height, 24) + 12;
        }

        // ---- Database ----
        Head("Database");
        _optDbCurrent = new TextBox { Width = 700, ReadOnly = true, BackColor = SystemColors.Control };
        Field("DB attualmente in uso:", _optDbCurrent);
        _optDbPath = new TextBox { Width = FW, PlaceholderText = "vuoto = history.db accanto al programma" };
        var btnBrowseDb = new Button { Text = "Sfoglia…", Width = 90, Height = 24 };
        btnBrowseDb.Click += (_, _) =>
        {
            using var sfd = new SaveFileDialog { Filter = "SQLite (*.db)|*.db|Tutti i file|*.*", FileName = "history.db", OverwritePrompt = false };
            if (sfd.ShowDialog() == DialogResult.OK) _optDbPath.Text = sfd.FileName;
        };
        Field("Percorso DB:", _optDbPath, btnBrowseDb);

        // ---- Splitwise ----
        Head("Splitwise / parametri");
        var help = new LinkLabel
        {
            Text = "Come ottenere le chiavi: registra un'app su secure.splitwise.com/apps → \"Register your application\", poi copia API key (Consumer Key) e Secret.",
            Left = 0, Top = y, AutoSize = true, MaximumSize = new Size(740, 0), LinkColor = Color.RoyalBlue
        };
        help.LinkClicked += (_, _) => OpenUrl("https://secure.splitwise.com/apps");
        p.Controls.Add(help); y += help.PreferredHeight + 4;
        Note("Group ID: è il numero nell'URL del gruppo su splitwise.com/groups/NUMERO (oppure avvia l'app con argomento \"groups\").");

        _optKey = new TextBox { Width = FW };               Field("Consumer Key:", _optKey);
        _optSecret = new TextBox { Width = FW, UseSystemPasswordChar = true }; Field("Consumer Secret:", _optSecret);
        _optGroup = new TextBox { Width = 200 };             Field("Group ID:", _optGroup);
        _optCurrency = new TextBox { Width = 120 };          Field("Valuta (es. EUR):", _optCurrency);
        _optNearby = new NumericUpDown { Width = 80, Minimum = 0, Maximum = 60, Value = 3 };
        Field("Giorni intorno per il controllo duplicati:", _optNearby);
        _optAmtTol = new NumericUpDown { Width = 90, Minimum = 0, Maximum = 100, DecimalPlaces = 2, Increment = 0.10M, Value = 0.50M };
        Field("Tolleranza importo Splitwise (€):", _optAmtTol);
        _optInbox = new TextBox { Width = FW };
        var btnBrowseInbox = new Button { Text = "Sfoglia…", Width = 90, Height = 24 };
        btnBrowseInbox.Click += (_, _) =>
        { using var fbd = new FolderBrowserDialog(); if (fbd.ShowDialog() == DialogResult.OK) _optInbox.Text = fbd.SelectedPath; };
        Field("Cartella inbox:", _optInbox, btnBrowseInbox);

        var btnSave = new Button { Text = "Salva impostazioni", Left = 0, Top = y, Width = 180, Height = 32 };
        btnSave.Click += (_, _) => SaveOptions();
        p.Controls.Add(btnSave);
        p.Controls.Add(new Label { Text = "Le modifiche a DB/chiavi richiedono il RIAVVIO.", AutoSize = true, Left = 190, Top = y + 7, ForeColor = Color.DimGray });
        y += 50;

        // ---- Sicurezza ----
        Head("⚠ Sicurezza — azzera database");
        Note("Cancella TUTTI i dati (movimenti, registri, regole, tag). Irreversibile, non tocca Splitwise.", Color.Firebrick);
        _optResetConfirm = new TextBox { Width = 160, PlaceholderText = "delete" };
        var btnReset = new Button { Text = "Azzera database", Width = 150, Height = 24, ForeColor = Color.Firebrick };
        btnReset.Click += (_, _) => ResetDatabase();
        Field("Per confermare scrivi \"delete\":", _optResetConfirm, btnReset);

        _tabOptions.Controls.Add(p);
    }

    // Mostra un banner rosso se si sta girando in build DEBUG o puntando a un DB dentro una cartella "Debug".
    private void UpdateEnvBanner()
    {
        bool debugBuild = false;
#if DEBUG
        debugBuild = true;
#endif
        var dbPath = _db?.DbPath ?? "";
        bool debugDb = dbPath.Replace('/', '\\').Contains("\\debug\\", StringComparison.OrdinalIgnoreCase);

        if (debugBuild || debugDb)
        {
            var why = debugBuild && debugDb ? "BUILD DEBUG + DB DI DEBUG"
                    : debugBuild ? "BUILD DEBUG"
                    : "DB DI DEBUG";
            _envBanner.Text = $"⚠ {why} — DB: {dbPath}";
            _envBanner.Visible = true;
            Text = "[DEBUG] " + Text.TrimStart('[', 'D', 'E', 'B', 'U', 'G', ']', ' ');
        }
        else _envBanner.Visible = false;
    }

    private static void OpenUrl(string url)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { /* nessun browser disponibile */ }
    }

    // ---------- TAB REGOLE SPLITWISE ----------
    private void BuildSwRulesTab()
    {
        var top = new Panel { Dock = DockStyle.Top, Height = 70, Padding = new Padding(10, 8, 10, 0) };
        top.Controls.Add(new Label { Dock = DockStyle.Top, Height = 44, Text =
            "Se la descrizione di un movimento importato CONTIENE una di queste frasi/parole (senza distinzione maiuscole/accenti), " +
            "l'app prova a inviarlo a Splitwise automaticamente. Prima verifica con 'Confronta Splitwise': se risulterebbe " +
            "verde/azzurro/giallo NON lo invia e scrive nelle Note \"verificare se caricare in splitwise\"." });

        var add = new Panel { Dock = DockStyle.Top, Height = 34 };
        _txtSwRule = new TextBox { Left = 0, Top = 4, Width = 360, PlaceholderText = "frase o parola (es. CRESCIAMO, ASILO, conto condiviso)" };
        var btnAdd = new Button { Text = "Aggiungi", Left = 368, Top = 2, Width = 90, Height = 26 };
        var btnDel = new Button { Text = "Elimina selezionata", Left = 464, Top = 2, Width = 150, Height = 26 };
        void AddRule()
        {
            var p = _txtSwRule.Text.Trim();
            if (p.Length == 0) return;
            if (!_swRules.Contains(p, StringComparer.OrdinalIgnoreCase)) _swRules.Add(p);
            _txtSwRule.Clear();
            _db.SaveSplitwiseRules(_swRules);
            RefreshSwRules();
        }
        btnAdd.Click += (_, _) => AddRule();
        _txtSwRule.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; AddRule(); } };
        btnDel.Click += (_, _) =>
        {
            if (_lstSwRules.SelectedItem is string s)
            { _swRules.RemoveAll(x => string.Equals(x, s, StringComparison.OrdinalIgnoreCase)); _db.SaveSplitwiseRules(_swRules); RefreshSwRules(); }
        };
        add.Controls.AddRange(new Control[] { _txtSwRule, btnAdd, btnDel });

        _lstSwRules = new ListBox { Dock = DockStyle.Fill };

        _tabSwRules.Controls.Add(_lstSwRules);
        _tabSwRules.Controls.Add(add);
        _tabSwRules.Controls.Add(top);
    }

    private void RefreshSwRules()
    {
        _lstSwRules.BeginUpdate();
        _lstSwRules.Items.Clear();
        foreach (var r in _swRules.OrderBy(x => x)) _lstSwRules.Items.Add(r);
        _lstSwRules.EndUpdate();
    }

    // Dal movimento sotto il cursore: prepara una regola Splitwise (descrizione modificabile) e la aggiunge.
    private void AddSplitwiseRuleForRow()
    {
        if (!_splitwiseReady) { SetStatus("Splitwise non attivo.", true); return; }
        var rec = _pendingView.FirstOrDefault(x => x.Id == _ctxRowId);
        var initial = rec?.Description ?? _ctxDescription ?? "";

        using var f = new Form
        {
            Text = "Aggiungi regola Splitwise", FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent, MinimizeBox = false, MaximizeBox = false,
            ClientSize = new Size(540, 210)
        };
        var lbl = new Label { Left = 12, Top = 10, Width = 516, Height = 40,
            Text = "Seleziona nella descrizione (o scrivi) la parola/frase: se un movimento la contiene, verrà inviato a Splitwise (con verifica anti-duplicato)." };
        var src = new TextBox { Left = 12, Top = 54, Width = 516, Height = 52, Multiline = true, ReadOnly = true,
            Text = initial, ScrollBars = ScrollBars.Vertical };
        var lblK = new Label { Left = 12, Top = 120, AutoSize = true, Text = "Regola:" };
        var kw = new TextBox { Left = 70, Top = 117, Width = 458, Text = initial };
        void Sync(object? s, EventArgs e) { if (!string.IsNullOrWhiteSpace(src.SelectedText)) kw.Text = src.SelectedText.Trim(); }
        src.MouseUp += Sync; src.KeyUp += Sync;
        var ok = new Button { Text = "Aggiungi", Left = 340, Top = 160, Width = 90, Height = 30, DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "Annulla", Left = 438, Top = 160, Width = 90, Height = 30, DialogResult = DialogResult.Cancel };
        f.AcceptButton = ok; f.CancelButton = cancel;
        f.Controls.AddRange(new Control[] { lbl, src, lblK, kw, ok, cancel });
        if (f.ShowDialog(this) != DialogResult.OK) return;

        var phrase = kw.Text.Trim();
        if (phrase.Length == 0) return;
        if (!_swRules.Contains(phrase, StringComparer.OrdinalIgnoreCase)) _swRules.Add(phrase);
        _db.SaveSplitwiseRules(_swRules);
        RefreshSwRules();
        SetStatus($"Regola Splitwise aggiunta: \"{phrase}\".");
    }

    // Applica le Regole Splitwise ai movimenti appena importati: invia gli "puliti", segnala nelle Note i dubbi.
    private async Task RunSplitwiseAutoFlush()
    {
        if (_autoCandidateIds.Count == 0) return;
        var ids = _autoCandidateIds.ToList();
        _autoCandidateIds.Clear();
        if (_client is null || !_splitwiseReady || _swRules.Count == 0) return;
        try
        {
            BeginBusy("Regole Splitwise: invio automatico…");
            var recs = _db.GetAll().Where(e => ids.Contains(e.Id)
                            && e.Direction == ExpenseDirection.Uscita
                            && !ExpenseParser.IsOverlapDescription(e.Description)
                            && !IsNonSharedExpense(e)).ToList();
            var (sent, flagged) = await SplitwiseAuto.ProcessAsync(
                _db, _client, _cfg.GroupId, _me, _cfg.CurrencyCode, _swRules,
                _numAmtTol.Value, (int)_numNearby.Value, recs);
            _pendingNeedsReload = true;
            LoadPending();
            if (sent + flagged > 0)
                SetStatus($"Regole Splitwise: {sent} inviate in automatico, {flagged} da verificare (vedi Note).");
        }
        catch (Exception ex) { SetStatus("Regole Splitwise: " + ex.Message, true); }
        finally { EndBusy(); }
    }

    private void LoadOptionsFromConfig()
    {
        if (_cfg is null) return;
        _optDbCurrent.Text = _db?.DbPath ?? "(non aperto)";
        _optDbPath.Text = _cfg.DbPath;
        _optKey.Text = _cfg.ConsumerKey;
        _optSecret.Text = _cfg.ConsumerSecret;
        _optGroup.Text = _cfg.GroupId == 0 ? "" : _cfg.GroupId.ToString();
        _optCurrency.Text = _cfg.CurrencyCode;
        _optNearby.Value = Math.Clamp(_cfg.NearbyDays, (int)_optNearby.Minimum, (int)_optNearby.Maximum);
        _optAmtTol.Value = Math.Clamp(_cfg.AmountTolerance, _optAmtTol.Minimum, _optAmtTol.Maximum);
        _optInbox.Text = _cfg.InboxFolder;
    }

    private void SaveOptions()
    {
        _cfg ??= new AppConfig();
        _cfg.DbPath = _optDbPath.Text.Trim();
        _cfg.ConsumerKey = _optKey.Text.Trim();
        _cfg.ConsumerSecret = _optSecret.Text.Trim();
        _cfg.GroupId = long.TryParse(_optGroup.Text.Trim(), out var g) ? g : 0;
        _cfg.CurrencyCode = string.IsNullOrWhiteSpace(_optCurrency.Text) ? "EUR" : _optCurrency.Text.Trim();
        _cfg.NearbyDays = (int)_optNearby.Value;
        _cfg.AmountTolerance = _optAmtTol.Value;
        _cfg.InboxFolder = _optInbox.Text.Trim();
        try
        {
            var json = JsonSerializer.Serialize(_cfg, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "appsettings.json"), json);
            _numNearby.Value = Math.Clamp(_cfg.NearbyDays, (int)_numNearby.Minimum, (int)_numNearby.Maximum);
            _numAmtTol.Value = Math.Clamp(_cfg.AmountTolerance, _numAmtTol.Minimum, _numAmtTol.Maximum);
            MessageBox.Show("Impostazioni salvate. Riavvia l'applicazione per applicare DB e chiavi.",
                "Opzioni", MessageBoxButtons.OK, MessageBoxIcon.Information);
            SetStatus("Impostazioni salvate (riavvia per applicare).");
        }
        catch (Exception ex) { MessageBox.Show("Errore nel salvataggio: " + ex.Message, "Opzioni"); }
    }

    private void ResetDatabase()
    {
        if (!string.Equals(_optResetConfirm.Text.Trim(), "delete", StringComparison.OrdinalIgnoreCase))
        { MessageBox.Show("Per azzerare il database scrivi esattamente \"delete\" nel campo.", "Sicurezza"); return; }
        if (MessageBox.Show("Cancellare DEFINITIVAMENTE tutti i dati del database?\nNon è reversibile (non tocca Splitwise).",
                "Conferma azzeramento", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;

        _db.ClearAllData();
        _checked.Clear(); _split.Clear(); _dupInfo.Clear(); _sentNotFound.Clear();
        _excludedDescriptions.Clear(); _importBatchFilter = 0;
        _tagRules = _db.GetTagRules();
        _optResetConfirm.Clear();
        RefreshTagFilter();
        _pendingNeedsReload = true;
        LoadPending();
        MessageBox.Show("Database azzerato.", "Sicurezza", MessageBoxButtons.OK, MessageBoxIcon.Information);
        SetStatus("Database azzerato: tutte le tabelle svuotate.");
    }

    private void LoadLog()
    {
        var log = _db.GetImportLog();
        _gridLog.SuspendLayout();
        try
        {
            _gridLog.Rows.Clear();
            foreach (var (id, when, origin, imp, skip) in log)
            {
                int idx = _gridLog.Rows.Add(id, when, origin, imp, skip);
                _gridLog.Rows[idx].Tag = id;
            }
        }
        finally { _gridLog.ResumeLayout(); }

        int totImp = log.Sum(x => x.Imported), totSkip = log.Sum(x => x.Skipped);
        _lblLogInfo.Text = $"{log.Count} import — {totImp} record importati, {totSkip} scartati in totale.";
    }

    // Elimina il registro selezionato e TUTTI i movimenti importati con quel batch.
    private void DeleteSelectedImportBatch()
    {
        if (_gridLog.CurrentRow?.Tag is not long id)
        { SetStatus("Seleziona una riga del registro.", true); return; }
        var origin = Convert.ToString(_gridLog.CurrentRow.Cells["colOrigin"].Value) ?? "";
        if (MessageBox.Show(
                $"Eliminare il registro \"{origin}\" e TUTTI i movimenti importati con esso?\n" +
                "Operazione irreversibile (non tocca Splitwise).",
                "Conferma eliminazione", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        int n = _db.DeleteImportBatch(id);
        _pendingNeedsReload = true;   // 'Da inviare' va ricaricata (righe rimosse)
        LoadLog();
        SetStatus($"Registro eliminato: {n} movimenti rimossi.");
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
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, MinimumWidth = 260, FillWeight = 200 });
        g.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Importo €", Width = 90, Name = "colAmt",
            DefaultCellStyle = new DataGridViewCellStyle { Format = "0.00", Alignment = DataGridViewContentAlignment.MiddleRight } });
        g.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Fonte", Width = 80, Name = "colSrc" });
    }

    private async Task InitAsync()
    {
        try
        {
            // 1) Config: se appsettings.json manca o è invalido, si parte vuoti e si configura dalle Opzioni
            var cfgPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
            try { _cfg = File.Exists(cfgPath) ? JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(cfgPath)) ?? new AppConfig() : new AppConfig(); }
            catch { _cfg = new AppConfig(); }

            // 2) DB al percorso configurato (vuoto = history.db accanto all'exe)
            _db = new HistoryStore(_cfg.DbPath);
            _tagRules = _db.GetTagRules();
            _swRules = _db.GetSplitwiseRules();
            RefreshSwRules();
            MigrateManualTagsDelimiter();
            MigrateMultiManualTagsToRules();
            if (_db.GetMeta("card_source_migrated") != "1")
            {
                int n = _db.ReclassifyCardSourceByOrigin();
                _db.SetMeta("card_source_migrated", "1");
                if (n > 0) SetStatus($"{n} movimenti carta riclassificati come BPERCARD.");
            }
            RefreshTagFilter();
            _numNearby.Value = Math.Clamp(_cfg.NearbyDays, (int)_numNearby.Minimum, (int)_numNearby.Maximum);
            _numAmtTol.Value = Math.Clamp(_cfg.AmountTolerance, _numAmtTol.Minimum, _numAmtTol.Maximum);
            LoadOptionsFromConfig();
            UpdateEnvBanner();
            LoadPending();

            // 3) Splitwise: solo se configurato (chiavi + GroupId); altrimenti app utilizzabile, invio disabilitato
            if (_cfg.GroupId == 0 || string.IsNullOrWhiteSpace(_cfg.ConsumerKey) || string.IsNullOrWhiteSpace(_cfg.ConsumerSecret))
            {
                _btnSend.Enabled = false;
                SetStatus("Splitwise non configurato: compila la scheda 'Opzioni' e riavvia.", true);
                return;
            }

            _client = new SplitwiseClient(_cfg.ConsumerKey, _cfg.ConsumerSecret);
            SetStatus("Autenticazione…");
            await _client.AuthenticateAsync();
            _me = await _client.GetCurrentUserAsync();
            _members = await _client.GetGroupMembersAsync(_cfg.GroupId);
            _splitwiseReady = true;
            _grpSplitwise.Enabled = true;   // abilita la sezione Splitwise solo ora
            SetStatus($"Pronto. Gruppo con {_members.Count} membri.");

            // auto-import dei file lasciati nella cartella "inbox"
            ImportFolder(_cfg.InboxFolder);
            await RunSplitwiseAutoFlush();   // regole Splitwise sui nuovi importati
            // confronto automatico sui dati attualmente mostrati
            await CheckPendingDuplicatesAsync();
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
    // Chiave di duplicato: data(giorno) + importo + descrizione normalizzata (no maiuscole/accenti/speciali).
    // Descrizione usata su Splitwise: la Nota (più parlante) se compilata, altrimenti la descrizione del movimento.
    private static string SplitwiseDesc(ExpenseRecord e) =>
        string.IsNullOrWhiteSpace(e.Note) ? (e.Description ?? "") : e.Note.Trim();

    internal static string NormDesc(string? d)
    {
        var s = (d ?? "").ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder();
        foreach (var ch in s)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue; // accenti
            if (char.IsLetterOrDigit(ch)) sb.Append(ch);   // tieni solo lettere/cifre
        }
        return sb.ToString();
    }

    private static string DupKey(DateTime? date, decimal amount, string? desc) =>
        $"{date?.Date.Ticks ?? 0}|{amount.ToString("0.00", CultureInfo.InvariantCulture)}|{NormDesc(desc)}";

    private (int added, int skipped) ImportRows(List<ExpenseRow> rows, bool checkDup = true, bool notify = true, string? origin = null)
    {
        // batch del registro: i record importati vi vengono collegati (per eliminarli in blocco)
        long batch = origin != null ? _db.BeginImportLog(origin) : 0;

        // chiavi già presenti nel DB (qualsiasi stato): scartiamo i duplicati esatti
        var keys = _db.GetAll().Select(e => DupKey(e.Date, e.Amount, e.Description)).ToHashSet();
        int added = 0, skipped = 0;
        foreach (var r in rows)
        {
            if (r.Amount <= 0 && string.IsNullOrWhiteSpace(r.Description)) continue;
            var key = DupKey(r.Date, r.Amount, Desc(r));
            if (!keys.Add(key)) { skipped++; continue; }   // già presente (o doppione nello stesso import)
            var id = _db.AddPending(r.Date, Desc(r), r.Amount, r.Source, r.Direction, batch);
            _split[id] = (r.Mode, r.CustomShares);
            _autoCandidateIds.Add(id);   // candidato per le Regole Splitwise (auto-invio)
            // nessuna pre-selezione automatica: l'utente sceglie cosa inviare spuntando le righe
            added++;
        }
        LoadPending();
        if (notify)
        {
            var msg = $"{added} importati" + (skipped > 0 ? $", {skipped} già presenti (scartati)" : "") + ".";
            SetStatus(msg, skipped > 0);
            if (skipped > 0)
                MessageBox.Show(
                    $"{skipped} movimenti erano già presenti (stessa data, importo e descrizione) e sono stati scartati.\n" +
                    $"{added} nuovi importati.",
                    "Duplicati scartati", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        if (origin != null) _db.UpdateImportLog(batch, added, skipped);   // aggiorna i contatori del registro
        if (checkDup && added > 0) _ = CheckPendingDuplicatesAsync();   // verifica su Splitwise ed evidenzia in rosso
        return (added, skipped);
    }

    private static readonly string[] DataExts = { ".xls", ".xlsx", ".csv", ".txt" };
    private static readonly string[] ImageExts = { ".png", ".jpg", ".jpeg", ".bmp", ".tif", ".tiff" };

    /// <summary>
    /// Importa tutti i file di una cartella: .xls=BPER, .xlsx=Satispay, .csv/.txt=testo,
    /// immagini=OCR (STAMP). Sposta i file processati in "processati". Ritorna i movimenti importati.
    /// </summary>
    private int ImportFolder(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return 0;
        var processed = Path.Combine(folder, "processati");
        var tessData = Path.Combine(AppContext.BaseDirectory, "tessdata");
        bool ocrOk = File.Exists(Path.Combine(tessData, "ita.traineddata"));

        int total = 0, files = 0, skippedImg = 0, added = 0, dup = 0;
        foreach (var file in Directory.EnumerateFiles(folder)
                     .Where(f => { var x = Path.GetExtension(f).ToLowerInvariant(); return DataExts.Contains(x) || ImageExts.Contains(x); })
                     .OrderBy(f => f))
        {
            try
            {
                var ext = Path.GetExtension(file).ToLowerInvariant();
                List<ExpenseRow> rows;
                if (ImageExts.Contains(ext))
                {
                    if (!ocrOk) { skippedImg++; continue; }   // niente tessdata: lascio l'immagine
                    rows = StampProcessor.ProcessSingleFile(file, tessData).Select(r => r.Row).ToList();
                }
                else
                {
                    rows = ext switch
                    {
                        ".xls" => BperXlsImporter.Import(file),
                        ".xlsx" => SatispayImporter.Import(file),
                        ".csv" when CaiImporter.IsCai(file) => CaiImporter.Import(file),   // export CAI
                        _ => ExpenseParser.ParseText(File.ReadAllText(file))
                    };
                }
                var (a, s) = ImportRows(rows, checkDup: false, notify: false, origin: Path.GetFileName(file));   // log per file
                total += rows.Count; added += a; dup += s; files++;

                Directory.CreateDirectory(processed);
                var dest = Path.Combine(processed, Path.GetFileName(file));
                if (File.Exists(dest))
                    dest = Path.Combine(processed,
                        $"{Path.GetFileNameWithoutExtension(file)}_{DateTime.Now:yyyyMMdd_HHmmss}{ext}");
                File.Move(file, dest);
            }
            catch (Exception ex) { SetStatus($"Errore su {Path.GetFileName(file)}: {ex.Message}", true); }
        }
        if (files > 0 || skippedImg > 0)
        {
            SetStatus($"Cartella: {files} file, {added} importati, {dup} già presenti (scartati)." +
                      (skippedImg > 0 ? $" {skippedImg} immagini saltate (manca tessdata)." : ""), dup > 0);
            if (dup > 0 || skippedImg > 0)
                MessageBox.Show(
                    $"{files} file processati.\n{added} movimenti importati.\n{dup} già presenti (scartati)." +
                    (skippedImg > 0 ? $"\n{skippedImg} immagini saltate (manca tessdata/ita.traineddata)." : ""),
                    "Import cartella", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        return added;
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
            // Confronta SOLO i movimenti VISIBILI (passano i filtri correnti) e inviabili a Splitwise:
            // uscite, non sovrapposizioni, non già inviate, non "personali" (investimenti). Così la finestra
            // di date della chiamata è stretta (niente chiamate inutili molto indietro nel tempo).
            var dated = _pendingView.Where(e =>
                    e.Date.HasValue
                    && PassesPendingFilter(e)
                    && e.Status != ExpenseStatus.Inviata
                    && e.Direction == ExpenseDirection.Uscita
                    && !ExpenseParser.IsOverlapDescription(e.Description)
                    && !IsNonSharedExpense(e))
                .ToList();
            if (dated.Count == 0) { LoadPending(); return; }   // cache additiva: non azzero le evidenziazioni esistenti

            BeginBusy("Controllo su Splitwise dei possibili duplicati…");
            int nearby = (int)_numNearby.Value;
            decimal amtTol = Math.Max(_numAmtTol.Value, 0.005m);   // tolleranza importo in € (min mezza centesima)
            var since = dated.Min(e => e.Date!.Value).AddDays(-nearby - 1);
            var existing = await _client.GetExpensesSinceAsync(_cfg.GroupId, since);

            // verso: le MIE uscite devono combaciare solo con spese pagate da ME su Splitwise (non dall'altro)
            var mine = existing.Where(x => x.PayerId == _me).ToList();

            // additivo: ricalcolo solo le righe in esame (le altre mantengono l'esito precedente in cache)
            foreach (var e in dated) _dupInfo.Remove(e.Id);
            foreach (var e in dated)
            {
                var day = e.Date!.Value.Date;

                // 1) VERDE: stessa data + stesso importo (esatto)
                var green = mine.Where(x => x.Date == day && Math.Abs(x.Cost - e.Amount) < 0.005m).ToList();
                if (green.Count > 0) { _dupInfo[e.Id] = (Format(green), DupGreen); continue; }

                // 2) AZZURRO: stesso importo (esatto) + descrizione simile
                var exactAmt = mine.Where(x => Math.Abs(x.Cost - e.Amount) < 0.005m).ToList();
                var descMatch = exactAmt.Where(x => DescriptionsOverlap(SplitwiseDesc(e), x.Description)).ToList();
                if (descMatch.Count > 0) { _dupInfo[e.Id] = (Format(descMatch), DupBlue); continue; }

                // 3) GIALLO: importo entro tolleranza (± €) + stessa data o data vicina (± gg). Qui le imprecisioni.
                var near = mine.Where(x => Math.Abs(x.Cost - e.Amount) <= amtTol
                                           && Math.Abs((x.Date - day).TotalDays) <= nearby).ToList();
                if (near.Count > 0) _dupInfo[e.Id] = (Format(near), DupYellow);
            }

            LoadPending();   // ri-render con evidenziazione
            SetStatus(_dupInfo.Count > 0
                ? $"Attenzione: {_dupInfo.Count} righe forse già su Splitwise (solo tue spese). Verde/azzurro = importo esatto; giallo = ±{amtTol:0.00}€ entro ±{nearby} gg."
                : $"Nessun possibile duplicato tra le tue spese su Splitwise (giallo: ±{amtTol:0.00}€, ±{nearby} gg).");
        }
        catch (Exception ex) { SetStatus("Controllo duplicati Splitwise non riuscito: " + ex.Message, true); }
        finally { EndBusy(); }
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

    // Movimenti "personali" che NON vanno mai su Splitwise (es. giroconti a salvadanaio/investimenti/risparmi):
    // si riconoscono dai tag (Investimenti/Risparmi) o dalla descrizione (salvadanaio).
    private bool IsNonSharedExpense(ExpenseRecord e)
    {
        bool tagHit = EffectiveTags(e).Any(t =>
            t.Contains("investiment", StringComparison.OrdinalIgnoreCase) ||
            t.Contains("risparmi", StringComparison.OrdinalIgnoreCase));
        var d = e.Description ?? "";
        bool descHit = d.Contains("salvadanaio", StringComparison.OrdinalIgnoreCase)
                    || d.Contains("investit", StringComparison.OrdinalIgnoreCase);
        return tagHit || descHit;
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
                ".csv" when CaiImporter.IsCai(ofd.FileName) => CaiImporter.Import(ofd.FileName),   // export CAI
                _ => ExpenseParser.ParseText(File.ReadAllText(ofd.FileName))
            };
            var name = Path.GetFileName(ofd.FileName);
            var (added, skipped) = ImportRows(rows, notify: false, origin: name);   // gestisco io messaggio e registro
            LoadLog();   // ricarica le righe del registro (scheda Importazioni) dietro
            MessageBox.Show(
                $"Import completato: {name}\n\n" +
                $"{added} movimenti importati" + (skipped > 0 ? $", {skipped} già presenti (scartati)" : "") + ".",
                "Import file", MessageBoxButtons.OK, MessageBoxIcon.Information);
            SetStatus($"{name}: {added} importati" + (skipped > 0 ? $", {skipped} scartati" : "") + ".");
            _ = RunSplitwiseAutoFlush();   // regole Splitwise sui nuovi importati
        }
        catch (Exception ex) { SetStatus("Errore import: " + ex.Message, true); }
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
            ImportRows(results.Select(r => r.Row).ToList(), origin: "Immagine appunti");
            _ = RunSplitwiseAutoFlush();
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

    // Applica i filtri ricaricando la griglia; azzera le selezioni (non devono restare spunte tra ricerche diverse).
    // Se Splitwise è configurato, lancia in automatico il confronto sui (soli) dati mostrati.
    private void ApplyPendingFilters()
    {
        _checked.Clear();
        LoadPending();
        if (_splitwiseReady) _ = CheckPendingDuplicatesAsync();
    }

    // ---------- RENDER GRIGLIE ----------
    private void LoadPending()
    {
        // Movimenti = non inviati (Pending) + già inviati (Inviata); gli archiviati restano in Archivio
        _pendingView = _db.GetByStatus(ExpenseStatus.Pending, ExpenseStatus.Inviata);
        decimal totU = 0, totE = 0, invOut = 0, invIn = 0; int shown = 0, invCount = 0;
        _gridPending.SuspendLayout();
        try
        {
            _gridPending.Rows.Clear();
            foreach (var e in _pendingView)
            {
                if (!PassesPendingFilter(e)) continue;   // tipo + sovrapposizioni + parole + date + esclusioni
                bool sent = e.Status == ExpenseStatus.Inviata;
                var mode = _split.TryGetValue(e.Id, out var s) ? s.Mode : SplitMode.Equal;
                int idx = _gridPending.Rows.Add(
                    !sent && _checked.Contains(e.Id),
                    e.Date, e.Description, e.Amount, e.Source.ToString(),
                    mode);
                var grow = _gridPending.Rows[idx];
                grow.Tag = e.Id;
                grow.Cells["colKind"].Value = e.Direction == ExpenseDirection.Entrata ? "Entrata" : "Uscita";
                grow.Cells["colTags"].Value = string.Join(", ", EffectiveTags(e));
                grow.Cells["colNote"].Value = e.Note;
                // descrizione/data/importo modificabili SOLO per le righe inserite a mano;
                // per i movimenti importati restano bloccati (servono al match duplicati). Le Note sono sempre editabili.
                bool manual = e.Source == ExpenseSource.MANUALE;
                grow.Cells["colDesc"].ReadOnly = !manual;
                grow.Cells["colDate"].ReadOnly = !manual;
                grow.Cells["colAmt"].ReadOnly = !manual;
                // escluse dai totali (es. padre di una suddivisione): visibili ma non conteggiate
                if (e.ExcludeTotals)
                {
                    // non conteggiare
                }
                // i movimenti di investimento/salvadanaio sono trasferimenti interni: non sono spese/entrate reali,
                // li conto a parte (la riga resta visibile con la sua direzione, così non perdo il -500 verso investimento)
                else if (IsNonSharedExpense(e))
                {
                    if (e.Direction == ExpenseDirection.Entrata) invIn += e.Amount; else invOut += e.Amount;
                    invCount++;
                }
                else if (e.Direction == ExpenseDirection.Entrata) totE += e.Amount; else totU += e.Amount;
                shown++;

                if (sent)
                {
                    // già inviata: in grigio, spunta bloccata, non re-inviabile
                    grow.DefaultCellStyle.BackColor = Color.Gainsboro;
                    grow.Cells["colDup"].Value = "Inviata";
                    grow.Cells["colSend"].ReadOnly = true;
                }
                else if (_dupInfo.TryGetValue(e.Id, out var dup))
                {
                    grow.Cells["colDup"].Value = dup.Text;
                    grow.DefaultCellStyle.BackColor = dup.Color;
                }
                if (e.ExcludeTotals)
                {
                    // marca visivamente: testo grigio corsivo + nota in colonna duplicati
                    _italicFont ??= new Font(_gridPending.Font, FontStyle.Italic);
                    grow.DefaultCellStyle.ForeColor = Color.Gray;
                    grow.DefaultCellStyle.Font = _italicFont;
                    if (string.IsNullOrEmpty(Convert.ToString(grow.Cells["colDup"].Value)))
                        grow.Cells["colDup"].Value = "(escluso dai totali)";
                }
            }
        }
        finally { _gridPending.ResumeLayout(); }

        var excl = _excludedDescriptions.Count > 0 ? $" — {_excludedDescriptions.Count} descr. escluse" : "";
        var invSeg = invCount > 0 ? $"  |  Investimenti: −{invOut:0.00} / +{invIn:0.00} €" : "";
        var batchNote = _importBatchFilter != 0 ? $"  |  Registro #{_importBatchFilter}" : "";
        _pendingTotalsBase = $"Uscite: {totU:0.00} €  |  Entrate: {totE:0.00} €{invSeg}  ({shown} voci){excl}{batchNote}";
        RefreshPendingTotalLabel();
        _pendingNeedsReload = false;   // griglia allineata ai dati correnti
    }

    // Tag effettivi di un movimento: quelli dalle regole (parola→tag) + i tag manuali della singola riga.
    // Converte una-tantum i tag manuali salvati col vecchio separatore "," → newline.
    // I nomi tag possono contenere virgole (es. "Bimbi (Asilo, pannolini..)"): ricostruisce usando i tag definiti.
    private void MigrateManualTagsDelimiter()
    {
        var byLen = _db.GetTags().OrderByDescending(t => t.Length).ToList();
        if (byLen.Count == 0) return;
        foreach (var e in _db.GetAll())
        {
            var raw = (e.ManualTags ?? "").Trim();
            if (raw.Length == 0 || raw.Contains('\n') || !raw.Contains(',')) continue; // già nuovo formato o singolo
            var parts = new List<string>();
            var s = raw;
            while (s.Length > 0)
            {
                var match = byLen.FirstOrDefault(t =>
                    s.StartsWith(t, StringComparison.OrdinalIgnoreCase) &&
                    (s.Length == t.Length || s[t.Length] == ','));
                if (match != null)
                {
                    parts.Add(match);
                    s = s.Substring(match.Length).TrimStart();
                }
                else
                {
                    int c = s.IndexOf(',');
                    parts.Add((c < 0 ? s : s[..c]).Trim());
                    s = c < 0 ? "" : s[(c + 1)..];
                }
                s = s.TrimStart();
                if (s.StartsWith(",")) s = s.Substring(1).TrimStart();
            }
            _db.SetManualTags(e.Id, string.Join("\n", parts.Where(p => p.Length > 0)));
        }
    }

    // Migrazione UNA TANTUM (flag in meta): le righe che al momento hanno PIÙ di un tag manuale
    // vengono ripulite (nessun tag manuale), così restano solo i tag derivati dalle regole in essere.
    // Guardata dal flag perché in futuro l'accodamento può creare legittimamente più tag manuali.
    private void MigrateMultiManualTagsToRules()
    {
        const string flag = "multitag_cleanup_done";
        if (_db.GetMeta(flag) == "1") return;

        int cleared = 0;
        foreach (var e in _db.GetAll())
        {
            var n = (e.ManualTags ?? "")
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length;
            if (n > 1) { _db.SetManualTags(e.Id, ""); cleared++; }
        }
        _db.SetMeta(flag, "1");
        if (cleared > 0) SetStatus($"Pulizia tag: {cleared} righe con più tag manuali azzerate (ora valgono le regole).");
    }

    private List<string> EffectiveTags(ExpenseRecord e)
    {
        var tags = TagEngine.TagsFor(e.Description, _tagRules)
            .Where(t => t != TagEngine.Untagged).ToList();
        // i tag manuali sono separati da newline (un nome di tag può contenere virgole, es. "Bimbi (Asilo, pannolini..)")
        foreach (var m in (e.ManualTags ?? "").Split('\n',
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (!tags.Contains(m, StringComparer.OrdinalIgnoreCase)) tags.Add(m);
        if (tags.Count == 0) tags.Add(TagEngine.Untagged);
        return tags;
    }

    // Ricalcola i totali (Uscite/Entrate/Investimenti) sulle righe visibili senza ricostruire la griglia.
    private void RefreshPendingTotalsOnly()
    {
        decimal totU = 0, totE = 0, invOut = 0, invIn = 0; int shown = 0, invCount = 0;
        foreach (var e in _pendingView)
        {
            if (!PassesPendingFilter(e)) continue;
            if (IsNonSharedExpense(e))
            {
                if (e.Direction == ExpenseDirection.Entrata) invIn += e.Amount; else invOut += e.Amount;
                invCount++;
            }
            else if (e.Direction == ExpenseDirection.Entrata) totE += e.Amount; else totU += e.Amount;
            shown++;
        }
        var excl = _excludedDescriptions.Count > 0 ? $" — {_excludedDescriptions.Count} descr. escluse" : "";
        var invSeg = invCount > 0 ? $"  |  Investimenti: −{invOut:0.00} / +{invIn:0.00} €" : "";
        var batchNote = _importBatchFilter != 0 ? $"  |  Registro #{_importBatchFilter}" : "";
        _pendingTotalsBase = $"Uscite: {totU:0.00} €  |  Entrate: {totE:0.00} €{invSeg}  ({shown} voci){excl}{batchNote}";
        RefreshPendingTotalLabel();
    }

    // Aggiorna l'etichetta totali aggiungendo il numero di righe selezionate (spuntate "Invia").
    private void RefreshPendingTotalLabel() =>
        _lblPendingTotal.Text = $"{_pendingTotalsBase}  |  Selezionate: {_checked.Count}";

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
            BeginBusy("Verifica delle spese inviate su Splitwise…");
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
        finally { EndBusy(); }
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
        foreach (var id in ids) { _checked.Remove(id); _split.Remove(id); _dupInfo.Remove(id); }
        _sentNotFound.Clear();
        _pendingNeedsReload = true;   // spariscono anche da Movimenti
        LoadSent();
        SetStatus($"{ids.Count} spese inviate eliminate dallo storico.");
    }

    // Elimina UNA singola spesa inviata (cancellazione fisica locale; non tocca Splitwise).
    // Utile quando l'hai cancellata su Splitwise: così sparisce sia da 'Inviate' sia da 'Movimenti'.
    private void DeleteSentRow(long id)
    {
        if (MessageBox.Show(
                "Eliminare DEFINITIVAMENTE questa spesa dallo storico locale?\n" +
                "(non tocca Splitwise; sparirà anche da 'Movimenti')",
                "Elimina spesa inviata", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        _db.DeleteMany(new List<long> { id });
        _checked.Remove(id); _split.Remove(id); _dupInfo.Remove(id); _sentNotFound.Remove(id);
        _pendingNeedsReload = true;
        LoadSent();
        SetStatus("Spesa inviata eliminata.");
    }

    // Cerca duplicati LOCALI (transazioni identiche già in archivio) e propone di eliminarli, tenendone 1 per gruppo.
    // Match su stessa DATA + stesso IMPORTO (la descrizione può variare leggermente tra fonti, es. .xls vs testo web).
    private void CheckLocalDuplicates()
    {
        var all = _db.GetAll()
            .Where(e => e.Date.HasValue && e.Status != ExpenseStatus.EliminataLogicamente)
            .ToList();
        var ignored = all.Where(e => e.DupIgnore).ToList();
        var batchWhen = _db.GetImportLog().ToDictionary(x => x.Id, x => x.When);   // codice registro → data/ora

        using var dlg = new DuplicatesDialog(all, ignored, batchWhen);
        var res = dlg.ShowDialog(this);

        // applica subito ignora/riattiva (anche se chiudi senza eliminare)
        if (dlg.IdsToIgnore.Count > 0) { _db.SetDupIgnore(dlg.IdsToIgnore, true); _pendingNeedsReload = true; }
        if (dlg.IdsToReenable.Count > 0) { _db.SetDupIgnore(dlg.IdsToReenable, false); _pendingNeedsReload = true; }

        if (res != DialogResult.OK)
        {
            SetStatus(dlg.IdsToIgnore.Count > 0
                ? $"{dlg.IdsToIgnore.Count} righe marcate 'ignora duplicati'."
                : "Controllo duplicati chiuso.");
            if (_pendingNeedsReload) LoadPending();
            return;
        }

        var toDelete = dlg.IdsToDelete;
        _db.DeleteMany(toDelete);
        foreach (var id in toDelete) { _checked.Remove(id); _split.Remove(id); _dupInfo.Remove(id); }
        _pendingNeedsReload = true;
        LoadPending();
        MessageBox.Show($"{toDelete.Count} righe doppione eliminate." +
            (dlg.IdsToIgnore.Count > 0 ? $"\n{dlg.IdsToIgnore.Count} righe marcate 'ignora duplicati'." : ""),
            "Controlla duplicati", MessageBoxButtons.OK, MessageBoxIcon.Information);
        SetStatus($"{toDelete.Count} doppioni eliminati.");
    }

    // Riporta una spesa inviata in 'Movimenti' come non inviata (modificabile/re-inviabile). Non tocca Splitwise.
    private void RestoreSent(long id)
    {
        if (MessageBox.Show("Riportare questa spesa in 'Movimenti' come NON inviata? (non tocca Splitwise)",
                "Ripristina", MessageBoxButtons.YesNo) != DialogResult.Yes) return;
        _db.SetStatus(id, ExpenseStatus.Pending);
        _checked.Add(id);
        _sentNotFound.Remove(id);
        _pendingNeedsReload = true;
        LoadSent();
        SetStatus("Spesa ripristinata in 'Movimenti'.");
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
            RefreshPendingTotalLabel();
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
        if (colName == "colNote")
        {
            _db.SetNote(id, Convert.ToString(row.Cells["colNote"].Value) ?? "");
            // aggiorna la copia in memoria così non si perde ricalcolando i totali
            var rec = _pendingView.FirstOrDefault(x => x.Id == id);
            if (rec != null) rec.Note = Convert.ToString(row.Cells["colNote"].Value) ?? "";
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
            .Where(e => ids.Contains(e.Id) && e.Amount > 0
                        && e.Direction == ExpenseDirection.Uscita        // le entrate non si inviano
                        && !ExpenseParser.IsOverlapDescription(e.Description))  // né le sovrapposizioni
            .ToList();
        if (toSend.Count == 0) { SetStatus("Nessuna spesa inviabile selezionata (solo uscite, escluse entrate e sovrapposizioni).", true); return; }

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

        // DEDUP lato SPLITWISE: verifica FRESCA e MIRATA solo alle voci che sto inviando (per giorno)
        try
        {
            BeginBusy("Verifica duplicati su Splitwise (solo le voci da inviare)…");
            var remoteDups = new List<ExpenseRecord>();
            foreach (var grp in toSend.Where(e => e.Date.HasValue).GroupBy(e => e.Date!.Value.Date))
            {
                var existing = await _client.GetExpensesOnDayAsync(_cfg.GroupId, grp.Key);
                foreach (var e in grp)
                    if (existing.Any(x => Math.Abs(x.Cost - e.Amount) < 0.005m))
                        remoteDups.Add(e);
            }
            EndBusy();
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
        catch (Exception ex) { EndBusy(); SetStatus("Verifica duplicati Splitwise non riuscita: " + ex.Message, true); }
        if (toSend.Count == 0) { SetStatus("Niente da inviare dopo aver saltato i duplicati."); return; }
        if (MessageBox.Show($"Inviare {toSend.Count} spese a Splitwise?", "Conferma",
                MessageBoxButtons.YesNo) != DialogResult.Yes) return;

        _btnSend.Enabled = false;
        BeginBusy("Invio in corso…");
        int ok = 0, fail = 0; var errs = new List<string>();
        var sentIds = new List<long>();
        try
        {
            foreach (var e in toSend)
            {
                try
                {
                    var mode = _split.TryGetValue(e.Id, out var s) ? s.Mode : SplitMode.Equal;
                    var swDesc = SplitwiseDesc(e);   // usa la Nota se compilata (più parlante), altrimenti la descrizione
                    long expId = mode == SplitMode.Equal
                        ? await _client.CreateEqualExpenseAsync(_cfg.GroupId, e.Amount, swDesc, _cfg.CurrencyCode, e.Date)
                        : await _client.CreateSharedExpenseAsync(_cfg.GroupId, e.Amount, swDesc, _cfg.CurrencyCode,
                            BuildShares(e.Amount, mode), e.Date);
                    _db.MarkSent(e.Id, expId);
                    _checked.Remove(e.Id);
                    _split.Remove(e.Id);
                    sentIds.Add(e.Id);
                    ok++;
                }
                catch (Exception ex) { fail++; errs.Add($"• {e.Description}: {ex.Message}"); }
                SetStatus($"Inviate {ok}, errori {fail}…");
            }
        }
        finally { _btnSend.Enabled = true; EndBusy(); }

        // le inviate restano in 'Movimenti' (in grigio): ricarico per aggiornarne lo stato
        foreach (var id in sentIds) _dupInfo.Remove(id);
        LoadPending();
        SetStatus($"Fatto. OK: {ok}, Errori: {fail}. (Restano in Movimenti, in grigio.)", fail > 0);
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

    // Spunta/deseleziona le righe della tab "Movimenti".
    // Deseleziona = azzera DAVVERO tutto (anche le righe non visibili per via dei filtri); seleziona = solo le visibili (no inviate).
    private void SetAllChecked(bool selected)
    {
        if (!selected) _checked.Clear();
        foreach (DataGridViewRow row in _gridPending.Rows)
        {
            if (row.Tag is not long id) continue;
            if (row.Cells["colSend"].ReadOnly) continue;   // righe già inviate: non selezionabili
            row.Cells["colSend"].Value = selected;
            if (selected) _checked.Add(id);
        }
        _gridPending.Invalidate();
        RefreshPendingTotalLabel();
        SetStatus(selected ? "Righe visibili selezionate." : "Tutte deselezionate.");
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
        _pendingNeedsReload = true;   // 'Da inviare' cambierà: ricarica al prossimo ingresso
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

    // Tolleranza in euro sul confronto degli importi con Splitwise (default 50 centesimi).
    public decimal AmountTolerance { get; set; } = 0.50m;

    // Cartella "in arrivo": all'avvio importa automaticamente i file qui dentro (.xls/.xlsx/.csv/.txt)
    // e li sposta in "processati". Vuoto = disattivato.
    public string InboxFolder { get; set; } = "";

    // Percorso del file database SQLite. Vuoto = "history.db" accanto all'eseguibile.
    public string DbPath { get; set; } = "";
}
