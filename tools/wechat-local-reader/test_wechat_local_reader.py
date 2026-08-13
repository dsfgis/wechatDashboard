"""wechat_local_reader 模块的单元测试。

测试覆盖三大核心能力：
1. 微信密钥提取（WeChatKeyExtractionTests）：内存扫描候选定位、密钥派生与校验。
2. SQLCipher V4 数据库读取（WeChatV4SchemaReaderTests）：页面解密、SQLite 结构重建、消息查询。
3. 采集命令流程（WeChatCaptureCommandTests）：capture 子命令的输出格式与增量偏移。

测试使用桩（stub）数据与临时数据库，不依赖真实微信环境。
"""

import hashlib
import hmac
import io
import json
import os
import struct
import sqlite3
import tempfile
import unittest
from unittest import mock

import wechat_local_reader


class WeChatKeyExtractionTests(unittest.TestCase):
    """微信密钥提取相关函数的测试集。"""
    def test_find_key_pointer_candidates_reads_pointer_from_v4_stub(self):
        pointer = 0x000001D234567890
        stub = (
            pointer.to_bytes(8, "little")
            + b"\x00" * 8
            + b"\x20"
            + b"\x00" * 7
            + (0x3F).to_bytes(8, "little")
        )

        candidates = wechat_local_reader.find_key_pointer_candidates(
            b"prefix" + stub + b"suffix"
        )

        self.assertEqual([pointer], candidates)

    def test_find_hex_key_candidates_supports_ascii_and_utf16(self):
        key_hex = "".join(f"{value:02x}" for value in range(32))
        memory = (
            b"prefix "
            + key_hex.encode("ascii")
            + b" middle "
            + key_hex.encode("utf-16-le")
            + b" suffix"
        )

        candidates = wechat_local_reader.find_hex_key_candidates(memory)

        self.assertEqual([bytes(range(32))], candidates)

    def test_plausible_passphrase_rejects_text_and_low_entropy_values(self):
        self.assertFalse(wechat_local_reader.is_plausible_passphrase(b"\x00" * 32))
        self.assertFalse(
            wechat_local_reader.is_plausible_passphrase(b"0123456789abcdef" * 2)
        )
        self.assertTrue(
            wechat_local_reader.is_plausible_passphrase(bytes(range(32)))
        )

    def test_derive_database_key_validates_wechat_v4_page(self):
        passphrase = bytes(range(32))
        page = bytearray((index * 17) % 256 for index in range(4096))
        salt = bytes(range(16, 32))
        page[:16] = salt

        database_key = hashlib.pbkdf2_hmac(
            "sha512",
            passphrase,
            salt,
            256000,
            dklen=32,
        )
        mac_salt = bytes(value ^ 0x3A for value in salt)
        mac_key = hashlib.pbkdf2_hmac(
            "sha512",
            database_key,
            mac_salt,
            2,
            dklen=32,
        )
        mac = hmac.new(mac_key, page[16:4032], hashlib.sha512)
        mac.update(struct.pack("<I", 1))
        page[4032:4096] = mac.digest()

        actual_key = wechat_local_reader.derive_database_key(
            passphrase,
            bytes(page),
        )

        self.assertEqual(database_key, actual_key)
        self.assertTrue(
            wechat_local_reader.verify_database_key(actual_key, bytes(page))
        )
        self.assertIsNone(
            wechat_local_reader.derive_database_key(b"x" * 32, bytes(page))
        )

    def test_find_direct_database_keys_maps_derived_key(self):
        passphrase = bytes(range(32))
        page = bytearray((index * 17) % 256 for index in range(4096))
        salt = bytes(range(16, 32))
        page[:16] = salt
        database_key = hashlib.pbkdf2_hmac(
            "sha512",
            passphrase,
            salt,
            256000,
            dklen=32,
        )
        mac_salt = bytes(value ^ 0x3A for value in salt)
        mac_key = hashlib.pbkdf2_hmac(
            "sha512",
            database_key,
            mac_salt,
            2,
            dklen=32,
        )
        mac = hmac.new(mac_key, page[16:4032], hashlib.sha512)
        mac.update(struct.pack("<I", 1))
        page[4032:4096] = mac.digest()
        entries = [("message/message_0.db", "unused", bytes(page))]

        actual = wechat_local_reader.find_direct_database_keys(
            [b"not-a-key".ljust(32, b"\x00"), database_key],
            entries,
        )

        self.assertEqual(
            database_key.hex(),
            actual["message/message_0.db"]["enc_key"],
        )

    def test_imported_db_key_derives_required_database_keys(self):
        passphrase = bytes(range(32))

        def make_page(salt_start):
            page = bytearray((index * 19 + salt_start) % 256 for index in range(4096))
            salt = bytes(range(salt_start, salt_start + 16))
            page[:16] = salt
            database_key = hashlib.pbkdf2_hmac(
                "sha512",
                passphrase,
                salt,
                256000,
                dklen=32,
            )
            mac_salt = bytes(value ^ 0x3A for value in salt)
            mac_key = hashlib.pbkdf2_hmac(
                "sha512",
                database_key,
                mac_salt,
                2,
                dklen=32,
            )
            mac = hmac.new(mac_key, page[16:4032], hashlib.sha512)
            mac.update(struct.pack("<I", 1))
            page[4032:4096] = mac.digest()
            return bytes(page), database_key

        with tempfile.TemporaryDirectory() as temp_dir:
            entries = []
            expected = {}
            for relative_path, salt_start in (
                ("session/session.db", 16),
                ("contact/contact.db", 32),
                ("message/message_0.db", 48),
            ):
                page, database_key = make_page(salt_start)
                file_path = os.path.join(temp_dir, relative_path.replace("/", os.sep))
                os.makedirs(os.path.dirname(file_path), exist_ok=True)
                with open(file_path, "wb") as output:
                    output.write(page)
                entries.append((relative_path, file_path, page))
                expected[relative_path] = database_key.hex()

            mode, keys = wechat_local_reader.derive_database_keys_from_imported_key(
                passphrase,
                entries,
            )

            self.assertEqual("imported_passphrase", mode)
            self.assertTrue(wechat_local_reader.has_required_database_keys(keys))
            self.assertEqual(expected["session/session.db"], keys["session/session.db"]["enc_key"])
            self.assertEqual(expected["contact/contact.db"], keys["contact/contact.db"]["enc_key"])
            self.assertEqual(expected["message/message_0.db"], keys["message/message_0.db"]["enc_key"])

    def test_extract_db_key_from_json_output(self):
        key_hex = bytes(range(32)).hex()
        output = json.dumps({"success": True, "data": {"db_key": key_hex}})

        actual = wechat_local_reader.extract_db_key_from_text(output)

        self.assertEqual(bytes(range(32)), actual)

    def test_extract_db_key_from_plain_text_output(self):
        key_hex = bytes(range(32)).hex()
        output = f"status ok\nDB Key: {key_hex}\n"

        actual = wechat_local_reader.extract_db_key_from_text(output)

        self.assertEqual(bytes(range(32)), actual)

    def test_extract_db_key_returns_none_without_key(self):
        self.assertIsNone(wechat_local_reader.extract_db_key_from_text("no key here"))

    def test_build_external_key_command_json_array_substitutes_pid(self):
        command = json.dumps(
            [
                "D:\\tools\\DbkeyHookCMD.exe",
                "-pid",
                "{pid}",
                "--db-dir",
                "{db_dir}",
            ]
        )

        actual = wechat_local_reader.build_external_key_command(
            command,
            process_id=22800,
            db_dir="D:\\cache\\xwechat_files\\account\\db_storage",
        )

        self.assertEqual(
            [
                "D:\\tools\\DbkeyHookCMD.exe",
                "-pid",
                "22800",
                "--db-dir",
                "D:\\cache\\xwechat_files\\account\\db_storage",
            ],
            actual,
        )

    def test_run_external_key_command_reads_key_file(self):
        key = bytes(range(32))

        class Result:
            returncode = 0
            stdout = ""
            stderr = ""

        with tempfile.TemporaryDirectory() as temp_dir:
            key_path = os.path.join(temp_dir, "dbkey.txt")
            with open(key_path, "w", encoding="utf-8") as output:
                output.write(f"DB Key: {key.hex()}\n")

            command = json.dumps(["D:\\tools\\DbkeyHookCMD.exe", "-pid", "{pid}"])
            with mock.patch.object(wechat_local_reader.subprocess, "run", return_value=Result()) as run:
                actual = wechat_local_reader.run_external_key_command(
                    command,
                    process_id=22800,
                    key_file=key_path,
                )

        self.assertEqual(key, actual)
        self.assertEqual(["D:\\tools\\DbkeyHookCMD.exe", "-pid", "22800"], run.call_args.args[0])

    def test_initialize_local_reader_reads_standalone_key_file(self):
        key = bytes(range(32))
        database_pages = [("session/session.db", "session.db", b"page")]
        derived_keys = {
            "session/session.db": {"enc_key": "a" * 64, "salt": "0" * 32, "size_mb": 0},
            "contact/contact.db": {"enc_key": "b" * 64, "salt": "1" * 32, "size_mb": 0},
            "message/message_0.db": {"enc_key": "c" * 64, "salt": "2" * 32, "size_mb": 0},
        }

        with tempfile.TemporaryDirectory() as temp_dir:
            db_dir = os.path.join(temp_dir, "db_storage")
            os.makedirs(db_dir)
            key_path = os.path.join(temp_dir, "dbkey.txt")
            config_path = os.path.join(temp_dir, "state", "config.json")
            with open(key_path, "w", encoding="utf-8") as output:
                output.write(f"DB Key: {key.hex()}\n")

            args = mock.Mock()
            args.db_dir = db_dir
            args.config = config_path
            args.db_key = None
            args.key_command = None
            args.key_file = key_path
            args.pid = None
            args.bootstrap_range = "30d"
            stdout = io.StringIO()

            with mock.patch.object(
                wechat_local_reader,
                "collect_database_pages",
                return_value=database_pages,
            ):
                with mock.patch.object(
                    wechat_local_reader,
                    "derive_database_keys_from_imported_key",
                    return_value=("imported_passphrase", derived_keys),
                ) as derive:
                    with mock.patch.object(wechat_local_reader.sys, "stdout", stdout):
                        wechat_local_reader.initialize_local_reader(args)

        derive.assert_called_once_with(key, database_pages)
        payload = json.loads(stdout.getvalue())
        provider_stages = [
            stage for stage in payload["stages"] if stage["stage"] == "key_provider"
        ]
        self.assertEqual("initialized", payload["status"])
        self.assertEqual("key_file", provider_stages[0]["status"])

    def test_find_key_salt_candidates_extracts_key_from_96_char_hex(self):
        key = bytes(range(32))
        salt = bytes(range(16))
        combined_hex = key.hex() + salt.hex()
        memory = b"prefix " + combined_hex.encode("ascii") + b" suffix"

        candidates = wechat_local_reader.find_key_salt_candidates(memory)

        self.assertEqual([key], candidates)

    def test_find_key_salt_candidates_utf16(self):
        key = bytes(range(32))
        salt = bytes(range(16))
        combined_hex = (key.hex() + salt.hex()).encode("utf-16-le")
        memory = b"prefix " + combined_hex + b" suffix"

        candidates = wechat_local_reader.find_key_salt_candidates(memory)

        self.assertEqual([key], candidates)

    def test_find_db_path_offsets_returns_match_positions(self):
        memory = b"prefix message_0.db middle session.db suffix"

        offsets = wechat_local_reader.find_db_path_offsets(memory)

        self.assertGreater(len(offsets), 0)
        self.assertIn(b"message_0.db", memory[offsets[0]:offsets[0] + 20])

    def test_find_raw_key_candidates_finds_high_entropy_32_bytes(self):
        key = bytes(range(1, 33))
        memory = b"\x00" * 64 + key + b"\x00" * 64

        candidates = wechat_local_reader.find_raw_key_candidates(memory, stride=1)

        self.assertIn(key, candidates)

    def test_find_raw_key_candidates_skips_zero_start(self):
        key = b"\x00" + bytes(range(1, 32))
        memory = key + b"\x00" * 32

        candidates = wechat_local_reader.find_raw_key_candidates(memory, stride=1)

        self.assertNotIn(key, candidates)

    def test_verify_database_key_v4_with_salt_in_mac(self):
        passphrase = bytes(range(32))
        page = bytearray((index * 17) % 256 for index in range(4096))
        salt = bytes(range(16, 32))
        page[:16] = salt

        database_key = hashlib.pbkdf2_hmac(
            "sha512",
            passphrase,
            salt,
            256000,
            dklen=32,
        )
        mac_salt = bytes(value ^ 0x3A for value in salt)
        mac_key = hashlib.pbkdf2_hmac(
            "sha512",
            database_key,
            mac_salt,
            2,
            dklen=32,
        )
        mac = hmac.new(mac_key, digestmod=hashlib.sha512)
        mac.update(salt)
        mac.update(struct.pack("<I", 1))
        mac.update(page[16:4016])
        page[4032:4096] = mac.digest()

        self.assertTrue(
            wechat_local_reader.verify_database_key_v4(
                database_key,
                bytes(page),
            )
        )
        self.assertFalse(
            wechat_local_reader.verify_database_key_v4(
                b"x" * 32,
                bytes(page),
            )
        )

    def test_try_verify_key_uses_both_formats(self):
        passphrase = bytes(range(32))
        page = bytearray((index * 17) % 256 for index in range(4096))
        salt = bytes(range(16, 32))
        page[:16] = salt
        database_key = hashlib.pbkdf2_hmac(
            "sha512",
            passphrase,
            salt,
            256000,
            dklen=32,
        )
        mac_salt = bytes(value ^ 0x3A for value in salt)
        mac_key = hashlib.pbkdf2_hmac(
            "sha512",
            database_key,
            mac_salt,
            2,
            dklen=32,
        )
        mac = hmac.new(mac_key, page[16:4032], hashlib.sha512)
        mac.update(struct.pack("<I", 1))
        page[4032:4096] = mac.digest()

        self.assertTrue(
            wechat_local_reader.try_verify_key(
                database_key,
                bytes(page),
            )
        )

    def test_derive_database_key_v4_produces_valid_key(self):
        passphrase = bytes(range(32))
        page = bytearray((index * 17) % 256 for index in range(4096))
        salt = bytes(range(16, 32))
        page[:16] = salt
        database_key = hashlib.pbkdf2_hmac(
            "sha512",
            passphrase,
            salt,
            256000,
            dklen=32,
        )
        mac_salt = bytes(value ^ 0x3A for value in salt)
        mac_key = hashlib.pbkdf2_hmac(
            "sha512",
            database_key,
            mac_salt,
            2,
            dklen=32,
        )
        mac = hmac.new(mac_key, digestmod=hashlib.sha512)
        mac.update(salt)
        mac.update(struct.pack("<I", 1))
        mac.update(page[16:4016])
        page[4032:4096] = mac.digest()

        actual_key = wechat_local_reader.derive_database_key_v4(
            passphrase,
            bytes(page),
        )

        self.assertEqual(database_key, actual_key)
        self.assertIsNone(
            wechat_local_reader.derive_database_key_v4(
                b"x" * 32,
                bytes(page),
            )
        )

    def test_find_db_path_offsets_finds_micromsg(self):
        memory = b"some data MicroMsg more data"

        offsets = wechat_local_reader.find_db_path_offsets(memory)

        self.assertEqual(1, len(offsets))
        self.assertEqual(b"MicroMsg", memory[offsets[0]:offsets[0] + 8])

    def test_find_raw_key_candidates_respects_stride(self):
        key = bytes(range(1, 33))
        memory = b"\x00" * 100 + key

        # With large stride that skips the key
        candidates = wechat_local_reader.find_raw_key_candidates(memory, stride=128)

        self.assertEqual([], candidates)

        # With small stride that catches the key
        candidates = wechat_local_reader.find_raw_key_candidates(memory, stride=1)

        self.assertIn(key, candidates)


class WeChatV4SchemaReaderTests(unittest.TestCase):
    """SQLCipher V4 数据库读取与解密相关函数的测试集（不依赖 wechat_cli）。"""

    def test_md5_hex_lower_produces_table_suffix(self):
        result = wechat_local_reader.md5_hex_lower("test_user")

        self.assertEqual(len(result), 32)
        self.assertEqual(result, result.lower())
        self.assertEqual(result, hashlib.md5(b"test_user").hexdigest())

    def test_split_msg_type_extracts_base_and_sub(self):
        self.assertEqual((1, 0), wechat_local_reader.split_msg_type(1))
        self.assertEqual((49, 5), wechat_local_reader.split_msg_type(0x0500 | 49))
        self.assertEqual((0, 0), wechat_local_reader.split_msg_type(None))

    def test_message_type_name_maps_known_types(self):
        self.assertEqual("Text", wechat_local_reader.message_type_name(1))
        self.assertEqual("Image", wechat_local_reader.message_type_name(3))
        self.assertEqual("Link", wechat_local_reader.message_type_name(0x0500 | 49))
        self.assertEqual("File", wechat_local_reader.message_type_name(0x0100 | 49))
        self.assertEqual("System", wechat_local_reader.message_type_name(10000))
        self.assertEqual("Text", wechat_local_reader.message_type_name(999))

    def test_summarize_message_content_replaces_image_xml(self):
        xml = '<?xml version="1.0"?><msg><img aeskey="abc" /></msg>'

        self.assertEqual("[图片]", wechat_local_reader.summarize_message_content(xml, 3))

    def test_summarize_message_content_extracts_link_title(self):
        xml = (
            '<?xml version="1.0"?><msg><appmsg>'
            "<title>项目通知</title><des>请今天确认</des><type>5</type>"
            "</appmsg></msg>"
        )

        self.assertEqual(
            "[链接] 项目通知 - 请今天确认",
            wechat_local_reader.summarize_message_content(xml, 0x0500 | 49),
        )

    def test_summarize_message_content_keeps_plain_text(self):
        self.assertEqual("好的", wechat_local_reader.summarize_message_content("好的", 1))

    def test_extract_sender_and_content_handles_group_prefix(self):
        name_map = {"wxid_abc": "Alice"}
        sender, text = wechat_local_reader.extract_sender_and_content(
            "wxid_abc:\nHello world",
            is_group=True,
            username="room@chatroom",
            display_name="room",
            name_map=name_map,
        )

        self.assertEqual("Alice", sender)
        self.assertEqual("Hello world", text)

    def test_extract_sender_and_content_keeps_direct_message(self):
        sender, text = wechat_local_reader.extract_sender_and_content(
            "Direct message",
            is_group=False,
            username="wxid_self",
            display_name="Me",
            name_map={},
        )

        self.assertEqual("", sender)
        self.assertEqual("Direct message", text)

    def test_bootstrap_lookback_seconds_maps_ranges(self):
        self.assertEqual(
            7 * 24 * 3600,
            wechat_local_reader.bootstrap_lookback_seconds("7d"),
        )
        self.assertEqual(
            30 * 24 * 3600,
            wechat_local_reader.bootstrap_lookback_seconds("30d"),
        )
        self.assertEqual(0, wechat_local_reader.bootstrap_lookback_seconds("all"))
        # Unknown values fall back to 30 days.
        self.assertEqual(
            30 * 24 * 3600,
            wechat_local_reader.bootstrap_lookback_seconds("unknown"),
        )
        self.assertEqual(
            30 * 24 * 3600,
            wechat_local_reader.bootstrap_lookback_seconds(""),
        )

    def test_read_session_table_returns_empty_when_missing(self):
        self.assertEqual(
            [],
            wechat_local_reader.read_session_table("/nonexistent/session.db"),
        )

    def test_read_name2id_map_returns_empty_when_missing(self):
        self.assertEqual(
            {},
            wechat_local_reader.read_name2id_map("/nonexistent/message_0.db"),
        )

    def test_read_session_table_reads_decrypted_sessions(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            session_db_path = os.path.join(temp_dir, "session.db")
            conn = sqlite3.connect(session_db_path)
            try:
                conn.execute(
                    "CREATE TABLE SessionTable ("
                    "username TEXT, last_timestamp INTEGER, "
                    "summary TEXT, display_name TEXT)"
                )
                conn.execute(
                    "INSERT INTO SessionTable VALUES (?, ?, ?, ?)",
                    ("room@chatroom", 1700000000, "last msg", "Test Room"),
                )
                conn.execute(
                    "INSERT INTO SessionTable VALUES (?, ?, ?, ?)",
                    ("wxid_friend", 1700000100, "hi", "Friend"),
                )
                conn.commit()
            finally:
                conn.close()

            sessions = wechat_local_reader.read_session_table(session_db_path)

            self.assertEqual(2, len(sessions))
            # Ordered by last_timestamp DESC.
            self.assertEqual("wxid_friend", sessions[0]["username"])
            self.assertEqual("Friend", sessions[0]["display_name"])
            self.assertEqual(1700000100, sessions[0]["last_timestamp"])
            self.assertEqual("room@chatroom", sessions[1]["username"])

    def test_read_name2id_map_reads_decrypted_map(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            db_path = os.path.join(temp_dir, "message_0.db")
            conn = sqlite3.connect(db_path)
            try:
                conn.execute("CREATE TABLE Name2Id (id INTEGER, name TEXT)")
                conn.execute(
                    "INSERT INTO Name2Id VALUES (?, ?)",
                    (123, "Alice"),
                )
                conn.execute(
                    "INSERT INTO Name2Id VALUES (?, ?)",
                    (456, "Bob"),
                )
                conn.commit()
            finally:
                conn.close()

            name_map = wechat_local_reader.read_name2id_map(db_path)

            self.assertEqual("Alice", name_map["123"])
            self.assertEqual("Bob", name_map["456"])

    def test_list_message_shards_finds_numbered_dbs(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            message_dir = os.path.join(temp_dir, "message")
            os.makedirs(message_dir)
            for name in ["message_0.db", "message_1.db", "not_a_db.txt", "message.db"]:
                open(os.path.join(message_dir, name), "w").close()

            shards = wechat_local_reader.list_message_shards(temp_dir)

            self.assertEqual(3, len(shards))
            self.assertTrue(any(path.endswith("message.db") for path in shards))
            self.assertTrue(any(path.endswith("message_0.db") for path in shards))
            self.assertTrue(any(path.endswith("message_1.db") for path in shards))

    def test_read_session_table_supports_real_v4_columns(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            session_db_path = os.path.join(temp_dir, "session.db")
            conn = sqlite3.connect(session_db_path)
            try:
                conn.execute(
                    "CREATE TABLE SessionTable ("
                    "username TEXT PRIMARY KEY, summary TEXT, "
                    "last_timestamp INTEGER, sort_timestamp INTEGER, "
                    "last_msg_sender TEXT, last_sender_display_name TEXT)"
                )
                conn.execute(
                    "INSERT INTO SessionTable VALUES (?, ?, ?, ?, ?, ?)",
                    (
                        "room@chatroom",
                        "last msg",
                        1700000000,
                        1700000001,
                        "wxid_alice",
                        "Alice",
                    ),
                )
                conn.commit()
            finally:
                conn.close()

            sessions = wechat_local_reader.read_session_table(
                session_db_path,
                contact_names={"room@chatroom": "Project Room"},
            )

            self.assertEqual(1, len(sessions))
            self.assertEqual("room@chatroom", sessions[0]["username"])
            self.assertEqual("Project Room", sessions[0]["display_name"])
            self.assertEqual(1700000000, sessions[0]["last_timestamp"])

    def test_read_name2id_map_supports_real_v4_user_name_column(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            db_path = os.path.join(temp_dir, "message_0.db")
            conn = sqlite3.connect(db_path)
            try:
                conn.execute("CREATE TABLE Name2Id (user_name TEXT)")
                conn.execute("INSERT INTO Name2Id(rowid, user_name) VALUES (?, ?)", (123, "wxid_alice"))
                conn.commit()
            finally:
                conn.close()

            name_map = wechat_local_reader.read_name2id_map(db_path)

            self.assertEqual("wxid_alice", name_map["123"])

    def test_read_contact_names_supports_v4_contact_table(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            db_path = os.path.join(temp_dir, "contact.db")
            conn = sqlite3.connect(db_path)
            try:
                conn.execute(
                    "CREATE TABLE contact ("
                    "username TEXT, remark TEXT, nick_name TEXT, "
                    "small_head_url TEXT, big_head_url TEXT)"
                )
                conn.execute(
                    "INSERT INTO contact VALUES (?, ?, ?, '', '')",
                    ("wxid_alice", "Alice Remark", "Alice Nick"),
                )
                conn.execute(
                    "INSERT INTO contact VALUES (?, ?, ?, '', '')",
                    ("room@chatroom", "", "Project Room"),
                )
                conn.commit()
            finally:
                conn.close()

            names = wechat_local_reader.read_contact_names(db_path)

            self.assertEqual("Alice Remark", names["wxid_alice"])
            self.assertEqual("Project Room", names["room@chatroom"])

    def test_read_messages_aggregates_across_shards(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            # Build a fake decrypted directory structure.
            session_dir = os.path.join(temp_dir, "session")
            message_dir = os.path.join(temp_dir, "message")
            os.makedirs(session_dir)
            os.makedirs(message_dir)

            username = "room@chatroom"
            table_name = f"Msg_{wechat_local_reader.md5_hex_lower(username)}"

            session_db_path = os.path.join(session_dir, "session.db")
            conn = sqlite3.connect(session_db_path)
            try:
                conn.execute(
                    "CREATE TABLE SessionTable ("
                    "username TEXT, last_timestamp INTEGER, "
                    "summary TEXT, display_name TEXT)"
                )
                conn.execute(
                    "INSERT INTO SessionTable VALUES (?, ?, ?, ?)",
                    (username, 1700000200, "summary", "Test Room"),
                )
                conn.commit()
            finally:
                conn.close()

            shard_path = os.path.join(message_dir, "message_0.db")
            conn = sqlite3.connect(shard_path)
            try:
                conn.execute(
                    f"CREATE TABLE {table_name} ("
                    "local_id INTEGER, server_id INTEGER, local_type INTEGER, "
                    "sort_seq INTEGER, real_sender_id INTEGER, create_time INTEGER, "
                    "message_content TEXT, compress_content BLOB, "
                    "packed_info_data BLOB, status INTEGER)"
                )
                conn.execute(
                    f"INSERT INTO {table_name} VALUES (?, ?, ?, ?, ?, ?, ?, NULL, NULL, ?)",
                    (1, 1001, 1, 1, 123, 1700000100, "wxid_abc:\nHello @白驹过隙", 0),
                )
                conn.execute(
                    f"INSERT INTO {table_name} VALUES (?, ?, ?, ?, ?, ?, ?, NULL, NULL, ?)",
                    (2, 1002, 1, 2, 456, 1700000150, "Second message", 0),
                )
                conn.execute("CREATE TABLE Name2Id (id INTEGER, name TEXT)")
                conn.execute("INSERT INTO Name2Id VALUES (?, ?)", (123, "Alice"))
                conn.execute("INSERT INTO Name2Id VALUES (?, ?)", (456, "Bob"))
                conn.commit()
            finally:
                conn.close()

            messages, max_ts, diag = wechat_local_reader.read_messages(
                temp_dir, start_timestamp=0
            )

            self.assertEqual(2, len(messages))
            self.assertEqual(1700000150, max_ts)
            self.assertEqual(1, diag["sessions"])
            self.assertEqual(1, diag["matched_tables"])
            self.assertEqual(1, diag["shards_scanned"])
            self.assertEqual(2, diag["rows_read"])

            # Cross-shard results follow the documented newest-first contract.
            self.assertEqual("Bob", messages[0]["senderName"])
            self.assertEqual("Second message", messages[0]["content"])
            self.assertEqual("Test Room", messages[0]["chatName"])
            self.assertEqual("Text", messages[0]["messageType"])
            self.assertEqual("Alice", messages[1]["senderName"])
            self.assertEqual("Hello @白驹过隙", messages[1]["content"])

    def test_read_messages_skips_malformed_message_shard(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            session_dir = os.path.join(temp_dir, "session")
            message_dir = os.path.join(temp_dir, "message")
            os.makedirs(session_dir)
            os.makedirs(message_dir)

            username = "room@chatroom"
            conn = sqlite3.connect(os.path.join(session_dir, "session.db"))
            try:
                conn.execute(
                    "CREATE TABLE SessionTable ("
                    "username TEXT, last_timestamp INTEGER, "
                    "summary TEXT, display_name TEXT)"
                )
                conn.execute(
                    "INSERT INTO SessionTable VALUES (?, ?, ?, ?)",
                    (username, 1700000200, "summary", "Test Room"),
                )
                conn.commit()
            finally:
                conn.close()

            with open(os.path.join(message_dir, "message_0.db"), "wb") as output:
                output.write(b"not a sqlite database")

            messages, max_ts, diag = wechat_local_reader.read_messages(
                temp_dir, start_timestamp=0
            )

            self.assertEqual([], messages)
            self.assertEqual(0, diag["matched_tables"])
            self.assertEqual(1, diag["shards_scanned"])
            self.assertEqual(1, diag["query_errors"])
            self.assertEqual(0, diag["rows_read"])
            self.assertEqual(0, max_ts)

    def test_read_messages_supports_real_v4_name2id_and_contact_names(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            session_dir = os.path.join(temp_dir, "session")
            contact_dir = os.path.join(temp_dir, "contact")
            message_dir = os.path.join(temp_dir, "message")
            os.makedirs(session_dir)
            os.makedirs(contact_dir)
            os.makedirs(message_dir)

            username = "room@chatroom"
            table_name = f"Msg_{wechat_local_reader.md5_hex_lower(username)}"

            conn = sqlite3.connect(os.path.join(session_dir, "session.db"))
            try:
                conn.execute(
                    "CREATE TABLE SessionTable ("
                    "username TEXT PRIMARY KEY, summary TEXT, "
                    "last_timestamp INTEGER, sort_timestamp INTEGER, "
                    "last_msg_sender TEXT, last_sender_display_name TEXT)"
                )
                conn.execute(
                    "INSERT INTO SessionTable VALUES (?, ?, ?, ?, ?, ?)",
                    (username, "summary", 1700000200, 1700000200, "wxid_alice", "Alice"),
                )
                conn.commit()
            finally:
                conn.close()

            conn = sqlite3.connect(os.path.join(contact_dir, "contact.db"))
            try:
                conn.execute("CREATE TABLE contact (username TEXT, remark TEXT, nick_name TEXT)")
                conn.execute("INSERT INTO contact VALUES (?, ?, ?)", (username, "项目群", "Room Nick"))
                conn.execute("INSERT INTO contact VALUES (?, ?, ?)", ("wxid_alice", "Alice", "Alice Nick"))
                conn.commit()
            finally:
                conn.close()

            conn = sqlite3.connect(os.path.join(message_dir, "message.db"))
            try:
                conn.execute("CREATE TABLE Name2Id (user_name TEXT)")
                conn.execute("INSERT INTO Name2Id(rowid, user_name) VALUES (?, ?)", (123, "wxid_alice"))
                conn.execute(
                    f"CREATE TABLE {table_name} ("
                    "local_id INTEGER PRIMARY KEY AUTOINCREMENT, "
                    "server_id INTEGER, local_type INTEGER, sort_seq INTEGER, "
                    "real_sender_id INTEGER, create_time INTEGER, status INTEGER, "
                    "message_content TEXT, compress_content BLOB, packed_info_data BLOB)"
                )
                conn.execute(
                    f"INSERT INTO {table_name} "
                    "(server_id, local_type, sort_seq, real_sender_id, create_time, status, message_content) "
                    "VALUES (?, ?, ?, ?, ?, ?, ?)",
                    (1001, 1, 1, 123, 1700000100, 4, "wxid_alice:\n@戴少峰 请确认"),
                )
                conn.commit()
            finally:
                conn.close()

            messages, max_ts, diag = wechat_local_reader.read_messages(
                temp_dir, start_timestamp=0
            )

            self.assertEqual(1, len(messages))
            self.assertEqual(1700000100, max_ts)
            self.assertEqual(1, diag["matched_tables"])
            self.assertEqual("项目群", messages[0]["chatName"])
            self.assertEqual("Alice", messages[0]["senderName"])
            self.assertEqual("@戴少峰 请确认", messages[0]["content"])

    def test_read_messages_filters_by_start_timestamp(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            session_dir = os.path.join(temp_dir, "session")
            message_dir = os.path.join(temp_dir, "message")
            os.makedirs(session_dir)
            os.makedirs(message_dir)

            username = "wxid_self"
            table_name = f"Msg_{wechat_local_reader.md5_hex_lower(username)}"

            conn = sqlite3.connect(os.path.join(session_dir, "session.db"))
            try:
                conn.execute(
                    "CREATE TABLE SessionTable ("
                    "username TEXT, last_timestamp INTEGER, "
                    "summary TEXT, display_name TEXT)"
                )
                conn.execute(
                    "INSERT INTO SessionTable VALUES (?, ?, ?, ?)",
                    (username, 1700000200, "s", "Self"),
                )
                conn.commit()
            finally:
                conn.close()

            conn = sqlite3.connect(os.path.join(message_dir, "message_0.db"))
            try:
                conn.execute(
                    f"CREATE TABLE {table_name} ("
                    "local_id INTEGER, server_id INTEGER, local_type INTEGER, "
                    "sort_seq INTEGER, real_sender_id INTEGER, create_time INTEGER, "
                    "message_content TEXT, compress_content BLOB, "
                    "packed_info_data BLOB, status INTEGER)"
                )
                conn.execute(
                    f"INSERT INTO {table_name} VALUES (?, ?, ?, ?, ?, ?, ?, NULL, NULL, ?)",
                    (1, 1, 1, 1, 0, 1700000000, "old", 0),
                )
                conn.execute(
                    f"INSERT INTO {table_name} VALUES (?, ?, ?, ?, ?, ?, ?, NULL, NULL, ?)",
                    (2, 2, 1, 2, 0, 1700000500, "new", 0),
                )
                conn.commit()
            finally:
                conn.close()

            messages, max_ts, _ = wechat_local_reader.read_messages(
                temp_dir, start_timestamp=1700000100
            )

            self.assertEqual(1, len(messages))
            self.assertEqual("new", messages[0]["content"])
            self.assertEqual(1700000500, max_ts)


    def test_read_messages_filters_by_end_timestamp(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            session_dir = os.path.join(temp_dir, "session")
            message_dir = os.path.join(temp_dir, "message")
            os.makedirs(session_dir)
            os.makedirs(message_dir)

            username = "wxid_self"
            table_name = f"Msg_{wechat_local_reader.md5_hex_lower(username)}"

            conn = sqlite3.connect(os.path.join(session_dir, "session.db"))
            try:
                conn.execute(
                    "CREATE TABLE SessionTable ("
                    "username TEXT, last_timestamp INTEGER, "
                    "summary TEXT, display_name TEXT)"
                )
                conn.execute(
                    "INSERT INTO SessionTable VALUES (?, ?, ?, ?)",
                    (username, 1700000300, "s", "Self"),
                )
                conn.commit()
            finally:
                conn.close()

            conn = sqlite3.connect(os.path.join(message_dir, "message_0.db"))
            try:
                conn.execute(
                    f"CREATE TABLE {table_name} ("
                    "local_id INTEGER, server_id INTEGER, local_type INTEGER, "
                    "sort_seq INTEGER, real_sender_id INTEGER, create_time INTEGER, "
                    "message_content TEXT, compress_content BLOB, "
                    "packed_info_data BLOB, status INTEGER)"
                )
                for local_id, create_time, content in [
                    (1, 1700000000, "before"),
                    (2, 1700000500, "inside"),
                    (3, 1700001000, "after"),
                ]:
                    conn.execute(
                        f"INSERT INTO {table_name} VALUES (?, ?, ?, ?, ?, ?, ?, NULL, NULL, ?)",
                        (local_id, local_id, 1, local_id, 0, create_time, content, 0),
                    )
                conn.commit()
            finally:
                conn.close()

            messages, max_ts, _ = wechat_local_reader.read_messages(
                temp_dir,
                start_timestamp=1700000100,
                end_timestamp=1700001000,
            )

            self.assertEqual(1, len(messages))
            self.assertEqual("inside", messages[0]["content"])
            self.assertEqual(1700000500, max_ts)

class WeChatCaptureCommandTests(unittest.TestCase):
    """capture 子命令的输出格式与增量偏移行为的测试集。"""

    def test_write_json_output_escapes_non_gbk_characters(self):
        stdout = io.StringIO()
        payload = {"messages": [{"content": "a\u2005b"}]}

        with mock.patch.object(wechat_local_reader.sys, "stdout", stdout):
            wechat_local_reader.write_json_output(payload)

        output = stdout.getvalue()
        self.assertIn("\\u2005", output)
        self.assertNotIn("\u2005", output.replace("\\u2005", ""))
        self.assertEqual(payload, json.loads(output))
    def test_read_offset_uses_environment_when_present(self):
        old = os.environ.get("WECHAT_DASHBOARD_OFFSET")
        try:
            os.environ["WECHAT_DASHBOARD_OFFSET"] = "1700001234"
            self.assertEqual(
                1700001234,
                wechat_local_reader.read_offset(300),
            )
        finally:
            if old is None:
                os.environ.pop("WECHAT_DASHBOARD_OFFSET", None)
            else:
                os.environ["WECHAT_DASHBOARD_OFFSET"] = old

    def test_read_offset_falls_back_to_lookback(self):
        old = os.environ.pop("WECHAT_DASHBOARD_OFFSET", None)
        try:
            offset = wechat_local_reader.read_offset(300)
            # Should be roughly now - 300, allow small skew.
            import time as _time

            self.assertAlmostEqual(
                _time.time() - 300,
                offset,
                delta=10,
            )
        finally:
            if old is not None:
                os.environ["WECHAT_DASHBOARD_OFFSET"] = old


if __name__ == "__main__":
    unittest.main()
