namespace SplitwiseUploader;

/// <summary>
/// Dialog per assegnare tag MANUALI a una singola riga (multi-selezione tra i tag codificati),
/// senza creare regole. I tag già presenti sulla riga sono pre-spuntati.
/// </summary>
public class ManualTagDialog : Form
{
    private readonly CheckedListBox _clb;

    public IEnumerable<string> SelectedTags => _clb.CheckedItems.Cast<string>();

    public ManualTagDialog(List<string> allTags, IEnumerable<string> current)
    {
        Text = "Tag manuali della riga";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false; MaximizeBox = false;
        ClientSize = new System.Drawing.Size(300, 320);

        var lbl = new Label { Text = "Spunta i tag da assegnare a questa riga:", AutoSize = true, Left = 12, Top = 10 };
        _clb = new CheckedListBox { Left = 12, Top = 32, Width = 276, Height = 230, CheckOnClick = true };
        var cur = new HashSet<string>(current, StringComparer.OrdinalIgnoreCase);
        foreach (var t in allTags) _clb.Items.Add(t, cur.Contains(t));

        var ok = new Button { Text = "OK", Left = 120, Top = 274, Width = 80, Height = 30, DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "Annulla", Left = 208, Top = 274, Width = 80, Height = 30, DialogResult = DialogResult.Cancel };
        AcceptButton = ok; CancelButton = cancel;
        Controls.AddRange(new Control[] { lbl, _clb, ok, cancel });
    }
}
