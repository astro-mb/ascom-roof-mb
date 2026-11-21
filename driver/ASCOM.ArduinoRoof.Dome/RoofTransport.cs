using System;
using System.IO.Ports;
using System.Text;

namespace ASCOM.ArduinoRoof
{
    /// <summary>
    /// Canal ligne à ligne vers le contrôleur de toit. <see cref="RoofLink"/> ne connaît que cette
    /// interface : le même code de liaison pilote le vrai port série et le simulateur intégré, ce qui
    /// permet de valider le driver (ConformU, reprises automatiques) sans matériel.
    /// </summary>
    internal interface IRoofTransport : IDisposable
    {
        /// <summary>Description courte pour le journal (« COM4 à 115200 bauds, 8N1 »…).</summary>
        string Description { get; }

        /// <summary>Attente imposée après l'ouverture, avant la première commande (§1 : reset DTR du Nano).</summary>
        int SettleMs { get; }

        bool IsOpen { get; }

        void Open();

        void WriteLine(string line);

        /// <summary>Lit une ligne. Lève <see cref="TimeoutException"/> au-delà du délai de lecture.</summary>
        string ReadLine();

        void DiscardInBuffer();

        void DiscardOutBuffer();
    }

    /// <summary>Port série réel, réglé selon le §1 du protocole.</summary>
    internal sealed class SerialRoofTransport : IRoofTransport
    {
        private readonly SerialPort _port;

        internal SerialRoofTransport(string portName)
        {
            _port = new SerialPort(portName, RoofLink.BaudRate, Parity.None, 8, StopBits.One);
            _port.Handshake = Handshake.None;
            _port.DtrEnable = false;           // imposé : pas de pilotage DTR/RTS
            _port.RtsEnable = false;
            _port.ReadTimeout = RoofLink.SerialTimeoutMs;
            _port.WriteTimeout = RoofLink.SerialTimeoutMs;
            _port.NewLine = "\n";              // §1 : PC → Arduino se termine par \n
            _port.Encoding = Encoding.ASCII;   // §1 : ASCII imprimable uniquement
            _port.ReadBufferSize = 4096;
            _port.WriteBufferSize = 1024;
        }

        public string Description
        {
            get { return _port.PortName + " à " + RoofLink.BaudRate + " bauds, 8N1, sans contrôle de flux"; }
        }

        public int SettleMs { get { return RoofLink.PortSettleMs; } }

        public bool IsOpen { get { return _port.IsOpen; } }

        public void Open() { _port.Open(); }

        public void WriteLine(string line) { _port.WriteLine(line); }

        public string ReadLine() { return _port.ReadLine(); }

        public void DiscardInBuffer() { _port.DiscardInBuffer(); }

        public void DiscardOutBuffer() { _port.DiscardOutBuffer(); }

        public void Dispose()
        {
            try { if (_port.IsOpen) _port.Close(); }
            finally { _port.Dispose(); }
        }
    }

    /// <summary>
    /// Liaison avec le simulateur intégré (<see cref="RoofSimulator"/>), sélectionnée par le port
    /// « SIM ». Comme un vrai port COM, elle ne peut être ouverte qu'une fois à la fois, et son
    /// ouverture redémarre le firmware simulé (reset DTR du Nano), le portail continuant sa course.
    /// </summary>
    internal sealed class SimulatedRoofTransport : IRoofTransport
    {
        private readonly RoofSimulator _simulator;
        private bool _open;

        internal SimulatedRoofTransport(RoofSimulator simulator)
        {
            _simulator = simulator;
        }

        public string Description { get { return "simulateur intégré (port " + RoofSimulator.PortName + ")"; } }

        /// <summary>Le simulateur redémarre instantanément : une courte attente suffit.</summary>
        public int SettleMs { get { return 200; } }

        public bool IsOpen { get { return _open; } }

        public void Open()
        {
            _simulator.Attach();
            _open = true;
        }

        public void WriteLine(string line)
        {
            if (!_open) throw new InvalidOperationException("Le port simulé est fermé.");
            _simulator.ReceiveLine(line);
        }

        public string ReadLine()
        {
            if (!_open) throw new InvalidOperationException("Le port simulé est fermé.");
            return _simulator.ReadLine(RoofLink.SerialTimeoutMs);
        }

        public void DiscardInBuffer() { _simulator.DiscardOutput(); }

        public void DiscardOutBuffer() { /* rien en attente côté émission */ }

        public void Dispose()
        {
            if (!_open) return;
            _open = false;
            _simulator.Detach();
        }
    }
}
