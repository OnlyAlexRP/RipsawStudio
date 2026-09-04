namespace RipsawStudio.UI;

/// <summary>
/// The dark backdrop behind the menu. Plain black, faded via <see cref="Form.Opacity"/> in
/// lockstep with <see cref="BarForm"/> and the settings window; a click anywhere on it closes
/// the whole menu, mirroring the old "click the video to dismiss" gesture.
/// </summary>
internal sealed class ScrimForm : Form
{
    /// <summary>Opacity of the backdrop once fully faded in (0..1), matching the ~80% black
    /// used in the supplied artwork.</summary>
    public const double MaxOpacity = 0.8;

    public event EventHandler? Clicked;

    public ScrimForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.Black;
        Cursor = Cursors.Default;
        KeyPreview = true;
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Clicked?.Invoke(this, EventArgs.Empty);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (Owner is MainForm main && main.DispatchShortcut(keyData)) return true;
        return base.ProcessCmdKey(ref msg, keyData);
    }
}
