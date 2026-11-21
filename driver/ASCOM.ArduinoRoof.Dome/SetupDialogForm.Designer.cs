namespace ASCOM.ArduinoRoof
{
    /// <summary>
    /// Code de construction de l'interface. Écrit <b>à la main</b> : aucun concepteur graphique
    /// n'est disponible sur ce poste (pas de Visual Studio). Les coordonnées sont donc explicites,
    /// en une seule colonne, pour rester lisibles et modifiables au clavier sans risque de
    /// chevauchement de libellés.
    /// </summary>
    internal partial class SetupDialogForm
    {
        private System.ComponentModel.IContainer components = null;

        // --- Liaison série ---
        private System.Windows.Forms.GroupBox grpLink;
        private System.Windows.Forms.Label lblPort;
        private System.Windows.Forms.ComboBox cboComPort;
        private System.Windows.Forms.Button btnRefreshPorts;
        private System.Windows.Forms.Label lblPortInfo;

        // --- Temporisations firmware (poussées par CFG) ---
        private System.Windows.Forms.GroupBox grpTimings;
        private System.Windows.Forms.Label lblPulse;
        private System.Windows.Forms.NumericUpDown numPulse;
        private System.Windows.Forms.Label lblPulseHint;
        private System.Windows.Forms.Label lblInter;
        private System.Windows.Forms.NumericUpDown numInter;
        private System.Windows.Forms.Label lblInterHint;
        private System.Windows.Forms.Label lblStart;
        private System.Windows.Forms.NumericUpDown numStart;
        private System.Windows.Forms.Label lblStartHint;
        private System.Windows.Forms.Label lblTravel;
        private System.Windows.Forms.NumericUpDown numTravel;
        private System.Windows.Forms.Label lblTravelHint;
        private System.Windows.Forms.Label lblDebounce;
        private System.Windows.Forms.NumericUpDown numDebounce;
        private System.Windows.Forms.Label lblDebounceHint;
        private System.Windows.Forms.Label lblTimingsWarning;

        // --- Comportement du driver ---
        private System.Windows.Forms.GroupBox grpDriver;
        private System.Windows.Forms.Label lblPoll;
        private System.Windows.Forms.NumericUpDown numPoll;
        private System.Windows.Forms.Label lblPollHint;
        private System.Windows.Forms.CheckBox chkTrace;
        private System.Windows.Forms.CheckBox chkIntermediateOpen;
        private System.Windows.Forms.Label lblRetries;
        private System.Windows.Forms.NumericUpDown numRetries;
        private System.Windows.Forms.Label lblRetriesHint;
        private System.Windows.Forms.CheckBox chkAutoRecover;

        // --- Panneau de test ---
        private System.Windows.Forms.GroupBox grpTest;
        private System.Windows.Forms.Button btnConnect;
        private System.Windows.Forms.Button btnOpenRoof;
        private System.Windows.Forms.Button btnCloseRoof;
        private System.Windows.Forms.Button btnStop;
        private System.Windows.Forms.Button btnDiag;
        private System.Windows.Forms.Button btnClearError;
        private System.Windows.Forms.Button btnPulse;
        private System.Windows.Forms.Button btnRecoverOpen;
        private System.Windows.Forms.Button btnRecoverClose;
        private System.Windows.Forms.Label lblRaw;
        private System.Windows.Forms.TextBox txtRaw;
        private System.Windows.Forms.Button btnSendRaw;
        private System.Windows.Forms.TextBox txtLive;

        // --- Validation ---
        private System.Windows.Forms.Button btnOk;
        private System.Windows.Forms.Button btnCancel;
        private System.Windows.Forms.Timer timerRefresh;

        protected override void Dispose(bool disposing)
        {
            if (disposing && components != null)
            {
                components.Dispose();
            }
            base.Dispose(disposing);
        }

        private void InitializeComponent()
        {
            this.components = new System.ComponentModel.Container();

            this.grpLink = new System.Windows.Forms.GroupBox();
            this.lblPort = new System.Windows.Forms.Label();
            this.cboComPort = new System.Windows.Forms.ComboBox();
            this.btnRefreshPorts = new System.Windows.Forms.Button();
            this.lblPortInfo = new System.Windows.Forms.Label();

            this.grpTimings = new System.Windows.Forms.GroupBox();
            this.lblPulse = new System.Windows.Forms.Label();
            this.numPulse = new System.Windows.Forms.NumericUpDown();
            this.lblPulseHint = new System.Windows.Forms.Label();
            this.lblInter = new System.Windows.Forms.Label();
            this.numInter = new System.Windows.Forms.NumericUpDown();
            this.lblInterHint = new System.Windows.Forms.Label();
            this.lblStart = new System.Windows.Forms.Label();
            this.numStart = new System.Windows.Forms.NumericUpDown();
            this.lblStartHint = new System.Windows.Forms.Label();
            this.lblTravel = new System.Windows.Forms.Label();
            this.numTravel = new System.Windows.Forms.NumericUpDown();
            this.lblTravelHint = new System.Windows.Forms.Label();
            this.lblDebounce = new System.Windows.Forms.Label();
            this.numDebounce = new System.Windows.Forms.NumericUpDown();
            this.lblDebounceHint = new System.Windows.Forms.Label();
            this.lblTimingsWarning = new System.Windows.Forms.Label();

            this.grpDriver = new System.Windows.Forms.GroupBox();
            this.lblPoll = new System.Windows.Forms.Label();
            this.numPoll = new System.Windows.Forms.NumericUpDown();
            this.lblPollHint = new System.Windows.Forms.Label();
            this.chkTrace = new System.Windows.Forms.CheckBox();
            this.chkIntermediateOpen = new System.Windows.Forms.CheckBox();
            this.lblRetries = new System.Windows.Forms.Label();
            this.numRetries = new System.Windows.Forms.NumericUpDown();
            this.lblRetriesHint = new System.Windows.Forms.Label();
            this.chkAutoRecover = new System.Windows.Forms.CheckBox();

            this.grpTest = new System.Windows.Forms.GroupBox();
            this.btnConnect = new System.Windows.Forms.Button();
            this.btnOpenRoof = new System.Windows.Forms.Button();
            this.btnCloseRoof = new System.Windows.Forms.Button();
            this.btnStop = new System.Windows.Forms.Button();
            this.btnDiag = new System.Windows.Forms.Button();
            this.btnClearError = new System.Windows.Forms.Button();
            this.btnPulse = new System.Windows.Forms.Button();
            this.btnRecoverOpen = new System.Windows.Forms.Button();
            this.btnRecoverClose = new System.Windows.Forms.Button();
            this.lblRaw = new System.Windows.Forms.Label();
            this.txtRaw = new System.Windows.Forms.TextBox();
            this.btnSendRaw = new System.Windows.Forms.Button();
            this.txtLive = new System.Windows.Forms.TextBox();

            this.btnOk = new System.Windows.Forms.Button();
            this.btnCancel = new System.Windows.Forms.Button();
            this.timerRefresh = new System.Windows.Forms.Timer(this.components);

            ((System.ComponentModel.ISupportInitialize)(this.numPulse)).BeginInit();
            ((System.ComponentModel.ISupportInitialize)(this.numInter)).BeginInit();
            ((System.ComponentModel.ISupportInitialize)(this.numStart)).BeginInit();
            ((System.ComponentModel.ISupportInitialize)(this.numTravel)).BeginInit();
            ((System.ComponentModel.ISupportInitialize)(this.numDebounce)).BeginInit();
            ((System.ComponentModel.ISupportInitialize)(this.numPoll)).BeginInit();
            ((System.ComponentModel.ISupportInitialize)(this.numRetries)).BeginInit();
            this.grpLink.SuspendLayout();
            this.grpTimings.SuspendLayout();
            this.grpDriver.SuspendLayout();
            this.grpTest.SuspendLayout();
            this.SuspendLayout();

            // ==========================================================
            // Groupe « Liaison série »
            // ==========================================================
            this.grpLink.Location = new System.Drawing.Point(12, 12);
            this.grpLink.Size = new System.Drawing.Size(636, 58);
            this.grpLink.TabIndex = 0;
            this.grpLink.TabStop = false;
            this.grpLink.Text = "Liaison série";
            this.grpLink.Controls.Add(this.lblPort);
            this.grpLink.Controls.Add(this.cboComPort);
            this.grpLink.Controls.Add(this.btnRefreshPorts);
            this.grpLink.Controls.Add(this.lblPortInfo);

            this.lblPort.AutoSize = true;
            this.lblPort.Location = new System.Drawing.Point(14, 26);
            this.lblPort.Text = "Port COM :";

            this.cboComPort.DropDownStyle = System.Windows.Forms.ComboBoxStyle.DropDownList;
            this.cboComPort.Location = new System.Drawing.Point(90, 22);
            this.cboComPort.Size = new System.Drawing.Size(120, 23);
            this.cboComPort.TabIndex = 1;

            this.btnRefreshPorts.Location = new System.Drawing.Point(218, 21);
            this.btnRefreshPorts.Size = new System.Drawing.Size(90, 25);
            this.btnRefreshPorts.TabIndex = 2;
            this.btnRefreshPorts.Text = "Rafraîchir";
            this.btnRefreshPorts.UseVisualStyleBackColor = true;
            this.btnRefreshPorts.Click += new System.EventHandler(this.OnRefreshPortsClick);

            this.lblPortInfo.AutoSize = true;
            this.lblPortInfo.ForeColor = System.Drawing.SystemColors.GrayText;
            this.lblPortInfo.Location = new System.Drawing.Point(322, 26);
            this.lblPortInfo.Text = "115200 bauds, 8N1, sans contrôle de flux";

            // ==========================================================
            // Groupe « Temporisations du firmware » — une ligne par clé CFG
            // ==========================================================
            this.grpTimings.Location = new System.Drawing.Point(12, 76);
            this.grpTimings.Size = new System.Drawing.Size(636, 190);
            this.grpTimings.TabIndex = 3;
            this.grpTimings.TabStop = false;
            this.grpTimings.Text = "Temporisations du firmware — poussées par CFG à chaque connexion";
            this.grpTimings.Controls.Add(this.lblPulse);
            this.grpTimings.Controls.Add(this.numPulse);
            this.grpTimings.Controls.Add(this.lblPulseHint);
            this.grpTimings.Controls.Add(this.lblInter);
            this.grpTimings.Controls.Add(this.numInter);
            this.grpTimings.Controls.Add(this.lblInterHint);
            this.grpTimings.Controls.Add(this.lblStart);
            this.grpTimings.Controls.Add(this.numStart);
            this.grpTimings.Controls.Add(this.lblStartHint);
            this.grpTimings.Controls.Add(this.lblTravel);
            this.grpTimings.Controls.Add(this.numTravel);
            this.grpTimings.Controls.Add(this.lblTravelHint);
            this.grpTimings.Controls.Add(this.lblDebounce);
            this.grpTimings.Controls.Add(this.numDebounce);
            this.grpTimings.Controls.Add(this.lblDebounceHint);
            this.grpTimings.Controls.Add(this.lblTimingsWarning);

            this.lblPulse.AutoSize = true;
            this.lblPulse.Location = new System.Drawing.Point(14, 28);
            this.lblPulse.Text = "PULSE — durée de l'impulsion relais :";

            this.numPulse.Location = new System.Drawing.Point(300, 24);
            this.numPulse.Size = new System.Drawing.Size(90, 23);
            this.numPulse.TabIndex = 4;
            this.numPulse.Maximum = new decimal(new int[] { 2000, 0, 0, 0 });
            this.numPulse.Minimum = new decimal(new int[] { 100, 0, 0, 0 });
            this.numPulse.Increment = new decimal(new int[] { 50, 0, 0, 0 });
            this.numPulse.TextAlign = System.Windows.Forms.HorizontalAlignment.Right;

            this.lblPulseHint.AutoSize = true;
            this.lblPulseHint.ForeColor = System.Drawing.SystemColors.GrayText;
            this.lblPulseHint.Location = new System.Drawing.Point(400, 28);
            this.lblPulseHint.Text = "100 à 2000 ms (défaut 1000)";

            this.lblInter.AutoSize = true;
            this.lblInter.Location = new System.Drawing.Point(14, 58);
            this.lblInter.Text = "INTER — délai entre deux impulsions :";

            this.numInter.Location = new System.Drawing.Point(300, 54);
            this.numInter.Size = new System.Drawing.Size(90, 23);
            this.numInter.TabIndex = 5;
            this.numInter.Maximum = new decimal(new int[] { 5000, 0, 0, 0 });
            this.numInter.Minimum = new decimal(new int[] { 200, 0, 0, 0 });
            this.numInter.Increment = new decimal(new int[] { 50, 0, 0, 0 });
            this.numInter.TextAlign = System.Windows.Forms.HorizontalAlignment.Right;

            this.lblInterHint.AutoSize = true;
            this.lblInterHint.ForeColor = System.Drawing.SystemColors.GrayText;
            this.lblInterHint.Location = new System.Drawing.Point(400, 58);
            this.lblInterHint.Text = "200 à 5000 ms (défaut 1000)";

            this.lblStart.AutoSize = true;
            this.lblStart.Location = new System.Drawing.Point(14, 88);
            this.lblStart.Text = "START — timeout de démarrage :";

            this.numStart.Location = new System.Drawing.Point(300, 84);
            this.numStart.Size = new System.Drawing.Size(90, 23);
            this.numStart.TabIndex = 6;
            this.numStart.Maximum = new decimal(new int[] { 30000, 0, 0, 0 });
            this.numStart.Minimum = new decimal(new int[] { 1000, 0, 0, 0 });
            this.numStart.Increment = new decimal(new int[] { 500, 0, 0, 0 });
            this.numStart.TextAlign = System.Windows.Forms.HorizontalAlignment.Right;

            this.lblStartHint.AutoSize = true;
            this.lblStartHint.ForeColor = System.Drawing.SystemColors.GrayText;
            this.lblStartHint.Location = new System.Drawing.Point(400, 88);
            this.lblStartHint.Text = "1000 à 30000 ms (défaut 5000)";

            this.lblTravel.AutoSize = true;
            this.lblTravel.Location = new System.Drawing.Point(14, 118);
            this.lblTravel.Text = "TRAVEL — timeout de course complète :";

            this.numTravel.Location = new System.Drawing.Point(300, 114);
            this.numTravel.Size = new System.Drawing.Size(90, 23);
            this.numTravel.TabIndex = 7;
            this.numTravel.Maximum = new decimal(new int[] { 300000, 0, 0, 0 });
            this.numTravel.Minimum = new decimal(new int[] { 5000, 0, 0, 0 });
            this.numTravel.Increment = new decimal(new int[] { 1000, 0, 0, 0 });
            this.numTravel.TextAlign = System.Windows.Forms.HorizontalAlignment.Right;

            this.lblTravelHint.AutoSize = true;
            this.lblTravelHint.ForeColor = System.Drawing.SystemColors.GrayText;
            this.lblTravelHint.Location = new System.Drawing.Point(400, 118);
            this.lblTravelHint.Text = "5000 à 300000 ms (défaut 60000)";

            this.lblDebounce.AutoSize = true;
            this.lblDebounce.Location = new System.Drawing.Point(14, 148);
            this.lblDebounce.Text = "DEBOUNCE — anti-rebond des capteurs :";

            this.numDebounce.Location = new System.Drawing.Point(300, 144);
            this.numDebounce.Size = new System.Drawing.Size(90, 23);
            this.numDebounce.TabIndex = 8;
            this.numDebounce.Maximum = new decimal(new int[] { 200, 0, 0, 0 });
            this.numDebounce.Minimum = new decimal(new int[] { 5, 0, 0, 0 });
            this.numDebounce.Increment = new decimal(new int[] { 5, 0, 0, 0 });
            this.numDebounce.TextAlign = System.Windows.Forms.HorizontalAlignment.Right;

            this.lblDebounceHint.AutoSize = true;
            this.lblDebounceHint.ForeColor = System.Drawing.SystemColors.GrayText;
            this.lblDebounceHint.Location = new System.Drawing.Point(400, 148);
            this.lblDebounceHint.Text = "5 à 200 ms (défaut 50)";

            this.lblTimingsWarning.AutoSize = true;
            this.lblTimingsWarning.ForeColor = System.Drawing.SystemColors.GrayText;
            this.lblTimingsWarning.Location = new System.Drawing.Point(14, 170);
            this.lblTimingsWarning.Text = "INTER doit rester supérieur au temps de prise en compte du portail, sinon l'impulsion d'arrêt est ignorée.";

            // ==========================================================
            // Groupe « Comportement du driver »
            // ==========================================================
            this.grpDriver.Location = new System.Drawing.Point(12, 272);
            this.grpDriver.Size = new System.Drawing.Size(636, 156);
            this.grpDriver.TabIndex = 9;
            this.grpDriver.TabStop = false;
            this.grpDriver.Text = "Comportement du driver";
            this.grpDriver.Controls.Add(this.lblPoll);
            this.grpDriver.Controls.Add(this.numPoll);
            this.grpDriver.Controls.Add(this.lblPollHint);
            this.grpDriver.Controls.Add(this.chkTrace);
            this.grpDriver.Controls.Add(this.chkIntermediateOpen);
            this.grpDriver.Controls.Add(this.lblRetries);
            this.grpDriver.Controls.Add(this.numRetries);
            this.grpDriver.Controls.Add(this.lblRetriesHint);
            this.grpDriver.Controls.Add(this.chkAutoRecover);

            this.lblPoll.AutoSize = true;
            this.lblPoll.Location = new System.Drawing.Point(14, 28);
            this.lblPoll.Text = "Période d'interrogation STATE :";

            this.numPoll.Location = new System.Drawing.Point(300, 24);
            this.numPoll.Size = new System.Drawing.Size(90, 23);
            this.numPoll.TabIndex = 10;
            this.numPoll.Maximum = new decimal(new int[] { 10000, 0, 0, 0 });
            this.numPoll.Minimum = new decimal(new int[] { 200, 0, 0, 0 });
            this.numPoll.Increment = new decimal(new int[] { 100, 0, 0, 0 });
            this.numPoll.TextAlign = System.Windows.Forms.HorizontalAlignment.Right;

            this.lblPollHint.AutoSize = true;
            this.lblPollHint.ForeColor = System.Drawing.SystemColors.GrayText;
            this.lblPollHint.Location = new System.Drawing.Point(400, 28);
            this.lblPollHint.Text = "ms — réglage driver, non transmis au firmware";

            this.chkTrace.AutoSize = true;
            this.chkTrace.Location = new System.Drawing.Point(16, 54);
            this.chkTrace.TabIndex = 11;
            this.chkTrace.Text = "Journaliser dans les traces ASCOM « ArduinoRoof » (chaque commande, chaque réponse)";
            this.chkTrace.UseVisualStyleBackColor = true;

            this.chkIntermediateOpen.AutoSize = true;
            this.chkIntermediateOpen.Location = new System.Drawing.Point(16, 76);
            this.chkIntermediateOpen.TabIndex = 13;
            this.chkIntermediateOpen.Text = "Position mi-course rapportée « ouvert » plutôt qu'« erreur » (défaut : erreur)";
            this.chkIntermediateOpen.UseVisualStyleBackColor = true;

            this.lblRetries.AutoSize = true;
            this.lblRetries.Location = new System.Drawing.Point(14, 106);
            this.lblRetries.Text = "Reprises automatiques par ouverture / fermeture :";

            this.numRetries.Location = new System.Drawing.Point(300, 102);
            this.numRetries.Size = new System.Drawing.Size(90, 23);
            this.numRetries.TabIndex = 14;
            this.numRetries.Maximum = new decimal(new int[] { 5, 0, 0, 0 });
            this.numRetries.Minimum = new decimal(new int[] { 0, 0, 0, 0 });
            this.numRetries.TextAlign = System.Windows.Forms.HorizontalAlignment.Right;

            this.lblRetriesHint.AutoSize = true;
            this.lblRetriesHint.ForeColor = System.Drawing.SystemColors.GrayText;
            this.lblRetriesHint.Location = new System.Drawing.Point(400, 106);
            this.lblRetriesHint.Text = "0 à 5 (défaut 3) — 0 : aucune reprise";

            this.chkAutoRecover.AutoSize = true;
            this.chkAutoRecover.Location = new System.Drawing.Point(16, 130);
            this.chkAutoRecover.TabIndex = 15;
            this.chkAutoRecover.Text = "Reprise automatique depuis une position inconnue (RECOVER : course complète possible à contresens)";
            this.chkAutoRecover.UseVisualStyleBackColor = true;

            // ==========================================================
            // Groupe « Panneau de test »
            // ==========================================================
            this.grpTest.Location = new System.Drawing.Point(12, 434);
            this.grpTest.Size = new System.Drawing.Size(636, 280);
            this.grpTest.TabIndex = 14;
            this.grpTest.TabStop = false;
            this.grpTest.Text = "Panneau de test — recette complète sans lancer NINA";
            this.grpTest.Controls.Add(this.btnConnect);
            this.grpTest.Controls.Add(this.btnOpenRoof);
            this.grpTest.Controls.Add(this.btnCloseRoof);
            this.grpTest.Controls.Add(this.btnStop);
            this.grpTest.Controls.Add(this.btnDiag);
            this.grpTest.Controls.Add(this.btnClearError);
            this.grpTest.Controls.Add(this.btnPulse);
            this.grpTest.Controls.Add(this.btnRecoverOpen);
            this.grpTest.Controls.Add(this.btnRecoverClose);
            this.grpTest.Controls.Add(this.lblRaw);
            this.grpTest.Controls.Add(this.txtRaw);
            this.grpTest.Controls.Add(this.btnSendRaw);
            this.grpTest.Controls.Add(this.txtLive);

            this.btnConnect.Location = new System.Drawing.Point(12, 22);
            this.btnConnect.Size = new System.Drawing.Size(104, 27);
            this.btnConnect.TabIndex = 15;
            this.btnConnect.Text = "Connecter";
            this.btnConnect.UseVisualStyleBackColor = true;
            this.btnConnect.Click += new System.EventHandler(this.OnConnectClick);

            this.btnOpenRoof.Location = new System.Drawing.Point(122, 22);
            this.btnOpenRoof.Size = new System.Drawing.Size(98, 27);
            this.btnOpenRoof.TabIndex = 16;
            this.btnOpenRoof.Text = "Ouvrir";
            this.btnOpenRoof.UseVisualStyleBackColor = true;
            this.btnOpenRoof.Click += new System.EventHandler(this.OnOpenRoofClick);

            this.btnCloseRoof.Location = new System.Drawing.Point(226, 22);
            this.btnCloseRoof.Size = new System.Drawing.Size(98, 27);
            this.btnCloseRoof.TabIndex = 17;
            this.btnCloseRoof.Text = "Fermer";
            this.btnCloseRoof.UseVisualStyleBackColor = true;
            this.btnCloseRoof.Click += new System.EventHandler(this.OnCloseRoofClick);

            this.btnStop.Location = new System.Drawing.Point(330, 22);
            this.btnStop.Size = new System.Drawing.Size(98, 27);
            this.btnStop.TabIndex = 18;
            this.btnStop.Text = "Stop";
            this.btnStop.UseVisualStyleBackColor = true;
            this.btnStop.Click += new System.EventHandler(this.OnStopClick);

            this.btnDiag.Location = new System.Drawing.Point(434, 22);
            this.btnDiag.Size = new System.Drawing.Size(88, 27);
            this.btnDiag.TabIndex = 19;
            this.btnDiag.Text = "Diag";
            this.btnDiag.UseVisualStyleBackColor = true;
            this.btnDiag.Click += new System.EventHandler(this.OnDiagClick);

            this.btnClearError.Location = new System.Drawing.Point(528, 22);
            this.btnClearError.Size = new System.Drawing.Size(98, 27);
            this.btnClearError.TabIndex = 20;
            this.btnClearError.Text = "Effacer erreur";
            this.btnClearError.UseVisualStyleBackColor = true;
            this.btnClearError.Click += new System.EventHandler(this.OnClearErrorClick);

            this.btnPulse.Location = new System.Drawing.Point(12, 55);
            this.btnPulse.Size = new System.Drawing.Size(104, 27);
            this.btnPulse.TabIndex = 21;
            this.btnPulse.Text = "Impulsion brute";
            this.btnPulse.UseVisualStyleBackColor = true;
            this.btnPulse.Click += new System.EventHandler(this.OnPulseClick);

            this.btnRecoverOpen.Location = new System.Drawing.Point(122, 55);
            this.btnRecoverOpen.Size = new System.Drawing.Size(150, 27);
            this.btnRecoverOpen.TabIndex = 22;
            this.btnRecoverOpen.Text = "Reprise → ouverture";
            this.btnRecoverOpen.UseVisualStyleBackColor = true;
            this.btnRecoverOpen.Click += new System.EventHandler(this.OnRecoverOpenClick);

            this.btnRecoverClose.Location = new System.Drawing.Point(278, 55);
            this.btnRecoverClose.Size = new System.Drawing.Size(150, 27);
            this.btnRecoverClose.TabIndex = 23;
            this.btnRecoverClose.Text = "Reprise → fermeture";
            this.btnRecoverClose.UseVisualStyleBackColor = true;
            this.btnRecoverClose.Click += new System.EventHandler(this.OnRecoverCloseClick);

            this.lblRaw.AutoSize = true;
            this.lblRaw.Location = new System.Drawing.Point(12, 93);
            this.lblRaw.Text = "Commande brute :";

            this.txtRaw.Location = new System.Drawing.Point(126, 89);
            this.txtRaw.Size = new System.Drawing.Size(390, 23);
            this.txtRaw.TabIndex = 24;
            this.txtRaw.MaxLength = 80;
            this.txtRaw.KeyDown += new System.Windows.Forms.KeyEventHandler(this.OnRawKeyDown);

            this.btnSendRaw.Location = new System.Drawing.Point(522, 88);
            this.btnSendRaw.Size = new System.Drawing.Size(104, 26);
            this.btnSendRaw.TabIndex = 25;
            this.btnSendRaw.Text = "Envoyer";
            this.btnSendRaw.UseVisualStyleBackColor = true;
            this.btnSendRaw.Click += new System.EventHandler(this.OnSendRawClick);

            this.txtLive.BackColor = System.Drawing.SystemColors.Window;
            this.txtLive.Font = new System.Drawing.Font("Consolas", 8.75F);
            this.txtLive.Location = new System.Drawing.Point(12, 122);
            this.txtLive.Multiline = true;
            this.txtLive.ReadOnly = true;
            this.txtLive.ScrollBars = System.Windows.Forms.ScrollBars.Vertical;
            this.txtLive.Size = new System.Drawing.Size(614, 148);
            this.txtLive.TabIndex = 26;
            this.txtLive.TabStop = false;
            this.txtLive.WordWrap = false;

            // ==========================================================
            // Boutons de validation
            // ==========================================================
            this.btnOk.Location = new System.Drawing.Point(466, 724);
            this.btnOk.Size = new System.Drawing.Size(85, 28);
            this.btnOk.TabIndex = 27;
            this.btnOk.Text = "Enregistrer";
            this.btnOk.UseVisualStyleBackColor = true;
            this.btnOk.Click += new System.EventHandler(this.OnOkClick);

            this.btnCancel.DialogResult = System.Windows.Forms.DialogResult.Cancel;
            this.btnCancel.Location = new System.Drawing.Point(557, 724);
            this.btnCancel.Size = new System.Drawing.Size(85, 28);
            this.btnCancel.TabIndex = 28;
            this.btnCancel.Text = "Annuler";
            this.btnCancel.UseVisualStyleBackColor = true;

            this.timerRefresh.Interval = 400;
            this.timerRefresh.Tick += new System.EventHandler(this.OnRefreshTick);

            // ==========================================================
            // Fenêtre
            // ==========================================================
            this.AutoScaleDimensions = new System.Drawing.SizeF(7F, 15F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            this.CancelButton = this.btnCancel;
            this.ClientSize = new System.Drawing.Size(660, 764);
            this.Controls.Add(this.grpLink);
            this.Controls.Add(this.grpTimings);
            this.Controls.Add(this.grpDriver);
            this.Controls.Add(this.grpTest);
            this.Controls.Add(this.btnOk);
            this.Controls.Add(this.btnCancel);
            this.FormBorderStyle = System.Windows.Forms.FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.Name = "SetupDialogForm";
            this.ShowInTaskbar = false;
            this.StartPosition = System.Windows.Forms.FormStartPosition.CenterScreen;
            this.Text = "Toit coulissant Arduino — configuration du driver ASCOM";

            ((System.ComponentModel.ISupportInitialize)(this.numPulse)).EndInit();
            ((System.ComponentModel.ISupportInitialize)(this.numInter)).EndInit();
            ((System.ComponentModel.ISupportInitialize)(this.numStart)).EndInit();
            ((System.ComponentModel.ISupportInitialize)(this.numTravel)).EndInit();
            ((System.ComponentModel.ISupportInitialize)(this.numDebounce)).EndInit();
            ((System.ComponentModel.ISupportInitialize)(this.numPoll)).EndInit();
            ((System.ComponentModel.ISupportInitialize)(this.numRetries)).EndInit();
            this.grpLink.ResumeLayout(false);
            this.grpLink.PerformLayout();
            this.grpTimings.ResumeLayout(false);
            this.grpTimings.PerformLayout();
            this.grpDriver.ResumeLayout(false);
            this.grpDriver.PerformLayout();
            this.grpTest.ResumeLayout(false);
            this.grpTest.PerformLayout();
            this.ResumeLayout(false);
        }
    }
}
