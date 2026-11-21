using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace ASCOM.ArduinoRoof
{
    /// <summary>
    /// Simulateur intégré, sélectionné par le port « SIM » : un portage C# de
    /// <c>firmware/RoofController/RoofController.ino</c> (version 1.1.0) branché sur un modèle
    /// physique de motorisation de portail séquentielle.
    /// <para>
    /// Il sert à passer ConformU et à rejouer les reprises automatiques sans matériel. Les
    /// fonctions portent les noms de celles du firmware : toute évolution du <c>.ino</c> doit être
    /// reportée ici, sans quoi le simulateur validerait un comportement que la carte n'a pas.
    /// </para>
    /// <para>
    /// Une seule instance par processus (un seul « toit ») : deux instances du driver voient donc
    /// le même portail, comme deux clients d'une même carte.
    /// </para>
    /// </summary>
    internal sealed class RoofSimulator
    {
        internal const string PortName = "SIM";
        internal const string FirmwareVersion = "1.1.0";

        private const uint MaxPulseMs = 2000;

        // Défauts du firmware (CFG_*_DEF du .ino). Distincts de ceux du driver, qui les écrase par
        // CFG à chaque connexion.
        private const uint CfgPulseDef = 600;
        private const uint CfgInterDef = 600;
        private const uint CfgStartDef = 5000;
        private const uint CfgTravelDef = 60000;
        private const uint CfgDebounceDef = 50;
        private const uint SensorLostConfirmMs = 3000;
        private const uint DepartRearmMs = 2000;
        private const int LineMax = 80;
        private const int TickMs = 5;

        private static readonly object SharedLock = new object();
        private static RoofSimulator _shared;

        /// <summary>Le simulateur du processus, créé à la première utilisation.</summary>
        internal static RoofSimulator Shared
        {
            get
            {
                lock (SharedLock)
                {
                    if (_shared == null) _shared = new RoofSimulator();
                    return _shared;
                }
            }
        }

        internal static bool IsSimulatorPort(string port)
        {
            return string.Equals((port ?? string.Empty).Trim(), PortName, StringComparison.OrdinalIgnoreCase);
        }

        // ------------------------------------------------------------------
        // Types internes du firmware
        // ------------------------------------------------------------------

        private enum MovePhase { None, Sequence, WaitStartRelease, Travel }
        private enum SeqKind { None, Move, Stop, Raw }
        private enum SensorRef { None, Closed, Open }
        private enum EeStatus { Ok, Init, Bad }

        private sealed class Sensor
        {
            internal bool IsClosedSensor;
            internal bool Raw;
            internal bool Stable;
            internal bool Candidate;
            internal uint LastChangeMs;
        }

        private sealed class EeBlock
        {
            internal RoofPosition Pos;
            internal RoofNextPulse Next;
            internal bool Moving;
            internal uint PulseMs, InterMs, StartMs, TravelMs, DebounceMs;
        }

        // ------------------------------------------------------------------
        // Modèle physique : motorisation de portail séquentielle
        // OUVRE → STOP → FERME → STOP → OUVRE …, arrêt autonome en butée.
        // ------------------------------------------------------------------

        private enum GateMotor { Stopped, Starting, Opening, Closing }

        private double _gatePosition;          // 0 = fermé, 1 = ouvert
        private GateMotor _gateMotor = GateMotor.Stopped;
        private bool _gateNextOpen = true;
        private bool _gateStartOpen;
        private uint _gateStartAtMs;
        private int _gateTravelMs = 10000;
        private int _gateStartDelayMs = 1000;

        // --- Pannes injectées ---
        private int _ignorePulses;
        private double _obstacle = -1;         // < 0 : aucun obstacle
        private bool _bounceArmed;
        private SensorRef _bounceSensor = SensorRef.None;
        private uint _bounceFromMs, _bounceToMs;
        private SensorRef _flickerSensor = SensorRef.None;
        private uint _flickerUntilMs;
        private bool _forceBoth;

        // ------------------------------------------------------------------
        // État du firmware (§6 du .ino)
        // ------------------------------------------------------------------

        private uint _cfgPulse, _cfgInter, _cfgStart, _cfgTravel, _cfgDebounce;

        private RoofPosition _pos = RoofPosition.Unknown;
        private RoofMotion _motion = RoofMotion.Idle;
        private RoofNextPulse _next = RoofNextPulse.Open;
        private RoofErrorCode _err = RoofErrorCode.None;
        private bool _motorRunning;

        private readonly Sensor _swClose = new Sensor { IsClosedSensor = true };
        private readonly Sensor _swOpen = new Sensor { IsClosedSensor = false };

        private bool _relayActive;
        private uint _relayOnMs;

        private SeqKind _seqKind = SeqKind.None;
        private int _pulsesLeft;
        private uint _lastPulseEndMs;
        private bool _havePulsed;
        private SensorRef _seqRefSensor = SensorRef.None;

        private MovePhase _phase = MovePhase.None;
        private RoofNextPulse _moveDir = RoofNextPulse.Open;
        private uint _moveStartMs;
        private uint _phaseStartMs;
        private SensorRef _departSensor = SensorRef.None;
        private bool _departReleased = true;
        private uint _departReleaseMs;

        private bool _lossPending;
        private uint _lossSinceMs;

        private bool _rawPulsePending;
        private uint _rawPulseMs;

        private EeStatus _eeStatus = EeStatus.Init;
        private EeBlock _eeprom;               // null = EEPROM vierge

        // ------------------------------------------------------------------
        // Liaison série simulée
        // ------------------------------------------------------------------

        private readonly object _lock = new object();
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly Queue<string> _rx = new Queue<string>();
        private readonly Queue<string> _tx = new Queue<string>();
        private readonly StringBuilder _out = new StringBuilder();
        private bool _attached;
        private uint _lastTickMs;

        private RoofSimulator()
        {
            lock (_lock)
            {
                Setup();
                _lastTickMs = Millis();
            }
            var thread = new Thread(Run);
            thread.IsBackground = true;
            thread.Name = "ArduinoRoof-Simulator";
            thread.Start();
        }

        // ==================================================================
        // Interface avec SimulatedRoofTransport
        // ==================================================================

        /// <summary>Ouverture du port : refusée s'il est déjà ouvert, puis reset DTR du Nano.</summary>
        internal void Attach()
        {
            lock (_lock)
            {
                if (_attached)
                    throw new IOException("Le port " + PortName + " est déjà utilisé par une autre connexion.");
                _attached = true;
                _rx.Clear();
                _tx.Clear();
                Setup();
            }
        }

        internal void Detach()
        {
            lock (_lock)
            {
                _attached = false;
                _rx.Clear();
                _tx.Clear();
            }
        }

        internal void ReceiveLine(string line)
        {
            lock (_lock)
            {
                _rx.Enqueue(line ?? string.Empty);
            }
        }

        internal string ReadLine(int timeoutMs)
        {
            var chrono = Stopwatch.StartNew();
            lock (_lock)
            {
                while (_tx.Count == 0)
                {
                    int remaining = timeoutMs - (int)chrono.ElapsedMilliseconds;
                    if (remaining <= 0 || !Monitor.Wait(_lock, remaining))
                    {
                        if (_tx.Count > 0) break;
                        throw new TimeoutException("Le simulateur n'a rien émis en " + timeoutMs + " ms.");
                    }
                }
                return _tx.Dequeue();
            }
        }

        internal void DiscardOutput()
        {
            lock (_lock)
            {
                _tx.Clear();
            }
        }

        // ==================================================================
        // Pilotage des pannes (action Sim:Fault)
        // ==================================================================

        /// <summary>
        /// Injecte une panne ou règle le portail simulé. Syntaxe :
        /// <c>status</c>, <c>clear</c>, <c>ignore-pulse N</c>, <c>obstacle 0..1</c>, <c>bounce</c>,
        /// <c>flicker open|closed MS</c>, <c>both on|off</c>, <c>travel MS</c>, <c>startdelay MS</c>,
        /// <c>reset</c> (redémarrage du Nano, le portail continue sa course).
        /// </summary>
        internal string Control(string parameters)
        {
            string[] t = (parameters ?? string.Empty).Trim().ToLowerInvariant()
                .Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            string verb = t.Length > 0 ? t[0] : "status";

            lock (_lock)
            {
                uint now = Millis();
                switch (verb)
                {
                    case "status":
                        break;
                    case "clear":
                        _ignorePulses = 0;
                        _obstacle = -1;
                        _bounceArmed = false;
                        _bounceSensor = SensorRef.None;
                        _flickerSensor = SensorRef.None;
                        _forceBoth = false;
                        break;
                    case "ignore-pulse":
                        _ignorePulses = t.Length > 1 ? ParseInt(t[1], 0, 100) : 1;
                        break;
                    case "obstacle":
                        if (t.Length < 2) throw new ASCOM.InvalidValueException("Sim:Fault obstacle attend une position entre 0 et 1.");
                        double x;
                        if (!double.TryParse(t[1], NumberStyles.Float, CultureInfo.InvariantCulture, out x) || x <= 0 || x >= 1)
                            throw new ASCOM.InvalidValueException("Sim:Fault obstacle : position strictement comprise entre 0 et 1.");
                        _obstacle = x;
                        break;
                    case "bounce":
                        _bounceArmed = true;
                        break;
                    case "flicker":
                        if (t.Length < 3 || (t[1] != "open" && t[1] != "closed"))
                            throw new ASCOM.InvalidValueException("Sim:Fault flicker attend « open|closed <ms> ».");
                        _flickerSensor = t[1] == "open" ? SensorRef.Open : SensorRef.Closed;
                        _flickerUntilMs = now + (uint)ParseInt(t[2], 1, 600000);
                        break;
                    case "both":
                        _forceBoth = t.Length < 2 || t[1] != "off";
                        break;
                    case "travel":
                        _gateTravelMs = t.Length > 1 ? ParseInt(t[1], 1000, 600000) : 10000;
                        break;
                    case "startdelay":
                        _gateStartDelayMs = t.Length > 1 ? ParseInt(t[1], 0, 60000) : 1000;
                        break;
                    case "reset":
                        _rx.Clear();
                        Setup();
                        break;
                    default:
                        throw new ASCOM.InvalidValueException(
                            "Sim:Fault : commande « " + verb + " » inconnue (status, clear, ignore-pulse, obstacle, bounce, " +
                            "flicker, both, travel, startdelay, reset).");
                }

                return string.Format(CultureInfo.InvariantCulture,
                    "SIM gate={0} pos={1:0.000} next={2} travel={3} startdelay={4} | ignore-pulse={5} obstacle={6} bounce={7} flicker={8} both={9}",
                    _gateMotor, _gatePosition, _gateNextOpen ? "OPEN" : "CLOSE", _gateTravelMs, _gateStartDelayMs,
                    _ignorePulses, _obstacle < 0 ? "-" : _obstacle.ToString("0.00", CultureInfo.InvariantCulture),
                    _bounceArmed ? "armé" : "-",
                    _flickerSensor == SensorRef.None || (int)(_flickerUntilMs - now) <= 0 ? "-" : _flickerSensor.ToString(),
                    _forceBoth ? "on" : "off");
            }
        }

        private static int ParseInt(string s, int min, int max)
        {
            int v;
            if (!int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out v) || v < min || v > max)
                throw new ASCOM.InvalidValueException("Sim:Fault : valeur « " + s + " » invalide (" + min + " à " + max + ").");
            return v;
        }

        // ==================================================================
        // Boucle
        // ==================================================================

        private uint Millis()
        {
            return unchecked((uint)_clock.ElapsedMilliseconds);
        }

        private void Run()
        {
            while (true)
            {
                lock (_lock)
                {
                    uint now = Millis();
                    GateUpdate(now, now - _lastTickMs);
                    _lastTickMs = now;
                    Loop(now);
                }
                Thread.Sleep(TickMs);
            }
        }

        /// <summary>loop() du firmware.</summary>
        private void Loop(uint now)
        {
            RelayUpdate(now);
            SensorsUpdate(now);
            ApplySensorInvariant(now);
            SupervisionUpdate(now);
            SequencerUpdate(now);
            SerialUpdate();
        }

        private void GateUpdate(uint now, uint dtMs)
        {
            if (_gateMotor == GateMotor.Starting && (int)(now - _gateStartAtMs) >= 0)
            {
                _gateMotor = _gateStartOpen ? GateMotor.Opening : GateMotor.Closing;
            }
            if (_gateMotor != GateMotor.Opening && _gateMotor != GateMotor.Closing) return;

            bool opening = _gateMotor == GateMotor.Opening;
            double before = _gatePosition;
            double step = (double)dtMs / _gateTravelMs;
            double after = opening ? before + step : before - step;

            // Rebond du capteur de départ : programmé au moment où le toit quitte sa butée.
            if (_bounceArmed && (before <= 0 || before >= 1) && after > 0 && after < 1)
            {
                _bounceArmed = false;
                _bounceSensor = before <= 0 ? SensorRef.Closed : SensorRef.Open;
                _bounceFromMs = now + 300;
                _bounceToMs = now + 500;
            }

            if (_obstacle >= 0 && ((opening && before < _obstacle && after >= _obstacle)
                                || (!opening && before > _obstacle && after <= _obstacle)))
            {
                // Protection anti-obstacle du portail : arrêt sur place.
                _gatePosition = _obstacle;
                _obstacle = -1;
                _gateMotor = GateMotor.Stopped;
                _gateNextOpen = !opening;
                return;
            }

            if (after >= 1)
            {
                _gatePosition = 1;
                _gateMotor = GateMotor.Stopped;
                _gateNextOpen = false;
            }
            else if (after <= 0)
            {
                _gatePosition = 0;
                _gateMotor = GateMotor.Stopped;
                _gateNextOpen = true;
            }
            else
            {
                _gatePosition = after;
            }
        }

        /// <summary>Front montant du contact relais : une impulsion sur l'entrée du portail.</summary>
        private void GatePulse(uint now)
        {
            if (_ignorePulses > 0)
            {
                _ignorePulses--;
                return;
            }
            if (_gateMotor == GateMotor.Stopped)
            {
                _gateStartOpen = _gateNextOpen;
                _gateStartAtMs = now + (uint)_gateStartDelayMs;
                _gateMotor = GateMotor.Starting;
                return;
            }
            bool wasOpening = _gateMotor == GateMotor.Starting ? _gateStartOpen : _gateMotor == GateMotor.Opening;
            _gateMotor = GateMotor.Stopped;
            _gateNextOpen = !wasOpening;
        }

        // ==================================================================
        // Portage du firmware — §7 utilitaires
        // ==================================================================

        private bool SensorRawRead(Sensor s, uint now)
        {
            bool v = s.IsClosedSensor ? _gatePosition <= 0 : _gatePosition >= 1;
            SensorRef me = s.IsClosedSensor ? SensorRef.Closed : SensorRef.Open;
            if (_bounceSensor == me && (int)(now - _bounceFromMs) >= 0)
            {
                if ((int)(now - _bounceToMs) < 0) v = true;
                else _bounceSensor = SensorRef.None;
            }
            if (_flickerSensor == me)
            {
                if ((int)(now - _flickerUntilMs) < 0) v = false;
                else _flickerSensor = SensorRef.None;
            }
            if (_forceBoth) v = true;
            return v;
        }

        private void SensorUpdate(Sensor s, uint now)
        {
            bool r = SensorRawRead(s, now);
            s.Raw = r;
            if (r != s.Candidate)
            {
                s.Candidate = r;
                s.LastChangeMs = now;
            }
            else if (s.Stable != s.Candidate && (now - s.LastChangeMs) >= _cfgDebounce)
            {
                s.Stable = s.Candidate;
            }
        }

        private void SensorsUpdate(uint now)
        {
            SensorUpdate(_swClose, now);
            SensorUpdate(_swOpen, now);
        }

        private void RelayOff()
        {
            _relayActive = false;
        }

        private void RelayUpdate(uint now)
        {
            if (!_relayActive) return;
            uint d = Math.Min(_cfgPulse, MaxPulseMs);
            if ((now - _relayOnMs) >= d)
            {
                RelayOff();
                _lastPulseEndMs = now;
                _havePulsed = true;
            }
        }

        private SensorRef ActiveSensorRef()
        {
            if (_swClose.Stable) return SensorRef.Closed;
            if (_swOpen.Stable) return SensorRef.Open;
            return SensorRef.None;
        }

        // ==================================================================
        // §8 EEPROM
        // ==================================================================

        private void CfgDefaults()
        {
            _cfgPulse = CfgPulseDef;
            _cfgInter = CfgInterDef;
            _cfgStart = CfgStartDef;
            _cfgTravel = CfgTravelDef;
            _cfgDebounce = CfgDebounceDef;
        }

        private void SaveState()
        {
            _eeprom = new EeBlock
            {
                Pos = _pos,
                Next = _next,
                Moving = _phase != MovePhase.None,
                PulseMs = _cfgPulse,
                InterMs = _cfgInter,
                StartMs = _cfgStart,
                TravelMs = _cfgTravel,
                DebounceMs = _cfgDebounce
            };
        }

        // ==================================================================
        // §9 Modèle de l'automate du portail
        // ==================================================================

        private void LatchOnSensor(RoofPosition p)
        {
            _pos = p;
            _next = p == RoofPosition.Closed ? RoofNextPulse.Open : RoofNextPulse.Close;
            _motorRunning = false;
            _rawPulsePending = false;
        }

        private void CancelSequence()
        {
            _pulsesLeft = 0;
            _seqKind = SeqKind.None;
        }

        private void SeqStart(SeqKind kind, int n)
        {
            _seqKind = kind;
            _pulsesLeft = n;
            _seqRefSensor = ActiveSensorRef();
        }

        private void EmitPulse(uint now)
        {
            if (_motorRunning)
            {
                _motorRunning = false;
            }
            else
            {
                _next = _next == RoofNextPulse.Open ? RoofNextPulse.Close : RoofNextPulse.Open;
                _motorRunning = true;
            }
            _relayActive = true;
            _relayOnMs = now;
            GatePulse(now);
        }

        private void SequencerUpdate(uint now)
        {
            if (_pulsesLeft == 0)
            {
                _seqKind = SeqKind.None;
                return;
            }
            if (_relayActive) return;
            if (_havePulsed && (now - _lastPulseEndMs) < _cfgInter) return;
            if (_seqKind == SeqKind.Stop && !_motorRunning)
            {
                CancelSequence();
                return;
            }
            EmitPulse(now);
            _pulsesLeft--;
            if (_pulsesLeft == 0) _seqKind = SeqKind.None;
        }

        // ==================================================================
        // §10 Invariant capteurs
        // ==================================================================

        private void RaiseError(RoofErrorCode e)
        {
            CancelSequence();
            _phase = MovePhase.None;
            _motion = RoofMotion.Error;
            _err = e;
        }

        private void ApplySensorInvariant(uint now)
        {
            bool c = _swClose.Stable;
            bool o = _swOpen.Stable;

            if (c && o)
            {
                if (_err != RoofErrorCode.BothSensors)
                {
                    RaiseError(RoofErrorCode.BothSensors);
                    SaveState();
                }
                return;
            }

            if (_phase != MovePhase.None)
            {
                _lossPending = false;
                return;
            }

            if (_pulsesLeft > 0 || _relayActive)
            {
                _lossPending = false;
                bool arrived = (c && _seqRefSensor != SensorRef.Closed) || (o && _seqRefSensor != SensorRef.Open);
                if (arrived)
                {
                    CancelSequence();
                    LatchOnSensor(c ? RoofPosition.Closed : RoofPosition.Open);
                    SaveState();
                }
                return;
            }

            if (_rawPulsePending)
            {
                if (!c && !o)
                {
                    _pos = RoofPosition.Unknown;
                    _rawPulsePending = false;
                    SaveState();
                }
                else if ((now - _rawPulseMs) >= _cfgStart)
                {
                    _rawPulsePending = false;
                }
                if (_rawPulsePending)
                {
                    _lossPending = false;
                    return;
                }
            }

            bool lossCandidate = !c && !o && _motion == RoofMotion.Idle
                                 && (_pos == RoofPosition.Closed || _pos == RoofPosition.Open);
            if (!lossCandidate) _lossPending = false;

            if (c)
            {
                if (_pos != RoofPosition.Closed || _next != RoofNextPulse.Open || _motorRunning)
                {
                    LatchOnSensor(RoofPosition.Closed);
                    SaveState();
                }
            }
            else if (o)
            {
                if (_pos != RoofPosition.Open || _next != RoofNextPulse.Close || _motorRunning)
                {
                    LatchOnSensor(RoofPosition.Open);
                    SaveState();
                }
            }
            else if (lossCandidate)
            {
                if (!_lossPending)
                {
                    _lossPending = true;
                    _lossSinceMs = now;
                }
                else if ((now - _lossSinceMs) >= SensorLostConfirmMs)
                {
                    _lossPending = false;
                    _pos = RoofPosition.Unknown;
                    RaiseError(RoofErrorCode.SensorLost);
                    SaveState();
                }
            }
        }

        // ==================================================================
        // §11 Supervision d'une course
        // ==================================================================

        private void OnArrival(RoofPosition reached)
        {
            CancelSequence();
            _phase = MovePhase.None;
            LatchOnSensor(reached);

            bool isTarget = _moveDir == RoofNextPulse.Open ? reached == RoofPosition.Open : reached == RoofPosition.Closed;
            if (isTarget)
            {
                _motion = RoofMotion.Idle;
                _err = RoofErrorCode.None;
            }
            else
            {
                _motion = RoofMotion.Error;
                _err = RoofErrorCode.WrongDirection;
            }
            SaveState();
        }

        private void FailMove(RoofErrorCode e)
        {
            RaiseError(e);
            if (!_swClose.Stable && !_swOpen.Stable) _pos = RoofPosition.Unknown;
            SaveState();
        }

        private void SupervisionUpdate(uint now)
        {
            if (_phase == MovePhase.None) return;

            bool c = _swClose.Stable;
            bool o = _swOpen.Stable;

            bool departGuard = !_departReleased || (now - _departReleaseMs) < DepartRearmMs;
            bool arrivedClosed = c && !(_departSensor == SensorRef.Closed && departGuard);
            bool arrivedOpen = o && !(_departSensor == SensorRef.Open && departGuard);
            if (arrivedClosed || arrivedOpen)
            {
                OnArrival(arrivedClosed ? RoofPosition.Closed : RoofPosition.Open);
                return;
            }

            if (!_departReleased)
            {
                bool stillHeld = (_departSensor == SensorRef.Closed && c) || (_departSensor == SensorRef.Open && o);
                if (!stillHeld)
                {
                    _departReleased = true;
                    _departReleaseMs = now;
                    if (_phase == MovePhase.WaitStartRelease)
                    {
                        _phase = MovePhase.Travel;
                        _phaseStartMs = now;
                    }
                }
            }

            switch (_phase)
            {
                case MovePhase.Sequence:
                    if (_pulsesLeft == 0 && !_relayActive)
                    {
                        _phase = _departReleased ? MovePhase.Travel : MovePhase.WaitStartRelease;
                        _phaseStartMs = now;
                    }
                    break;
                case MovePhase.WaitStartRelease:
                    if ((now - _phaseStartMs) >= _cfgStart) FailMove(RoofErrorCode.TimeoutStart);
                    break;
                case MovePhase.Travel:
                    if ((now - _phaseStartMs) >= _cfgTravel) FailMove(RoofErrorCode.TimeoutTravel);
                    break;
            }
        }

        private void StartSupervisedMove(RoofNextPulse dir, int pulses)
        {
            uint now = Millis();
            _moveDir = dir;
            _motion = dir == RoofNextPulse.Open ? RoofMotion.Opening : RoofMotion.Closing;
            _err = RoofErrorCode.None;
            _phase = MovePhase.Sequence;
            _moveStartMs = now;
            _phaseStartMs = now;
            _departSensor = ActiveSensorRef();
            _departReleased = _departSensor == SensorRef.None;
            _rawPulsePending = false;
            SeqStart(SeqKind.Move, pulses);
            SaveState();
        }

        // ==================================================================
        // §12 Formatage
        // ==================================================================

        private static string PosName(RoofPosition p)
        {
            switch (p)
            {
                case RoofPosition.Closed: return "CLOSED";
                case RoofPosition.Open: return "OPEN";
                case RoofPosition.Intermediate: return "INTERMEDIATE";
                default: return "UNKNOWN";
            }
        }

        private static string MotionName(RoofMotion m)
        {
            switch (m)
            {
                case RoofMotion.Idle: return "IDLE";
                case RoofMotion.Opening: return "OPENING";
                case RoofMotion.Closing: return "CLOSING";
                default: return "ERROR";
            }
        }

        private static string DirName(RoofNextPulse d)
        {
            return d == RoofNextPulse.Open ? "OPEN" : "CLOSE";
        }

        private static string ErrName(RoofErrorCode e)
        {
            switch (e)
            {
                case RoofErrorCode.TimeoutStart: return "TIMEOUT_START";
                case RoofErrorCode.TimeoutTravel: return "TIMEOUT_TRAVEL";
                case RoofErrorCode.BothSensors: return "BOTH_SENSORS";
                case RoofErrorCode.WrongDirection: return "WRONG_DIRECTION";
                case RoofErrorCode.Interrupted: return "INTERRUPTED";
                case RoofErrorCode.SensorLost: return "SENSOR_LOST";
                default: return "NONE";
            }
        }

        private static string Flag(bool v)
        {
            return v ? "1" : "0";
        }

        private void SendLine(string line)
        {
            if (!_attached) return;    // USB débranché : la carte parle dans le vide
            _tx.Enqueue(line);
            Monitor.PulseAll(_lock);
        }

        // ==================================================================
        // §13 Commandes
        // ==================================================================

        private uint ElapsedMs()
        {
            if (_motion == RoofMotion.Opening || _motion == RoofMotion.Closing) return Millis() - _moveStartMs;
            return 0;
        }

        private void CmdState()
        {
            SendLine("STATE " + PosName(_pos) + " " + MotionName(_motion) + " " + Flag(_swClose.Stable) + " "
                     + Flag(_swOpen.Stable) + " " + Flag(_relayActive) + " " + DirName(_next) + " "
                     + ElapsedMs().ToString(CultureInfo.InvariantCulture) + " " + ErrName(_err));
        }

        private void CmdMove(RoofNextPulse dir)
        {
            string name = DirName(dir);
            if (_motion == RoofMotion.Error)
            {
                SendLine("NAK " + name + " ERROR");
                return;
            }
            if (_motion == RoofMotion.Opening || _motion == RoofMotion.Closing || _phase != MovePhase.None
                || _pulsesLeft > 0 || _relayActive || _rawPulsePending)
            {
                SendLine("NAK " + name + " BUSY");
                return;
            }
            if (_pos == RoofPosition.Unknown)
            {
                SendLine("NAK " + name + " UNKNOWN_POSITION");
                return;
            }
            RoofPosition target = dir == RoofNextPulse.Open ? RoofPosition.Open : RoofPosition.Closed;
            if (_pos == target)
            {
                SendLine("ACK " + name + " ALREADY");
                return;
            }
            bool reverse = _next != dir;
            StartSupervisedMove(dir, reverse ? 3 : 1);
            SendLine("ACK " + name + (reverse ? " MOVING_REVERSE" : " MOVING"));
        }

        private void CmdAbort()
        {
            CancelSequence();
            bool wasSupervised = _phase != MovePhase.None;
            _phase = MovePhase.None;
            _departReleased = true;
            _rawPulsePending = false;

            bool stopping = _motorRunning;
            if (stopping) SeqStart(SeqKind.Stop, 1);

            if (_motion != RoofMotion.Error) _motion = RoofMotion.Idle;

            if (wasSupervised && _pos != RoofPosition.Unknown && !_swClose.Stable && !_swOpen.Stable)
            {
                _pos = RoofPosition.Intermediate;
            }
            SaveState();
            SendLine(stopping ? "ACK ABORT STOPPING" : "ACK ABORT IDLE");
        }

        private void CmdClearErr()
        {
            RoofErrorCode acquittee = _err;
            CancelSequence();
            _phase = MovePhase.None;
            _departReleased = true;
            _rawPulsePending = false;
            _err = RoofErrorCode.None;
            _motion = RoofMotion.Idle;

            if (_swClose.Stable && _swOpen.Stable) _pos = RoofPosition.Unknown;
            else if (_swClose.Stable) LatchOnSensor(RoofPosition.Closed);
            else if (_swOpen.Stable) LatchOnSensor(RoofPosition.Open);
            else if (acquittee != RoofErrorCode.None) _pos = RoofPosition.Unknown;

            SaveState();
            SendLine("ACK CLEARERR " + PosName(_pos));
        }

        private void CmdRecover(string arg)
        {
            RoofNextPulse d;
            if (arg == "OPEN") d = RoofNextPulse.Open;
            else if (arg == "CLOSE") d = RoofNextPulse.Close;
            else
            {
                SendLine("NAK RECOVER BAD_ARGS");
                return;
            }
            if (_motion == RoofMotion.Error)
            {
                SendLine("NAK RECOVER ERROR");
                return;
            }
            if (_phase != MovePhase.None || _pulsesLeft > 0 || _relayActive || _rawPulsePending)
            {
                SendLine("NAK RECOVER BUSY");
                return;
            }
            if (_pos != RoofPosition.Unknown)
            {
                SendLine("NAK RECOVER NOT_NEEDED");
                return;
            }
            StartSupervisedMove(d, 1);
            SendLine("ACK RECOVER " + DirName(d));
        }

        private void CmdPulse()
        {
            if (_phase != MovePhase.None || _pulsesLeft > 0 || _relayActive)
            {
                SendLine("NAK PULSE BUSY");
                return;
            }
            SeqStart(SeqKind.Raw, 1);
            _rawPulsePending = true;
            _rawPulseMs = Millis();
            if (!_swClose.Stable && !_swOpen.Stable)
            {
                _pos = RoofPosition.Unknown;
                _rawPulsePending = false;
            }
            SaveState();
            SendLine("ACK PULSE");
        }

        private void CmdDiag()
        {
            string ee = _eeStatus == EeStatus.Ok ? "OK" : _eeStatus == EeStatus.Init ? "INIT" : "BAD";
            SendLine("DIAG SWC=" + Flag(_swClose.Raw) + "/" + Flag(_swClose.Stable)
                     + " SWO=" + Flag(_swOpen.Raw) + "/" + Flag(_swOpen.Stable)
                     + " RELAY=" + Flag(_relayActive) + " NEXT=" + DirName(_next) + " POS=" + PosName(_pos)
                     + " MOTION=" + MotionName(_motion) + " ERR=" + ErrName(_err) + " RUN=" + Flag(_motorRunning)
                     + " UP=" + Millis().ToString(CultureInfo.InvariantCulture) + " EE=" + ee);
        }

        private void CmdCfgQuery()
        {
            SendLine(string.Format(CultureInfo.InvariantCulture, "CFG PULSE={0} INTER={1} START={2} TRAVEL={3} DEBOUNCE={4}",
                                   _cfgPulse, _cfgInter, _cfgStart, _cfgTravel, _cfgDebounce));
        }

        private void CmdCfgSet(string args)
        {
            string[] t = (args ?? string.Empty).Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (t.Length < 2)
            {
                SendLine("NAK CFG BAD_ARGS");
                return;
            }
            uint v;
            if (!uint.TryParse(t[1], NumberStyles.None, CultureInfo.InvariantCulture, out v))
            {
                SendLine("NAK CFG BAD_ARGS");
                return;
            }

            uint lo, hi;
            switch (t[0])
            {
                case "PULSE": lo = DriverSettings.PulseMin; hi = DriverSettings.PulseMax; break;
                case "INTER": lo = DriverSettings.InterMin; hi = DriverSettings.InterMax; break;
                case "START": lo = DriverSettings.StartMin; hi = DriverSettings.StartMax; break;
                case "TRAVEL": lo = DriverSettings.TravelMin; hi = DriverSettings.TravelMax; break;
                case "DEBOUNCE": lo = DriverSettings.DebounceMin; hi = DriverSettings.DebounceMax; break;
                default:
                    SendLine("NAK CFG BAD_KEY");
                    return;
            }
            if (v < lo || v > hi)
            {
                SendLine("NAK CFG OUT_OF_RANGE");
                return;
            }
            switch (t[0])
            {
                case "PULSE": _cfgPulse = v; break;
                case "INTER": _cfgInter = v; break;
                case "START": _cfgStart = v; break;
                case "TRAVEL": _cfgTravel = v; break;
                default: _cfgDebounce = v; break;
            }
            SaveState();
            SendLine("ACK CFG " + t[0] + "=" + v.ToString(CultureInfo.InvariantCulture));
        }

        // ==================================================================
        // §14 Réception série et aiguillage
        // ==================================================================

        private void HandleLine(string line)
        {
            if (line.Length == 0 || line[0] == '#') return;

            string verb = line;
            string args = null;
            int sp = line.IndexOf(' ');
            if (sp >= 0)
            {
                verb = line.Substring(0, sp);
                args = line.Substring(sp + 1).TrimStart(' ');
            }

            switch (verb)
            {
                case "STATE": CmdState(); break;
                case "PING": SendLine("PONG ROOF " + Dome.SupportedProtocolVersion + " " + FirmwareVersion); break;
                case "OPEN": CmdMove(RoofNextPulse.Open); break;
                case "CLOSE": CmdMove(RoofNextPulse.Close); break;
                case "ABORT": CmdAbort(); break;
                case "CLEARERR": CmdClearErr(); break;
                case "RECOVER":
                    string first = args == null ? null : args.Split(' ')[0];
                    CmdRecover(first);
                    break;
                case "PULSE": CmdPulse(); break;
                case "DIAG": CmdDiag(); break;
                case "CFG?": CmdCfgQuery(); break;
                case "CFGRESET":
                    CfgDefaults();
                    SaveState();
                    SendLine("ACK CFGRESET");
                    break;
                case "CFG": CmdCfgSet(args); break;
                default: SendLine("NAK ? UNKNOWN_COMMAND"); break;
            }
        }

        /// <summary>Une ligne complète au plus par tour de boucle, comme le firmware.</summary>
        private void SerialUpdate()
        {
            if (_rx.Count == 0) return;
            string raw = _rx.Dequeue();

            _out.Clear();
            foreach (char ch in raw)
            {
                if (ch == '\r' || ch < 0x20 || ch > 0x7E) continue;
                _out.Append(ch >= 'a' && ch <= 'z' ? (char)(ch - 32) : ch);
            }
            if (_out.Length > LineMax)
            {
                SendLine("NAK ? UNKNOWN_COMMAND");
                return;
            }
            HandleLine(_out.ToString());
        }

        // ==================================================================
        // §16 Démarrage
        // ==================================================================

        private void BootRestoreState()
        {
            EeBlock b = _eeprom;
            bool valid = b != null;
            _eeStatus = valid ? EeStatus.Ok : EeStatus.Init;

            if (valid)
            {
                _cfgPulse = b.PulseMs;
                _cfgInter = b.InterMs;
                _cfgStart = b.StartMs;
                _cfgTravel = b.TravelMs;
                _cfgDebounce = b.DebounceMs;
            }
            else
            {
                CfgDefaults();
            }

            uint now = Millis();
            _swClose.Raw = _swClose.Stable = _swClose.Candidate = SensorRawRead(_swClose, now);
            _swOpen.Raw = _swOpen.Stable = _swOpen.Candidate = SensorRawRead(_swOpen, now);
            _swClose.LastChangeMs = now;
            _swOpen.LastChangeMs = now;

            bool c = _swClose.Stable;
            bool o = _swOpen.Stable;

            _phase = MovePhase.None;
            _departReleased = true;
            _motorRunning = false;

            if (c && o)
            {
                _pos = RoofPosition.Unknown;
                _next = RoofNextPulse.Open;
                _motion = RoofMotion.Error;
                _err = RoofErrorCode.BothSensors;
                return;
            }
            if (c || o)
            {
                LatchOnSensor(c ? RoofPosition.Closed : RoofPosition.Open);
                _motion = RoofMotion.Idle;
                _err = RoofErrorCode.None;
                return;
            }
            if (!valid || b.Moving)
            {
                _pos = RoofPosition.Unknown;
                _next = valid ? b.Next : RoofNextPulse.Open;
                _motion = RoofMotion.Error;
                _err = RoofErrorCode.Interrupted;
                return;
            }
            _next = b.Next;
            _motion = RoofMotion.Idle;
            _err = RoofErrorCode.None;
            _pos = b.Pos == RoofPosition.Unknown ? RoofPosition.Unknown : RoofPosition.Intermediate;
        }

        /// <summary>setup() : appelé à la création et à chaque reset (ouverture du port, panne « reset »).</summary>
        private void Setup()
        {
            RelayOff();

            // La RAM du Nano repart de zéro.
            _pos = RoofPosition.Unknown;
            _motion = RoofMotion.Idle;
            _next = RoofNextPulse.Open;
            _err = RoofErrorCode.None;
            _seqKind = SeqKind.None;
            _pulsesLeft = 0;
            _havePulsed = false;
            _seqRefSensor = SensorRef.None;
            _departSensor = SensorRef.None;
            _lossPending = false;
            _rawPulsePending = false;

            CfgDefaults();
            BootRestoreState();
            SaveState();
            SendLine("# ROOF CONTROLLER " + FirmwareVersion + " BOOT");
        }
    }
}
