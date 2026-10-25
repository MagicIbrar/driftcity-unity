// Drift City UI texture bundles (Data\GameUI\uitex.agt -> tex\*.ntx).
// Each entry: char[64] name, u32 size, u32 reserved, then a DDS of that size.
// Entry names are the exact image references used by game.nui, e.g.
// "595,303~742,358_ui_button19.dds" (a pre-cut region of the original atlas).
using System;
using System.Collections.Generic;
using System.Text;

namespace DriftCity.Core
{
    public sealed class NtxBundle
    {
        struct Slot { public int File; public int Offset; public int Size; }

        readonly List<byte[]> files = new List<byte[]>();
        readonly Dictionary<string, Slot> slots = new Dictionary<string, Slot>(StringComparer.OrdinalIgnoreCase);

        public int Count => slots.Count;
        public IEnumerable<string> Names => slots.Keys;

        public void Add(byte[] ntx)
        {
            int fileIndex = files.Count;
            files.Add(ntx);
            int p = 0;
            while (p + 72 <= ntx.Length)
            {
                int nameLen = 0;
                while (nameLen < 64 && ntx[p + nameLen] != 0) nameLen++;
                string name = Encoding.ASCII.GetString(ntx, p, nameLen);
                int size = BitConverter.ToInt32(ntx, p + 64);
                int data = p + 72;
                if (size <= 0 || data + size > ntx.Length) break;
                slots[name] = new Slot { File = fileIndex, Offset = data, Size = size };
                p = data + size;
            }
        }

        public bool Contains(string name) => slots.ContainsKey(name);

        public byte[] Get(string name)
        {
            if (!slots.TryGetValue(name, out var s)) return null;
            var d = new byte[s.Size];
            Buffer.BlockCopy(files[s.File], s.Offset, d, 0, s.Size);
            return d;
        }

        public static NtxBundle LoadFrom(GameFileSystem fs)
        {
            var b = new NtxBundle();
            foreach (var path in fs.List("tex\\"))
                if (path.EndsWith(".ntx", StringComparison.OrdinalIgnoreCase)) b.Add(fs.Read(path));
            return b;
        }
    }
}
