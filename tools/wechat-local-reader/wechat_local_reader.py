"""微信本地数据库读取器（wechat-local-reader）。

本模块用于从微信本地加密数据库（SQLCipher V4）中读取消息。
主要流程：
1. 提取数据库密钥（DB Key）：可从微信进程内存中扫描，或通过外部命令/导入密钥获取。
2. 解密数据库：使用 AES-256-CBC 解密每个数据页，必要时用 zstd 解压消息内容。
3. 读取消息：跨多个消息分片库聚合查询，按时间范围与数量过滤后输出 JSON。

命令行子命令：
- initialize : 初始化本地库（提取/校验密钥并解密所有数据库）
- capture     : 采集最近消息（增量，基于上次偏移）
- extract-key : 仅提取 DB Key 并写出

输出统一为 JSON（写入 stdout 或文件），便于上层 C# 程序解析。
"""

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
import shlex
import sqlite3
import struct
import subprocess
import sys
import time
import xml.etree.ElementTree as ET


def configure_standard_streams():
    """将标准输出/错误流重置为 UTF-8，避免 Windows 控制台编码破坏 JSON 输出。"""
    for stream in (sys.stdout, sys.stderr):
        reconfigure = getattr(stream, "reconfigure", None)
        if reconfigure is None:
            continue
        try:
            reconfigure(encoding="utf-8", errors="backslashreplace")
        except Exception:
            pass


configure_standard_streams()


def write_json_output(value):
    """将 value 序列化为 JSON 写入 stdout，并追加换行。"""
    json.dump(value, sys.stdout, ensure_ascii=True)
    sys.stdout.write("\n")
# ---------------------------------------------------------------------------
# 可选加密后端。读取器需要 AES-256-CBC 用于 SQLCipher V4 数据页解密，
# 需要 zstd 用于压缩消息内容的解压。两者均延迟加载，使得在未安装
# 这些额外依赖的机器上密钥提取诊断仍可工作。
# ---------------------------------------------------------------------------

_AES_BACKEND = None
_ZSTD_BACKEND = None


def _load_aes_backend():
    global _AES_BACKEND
    if _AES_BACKEND is not None:
        return _AES_BACKEND

    try:
        from Crypto.Cipher import AES as _AES  # pycryptodome

        def decrypt(key, iv, ciphertext):
            return _AES.new(key, _AES.MODE_CBC, iv).decrypt(ciphertext)

        _AES_BACKEND = ("pycryptodome", decrypt)
        return _AES_BACKEND
    except ImportError:
        pass

    try:
        from cryptography.hazmat.primitives.ciphers import Cipher as _Cipher
        from cryptography.hazmat.primitives.ciphers import algorithms as _alg
        from cryptography.hazmat.primitives.ciphers import modes as _modes

        def decrypt(key, iv, ciphertext):
            decryptor = _Cipher(_alg.AES(key), _modes.CBC(iv)).decryptor()
            return decryptor.update(ciphertext) + decryptor.finalize()

        _AES_BACKEND = ("cryptography", decrypt)
        return _AES_BACKEND
    except ImportError:
        pass

    return None


def _load_zstd_backend():
    global _ZSTD_BACKEND
    if _ZSTD_BACKEND is not None:
        return _ZSTD_BACKEND

    try:
        import zstandard as _zstd

        _ZSTD_BACKEND = ("zstandard", lambda data: _zstd.ZstdDecompressor().decompress(data))
        return _ZSTD_BACKEND
    except ImportError:
        pass

    try:
        import pyzstd as _pyzstd

        _ZSTD_BACKEND = ("pyzstd", lambda data: _pyzstd.decompress(data))
        return _ZSTD_BACKEND
    except ImportError:
        pass

    try:
        import compression.zstd as _stdlib_zstd  # Python 3.14+

        _ZSTD_BACKEND = (
            "stdlib",
            lambda data: _stdlib_zstd.ZstdDecompressor().decompress(data),
        )
        return _ZSTD_BACKEND
    except ImportError:
        pass

    return None


def aes_decrypt(key, iv, ciphertext):
    """使用 AES-256-CBC 解密密文。自动选择可用的加密后端（pycryptodome 或 cryptography）。"""
    backend = _load_aes_backend()
    if backend is None:
        raise RuntimeError(
            "AES backend not available. Install 'cryptography' or 'pycryptodome'."
        )
    return backend[1](key, iv, ciphertext)


def zstd_decompress(data):
    """使用 zstd 解压数据。自动选择可用的解压后端（zstandard 或 pyzstd）。"""
    backend = _load_zstd_backend()
    if backend is None:
        raise RuntimeError(
            "zstd backend not available. Install 'zstandard' or 'pyzstd'."
        )
    return backend[1](data)


# SQLite 与微信 V4 加密相关常量
PAGE_SIZE = 4096              # SQLite 默认页大小
KEY_SIZE = 32                 # 256 位密钥长度
SALT_SIZE = 16                # 盐值长度
RESERVE_SIZE = 80             # 每页保留字节数（用于 HMAC）
WECHAT_V4_ROUND_COUNT = 256000   # SQLCipher V4 KDF 迭代轮数
PROCESS_VM_READ = 0x0010      # 进程读内存权限
PROCESS_QUERY_INFORMATION = 0x0400   # 进程查询信息权限
MEM_COMMIT = 0x1000           # 已提交内存
MEM_PRIVATE = 0x20000         # 私有内存
READABLE_PAGE_PROTECTIONS = {0x02, 0x04, 0x08, 0x20, 0x40, 0x80}   # 可读页保护标志集
MEMORY_READ_CHUNK_SIZE = 1 * 1024 * 1024    # 单次读内存块大小（1MB）
MAX_REGION_SIZE = 200 * 1024 * 1024         # 单个内存区域最大扫描字节数
SCAN_TIMEOUT_SECONDS = 120                  # 扫描超时
# 密钥指针结构特征正则：定位微信内存中存放 DB Key 指针的结构
KEY_POINTER_STRUCTURE = re.compile(
    b"(.{6}\\x00\\x00)"
    b"\\x00{8}\\x20\\x00{7}(.{8})",
    re.DOTALL,
)
# 64 位十六进制密钥（ASCII 形式）正则
HEX_KEY_ASCII = re.compile(
    rb"(?<![0-9a-fA-F])([0-9a-fA-F]{64})(?![0-9a-fA-F])"
)
# 64 位十六进制密钥（UTF-16 形式）正则
HEX_KEY_UTF16 = re.compile(
    rb"(?<![0-9a-fA-F]\x00)((?:[0-9a-fA-F]\x00){64})"
    rb"(?![0-9a-fA-F]\x00)"
)
# 96 位十六进制密钥+盐（ASCII 形式）正则
HEX_KEY_SALT_ASCII = re.compile(
    rb"(?<![0-9a-fA-F])([0-9a-fA-F]{96})(?![0-9a-fA-F])"
)
# 96 位十六进制密钥+盐（UTF-16 形式）正则
HEX_KEY_SALT_UTF16 = re.compile(
    rb"(?<![0-9a-fA-F]\x00)((?:[0-9a-fA-F]\x00){96})"
    rb"(?![0-9a-fA-F]\x00)"
)
# 数据库文件路径关键字正则，用于在内存中定位微信数据库相关区域
DB_PATH_KEYWORD = re.compile(
    rb"(message_\d+\.db|session\.db|contact\.db|favorite\.db|head_image\.db|"
    rb"MicroMsg|db_storage|MsgDB|HardLink)",
    re.IGNORECASE,
)

SQLITE_HEADER = b"SQLite format 3\x00"   # SQLite 文件头标识
# 历史回溯范围映射：7 天/30 天/全部
BOOTSTRAP_RANGES = {
    "7d": 7 * 24 * 3600,
    "30d": 30 * 24 * 3600,
    "all": 0,
}


class MemoryBasicInformation(ctypes.Structure):
    """Windows MEMORY_BASIC_INFORMATION 结构的 ctypes 映射，用于 VirtualQueryEx。"""
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


# ---------------------------------------------------------------------------
# 内存扫描与密钥提取（从既有实现保留）
# ---------------------------------------------------------------------------

def windows_kernel32():
    """加载 kernel32 并设置 OpenProcess/ReadProcessMemory/VirtualQueryEx/CloseHandle 的签名。"""
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
    """在内存中按密钥指针结构特征查找候选指针。"""
    pointers = []
    for match in KEY_POINTER_STRUCTURE.finditer(memory):
        capacity = int.from_bytes(match.group(2), "little")
        if 31 <= capacity <= 4096:
            pointers.append(int.from_bytes(match.group(1), "little"))
    return list(dict.fromkeys(pointers))


def find_hex_key_candidates(memory):
    """在内存中按 ASCII/UTF-16 形式查找 64 位十六进制密钥候选。"""
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
    """在内存中查找微信数据库路径关键字出现位置，用于定位相关内存区域。"""
    offsets = []
    for match in DB_PATH_KEYWORD.finditer(memory):
        offsets.append(match.start())
    return offsets


def find_raw_key_candidates(memory, stride=8):
    """以 stride 步长扫描内存，找出形态合理的原始密钥候选（32 字节）。"""
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
    """校验旧版（V3）密钥是否匹配数据库首页：通过 HMAC 比对页尾校验码。"""
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
    """校验 SQLCipher V4 密钥是否匹配数据库首页，支持 64/80 两种保留字节长度。"""
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
    """依次尝试 V3 与 V4 校验，任一通过即认为密钥有效。"""
    if verify_database_key(database_key, page):
        return True
    if verify_database_key_v4(database_key, page):
        return True
    return False


def derive_database_key(passphrase, page):
    """由口令派生数据库密钥（V3，KDF 迭代），并校验是否匹配首页。"""
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
    """由口令派生数据库密钥（V4，KDF 迭代），并校验是否匹配首页。"""
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
    """通过 tasklist 列出所有 Weixin.exe 进程，返回 (pid, 内存占用KB) 列表。"""
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


# ---------------------------------------------------------------------------
# 数据库发现、密钥派生与解密
# ---------------------------------------------------------------------------

def collect_database_pages(database_root):
    """遍历 database_root 下所有 .db 文件，读取首页（前 PAGE_SIZE 字节）用于后续密钥校验。"""
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


def normalize_database_root(path):
    """Accept either an account directory or its db_storage directory."""
    absolute = os.path.abspath(path)
    db_storage = os.path.join(absolute, "db_storage")
    if os.path.isdir(db_storage):
        return db_storage
    return absolute


def choose_validation_page(database_pages):
    """按优先顺序选择用于校验密钥的数据库首页（收藏/头像/会话/联系人/消息）。"""
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


def parse_hex_key(value):
    if value is None:
        return None
    cleaned = value.strip().lower()
    if not cleaned:
        return None
    if cleaned.startswith("0x"):
        cleaned = cleaned[2:]
    if not re.fullmatch(r"[0-9a-f]{64}", cleaned):
        raise RuntimeError("Imported DB key must be a 64-character hexadecimal string.")
    return bytes.fromhex(cleaned)


def extract_db_key_from_text(text):
    """Extract a 64-hex-character DB key from JSON or plain command output."""
    if not text:
        return None

    def walk_json(value):
        if isinstance(value, str):
            try:
                return parse_hex_key(value)
            except RuntimeError:
                return None
        if isinstance(value, dict):
            preferred = (
                "key",
                "dbKey",
                "db_key",
                "databaseKey",
                "database_key",
                "WECHAT_DB_KEY",
            )
            for name in preferred:
                if name in value:
                    found = walk_json(value[name])
                    if found is not None:
                        return found
            for item in value.values():
                found = walk_json(item)
                if found is not None:
                    return found
        if isinstance(value, list):
            for item in value:
                found = walk_json(item)
                if found is not None:
                    return found
        return None

    try:
        parsed = json.loads(text)
        found = walk_json(parsed)
        if found is not None:
            return found
    except Exception:
        pass

    match = re.search(r"(?<![0-9a-fA-F])([0-9a-fA-F]{64})(?![0-9a-fA-F])", text)
    return bytes.fromhex(match.group(1)) if match else None


def extract_db_key_from_file(path):
    if not path:
        return None
    normalized = os.path.abspath(os.path.expanduser(path))
    if not os.path.isfile(normalized):
        return None
    with open(normalized, "r", encoding="utf-8", errors="ignore") as key_file:
        return extract_db_key_from_text(key_file.read())


def external_key_command_has_pid_token(command_text):
    return "{pid}" in command_text or "{wechat_pid}" in command_text


def build_external_key_command(command_text, process_id=None, db_dir=None, config_path=None):
    """Build a command for subprocess.run without invoking a shell.

    Plain strings are passed directly on Windows so quoted executable paths such
    as "C:\\Program Files\\Tool\\DbkeyHookCMD.exe" -pid {pid} keep working.
    JSON arrays are supported for callers that want explicit argv boundaries.
    """
    if not command_text or not command_text.strip():
        return None

    replacements = {
        "{pid}": "" if process_id is None else str(process_id),
        "{wechat_pid}": "" if process_id is None else str(process_id),
        "{db_dir}": "" if db_dir is None else str(db_dir),
        "{config}": "" if config_path is None else str(config_path),
    }
    raw_command = command_text.strip()

    def replace_tokens(value):
        for token, replacement in replacements.items():
            value = value.replace(token, replacement)
        return value

    if raw_command.startswith("["):
        try:
            parsed = json.loads(raw_command)
        except json.JSONDecodeError as error:
            raise RuntimeError(f"Invalid external key command JSON: {error}") from error
        if not isinstance(parsed, list) or not parsed or not all(isinstance(item, str) for item in parsed):
            raise RuntimeError("External key command JSON must be a non-empty string array.")
        return [replace_tokens(item) for item in parsed]

    expanded = replace_tokens(raw_command)

    if os.name == "nt":
        return expanded

    try:
        return shlex.split(expanded, posix=True)
    except ValueError as error:
        raise RuntimeError(f"Invalid external key command: {error}") from error


def run_external_key_command(command_text, process_id=None, db_dir=None, config_path=None, key_file=None):
    """Run a user-configured external key tool and parse a DB key from output."""
    command = build_external_key_command(
        command_text,
        process_id=process_id,
        db_dir=db_dir,
        config_path=config_path,
    )
    if not command:
        return None

    result = subprocess.run(
        command,
        capture_output=True,
        check=False,
        text=True,
        timeout=180,
    )
    output = (result.stdout or "") + "\n" + (result.stderr or "")
    key = extract_db_key_from_text(output)
    if key is None and key_file:
        key = extract_db_key_from_file(key_file)
    if key is None:
        raise RuntimeError(
            f"External key command did not return a usable DB key (exit code {result.returncode})."
        )
    return key


def derive_database_keys_from_imported_key(imported_key, database_pages):
    """Validate an imported WeChat DB master key against all database pages."""
    database_keys = derive_database_keys(imported_key, database_pages)
    if has_required_database_keys(database_keys):
        return "imported_passphrase", database_keys

    # Some tools may export an already-derived page key for one database.
    # This is not sufficient for full capture, but the diagnostic makes the
    # mismatch explicit instead of silently accepting a partial setup.
    direct_keys = find_direct_database_keys([imported_key], database_pages)
    if has_required_database_keys(direct_keys):
        return "imported_direct", direct_keys

    return "imported_invalid", database_keys or direct_keys


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
    """判断密钥集合是否包含必需的数据库（会话/联系人/至少一个消息库）。"""
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
    """原子写入 JSON 文件：先写临时文件再替换，避免半截写入。"""
    os.makedirs(os.path.dirname(path), exist_ok=True)
    temporary_path = f"{path}.tmp"
    with open(temporary_path, "w", encoding="utf-8") as output:
        json.dump(value, output, indent=2, ensure_ascii=False)
    os.replace(temporary_path, path)


# ---------------------------------------------------------------------------
# SQLCipher V4 数据页解密（自包含，不依赖 wechat_cli）
# ---------------------------------------------------------------------------

def decrypt_database_page(page, database_key, page_number, reserve_size=RESERVE_SIZE):
    """解密单个 SQLCipher V4 数据页，返回 4096 字节的明文页。

    页面布局（reserve=80）：
      第 1 页：salt(16) + 密文(4000) + iv(16) + hmac(64)
      第 N 页：密文(4016) + iv(16) + hmac(64)

    明文页被重建为标准 4096 字节 SQLite 页，保留区清零，
    以便 SQLite 按普通文件读取。
    """
    if len(page) != PAGE_SIZE:
        return None

    salt = page[:SALT_SIZE]
    content_end = PAGE_SIZE - reserve_size
    iv = page[content_end:content_end + 16]

    if page_number == 1:
        ciphertext = page[SALT_SIZE:content_end]
        plaintext = aes_decrypt(database_key, iv, ciphertext)
        rebuilt = SQLITE_HEADER + plaintext
    else:
        ciphertext = page[:content_end]
        plaintext = aes_decrypt(database_key, iv, ciphertext)
        rebuilt = plaintext

    # Pad to full page size; the reserve area is zeroed.
    if len(rebuilt) < PAGE_SIZE:
        rebuilt = rebuilt + b"\x00" * (PAGE_SIZE - len(rebuilt))
    elif len(rebuilt) > PAGE_SIZE:
        rebuilt = rebuilt[:PAGE_SIZE]

    # Clear the reserved-space field (byte 20) on page 1 so SQLite does not
    # expect trailing reserve bytes in the decrypted file.
    if page_number == 1:
        rebuilt = rebuilt[:20] + b"\x00" + rebuilt[21:]

    return rebuilt


def decrypt_database_file(source_path, destination_path, database_key, reserve_size=RESERVE_SIZE):
    """Decrypt an entire SQLCipher V4 database file to a plain SQLite file."""
    file_size = os.path.getsize(source_path)
    if file_size < PAGE_SIZE:
        return False, "file smaller than one page"

    page_count = file_size // PAGE_SIZE
    if file_size % PAGE_SIZE != 0:
        page_count += 1

    os.makedirs(os.path.dirname(destination_path), exist_ok=True)
    bytes_written = 0
    with open(source_path, "rb") as source, open(destination_path, "wb") as destination:
        for page_number in range(1, page_count + 1):
            page = source.read(PAGE_SIZE)
            if len(page) < PAGE_SIZE:
                page = page + b"\x00" * (PAGE_SIZE - len(page))

            decrypted = decrypt_database_page(
                page, database_key, page_number, reserve_size
            )
            if decrypted is None:
                return False, f"failed to decrypt page {page_number}"

            destination.write(decrypted)
            bytes_written += PAGE_SIZE

    return True, f"{page_count} pages, {bytes_written} bytes"


def decrypt_all_databases(database_root, decrypted_root, keys, changed_only=True):
    """Decrypt every database listed in *keys* into *decrypted_root*.

    Returns a diagnostics dict with per-file status and aggregate counts.
    """
    diagnostics = {
        "total": 0,
        "decrypted": 0,
        "skipped": 0,
        "failed": 0,
        "failures": [],
    }

    for relative_path, key_info in keys.items():
        diagnostics["total"] += 1
        source_path = os.path.join(database_root, relative_path.replace("/", os.sep))
        destination_path = os.path.join(decrypted_root, relative_path.replace("/", os.sep))

        if not os.path.exists(source_path):
            diagnostics["failed"] += 1
            diagnostics["failures"].append(
                {"path": relative_path, "error": "source missing"}
            )
            continue

        if changed_only and os.path.exists(destination_path):
            source_mtime = os.path.getmtime(source_path)
            dest_mtime = os.path.getmtime(destination_path)
            if source_mtime <= dest_mtime:
                diagnostics["skipped"] += 1
                continue

        database_key = bytes.fromhex(key_info["enc_key"])
        ok, detail = decrypt_database_file(source_path, destination_path, database_key)
        if ok:
            diagnostics["decrypted"] += 1
        else:
            diagnostics["failed"] += 1
            diagnostics["failures"].append(
                {"path": relative_path, "error": detail}
            )

    return diagnostics


# ---------------------------------------------------------------------------
# V4 schema reading (SessionTable, Msg_<md5(username)>, Name2Id)
# ---------------------------------------------------------------------------

def md5_hex_lower(value):
    return hashlib.md5(value.encode("utf-8")).hexdigest()


def quote_identifier(identifier):
    return '"' + identifier.replace('"', '""') + '"'


def table_exists(connection, table_name):
    cursor = connection.execute(
        """
        SELECT name FROM sqlite_master
        WHERE type='table' AND name=?
        """,
        (table_name,),
    )
    return cursor.fetchone() is not None


def table_columns(connection, table_name):
    rows = connection.execute(
        f"PRAGMA table_info({quote_identifier(table_name)})"
    ).fetchall()
    return [row[1] for row in rows]


def pick_column(columns, *candidates):
    by_lower = {column.lower(): column for column in columns}
    for candidate in candidates:
        column = by_lower.get(candidate.lower())
        if column:
            return column
    return None


def select_expr(column, alias, fallback="NULL"):
    if column:
        return f"{quote_identifier(column)} AS {quote_identifier(alias)}"
    return f"{fallback} AS {quote_identifier(alias)}"


def read_session_table(session_db_path, contact_names=None):
    """Read sessions from session/session.db.

    Returns a list of dicts with username, last_timestamp, summary, and
    display_name fields. Does not log message content.
    """
    if not os.path.exists(session_db_path):
        return []

    sessions = []
    with sqlite3.connect(session_db_path) as connection:
        connection.row_factory = sqlite3.Row
        if not table_exists(connection, "SessionTable"):
            return []

        columns = table_columns(connection, "SessionTable")
        username_col = pick_column(columns, "username", "strUsrName", "user_name")
        last_timestamp_col = pick_column(
            columns, "last_timestamp", "sort_timestamp", "nTime"
        )
        summary_col = pick_column(columns, "summary", "strContent", "content")
        display_col = pick_column(
            columns,
            "display_name",
            "last_sender_display_name",
            "strNickName",
            "nick_name",
        )

        if not username_col:
            return []

        order_col = last_timestamp_col or username_col
        query = f"""
            SELECT
                {select_expr(username_col, "username")},
                {select_expr(last_timestamp_col, "last_timestamp", "0")},
                {select_expr(summary_col, "summary", "''")},
                {select_expr(display_col, "display_name", "''")}
            FROM SessionTable
            ORDER BY {quote_identifier(order_col)} DESC
        """
        rows = connection.execute(query).fetchall()

        for row in rows:
            username = row["username"] or ""
            display_name = ""
            if contact_names:
                display_name = contact_names.get(username, "")
            if not display_name:
                display_name = row["display_name"] or ""
            sessions.append(
                {
                    "username": username,
                    "last_timestamp": int(row["last_timestamp"] or 0),
                    "summary": row["summary"] or "",
                    "display_name": display_name,
                }
            )

    return sessions


def read_name2id_map(message_db_path):
    """Read the Name2Id table from a message database.

    Returns a dict mapping id -> name.
    """
    name_map = {}
    if not os.path.exists(message_db_path):
        return name_map

    with sqlite3.connect(message_db_path) as connection:
        connection.row_factory = sqlite3.Row
        if not table_exists(connection, "Name2Id"):
            return name_map

        columns = table_columns(connection, "Name2Id")
        id_col = pick_column(columns, "id")
        name_col = pick_column(columns, "user_name", "username", "name", "UsrName")
        if not name_col:
            return name_map

        select_parts = ["rowid AS __rowid", select_expr(name_col, "name")]
        if id_col:
            select_parts.append(select_expr(id_col, "id"))
        else:
            select_parts.append("NULL AS id")

        rows = connection.execute(
            f"SELECT {', '.join(select_parts)} FROM Name2Id"
        ).fetchall()
        for row in rows:
            name = row["name"] or ""
            if not name:
                continue
            name_map[str(row["__rowid"])] = name
            if row["id"] is not None:
                name_map[str(row["id"])] = name

    return name_map


def read_contact_names(contact_db_path):
    """Read contact display names from a decrypted contact database."""
    names = {}
    if not os.path.exists(contact_db_path):
        return names

    with sqlite3.connect(contact_db_path) as connection:
        connection.row_factory = sqlite3.Row
        table_name = "contact" if table_exists(connection, "contact") else None
        if table_name is None and table_exists(connection, "Contact"):
            table_name = "Contact"
        if table_name is None:
            return names

        columns = table_columns(connection, table_name)
        username_col = pick_column(columns, "username", "UserName", "user_name")
        remark_col = pick_column(columns, "remark", "Remark")
        nick_col = pick_column(columns, "nick_name", "NickName", "nickname")
        if not username_col:
            return names

        rows = connection.execute(
            f"""
            SELECT
                {select_expr(username_col, "username")},
                {select_expr(remark_col, "remark", "''")},
                {select_expr(nick_col, "nick_name", "''")}
            FROM {quote_identifier(table_name)}
            """
        ).fetchall()

        for row in rows:
            username = row["username"] or ""
            if not username:
                continue
            display = row["remark"] or row["nick_name"] or username
            names[username] = display

    return names


def list_message_shards(decrypted_root):
    """Find all decrypted message.db and message_N.db files."""
    message_dir = os.path.join(decrypted_root, "message")
    shards = []
    if not os.path.isdir(message_dir):
        return shards

    for filename in sorted(os.listdir(message_dir)):
        if re.match(r"message(?:_\d+)?\.db$", filename):
            shards.append(os.path.join(message_dir, filename))

    return shards


def list_msg_tables(connection, username):
    """Find all Msg_<md5(username)> tables across message shards."""
    table_suffix = md5_hex_lower(username)
    pattern = f"Msg_{table_suffix}"
    tables = []
    cursor = connection.execute(
        """
        SELECT name FROM sqlite_master
        WHERE type='table' AND name=?
        """,
        (pattern,),
    )
    for row in cursor.fetchall():
        tables.append(row[0])
    return tables


def decompress_content(content, content_type):
    """Decompress message content based on its type field.

    WeChat V4 stores some content as zstd-compressed blobs. The content_type
    field indicates the encoding.
    """
    if content is None:
        return ""

    if isinstance(content, bytes):
        if content_type == 1 or content.startswith(b"\x28\xb5\x2f\xfd"):
            try:
                return zstd_decompress(content).decode("utf-8", errors="replace")
            except Exception:
                return ""
        try:
            return content.decode("utf-8", errors="replace")
        except Exception:
            return ""
    else:
        if content_type == 1:
            try:
                return zstd_decompress(content.encode("latin-1")).decode(
                    "utf-8", errors="replace"
                )
            except Exception:
                return content
        return content


def split_msg_type(local_type):
    """Split a WeChat local_type into (base_type, sub_type).

    WeChat encodes the type as a 32-bit integer where the low byte is the
    base type and the next byte is the sub-type. However, system message
    types (10000, 10002) are stored as the full integer and must be matched
    directly.
    """
    if local_type is None:
        return 0, 0
    if local_type in (10000, 10002):
        return local_type, 0
    base = local_type & 0xFF
    sub = (local_type >> 8) & 0xFF
    return base, sub


def message_type_name(local_type):
    base_type, sub_type = split_msg_type(local_type)
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




def is_xml_message(text):
    stripped = (text or "").lstrip()
    return stripped.startswith("<?xml") or stripped.startswith("<msg")


def xml_text(root, path):
    node = root.find(path)
    if node is not None and node.text:
        return node.text.strip()
    return ""


def summarize_xml_message(text, local_type):
    base_type, sub_type = split_msg_type(local_type)
    try:
        root = ET.fromstring(text.lstrip())
    except ET.ParseError:
        return ""

    if root.find(".//videomsg") is not None:
        return "[视频]"
    if root.find(".//emoji") is not None:
        return "[表情]"
    if root.find(".//img") is not None:
        return "[图片]"

    appmsg = root.find(".//appmsg")
    if appmsg is not None:
        title = xml_text(appmsg, "title")
        des = xml_text(appmsg, "des")
        url = xml_text(appmsg, "url")
        app_type = xml_text(appmsg, "type")
        if app_type == "6":
            return "[文件]"
        parts = ["[链接]"]
        if title:
            parts.append(title)
        if des:
            parts.append(f"- {des}")
        if url:
            parts.append(url)
        return " ".join(parts) if len(parts) > 1 else "[链接]"

    location = root.find(".//location")
    if location is not None:
        label = location.attrib.get("label", "").strip()
        return f"[位置] {label}" if label else "[位置]"

    if base_type == 3:
        return "[图片]"
    if base_type == 43:
        return "[视频]"
    if base_type == 47:
        return "[表情]"
    if base_type == 49 and sub_type == 5:
        return "[链接]"
    if base_type == 49:
        return "[文件]"
    return "[非文本消息]"


def summarize_message_content(text, local_type):
    cleaned = (text or "").strip()
    if not cleaned:
        return ""
    if not is_xml_message(cleaned):
        return cleaned

    summary = summarize_xml_message(cleaned, local_type)
    if summary:
        return summary

    message_type = message_type_name(local_type)
    return {
        "Image": "[图片]",
        "Link": "[链接]",
        "File": "[文件]",
        "System": "[系统消息]",
    }.get(message_type, "[非文本消息]")

def extract_sender_and_content(raw_content, is_group, username, display_name, name_map):
    """Parse a WeChat message content field into (sender_name, text).

    Group messages often have the format 'sender_wxid:\nactual_content'.
    Non-group messages have the content directly.
    """
    if raw_content is None:
        return "", ""

    text = raw_content
    sender_name = ""

    if is_group and ":\n" in text:
        parts = text.split(":\n", 1)
        if len(parts) == 2:
            sender_id = parts[0]
            text = parts[1]
            sender_name = name_map.get(sender_id, sender_id)

    return sender_name, text


def query_messages_from_shard(
    shard_path,
    username,
    display_name,
    name_map,
    start_timestamp,
    end_timestamp=None,
    limit=None,
):
    """Query messages for a single user from a single message shard."""
    messages = []
    if not os.path.exists(shard_path):
        return messages

    table_suffix = md5_hex_lower(username)
    table_name = f"Msg_{table_suffix}"
    is_group = "@chatroom" in username

    with sqlite3.connect(shard_path) as connection:
        connection.row_factory = sqlite3.Row
        if not table_exists(connection, table_name):
            return messages

        columns = table_columns(connection, table_name)
        local_id_col = pick_column(columns, "local_id", "localId", "msgId")
        server_id_col = pick_column(columns, "server_id", "MsgSvrID", "msg_svr_id")
        local_type_col = pick_column(columns, "local_type", "type", "Type")
        sort_seq_col = pick_column(columns, "sort_seq", "Sequence", "sequence")
        real_sender_id_col = pick_column(columns, "real_sender_id", "TalkerId")
        create_time_col = pick_column(columns, "create_time", "CreateTime", "nTime")
        message_content_col = pick_column(
            columns, "message_content", "StrContent", "content"
        )
        compress_content_col = pick_column(
            columns, "compress_content", "CompressContent"
        )
        packed_info_col = pick_column(columns, "packed_info_data", "BytesExtra")
        status_col = pick_column(columns, "status", "IsSender")

        if not create_time_col:
            return messages

        # Merge the shard's own Name2Id map.
        shard_name_map = dict(name_map)
        shard_name_map.update(read_name2id_map(shard_path))

        local_id_expr = (
            select_expr(local_id_col, "local_id")
            if local_id_col
            else "rowid AS local_id"
        )
        select_parts = [
            local_id_expr,
            select_expr(server_id_col, "server_id"),
            select_expr(local_type_col, "local_type", "1"),
            select_expr(sort_seq_col, "sort_seq", "0"),
            select_expr(real_sender_id_col, "real_sender_id"),
            select_expr(create_time_col, "create_time"),
            select_expr(message_content_col, "message_content", "''"),
            select_expr(compress_content_col, "compress_content"),
            select_expr(packed_info_col, "packed_info_data"),
            select_expr(status_col, "status", "0"),
        ]

        query = f"""
            SELECT {', '.join(select_parts)}
            FROM {quote_identifier(table_name)}
            WHERE {quote_identifier(create_time_col)} >= ?
        """
        params = [start_timestamp]
        if end_timestamp is not None:
            query += f" AND {quote_identifier(create_time_col)} < ?"
            params.append(end_timestamp)
        query += f" ORDER BY {quote_identifier(create_time_col)} ASC"
        if limit:
            query += " LIMIT ?"
            params.append(limit)

        rows = connection.execute(query, tuple(params)).fetchall()
        for row in rows:
            local_id = row["local_id"]
            server_id = row["server_id"]
            local_type = row["local_type"]
            create_time = row["create_time"]
            real_sender_id = row["real_sender_id"]
            message_content = row["message_content"]
            compress_content = row["compress_content"]

            # Text is normally in message_content. compress_content often holds
            # media payloads, so only use it when the text field is empty.
            decoded = decompress_content(message_content, 0)
            if not decoded and compress_content:
                decoded = decompress_content(compress_content, 1)

            sender_from_content, text = extract_sender_and_content(
                decoded, is_group, username, display_name, shard_name_map
            )
            text = summarize_message_content(text, local_type)

            # Resolve sender: prefer real_sender_id via Name2Id, fall back to
            # the sender parsed from content, then display_name.
            if real_sender_id:
                resolved_user = shard_name_map.get(str(real_sender_id), "")
                sender_name = (
                    shard_name_map.get(resolved_user, resolved_user)
                    or sender_from_content
                    or display_name
                )
            else:
                sender_name = sender_from_content or display_name

            source_id = (
                f"{username}:{os.path.basename(shard_path)}:{server_id or local_id}:{create_time}"
            )

            messages.append(
                {
                    "id": source_id,
                    "chatId": username,
                    "chatName": display_name or username,
                    "senderName": sender_name or "Unknown sender",
                    "content": text or "",
                    "sentAt": int(create_time or 0),
                    "messageType": message_type_name(local_type),
                }
            )

    return messages


def read_messages(decrypted_root, start_timestamp, end_timestamp=None, limit=None):
    """读取自 start_timestamp 起的所有消息（跨会话与分片库）。

    返回 (messages, max_timestamp, diagnostics)。
    """
    diagnostics = {
        "sessions": 0,
        "matched_tables": 0,
        "rows_read": 0,
        "query_errors": 0,
        "shards_scanned": 0,
    }

    session_db_path = os.path.join(decrypted_root, "session", "session.db")
    contact_db_path = os.path.join(decrypted_root, "contact", "contact.db")
    contact_names = read_contact_names(contact_db_path)
    sessions = read_session_table(session_db_path, contact_names=contact_names)
    diagnostics["sessions"] = len(sessions)

    # Global name map resolves wxid/user_name values to display names.
    global_name_map = dict(contact_names)

    shards = list_message_shards(decrypted_root)
    diagnostics["shards_scanned"] = len(shards)

    messages = []
    seen_keys = set()
    max_timestamp = start_timestamp

    for session in sessions:
        username = session["username"]
        if not username:
            continue

        display_name = session["display_name"] or username
        for shard_path in shards:
            try:
                shard_messages = query_messages_from_shard(
                    shard_path,
                    username,
                    display_name,
                    global_name_map,
                    start_timestamp,
                    end_timestamp,
                    limit,
                )
            except sqlite3.DatabaseError:
                diagnostics["query_errors"] += 1
                continue
            if shard_messages:
                diagnostics["matched_tables"] += 1
            for message in shard_messages:
                if message["id"] in seen_keys:
                    continue
                seen_keys.add(message["id"])
                messages.append(message)
                if message["sentAt"] > max_timestamp:
                    max_timestamp = message["sentAt"]

    diagnostics["rows_read"] = len(messages)
    messages.sort(key=lambda item: (item["sentAt"], item["id"]))
    return messages, max_timestamp, diagnostics


# ---------------------------------------------------------------------------
# 偏移量与回溯范围管理
# ---------------------------------------------------------------------------

def read_offset(initial_lookback_seconds):
    """从环境变量读取上次偏移量；缺失则回溯到 initial_lookback_seconds 前。"""
    raw_offset = os.environ.get("WECHAT_DASHBOARD_OFFSET", "").strip()
    try:
        return max(0, int(raw_offset))
    except ValueError:
        return max(0, int(time.time()) - initial_lookback_seconds)


def bootstrap_lookback_seconds(value):
    """把回溯范围标识（7d/30d/all）解析为秒数，默认 30 天。"""
    if not value:
        return 30 * 24 * 3600
    return BOOTSTRAP_RANGES.get(value, 30 * 24 * 3600)


# ---------------------------------------------------------------------------
# 初始化命令（修复版：不再引用 direct_database_keys；分阶段输出）
# ---------------------------------------------------------------------------

def initialize_local_reader(args):
    """初始化本地读取器：发现数据库、提取/派生密钥、解密全部数据库并写出 keys.json。

    整个过程分阶段记录状态，便于上层追踪进度与诊断失败原因。
    """
    stages = []
    started_at = time.monotonic()
    database_root = normalize_database_root(args.db_dir)
    if not os.path.isdir(database_root):
        raise RuntimeError(f"WeChat database directory not found: {database_root}")

    stages.append({"stage": "path", "status": "ok", "db_dir": database_root})

    database_pages = collect_database_pages(database_root)
    validation_page = choose_validation_page(database_pages)
    if validation_page is None:
        stages.append({"stage": "path", "status": "no_databases"})
        raise RuntimeError("No WeChat database files were found.")

    stages.append(
        {
            "stage": "path",
            "status": "databases_found",
            "count": len(database_pages),
        }
    )

    imported_key = parse_hex_key(args.db_key or os.environ.get("WECHAT_DASHBOARD_DB_KEY"))
    key_command = args.key_command or os.environ.get("WECHAT_DASHBOARD_KEY_COMMAND")
    key_file = args.key_file or os.environ.get("WECHAT_DASHBOARD_KEY_FILE")
    database_keys = {}
    extraction_mode = "unknown"
    key_provider_label = "Weixin.exe"
    candidate_values = []
    scanned_processes = 0
    hex_count = 0
    pointer_count = 0
    key_salt_count = 0
    raw_count = 0
    db_path_ref_count = 0
    plausible_candidate_count = 0

    if imported_key is not None:
        stages.append({"stage": "key_provider", "status": "imported"})
        key_provider_label = "imported DB key"
        extraction_mode, database_keys = derive_database_keys_from_imported_key(
            imported_key,
            database_pages,
        )
    elif key_file:
        stages.append({"stage": "key_provider", "status": "key_file"})
        imported_key = extract_db_key_from_file(key_file)
        if imported_key is None:
            raise RuntimeError("External key file did not contain a usable DB key.")
        key_provider_label = "external key file"
        extraction_mode, database_keys = derive_database_keys_from_imported_key(
            imported_key,
            database_pages,
        )
    elif key_command:
        uses_pid = external_key_command_has_pid_token(key_command)
        process_ids = [args.pid] if args.pid else (list_weixin_process_ids() if uses_pid else [None])
        if uses_pid and not process_ids:
            stages.append({"stage": "key_provider", "status": "no_process"})
            raise RuntimeError("Weixin.exe is not running.")

        stages.append(
            {
                "stage": "key_provider",
                "status": "external_command",
                "pid_token": uses_pid,
                "attempts": len(process_ids),
                "key_file": bool(key_file),
            }
        )
        key_provider_label = "external key command"
        scanned_processes = len(process_ids) if uses_pid else 0
        last_external_error = None
        for process_id in process_ids:
            try:
                candidate_key = run_external_key_command(
                    key_command,
                    process_id=process_id,
                    db_dir=database_root,
                    config_path=args.config,
                    key_file=key_file,
                )
            except RuntimeError as error:
                last_external_error = error
                continue

            candidate_mode, candidate_database_keys = derive_database_keys_from_imported_key(
                candidate_key,
                database_pages,
            )
            imported_key = candidate_key
            extraction_mode = "external_" + candidate_mode
            database_keys = candidate_database_keys
            if has_required_database_keys(database_keys):
                break

        if imported_key is None and last_external_error is not None:
            raise RuntimeError(str(last_external_error)) from last_external_error
    else:
        process_ids = [args.pid] if args.pid else list_weixin_process_ids()
        if not process_ids:
            stages.append({"stage": "key_provider", "status": "no_process"})
            raise RuntimeError("Weixin.exe is not running.")

        stages.append(
            {"stage": "key_provider", "status": "scanning", "processes": len(process_ids)}
        )

        print("正在扫描微信进程内存以提取数据库密钥...", file=sys.stderr)
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
        print(
            f"扫描完成: 找到 {len(candidate_values)} 个密钥候选, 处理 {scanned_processes} 个进程",
            file=sys.stderr,
        )
        plausible_candidate_count = sum(
            is_plausible_passphrase(candidate)
            for candidate in candidate_values
        )
        print(f"其中 {plausible_candidate_count} 个可能是口令, 正在验证...", file=sys.stderr)

        direct_key = find_key_from_candidates(candidate_values, validation_page)
        passphrase = None
        extraction_mode = "direct"

        if direct_key is not None:
            passphrase = direct_key if is_plausible_passphrase(direct_key) else None
            database_keys = find_direct_database_keys([direct_key], database_pages)
            if not has_required_database_keys(database_keys) and passphrase is not None:
                database_keys = derive_database_keys(passphrase, database_pages)
                extraction_mode = "passphrase"
        else:
            print(
                f"直接密钥验证未找到, 尝试口令派生 ({plausible_candidate_count} 个候选)...",
                file=sys.stderr,
            )
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

    stages.append(
        {
            "stage": "key_validation",
            "status": "ok" if has_required_database_keys(database_keys) else "failed",
            "extraction_mode": extraction_mode,
            "candidate_count": len(candidate_values),
            "plausible_candidate_count": plausible_candidate_count,
            "validated_database_count": len(database_keys),
            "process_count": scanned_processes,
            "hex_candidates": hex_count,
            "pointer_candidates": pointer_count,
            "key_salt_candidates": key_salt_count,
            "raw_candidates": raw_count,
            "db_path_refs": db_path_ref_count,
        }
    )

    if not has_required_database_keys(database_keys):
        raise RuntimeError(
            f"No usable WeChat 4.x database keys were found from {key_provider_label} "
            f"({len(candidate_values)} candidates, "
            f"{plausible_candidate_count} plausible, "
            f"{len(database_keys)} direct database matches)."
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
            "bootstrap_range": args.bootstrap_range,
        },
    )

    stages.append(
        {
            "stage": "config",
            "status": "written",
            "config_path": config_path,
            "decrypted_dir": decrypted_directory,
        }
    )

    write_json_output(
        {
            "status": "initialized",
            "stages": stages,
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
        }
    )


# ---------------------------------------------------------------------------
# 采集命令（自包含，不依赖 wechat_cli）
# ---------------------------------------------------------------------------

def load_config(config_path):
    """读取 JSON 配置文件并返回字典。"""
    with open(config_path, "r", encoding="utf-8") as config_file:
        return json.load(config_file)


def load_keys(keys_path):
    """读取 keys.json（含各数据库密钥与盐）。"""
    with open(keys_path, "r", encoding="utf-8") as keys_file:
        return json.load(keys_file)


def capture(args):
    """采集命令：解密变更数据库并按时间窗口/偏移读取消息，输出 JSON 结果。

    支持增量（基于上次偏移）与分页（显式时间戳/offset/limit）两种模式。
    输出包含 stages 进度、nextOffset、totalMessages 与 messages 列表。
    """
    stages = []
    config = load_config(args.config)
    database_root = config["db_dir"]
    keys_path = config["keys_file"]
    decrypted_root = config["decrypted_dir"]
    bootstrap_range = config.get("bootstrap_range", "30d")

    stages.append({"stage": "config", "status": "loaded", "db_dir": database_root})

    keys = load_keys(keys_path)
    stages.append(
        {"stage": "keys", "status": "loaded", "key_count": len(keys)}
    )

    # 解密发生变更的数据库
    decrypt_diag = decrypt_all_databases(database_root, decrypted_root, keys)
    stages.append(
        {
            "stage": "decrypt",
            "status": "ok" if decrypt_diag["failed"] == 0 else "partial",
            "total": decrypt_diag["total"],
            "decrypted": decrypt_diag["decrypted"],
            "skipped": decrypt_diag["skipped"],
            "failed": decrypt_diag["failed"],
            "failures": decrypt_diag["failures"][:5],
        }
    )

    # 确定查询时间窗口。默认采用增量、偏移驱动；
    # 当显式指定时间戳时，用于 WPF 分页消息视图。
    if args.start_timestamp is not None:
        last_offset = args.start_timestamp
        query_start = max(0, args.start_timestamp)
    else:
        last_offset = read_offset(bootstrap_lookback_seconds(bootstrap_range))
        query_start = max(0, last_offset - args.lookback_seconds)
    query_end = args.end_timestamp
    paged_query = (
        args.start_timestamp is not None
        or args.end_timestamp is not None
        or args.offset > 0
    )
    query_limit = None if paged_query else args.limit or None
    stages.append(
        {
            "stage": "offset",
            "status": "ok",
            "last_offset": last_offset,
            "query_start": query_start,
            "query_end": query_end,
        }
    )

    # 读取消息
    messages, max_timestamp, read_diag = read_messages(
        decrypted_root,
        query_start,
        end_timestamp=query_end,
        limit=query_limit,
    )
    total_messages = len(messages)
    result_offset = max(0, args.offset)
    if paged_query:
        # 分页模式：按 offset/limit 切片
        if args.limit > 0:
            result_messages = messages[result_offset : result_offset + args.limit]
        else:
            result_messages = messages[result_offset:]
    else:
        result_messages = messages
    stages.append(
        {
            "stage": "query",
            "status": "ok",
            "sessions": read_diag["sessions"],
            "matched_tables": read_diag["matched_tables"],
            "shards_scanned": read_diag["shards_scanned"],
            "rows_read": read_diag["rows_read"],
            "query_errors": read_diag["query_errors"],
        }
    )

    next_offset = str(max(last_offset, max_timestamp))

    write_json_output(
        {
            "status": "ok",
            "stages": stages,
            "nextOffset": next_offset,
            "totalMessages": total_messages,
            "offset": result_offset,
            "limit": args.limit,
            "messages": result_messages,
        }
    )


def build_parser():
    """构建命令行参数解析器，注册 capture/init/extract-key 子命令。"""
    parser = argparse.ArgumentParser(prog="wechat-local-reader")
    subparsers = parser.add_subparsers(dest="command", required=True)

    capture_parser = subparsers.add_parser("capture")
    capture_parser.add_argument("--config", required=True)
    capture_parser.add_argument("--format", choices=("json",), default="json")
    capture_parser.add_argument("--initial-lookback-seconds", type=int, default=300)
    capture_parser.add_argument("--lookback-seconds", type=int, default=10)
    capture_parser.add_argument("--start-timestamp", type=int)
    capture_parser.add_argument("--end-timestamp", type=int)
    capture_parser.add_argument("--offset", type=int, default=0)
    capture_parser.add_argument("--limit", type=int, default=0)
    capture_parser.set_defaults(handler=capture)

    init_parser = subparsers.add_parser("init")
    init_parser.add_argument("--db-dir", required=True)
    init_parser.add_argument("--config", required=True)
    init_parser.add_argument("--pid", type=int)
    init_parser.add_argument(
        "--db-key",
        help="Optional 64-character hex DB master key. Prefer WECHAT_DASHBOARD_DB_KEY to avoid command-line exposure.",
    )
    init_parser.add_argument(
        "--key-command",
        help="Optional external command that prints a 64-character hex DB key as JSON or text. Prefer WECHAT_DASHBOARD_KEY_COMMAND.",
    )
    init_parser.add_argument(
        "--key-file",
        help="Optional file written by an external key tool. Prefer WECHAT_DASHBOARD_KEY_FILE.",
    )
    init_parser.add_argument(
        "--bootstrap-range",
        choices=("7d", "30d", "all"),
        default="30d",
    )
    init_parser.set_defaults(handler=initialize_local_reader)
    return parser


def main():
    """程序入口：解析命令行参数并分发到对应子命令；异常时输出到 stderr 并返回 1。"""
    args = build_parser().parse_args()
    try:
        args.handler(args)
    except Exception as error:
        print(f"wechat-local-reader failed: {error}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
