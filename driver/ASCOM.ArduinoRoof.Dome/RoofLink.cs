using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading;

namespace ASCOM.ArduinoRoof
{
    /// <summary>
    /// Liaison série avec le contrôleur de toit.
    /// <para>Règles imposées au driver (§8 du protocole) appliquées ici :</para>
    /// <list type="number">
    ///   <item>seule la trame <c>STATE</c> alimente l'état interne — les ACK ne sont jamais interprétés ;</item>
    ///   <item>un unique verrou sérialise tous les accès au port : une requête → une réponse ;</item>
    ///   <item>toute ligne commençant par <c>#</c> est ignorée, y compris avant la réponse attendue ;</item>
    ///   <item>au-delà de 3 échecs consécutifs la liaison est déclarée perdue ; seule la reconnexion
    ///         <b>du port</b> est tentée, jamais le moindre mouvement correctif.</item>
    /// </list>
    /// </summary>
    internal sealed class RoofLink : IDisposable
    {
        // --- Valeurs imposées par le protocole (§1, §2) ---
        internal const int BaudRate = 115200;
        internal const int PortSettleMs = 2500;      // reset DTR du Nano
        internal const int HandshakeAttempts = 3;
        internal const int HandshakeRetryDelayMs = 1000;
        internal const int SerialTimeoutMs = 1000;   // lecture ET écriture
        internal const int MaxLineLength = 80;

        // --- Robustesse propre au driver ---
        internal const int MaxConsecutiveFailures = 3;
        internal const int ReconnectDelayMs = 5000;

        /// <summary>Garde-fou global d'une transaction : évite qu'un déluge de lignes « # » ne bloque le driver.</summary>
        private const int TransactionBudgetMs = 3000;

        private const int MaxCommentLinesPerTransaction = 32;

        /// <summary>Attente maximale du verrou d'E/S côté appelant ASCOM (une reconnexion peut le tenir ~6 s).</summary>
        private const int LockAcquireTimeoutMs = 12000;

        /// <summary>Période de polling maximale pendant une mission : les reprises réagissent plus vite.</summary>
        private const int FastPollPeriodMs = 500;

        private readonly object _ioLock = new object();
        private readonly DriverSettings _settings;
        private readonly DriverLogger _log;
        private readonly ManualResetEventSlim _wake = new ManualResetEventSlim(false);

        private IRoofTransport _transport;
        private Thread _pollThread;
        private volatile bool _stopRequested;

        /// <summary>Vrai uniquement après un handshake réussi : garantit qu'aucune commande ne part pendant une reconnexion.</summary>
        private volatile bool _linkReady;

        private volatile RoofSnapshot _snapshot;
        private int _consecutiveFailures;
        private DateTime _nextReconnectUtc = DateTime.MinValue;
        private string _protocolVersion;
        private string _firmwareVersion;
        private string _configWarning;
        private bool _disposed;
        private volatile bool _fastPolling;

        /// <summary>Vrai entre l'ouverture du port et la première trame STATE valide qui la suit.</summary>
        private bool _firstStateAfterOpen;

        private volatile string _startupWarning;

        /// <summary>
        /// Appelé par la tâche de fond après chaque tour de polling, verrou d'E/S relâché, avec
        /// l'instantané courant. C'est le point d'entrée du moteur de missions (reprises automatiques).
        /// </summary>
        internal Action<RoofSnapshot> TickHandler { get; set; }

        /// <summary>Appelé au début de <see cref="Close"/> : toute mission en cours doit être abandonnée.</summary>
        internal Action ClosingHandler { get; set; }

        internal RoofLink(DriverSettings settings, DriverLogger log)
        {
            _settings = settings;
            _log = log;
            _snapshot = RoofSnapshot.Down(settings.ComPort, null);
        }

        /// <summary>Vrai quand le port est ouvert et le handshake validé.</summary>
        internal bool IsOpen { get { return _linkReady; } }

        /// <summary>Dernier instantané publié. Toutes les propriétés ASCOM lisent ce cache, jamais le port.</summary>
        internal RoofSnapshot Snapshot { get { return _snapshot; } }

        internal string ProtocolVersion { get { return _protocolVersion; } }

        internal string FirmwareVersion { get { return _firmwareVersion; } }

        /// <summary>
        /// Dernière anomalie constatée sur la première trame <c>STATE</c> après une ouverture du port
        /// (connexion ou reconnexion), horodatée ; <c>null</c> s'il n'y en a jamais eu. Elle reste
        /// affichée jusqu'à la suivante, pour être encore lisible le matin.
        /// </summary>
        internal string StartupWarning { get { return _startupWarning; } }

        /// <summary>Vrai quand le port configuré est le simulateur intégré (« SIM »).</summary>
        internal bool IsSimulated { get { return RoofSimulator.IsSimulatorPort(_settings.ComPort); } }

        /// <summary>Accélère le polling (<see cref="FastPollPeriodMs"/>) tant qu'une mission est en cours.</summary>
        internal bool FastPolling
        {
            get { return _fastPolling; }
            set
            {
                _fastPolling = value;
                if (value) RequestImmediatePoll();
            }
        }

        // ------------------------------------------------------------------
        // Ouverture / fermeture
        // ------------------------------------------------------------------

        /// <summary>
        /// Ouvre le port, attend le reset du Nano, effectue le handshake <c>PING</c>/<c>PONG</c>,
        /// pousse les cinq temporisations par <c>CFG</c> puis démarre la tâche de polling.
        /// </summary>
        /// <exception cref="ASCOM.NotConnectedException">Port indisponible, firmware muet ou version de protocole incompatible.</exception>
        internal void Open()
        {
            lock (_ioLock)
            {
                if (_linkReady) return;
                OpenAndHandshakeLocked();
            }

            // Première lecture SYNCHRONE, avant de rendre la main. Sans elle, l'instantané vaut
            // encore « liaison basse » au retour de Open() : ShutterStatus renverrait shutterError
            // pendant la seconde suivant la connexion, et le client afficherait une erreur fantôme
            // alors que tout va bien. Constaté dans NINA, qui lit l'état aussitôt après Connect.
            // PollOnce() prend lui-même le verrou d'E/S, il doit donc rester hors du lock ci-dessus.
            PollOnce();

            StartPolling();
            RequestImmediatePoll();
        }

        /// <summary>Arrête le polling et ferme le port. Ne commande jamais le moindre mouvement (§8.5).</summary>
        internal void Close()
        {
            Action closing = ClosingHandler;
            if (closing != null)
            {
                try { closing(); }
                catch (Exception ex) { _log.LogIssue("Liaison", "abandon de la mission : " + ex.Message); }
            }
            StopPolling();
            lock (_ioLock)
            {
                ClosePortLocked();
                _consecutiveFailures = 0;
            }
            _snapshot = RoofSnapshot.Down(_settings.ComPort, null);
            _log.Log("Liaison", "port fermé — le toit n'est pas touché (§8.5)");
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { Close(); }
            catch (Exception ex) { _log.LogIssue("Liaison", "erreur pendant la fermeture : " + ex.Message); }
            try { _wake.Dispose(); }
            catch (Exception) { /* déjà libéré */ }
        }

        // ------------------------------------------------------------------
        // Transactions
        // ------------------------------------------------------------------

        /// <summary>
        /// Envoie une ligne et retourne la réponse (hors lignes de commentaire).
        /// Sérialisé par le verrou d'E/S : aucun appel concurrent n'atteint le port.
        /// </summary>
        internal string Transact(string command)
        {
            if (string.IsNullOrWhiteSpace(command))
                throw new ASCOM.InvalidValueException("La commande envoyée au contrôleur de toit est vide.");

            if (!_linkReady)
                throw new ASCOM.NotConnectedException(
                    "Liaison série indisponible avec le contrôleur de toit (port « " + _settings.ComPort + " »). " +
                    "Le driver tente une reconnexion automatique du port ; aucun mouvement n'est commandé.");

            if (!Monitor.TryEnter(_ioLock, LockAcquireTimeoutMs))
                throw new ASCOM.DriverException(
                    "Le port série est resté occupé plus de " + (LockAcquireTimeoutMs / 1000) +
                    " s (reconnexion en cours ?). Commande « " + command + " » abandonnée.");
            try
            {
                if (_transport == null || !_transport.IsOpen || !_linkReady)
                    throw new ASCOM.NotConnectedException("La liaison série a été perdue pendant l'envoi de « " + command + " ».");
                return TransactLocked(command);
            }
            finally
            {
                Monitor.Exit(_ioLock);
            }
        }

        /// <summary>
        /// Interroge <c>STATE</c> immédiatement et publie l'instantané avant de rendre la main.
        /// Après une commande de mouvement, c'est ce qui garantit qu'<c>OpenShutter</c> et
        /// <c>CloseShutter</c> rendent la main avec <c>ShutterStatus</c> déjà à jour (exigence ASCOM),
        /// tout en respectant le §8.1 : l'état publié vient de <c>STATE</c>, jamais de l'<c>ACK</c>.
        /// </summary>
        /// <exception cref="ASCOM.NotConnectedException">Liaison indisponible, ou trame STATE illisible.</exception>
        internal RoofSnapshot PollNow()
        {
            if (!_linkReady)
                throw new ASCOM.NotConnectedException(
                    "Liaison série indisponible avec le contrôleur de toit (port « " + _settings.ComPort + " »).");
            if (!Monitor.TryEnter(_ioLock, LockAcquireTimeoutMs))
                throw new ASCOM.DriverException(
                    "Le port série est resté occupé plus de " + (LockAcquireTimeoutMs / 1000) + " s : lecture de l'état abandonnée.");
            try
            {
                if (!PollLocked())
                {
                    RoofSnapshot failed = _snapshot;
                    throw new ASCOM.NotConnectedException(
                        "Lecture de l'état du toit impossible : " + (failed.LastIssue ?? "liaison indisponible") + ".");
                }
                return _snapshot;
            }
            finally
            {
                Monitor.Exit(_ioLock);
            }
        }

        /// <summary>Réveille immédiatement la tâche de polling (après une commande de mouvement, par exemple).</summary>
        internal void RequestImmediatePoll()
        {
            try { _wake.Set(); }
            catch (ObjectDisposedException) { /* driver en cours de libération */ }
        }

        // ------------------------------------------------------------------
        // Implémentation — toujours appelée verrou tenu
        // ------------------------------------------------------------------

        private void OpenAndHandshakeLocked()
        {
            string portName = _settings.ComPort;
            if (string.IsNullOrWhiteSpace(portName))
                throw new ASCOM.NotConnectedException("Aucun port COM n'est configuré. Ouvrez la boîte de configuration du driver et choisissez le port de l'Arduino.");

            ClosePortLocked();

            IRoofTransport transport = RoofSimulator.IsSimulatorPort(portName)
                ? (IRoofTransport)new SimulatedRoofTransport(RoofSimulator.Shared)
                : new SerialRoofTransport(portName);

            _log.Log("Liaison", "ouverture : " + transport.Description);
            try
            {
                transport.Open();
            }
            catch (Exception ex)
            {
                try { transport.Dispose(); } catch (Exception) { /* best-effort */ }
                throw new ASCOM.NotConnectedException(
                    "Impossible d'ouvrir le port « " + portName + " » : " + ex.Message +
                    ". Vérifiez que l'Arduino est branché, que le port existe et qu'aucune autre application " +
                    "(moniteur série de l'IDE Arduino, autre instance du driver) ne l'utilise déjà.", ex);
            }
            _transport = transport;

            // §1 : l'ouverture du port réinitialise le Nano (DTR). Attente obligatoire de 2500 ms,
            // puis purge des tampons — le firmware a émis « # ROOF CONTROLLER <version> BOOT ».
            _log.Log("Liaison", "attente de " + transport.SettleMs + " ms (reset DTR du Nano) puis purge des tampons");
            Thread.Sleep(transport.SettleMs);
            try
            {
                _transport.DiscardInBuffer();
                _transport.DiscardOutBuffer();
            }
            catch (Exception ex)
            {
                _log.LogIssue("Liaison", "purge des tampons impossible : " + ex.Message);
            }

            HandshakeLocked(portName);

            try
            {
                PushConfigurationLocked();
            }
            catch (Exception ex)
            {
                ClosePortLocked();
                throw new ASCOM.NotConnectedException(
                    "Le contrôleur a répondu au PING mais l'envoi de la configuration (CFG) a échoué : " + ex.Message, ex);
            }

            _linkReady = true;
            _firstStateAfterOpen = true;
            _consecutiveFailures = 0;
            _log.Log("Liaison", "liaison établie — protocole " + _protocolVersion + ", firmware " + _firmwareVersion);
        }

        /// <summary>§1 : trois tentatives de <c>PING</c> espacées d'une seconde avant de déclarer l'échec.</summary>
        private void HandshakeLocked(string portName)
        {
            string lastError = null;

            for (int attempt = 1; attempt <= HandshakeAttempts; attempt++)
            {
                _log.Log("Handshake", "tentative " + attempt + "/" + HandshakeAttempts);
                try
                {
                    string response = TransactLocked("PING");

                    // Format imposé : PONG ROOF <versionProtocole> <versionFirmware>
                    string[] t = response.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    if (t.Length != 4 || t[0] != "PONG" || t[1] != "ROOF")
                    {
                        lastError = "réponse inattendue au PING : « " + response + " » (attendu « PONG ROOF <protocole> <firmware> »)";
                    }
                    else if (!t[2].StartsWith("1.", StringComparison.Ordinal))
                    {
                        // Incompatibilité franche : inutile de réessayer.
                        string bad = t[2];
                        ClosePortLocked();
                        throw new ASCOM.NotConnectedException(
                            "Version de protocole incompatible : le firmware annonce « " + bad + " » alors que ce driver " +
                            "implémente la version 1.x. Reprogrammez l'Arduino avec un firmware conforme au document " +
                            "docs/PROTOCOLE.md, ou mettez le driver à jour.");
                    }
                    else
                    {
                        _protocolVersion = t[2];
                        _firmwareVersion = t[3];
                        return;
                    }
                }
                catch (ASCOM.NotConnectedException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                }

                _log.LogIssue("Handshake", "tentative " + attempt + " échouée : " + lastError);
                if (attempt < HandshakeAttempts) Thread.Sleep(HandshakeRetryDelayMs);
            }

            ClosePortLocked();
            throw new ASCOM.NotConnectedException(
                "Le contrôleur de toit n'a pas répondu au PING sur « " + portName + " » après " + HandshakeAttempts +
                " tentatives. Dernière erreur : " + lastError + ". Vérifiez le port COM choisi, le câble USB, " +
                "l'alimentation de l'Arduino et le firmware chargé (le moniteur série doit afficher « # ROOF CONTROLLER … BOOT » à la connexion).");
        }

        /// <summary>
        /// Pousse les cinq temporisations vers le firmware (§6). Un <c>NAK</c> n'interrompt pas la
        /// connexion — refuser de se connecter empêcherait de fermer le toit, ce qui serait pire —
        /// mais il est journalisé et remonté dans l'instantané.
        /// </summary>
        private void PushConfigurationLocked()
        {
            var warnings = new List<string>();

            PushOneConfigLocked("PULSE", _settings.PulseMs, warnings);
            PushOneConfigLocked("INTER", _settings.InterPulseMs, warnings);
            PushOneConfigLocked("START", _settings.StartTimeoutMs, warnings);
            PushOneConfigLocked("TRAVEL", _settings.TravelTimeoutMs, warnings);
            PushOneConfigLocked("DEBOUNCE", _settings.DebounceMs, warnings);

            // Relecture de contrôle.
            try
            {
                string cfg = TransactLocked("CFG?");
                RoofConfig parsed;
                string reason;
                if (RoofConfig.TryParse(cfg, out parsed, out reason))
                {
                    _log.Log("Config", "relecture firmware : " + parsed.RawLine);
                    if (parsed.PulseMs != _settings.PulseMs || parsed.InterPulseMs != _settings.InterPulseMs
                        || parsed.StartTimeoutMs != _settings.StartTimeoutMs || parsed.TravelTimeoutMs != _settings.TravelTimeoutMs
                        || parsed.DebounceMs != _settings.DebounceMs)
                    {
                        warnings.Add("la relecture CFG? ne correspond pas aux réglages du driver (" + parsed.RawLine + ")");
                    }
                }
                else
                {
                    warnings.Add("réponse CFG? illisible (" + reason + ")");
                }
            }
            catch (Exception ex)
            {
                warnings.Add("relecture CFG? impossible : " + ex.Message);
            }

            _configWarning = warnings.Count == 0 ? null : string.Join(" ; ", warnings.ToArray());
            if (_configWarning != null) _log.LogIssue("Config", _configWarning);
        }

        private void PushOneConfigLocked(string key, int value, List<string> warnings)
        {
            string command = "CFG " + key + " " + value.ToString(CultureInfo.InvariantCulture);
            string response = TransactLocked(command);
            string expected = "ACK CFG " + key + "=" + value.ToString(CultureInfo.InvariantCulture);
            if (!string.Equals(response, expected, StringComparison.Ordinal))
            {
                warnings.Add(key + " refusé ou modifié par le firmware (réponse « " + response + " », attendu « " + expected + " »)");
            }
        }

        /// <summary>
        /// Cœur du modèle requête/réponse. Le tampon d'entrée est purgé avant l'émission pour se
        /// resynchroniser si une réponse précédente est arrivée après son délai d'attente.
        /// </summary>
        private string TransactLocked(string command)
        {
            if (command.Length > MaxLineLength)
                throw new ASCOM.InvalidValueException(
                    "Ligne de " + command.Length + " caractères : le protocole limite une ligne à " + MaxLineLength + " caractères.");

            try { _transport.DiscardInBuffer(); }
            catch (Exception) { /* purge best-effort */ }

            _log.Log("→ TX", command);
            _transport.WriteLine(command);

            var chrono = Stopwatch.StartNew();
            int comments = 0;

            while (true)
            {
                if (chrono.ElapsedMilliseconds > TransactionBudgetMs)
                    throw new TimeoutException(
                        "Aucune réponse exploitable à « " + command + " » en " + TransactionBudgetMs + " ms.");

                string line;
                try
                {
                    line = _transport.ReadLine();
                }
                catch (TimeoutException)
                {
                    throw new TimeoutException(
                        "Le contrôleur n'a pas répondu à « " + command + " » en " + SerialTimeoutMs +
                        " ms (le protocole impose une réponse en moins de 100 ms).");
                }

                // §1 : Arduino → PC se termine par \r\n ; NewLine vaut "\n", il reste le \r final.
                line = line.Trim();

                if (line.Length == 0) continue;

                // §2/§8.3 : les lignes de commentaire sont ignorées, y compris avant la réponse.
                if (line[0] == '#')
                {
                    _log.Log("  # RX", line);
                    if (++comments > MaxCommentLinesPerTransaction)
                        throw new ASCOM.DriverException(
                            "Le contrôleur a émis plus de " + MaxCommentLinesPerTransaction +
                            " lignes de commentaire sans répondre à « " + command + " ».");
                    continue;
                }

                _log.Log("← RX", line);
                return line;
            }
        }

        private void ClosePortLocked()
        {
            _linkReady = false;
            if (_transport == null) return;
            try { _transport.Dispose(); }
            catch (Exception ex) { _log.LogIssue("Liaison", "fermeture du port : " + ex.Message); }
            _transport = null;
        }

        // ------------------------------------------------------------------
        // Tâche de fond : polling STATE + reconnexion du port
        // ------------------------------------------------------------------

        private void StartPolling()
        {
            lock (_ioLock)
            {
                if (_pollThread != null) return;
                _stopRequested = false;
                _pollThread = new Thread(PollLoop);
                _pollThread.IsBackground = true;   // ne doit jamais retenir le processus hôte
                _pollThread.Name = "ArduinoRoof-Poll";
                _pollThread.Start();
            }
            _log.Log("Polling", "tâche de fond démarrée, période " + _settings.PollPeriodMs + " ms");
        }

        private void StopPolling()
        {
            Thread thread;
            lock (_ioLock)
            {
                thread = _pollThread;
                _pollThread = null;
                _stopRequested = true;
            }
            if (thread == null) return;

            try { _wake.Set(); }
            catch (ObjectDisposedException) { /* déjà libéré */ }

            // Jamais sous _ioLock : la tâche de fond prend ce même verrou.
            if (!thread.Join(5000))
            {
                _log.LogIssue("Polling", "la tâche de fond ne s'est pas arrêtée en 5 s (elle est en arrière-plan, elle n'empêchera pas la fermeture)");
            }
            else
            {
                _log.Log("Polling", "tâche de fond arrêtée");
            }
        }

        private void PollLoop()
        {
            while (!_stopRequested)
            {
                try
                {
                    if (_linkReady) PollOnce();
                    else TryReconnect();
                }
                catch (Exception ex)
                {
                    _log.LogIssue("Polling", "exception inattendue : " + ex.Message);
                }

                if (_stopRequested) break;

                Action<RoofSnapshot> tick = TickHandler;
                if (tick != null)
                {
                    try { tick(_snapshot); }
                    catch (Exception ex) { _log.LogIssue("Mission", "exception inattendue : " + ex.Message); }
                }

                if (_stopRequested) break;

                int period = _settings.PollPeriodMs;
                if (period < DriverSettings.PollMin) period = DriverSettings.PollMin;
                if (_fastPolling && period > FastPollPeriodMs) period = FastPollPeriodMs;
                try
                {
                    _wake.Wait(period);
                    _wake.Reset();
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
            }
        }

        private void PollOnce()
        {
            if (!Monitor.TryEnter(_ioLock, 2000)) return;   // une commande ASCOM est prioritaire, on repassera
            try
            {
                PollLocked();
            }
            finally
            {
                Monitor.Exit(_ioLock);
            }
        }

        /// <summary>Un tour de <c>STATE</c>, verrou tenu. Retourne vrai si une trame valide a été publiée.</summary>
        private bool PollLocked()
        {
            if (_transport == null || !_transport.IsOpen || !_linkReady) return false;

            string line;
            try
            {
                line = TransactLocked("STATE");
            }
            catch (Exception ex)
            {
                RegisterFailureLocked("STATE : " + ex.Message);
                return false;
            }

            RoofState state;
            string reason;
            if (!RoofState.TryParse(line, out state, out reason))
            {
                RegisterFailureLocked("trame STATE invalide (« " + line + " ») : " + reason);
                return false;
            }

            // Toit déplacé à la main : le firmware lève SENSOR_LOST au départ de la butée et le garde
            // une fois l'autre butée atteinte. Une fin de course active rend la position sûre : on acquitte.
            if (state.Error == RoofErrorCode.SensorLost && state.Motion == RoofMotion.Error
                && (state.Position == RoofPosition.Open || state.Position == RoofPosition.Closed))
            {
                try
                {
                    _log.Log("Polling", "SENSOR_LOST avec toit en butée (déplacement manuel) — CLEARERR automatique : "
                                        + TransactLocked("CLEARERR"));
                    RoofState cleared;
                    if (RoofState.TryParse(TransactLocked("STATE"), out cleared, out reason)) state = cleared;
                }
                catch (Exception ex)
                {
                    _log.LogIssue("Polling", "CLEARERR automatique échoué : " + ex.Message);
                }
            }

            _consecutiveFailures = 0;
            _snapshot = new RoofSnapshot(true, state, 0, null, _settings.ComPort,
                                         _protocolVersion, _firmwareVersion, _configWarning);

            if (_firstStateAfterOpen)
            {
                _firstStateAfterOpen = false;
                CheckStartupState(state);
            }
            return true;
        }

        /// <summary>
        /// Examine la première trame après une ouverture du port. L'ouverture redémarre la carte, et
        /// après une coupure en pleine course la première ouverture qui suit la ré-énumération USB la
        /// redémarre <b>une seconde fois</b> : le premier démarrage a déjà enregistré « plus de
        /// mouvement en cours », le second ne trouve qu'une position <c>UNKNOWN</c> sans erreur et
        /// <c>INTERRUPTED</c> est perdu (TESTS.md, étape 3). Le driver le signale donc lui-même, sans
        /// changement du protocole : c'est le seul indice d'une coupure qui reste dans la trace.
        /// </summary>
        private void CheckStartupState(RoofState state)
        {
            string message = null;
            if (state.Position == RoofPosition.Unknown && state.Motion == RoofMotion.Idle && state.Error == RoofErrorCode.None)
            {
                message = "position du toit inconnue, sans erreur, dès l'ouverture du port : la carte a "
                        + "probablement été coupée en pleine course puis redémarrée deux fois (INTERRUPTED perdu "
                        + "au second démarrage), ou une impulsion brute (PULSE) a été émise. Une reprise par "
                        + "RECOVER sera nécessaire (§8.6) — " + state.RawLine;
            }
            else if (state.Error == RoofErrorCode.Interrupted)
            {
                message = "coupure de la carte en pleine course (INTERRUPTED) dès l'ouverture du port — " + state.RawLine;
            }
            if (message == null) return;

            _startupWarning = DateTime.Now.ToString("dd/MM/yyyy HH:mm:ss", CultureInfo.InvariantCulture) + " " + message;
            _log.LogIssue("Connexion", message);
        }

        /// <summary>
        /// Comptabilise un échec. Au-delà de <see cref="MaxConsecutiveFailures"/> échecs consécutifs
        /// la liaison est déclarée perdue : le port est refermé (seule reconnexion possible) et
        /// <c>ShutterStatus</c> passera à <c>shutterError</c> (§8.4). Aucun mouvement correctif.
        /// </summary>
        private void RegisterFailureLocked(string message)
        {
            _consecutiveFailures++;
            RoofSnapshot previous = _snapshot;
            RoofState lastKnown = previous == null ? null : previous.State;

            if (_consecutiveFailures > MaxConsecutiveFailures)
            {
                _log.LogIssue("Liaison",
                    "liaison déclarée perdue après " + _consecutiveFailures + " échecs consécutifs (" + message +
                    ") — ShutterStatus passe à shutterError. Aucun mouvement correctif n'est déclenché (§8.4).");
                ClosePortLocked();
                _nextReconnectUtc = DateTime.UtcNow.AddMilliseconds(ReconnectDelayMs);
                _snapshot = new RoofSnapshot(false, lastKnown, _consecutiveFailures, message, _settings.ComPort,
                                             _protocolVersion, _firmwareVersion, _configWarning);
            }
            else
            {
                _log.LogIssue("Liaison", "échec " + _consecutiveFailures + "/" + MaxConsecutiveFailures + " : " + message);
                _snapshot = new RoofSnapshot(true, lastKnown, _consecutiveFailures, message, _settings.ComPort,
                                             _protocolVersion, _firmwareVersion, _configWarning);
            }
        }

        /// <summary>Reconnexion automatique <b>du port seulement</b> : jamais de commande de mouvement (§8.4).</summary>
        private void TryReconnect()
        {
            if (DateTime.UtcNow < _nextReconnectUtc) return;
            if (!Monitor.TryEnter(_ioLock, 500)) return;
            try
            {
                if (_stopRequested || _linkReady) return;
                _nextReconnectUtc = DateTime.UtcNow.AddMilliseconds(ReconnectDelayMs);
                _log.Log("Liaison", "tentative de reconnexion du port " + _settings.ComPort + " (aucun mouvement n'est commandé)");
                try
                {
                    OpenAndHandshakeLocked();
                    _log.Log("Liaison", "reconnexion réussie");
                }
                catch (Exception ex)
                {
                    _log.LogIssue("Liaison", "reconnexion échouée : " + ex.Message + " — nouvelle tentative dans " + (ReconnectDelayMs / 1000) + " s");
                    RoofSnapshot previous = _snapshot;
                    _snapshot = new RoofSnapshot(false, previous == null ? null : previous.State,
                                                 _consecutiveFailures, ex.Message, _settings.ComPort,
                                                 _protocolVersion, _firmwareVersion, _configWarning);
                }
            }
            finally
            {
                Monitor.Exit(_ioLock);
            }
        }
    }
}
