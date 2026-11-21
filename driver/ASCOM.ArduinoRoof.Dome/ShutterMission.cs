using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace ASCOM.ArduinoRoof
{
    /// <summary>Position visée par une mission.</summary>
    internal enum MissionTarget
    {
        Open,
        Closed
    }

    /// <summary>Issue d'une mission.</summary>
    internal enum MissionOutcome
    {
        Running,
        Succeeded,
        Failed,
        Cancelled
    }

    /// <summary>Image immuable d'une mission, pour l'action <c>Roof:Mission</c> et le panneau de test.</summary>
    internal sealed class MissionReport
    {
        internal MissionReport(MissionTarget target, MissionOutcome outcome, int retriesUsed, int retriesAllowed,
                               RoofErrorCode lastError, string message, DateTime startedUtc, DateTime? endedUtc,
                               string history)
        {
            Target = target;
            Outcome = outcome;
            RetriesUsed = retriesUsed;
            RetriesAllowed = retriesAllowed;
            LastError = lastError;
            Message = message;
            StartedUtc = startedUtc;
            EndedUtc = endedUtc;
            History = history;
        }

        internal MissionTarget Target { get; private set; }
        internal MissionOutcome Outcome { get; private set; }
        internal int RetriesUsed { get; private set; }
        internal int RetriesAllowed { get; private set; }
        internal RoofErrorCode LastError { get; private set; }
        internal string Message { get; private set; }
        internal DateTime StartedUtc { get; private set; }
        internal DateTime? EndedUtc { get; private set; }
        internal string History { get; private set; }

        /// <summary>Résumé sur une ligne.</summary>
        internal string Summary
        {
            get
            {
                DateTime end = EndedUtc ?? DateTime.UtcNow;
                return string.Format(CultureInfo.InvariantCulture,
                    "{0} {1} reprises={2}/{3} erreur={4} durée={5:0}s — {6}",
                    Target == MissionTarget.Open ? "OUVERTURE" : "FERMETURE", Outcome, RetriesUsed, RetriesAllowed,
                    LastError, (end - StartedUtc).TotalSeconds, Message);
            }
        }
    }

    /// <summary>
    /// Moteur de missions : amène le toit en position ouverte ou fermée, avec <b>reprise automatique</b>
    /// en cas d'échec (§8.6 du protocole).
    /// <para>
    /// Sans lui, la moindre erreur du firmware (impulsion ignorée par le portail, course trop lente,
    /// capteur qui frémit) bloquait tout mouvement jusqu'à un <c>CLEARERR</c> que NINA ne sait pas
    /// envoyer : le toit restait ouvert jusqu'au matin. La mission acquitte l'erreur, attend que le
    /// toit soit immobile, puis relance la course — jusqu'à <see cref="DriverSettings.RetryCount"/> fois.
    /// </para>
    /// <para>
    /// Machine à états non bloquante. Une première étape s'exécute dans <c>OpenShutter</c> /
    /// <c>CloseShutter</c> (la commande part avant le retour, une erreur immédiate est levée) ; les
    /// suivantes à chaque tour de la tâche de polling de <see cref="RoofLink"/>, sur une trame
    /// <c>STATE</c> fraîche. Tant qu'une mission est active, <c>ShutterStatus</c> rapporte
    /// <c>shutterOpening</c> / <c>shutterClosing</c>, reprises comprises : NINA continue d'attendre.
    /// </para>
    /// <para>Ce qui n'est jamais repris automatiquement :</para>
    /// <list type="bullet">
    ///   <item><c>BOTH_SENSORS</c> — défaut de câblage, plus rien n'est fiable ;</item>
    ///   <item>la perte de liaison — §8.4, aucun mouvement correctif après une reconnexion ;</item>
    ///   <item>une course interrompue par une autre commande (<c>AbortSlew</c>, panneau de test…).</item>
    /// </list>
    /// </summary>
    internal sealed class ShutterSupervisor
    {
        /// <summary>Durée maximale pendant laquelle un <c>NAK BUSY</c> est réessayé (fin d'impulsion, INTER…).</summary>
        private const int BusyTimeoutMs = 15000;

        /// <summary>Un capteur actif doit être stable depuis ce délai avant une reprise : le toit est immobile.</summary>
        private const int SettleStableMs = 2000;

        /// <summary>Délai minimal d'attente d'une fin de course quand aucune n'est active après une erreur.</summary>
        private const int MinGraceMs = 10000;

        private const int DeadlineMarginMs = 10000;
        private const int HistoryMax = 30;

        private enum Phase
        {
            /// <summary>Émettre (ou réémettre) la commande de course.</summary>
            Command,
            /// <summary>Course en cours, suivie par STATE.</summary>
            Moving,
            /// <summary>Erreur constatée : attendre que le toit soit immobile avant d'acquitter.</summary>
            Settle
        }

        private sealed class Mission
        {
            internal Mission(MissionTarget target, DateTime nowUtc, DateTime deadlineUtc)
            {
                Target = target;
                StartedUtc = nowUtc;
                DeadlineUtc = deadlineUtc;
                PhaseSinceUtc = nowUtc;
                BusySinceUtc = DateTime.MinValue;
                Outcome = MissionOutcome.Running;
                Message = "en cours";
            }

            internal readonly MissionTarget Target;
            internal readonly DateTime StartedUtc;
            internal readonly DateTime DeadlineUtc;
            internal readonly List<string> History = new List<string>();

            internal Phase Phase;
            internal DateTime PhaseSinceUtc;
            internal DateTime BusySinceUtc;
            internal bool AbortSent;
            internal int RetriesUsed;
            internal RoofErrorCode LastError;
            internal bool SwitchClosed;
            internal bool SwitchOpen;
            internal DateTime SensorsSinceUtc;
            internal long LastSequence;
            internal MissionOutcome Outcome;
            internal string Message;
        }

        private readonly object _lock = new object();
        private readonly RoofLink _link;
        private readonly DriverSettings _settings;
        private readonly DriverLogger _log;

        private volatile Mission _current;
        private volatile MissionReport _lastReport;

        internal ShutterSupervisor(RoofLink link, DriverSettings settings, DriverLogger log)
        {
            _link = link;
            _settings = settings;
            _log = log;
            _link.TickHandler = OnTick;
            _link.ClosingHandler = delegate { Cancel("liaison fermée"); };
        }

        /// <summary>Cible de la mission en cours, ou <c>null</c>. Lecture sans verrou, pour <c>ShutterStatus</c>.</summary>
        internal MissionTarget? ActiveTarget
        {
            get
            {
                Mission m = _current;
                return m == null ? (MissionTarget?)null : m.Target;
            }
        }

        /// <summary>Description de la mission en cours, ou de la dernière terminée.</summary>
        internal string Describe(bool withHistory)
        {
            lock (_lock)
            {
                MissionReport report = _current != null ? BuildReport(_current, null) : _lastReport;
                string text;
                if (report == null)
                {
                    text = "aucune mission depuis la connexion";
                }
                else
                {
                    text = (_current != null ? "EN COURS " : "DERNIÈRE ") + report.Summary;
                    if (withHistory && !string.IsNullOrEmpty(report.History)) text += " | " + report.History;
                }

                string startup = _link.StartupWarning;
                if (withHistory && startup != null) text += " | CONNEXION " + startup;
                return text;
            }
        }

        // ==================================================================
        // Démarrage / annulation
        // ==================================================================

        /// <summary>
        /// Démarre une mission (<c>OpenShutter</c> / <c>CloseShutter</c>). Rend la main dès que la
        /// première commande est partie, avec un instantané à jour.
        /// </summary>
        /// <exception cref="ASCOM.InvalidOperationException">La mission échoue d'emblée (erreur non reprise, reprises désactivées…).</exception>
        /// <exception cref="ASCOM.NotConnectedException">Liaison indisponible.</exception>
        internal void Start(MissionTarget target, string origin)
        {
            lock (_lock)
            {
                Mission previous = _current;
                if (previous != null)
                {
                    if (previous.Target == target)
                    {
                        // Exigence ASCOM : une demande identique pendant la course est acceptée sans erreur.
                        Record(previous, origin + " : mission identique déjà en cours, rien à faire");
                        return;
                    }
                    End(previous, MissionOutcome.Cancelled, "remplacée par une demande " + Label(target) + " (" + origin + ")");
                }

                RoofSnapshot snapshot = _link.PollNow();

                DateTime now = DateTime.UtcNow;
                var mission = new Mission(target, now, now.AddMilliseconds(DeadlineMs()));
                _current = mission;
                _link.FastPolling = true;
                Record(mission, "mission " + Label(target) + " démarrée (" + origin + "), reprises autorisées : "
                                + _settings.RetryCount + ", RECOVER automatique : " + (_settings.AutoRecover ? "oui" : "non")
                                + " — " + snapshot.State.RawLine);

                try
                {
                    Step(mission, snapshot.State, now);
                }
                catch (Exception ex)
                {
                    if (mission.Outcome == MissionOutcome.Running)
                        End(mission, MissionOutcome.Failed, "commande impossible : " + ex.Message);
                    throw;
                }

                if (mission.Outcome == MissionOutcome.Failed)
                    throw new ASCOM.InvalidOperationException(mission.Message);
            }
        }

        /// <summary>Abandonne la mission en cours (AbortSlew, déconnexion…). Aucune commande n'est émise ici.</summary>
        internal void Cancel(string reason)
        {
            lock (_lock)
            {
                Mission m = _current;
                if (m != null) End(m, MissionOutcome.Cancelled, reason);
            }
        }

        // ==================================================================
        // Tâche de fond
        // ==================================================================

        private void OnTick(RoofSnapshot ignored)
        {
            if (_current == null) return;
            lock (_lock)
            {
                Mission m = _current;
                if (m == null) return;

                // Instantané relu APRÈS la prise du verrou : celui reçu en paramètre a pu être publié
                // avant la dernière commande de la mission (OpenShutter tenait le verrou entre-temps).
                RoofSnapshot snapshot = _link.Snapshot;

                if (snapshot == null || !snapshot.LinkUp)
                {
                    // §8.4 : la liaison est perdue, plus rien n'est commandé. La course déjà lancée va
                    // à son terme (le portail est autonome) mais rien ne sera repris à la reconnexion.
                    End(m, MissionOutcome.Failed, "liaison perdue pendant la mission — aucune reprise automatique (§8.4)");
                    return;
                }

                RoofState state = snapshot.State;
                if (state == null || state.Sequence <= m.LastSequence) return;   // pas de trame neuve

                try
                {
                    Step(m, state, DateTime.UtcNow);
                }
                catch (Exception ex)
                {
                    // Transaction ratée (délai, port occupé) : la mission continue, l'étape sera rejouée
                    // sur la prochaine trame STATE. Le délai global borne l'ensemble.
                    Record(m, "commande en échec, nouvel essai au prochain tour : " + ex.Message);
                }
            }
        }

        // ==================================================================
        // Machine à états — appelée verrou de mission tenu
        // ==================================================================

        private void Step(Mission m, RoofState s, DateTime now)
        {
            m.LastSequence = s.Sequence;

            if (now > m.DeadlineUtc)
            {
                End(m, MissionOutcome.Failed, "délai global de la mission dépassé (" + (DeadlineMs() / 1000) +
                                              " s) — dernier état : " + s.RawLine);
                return;
            }

            if (s.Motion == RoofMotion.Idle && s.Position == TargetPosition(m.Target))
            {
                End(m, MissionOutcome.Succeeded, m.RetriesUsed == 0
                    ? "toit " + Reached(m.Target)
                    : "toit " + Reached(m.Target) + " après " + m.RetriesUsed + " reprise(s)");
                return;
            }

            switch (m.Phase)
            {
                case Phase.Command:
                    StepCommand(m, s, now);
                    break;
                case Phase.Moving:
                    StepMoving(m, s, now);
                    break;
                default:
                    StepSettle(m, s, now);
                    break;
            }
        }

        private void StepCommand(Mission m, RoofState s, DateTime now)
        {
            if (s.Motion == Toward(m.Target))
            {
                SetPhase(m, Phase.Moving, now);
                Record(m, "course en cours — " + s.RawLine);
                return;
            }

            if (s.Motion == Away(m.Target))
            {
                // Mouvement dans le sens opposé (fermeture demandée pendant une ouverture, par exemple) :
                // on l'arrête, puis la commande partira dès que le contrôleur sera libre.
                if (!m.AbortSent)
                {
                    string abort = _link.Transact("ABORT");
                    m.AbortSent = true;
                    Record(m, "mouvement en sens inverse, arrêt préalable : ABORT → " + abort);
                    RefreshQuietly(m);
                }
                return;
            }

            if (s.Motion == RoofMotion.Error)
            {
                EnterSettle(m, s, now);
                return;
            }

            string command;
            if (s.Position == RoofPosition.Unknown)
            {
                if (!_settings.AutoRecover)
                {
                    End(m, MissionOutcome.Failed,
                        "position du toit inconnue et reprise automatique (RECOVER) désactivée dans la configuration : " +
                        "utilisez l'action Roof:Recover sous surveillance visuelle.");
                    return;
                }
                command = "RECOVER " + Verb(m.Target);
            }
            else
            {
                command = Verb(m.Target);
            }

            string response = _link.Transact(command);

            if (response.StartsWith("ACK", StringComparison.Ordinal))
            {
                Record(m, command + " → " + response);
                m.BusySinceUtc = DateTime.MinValue;
                m.AbortSent = false;
                if (!response.EndsWith(" ALREADY", StringComparison.Ordinal)) SetPhase(m, Phase.Moving, now);
                RefreshQuietly(m);
                return;
            }

            if (response.EndsWith(" BUSY", StringComparison.Ordinal))
            {
                // Typiquement l'impulsion d'arrêt d'un ABORT pas encore partie (INTER) : on réessaie.
                if (m.BusySinceUtc == DateTime.MinValue)
                {
                    m.BusySinceUtc = now;
                    Record(m, command + " → " + response + " : nouvel essai au prochain tour");
                }
                else if ((now - m.BusySinceUtc).TotalMilliseconds > BusyTimeoutMs)
                {
                    End(m, MissionOutcome.Failed, "le contrôleur reste occupé depuis " + (BusyTimeoutMs / 1000) +
                                                  " s (« " + response + " ») : " + ExplainNak(response));
                }
                return;
            }

            if (response.EndsWith(" ERROR", StringComparison.Ordinal)
                || response.EndsWith(" UNKNOWN_POSITION", StringComparison.Ordinal)
                || response.EndsWith(" NOT_NEEDED", StringComparison.Ordinal))
            {
                // L'état a changé entre la lecture et la commande : réévaluation sur la prochaine trame.
                Record(m, command + " → " + response + " : état modifié entre-temps, réévaluation");
                RefreshQuietly(m);
                return;
            }

            End(m, MissionOutcome.Failed, "le contrôleur a refusé « " + command + " » (« " + response + " ») : " + ExplainNak(response));
        }

        private void StepMoving(Mission m, RoofState s, DateTime now)
        {
            if (s.Motion == Toward(m.Target)) return;

            if (s.Motion == RoofMotion.Error)
            {
                EnterSettle(m, s, now);
                return;
            }

            // À l'arrêt hors cible, ou repartie dans l'autre sens : quelqu'un d'autre a commandé le toit.
            End(m, MissionOutcome.Cancelled, "course interrompue par une autre commande (" + s.RawLine + ") — mission abandonnée");
        }

        private void EnterSettle(Mission m, RoofState s, DateTime now)
        {
            m.LastError = s.Error;

            if (s.Error == RoofErrorCode.BothSensors)
            {
                End(m, MissionOutcome.Failed,
                    "les deux fins de course sont actives (BOTH_SENSORS) : défaut de câblage ou de capteur, " +
                    "aucune reprise automatique. Vérifiez les capteurs avant d'acquitter avec Roof:ClearError.");
                return;
            }

            if (m.RetriesUsed >= _settings.RetryCount)
            {
                string reason = _settings.RetryCount == 0
                    ? "erreur active sur le contrôleur et reprise automatique désactivée : "
                    : "échec après " + m.RetriesUsed + " reprise(s) automatique(s) : ";
                End(m, MissionOutcome.Failed, reason + RoofVocabulary.Describe(s.Error) +
                                              ". Diagnostiquez avec l'action Roof:Diag puis acquittez avec Roof:ClearError.");
                return;
            }

            m.RetriesUsed++;
            SetPhase(m, Phase.Settle, now);
            m.SwitchClosed = s.SwitchClosed;
            m.SwitchOpen = s.SwitchOpen;
            m.SensorsSinceUtc = now;
            m.AbortSent = false;
            m.BusySinceUtc = DateTime.MinValue;
            Record(m, "reprise " + m.RetriesUsed + "/" + _settings.RetryCount + " après " + s.Error +
                      " — attente de l'immobilisation du toit (" + s.RawLine + ")", true);
        }

        private void StepSettle(Mission m, RoofState s, DateTime now)
        {
            if (s.Motion != RoofMotion.Error)
            {
                // Erreur acquittée par ailleurs : on reprend directement.
                SetPhase(m, Phase.Command, now);
                StepCommand(m, s, now);
                return;
            }

            if (s.SwitchClosed != m.SwitchClosed || s.SwitchOpen != m.SwitchOpen)
            {
                m.SwitchClosed = s.SwitchClosed;
                m.SwitchOpen = s.SwitchOpen;
                m.SensorsSinceUtc = now;
            }

            bool targetSensor = m.Target == MissionTarget.Open ? s.SwitchOpen : s.SwitchClosed;
            bool anySensor = s.SwitchClosed || s.SwitchOpen;
            double stableMs = (now - m.SensorsSinceUtc).TotalMilliseconds;
            double waitedMs = (now - m.PhaseSinceUtc).TotalMilliseconds;

            string why;
            if (targetSensor)
                why = "fin de course cible active";
            else if (anySensor && stableMs >= SettleStableMs)
                why = "toit immobile sur une fin de course";
            else if (!anySensor && waitedMs >= GraceMs())
                why = "aucune fin de course atteinte en " + (GraceMs() / 1000) + " s, position perdue";
            else
                return;   // le toit bouge peut-être encore : on attend

            string response = _link.Transact("CLEARERR");
            Record(m, why + " — CLEARERR → " + response, true);
            SetPhase(m, Phase.Command, now);
            RefreshQuietly(m);
        }

        // ==================================================================
        // Utilitaires
        // ==================================================================

        /// <summary>
        /// Publie un état à jour après une commande. Toute trame décodée avant celle-ci est désormais
        /// périmée pour la mission : la prochaine étape portera sur un état postérieur à la commande.
        /// Un échec n'est pas fatal : la tâche de polling relira l'état au tour suivant.
        /// </summary>
        private void RefreshQuietly(Mission m)
        {
            try
            {
                RoofSnapshot fresh = _link.PollNow();
                if (fresh.State != null) m.LastSequence = fresh.State.Sequence - 1;
            }
            catch (Exception ex)
            {
                Record(m, "relecture de l'état impossible : " + ex.Message);
            }
        }

        private static void SetPhase(Mission m, Phase phase, DateTime now)
        {
            m.Phase = phase;
            m.PhaseSinceUtc = now;
        }

        private void End(Mission m, MissionOutcome outcome, string message)
        {
            if (m.Outcome != MissionOutcome.Running) return;
            m.Outcome = outcome;
            m.Message = message;
            Record(m, outcome + " : " + message, outcome != MissionOutcome.Succeeded);
            _lastReport = BuildReport(m, DateTime.UtcNow);
            if (ReferenceEquals(_current, m))
            {
                _current = null;
                _link.FastPolling = false;
            }
        }

        private MissionReport BuildReport(Mission m, DateTime? endedUtc)
        {
            return new MissionReport(m.Target, m.Outcome, m.RetriesUsed, _settings.RetryCount, m.LastError,
                                     m.Message, m.StartedUtc, endedUtc, string.Join(" ; ", m.History.ToArray()));
        }

        private void Record(Mission m, string message, bool issue = false)
        {
            string stamped = string.Format(CultureInfo.InvariantCulture, "+{0:0.0}s {1}",
                                           (DateTime.UtcNow - m.StartedUtc).TotalSeconds, message);
            if (m.History.Count >= HistoryMax) m.History.RemoveAt(1);   // on garde la ligne de démarrage
            m.History.Add(stamped);

            if (issue) _log.LogIssue("Mission", stamped);
            else _log.Log("Mission", stamped);
        }

        /// <summary>Délai d'attente d'une fin de course après une erreur : une course complète, 10 s au moins.</summary>
        private int GraceMs()
        {
            return Math.Max(MinGraceMs, _settings.TravelTimeoutMs);
        }

        /// <summary>Garde-fou global : chaque tentative peut durer START + TRAVEL + attente + marge.</summary>
        private int DeadlineMs()
        {
            int perAttempt = _settings.StartTimeoutMs + _settings.TravelTimeoutMs + GraceMs() + DeadlineMarginMs;
            return (_settings.RetryCount + 1) * perAttempt + BusyTimeoutMs;
        }

        private static RoofPosition TargetPosition(MissionTarget target)
        {
            return target == MissionTarget.Open ? RoofPosition.Open : RoofPosition.Closed;
        }

        private static RoofMotion Toward(MissionTarget target)
        {
            return target == MissionTarget.Open ? RoofMotion.Opening : RoofMotion.Closing;
        }

        private static RoofMotion Away(MissionTarget target)
        {
            return target == MissionTarget.Open ? RoofMotion.Closing : RoofMotion.Opening;
        }

        private static string Verb(MissionTarget target)
        {
            return target == MissionTarget.Open ? "OPEN" : "CLOSE";
        }

        private static string Label(MissionTarget target)
        {
            return target == MissionTarget.Open ? "d'ouverture" : "de fermeture";
        }

        private static string Reached(MissionTarget target)
        {
            return target == MissionTarget.Open ? "ouvert" : "fermé";
        }

        /// <summary>Traduit en français les motifs de refus définis au §6 du protocole.</summary>
        internal static string ExplainNak(string response)
        {
            if (response.EndsWith(" BUSY", StringComparison.Ordinal))
                return "un mouvement est déjà en cours. Attendez la fin de la course ou appelez AbortSlew().";
            if (response.EndsWith(" ERROR", StringComparison.Ordinal))
                return "une erreur est active sur le contrôleur. Diagnostiquez avec l'action Roof:Diag puis acquittez avec Roof:ClearError.";
            if (response.EndsWith(" UNKNOWN_POSITION", StringComparison.Ordinal))
                return "la position du toit est inconnue (automate désynchronisé). Utilisez l'action Roof:Recover " +
                       "avec « open » ou « close », sous surveillance visuelle : le toit peut parcourir une course complète " +
                       "dans le sens non désiré.";
            if (response.EndsWith(" NOT_NEEDED", StringComparison.Ordinal))
                return "la position n'est pas inconnue, aucune reprise n'est nécessaire.";
            if (response.EndsWith(" BAD_ARGS", StringComparison.Ordinal))
                return "argument absent ou invalide.";
            if (response.EndsWith(" UNKNOWN_COMMAND", StringComparison.Ordinal))
                return "commande inconnue du firmware.";
            return "motif non documenté.";
        }
    }
}
