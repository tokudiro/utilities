using System.Net;
using System.Text;

namespace NfsServe;

public sealed class NfsError : Exception
{
    public uint Status { get; }
    public NfsError(uint status) : base($"NFS3ERR {status}") { Status = status; }
}

static class St
{
    public const uint Ok = 0, NoEnt = 2, Io = 5, Acces = 13, Exist = 17, NotDir = 20, IsDir = 21, Inval = 22,
        NoSpc = 28, RoFs = 30, NameTooLong = 63, NotEmpty = 66, Stale = 70, BadHandle = 10001, NotSupp = 10004;
}

/// <summary>
/// 公開フォルダと、NFSのファイルハンドルの対応を持つ。
/// ハンドルは「起動ごとの世代(8) + 連番ID(8)」で、IDから公開フォルダ内の相対パスを引く。
/// パスそのものをハンドルに入れないのは、クライアントが任意のパスを組み立てて外へ出るのを防ぐため。
/// </summary>
public sealed class Export
{
    static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    readonly object _lock = new();
    readonly Dictionary<ulong, string> _byId = new();
    readonly Dictionary<string, ulong> _byRel = new(StringComparer.OrdinalIgnoreCase);
    readonly string _rootSep;
    ulong _next = 2;

    public string Root { get; }
    public bool ReadOnly { get; }
    public ulong Gen { get; } = (ulong)Random.Shared.NextInt64();

    public Export(string root, bool readOnly)
    {
        var full = Path.GetFullPath(root);
        Root = full.Length > 3 ? full.TrimEnd('\\') : full;
        _rootSep = Root.EndsWith('\\') ? Root : Root + "\\";
        ReadOnly = readOnly;
        _byId[1] = ""; _byRel[""] = 1;
    }

    public ulong IdFor(string rel)
    {
        lock (_lock)
        {
            if (_byRel.TryGetValue(rel, out var id)) return id;
            id = _next++;
            _byId[id] = rel; _byRel[rel] = id;
            return id;
        }
    }

    public byte[] Handle(string rel)
    {
        var h = new byte[16];
        var id = IdFor(rel);
        for (var i = 0; i < 8; i++) { h[i] = (byte)(Gen >> (56 - 8 * i)); h[8 + i] = (byte)(id >> (56 - 8 * i)); }
        return h;
    }

    public string RelFor(byte[] fh)
    {
        if (fh.Length != 16) throw new NfsError(St.BadHandle);
        ulong gen = 0, id = 0;
        for (var i = 0; i < 8; i++) { gen = gen << 8 | fh[i]; id = id << 8 | fh[8 + i]; }
        if (gen != Gen) throw new NfsError(St.Stale);
        lock (_lock) return _byId.TryGetValue(id, out var rel) ? rel : throw new NfsError(St.Stale);
    }

    public string Full(string rel)
    {
        var full = Path.GetFullPath(rel.Length == 0 ? Root : Path.Combine(Root, rel));
        if (!(full.Equals(Root, StringComparison.OrdinalIgnoreCase) || full.StartsWith(_rootSep, StringComparison.OrdinalIgnoreCase)))
            throw new NfsError(St.Acces);
        return full;
    }

    public static string Child(string rel, string name) => rel.Length == 0 ? name : rel + "\\" + name;

    public static string Parent(string rel)
    {
        var i = rel.LastIndexOf('\\');
        return i < 0 ? "" : rel[..i];
    }

    /// <summary>Windows側で意味を持つ名前 (パス区切り・ADS の ':'・予約デバイス名など) を拒否する。</summary>
    public static string ValidateName(string name)
    {
        if (name.Length == 0 || name is "." or "..") throw new NfsError(St.Inval);
        if (Encoding.UTF8.GetByteCount(name) > 255) throw new NfsError(St.NameTooLong);
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.EndsWith('.') || name.EndsWith(' '))
            throw new NfsError(St.Inval);
        var stem = name.Split('.')[0].TrimEnd();
        if (Reserved.Contains(stem)) throw new NfsError(St.Inval);
        return name;
    }

    public FileSystemInfo? Info(string rel)
    {
        var full = Full(rel);
        FileSystemInfo? i = Directory.Exists(full) ? new DirectoryInfo(full) : File.Exists(full) ? new FileInfo(full) : null;
        // ジャンクション/シンボリックリンクは公開フォルダの外へ出られるため、存在しないものとして扱う。
        return i is not null && (i.Attributes & FileAttributes.ReparsePoint) != 0 ? null : i;
    }

    public void Removed(string rel)
    {
        lock (_lock)
            foreach (var (id, r) in _byId.Where(kv => kv.Value.Length > 0 && IsSameOrUnder(kv.Value, rel)).ToList())
            { _byId.Remove(id); _byRel.Remove(r); }
    }

    public void Renamed(string oldRel, string newRel)
    {
        lock (_lock)
        {
            foreach (var (id, r) in _byId.Where(kv => kv.Value.Length > 0 && IsSameOrUnder(kv.Value, newRel)).ToList())
            { _byId.Remove(id); _byRel.Remove(r); }

            foreach (var (id, r) in _byId.Where(kv => kv.Value.Length > 0 && IsSameOrUnder(kv.Value, oldRel)).ToList())
            {
                var moved = newRel + r[oldRel.Length..];
                _byRel.Remove(r);
                _byId[id] = moved;
                _byRel[moved] = id;
            }
        }
    }

    static bool IsSameOrUnder(string path, string parent) =>
        path.Equals(parent, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(parent + "\\", StringComparison.OrdinalIgnoreCase);

    public void WriteAttr(XdrWriter w, CallInfo c, string rel, FileSystemInfo info)
    {
        var isDir = info is DirectoryInfo;
        var size = isDir ? 4096UL : (ulong)((FileInfo)info).Length;
        // 認証が無いので、誰でも読み書きできる権限ビットを見せる (制限は許可IPと読み取り専用設定で行う)。
        uint mode = isDir ? (ReadOnly ? 0x16Du : 0x1FFu) : (ReadOnly || (info.Attributes & FileAttributes.ReadOnly) != 0 ? 0x124u : 0x1B6u);

        w.U32(isDir ? 2u : 1u);
        w.U32(mode);
        w.U32(isDir ? 2u : 1u);
        w.U32(c.Uid); w.U32(c.Gid);
        w.U64(size);
        w.U64(isDir ? 4096UL : (size + 4095) / 4096 * 4096);
        w.U32(0); w.U32(0);
        w.U64(1);
        w.U64(IdFor(rel));
        Time(w, info.LastAccessTimeUtc);
        Time(w, info.LastWriteTimeUtc);
        Time(w, info.LastWriteTimeUtc);
    }

    public static void Time(XdrWriter w, DateTime t)
    {
        var d = t.ToUniversalTime() - DateTime.UnixEpoch;
        var sec = (long)Math.Floor(d.TotalSeconds);
        if (sec < 0) sec = 0;
        w.U32((uint)sec);
        w.U32((uint)(d.Ticks % TimeSpan.TicksPerSecond * 100));
    }
}

/// <summary>sattr3: 設定する属性。権限・所有者はWindowsに対応が無いので無視し、サイズと時刻だけ反映する。</summary>
sealed class Sattr
{
    public ulong? Size;
    public DateTime? Atime, Mtime;

    public static Sattr Read(XdrReader r)
    {
        var s = new Sattr();
        if (r.Bool()) r.U32(); // mode
        if (r.Bool()) r.U32(); // uid
        if (r.Bool()) r.U32(); // gid
        if (r.Bool()) s.Size = r.U64();
        s.Atime = ReadTime(r);
        s.Mtime = ReadTime(r);
        return s;
    }

    static DateTime? ReadTime(XdrReader r)
    {
        switch (r.U32())
        {
            case 1: return DateTime.UtcNow;
            case 2:
                var sec = r.U32(); var nsec = r.U32();
                return DateTime.UnixEpoch.AddSeconds(sec).AddTicks(nsec / 100);
            default: return null;
        }
    }

    public void Apply(string path, FileSystemInfo info)
    {
        if (Size is { } sz)
        {
            if (info is DirectoryInfo) throw new NfsError(St.IsDir);
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            fs.SetLength((long)sz);
        }
        if (Mtime is { } m) { if (info is DirectoryInfo) Directory.SetLastWriteTimeUtc(path, m); else File.SetLastWriteTimeUtc(path, m); }
        if (Atime is { } a) { if (info is DirectoryInfo) Directory.SetLastAccessTimeUtc(path, a); else File.SetLastAccessTimeUtc(path, a); }
    }
}

/// <summary>NFSバージョン3 (RFC 1813)。ロック・シンボリックリンク・特殊ファイルは非対応。</summary>
public sealed class NfsProgram : RpcProgram
{
    const int MaxIo = 1 << 20;
    readonly Export _fs;
    readonly Action<string> _log;

    public NfsProgram(Export fs, Action<string> log) { _fs = fs; _log = log; }

    public override uint Program => 100003;
    public override uint MinVersion => 3;
    public override uint MaxVersion => 3;

    /// <summary>失敗応答に載せる、関係するパス (属性を返すため)。</summary>
    sealed class Ctx { public string? Rel1, Rel2; }

    public override Accept Call(CallInfo c, XdrReader a, XdrWriter result)
    {
        var cx = new Ctx();
        var t = new XdrWriter();
        try
        {
            t.U32(St.Ok);
            if (!Dispatch(c, a, t, cx)) return Accept.ProcUnavail;
            result.Append(t);
        }
        catch (NfsError e) { result.Append(Fail(c, cx, e.Status)); }
        catch (XdrException) { throw; }
        catch (Exception e)
        {
            _log($"NFSエラー (proc {c.Proc}): {e.Message}");
            result.Append(Fail(c, cx, Map(e)));
        }
        return Accept.Success;
    }

    static uint Map(Exception e) => e switch
    {
        FileNotFoundException or DirectoryNotFoundException => St.NoEnt,
        UnauthorizedAccessException => St.Acces,
        PathTooLongException => St.NameTooLong,
        IOException io => (io.HResult & 0xFFFF) switch
        {
            80 or 183 => St.Exist,
            145 => St.NotEmpty,
            39 or 112 => St.NoSpc,
            32 or 33 => St.Acces,
            _ => St.Io,
        },
        _ => St.Io,
    };

    XdrWriter Fail(CallInfo c, Ctx cx, uint status)
    {
        var f = new XdrWriter();
        f.U32(status);
        switch (c.Proc)
        {
            case 1: break; // GETATTR
            case 2 or 7 or 8 or 9 or 12 or 13 or 21 or 10 or 11: Wcc(f, c, cx.Rel1); break;
            case 14: Wcc(f, c, cx.Rel1); Wcc(f, c, cx.Rel2); break;
            case 15: PostOp(f, c, cx.Rel1); Wcc(f, c, cx.Rel2); break;
            default: PostOp(f, c, cx.Rel1); break;
        }
        return f;
    }

    void PostOp(XdrWriter w, CallInfo c, string? rel)
    {
        FileSystemInfo? i = null;
        try { if (rel is not null) i = _fs.Info(rel); } catch { }
        if (i is null) { w.U32(0); return; }
        w.U32(1);
        _fs.WriteAttr(w, c, rel!, i);
    }

    void Wcc(XdrWriter w, CallInfo c, string? rel) { w.U32(0); PostOp(w, c, rel); }

    string Dir(XdrReader a, Ctx cx, bool write = false, int slot = 1)
    {
        var rel = _fs.RelFor(a.Opaque(64));
        if (slot == 1) cx.Rel1 = rel; else cx.Rel2 = rel;
        if (write && _fs.ReadOnly) throw new NfsError(St.RoFs);
        return rel;
    }

    FileSystemInfo MustExist(string rel) => _fs.Info(rel) ?? throw new NfsError(St.Stale);

    bool Dispatch(CallInfo c, XdrReader a, XdrWriter w, Ctx cx)
    {
        switch (c.Proc)
        {
            case 0: break; // NULL

            case 1: // GETATTR
            {
                var rel = _fs.RelFor(a.Opaque(64));
                _fs.WriteAttr(w, c, rel, MustExist(rel));
                break;
            }

            case 2: // SETATTR
            {
                var rel = Dir(a, cx, write: true);
                var s = Sattr.Read(a);
                var info = MustExist(rel);
                s.Apply(_fs.Full(rel), info);
                Wcc(w, c, rel);
                break;
            }

            case 3: // LOOKUP
            {
                var dir = Dir(a, cx);
                var name = a.String(1024);
                if (MustExist(dir) is not DirectoryInfo) throw new NfsError(St.NotDir);
                string child;
                if (name == ".") child = dir;
                else if (name == "..") child = Export.Parent(dir);
                else
                {
                    try { Export.ValidateName(name); } catch (NfsError) { throw new NfsError(St.NoEnt); }
                    child = Export.Child(dir, name);
                }
                var info = _fs.Info(child) ?? throw new NfsError(St.NoEnt);
                w.Opaque(_fs.Handle(child));
                w.U32(1); _fs.WriteAttr(w, c, child, info);
                PostOp(w, c, dir);
                break;
            }

            case 4: // ACCESS
            {
                var rel = _fs.RelFor(a.Opaque(64));
                cx.Rel1 = rel;
                var mask = a.U32();
                MustExist(rel);
                PostOp(w, c, rel);
                w.U32(mask & (_fs.ReadOnly ? 0x23u : 0x3Fu));
                break;
            }

            case 6: // READ
            {
                var rel = Dir(a, cx);
                var offset = a.U64();
                var count = (int)Math.Min(a.U32(), (uint)MaxIo);
                if (MustExist(rel) is not FileInfo fi) throw new NfsError(St.IsDir);
                var buf = new byte[count];
                int n;
                using (var fs = new FileStream(fi.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    fs.Seek((long)offset, SeekOrigin.Begin);
                    n = 0;
                    while (n < count) { var r = fs.Read(buf, n, count - n); if (r == 0) break; n += r; }
                    var eof = offset + (ulong)n >= (ulong)fs.Length;
                    PostOp(w, c, rel);
                    w.U32((uint)n);
                    w.Bool(eof);
                    w.Opaque(buf.AsSpan(0, n));
                }
                break;
            }

            case 7: // WRITE
            {
                var rel = Dir(a, cx, write: true);
                var offset = a.U64();
                a.U32(); // count (データ長を信じる)
                var stable = a.U32();
                var data = a.Opaque(MaxIo + 4096);
                if (MustExist(rel) is not FileInfo fi) throw new NfsError(St.IsDir);
                using (var fs = new FileStream(fi.FullName, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
                {
                    fs.Seek((long)offset, SeekOrigin.Begin);
                    fs.Write(data, 0, data.Length);
                    if (stable != 0) fs.Flush(true);
                }
                Wcc(w, c, rel);
                w.U32((uint)data.Length);
                w.U32(stable); // 要求どおりの安定度で書けた
                w.U64(_fs.Gen);
                break;
            }

            case 8: // CREATE
            {
                var dir = Dir(a, cx, write: true);
                var name = Export.ValidateName(a.String(1024));
                var mode = a.U32();
                Sattr? s = null;
                if (mode == 2) a.Fixed(8); else s = Sattr.Read(a);
                if (MustExist(dir) is not DirectoryInfo) throw new NfsError(St.NotDir);
                var child = Export.Child(dir, name);
                var path = _fs.Full(child);
                if (Directory.Exists(path)) throw new NfsError(St.IsDir);
                if (File.Exists(path) && mode != 0) throw new NfsError(St.Exist);
                if (!File.Exists(path)) File.Open(path, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite).Dispose();
                var info = MustExist(child);
                s?.Apply(path, info);
                w.Bool(true); w.Opaque(_fs.Handle(child));
                PostOp(w, c, child);
                Wcc(w, c, dir);
                break;
            }

            case 9: // MKDIR
            {
                var dir = Dir(a, cx, write: true);
                var name = Export.ValidateName(a.String(1024));
                Sattr.Read(a);
                if (MustExist(dir) is not DirectoryInfo) throw new NfsError(St.NotDir);
                var child = Export.Child(dir, name);
                var path = _fs.Full(child);
                if (Directory.Exists(path) || File.Exists(path)) throw new NfsError(St.Exist);
                Directory.CreateDirectory(path);
                w.Bool(true); w.Opaque(_fs.Handle(child));
                PostOp(w, c, child);
                Wcc(w, c, dir);
                break;
            }

            case 12: // REMOVE
            {
                var dir = Dir(a, cx, write: true);
                var name = Export.ValidateName(a.String(1024));
                var child = Export.Child(dir, name);
                var info = _fs.Info(child) ?? throw new NfsError(St.NoEnt);
                if (info is DirectoryInfo) throw new NfsError(St.IsDir);
                if ((info.Attributes & FileAttributes.ReadOnly) != 0) info.Attributes &= ~FileAttributes.ReadOnly;
                info.Delete();
                _fs.Removed(child);
                Wcc(w, c, dir);
                break;
            }

            case 13: // RMDIR
            {
                var dir = Dir(a, cx, write: true);
                var name = Export.ValidateName(a.String(1024));
                var child = Export.Child(dir, name);
                var info = _fs.Info(child) ?? throw new NfsError(St.NoEnt);
                if (info is not DirectoryInfo di) throw new NfsError(St.NotDir);
                if (di.EnumerateFileSystemInfos().Any()) throw new NfsError(St.NotEmpty);
                di.Delete();
                _fs.Removed(child);
                Wcc(w, c, dir);
                break;
            }

            case 14: // RENAME
            {
                var fromDir = Dir(a, cx, write: true);
                var fromName = Export.ValidateName(a.String(1024));
                var toDir = Dir(a, cx, write: true, slot: 2);
                var toName = Export.ValidateName(a.String(1024));
                var from = Export.Child(fromDir, fromName);
                var to = Export.Child(toDir, toName);
                var src = _fs.Info(from) ?? throw new NfsError(St.NoEnt);
                if (MustExist(toDir) is not DirectoryInfo) throw new NfsError(St.NotDir);

                if (!from.Equals(to, StringComparison.Ordinal))
                {
                    if (src is DirectoryInfo && (to + "\\").StartsWith(from + "\\", StringComparison.OrdinalIgnoreCase))
                        throw new NfsError(St.Inval);

                    var dst = _fs.Info(to);
                    var sameFile = from.Equals(to, StringComparison.OrdinalIgnoreCase); // 大文字小文字だけの変更
                    if (dst is not null && !sameFile)
                    {
                        if (dst is DirectoryInfo dd)
                        {
                            if (src is not DirectoryInfo) throw new NfsError(St.IsDir);
                            if (dd.EnumerateFileSystemInfos().Any()) throw new NfsError(St.NotEmpty);
                            dd.Delete();
                        }
                        else
                        {
                            if (src is DirectoryInfo) throw new NfsError(St.NotDir);
                            dst.Delete();
                        }
                    }

                    var sp = _fs.Full(from); var dp = _fs.Full(to);
                    if (src is DirectoryInfo) Directory.Move(sp, dp); else File.Move(sp, dp);
                    _fs.Renamed(from, to);
                }
                Wcc(w, c, fromDir);
                Wcc(w, c, toDir);
                break;
            }

            case 16 or 17: // READDIR / READDIRPLUS
                ReadDir(c, a, w, cx, plus: c.Proc == 17);
                break;

            case 18: // FSSTAT
            {
                var rel = _fs.RelFor(a.Opaque(64));
                cx.Rel1 = rel;
                ulong total = 1UL << 40, free = 1UL << 39;
                try
                {
                    var di = new DriveInfo(Path.GetPathRoot(_fs.Root)!);
                    total = (ulong)di.TotalSize; free = (ulong)di.AvailableFreeSpace;
                }
                catch { }
                PostOp(w, c, rel);
                w.U64(total); w.U64(free); w.U64(free);
                w.U64(1UL << 32); w.U64(1UL << 31); w.U64(1UL << 31);
                w.U32(0);
                break;
            }

            case 19: // FSINFO
            {
                var rel = _fs.RelFor(a.Opaque(64));
                cx.Rel1 = rel;
                PostOp(w, c, rel);
                w.U32(MaxIo); w.U32(MaxIo); w.U32(4096);
                w.U32(MaxIo); w.U32(MaxIo); w.U32(4096);
                w.U32(32768);
                w.U64(long.MaxValue);
                w.U32(0); w.U32(1_000_000);
                w.U32(0x08 | 0x10); // HOMOGENEOUS | CANSETTIME (リンクなし)
                break;
            }

            case 20: // PATHCONF
            {
                var rel = _fs.RelFor(a.Opaque(64));
                cx.Rel1 = rel;
                PostOp(w, c, rel);
                w.U32(1); w.U32(255);
                w.Bool(true); w.Bool(true);
                w.Bool(true); w.Bool(true); // 大文字小文字は区別しないが保持する (Windows)
                break;
            }

            case 21: // COMMIT
            {
                var rel = Dir(a, cx, write: true);
                a.U64(); a.U32();
                Wcc(w, c, rel);
                w.U64(_fs.Gen);
                break;
            }

            case 5 or 10 or 11 or 15: // READLINK / SYMLINK / MKNOD / LINK: 非対応
                throw new NfsError(St.NotSupp);

            default: return false;
        }
        return true;
    }

    void ReadDir(CallInfo c, XdrReader a, XdrWriter w, Ctx cx, bool plus)
    {
        var rel = Dir(a, cx);
        var cookie = a.U64();
        a.Fixed(8); // cookieverf
        uint maxBytes;
        if (plus) { a.U32(); maxBytes = a.U32(); } else maxBytes = a.U32();
        if (MustExist(rel) is not DirectoryInfo di) throw new NfsError(St.NotDir);

        // 並び順を固定して、cookie=一覧中の位置(1始まり)として使う。
        var entries = new List<(string name, string rel)> { (".", rel), ("..", Export.Parent(rel)) };
        entries.AddRange(di.EnumerateFileSystemInfos()
            .Where(e => (e.Attributes & FileAttributes.ReparsePoint) == 0)
            .Select(e => e.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .Select(n => (n, Export.Child(rel, n))));

        PostOp(w, c, rel);
        w.U64(0); // cookieverf

        var budget = (int)Math.Min(maxBytes, 1u << 20) - 256;
        var used = 0;
        var start = (int)Math.Min(cookie, (ulong)entries.Count);
        var i = start;
        var eof = true;
        for (; i < entries.Count; i++)
        {
            var (name, erel) = entries[i];
            var e = new XdrWriter();
            e.Bool(true);
            e.U64(_fs.IdFor(erel));
            e.String(name);
            e.U64((ulong)i + 1);
            if (plus)
            {
                var info = _fs.Info(erel);
                if (info is null) { e.U32(0); e.Bool(false); }
                else
                {
                    e.U32(1); _fs.WriteAttr(e, c, erel, info);
                    e.Bool(true); e.Opaque(_fs.Handle(erel));
                }
            }
            if (used + e.Length > budget && i > start) { eof = false; break; }
            w.Append(e);
            used += e.Length;
        }
        w.Bool(false); // 一覧の終わり
        w.Bool(eof);
    }
}

/// <summary>MOUNT プロトコル v3。どのパスを要求されても公開フォルダのルートを返す。</summary>
public sealed class MountProgram : RpcProgram
{
    readonly Export _fs;
    readonly Action<string> _log;

    public MountProgram(Export fs, Action<string> log) { _fs = fs; _log = log; }

    public override uint Program => 100005;
    public override uint MinVersion => 3;
    public override uint MaxVersion => 3;

    public override Accept Call(CallInfo c, XdrReader a, XdrWriter w)
    {
        switch (c.Proc)
        {
            case 0: return Accept.Success;
            case 1: // MNT
            {
                var path = a.String(1024);
                _log($"マウント: {c.Remote} が {path} を要求");
                w.U32(0);
                w.Opaque(_fs.Handle(""));
                w.U32(1); w.U32(1); // 認証方式: AUTH_SYS のみ
                return Accept.Success;
            }
            case 2: w.Bool(false); return Accept.Success; // DUMP
            case 3: _log($"アンマウント: {c.Remote}"); return Accept.Success; // UMNT
            case 4: return Accept.Success; // UMNTALL
            case 5: // EXPORT
                w.Bool(true); w.String("/"); w.Bool(false); w.Bool(false);
                return Accept.Success;
            default: return Accept.ProcUnavail;
        }
    }
}

/// <summary>portmap v2: クライアントに NFS と MOUNT のポートを教える。</summary>
public sealed class PortmapProgram : RpcProgram
{
    readonly int _nfsPort;
    public PortmapProgram(int nfsPort) { _nfsPort = nfsPort; }

    public override uint Program => 100000;
    public override uint MinVersion => 2;
    public override uint MaxVersion => 2;

    public override Accept Call(CallInfo c, XdrReader a, XdrWriter w)
    {
        switch (c.Proc)
        {
            case 0: return Accept.Success;
            case 3: // GETPORT
            {
                var prog = a.U32(); var vers = a.U32(); var prot = a.U32(); a.U32();
                w.U32(prot == 6 && vers == 3 && prog is 100003 or 100005 ? (uint)_nfsPort : 0);
                return Accept.Success;
            }
            case 4: // DUMP
                foreach (var (p, v, port) in new[] { (100000u, 2u, 111u), (100003u, 3u, (uint)_nfsPort), (100005u, 3u, (uint)_nfsPort) })
                { w.Bool(true); w.U32(p); w.U32(v); w.U32(6); w.U32(port); }
                w.Bool(false);
                return Accept.Success;
            default: return Accept.ProcUnavail;
        }
    }
}
