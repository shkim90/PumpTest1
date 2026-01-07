using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using System.Text.RegularExpressions;

namespace PumpTest1  // ★ 프로젝트 이름으로 통일
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
        public int IntervalMs { get; set; } = 1000;
        public string LogFolderPath { get; set; } = AppDomain.CurrentDomain.BaseDirectory;

        private bool _isRunning = false;
        private TcpClient _client;
        private NetworkStream _stream;
        private StreamWriter _csvWriter;

        public event Action<DeviceData> OnDataReceived;
        public event Action<string> OnError;
        public event Action<string> OnStatusChanged;
        public event Action<string[]> OnChannelInfoReceived;

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
            OnStatusChanged?.Invoke("Stopped.");
        }

        private async Task CommunicationLoop()
        {
            if (!Directory.Exists(LogFolderPath))
            {
                try { Directory.CreateDirectory(LogFolderPath); }
                catch (Exception ex) { OnError?.Invoke($"Dir Error: {ex.Message}"); _isRunning = false; return; }
            }

            try
            {
                OnStatusChanged?.Invoke($"Connecting to {IpAddress}:{Port}...");
                _client = new TcpClient();
                var connectTask = _client.ConnectAsync(IpAddress, Port);
                if (await Task.WhenAny(connectTask, Task.Delay(3000)) != connectTask) throw new Exception("Timeout");
                await connectTask;
                _stream = _client.GetStream();
                _stream.ReadTimeout = 3000;
                OnStatusChanged?.Invoke("Connected.");
            }
            catch (Exception ex) { OnError?.Invoke($"Conn Failed: {ex.Message}"); _isRunning = false; Cleanup(); return; }

            string[] headers = await GetChannelMetadata();
            OnChannelInfoReceived?.Invoke(headers);

            string fileName = $"tcp_log_{DateTime.Now:yyyyMMdd_HHmmss}.csv";
            string fullPath = Path.Combine(LogFolderPath, fileName);

            try
            {
                var fs = new FileStream(fullPath, FileMode.Create, FileAccess.Write, FileShare.Read);
                _csvWriter = new StreamWriter(fs, Encoding.UTF8) { AutoFlush = true };
                await _csvWriter.WriteLineAsync("Timestamp," + string.Join(",", headers));
                OnStatusChanged?.Invoke($"Logging to {fileName}");
            }
            catch (Exception ex) { OnError?.Invoke($"CSV Error: {ex.Message}"); _isRunning = false; Cleanup(); return; }

            byte[] buffer = new byte[1024];

            while (_isRunning)
            {
                long start = DateTimeOffset.Now.ToUnixTimeMilliseconds();
                try
                {
                    if (_client != null && _client.Connected)
                    {
                        if (_client.Available > 0) await _stream.ReadAsync(buffer, 0, _client.Available);
                        byte[] cmd = Encoding.ASCII.GetBytes("ar\r\n");
                        await _stream.WriteAsync(cmd, 0, cmd.Length);
                        string response = await ReadLineAsync(_stream);
                        if (response.Contains("READ:")) ProcessReadData(response);
                    }
                    else throw new Exception("Disconnected");
                }
                catch (Exception ex) { OnError?.Invoke($"Error: {ex.Message}"); _isRunning = false; }

                int wait = IntervalMs - (int)(DateTimeOffset.Now.ToUnixTimeMilliseconds() - start);
                if (wait > 0) await Task.Delay(wait);
            }
            Cleanup();
        }

        private async Task<string[]> GetChannelMetadata()
        {
            string[] labels = { "CH1", "CH2", "CH3", "CH4" };
            string[] units = { "", "", "", "" };
            try
            {
                OnStatusChanged?.Invoke("Reading Meta...");
                byte[] cmd = Encoding.ASCII.GetBytes("adil?\r\n");
                await _stream.WriteAsync(cmd, 0, cmd.Length);
                string lResp = await ReadMultiLinesAsync(500);
                foreach (Match m in Regex.Matches(lResp, @"CH(\d)\s+LABEL:\s*""([^""]+)"""))
                    if (int.TryParse(m.Groups[1].Value, out int i) && i >= 1 && i <= 4) labels[i - 1] = m.Groups[2].Value.Trim();

                cmd = Encoding.ASCII.GetBytes("auiu?\r\n");
                await _stream.WriteAsync(cmd, 0, cmd.Length);
                string uResp = await ReadMultiLinesAsync(500);
                foreach (Match m in Regex.Matches(uResp, @"CH(\d)\s+UNITS\s+STR:\s*([^\r\n]+)"))
                    if (int.TryParse(m.Groups[1].Value, out int i) && i >= 1 && i <= 4) units[i - 1] = m.Groups[2].Value.Trim();
            }
            catch { }

            string[] ret = new string[4];
            for (int i = 0; i < 4; i++) ret[i] = $"{labels[i]}{(string.IsNullOrEmpty(units[i]) ? "" : $"({units[i]})")}";
            return ret;
        }

        private async Task<string> ReadLineAsync(NetworkStream s)
        {
            List<byte> b = new List<byte>(); byte[] buf = new byte[1];
            while (true) { if (await s.ReadAsync(buf, 0, 1) == 0 || buf[0] == '\n') break; b.Add(buf[0]); }
            return Encoding.ASCII.GetString(b.ToArray()).Trim();
        }

        private async Task<string> ReadMultiLinesAsync(int ms)
        {
            StringBuilder sb = new StringBuilder(); byte[] buf = new byte[4096]; long s = DateTimeOffset.Now.ToUnixTimeMilliseconds();
            while (DateTimeOffset.Now.ToUnixTimeMilliseconds() - s < ms)
            {
                if (_client.Available > 0) { int r = await _stream.ReadAsync(buf, 0, buf.Length); sb.Append(Encoding.ASCII.GetString(buf, 0, r)); }
                else await Task.Delay(50);
            }
            return sb.ToString();
        }

        private void ProcessReadData(string res)
        {
            var m = Regex.Match(res, @"READ:([^;]+)"); if (!m.Success) return;
            string[] raw = m.Groups[1].Value.Split(',');
            string now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            List<string> clean = new List<string>(); List<double> vals = new List<double>();
            foreach (var r in raw) { string c = r.Trim(); if (c == "!RANGE!") c = "0"; clean.Add(c); }
            for (int i = 0; i < 4; i++) vals.Add(double.TryParse((i < clean.Count ? clean[i] : "0"), out double d) ? d : 0);

            if (_csvWriter?.BaseStream.CanWrite == true) _csvWriter.WriteLine($"{now},{string.Join(",", clean.GetRange(0, Math.Min(4, clean.Count)))}");
            OnDataReceived?.Invoke(new DeviceData { Timestamp = now, V1 = vals[0], V2 = vals[1], V3 = vals[2], V4 = vals[3], RawValues = clean });
        }
        private void Cleanup() { try { _client?.Close(); _csvWriter?.Close(); } catch { } }
    }
}