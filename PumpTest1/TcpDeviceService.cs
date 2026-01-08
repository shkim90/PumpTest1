using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using System.Text.RegularExpressions;

namespace PumpTest1
{
    // [핵심] 이 클래스가 있어야 에러가 안 납니다.
    public class DeviceData
    {
        public string Timestamp { get; set; }
        public double V1 { get; set; }
        public double V2 { get; set; }
        public double V3 { get; set; }
        public double V4 { get; set; }
        public List<string> RawValues { get; set; }
    }

    public class TcpDeviceService
    {
        public string IpAddress { get; set; } = "192.168.1.180";
        public int Port { get; set; } = 101;
        public int IntervalMs { get; set; } = 500;

        private bool _isRunning = false;
        private TcpClient _client;
        private NetworkStream _stream;

        // UI에서 가져다 쓸 데이터
        public DeviceData CurrentData { get; private set; }
        public string[] ChannelLabels { get; private set; } = new string[] { "CH1", "CH2", "CH3", "CH4" }; // 기본값
        public string[] ChannelUnits { get; private set; } = new string[] { "", "", "", "" };

        public bool Connected => _client != null && _client.Connected;

        public event Action<string> OnError;
        public event Action<string> OnStatusChanged;
        public event Action OnChannelInfoReady;

        public void Start()
        {
            if (_isRunning) return;
            _isRunning = true;
            Task.Run(CommunicationLoop); // 별도 스레드에서 통신 시작
        }

        public void Stop()
        {
            _isRunning = false;
            Cleanup();
            OnStatusChanged?.Invoke("Stopped.");
        }

        private async Task CommunicationLoop()
        {
            // 무한 루프: 프로그램이 꺼지거나 Stop()할 때까지 계속 재연결 시도
            while (_isRunning)
            {
                try
                {
                    // 1. 연결이 안 되어 있다면 연결 시도
                    if (!Connected)
                    {
                        OnStatusChanged?.Invoke($"Connecting to {IpAddress}:{Port}...");
                        _client = new TcpClient();

                        // 타임아웃(3초)을 건 연결 시도
                        var connectTask = _client.ConnectAsync(IpAddress, Port);
                        if (await Task.WhenAny(connectTask, Task.Delay(3000)) != connectTask)
                        {
                            throw new Exception("Connection Timeout");
                        }
                        await connectTask;

                        _stream = _client.GetStream();
                        _stream.ReadTimeout = 3000; // 읽기 타임아웃
                        OnStatusChanged?.Invoke("Connected.");

                        // 연결 성공 직후 라벨(adil?)과 단위(auiu?) 가져오기
                        await FetchMetadata();
                        OnChannelInfoReady?.Invoke();
                    }

                    // 2. 데이터 주기적 요청 (ar)
                    if (Connected)
                    {
                        long start = DateTimeOffset.Now.ToUnixTimeMilliseconds();

                        // 쓰레기 데이터 비우기
                        if (_client.Available > 0)
                        {
                            byte[] trash = new byte[_client.Available];
                            await _stream.ReadAsync(trash, 0, trash.Length);
                        }

                        // 데이터 요청 명령 전송
                        byte[] cmd = Encoding.ASCII.GetBytes("ar\r\n");
                        await _stream.WriteAsync(cmd, 0, cmd.Length);

                        // 응답 읽기 (최대 2초 대기)
                        string response = await ReadLineAsync(_stream, 2000);

                        if (!string.IsNullOrEmpty(response) && response.Contains("READ:"))
                        {
                            ProcessReadData(response);
                        }

                        // 주기(Interval) 맞추기
                        int elapsed = (int)(DateTimeOffset.Now.ToUnixTimeMilliseconds() - start);
                        int wait = IntervalMs - elapsed;
                        if (wait > 0) await Task.Delay(wait);
                    }
                }
                catch (Exception ex)
                {
                    // 에러 발생 시 로그 찍고, 잠시 대기 후 재시도
                    OnError?.Invoke($"Error: {ex.Message}");
                    Cleanup();
                    await Task.Delay(3000); // 3초 뒤 재접속 시도
                }
            }
        }

        private async Task FetchMetadata()
        {
            try
            {
                // 라벨 읽기 (adil?)
                OnStatusChanged?.Invoke("Reading Labels...");
                byte[] cmd = Encoding.ASCII.GetBytes("adil?\r\n");
                await _stream.WriteAsync(cmd, 0, cmd.Length);

                string lResp = await ReadMultiLinesAsync(1000); // 1초간 수집
                foreach (Match m in Regex.Matches(lResp, @"CH(\d)\s+LABEL:\s*""([^""]+)"""))
                    if (int.TryParse(m.Groups[1].Value, out int i) && i >= 1 && i <= 4)
                        ChannelLabels[i - 1] = m.Groups[2].Value.Trim();

                // 단위 읽기 (auiu?)
                OnStatusChanged?.Invoke("Reading Units...");
                cmd = Encoding.ASCII.GetBytes("auiu?\r\n");
                await _stream.WriteAsync(cmd, 0, cmd.Length);

                string uResp = await ReadMultiLinesAsync(1000); // 1초간 수집
                foreach (Match m in Regex.Matches(uResp, @"CH(\d)\s+UNITS\s+STR:\s*([^\r\n]+)"))
                    if (int.TryParse(m.Groups[1].Value, out int i) && i >= 1 && i <= 4)
                        ChannelUnits[i - 1] = m.Groups[2].Value.Trim();

                OnStatusChanged?.Invoke("Metadata Ready.");
            }
            catch (Exception ex)
            {
                OnStatusChanged?.Invoke($"Meta Fail: {ex.Message}");
            }
        }

        private async Task<string> ReadLineAsync(NetworkStream s, int timeoutMs)
        {
            // 타임아웃 기능이 포함된 한 줄 읽기
            using (var cts = new System.Threading.CancellationTokenSource(timeoutMs))
            {
                try
                {
                    List<byte> b = new List<byte>();
                    byte[] buf = new byte[1];
                    while (true)
                    {
                        int read = await s.ReadAsync(buf, 0, 1, cts.Token);
                        if (read == 0) break;
                        if (buf[0] == '\n') break;
                        b.Add(buf[0]);
                    }
                    return Encoding.ASCII.GetString(b.ToArray()).Trim();
                }
                catch { return null; }
            }
        }

        private async Task<string> ReadMultiLinesAsync(int ms)
        {
            // 지정된 시간(ms) 동안 들어오는 모든 데이터를 읽음
            StringBuilder sb = new StringBuilder();
            byte[] buf = new byte[4096];
            long s = DateTimeOffset.Now.ToUnixTimeMilliseconds();
            while (DateTimeOffset.Now.ToUnixTimeMilliseconds() - s < ms)
            {
                if (_client != null && _client.Available > 0)
                {
                    int r = await _stream.ReadAsync(buf, 0, buf.Length);
                    sb.Append(Encoding.ASCII.GetString(buf, 0, r));
                }
                else await Task.Delay(50);
            }
            return sb.ToString();
        }

        private void ProcessReadData(string res)
        {
            // 예: READ: 1.23, 4.56, !RANGE!, 0.00;
            var m = Regex.Match(res, @"READ:([^;]+)");
            if (!m.Success) return;

            string[] raw = m.Groups[1].Value.Split(',');
            string now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            List<string> clean = new List<string>();
            List<double> vals = new List<double>();

            foreach (var r in raw)
            {
                string c = r.Trim();
                if (c == "!RANGE!") c = "0"; // 에러 값 처리
                clean.Add(c);
            }

            for (int i = 0; i < 4; i++)
                vals.Add(double.TryParse((i < clean.Count ? clean[i] : "0"), out double d) ? d : 0);

            CurrentData = new DeviceData
            {
                Timestamp = now,
                V1 = vals[0],
                V2 = vals[1],
                V3 = vals[2],
                V4 = vals[3],
                RawValues = clean
            };
        }

        private void Cleanup()
        {
            try { _stream?.Close(); } catch { }
            try { _client?.Close(); } catch { }
            _client = null;
        }
    }
}