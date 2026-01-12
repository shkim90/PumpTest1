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

        // 단위 실시간 반영을 위해 private set 제거하거나 내부에서 수정 가능하게 함
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

        // [수정] 단위 변경 성공 시 CurrentUnit 즉시 업데이트
        public bool SetUnit(string unitName, string portName)
        {
            try
            {
                // 현재 포트가 열려있다면 잠시 닫고 설정하거나, 별도 포트 인스턴스로 설정
                // 여기서는 안전하게 별도 인스턴스로 설정 시도
                using (var sp = new SerialPort(portName, 9600, Parity.None, 8, StopBits.One))
                {
                    sp.ReadTimeout = 1000; sp.Open();
                    sp.DiscardInBuffer(); sp.Write($"@{DEVICE_ID}U!{unitName}{TERMINATOR}");
                    Thread.Sleep(500);
                    sp.DiscardInBuffer(); sp.Write($"@{DEVICE_ID}U?{TERMINATOR}");
                    string resp = sp.ReadTo(TERMINATOR);

                    bool success = resp.ToUpper().Contains(unitName);
                    if (success)
                    {
                        // [핵심] 성공하면 즉시 내부 변수 반영 -> 실시간 데이터에 바로 적용됨
                        CurrentUnit = unitName;
                    }
                    return success;
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
                _serialPort.Open();
                OnStatusChanged?.Invoke($"Connected {PortName}");
            }
            catch (Exception ex)
            {
                OnError?.Invoke($"Conn Failed: {ex.Message}");
                _isRunning = false;
                return;
            }

            try { CurrentUnit = GetUnitInternal(); } catch { CurrentUnit = "Unknown"; }

            while (_isRunning)
            {
                DateTime start = DateTime.Now;

                try
                {
                    if (!_serialPort.IsOpen) throw new Exception("Port Closed");

                    double? v1 = GetPressureInternal(1);
                    double? v2 = GetPressureInternal(2);
                    double? v3 = GetPressureInternal(3);
                    string now = start.ToString("yyyy-MM-dd HH:mm:ss");

                    // CurrentUnit이 SetUnit에 의해 바뀌면 여기서 바로 반영됨
                    CurrentData = new MksData { Timestamp = now, Ch1 = v1, Ch2 = v2, Ch3 = v3, Unit = CurrentUnit };
                }
                catch (Exception ex)
                {
                    // 에러 발생 시 루프 종료 및 에러 이벤트 전송 -> UI에서 Disconnect 처리
                    OnError?.Invoke($"Loop Error: {ex.Message}");
                    _isRunning = false;
                    break;
                }

                int wait = IntervalMs - (int)(DateTime.Now - start).TotalMilliseconds;
                if (wait > 0) await Task.Delay(wait);
            }

            if (_serialPort != null && _serialPort.IsOpen) _serialPort.Close();
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
            string[] units = { "PASCAL", "TORR", "MBAR", "MICRON", "ATM", "PA" };
            for (int i = 0; i < 3; i++)
            {
                try
                {
                    string resp = WriteRead("U?");
                    if (!string.IsNullOrEmpty(resp))
                    {
                        string upper = resp.ToUpper();
                        foreach (var u in units) if (upper.Contains(u)) return u;
                    }
                }
                catch { }
                Thread.Sleep(100);
            }
            return "Unknown";
        }

        private double? GetPressureInternal(int ch)
        {
            for (int i = 0; i < 3; i++)
            {
                try
                {
                    string resp = WriteRead($"PR{ch}?");
                    if (!string.IsNullOrEmpty(resp))
                    {
                        string clean = resp.Replace($"@{DEVICE_ID}", "");
                        var match = Regex.Match(clean, @"([-+]?[0-9]*\.[0-9]+([eE][-+]?[0-9]+)?)");
                        if (match.Success && double.TryParse(match.Groups[1].Value, out double val)) return val;
                    }
                }
                catch { }
                Thread.Sleep(50);
            }
            return null;
        }
    }
}