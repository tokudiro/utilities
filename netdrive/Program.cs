using Microsoft.Win32;

namespace NetDrive;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        using var mutex = new Mutex(true, "netdrive-single-instance", out var first);
        if (!first) return;

        ApplicationConfiguration.Initialize();
        // 自動起動時 (--minimized) はトレイだけに常駐する。
        Application.Run(new MainForm(startHidden: args.Contains("--minimized")));
    }
}

sealed class MainForm : Form
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    readonly Config _cfg = Config.Load();
    readonly Monitor _monitor;
    readonly DataGridView _grid = new()
    {
        Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, RowHeadersVisible = false,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = false,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
    };
    readonly Dictionary<string, string> _status = new();
    readonly NotifyIcon _tray = new();
    readonly bool _startHidden;
    bool _quit;

    public MainForm(bool startHidden)
    {
        _startHidden = startHidden;
        Text = "netdrive";
        // exeに埋め込んだアイコンを、ウィンドウとトレイで共通に使う。
        var appIcon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? SystemIcons.Application;
        Icon = appIcon;
        ClientSize = new Size(760, 360);
        StartPosition = FormStartPosition.CenterScreen;

        foreach (var h in new[] { "自動", "ドライブ", "接続先", "ユーザー", "状態" }) _grid.Columns.Add(h, h);
        _grid.Columns[0].FillWeight = 20;
        _grid.Columns[1].FillWeight = 30;
        _grid.Columns[2].FillWeight = 90;
        _grid.Columns[3].FillWeight = 50;
        _grid.Columns[4].FillWeight = 100;
        _grid.CellDoubleClick += (_, _) => EditSelected();

        var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 40, Padding = new Padding(6) };
        bar.Controls.Add(Btn("接続", () => { if (Selected() is { } d) _monitor!.Connect(d); }));
        bar.Controls.Add(Btn("切断", () => { if (Selected() is { } d) _monitor!.Disconnect(d); }));
        bar.Controls.Add(new Label { Text = "|", AutoSize = true, Padding = new Padding(4, 6, 4, 0) });
        bar.Controls.Add(Btn("追加", AddDrive));
        bar.Controls.Add(Btn("編集", EditSelected));
        bar.Controls.Add(Btn("削除", DeleteSelected));
        var auto = new CheckBox { Text = "ログオン時に自動起動", AutoSize = true, Checked = IsAutoStart(), Margin = new Padding(12, 6, 0, 0) };
        auto.CheckedChanged += (_, _) => SetAutoStart(auto.Checked);
        bar.Controls.Add(auto);

        Controls.Add(_grid);
        Controls.Add(bar);

        var menu = new ContextMenuStrip();
        menu.Items.Add("開く", null, (_, _) => ShowWindow());
        menu.Items.Add("今すぐ確認", null, (_, _) => _monitor!.RunNow());
        menu.Items.Add("終了", null, (_, _) => { _quit = true; Close(); });
        _tray.Icon = appIcon;
        _tray.Text = "netdrive";
        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => ShowWindow();
        _tray.Visible = true;

        Refill();
        _monitor = new Monitor(() => _cfg);
        _monitor.StatusChanged += (d, _, text) => OnUi(() => { _status[d.Id] = text; Refill(); });
        _monitor.Alert += (_, msg) => OnUi(() => _tray.ShowBalloonTip(10000, "netdrive: 接続を停止しました", msg, ToolTipIcon.Warning));

        // スリープ復帰・ネットワーク変更の直後は、待たずに確認する。
        SystemEvents.PowerModeChanged += OnPower;
        System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged += OnNetworkChanged;
    }

    void OnPower(object? s, PowerModeChangedEventArgs e) { if (e.Mode == PowerModes.Resume) _monitor.RunNow(); }
    void OnNetworkChanged(object? s, EventArgs e) => _monitor.RunNow();

    void OnUi(Action a)
    {
        if (IsDisposed) return;
        try { BeginInvoke(a); } catch (InvalidOperationException) { }
    }

    static Button Btn(string text, Action onClick)
    {
        var b = new Button { Text = text, AutoSize = true };
        b.Click += (_, _) => onClick();
        return b;
    }

    protected override void SetVisibleCore(bool value) =>
        base.SetVisibleCore(_startHidden && !IsHandleCreated ? false : value);

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_quit && e.CloseReason != CloseReason.WindowsShutDown)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        SystemEvents.PowerModeChanged -= OnPower;
        System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged -= OnNetworkChanged;
        _monitor.Dispose();
        _tray.Visible = false;
        base.OnFormClosing(e);
    }

    void ShowWindow() { Show(); WindowState = FormWindowState.Normal; Activate(); }

    void Refill()
    {
        var sel = _grid.CurrentRow?.Index ?? -1;
        _grid.Rows.Clear();
        foreach (var d in _cfg.Drives)
        {
            _status.TryGetValue(d.Id, out var s);
            _grid.Rows.Add(d.AutoConnect ? "✓" : "", d.Local, d.Target, d.Username, s ?? "");
        }
        if (sel >= 0 && sel < _grid.Rows.Count) _grid.CurrentCell = _grid.Rows[sel].Cells[1];
    }

    Drive? Selected() =>
        _grid.CurrentRow is { } r && r.Index < _cfg.Drives.Count ? _cfg.Drives[r.Index] : null;

    void Save() { _cfg.Save(); Refill(); _monitor.RunNow(); }

    string FreeLetter() =>
        "ZYXWVUTSRQPONMLKJIHGFED".Select(c => c.ToString())
            .FirstOrDefault(c => _cfg.Drives.All(d => !d.Letter.Equals(c, StringComparison.OrdinalIgnoreCase))) ?? "Z";

    void AddDrive()
    {
        var d = new Drive { Letter = FreeLetter() };
        if (DriveDialog.Show(this, d, _cfg.Drives)) { _cfg.Drives.Add(d); Save(); }
    }

    void EditSelected()
    {
        if (Selected() is { } d && DriveDialog.Show(this, d, _cfg.Drives)) Save();
    }

    void DeleteSelected()
    {
        if (Selected() is { } d && MessageBox.Show(this, $"{d.Local} ({d.Target}) を一覧から削除しますか？\n接続中のドライブは切断しません。",
                "削除", MessageBoxButtons.OKCancel) == DialogResult.OK)
        { _cfg.Drives.Remove(d); _status.Remove(d.Id); Save(); }
    }

    static bool IsAutoStart()
    {
        using var k = Registry.CurrentUser.OpenSubKey(RunKey);
        return k?.GetValue("netdrive") is not null;
    }

    static void SetAutoStart(bool on)
    {
        using var k = Registry.CurrentUser.OpenSubKey(RunKey, true)!;
        if (on) k.SetValue("netdrive", $"\"{Environment.ProcessPath}\" --minimized");
        else k.DeleteValue("netdrive", false);
    }
}

static class DriveDialog
{
    public static bool Show(IWin32Window owner, Drive d, List<Drive> all)
    {
        using var f = new Form
        {
            Text = "ドライブの設定", FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false,
            StartPosition = FormStartPosition.CenterParent, ClientSize = new Size(380, 420),
        };

        var auto = new CheckBox { Text = "起動時・切断時に自動で接続する", Checked = d.AutoConnect, AutoSize = true };
        var letter = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 60 };
        letter.Items.AddRange("DEFGHIJKLMNOPQRSTUVWXYZ".Select(c => (object)c.ToString()).ToArray());
        letter.SelectedItem = d.Letter.ToUpperInvariant();
        var host = new TextBox { Text = d.Host, Width = 220 };
        var share = new TextBox { Text = d.Share, Width = 220 };
        var port = new NumericUpDown { Minimum = 1, Maximum = 65535, Value = d.Port, Width = 80 };
        var user = new TextBox { Text = d.Username, Width = 220 };
        var pass = new TextBox { UseSystemPasswordChar = true, Width = 220 };

        var kind = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 220 };
        kind.Items.AddRange(new object[] { "ネットワーク共有", "ローカルフォルダ" });
        kind.SelectedIndex = d.IsLocal ? 1 : 0;
        var folder = new TextBox { Text = d.LocalPath, Width = 150 };
        var browse = new Button { Text = "参照…", AutoSize = true };
        browse.Click += (_, _) =>
        {
            using var fb = new FolderBrowserDialog { SelectedPath = folder.Text };
            if (fb.ShowDialog(f) == DialogResult.OK) folder.Text = fb.SelectedPath;
        };
        var folderRow = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0), WrapContents = false };
        folderRow.Controls.AddRange(new Control[] { folder, browse });

        // 種別に応じて、ネットワーク共有用の行とローカルフォルダ用の行を切り替える。
        var netRows = new List<Control>();
        var localRows = new List<Control>();
        var t = new TableLayoutPanel { Dock = DockStyle.Top, Height = 360, Padding = new Padding(12), ColumnCount = 2 };
        void Row(string label, Control c, List<Control>? group = null)
        {
            var l = new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Padding = new Padding(0, 4, 8, 4) };
            t.Controls.Add(l);
            t.Controls.Add(c);
            group?.Add(l);
            group?.Add(c);
        }
        var pick = new Button { Text = "既存の接続から選ぶ…", AutoSize = true };
        pick.Click += (_, _) =>
        {
            var known = all.Where(o => o != d && !o.IsLocal).Select(o => o.Unc.ToLowerInvariant()).ToHashSet();
            var found = Importer.Scan().Where(c => !known.Contains($@"\\{c.Host}\{c.Share}".ToLowerInvariant())).ToList();
            if (found.Count == 0) { MessageBox.Show(f, "選べる既存の接続は見つかりませんでした。"); return; }
            if (ImportDialog.Show(f, found) is not { } c) return;
            host.Text = c.Host;
            share.Text = c.Share;
            user.Text = c.Username;
            // 取得元にドライブ文字があり、他の行で使われていなければ引き継ぐ。
            if (c.Letter.Length > 0 && all.All(o => o == d || !o.Letter.Equals(c.Letter, StringComparison.OrdinalIgnoreCase)))
                letter.SelectedItem = c.Letter.ToUpperInvariant();
        };
        Row("種別", kind);
        Row("", auto);
        Row("ドライブ文字", letter);
        Row("フォルダ", folderRow, localRows);
        Row("", pick, netRows);
        Row("ホスト", host, netRows);
        Row("共有名", share, netRows);
        Row("ポート", port, netRows);
        Row("ユーザー", user, netRows);
        Row("パスワード", pass, netRows);
        var note = new Label
        {
            Text = "ログインが不要なら、ユーザーとパスワードは空欄です。\nパスワードは資格情報マネージャーに保存します。\n空欄なら既存の登録をそのまま使います。",
            AutoSize = true, ForeColor = SystemColors.GrayText,
        };
        var noteSpacer = new Label { AutoSize = true };
        t.Controls.Add(noteSpacer);
        t.Controls.Add(note);
        netRows.Add(noteSpacer);
        netRows.Add(note);

        void ApplyKind()
        {
            var local = kind.SelectedIndex == 1;
            foreach (var c in netRows) c.Visible = !local;
            foreach (var c in localRows) c.Visible = local;
        }
        kind.SelectedIndexChanged += (_, _) => ApplyKind();
        ApplyKind();

        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Location = new Point(210, 380) };
        var cancel = new Button { Text = "キャンセル", DialogResult = DialogResult.Cancel, Location = new Point(292, 380) };
        f.AcceptButton = ok; f.CancelButton = cancel;
        f.Controls.AddRange(new Control[] { t, ok, cancel });

        ok.Click += (_, _) =>
        {
            var local = kind.SelectedIndex == 1;
            string? err = null;
            if (local && string.IsNullOrWhiteSpace(folder.Text)) err = "フォルダを指定してください。";
            else if (local && !Directory.Exists(folder.Text.Trim())) err = "指定したフォルダが見つかりません。";
            else if (!local && (string.IsNullOrWhiteSpace(host.Text) || string.IsNullOrWhiteSpace(share.Text))) err = "ホストと共有名は必須です。";
            else if (all.Any(o => o != d && o.Letter.Equals((string)letter.SelectedItem!, StringComparison.OrdinalIgnoreCase)))
                err = "そのドライブ文字は他の行で使われています。";
            else if (!local && pass.Text.Length > 0 && user.Text.Length == 0) err = "パスワードを保存するにはユーザーも入力してください。";
            if (err is null && !local && pass.Text.Length > 0)
            {
                try { Native.SaveCredential(host.Text.Trim(), user.Text.Trim(), pass.Text); }
                catch (Exception e) { err = $"資格情報を保存できません: {e.Message}"; }
            }
            if (err is not null) { MessageBox.Show(f, err); f.DialogResult = DialogResult.None; }
        };

        if (f.ShowDialog(owner) != DialogResult.OK) return false;
        d.AutoConnect = auto.Checked;
        d.Letter = (string)letter.SelectedItem!;
        if (kind.SelectedIndex == 1)
        {
            d.LocalPath = folder.Text.Trim();
            d.Host = d.Share = d.Username = "";
        }
        else
        {
            d.LocalPath = "";
            d.Host = host.Text.Trim();
            d.Share = share.Text.Trim();
            d.Port = (int)port.Value;
            d.Username = user.Text.Trim();
        }
        return true;
    }
}

static class ImportDialog
{
    public static Candidate? Show(IWin32Window owner, List<Candidate> found)
    {
        using var f = new Form
        {
            Text = "既存の接続から選ぶ", MaximizeBox = false, MinimizeBox = false,
            StartPosition = FormStartPosition.CenterParent, ClientSize = new Size(620, 340),
        };
        var list = new ListView { Dock = DockStyle.Top, Height = 280, View = View.Details, FullRowSelect = true, MultiSelect = false };
        foreach (var (h, w) in new[] { ("文字", 50), ("接続先", 290), ("ユーザー", 140), ("取得元", 100) })
            list.Columns.Add(h, w);
        foreach (var c in found)
            list.Items.Add(new ListViewItem(new[] { c.Letter.Length > 0 ? c.Letter + ":" : "-", $@"\\{c.Host}\{c.Share}", c.Username, c.Source }) { Tag = c });
        var ok = new Button { Text = "選ぶ", DialogResult = DialogResult.OK, Location = new Point(440, 300) };
        var cancel = new Button { Text = "キャンセル", DialogResult = DialogResult.Cancel, Location = new Point(530, 300) };
        f.AcceptButton = ok; f.CancelButton = cancel;
        list.DoubleClick += (_, _) => { if (list.SelectedItems.Count > 0) f.DialogResult = DialogResult.OK; };
        f.Controls.AddRange(new Control[] { list, ok, cancel });

        return f.ShowDialog(owner) == DialogResult.OK && list.SelectedItems.Count > 0
            ? (Candidate)list.SelectedItems[0].Tag!
            : null;
    }
}
