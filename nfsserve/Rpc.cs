using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace NfsServe;

public enum Accept : uint { Success = 0, ProgUnavail = 1, ProgMismatch = 2, ProcUnavail = 3, GarbageArgs = 4, SystemErr = 5 }

/// <summary>1回のRPC呼び出しの情報。uid/gid は AUTH_SYS の資格情報から取る (無ければ0)。</summary>
public sealed record CallInfo(uint Xid, uint Prog, uint Vers, uint Proc, uint Uid, uint Gid, IPAddress? Remote);

public abstract class RpcProgram
{
    public abstract uint Program { get; }
    public abstract uint MinVersion { get; }
    public abstract uint MaxVersion { get; }

    /// <summary>Success を返すとき、<paramref name="result"/> が応答本体になる。</summary>
    public abstract Accept Call(CallInfo call, XdrReader args, XdrWriter result);
}

/// <summary>ONC RPC (RFC 5531) を TCP のレコード形式で受け付け、プログラム番号で振り分ける。</summary>
public sealed class RpcServer : IDisposable
{
    const int MaxRecord = 4 * 1024 * 1024;

    readonly Dictionary<uint, RpcProgram> _programs;
    readonly Func<IPAddress, bool> _allow;
    readonly Action<string> _log;
    readonly CancellationTokenSource _cts = new();
    readonly List<TcpListener> _listeners = new();
    readonly ConcurrentDictionary<TcpClient, byte> _clients = new();

    public RpcServer(IEnumerable<RpcProgram> programs, Func<IPAddress, bool> allow, Action<string> log)
    {
        _programs = programs.ToDictionary(p => p.Program);
        _allow = allow;
        _log = log;
    }

    /// <summary>NFS用ポートは必須。portmap用ポートは取れなくても続行する (クライアントがポートを明示すれば不要)。</summary>
    public void Start(int nfsPort, int portmapPort)
    {
        Listen(nfsPort);
        try { Listen(portmapPort); }
        catch (SocketException e)
        {
            _log($"警告: ポート {portmapPort} を使えません ({e.Message})。クライアントで port=/mountport= を指定してください。");
        }
    }

    void Listen(int port)
    {
        var l = new TcpListener(IPAddress.Any, port);
        l.Start();
        _listeners.Add(l);
        _log($"待ち受け開始: TCP {port}");
        _ = Task.Run(() => AcceptLoop(l));
    }

    async Task AcceptLoop(TcpListener l)
    {
        var ct = _cts.Token;
        while (!ct.IsCancellationRequested)
        {
            TcpClient c;
            try { c = await l.AcceptTcpClientAsync(ct); }
            catch { return; }
            _ = Task.Run(() => Serve(c, ct));
        }
    }

    async Task Serve(TcpClient c, CancellationToken ct)
    {
        var remote = (c.Client.RemoteEndPoint as IPEndPoint)?.Address;
        if (remote is not null && remote.IsIPv4MappedToIPv6) remote = remote.MapToIPv4();
        if (remote is null || !_allow(remote))
        {
            _log($"拒否: {remote} は許可IPに含まれません");
            c.Dispose();
            return;
        }

        c.NoDelay = true;
        _clients[c] = 0;
        try
        {
            var s = c.GetStream();
            while (!ct.IsCancellationRequested)
            {
                var rec = await ReadRecord(s, ct);
                if (rec is null) break;
                var reply = Handle(rec, remote);
                if (reply is null) continue;

                var framed = new byte[4 + reply.Length];
                var hdr = 0x80000000u | (uint)reply.Length;
                framed[0] = (byte)(hdr >> 24); framed[1] = (byte)(hdr >> 16); framed[2] = (byte)(hdr >> 8); framed[3] = (byte)hdr;
                Buffer.BlockCopy(reply, 0, framed, 4, reply.Length);
                await s.WriteAsync(framed, ct);
            }
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException or SocketException) { }
        catch (Exception e) { _log($"接続エラー ({remote}): {e.Message}"); }
        finally
        {
            _clients.TryRemove(c, out _);
            c.Dispose();
        }
    }

    static async Task<byte[]?> ReadRecord(NetworkStream s, CancellationToken ct)
    {
        var ms = new MemoryStream();
        var hdr = new byte[4];
        while (true)
        {
            if (!await ReadExact(s, hdr, 4, ct)) return null;
            var h = (uint)hdr[0] << 24 | (uint)hdr[1] << 16 | (uint)hdr[2] << 8 | hdr[3];
            var len = (int)(h & 0x7fffffff);
            if (ms.Length + len > MaxRecord) throw new IOException("RPCレコードが大きすぎます");
            var buf = new byte[len];
            if (!await ReadExact(s, buf, len, ct)) return null;
            ms.Write(buf);
            if ((h & 0x80000000) != 0) return ms.ToArray();
        }
    }

    static async Task<bool> ReadExact(NetworkStream s, byte[] buf, int n, CancellationToken ct)
    {
        var got = 0;
        while (got < n)
        {
            var r = await s.ReadAsync(buf.AsMemory(got, n - got), ct);
            if (r == 0) return false;
            got += r;
        }
        return true;
    }

    byte[]? Handle(byte[] rec, IPAddress remote)
    {
        var r = new XdrReader(rec, 0, rec.Length);
        uint xid;
        try
        {
            xid = r.U32();
            if (r.U32() != 0) return null; // CALL 以外は無視
            var rpcVers = r.U32();
            if (rpcVers != 2)
            {
                var d = new XdrWriter();
                d.U32(xid); d.U32(1); d.U32(1); d.U32(0); d.U32(2); d.U32(2); // MSG_DENIED / RPC_MISMATCH 2..2
                return d.ToArray();
            }

            var prog = r.U32(); var vers = r.U32(); var proc = r.U32();
            var credFlavor = r.U32();
            var cred = r.Opaque(400);
            r.U32(); r.Opaque(400); // verifier は使わない

            uint uid = 0, gid = 0;
            if (credFlavor == 1 && cred.Length >= 4)
            {
                try
                {
                    var cr = new XdrReader(cred, 0, cred.Length);
                    cr.U32(); cr.String(255); uid = cr.U32(); gid = cr.U32();
                }
                catch (XdrException) { }
            }

            var call = new CallInfo(xid, prog, vers, proc, uid, gid, remote);
            return Dispatch(call, r);
        }
        catch (XdrException) { return null; }
    }

    byte[] Dispatch(CallInfo call, XdrReader args)
    {
        var w = new XdrWriter();
        w.U32(call.Xid); w.U32(1); w.U32(0); w.U32(0); w.U32(0); // REPLY / MSG_ACCEPTED / verifier(AUTH_NONE)

        if (!_programs.TryGetValue(call.Prog, out var p)) { w.U32((uint)Accept.ProgUnavail); return w.ToArray(); }
        if (call.Vers < p.MinVersion || call.Vers > p.MaxVersion)
        {
            w.U32((uint)Accept.ProgMismatch); w.U32(p.MinVersion); w.U32(p.MaxVersion);
            return w.ToArray();
        }

        var result = new XdrWriter();
        Accept stat;
        try { stat = p.Call(call, args, result); }
        catch (XdrException) { stat = Accept.GarbageArgs; }
        catch (Exception e)
        {
            _log($"内部エラー (prog {call.Prog} proc {call.Proc}): {e.Message}");
            stat = Accept.SystemErr;
        }

        w.U32((uint)stat);
        if (stat == Accept.Success) w.Append(result);
        return w.ToArray();
    }

    public void Dispose()
    {
        _cts.Cancel();
        foreach (var l in _listeners) l.Stop();
        foreach (var c in _clients.Keys) c.Dispose();
        _log("停止しました");
    }
}

/// <summary>"192.168.0.0/24, 10.0.0.5" 形式の許可リスト (IPv4のみ)。</summary>
public sealed class IpFilter
{
    readonly List<(uint net, uint mask)> _rules = new();

    public static IpFilter Parse(string text)
    {
        var f = new IpFilter();
        foreach (var raw in text.Split(new[] { ',', ';', ' ', '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = raw.Split('/');
            if (!IPAddress.TryParse(parts[0], out var ip) || ip.AddressFamily != AddressFamily.InterNetwork)
                throw new FormatException($"IPアドレスとして読めません: {raw}");
            var bits = 32;
            if (parts.Length == 2 && (!int.TryParse(parts[1], out bits) || bits is < 0 or > 32))
                throw new FormatException($"プレフィックス長が不正です: {raw}");
            if (parts.Length > 2) throw new FormatException($"形式が不正です: {raw}");
            var mask = bits == 0 ? 0u : uint.MaxValue << (32 - bits);
            f._rules.Add((ToUInt(ip) & mask, mask));
        }
        if (f._rules.Count == 0) throw new FormatException("許可IPが空です。全許可にするなら 0.0.0.0/0 と書いてください。");
        return f;
    }

    public bool IsAllowed(IPAddress ip) =>
        ip.AddressFamily == AddressFamily.InterNetwork && _rules.Any(r => (ToUInt(ip) & r.mask) == r.net);

    static uint ToUInt(IPAddress ip)
    {
        var b = ip.GetAddressBytes();
        return (uint)b[0] << 24 | (uint)b[1] << 16 | (uint)b[2] << 8 | b[3];
    }
}
