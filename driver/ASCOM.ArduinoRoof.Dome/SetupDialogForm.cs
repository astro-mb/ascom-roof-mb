using System;
using System.Globalization;
using System.IO.Ports;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using ASCOM.DeviceInterface;

namespace ASCOM.ArduinoRoof
{
    /// <summary>
    /// Boîte de configuration du driver, avec panneau de test intégré.
    /// <para>
    /// Le panneau de test partage la <see cref="RoofLink"/> et le moteur de missions du driver :
    /// impossible d'ouvrir deux fois le même port COM, les boutons Ouvrir / Fermer passent par les
    /// mêmes reprises automatiques que NINA, et l'état affiché est exactement celui que verra NINA.
    /// </para>
    /// <para>
    /// Les réglages sont modifiés sur une copie de travail ; « Enregistrer » les recopie dans les
    /// réglages vivants et les persiste dans le Profile ASCOM, « Annuler » les abandonne.
    /// Exception : se connecter depuis le panneau de test applique immédiatement la copie de
    /// travail — on ne peut pas tester des temporisations qu'on n'a pas envoyées.
    /// </para>
    /// </summary>
    [ComVisible(false)]
    [Guid("2d86093c-6ae7-4141-8155-2973207e6cf6")]
    internal partial class SetupDialogForm : Form
    {
        private readonly DriverSettings _liveSettings;
        private readonly RoofLink _link;
        private readonly ShutterSupervisor _supervisor;
        private readonly DriverLogger _log;
        private readonly bool _driverConnected;
        private readonly DriverSettings _working;

        private string _lastAction = "(aucune)";
        private string _lastDisplay = string.Empty;

        /// <summary>0 = disponible, 1 = une opération série est en cours.</summary>
        private int _busy;

        internal SetupDialogForm(DriverSettings liveSettings, RoofLink link, ShutterSupervisor supervisor,
                                 DriverLogger log, bool driverConnected)
        {
            InitializeComponent();

            _liveSettings = liveSettings;
            _link = link;
            _supervisor = supervisor;
            _log = log;
            _driverConnected = driverConnected;
            _working = liveSettings.Clone();

            LoadPortList(_working.ComPort);
            LoadSettingsIntoControls();

            if (_driverConnected)
            {
                btnConnect.Text = "Connecté";
                btnConnect.Enabled = false;
                cboComPort.Enabled = false;
                btnRefreshPorts.Enabled = false;
                _lastAction = "(driver déjà connecté — la liaison du driver est réutilisée)";
            }
        }

        /// <summary>Copie de travail des réglages, lue par le driver si l'utilisateur valide.</summary>
        internal DriverSettings WorkingSettings { get { return _working; } }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            timerRefresh.Start();
            RefreshLiveDisplay();
            UpdateButtonStates();
            Activate();
            BringToFront();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (Interlocked.CompareExchange(ref _busy, 0, 0) != 0)
            {
                // Une transaction série est en cours : la fermer maintenant laisserait un fil
                // d'exécution parler à un port qu'on est en train de fermer.
                MessageBox.Show(this,
                    "Une commande est en cours d'exécution sur le port série. Attendez sa fin avant de fermer cette fenêtre.",
                    "Opération en cours", MessageBoxButtons.OK, MessageBoxIcon.Information);
                e.Cancel = true;
                return;
            }
            timerRefresh.Stop();
            base.OnFormClosing(e);
        }

        // ==================================================================
        // Chargement / relecture des contrôles
        // ==================================================================

        private void LoadPortList(string selected)
        {
            cboComPort.BeginUpdate();
            try
            {
                cboComPort.Items.Clear();
                string[] ports = SerialPort.GetPortNames();
                Array.Sort(ports, ComparePortNames);
                cboComPort.Items.AddRange(ports);

                // Simulateur intégré : valide le driver (ConformU, reprises) sans matériel.
                cboComPort.Items.Add(RoofSimulator.PortName);

                // Le port configuré peut être momentanément absent (Arduino débranché) :
                // on le conserve dans la liste pour ne pas perdre le réglage.
                if (!string.IsNullOrEmpty(selected) && !cboComPort.Items.Contains(selected))
                {
                    cboComPort.Items.Add(selected);
                }
                if (!string.IsNullOrEmpty(selected)) cboComPort.SelectedItem = selected;
                else if (cboComPort.Items.Count > 0) cboComPort.SelectedIndex = 0;
            }
            finally
            {
                cboComPort.EndUpdate();
            }
        }

        /// <summary>Tri naturel : COM3 avant COM10.</summary>
        private static int ComparePortNames(string a, string b)
        {
            int na, nb;
            bool oka = int.TryParse(a.Length > 3 ? a.Substring(3) : string.Empty, NumberStyles.None, CultureInfo.InvariantCulture, out na);
            bool okb = int.TryParse(b.Length > 3 ? b.Substring(3) : string.Empty, NumberStyles.None, CultureInfo.InvariantCulture, out nb);
            if (oka && okb) return na.CompareTo(nb);
            return string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
        }

        private void LoadSettingsIntoControls()
        {
            numPulse.Value = _working.PulseMs;
            numInter.Value = _working.InterPulseMs;
            numStart.Value = _working.StartTimeoutMs;
            numTravel.Value = _working.TravelTimeoutMs;
            numDebounce.Value = _working.DebounceMs;
            numPoll.Value = _working.PollPeriodMs;
            chkTrace.Checked = _working.TraceEnabled;
            chkIntermediateOpen.Checked = _working.IntermediateReportedAsOpen;
            numRetries.Value = _working.RetryCount;
            chkAutoRecover.Checked = _working.AutoRecover;
        }

        /// <summary>Recopie les contrôles dans la copie de travail.</summary>
        private bool ReadControlsIntoWorking()
        {
            string port = cboComPort.SelectedItem as string;
            if (string.IsNullOrWhiteSpace(port))
            {
                MessageBox.Show(this,
                    "Aucun port COM n'est sélectionné. Branchez l'Arduino puis cliquez sur « Rafraîchir ».",
                    "Port COM manquant", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }

            _working.ComPort = port;
            _working.PulseMs = (int)numPulse.Value;
            _working.InterPulseMs = (int)numInter.Value;
            _working.StartTimeoutMs = (int)numStart.Value;
            _working.TravelTimeoutMs = (int)numTravel.Value;
            _working.DebounceMs = (int)numDebounce.Value;
            _working.PollPeriodMs = (int)numPoll.Value;
            _working.TraceEnabled = chkTrace.Checked;
            _working.IntermediateReportedAsOpen = chkIntermediateOpen.Checked;
            _working.RetryCount = (int)numRetries.Value;
            _working.AutoRecover = chkAutoRecover.Checked;
            _working.Clamp();
            return true;
        }

        // ==================================================================
        // Gestionnaires d'événements
        // ==================================================================

        private void OnRefreshPortsClick(object sender, EventArgs e)
        {
            LoadPortList(cboComPort.SelectedItem as string);
        }

        private void OnOkClick(object sender, EventArgs e)
        {
            if (!ReadControlsIntoWorking()) return;
            DialogResult = DialogResult.OK;
            Close();
        }

        private void OnConnectClick(object sender, EventArgs e)
        {
            if (_driverConnected) return;   // la liaison appartient au driver

            if (_link.IsOpen)
            {
                RunSerialOperation("Déconnexion", delegate
                {
                    _link.Close();
                    return "port fermé — le toit n'est pas touché";
                });
                return;
            }

            if (!ReadControlsIntoWorking()) return;

            // Impossible de tester des temporisations qui n'ont pas été envoyées : la connexion de
            // test applique la copie de travail aux réglages vivants (que la boîte soit validée ou non).
            _liveSettings.CopyFrom(_working);
            _log.Enabled = _working.TraceEnabled;

            RunSerialOperation("Connexion", delegate
            {
                _link.Open();
                return "liaison établie — protocole " + _link.ProtocolVersion + ", firmware " + _link.FirmwareVersion;
            });
        }

        private void OnOpenRoofClick(object sender, EventArgs e)
        {
            StartTestMission(MissionTarget.Open, "Ouverture");
        }

        private void OnCloseRoofClick(object sender, EventArgs e)
        {
            StartTestMission(MissionTarget.Closed, "Fermeture");
        }

        private void OnStopClick(object sender, EventArgs e)
        {
            // Comme AbortSlew : la mission est abandonnée AVANT l'arrêt, aucune reprise ne doit relancer le toit.
            _supervisor.Cancel("Stop du panneau de test");
            SendTestCommand("ABORT", "Arrêt");
        }

        private void OnDiagClick(object sender, EventArgs e)
        {
            SendTestCommand("DIAG", "Diagnostic");
        }

        private void OnClearErrorClick(object sender, EventArgs e)
        {
            SendTestCommand("CLEARERR", "Effacement d'erreur");
        }

        private void OnPulseClick(object sender, EventArgs e)
        {
            if (!Confirm("Envoyer une impulsion brute (PULSE) ?\r\n\r\n"
                       + "Le mouvement résultant n'est PAS surveillé et la position passera à « inconnue » "
                       + "si aucun capteur n'est actif. Réservé à la mise au point, sous surveillance visuelle."))
            {
                return;
            }
            SendTestCommand("PULSE", "Impulsion brute");
        }

        private void OnRecoverOpenClick(object sender, EventArgs e)
        {
            if (!ConfirmRecover("ouverture")) return;
            SendTestCommand("RECOVER OPEN", "Reprise vers l'ouverture");
        }

        private void OnRecoverCloseClick(object sender, EventArgs e)
        {
            if (!ConfirmRecover("fermeture")) return;
            SendTestCommand("RECOVER CLOSE", "Reprise vers la fermeture");
        }

        private void OnSendRawClick(object sender, EventArgs e)
        {
            string command = txtRaw.Text.Trim();
            if (command.Length == 0)
            {
                MessageBox.Show(this, "Saisissez la ligne à envoyer (par exemple « CFG? » ou « STATE »).",
                    "Commande vide", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            SendTestCommand(command, "Commande brute « " + command + " »");
        }

        private void OnRawKeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode != Keys.Enter) return;
            e.Handled = true;
            e.SuppressKeyPress = true;
            OnSendRawClick(sender, EventArgs.Empty);
        }

        private void OnRefreshTick(object sender, EventArgs e)
        {
            RefreshLiveDisplay();
            UpdateButtonStates();
        }

        // ==================================================================
        // Exécution des commandes de test
        // ==================================================================

        private void SendTestCommand(string command, string label)
        {
            if (!_link.IsOpen)
            {
                MessageBox.Show(this,
                    "La liaison de test n'est pas ouverte. Cliquez d'abord sur « Connecter ».",
                    "Liaison fermée", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            RunSerialOperation(label, delegate
            {
                string response = _link.Transact(command);
                _link.RequestImmediatePoll();
                return response;
            });
        }

        /// <summary>
        /// Ouverture / fermeture par le moteur de missions, avec les mêmes reprises automatiques que
        /// NINA. Les réglages de reprise de la copie de travail sont appliqués avant le départ.
        /// </summary>
        private void StartTestMission(MissionTarget target, string label)
        {
            if (!_link.IsOpen)
            {
                MessageBox.Show(this,
                    "La liaison de test n'est pas ouverte. Cliquez d'abord sur « Connecter ».",
                    "Liaison fermée", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            _liveSettings.RetryCount = (int)numRetries.Value;
            _liveSettings.AutoRecover = chkAutoRecover.Checked;

            RunSerialOperation(label, delegate
            {
                _supervisor.Start(target, "panneau de test");
                return "mission lancée, suivie ci-dessous";
            });
        }

        /// <summary>
        /// Exécute une opération série hors du fil de l'interface : une connexion prend au moins
        /// 2,5 s (reset du Nano) et gèlerait la fenêtre.
        /// </summary>
        private void RunSerialOperation(string label, Func<string> operation)
        {
            if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0) return;

            _lastAction = label + " → en cours…";
            RefreshLiveDisplay();
            UpdateButtonStates();

            ThreadPool.QueueUserWorkItem(delegate
            {
                string outcome;
                try
                {
                    outcome = operation();
                }
                catch (Exception ex)
                {
                    outcome = "ÉCHEC — " + ex.Message;
                }

                PostToUi(delegate
                {
                    _lastAction = label + " → " + outcome;
                    Interlocked.Exchange(ref _busy, 0);
                    RefreshLiveDisplay();
                    UpdateButtonStates();
                });
            });
        }

        private void PostToUi(MethodInvoker action)
        {
            try
            {
                if (IsDisposed || Disposing || !IsHandleCreated) return;
                BeginInvoke(action);
            }
            catch (ObjectDisposedException) { /* fenêtre déjà fermée */ }
            catch (InvalidOperationException) { /* poignée détruite entre-temps */ }
        }

        private bool Confirm(string message)
        {
            return MessageBox.Show(this, message, "Confirmation",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) == DialogResult.Yes;
        }

        private bool ConfirmRecover(string direction)
        {
            return Confirm("Lancer une reprise vers la " + direction + " (RECOVER) ?\r\n\r\n"
                         + "La position du toit étant inconnue, le firmware émet UNE impulsion et surveille les deux "
                         + "capteurs. Le toit peut donc parcourir une course complète DANS LE SENS NON DÉSIRÉ.\r\n\r\n"
                         + "À n'exécuter que sous surveillance visuelle directe du toit.");
        }

        // ==================================================================
        // Affichage temps réel
        // ==================================================================

        private void UpdateButtonStates()
        {
            bool idle = Interlocked.CompareExchange(ref _busy, 0, 0) == 0;
            bool linkOpen = _link.IsOpen;

            btnConnect.Enabled = idle && !_driverConnected;
            if (!_driverConnected) btnConnect.Text = linkOpen ? "Déconnecter" : "Connecter";

            btnOpenRoof.Enabled = idle && linkOpen;
            btnCloseRoof.Enabled = idle && linkOpen;
            btnStop.Enabled = idle && linkOpen;
            btnDiag.Enabled = idle && linkOpen;
            btnClearError.Enabled = idle && linkOpen;
            btnPulse.Enabled = idle && linkOpen;
            btnRecoverOpen.Enabled = idle && linkOpen;
            btnRecoverClose.Enabled = idle && linkOpen;
            btnSendRaw.Enabled = idle && linkOpen;

            btnOk.Enabled = idle;
            btnCancel.Enabled = idle;
            btnRefreshPorts.Enabled = idle && !_driverConnected;
        }

        private void RefreshLiveDisplay()
        {
            RoofSnapshot snapshot = _link.Snapshot;
            bool portOpen = _link.IsOpen;
            var sb = new StringBuilder();

            if (!portOpen)
            {
                sb.Append("Liaison         : FERMÉE");
                if (!string.IsNullOrEmpty(snapshot.LastIssue)) sb.Append(" — ").Append(snapshot.LastIssue);
                sb.AppendLine();
            }
            else if (!snapshot.LinkUp)
            {
                sb.Append("Liaison         : PERDUE (").Append(snapshot.ConsecutiveFailures)
                  .Append(" échecs consécutifs) — reconnexion automatique du port en cours");
                sb.AppendLine();
            }
            else
            {
                sb.Append("Liaison         : OK sur ").Append(snapshot.PortName)
                  .Append(" — protocole ").Append(snapshot.ProtocolVersion ?? "?")
                  .Append(", firmware ").Append(snapshot.FirmwareVersion ?? "?");
                if (snapshot.ConsecutiveFailures > 0)
                {
                    sb.Append("  [").Append(snapshot.ConsecutiveFailures).Append(" échec(s) récent(s)]");
                }
                sb.AppendLine();
            }

            ShutterState shutter = Dome.MapShutterState(snapshot, chkIntermediateOpen.Checked, _supervisor.ActiveTarget);
            sb.Append("ShutterStatus   : ").Append(shutter).Append("   ← ce que verra NINA").AppendLine();
            sb.Append("Mission         : ").Append(_supervisor.Describe(false)).AppendLine();
            if (_link.StartupWarning != null)
            {
                sb.Append("Avert. connexion: ").Append(_link.StartupWarning).AppendLine();
            }

            RoofState state = snapshot.State;
            if (state == null)
            {
                sb.AppendLine("État du toit    : aucune trame STATE reçue pour l'instant");
            }
            else
            {
                sb.Append("Position        : ").Append(RoofVocabulary.Describe(state.Position)).AppendLine();
                sb.Append("Mouvement       : ").Append(RoofVocabulary.Describe(state.Motion)).AppendLine();
                sb.Append("Capteurs        : fermé=").Append(state.SwitchClosed ? "1" : "0")
                  .Append("  ouvert=").Append(state.SwitchOpen ? "1" : "0")
                  .Append("     Relais : ").Append(state.RelayActive ? "ACTIF" : "repos").AppendLine();
                sb.Append("Prochaine impul.: ").Append(state.NextPulse == RoofNextPulse.Open ? "OPEN (ouvre)" : "CLOSE (ferme)")
                  .Append("     Écoulé : ").Append(state.ElapsedMs.ToString(CultureInfo.InvariantCulture)).Append(" ms").AppendLine();
                sb.Append("Erreur          : ").Append(RoofVocabulary.Describe(state.Error)).AppendLine();
                sb.Append("Trame STATE     : ").Append(state.RawLine)
                  .Append("   (il y a ").Append(((DateTime.UtcNow - state.TimestampUtc).TotalSeconds).ToString("F1", CultureInfo.InvariantCulture))
                  .Append(" s)").AppendLine();
            }

            if (!string.IsNullOrEmpty(snapshot.ConfigWarning))
            {
                sb.Append("Avert. CFG      : ").Append(snapshot.ConfigWarning).AppendLine();
            }

            sb.Append("Dernière action : ").Append(_lastAction);

            string text = sb.ToString();
            if (text == _lastDisplay) return;   // évite le scintillement et le saut du curseur
            _lastDisplay = text;
            txtLive.Text = text;
        }
    }
}
