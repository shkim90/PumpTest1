using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using System.Text.RegularExpressions;

namespace PumpTest1
{
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
        public string[] ChannelLabels { get; private set; } = new string[] { "CH1", "CH2", "CH3", "CH4" };
        public string[] ChannelUnits { get; private set; } = new string[] { "", "", "", "" };

        public bool Connected => _client != null && _client.Connected;

        public event Action<string> OnError;
        public event Action<string> OnStatusChanged;
        public event Action OnChannelInfoReady;

        public void Start()
        {
            if (_isRunning) return;
            _isRunning = true;
            Task.Run(CommunicationLoop);
        }

        public void Stop()
        {
            _isRunning = false;
            Cleanup();
            // Stop 시 CurrentData 초기화 (선택사항, UI에서 처리하므로 필수 아님)
            CurrentData = null;
            OnStatusChanged?.Invoke("Stopped.");
        }

        private async Task CommunicationLoop()
        {
            while (_isRunning)
            {
                try
                {
                    if (!Connected)
                    {
                        OnStatusChanged?.Invoke($"Connecting to {IpAddress}:{Port}...");
                        _client = new TcpClient();
                        var connectTask = _client.ConnectAsync(IpAddress, Port);
                        if (await Task.WhenAny(connectTask, Task.Delay(3000)) != connectTask)
                        {
                            throw new Exception("Connection Timeout");
                        }
                        await connectTask;

                        _stream = _client.GetStream();
                        _stream.ReadTimeout = 3000;
                        OnStatusChanged?.Invoke("Connected.");

                        await FetchMetadata();
                        OnChannelInfoReady?.Invoke();
                    }

                    if (Connected)
                    {
                        long start = DateTimeOffset.Now.ToUnixTimeMilliseconds();

                        if (_client.Available > 0)
                        {
                            byte[] trash = new byte[_client.Available];
                            await _stream.ReadAsync(trash, 0, trash.Length);
                        }

                        byte[] cmd = Encoding.ASCII.GetBytes("ar\r\n");
                        await _stream.WriteAsync(cmd, 0, cmd.Length);

                        string response = await ReadLineAsync(_stream, 2000);

                        if (!string.IsNullOrEmpty(response) && response.Contains("READ:"))
                        {
                            ProcessReadData(response);
                        }
                        else
                        {
                            // 응답이 이상하면 연결 체크를 위해 예외 던지기 가능하나 일단 패스
                        }

                        int elapsed = (int)(DateTimeOffset.Now.ToUnixTimeMilliseconds() - start);
                        int wait = IntervalMs - elapsed;
                        if (wait > 0) await Task.Delay(wait);
                    }
                }
                catch (Exception ex)
                {
                    OnError?.Invoke($"Error: {ex.Message}");
                    Cleanup(); // 연결 끊고 재접속 대기
                    CurrentData = null; // 데이터 초기화
                    await Task.Delay(3000);
                }
            }
        }

        private async Task FetchMetadata()
        {
            try
            {
                OnStatusChanged?.Invoke("Reading Labels...");
                byte[] cmd = Encoding.ASCII.GetBytes("adil?\r\n");
                await _stream.WriteAsync(cmd, 0, cmd.Length);

                string lResp = await ReadMultiLinesAsync(1000);
                foreach (Match m in Regex.Matches(lResp, @"CH(\d)\s+LABEL:\s*""([^""]+)"""))
                    if (int.TryParse(m.Groups[1].Value, out int i) && i >= 1 && i <= 4)
                        ChannelLabels[i - 1] = m.Groups[2].Value.Trim();

                OnStatusChanged?.Invoke("Reading Units...");
                cmd = Encoding.ASCII.GetBytes("auiu?\r\n");
                await _stream.WriteAsync(cmd, 0, cmd.Length);

                string uResp = await ReadMultiLinesAsync(1000);
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
            var m = Regex.Match(res, @"READ:([^;]+)");
            if (!m.Success) return;

            string[] raw = m.Groups[1].Value.Split(',');
            string now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            List<string> clean = new List<string>();
            List<double> vals = new List<double>();

            foreach (var r in raw)
            {
                string c = r.Trim();
                if (c == "!RANGE!") c = "0";
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