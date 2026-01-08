using System;
using System.Drawing;
using System.Windows.Forms;
using System.IO;
using System.Threading.Tasks;
using System.Text;
using System.IO.Ports;
using System.Diagnostics;

namespace PumpTest1
{
    public partial class Form1 : Form
    {
        private string _configFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.txt");

        private bool _isLogging = false;

        // 연결 상태 플래그
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

        // [MKS]
        private ComboBox _cboMksPort, _cboMksUnit;
        private Button _btnMksRefresh, _btnMksUnitApply;
        private Button _btnMksConnect;
        private Label _lblMksLight;
        private Label[] _mksValueLabels;

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

            // TCP 이벤트
            _tcpService.OnStatusChanged += (msg) => UpdateStatus($"[TCP] {msg}");
            _tcpService.OnChannelInfoReady += Tcp_OnChannelInfoReady;
            _tcpService.OnError += (msg) => {
                UpdateStatus($"[TCP Err] {msg}");
                UpdateLight(_lblTcpLight, Color.Red);
            };

            // MKS 이벤트
            _mksService.OnStatusChanged += (msg) => UpdateStatus($"[MKS] {msg}");
            _mksService.OnError += (msg) => {
                UpdateStatus($"[MKS Err] {msg}");
                UpdateLight(_lblMksLight, Color.Red);
            };

            // UI 갱신용 타이머 (시작 시 바로 가동)
            _timerElapsed = new System.Windows.Forms.Timer { Interval = 500 };
            _timerElapsed.Tick += Timer_Tick;
            _timerElapsed.Start();

            // 설정 로드
            LoadSettings();
        }

        // =======================================================================
        // [FMS] 연결 / 해제
        // =======================================================================
        private async void BtnTcpConnect_Click(object sender, EventArgs e)
        {
            if (_isTcpConnected)
            {
                // DISCONNECT
                _tcpService.Stop();
                _isTcpConnected = false;

                _btnTcpConnect.Text = "CONNECT";
                _btnTcpConnect.BackColor = Color.LightGray;

                ToggleFmsInputs(true);
                ResetTcpUI();
                UpdateStatus("FMS Disconnected.");
            }
            else
            {
                // CONNECT
                _tcpService.IpAddress = _txtTcpIp.Text;
                if (int.TryParse(_txtTcpPort.Text, out int port)) _tcpService.Port = port;
                _tcpService.IntervalMs = 500;

                _tcpService.Start();
                _isTcpConnected = true;

                _btnTcpConnect.Text = "DISCONNECT";
                _btnTcpConnect.BackColor = Color.Salmon;

                ToggleFmsInputs(false);
                UpdateLight(_lblTcpLight, Color.Gold);

                UpdateStatus("FMS Connecting...");

                if (!_timerElapsed.Enabled) _timerElapsed.Start();

                await WaitLabelsAsync();
            }
        }

        // =======================================================================
        // [MKS] 연결 / 해제
        // =======================================================================
        private void BtnMksConnect_Click(object sender, EventArgs e)
        {
            if (_isMksConnected)
            {
                // DISCONNECT
                _mksService.Stop();
                _isMksConnected = false;

                _btnMksConnect.Text = "CONNECT";
                _btnMksConnect.BackColor = Color.LightGray;

                ToggleMksInputs(true);
                ResetMksUI();
                UpdateStatus("MKS Disconnected.");
            }
            else
            {
                // CONNECT
                if (string.IsNullOrEmpty(_cboMksPort.Text)) { MessageBox.Show("Select MKS Port"); return; }

                _mksService.PortName = _cboMksPort.Text;
                _mksService.IntervalMs = 500;

                _mksService.Start();
                _isMksConnected = true;

                _btnMksConnect.Text = "DISCONNECT";
                _btnMksConnect.BackColor = Color.Salmon;

                ToggleMksInputs(false);
                UpdateLight(_lblMksLight, Color.Gold);
                UpdateStatus("MKS Connecting...");

                if (!_timerElapsed.Enabled) _timerElapsed.Start();
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

        // =======================================================================
        // 로깅(저장) 로직
        // =======================================================================
        private void BtnStartLog_Click(object sender, EventArgs e)
        {
            if (!_isTcpConnected && !_isMksConnected)
            {
                if (MessageBox.Show("No devices connected. Start logging anyway?", "Warning", MessageBoxButtons.YesNo) == DialogResult.No)
                    return;
            }

            _isLogging = true;
            _logStartTime = DateTime.Now;

            if (!_timerElapsed.Enabled) _timerElapsed.Start();

            _btnStart.Enabled = false;
            _btnStop.Enabled = true;

            _btnTcpConnect.Enabled = true;
            _btnMksConnect.Enabled = true;

            _ = Task.Run(() => MainLoggingLoop((int)_numInterval.Value));
            UpdateStatus("Logging Started.");
        }

        private void BtnStopLog_Click(object sender, EventArgs e)
        {
            _isLogging = false;

            _btnStart.Enabled = true;
            _btnStop.Enabled = false;
            UpdateStatus("Logging Stopped.");
        }

        private async Task MainLoggingLoop(int intervalMs)
        {
            string fileName = $"Integrated_Log_{DateTime.Now:yyyyMMdd_HHmmss}.csv";
            string fullPath = Path.Combine(_txtPath.Text, fileName);

            StringBuilder sbHeader = new StringBuilder();
            sbHeader.Append("Timestamp");

            // FMS Header
            var tcpLabels = _tcpService.ChannelLabels;
            var tcpUnits = _tcpService.ChannelUnits;
            for (int i = 0; i < 4; i++)
            {
                string lbl = (tcpLabels != null && i < tcpLabels.Length) ? tcpLabels[i] : $"CH{i + 1}";
                string unt = (tcpUnits != null && i < tcpUnits.Length) ? tcpUnits[i] : "";
                lbl = lbl.Replace(",", "_"); unt = unt.Replace(",", "_");
                sbHeader.Append($",FMS_{lbl}({unt})");
            }

            // [수정됨] MKS Header: 단위를 헤더에 포함하고 Unit 컬럼 제거
            string mUnit = _mksService.CurrentUnit;
            if (string.IsNullOrEmpty(mUnit)) mUnit = "Unit";
            // 예: MKS_Ch1(PASCAL),MKS_Ch2(PASCAL),MKS_Ch3(PASCAL)
            sbHeader.Append($",MKS_Ch1({mUnit}),MKS_Ch2({mUnit}),MKS_Ch3({mUnit})");

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

                        // TCP Data
                        if (tcpData != null && tcpData.RawValues != null)
                        {
                            for (int i = 0; i < 4; i++)
                                sb.Append("," + ((i < tcpData.RawValues.Count) ? tcpData.RawValues[i] : "0"));
                        }
                        else
                        {
                            sb.Append(",0,0,0,0");
                        }

                        // MKS Data
                        if (mksData != null)
                        {
                            sb.Append($",{(mksData.Ch1.HasValue ? mksData.Ch1.ToString() : "Err")}");
                            sb.Append($",{(mksData.Ch2.HasValue ? mksData.Ch2.ToString() : "Err")}");
                            sb.Append($",{(mksData.Ch3.HasValue ? mksData.Ch3.ToString() : "Err")}");
                            // [수정됨] Unit 값 저장 부분 제거 (헤더에 포함되었으므로)
                        }
                        else
                        {
                            sb.Append(",Err,Err,Err"); // Unit 컬럼에 대한 None 제거
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

        // =======================================================================
        // UI 갱신 (Timer Tick)
        // =======================================================================
        private void Timer_Tick(object sender, EventArgs e)
        {
            UpdateRealtimeUI();

            if (_isLogging)
            {
                TimeSpan ts = DateTime.Now - _logStartTime;
                _lblElapsed.Text = $"{ts:hh\\:mm\\:ss}";
            }
            else
            {
                _lblElapsed.Text = "Ready";
            }
        }

        private void UpdateRealtimeUI()
        {
            if (IsDisposed) return;

            // TCP UI Update
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

            // MKS UI Update
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

        private void ResetTcpUI()
        {
            UpdateLight(_lblTcpLight, Color.Gray);
            ResetTcpLabelsInternal();
        }

        private void ResetMksUI()
        {
            UpdateLight(_lblMksLight, Color.Gray);
            ResetMksLabelsInternal();
        }

        private void ResetTcpLabelsInternal()
        {
            if (_tcpValueLabels == null) return;
            foreach (var lbl in _tcpValueLabels) if (lbl != null) lbl.Text = "----";
        }

        private void ResetMksLabelsInternal()
        {
            if (_mksValueLabels == null) return;
            foreach (var lbl in _mksValueLabels) if (lbl != null) lbl.Text = "----";
        }

        // =======================================================================
        // UI 생성
        // =======================================================================
        private void InitializeUnifiedUI()
        {
            this.Text = "Integrated Pump Monitor v1.0";
            this.Size = new Size(1150, 600);
            this.MinimumSize = new Size(1150, 600);

            TableLayoutPanel mainLayout = new TableLayoutPanel();
            mainLayout.Dock = DockStyle.Fill;
            mainLayout.ColumnCount = 3;
            mainLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33f));
            mainLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33f));
            mainLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33f));
            mainLayout.Parent = this;

            // =================================================================================
            // [Col 1] FMS
            // =================================================================================
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

            _tcpNameLabels = new Label[4]; _tcpValueLabels = new Label[4];
            int y = 150;
            for (int i = 0; i < 4; i++)
            {
                _tcpNameLabels[i] = new Label { Text = $"CH{i + 1}", Location = new Point(20, y), AutoSize = true, Font = new Font("Arial", 12), Parent = grpFms };
                _tcpValueLabels[i] = new Label
                {
                    Text = "----",
                    Location = new Point(100, y),
                    Size = new Size(220, 30),
                    AutoSize = false,
                    TextAlign = ContentAlignment.MiddleRight,
                    Font = new Font("Arial", 14, FontStyle.Bold),
                    ForeColor = Color.Blue,
                    Parent = grpFms
                };
                y += 60;
            }

            // =================================================================================
            // [Col 2] MKS
            // =================================================================================
            GroupBox grpMks = new GroupBox { Text = "MKS 946", Dock = DockStyle.Fill, Font = new Font("Arial", 10, FontStyle.Bold) };
            mainLayout.Controls.Add(grpMks, 1, 0);

            Panel pnlMksTop = new Panel { Location = new Point(10, 25), Size = new Size(330, 150), BorderStyle = BorderStyle.FixedSingle, Parent = grpMks };
            new Label { Text = "Port:", Location = new Point(10, 15), AutoSize = true, Font = new Font("Arial", 9), Parent = pnlMksTop };
            _cboMksPort = new ComboBox { Location = new Point(50, 12), Width = 100, Font = new Font("Arial", 9), Parent = pnlMksTop };

            _btnMksRefresh = new Button { Text = "R", Location = new Point(155, 11), Width = 30, Height = 23, Font = new Font("Arial", 8), Parent = pnlMksTop };
            _btnMksRefresh.Click += (s, e) => {
                _cboMksPort.Items.Clear();
                _cboMksPort.Items.AddRange(SerialPort.GetPortNames());
                if (_cboMksPort.Items.Count > 0) _cboMksPort.SelectedIndex = _cboMksPort.Items.Count - 1;
            };

            new Label { Text = "Unit:", Location = new Point(10, 45), AutoSize = true, Font = new Font("Arial", 9), Parent = pnlMksTop };
            _cboMksUnit = new ComboBox { Location = new Point(50, 42), Width = 80, Font = new Font("Arial", 9), Parent = pnlMksTop };
            _cboMksUnit.Items.AddRange(new object[] { "PASCAL", "TORR", "MBAR", "MICRON" });
            _cboMksUnit.SelectedIndex = 0;
            _btnMksUnitApply = new Button { Text = "Set", Location = new Point(135, 41), Width = 50, Height = 25, Font = new Font("Arial", 9), Parent = pnlMksTop };
            _btnMksUnitApply.Click += BtnMksUnitApply_Click;

            _btnMksConnect = new Button { Text = "CONNECT", Location = new Point(10, 80), Size = new Size(180, 50), BackColor = Color.LightGray, Font = new Font("Arial", 10, FontStyle.Bold), Parent = pnlMksTop };
            _btnMksConnect.Click += BtnMksConnect_Click;

            new Label { Text = "STATUS", Location = new Point(200, 85), AutoSize = true, Font = new Font("Arial", 8), Parent = pnlMksTop };
            _lblMksLight = new Label { Location = new Point(210, 105), Size = new Size(20, 20), BackColor = Color.Gray, BorderStyle = BorderStyle.FixedSingle, Parent = pnlMksTop };

            _mksValueLabels = new Label[4];
            y = 190;
            for (int i = 1; i <= 3; i++)
            {
                new Label { Text = $"CH{i}", Location = new Point(20, y), AutoSize = true, Font = new Font("Arial", 12), Parent = grpMks };
                _mksValueLabels[i] = new Label
                {
                    Text = "----",
                    Location = new Point(80, y),
                    Size = new Size(250, 30),
                    AutoSize = false,
                    TextAlign = ContentAlignment.MiddleRight,
                    Font = new Font("Arial", 14, FontStyle.Bold),
                    ForeColor = Color.DarkRed,
                    Parent = grpMks
                };
                y += 60;
            }

            // =================================================================================
            // [Col 3] Logging
            // =================================================================================
            GroupBox grpLog = new GroupBox { Text = "Data Logging", Dock = DockStyle.Fill, Font = new Font("Arial", 10, FontStyle.Bold) };
            mainLayout.Controls.Add(grpLog, 2, 0);

            // [중요] 여기서 pnlLog를 선언 및 초기화합니다.
            Panel pnlLog = new Panel { Location = new Point(20, 30), Size = new Size(300, 250), BorderStyle = BorderStyle.FixedSingle, Parent = grpLog };

            // [중요] 아래 컨트롤들은 Parent = pnlLog 를 사용하므로, 위에서 pnlLog가 먼저 생성되어야 합니다.
            new Label { Text = "Save Path:", Location = new Point(10, 15), AutoSize = true, Font = new Font("Arial", 9), Parent = pnlLog };
            _txtPath = new TextBox { Text = AppDomain.CurrentDomain.BaseDirectory, Location = new Point(10, 35), Width = 230, Font = new Font("Arial", 8), Parent = pnlLog };
            _btnBrowse = new Button { Text = "...", Location = new Point(245, 34), Width = 30, Height = 22, Parent = pnlLog };
            _btnBrowse.Click += (s, e) => { using (var d = new FolderBrowserDialog()) if (d.ShowDialog() == DialogResult.OK) _txtPath.Text = d.SelectedPath; };

            _btnOpenFolder = new Button { Text = "Open Folder", Location = new Point(10, 65), Width = 265, Height = 25, Font = new Font("Arial", 9), Parent = pnlLog };
            _btnOpenFolder.Click += (s, e) => { try { Process.Start("explorer.exe", _txtPath.Text); } catch { } };

            new Label { Text = "Interval (ms):", Location = new Point(10, 105), AutoSize = true, Font = new Font("Arial", 9), Parent = pnlLog };
            _numInterval = new NumericUpDown { Minimum = 100, Maximum = 60000, Value = 500, Increment = 100, Location = new Point(100, 103), Width = 80, Font = new Font("Arial", 9), Parent = pnlLog };

            _btnStart = new Button { Text = "START", Location = new Point(10, 145), Size = new Size(130, 50), BackColor = Color.LightGreen, Font = new Font("Arial", 10, FontStyle.Bold), Parent = pnlLog };
            _btnStart.Click += BtnStartLog_Click;

            _btnStop = new Button { Text = "STOP", Location = new Point(145, 145), Size = new Size(130, 50), BackColor = Color.LightPink, Enabled = false, Font = new Font("Arial", 10, FontStyle.Bold), Parent = pnlLog };
            _btnStop.Click += BtnStopLog_Click;

            _lblElapsed = new Label { Text = "Ready", Location = new Point(100, 210), AutoSize = true, Font = new Font("Arial", 11, FontStyle.Bold), ForeColor = Color.DarkSlateBlue, Parent = pnlLog };
            _lblStatus = new Label { Dock = DockStyle.Bottom, Height = 30, Text = "Ready", BackColor = Color.FromArgb(240, 240, 240), TextAlign = ContentAlignment.MiddleLeft, Parent = this };

            _btnMksRefresh.PerformClick();
        }

        private void LoadSettings()
        {
            try
            {
                if (File.Exists(_configFile))
                {
                    foreach (var line in File.ReadAllLines(_configFile))
                    {
                        var p = line.Split('='); if (p.Length < 2) continue;
                        string k = p[0].Trim(), v = p[1].Trim();
                        if (k == "TCP_IP") _txtTcpIp.Text = v;
                        else if (k == "TCP_PORT") _txtTcpPort.Text = v;
                        else if (k == "LOG_PATH" && Directory.Exists(v)) _txtPath.Text = v;
                    }
                }
            }
            catch { }
        }

        private void SaveSettings()
        {
            try
            {
                StringBuilder sb = new StringBuilder();
                sb.AppendLine($"MKS_PORT={_cboMksPort.Text}");
                sb.AppendLine($"TCP_IP={_txtTcpIp.Text}");
                sb.AppendLine($"TCP_PORT={_txtTcpPort.Text}");
                sb.AppendLine($"LOG_PATH={_txtPath.Text}");
                File.WriteAllText(_configFile, sb.ToString());
            }
            catch { }
        }

        private void Form1_Load(object sender, EventArgs e)
        {
            _btnTcpConnect.PerformClick();
            if (_cboMksPort.Items.Count > 0) _btnMksConnect.PerformClick();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            SaveSettings();
            _isLogging = false;
            _tcpService?.Stop();
            _mksService?.Stop();
            base.OnFormClosing(e);
        }

        private void UpdateStatus(string msg) { if (!IsDisposed) Invoke(new Action(() => _lblStatus.Text = msg)); }
        private void UpdateLight(Label light, Color color) { if (!IsDisposed && light != null) Invoke(new Action(() => light.BackColor = color)); }
        private void BtnMksUnitApply_Click(object sender, EventArgs e) { if (!string.IsNullOrEmpty(_cboMksPort.Text)) _mksService.SetUnit(_cboMksUnit.Text, _cboMksPort.Text); }

        private void ToggleFmsInputs(bool en) { _txtTcpIp.Enabled = _txtTcpPort.Enabled = en; }
        private void ToggleMksInputs(bool en) { _cboMksPort.Enabled = _btnMksRefresh.Enabled = en; }

        private void Tcp_OnChannelInfoReady()
        {
            if (IsDisposed) return;
            Invoke(new Action(() => {
                var l = _tcpService.ChannelLabels;
                for (int i = 0; i < 4; i++) if (_tcpNameLabels[i] != null && i < l.Length) _tcpNameLabels[i].Text = l[i];
            }));
        }
    }
}