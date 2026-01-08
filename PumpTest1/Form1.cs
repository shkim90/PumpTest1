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

        // 연결 상태 플래그 (UI 버튼 상태 관리용)
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
        // private Label _lblMksCurrentUnit; // [삭제] 개별 표시로 변경
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

            // [핵심] UI 갱신용 타이머 (0.5초 간격)
            _timerElapsed = new System.Windows.Forms.Timer { Interval = 500 };
            _timerElapsed.Tick += Timer_Tick;

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
                // DISCONNECT 로직
                _tcpService.Stop();
                _isTcpConnected = false;

                _btnTcpConnect.Text = "CONNECT FMS";
                _btnTcpConnect.BackColor = Color.LightGray;

                ToggleFmsInputs(true);
                ResetTcpUI(); // [중요] 즉시 화면 초기화
                UpdateStatus("FMS Disconnected.");
            }
            else
            {
                // CONNECT 로직
                _tcpService.IpAddress = _txtTcpIp.Text;
                if (int.TryParse(_txtTcpPort.Text, out int port)) _tcpService.Port = port;
                _tcpService.IntervalMs = 500; // Live Monitoring 기본 속도

                _tcpService.Start();
                _isTcpConnected = true;

                _btnTcpConnect.Text = "DISCONNECT";
                _btnTcpConnect.BackColor = Color.Salmon;

                ToggleFmsInputs(false);
                UpdateLight(_lblTcpLight, Color.Gold); // 연결 시도 중 노란불

                UpdateStatus("FMS Connecting...");
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
                // DISCONNECT 로직
                _mksService.Stop();
                _isMksConnected = false;

                _btnMksConnect.Text = "CONNECT MKS";
                _btnMksConnect.BackColor = Color.LightGray;

                ToggleMksInputs(true);
                ResetMksUI(); // [중요] 즉시 화면 초기화
                UpdateStatus("MKS Disconnected.");
            }
            else
            {
                // CONNECT 로직
                if (string.IsNullOrEmpty(_cboMksPort.Text)) { MessageBox.Show("Select MKS Port"); return; }

                _mksService.PortName = _cboMksPort.Text;
                _mksService.IntervalMs = 500; // Live Monitoring 기본 속도

                _mksService.Start();
                _isMksConnected = true;

                _btnMksConnect.Text = "DISCONNECT";
                _btnMksConnect.BackColor = Color.Salmon;

                ToggleMksInputs(false);
                UpdateLight(_lblMksLight, Color.Gold); // 연결 시도 중 노란불
                UpdateStatus("MKS Connecting...");
            }
        }

        // FMS 라벨 업데이트 대기
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
            // 하나라도 연결되어 있어야 로깅 가능 (원하는 경우 둘 다 끊겨도 로깅은 돌릴 수 있으나 데이터가 없으므로 체크)
            if (!_isTcpConnected && !_isMksConnected)
            {
                if (MessageBox.Show("No devices connected. Start logging anyway?", "Warning", MessageBoxButtons.YesNo) == DialogResult.No)
                    return;
            }

            _isLogging = true;
            _logStartTime = DateTime.Now;
            _timerElapsed.Start(); // 타이머 확실히 시작

            _btnStart.Enabled = false;
            _btnStop.Enabled = true;

            // 로깅 중에도 연결/해제 자유롭게 가능하도록 버튼 Enabled 유지
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

            // Header 작성
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

            // MKS Header (MKS는 Live Unit이 바뀌면 데이터에도 반영되지만 헤더는 초기값 기준 혹은 고정)
            sbHeader.Append(",MKS_Ch1,MKS_Ch2,MKS_Ch3,MKS_Unit");

            try
            {
                using (var fs = new FileStream(fullPath, FileMode.Create, FileAccess.Write, FileShare.Read))
                using (var sw = new StreamWriter(fs, Encoding.UTF8))
                {
                    await sw.WriteLineAsync(sbHeader.ToString());

                    while (_isLogging)
                    {
                        long loopStart = DateTimeOffset.Now.ToUnixTimeMilliseconds();

                        // [핵심] 현재 Live Data 스냅샷 가져오기
                        // 화면에 ---- 가 떠있다면(연결 안됨) 여기서도 데이터를 0이나 Err로 처리
                        var tcpData = (_isTcpConnected && _tcpService.Connected) ? _tcpService.CurrentData : null;
                        var mksData = (_isMksConnected) ? _mksService.CurrentData : null;

                        StringBuilder sb = new StringBuilder();
                        sb.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"));

                        // TCP Data Logging
                        if (tcpData != null && tcpData.RawValues != null)
                        {
                            for (int i = 0; i < 4; i++)
                                sb.Append("," + ((i < tcpData.RawValues.Count) ? tcpData.RawValues[i] : "0"));
                        }
                        else
                        {
                            // 연결 끊김 -> 0 또는 빈값 처리 (요청에 따라 ---- 대신 0 기록 등)
                            sb.Append(",0,0,0,0");
                        }

                        // MKS Data Logging
                        if (mksData != null)
                        {
                            sb.Append($",{(mksData.Ch1.HasValue ? mksData.Ch1.ToString() : "Err")}");
                            sb.Append($",{(mksData.Ch2.HasValue ? mksData.Ch2.ToString() : "Err")}");
                            sb.Append($",{(mksData.Ch3.HasValue ? mksData.Ch3.ToString() : "Err")}");
                            sb.Append($",{mksData.Unit}");
                        }
                        else
                        {
                            sb.Append(",Err,Err,Err,None");
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
            // 1. Live Data UI Update
            UpdateRealtimeUI();

            // 2. Logging Status
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
                    // 연결 시도 중이거나 끊김
                    UpdateLight(_lblTcpLight, Color.Red); // 혹은 Gold
                    // 만약 연결이 끊겼다면 값을 지운다
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
                // MKS는 SerialPort.IsOpen 등으로 연결 확인, 데이터가 갱신되고 있는지 확인
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

        // 외부 호출용 초기화 (버튼 클릭 시)
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

        // 내부 라벨 텍스트 변경
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
        // UI 생성 (기존 코드 수정)
        // =======================================================================
        private void InitializeUnifiedUI()
        {
            this.Text = "Integrated Pump Monitor v3.0 (Live)";
            this.Size = new Size(1150, 600);

            TableLayoutPanel mainLayout = new TableLayoutPanel();
            mainLayout.Dock = DockStyle.Fill;
            mainLayout.ColumnCount = 3;
            mainLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33f));
            mainLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33f));
            mainLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33f));
            mainLayout.Parent = this;

            // [Col 1] FMS
            GroupBox grpFms = new GroupBox { Text = "FMS System (TCP)", Dock = DockStyle.Fill, Font = new Font("Arial", 10, FontStyle.Bold) };
            mainLayout.Controls.Add(grpFms, 0, 0);

            Panel pnlFmsTop = new Panel { Location = new Point(10, 25), Size = new Size(330, 110), BorderStyle = BorderStyle.FixedSingle, Parent = grpFms };
            new Label { Text = "IP:", Location = new Point(10, 15), AutoSize = true, Font = new Font("Arial", 9), Parent = pnlFmsTop };
            _txtTcpIp = new TextBox { Text = "192.168.1.180", Location = new Point(40, 12), Width = 110, Font = new Font("Arial", 9), Parent = pnlFmsTop };
            new Label { Text = "Port:", Location = new Point(160, 15), AutoSize = true, Font = new Font("Arial", 9), Parent = pnlFmsTop };
            _txtTcpPort = new TextBox { Text = "101", Location = new Point(200, 12), Width = 50, Font = new Font("Arial", 9), Parent = pnlFmsTop };

            _btnTcpConnect = new Button { Text = "CONNECT FMS", Location = new Point(10, 45), Size = new Size(180, 50), BackColor = Color.LightGray, Font = new Font("Arial", 10, FontStyle.Bold), Parent = pnlFmsTop };
            _btnTcpConnect.Click += BtnTcpConnect_Click;

            new Label { Text = "STATUS", Location = new Point(200, 50), AutoSize = true, Font = new Font("Arial", 8), Parent = pnlFmsTop };
            _lblTcpLight = new Label { Location = new Point(210, 70), Size = new Size(20, 20), BackColor = Color.Gray, BorderStyle = BorderStyle.FixedSingle, Parent = pnlFmsTop };

            _tcpNameLabels = new Label[4]; _tcpValueLabels = new Label[4];
            int y = 150;
            for (int i = 0; i < 4; i++)
            {
                _tcpNameLabels[i] = new Label { Text = $"CH{i + 1}", Location = new Point(20, y), AutoSize = true, Font = new Font("Arial", 12), Parent = grpFms };
                _tcpValueLabels[i] = new Label { Text = "----", Location = new Point(100, y), AutoSize = true, Font = new Font("Arial", 14, FontStyle.Bold), ForeColor = Color.Blue, Parent = grpFms };
                y += 60;
            }

            // [Col 2] MKS
            GroupBox grpMks = new GroupBox { Text = "MKS 946 (Serial)", Dock = DockStyle.Fill, Font = new Font("Arial", 10, FontStyle.Bold) };
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

            _btnMksConnect = new Button { Text = "CONNECT MKS", Location = new Point(10, 80), Size = new Size(180, 50), BackColor = Color.LightGray, Font = new Font("Arial", 10, FontStyle.Bold), Parent = pnlMksTop };
            _btnMksConnect.Click += BtnMksConnect_Click;

            new Label { Text = "STATUS", Location = new Point(200, 85), AutoSize = true, Font = new Font("Arial", 8), Parent = pnlMksTop };
            _lblMksLight = new Label { Location = new Point(210, 105), Size = new Size(20, 20), BackColor = Color.Gray, BorderStyle = BorderStyle.FixedSingle, Parent = pnlMksTop };

            // [변경] 큰 유닛 라벨 제거, 대신 개별 수치 라벨 옆에 표시
            _mksValueLabels = new Label[4];
            y = 190; // 위치 조정
            for (int i = 1; i <= 3; i++)
            {
                new Label { Text = $"CH{i}", Location = new Point(20, y), AutoSize = true, Font = new Font("Arial", 12), Parent = grpMks };
                _mksValueLabels[i] = new Label
                {
                    Text = "----",
                    Location = new Point(80, y),
                    Size = new Size(250, 30), // 너비를 넓혀서 유닛까지 표시
                    AutoSize = false,
                    TextAlign = ContentAlignment.MiddleLeft, // 왼쪽 정렬
                    Font = new Font("Arial", 14, FontStyle.Bold), // 폰트 사이즈 조정
                    ForeColor = Color.DarkRed,
                    Parent = grpMks
                };
                y += 60;
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
            _numInterval = new NumericUpDown { Minimum = 100, Maximum = 60000, Value = 500, Increment = 100, Location = new Point(100, 103), Width = 80, Font = new Font("Arial", 9), Parent = pnlLog };

            _btnStart = new Button { Text = "REC START", Location = new Point(10, 145), Size = new Size(130, 50), BackColor = Color.LightGreen, Font = new Font("Arial", 10, FontStyle.Bold), Parent = pnlLog };
            _btnStart.Click += BtnStartLog_Click;

            _btnStop = new Button { Text = "REC STOP", Location = new Point(145, 145), Size = new Size(130, 50), BackColor = Color.LightPink, Enabled = false, Font = new Font("Arial", 10, FontStyle.Bold), Parent = pnlLog };
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
            // 프로그램 시작 시 타이머 가동 (UI 업데이트용)
            _timerElapsed.Start();

            // 시작 시 자동 연결 (선택 사항)
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