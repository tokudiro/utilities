#!/bin/bash
# Linux カーネルの NFS クライアントで、マウント済みのディレクトリに対して操作を試す。
# 使い方: sudo bash mount_test.sh /mnt/nfst
set -u
D=${1:?mount point}
fail=0
ok() { echo "PASS $1"; }
ng() { echo "FAIL $1"; fail=1; }
t() { local n=$1; shift; if "$@" >/dev/null 2>&1; then ok "$n"; else ng "$n"; fi; }

T=$D/t_$$
t "mkdir"            mkdir "$T"
t "write file"       bash -c "echo hello > $T/a.txt"
t "read back"        bash -c "[ \"\$(cat $T/a.txt)\" = hello ]"
t "append"           bash -c "echo more >> $T/a.txt; [ \$(wc -l < $T/a.txt) -eq 2 ]"
t "truncate"         truncate -s 3 "$T/a.txt"
t "size is 3"        bash -c "[ \$(stat -c %s $T/a.txt) -eq 3 ]"
t "mv"               mv "$T/a.txt" "$T/b.txt"
t "ls shows b.txt"   bash -c "ls $T | grep -qx b.txt"
t "touch mtime"      touch -d '2020-01-02 03:04:05' "$T/b.txt"
t "mtime preserved"  bash -c "[ \"\$(stat -c %Y $T/b.txt)\" = \"\$(date -d '2020-01-02 03:04:05' +%s)\" ]"
t "mkdir nested"     mkdir -p "$T/x/y/z"
t "mv dir"           mv "$T/x" "$T/x2"
t "file in moved dir" bash -c "echo q > $T/x2/y/z/f && [ -f $T/x2/y/z/f ]"

# 大きなファイル (複数のREAD/WRITEに分割される) の往復
head -c 20000000 /dev/urandom > /tmp/big.bin
t "copy 20MB in"     cp /tmp/big.bin "$T/big.bin"
t "20MB checksum"    bash -c "[ \"\$(md5sum < /tmp/big.bin)\" = \"\$(md5sum < $T/big.bin)\" ]"

# 多数のファイルを含むディレクトリ (READDIRPLUS の分割)
mkdir "$T/many"
for i in $(seq 1 600); do : > "$T/many/file_with_a_fairly_long_name_$i.txt"; done
t "600 entries listed" bash -c "[ \$(ls $T/many | wc -l) -eq 600 ]"

t "rm file"          rm "$T/b.txt"
t "rmdir non-empty fails" bash -c "! rmdir $T/x2 2>/dev/null"
t "rm -r tree"       rm -r "$T"
t "tree gone"        bash -c "[ ! -e $T ]"
t "df works"         df "$D"
rm -f /tmp/big.bin
[ $fail -eq 0 ] && echo "ALL PASSED" || echo "SOME FAILED"
exit $fail
