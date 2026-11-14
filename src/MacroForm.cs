using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace MultiRLBX
{
    /// <summary>
    /// Classic floating Windows Macro Recorder and Player supporting custom <c>.v9m</c> files,
    /// global Windows hotkeys (<c>F9</c> Record/Stop, <c>F10</c> Play/Stop), variable speed,
    /// repeat / continuous loop modes, and automatic idle trimming.
    /// </summary>
    public class MacroForm : Form
    {
        static MacroForm activeInstance;

        readonly List<MacroEvent> events = new List<MacroEvent>();
        readonly MacroPlayer player = new MacroPlayer();
        readonly System.Windows.Forms.Timer uiTimer = new System.Windows.Forms.Timer();
        readonly Stopwatch recWatch = new Stopwatch();

        readonly Label lblStatus;
        readonly Label lblStats;
        readonly RoundButton btnRec;
        readonly RoundButton btnPlay;
        readonly RoundButton btnSave;
        readonly RoundButton btnLoad;
        readonly RoundButton btnClear;
        readonly CheckBox chkMoves;
        readonly CheckBox chkTrim;
        readonly CheckBox chkLoop;
        readonly CheckBox chkTopMost;
        readonly NumericUpDown numSpeed;
        readonly NumericUpDown numRepeats;
        readonly NumericUpDown numPause;

        const int HOTKEY_REC = 9001;
        const int HOTKEY_PLAY = 9002;
        const int WM_HOTKEY = 0x0312;

        [DllImport("user32.dll", SetLastError = true)]
        static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        public static void ShowOrFocus()
        {
            if (activeInstance != null && !activeInstance.IsDisposed)
            {
                if (activeInstance.WindowState == FormWindowState.Minimized)
                    activeInstance.WindowState = FormWindowState.Normal;
                activeInstance.Activate();
                return;
            }
            activeInstance = new MacroForm();
            activeInstance.Show();
        }

        public MacroForm()
        {
            Text = "v9 MultiRLBX - Macro (.v9m) [F9 / F10]";
            Width = 460;
            Height = 355;
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            TopMost = true;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch (Exception) { }

            int y = 14;
            Label title = Ui.Lbl("Macro Recorder & Player (.v9m)", "title");
            title.Font = Theme.CardName;
            title.Location = new Point(16, y);
            Controls.Add(title);
            y += 24;

            lblStatus = Ui.Lbl("Gotowy. Naciśnij F9 aby nagrać lub F10 aby odtworzyć.", "muted");
            lblStatus.AutoSize = false;
            lblStatus.Width = 415;
            lblStatus.Height = 20;
            lblStatus.Location = new Point(16, y);
            Controls.Add(lblStatus);
            y += 22;

            lblStats = Ui.Lbl("Kroki: 0  |  Czas: 0.0s", "section");
            lblStats.Font = Theme.Mono;
            lblStats.Location = new Point(16, y);
            Controls.Add(lblStats);
            y += 30;

            btnRec = Ui.Btn("Nagraj (F9)", 130, delegate { ToggleRecord(); }, "danger");
            btnRec.Location = new Point(16, y);
            Controls.Add(btnRec);

            btnPlay = Ui.Btn("Odtwórz (F10)", 136, delegate { TogglePlay(); }, "primary");
            btnPlay.Location = new Point(154, y);
            Controls.Add(btnPlay);

            btnClear = Ui.Btn("Wyczyść", 96, delegate { ClearEvents(); }, "");
            btnClear.Location = new Point(298, y);
            Controls.Add(btnClear);
            y += 40;

            btnSave = Ui.Btn("Zapisz .v9m", 130, delegate { SaveDialog(); }, "");
            btnSave.Location = new Point(16, y);
            Controls.Add(btnSave);

            btnLoad = Ui.Btn("Wczytaj .v9m", 136, delegate { LoadDialog(); }, "");
            btnLoad.Location = new Point(154, y);
            Controls.Add(btnLoad);
            y += 44;

            // Playback options row
            Label lSpd = Ui.Fld("Prędkość (x):", 90);
            lSpd.Location = new Point(16, y);
            Controls.Add(lSpd);

            numSpeed = new NumericUpDown();
            numSpeed.DecimalPlaces = 2;
            numSpeed.Increment = 0.25M;
            numSpeed.Minimum = 0.10M;
            numSpeed.Maximum = 50.00M;
            numSpeed.Value = (decimal)Math.Max(0.1, Math.Min(50.0, Store.Settings.Macro.Speed));
            numSpeed.Width = 72;
            numSpeed.Location = new Point(108, y);
            Controls.Add(numSpeed);

            Label lRep = Ui.Fld("Powtórzenia:", 86);
            lRep.Location = new Point(196, y);
            Controls.Add(lRep);

            numRepeats = Ui.Num(1, 9999, Math.Max(1, Store.Settings.Macro.Repeats), 72);
            numRepeats.Location = new Point(284, y);
            Controls.Add(numRepeats);
            y += 32;

            Label lPau = Ui.Fld("Pauza (ms):", 90);
            lPau.Location = new Point(16, y);
            Controls.Add(lPau);

            numPause = Ui.Num(0, 600000, Math.Max(0, Store.Settings.Macro.PauseMs), 72);
            numPause.Increment = 100;
            numPause.Location = new Point(108, y);
            Controls.Add(numPause);

            chkLoop = Ui.Chk("Pętla ciągła", Store.Settings.Macro.Continuous, "Powtarza makro w nieskończoność do momentu zatrzymania (F10).");
            chkLoop.Location = new Point(196, y + 2);
            chkLoop.CheckedChanged += delegate { numRepeats.Enabled = !chkLoop.Checked; };
            numRepeats.Enabled = !chkLoop.Checked;
            Controls.Add(chkLoop);
            y += 34;

            chkMoves = Ui.Chk("Nagrywaj ruchy myszy", Store.Settings.Macro.RecordMoves, "Wyłącz aby nagrywać tylko kliknięcia i klawisze (lżejszy plik).");
            chkMoves.Location = new Point(16, y);
            Controls.Add(chkMoves);

            chkTrim = Ui.Chk("Przytnij pusty start", Store.Settings.Macro.TrimLeadingIdle, "Usuwa początkowe opóźnienie przed pierwszą akcją.");
            chkTrim.Location = new Point(200, y);
            Controls.Add(chkTrim);
            y += 26;

            chkTopMost = Ui.Chk("Okno zawsze na wierzchu (Roblox overlay)", true, "");
            chkTopMost.Location = new Point(16, y);
            chkTopMost.CheckedChanged += delegate { TopMost = chkTopMost.Checked; };
            Controls.Add(chkTopMost);

            player.RepeatStarted += delegate(int rep)
            {
                if (!IsDisposed) BeginInvoke(new Action(delegate
                {
                    lblStatus.Text = chkLoop.Checked
                        ? "Odtwarzanie w pętli (cykl #" + rep + ") - F10 aby zatrzymać"
                        : "Odtwarzanie " + rep + "/" + (int)numRepeats.Value + " - F10 aby zatrzymać";
                }));
            };
            player.Finished += delegate
            {
                if (!IsDisposed) BeginInvoke(new Action(delegate
                {
                    lblStatus.Text = "Zakończono odtwarzanie.";
                    RefreshButtons();
                }));
            };

            uiTimer.Interval = 120;
            uiTimer.Tick += delegate
            {
                if (MacroRecorder.Recording)
                {
                    lblStats.Text = "NAGRYWANIE... Kroki: " + MacroRecorder.Events.Count + "  |  " + (recWatch.ElapsedMilliseconds / 1000.0).ToString("0.0") + "s";
                }
            };

            Theme.Apply(this);
            NativeTheme.TitleBar(this);
            RefreshStats();
            RefreshButtons();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            RegisterHotKey(Handle, HOTKEY_REC, 0, (uint)Keys.F9);
            RegisterHotKey(Handle, HOTKEY_PLAY, 0, (uint)Keys.F10);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_HOTKEY)
            {
                int id = m.WParam.ToInt32();
                if (id == HOTKEY_REC) ToggleRecord();
                else if (id == HOTKEY_PLAY) TogglePlay();
            }
            base.WndProc(ref m);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            try { UnregisterHotKey(Handle, HOTKEY_REC); } catch (Exception) { }
            try { UnregisterHotKey(Handle, HOTKEY_PLAY); } catch (Exception) { }
            if (MacroRecorder.Recording) MacroRecorder.Stop();
            if (player.Playing) player.Stop();
            uiTimer.Stop();
            SavePrefs();
            base.OnFormClosing(e);
        }

        void SavePrefs()
        {
            try
            {
                Store.Settings.Macro.Speed = (double)numSpeed.Value;
                Store.Settings.Macro.Repeats = (int)numRepeats.Value;
                Store.Settings.Macro.PauseMs = (int)numPause.Value;
                Store.Settings.Macro.Continuous = chkLoop.Checked;
                Store.Settings.Macro.RecordMoves = chkMoves.Checked;
                Store.Settings.Macro.TrimLeadingIdle = chkTrim.Checked;
                Store.SaveSettings();
            }
            catch (Exception) { }
        }

        void ToggleRecord()
        {
            if (player.Playing) return;
            if (!MacroRecorder.Recording)
            {
                MacroRecorder.RecordMoves = chkMoves.Checked;
                MacroRecorder.HiRes = true;
                if (!MacroRecorder.Start())
                {
                    lblStatus.Text = "Błąd: Windows zablokował hooka nagrywania.";
                    return;
                }
                recWatch.Restart();
                uiTimer.Start();
                lblStatus.Text = "Nagrywanie aktywne - wykonaj akcje w Roblox, potem F9.";
            }
            else
            {
                uiTimer.Stop();
                recWatch.Stop();
                MacroRecorder.Stop();
                events.Clear();
                events.AddRange(MacroRecorder.Events);
                if (chkTrim.Checked) MacroRecorder.TrimIdle(events);
                lblStatus.Text = "Nagrano " + events.Count + " kroków. Naciśnij F10 lub Zapisz .v9m.";
                RefreshStats();
            }
            RefreshButtons();
        }

        void TogglePlay()
        {
            if (MacroRecorder.Recording) return;
            if (player.Playing)
            {
                player.Stop();
                lblStatus.Text = "Zatrzymywanie makra...";
                RefreshButtons();
                return;
            }
            if (events.Count == 0)
            {
                lblStatus.Text = "Brak kroków. Nagraj makro (F9) lub wczytaj plik .v9m.";
                return;
            }
            SavePrefs();
            player.Speed = (double)numSpeed.Value;
            player.Repeats = (int)numRepeats.Value;
            player.Continuous = chkLoop.Checked;
            player.PauseMs = (int)numPause.Value;
            player.Start(events);
            RefreshButtons();
        }

        void ClearEvents()
        {
            if (MacroRecorder.Recording || player.Playing) return;
            events.Clear();
            RefreshStats();
            RefreshButtons();
            lblStatus.Text = "Wyczyszczono bufor makra.";
        }

        void SaveDialog()
        {
            if (events.Count == 0)
            {
                lblStatus.Text = "Brak kroków do zapisania.";
                return;
            }
            using (SaveFileDialog sfd = new SaveFileDialog())
            {
                sfd.InitialDirectory = MacroRecorder.LibraryDir;
                sfd.Filter = "v9 MultiRLBX Macro (*.v9m)|*.v9m|All files (*.*)|*.*";
                sfd.DefaultExt = "v9m";
                sfd.FileName = "macro_" + DateTime.Now.ToString("HHmmss") + ".v9m";
                if (sfd.ShowDialog(this) == DialogResult.OK)
                {
                    MacroDocument doc = new MacroDocument();
                    doc.Name = Path.GetFileNameWithoutExtension(sfd.FileName);
                    doc.Author = Updater.Author;
                    doc.Speed = (double)numSpeed.Value;
                    doc.Repeats = (int)numRepeats.Value;
                    doc.Events = new List<MacroEvent>(events);
                    MacroRecorder.SaveV9m(sfd.FileName, doc);
                    lblStatus.Text = "Zapisano: " + Path.GetFileName(sfd.FileName);
                    AppEngine.AddLog("Zapisano makro .v9m: " + Path.GetFileName(sfd.FileName));
                }
            }
        }

        void LoadDialog()
        {
            if (MacroRecorder.Recording || player.Playing) return;
            using (OpenFileDialog ofd = new OpenFileDialog())
            {
                ofd.InitialDirectory = MacroRecorder.LibraryDir;
                ofd.Filter = "v9 MultiRLBX Macro (*.v9m)|*.v9m|All files (*.*)|*.*";
                if (ofd.ShowDialog(this) == DialogResult.OK)
                {
                    try
                    {
                        MacroDocument doc = MacroRecorder.LoadV9m(ofd.FileName);
                        events.Clear();
                        events.AddRange(doc.Events);
                        if (doc.Speed >= 0.1 && doc.Speed <= 50) numSpeed.Value = (decimal)doc.Speed;
                        if (doc.Repeats >= 1 && doc.Repeats <= 9999) numRepeats.Value = doc.Repeats;
                        RefreshStats();
                        RefreshButtons();
                        lblStatus.Text = "Wczytano: " + doc.Name + " (" + events.Count + " kroków)";
                    }
                    catch (Exception ex)
                    {
                        lblStatus.Text = "Błąd odczytu .v9m: " + ex.Message;
                    }
                }
            }
        }

        void RefreshStats()
        {
            double sec = MacroRecorder.LengthMs(events) / 1000.0;
            lblStats.Text = "Kroki: " + events.Count + "  |  Czas: " + sec.ToString("0.0") + "s";
        }

        void RefreshButtons()
        {
            bool rec = MacroRecorder.Recording;
            bool play = player.Playing;
            btnRec.Text = rec ? "Stop (F9)" : "Nagraj (F9)";
            btnPlay.Text = play ? "Stop (F10)" : "Odtwórz (F10)";
            btnRec.Enabled = !play;
            btnPlay.Enabled = !rec && (play || events.Count > 0);
            btnSave.Enabled = !rec && !play && events.Count > 0;
            btnLoad.Enabled = !rec && !play;
            btnClear.Enabled = !rec && !play && events.Count > 0;
        }
    }
}
