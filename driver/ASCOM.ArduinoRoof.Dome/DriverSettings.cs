using System;
using System.Globalization;
using ASCOM.Utilities;

namespace ASCOM.ArduinoRoof
{
    /// <summary>
    /// Réglages persistés dans le Profile ASCOM sous le ProgID du driver.
    /// <para>
    /// Cinq temporisations sont poussées vers le firmware par des commandes <c>CFG</c> à chaque
    /// connexion (§6 du protocole) : PULSE, INTER, START, TRAVEL, DEBOUNCE. Les bornes appliquées
    /// ici sont <b>exactement</b> celles du tableau §6, de sorte qu'un <c>NAK CFG OUT_OF_RANGE</c>
    /// ne puisse pas être provoqué par le driver.
    /// </para>
    /// <para>
    /// Les <b>défauts</b> de PULSE et INTER (1000 ms) diffèrent volontairement de ceux du firmware
    /// (600 ms) : le portail de l'observatoire ignore une impulsion de 600 ms (recette du 06/10/2026).
    /// Comme le driver pousse ses valeurs à chaque connexion, ce sont ses défauts qui comptent quand
    /// le Profile est réinitialisé ; ceux du firmware ne servent qu'au moniteur série.
    /// </para>
    /// </summary>
    internal sealed class DriverSettings
    {
        // --- Bornes du protocole, §6 ---
        internal const int PulseMin = 100, PulseMax = 2000, PulseDefault = 1000;
        internal const int InterMin = 200, InterMax = 5000, InterDefault = 1000;
        internal const int StartMin = 1000, StartMax = 30000, StartDefault = 5000;
        internal const int TravelMin = 5000, TravelMax = 300000, TravelDefault = 60000;
        internal const int DebounceMin = 5, DebounceMax = 200, DebounceDefault = 50;

        // --- Borne propre au driver : période d'interrogation STATE (§6 : 1000 ms par défaut) ---
        internal const int PollMin = 200, PollMax = 10000, PollDefault = 1000;

        // --- Reprises automatiques (§8.6) ---
        // Trois reprises couvrent le pire cas réaliste d'une fermeture : erreur déjà présente,
        // RECOVER parti dans le mauvais sens, puis course normale depuis la butée opposée.
        internal const int RetryMin = 0, RetryMax = 5, RetryDefault = 3;

        internal const string ComPortDefault = "COM3";

        // Noms des valeurs dans le Profile ASCOM.
        private const string KeyComPort = "COM Port";
        private const string KeyPulse = "Pulse ms";
        private const string KeyInter = "Inter pulse ms";
        private const string KeyStart = "Start timeout ms";
        private const string KeyTravel = "Travel timeout ms";
        private const string KeyDebounce = "Debounce ms";
        private const string KeyPoll = "Poll period ms";
        private const string KeyTrace = "Trace";
        private const string KeyIntermediateOpen = "Intermediate reported as open";
        private const string KeyRetryCount = "Retry count";
        private const string KeyAutoRecover = "Auto recover";

        internal DriverSettings()
        {
            ComPort = ComPortDefault;
            PulseMs = PulseDefault;
            InterPulseMs = InterDefault;
            StartTimeoutMs = StartDefault;
            TravelTimeoutMs = TravelDefault;
            DebounceMs = DebounceDefault;
            PollPeriodMs = PollDefault;
            TraceEnabled = false;
            IntermediateReportedAsOpen = false;
            RetryCount = RetryDefault;
            AutoRecover = true;
        }

        /// <summary>Port série du contrôleur (ex. « COM4 »).</summary>
        internal string ComPort { get; set; }

        /// <summary>CFG PULSE — durée de fermeture du contact relais, ms.</summary>
        internal int PulseMs { get; set; }

        /// <summary>CFG INTER — délai entre la fin d'une impulsion et le début de la suivante, ms.</summary>
        internal int InterPulseMs { get; set; }

        /// <summary>CFG START — délai max pour que le capteur de départ se relâche, ms.</summary>
        internal int StartTimeoutMs { get; set; }

        /// <summary>CFG TRAVEL — durée max d'une course complète, ms.</summary>
        internal int TravelTimeoutMs { get; set; }

        /// <summary>CFG DEBOUNCE — anti-rebond des capteurs, ms.</summary>
        internal int DebounceMs { get; set; }

        /// <summary>Période d'interrogation <c>STATE</c>. Réglage <b>driver</b>, jamais envoyé au firmware.</summary>
        internal int PollPeriodMs { get; set; }

        /// <summary>Journalisation ASCOM TraceLogger.</summary>
        internal bool TraceEnabled { get; set; }

        /// <summary>
        /// Si vrai, <c>pos=INTERMEDIATE</c> + <c>motion=IDLE</c> est rapporté <c>shutterOpen</c>
        /// au lieu de <c>shutterError</c>. Défaut : faux — un toit non fermé ne doit jamais être
        /// annoncé comme fermé, et « mi-course » n'existe pas dans la norme ASCOM.
        /// </summary>
        internal bool IntermediateReportedAsOpen { get; set; }

        /// <summary>
        /// Nombre de reprises automatiques par ouverture ou fermeture : après une erreur, le driver
        /// acquitte, attend que le toit soit immobile et relance la course. 0 = comportement d'origine,
        /// la première erreur est rapportée telle quelle.
        /// </summary>
        internal int RetryCount { get; set; }

        /// <summary>
        /// Autorise la reprise depuis une position inconnue par <c>RECOVER</c> (toit bloqué à
        /// mi-course, coupure secteur). Le toit peut alors parcourir une course complète dans le sens
        /// non désiré avant de repartir dans le bon : c'est le prix d'une fermeture garantie sans
        /// intervention.
        /// </summary>
        internal bool AutoRecover { get; set; }

        /// <summary>Lit les réglages depuis le Profile ASCOM ; toute valeur absente ou illisible retombe sur le défaut.</summary>
        internal static DriverSettings Load()
        {
            var s = new DriverSettings();
            try
            {
                using (var profile = new Profile())
                {
                    profile.DeviceType = "Dome";
                    s.ComPort = ReadString(profile, KeyComPort, ComPortDefault);
                    s.PulseMs = ReadInt(profile, KeyPulse, PulseDefault);
                    s.InterPulseMs = ReadInt(profile, KeyInter, InterDefault);
                    s.StartTimeoutMs = ReadInt(profile, KeyStart, StartDefault);
                    s.TravelTimeoutMs = ReadInt(profile, KeyTravel, TravelDefault);
                    s.DebounceMs = ReadInt(profile, KeyDebounce, DebounceDefault);
                    s.PollPeriodMs = ReadInt(profile, KeyPoll, PollDefault);
                    s.TraceEnabled = ReadBool(profile, KeyTrace, false);
                    s.IntermediateReportedAsOpen = ReadBool(profile, KeyIntermediateOpen, false);
                    s.RetryCount = ReadInt(profile, KeyRetryCount, RetryDefault);
                    s.AutoRecover = ReadBool(profile, KeyAutoRecover, true);
                }
            }
            catch (Exception)
            {
                // Profile inaccessible (driver pas encore enregistré, par exemple) : valeurs par défaut.
            }
            s.Clamp();
            return s;
        }

        /// <summary>Écrit les réglages dans le Profile ASCOM.</summary>
        internal void Save()
        {
            Clamp();
            using (var profile = new Profile())
            {
                profile.DeviceType = "Dome";
                profile.WriteValue(Dome.DriverProgId, KeyComPort, ComPort);
                profile.WriteValue(Dome.DriverProgId, KeyPulse, PulseMs.ToString(CultureInfo.InvariantCulture));
                profile.WriteValue(Dome.DriverProgId, KeyInter, InterPulseMs.ToString(CultureInfo.InvariantCulture));
                profile.WriteValue(Dome.DriverProgId, KeyStart, StartTimeoutMs.ToString(CultureInfo.InvariantCulture));
                profile.WriteValue(Dome.DriverProgId, KeyTravel, TravelTimeoutMs.ToString(CultureInfo.InvariantCulture));
                profile.WriteValue(Dome.DriverProgId, KeyDebounce, DebounceMs.ToString(CultureInfo.InvariantCulture));
                profile.WriteValue(Dome.DriverProgId, KeyPoll, PollPeriodMs.ToString(CultureInfo.InvariantCulture));
                profile.WriteValue(Dome.DriverProgId, KeyTrace, TraceEnabled.ToString(CultureInfo.InvariantCulture));
                profile.WriteValue(Dome.DriverProgId, KeyIntermediateOpen, IntermediateReportedAsOpen.ToString(CultureInfo.InvariantCulture));
                profile.WriteValue(Dome.DriverProgId, KeyRetryCount, RetryCount.ToString(CultureInfo.InvariantCulture));
                profile.WriteValue(Dome.DriverProgId, KeyAutoRecover, AutoRecover.ToString(CultureInfo.InvariantCulture));
            }
        }

        /// <summary>Écrit les valeurs par défaut à l'enregistrement COM, pour qu'elles soient visibles dans le Profile Explorer.</summary>
        internal static void WriteDefaultsIfMissing()
        {
            var defaults = new DriverSettings();
            try
            {
                using (var profile = new Profile())
                {
                    profile.DeviceType = "Dome";
                    if (string.IsNullOrEmpty(profile.GetValue(Dome.DriverProgId, KeyComPort, string.Empty, string.Empty)))
                    {
                        defaults.Save();
                    }
                }
            }
            catch (Exception)
            {
                // L'enregistrement COM ne doit jamais échouer à cause du Profile.
            }
        }

        /// <summary>Recopie l'intégralité des réglages depuis une autre instance (l'objet partagé conserve son identité).</summary>
        internal void CopyFrom(DriverSettings other)
        {
            if (other == null) return;
            ComPort = other.ComPort;
            PulseMs = other.PulseMs;
            InterPulseMs = other.InterPulseMs;
            StartTimeoutMs = other.StartTimeoutMs;
            TravelTimeoutMs = other.TravelTimeoutMs;
            DebounceMs = other.DebounceMs;
            PollPeriodMs = other.PollPeriodMs;
            TraceEnabled = other.TraceEnabled;
            IntermediateReportedAsOpen = other.IntermediateReportedAsOpen;
            RetryCount = other.RetryCount;
            AutoRecover = other.AutoRecover;
        }

        internal DriverSettings Clone()
        {
            var copy = new DriverSettings();
            copy.CopyFrom(this);
            return copy;
        }

        /// <summary>Ramène toutes les valeurs dans les bornes du protocole (§6).</summary>
        internal void Clamp()
        {
            if (string.IsNullOrWhiteSpace(ComPort)) ComPort = ComPortDefault;
            ComPort = ComPort.Trim();
            PulseMs = Bound(PulseMs, PulseMin, PulseMax);
            InterPulseMs = Bound(InterPulseMs, InterMin, InterMax);
            StartTimeoutMs = Bound(StartTimeoutMs, StartMin, StartMax);
            TravelTimeoutMs = Bound(TravelTimeoutMs, TravelMin, TravelMax);
            DebounceMs = Bound(DebounceMs, DebounceMin, DebounceMax);
            PollPeriodMs = Bound(PollPeriodMs, PollMin, PollMax);
            RetryCount = Bound(RetryCount, RetryMin, RetryMax);
        }

        /// <summary>Résumé sur une ligne, pour le journal.</summary>
        internal string Describe()
        {
            return string.Format(CultureInfo.InvariantCulture,
                "port={0} PULSE={1} INTER={2} START={3} TRAVEL={4} DEBOUNCE={5} poll={6}ms trace={7} intermediaireOuvert={8} reprises={9} recoverAuto={10}",
                ComPort, PulseMs, InterPulseMs, StartTimeoutMs, TravelTimeoutMs, DebounceMs,
                PollPeriodMs, TraceEnabled, IntermediateReportedAsOpen, RetryCount, AutoRecover);
        }

        private static int Bound(int value, int min, int max)
        {
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }

        private static string ReadString(Profile profile, string key, string fallback)
        {
            string raw = profile.GetValue(Dome.DriverProgId, key, string.Empty, fallback);
            return string.IsNullOrWhiteSpace(raw) ? fallback : raw.Trim();
        }

        private static int ReadInt(Profile profile, string key, int fallback)
        {
            string raw = profile.GetValue(Dome.DriverProgId, key, string.Empty, fallback.ToString(CultureInfo.InvariantCulture));
            int value;
            return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out value) ? value : fallback;
        }

        private static bool ReadBool(Profile profile, string key, bool fallback)
        {
            string raw = profile.GetValue(Dome.DriverProgId, key, string.Empty, fallback.ToString(CultureInfo.InvariantCulture));
            bool value;
            return bool.TryParse(raw, out value) ? value : fallback;
        }
    }
}
