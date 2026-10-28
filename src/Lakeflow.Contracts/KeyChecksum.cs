using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Lakeflow.Contracts;

/// <summary>
/// Order-independent checksum of a table's keys, computed the same way by the agent (over the source) and by the
/// Fabric reconciliation notebook (over silver): the sum, modulo 2^64, of the first 8 bytes (big-endian) of the
/// SHA-256 of each key's canonical text (<see cref="ColumnValues.KeyText"/>). In Spark:
/// <c>conv(substr(sha2(key_id, 256), 1, 16), 16, 10)</c>, summed, mod 2^64.
/// </summary>
public static class KeyChecksum
{
    public static ulong Of(string keyText) =>
        BinaryPrimitives.ReadUInt64BigEndian(SHA256.HashData(Encoding.UTF8.GetBytes(keyText)).AsSpan(0, 8));

    /// <summary>Running total over a stream of keys.</summary>
    public sealed class Accumulator
    {
        public long Count { get; private set; }
        public ulong Sum { get; private set; }

        public void Add(string keyText)
        {
            Count++;
            Sum = unchecked(Sum + Of(keyText));
        }
    }
}
