// Reader for Drift City "NayaPack" archives (.agt, Patch.###).
//
// Layout (reverse engineered from DriftCity.exe 0x7cc280 / 0x7cc8f0 / 0x7cb5d0):
//   0x00  char[8]  "NayaPack", then 0x00 x4
//   0x0C  u16 1, u16 1
//   0x10  u32 entryCount
//   0x20  entry table, then file data
// Everything after the 32-byte header is XORed with a 90-byte key indexed by
// (absolute file position % 90).
//   entry  = u32 dataOffset, u32 chunkCount, u32 size, u32 nameLength, char[nameLength] name
//   data   = u16 compressedSize[chunkCount], then chunkCount zlib streams that
//            each inflate to 16 KiB (the last one to the remainder)
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace DriftCity.Core
{
    public sealed class NayaPack : IDisposable
    {
        const int ChunkSize = 0x4000;

        static readonly byte[] Key = Encoding.ASCII.GetBytes(
            "abSSgLNw5ER4pUAqBrcYMsC44NMNuhvzaVBk79YGe4Ms6FUYyYzfy8sKwhLC39QfvupxteRbZ5JDAGm64A4w9kWD8A");

        public struct Entry
        {
            public string Name;
            public uint Offset;
            public uint ChunkCount;
            public uint Size;
        }

        readonly FileStream stream;
        readonly object gate = new object();
        public string Path { get; }
        public Dictionary<string, Entry> Entries { get; } = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);

        public NayaPack(string path)
        {
            Path = path;
            stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
            var header = new byte[0x20];
            ReadExactly(header, 0, header.Length);
            if (Encoding.ASCII.GetString(header, 0, 8) != "NayaPack")
                throw new InvalidDataException($"{path}: not a NayaPack archive");
            uint count = BitConverter.ToUInt32(header, 0x10);
            var fields = new byte[16];
            for (uint i = 0; i < count; i++)
            {
                ReadDecrypted(fields, 16);
                var e = new Entry
                {
                    Offset = BitConverter.ToUInt32(fields, 0),
                    ChunkCount = BitConverter.ToUInt32(fields, 4),
                    Size = BitConverter.ToUInt32(fields, 8),
                };
                int nameLength = (int)BitConverter.ToUInt32(fields, 12);
                var name = new byte[nameLength];
                ReadDecrypted(name, nameLength);
                e.Name = NormalizePath(Latin1.Decode(name, 0, name.Length));
                Entries[e.Name] = e;
            }
        }

        public static string NormalizePath(string p) => p.Replace('/', '\\').TrimStart('\\').ToLowerInvariant();

        public bool Contains(string name) => Entries.ContainsKey(NormalizePath(name));

        public byte[] Read(string name)
        {
            if (!Entries.TryGetValue(NormalizePath(name), out var e))
                throw new FileNotFoundException(name);
            return Read(e);
        }

        public byte[] Read(Entry e)
        {
            lock (gate)
            {
                var output = new byte[e.Size];
                stream.Position = e.Offset;
                var sizeTable = new byte[e.ChunkCount * 2];
                ReadDecrypted(sizeTable, sizeTable.Length);
                int written = 0;
                var block = new byte[ChunkSize + 64];
                for (int c = 0; c < e.ChunkCount; c++)
                {
                    int compressed = BitConverter.ToUInt16(sizeTable, c * 2);
                    int expected = Math.Min(ChunkSize, (int)e.Size - written);
                    if (block.Length < compressed) block = new byte[compressed];
                    ReadDecrypted(block, compressed);
                    bool isZlib = compressed >= 2 && block[0] == 0x78 && compressed != expected;
                    if (!isZlib)
                    {
                        Buffer.BlockCopy(block, 0, output, written, Math.Min(compressed, expected));
                    }
                    else
                    {
                        // skip the 2-byte zlib header; DeflateStream wants raw deflate
                        using (var ms = new MemoryStream(block, 2, compressed - 2))
                        using (var ds = new DeflateStream(ms, CompressionMode.Decompress))
                        {
                            int got = 0;
                            while (got < expected)
                            {
                                int n = ds.Read(output, written + got, expected - got);
                                if (n <= 0) break;
                                got += n;
                            }
                            if (got != expected)
                                throw new InvalidDataException($"{e.Name}: chunk {c} inflated to {got}, expected {expected}");
                        }
                    }
                    written += expected;
                }
                return output;
            }
        }

        void ReadDecrypted(byte[] buffer, int count)
        {
            long pos = stream.Position;
            ReadExactly(buffer, 0, count);
            for (int i = 0; i < count; i++)
                buffer[i] ^= Key[(int)((pos + i) % 90)];
        }

        void ReadExactly(byte[] buffer, int offset, int count)
        {
            while (count > 0)
            {
                int n = stream.Read(buffer, offset, count);
                if (n <= 0) throw new EndOfStreamException(Path);
                offset += n;
                count -= n;
            }
        }

        public void Dispose() => stream.Dispose();
    }
}
