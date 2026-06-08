import hashlib
import hmac
import struct
import unittest

import wechat_local_reader


class WeChatKeyExtractionTests(unittest.TestCase):
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


if __name__ == "__main__":
    unittest.main()
