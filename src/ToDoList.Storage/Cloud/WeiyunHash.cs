using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;

namespace ToDoList.Storage.Cloud;

public sealed record WeiyunHashes(string FileSha, string FileMd5, List<string> BlockShaList, string CheckSha, string CheckData);
public static class WeiyunHash
{
    public const int BlockSize = 524288;
    public static WeiyunHashes Compute(byte[] bytes)
    {
        if (bytes.Length == 0) throw new InvalidDataException("不能上传空的云存档对象。");
        uint[] h = [0x67452301, 0xefcdab89, 0x98badcfe, 0x10325476, 0xc3d2e1f0];
        var last = bytes.Length % BlockSize; if (last == 0) last = BlockSize;
        var check = last % 128; if (check == 0) check = 128;
        var blocks = new List<string>();
        for (var offset = 0; offset < bytes.Length - check; offset += 64)
        {
            Compress(bytes.AsSpan(offset, 64), h);
            if ((offset + 64) % BlockSize == 0 && offset + 64 <= bytes.Length - last) blocks.Add(State(h));
        }
        var sha = Convert.ToHexStringLower(SHA1.HashData(bytes)); blocks.Add(sha);
        return new(sha, Convert.ToHexStringLower(MD5.HashData(bytes)), blocks, State(h), Convert.ToBase64String(bytes, bytes.Length - check, check));
    }
    private static string State(uint[] h)
    {
        Span<byte> bytes = stackalloc byte[20];
        for (var i = 0; i < 5; i++) BinaryPrimitives.WriteUInt32LittleEndian(bytes.Slice(i * 4, 4), h[i]);
        return Convert.ToHexStringLower(bytes);
    }
    private static void Compress(ReadOnlySpan<byte> block, uint[] h)
    {
        Span<uint> w = stackalloc uint[80];
        for (var i = 0; i < 16; i++) w[i] = BinaryPrimitives.ReadUInt32BigEndian(block.Slice(i * 4, 4));
        for (var i = 16; i < 80; i++) w[i] = BitOperations.RotateLeft(w[i - 3] ^ w[i - 8] ^ w[i - 14] ^ w[i - 16], 1);
        uint a = h[0], b = h[1], c = h[2], d = h[3], e = h[4];
        unchecked
        {
            for (var i = 0; i < 80; i++)
            {
                var f = i < 20 ? (b & c) | (~b & d) : i < 40 ? b ^ c ^ d : i < 60 ? (b & c) | (b & d) | (c & d) : b ^ c ^ d;
                var k = i < 20 ? 0x5a827999u : i < 40 ? 0x6ed9eba1u : i < 60 ? 0x8f1bbcdcu : 0xca62c1d6u;
                var temp = BitOperations.RotateLeft(a, 5) + f + e + k + w[i]; e = d; d = c; c = BitOperations.RotateLeft(b, 30); b = a; a = temp;
            }
            h[0] += a; h[1] += b; h[2] += c; h[3] += d; h[4] += e;
        }
    }
}
