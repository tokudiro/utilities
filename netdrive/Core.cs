using System.ComponentModel;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;

namespace NetDrive;

/// <summary>1行 = 1ドライブ。接続先・ドライブ文字・自動接続の有無をまとめて持つ。</summary>
public class Drive
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public bool AutoConnect { get; set; } = true;
    public string Letter { get; set; } = "Z";
    public string Host { get; set; } = "";
    public string Share { get; set; } = "";
    public int Port { get; set; } = 445;
    public string Username { get; set; } = "";

    /// <summary>空でなければ、ネットワーク共有ではなくローカルフォルダをドライブ文字に割り当てる (subst と同じ)。</summary>
    public string LocalPath { get; set; } = "";

    [JsonIgnore] public bool IsLocal => LocalPath.Length > 0;
    [JsonIgnore] public string Target => IsLocal ? LocalPath : Unc;
    [JsonIgnore] public string Local => Letter.TrimEnd(':').ToUpperInvariant() + ":";
    [JsonIgnore] public string Unc => $@"\\{Host}\{Share.Trim('\\')}";
}

public class Config
{
    public List<Drive> Drives { get; set; } = new();

    // 1つ前の形式(接続先と割り当ての2リスト)。読み込み時に Drives へ移行する。
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<OldConnection>? Connections { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<OldAssignment>? Assignments { get; set; }

    public class OldConnection
    {
        public string Id { get; set; } = "";
        public string Host { get; set; } = "";
        public string Share { get; set; } = "";
        public int Port { get; set; } = 445;
        public string Username { get; set; } = "";
    }

    public class OldAssignment
    {
        public bool Enabled { get; set; } = true;
        public string Letter { get; set; } = "Z";
        public string ConnectionId { get; set; } = "";
    }

    // パスワードは保存しない。Windows資格情報マネージャー(CredWrite)に任せる。
    public static string Path { get; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "netdrive", "netdrive.json");

    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static Config Load()
    {
        Config c;
        try { c = JsonSerializer.Deserialize<Config>(File.ReadAllText(Path), Json) ?? new(); }
        catch { return new(); }

        if (c.Assignments is { Count: > 0 })
        {
            foreach (var a in c.Assignments)
            {
                var o = c.Connections?.FirstOrDefault(x => x.Id == a.ConnectionId);
                if (o is null) continue;
                c.Drives.Add(new Drive
                {
                    AutoConnect = a.Enabled, Letter = a.Letter,
                    Host = o.Host, Share = o.Share, Port = o.Port, Username = o.Username,
                });
            }
            c.Connections = null;
            c.Assignments = null;
            c.Save();
        }
        return c;
    }

    public void Save()
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        File.WriteAllText(Path, JsonSerializer.Serialize(this, Json));
    }
}

public enum DriveState { Off, Connected, Waiting, Stopped }

/// <summary>
/// 自動接続ONのドライブを監視してつなぐ。
/// 届かない間 (VPN確立前など) は間隔を延ばしながら静かに再試行し、
/// 認証エラーなど待っても直らない失敗では再試行を止めて通知する。
/// </summary>
public sealed class Monitor : IDisposable
{
    const int MinBackoff = 10, MaxBackoff = 300, TickSeconds = 5;

    // 再試行しても直らない Win32 エラー: 権限不足・共有名誤り・パスワード誤り・資格情報の衝突など。
    static readonly HashSet<uint> Fatal = new() { 5, 67, 86, 1219, 1326, 1327, 1328, 1330, 1331, 1909, 2202 };

    class Runtime
    {
        public DateTime Next = DateTime.MinValue;
        public int Backoff;
        public bool Stopped, Suppressed, Manual;
    }

    readonly Func<Config> _config;
    readonly CancellationTokenSource _cts = new();
    readonly SemaphoreSlim _kick = new(0);
    readonly Dictionary<string, Runtime> _rt = new();
    readonly Dictionary<string, string> _last = new();
    readonly object _lock = new();

    public event Action<Drive, DriveState, string>? StatusChanged;
    /// <summary>再試行を止める失敗が起きたときに1回だけ呼ばれる。</summary>
    public event Action<Drive, string>? Alert;

    public Monitor(Func<Config> config)
    {
        _config = config;
        _ = Task.Run(LoopAsync);
    }

    Runtime Get(Drive d)
    {
        lock (_lock) return _rt.TryGetValue(d.Id, out var r) ? r : _rt[d.Id] = new Runtime();
    }

    /// <summary>待機を打ち切って今すぐ確認する (復帰・ネットワーク変更時)。停止中のドライブは再開しない。</summary>
    public void RunNow()
    {
        lock (_lock) foreach (var r in _rt.Values) r.Next = DateTime.MinValue;
        _kick.Release();
    }

    /// <summary>手動の接続。停止状態や切断済みの抑止を解除して試す。</summary>
    public void Connect(Drive d)
    {
        var r = Get(d);
        r.Stopped = false; r.Suppressed = false; r.Manual = true; r.Next = DateTime.MinValue;
        _kick.Release();
    }

    public void Disconnect(Drive d)
    {
        var r = Get(d);
        r.Suppressed = true; r.Manual = false; r.Stopped = false;
        if (d.IsLocal) Native.RemoveSubst(d.Local);
        else Native.WNetCancelConnection2(d.Local, 0, true);
        Report(d, DriveState.Off, "切断済み");
        _kick.Release();
    }

    async Task LoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            foreach (var d in _config().Drives.ToList())
            {
                try { await ProcessAsync(d, _cts.Token); }
                catch (OperationCanceledException) { return; }
                catch (Exception e) { Report(d, DriveState.Waiting, $"エラー: {e.Message}"); }
            }
            try { await _kick.WaitAsync(TimeSpan.FromSeconds(TickSeconds), _cts.Token); }
            catch (OperationCanceledException) { return; }
        }
    }

    void ProcessLocal(Drive d, Runtime r)
    {
        var path = Native.NormalizePath(d.LocalPath);
        var current = Native.QueryDos(d.Local);

        if (current is not null && current.Equals(@"\??\" + path, StringComparison.OrdinalIgnoreCase))
        {
            r.Backoff = 0; r.Manual = false; r.Stopped = false;
            Report(d, DriveState.Connected, "割り当て済み");
            return;
        }

        if (r.Suppressed) { Report(d, DriveState.Off, "切断済み"); return; }
        if (r.Stopped) return;
        if (!d.AutoConnect && !r.Manual) { Report(d, DriveState.Off, "未接続（手動）"); return; }
        if (DateTime.Now < r.Next) return;

        // 外付けやネットワーク上のフォルダは、まだ見えないことがあるので待つ。
        if (!Directory.Exists(path))
        {
            Wait(r);
            Report(d, DriveState.Waiting, "待機中（フォルダが見つかりません）");
            return;
        }

        // 別のものが同じ文字を使っているときは、既存の割り当てを壊さず止める。
        if (current is not null) { Stop(d, r, $"{d.Local} は別のものに割り当て済みです"); return; }

        if (Native.AddSubst(d.Local, path)) { r.Backoff = 0; r.Manual = false; Report(d, DriveState.Connected, "割り当てました"); }
        else Stop(d, r, "割り当てに失敗しました");
    }

    async Task ProcessAsync(Drive d, CancellationToken ct)
    {
        var r = Get(d);
        if (d.IsLocal) { ProcessLocal(d, r); return; }
        var current = Native.GetRemote(d.Local);
        var same = current is not null && current.Equals(d.Unc, StringComparison.OrdinalIgnoreCase);

        if (same && await ResponsiveAsync(d.Local))
        {
            r.Backoff = 0; r.Manual = false; r.Stopped = false;
            Report(d, DriveState.Connected, "接続済み");
            return;
        }

        if (r.Suppressed) { Report(d, DriveState.Off, "切断済み"); return; }
        if (r.Stopped) return;
        if (!d.AutoConnect && !r.Manual) { Report(d, DriveState.Off, "未接続（手動）"); return; }
        if (DateTime.Now < r.Next) return;

        // VPN確立前の長いタイムアウトを避けるため、ポート疎通が取れてから割り当てる。
        if (!await PortOpenAsync(d.Host, d.Port, ct))
        {
            Wait(r);
            Report(d, DriveState.Waiting, $"待機中（{d.Host} に届きません）");
            return;
        }

        // 別の共有が同じ文字に割り当て済みなら、利用者の接続を壊さないよう触らずに止める。
        if (current is not null && !same)
        {
            Stop(d, r, $"{d.Local} は別の共有 ({current}) に割り当て済みです");
            return;
        }
        if (same) Native.WNetCancelConnection2(d.Local, 0, true); // 応答しない古い接続を張り直す

        var nr = new Native.NETRESOURCE { dwType = 1, lpLocalName = d.Local, lpRemoteName = d.Unc };
        var rc = Native.WNetAddConnection2(ref nr, null, null, 0);
        if (rc == 0)
        {
            r.Backoff = 0; r.Manual = false;
            Report(d, DriveState.Connected, "接続しました");
        }
        else if (Fatal.Contains(rc))
            Stop(d, r, ExplainError(rc));
        else
        {
            Wait(r);
            Report(d, DriveState.Waiting, $"再試行待ち: {new Win32Exception((int)rc).Message}");
        }
    }

    static string ExplainError(uint rc) => rc switch
    {
        86 or 1326 or 1327 or 1328 or 1330 or 1331 => "認証エラー（ユーザー名かパスワードを確認）",
        1219 => "同じサーバーに別の資格情報で接続済みです",
        67 => "共有名が見つかりません",
        5 => "アクセスが拒否されました",
        _ => new Win32Exception((int)rc).Message,
    };

    void Stop(Drive d, Runtime r, string reason)
    {
        r.Stopped = true; r.Manual = false;
        Report(d, DriveState.Stopped, $"停止: {reason}");
        Alert?.Invoke(d, $"{d.Local} {d.Unc}\n{reason}");
    }

    static void Wait(Runtime r)
    {
        r.Backoff = r.Backoff == 0 ? MinBackoff : Math.Min(r.Backoff * 2, MaxBackoff);
        r.Next = DateTime.Now.AddSeconds(r.Backoff);
    }

    void Report(Drive d, DriveState s, string text)
    {
        lock (_lock)
        {
            if (_last.TryGetValue(d.Id, out var prev) && prev == text) return;
            _last[d.Id] = text;
        }
        StatusChanged?.Invoke(d, s, text);
    }

    // 応答しないネットワークドライブへの Directory.Exists は長くブロックするため、時間を区切る。
    static async Task<bool> ResponsiveAsync(string local)
    {
        var probe = Task.Run(() => Directory.Exists(local + "\\"));
        return await Task.WhenAny(probe, Task.Delay(3000)) == probe && probe.Result;
    }

    static async Task<bool> PortOpenAsync(string host, int port, CancellationToken ct)
    {
        try
        {
            using var c = new TcpClient();
            using var t = CancellationTokenSource.CreateLinkedTokenSource(ct);
            t.CancelAfter(TimeSpan.FromSeconds(3));
            await c.ConnectAsync(host, port, t.Token);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return false; }
    }

    public void Dispose() => _cts.Cancel();
}

/// <summary>Windowsが覚えている接続情報から、取り込み候補を集める。パスワードは読めない。</summary>
public record Candidate(string Letter, string Host, string Share, string Username, string Source);

public static class Importer
{
    public static List<Candidate> Scan()
    {
        var creds = Native.EnumerateCredentials(); // host -> user
        var result = new List<Candidate>();

        void Add(string letter, string unc, string user, string source)
        {
            if (!TrySplit(unc, out var host, out var share)) return;
            if (string.IsNullOrEmpty(user) && creds.TryGetValue(host, out var found)) user = found;
            result.Add(new Candidate(letter, host, share, user ?? "", source));
        }

        // 現在つながっているドライブ
        foreach (var di in DriveInfo.GetDrives().Where(x => x.DriveType == DriveType.Network))
        {
            var local = di.Name.Substring(0, 2);
            var remote = Native.GetRemote(local);
            if (remote is not null) Add(local.TrimEnd(':'), remote, "", "接続中");
        }

        // 「再接続する」にした永続マッピング
        using (var net = Registry.CurrentUser.OpenSubKey("Network"))
            foreach (var letter in net?.GetSubKeyNames() ?? Array.Empty<string>())
            {
                using var k = net!.OpenSubKey(letter);
                if (k?.GetValue("RemotePath") is string p)
                    Add(letter.ToUpperInvariant(), p, k.GetValue("UserName") as string ?? "", "永続設定");
            }

        // 「ネットワークドライブの割り当て」の入力履歴 (ドライブ文字は残っていない)
        using (var mru = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Map Network Drive MRU"))
            foreach (var name in mru?.GetValueNames() ?? Array.Empty<string>())
                if (name != "MRUList" && mru!.GetValue(name) is string p)
                    Add("", p, "", "入力履歴");

        // 同じ接続先は、情報の多い順 (接続中 > 永続設定 > 履歴) に1件だけ残す。
        return result
            .GroupBy(c => $@"\\{c.Host}\{c.Share}".ToLowerInvariant())
            .Select(g => g.OrderBy(c => c.Letter.Length == 0).First())
            .ToList();
    }

    static bool TrySplit(string unc, out string host, out string share)
    {
        host = share = "";
        var parts = unc.TrimStart('\\').Split('\\', 2, StringSplitOptions.RemoveEmptyEntries);
        if (!unc.StartsWith(@"\\") || parts.Length < 2) return false;
        host = parts[0]; share = parts[1].TrimEnd('\\');
        return true;
    }
}

public static class Native
{
    [StructLayout(LayoutKind.Sequential)]
    public struct NETRESOURCE
    {
        public uint dwScope, dwType, dwDisplayType, dwUsage;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpLocalName;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpRemoteName;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpComment;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpProvider;
    }

    [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
    public static extern uint WNetAddConnection2(ref NETRESOURCE r, string? password, string? user, uint flags);
    [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
    public static extern uint WNetCancelConnection2(string name, uint flags, bool force);
    [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
    static extern uint WNetGetConnection(string local, StringBuilder remote, ref int len);

    public static string? GetRemote(string local)
    {
        var len = 512;
        var sb = new StringBuilder(len);
        return WNetGetConnection(local, sb, ref len) == 0 ? sb.ToString() : null;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool DefineDosDevice(uint flags, string name, string? target);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern uint QueryDosDevice(string name, StringBuilder target, uint max);

    /// <summary>末尾の \ を除いた絶対パス。ルート (C:\) は \ を残す。</summary>
    public static string NormalizePath(string p)
    {
        var full = System.IO.Path.GetFullPath(p);
        return full.Length > 3 ? full.TrimEnd('\\') : full;
    }

    /// <summary>ドライブ文字の現在の割り当て先 (例 \??\C:\work)。未割り当てなら null。</summary>
    public static string? QueryDos(string local)
    {
        var sb = new StringBuilder(1024);
        return QueryDosDevice(local, sb, 1024) == 0 ? null : sb.ToString();
    }

    public static bool AddSubst(string local, string path) => DefineDosDevice(0, local, path);

    public static void RemoveSubst(string local)
    {
        if (QueryDos(local) is { } target) DefineDosDevice(1 | 2 | 4, local, target); // RAW_TARGET_PATH|REMOVE|EXACT_MATCH
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct CREDENTIAL
    {
        public uint Flags, Type;
        public string TargetName;
        public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist, AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string UserName;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool CredWrite(ref CREDENTIAL c, uint flags);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool CredEnumerate(string? filter, uint flags, out uint count, out IntPtr creds);
    [DllImport("advapi32.dll")]
    static extern void CredFree(IntPtr buffer);

    /// <summary>資格情報マネージャーにホスト宛の資格情報を保存する (CRED_TYPE_DOMAIN_PASSWORD, 永続)。</summary>
    public static void SaveCredential(string host, string user, string password)
    {
        var bytes = Encoding.Unicode.GetBytes(password);
        var blob = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, blob, bytes.Length);
            var c = new CREDENTIAL
            {
                Type = 2, TargetName = host, UserName = user,
                CredentialBlobSize = (uint)bytes.Length, CredentialBlob = blob, Persist = 2,
            };
            if (!CredWrite(ref c, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        finally { Marshal.FreeHGlobal(blob); }
    }

    /// <summary>登録済みの資格情報から、ホスト名 -> ユーザー名の対応を返す (パスワードは読まない)。</summary>
    public static Dictionary<string, string> EnumerateCredentials()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!CredEnumerate(null, 0, out var count, out var buf)) return map;
        try
        {
            for (var i = 0; i < count; i++)
            {
                var p = Marshal.ReadIntPtr(buf, i * IntPtr.Size);
                var c = Marshal.PtrToStructure<CREDENTIAL>(p);
                if (c.Type is 1 or 2 && !string.IsNullOrEmpty(c.UserName))
                    map.TryAdd(c.TargetName.Replace("TERMSRV/", ""), c.UserName);
            }
        }
        finally { CredFree(buf); }
        return map;
    }
}
