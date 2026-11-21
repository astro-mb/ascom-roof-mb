using System;
using ASCOM.Utilities;

namespace ASCOM.ArduinoRoof
{
    /// <summary>
    /// Enveloppe verrouillée autour de <see cref="TraceLogger"/>.
    /// Le driver écrit dans le journal depuis au moins deux fils d'exécution (la tâche de polling et
    /// le fil appelant de NINA) : la sérialisation est indispensable.
    /// Le journal est l'outil de dépannage principal ; il trace chaque ligne émise et chaque ligne reçue.
    /// </summary>
    internal sealed class DriverLogger : IDisposable
    {
        private readonly object _lock = new object();
        private TraceLogger _logger;
        private bool _enabled;
        private bool _disposed;

        internal DriverLogger(string logFileType, bool enabled)
        {
            try
            {
                // TraceLogger("", "ArduinoRoof") → fichiers ASCOM.ArduinoRoof.<date>.<heure>.txt
                // dans Documents\ASCOM\Logs <date>\.
                _logger = new TraceLogger(string.Empty, logFileType);
                _logger.IdentifierWidth = 14;
                _logger.Enabled = enabled;
                _enabled = enabled;
            }
            catch (Exception)
            {
                // Un journal indisponible (disque plein, droits…) ne doit jamais empêcher
                // le pilotage du toit : on dégrade silencieusement.
                _logger = null;
                _enabled = false;
            }
        }

        /// <summary>Active ou désactive la journalisation à chaud (réglage de la boîte de configuration).</summary>
        internal bool Enabled
        {
            get { lock (_lock) { return _enabled; } }
            set
            {
                lock (_lock)
                {
                    _enabled = value;
                    if (_logger != null)
                    {
                        try { _logger.Enabled = value; }
                        catch (Exception) { /* journal indisponible : ignoré volontairement */ }
                    }
                }
            }
        }

        /// <summary>Trace une opération normale.</summary>
        internal void Log(string identifier, string message)
        {
            lock (_lock)
            {
                if (_disposed || _logger == null || !_enabled) return;
                try { _logger.LogMessageCrLf(identifier, message); }
                catch (Exception) { /* journal indisponible : ignoré volontairement */ }
            }
        }

        /// <summary>Trace un incident (échec de liaison, NAK inattendu, trame illisible…).</summary>
        internal void LogIssue(string identifier, string message)
        {
            lock (_lock)
            {
                if (_disposed || _logger == null || !_enabled) return;
                try { _logger.LogIssue(identifier, message); }
                catch (Exception) { /* journal indisponible : ignoré volontairement */ }
            }
        }

        /// <summary>Chemin du fichier journal courant, ou une explication si la journalisation est inactive.</summary>
        internal string CurrentLogFile
        {
            get
            {
                lock (_lock)
                {
                    if (_logger == null) return "(journal indisponible)";
                    if (!_enabled) return "(journalisation désactivée)";
                    try { return _logger.LogFileName; }
                    catch (Exception) { return "(inconnu)"; }
                }
            }
        }

        public void Dispose()
        {
            lock (_lock)
            {
                if (_disposed) return;
                _disposed = true;
                if (_logger != null)
                {
                    try { _logger.Dispose(); }
                    catch (Exception) { /* fermeture best-effort */ }
                    _logger = null;
                }
            }
        }
    }
}
