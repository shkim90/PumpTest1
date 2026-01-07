using System;
using System.Drawing;
using System.Windows.Forms;
using System.IO;

namespace PumpTest1
{
    public partial class Form1 : Form
    {
        // ==========================================
        // [1] TCP Monitor 관련 변수 (왼쪽)
        // ==========================================
        private TcpDeviceService _tcpService;
        private Label[] _tcpNameLabels;
        private Label[] _tcpValueLabels;
        private Label _lblTcpStatus;

        // TCP Settings UI
        private GroupBox _grpTcpSettings;
        private TextBox _txtTcpIp, _txtTcpPort, _txtTcpPath;
        private NumericUpDown _numTcpInterval;
        private Button _btnTcpBrowse, _btnTcpStart, _btnTcpStop;

        // ==========================================
        // [2] MKS Logger 관련 변수 (오른쪽)
        // ==========================================
        private MksDeviceService _mksService;
        private Label[] _mksValueLabels;
        private Label _lblMksStatus;

        // MKS Settings UI
        private GroupBox _grpMksSettings;
        private ComboBox _cboMksPort;
        private TextBox _txtMksPath;
        private NumericUpDown _numMksInterval;
        private Button _btnMksBrowse, _btnMksRefresh, _btnMksStart, _btnMksStop;
        private ComboBox _cboMksUnit;
        private Button _btnMksUnitApply;

        public Form1()
        {
            InitializeComponent();
            InitializeOneScreenUI(); // 통합 UI 초기화

            // --- TCP 서비스 초기화 ---
            _tcpService = new TcpDeviceService();
            _tcpService.OnDataReceived += Tcp_OnDataReceived;
            _tcpService.OnError += (msg) => UpdateTcpStatus(msg, Color.Red);
            _tcpService.OnStatusChanged += (msg) => UpdateTcpStatus(msg, Color.Black);
            _tcpService.OnChannelInfoReceived += Tcp_OnChannelInfoReceived;

            // --- MKS 서비스 초기화 ---
            _mksService = new MksDeviceService();
            _mksService.OnDataReceived += Mks_OnDataReceived;
            _mksService.OnError += (msg) => UpdateMksStatus(msg, Color.Red);
            _mksService.OnStatusChanged += (msg) => UpdateMksStatus(msg, Color.Black);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            _tcpService?.Stop();
            _mksService?.Stop();
            base.OnFormClosing(e);
        }

        // ==========================================
        // UI 초기화 (좌우 배치)
        // ==========================================
        private void InitializeOneScreenUI()
        {
            this.Text = "Integrated Monitor System (TCP & MKS)";
            this.Size = new Size(1000, 600); // 화면 넓게

            // UI 영역 구분용 구분선 (선택사항)
            Label divider = new Label
            {
                AutoSize = false,
                BorderStyle = BorderStyle.Fixed3D,
                Location = new Point(490, 10),
                Size = new Size(2, 540),
                Parent = this,
                Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left
            };

            // 좌측: TCP 초기화
            InitTcpPanel(new Point(10, 10));

            // 우측: MKS 초기화
            InitMksPanel(new Point(510, 10));
        }

        // ------------------------------------------
        // [TCP] UI 구성
        // ------------------------------------------
        private void InitTcpPanel(Point startPos)
        {
            // 1. 제목
            new Label { Text = "FMS", Location = new Point(startPos.X + 10, startPos.Y), Font = new Font("Arial", 16, FontStyle.Bold), AutoSize = true, ForeColor = Color.DarkBlue, Parent = this };

            // 2. 설정 그룹박스
            _grpTcpSettings = new GroupBox { Text = "TCP Settings", Location = new Point(startPos.X, startPos.Y + 40), Size = new Size(460, 140), Parent = this };

            // IP/Port
            new Label { Text = "IP:", Location = new Point(20, 30), AutoSize = true, Parent = _grpTcpSettings };
            _txtTcpIp = new TextBox { Text = "192.168.1.180", Location = new Point(50, 27), Width = 100, Parent = _grpTcpSettings };
            new Label { Text = "Port:", Location = new Point(160, 30), AutoSize = true, Parent = _grpTcpSettings };
            _txtTcpPort = new TextBox { Text = "101", Location = new Point(200, 27), Width = 40, Parent = _grpTcpSettings };

            // Interval
            new Label { Text = "Interval:", Location = new Point(250, 30), AutoSize = true, Parent = _grpTcpSettings };
            _numTcpInterval = new NumericUpDown { Minimum = 100, Maximum = 60000, Value = 1000, Location = new Point(310, 27), Width = 50, Parent = _grpTcpSettings };

            // Path
            new Label { Text = "Path:", Location = new Point(20, 65), AutoSize = true, Parent = _grpTcpSettings };
            _txtTcpPath = new TextBox { Text = AppDomain.CurrentDomain.BaseDirectory, Location = new Point(60, 62), Width = 230, Parent = _grpTcpSettings };
            _btnTcpBrowse = new Button { Text = "...", Location = new Point(300, 60), Width = 30, Parent = _grpTcpSettings };
            _btnTcpBrowse.Click += (s, e) => SelectFolder(_txtTcpPath);

            // Start/Stop
            _btnTcpStart = new Button { Text = "START", Location = new Point(340, 20), Size = new Size(50, 65), BackColor = Color.LightGreen, Parent = _grpTcpSettings };
            _btnTcpStart.Click += BtnTcpStart_Click;
            _btnTcpStop = new Button { Text = "STOP", Location = new Point(400, 20), Size = new Size(50, 65), BackColor = Color.LightPink, Enabled = false, Parent = _grpTcpSettings };
            _btnTcpStop.Click += BtnTcpStop_Click;

            // 3. 값 표시 영역
            int y = startPos.Y + 200;
            _tcpNameLabels = new Label[4];
            _tcpValueLabels = new Label[4];

            for (int i = 0; i < 4; i++)
            {
                _tcpNameLabels[i] = new Label { Text = $"Value {i + 1}:", Location = new Point(startPos.X + 20, y), AutoSize = true, Font = new Font("Arial", 14), Parent = this };
                _tcpValueLabels[i] = new Label { Text = "---", Location = new Point(startPos.X + 180, y), AutoSize = true, Font = new Font("Arial", 16, FontStyle.Bold), Parent = this };
                y += 50;
            }

            // 4. 상태바 (좌측 하단)
            _lblTcpStatus = new Label { Location = new Point(startPos.X, 520), Size = new Size(460, 30), Text = "Ready", BackColor = Color.LightGray, TextAlign = ContentAlignment.MiddleLeft, Parent = this, Anchor = AnchorStyles.Bottom | AnchorStyles.Left };
        }

        // ------------------------------------------
        // [MKS] UI 구성
        // ------------------------------------------
        private void InitMksPanel(Point startPos)
        {
            // 1. 제목
            new Label { Text = "MKS 946", Location = new Point(startPos.X + 10, startPos.Y), Font = new Font("Arial", 16, FontStyle.Bold), AutoSize = true, ForeColor = Color.DarkRed, Parent = this };

            // 2. 설정 그룹박스
            _grpMksSettings = new GroupBox { Text = "MKS Settings", Location = new Point(startPos.X, startPos.Y + 40), Size = new Size(460, 140), Parent = this };

            // Port
            new Label { Text = "Port:", Location = new Point(20, 30), AutoSize = true, Parent = _grpMksSettings };
            _cboMksPort = new ComboBox { Location = new Point(60, 27), Width = 70, Parent = _grpMksSettings };
            _btnMksRefresh = new Button { Text = "R", Location = new Point(135, 26), Width = 25, Height = 23, Parent = _grpMksSettings };
            _btnMksRefresh.Click += (s, e) => { _cboMksPort.Items.Clear(); _cboMksPort.Items.AddRange(System.IO.Ports.SerialPort.GetPortNames()); if (_cboMksPort.Items.Count > 0) _cboMksPort.SelectedIndex = 0; };
            _btnMksRefresh.PerformClick(); // 초기 로드

            // Interval
            new Label { Text = "Interval:", Location = new Point(170, 30), AutoSize = true, Parent = _grpMksSettings };
            _numMksInterval = new NumericUpDown { Minimum = 100, Maximum = 60000, Value = 500, Location = new Point(230, 27), Width = 50, Parent = _grpMksSettings };

            // Path
            new Label { Text = "Path:", Location = new Point(20, 65), AutoSize = true, Parent = _grpMksSettings };
            _txtMksPath = new TextBox { Text = AppDomain.CurrentDomain.BaseDirectory, Location = new Point(60, 62), Width = 220, Parent = _grpMksSettings };
            _btnMksBrowse = new Button { Text = "...", Location = new Point(290, 60), Width = 30, Parent = _grpMksSettings };
            _btnMksBrowse.Click += (s, e) => SelectFolder(_txtMksPath);

            // Unit
            new Label { Text = "Unit:", Location = new Point(20, 100), AutoSize = true, Parent = _grpMksSettings };
            _cboMksUnit = new ComboBox { Location = new Point(60, 97), Width = 80, Parent = _grpMksSettings };
            _cboMksUnit.Items.AddRange(new object[] { "PASCAL", "TORR", "MBAR", "MICRON" });
            _cboMksUnit.SelectedIndex = 0;
            _btnMksUnitApply = new Button { Text = "Apply", Location = new Point(150, 96), Width = 60, Parent = _grpMksSettings };
            _btnMksUnitApply.Click += BtnMksUnitApply_Click;

            // Start/Stop
            _btnMksStart = new Button { Text = "START", Location = new Point(340, 20), Size = new Size(50, 65), BackColor = Color.LightGreen, Parent = _grpMksSettings };
            _btnMksStart.Click += BtnMksStart_Click;
            _btnMksStop = new Button { Text = "STOP", Location = new Point(400, 20), Size = new Size(50, 65), BackColor = Color.LightPink, Enabled = false, Parent = _grpMksSettings };
            _btnMksStop.Click += BtnMksStop_Click;

            // 3. 값 표시 영역
            int y = startPos.Y + 200;
            _mksValueLabels = new Label[4]; // 인덱스 1~3 사용
            for (int i = 1; i <= 3; i++)
            {
                new Label { Text = $"Channel {i}:", Location = new Point(startPos.X + 20, y), AutoSize = true, Font = new Font("Arial", 14), Parent = this };
                _mksValueLabels[i] = new Label { Text = "---", Location = new Point(startPos.X + 180, y), AutoSize = true, Font = new Font("Arial", 16, FontStyle.Bold), Parent = this };
                y += 50;
            }

            // 4. 상태바 (우측 하단)
            _lblMksStatus = new Label { Location = new Point(startPos.X, 520), Size = new Size(460, 30), Text = "Ready", BackColor = Color.LightGray, TextAlign = ContentAlignment.MiddleLeft, Parent = this, Anchor = AnchorStyles.Bottom | AnchorStyles.Left };
        }

        // ==========================================
        // TCP 이벤트 및 로직
        // ==========================================
        private void BtnTcpStart_Click(object sender, EventArgs e)
        {
            _tcpService.IpAddress = _txtTcpIp.Text;
            if (int.TryParse(_txtTcpPort.Text, out int port)) _tcpService.Port = port;
            _tcpService.IntervalMs = (int)_numTcpInterval.Value;
            _tcpService.LogFolderPath = _txtTcpPath.Text;

            _tcpService.Start();
            ToggleTcpInputs(false);
        }

        private void BtnTcpStop_Click(object sender, EventArgs e)
        {
            _tcpService.Stop();
            ToggleTcpInputs(true);
        }

        private void Tcp_OnChannelInfoReceived(string[] headers)
        {
            if (IsDisposed) return;
            Invoke(new Action(() => {
                for (int i = 0; i < 4; i++) if (_tcpNameLabels[i] != null) _tcpNameLabels[i].Text = headers[i] + ":";
            }));
        }

        private void Tcp_OnDataReceived(DeviceData d)
        {
            if (IsDisposed) return;
            Invoke(new Action(() => {
                for (int i = 0; i < 4; i++) if (_tcpValueLabels[i] != null) _tcpValueLabels[i].Text = (i < d.RawValues.Count) ? d.RawValues[i] : "---";
            }));
        }

        private void UpdateTcpStatus(string msg, Color c)
        {
            if (IsDisposed) return;
            Invoke(new Action(() => {
                _lblTcpStatus.Text = msg;
                _lblTcpStatus.ForeColor = c;
                if (msg.Contains("Stopped") || msg.Contains("Error")) ToggleTcpInputs(true);
            }));
        }

        private void ToggleTcpInputs(bool en)
        {
            _txtTcpIp.Enabled = _txtTcpPort.Enabled = _numTcpInterval.Enabled = _txtTcpPath.Enabled = _btnTcpBrowse.Enabled = en;
            _btnTcpStart.Enabled = en; _btnTcpStop.Enabled = !en;
        }

        // ==========================================
        // MKS 이벤트 및 로직
        // ==========================================
        private void BtnMksStart_Click(object sender, EventArgs e)
        {
            if (string.IsNullOrEmpty(_cboMksPort.Text)) { MessageBox.Show("Select Port"); return; }
            _mksService.PortName = _cboMksPort.Text;
            _mksService.IntervalMs = (int)_numMksInterval.Value;
            _mksService.LogFolderPath = _txtMksPath.Text;
            _mksService.Start();
            ToggleMksInputs(false);
        }

        private void BtnMksStop_Click(object sender, EventArgs e)
        {
            _mksService.Stop();
            ToggleMksInputs(true);
        }

        private void BtnMksUnitApply_Click(object sender, EventArgs e)
        {
            if (string.IsNullOrEmpty(_cboMksPort.Text)) { MessageBox.Show("Select Port"); return; }
            bool wasRunning = !_btnMksStart.Enabled;
            if (wasRunning) _mksService.Stop();

            bool ok = _mksService.SetUnit(_cboMksUnit.Text, _cboMksPort.Text);
            MessageBox.Show(ok ? "Unit Changed" : "Change Failed");

            if (wasRunning) _mksService.Start();
        }

        private void Mks_OnDataReceived(MksData d)
        {
            if (IsDisposed) return;
            Invoke(new Action(() => {
                if (_mksValueLabels[1] != null) _mksValueLabels[1].Text = d.Ch1.HasValue ? $"{d.Ch1:F2} {d.Unit}" : "Error";
                if (_mksValueLabels[2] != null) _mksValueLabels[2].Text = d.Ch2.HasValue ? $"{d.Ch2:F2} {d.Unit}" : "Error";
                if (_mksValueLabels[3] != null) _mksValueLabels[3].Text = d.Ch3.HasValue ? $"{d.Ch3:F2} {d.Unit}" : "Error";
            }));
        }

        private void UpdateMksStatus(string msg, Color c)
        {
            if (IsDisposed) return;
            Invoke(new Action(() => {
                _lblMksStatus.Text = msg;
                _lblMksStatus.ForeColor = c;
                if (msg.Contains("Stopped") || msg.Contains("Error")) ToggleMksInputs(true);
            }));
        }

        private void ToggleMksInputs(bool en)
        {
            _cboMksPort.Enabled = _btnMksRefresh.Enabled = _numMksInterval.Enabled = _txtMksPath.Enabled = _btnMksBrowse.Enabled = en;
            _btnMksStart.Enabled = en; _btnMksStop.Enabled = !en;
        }

        // ==========================================
        // 공통 유틸
        // ==========================================
        private void SelectFolder(TextBox t)
        {
            using (var d = new FolderBrowserDialog())
            {
                if (d.ShowDialog() == DialogResult.OK) t.Text = d.SelectedPath;
            }
        }
    }
}