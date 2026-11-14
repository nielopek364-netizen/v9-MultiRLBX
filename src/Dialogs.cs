using System;
using System.Drawing;
using System.Windows.Forms;

namespace MultiRLBX
{
    /// <summary>
    /// Minimal WinForms dialog helpers used strictly for startup password vault unlock.
    /// Account management, bulk import, and editing are handled in the web desktop UI.
    /// </summary>
    public static class Dialogs
    {
        /// <summary>Creates a DPI-aware fixed modal form matching the active theme.</summary>
        public static Form Make(string title, int w, int h)
        {
            Form f = new Form();
            f.Text = title;
            f.AutoScaleDimensions = new SizeF(96F, 96F);
            f.AutoScaleMode = AutoScaleMode.Dpi;
            f.StartPosition = FormStartPosition.CenterParent;
            f.FormBorderStyle = FormBorderStyle.FixedDialog;
            f.MaximizeBox = false;
            f.MinimizeBox = false;
            f.ShowInTaskbar = false;
            f.Font = Theme.Base;
            f.ClientSize = new Size(w, h);
            try { f.Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch (Exception) { }
            return f;
        }

        /// <summary>Places a control at explicit client coordinates inside a form.</summary>
        public static T Put<T>(Form f, T c, int x, int y) where T : Control
        {
            c.Location = new Point(x, y);
            f.Controls.Add(c);
            return c;
        }

        static void OkCancel(Form f, string okText)
        {
            Button ok = Ui.Btn(okText, 110, null, "primary");
            ok.DialogResult = DialogResult.OK;
            Button cancel = Ui.Btn("Cancel", 90, null, "");
            cancel.DialogResult = DialogResult.Cancel;
            Put(f, ok, f.ClientSize.Width - 110 - 90 - 30, f.ClientSize.Height - 46);
            Put(f, cancel, f.ClientSize.Width - 90 - 16, f.ClientSize.Height - 46);
            f.AcceptButton = ok;
            f.CancelButton = cancel;
        }

        /// <summary>Prompts the user for a vault password (optionally with confirmation).</summary>
        public static string AskPassword(IWin32Window owner, string title, string prompt, bool confirm)
        {
            Form f = Make(title, 420, confirm ? 220 : 170);
            if (owner == null) { f.ShowInTaskbar = true; f.StartPosition = FormStartPosition.CenterScreen; }
            Put(f, Ui.Lbl(prompt, "muted"), 16, 14);
            RoundTextBox t1 = Put(f, Ui.Txt(386, ""), 16, 44);
            t1.UseSystemPasswordChar = true;
            RoundTextBox t2 = null;
            if (confirm)
            {
                Put(f, Ui.Lbl("Type it again:", "muted"), 16, 80);
                t2 = Put(f, Ui.Txt(386, ""), 16, 102);
                t2.UseSystemPasswordChar = true;
            }
            OkCancel(f, "OK");
            Theme.Apply(f);
            while (true)
            {
                if (f.ShowDialog(owner) != DialogResult.OK) { f.Dispose(); return null; }
                if (t1.Text.Length == 0) { MessageBox.Show(f, "Password cannot be empty."); continue; }
                if (confirm && t1.Text != t2.Text) { MessageBox.Show(f, "Passwords do not match."); continue; }
                string pw = t1.Text;
                f.Dispose();
                return pw;
            }
        }
    }
}
