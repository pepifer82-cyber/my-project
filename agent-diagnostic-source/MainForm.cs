using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Net;
using System.Net.Sockets;

namespace ATPrinterAgent;

public sealed class MainForm : Form
{
    private readonly ConfigStore _store = new();
    private readonly SupabaseSync _sync = new();
    private readonly UpdateManager _updates = new();
    private System.Windows.Forms.Timer? _updateTimer;
    private readonly bool _startInBackground;
    private AgentConfig _config;
    private List<PrinterInfo> _printers = new();
    private CancellationTokenSource _shutdown = new();
    private NotifyIcon _tray = null!;
    private DataGridView _grid = null!;
    private Label _lblStatus = null!;
    private Label _lblCounts = null!;
    private Label _lblAgent = null!;
    private TextBox _txtSerial = null!;
    private TextBox _txtSyncUrl = null!;
    private TextBox _txtCommunity = null!;
    private ComboBox _cboSnmpVersion = null!;
    private TextBox _txtSnmpV3User = null!;
    private ComboBox _cboSnmpV3Auth = null!;
    private TextBox _txtSnmpV3AuthPassword = null!;
    private ComboBox _cboSnmpV3Privacy = null!;
    private TextBox _txtSnmpV3PrivacyPassword = null!;
    private TextBox _txtSnmpV3Context = null!;
    private TextBox _txtNetworkIp = null!;
    private ListBox _lstNetworkIps = null!;
    private Label _lblIpCount = null!;
    private Button _btnActivate = null!;
    private Button _btnInitialSearch = null!;
    private bool _serialValidatedForCurrentValue;
    private TextBox _txtNetworkStart = null!;
    private TextBox _txtNetworkEnd = null!;
    private TextBox _txtNewRangeStart = null!;
    private TextBox _txtNewRangeEnd = null!;
    private ListBox _lstAdditionalRanges = null!;
    private Label _lblRangeCount = null!;
    private ComboBox _cboInterval = null!;
    private CheckBox _chkUsbControl = null!;
    private CheckBox _chkSnmp = null!;
    private CheckBox _chkStartup = null!;
    private CheckBox _chkScan = null!;
    private CheckBox _chkWindowsScan = null!;
    private CheckBox _chkNetworkScan = null!;
    private CheckBox _chkNetworkOnStart = null!;
    private Button _btnSync = null!;
    private System.Windows.Forms.Timer _timer = null!;
    private System.Windows.Forms.Timer? _remoteTimer;
    private System.Windows.Forms.Timer _showTimer = null!;
    private bool _busy;

    public MainForm(bool startInBackground)
    {
        _startInBackground = startInBackground;
        _config = _store.Load();
        Text = "A&T Printer Agent 0.3.9";
        ShowInTaskbar = !_startInBackground;
        Opacity = _startInBackground ? 0 : 1;
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(980, 620);
        ClientSize = new Size(1100, 700);
        Font = new Font("Segoe UI", 9F);
        BackColor = Color.FromArgb(245, 247, 250);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;

        BuildUi();
        BuildTray();
        ApplyConfigToUi();

        if (_startInBackground) WindowState = FormWindowState.Minimized;

        Shown += async (_, _) =>
        {
            if (_startInBackground)
            {
                Hide();
                Opacity = 1;
                ShowInTaskbar = false;
            }
            _showTimer = new System.Windows.Forms.Timer { Interval = 500 };
            _showTimer.Tick += (_, _) =>
            {
                if (Program.ConsumeShowRequest()) ShowFromProgramFiles();
            };
            _showTimer.Start();
            await StartAsync();
            _ = CheckForUpdatesAsync(false);
            _updateTimer = new System.Windows.Forms.Timer { Interval = 6 * 60 * 60 * 1000 };
            _updateTimer.Tick += async (_, _) => await CheckForUpdatesAsync(false);
            _updateTimer.Start();
        };
        FormClosing += (_, e) =>
        {
            if (e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                _tray.Visible = true;
                Hide();
                ShowInTaskbar = false;
            }
        };
    }

    private void BuildUi()
    {
        var top = new Panel { Dock = DockStyle.Top, Height = 76, BackColor = Color.White, Padding = new Padding(18, 12, 18, 12) };
        Controls.Add(top);

        var title = new Label { AutoSize = true, Text = "A&T PRINTER AGENT", Font = new Font("Segoe UI Semibold", 16F), ForeColor = Color.FromArgb(15, 23, 42), Location = new Point(18, 12) };
        var version = new Label { AutoSize = true, Text = "v0.3.9 · Windows", Font = new Font("Segoe UI", 9F), ForeColor = Color.FromArgb(100, 116, 139), Location = new Point(20, 42) };
        top.Controls.Add(title); top.Controls.Add(version);

        _btnSync = MakeButton("Sincronizar ahora", Color.FromArgb(16, 185, 129));
        _btnSync.Location = new Point(850, 18); _btnSync.Size = new Size(220, 40); _btnSync.Click += async (_, _) => await SyncNowAsync(); top.Controls.Add(_btnSync);

        var tabs = new TabControl { Dock = DockStyle.Fill, Padding = new Point(15, 5) };
        Controls.Add(tabs);
        tabs.BringToFront();

        var tabPrinters = new TabPage("Impresoras");
        var tabConfig = new TabPage("Configuración");
        tabs.TabPages.Add(tabPrinters); tabs.TabPages.Add(tabConfig);

        BuildPrintersTab(tabPrinters);
        BuildConfigTab(tabConfig);

        var status = new Panel { Dock = DockStyle.Bottom, Height = 42, BackColor = Color.White, Padding = new Padding(18, 8, 18, 8) };
        Controls.Add(status);
        _lblStatus = new Label { Dock = DockStyle.Fill, Text = "Preparando…", ForeColor = Color.FromArgb(71, 85, 105), TextAlign = ContentAlignment.MiddleLeft };
        status.Controls.Add(_lblStatus);
    }

    private void BuildPrintersTab(TabPage tab)
    {
        var action = new Panel { Dock = DockStyle.Top, Height = 68, BackColor = Color.Transparent };
        tab.Controls.Add(action);
        var btnScan = MakeButton("Buscar configuradas", Color.FromArgb(37, 99, 235));
        btnScan.Location = new Point(14, 14); btnScan.Size = new Size(190, 38);
        btnScan.Click += async (_, _) =>
        {
            if (!_config.InitialDiscoveryComplete)
            {
                SetStatus("Primero activá el serial y usá ‘Iniciar búsqueda de impresoras’ en Configuración.", true);
                return;
            }
            await ScanAsync();
        };
        action.Controls.Add(btnScan);
        var btnNet = MakeButton("Escanear red", Color.FromArgb(124, 58, 237));
        btnNet.Location = new Point(214, 14); btnNet.Size = new Size(150, 38);
        btnNet.Click += async (_, _) => await ScanNetworkOnlyAsync(); action.Controls.Add(btnNet);

        _lblCounts = new Label { AutoSize = true, Text = "0 impresoras", Location = new Point(380, 25), ForeColor = Color.FromArgb(71, 85, 105) };
        action.Controls.Add(_lblCounts);
        _lblAgent = new Label { AutoSize = true, Text = "Agente: sin registrar", Location = new Point(580, 25), ForeColor = Color.FromArgb(71, 85, 105) };
        action.Controls.Add(_lblAgent);

        _grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            AutoGenerateColumns = false,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            MultiSelect = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            RowHeadersVisible = false,
            BackgroundColor = Color.White,
            BorderStyle = BorderStyle.None,
            AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.None,
            RowTemplate = { Height = 32 }
        };
        AddCol("Impresora", nameof(PrinterInfo.DisplayName), 230);
        AddCol("Fabricante", nameof(PrinterInfo.Manufacturer), 100);
        AddCol("Modelo / Driver", nameof(PrinterInfo.Model), 220);
        AddCol("Tipo", nameof(PrinterInfo.ConnectionType), 70);
        AddCol("IP", nameof(PrinterInfo.IpAddress), 120);
        AddCol("Estado", nameof(PrinterInfo.LastStatus), 90);
        AddCol("Contador general", nameof(PrinterInfo.PageCount), 125);
        AddCol("Serie", nameof(PrinterInfo.SerialNumber), 145);
        AddCol("Métodos de lectura", nameof(PrinterInfo.AcquisitionSummary), 300);
        AddCol("Tóner", "TonerSummary", 160);
        tab.Controls.Add(_grid);
        _grid.BringToFront();
    }

    private void AddCol(string header, string prop, int width)
    {
        if (prop == "TonerSummary")
        {
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = header, Width = width, Name = prop });
            return;
        }
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = header, DataPropertyName = prop, Width = width, Name = prop });
    }

    private void BuildConfigTab(TabPage tab)
    {
        var panel = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(24) };
        tab.Controls.Add(panel);

        var y = 22;
        // The activation button sits beside the label, as marked in the user's screenshot.
        panel.Controls.Add(new Label { Text = "Serial de activación", AutoSize = true, Location = new Point(24, y), ForeColor = Color.FromArgb(71, 85, 105) });
        _txtSerial = new TextBox { Location = new Point(24, y + 24), Width = 520, UseSystemPasswordChar = false };
        panel.Controls.Add(_txtSerial);
        _btnActivate = MakeButton("Activar serial", Color.FromArgb(16, 185, 129));
        _btnActivate.Location = new Point(160, y - 3);
        _btnActivate.Size = new Size(160, 30);
        _btnActivate.Click += async (_, _) => await ActivateSerialAsync();
        panel.Controls.Add(_btnActivate);
        _txtSerial.TextChanged += (_, _) => _serialValidatedForCurrentValue = false;
        y += 75;
        var serialHint = new Label { AutoSize = false, Size = new Size(760, 21), Location = new Point(24, y - 23), Text = "Pegá el serial del proyecto y pulsá ‘Activar serial’. Se guarda antes de validarlo y vincular el agente.", ForeColor = Color.FromArgb(100, 116, 139) };
        panel.Controls.Add(serialHint);
        AddField(panel, "URL de sincronización", ref _txtSyncUrl, ref y, 700, false);
        AddField(panel, "Comunidad SNMP (v1 / v2c)", ref _txtCommunity, ref y, 260, false);

        var v3Group = new GroupBox { Text = "SNMP · Versiones y seguridad", Location = new Point(24, y), Size = new Size(820, 164), ForeColor = Color.FromArgb(30, 64, 100) };
        panel.Controls.Add(v3Group);
        v3Group.Controls.Add(new Label { Text = "Protocolo", AutoSize = true, Location = new Point(12, 25) });
        _cboSnmpVersion = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(12, 46), Width = 190 };
        _cboSnmpVersion.Items.AddRange(new object[] { "Automático (v2c → v1)", "SNMP v1", "SNMP v2c", "SNMP v3" });
        v3Group.Controls.Add(_cboSnmpVersion);
        v3Group.Controls.Add(new Label { Text = "Usuario SNMPv3", AutoSize = true, Location = new Point(216, 25) });
        _txtSnmpV3User = new TextBox { Location = new Point(216, 46), Width = 180 };
        v3Group.Controls.Add(_txtSnmpV3User);
        v3Group.Controls.Add(new Label { Text = "Autenticación", AutoSize = true, Location = new Point(408, 25) });
        _cboSnmpV3Auth = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(408, 46), Width = 150 };
        _cboSnmpV3Auth.Items.AddRange(new object[] { "SHA256", "SHA384", "SHA512", "SHA1", "MD5", "None" });
        v3Group.Controls.Add(_cboSnmpV3Auth);
        v3Group.Controls.Add(new Label { Text = "Privacidad", AutoSize = true, Location = new Point(570, 25) });
        _cboSnmpV3Privacy = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(570, 46), Width = 150 };
        _cboSnmpV3Privacy.Items.AddRange(new object[] { "AES128", "AES192", "AES256", "DES", "None" });
        v3Group.Controls.Add(_cboSnmpV3Privacy);
        v3Group.Controls.Add(new Label { Text = "Contraseña de autenticación (mín. 8 caracteres)", AutoSize = true, Location = new Point(12, 82) });
        _txtSnmpV3AuthPassword = new TextBox { Location = new Point(12, 103), Width = 245, UseSystemPasswordChar = true, PlaceholderText = "Vacío = conservar guardada" };
        v3Group.Controls.Add(_txtSnmpV3AuthPassword);
        v3Group.Controls.Add(new Label { Text = "Contraseña de privacidad (mín. 8 caracteres)", AutoSize = true, Location = new Point(270, 82) });
        _txtSnmpV3PrivacyPassword = new TextBox { Location = new Point(270, 103), Width = 245, UseSystemPasswordChar = true, PlaceholderText = "Vacío = conservar guardada" };
        v3Group.Controls.Add(_txtSnmpV3PrivacyPassword);
        v3Group.Controls.Add(new Label { Text = "Contexto", AutoSize = true, Location = new Point(530, 82) });
        _txtSnmpV3Context = new TextBox { Location = new Point(530, 103), Width = 190 };
        v3Group.Controls.Add(_txtSnmpV3Context);
        y += 174;

        var discovery = new Label { Text = "DESCUBRIMIENTO", AutoSize = true, Location = new Point(24, y + 4), Font = new Font("Segoe UI Semibold", 10F), ForeColor = Color.FromArgb(15, 23, 42) };
        panel.Controls.Add(discovery);
        // Repeated IP addition button is next to the discovery heading, as marked.
        var btnAddIp = MakeButton("+ Agregar IP", Color.FromArgb(16, 185, 129));
        btnAddIp.Location = new Point(160, y - 2); btnAddIp.Size = new Size(150, 30);
        btnAddIp.Click += (_, _) => AddNetworkIpFromInput(persist: true);
        panel.Controls.Add(btnAddIp);
        y += 32;

        panel.Controls.Add(new Label { Text = "IP individual (ej. 192.168.1.50)", AutoSize = true, Location = new Point(24, y), ForeColor = Color.FromArgb(71, 85, 105) });
        _txtNetworkIp = new TextBox { Location = new Point(24, y + 24), Width = 300 };
        panel.Controls.Add(_txtNetworkIp);
        _txtNetworkIp.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.SuppressKeyPress = true;
                AddNetworkIpFromInput(persist: true);
            }
        };
        y += 61;

        _lstNetworkIps = new ListBox { Location = new Point(24, y), Size = new Size(300, 118), IntegralHeight = false, BorderStyle = BorderStyle.FixedSingle };
        panel.Controls.Add(_lstNetworkIps);
        var btnRemoveIp = MakeButton("Quitar IP seleccionada", Color.FromArgb(225, 75, 85));
        btnRemoveIp.Location = new Point(340, y + 2); btnRemoveIp.Size = new Size(190, 34);
        btnRemoveIp.Click += (_, _) => RemoveSelectedNetworkIp();
        panel.Controls.Add(btnRemoveIp);
        _lblIpCount = new Label { AutoSize = true, Location = new Point(340, y + 43), Text = "0 IP individuales guardadas", ForeColor = Color.FromArgb(30, 100, 70), Font = new Font("Segoe UI Semibold", 9F) };
        panel.Controls.Add(_lblIpCount);
        var ipListHint = new Label { AutoSize = false, Size = new Size(520, 44), Location = new Point(340, y + 66), Text = "Podés agregar una IP, volver a escribir otra y pulsar el mismo botón tantas veces como necesites. Se guardan al instante.", ForeColor = Color.FromArgb(100, 116, 139) };
        panel.Controls.Add(ipListHint);
        y += 137;

        AddField(panel, "Rango inicial (ej. 192.168.1.1)", ref _txtNetworkStart, ref y, 300, false);
        AddField(panel, "Rango final (ej. 192.168.1.254)", ref _txtNetworkEnd, ref y, 300, false);

        var btnLocal = MakeButton("Usar red local detectada", Color.FromArgb(71, 85, 105));
        btnLocal.Location = new Point(350, y - 51); btnLocal.Size = new Size(220, 38);
        btnLocal.Click += (_, _) => UseLocalNetworkRange();
        panel.Controls.Add(btnLocal);
        var btnClearMainRange = MakeButton("Quitar rango principal", Color.FromArgb(225, 75, 85));
        btnClearMainRange.Location = new Point(585, y - 51); btnClearMainRange.Size = new Size(190, 38);
        btnClearMainRange.Click += (_, _) => ClearMainRange();
        panel.Controls.Add(btnClearMainRange);

        var rangeNote = new Label { AutoSize = false, Size = new Size(720, 48), Location = new Point(350, y - 5), Text = "Podés combinar IP individuales con el rango principal y varios rangos de routers. Para eliminar el principal, usá el botón rojo; los rangos secundarios se administran debajo.", ForeColor = Color.FromArgb(100, 116, 139) };
        panel.Controls.Add(rangeNote);
        y += 60;

        panel.Controls.Add(new Label { Text = "RANGOS DE OTROS ROUTERS", AutoSize = true, Location = new Point(24, y), Font = new Font("Segoe UI Semibold", 9.5F), ForeColor = Color.FromArgb(15, 23, 42) });
        panel.Controls.Add(new Label { Text = "IP inicial", AutoSize = true, Location = new Point(24, y + 25), ForeColor = Color.FromArgb(71, 85, 105) });
        panel.Controls.Add(new Label { Text = "IP final", AutoSize = true, Location = new Point(265, y + 25), ForeColor = Color.FromArgb(71, 85, 105) });
        _txtNewRangeStart = new TextBox { Location = new Point(24, y + 46), Width = 220, PlaceholderText = "192.168.2.1" };
        _txtNewRangeEnd = new TextBox { Location = new Point(265, y + 46), Width = 220, PlaceholderText = "192.168.2.254" };
        panel.Controls.Add(_txtNewRangeStart);
        panel.Controls.Add(_txtNewRangeEnd);
        _txtNewRangeEnd.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.SuppressKeyPress = true;
                AddAdditionalRangeFromInput(persist: true);
            }
        };
        var btnAddRange = MakeButton("+ Agregar rango", Color.FromArgb(16, 185, 129));
        btnAddRange.Location = new Point(510, y + 44); btnAddRange.Size = new Size(165, 32);
        btnAddRange.Click += (_, _) => AddAdditionalRangeFromInput(persist: true);
        panel.Controls.Add(btnAddRange);

        _lstAdditionalRanges = new ListBox { Location = new Point(24, y + 82), Size = new Size(461, 94), IntegralHeight = false, BorderStyle = BorderStyle.FixedSingle };
        panel.Controls.Add(_lstAdditionalRanges);
        var btnRemoveRange = MakeButton("Quitar rango seleccionado", Color.FromArgb(225, 75, 85));
        btnRemoveRange.Location = new Point(510, y + 82); btnRemoveRange.Size = new Size(210, 34);
        btnRemoveRange.Click += (_, _) => RemoveSelectedAdditionalRange();
        panel.Controls.Add(btnRemoveRange);
        _lblRangeCount = new Label { AutoSize = true, Location = new Point(510, y + 123), Text = "0 rangos adicionales guardados", ForeColor = Color.FromArgb(30, 100, 70), Font = new Font("Segoe UI Semibold", 9F) };
        panel.Controls.Add(_lblRangeCount);
        var additionalRangeHint = new Label { AutoSize = false, Size = new Size(410, 44), Location = new Point(510, y + 146), Text = "Agregá un rango por router y quitá los que ya no uses. Se guardan en esta PC; las IP superpuestas se escanean una sola vez.", ForeColor = Color.FromArgb(100, 116, 139) };
        panel.Controls.Add(additionalRangeHint);
        y += 190;

        var intervalLabel = new Label { Text = "Enviar datos a A&T Printer Control cada:", AutoSize = true, Location = new Point(24, y), ForeColor = Color.FromArgb(71, 85, 105) };
        panel.Controls.Add(intervalLabel);
        _cboInterval = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(24, y + 24), Width = 220 };
        _cboInterval.Items.AddRange(new object[] { "Cada 30 minutos", "Cada 1 hora", "Cada 2 horas" });
        _cboInterval.SelectedIndex = IntervalToIndex(_config.SyncIntervalSeconds);
        panel.Controls.Add(_cboInterval); y += 75;

        _chkSnmp = new CheckBox { Text = "Consultar SNMP para IP: nombre, serial, contador y tóner", AutoSize = true, Location = new Point(24, y), Checked = _config.SnmpEnabled };
        _chkStartup = new CheckBox { Text = "Iniciar automáticamente con Windows", AutoSize = true, Location = new Point(24, y + 30), Checked = _config.StartWithWindows };
        _chkScan = new CheckBox { Text = "Escanear al iniciar", AutoSize = true, Location = new Point(24, y + 60), Checked = _config.ScanOnStart };
        _chkWindowsScan = new CheckBox { Text = "Buscar también impresoras instaladas en esta PC", AutoSize = true, Location = new Point(310, y), Checked = _config.WindowsScanEnabled };
        _chkNetworkScan = new CheckBox { Text = "Buscar impresoras directamente en la red", AutoSize = true, Location = new Point(310, y + 30), Checked = _config.NetworkScanEnabled };
        _chkNetworkOnStart = new CheckBox { Text = "Incluir escaneo de red en cada sincronización", AutoSize = true, Location = new Point(310, y + 60), Checked = _config.NetworkScanOnStart };
        _chkUsbControl = new CheckBox { Text = "Control USB: incluir USB y evitar duplicados con red", AutoSize = true, Location = new Point(310, y + 90), Checked = _config.UsbControlEnabled };
        panel.Controls.Add(_chkSnmp); panel.Controls.Add(_chkStartup); panel.Controls.Add(_chkScan); panel.Controls.Add(_chkWindowsScan); panel.Controls.Add(_chkNetworkScan); panel.Controls.Add(_chkNetworkOnStart); panel.Controls.Add(_chkUsbControl); y += 135;

        _btnInitialSearch = MakeButton(
            _config.InitialDiscoveryComplete ? "Buscar impresoras ahora" : "Iniciar búsqueda de impresoras",
            Color.FromArgb(37, 99, 235));
        _btnInitialSearch.Location = new Point(24, y);
        _btnInitialSearch.Size = new Size(280, 44);
        _btnInitialSearch.Click += async (_, _) => await StartInitialSearchAsync();
        panel.Controls.Add(_btnInitialSearch);
        var initialSearchHint = new Label
        {
            AutoSize = false,
            Size = new Size(650, 44),
            Location = new Point(320, y + 2),
            Text = "Primera vez: agregá el serial y las IP o el rango, activá el serial y pulsá este botón. La búsqueda se ejecuta una vez y las IP quedan guardadas para las siguientes sincronizaciones.",
            ForeColor = Color.FromArgb(100, 116, 139)
        };
        panel.Controls.Add(initialSearchHint);
        y += 62;

        var btnSave = MakeButton("Guardar configuración", Color.FromArgb(16, 185, 129));
        btnSave.Location = new Point(24, y); btnSave.Size = new Size(220, 40); btnSave.Click += (_, _) => SaveConfigFromUi(); panel.Controls.Add(btnSave);

        var note = new Label
        {
            AutoSize = false,
            Size = new Size(800, 120),
            Location = new Point(24, y + 70),
            ForeColor = Color.FromArgb(100, 116, 139),
            Text = "La clave usada aquí es la PUBLISHABLE KEY del proyecto, no la service_role.\n" +
                   "El serial de activación es validado por la función printer-agent-sync de Supabase.\n" +
                   "Configuración local: %LOCALAPPDATA%\\ATPrinterAgent\\config.json"
        };
        panel.Controls.Add(note);
    }

    private void AddField(Control parent, string label, ref TextBox target, ref int y, int width, bool password)
    {
        parent.Controls.Add(new Label { Text = label, AutoSize = true, Location = new Point(24, y), ForeColor = Color.FromArgb(71, 85, 105) });
        target = new TextBox { Location = new Point(24, y + 24), Width = width, UseSystemPasswordChar = password };
        parent.Controls.Add(target); y += 75;
    }

    private Button MakeButton(string text, Color color)
    {
        var b = new Button
        {
            Text = text,
            BackColor = color,
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            FlatAppearance = { BorderSize = 0 },
            Font = new Font("Segoe UI Semibold", 9.5F),
            Cursor = Cursors.Hand
        };
        return b;
    }

    private void BuildTray()
    {
        _tray = new NotifyIcon
        {
            Visible = true,
            Text = "A&T Printer Agent",
            Icon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? System.Drawing.SystemIcons.Application,
            ContextMenuStrip = new ContextMenuStrip()
        };
        _tray.ContextMenuStrip.Items.Add("Abrir", null, (_, _) => ShowFromTray());
        _tray.ContextMenuStrip.Items.Add("Sincronizar ahora", null, async (_, _) => await SyncNowAsync());
        _tray.ContextMenuStrip.Items.Add("Buscar actualizaciones", null, async (_, _) => await CheckForUpdatesAsync(true));
        _tray.ContextMenuStrip.Items.Add("Salir", null, (_, _) => ExitApplication());
        _tray.DoubleClick += (_, _) => ShowFromTray();
    }

    private void ApplyConfigToUi()
    {
        _txtSerial.Text = _config.Serial;
        _txtSyncUrl.Text = _config.SyncUrl;
        _txtCommunity.Text = _config.SnmpCommunity;
        _cboSnmpVersion.SelectedIndex = _config.SnmpVersion?.ToLowerInvariant() switch { "v1" => 1, "v2c" => 2, "v3" => 3, _ => 0 };
        _txtSnmpV3User.Text = _config.SnmpV3User ?? "";
        _cboSnmpV3Auth.SelectedItem = string.IsNullOrWhiteSpace(_config.SnmpV3AuthProtocol) ? "SHA256" : _config.SnmpV3AuthProtocol;
        if (_cboSnmpV3Auth.SelectedIndex < 0) _cboSnmpV3Auth.SelectedItem = "SHA256";
        _cboSnmpV3Privacy.SelectedItem = string.IsNullOrWhiteSpace(_config.SnmpV3PrivacyProtocol) ? "AES128" : _config.SnmpV3PrivacyProtocol;
        if (_cboSnmpV3Privacy.SelectedIndex < 0) _cboSnmpV3Privacy.SelectedItem = "AES128";
        _txtSnmpV3Context.Text = _config.SnmpV3ContextName ?? "";
        _txtSnmpV3AuthPassword.Clear();
        _txtSnmpV3PrivacyPassword.Clear();
        _txtNetworkIp.Text = "";
        RefreshConfiguredIpList();
        _txtNetworkStart.Text = _config.NetworkRangeStart;
        _txtNetworkEnd.Text = _config.NetworkRangeEnd;
        _config.NetworkRanges ??= new List<string>();
        RefreshAdditionalRangeList();
        _cboInterval.SelectedIndex = IntervalToIndex(_config.SyncIntervalSeconds);
        _chkSnmp.Checked = _config.SnmpEnabled;
        _chkStartup.Checked = _config.StartWithWindows;
        _chkScan.Checked = _config.ScanOnStart;
        _chkWindowsScan.Checked = _config.WindowsScanEnabled;
        _chkNetworkScan.Checked = _config.NetworkScanEnabled;
        _chkNetworkOnStart.Checked = _config.NetworkScanOnStart;
        _chkUsbControl.Checked = _config.UsbControlEnabled;
        UpdateInitialSearchButton();
    }

    private static int IntervalToIndex(int seconds) => seconds switch
    {
        3600 => 1,
        7200 => 2,
        _ => 0
    };

    private int SelectedIntervalSeconds() => _cboInterval.SelectedIndex switch
    {
        1 => 3600,
        2 => 7200,
        _ => 1800
    };

    private void UpdateInitialSearchButton()
    {
        if (_btnInitialSearch is null) return;
        _btnInitialSearch.Text = _config.InitialDiscoveryComplete
            ? "Buscar impresoras ahora"
            : "Iniciar búsqueda de impresoras";
    }

    private void RefreshConfiguredIpList()
    {
        if (_lstNetworkIps is null) return;
        _config.NetworkIps ??= new List<string>();
        _lstNetworkIps.BeginUpdate();
        _lstNetworkIps.Items.Clear();
        foreach (var ip in _config.NetworkIps
                     .Where(x => !string.IsNullOrWhiteSpace(x))
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            _lstNetworkIps.Items.Add(ip);
        _lstNetworkIps.EndUpdate();
        if (_lblIpCount is not null)
        {
            var count = _lstNetworkIps.Items.Count;
            _lblIpCount.Text = $"{count} IP individual{(count == 1 ? "" : "es")} guardada{(count == 1 ? "" : "s")}";
        }
    }

    private bool AddNetworkIpFromInput(bool persist)
    {
        var value = _txtNetworkIp.Text.Trim();
        if (string.IsNullOrWhiteSpace(value)) return true;

        if (!IPAddress.TryParse(value, out var address) || address.AddressFamily != AddressFamily.InterNetwork)
        {
            SetStatus("La IP individual no es válida. Usá una dirección IPv4, por ejemplo 192.168.1.50.", true);
            _txtNetworkIp.Focus();
            return false;
        }

        var normalized = address.ToString();
        _config.NetworkIps ??= new List<string>();
        if (_config.NetworkIps.Any(ip => string.Equals(ip?.Trim(), normalized, StringComparison.OrdinalIgnoreCase)))
        {
            _txtNetworkIp.Clear();
            RefreshConfiguredIpList();
            SetStatus("Esa IP ya está agregada.", false);
            return true;
        }

        _config.NetworkIps.Add(normalized);
        _config.NetworkIp = "";
        _config.NetworkScanEnabled = true;
        if (_chkNetworkScan is not null) _chkNetworkScan.Checked = true;
        try
        {
            if (persist) _store.Save(_config);
            _txtNetworkIp.Clear();
            RefreshConfiguredIpList();
            SetStatus($"IP {normalized} agregada a la lista de escaneo.", false);
            return true;
        }
        catch (Exception ex)
        {
            _config.NetworkIps.RemoveAll(ip => string.Equals(ip?.Trim(), normalized, StringComparison.OrdinalIgnoreCase));
            RefreshConfiguredIpList();
            SetStatus("No se pudo guardar la IP: " + ex.Message, true);
            return false;
        }
    }

    private void RemoveSelectedNetworkIp()
    {
        if (_lstNetworkIps.SelectedItem is not string selected)
        {
            SetStatus("Seleccioná una IP de la lista para quitarla.", true);
            return;
        }

        var index = _config.NetworkIps.FindIndex(ip => string.Equals(ip?.Trim(), selected, StringComparison.OrdinalIgnoreCase));
        if (index < 0) { RefreshConfiguredIpList(); return; }
        var removed = _config.NetworkIps[index];
        _config.NetworkIps.RemoveAt(index);
        try
        {
            _store.Save(_config);
            RefreshConfiguredIpList();
            SetStatus($"IP {selected} quitada de la lista.", false);
        }
        catch (Exception ex)
        {
            _config.NetworkIps.Insert(index, removed);
            RefreshConfiguredIpList();
            SetStatus("No se pudo quitar la IP: " + ex.Message, true);
        }
    }

    private void SaveConfigFromUi() => PersistConfigFromUi(showFeedback: true);

    private bool PersistConfigFromUi(bool showFeedback)
    {
        if (!AddNetworkIpFromInput(persist: false)) return false;

        _config.Serial = _txtSerial.Text.Trim().Replace("\r", "").Replace("\n", "").ToUpperInvariant();
        _config.SyncUrl = _txtSyncUrl.Text.Trim();
        _config.SnmpCommunity = string.IsNullOrWhiteSpace(_txtCommunity.Text) ? "public" : _txtCommunity.Text.Trim();
        _config.SnmpVersion = _cboSnmpVersion.SelectedIndex switch { 1 => "v1", 2 => "v2c", 3 => "v3", _ => "auto" };
        _config.SnmpV3User = _txtSnmpV3User.Text.Trim();
        _config.SnmpV3AuthProtocol = _cboSnmpV3Auth.SelectedItem?.ToString() ?? "SHA256";
        _config.SnmpV3PrivacyProtocol = _cboSnmpV3Privacy.SelectedItem?.ToString() ?? "AES128";
        _config.SnmpV3ContextName = _txtSnmpV3Context.Text.Trim();
        if (!string.IsNullOrWhiteSpace(_txtSnmpV3AuthPassword.Text)) _config.SnmpV3AuthPassword = _txtSnmpV3AuthPassword.Text;
        if (!string.IsNullOrWhiteSpace(_txtSnmpV3PrivacyPassword.Text)) _config.SnmpV3PrivacyPassword = _txtSnmpV3PrivacyPassword.Text;
        if (_chkSnmp.Checked && _config.SnmpVersion == "v3")
        {
            if (string.IsNullOrWhiteSpace(_config.SnmpV3User))
            {
                SetStatus("SNMPv3 requiere un usuario configurado en la impresora.", true);
                _txtSnmpV3User.Focus();
                return false;
            }
            if (_config.SnmpV3AuthProtocol != "None" && _config.SnmpV3AuthPassword.Length < 8)
            {
                SetStatus("La contraseña SNMPv3 de autenticación debe tener al menos 8 caracteres.", true);
                _txtSnmpV3AuthPassword.Focus();
                return false;
            }
            if (_config.SnmpV3PrivacyProtocol != "None" && (_config.SnmpV3AuthProtocol == "None" || _config.SnmpV3PrivacyPassword.Length < 8))
            {
                SetStatus("SNMPv3 con privacidad requiere autenticación habilitada y una contraseña de privacidad de al menos 8 caracteres.", true);
                _txtSnmpV3PrivacyPassword.Focus();
                return false;
            }
        }
        _config.NetworkIp = "";
        _config.NetworkIps ??= new List<string>();
        _config.NetworkRangeStart = _txtNetworkStart.Text.Trim();
        _config.NetworkRangeEnd = _txtNetworkEnd.Text.Trim();
        var hasRangeStart = !string.IsNullOrWhiteSpace(_config.NetworkRangeStart);
        var hasRangeEnd = !string.IsNullOrWhiteSpace(_config.NetworkRangeEnd);
        if (hasRangeStart != hasRangeEnd || (hasRangeStart && (!IsValidIpv4(_config.NetworkRangeStart) || !IsValidIpv4(_config.NetworkRangeEnd) || CompareIpv4(_config.NetworkRangeStart, _config.NetworkRangeEnd) > 0)))
        {
            SetStatus("El rango inicial/final debe contener dos direcciones IPv4 válidas, o dejar ambos campos vacíos.", true);
            _txtNetworkStart.Focus();
            return false;
        }
        var additionalRanges = GetAdditionalRangesFromList();
        var invalidRange = additionalRanges.FirstOrDefault(r => !IsValidRangeLine(r));
        if (invalidRange is not null)
        {
            SetStatus($"Rango adicional no válido: {invalidRange}. Quitalo y volvé a agregarlo con IP inicial y final.", true);
            _lstAdditionalRanges.Focus();
            return false;
        }
        _config.NetworkRanges = additionalRanges.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        _config.SyncIntervalSeconds = SelectedIntervalSeconds();
        _config.SnmpEnabled = _chkSnmp.Checked;
        _config.StartWithWindows = _chkStartup.Checked;
        _config.ScanOnStart = _chkScan.Checked;
        _config.WindowsScanEnabled = _chkWindowsScan.Checked;
        _config.NetworkScanEnabled = _chkNetworkScan.Checked;
        _config.NetworkScanOnStart = _chkNetworkOnStart.Checked;
        _config.UsbControlEnabled = _chkUsbControl.Checked;

        try
        {
            _store.Save(_config);
            _txtSnmpV3AuthPassword.Clear();
            _txtSnmpV3PrivacyPassword.Clear();
            RefreshConfiguredIpList();
            AutostartManager.SetEnabled(_config.StartWithWindows);
            ResetTimer();
            if (showFeedback)
            {
                SetStatus("Configuración guardada.", false);
                ShowTrayMessage("A&T Printer Agent", "Configuración guardada correctamente.");
            }
            return true;
        }
        catch (Exception ex)
        {
            SetStatus("No se pudo guardar: " + ex.Message, true);
            return false;
        }
    }

    private async Task ActivateSerialAsync()
    {
        if (_busy) return;
        var serial = _txtSerial.Text.Trim().Replace("\r", "").Replace("\n", "").ToUpperInvariant();
        if (serial.Length < 16)
        {
            SetStatus("El serial está vacío o incompleto. Copiá el serial del proyecto y volvé a intentar.", true);
            _txtSerial.Focus();
            return;
        }
        if (string.IsNullOrWhiteSpace(_txtSyncUrl.Text) || !Uri.TryCreate(_txtSyncUrl.Text.Trim(), UriKind.Absolute, out var syncUri) || syncUri.Scheme != Uri.UriSchemeHttps)
        {
            SetStatus("La URL de sincronización no es válida. Debe ser una dirección HTTPS de Supabase.", true);
            _txtSyncUrl.Focus();
            return;
        }

        _txtSerial.Text = serial;
        if (!PersistConfigFromUi(showFeedback: false)) return;
        _serialValidatedForCurrentValue = false;

        _busy = true;
        _btnActivate.Enabled = false;
        try
        {
            _lblAgent.Text = "Agente: validando serial…";
            SetStatus("Validando serial y vinculando este agente al proyecto…", false);
            var result = await SendSyncCoreAsync();
            if (result?.Ok == true)
            {
                _serialValidatedForCurrentValue = true;
                _lblAgent.Text = $"Agente: {result.AgentCode ?? "registrado"} · {result.ProjectName ?? "proyecto vinculado"}";
                ShowTrayMessage("A&T Printer Agent", $"Agente vinculado al proyecto {result.ProjectName ?? "seleccionado"}.");
            }
            else
            {
                _serialValidatedForCurrentValue = false;
                _lblAgent.Text = "Agente: no vinculado";
            }
        }
        catch (Exception ex)
        {
            _lblAgent.Text = "Agente: no vinculado";
            SetStatus("No se pudo activar el serial: " + ex.Message, true);
        }
        finally
        {
            _btnActivate.Enabled = true;
            _busy = false;
        }
    }

    private async Task StartAsync()
    {
        try
        {
            AutostartManager.SetEnabled(_config.StartWithWindows);
        }
        catch { }

        ResetTimer();
        if (!_config.InitialDiscoveryComplete)
        {
            SetStatus("Configuración inicial pendiente: agregá IP o rango, activá el serial y pulsá ‘Iniciar búsqueda de impresoras’.", false);
            return;
        }

        if (_config.ScanOnStart)
            await ScanAndMaybeSyncAsync();
        else
            SetStatus("Agente iniciado. Esperando sincronización automática…", false);
    }

    private void ResetTimer()
    {
        _timer?.Stop();
        _timer?.Dispose();
        _timer = new System.Windows.Forms.Timer { Interval = Math.Clamp(_config.SyncIntervalSeconds, 1800, 7200) * 1000 };
        _timer.Tick += async (_, _) => await ScanAndMaybeSyncAsync();
        _timer.Start();

        // The data-upload interval may be 30 minutes, 1 hour or 2 hours.
        // Remote configuration uses a separate lightweight poll every 30 seconds.
        if (_remoteTimer is null)
        {
            _remoteTimer = new System.Windows.Forms.Timer { Interval = 30_000 };
            _remoteTimer.Tick += async (_, _) => await PollRemoteCommandsAsync();
        }
        _remoteTimer.Start();
    }

    private async Task PollRemoteCommandsAsync()
    {
        if (_busy || string.IsNullOrWhiteSpace(_config.Serial) || string.IsNullOrWhiteSpace(_config.InstallId)) return;
        _busy = true;
        try
        {
            var result = await _sync.PollRemoteCommandsAsync(_config, _shutdown.Token);
            if (!result.Ok)
            {
                // The agent may be starting up before its first registration. Avoid showing
                // repeated transient polling errors in the status bar every 30 seconds.
                return;
            }

            var backup = System.Text.Json.JsonSerializer.Deserialize<AgentConfig>(
                System.Text.Json.JsonSerializer.Serialize(_config),
                new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)) ?? new AgentConfig();
            var confirmedIds = result.AcknowledgedCommandIds.ToHashSet();
            var remoteUpdateConfirmed = _config.RemoteCommandAcks.Any(a =>
                confirmedIds.Contains(a.id) &&
                a.status == "done" &&
                string.Equals(a.result_message, "REMOTE_UPDATE_PENDING", StringComparison.Ordinal));
            if (confirmedIds.Count > 0)
                _config.RemoteCommandAcks.RemoveAll(a => confirmedIds.Contains(a.id));

            var acknowledgementBatch = new List<RemoteCommandAck>();
            var settingsChanged = false;
            var intervalChanged = false;
            var rescanRequested = false;

            foreach (var command in result.RemoteCommands.GroupBy(c => c.id).Select(g => g.First()))
            {
                if (string.Equals(command.command, "update_now", StringComparison.OrdinalIgnoreCase))
                {
                    acknowledgementBatch.Add(new RemoteCommandAck
                    {
                        id = command.id,
                        status = "done",
                        result_message = "REMOTE_UPDATE_PENDING"
                    });
                    continue;
                }

                try
                {
                    var message = ApplyRemoteCommand(command, ref settingsChanged, ref intervalChanged, ref rescanRequested);
                    acknowledgementBatch.Add(new RemoteCommandAck { id = command.id, status = "done", result_message = message });
                }
                catch (Exception ex)
                {
                    acknowledgementBatch.Add(new RemoteCommandAck
                    {
                        id = command.id,
                        status = "failed",
                        result_message = (ex.Message.Length > 900 ? ex.Message[..900] : ex.Message)
                    });
                }
            }

            // Persist configuration first. If persistence fails, revert settings and explicitly
            // report command failure instead of falsely confirming a change that was not saved.
            if (settingsChanged)
            {
                try
                {
                    _store.Save(_config);
                }
                catch (Exception ex)
                {
                    _config = backup;
                    settingsChanged = false;
                    intervalChanged = false;
                    rescanRequested = false;
                    acknowledgementBatch = acknowledgementBatch.Select(a => a.status == "done"
                        ? new RemoteCommandAck { id = a.id, status = "failed", result_message = "No se pudo guardar la configuración local: " + ex.Message }
                        : a).ToList();
                    ApplyConfigToUi();
                }
            }

            foreach (var ack in acknowledgementBatch)
            {
                _config.RemoteCommandAcks.RemoveAll(existing => existing.id == ack.id);
                _config.RemoteCommandAcks.Add(ack);
            }
            if (confirmedIds.Count > 0 || acknowledgementBatch.Count > 0)
            {
                try { _store.Save(_config); }
                catch (Exception ex) { SetStatus("La configuración se aplicó, pero no se pudo guardar el acuse remoto: " + ex.Message, true); }
            }

            if (settingsChanged)
            {
                _chkUsbControl.Checked = _config.UsbControlEnabled;
                _chkNetworkScan.Checked = _config.NetworkScanEnabled;
                _chkNetworkOnStart.Checked = _config.NetworkScanOnStart;
                _cboInterval.SelectedIndex = IntervalToIndex(_config.SyncIntervalSeconds);
                RefreshConfiguredIpList();
                RefreshAdditionalRangeList();
                if (intervalChanged) ResetTimer();
            }

            if (acknowledgementBatch.Count > 0)
            {
                var successful = acknowledgementBatch.Count(a => a.status == "done");
                var failed = acknowledgementBatch.Count - successful;
                SetStatus(failed == 0
                    ? $"Configuración remota aplicada: {successful} comando(s). Se confirmó en la próxima consulta."
                    : $"Configuración remota: {successful} aplicado(s), {failed} rechazado(s). Revisá el estado del comando en el panel.", failed > 0);
            }

            if (rescanRequested && _config.InitialDiscoveryComplete)
            {
                var scanned = await ScanAsync();
                if (scanned) await SendSyncCoreAsync();
            }

            if (remoteUpdateConfirmed)
            {
                var updateResult = await _updates.CheckAndOfferAsync(this, false, _shutdown.Token, remoteRequest: true);
                if (updateResult == "UPDATE_STARTED")
                {
                    SetStatus("Actualización remota verificada e iniciada. El agente se cerrará para instalar la nueva versión.", false);
                    ExitApplication();
                    return;
                }
                if (updateResult == "UPDATE_DECLINED")
                    SetStatus("Actualización rechazada en esta PC. A&T Printer Agent continúa funcionando con la versión actual.", false);
                else if (updateResult == "UPDATE_ADMIN_DECLINED")
                    SetStatus("Se canceló el permiso de Windows en esta PC. No se instaló la actualización y el agente continúa funcionando.", false);
                else
                    SetStatus("Se recibió la orden remota, pero no hay una versión posterior disponible o no se pudo iniciar el instalador. Revisá la conexión, el registro del lanzamiento y los permisos de Windows.", true);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            SetStatus("Error al aplicar configuración remota: " + ex.Message, true);
        }
        finally { _busy = false; }
    }

    private string ApplyRemoteCommand(RemoteAgentCommand command, ref bool settingsChanged, ref bool intervalChanged, ref bool rescanRequested)
    {
        var payload = command.payload;
        string GetText(params string[] names)
        {
            if (payload.ValueKind != System.Text.Json.JsonValueKind.Object) return "";
            foreach (var name in names)
                if (payload.TryGetProperty(name, out var value) && value.ValueKind == System.Text.Json.JsonValueKind.String)
                    return value.GetString()?.Trim() ?? "";
            return "";
        }
        bool? GetBool(params string[] names)
        {
            if (payload.ValueKind != System.Text.Json.JsonValueKind.Object) return null;
            foreach (var name in names)
            {
                if (!payload.TryGetProperty(name, out var value)) continue;
                if (value.ValueKind == System.Text.Json.JsonValueKind.True) return true;
                if (value.ValueKind == System.Text.Json.JsonValueKind.False) return false;
            }
            return null;
        }
        int? GetInt(params string[] names)
        {
            if (payload.ValueKind != System.Text.Json.JsonValueKind.Object) return null;
            foreach (var name in names)
                if (payload.TryGetProperty(name, out var value) && value.TryGetInt32(out var number)) return number;
            return null;
        }
        string CanonicalIp(string raw)
        {
            if (!IsValidIpv4(raw)) throw new InvalidOperationException("IP inválida. Se requiere una dirección IPv4, por ejemplo 192.168.1.25.");
            return IPAddress.Parse(raw.Trim()).ToString();
        }

        switch (command.command)
        {
            case "set_usb_control":
            {
                var enabled = GetBool("enabled", "usb_control_enabled")
                    ?? throw new InvalidOperationException("Falta el valor enabled para la búsqueda USB.");
                if (_config.UsbControlEnabled != enabled)
                {
                    _config.UsbControlEnabled = enabled;
                    settingsChanged = true;
                    rescanRequested = true;
                }
                return enabled ? "Búsqueda USB activada; la búsqueda por IP no se modificó." : "Búsqueda USB desactivada; la búsqueda por IP permanece independiente.";
            }
            case "add_ip":
            {
                var ip = CanonicalIp(GetText("ip", "address"));
                _config.NetworkIps ??= new List<string>();
                if (!_config.NetworkIps.Contains(ip, StringComparer.OrdinalIgnoreCase))
                {
                    _config.NetworkIps.Add(ip);
                    settingsChanged = true;
                }
                if (!_config.NetworkScanEnabled || !_config.NetworkScanOnStart)
                {
                    _config.NetworkScanEnabled = true;
                    _config.NetworkScanOnStart = true;
                    settingsChanged = true;
                }
                rescanRequested = true;
                return $"IP {ip} configurada para búsqueda de red.";
            }
            case "remove_ip":
            {
                var ip = CanonicalIp(GetText("ip", "address"));
                var removed = _config.NetworkIps.RemoveAll(x => string.Equals(x?.Trim(), ip, StringComparison.OrdinalIgnoreCase));
                if (removed > 0) { settingsChanged = true; rescanRequested = true; }
                return removed > 0 ? $"IP {ip} quitada de la búsqueda." : $"La IP {ip} ya no estaba configurada.";
            }
            case "add_ip_range":
            {
                var start = CanonicalIp(GetText("start", "ip_start", "from"));
                var end = CanonicalIp(GetText("end", "ip_end", "to"));
                if (CompareIpv4(start, end) > 0) throw new InvalidOperationException("La IP inicial del rango debe ser menor o igual que la final.");
                var range = $"{start} - {end}";
                _config.NetworkRanges ??= new List<string>();
                var isPrimaryRange = IsValidIpv4(_config.NetworkRangeStart) && IsValidIpv4(_config.NetworkRangeEnd)
                    && string.Equals(IPAddress.Parse(_config.NetworkRangeStart).ToString(), start, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(IPAddress.Parse(_config.NetworkRangeEnd).ToString(), end, StringComparison.OrdinalIgnoreCase);
                var isAdditionalRange = _config.NetworkRanges.Any(x => string.Equals(NormalizeRangeLine(x), range, StringComparison.OrdinalIgnoreCase));
                if (!isPrimaryRange && !isAdditionalRange)
                {
                    _config.NetworkRanges.Add(range);
                    settingsChanged = true;
                }
                if (!_config.NetworkScanEnabled || !_config.NetworkScanOnStart)
                {
                    _config.NetworkScanEnabled = true;
                    _config.NetworkScanOnStart = true;
                    settingsChanged = true;
                }
                rescanRequested = true;
                return $"Rango {range} configurado para búsqueda de red.";
            }
            case "remove_ip_range":
            {
                var start = CanonicalIp(GetText("start", "ip_start", "from"));
                var end = CanonicalIp(GetText("end", "ip_end", "to"));
                if (CompareIpv4(start, end) > 0) throw new InvalidOperationException("La IP inicial del rango debe ser menor o igual que la final.");
                var range = $"{start} - {end}";
                var removed = (_config.NetworkRanges ?? new List<string>()).RemoveAll(x => string.Equals(NormalizeRangeLine(x), range, StringComparison.OrdinalIgnoreCase));
                var removedPrimary = IsValidIpv4(_config.NetworkRangeStart) && IsValidIpv4(_config.NetworkRangeEnd)
                    && string.Equals(IPAddress.Parse(_config.NetworkRangeStart).ToString(), start, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(IPAddress.Parse(_config.NetworkRangeEnd).ToString(), end, StringComparison.OrdinalIgnoreCase);
                if (removedPrimary)
                {
                    _config.NetworkRangeStart = "";
                    _config.NetworkRangeEnd = "";
                }
                if (removed > 0 || removedPrimary) { settingsChanged = true; rescanRequested = true; }
                return removed > 0 || removedPrimary ? $"Rango {range} quitado." : $"El rango {range} no estaba configurado.";
            }
            case "set_network_scan":
            {
                var enabled = GetBool("enabled", "network_scan_enabled")
                    ?? throw new InvalidOperationException("Falta el valor enabled para la búsqueda de red.");
                if (_config.NetworkScanEnabled != enabled || (enabled && !_config.NetworkScanOnStart))
                {
                    _config.NetworkScanEnabled = enabled;
                    if (enabled) _config.NetworkScanOnStart = true;
                    settingsChanged = true;
                    if (enabled) rescanRequested = true;
                }
                return enabled ? "Búsqueda por IP/red activada." : "Búsqueda por IP/red desactivada; la búsqueda USB no se modificó.";
            }
            case "set_interval":
            {
                var seconds = GetInt("seconds", "sync_interval_seconds")
                    ?? throw new InvalidOperationException("Falta el intervalo de sincronización.");
                if (seconds is not (1800 or 3600 or 7200)) throw new InvalidOperationException("Intervalo no válido. Opciones: 1800, 3600 o 7200 segundos.");
                if (_config.SyncIntervalSeconds != seconds)
                {
                    _config.SyncIntervalSeconds = seconds;
                    settingsChanged = true;
                    intervalChanged = true;
                }
                return $"Intervalo de envío actualizado a {seconds / 60} minutos.";
            }
            case "rescan_now":
                if (!_config.InitialDiscoveryComplete) throw new InvalidOperationException("Primero completá la búsqueda inicial de impresoras en esta PC.");
                rescanRequested = true;
                return "Búsqueda solicitada; se iniciará ahora.";
            default:
                throw new InvalidOperationException("Comando remoto no reconocido: " + command.command);
        }
    }

    private async Task ScanAndMaybeSyncAsync()
    {
        if (_busy || !_config.InitialDiscoveryComplete) return;
        _busy = true;
        try
        {
            var scanned = await ScanAsync();
            if (!scanned) return;
            if (!string.IsNullOrWhiteSpace(_config.Serial))
                await SendSyncCoreAsync();
            else
                SetStatus("Impresoras detectadas. Falta el serial de activación para subir datos.", true);
        }
        finally { _busy = false; }
    }

    private async Task<bool> ScanAsync()
    {
        try
        {
            var combined = new List<PrinterInfo>();

            if (_config.WindowsScanEnabled)
            {
                SetStatus("Buscando impresoras instaladas en Windows…", false);
                var windows = await Task.Run(PrinterScanner.ScanWindowsPrinters, _shutdown.Token);
                if (!_config.UsbControlEnabled)
                    windows = windows.Where(p => !string.Equals(p.ConnectionType, "usb", StringComparison.OrdinalIgnoreCase)).ToList();
                combined.AddRange(windows);
            }

            if (_config.NetworkScanEnabled)
            {
                var progress = new Progress<string>(SetStatusFromProgress);
                SetStatus("Buscando impresoras directamente en la red…", false);
                var network = await NetworkPrinterScanner.ScanAsync(_config, _shutdown.Token, progress);
                combined = MergePrinters(combined, network);
            }

            await EnrichIpPrintersAsync(combined);
            _printers = combined
                .OrderBy(x => x.IpAddress, StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .ToList();

            RefreshGrid();
            SetStatus($"Escaneo terminado: {_printers.Count} impresoras.", false);
            return true;
        }
        catch (OperationCanceledException)
        {
            SetStatus("Escaneo cancelado.", false);
            return false;
        }
        catch (Exception ex)
        {
            SetStatus("Error de escaneo: " + ex.Message, true);
            return false;
        }
    }

    private async Task StartInitialSearchAsync()
    {
        if (_busy) return;
        if (!PersistConfigFromUi(showFeedback: false)) return;

        if (string.IsNullOrWhiteSpace(_config.Serial))
        {
            SetStatus("Primero pegá el serial del proyecto y pulsá ‘Activar serial’.", true);
            _txtSerial.Focus();
            return;
        }
        if (!_serialValidatedForCurrentValue ||
            !string.Equals(_config.Serial.Trim(), _txtSerial.Text.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            SetStatus("El serial todavía no está activado. Pulsá ‘Activar serial’ y esperá la confirmación del proyecto.", true);
            _btnActivate.Focus();
            return;
        }

        var start = _txtNetworkStart.Text.Trim();
        var end = _txtNetworkEnd.Text.Trim();
        var hasStart = !string.IsNullOrWhiteSpace(start);
        var hasEnd = !string.IsNullOrWhiteSpace(end);
        if (hasStart != hasEnd)
        {
            SetStatus("Para usar un rango, completá tanto la IP inicial como la IP final.", true);
            return;
        }
        var additionalRanges = GetAdditionalRangesFromList();
        var hasAdditionalRanges = additionalRanges.Count > 0;
        if (_config.NetworkIps.Count == 0 && !hasStart && !hasAdditionalRanges)
        {
            SetStatus("Agregá al menos una IP individual o completá uno o más rangos antes de iniciar la búsqueda.", true);
            _txtNetworkIp.Focus();
            return;
        }
        if (hasStart && (!IsValidIpv4(start) || !IsValidIpv4(end) || CompareIpv4(start, end) > 0))
        {
            SetStatus("El rango inicial/final no es válido. Ingresá dos direcciones IPv4, por ejemplo 192.168.1.1 y 192.168.1.254.", true);
            return;
        }
        var invalidAdditional = additionalRanges.FirstOrDefault(r => !IsValidRangeLine(r));
        if (invalidAdditional is not null)
        {
            SetStatus($"Rango adicional no válido: {invalidAdditional}. Usá formato IP inicial - IP final.", true);
            _lstAdditionalRanges.Focus();
            return;
        }

        _busy = true;
        _btnInitialSearch.Enabled = false;
        try
        {
            _config.NetworkScanEnabled = true;
            _config.NetworkScanOnStart = true;
            _chkNetworkScan.Checked = true;
            _chkNetworkOnStart.Checked = true;
            _config.NetworkRangeStart = start;
            _config.NetworkRangeEnd = end;
            _config.NetworkRanges = additionalRanges.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            _store.Save(_config);

            SetStatus("Búsqueda inicial iniciada. Esperá a que termine el escaneo; se muestran los avances abajo.", false);
            var scanned = await ScanAsync();
            if (!scanned) return;

            _config.InitialDiscoveryComplete = true;
            _store.Save(_config);
            UpdateInitialSearchButton();
            ResetTimer();

            var result = await SendSyncCoreAsync();
            if (result?.Ok == true)
            {
                SetStatus($"Búsqueda finalizada: {_printers.Count} impresoras. Proyecto {result.ProjectName ?? "vinculado"}; sincronización correcta. Se repetirá automáticamente cada {_config.SyncIntervalSeconds} segundos.", false);
            }
            else
            {
                SetStatus("Búsqueda guardada, pero la sincronización falló. Revisá el error de Supabase; el agente volverá a intentar sincronizar automáticamente.", true);
            }
        }
        catch (Exception ex)
        {
            SetStatus("No se pudo guardar o iniciar la búsqueda: " + ex.Message, true);
        }
        finally
        {
            _btnInitialSearch.Enabled = true;
            _busy = false;
        }
    }

    private async Task ScanNetworkOnlyAsync()
    {
        if (_busy) return;
        if (!_config.InitialDiscoveryComplete)
        {
            SetStatus("Primero activá el serial y usá ‘Iniciar búsqueda de impresoras’ en Configuración.", true);
            return;
        }
        _busy = true;
        try
        {
            if (!PersistConfigFromUi(showFeedback: false)) return;
            _chkNetworkScan.Checked = true;
            _config.NetworkScanEnabled = true;
            _store.Save(_config);
            var progress = new Progress<string>(SetStatusFromProgress);
            SetStatus("Escaneando la red…", false);
            var network = await NetworkPrinterScanner.ScanAsync(_config, _shutdown.Token, progress);
            var combined = MergePrinters(_printers, network);
            await EnrichIpPrintersAsync(combined);
            _printers = combined
                .OrderBy(x => x.IpAddress, StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
            RefreshGrid();
            SetStatus($"Escaneo de red terminado: {network.Count} impresoras encontradas.", false);
            if (!string.IsNullOrWhiteSpace(_config.Serial))
                await SendSyncCoreAsync();
            else
                SetStatus("Red escaneada. Falta el serial de activación para subir datos.", true);
        }
        catch (OperationCanceledException)
        {
            SetStatus("Escaneo de red cancelado.", false);
        }
        catch (Exception ex)
        {
            SetStatus("Error de red: " + ex.Message, true);
        }
        finally
        {
            _busy = false;
        }
    }

    private void ReadNetworkSettingsFromUi()
    {
        if (!AddNetworkIpFromInput(persist: true))
            throw new InvalidOperationException("Corregí la IP individual antes de escanear.");
        _config.NetworkIp = "";
        _config.NetworkRangeStart = _txtNetworkStart.Text.Trim();
        _config.NetworkRangeEnd = _txtNetworkEnd.Text.Trim();
        _config.NetworkScanEnabled = true;
    }

    private List<string> GetAdditionalRangesFromList() =>
        _lstAdditionalRanges is null
            ? (_config.NetworkRanges ?? new List<string>()).Where(x => !string.IsNullOrWhiteSpace(x)).ToList()
            : _lstAdditionalRanges.Items.Cast<object>().Select(x => x.ToString()?.Trim() ?? string.Empty)
                .Where(x => !string.IsNullOrWhiteSpace(x)).ToList();

    private void RefreshAdditionalRangeList()
    {
        if (_lstAdditionalRanges is null) return;
        _config.NetworkRanges ??= new List<string>();
        _lstAdditionalRanges.BeginUpdate();
        _lstAdditionalRanges.Items.Clear();
        foreach (var range in _config.NetworkRanges
                     .Where(x => !string.IsNullOrWhiteSpace(x))
                     .Select(NormalizeRangeLine)
                     .Distinct(StringComparer.OrdinalIgnoreCase))
            _lstAdditionalRanges.Items.Add(range);
        _lstAdditionalRanges.EndUpdate();
        if (_lblRangeCount is not null)
        {
            var count = _lstAdditionalRanges.Items.Count;
            _lblRangeCount.Text = $"{count} rango{(count == 1 ? "" : "s")} adicional{(count == 1 ? "" : "es")} guardado{(count == 1 ? "" : "s")}";
        }
    }

    private bool AddAdditionalRangeFromInput(bool persist)
    {
        var startText = _txtNewRangeStart.Text.Trim();
        var endText = _txtNewRangeEnd.Text.Trim();
        if (string.IsNullOrWhiteSpace(startText) || string.IsNullOrWhiteSpace(endText))
        {
            SetStatus("Completá la IP inicial y la IP final del router.", true);
            (string.IsNullOrWhiteSpace(startText) ? _txtNewRangeStart : _txtNewRangeEnd).Focus();
            return false;
        }
        if (!IsValidIpv4(startText) || !IsValidIpv4(endText))
        {
            SetStatus("El rango no es válido. Ingresá dos direcciones IPv4, por ejemplo 192.168.2.1 y 192.168.2.254.", true);
            _txtNewRangeStart.Focus();
            return false;
        }
        var start = IPAddress.Parse(startText).ToString();
        var end = IPAddress.Parse(endText).ToString();
        if (CompareIpv4(start, end) > 0)
        {
            SetStatus("La IP inicial debe ser menor o igual que la IP final.", true);
            _txtNewRangeStart.Focus();
            return false;
        }
        var value = $"{start} - {end}";
        var ranges = GetAdditionalRangesFromList();
        if (ranges.Any(x => string.Equals(NormalizeRangeLine(x), value, StringComparison.OrdinalIgnoreCase)))
        {
            SetStatus("Ese rango ya está agregado.", false);
            _txtNewRangeStart.Clear();
            _txtNewRangeEnd.Clear();
            return true;
        }
        _config.NetworkRanges = ranges;
        _config.NetworkRanges.Add(value);
        _config.NetworkScanEnabled = true;
        if (_chkNetworkScan is not null) _chkNetworkScan.Checked = true;
        try
        {
            if (persist) _store.Save(_config);
            RefreshAdditionalRangeList();
            _txtNewRangeStart.Clear();
            _txtNewRangeEnd.Clear();
            SetStatus($"Rango {value} agregado y guardado.", false);
            return true;
        }
        catch (Exception ex)
        {
            _config.NetworkRanges.RemoveAll(x => string.Equals(NormalizeRangeLine(x), value, StringComparison.OrdinalIgnoreCase));
            RefreshAdditionalRangeList();
            SetStatus("No se pudo guardar el rango: " + ex.Message, true);
            return false;
        }
    }

    private void RemoveSelectedAdditionalRange()
    {
        if (_lstAdditionalRanges.SelectedItem is not string selected)
        {
            SetStatus("Seleccioná un rango de la lista para quitarlo.", true);
            return;
        }
        var ranges = GetAdditionalRangesFromList();
        var index = ranges.FindIndex(x => string.Equals(NormalizeRangeLine(x), NormalizeRangeLine(selected), StringComparison.OrdinalIgnoreCase));
        if (index < 0) { RefreshAdditionalRangeList(); return; }
        var removed = ranges[index];
        ranges.RemoveAt(index);
        _config.NetworkRanges = ranges;
        try
        {
            _store.Save(_config);
            RefreshAdditionalRangeList();
            SetStatus($"Rango {NormalizeRangeLine(removed)} quitado de la configuración.", false);
        }
        catch (Exception ex)
        {
            _config.NetworkRanges.Insert(index, removed);
            RefreshAdditionalRangeList();
            SetStatus("No se pudo quitar el rango: " + ex.Message, true);
        }
    }

    private void ClearMainRange()
    {
        _txtNetworkStart.Clear();
        _txtNetworkEnd.Clear();
        _config.NetworkRangeStart = string.Empty;
        _config.NetworkRangeEnd = string.Empty;
        try
        {
            _store.Save(_config);
            SetStatus("Rango principal quitado. Los rangos adicionales y las IP individuales se conservaron.", false);
        }
        catch (Exception ex)
        {
            SetStatus("No se pudo quitar el rango principal: " + ex.Message, true);
        }
    }

    private static string NormalizeRangeLine(string? line)
    {
        if (!IsValidRangeLine(line)) return (line ?? string.Empty).Trim();
        var parts = line!.Split('-', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return $"{IPAddress.Parse(parts[0])} - {IPAddress.Parse(parts[1])}";
    }

    private static int CompareIpv4(string first, string second)
    {
        static uint ToUInt(string value)
        {
            var b = IPAddress.Parse(value).GetAddressBytes();
            return ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3];
        }
        return ToUInt(first).CompareTo(ToUInt(second));
    }

    private static bool IsValidIpv4(string? value) =>
        IPAddress.TryParse(value?.Trim(), out var ip) && ip.AddressFamily == AddressFamily.InterNetwork;

    private static bool IsValidRangeLine(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return false;
        var parts = line.Split('-', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 2 && IsValidIpv4(parts[0]) && IsValidIpv4(parts[1]) && CompareIpv4(parts[0], parts[1]) <= 0;
    }

    private void UseLocalNetworkRange()
    {
        var range = NetworkPrinterScanner.DetectLocalNetworkRange();
        if (string.IsNullOrWhiteSpace(range))
        {
            SetStatus("No se pudo determinar una red IPv4 local.", true);
            return;
        }

        var parts = range.Split('-', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 2)
        {
            _txtNetworkStart.Text = parts[0];
            _txtNetworkEnd.Text = parts[1];
            _config.NetworkRangeStart = parts[0];
            _config.NetworkRangeEnd = parts[1];
            _config.NetworkScanEnabled = true;
            _config.NetworkScanOnStart = true;
            _chkNetworkScan.Checked = true;
            _chkNetworkOnStart.Checked = true;
            try
            {
                _store.Save(_config);
                SetStatus($"Red local detectada y guardada: {parts[0]} - {parts[1]}", false);
            }
            catch (Exception ex)
            {
                SetStatus("Se detectó la red, pero no se pudo guardar: " + ex.Message, true);
            }
        }
    }

    private async Task EnrichIpPrintersAsync(List<PrinterInfo> printers)
    {
        if (!_config.SnmpEnabled) return;
        using var gate = new SemaphoreSlim(12);
        var tasks = printers.Where(p => !string.IsNullOrWhiteSpace(p.IpAddress)).Select(async printer =>
        {
            await gate.WaitAsync(_shutdown.Token).ConfigureAwait(false);
            try
            {
                var client = new SnmpClient(_config);
                await client.EnrichAsync(printer, _shutdown.Token).ConfigureAwait(false);
            }
            finally { gate.Release(); }
        });
        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private static List<PrinterInfo> MergePrinters(IEnumerable<PrinterInfo> existing, IEnumerable<PrinterInfo> discovered)
    {
        var result = existing.ToList();
        var incoming = discovered.ToList();
        foreach (var network in incoming)
        {
            PrinterInfo? match = null;

            // A matching physical serial is the strongest indication that USB and IP are the same device.
            if (!string.IsNullOrWhiteSpace(network.SerialNumber))
                match = result.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x.SerialNumber) &&
                    string.Equals(x.SerialNumber.Trim(), network.SerialNumber.Trim(), StringComparison.OrdinalIgnoreCase));

            if (match is null && !string.IsNullOrWhiteSpace(network.IpAddress))
                match = result.FirstOrDefault(x => string.Equals(x.IpAddress, network.IpAddress, StringComparison.OrdinalIgnoreCase));

            if (match is null)
                match = result.FirstOrDefault(x => x.ConnectionType == "ip" &&
                    x.WindowsPort?.StartsWith("WSD", StringComparison.OrdinalIgnoreCase) == true && SamePrinterText(x, network));

            // If the hardware exposes no serial, merge USB and network entries only when the model/manufacturer pair
            // is unique on both sides. This avoids collapsing two printers of the same model at one customer site.
            if (match is null && network.ConnectionType == "ip" && !string.IsNullOrWhiteSpace(network.Model))
            {
                var usbCandidates = result.Where(x => x.ConnectionType == "usb" && SameModelAndManufacturer(x, network)).ToList();
                var networkCandidates = incoming.Where(x => x.ConnectionType == "ip" && SameModelAndManufacturer(x, network)).ToList();
                if (usbCandidates.Count == 1 && networkCandidates.Count == 1)
                    match = usbCandidates[0];
            }

            if (match is null)
            {
                result.Add(network);
                continue;
            }

            // Network identity wins when the same physical printer is also installed as USB in Windows.
            match.IpAddress = network.IpAddress ?? match.IpAddress;
            if (network.ConnectionType == "ip") match.ConnectionType = "ip";
            match.Manufacturer = Prefer(network.Manufacturer, match.Manufacturer);
            match.Model = Prefer(network.Model, match.Model);
            match.SerialNumber = Prefer(network.SerialNumber, match.SerialNumber);
            match.SerialSource = Prefer(network.SerialSource, match.SerialSource);
            match.IdentitySource = Prefer(network.IdentitySource, match.IdentitySource);
            match.SnmpName = Prefer(network.SnmpName, match.SnmpName);
            match.CounterSource = Prefer(network.CounterSource, match.CounterSource);
            match.PageCount = network.PageCount ?? match.PageCount;
            match.ScannerCount = network.ScannerCount ?? match.ScannerCount;
            match.TonerBlackPercent = network.TonerBlackPercent ?? match.TonerBlackPercent;
            match.TonerCyanPercent = network.TonerCyanPercent ?? match.TonerCyanPercent;
            match.TonerMagentaPercent = network.TonerMagentaPercent ?? match.TonerMagentaPercent;
            match.TonerYellowPercent = network.TonerYellowPercent ?? match.TonerYellowPercent;
            match.SnmpReachable |= network.SnmpReachable;
            match.LastStatus = network.LastStatus ?? match.LastStatus;
            if (network.SnmpReachable) match.SnmpError = null;
            else if (!string.IsNullOrWhiteSpace(network.SnmpError)) match.SnmpError = network.SnmpError;
            match.UsbPort = match.UsbPort ?? network.UsbPort;
            match.WindowsPort = network.WindowsPort ?? match.WindowsPort;
            if (IsBetterPrinterName(network.DisplayName)) match.DisplayName = network.DisplayName;
        }

        return result
            .Where(p => !string.IsNullOrWhiteSpace(p.DisplayName))
            .GroupBy(p => !string.IsNullOrWhiteSpace(p.SerialNumber)
                ? "serial:" + p.SerialNumber.Trim().ToLowerInvariant()
                : !string.IsNullOrWhiteSpace(p.IpAddress)
                    ? "ip:" + p.IpAddress.Trim().ToLowerInvariant()
                    : p.PrinterKey, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.Aggregate(MergeTwoPrinters))
            .ToList();
    }

    private static bool SameModelAndManufacturer(PrinterInfo a, PrinterInfo b)
    {
        var am = NormalizeText(a.Model);
        var bm = NormalizeText(b.Model);
        if (string.IsNullOrWhiteSpace(am) || !string.Equals(am, bm, StringComparison.OrdinalIgnoreCase)) return false;
        var af = NormalizeText(a.Manufacturer);
        var bf = NormalizeText(b.Manufacturer);
        return string.IsNullOrWhiteSpace(af) || string.IsNullOrWhiteSpace(bf) || af == bf;
    }

    private static PrinterInfo MergeTwoPrinters(PrinterInfo a, PrinterInfo b)
    {
        if (IsBetterPrinterName(b.DisplayName)) a.DisplayName = b.DisplayName;
        a.Manufacturer = Prefer(b.Manufacturer, a.Manufacturer);
        a.Model = Prefer(b.Model, a.Model);
        a.IpAddress = Prefer(b.IpAddress, a.IpAddress);
        if (b.ConnectionType == "ip") a.ConnectionType = "ip";
        a.UsbPort = Prefer(b.UsbPort, a.UsbPort);
        a.WindowsPort = Prefer(b.WindowsPort, a.WindowsPort);
        a.SerialNumber = Prefer(b.SerialNumber, a.SerialNumber);
        a.SerialSource = Prefer(b.SerialSource, a.SerialSource);
        a.CounterSource = Prefer(b.CounterSource, a.CounterSource);
        a.IdentitySource = Prefer(b.IdentitySource, a.IdentitySource);
        a.SnmpName = Prefer(b.SnmpName, a.SnmpName);
        a.PageCount = b.PageCount ?? a.PageCount;
        a.ScannerCount = b.ScannerCount ?? a.ScannerCount;
        a.TonerBlackPercent = b.TonerBlackPercent ?? a.TonerBlackPercent;
        a.TonerCyanPercent = b.TonerCyanPercent ?? a.TonerCyanPercent;
        a.TonerMagentaPercent = b.TonerMagentaPercent ?? a.TonerMagentaPercent;
        a.TonerYellowPercent = b.TonerYellowPercent ?? a.TonerYellowPercent;
        a.SnmpReachable |= b.SnmpReachable;
        if (b.SnmpReachable) a.SnmpError = null; else if (!string.IsNullOrWhiteSpace(b.SnmpError)) a.SnmpError = b.SnmpError;
        return a;
    }

    private static string? Prefer(string? preferred, string? fallback)
        => !string.IsNullOrWhiteSpace(preferred) ? preferred.Trim() : fallback;

    private static bool IsBetterPrinterName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var v = value.Trim().ToLowerInvariant();
        return v.Contains("laserjet") || v.Contains("officejet") || v.Contains("deskjet") ||
               v.Contains("pagewide") || v.Contains("printer") || v.Contains("ricoh") ||
               v.Contains("brother") || v.Contains("epson") || v.Contains("canon") ||
               v.Contains("kyocera") || v.Contains("xerox") || v.Contains("lexmark") ||
               v.StartsWith("hp ") || v.StartsWith("hewlett");
    }

    private static bool SamePrinterText(PrinterInfo a, PrinterInfo b)
    {
        var an = NormalizeText(a.DisplayName);
        var bn = NormalizeText(b.DisplayName);
        if (!string.IsNullOrWhiteSpace(an) && !string.IsNullOrWhiteSpace(bn) && (an.Contains(bn) || bn.Contains(an))) return true;
        var am = NormalizeText(a.Model);
        var bm = NormalizeText(b.Model);
        return !string.IsNullOrWhiteSpace(am) && !string.IsNullOrWhiteSpace(bm) && (am.Contains(bm) || bm.Contains(am));
    }

    private static string NormalizeText(string? value)
        => new string((value ?? "").ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    private void SetStatusFromProgress(string message) => SetStatus(message, false);

    private void RefreshGrid()
    {
        _grid.Rows.Clear();
        foreach (var p in _printers)
        {
            var row = _grid.Rows.Add(p.DisplayName, p.Manufacturer, p.Model, p.ConnectionType, p.IpAddress, p.LastStatus, p.PageCount?.ToString() ?? "—", p.SerialNumber ?? "—", p.AcquisitionSummary, FormatToner(p));
            _grid.Rows[row].Tag = p;
        }
        _lblCounts.Text = $"{_printers.Count} impresoras";
    }

    private static string FormatToner(PrinterInfo p)
    {
        var parts = new List<string>();
        if (p.TonerBlackPercent is not null) parts.Add($"K {p.TonerBlackPercent:0}%");
        if (p.TonerCyanPercent is not null) parts.Add($"C {p.TonerCyanPercent:0}%");
        if (p.TonerMagentaPercent is not null) parts.Add($"M {p.TonerMagentaPercent:0}%");
        if (p.TonerYellowPercent is not null) parts.Add($"Y {p.TonerYellowPercent:0}%");
        return parts.Count == 0 ? "—" : string.Join("  ", parts);
    }

    private async Task SyncNowAsync()
    {
        if (_busy) return;
        if (!_config.InitialDiscoveryComplete)
        {
            SetStatus("Para la primera ejecución, configurá IP/rango, activá el serial y pulsá ‘Iniciar búsqueda de impresoras’.", true);
            return;
        }
        if (string.IsNullOrWhiteSpace(_config.Serial))
        {
            SetStatus("Falta el serial de activación. Podés cargarlo en Configuración.", true);
            return;
        }
        _busy = true;
        try
        {
            if (_printers.Count == 0)
                await ScanAsync();
            await SendSyncCoreAsync();
        }
        finally
        {
            _busy = false;
        }
    }

    private async Task<SyncResult?> SendSyncCoreAsync()
    {
        if (string.IsNullOrWhiteSpace(_config.Serial))
        {
            SetStatus("Falta el serial de activación. Podés cargarlo en Configuración.", true);
            return null;
        }

        _btnSync.Enabled = false;
        try
        {
            SetStatus("Enviando información a Supabase…", false);
            var result = await _sync.SendAsync(_config, _printers, _shutdown.Token);
            if (result.AcknowledgedCommandIds.Count > 0)
            {
                var confirmed = result.AcknowledgedCommandIds.ToHashSet();
                if (_config.RemoteCommandAcks.RemoveAll(a => confirmed.Contains(a.id)) > 0)
                {
                    try { _store.Save(_config); } catch { }
                }
            }
            if (result.Ok)
            {
                _serialValidatedForCurrentValue = string.Equals(_config.Serial.Trim(), _txtSerial.Text.Trim(), StringComparison.OrdinalIgnoreCase);
                _lblAgent.Text = $"Agente: {result.AgentCode ?? "registrado"} · {result.ProjectName ?? "proyecto vinculado"}";
                SetStatus($"{result.Message} Proyecto: {result.ProjectName ?? "(sin nombre)"}. Código: {result.AgentCode ?? "registrado"}.", false);
                _tray.Text = "A&T Printer Agent · conectado";
            }
            else
            {
                _serialValidatedForCurrentValue = false;
                SetStatus("Supabase: " + result.Message, true);
                _tray.Text = "A&T Printer Agent · sin conexión";
            }
            return result;
        }
        finally
        {
            _btnSync.Enabled = true;
        }
    }

    private void SetStatus(string text, bool error)
    {
        if (InvokeRequired) { BeginInvoke(() => SetStatus(text, error)); return; }
        _lblStatus.Text = text;
        _lblStatus.ForeColor = error ? Color.FromArgb(185, 28, 28) : Color.FromArgb(71, 85, 105);
    }

    private void ShowFromTray() => ShowFromProgramFiles();

    private void ShowFromProgramFiles()
    {
        ShowInTaskbar = true;
        Opacity = 1;
        Show();
        WindowState = FormWindowState.Normal;
        BringToFront();
        Activate();
    }

    private void ShowTrayMessage(string title, string text)
    {
        // The agent intentionally has no notification-area icon or balloon in silent mode.
        if (_tray is not null && _tray.Visible) _tray.ShowBalloonTip(2500, title, text, ToolTipIcon.Info);
    }

    private async Task CheckForUpdatesAsync(bool manual)
    {
        var result = await _updates.CheckAndOfferAsync(this, manual, _shutdown.Token);
        if (result == "UPDATE_STARTED") ExitApplication();
    }

    private void ExitApplication()
    {
        _updateTimer?.Stop();
        _updateTimer?.Dispose();
        _shutdown.Cancel();
        _remoteTimer?.Stop();
        _remoteTimer?.Dispose();
        _timer?.Stop();
        _timer?.Dispose();
        _tray.Visible = false;
        _tray.Dispose();
        Application.Exit();
    }
}
