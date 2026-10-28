"""Key checksum, the same as Lakeflow.Contracts.KeyChecksum (C#)."""
import hashlib

from pyspark.sql import Column
from pyspark.sql import functions as F

MOD = 2**64


def key_hash(key_id: str) -> int:
    """First 8 bytes (big-endian) of SHA-256 of the key's canonical text."""
    return int(hashlib.sha256(key_id.encode("utf-8")).hexdigest()[:16], 16)


def key_checksum(key_ids) -> int:
    """Sum of key hashes mod 2^64: order does not matter."""
    return sum(key_hash(k) for k in key_ids) % MOD


def key_hash_column(key_id: Column) -> Column:
    """key_hash as a Spark expression (decimal(20,0), as values reach 2^64)."""
    return F.conv(F.substring(F.sha2(key_id, 256), 1, 16), 16, 10).cast("decimal(20,0)")


def checksum_text(total) -> str:
    """Sum (any size) reduced mod 2^64, as text like the agent sends it."""
    return str(int(total or 0) % MOD)
