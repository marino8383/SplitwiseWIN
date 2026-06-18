using System.Drawing;

namespace SplitwiseUploader;

/// <summary>
/// Dialog per creare velocemente una regola tag da un movimento: la descrizione è selezionabile;
/// selezionando del testo, la "parola chiave" si compila da sola. Si sceglie un tag tra quelli codificati.
/// </summary>
public class QuickRuleDialog : Form
{
    private readonly TextBox _txtDesc;
    private readonly TextBox _txtKw;
    private readonly ComboBox _cmbTag;

    public string Keyword => _txtKw.Text.Trim();
    public string Tag => _cmbTag.SelectedItem?.ToString() ?? "";

    public QuickRuleDialog(string description, List<string> tags)
    {
        Text = "Crea regola tag";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false; MaximizeBox = false;
        ClientSize = new Size(560, 320);

        var lblD = new Label { Text = "Descrizione — seleziona la parola da usare (si copia sotto):", AutoSize = true, Left = 12, Top = 12 };
        _txtDesc = new TextBox { Left = 12, Top = 34, Width = 536, Height = 84, Multiline = true, ReadOnly = true,
            Text = description, ScrollBars = ScrollBars.Vertical };

        _txtKw = new TextBox { Left = 130, Top = 165, Width = 418 };
        // selezionando testo nella descrizione, riempi la parola chiave
        void SyncSel(object? s, EventArgs e)
        { if (!string.IsNullOrWhiteSpace(_txtDesc.SelectedText)) _txtKw.Text = _txtDesc.SelectedText.Trim(); }
        _txtDesc.MouseUp += SyncSel;
        _txtDesc.KeyUp += SyncSel;

        var btnUse = new Button { Text = "Usa testo selezionato", Left = 12, Top = 126, Width = 200, Height = 28 };
        btnUse.Click += SyncSel;

        var lblK = new Label { Text = "Parola chiave:", AutoSize = true, Left = 12, Top = 168 };
        var lblT = new Label { Text = "Tag:", AutoSize = true, Left = 12, Top = 206 };
        _cmbTag = new ComboBox { Left = 130, Top = 203, Width = 230, DropDownStyle = ComboBoxStyle.DropDownList };
        _cmbTag.Items.AddRange(tags.Cast<object>().ToArray());
        if (_cmbTag.Items.Count > 0) _cmbTag.SelectedIndex = 0;

        var ok = new Button { Text = "Aggiungi regola", Left = 300, Top = 270, Width = 150, Height = 32, DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "Annulla", Left = 460, Top = 270, Width = 88, Height = 32, DialogResult = DialogResult.Cancel };
        ok.Click += (_, _) =>
        {
            if (Keyword.Length == 0 || Tag.Length == 0)
            {
                MessageBox.Show("Seleziona/scrivi una parola e scegli un tag.", "Dati mancanti");
                DialogResult = DialogResult.None;   // non chiudere
            }
        };
        AcceptButton = ok; CancelButton = cancel;
        Controls.AddRange(new Control[] { lblD, _txtDesc, btnUse, lblK, _txtKw, lblT, _cmbTag, ok, cancel });
    }
}
