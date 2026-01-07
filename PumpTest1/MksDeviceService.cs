using System;
using System.IO;
using System.IO.Ports;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Text.RegularExpressions;

namespace PumpTest1
{
    // MksData 클래스 정의
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
        // 설정 변수
        public string PortName { get; set; } = "COM1";
        public int BaudRate { get; set; } = 9600;
        public int IntervalMs { get; set; } = 500;
        public string LogFolderPath { get; set; } = AppDomain.CurrentDomain.BaseDirectory;

        private SerialPort _serialPort;
        private bool _isRunning = false;
        private const int DEVICE_ID = 253;
        private const string TERMINATOR = ";FF";

        // 이벤트 정의
        public event Action<MksData> OnDataReceived;
        public event Action<string> OnError;
        public event Action<string> OnStatusChanged;
        public event Action<string> OnUnitChanged;

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

        // 단위 변경 메서드
        public bool SetUnit(string unitName, string portName)
        {
            try
            {
                using (var sp = new SerialPort(portName, 9600, Parity.None, 8, StopBits.One))
                {
                    sp.ReadTimeout = 1000;
                    sp.Open();

                    sp.DiscardInBuffer();
                    sp.Write($"@{DEVICE_ID}U!{unitName}{TERMINATOR}");
                    Thread.Sleep(500);

                    sp.DiscardInBuffer();
                    sp.Write($"@{DEVICE_ID}U?{TERMINATOR}");
                    string resp = sp.ReadTo(TERMINATOR);

                    return resp.ToUpper().Contains(unitName);
                }
            }
            catch
            {
                return false;
            }
        }

        // 메인 로깅 루프
        private async Task LoggingLoop()
        {
            // 1. 폴더 생성
            if (!Directory.Exists(LogFolderPath))
            {
                try { Directory.CreateDirectory(LogFolderPath); }
                catch (Exception ex)
                {
                    OnError?.Invoke($"Dir Error: {ex.Message}");
                    _isRunning = false;
                    return;
                }
            }

            // 2. 포트 연결
            try
            {
                if (_serialPort != null && _serialPort.IsOpen) _serialPort.Close();
                _serialPort = new SerialPort(PortName, BaudRate, Parity.None, 8, StopBits.One);

                // 노이즈 대응: 타임아웃을 짧게(400ms) 설정하여 깨진 패킷을 빨리 버림
                _serialPort.ReadTimeout = 400;
                _serialPort.WriteTimeout = 400;
                _serialPort.Open();

                OnStatusChanged?.Invoke($"Connected to {PortName}");
            }
            catch (Exception ex)
            {
                OnError?.Invoke($"Connection Failed: {ex.Message}");
                _isRunning = false;
                return;
            }

            // 3. 초기 단위 확인
            string currentUnit = "Unknown";
            try
            {
                currentUnit = GetUnitInternal();
                OnUnitChanged?.Invoke(currentUnit);
            }
            catch { }

            // 4. CSV 파일 준비
            string fileName = $"mks_log_{DateTime.Now:yyyyMMdd_HHmmss}.csv";
            string fullPath = Path.Combine(LogFolderPath, fileName);
            StreamWriter csvWriter = null;

            try
            {
                var fs = new FileStream(fullPath, FileMode.Create, FileAccess.Write, FileShare.Read);
                csvWriter = new StreamWriter(fs, Encoding.UTF8) { AutoFlush = true };
                await csvWriter.WriteLineAsync($"Time,Ch1 ({currentUnit}),Ch2 ({currentUnit}),Ch3 ({currentUnit})");
                OnStatusChanged?.Invoke($"Logging to {fileName}");
            }
            catch (Exception ex)
            {
                OnError?.Invoke($"CSV Error: {ex.Message}");
                _isRunning = false;
                Cleanup(csvWriter);
                return;
            }

            // 5. 데이터 수집 반복
            while (_isRunning)
            {
                DateTime loopStart = DateTime.Now;

                // 값 읽기 (수정된 메서드 사용)
                double? v1 = GetPressureInternal(1);
                double? v2 = GetPressureInternal(2);
                double? v3 = GetPressureInternal(3);

                string now = loopStart.ToString("yyyy-MM-dd HH:mm:ss");

                // 파일 저장
                if (csvWriter != null)
                {
                    string s1 = v1.HasValue ? v1.Value.ToString("F2") : "Error";
                    string s2 = v2.HasValue ? v2.Value.ToString("F2") : "Error";
                    string s3 = v3.HasValue ? v3.Value.ToString("F2") : "Error";
                    await csvWriter.WriteLineAsync($"{now},{s1},{s2},{s3}");
                }

                // UI 업데이트 알림
                OnDataReceived?.Invoke(new MksData
                {
                    Timestamp = now,
                    Ch1 = v1,
                    Ch2 = v2,
                    Ch3 = v3,
                    Unit = currentUnit
                });

                // 주기 대기
                int elapsed = (int)(DateTime.Now - loopStart).TotalMilliseconds;
                int wait = IntervalMs - elapsed;
                if (wait > 0) await Task.Delay(wait);
            }

            OnStatusChanged?.Invoke("Stopped.");
            Cleanup(csvWriter);
        }

        private void Cleanup(StreamWriter writer)
        {
            try { writer?.Close(); } catch { }
            try { if (_serialPort != null && _serialPort.IsOpen) _serialPort.Close(); } catch { }
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
            catch
            {
                return null;
            }
        }

        private string GetUnitInternal()
        {
            string[] knownUnits = { "PASCAL", "TORR", "MBAR", "MICRON", "ATM", "PA" };
            for (int i = 0; i < 3; i++)
            {
                string resp = WriteRead("U?");
                if (!string.IsNullOrEmpty(resp))
                {
                    string upper = resp.ToUpper();
                    foreach (var u in knownUnits)
                        if (upper.Contains(u)) return u;
                }
                Thread.Sleep(100);
            }
            return "Unknown";
        }

        // ★ [핵심 수정] 압력값 읽기 로직 개선
        private double? GetPressureInternal(int ch)
        {
            // 재시도 횟수: 3회
            for (int i = 0; i < 3; i++)
            {
                string resp = WriteRead($"PR{ch}?");
                if (!string.IsNullOrEmpty(resp))
                {
                    // 1. 장비 ID(@253) 제거 (이게 253.00으로 읽히는 원인)
                    string cleanResp = resp.Replace($"@{DEVICE_ID}", "");

                    // 2. 소수점(\.)이 있는 숫자만 찾도록 강제 (1>025 같은 노이즈 무시)
                    var match = Regex.Match(cleanResp, @"([-+]?[0-9]*\.[0-9]+([eE][-+]?[0-9]+)?)");

                    if (match.Success && double.TryParse(match.Groups[1].Value, out double val))
                    {
                        return val;
                    }
                }
                Thread.Sleep(50);
            }
            return null;
        }
    }
}