import argparse
import concurrent.futures
import ctypes
import ctypes.wintypes
import hashlib
import hmac
import json
import math
import os
import re
import sqlite3
import struct
import subprocess
import sys
import time

try:
    from wechat_cli.core.contacts import get_contact_names
    from wechat_cli.core.context import AppContext
    from wechat_cli.core.messages import (
        _find_msg_tables_for_user,
        _format_message_text,
        _iter_table_contexts,
        _load_name2id_maps,
        _query_messages,
        _resolve_sender_label,
        _split_msg_type,
        decompress_content,
    )
except ImportError:
    pass

PAGE_SIZE = 4096
KEY_SIZE = 32
SALT_SIZE = 16
RESERVE_SIZE = 80
WECHAT_V4_ROUND_COUNT = 256000
PROCESS_VM_READ = 0x0010
PROCESS_QUERY_INFORMATION = 0x0400
MEM_COMMIT = 0x1000
MEM_PRIVATE = 0x20000
READABLE_PAGE_PROTECTIONS = {0x02, 0x04, 0x08, 0x20, 0x40, 0x80}
MEMORY_READ_CHUNK_SIZE = 1 * 1024 * 1024
MAX_REGION_SIZE = 200 * 1024 * 1024
SCAN_TIMEOUT_SECONDS = 120
KEY_POINTER_STRUCTURE = re.compile(
    b"(.{6}\\x00\\x00)"
    b"\\x00{8}\\x20\\x00{7}(.{8})",
    re.DOTALL,
)
HEX_KEY_ASCII = re.compile(
    rb"(?<![0-9a-fA-F])([0-9a-fA-F]{64})(?![0-9a-fA-F])"
)
HEX_KEY_UTF16 = re.compile(
    rb"(?<![0-9a-fA-F]\x00)((?:[0-9a-fA-F]\x00){64})"
    rb"(?![0-9a-fA-F]\x00)"
)
HEX_KEY_SALT_ASCII = re.compile(
    rb"(?<![0-9a-fA-F])([0-9a-fA-F]{96})(?![0-9a-fA-F])"
)
HEX_KEY_SALT_UTF16 = re.compile(
    rb"(?<![0-9a-fA-F]\x00)((?:[0-9a-fA-F]\x00){96})"
    rb"(?![0-9a-fA-F]\x00)"
)
DB_PATH_KEYWORD = re.compile(
    rb"(message_\d+\.db|session\.db|contact\.db|favorite\.db|head_image\.db|"
    rb"MicroMsg|db_storage|MsgDB|HardLink)",
    re.IGNORECASE,
)


class MemoryBasicInformation(ctypes.Structure):
    _fields_ = [
        ("BaseAddress", ctypes.c_void_p),
        ("AllocationBase", ctypes.c_void_p),
        ("AllocationProtect", ctypes.wintypes.DWORD),
        ("PartitionId", ctypes.wintypes.WORD),
        ("RegionSize", ctypes.c_size_t),
        ("State", ctypes.wintypes.DWORD),
        ("Protect", ctypes.wintypes.DWORD),
        ("Type", ctypes.wintypes.DWORD),
    ]


def windows_kernel32():
    kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
    kernel32.OpenProcess.argtypes = [
        ctypes.wintypes.DWORD,
        ctypes.wintypes.BOOL,
        ctypes.wintypes.DWORD,
    ]
    kernel32.OpenProcess.restype = ctypes.wintypes.HANDLE
    kernel32.ReadProcessMemory.argtypes = [
        ctypes.wintypes.HANDLE,
        ctypes.wintypes.LPCVOID,
        ctypes.wintypes.LPVOID,
        ctypes.c_size_t,
        ctypes.POINTER(ctypes.c_size_t),
    ]
    kernel32.ReadProcessMemory.restype = ctypes.wintypes.BOOL
    kernel32.VirtualQueryEx.argtypes = [
        ctypes.wintypes.HANDLE,
        ctypes.wintypes.LPCVOID,
        ctypes.POINTER(MemoryBasicInformation),
        ctypes.c_size_t,
    ]
    kernel32.VirtualQueryEx.restype = ctypes.c_size_t
    kernel32.CloseHandle.argtypes = [ctypes.wintypes.HANDLE]
    kernel32.CloseHandle.restype = ctypes.wintypes.BOOL
    return kernel32


def find_key_pointer_candidates(memory):
    pointers = []
    for match in KEY_POINTER_STRUCTURE.finditer(memory):
        capacity = int.from_bytes(match.group(2), "little")
        if 31 <= capacity <= 4096:
            pointers.append(int.from_bytes(match.group(1), "little"))
    return list(dict.fromkeys(pointers))


def find_hex_key_candidates(memory):
    values = []
    for match in HEX_KEY_ASCII.finditer(memory):
        values.append(bytes.fromhex(match.group(1).decode("ascii")))
    for match in HEX_KEY_UTF16.finditer(memory):
        values.append(bytes.fromhex(match.group(1)[::2].decode("ascii")))
    return list(dict.fromkeys(values))


def is_plausible_passphrase(candidate):
    if len(candidate) != KEY_SIZE:
        return False
    byte_counts = {
        value: candidate.count(value)
        for value in set(candidate)
    }
    entropy = -sum(
        (count / KEY_SIZE) * math.log2(count / KEY_SIZE)
        for count in byte_counts.values()
    )
    printable_count = sum(32 <= value <= 126 for value in candidate)
    return (
        len(byte_counts) >= 20
        and max(byte_counts.values()) <= 4
        and entropy >= 4.5
        and printable_count < 12
    )


def find_key_salt_candidates(memory):
    values = []
    for match in HEX_KEY_SALT_ASCII.finditer(memory):
        combined = match.group(1).decode("ascii")
        values.append(bytes.fromhex(combined[:64]))
    for match in HEX_KEY_SALT_UTF16.finditer(memory):
        combined = match.group(1)[::2].decode("ascii")
        values.append(bytes.fromhex(combined[:64]))
    return list(dict.fromkeys(values))


def find_db_path_offsets(memory):
    offsets = []
    for match in DB_PATH_KEYWORD.finditer(memory):
        offsets.append(match.start())
    return offsets


def find_raw_key_candidates(memory, stride=8):
    if len(memory) < KEY_SIZE:
        return []
    candidates = []
    for offset in range(0, len(memory) - KEY_SIZE + 1, stride):
        candidate = memory[offset:offset + KEY_SIZE]
        if candidate[0] == 0:
            continue
        if is_plausible_passphrase(candidate):
            candidates.append(candidate)
    return list(dict.fromkeys(candidates))


def verify_database_key(database_key, page):
    if len(database_key) != KEY_SIZE or len(page) < PAGE_SIZE:
        return False

    salt = page[:SALT_SIZE]
    mac_salt = bytes(value ^ 0x3A for value in salt)
    mac_key = hashlib.pbkdf2_hmac(
        "sha512",
        database_key,
        mac_salt,
        2,
        dklen=KEY_SIZE,
    )
    mac = hmac.new(
        mac_key,
        page[SALT_SIZE:PAGE_SIZE - RESERVE_SIZE + 16],
        hashlib.sha512,
    )
    mac.update(struct.pack("<I", 1))
    return hmac.compare_digest(
        mac.digest(),
        page[PAGE_SIZE - 64:PAGE_SIZE],
    )


def verify_database_key_v4(database_key, page):
    if len(database_key) != KEY_SIZE or len(page) < PAGE_SIZE:
        return False

    salt = page[:SALT_SIZE]
    mac_salt = bytes(value ^ 0x3A for value in salt)
    mac_key = hashlib.pbkdf2_hmac(
        "sha512",
        database_key,
        mac_salt,
        2,
        dklen=KEY_SIZE,
    )
    for reserve_candidate in (64, 80):
        content_end = PAGE_SIZE - reserve_candidate
        if content_end <= SALT_SIZE:
            continue
        mac = hmac.new(mac_key, digestmod=hashlib.sha512)
        mac.update(salt)
        mac.update(struct.pack("<I", 1))
        mac.update(page[SALT_SIZE:content_end])
        if hmac.compare_digest(mac.digest(), page[PAGE_SIZE - 64:PAGE_SIZE]):
            return True
    return False


def try_verify_key(database_key, page):
    if verify_database_key(database_key, page):
        return True
    if verify_database_key_v4(database_key, page):
        return True
    return False


def derive_database_key(passphrase, page):
    if len(passphrase) != KEY_SIZE or len(page) < PAGE_SIZE:
        return None

    database_key = hashlib.pbkdf2_hmac(
        "sha512",
        passphrase,
        page[:SALT_SIZE],
        WECHAT_V4_ROUND_COUNT,
        dklen=KEY_SIZE,
    )
    return database_key if verify_database_key(database_key, page) else None


def derive_database_key_v4(passphrase, page):
    if len(passphrase) != KEY_SIZE or len(page) < PAGE_SIZE:
        return None

    database_key = hashlib.pbkdf2_hmac(
        "sha512",
        passphrase,
        page[:SALT_SIZE],
        WECHAT_V4_ROUND_COUNT,
        dklen=KEY_SIZE,
    )
    return database_key if verify_database_key_v4(database_key, page) else None


def list_weixin_process_ids():
    result = subprocess.run(
        [
            "tasklist",
            "/FI",
            "IMAGENAME eq Weixin.exe",
            "/FO",
            "CSV",
            "/NH",
        ],
        capture_output=True,
        check=False,
        text=True,
    )
    processes = []
    for line in result.stdout.splitlines():
        columns = line.strip().strip('"').split('","')
        if len(columns) < 5 or columns[0].lower() != "weixin.exe":
            continue
        memory_kb = int(
            columns[4]
            .replace(",", "")
            .replace("K", "")
            .replace("k", "")
            .strip()
            or "0"
        )
        processes.append((int(columns[1]), memory_kb))

    return [pid for pid, _ in sorted(processes, key=lambda item: item[1], reverse=True)]


def iter_readable_private_regions(kernel32, process_handle):
    address = 0
    maximum_address = 0x7FFFFFFFFFFF
    information = MemoryBasicInformation()

    while address < maximum_address:
        queried = kernel32.VirtualQueryEx(
            process_handle,
            ctypes.c_void_p(address),
            ctypes.byref(information),
            ctypes.sizeof(information),
        )
        if not queried:
            break

        base_address = int(information.BaseAddress or 0)
        region_size = int(information.RegionSize or 0)
        if (
            information.State == MEM_COMMIT
            and information.Type == MEM_PRIVATE
            and information.Protect in READABLE_PAGE_PROTECTIONS
            and region_size > 0
        ):
            yield base_address, region_size

        next_address = base_address + region_size
        if next_address <= address:
            break
        address = next_address


def read_process_memory(kernel32, process_handle, address, size):
    buffer = ctypes.create_string_buffer(size)
    bytes_read = ctypes.c_size_t(0)
    succeeded = kernel32.ReadProcessMemory(
        process_handle,
        ctypes.c_void_p(address),
        buffer,
        size,
        ctypes.byref(bytes_read),
    )
    if not succeeded or bytes_read.value == 0:
        return b""
    return buffer.raw[:bytes_read.value]


def collect_wechat_v4_candidates(process_ids):
    kernel32 = windows_kernel32()
    candidate_values = set()
    raw_candidate_values = set()
    hex_count = 0
    pointer_count = 0
    key_salt_count = 0
    process_count = 0
    db_path_ref_count = 0
    scan_deadline = time.monotonic() + SCAN_TIMEOUT_SECONDS

    for process_id in process_ids:
        if time.monotonic() > scan_deadline:
            break

        process_handle = kernel32.OpenProcess(
            PROCESS_VM_READ | PROCESS_QUERY_INFORMATION,
            False,
            process_id,
        )
        if not process_handle:
            continue

        process_count += 1
        db_path_offsets_in_process = []
        region_count = 0
        try:
            for base_address, region_size in iter_readable_private_regions(
                kernel32,
                process_handle,
            ):
                if time.monotonic() > scan_deadline:
                    break

                if region_size > MAX_REGION_SIZE:
                    continue

                region_count += 1
                offset = 0
                while offset < region_size and time.monotonic() < scan_deadline:
                    read_size = min(MEMORY_READ_CHUNK_SIZE, region_size - offset)
                    try:
                        data = read_process_memory(
                            kernel32,
                            process_handle,
                            base_address + offset,
                            read_size,
                        )
                    except Exception:
                        break

                    if not data:
                        offset += read_size
                        continue

                    hex_keys = find_hex_key_candidates(data)
                    candidate_values.update(hex_keys)
                    hex_count += len(hex_keys)

                    key_salt_keys = find_key_salt_candidates(data)
                    candidate_values.update(key_salt_keys)
                    key_salt_count += len(key_salt_keys)

                    if time.monotonic() - scan_deadline < 60:
                        pointer_candidates = find_key_pointer_candidates(data)
                        for pointer in pointer_candidates:
                            try:
                                candidate = read_process_memory(
                                    kernel32,
                                    process_handle,
                                    pointer,
                                    KEY_SIZE,
                                )
                            except Exception:
                                continue
                            if len(candidate) != KEY_SIZE:
                                continue
                            pointer_count += 1
                            if candidate not in candidate_values:
                                candidate_values.add(candidate)

                    db_path_offsets = find_db_path_offsets(data)
                    if db_path_offsets:
                        for local_offset in db_path_offsets:
                            db_path_offsets_in_process.append(
                                (base_address + offset + local_offset, region_size, base_address)
                            )

                    if not hex_keys and not key_salt_keys and not db_path_offsets:
                        raw_keys = find_raw_key_candidates(data, stride=32)
                        for candidate in raw_keys:
                            if candidate not in candidate_values:
                                raw_candidate_values.add(candidate)

                    offset += read_size

            for abs_addr, region_size, region_base in db_path_offsets_in_process:
                if time.monotonic() > scan_deadline:
                    break
                scan_start = max(region_base, abs_addr - 4096)
                scan_end = min(region_base + region_size, abs_addr + 4096 + KEY_SIZE)
                scan_size = scan_end - scan_start
                if scan_size < KEY_SIZE:
                    continue
                try:
                    surrounding = read_process_memory(
                        kernel32, process_handle, scan_start, scan_size
                    )
                except Exception:
                    continue
                if not surrounding:
                    continue
                for candidate in find_raw_key_candidates(surrounding, stride=1):
                    if candidate not in candidate_values:
                        candidate_values.add(candidate)

            db_path_ref_count += len(db_path_offsets_in_process)

        finally:
            kernel32.CloseHandle(process_handle)

    candidate_values.update(raw_candidate_values)
    elapsed = round(time.monotonic() + SCAN_TIMEOUT_SECONDS - scan_deadline, 1)
    return (
        list(candidate_values),
        process_count,
        hex_count,
        pointer_count,
        key_salt_count,
        len(raw_candidate_values),
        db_path_ref_count,
        elapsed,
    )


def find_wechat_v4_passphrase(candidate_values, validation_page):
    plausible_candidates = [
        candidate
        for candidate in candidate_values
        if is_plausible_passphrase(candidate)
    ]
    max_candidates = 10000
    if len(plausible_candidates) > max_candidates:
        plausible_candidates = plausible_candidates[:max_candidates]

    print(f"尝试 {len(plausible_candidates)} 个口令候选 (v3 格式)...", file=sys.stderr)
    worker_count = min(16, max(1, os.cpu_count() or 1))
    batch_size = worker_count * 8
    deadline = time.monotonic() + 180
    with concurrent.futures.ThreadPoolExecutor(
        max_workers=worker_count
    ) as executor:
        for index in range(0, len(plausible_candidates), batch_size):
            if time.monotonic() > deadline:
                break
            batch = plausible_candidates[index:index + batch_size]
            for candidate, derived_key in zip(
                batch,
                executor.map(
                    lambda c: derive_database_key(c, validation_page),
                    batch,
                ),
            ):
                if derived_key is not None:
                    return (
                        candidate,
                        len(candidate_values),
                        len(plausible_candidates),
                    )

        if time.monotonic() < deadline:
            print("尝试 v4 格式...", file=sys.stderr)
            for index in range(0, len(plausible_candidates), batch_size):
                if time.monotonic() > deadline:
                    break
                batch = plausible_candidates[index:index + batch_size]
                for candidate, derived_key in zip(
                    batch,
                    executor.map(
                        lambda c: derive_database_key_v4(c, validation_page),
                        batch,
                    ),
                ):
                    if derived_key is not None:
                        return (
                            candidate,
                            len(candidate_values),
                            len(plausible_candidates),
                        )

    return (
        None,
        len(candidate_values),
        len(plausible_candidates),
    )


def collect_database_pages(database_root):
    pages = []
    for root, _, files in os.walk(database_root):
        for filename in files:
            if not filename.lower().endswith(".db"):
                continue
            path = os.path.join(root, filename)
            if os.path.getsize(path) < PAGE_SIZE:
                continue
            with open(path, "rb") as database_file:
                page = database_file.read(PAGE_SIZE)
            if len(page) != PAGE_SIZE:
                continue
            pages.append(
                (
                    os.path.relpath(path, database_root).replace("\\", "/"),
                    path,
                    page,
                )
            )
    return pages


def choose_validation_page(database_pages):
    preferred_paths = (
        "favorite/favorite_fts.db",
        "head_image/head_image.db",
        "session/session.db",
        "contact/contact.db",
        "message/message_0.db",
    )
    by_relative_path = {
        relative_path.lower(): page
        for relative_path, _, page in database_pages
    }
    for relative_path in preferred_paths:
        page = by_relative_path.get(relative_path)
        if page:
            return page
    return database_pages[0][2] if database_pages else None


def derive_key_entry(passphrase, database_entry):
    relative_path, path, page = database_entry
    database_key = derive_database_key(passphrase, page)
    if database_key is None:
        database_key = derive_database_key_v4(passphrase, page)
    if database_key is None:
        return relative_path, None
    return (
        relative_path,
        {
            "enc_key": database_key.hex(),
            "salt": page[:SALT_SIZE].hex(),
            "size_mb": round(os.path.getsize(path) / 1024 / 1024, 1),
        },
    )


def derive_database_keys(passphrase, database_pages):
    worker_count = min(8, max(1, os.cpu_count() or 1))
    with concurrent.futures.ThreadPoolExecutor(
        max_workers=worker_count
    ) as executor:
        entries = list(
            executor.map(
                lambda entry: derive_key_entry(passphrase, entry),
                database_pages,
            )
        )
    return {
        relative_path: key_info
        for relative_path, key_info in entries
        if key_info is not None
    }


def find_direct_database_keys(candidate_values, database_pages):
    direct_keys = {}
    for relative_path, path, page in database_pages:
        for candidate in candidate_values:
            if try_verify_key(candidate, page):
                direct_keys[relative_path] = {
                    "enc_key": candidate.hex(),
                    "salt": page[:SALT_SIZE].hex(),
                    "size_mb": round(os.path.getsize(path) / 1024 / 1024, 1)
                    if os.path.exists(path)
                    else 0,
                }
                break
    return direct_keys


def find_key_from_candidates(candidate_values, validation_page):
    deadline = time.monotonic() + 30
    for candidate in candidate_values:
        if time.monotonic() > deadline:
            break
        if try_verify_key(candidate, validation_page):
            return candidate
    return None


def has_required_database_keys(database_keys):
    normalized_paths = {path.lower() for path in database_keys}
    return (
        "session/session.db" in normalized_paths
        and "contact/contact.db" in normalized_paths
        and any(
            path.startswith("message/message_") and path.endswith(".db")
            for path in normalized_paths
        )
    )


def write_json_atomically(path, value):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    temporary_path = f"{path}.tmp"
    with open(temporary_path, "w", encoding="utf-8") as output:
        json.dump(value, output, indent=2, ensure_ascii=False)
    os.replace(temporary_path, path)


def initialize_local_reader(args):
    database_root = os.path.abspath(args.db_dir)
    if not os.path.isdir(database_root):
        raise RuntimeError(f"WeChat database directory not found: {database_root}")

    database_pages = collect_database_pages(database_root)
    validation_page = choose_validation_page(database_pages)
    if validation_page is None:
        raise RuntimeError("No WeChat database files were found.")

    process_ids = [args.pid] if args.pid else list_weixin_process_ids()
    if not process_ids:
        raise RuntimeError("Weixin.exe is not running.")

    print("正在扫描微信进程内存以提取数据库密钥...", file=sys.stderr)
    started_at = time.monotonic()
    (
        candidate_values,
        scanned_processes,
        hex_count,
        pointer_count,
        key_salt_count,
        raw_count,
        db_path_ref_count,
        _,
    ) = collect_wechat_v4_candidates(
        process_ids,
    )
    print(f"扫描完成: 找到 {len(candidate_values)} 个密钥候选, 处理 {scanned_processes} 个进程", file=sys.stderr)
    plausible_candidate_count = sum(
        is_plausible_passphrase(candidate)
        for candidate in candidate_values
    )
    print(f"其中 {plausible_candidate_count} 个可能是口令, 正在验证...", file=sys.stderr)

    direct_key = find_key_from_candidates(candidate_values, validation_page)
    passphrase = None
    extraction_mode = "direct"
    database_keys = {}

    if direct_key is not None:
        passphrase = direct_key if is_plausible_passphrase(direct_key) else None
        database_keys = find_direct_database_keys([direct_key], database_pages)
        if not has_required_database_keys(database_keys) and passphrase is not None:
            database_keys = derive_database_keys(passphrase, database_pages)
            extraction_mode = "passphrase"
    else:
        print(f"直接密钥验证未找到, 尝试口令派生 ({plausible_candidate_count} 个候选)...", file=sys.stderr)
        (
            passphrase,
            _,
            _,
        ) = find_wechat_v4_passphrase(
            candidate_values,
            validation_page,
        )
        if passphrase is not None:
            extraction_mode = "passphrase"
            database_keys = derive_database_keys(passphrase, database_pages)

    if not has_required_database_keys(database_keys):
        raise RuntimeError(
            "No usable WeChat 4.x database keys were found in Weixin.exe "
            f"({len(candidate_values)} candidates, "
            f"{plausible_candidate_count} plausible, "
            f"{len(direct_database_keys)} direct database matches)."
        )

    config_path = os.path.abspath(args.config)
    state_directory = os.path.dirname(config_path)
    keys_path = os.path.join(state_directory, "all_keys.json")
    decrypted_directory = os.path.join(state_directory, "decrypted")
    decoded_image_directory = os.path.join(state_directory, "decoded_images")

    write_json_atomically(keys_path, database_keys)
    write_json_atomically(
        config_path,
        {
            "db_dir": database_root,
            "keys_file": keys_path,
            "decrypted_dir": decrypted_directory,
            "decoded_image_dir": decoded_image_directory,
        },
    )

    json.dump(
        {
            "status": "initialized",
            "databaseCount": len(database_pages),
            "keyCount": len(database_keys),
            "extractionMode": extraction_mode,
            "processCount": scanned_processes,
            "candidateCount": len(candidate_values),
            "plausibleCandidateCount": plausible_candidate_count,
            "hexCandidates": hex_count,
            "pointerCandidates": pointer_count,
            "keySaltCandidates": key_salt_count,
            "rawCandidates": raw_count,
            "dbPathRefs": db_path_ref_count,
            "elapsedSeconds": round(time.monotonic() - started_at, 1),
        },
        sys.stdout,
        ensure_ascii=False,
    )
    sys.stdout.write("\n")


def message_type_name(local_type):
    base_type, sub_type = _split_msg_type(local_type)
    if base_type == 1:
        return "Text"
    if base_type == 3:
        return "Image"
    if base_type == 49 and sub_type == 5:
        return "Link"
    if base_type == 49:
        return "File"
    if base_type in (34, 43, 47, 48, 50):
        return "File"
    if base_type in (10000, 10002):
        return "System"
    return "Text"


def read_offset(initial_lookback_seconds):
    raw_offset = os.environ.get("WECHAT_DASHBOARD_OFFSET", "").strip()
    try:
        return max(0, int(raw_offset))
    except ValueError:
        return max(0, int(time.time()) - initial_lookback_seconds)


def read_sessions(app, start_timestamp):
    session_path = app.cache.get(os.path.join("session", "session.db"))
    if not session_path:
        raise RuntimeError("Unable to decrypt session/session.db.")

    with sqlite3.connect(session_path) as connection:
        return connection.execute(
            """
            SELECT username, last_timestamp
            FROM SessionTable
            WHERE last_timestamp >= ?
            ORDER BY last_timestamp
            """,
            (start_timestamp,),
        ).fetchall()


def read_messages(app, start_timestamp):
    names = get_contact_names(app.cache, app.decrypted_dir)
    messages = []
    seen_keys = set()
    max_timestamp = start_timestamp

    for username, _ in read_sessions(app, start_timestamp):
        display_name = names.get(username, username)
        context = {
            "query": username,
            "username": username,
            "display_name": display_name,
            "message_tables": _find_msg_tables_for_user(
                username,
                app.msg_db_keys,
                app.cache,
            ),
            "is_group": "@chatroom" in username,
        }

        for table_context in _iter_table_contexts(context):
            database_name = os.path.basename(table_context["db_path"])
            with sqlite3.connect(table_context["db_path"]) as connection:
                id_to_username = _load_name2id_maps(connection)
                rows = _query_messages(
                    connection,
                    table_context["table_name"],
                    start_ts=start_timestamp,
                    limit=None,
                )

            for row in rows:
                local_id, local_type, create_time, real_sender_id, content, content_type = row
                source_id = f"{username}:{database_name}:{local_id}:{create_time}"
                if source_id in seen_keys:
                    continue
                seen_keys.add(source_id)

                decoded_content = decompress_content(content, content_type)
                if decoded_content is None:
                    decoded_content = "(unable to decompress)"

                sender_from_content, text = _format_message_text(
                    local_id,
                    local_type,
                    decoded_content,
                    table_context["is_group"],
                    username,
                    display_name,
                    names,
                    app.display_name_fn,
                    db_dir=app.db_dir,
                    create_time_ts=create_time,
                    resolve_media=False,
                )
                sender_name = _resolve_sender_label(
                    real_sender_id,
                    sender_from_content,
                    table_context["is_group"],
                    username,
                    display_name,
                    names,
                    id_to_username,
                    app.display_name_fn,
                )

                messages.append(
                    {
                        "id": source_id,
                        "chatId": username,
                        "chatName": display_name,
                        "senderName": sender_name or "Unknown sender",
                        "content": text or "",
                        "sentAt": int(create_time),
                        "messageType": message_type_name(local_type),
                    }
                )
                max_timestamp = max(max_timestamp, int(create_time))

    messages.sort(key=lambda item: (item["sentAt"], item["id"]))
    return messages, max_timestamp


def capture(args):
    app = AppContext(args.config)
    last_offset = read_offset(args.initial_lookback_seconds)
    query_start = max(0, last_offset - args.lookback_seconds)
    messages, max_timestamp = read_messages(app, query_start)

    json.dump(
        {
            "nextOffset": str(max(last_offset, max_timestamp)),
            "messages": messages,
        },
        sys.stdout,
        ensure_ascii=False,
    )
    sys.stdout.write("\n")


def build_parser():
    parser = argparse.ArgumentParser(prog="wechat-local-reader")
    subparsers = parser.add_subparsers(dest="command", required=True)

    capture_parser = subparsers.add_parser("capture")
    capture_parser.add_argument("--config", required=True)
    capture_parser.add_argument("--format", choices=("json",), default="json")
    capture_parser.add_argument("--initial-lookback-seconds", type=int, default=300)
    capture_parser.add_argument("--lookback-seconds", type=int, default=10)
    capture_parser.set_defaults(handler=capture)

    init_parser = subparsers.add_parser("init")
    init_parser.add_argument("--db-dir", required=True)
    init_parser.add_argument("--config", required=True)
    init_parser.add_argument("--pid", type=int)
    init_parser.set_defaults(handler=initialize_local_reader)
    return parser


def main():
    args = build_parser().parse_args()
    try:
        args.handler(args)
    except Exception as error:
        print(f"wechat-local-reader failed: {error}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
