using System;
using System.Drawing;
using System.Windows.Forms;
using System.IO;
using System.Threading.Tasks;
using System.Text;
using System.IO.Ports;
using System.Diagnostics;
using System.Linq; // LINQ 사용 (Any 등)

namespace PumpTest1
{
    public partial class Form1 : Form
    {
        private string _configFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.txt");

        private bool _isLogging = false;

        private bool _isTcpConnected = false;
        private bool _isMksConnected = false;

        private TcpDeviceService _tcpService;
        private MksDeviceService _mksService;

        // === UI Controls ===
        // [FMS]
        private TextBox _txtTcpIp, _txtTcpPort;
        private Button _btnTcpConnect;
        private Label _lblTcpLight;
        private Label[] _tcpNameLabels;
        private Label[] _tcpValueLabels;
        private CheckBox _chkFmsAll;      // [NEW] FMS 전체 선택
        private CheckBox[] _chkFmsChs;    // [NEW] FMS 개별 체크박스

        // [MKS]
        private ComboBox _cboMksPort, _cboMksUnit;
        private Button _btnMksRefresh, _btnMksUnitApply;
        private Button _btnMksConnect;
        private Label _lblMksLight;
        private Label[] _mksValueLabels;
        private CheckBox _chkMksAll;      // [NEW] MKS 전체 선택
        private CheckBox[] _chkMksChs;    // [NEW] MKS 개별 체크박스

        // [Logging]
        private TextBox _txtPath;
        private NumericUpDown _numInterval;
        private Button _btnBrowse, _btnOpenFolder;
        private Button _btnStart, _btnStop;
        private Label _lblElapsed;
        private Label _lblStatus;

        private System.Windows.Forms.Timer _timerElapsed;
        private DateTime _logStartTime;

        public Form1()
        {
            InitializeComponent();
            InitializeUnifiedUI();

            _tcpService = new TcpDeviceService();
            _mksService = new MksDeviceService();

            _tcpService.OnStatusChanged += (msg) => UpdateStatus($"[TCP] {msg}");
            _tcpService.OnChannelInfoReady += Tcp_OnChannelInfoReady;
            _tcpService.OnError += (msg) => {
                UpdateStatus($"[TCP Err] {msg}");
                UpdateLight(_lblTcpLight, Color.Red);
            };

            _mksService.OnStatusChanged += (msg) => UpdateStatus($"[MKS] {msg}");
            _mksService.OnError += (msg) => {
                Invoke(new Action(() => HandleMksError(msg)));
            };

            _timerElapsed = new System.Windows.Forms.Timer { Interval = 500 };
            _timerElapsed.Tick += Timer_Tick;
            _timerElapsed.Start();

            LoadSettings();
        }

        private void HandleMksError(string msg)
        {
            UpdateStatus($"[MKS Err] {msg}");
            if (_isMksConnected)
            {
                BtnMksConnect_Click(null, null);
                MessageBox.Show($"MKS Port Error: {msg}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        // =======================================================================
        // [FMS] Connect
        // =======================================================================
        private async void BtnTcpConnect_Click(object sender, EventArgs e)
        {
            if (_isTcpConnected)
            {
                _tcpService.Stop();
                _isTcpConnected = false;
                _btnTcpConnect.Text = "CONNECT";
                _btnTcpConnect.BackColor = Color.LightGray;
                ToggleFmsInputs(true);
                ResetTcpUI();
                UpdateStatus("FMS Disconnected.");
                if (_isLogging) _btnTcpConnect.Enabled = false;
            }
            else
            {
                _tcpService.IpAddress = _txtTcpIp.Text;
                if (int.TryParse(_txtTcpPort.Text, out int port)) _tcpService.Port = port;
                _tcpService.IntervalMs = (int)_numInterval.Value;
                _tcpService.Start();
                _isTcpConnected = true;
                _btnTcpConnect.Text = "DISCONNECT";
                _btnTcpConnect.BackColor = Color.Salmon;
                ToggleFmsInputs(false);
                UpdateLight(_lblTcpLight, Color.Gold);
                UpdateStatus("FMS Connecting...");
                await WaitLabelsAsync();
            }
        }

        // =======================================================================
        // [MKS] Connect
        // =======================================================================
        private void BtnMksConnect_Click(object sender, EventArgs e)
        {
            if (_isMksConnected)
            {
                _mksService.Stop();
                _isMksConnected = false;
                _btnMksConnect.Text = "CONNECT";
                _btnMksConnect.BackColor = Color.LightGray;
                ToggleMksInputs(true);
                ResetMksUI();
                UpdateStatus("MKS Disconnected.");
                if (_isLogging) _btnMksConnect.Enabled = false;
            }
            else
            {
                if (string.IsNullOrEmpty(_cboMksPort.Text)) { MessageBox.Show("Select MKS Port"); return; }
                _mksService.PortName = _cboMksPort.Text;
                _mksService.IntervalMs = (int)_numInterval.Value;
                _mksService.Start();
                _isMksConnected = true;
                _btnMksConnect.Text = "DISCONNECT";
                _btnMksConnect.BackColor = Color.Salmon;
                ToggleMksInputs(false);
                UpdateLight(_lblMksLight, Color.Gold);
                UpdateStatus("MKS Connecting...");
            }
        }

        private async Task WaitLabelsAsync()
        {
            int retry = 0;
            while (retry < 30)
            {
                if (_tcpService.Connected && _tcpService.ChannelLabels[0] != "CH1") break;
                await Task.Delay(100);
                retry++;
            }
        }

        private async void CboMksUnit_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (_isLogging || !_cboMksUnit.Enabled) return;
            string targetUnit = _cboMksUnit.Text;
            string port = _cboMksPort.Text;
            if (string.IsNullOrEmpty(port)) return;

            bool wasConnected = _isMksConnected;
            if (wasConnected)
            {
                _mksService.Stop();
                await Task.Delay(500);
            }

            bool ok = _mksService.SetUnit(targetUnit, port);
            if (ok) UpdateStatus($"Unit changed to {targetUnit}");
            else UpdateStatus("Failed to change unit.");

            if (wasConnected) _mksService.Start();
        }

        // =======================================================================
        // [Logging Logic] 선택된 채널만 로깅
        // =======================================================================
        private void BtnStartLog_Click(object sender, EventArgs e)
        {
            if (!_isTcpConnected && !_isMksConnected)
            {
                if (MessageBox.Show("No devices connected. Start logging anyway?", "Warning", MessageBoxButtons.YesNo) == DialogResult.No)
                    return;
            }

            // [NEW] 채널 선택 확인 로직
            bool anyFmsSelected = _chkFmsChs.Any(c => c.Checked);
            bool anyMksSelected = _chkMksChs.Any(c => c.Checked);

            if (_isTcpConnected && !anyFmsSelected)
            {
                if (MessageBox.Show("FMS is connected but NO channels are selected.\nContinue without FMS data?", "Confirm", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.No) return;
            }
            if (_isMksConnected && !anyMksSelected)
            {
                if (MessageBox.Show("MKS is connected but NO channels are selected.\nContinue without MKS data?", "Confirm", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.No) return;
            }

            // 로깅 시작 시점의 체크박스 상태를 캡처 (도중에 바꿔도 파일 포맷 유지 위해)
            bool[] fmsLogFlags = _chkFmsChs.Select(c => c.Checked).ToArray();
            bool[] mksLogFlags = _chkMksChs.Select(c => c.Checked).ToArray();

            _isLogging = true;
            _logStartTime = DateTime.Now;
            _btnStart.Enabled = false;
            _btnStop.Enabled = true;
            _cboMksUnit.Enabled = false;
            
            // 체크박스들도 로깅 중엔 변경 못하게 잠금 (파일 헤더 깨짐 방지)
            ToggleCheckboxes(false);

            _btnTcpConnect.Enabled = true;
            _btnMksConnect.Enabled = true;

            _ = Task.Run(() => MainLoggingLoop((int)_numInterval.Value, fmsLogFlags, mksLogFlags));
            UpdateStatus("Logging Started.");
        }

        private void BtnStopLog_Click(object sender, EventArgs e)
        {
            _isLogging = false;
            _btnStart.Enabled = true;
            _btnStop.Enabled = false;
            _cboMksUnit.Enabled = true;
            ToggleCheckboxes(true); // 체크박스 잠금 해제

            _btnTcpConnect.Enabled = true;
            _btnMksConnect.Enabled = true;
            UpdateStatus("Logging Stopped.");
        }

        private async Task MainLoggingLoop(int intervalMs, bool[] fmsFlags, bool[] mksFlags)
        {
            string fileName = $"Integrated_Log_{DateTime.Now:yyyyMMdd_HHmmss}.csv";
            string fullPath = Path.Combine(_txtPath.Text, fileName);

            StringBuilder sbHeader = new StringBuilder();
            sbHeader.Append("Timestamp");

            // [NEW] 선택된 FMS 채널만 헤더 생성
            var tcpLabels = _tcpService.ChannelLabels;
            var tcpUnits = _tcpService.ChannelUnits;
            for (int i = 0; i < 4; i++)
            {
                if (fmsFlags[i]) // 체크된 것만
                {
                    string lbl = (tcpLabels != null && i < tcpLabels.Length) ? tcpLabels[i] : $"CH{i + 1}";
                    string unt = (tcpUnits != null && i < tcpUnits.Length) ? tcpUnits[i] : "";
                    lbl = lbl.Replace(",", "_"); unt = unt.Replace(",", "_");
                    sbHeader.Append($",FMS_{lbl}({unt})");
                }
            }

            // [NEW] 선택된 MKS 채널만 헤더 생성
            string mUnit = _mksService.CurrentUnit;
            if (string.IsNullOrEmpty(mUnit)) mUnit = "Unit";
            
            for(int i=0; i<4; i++)
            {
                if(mksFlags[i])
                {
                    sbHeader.Append($",MKS_Ch{i+1}({mUnit})");
                }
            }

            try
            {
                using (var fs = new FileStream(fullPath, FileMode.Create, FileAccess.Write, FileShare.Read))
                using (var sw = new StreamWriter(fs, Encoding.UTF8))
                {
                    await sw.WriteLineAsync(sbHeader.ToString());

                    while (_isLogging)
                    {
                        long loopStart = DateTimeOffset.Now.ToUnixTimeMilliseconds();
                        var tcpData = (_isTcpConnected && _tcpService.Connected) ? _tcpService.CurrentData : null;
                        var mksData = (_isMksConnected) ? _mksService.CurrentData : null;

                        StringBuilder sb = new StringBuilder();
                        sb.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"));

                        // FMS Data Logging
                        if (tcpData != null && tcpData.RawValues != null)
                        {
                            for (int i = 0; i < 4; i++)
                            {
                                if (fmsFlags[i]) // 체크된 채널만 기록
                                    sb.Append("," + ((i < tcpData.RawValues.Count) ? tcpData.RawValues[i] : "0"));
                            }
                        }
                        else
                        {
                            // 연결 끊김 or 데이터 없음 -> 체크된 채널 수만큼 0 기록
                            for (int i = 0; i < 4; i++) if (fmsFlags[i]) sb.Append(",0");
                        }

                        // MKS Data Logging
                        if (mksData != null)
                        {
                            double?[] vals = { mksData.Ch1, mksData.Ch2, mksData.Ch3, mksData.Ch4 };
                            for(int i=0; i<4; i++)
                            {
                                if(mksFlags[i])
                                    sb.Append($",{(vals[i].HasValue ? vals[i].ToString() : "Err")}");
                            }
                        }
                        else
                        {
                            for(int i=0; i<4; i++) if(mksFlags[i]) sb.Append(",Err");
                        }

                        await sw.WriteLineAsync(sb.ToString());

                        int wait = intervalMs - (int)(DateTimeOffset.Now.ToUnixTimeMilliseconds() - loopStart);
                        if (wait > 0) await Task.Delay(wait);
                    }
                }
            }
            catch (Exception ex)
            {
                Invoke(new Action(() => {
                    MessageBox.Show($"File Error: {ex.Message}");
                    if (_isLogging) BtnStopLog_Click(null, null);
                }));
            }
        }

        private void Timer_Tick(object sender, EventArgs e)
        {
            UpdateRealtimeUI();
            if (_isLogging)
            {
                TimeSpan ts = DateTime.Now - _logStartTime;
                _lblElapsed.Text = $"{ts.Days}d {ts.Hours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2}";
            }
            else _lblElapsed.Text = "Ready";
        }

        // =======================================================================
        // UI Initialization & Helper Methods
        // =======================================================================
        private void InitializeUnifiedUI()
        {
            this.Text = "Integrated Pump Monitor v1.4";
            // [개선 1] 창 크기 고정 및 최대화 방지
            this.FormBorderStyle = FormBorderStyle.FixedSingle;
            this.MaximizeBox = false;
            this.Size = new Size(1150, 600);

            TableLayoutPanel mainLayout = new TableLayoutPanel();
            mainLayout.Dock = DockStyle.Fill;
            mainLayout.ColumnCount = 3;
            mainLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33f));
            mainLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33f));
            mainLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33f));
            mainLayout.Parent = this;

            // [Col 1] FMS
            GroupBox grpFms = new GroupBox { Text = "FMS", Dock = DockStyle.Fill, Font = new Font("Arial", 10, FontStyle.Bold) };
            mainLayout.Controls.Add(grpFms, 0, 0);

            Panel pnlFmsTop = new Panel { Location = new Point(10, 25), Size = new Size(330, 110), BorderStyle = BorderStyle.FixedSingle, Parent = grpFms };
            new Label { Text = "IP:", Location = new Point(10, 15), AutoSize = true, Font = new Font("Arial", 9), Parent = pnlFmsTop };
            _txtTcpIp = new TextBox { Text = "192.168.1.180", Location = new Point(40, 12), Width = 110, Font = new Font("Arial", 9), Parent = pnlFmsTop };
            new Label { Text = "Port:", Location = new Point(160, 15), AutoSize = true, Font = new Font("Arial", 9), Parent = pnlFmsTop };
            _txtTcpPort = new TextBox { Text = "101", Location = new Point(200, 12), Width = 50, Font = new Font("Arial", 9), Parent = pnlFmsTop };
            _btnTcpConnect = new Button { Text = "CONNECT", Location = new Point(10, 45), Size = new Size(180, 50), BackColor = Color.LightGray, Font = new Font("Arial", 10, FontStyle.Bold), Parent = pnlFmsTop };
            _btnTcpConnect.Click += BtnTcpConnect_Click;
            new Label { Text = "STATUS", Location = new Point(200, 50), AutoSize = true, Font = new Font("Arial", 8), Parent = pnlFmsTop };
            _lblTcpLight = new Label { Location = new Point(210, 70), Size = new Size(20, 20), BackColor = Color.Gray, BorderStyle = BorderStyle.FixedSingle, Parent = pnlFmsTop };

            // [NEW] FMS 전체 선택 체크박스
            _chkFmsAll = new CheckBox { Text = "ALL", Location = new Point(20, 145), AutoSize = true, Checked = true, Font = new Font("Arial", 9, FontStyle.Bold), Parent = grpFms };
            _chkFmsAll.CheckedChanged += (s, e) => { foreach (var c in _chkFmsChs) c.Checked = _chkFmsAll.Checked; };

            _tcpNameLabels = new Label[4]; _tcpValueLabels = new Label[4];
            _chkFmsChs = new CheckBox[4]; // [NEW] 개별 체크박스 배열
            int y = 170;
            for (int i = 0; i < 4; i++)
            {
                // 체크박스 추가
                _chkFmsChs[i] = new CheckBox { Location = new Point(20, y + 5), AutoSize = true, Checked = true, Parent = grpFms };
                _tcpNameLabels[i] = new Label { Text = $"CH{i + 1}", Location = new Point(45, y), AutoSize = true, Font = new Font("Arial", 12), Parent = grpFms };
                _tcpValueLabels[i] = new Label { Text = "----", Location = new Point(120, y), Size = new Size(200, 30), AutoSize = false, TextAlign = ContentAlignment.MiddleRight, Font = new Font("Arial", 14, FontStyle.Bold), ForeColor = Color.Blue, Parent = grpFms };
                y += 50;
            }

            // [Col 2] MKS
            GroupBox grpMks = new GroupBox { Text = "MKS 946", Dock = DockStyle.Fill, Font = new Font("Arial", 10, FontStyle.Bold) };
            mainLayout.Controls.Add(grpMks, 1, 0);

            Panel pnlMksTop = new Panel { Location = new Point(10, 25), Size = new Size(330, 150), BorderStyle = BorderStyle.FixedSingle, Parent = grpMks };
            new Label { Text = "Port:", Location = new Point(10, 15), AutoSize = true, Font = new Font("Arial", 9), Parent = pnlMksTop };
            _cboMksPort = new ComboBox { Location = new Point(50, 12), Width = 100, Font = new Font("Arial", 9), Parent = pnlMksTop };
            _btnMksRefresh = new Button { Text = "R", Location = new Point(155, 11), Width = 30, Height = 23, Font = new Font("Arial", 8), Parent = pnlMksTop };
            _btnMksRefresh.Click += (s, e) => { _cboMksPort.Items.Clear(); _cboMksPort.Items.AddRange(SerialPort.GetPortNames()); if (_cboMksPort.Items.Count > 0) _cboMksPort.SelectedIndex = _cboMksPort.Items.Count - 1; };

            new Label { Text = "Unit:", Location = new Point(10, 45), AutoSize = true, Font = new Font("Arial", 9), Parent = pnlMksTop };
            _cboMksUnit = new ComboBox { Location = new Point(50, 42), Width = 135, Font = new Font("Arial", 9), Parent = pnlMksTop };
            _cboMksUnit.Items.AddRange(new object[] { "PASCAL", "TORR", "MBAR", "MICRON" });
            _cboMksUnit.SelectedIndex = 0;
            _cboMksUnit.SelectedIndexChanged += CboMksUnit_SelectedIndexChanged;

            _btnMksConnect = new Button { Text = "CONNECT", Location = new Point(10, 80), Size = new Size(180, 50), BackColor = Color.LightGray, Font = new Font("Arial", 10, FontStyle.Bold), Parent = pnlMksTop };
            _btnMksConnect.Click += BtnMksConnect_Click;
            new Label { Text = "STATUS", Location = new Point(200, 85), AutoSize = true, Font = new Font("Arial", 8), Parent = pnlMksTop };
            _lblMksLight = new Label { Location = new Point(210, 105), Size = new Size(20, 20), BackColor = Color.Gray, BorderStyle = BorderStyle.FixedSingle, Parent = pnlMksTop };

            // [NEW] MKS 전체 선택 체크박스
            _chkMksAll = new CheckBox { Text = "ALL", Location = new Point(20, 185), AutoSize = true, Checked = true, Font = new Font("Arial", 9, FontStyle.Bold), Parent = grpMks };
            _chkMksAll.CheckedChanged += (s, e) => { foreach (var c in _chkMksChs) c.Checked = _chkMksAll.Checked; };

            _mksValueLabels = new Label[5];
            _chkMksChs = new CheckBox[4]; // [NEW]
            y = 210;
            for (int i = 1; i <= 4; i++)
            {
                // 체크박스 추가 (배열 인덱스 0~3 사용)
                _chkMksChs[i-1] = new CheckBox { Location = new Point(20, y + 5), AutoSize = true, Checked = true, Parent = grpMks };
                new Label { Text = $"CH{i}", Location = new Point(45, y), AutoSize = true, Font = new Font("Arial", 12), Parent = grpMks };
                _mksValueLabels[i] = new Label { Text = "----", Location = new Point(100, y), Size = new Size(230, 30), AutoSize = false, TextAlign = ContentAlignment.MiddleRight, Font = new Font("Arial", 14, FontStyle.Bold), ForeColor = Color.DarkRed, Parent = grpMks };
                y += 50;
            }

            // [Col 3] Logging
            GroupBox grpLog = new GroupBox { Text = "Data Logging", Dock = DockStyle.Fill, Font = new Font("Arial", 10, FontStyle.Bold) };
            mainLayout.Controls.Add(grpLog, 2, 0);

            Panel pnlLog = new Panel { Location = new Point(20, 30), Size = new Size(300, 250), BorderStyle = BorderStyle.FixedSingle, Parent = grpLog };
            new Label { Text = "Save Path:", Location = new Point(10, 15), AutoSize = true, Font = new Font("Arial", 9), Parent = pnlLog };
            _txtPath = new TextBox { Text = AppDomain.CurrentDomain.BaseDirectory, Location = new Point(10, 35), Width = 230, Font = new Font("Arial", 8), Parent = pnlLog };
            _btnBrowse = new Button { Text = "...", Location = new Point(245, 34), Width = 30, Height = 22, Parent = pnlLog };
            _btnBrowse.Click += (s, e) => { using (var d = new FolderBrowserDialog()) if (d.ShowDialog() == DialogResult.OK) _txtPath.Text = d.SelectedPath; };
            _btnOpenFolder = new Button { Text = "Open Folder", Location = new Point(10, 65), Width = 265, Height = 25, Font = new Font("Arial", 9), Parent = pnlLog };
            _btnOpenFolder.Click += (s, e) => { try { Process.Start("explorer.exe", _txtPath.Text); } catch { } };

            new Label { Text = "Interval (ms):", Location = new Point(10, 105), AutoSize = true, Font = new Font("Arial", 9), Parent = pnlLog };
            _numInterval = new NumericUpDown { Minimum = 100, Maximum = 60000, Value = 500, Increment = 100, Location = new Point(10, 125), Width = 80, Parent = pnlLog };

            _btnStart = new Button { Text = "START", Location = new Point(10, 160), Size = new Size(130, 50), BackColor = Color.LightGreen, Font = new Font("Arial", 10, FontStyle.Bold), Parent = pnlLog };
            _btnStart.Click += BtnStartLog_Click;
            _btnStop = new Button { Text = "STOP", Location = new Point(145, 160), Size = new Size(130, 50), BackColor = Color.LightPink, Enabled = false, Font = new Font("Arial", 10, FontStyle.Bold), Parent = pnlLog };
            _btnStop.Click += BtnStopLog_Click;

            _lblElapsed = new Label { Text = "Ready", Location = new Point(100, 220), AutoSize = true, Font = new Font("Arial", 11, FontStyle.Bold), ForeColor = Color.DarkSlateBlue, Parent = pnlLog };
            _lblStatus = new Label { Dock = DockStyle.Bottom, Height = 30, Text = "Ready", BackColor = Color.FromArgb(240, 240, 240), TextAlign = ContentAlignment.MiddleLeft, Parent = this };

            _btnMksRefresh.PerformClick();
        }

        private void ToggleCheckboxes(bool enable)
        {
            _chkFmsAll.Enabled = enable;
            foreach (var c in _chkFmsChs) c.Enabled = enable;
            _chkMksAll.Enabled = enable;
            foreach (var c in _chkMksChs) c.Enabled = enable;
        }

        private void UpdateRealtimeUI()
        {
            if (IsDisposed) return;

            // TCP UI
            if (_isTcpConnected)
            {
                if (_tcpService.Connected && _tcpService.CurrentData != null)
                {
                    UpdateLight(_lblTcpLight, Color.Lime);
                    var d = _tcpService.CurrentData;
                    for (int i = 0; i < 4; i++)
                    {
                        string val = (i < d.RawValues.Count) ? d.RawValues[i] : "---";
                        string unit = (i < _tcpService.ChannelUnits.Length) ? _tcpService.ChannelUnits[i] : "";
                        if (_tcpValueLabels[i] != null) _tcpValueLabels[i].Text = $"{val} {unit}";
                    }
                }
                else
                {
                    UpdateLight(_lblTcpLight, Color.Red);
                    if (!_tcpService.Connected) ResetTcpLabelsInternal();
                }
            }
            else
            {
                ResetTcpLabelsInternal();
                UpdateLight(_lblTcpLight, Color.Gray);
            }

            // MKS UI
            if (_isMksConnected)
            {
                var d = _mksService.CurrentData;
                if (d != null)
                {
                    UpdateLight(_lblMksLight, Color.Lime);
                    string unit = d.Unit ?? "";
                    if (_mksValueLabels[1] != null) _mksValueLabels[1].Text = (d.Ch1.HasValue ? $"{d.Ch1} {unit}" : $"Err {unit}");
                    if (_mksValueLabels[2] != null) _mksValueLabels[2].Text = (d.Ch2.HasValue ? $"{d.Ch2} {unit}" : $"Err {unit}");
                    if (_mksValueLabels[3] != null) _mksValueLabels[3].Text = (d.Ch3.HasValue ? $"{d.Ch3} {unit}" : $"Err {unit}");
                    if (_mksValueLabels[4] != null) _mksValueLabels[4].Text = (d.Ch4.HasValue ? $"{d.Ch4} {unit}" : $"Err {unit}");
                }
                else
                {
                    UpdateLight(_lblMksLight, Color.Red);
                }
            }
            else
            {
                ResetMksLabelsInternal();
                UpdateLight(_lblMksLight, Color.Gray);
            }
        }

        // --- Helpers ---
        private void LoadSettings()
        {
            try { if (File.Exists(_configFile)) foreach (var line in File.ReadAllLines(_configFile)) { var p = line.Split('='); if (p.Length >= 2) { if (p[0].Trim() == "TCP_IP") _txtTcpIp.Text = p[1].Trim(); else if (p[0].Trim() == "TCP_PORT") _txtTcpPort.Text = p[1].Trim(); else if (p[0].Trim() == "LOG_PATH" && Directory.Exists(p[1].Trim())) _txtPath.Text = p[1].Trim(); } } } catch { }
        }
        private void SaveSettings()
        {
            try { File.WriteAllText(_configFile, $"MKS_PORT={_cboMksPort.Text}\nTCP_IP={_txtTcpIp.Text}\nTCP_PORT={_txtTcpPort.Text}\nLOG_PATH={_txtPath.Text}\n"); } catch { }
        }
        private void Form1_Load(object sender, EventArgs e) { _btnTcpConnect.PerformClick(); if (_cboMksPort.Items.Count > 0) _btnMksConnect.PerformClick(); }
        private void UpdateStatus(string msg) { if (!IsDisposed) Invoke(new Action(() => _lblStatus.Text = msg)); }
        private void UpdateLight(Label light, Color color) { if (!IsDisposed && light != null) Invoke(new Action(() => light.BackColor = color)); }
        private void ToggleFmsInputs(bool en) { _txtTcpIp.Enabled = _txtTcpPort.Enabled = en; }
        private void ToggleMksInputs(bool en) { _cboMksPort.Enabled = _btnMksRefresh.Enabled = en; }
        private void Tcp_OnChannelInfoReady() { if (IsDisposed) return; Invoke(new Action(() => { var l = _tcpService.ChannelLabels; for (int i = 0; i < 4; i++) if (_tcpNameLabels[i] != null && i < l.Length) _tcpNameLabels[i].Text = l[i]; })); }
        private void ResetTcpUI() { UpdateLight(_lblTcpLight, Color.Gray); ResetTcpLabelsInternal(); }
        private void ResetMksUI() { UpdateLight(_lblMksLight, Color.Gray); ResetMksLabelsInternal(); }
        private void ResetTcpLabelsInternal() { if (_tcpValueLabels == null) return; foreach (var lbl in _tcpValueLabels) if (lbl != null) lbl.Text = "----"; }
        private void ResetMksLabelsInternal() { if (_mksValueLabels == null) return; foreach (var lbl in _mksValueLabels) if (lbl != null) lbl.Text = "----"; }
        private void BtnMksUnitApply_Click(object sender, EventArgs e) { if (!string.IsNullOrEmpty(_cboMksPort.Text)) _mksService.SetUnit(_cboMksUnit.Text, _cboMksPort.Text); }
    }
}