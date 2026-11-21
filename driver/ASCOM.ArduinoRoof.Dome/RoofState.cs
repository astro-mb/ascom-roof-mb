using System;
using System.Collections.Generic;
using System.Globalization;

namespace ASCOM.ArduinoRoof
{
    /// <summary>Position du toit — §3 du protocole, champ &lt;pos&gt;.</summary>
    internal enum RoofPosition
    {
        /// <summary>Capteur « fermé » actif.</summary>
        Closed,
        /// <summary>Capteur « ouvert » actif.</summary>
        Open,
        /// <summary>Aucun capteur actif, position connue de l'automate (arrêt volontaire à mi-course).</summary>
        Intermediate,
        /// <summary>Aucun capteur actif et automate désynchronisé — un RECOVER est nécessaire.</summary>
        Unknown
    }

    /// <summary>Mouvement en cours — §3 du protocole, champ &lt;motion&gt;.</summary>
    internal enum RoofMotion
    {
        Idle,
        Opening,
        Closing,
        /// <summary>Erreur active : tout mouvement est refusé jusqu'à CLEARERR.</summary>
        Error
    }

    /// <summary>Effet de la prochaine impulsion — §3/§4 du protocole, champ &lt;next&gt;.</summary>
    internal enum RoofNextPulse
    {
        Open,
        Close
    }

    /// <summary>Code d'erreur — §3 du protocole, champ &lt;err&gt;.</summary>
    internal enum RoofErrorCode
    {
        None,
        TimeoutStart,
        TimeoutTravel,
        BothSensors,
        WrongDirection,
        Interrupted,
        SensorLost
    }

    /// <summary>État du bloc EEPROM rapporté par DIAG (champ EE=).</summary>
    internal enum RoofEepromStatus
    {
        Ok,
        Init,
        Bad
    }

    /// <summary>
    /// Tables de conversion chaîne → énumération. Le décodage est <b>strict</b> : seules les
    /// orthographes exactes du protocole (majuscules) sont acceptées. Toute autre valeur fait
    /// échouer le parsing, ce qui est compté comme un échec de liaison plutôt que d'être deviné.
    /// </summary>
    internal static class RoofVocabulary
    {
        private static readonly Dictionary<string, RoofPosition> Positions = new Dictionary<string, RoofPosition>(StringComparer.Ordinal)
        {
            { "CLOSED", RoofPosition.Closed },
            { "OPEN", RoofPosition.Open },
            { "INTERMEDIATE", RoofPosition.Intermediate },
            { "UNKNOWN", RoofPosition.Unknown }
        };

        private static readonly Dictionary<string, RoofMotion> Motions = new Dictionary<string, RoofMotion>(StringComparer.Ordinal)
        {
            { "IDLE", RoofMotion.Idle },
            { "OPENING", RoofMotion.Opening },
            { "CLOSING", RoofMotion.Closing },
            { "ERROR", RoofMotion.Error }
        };

        private static readonly Dictionary<string, RoofNextPulse> NextPulses = new Dictionary<string, RoofNextPulse>(StringComparer.Ordinal)
        {
            { "OPEN", RoofNextPulse.Open },
            { "CLOSE", RoofNextPulse.Close }
        };

        private static readonly Dictionary<string, RoofErrorCode> Errors = new Dictionary<string, RoofErrorCode>(StringComparer.Ordinal)
        {
            { "NONE", RoofErrorCode.None },
            { "TIMEOUT_START", RoofErrorCode.TimeoutStart },
            { "TIMEOUT_TRAVEL", RoofErrorCode.TimeoutTravel },
            { "BOTH_SENSORS", RoofErrorCode.BothSensors },
            { "WRONG_DIRECTION", RoofErrorCode.WrongDirection },
            { "INTERRUPTED", RoofErrorCode.Interrupted },
            { "SENSOR_LOST", RoofErrorCode.SensorLost }
        };

        private static readonly Dictionary<string, RoofEepromStatus> Eeproms = new Dictionary<string, RoofEepromStatus>(StringComparer.Ordinal)
        {
            { "OK", RoofEepromStatus.Ok },
            { "INIT", RoofEepromStatus.Init },
            { "BAD", RoofEepromStatus.Bad }
        };

        internal static bool TryPosition(string token, out RoofPosition value) { return Positions.TryGetValue(token ?? string.Empty, out value); }
        internal static bool TryMotion(string token, out RoofMotion value) { return Motions.TryGetValue(token ?? string.Empty, out value); }
        internal static bool TryNextPulse(string token, out RoofNextPulse value) { return NextPulses.TryGetValue(token ?? string.Empty, out value); }
        internal static bool TryError(string token, out RoofErrorCode value) { return Errors.TryGetValue(token ?? string.Empty, out value); }
        internal static bool TryEeprom(string token, out RoofEepromStatus value) { return Eeproms.TryGetValue(token ?? string.Empty, out value); }

        /// <summary>Entier décimal strict : ni signe, ni espace, ni séparateur de milliers.</summary>
        internal static bool TryInteger(string token, out long value)
        {
            return long.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out value);
        }

        /// <summary>Booléen du protocole : exactement « 0 » ou « 1 ».</summary>
        internal static bool TryBit(string token, out bool value)
        {
            if (token == "0") { value = false; return true; }
            if (token == "1") { value = true; return true; }
            value = false;
            return false;
        }

        // --- Libellés français pour le panneau de test ---

        internal static string Describe(RoofPosition p)
        {
            switch (p)
            {
                case RoofPosition.Closed: return "FERMÉ";
                case RoofPosition.Open: return "OUVERT";
                case RoofPosition.Intermediate: return "MI-COURSE (position connue)";
                default: return "INCONNUE (RECOVER requis)";
            }
        }

        internal static string Describe(RoofMotion m)
        {
            switch (m)
            {
                case RoofMotion.Idle: return "à l'arrêt";
                case RoofMotion.Opening: return "ouverture en cours";
                case RoofMotion.Closing: return "fermeture en cours";
                default: return "ERREUR ACTIVE (CLEARERR requis)";
            }
        }

        internal static string Describe(RoofErrorCode e)
        {
            switch (e)
            {
                case RoofErrorCode.None: return "aucune";
                case RoofErrorCode.TimeoutStart: return "TIMEOUT_START — l'impulsion n'a rien produit (moteur hors tension, relais ou contact HS)";
                case RoofErrorCode.TimeoutTravel: return "TIMEOUT_TRAVEL — le capteur cible n'a pas été atteint (obstacle, moteur bloqué, capteur HS)";
                case RoofErrorCode.BothSensors: return "BOTH_SENSORS — les deux capteurs sont actifs (câblage ou capteur défaillant)";
                case RoofErrorCode.WrongDirection: return "WRONG_DIRECTION — arrivée sur le capteur opposé, modèle « next » corrigé";
                case RoofErrorCode.SensorLost: return "SENSOR_LOST — une fin de course s'est désactivée sans mouvement commandé (toit déplacé manuellement, capteur déréglé ou câble coupé)";
                default: return "INTERRUPTED — mouvement interrompu au dernier démarrage, position perdue";
            }
        }
    }

    /// <summary>
    /// Image immuable d'une trame <c>STATE</c> (§6 du protocole).
    /// C'est la <b>seule</b> source de vérité du driver (§8.1) : aucun ACK n'alimente cet objet.
    /// </summary>
    internal sealed class RoofState
    {
        /// <summary>Nombre de champs de la trame STATE, mot-clé compris.</summary>
        private const int FieldCount = 9;

        private static long _sequenceCounter;

        private RoofState(RoofPosition position, RoofMotion motion, bool switchClosed, bool switchOpen,
                          bool relayActive, RoofNextPulse nextPulse, long elapsedMs, RoofErrorCode error, string rawLine)
        {
            Position = position;
            Motion = motion;
            SwitchClosed = switchClosed;
            SwitchOpen = switchOpen;
            RelayActive = relayActive;
            NextPulse = nextPulse;
            ElapsedMs = elapsedMs;
            Error = error;
            RawLine = rawLine;
            TimestampUtc = DateTime.UtcNow;
            Sequence = System.Threading.Interlocked.Increment(ref _sequenceCounter);
        }

        internal RoofPosition Position { get; private set; }
        internal RoofMotion Motion { get; private set; }
        internal bool SwitchClosed { get; private set; }
        internal bool SwitchOpen { get; private set; }
        internal bool RelayActive { get; private set; }
        internal RoofNextPulse NextPulse { get; private set; }
        internal long ElapsedMs { get; private set; }
        internal RoofErrorCode Error { get; private set; }
        internal string RawLine { get; private set; }
        internal DateTime TimestampUtc { get; private set; }

        /// <summary>
        /// Numéro d'ordre de décodage, strictement croissant. Permet au moteur de missions d'écarter
        /// une trame lue <b>avant</b> sa dernière commande, même si elle lui parvient après.
        /// </summary>
        internal long Sequence { get; private set; }

        /// <summary>
        /// Décodage strict de <c>STATE &lt;pos&gt; &lt;motion&gt; &lt;swClose&gt; &lt;swOpen&gt;
        /// &lt;relay&gt; &lt;next&gt; &lt;elapsed&gt; &lt;err&gt;</c>.
        /// </summary>
        /// <returns><c>true</c> si la trame est conforme ; sinon <c>false</c> et <paramref name="reason"/> est renseigné.</returns>
        internal static bool TryParse(string line, out RoofState state, out string reason)
        {
            state = null;
            reason = null;

            if (string.IsNullOrEmpty(line)) { reason = "ligne vide"; return false; }

            string[] t = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (t.Length != FieldCount)
            {
                reason = "attendu " + FieldCount + " champs, reçu " + t.Length;
                return false;
            }
            if (t[0] != "STATE") { reason = "mot-clé « STATE » attendu, reçu « " + t[0] + " »"; return false; }

            RoofPosition pos;
            if (!RoofVocabulary.TryPosition(t[1], out pos)) { reason = "position inconnue « " + t[1] + " »"; return false; }

            RoofMotion motion;
            if (!RoofVocabulary.TryMotion(t[2], out motion)) { reason = "mouvement inconnu « " + t[2] + " »"; return false; }

            bool swClose;
            if (!RoofVocabulary.TryBit(t[3], out swClose)) { reason = "swClose doit valoir 0 ou 1, reçu « " + t[3] + " »"; return false; }

            bool swOpen;
            if (!RoofVocabulary.TryBit(t[4], out swOpen)) { reason = "swOpen doit valoir 0 ou 1, reçu « " + t[4] + " »"; return false; }

            bool relay;
            if (!RoofVocabulary.TryBit(t[5], out relay)) { reason = "relay doit valoir 0 ou 1, reçu « " + t[5] + " »"; return false; }

            RoofNextPulse next;
            if (!RoofVocabulary.TryNextPulse(t[6], out next)) { reason = "next inconnu « " + t[6] + " »"; return false; }

            long elapsed;
            if (!RoofVocabulary.TryInteger(t[7], out elapsed)) { reason = "elapsed non entier « " + t[7] + " »"; return false; }

            RoofErrorCode err;
            if (!RoofVocabulary.TryError(t[8], out err)) { reason = "code d'erreur inconnu « " + t[8] + " »"; return false; }

            state = new RoofState(pos, motion, swClose, swOpen, relay, next, elapsed, err, line);
            return true;
        }
    }

    /// <summary>Image immuable d'une trame <c>DIAG</c> (§6 du protocole).</summary>
    internal sealed class RoofDiagnostics
    {
        private static readonly string[] RequiredKeys =
        {
            "SWC", "SWO", "RELAY", "NEXT", "POS", "MOTION", "ERR", "RUN", "UP", "EE"
        };

        private RoofDiagnostics() { }

        internal bool SwitchClosedRaw { get; private set; }
        internal bool SwitchClosedDebounced { get; private set; }
        internal bool SwitchOpenRaw { get; private set; }
        internal bool SwitchOpenDebounced { get; private set; }
        internal bool RelayActive { get; private set; }
        internal RoofNextPulse NextPulse { get; private set; }
        internal RoofPosition Position { get; private set; }
        internal RoofMotion Motion { get; private set; }
        internal RoofErrorCode Error { get; private set; }
        internal bool MotorAssumedRunning { get; private set; }
        internal long UptimeMs { get; private set; }
        internal RoofEepromStatus Eeprom { get; private set; }
        internal string RawLine { get; private set; }

        /// <summary>
        /// Décodage strict de
        /// <c>DIAG SWC=&lt;raw&gt;/&lt;deb&gt; SWO=&lt;raw&gt;/&lt;deb&gt; RELAY=… NEXT=… POS=… MOTION=… ERR=… RUN=… UP=… EE=…</c>.
        /// </summary>
        internal static bool TryParse(string line, out RoofDiagnostics diag, out string reason)
        {
            diag = null;
            reason = null;

            if (string.IsNullOrEmpty(line)) { reason = "ligne vide"; return false; }

            string[] t = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (t.Length != RequiredKeys.Length + 1) { reason = "attendu " + (RequiredKeys.Length + 1) + " champs, reçu " + t.Length; return false; }
            if (t[0] != "DIAG") { reason = "mot-clé « DIAG » attendu, reçu « " + t[0] + " »"; return false; }

            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            for (int i = 1; i < t.Length; i++)
            {
                int eq = t[i].IndexOf('=');
                if (eq <= 0 || eq == t[i].Length - 1) { reason = "champ « " + t[i] + " » n'est pas de la forme CLÉ=VALEUR"; return false; }
                string key = t[i].Substring(0, eq);
                if (map.ContainsKey(key)) { reason = "clé « " + key + " » présente deux fois"; return false; }
                map[key] = t[i].Substring(eq + 1);
            }
            foreach (string k in RequiredKeys)
            {
                if (!map.ContainsKey(k)) { reason = "clé « " + k + " » absente"; return false; }
            }

            var result = new RoofDiagnostics();

            bool raw, deb;
            if (!TrySensorPair(map["SWC"], out raw, out deb)) { reason = "SWC doit être de la forme 0/1"; return false; }
            result.SwitchClosedRaw = raw;
            result.SwitchClosedDebounced = deb;

            if (!TrySensorPair(map["SWO"], out raw, out deb)) { reason = "SWO doit être de la forme 0/1"; return false; }
            result.SwitchOpenRaw = raw;
            result.SwitchOpenDebounced = deb;

            bool bit;
            if (!RoofVocabulary.TryBit(map["RELAY"], out bit)) { reason = "RELAY doit valoir 0 ou 1"; return false; }
            result.RelayActive = bit;

            if (!RoofVocabulary.TryBit(map["RUN"], out bit)) { reason = "RUN doit valoir 0 ou 1"; return false; }
            result.MotorAssumedRunning = bit;

            RoofNextPulse next;
            if (!RoofVocabulary.TryNextPulse(map["NEXT"], out next)) { reason = "NEXT inconnu « " + map["NEXT"] + " »"; return false; }
            result.NextPulse = next;

            RoofPosition pos;
            if (!RoofVocabulary.TryPosition(map["POS"], out pos)) { reason = "POS inconnue « " + map["POS"] + " »"; return false; }
            result.Position = pos;

            RoofMotion motion;
            if (!RoofVocabulary.TryMotion(map["MOTION"], out motion)) { reason = "MOTION inconnu « " + map["MOTION"] + " »"; return false; }
            result.Motion = motion;

            RoofErrorCode err;
            if (!RoofVocabulary.TryError(map["ERR"], out err)) { reason = "ERR inconnu « " + map["ERR"] + " »"; return false; }
            result.Error = err;

            long up;
            if (!RoofVocabulary.TryInteger(map["UP"], out up)) { reason = "UP non entier « " + map["UP"] + " »"; return false; }
            result.UptimeMs = up;

            RoofEepromStatus ee;
            if (!RoofVocabulary.TryEeprom(map["EE"], out ee)) { reason = "EE inconnu « " + map["EE"] + " »"; return false; }
            result.Eeprom = ee;

            result.RawLine = line;
            diag = result;
            return true;
        }

        private static bool TrySensorPair(string token, out bool raw, out bool debounced)
        {
            raw = false;
            debounced = false;
            int slash = token.IndexOf('/');
            if (slash <= 0 || slash == token.Length - 1) return false;
            return RoofVocabulary.TryBit(token.Substring(0, slash), out raw)
                && RoofVocabulary.TryBit(token.Substring(slash + 1), out debounced);
        }

        /// <summary>Un écart raw/debounced persistant trahit un capteur qui vibre ou un câble parasité.</summary>
        internal bool HasSensorJitter
        {
            get { return SwitchClosedRaw != SwitchClosedDebounced || SwitchOpenRaw != SwitchOpenDebounced; }
        }
    }

    /// <summary>Image immuable d'une trame <c>CFG</c> (réponse à <c>CFG?</c>, §6 du protocole).</summary>
    internal sealed class RoofConfig
    {
        private static readonly string[] RequiredKeys = { "PULSE", "INTER", "START", "TRAVEL", "DEBOUNCE" };

        private RoofConfig() { }

        internal long PulseMs { get; private set; }
        internal long InterPulseMs { get; private set; }
        internal long StartTimeoutMs { get; private set; }
        internal long TravelTimeoutMs { get; private set; }
        internal long DebounceMs { get; private set; }
        internal string RawLine { get; private set; }

        internal static bool TryParse(string line, out RoofConfig config, out string reason)
        {
            config = null;
            reason = null;

            if (string.IsNullOrEmpty(line)) { reason = "ligne vide"; return false; }

            string[] t = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (t.Length != RequiredKeys.Length + 1) { reason = "attendu " + (RequiredKeys.Length + 1) + " champs, reçu " + t.Length; return false; }
            if (t[0] != "CFG") { reason = "mot-clé « CFG » attendu, reçu « " + t[0] + " »"; return false; }

            var map = new Dictionary<string, long>(StringComparer.Ordinal);
            for (int i = 1; i < t.Length; i++)
            {
                int eq = t[i].IndexOf('=');
                if (eq <= 0 || eq == t[i].Length - 1) { reason = "champ « " + t[i] + " » n'est pas de la forme CLÉ=VALEUR"; return false; }
                string key = t[i].Substring(0, eq);
                long value;
                if (!RoofVocabulary.TryInteger(t[i].Substring(eq + 1), out value)) { reason = "valeur non entière pour « " + key + " »"; return false; }
                if (map.ContainsKey(key)) { reason = "clé « " + key + " » présente deux fois"; return false; }
                map[key] = value;
            }
            foreach (string k in RequiredKeys)
            {
                if (!map.ContainsKey(k)) { reason = "clé « " + k + " » absente"; return false; }
            }

            config = new RoofConfig
            {
                PulseMs = map["PULSE"],
                InterPulseMs = map["INTER"],
                StartTimeoutMs = map["START"],
                TravelTimeoutMs = map["TRAVEL"],
                DebounceMs = map["DEBOUNCE"],
                RawLine = line
            };
            return true;
        }
    }

    /// <summary>
    /// Instantané immuable publié par <see cref="RoofLink"/> et consommé par le driver et le
    /// panneau de test. Toutes les propriétés ASCOM lisent cet objet, jamais le port série.
    /// </summary>
    internal sealed class RoofSnapshot
    {
        internal RoofSnapshot(bool linkUp, RoofState state, int consecutiveFailures, string lastIssue,
                              string portName, string protocolVersion, string firmwareVersion, string configWarning)
        {
            LinkUp = linkUp;
            State = state;
            ConsecutiveFailures = consecutiveFailures;
            LastIssue = lastIssue;
            PortName = portName;
            ProtocolVersion = protocolVersion;
            FirmwareVersion = firmwareVersion;
            ConfigWarning = configWarning;
            UpdatedUtc = DateTime.UtcNow;
        }

        /// <summary>Liaison série considérée comme vivante (moins de 4 échecs consécutifs).</summary>
        internal bool LinkUp { get; private set; }

        /// <summary>Dernière trame STATE décodée, ou <c>null</c> si aucune n'a jamais été reçue.</summary>
        internal RoofState State { get; private set; }

        internal int ConsecutiveFailures { get; private set; }
        internal string LastIssue { get; private set; }
        internal string PortName { get; private set; }
        internal string ProtocolVersion { get; private set; }
        internal string FirmwareVersion { get; private set; }

        /// <summary>Avertissement éventuel émis lors de la poussée des CFG à la connexion.</summary>
        internal string ConfigWarning { get; private set; }

        internal DateTime UpdatedUtc { get; private set; }

        /// <summary>Instantané « port fermé » utilisé avant toute connexion et après déconnexion.</summary>
        internal static RoofSnapshot Down(string portName, string lastIssue)
        {
            return new RoofSnapshot(false, null, 0, lastIssue, portName, null, null, null);
        }
    }
}
