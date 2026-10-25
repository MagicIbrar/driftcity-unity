// CP949 (Korean, superset of EUC-KR) decoding without platform codepage support.
// The double-byte mapping table is the public Windows code page 949 table, generated
// once from .NET's CodePagesEncodingProvider into cp949.bin (pairs of u16 code, u16 char).
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace DriftCity.Core
{
    public static class Cp949
    {
        static Dictionary<ushort, char> table;

        public static bool Loaded => table != null;

        public static void LoadTable(byte[] data)
        {
            var t = new Dictionary<ushort, char>(data.Length / 4);
            for (int i = 0; i + 3 < data.Length; i += 4)
                t[BitConverter.ToUInt16(data, i)] = (char)BitConverter.ToUInt16(data, i + 2);
            table = t;
        }

        public static string Decode(byte[] data, int offset, int count)
        {
            var sb = new StringBuilder(count);
            int end = offset + count;
            for (int i = offset; i < end; i++)
            {
                byte b = data[i];
                if (b < 0x80) { sb.Append((char)b); continue; }
                if (i + 1 < end && table != null)
                {
                    ushort code = (ushort)((b << 8) | data[i + 1]);
                    if (table.TryGetValue(code, out char c)) { sb.Append(c); i++; continue; }
                }
                sb.Append('?');
            }
            return sb.ToString();
        }

        public static string Decode(byte[] data) => Decode(data, 0, data.Length);
    }
}
