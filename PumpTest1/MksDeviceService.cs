using System;
using System.IO.Ports;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace PumpTest1
{
    public class MksData
    {
        public string Timestamp { get; set; }
        public double? Ch1 { get; set; }
        public double? Ch2 { get; set; }
        public double? Ch3 { get; set; }
        public string Unit { get; set; } // 데이터에 단위를 포함해서 전달
    }

    public class MksDeviceService
    {
        public string PortName { get; set; } = "COM1";
        public int BaudRate { get; set; } = 9600;
        public int IntervalMs { get; set; } = 500;

        private SerialPort _serialPort;
        private bool _isRunning = false;
        private const int DEVICE_ID = 253;
        private const string TERMINATOR = ";FF";

        // [1번요청] 현재 단위 (최초 1회만 읽음)
        public string CurrentUnit { get; private set; } = "Unknown";
        public MksData CurrentData { get; private set; }

        public event Action<string> OnError;
        public event Action<string> OnStatusChanged;

        public void Start()
        {
            if (_isRunning) return;
            _isRunning = true;
            Task.Run(LoggingLoop);
        }

        public void Stop()
        {
            _isRunning = false;
        }

        public bool SetUnit(string unitName, string portName)
        {
            try
            {
                using (var sp = new SerialPort(portName, 9600, Parity.None, 8, StopBits.One))
                {
                    sp.ReadTimeout = 1000; sp.Open();
                    sp.DiscardInBuffer(); sp.Write($"@{DEVICE_ID}U!{unitName}{TERMINATOR}");
                    Thread.Sleep(500);
                    sp.DiscardInBuffer(); sp.Write($"@{DEVICE_ID}U?{TERMINATOR}");
                    string resp = sp.ReadTo(TERMINATOR);
                    return resp.ToUpper().Contains(unitName);
                }
            }
            catch { return false; }
        }

        private async Task LoggingLoop()
        {
            try
            {
                if (_serialPort != null && _serialPort.IsOpen) _serialPort.Close();
                _serialPort = new SerialPort(PortName, BaudRate, Parity.None, 8, StopBits.One);
                _serialPort.ReadTimeout = 400;
                _serialPort.WriteTimeout = 400;
                _serialPort.Open();
                OnStatusChanged?.Invoke($"Connected {PortName}");
            }
            catch (Exception ex) { OnError?.Invoke($"Conn Failed: {ex.Message}"); _isRunning = false; return; }

            // [1번요청 반영] 시작 시 단위를 1회만 읽고 저장
            try { CurrentUnit = GetUnitInternal(); } catch { CurrentUnit = "Unknown"; }

            while (_isRunning)
            {
                DateTime start = DateTime.Now;

                double? v1 = GetPressureInternal(1);
                double? v2 = GetPressureInternal(2);
                double? v3 = GetPressureInternal(3);
                string now = start.ToString("yyyy-MM-dd HH:mm:ss");

                // 읽어둔 단위를 그대로 사용
                CurrentData = new MksData { Timestamp = now, Ch1 = v1, Ch2 = v2, Ch3 = v3, Unit = CurrentUnit };

                int wait = IntervalMs - (int)(DateTime.Now - start).TotalMilliseconds;
                if (wait > 0) await Task.Delay(wait);
            }

            OnStatusChanged?.Invoke("Stopped.");
            if (_serialPort != null && _serialPort.IsOpen) _serialPort.Close();
        }

        private string WriteRead(string cmd)
        {
            if (_serialPort == null || !_serialPort.IsOpen) return null;
            try
            {
                _serialPort.DiscardInBuffer();
                _serialPort.Write($"@{DEVICE_ID}{cmd}{TERMINATOR}");
                return _serialPort.ReadTo(TERMINATOR).Trim();
            }
            catch { return null; }
        }

        private string GetUnitInternal()
        {
            string[] units = { "PASCAL", "TORR", "MBAR", "MICRON", "ATM", "PA" };
            for (int i = 0; i < 3; i++)
            {
                string resp = WriteRead("U?");
                if (!string.IsNullOrEmpty(resp))
                {
                    string upper = resp.ToUpper();
                    foreach (var u in units) if (upper.Contains(u)) return u;
                }
                Thread.Sleep(100);
            }
            return "Unknown";
        }

        private double? GetPressureInternal(int ch)
        {
            for (int i = 0; i < 3; i++)
            {
                string resp = WriteRead($"PR{ch}?");
                if (!string.IsNullOrEmpty(resp))
                {
                    string clean = resp.Replace($"@{DEVICE_ID}", "");
                    var match = Regex.Match(clean, @"([-+]?[0-9]*\.[0-9]+([eE][-+]?[0-9]+)?)");
                    if (match.Success && double.TryParse(match.Groups[1].Value, out double val)) return val;
                }
                Thread.Sleep(50);
            }
            return null;
        }
    }
}