using System;
using System.IO.Ports;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace PumpTest1
{
    // 1. 데이터 구조에 Ch4 추가
    public class MksData
    {
        public string Timestamp { get; set; }
        public double? Ch1 { get; set; }
        public double? Ch2 { get; set; }
        public double? Ch3 { get; set; }
        public double? Ch4 { get; set; } // [필수] 4번 채널 변수
        public string Unit { get; set; }
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
        private readonly object _lockObj = new object(); // 충돌 방지용 잠금

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

        // 단위 변경 시 포트 충돌 방지 로직 적용
        public bool SetUnit(string unitName, string portName)
        {
            lock (_lockObj)
            {
                try
                {
                    if (_isRunning && _serialPort != null && _serialPort.IsOpen)
                    {
                        return SendUnitCommand(_serialPort, unitName);
                    }
                    else
                    {
                        using (var sp = new SerialPort(portName, 9600, Parity.None, 8, StopBits.One))
                        {
                            sp.ReadTimeout = 1000;
                            sp.Open();
                            return SendUnitCommand(sp, unitName);
                        }
                    }
                }
                catch (Exception ex)
                {
                    OnError?.Invoke($"SetUnit Error: {ex.Message}");
                    return false;
                }
            }
        }

        private bool SendUnitCommand(SerialPort sp, string unitName)
        {
            sp.DiscardInBuffer();
            sp.Write($"@{DEVICE_ID}U!{unitName}{TERMINATOR}");
            Thread.Sleep(500);
            sp.DiscardInBuffer();
            sp.Write($"@{DEVICE_ID}U?{TERMINATOR}");
            try
            {
                string resp = sp.ReadTo(TERMINATOR);
                bool success = resp.ToUpper().Contains(unitName);
                if (success) CurrentUnit = unitName;
                return success;
            }
            catch { return false; }
        }

        private async Task LoggingLoop()
        {
            try
            {
                lock (_lockObj)
                {
                    if (_serialPort != null && _serialPort.IsOpen) _serialPort.Close();
                    _serialPort = new SerialPort(PortName, BaudRate, Parity.None, 8, StopBits.One);
                    _serialPort.ReadTimeout = 500;
                    _serialPort.WriteTimeout = 500;
                    _serialPort.Open();
                }
                OnStatusChanged?.Invoke($"Connected {PortName}");
            }
            catch (Exception ex) { OnError?.Invoke($"Conn Failed: {ex.Message}"); _isRunning = false; return; }

            try { CurrentUnit = GetUnitInternal(); } catch { CurrentUnit = "Unknown"; }

            while (_isRunning)
            {
                DateTime start = DateTime.Now;
                string now = start.ToString("yyyy-MM-dd HH:mm:ss");

                double? v1 = null, v2 = null, v3 = null, v4 = null;

                lock (_lockObj)
                {
                    if (_serialPort != null && _serialPort.IsOpen)
                    {
                        v1 = GetPressureInternal(1);
                        v2 = GetPressureInternal(2);
                        v3 = GetPressureInternal(3);
                        v4 = GetPressureInternal(4); // [핵심] 여기서 장비에 4번 채널을 요청해야 함
                    }
                }

                CurrentData = new MksData
                {
                    Timestamp = now,
                    Ch1 = v1,
                    Ch2 = v2,
                    Ch3 = v3,
                    Ch4 = v4, // 4번 채널 값 저장
                    Unit = CurrentUnit
                };

                int wait = IntervalMs - (int)(DateTime.Now - start).TotalMilliseconds;
                if (wait > 0) await Task.Delay(wait);
            }

            lock (_lockObj)
            {
                if (_serialPort != null && _serialPort.IsOpen) _serialPort.Close();
            }
            OnStatusChanged?.Invoke("Stopped.");
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
            lock (_lockObj)
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
        }

        private double? GetPressureInternal(int ch)
        {
            for (int i = 0; i < 3; i++) // 3회 재시도
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