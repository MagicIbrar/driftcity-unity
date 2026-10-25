// Binary reader for Gamebryo NIF files (targets 20.0.0.4, user version 0, as shipped
// with Drift City). Pure C#, no engine dependencies.
using System;
using System.IO;
using System.Text;

namespace DriftCity.Core.Nif
{
    public struct Vec2 { public float X, Y; public Vec2(float x, float y) { X = x; Y = y; } }
    public struct Vec3 { public float X, Y, Z; public Vec3(float x, float y, float z) { X = x; Y = y; Z = z; } }
    public struct Quat { public float W, X, Y, Z; }
    public struct Color4 { public float R, G, B, A; public Color4(float r, float g, float b, float a) { R = r; G = g; B = b; A = a; } }

    /// <summary>3x3 rotation, row-major as stored by Gamebryo (v' = M * v).</summary>
    public struct Mat3
    {
        public float M00, M01, M02, M10, M11, M12, M20, M21, M22;
        public static Mat3 Identity => new Mat3 { M00 = 1, M11 = 1, M22 = 1 };
    }

    public sealed class NifReader
    {
        readonly byte[] d;
        public int Pos;
        public uint Version;
        public uint UserVersion;

        public NifReader(byte[] data) { d = data; }
        public int Length => d.Length;
        public bool AtEnd => Pos >= d.Length;

        void Need(int n) { if (Pos + n > d.Length) throw new EndOfStreamException($"NIF read past end at 0x{Pos:x}"); }
        public byte U8() { Need(1); return d[Pos++]; }
        public bool Bool() => U8() != 0;
        public ushort U16() { Need(2); var v = BitConverter.ToUInt16(d, Pos); Pos += 2; return v; }
        public short I16() { Need(2); var v = BitConverter.ToInt16(d, Pos); Pos += 2; return v; }
        public uint U32() { Need(4); var v = BitConverter.ToUInt32(d, Pos); Pos += 4; return v; }
        public int I32() { Need(4); var v = BitConverter.ToInt32(d, Pos); Pos += 4; return v; }
        public float F32() { Need(4); var v = BitConverter.ToSingle(d, Pos); Pos += 4; return v; }
        public int Ref() => I32();
        public Vec2 V2() => new Vec2(F32(), F32());
        public Vec3 V3() => new Vec3(F32(), F32(), F32());
        public Color4 C3() => new Color4(F32(), F32(), F32(), 1f);
        public Color4 C4() => new Color4(F32(), F32(), F32(), F32());
        public Quat Q() => new Quat { W = F32(), X = F32(), Y = F32(), Z = F32() };

        public Mat3 M33() => new Mat3
        {
            M00 = F32(), M01 = F32(), M02 = F32(),
            M10 = F32(), M11 = F32(), M12 = F32(),
            M20 = F32(), M21 = F32(), M22 = F32(),
        };

        public string Str()
        {
            uint n = U32();
            if (n > 0x100000) throw new InvalidDataException($"string length {n} at 0x{Pos - 4:x}");
            Need((int)n);
            var s = Latin1.Decode(d, Pos, (int)n);
            Pos += (int)n;
            return s;
        }

        public string Line()
        {
            int start = Pos;
            while (Pos < d.Length && d[Pos] != 0x0A) Pos++;
            var s = Latin1.Decode(d, start, Pos - start);
            Pos++;
            return s;
        }

        public byte[] Bytes(int n) { Need(n); var b = new byte[n]; Buffer.BlockCopy(d, Pos, b, 0, n); Pos += n; return b; }
        public void Skip(int n) { Need(n); Pos += n; }

        public int[] Refs() { uint n = Count(); var r = new int[n]; for (int i = 0; i < n; i++) r[i] = I32(); return r; }
        public uint Count(uint limit = 1u << 24)
        {
            uint n = U32();
            if (n > limit) throw new InvalidDataException($"implausible count {n} at 0x{Pos - 4:x}");
            return n;
        }

        public float[] Floats(int n) { var r = new float[n]; for (int i = 0; i < n; i++) r[i] = F32(); return r; }
        public Vec3[] V3s(int n) { var r = new Vec3[n]; for (int i = 0; i < n; i++) r[i] = V3(); return r; }
        public ushort[] U16s(int n) { var r = new ushort[n]; for (int i = 0; i < n; i++) r[i] = U16(); return r; }
    }
}
