using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;

namespace NfsServe;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        using var mutex = new Mutex(true, "nfsserve-single-instance", out var first);
        if (!first) return;
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm(args));
    }
}

sealed class Settings
{
    public string Folder { get; set; } = "";
    // 既定はプライベートアドレスのみ。WSL2 (172.16.0.0/12) も含む。
    public string Allow { get; set; } = "127.0.0.0/8, 10.0.0.0/8, 172.16.0.0/12, 192.168.0.0/16";
    public bool ReadOnly { get; set; }

    static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "nfsserve", "nfsserve.json");

    public static Settings Load()
    {
        try { return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new(); }
        catch { return new(); }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}

sealed class MainForm : Form
{
    const int NfsPort = 2049, PortmapPort = 111;

    readonly Settings _set = Settings.Load();
    readonly TextBox _folder = new() { Dock = DockStyle.Fill };
    readonly TextBox _allow = new() { Dock = DockStyle.Fill };
    readonly CheckBox _ro = new() { Text = "読み取り専用で公開する", AutoSize = true };
    readonly Button _start = new() { Text = "START", Width = 90, Height = 32 };
    readonly Button _stop = new() { Text = "STOP", Width = 90, Height = 32, Enabled = false };
    readonly Label _state = new() { AutoSize = true, Text = "停止中", Padding = new Padding(8, 8, 0, 0) };
    readonly TextBox _log = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill };
    readonly TextBox _example = new() { ReadOnly = true, Multiline = true, Dock = DockStyle.Fill, Height = 56 };
    RpcServer? _server;

    // 起動オプション: --folder <path> / --allow <list> / --rw|--ro / --start (起動と同時に公開開始)
    public MainForm(string[] args)
    {
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--folder" when i + 1 < args.Length: _set.Folder = args[++i]; break;
                case "--allow" when i + 1 < args.Length: _set.Allow = args[++i]; break;
                case "--ro": _set.ReadOnly = true; break;
                case "--rw": _set.ReadOnly = false; break;
                case "--start": Shown += (_, _) => StartServer(); break;
            }
        }

        Text = "nfsserve";
        Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? SystemIcons.Application;
        ClientSize = new Size(640, 520);
        StartPosition = FormStartPosition.CenterScreen;

        _folder.Text = _set.Folder;
        _allow.Text = _set.Allow;
        _ro.Checked = _set.ReadOnly;

        var browse = new Button { Text = "参照…", AutoSize = true };
        browse.Click += (_, _) =>
        {
            using var d = new FolderBrowserDialog { SelectedPath = _folder.Text };
            if (d.ShowDialog(this) == DialogResult.OK) _folder.Text = d.SelectedPath;
        };

        var form = new TableLayoutPanel { Dock = DockStyle.Top, Height = 120, ColumnCount = 3, Padding = new Padding(10) };
        form.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        form.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        form.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        void Row(string label, Control c, Control? extra = null)
        {
            form.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Padding = new Padding(0, 6, 8, 0) });
            form.Controls.Add(c);
            form.Controls.Add(extra ?? new Label());
        }
        Row("公開フォルダ", _folder, browse);
        Row("許可IP", _allow);
        form.Controls.Add(new Label());
        form.Controls.Add(_ro);
        form.Controls.Add(new Label());

        var warn = new Label
        {
            Dock = DockStyle.Top, Height = 36, Padding = new Padding(10, 0, 10, 0), ForeColor = SystemColors.GrayText,
            Text = "NFSにはパスワード認証がありません。許可IPに含まれる機器は、だれでも読み書きできます（0.0.0.0/0 で全許可）。",
        };

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 44, Padding = new Padding(10, 4, 10, 4) };
        buttons.Controls.AddRange(new Control[] { _start, _stop, _state });

        var exampleBox = new GroupBox { Text = "Linux からのマウント例", Dock = DockStyle.Top, Height = 90, Padding = new Padding(8, 4, 8, 8) };
        exampleBox.Controls.Add(_example);
        var logBox = new GroupBox { Text = "ログ", Dock = DockStyle.Fill, Padding = new Padding(8, 4, 8, 8) };
        logBox.Controls.Add(_log);

        Controls.Add(logBox);
        Controls.Add(exampleBox);
        Controls.Add(buttons);
        Controls.Add(warn);
        Controls.Add(form);

        ShowExample();
        _start.Click += (_, _) => StartServer();
        _stop.Click += (_, _) => StopServer();
    }

    void ShowExample()
    {
        var ips = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
            .Select(a => a.Address.ToString())
            .ToList();
        _example.Text = string.Join("\r\n", (ips.Count > 0 ? ips : new() { "<このPCのIP>" })
            .Select(ip => $"sudo mount -t nfs -o vers=3,proto=tcp,port={NfsPort},mountport={NfsPort},nolock {ip}:/ /mnt/nfs"));
    }

    void Log(string msg)
    {
        if (IsDisposed) return;
        void Append()
        {
            _log.AppendText($"{DateTime.Now:HH:mm:ss} {msg}\r\n");
        }
        try { if (InvokeRequired) BeginInvoke(Append); else Append(); } catch (InvalidOperationException) { }
    }

    void StartServer()
    {
        var folder = _folder.Text.Trim();
        if (!Directory.Exists(folder)) { MessageBox.Show(this, "公開フォルダが見つかりません。"); return; }

        IpFilter filter;
        try { filter = IpFilter.Parse(_allow.Text); }
        catch (FormatException e) { MessageBox.Show(this, e.Message); return; }

        var export = new Export(folder, _ro.Checked);
        try
        {
            _server = new RpcServer(
                new RpcProgram[] { new PortmapProgram(NfsPort), new MountProgram(export, Log), new NfsProgram(export, Log) },
                filter.IsAllowed, Log);
            _server.Start(NfsPort, PortmapPort);
        }
        catch (Exception e)
        {
            _server?.Dispose();
            _server = null;
            MessageBox.Show(this, $"開始できません: {e.Message}\nポート {NfsPort} を他のNFSサーバーが使っていないか確認してください。");
            return;
        }

        _set.Folder = folder; _set.Allow = _allow.Text; _set.ReadOnly = _ro.Checked;
        _set.Save();
        Log($"公開開始: {export.Root} ({(export.ReadOnly ? "読み取り専用" : "読み書き")})");
        SetRunning(true);
    }

    void StopServer()
    {
        _server?.Dispose();
        _server = null;
        SetRunning(false);
    }

    void SetRunning(bool on)
    {
        _start.Enabled = !on; _stop.Enabled = on;
        _folder.Enabled = _allow.Enabled = _ro.Enabled = !on;
        _state.Text = on ? $"公開中 (TCP {NfsPort})" : "停止中";
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _server?.Dispose();
        base.OnFormClosing(e);
    }
}
