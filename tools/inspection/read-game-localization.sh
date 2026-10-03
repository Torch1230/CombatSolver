#!/usr/bin/env bash
set -Eeuo pipefail

user_home="${HOME:?HOME is not set}"
pck_path="${STS2_PCK_PATH:-$user_home/.local/share/Steam/steamapps/common/Slay the Spire 2/SlayTheSpire2.pck}"
keys=()

usage() {
    cat <<'EOF'
Usage: read-game-localization.sh --key KEY [--key KEY ...] [options]

Options:
  -k, --key KEY       Exact localization key; repeat for multiple keys
      --pck-path FILE Godot PCK to read (default: $STS2_PCK_PATH or Steam library)
  -h, --help          Show this help
EOF
}

die() {
    echo "read-game-localization.sh: $*" >&2
    exit 2
}

while (($# > 0)); do
    case "$1" in
        -k|--key)
            (($# >= 2)) || die "missing value for $1"
            keys+=("$2")
            shift 2
            ;;
        --pck-path)
            (($# >= 2)) || die "missing value for $1"
            pck_path="$2"
            shift 2
            ;;
        -h|--help)
            usage
            exit 0
            ;;
        *)
            die "unknown argument: $1"
            ;;
    esac
done

((${#keys[@]} > 0)) || die "at least one --key is required"
[[ -f "$pck_path" ]] || {
    echo "read-game-localization.sh: game PCK not found: $pck_path" >&2
    exit 1
}
command -v python3 >/dev/null 2>&1 || {
    echo "read-game-localization.sh: python3 is required" >&2
    exit 1
}

# Bash handles the command-line contract while Python performs bounded binary
# reads. The PCK is nearly 2 GiB, so loading the whole archive is intentionally
# avoided.
python3 - "$pck_path" "${keys[@]}" <<'PY'
import json
import re
import struct
import sys


def read_exact(stream, size, description):
    data = stream.read(size)
    if len(data) != size:
        raise SystemExit(f"Godot PCK {description} is truncated")
    return data


pck_path = sys.argv[1]
requested = sys.argv[2:]
path_pattern = re.compile(r"^localization/zhs/[^/]+\.json$")

with open(pck_path, "rb") as stream:
    if read_exact(stream, 4, "header") != b"GDPC":
        raise SystemExit(f"不是有效的 Godot PCK：{pck_path}")

    stream.seek(24)
    file_base, directory_offset = struct.unpack(
        "<qq", read_exact(stream, 16, "header")
    )
    if file_base < 0 or directory_offset < 0:
        raise SystemExit(f"Godot PCK header contains a negative offset: {pck_path}")

    stream.seek(directory_offset)
    file_count = struct.unpack("<I", read_exact(stream, 4, "directory"))[0]
    entries = []
    for _ in range(file_count):
        path_length = struct.unpack(
            "<I", read_exact(stream, 4, "directory entry")
        )[0]
        entry_path = (
            read_exact(stream, path_length, "directory entry path")
            .rstrip(b"\0")
            .decode("utf-8")
        )
        offset, size = struct.unpack(
            "<QQ", read_exact(stream, 16, "directory entry offsets")
        )
        read_exact(stream, 16, "directory entry checksum")
        flags = struct.unpack(
            "<I", read_exact(stream, 4, "directory entry flags")
        )[0]
        if path_pattern.fullmatch(entry_path):
            if flags != 0:
                raise SystemExit(
                    "简中本地化资源使用了不支持的 PCK 标志："
                    f"{entry_path} flags={flags}"
                )
            entries.append((entry_path, offset, size))

    if not entries:
        raise SystemExit("PCK 中没有找到 localization/zhs/*.json。")

    localized = {}
    requested_set = set(requested)
    for entry_path, offset, size in entries:
        stream.seek(file_base + offset)
        payload = read_exact(stream, size, f"entry {entry_path}")
        table = json.loads(payload.decode("utf-8"))
        for key in requested_set.intersection(table):
            if key in localized:
                raise SystemExit(f"简中本地化键重复：{key}")
            localized[key] = table[key]

for key in requested:
    print(
        json.dumps(
            {"Key": key, "Text": localized.get(key)},
            ensure_ascii=False,
            separators=(",", ":"),
        )
    )
PY
