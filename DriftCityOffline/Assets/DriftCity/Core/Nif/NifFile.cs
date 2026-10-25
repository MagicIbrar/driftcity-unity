using System;
using System.Collections.Generic;
using System.IO;

namespace DriftCity.Core.Nif
{
    public sealed class NifFile
    {
        public string Header;
        public uint Version, UserVersion;
        public NiObject[] Blocks;
        public string[] BlockTypes;
        public int[] Roots = new int[0];
        /// <summary>Null when every block parsed and the footer landed exactly on end of file.</summary>
        public string Error;
        public int ParsedBlocks;

        public T Get<T>(int index) where T : NiObject =>
            index >= 0 && index < Blocks.Length ? Blocks[index] as T : null;

        public static NifFile Parse(byte[] data)
        {
            var nif = new NifFile();
            var r = new NifReader(data);
            nif.Header = r.Line();
            if (!nif.Header.StartsWith("Gamebryo File Format") && !nif.Header.StartsWith("NetImmerse File Format"))
                throw new InvalidDataException("not a NIF: " + nif.Header);
            nif.Version = r.Version = r.U32();
            if (nif.Version != 0x14000004 && nif.Version != 0x14000005)
                throw new NotSupportedException($"NIF version 0x{nif.Version:x8} not supported");
            r.U8(); // endianness
            nif.UserVersion = r.UserVersion = r.U32();
            uint numBlocks = r.Count(1 << 20);
            if (nif.UserVersion >= 3)
            {
                // Bethesda-style export info; not used by Drift City but harmless to skip
                r.U32(); ShortString(r); ShortString(r); ShortString(r);
            }
            int numTypes = r.U16();
            nif.BlockTypes = new string[numTypes];
            for (int i = 0; i < numTypes; i++) nif.BlockTypes[i] = r.Str();
            var typeIndex = r.U16s((int)numBlocks);
            uint groups = r.U32();
            r.Skip((int)groups * 4);

            nif.Blocks = new NiObject[numBlocks];
            for (int i = 0; i < numBlocks; i++)
            {
                string type = nif.BlockTypes[typeIndex[i] & 0x7FFF];
                var block = NifTypes.Create(type);
                if (block == null)
                {
                    nif.Error = $"unsupported block {type} (#{i}) at 0x{r.Pos:x}";
                    return nif;
                }
                block.Index = i;
                block.TypeName = type;
                int start = r.Pos;
                try { block.Read(r); }
                catch (Exception ex)
                {
                    nif.Error = $"{type} (#{i}) at 0x{start:x}: {ex.Message}";
                    return nif;
                }
                nif.Blocks[i] = block;
                nif.ParsedBlocks = i + 1;
            }

            try
            {
                uint roots = r.Count(numBlocks + 1);
                nif.Roots = new int[roots];
                for (int i = 0; i < roots; i++) nif.Roots[i] = r.I32();
                // some files are zero-padded up to the archive's 16 KiB chunk size
                int footerEnd = r.Pos;
                for (int p = footerEnd; p < data.Length; p++)
                    if (data[p] != 0)
                    {
                        nif.Error = $"footer ended at 0x{footerEnd:x}, file is 0x{data.Length:x} (misparsed block?)";
                        break;
                    }
            }
            catch (Exception ex) { nif.Error = "footer: " + ex.Message; }
            return nif;
        }

        static void ShortString(NifReader r) { int n = r.U8(); r.Skip(n); }

        public IEnumerable<NiAVObject> RootObjects()
        {
            foreach (var i in Roots)
                if (Get<NiAVObject>(i) is NiAVObject o) yield return o;
            if (Roots.Length == 0 && Blocks.Length > 0 && Blocks[0] is NiAVObject first) yield return first;
        }
    }
}
