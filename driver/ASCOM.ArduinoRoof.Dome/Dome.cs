using System;
using System.Collections;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using ASCOM.DeviceInterface;
using ASCOM.Utilities;

namespace ASCOM.ArduinoRoof
{
    /// <summary>
    /// Driver ASCOM <see cref="IDomeV3"/> pour un toit coulissant (roll-off roof) piloté par un
    /// Arduino via le protocole série décrit dans <c>docs/PROTOCOLE.md</c> (version 1.0).
    /// <para>
    /// Le toit n'a qu'un axe : ouverture / fermeture. Toutes les fonctions d'azimut, de parking et
    /// d'asservissement sont donc non implémentées, comme l'autorise la norme lorsque les
    /// propriétés <c>CanXxx</c> correspondantes valent <c>false</c>.
    /// </para>
    /// <para>
    /// La classe est exposée à COM ; l'assembly, lui, est <c>ComVisible(false)</c> : la visibilité
    /// est déclarée explicitement, type par type.
    /// </para>
    /// </summary>
    [ComVisible(true)]
    [Guid("73b4fd55-d9d1-4163-9a43-6508d959dc27")]
    [ProgId(DriverProgId)]
    [ClassInterface(ClassInterfaceType.None)]
    [ComDefaultInterface(typeof(IDomeV3))]
    public class Dome : IDomeV3, IDomeV2
    {
        // ------------------------------------------------------------------
        // Identité du driver
        // ------------------------------------------------------------------

        /// <summary>Identifiant COM du driver.</summary>
        internal const string DriverProgId = "ASCOM.ArduinoRoof.Dome";

        /// <summary>Libellé affiché dans le Chooser ASCOM.</summary>
        internal const string DriverDisplayName = "Drivers Ascom toit Obs principal Version 2026-02";

        /// <summary>Type de périphérique ASCOM.</summary>
        internal const string DeviceTypeName = "Dome";

        /// <summary>Version du driver au format ASCOM « majeure.mineure ».</summary>
        internal const string DriverVersionString = "2.0";

        /// <summary>Version du protocole série implémentée.</summary>
        internal const string SupportedProtocolVersion = "1.0";

        /// <summary>Nom du fichier journal ASCOM TraceLogger.</summary>
        internal const string TraceLoggerName = "ArduinoRoof";

        // --- Actions personnalisées ---
        private const string ActionStatus = "Roof:Status";
        private const string ActionDiag = "Roof:Diag";
        private const string ActionClearError = "Roof:ClearError";
        private const string ActionRecover = "Roof:Recover";
        private const string ActionPulse = "Roof:Pulse";
        private const string ActionRawCommand = "Roof:RawCommand";
        private const string ActionMission = "Roof:Mission";
        private const string ActionSimFault = "Sim:Fault";

        // ------------------------------------------------------------------
        // État de l'instance
        // ------------------------------------------------------------------

        private readonly object _connectLock = new object();
        private readonly DriverSettings _settings;
        private readonly DriverLogger _logger;
        private readonly RoofLink _link;
        private readonly ShutterSupervisor _supervisor;

        /// <summary>
        /// Connexion <b>logique</b> ASCOM. Elle reste vraie même si la liaison série est
        /// momentanément perdue : dans ce cas <c>ShutterStatus</c> vaut <c>shutterError</c> et le
        /// driver tente de rouvrir le port (§8.4). Un client ne doit pas être « déconnecté » à son insu.
        /// </summary>
        private volatile bool _connected;

        private volatile bool _connecting;
        private Exception _connectException;
        private bool _disposed;

        /// <summary>Construit le driver. Aucune ouverture de port ici : le Chooser instancie sans connecter.</summary>
        public Dome()
        {
            _settings = DriverSettings.Load();
            _logger = new DriverLogger(TraceLoggerName, _settings.TraceEnabled);
            _logger.Log("Dome", "instanciation de " + DriverProgId + " v" + DriverVersionString
                              + " (protocole " + SupportedProtocolVersion + ") — " + _settings.Describe());
            _link = new RoofLink(_settings, _logger);
            _supervisor = new ShutterSupervisor(_link, _settings, _logger);
        }

        // ==================================================================
        // Enregistrement COM / Chooser ASCOM
        // ==================================================================

        /// <summary>
        /// Appelée par <c>regasm</c> : inscrit le driver dans le Profile ASCOM pour qu'il apparaisse
        /// dans le Chooser sous le nom <see cref="DriverDisplayName"/>.
        /// </summary>
        [ComRegisterFunction]
        public static void RegisterASCOM(Type type)
        {
            using (var profile = new Profile())
            {
                profile.DeviceType = DeviceTypeName;
                profile.Register(DriverProgId, DriverDisplayName);
            }
            DriverSettings.WriteDefaultsIfMissing();
        }

        /// <summary>
        /// Appelée par <c>regasm /unregister</c> : retire le driver du Chooser ASCOM.
        /// <para>
        /// <c>Profile.Unregister</c> supprime la clé du driver <b>avec ses valeurs</b> : port COM,
        /// temporisations, reprises. C'est voulu à la désinstallation (un driver absent ne doit pas
        /// rester au Chooser), mais une mise à jour doit donc sauvegarder et restaurer ces valeurs
        /// autour du désenregistrement : c'est ce que fait <c>release\install.ps1</c>.
        /// </para>
        /// </summary>
        [ComUnregisterFunction]
        public static void UnregisterASCOM(Type type)
        {
            using (var profile = new Profile())
            {
                profile.DeviceType = DeviceTypeName;
                profile.Unregister(DriverProgId);
            }
        }

        // ==================================================================
        // Membres communs ASCOM
        // ==================================================================

        /// <summary>
        /// Connexion synchrone. Ouvre le port, effectue le handshake, pousse les CFG et démarre le polling.
        /// La déconnexion ne ferme <b>jamais</b> le toit (§8.5).
        /// </summary>
        public bool Connected
        {
            get { return _connected; }
            set
            {
                if (value == _connected) return;
                if (value) ConnectCore();
                else DisconnectCore();
            }
        }

        /// <summary>Connexion asynchrone (Platform 7). Rend la main immédiatement ; suivre <see cref="Connecting"/>.</summary>
        public void Connect()
        {
            StartAsyncConnectionChange(true);
        }

        /// <summary>Déconnexion asynchrone (Platform 7). Le toit n'est jamais commandé (§8.5).</summary>
        public void Disconnect()
        {
            StartAsyncConnectionChange(false);
        }

        /// <summary>
        /// Vrai tant qu'un <see cref="Connect"/> ou <see cref="Disconnect"/> asynchrone est en cours.
        /// <para>
        /// Si l'opération a échoué, l'exception correspondante est levée <b>une fois</b> par cette
        /// propriété, dès que l'opération est terminée : c'est le seul canal de remontée d'erreur
        /// prévu par le modèle asynchrone de la Platform 7.
        /// </para>
        /// </summary>
        public bool Connecting
        {
            get
            {
                if (!_connecting)
                {
                    Exception pending = Interlocked.Exchange(ref _connectException, null);
                    if (pending != null) throw pending;
                }
                return _connecting;
            }
        }

        /// <summary>Description courte affichée par les clients ASCOM.</summary>
        public string Description
        {
            get { return DriverDisplayName; }
        }

        /// <summary>Informations détaillées sur le driver.</summary>
        public string DriverInfo
        {
            get { return DriverDisplayName; }
        }

        /// <summary>Version du driver, au format « majeure.mineure » imposé par ASCOM.</summary>
        public string DriverVersion
        {
            get { return DriverVersionString; }
        }

        /// <summary>Version de l'interface implémentée : <c>IDomeV3</c>.</summary>
        public short InterfaceVersion
        {
            get { return 3; }
        }

        /// <summary>Nom court du périphérique.</summary>
        public string Name
        {
            get { return DriverDisplayName; }
        }

        /// <summary>
        /// Liste des actions personnalisées (§ « Actions personnalisées » du cahier des charges).
        /// <c>Sim:Fault</c> n'est proposée qu'avec le simulateur intégré (port « SIM »).
        /// </summary>
        public ArrayList SupportedActions
        {
            get
            {
                var actions = new ArrayList
                {
                    ActionStatus,
                    ActionDiag,
                    ActionClearError,
                    ActionRecover,
                    ActionPulse,
                    ActionRawCommand,
                    ActionMission
                };
                if (_link.IsSimulated) actions.Add(ActionSimFault);
                return actions;
            }
        }

        /// <summary>
        /// Actions personnalisées. Toutes retournent <b>la réponse brute du firmware</b>, ce qui rend
        /// le diagnostic transparent depuis un script ou depuis la console d'actions de NINA.
        /// </summary>
        /// <exception cref="ASCOM.ActionNotImplementedException">Action inconnue.</exception>
        public string Action(string actionName, string actionParameters)
        {
            CheckConnected("Action");
            string name = (actionName ?? string.Empty).Trim();
            string parameters = (actionParameters ?? string.Empty).Trim();
            _logger.Log("Action", name + " (« " + parameters + " »)");

            if (string.Equals(name, ActionStatus, StringComparison.OrdinalIgnoreCase))
            {
                RoofSnapshot snapshot = _link.Snapshot;
                if (!snapshot.LinkUp || snapshot.State == null)
                {
                    return "LINK_DOWN " + (snapshot.LastIssue ?? "liaison série indisponible");
                }
                return snapshot.State.RawLine;
            }

            if (string.Equals(name, ActionDiag, StringComparison.OrdinalIgnoreCase))
            {
                return _link.Transact("DIAG");
            }

            if (string.Equals(name, ActionClearError, StringComparison.OrdinalIgnoreCase))
            {
                string response = _link.Transact("CLEARERR");
                _link.RequestImmediatePoll();
                return response;
            }

            if (string.Equals(name, ActionRecover, StringComparison.OrdinalIgnoreCase))
            {
                string direction;
                if (string.Equals(parameters, "open", StringComparison.OrdinalIgnoreCase)) direction = "OPEN";
                else if (string.Equals(parameters, "close", StringComparison.OrdinalIgnoreCase)) direction = "CLOSE";
                else
                {
                    throw new ASCOM.InvalidValueException(
                        "Roof:Recover attend le paramètre « open » ou « close » (reçu « " + parameters + " »). " +
                        "Attention : une reprise peut faire parcourir au toit une course complète dans le sens " +
                        "non désiré — à n'exécuter que sous surveillance visuelle.");
                }
                string response = _link.Transact("RECOVER " + direction);
                _link.RequestImmediatePoll();
                return response;
            }

            if (string.Equals(name, ActionPulse, StringComparison.OrdinalIgnoreCase))
            {
                string response = _link.Transact("PULSE");
                _link.RequestImmediatePoll();
                return response;
            }

            if (string.Equals(name, ActionMission, StringComparison.OrdinalIgnoreCase))
            {
                return _supervisor.Describe(true);
            }

            if (string.Equals(name, ActionSimFault, StringComparison.OrdinalIgnoreCase) && _link.IsSimulated)
            {
                string result = RoofSimulator.Shared.Control(parameters);
                _link.RequestImmediatePoll();
                return result;
            }

            if (string.Equals(name, ActionRawCommand, StringComparison.OrdinalIgnoreCase))
            {
                if (parameters.Length == 0)
                    throw new ASCOM.InvalidValueException("Roof:RawCommand attend la ligne à envoyer en paramètre.");
                if (parameters.IndexOf('\n') >= 0 || parameters.IndexOf('\r') >= 0)
                    throw new ASCOM.InvalidValueException("Roof:RawCommand n'accepte qu'une seule ligne (ni CR ni LF).");
                if (parameters.Length > RoofLink.MaxLineLength)
                    throw new ASCOM.InvalidValueException(
                        "Roof:RawCommand : le protocole limite une ligne à " + RoofLink.MaxLineLength + " caractères.");

                string response = _link.Transact(parameters);
                _link.RequestImmediatePoll();
                return response;
            }

            throw new ASCOM.ActionNotImplementedException(
                "Action « " + name + " » inconnue. Actions disponibles : " + ActionStatus + ", " + ActionDiag + ", "
                + ActionClearError + ", " + ActionRecover + ", " + ActionPulse + ", " + ActionRawCommand + ", "
                + ActionMission + (_link.IsSimulated ? ", " + ActionSimFault : string.Empty) + ".");
        }

        /// <summary>
        /// Envoie une ligne brute sans exploiter la réponse. Celle-ci est tout de même lue : le
        /// protocole est strictement requête/réponse (§2), la laisser dans le tampon désynchroniserait
        /// la liaison.
        /// </summary>
        public void CommandBlind(string command, bool raw)
        {
            CheckConnected("CommandBlind");
            _link.Transact(NormaliseCommand(command));
        }

        /// <summary>Envoie une ligne brute et retourne <c>true</c> si le firmware a répondu par un <c>ACK</c>.</summary>
        public bool CommandBool(string command, bool raw)
        {
            CheckConnected("CommandBool");
            string response = _link.Transact(NormaliseCommand(command));
            return response.StartsWith("ACK", StringComparison.Ordinal);
        }

        /// <summary>Envoie une ligne brute et retourne la réponse telle quelle (commentaires « # » exclus).</summary>
        public string CommandString(string command, bool raw)
        {
            CheckConnected("CommandString");
            return _link.Transact(NormaliseCommand(command));
        }

        /// <summary>Libère les ressources. Ne commande jamais le toit (§8.5).</summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _logger.Log("Dome", "Dispose — fermeture de la liaison, le toit n'est pas touché (§8.5)");
            try { _link.Dispose(); }
            catch (Exception) { /* libération best-effort */ }
            _connected = false;
            try { _logger.Dispose(); }
            catch (Exception) { /* libération best-effort */ }
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Boîte de configuration, avec panneau de test intégré.
        /// <para>
        /// Elle reste accessible driver connecté : le panneau de test réutilise alors la liaison
        /// existante, ce qui évite tout conflit d'accès au port COM.
        /// </para>
        /// </summary>
        public void SetupDialog()
        {
            _logger.Log("SetupDialog", "ouverture de la boîte de configuration");
            bool wasConnected = _connected;

            using (var form = new SetupDialogForm(_settings, _link, _supervisor, _logger, wasConnected))
            {
                DialogResult result = form.ShowDialog();
                if (result == DialogResult.OK)
                {
                    _settings.CopyFrom(form.WorkingSettings);
                    _settings.Save();
                    _logger.Enabled = _settings.TraceEnabled;
                    _logger.Log("SetupDialog", "réglages enregistrés : " + _settings.Describe());
                    if (_connected)
                    {
                        _logger.Log("SetupDialog",
                            "la période de polling est prise en compte immédiatement ; le port COM et les " +
                            "temporisations CFG ne le seront qu'à la prochaine connexion.");
                    }
                }
                else
                {
                    _settings.CopyFrom(DriverSettings.Load());
                    _logger.Enabled = _settings.TraceEnabled;
                    _logger.Log("SetupDialog", "réglages annulés — retour aux valeurs du Profile ASCOM");
                }
            }

            // Une connexion ouverte pour les essais ne doit pas survivre à la boîte de dialogue
            // si le driver n'était pas connecté. On ferme uniquement le port (§8.5).
            if (!wasConnected && _link.IsOpen)
            {
                _logger.Log("SetupDialog", "fermeture de la liaison de test");
                _link.Close();
            }
        }

        /// <summary>
        /// État opérationnel groupé (Platform 7). Les propriétés non implémentées
        /// (<c>Azimuth</c>, <c>Altitude</c>) sont volontairement absentes de la collection.
        /// </summary>
        public IStateValueCollection DeviceState
        {
            get
            {
                CheckConnected("DeviceState");
                var state = new StateValueCollection();
                state.Add(new StateValue("AtHome", AtHome));
                state.Add(new StateValue("AtPark", AtPark));
                state.Add(new StateValue("ShutterStatus", ShutterStatus));
                state.Add(new StateValue("Slewing", Slewing));
                state.AddLocalDateTime();
                return state;
            }
        }

        // ==================================================================
        // Capacités
        // ==================================================================

        /// <summary>Vrai : c'est la seule capacité d'un toit coulissant.</summary>
        public bool CanSetShutter { get { CheckConnected("CanSetShutter"); return true; } }

        /// <summary>Faux : aucun mouvement en azimut.</summary>
        public bool CanSetAzimuth { get { CheckConnected("CanSetAzimuth"); return false; } }

        /// <summary>Faux : aucun mouvement en altitude.</summary>
        public bool CanSetAltitude { get { CheckConnected("CanSetAltitude"); return false; } }

        /// <summary>Faux : pas de position de parking.</summary>
        public bool CanPark { get { CheckConnected("CanPark"); return false; } }

        /// <summary>Faux : pas de position de parking.</summary>
        public bool CanSetPark { get { CheckConnected("CanSetPark"); return false; } }

        /// <summary>Faux : pas de position d'origine.</summary>
        public bool CanFindHome { get { CheckConnected("CanFindHome"); return false; } }

        /// <summary>Faux : un toit coulissant ne s'asservit pas au télescope.</summary>
        public bool CanSlave { get { CheckConnected("CanSlave"); return false; } }

        /// <summary>Faux : pas de synchronisation d'azimut.</summary>
        public bool CanSyncAzimuth { get { CheckConnected("CanSyncAzimuth"); return false; } }

        // ==================================================================
        // État
        // ==================================================================

        /// <summary>Toujours faux : pas de position d'origine sur un toit coulissant.</summary>
        public bool AtHome { get { CheckConnected("AtHome"); return false; } }

        /// <summary>Toujours faux : pas de position de parking sur un toit coulissant.</summary>
        public bool AtPark { get { CheckConnected("AtPark"); return false; } }

        /// <summary>Toujours faux en lecture ; l'écriture n'est pas implémentée.</summary>
        public bool Slaved
        {
            get { CheckConnected("Slaved"); return false; }
            set { throw new ASCOM.PropertyNotImplementedException("Slaved", true); }
        }

        /// <summary>
        /// Vrai tant que le toit bouge, reprises automatiques comprises. IDomeV3 l'exige : <c>Slewing</c>
        /// doit être vrai dès qu'une partie du dôme est en mouvement, volet compris (ConformU le
        /// vérifie). NINA, lui, suit l'avancement du toit par <see cref="ShutterStatus"/>.
        /// </summary>
        public bool Slewing
        {
            get
            {
                CheckConnected("Slewing");
                if (_supervisor.ActiveTarget.HasValue) return true;

                RoofSnapshot snapshot = _link.Snapshot;
                if (!snapshot.LinkUp || snapshot.State == null) return false;
                return snapshot.State.Motion == RoofMotion.Opening || snapshot.State.Motion == RoofMotion.Closing;
            }
        }

        /// <summary>
        /// Correspondance état firmware → état ASCOM du volet.
        /// <para>
        /// La norme ASCOM ne connaît pas d'état « partiellement ouvert ». Le mapping garantit
        /// qu'<b>un toit non fermé n'est jamais rapporté comme fermé</b> : toute situation ambiguë
        /// (mi-course, position inconnue, erreur firmware, liaison perdue) devient
        /// <c>shutterError</c>. Une option de configuration permet de rapporter la position
        /// intermédiaire en <c>shutterOpen</c>, mais <c>shutterError</c> reste le défaut.
        /// </para>
        /// <para>
        /// Pendant une mission (ouverture ou fermeture demandée, reprises automatiques comprises),
        /// l'état rapporté est <c>shutterOpening</c> / <c>shutterClosing</c> jusqu'à l'arrivée ou
        /// l'échec définitif : NINA attend tant que l'état vaut « en cours ».
        /// </para>
        /// </summary>
        public ShutterState ShutterStatus
        {
            get
            {
                CheckConnected("ShutterStatus");
                return MapShutterState(_link.Snapshot, _settings.IntermediateReportedAsOpen, _supervisor.ActiveTarget);
            }
        }

        /// <summary>
        /// Traduction état firmware → <see cref="ShutterState"/>. Extraite de la propriété pour que
        /// le panneau de test de la boîte de configuration affiche exactement ce que verra NINA.
        /// </summary>
        internal static ShutterState MapShutterState(RoofSnapshot snapshot, bool intermediateAsOpen, MissionTarget? mission)
        {
            // §8.4 : liaison perdue ⇒ shutterError, et aucun mouvement correctif.
            if (snapshot == null || !snapshot.LinkUp || snapshot.State == null) return ShutterState.shutterError;

            RoofState state = snapshot.State;

            if (mission.HasValue)
            {
                if (state.Motion == RoofMotion.Idle)
                {
                    if (mission.Value == MissionTarget.Open && state.Position == RoofPosition.Open) return ShutterState.shutterOpen;
                    if (mission.Value == MissionTarget.Closed && state.Position == RoofPosition.Closed) return ShutterState.shutterClosed;
                }
                return mission.Value == MissionTarget.Open ? ShutterState.shutterOpening : ShutterState.shutterClosing;
            }

            // Le mouvement prime sur la position.
            switch (state.Motion)
            {
                case RoofMotion.Opening: return ShutterState.shutterOpening;
                case RoofMotion.Closing: return ShutterState.shutterClosing;
                case RoofMotion.Error: return ShutterState.shutterError;
            }

            // motion == IDLE
            switch (state.Position)
            {
                case RoofPosition.Open:
                    return ShutterState.shutterOpen;
                case RoofPosition.Closed:
                    return ShutterState.shutterClosed;
                case RoofPosition.Intermediate:
                    return intermediateAsOpen ? ShutterState.shutterOpen : ShutterState.shutterError;
                default:
                    // UNKNOWN : automate désynchronisé, un RECOVER opérateur est nécessaire.
                    return ShutterState.shutterError;
            }
        }

        // ==================================================================
        // Mouvement
        // ==================================================================

        /// <summary>
        /// Démarre l'ouverture. <b>Asynchrone</b> : rend la main dès que la commande est partie, avec
        /// <see cref="ShutterStatus"/> déjà à <c>shutterOpening</c> (ou <c>shutterOpen</c> si le toit
        /// l'était déjà). Une erreur en cours de route est reprise automatiquement (voir
        /// <see cref="ShutterSupervisor"/>). Un appel pendant une ouverture est accepté sans effet ;
        /// pendant une fermeture, celle-ci est arrêtée puis le toit repart dans l'autre sens.
        /// </summary>
        public void OpenShutter()
        {
            CheckConnected("OpenShutter");
            _supervisor.Start(MissionTarget.Open, "OpenShutter");
        }

        /// <summary>
        /// Démarre la fermeture. <b>Asynchrone</b> : rend la main dès que la commande est partie, avec
        /// <see cref="ShutterStatus"/> déjà à <c>shutterClosing</c> (ou <c>shutterClosed</c> si le toit
        /// l'était déjà). Une erreur en cours de route — ou déjà présente — est reprise
        /// automatiquement. Un appel pendant une fermeture est accepté sans effet ; pendant une
        /// ouverture, celle-ci est arrêtée puis le toit se referme.
        /// </summary>
        public void CloseShutter()
        {
            CheckConnected("CloseShutter");
            _supervisor.Start(MissionTarget.Closed, "CloseShutter");
        }

        /// <summary>
        /// Arrêt d'urgence. La mission en cours est d'abord abandonnée (aucune reprise ne doit
        /// relancer le toit), puis <c>ABORT</c> est émis. Le firmware l'accepte même en état
        /// d'erreur : c'est une commande de sécurité, elle ne doit jamais être refusée (§6).
        /// </summary>
        public void AbortSlew()
        {
            CheckConnected("AbortSlew");
            _supervisor.Cancel("AbortSlew");
            string response = _link.Transact("ABORT");
            _link.RequestImmediatePoll();

            if (response.StartsWith("NAK", StringComparison.Ordinal))
            {
                throw new ASCOM.DriverException(
                    "Le contrôleur a refusé l'arrêt d'urgence : « " + response + " ». " +
                    "Ce cas ne devrait jamais se produire, ABORT est une commande de sécurité. " +
                    "Coupez l'alimentation du moteur et vérifiez le firmware.");
            }
        }

        // ==================================================================
        // Membres non implémentés (CanXxx correspondants à false)
        // ==================================================================

        /// <summary>Non implémenté : un toit coulissant n'a pas d'azimut.</summary>
        public double Azimuth
        {
            get { throw new ASCOM.PropertyNotImplementedException("Azimuth", false); }
        }

        /// <summary>Non implémenté : un toit coulissant n'a pas d'altitude de volet.</summary>
        public double Altitude
        {
            get { throw new ASCOM.PropertyNotImplementedException("Altitude", false); }
        }

        /// <summary>Non implémenté : pas de position de parking.</summary>
        public void Park()
        {
            throw new ASCOM.MethodNotImplementedException("Park");
        }

        /// <summary>Non implémenté : pas de position de parking.</summary>
        public void SetPark()
        {
            throw new ASCOM.MethodNotImplementedException("SetPark");
        }

        /// <summary>Non implémenté : pas de position d'origine.</summary>
        public void FindHome()
        {
            throw new ASCOM.MethodNotImplementedException("FindHome");
        }

        /// <summary>Non implémenté : pas de mouvement en azimut.</summary>
        public void SlewToAzimuth(double azimuth)
        {
            throw new ASCOM.MethodNotImplementedException("SlewToAzimuth");
        }

        /// <summary>Non implémenté : pas de mouvement en altitude.</summary>
        public void SlewToAltitude(double altitude)
        {
            throw new ASCOM.MethodNotImplementedException("SlewToAltitude");
        }

        /// <summary>Non implémenté : pas de synchronisation d'azimut.</summary>
        public void SyncToAzimuth(double azimuth)
        {
            throw new ASCOM.MethodNotImplementedException("SyncToAzimuth");
        }

        // ==================================================================
        // Interne
        // ==================================================================

        /// <summary>Normalise une ligne brute avant émission (les commandes du protocole sont en majuscules).</summary>
        private static string NormaliseCommand(string command)
        {
            if (string.IsNullOrWhiteSpace(command))
                throw new ASCOM.InvalidValueException("La commande à envoyer est vide.");
            string trimmed = command.Trim();
            if (trimmed.IndexOf('\n') >= 0 || trimmed.IndexOf('\r') >= 0)
                throw new ASCOM.InvalidValueException("Une commande doit tenir sur une seule ligne (ni CR ni LF).");
            return trimmed;
        }

        /// <summary>Lève <see cref="ASCOM.NotConnectedException"/> tant que le driver n'est pas connecté.</summary>
        private void CheckConnected(string member)
        {
            if (_connected) return;
            throw new ASCOM.NotConnectedException(
                "Le driver du toit n'est pas connecté : « " + member + " » nécessite une connexion établie. " +
                "Appelez Connected = true (ou Connect()) au préalable.");
        }

        private void ConnectCore()
        {
            lock (_connectLock)
            {
                if (_connected) return;
                _logger.Enabled = _settings.TraceEnabled;
                _logger.Log("Connexion", "connexion demandée — " + _settings.Describe());
                _link.Open();          // lève NotConnectedException en cas d'échec
                _connected = true;
                _logger.Log("Connexion", "driver connecté (protocole " + _link.ProtocolVersion
                                        + ", firmware " + _link.FirmwareVersion + ")");
            }
        }

        private void DisconnectCore()
        {
            lock (_connectLock)
            {
                if (!_connected) return;
                _logger.Log("Connexion", "déconnexion demandée — le toit n'est pas touché (§8.5)");
                _connected = false;
                try { _link.Close(); }
                catch (Exception ex) { _logger.LogIssue("Connexion", "erreur pendant la fermeture : " + ex.Message); }
                _logger.Log("Connexion", "driver déconnecté");
            }
        }

        /// <summary>Lance un changement d'état de connexion en tâche de fond (modèle asynchrone Platform 7).</summary>
        private void StartAsyncConnectionChange(bool connect)
        {
            lock (_connectLock)
            {
                if (_connecting) return;
                if (connect == _connected) return;   // déjà dans l'état demandé : rien à faire
                _connecting = true;
                Interlocked.Exchange(ref _connectException, null);
            }

            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    if (connect) ConnectCore();
                    else DisconnectCore();
                }
                catch (Exception ex)
                {
                    _logger.LogIssue("Connexion", (connect ? "Connect()" : "Disconnect()") + " a échoué : " + ex.Message);
                    Interlocked.Exchange(ref _connectException, ex);
                }
                finally
                {
                    _connecting = false;
                }
            });
        }
    }
}
