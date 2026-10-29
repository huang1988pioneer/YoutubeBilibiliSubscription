#!/usr/bin/env python3
"""把 .app bundle 壓成 zip，並正確設定 UTF-8 檔名旗標與 Unix 權限。

macOS 的 ditto / Info-ZIP 在檔名含中文時不會設定 zip 的 UTF-8 旗標（general
purpose bit 11），導致 Windows / Linux 的解壓工具把「訂閱管理.app」顯示成亂碼。
Python 的 zipfile 會自動設定該旗標，這裡再補上 external_attr 以保留執行權限，
避免解壓後 .app 無法啟動。

用法：python3 scripts/pack-app.py "dist/訂閱管理.app" out.zip
"""
import os
import shutil
import stat
import sys
import zipfile


def add_entry(zf, full_path, arcname, st):
    info = zipfile.ZipInfo(arcname)
    info.create_system = 3  # Unix，讓解壓端看得懂 external_attr
    info.date_time = __import__("time").localtime(st.st_mtime)[:6]
    mode = stat.S_IMODE(st.st_mode)
    if stat.S_ISDIR(st.st_mode):
        mode |= 0x10  # MS-DOS directory 位元
    info.external_attr = mode << 16
    info.compress_type = zipfile.ZIP_DEFLATED

    if stat.S_ISDIR(st.st_mode):
        zf.writestr(info, b"")
    elif stat.S_ISLNK(st.st_mode):
        zf.writestr(info, os.readlink(full_path).encode("utf-8"))
    else:
        with open(full_path, "rb") as src, zf.open(info, "w") as dst:
            shutil.copyfileobj(src, dst)


def main():
    if len(sys.argv) != 3:
        sys.exit(__doc__)

    app, out = sys.argv[1], sys.argv[2]
    if not os.path.isdir(app):
        sys.exit(f"not a directory: {app}")

    parent = os.path.dirname(os.path.abspath(app))
    with zipfile.ZipFile(out, "w", zipfile.ZIP_DEFLATED) as zf:
        for dirpath, dirnames, filenames in os.walk(app):
            dirnames[:] = sorted(d for d in dirnames if d != ".DS_Store")
            entries = [""] + sorted(filenames)
            for name in entries:
                if name == ".DS_Store":
                    continue
                full = os.path.join(dirpath, name) if name else dirpath
                arc = os.path.relpath(full, parent).replace(os.sep, "/")
                if name == "":
                    arc += "/"
                add_entry(zf, full, arc, os.lstat(full))

    print(f"{out}  {os.path.getsize(out):,} bytes")


if __name__ == "__main__":
    main()
