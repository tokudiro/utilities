"""nfsserve のプロトコル検証用の最小NFSv3クライアント (標準ライブラリのみ)。

使い方: python nfs_client_test.py <host> [port]
実際のLinuxカーネルのクライアントを使わずに、RPC/XDRの応答の形式と各操作の結果を確認する。
"""
import socket
import struct
import sys

HOST = sys.argv[1] if len(sys.argv) > 1 else "127.0.0.1"
PORT = int(sys.argv[2]) if len(sys.argv) > 2 else 2049

xid = 0
sock = socket.create_connection((HOST, PORT), timeout=10)


def u32(v):
    return struct.pack(">I", v)


def u64(v):
    return struct.pack(">Q", v)


def opaque(b):
    return u32(len(b)) + b + b"\0" * ((4 - len(b) % 4) % 4)


def string(s):
    return opaque(s.encode())


class R:
    def __init__(self, b):
        self.b, self.p = b, 0

    def u32(self):
        v = struct.unpack_from(">I", self.b, self.p)[0]
        self.p += 4
        return v

    def u64(self):
        v = struct.unpack_from(">Q", self.b, self.p)[0]
        self.p += 8
        return v

    def opaque(self):
        n = self.u32()
        v = self.b[self.p:self.p + n]
        self.p += (n + 3) & ~3
        return v

    def fixed(self, n):
        v = self.b[self.p:self.p + n]
        self.p += (n + 3) & ~3
        return v


def call(prog, vers, proc, args=b""):
    global xid
    xid += 1
    cred = u32(1) + opaque(u32(0) + string("test") + u32(1000) + u32(1000) + u32(0))
    msg = u32(xid) + u32(0) + u32(2) + u32(prog) + u32(vers) + u32(proc) + cred + u32(0) + opaque(b"") + args
    sock.sendall(u32(0x80000000 | len(msg)) + msg)
    buf = b""
    while True:
        h = b""
        while len(h) < 4:
            h += sock.recv(4 - len(h))
        n = struct.unpack(">I", h)[0]
        last = n & 0x80000000
        n &= 0x7FFFFFFF
        d = b""
        while len(d) < n:
            d += sock.recv(n - len(d))
        buf += d
        if last:
            break
    r = R(buf)
    assert r.u32() == xid, "xid mismatch"
    assert r.u32() == 1, "not a reply"
    assert r.u32() == 0, "denied"
    r.u32(); r.opaque()
    stat = r.u32()
    assert stat == 0, f"accept_stat={stat}"
    return r


def attr(r):
    t, mode, nlink, uid, gid = r.u32(), r.u32(), r.u32(), r.u32(), r.u32()
    size, used = r.u64(), r.u64()
    r.u32(); r.u32(); r.u64()
    fileid = r.u64()
    r.u32(); r.u32(); r.u32(); r.u32(); r.u32(); r.u32()
    return dict(type=t, mode=oct(mode), nlink=nlink, uid=uid, gid=gid, size=size, fileid=fileid)


def post_op(r):
    return attr(r) if r.u32() else None


def wcc(r):
    if r.u32():
        r.u64(); r.u32(); r.u32(); r.u32(); r.u32()
    return post_op(r)


NFS, MNT = 100003, 100005
fails = 0


def check(name, cond, extra=""):
    global fails
    print(("PASS " if cond else "FAIL ") + name + (f"  {extra}" if extra else ""))
    if not cond:
        fails += 1


def status(r):
    return r.u32()


# portmap (111) は別ポートなので、ここではNFSポートへ直接つなぐ
call(NFS, 3, 0)
check("NFS NULL", True)

r = call(MNT, 3, 1, string("/"))
check("MNT status", r.u32() == 0)
root = r.opaque()
check("MNT handle length", len(root) == 16)

r = call(NFS, 3, 1, opaque(root))
check("GETATTR root", status(r) == 0)
a = attr(r)
check("root is dir", a["type"] == 2, a)

r = call(NFS, 3, 19, opaque(root))
check("FSINFO", status(r) == 0)

r = call(NFS, 3, 20, opaque(root))
check("PATHCONF", status(r) == 0)

r = call(NFS, 3, 18, opaque(root))
check("FSSTAT", status(r) == 0)

# READDIRPLUS
r = call(NFS, 3, 17, opaque(root) + u64(0) + u64(0) + u32(8192) + u32(32768))
check("READDIRPLUS", status(r) == 0)
post_op(r)
r.u64()
names = []
while r.u32():
    r.u64()
    names.append(r.opaque().decode())
    r.u64()
    post_op(r)
    if r.u32():
        r.opaque()
eof = r.u32()
check("READDIRPLUS lists hello.txt and sub", "hello.txt" in names and "sub" in names, names)
check("READDIRPLUS eof", eof == 1)

# LOOKUP + READ
r = call(NFS, 3, 3, opaque(root) + string("hello.txt"))
check("LOOKUP hello.txt", status(r) == 0)
fh = r.opaque()
r = call(NFS, 3, 6, opaque(fh) + u64(0) + u32(4096))
check("READ status", status(r) == 0)
post_op(r)
n = r.u32()
eof = r.u32()
data = r.opaque()
check("READ content", data.startswith(b"hello from windows"), data[:30])
check("READ eof", eof == 1)

r = call(NFS, 3, 3, opaque(root) + string("nonexistent"))
check("LOOKUP missing -> NOENT", status(r) == 2)

r = call(NFS, 3, 3, opaque(root) + string(".."))
check("LOOKUP .. at root stays at root", status(r) == 0)

for bad in ("a:b", "con", "x/y", "a\\b", "trail."):
    r = call(NFS, 3, 8, opaque(root) + string(bad) + u32(1) + u32(0) * 6)
    st = status(r)
    check(f"CREATE rejects unsafe name {bad!r}", st in (22, 2), st)

# CREATE + WRITE + READ back
sattr = u32(0) * 3 + u32(0) + u32(0) + u32(0)
r = call(NFS, 3, 8, opaque(root) + string("new.txt") + u32(1) + sattr)
check("CREATE new.txt", status(r) == 0)
r.u32()
newfh = r.opaque()
r = call(NFS, 3, 8, opaque(root) + string("new.txt") + u32(1) + sattr)
check("CREATE guarded on existing -> EXIST", status(r) == 17)

payload = b"written over nfs\n" * 100
r = call(NFS, 3, 7, opaque(newfh) + u64(0) + u32(len(payload)) + u32(2) + opaque(payload))
check("WRITE", status(r) == 0)
wcc(r)
check("WRITE count", r.u32() == len(payload))
r = call(NFS, 3, 6, opaque(newfh) + u64(0) + u32(len(payload) + 10))
check("READ back status", status(r) == 0)
post_op(r)
r.u32(); r.u32()
check("READ back equals", r.opaque() == payload)

# SETATTR truncate
r = call(NFS, 3, 2, opaque(newfh) + u32(0) * 3 + u32(1) + u64(5) + u32(0) + u32(0) + u32(0))
check("SETATTR truncate", status(r) == 0)
r = call(NFS, 3, 1, opaque(newfh))
status(r)
check("size after truncate == 5", attr(r)["size"] == 5)

# MKDIR, RENAME, RMDIR, REMOVE
r = call(NFS, 3, 9, opaque(root) + string("d1") + sattr)
check("MKDIR", status(r) == 0)
r.u32()
d1 = r.opaque()
r = call(NFS, 3, 14, opaque(root) + string("new.txt") + opaque(d1) + string("moved.txt"))
check("RENAME into d1", status(r) == 0)
r = call(NFS, 3, 3, opaque(d1) + string("moved.txt"))
check("LOOKUP moved.txt", status(r) == 0)
r = call(NFS, 3, 13, opaque(root) + string("d1"))
check("RMDIR non-empty -> NOTEMPTY", status(r) == 66)
r = call(NFS, 3, 12, opaque(d1) + string("moved.txt"))
check("REMOVE moved.txt", status(r) == 0)
r = call(NFS, 3, 13, opaque(root) + string("d1"))
check("RMDIR d1", status(r) == 0)
r = call(NFS, 3, 1, opaque(newfh))
check("GETATTR on removed handle -> STALE", status(r) == 70)

r = call(NFS, 3, 1, opaque(b"\x01" * 16))
check("GETATTR bogus handle -> STALE", status(r) == 70)

print("FAILED" if fails else "ALL PASSED", fails)
sys.exit(1 if fails else 0)
