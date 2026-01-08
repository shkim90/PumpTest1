using System;
using System.Drawing;
using System.Windows.Forms;
using System.IO;
using System.Threading.Tasks;
using System.Text;
using System.IO.Ports; // 포트 검색용

namespace PumpTest1
{
    public partial class Form1 : Form
    {
        // [설정] 자주 쓰는 MKS 포트가 있다면 여기에 적으세요.
        private string _defaultMksPort = "COM3";

        private bool _isLogging = false;

        private TcpDeviceService _tcpService;
        private MksDeviceService _mksService;

        // UI Controls
        private TextBox _txtTcpIp, _txtTcpPort;
        private ComboBox _cboMksPort;
        private Button _btnMksRefresh;
        private TextBox _txtPath;
        private NumericUpDown _numInterval;
        private Button _btnBrowse, _btnStart, _btnStop;
        private ComboBox _cboMksUnit;
        private Button _btnMksUnitApply;

        private Label[] _tcpNameLabels;
        private Label[] _tcpValueLabels;
        private Label[] _mksValueLabels;
        private Label _lblStatus;
        private Label _lblElapsed;
        private Label _lblTcpLight, _lblMksLight;

        private System.Windows.Forms.Timer _timerElapsed;
        private DateTime _logStartTime;

        public Form1()
        {
            InitializeComponent();
            InitializeUnifiedUI();

            // 서비스 초기화
            _tcpService = new TcpDeviceService();
            _mksService = new MksDeviceService();

            // TCP 이벤트 연결
            _tcpService.OnStatusChanged += (msg) => UpdateStatus($"[TCP] {msg}");
            _tcpService.OnChannelInfoReady += Tcp_OnChannelInfoReady;
            _tcpService.OnError += (msg) => {
                UpdateStatus($"[TCP Error] {msg}");
                UpdateLight(_lblTcpLight, Color.Red); // 에러시 빨간불
            };

            // MKS 이벤트 연결
            _mksService.OnStatusChanged += (msg) => UpdateStatus($"[MKS] {msg}");
            _mksService.OnError += (msg) => {
                UpdateStatus($"[MKS Error] {msg}");
                UpdateLight(_lblMksLight, Color.Red); // 에러시 빨간불
            };

            // 화면 갱신 타이머 (1초 -> 0.5초로 더 부드럽게)
            _timerElapsed = new System.Windows.Forms.Timer { Interval = 500 };
            _timerElapsed.Tick += Timer_Tick;
        }

        // START 버튼: 연결 시작 및 로깅 시작
        private async void BtnStart_Click(object sender, EventArgs e)
        {
            // 설정 적용
            _tcpService.IpAddress = _txtTcpIp.Text;
            if (int.TryParse(_txtTcpPort.Text, out int port)) _tcpService.Port = port;

            _mksService.PortName = _cboMksPort.Text;
            int interval = (int)_numInterval.Value;
            _tcpService.IntervalMs = interval;
            _mksService.IntervalMs = interval;

            ToggleInputs(false);

            // 서비스 시작 (내부에서 알아서 재접속함)
            _tcpService.Start();

            if (!string.IsNullOrEmpty(_cboMksPort.Text))
                _mksService.Start();

            // 상태등 일단 노란색(시도중)
            UpdateLight(_lblTcpLight, Color.Gold);
            UpdateLight(_lblMksLight, Color.Gold);

            // 라벨 수신 대기 (최대 3초만 기다리고 진행)
            UpdateStatus("Waiting for device info...");
            int retry = 0;
            while (retry < 30) // 3초
            {
                if (_tcpService.Connected && _tcpService.ChannelLabels[0] != "CH1") break; // 라벨 들어오면 통과
                await Task.Delay(100);
                retry++;
            }

            // 로깅 시작
            _isLogging = true;
            _logStartTime = DateTime.Now;
            _timerElapsed.Start();

            _ = Task.Run(() => MainLoggingLoop(interval));
            UpdateStatus("Logging Started.");
        }

        // STOP 버튼
        private void BtnStop_Click(object sender, EventArgs e)
        {
            _isLogging = false;
            _timerElapsed.Stop();

            _tcpService.Stop();
            _mksService.Stop();

            ToggleInputs(true);
            UpdateStatus("Stopped.");
            UpdateLight(_lblTcpLight, Color.Gray);
            UpdateLight(_lblMksLight, Color.Gray);

            ResetMonitorValues();
        }

        private async Task MainLoggingLoop(int intervalMs)
        {
            string fileName = $"Integrated_Log_{DateTime.Now:yyyyMMdd_HHmmss}.csv";
            string fullPath = Path.Combine(_txtPath.Text, fileName);

            // 헤더 작성 (현재 시점의 라벨 사용)
            StringBuilder sbHeader = new StringBuilder();
            sbHeader.Append("Timestamp");

            var tcpLabels = _tcpService.ChannelLabels;
            var tcpUnits = _tcpService.ChannelUnits;
            for (int i = 0; i < 4; i++)
            {
                string lbl = (tcpLabels != null && i < tcpLabels.Length) ? tcpLabels[i] : $"CH{i + 1}";
                string unt = (tcpUnits != null && i < tcpUnits.Length) ? tcpUnits[i] : "";
                sbHeader.Append($",FMS_{lbl}({unt})");
            }
            string mksUnit = _mksService.CurrentUnit;
            sbHeader.Append($",MKS_Ch1({mksUnit}),MKS_Ch2({mksUnit}),MKS_Ch3({mksUnit})");

            try
            {
                using (var fs = new FileStream(fullPath, FileMode.Create, FileAccess.Write, FileShare.Read))
                using (var sw = new StreamWriter(fs, Encoding.UTF8))
                {
                    await sw.WriteLineAsync(sbHeader.ToString());

                    while (_isLogging)
                    {
                        long loopStart = DateTimeOffset.Now.ToUnixTimeMilliseconds();

                        // 데이터 가져오기
                        var tcpData = _tcpService.CurrentData;
                        var mksData = _mksService.CurrentData;

                        // UI 업데이트
                        UpdateUI(tcpData, mksData);

                        // 파일 쓰기
                        StringBuilder sb = new StringBuilder();
                        sb.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"));

                        // TCP 값
                        if (tcpData != null && tcpData.RawValues != null)
                        {
                            foreach (var val in tcpData.RawValues) sb.Append("," + val);
                        }
                        else
                        {
                            sb.Append(",0,0,0,0"); // 끊겼을 때
                        }

                        // MKS 값
                        if (mksData != null)
                        {
                            sb.Append($",{(mksData.Ch1.HasValue ? mksData.Ch1.ToString() : "Err")}");
                            sb.Append($",{(mksData.Ch2.HasValue ? mksData.Ch2.ToString() : "Err")}");
                            sb.Append($",{(mksData.Ch3.HasValue ? mksData.Ch3.ToString() : "Err")}");
                        }
                        else
                        {
                            sb.Append(",Err,Err,Err");
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
                    BtnStop_Click(null, null);
                }));
            }
        }

        private void UpdateUI(DeviceData tcp, MksData mks)
        {
            if (IsDisposed) return;
            Invoke(new Action(() => {
                // TCP
                if (_tcpService.Connected && tcp != null)
                {
                    UpdateLight(_lblTcpLight, Color.Lime);
                    for (int i = 0; i < 4; i++)
                    {
                        string val = (i < tcp.RawValues.Count) ? tcp.RawValues[i] : "---";
                        string unit = (i < _tcpService.ChannelUnits.Length) ? _tcpService.ChannelUnits[i] : "";
                        if (_tcpValueLabels[i] != null) _tcpValueLabels[i].Text = $"{val} {unit}";
                    }
                }
                else
                {
                    UpdateLight(_lblTcpLight, Color.Red); // 끊김
                }

                // MKS
                if (mks != null)
                {
                    UpdateLight(_lblMksLight, Color.Lime);
                    string u = mks.Unit;
                    if (_mksValueLabels[1] != null) _mksValueLabels[1].Text = mks.Ch1.HasValue ? $"{mks.Ch1} {u}" : "Err";
                    if (_mksValueLabels[2] != null) _mksValueLabels[2].Text = mks.Ch2.HasValue ? $"{mks.Ch2} {u}" : "Err";
                    if (_mksValueLabels[3] != null) _mksValueLabels[3].Text = mks.Ch3.HasValue ? $"{mks.Ch3} {u}" : "Err";
                }
                else
                {
                    UpdateLight(_lblMksLight, Color.Red); // 끊김
                }
            }));
        }

        // 타이머: 경과 시간 표시
        private void Timer_Tick(object sender, EventArgs e)
        {
            if (_isLogging)
            {
                TimeSpan ts = DateTime.Now - _logStartTime;
                _lblElapsed.Text = $"Recording: {ts:hh\\:mm\\:ss}";
            }
        }

        // 라벨 이름 업데이트 (연결 시 1회 호출됨)
        private void Tcp_OnChannelInfoReady()
        {
            if (IsDisposed) return;
            Invoke(new Action(() => {
                var labels = _tcpService.ChannelLabels;
                for (int i = 0; i < 4; i++)
                    if (_tcpNameLabels[i] != null && i < labels.Length)
                        _tcpNameLabels[i].Text = labels[i] + ":";
            }));
        }

        private void InitializeUnifiedUI()
        {
            this.Text = "Integrated Pump Monitor";
            this.Size = new Size(950, 650);

            GroupBox grpSettings = new GroupBox { Text = "Settings", Location = new Point(10, 10), Size = new Size(910, 140), Parent = this };

            // 1. TCP UI
            Panel pnlTcp = new Panel { Location = new Point(10, 20), Size = new Size(250, 110), BorderStyle = BorderStyle.FixedSingle, Parent = grpSettings };
            new Label { Text = "FMS (TCP)", Location = new Point(5, 5), Font = new Font("Arial", 10, FontStyle.Bold), AutoSize = true, Parent = pnlTcp };
            new Label { Text = "IP:", Location = new Point(10, 35), AutoSize = true, Parent = pnlTcp };
            _txtTcpIp = new TextBox { Text = "192.168.1.180", Location = new Point(40, 32), Width = 100, Parent = pnlTcp };
            new Label { Text = "Port:", Location = new Point(150, 35), AutoSize = true, Parent = pnlTcp };
            _txtTcpPort = new TextBox { Text = "101", Location = new Point(185, 32), Width = 50, Parent = pnlTcp };

            // 2. MKS UI
            Panel pnlMks = new Panel { Location = new Point(270, 20), Size = new Size(260, 110), BorderStyle = BorderStyle.FixedSingle, Parent = grpSettings };
            new Label { Text = "MKS 946", Location = new Point(5, 5), Font = new Font("Arial", 10, FontStyle.Bold), AutoSize = true, Parent = pnlMks };
            new Label { Text = "Port:", Location = new Point(10, 35), AutoSize = true, Parent = pnlMks };
            _cboMksPort = new ComboBox { Location = new Point(50, 32), Width = 80, Parent = pnlMks };
            _btnMksRefresh = new Button { Text = "R", Location = new Point(135, 31), Width = 30, Height = 23, Parent = pnlMks };

            // 포트 새로고침 및 기본값 자동 선택 로직
            _btnMksRefresh.Click += (s, e) => {
                _cboMksPort.Items.Clear();
                string[] ports = SerialPort.GetPortNames();
                _cboMksPort.Items.AddRange(ports);
                // 기본 포트 자동 선택
                int idx = -1;
                for (int i = 0; i < ports.Length; i++)
                {
                    if (ports[i].Equals(_defaultMksPort, StringComparison.OrdinalIgnoreCase)) idx = i;
                }
                if (idx >= 0) _cboMksPort.SelectedIndex = idx;
                else if (_cboMksPort.Items.Count > 0) _cboMksPort.SelectedIndex = 0;
            };
            _btnMksRefresh.PerformClick(); // 시작 시 자동 실행

            new Label { Text = "Unit:", Location = new Point(10, 70), AutoSize = true, Parent = pnlMks };
            _cboMksUnit = new ComboBox { Location = new Point(50, 67), Width = 80, Parent = pnlMks };
            _cboMksUnit.Items.AddRange(new object[] { "PASCAL", "TORR", "MBAR", "MICRON" });
            _cboMksUnit.SelectedIndex = 0;
            _btnMksUnitApply = new Button { Text = "Set", Location = new Point(135, 66), Width = 50, Parent = pnlMks };
            _btnMksUnitApply.Click += BtnMksUnitApply_Click;

            // 3. Common UI
            Panel pnlCommon = new Panel { Location = new Point(540, 20), Size = new Size(360, 110), BorderStyle = BorderStyle.FixedSingle, Parent = grpSettings };
            new Label { Text = "Control", Location = new Point(5, 5), Font = new Font("Arial", 10, FontStyle.Bold), AutoSize = true, Parent = pnlCommon };
            new Label { Text = "Path:", Location = new Point(10, 35), AutoSize = true, Parent = pnlCommon };
            _txtPath = new TextBox { Text = AppDomain.CurrentDomain.BaseDirectory, Location = new Point(50, 32), Width = 180, Parent = pnlCommon };
            _btnBrowse = new Button { Text = "...", Location = new Point(235, 31), Width = 30, Parent = pnlCommon };
            _btnBrowse.Click += (s, e) => { using (var d = new FolderBrowserDialog()) if (d.ShowDialog() == DialogResult.OK) _txtPath.Text = d.SelectedPath; };
            new Label { Text = "Interval:", Location = new Point(10, 70), AutoSize = true, Parent = pnlCommon };
            _numInterval = new NumericUpDown { Minimum = 100, Maximum = 60000, Value = 500, Increment = 100, Location = new Point(70, 68), Width = 60, Parent = pnlCommon };
            _btnStart = new Button { Text = "START", Location = new Point(260, 10), Size = new Size(85, 40), BackColor = Color.LightGreen, Parent = pnlCommon };
            _btnStart.Click += BtnStart_Click;
            _btnStop = new Button { Text = "STOP", Location = new Point(260, 60), Size = new Size(85, 40), BackColor = Color.LightPink, Enabled = false, Parent = pnlCommon };
            _btnStop.Click += BtnStop_Click;

            // Monitor UI
            GroupBox grpMonitor = new GroupBox { Text = "Monitor", Location = new Point(10, 160), Size = new Size(910, 400), Parent = this };
            _lblElapsed = new Label { Text = "Ready", Location = new Point(720, 30), AutoSize = true, Font = new Font("Arial", 12, FontStyle.Bold), ForeColor = Color.DarkSlateBlue, Parent = grpMonitor };
            _lblTcpLight = new Label { Location = new Point(160, 32), Size = new Size(20, 20), BackColor = Color.Gray, BorderStyle = BorderStyle.FixedSingle, Parent = grpMonitor };
            _lblMksLight = new Label { Location = new Point(640, 32), Size = new Size(20, 20), BackColor = Color.Gray, BorderStyle = BorderStyle.FixedSingle, Parent = grpMonitor };

            new Label { Text = "[ FMS ]", Location = new Point(20, 30), AutoSize = true, Font = new Font("Arial", 14, FontStyle.Bold | FontStyle.Underline), Parent = grpMonitor };
            _tcpNameLabels = new Label[4]; _tcpValueLabels = new Label[4];
            int y = 70;
            for (int i = 0; i < 4; i++)
            {
                _tcpNameLabels[i] = new Label { Text = $"CH{i + 1}:", Location = new Point(30, y), AutoSize = true, Font = new Font("Arial", 14), Parent = grpMonitor };
                _tcpValueLabels[i] = new Label { Text = "---", Location = new Point(250, y), AutoSize = true, Font = new Font("Arial", 16, FontStyle.Bold), ForeColor = Color.Blue, Parent = grpMonitor };
                y += 50;
            }

            new Label { Text = "[ MKS 946 ]", Location = new Point(500, 30), AutoSize = true, Font = new Font("Arial", 14, FontStyle.Bold | FontStyle.Underline), Parent = grpMonitor };
            _mksValueLabels = new Label[4];
            y = 70;
            for (int i = 1; i <= 3; i++)
            {
                new Label { Text = $"Channel {i}:", Location = new Point(510, y), AutoSize = true, Font = new Font("Arial", 14), Parent = grpMonitor };
                _mksValueLabels[i] = new Label { Text = "---", Location = new Point(650, y), AutoSize = true, Font = new Font("Arial", 16, FontStyle.Bold), ForeColor = Color.DarkRed, Parent = grpMonitor };
                y += 50;
            }

            _lblStatus = new Label { Dock = DockStyle.Bottom, Height = 30, Text = "Ready", BackColor = Color.LightGray, TextAlign = ContentAlignment.MiddleLeft, Parent = this };
        }

        private void UpdateStatus(string msg) { if (!IsDisposed) Invoke(new Action(() => _lblStatus.Text = msg)); }
        private void UpdateLight(Label light, Color color) { if (!IsDisposed && light != null) Invoke(new Action(() => light.BackColor = color)); }
        private void ToggleInputs(bool en) { _txtTcpIp.Enabled = _txtTcpPort.Enabled = _cboMksPort.Enabled = _btnMksRefresh.Enabled = en; _btnMksUnitApply.Enabled = _cboMksUnit.Enabled = en; _txtPath.Enabled = _btnBrowse.Enabled = _numInterval.Enabled = en; _btnStart.Enabled = en; _btnStop.Enabled = !en; }
        private void BtnMksUnitApply_Click(object sender, EventArgs e) { if (!string.IsNullOrEmpty(_cboMksPort.Text)) _mksService.SetUnit(_cboMksUnit.Text, _cboMksPort.Text); }
        private void ResetMonitorValues() { /* 필요시 구현 */ }
        protected override void OnFormClosing(FormClosingEventArgs e) { _isLogging = false; _tcpService?.Stop(); _mksService?.Stop(); base.OnFormClosing(e); }
    }
}