// NIF block definitions for version 20.0.0.4 (Drift City / Gamebryo 2.0).
// Field layouts follow the public NifTools format description; only data the
// Unity converter uses is kept, everything else is read and discarded so the
// stream stays aligned (20.0.0.4 has no block-size table).
using System;
using System.Collections.Generic;
using System.IO;

namespace DriftCity.Core.Nif
{
    public abstract class NiObject
    {
        public int Index;
        public string TypeName;
        public abstract void Read(NifReader r);
    }

    public class NiObjectNET : NiObject
    {
        public string Name;
        public int[] ExtraData;
        public int Controller;
        public override void Read(NifReader r)
        {
            Name = r.Str();
            ExtraData = r.Refs();
            Controller = r.Ref();
        }
    }

    public class NiAVObject : NiObjectNET
    {
        public ushort Flags;
        public Vec3 Translation;
        public Mat3 Rotation;
        public float Scale;
        public int[] Properties;
        public int Collision;
        public bool Hidden => (Flags & 1) != 0;
        public override void Read(NifReader r)
        {
            base.Read(r);
            Flags = r.U16();
            Translation = r.V3();
            Rotation = r.M33();
            Scale = r.F32();
            Properties = r.Refs();
            Collision = r.Ref();
        }
    }

    public class NiNode : NiAVObject
    {
        public int[] Children;
        public int[] Effects;
        public override void Read(NifReader r)
        {
            base.Read(r);
            Children = r.Refs();
            Effects = r.Refs();
        }
    }

    public class NiBillboardNode : NiNode
    {
        public ushort Mode;
        public override void Read(NifReader r) { base.Read(r); Mode = r.U16(); }
    }

    public class NiSwitchNode : NiNode
    {
        public ushort SwitchFlags;
        public int ActiveIndex;
        public override void Read(NifReader r) { base.Read(r); SwitchFlags = r.U16(); ActiveIndex = r.I32(); }
    }

    public class NiLODNode : NiSwitchNode
    {
        public int LodData;
        public override void Read(NifReader r) { base.Read(r); LodData = r.Ref(); }
    }

    public class NiRangeLODData : NiObject
    {
        public Vec3 Center;
        public Vec2[] Ranges; // (near, far)
        public override void Read(NifReader r)
        {
            Center = r.V3();
            uint n = r.Count(1024);
            Ranges = new Vec2[n];
            for (int i = 0; i < n; i++) Ranges[i] = r.V2();
        }
    }

    public class NiGeometry : NiAVObject
    {
        public int Data;
        public int SkinInstance;
        public string ShaderName;
        public override void Read(NifReader r)
        {
            base.Read(r);
            Data = r.Ref();
            SkinInstance = r.Ref();
            if (r.Bool()) { ShaderName = r.Str(); r.I32(); }
        }
    }

    public class NiTriShape : NiGeometry { }
    public class NiTriStrips : NiGeometry { }

    public class NiGeometryData : NiObject
    {
        public int NumVertices;
        public Vec3[] Vertices, Normals;
        public Color4[] Colors;
        public Vec2[][] UVSets;
        public Vec3 Center;
        public float Radius;
        public override void Read(NifReader r)
        {
            r.I32(); // group id
            NumVertices = r.U16();
            r.U8(); r.U8(); // keep / compress flags
            if (r.Bool()) Vertices = r.V3s(NumVertices);
            ushort dataFlags = r.U16();
            if (r.Bool())
            {
                Normals = r.V3s(NumVertices);
                if ((dataFlags & 0xF000) != 0) { r.Skip(NumVertices * 24); } // tangents + bitangents
            }
            Center = r.V3();
            Radius = r.F32();
            if (r.Bool())
            {
                Colors = new Color4[NumVertices];
                for (int i = 0; i < NumVertices; i++) Colors[i] = r.C4();
            }
            int uvSets = dataFlags & 0x3F;
            UVSets = new Vec2[uvSets][];
            for (int s = 0; s < uvSets; s++)
            {
                var uv = new Vec2[NumVertices];
                for (int i = 0; i < NumVertices; i++) uv[i] = r.V2();
                UVSets[s] = uv;
            }
            r.U16(); // consistency flags
            r.Ref(); // additional data
        }
    }

    public class NiTriShapeData : NiGeometryData
    {
        public ushort[] Triangles; // 3 per triangle
        public override void Read(NifReader r)
        {
            base.Read(r);
            int numTris = r.U16();
            r.U32(); // num triangle points
            bool has = r.Bool();
            Triangles = has ? r.U16s(numTris * 3) : new ushort[0];
            int groups = r.U16();
            for (int g = 0; g < groups; g++) { int n = r.U16(); r.Skip(n * 2); }
        }
    }

    public class NiTriStripsData : NiGeometryData
    {
        public ushort[][] Strips;
        public override void Read(NifReader r)
        {
            base.Read(r);
            r.U16(); // num triangles
            int numStrips = r.U16();
            var lengths = r.U16s(numStrips);
            bool has = r.Bool();
            Strips = new ushort[numStrips][];
            for (int s = 0; s < numStrips; s++)
                Strips[s] = has ? r.U16s(lengths[s]) : new ushort[0];
        }

        public ushort[] ToTriangles()
        {
            var list = new List<ushort>();
            foreach (var s in Strips)
                for (int i = 2; i < s.Length; i++)
                {
                    ushort a = s[i - 2], b = s[i - 1], c = s[i];
                    if (a == b || b == c || a == c) continue;
                    if ((i & 1) == 0) { list.Add(a); list.Add(b); list.Add(c); }
                    else { list.Add(a); list.Add(c); list.Add(b); }
                }
            return list.ToArray();
        }
    }

    // ---------- particles (parsed for alignment, not rendered yet) ----------
    public class NiParticleSystem : NiGeometry
    {
        public bool WorldSpace;
        public int[] Modifiers;
        public override void Read(NifReader r) { base.Read(r); WorldSpace = r.Bool(); Modifiers = r.Refs(); }
    }

    public class NiParticlesData : NiGeometryData
    {
        public override void Read(NifReader r)
        {
            base.Read(r);
            int n = NumVertices;
            if (r.Bool()) r.Skip(n * 4);   // radii
            r.U16();                        // num active
            if (r.Bool()) r.Skip(n * 4);   // sizes
            if (r.Bool()) r.Skip(n * 16);  // rotations
            if (r.Bool()) r.Skip(n * 4);   // rotation angles
            if (r.Bool()) r.Skip(n * 12);  // rotation axes
        }
    }

    public class NiPSysData : NiParticlesData
    {
        public override void Read(NifReader r)
        {
            base.Read(r);
            r.Skip(NumVertices * (12 + 4 * 3 + 2 * 2)); // particle info
            if (r.Bool()) r.Skip(NumVertices * 4);       // rotation speeds
            r.U16(); r.U16();                             // added particles
        }
    }

    public class NiPSysModifier : NiObject
    {
        public string Name;
        public override void Read(NifReader r) { Name = r.Str(); r.U32(); r.Ref(); r.Bool(); }
    }
    public class NiPSysAgeDeathModifier : NiPSysModifier { public override void Read(NifReader r) { base.Read(r); r.Bool(); r.Ref(); } }
    public class NiPSysSpawnModifier : NiPSysModifier
    {
        public override void Read(NifReader r) { base.Read(r); r.U16(); r.F32(); r.U16(); r.U16(); r.F32(); r.F32(); r.F32(); r.F32(); }
    }
    public class NiPSysRotationModifier : NiPSysModifier
    {
        public override void Read(NifReader r) { base.Read(r); r.F32(); r.F32(); r.F32(); r.F32(); r.Bool(); r.Bool(); r.V3(); }
    }
    public class NiPSysGravityModifier : NiPSysModifier
    {
        public override void Read(NifReader r) { base.Read(r); r.Ref(); r.V3(); r.F32(); r.F32(); r.U32(); r.F32(); r.F32(); }
    }
    public class NiPSysPositionModifier : NiPSysModifier { }
    public class NiPSysGrowFadeModifier : NiPSysModifier
    {
        public override void Read(NifReader r) { base.Read(r); r.F32(); r.U16(); r.F32(); r.U16(); }
    }
    public class NiPSysBoundUpdateModifier : NiPSysModifier { public override void Read(NifReader r) { base.Read(r); r.U16(); } }
    public class NiPSysColorModifier : NiPSysModifier { public override void Read(NifReader r) { base.Read(r); r.Ref(); } }
    public class NiPSysDragModifier : NiPSysModifier
    {
        public override void Read(NifReader r) { base.Read(r); r.Ref(); r.V3(); r.F32(); r.F32(); r.F32(); }
    }

    public class NiPSysEmitter : NiPSysModifier
    {
        public override void Read(NifReader r)
        {
            base.Read(r);
            r.Skip(6 * 4); // speed, var, declination, var, planar angle, var
            r.C4();        // initial color
            r.F32(); r.F32(); r.F32(); r.F32(); // radius, radius var, life span, life span var
        }
    }
    public class NiPSysVolumeEmitter : NiPSysEmitter { public override void Read(NifReader r) { base.Read(r); r.Ref(); } }
    public class NiPSysBoxEmitter : NiPSysVolumeEmitter { public override void Read(NifReader r) { base.Read(r); r.F32(); r.F32(); r.F32(); } }
    public class NiPSysSphereEmitter : NiPSysVolumeEmitter { public override void Read(NifReader r) { base.Read(r); r.F32(); } }
    public class NiPSysCylinderEmitter : NiPSysVolumeEmitter { public override void Read(NifReader r) { base.Read(r); r.F32(); r.F32(); } }
    public class NiPSysMeshEmitter : NiPSysEmitter
    {
        public override void Read(NifReader r) { base.Read(r); r.Refs(); r.U32(); r.U32(); r.V3(); }
    }

    // ---------- properties ----------
    public class NiProperty : NiObjectNET { }

    public class NiMaterialProperty : NiProperty
    {
        public Color4 Ambient, Diffuse, Specular, Emissive;
        public float Glossiness, Alpha;
        public override void Read(NifReader r)
        {
            base.Read(r);
            Ambient = r.C3(); Diffuse = r.C3(); Specular = r.C3(); Emissive = r.C3();
            Glossiness = r.F32(); Alpha = r.F32();
        }
    }

    public class NiAlphaProperty : NiProperty
    {
        public ushort Flags;
        public byte Threshold;
        public bool Blend => (Flags & 1) != 0;
        public bool Test => (Flags & 0x200) != 0;
        public int SrcBlend => (Flags >> 1) & 0xF;
        public int DstBlend => (Flags >> 5) & 0xF;
        public override void Read(NifReader r) { base.Read(r); Flags = r.U16(); Threshold = r.U8(); }
    }

    public class NiZBufferProperty : NiProperty
    {
        public ushort Flags;
        public uint Function;
        public bool Test => (Flags & 1) != 0;
        public bool Write => (Flags & 2) != 0;
        public override void Read(NifReader r) { base.Read(r); Flags = r.U16(); Function = r.U32(); }
    }

    public class NiVertexColorProperty : NiProperty
    {
        public ushort Flags;
        public uint VertexMode, LightingMode;
        public override void Read(NifReader r) { base.Read(r); Flags = r.U16(); VertexMode = r.U32(); LightingMode = r.U32(); }
    }

    public class NiStencilProperty : NiProperty
    {
        public uint DrawMode; // 0 = CCW/default (back-face cull), 1 = CCW, 2 = CW, 3 = both (no culling)
        public override void Read(NifReader r)
        {
            base.Read(r);
            r.U8(); r.U32(); r.U32(); r.U32(); r.U32(); r.U32(); r.U32();
            DrawMode = r.U32();
        }
    }

    public class NiFlagProperty : NiProperty
    {
        public ushort Flags;
        public override void Read(NifReader r) { base.Read(r); Flags = r.U16(); }
    }
    public class NiSpecularProperty : NiFlagProperty { }
    public class NiShadeProperty : NiFlagProperty { }
    public class NiWireframeProperty : NiFlagProperty { }
    public class NiDitherProperty : NiFlagProperty { }

    public class TexDesc
    {
        public int Source;
        public uint ClampMode, FilterMode, UVSet;
        public Vec2 Offset, Tiling = new Vec2(1, 1);
        public static TexDesc Read(NifReader r)
        {
            var t = new TexDesc { Source = r.Ref(), ClampMode = r.U32(), FilterMode = r.U32(), UVSet = r.U32() };
            if (r.Bool())
            {
                t.Offset = r.V2(); t.Tiling = r.V2(); r.F32(); r.U32(); r.V2();
            }
            return t;
        }
    }

    public class NiTexturingProperty : NiProperty
    {
        public uint ApplyMode;
        public TexDesc Base, Dark, Detail, Gloss, Glow, Bump;
        public TexDesc[] Decals = new TexDesc[4];
        /// <summary>Extra texture slots consumed by custom shaders (e.g. VCAlphaTextureBlender).</summary>
        public List<TexDesc> ShaderTextures = new List<TexDesc>();
        public override void Read(NifReader r)
        {
            base.Read(r);
            ApplyMode = r.U32();
            uint count = r.U32();
            if (r.Bool()) Base = TexDesc.Read(r);
            if (r.Bool()) Dark = TexDesc.Read(r);
            if (r.Bool()) Detail = TexDesc.Read(r);
            if (r.Bool()) Gloss = TexDesc.Read(r);
            if (r.Bool()) Glow = TexDesc.Read(r);
            if (r.Bool()) { Bump = TexDesc.Read(r); r.F32(); r.F32(); r.Skip(16); }
            for (int i = 0; i < 4; i++)
                if (count > 6 + i && r.Bool()) Decals[i] = TexDesc.Read(r);
            uint shaderTex = r.Count(64);
            for (int i = 0; i < shaderTex; i++)
            {
                TexDesc desc = null;
                if (r.Bool()) { desc = TexDesc.Read(r); r.U32(); } // map id
                ShaderTextures.Add(desc);
            }
        }
    }

    public class NiSourceTexture : NiObjectNET
    {
        public bool External;
        public string FileName;
        public int PixelData;
        public override void Read(NifReader r)
        {
            base.Read(r);
            External = r.U8() != 0;
            FileName = r.Str();
            int link = r.Ref();
            PixelData = External ? -1 : link;
            r.U32(); r.U32(); r.U32(); // pixel layout, use mipmaps, alpha format
            r.U8();                    // is static
            r.Bool();                  // direct render
        }
    }

    public class NiPixelData : NiObject
    {
        public uint PixelFormat; // 0 RGB, 1 RGBA, 2 PAL, 3 PALA, 4 DXT1, 5 DXT3, 6 DXT5
        public byte BitsPerPixel;
        public uint BytesPerPixel;
        public int Palette;
        public (uint w, uint h, uint offset)[] Mips;
        public byte[] Pixels;
        public override void Read(NifReader r)
        {
            PixelFormat = r.U32();
            BitsPerPixel = r.U8();
            r.U32(); r.U32(); r.U8(); r.U32(); // renderer hint, extra data, flags, tiling
            for (int c = 0; c < 4; c++) { r.U32(); r.U32(); r.U8(); r.U8(); }
            Palette = r.Ref();
            uint mipCount = r.Count(32);
            BytesPerPixel = r.U32();
            Mips = new (uint, uint, uint)[mipCount];
            for (int i = 0; i < mipCount; i++) Mips[i] = (r.U32(), r.U32(), r.U32());
            uint numPixels = r.U32();
            uint faces = r.U32();
            Pixels = r.Bytes((int)(numPixels * faces));
        }
    }

    public class NiPalette : NiObject
    {
        public byte[] Rgba;
        public override void Read(NifReader r)
        {
            r.U8(); // has alpha
            uint n = r.Count(256);
            Rgba = r.Bytes((int)n * 4);
        }
    }

    // ---------- extra data ----------
    public class NiExtraData : NiObject
    {
        public string Name;
        public override void Read(NifReader r) { Name = r.Str(); }
    }
    public class NiStringExtraData : NiExtraData { public string Value; public override void Read(NifReader r) { base.Read(r); Value = r.Str(); } }
    public class NiIntegerExtraData : NiExtraData { public uint Value; public override void Read(NifReader r) { base.Read(r); Value = r.U32(); } }
    public class NiFloatExtraData : NiExtraData { public float Value; public override void Read(NifReader r) { base.Read(r); Value = r.F32(); } }
    public class NiBooleanExtraData : NiExtraData { public bool Value; public override void Read(NifReader r) { base.Read(r); Value = r.Bool(); } }
    public class NiColorExtraData : NiExtraData { public Color4 Value; public override void Read(NifReader r) { base.Read(r); Value = r.C4(); } }
    public class NiVectorExtraData : NiExtraData { public override void Read(NifReader r) { base.Read(r); r.Skip(16); } }
    public class NiFloatsExtraData : NiExtraData { public float[] Values; public override void Read(NifReader r) { base.Read(r); Values = r.Floats((int)r.Count()); } }
    public class NiIntegersExtraData : NiExtraData { public override void Read(NifReader r) { base.Read(r); r.Skip((int)r.Count() * 4); } }
    public class NiBinaryExtraData : NiExtraData { public byte[] Value; public override void Read(NifReader r) { base.Read(r); Value = r.Bytes((int)r.Count()); } }
    public class NiTextKeyExtraData : NiExtraData
    {
        public override void Read(NifReader r)
        {
            base.Read(r);
            uint n = r.Count();
            for (int i = 0; i < n; i++) { r.F32(); r.Str(); }
        }
    }

    // ---------- animation ----------
    public class NiTimeController : NiObject
    {
        public int Next;
        public ushort Flags;
        public float Frequency, Phase, StartTime, StopTime;
        public int Target;
        public override void Read(NifReader r)
        {
            Next = r.Ref(); Flags = r.U16();
            Frequency = r.F32(); Phase = r.F32(); StartTime = r.F32(); StopTime = r.F32();
            Target = r.Ref();
        }
    }
    public class NiSingleInterpController : NiTimeController
    {
        public int Interpolator;
        public override void Read(NifReader r) { base.Read(r); Interpolator = r.Ref(); }
    }
    public class NiTransformController : NiSingleInterpController { }
    public class NiAlphaController : NiSingleInterpController { }
    public class NiVisController : NiSingleInterpController { }
    public class NiMaterialColorController : NiSingleInterpController { public override void Read(NifReader r) { base.Read(r); r.U16(); } }
    public class NiFlipController : NiSingleInterpController
    {
        public uint TextureSlot;
        public int[] Sources;
        public override void Read(NifReader r) { base.Read(r); TextureSlot = r.U32(); Sources = r.Refs(); }
    }
    public class NiTextureTransformController : NiSingleInterpController
    {
        public override void Read(NifReader r) { base.Read(r); r.Bool(); r.U32(); r.U32(); }
    }
    public class NiGeomMorpherController : NiTimeController
    {
        public int Data;
        public int[] Interpolators;
        public override void Read(NifReader r)
        {
            base.Read(r);
            r.U16();          // morpher flags
            Data = r.Ref();
            r.U8();           // always update
            Interpolators = r.Refs();
        }
    }
    public class NiMultiTargetTransformController : NiTimeController
    {
        public override void Read(NifReader r) { base.Read(r); int n = r.U16(); r.Skip(n * 4); }
    }
    public class NiPSysModifierCtlr : NiSingleInterpController
    {
        public string ModifierName;
        public override void Read(NifReader r) { base.Read(r); ModifierName = r.Str(); }
    }
    public class NiPSysEmitterCtlr : NiPSysModifierCtlr { public override void Read(NifReader r) { base.Read(r); r.Ref(); } }
    public class NiPSysModifierActiveCtlr : NiPSysModifierCtlr { }
    public class NiPSysUpdateCtlr : NiTimeController { }
    public class NiControllerManager : NiTimeController
    {
        public override void Read(NifReader r) { base.Read(r); r.Bool(); r.Refs(); r.Ref(); }
    }
    public class NiControllerSequence : NiObject
    {
        /// <summary>One animated object: its interpolator and, through the string palette, the target node's name.</summary>
        public struct Block { public int Interpolator, Controller, Palette, NodeOffset, ControllerTypeOffset; }
        public string Name, AccumRootName;
        public Block[] Blocks = Array.Empty<Block>();
        public float Weight, Frequency, StartTime, StopTime;
        public uint CycleType;
        public int TextKeys, StringPalette;
        public override void Read(NifReader r)
        {
            Name = r.Str();
            uint blocks = r.Count(65536);
            r.U32(); // array grow by
            Blocks = new Block[blocks];
            for (int i = 0; i < blocks; i++)
            {
                var b = new Block { Interpolator = r.Ref(), Controller = r.Ref() };
                if (r.UserVersion >= 10) r.U8();   // priority: Bethesda files only (KF 20.0.0.4 files have none)
                b.Palette = r.Ref();
                b.NodeOffset = r.I32();
                r.I32();                          // property type offset
                b.ControllerTypeOffset = r.I32();
                r.I32(); r.I32();                 // controller id / interpolator id offsets
                Blocks[i] = b;
            }
            Weight = r.F32(); TextKeys = r.Ref(); CycleType = r.U32(); Frequency = r.F32(); StartTime = r.F32(); StopTime = r.F32();
            r.Ref(); AccumRootName = r.Str();     // manager, accum root name
            StringPalette = r.Ref();
        }
    }
    public class NiStringPalette : NiObject
    {
        public string Palette = "";
        public override void Read(NifReader r) { Palette = r.Str(); r.U32(); }
        /// <summary>The NUL-terminated name at a palette offset.</summary>
        public string At(int offset)
        {
            if (offset < 0 || offset >= Palette.Length) return null;
            int end = Palette.IndexOf((char)0, offset);
            return end < 0 ? Palette.Substring(offset) : Palette.Substring(offset, end - offset);
        }
    }
    public class NiDefaultAVObjectPalette : NiObject
    {
        public override void Read(NifReader r) { r.Ref(); uint n = r.Count(); for (int i = 0; i < n; i++) { r.Str(); r.Ref(); } }
    }

    public class NiFloatInterpolator : NiObject { public float Value; public int Data; public override void Read(NifReader r) { Value = r.F32(); Data = r.Ref(); } }
    public class NiBoolInterpolator : NiObject { public bool Value; public int Data; public override void Read(NifReader r) { Value = r.Bool(); Data = r.Ref(); } }
    public class NiPoint3Interpolator : NiObject { public override void Read(NifReader r) { r.V3(); r.Ref(); } }
    public class NiTransformInterpolator : NiObject
    {
        public Vec3 Translation; public Quat Rotation; public float Scale; public int Data;
        public override void Read(NifReader r) { Translation = r.V3(); Rotation = r.Q(); Scale = r.F32(); Data = r.Ref(); }
    }

    /// <summary>A key group: times and values (tangents and TBC parameters are read past; playback is linear).</summary>
    public sealed class KeyGroup<T>
    {
        public float[] Times = Array.Empty<float>();
        public T[] Values = Array.Empty<T>();
        public int Count => Times.Length;
    }

    static class Keys
    {
        /// <summary>One key group of n keys: interpolation type, then per key a time, the value and, for quadratic
        /// keys, two tangents of the value's size, or for TBC keys tension/bias/continuity.</summary>
        public static KeyGroup<T> Read<T>(NifReader r, int valueBytes, Func<NifReader, T> value)
        {
            var g = new KeyGroup<T>();
            uint n = r.Count();
            if (n == 0) return g;
            uint type = r.U32();
            g.Times = new float[n];
            g.Values = new T[n];
            for (int i = 0; i < n; i++)
            {
                g.Times[i] = r.F32();
                g.Values[i] = value(r);
                if (type == 2) r.Skip(2 * valueBytes);
                else if (type == 3) r.Skip(12);
            }
            return g;
        }

        public static void SkipGroup(NifReader r, int valueBytes) => Read(r, valueBytes, rr => { rr.Skip(valueBytes); return 0; });
    }
    public class NiFloatData : NiObject { public KeyGroup<float> Keys; public override void Read(NifReader r) => Keys = Nif.Keys.Read(r, 4, rr => rr.F32()); }
    public class NiBoolData : NiObject { public KeyGroup<float> Keys; public override void Read(NifReader r) => Keys = Nif.Keys.Read(r, 1, rr => rr.U8() != 0 ? 1f : 0f); }
    public class NiColorData : NiObject { public override void Read(NifReader r) => Keys.SkipGroup(r, 16); }
    public class NiPosData : NiObject { public override void Read(NifReader r) => Keys.SkipGroup(r, 12); }
    public class NiTransformData : NiObject
    {
        public KeyGroup<Quat> Rotations = new KeyGroup<Quat>();
        /// <summary>Rotation as separate X/Y/Z angle groups (rotation type 4) instead of quaternion keys.</summary>
        public KeyGroup<float>[] Euler;
        public KeyGroup<Vec3> Translations;
        public KeyGroup<float> Scales;
        public override void Read(NifReader r)
        {
            uint n = r.Count();
            if (n > 0)
            {
                uint type = r.U32();
                if (type == 4)
                {
                    Euler = new KeyGroup<float>[3];
                    for (int a = 0; a < 3; a++) Euler[a] = Keys.Read(r, 4, rr => rr.F32());
                }
                else
                {
                    // quaternion keys carry no tangents, even when quadratic
                    Rotations.Times = new float[n];
                    Rotations.Values = new Quat[n];
                    for (int i = 0; i < n; i++)
                    {
                        Rotations.Times[i] = r.F32();
                        Rotations.Values[i] = r.Q();
                        if (type == 3) r.Skip(12);
                    }
                }
            }
            Translations = Keys.Read(r, 12, rr => rr.V3());
            Scales = Keys.Read(r, 4, rr => rr.F32());
        }
    }
    public class NiMorphData : NiObject
    {
        public override void Read(NifReader r)
        {
            uint morphs = r.Count(4096);
            uint verts = r.Count();
            r.U8();
            for (int i = 0; i < morphs; i++) { r.Str(); r.F32(); r.Skip((int)verts * 12); }
        }
    }

    // ---------- compressed B-spline animation (the character .kf files) ----------
    public class NiBSplineInterpolator : NiObject
    {
        public float StartTime, StopTime;
        public int SplineData, BasisData;
        public override void Read(NifReader r) { StartTime = r.F32(); StopTime = r.F32(); SplineData = r.Ref(); BasisData = r.Ref(); }
    }
    /// <summary>Translation / rotation / scale as cubic B-splines; a handle of 0xFFFF(FFFF) means the channel is the
    /// constant transform instead.</summary>
    public class NiBSplineTransformInterpolator : NiBSplineInterpolator
    {
        public Vec3 Translation; public Quat Rotation; public float Scale;
        public uint TranslationHandle, RotationHandle, ScaleHandle;
        public override void Read(NifReader r)
        {
            base.Read(r);
            Translation = r.V3(); Rotation = r.Q(); Scale = r.F32();
            TranslationHandle = r.U32(); RotationHandle = r.U32(); ScaleHandle = r.U32();
        }
        public static bool Valid(uint handle) => handle != 0xFFFFu && handle != 0xFFFFFFFFu;
    }
    /// <summary>Control points stored as shorts: value = short / 32767 * half range + offset.</summary>
    public class NiBSplineCompTransformInterpolator : NiBSplineTransformInterpolator
    {
        public float TranslationOffset, TranslationHalfRange, RotationOffset, RotationHalfRange, ScaleOffset, ScaleHalfRange;
        public override void Read(NifReader r)
        {
            base.Read(r);
            TranslationOffset = r.F32(); TranslationHalfRange = r.F32();
            RotationOffset = r.F32(); RotationHalfRange = r.F32();
            ScaleOffset = r.F32(); ScaleHalfRange = r.F32();
        }
    }
    public class NiBSplineData : NiObject
    {
        public float[] Floats = Array.Empty<float>();
        public short[] Compact = Array.Empty<short>();
        public override void Read(NifReader r)
        {
            uint nf = r.Count(); Floats = new float[nf]; for (int i = 0; i < nf; i++) Floats[i] = r.F32();
            uint nc = r.Count(); Compact = new short[nc]; for (int i = 0; i < nc; i++) Compact[i] = (short)r.U16();
        }
    }
    public class NiBSplineBasisData : NiObject
    {
        public int ControlPoints;
        public override void Read(NifReader r) { ControlPoints = (int)r.U32(); }
    }

    /// <summary>Open uniform cubic B-spline evaluation (Gamebryo NiBSplineBasis degree 3).</summary>
    public static class BSpline
    {
        /// <summary>Samples <paramref name="dim"/> components of compressed control points starting at <paramref name="handle"/>
        /// at normalized time u (0..1).</summary>
        public static void Sample(short[] cp, uint handle, int count, int dim, float offset, float halfRange, float u, float[] result)
        {
            for (int k = 0; k < dim; k++) result[k] = 0;
            if (count <= 0) return;
            const int degree = 3;
            int n = count - 1;                         // last control point index
            if (count <= degree)
            {
                // too few points for a cubic: the nearest control point
                int i0 = Math.Min(n, (int)Math.Round(u * n));
                for (int k = 0; k < dim; k++) result[k] = Value(cp, handle, i0, dim, k, offset, halfRange);
                return;
            }
            float x = Math.Max(0, Math.Min(1, u)) * (count - degree);
            int span = Math.Min((int)x, count - degree - 1);   // segment index 0..count-4
            // knots: degree+1 zeros, 1..count-degree-1, degree+1 times (count-degree)
            float Knot(int j) => j <= degree ? 0 : j >= count ? count - degree : j - degree;
            int s = span + degree;                     // knot span index
            Span<float> d = stackalloc float[(degree + 1) * 4];
            for (int j = 0; j <= degree; j++)
                for (int k = 0; k < dim; k++) d[j * 4 + k] = Value(cp, handle, s - degree + j, dim, k, offset, halfRange);
            for (int rr = 1; rr <= degree; rr++)
                for (int j = degree; j >= rr; j--)
                {
                    int i = s - degree + j;
                    float den = Knot(i + degree - rr + 1) - Knot(i);
                    float a = den > 1e-6f ? (x - Knot(i)) / den : 0f;
                    for (int k = 0; k < dim; k++) d[j * 4 + k] = (1 - a) * d[(j - 1) * 4 + k] + a * d[j * 4 + k];
                }
            for (int k = 0; k < dim; k++) result[k] = d[degree * 4 + k];
        }

        static float Value(short[] cp, uint handle, int i, int dim, int k, float offset, float halfRange)
        {
            long idx = handle + (long)i * dim + k;
            if (idx < 0 || idx >= cp.Length) return offset;
            return cp[idx] / 32767f * halfRange + offset;
        }
    }

    // ---------- skinning ----------
    public class NiSkinInstance : NiObject
    {
        public int Data, Partition, SkeletonRoot;
        public int[] Bones;
        public override void Read(NifReader r) { Data = r.Ref(); Partition = r.Ref(); SkeletonRoot = r.Ref(); Bones = r.Refs(); }
    }
    public class NiSkinData : NiObject
    {
        /// <summary>Per bone: the skin-to-bone transform and the vertices it moves (index, weight).</summary>
        public sealed class Bone { public Mat3 Rotation; public Vec3 Translation; public float Scale; public ushort[] Vertices; public float[] Weights; }
        public Mat3 Rotation; public Vec3 Translation; public float Scale;
        public Bone[] Bones = Array.Empty<Bone>();
        public override void Read(NifReader r)
        {
            Rotation = r.M33(); Translation = r.V3(); Scale = r.F32();
            uint bones = r.Count(4096);
            bool hasWeights = r.Bool();
            Bones = new Bone[bones];
            for (int b = 0; b < bones; b++)
            {
                var bone = new Bone { Rotation = r.M33(), Translation = r.V3(), Scale = r.F32() };
                r.V3(); r.F32();   // bounding sphere
                int n = r.U16();
                bone.Vertices = new ushort[hasWeights ? n : 0];
                bone.Weights = new float[hasWeights ? n : 0];
                if (hasWeights) for (int k = 0; k < n; k++) { bone.Vertices[k] = (ushort)r.U16(); bone.Weights[k] = r.F32(); }
                Bones[b] = bone;
            }
        }
    }
    public class NiSkinPartition : NiObject
    {
        public override void Read(NifReader r)
        {
            uint parts = r.Count(4096);
            for (int p = 0; p < parts; p++)
            {
                int nv = r.U16(), nt = r.U16(), nb = r.U16(), ns = r.U16(), nw = r.U16();
                r.Skip(nb * 2);
                if (r.Bool()) r.Skip(nv * 2);
                if (r.Bool()) r.Skip(nv * nw * 4);
                var lens = r.U16s(ns);
                bool faces = r.Bool();
                if (faces && ns != 0) foreach (var l in lens) r.Skip(l * 2);
                else if (faces) r.Skip(nt * 6);
                if (r.Bool()) r.Skip(nv * nw);
            }
        }
    }

    // ---------- lights / camera ----------
    public class NiDynamicEffect : NiAVObject
    {
        public override void Read(NifReader r) { base.Read(r); r.Bool(); r.Refs(); }
    }
    public class NiLight : NiDynamicEffect
    {
        public float Dimmer; public Color4 Ambient, Diffuse, Specular;
        public override void Read(NifReader r) { base.Read(r); Dimmer = r.F32(); Ambient = r.C3(); Diffuse = r.C3(); Specular = r.C3(); }
    }
    public class NiAmbientLight : NiLight { }
    public class NiDirectionalLight : NiLight { }
    public class NiPointLight : NiLight { public override void Read(NifReader r) { base.Read(r); r.F32(); r.F32(); r.F32(); } }
    public class NiSpotLight : NiPointLight { public override void Read(NifReader r) { base.Read(r); r.F32(); r.F32(); } }
    public class NiCamera : NiAVObject
    {
        public override void Read(NifReader r)
        {
            base.Read(r);
            r.U16(); r.Skip(6 * 4); r.Bool(); r.Skip(4 * 4); r.F32(); r.Ref(); r.U32(); r.U32();
        }
    }

    public class NiSortAdjustNode : NiNode { public override void Read(NifReader r) { base.Read(r); r.U32(); } }
    public class NiSourceCubeMap : NiSourceTexture { }
    public class NiTextureEffect : NiDynamicEffect
    {
        public override void Read(NifReader r)
        {
            base.Read(r);
            r.M33(); r.V3();           // model projection
            r.U32(); r.U32(); r.U32(); r.U32(); // filtering, clamping, texture type, coordinate generation
            r.Ref();                   // source texture
            r.U8(); r.V3(); r.F32();   // clipping plane
        }
    }
    public class NiLookAtInterpolator : NiObject
    {
        public override void Read(NifReader r) { r.U16(); r.Ref(); r.Str(); r.V3(); r.Q(); r.F32(); r.Ref(); r.Ref(); r.Ref(); }
    }
    public class NiPathInterpolator : NiObject
    {
        public override void Read(NifReader r) { r.U16(); r.I32(); r.F32(); r.F32(); r.U16(); r.Ref(); r.Ref(); }
    }
    public class NiFloatExtraDataController : NiSingleInterpController { public override void Read(NifReader r) { base.Read(r); r.Str(); } }
    public class NiPSysResetOnLoopCtlr : NiTimeController { }
    public class NiCollisionData : NiObject
    {
        public override void Read(NifReader r)
        {
            r.Ref(); r.U32(); r.U32();
            if (r.U8() != 0) BoundingVolume(r);
        }
        static void BoundingVolume(NifReader r)
        {
            switch (r.U32())
            {
                case 0: r.V3(); r.F32(); break;                         // sphere
                case 1: r.V3(); r.Skip(36); r.Skip(12); break;          // box
                case 2: r.V3(); r.V3(); r.F32(); r.F32(); break;        // capsule
                case 4: { uint n = r.Count(256); for (int i = 0; i < n; i++) BoundingVolume(r); break; }
                case 5: r.V3(); r.F32(); r.V3(); break;                 // half space
                default: break;
            }
        }
    }

    public static class NifTypes
    {
        static readonly Dictionary<string, Func<NiObject>> factories = new Dictionary<string, Func<NiObject>>();
        static void Add<T>() where T : NiObject, new() => factories[typeof(T).Name] = () => new T();

        static NifTypes()
        {
            Add<NiNode>(); Add<NiBillboardNode>(); Add<NiSwitchNode>(); Add<NiLODNode>(); Add<NiRangeLODData>();
            Add<NiTriShape>(); Add<NiTriStrips>(); Add<NiTriShapeData>(); Add<NiTriStripsData>();
            Add<NiParticleSystem>(); Add<NiPSysData>(); Add<NiParticlesData>();
            Add<NiPSysAgeDeathModifier>(); Add<NiPSysSpawnModifier>(); Add<NiPSysRotationModifier>();
            Add<NiPSysGravityModifier>(); Add<NiPSysPositionModifier>(); Add<NiPSysGrowFadeModifier>();
            Add<NiPSysBoundUpdateModifier>(); Add<NiPSysColorModifier>(); Add<NiPSysDragModifier>();
            Add<NiPSysBoxEmitter>(); Add<NiPSysSphereEmitter>(); Add<NiPSysCylinderEmitter>(); Add<NiPSysMeshEmitter>();
            Add<NiMaterialProperty>(); Add<NiAlphaProperty>(); Add<NiZBufferProperty>(); Add<NiVertexColorProperty>();
            Add<NiStencilProperty>(); Add<NiSpecularProperty>(); Add<NiShadeProperty>(); Add<NiWireframeProperty>(); Add<NiDitherProperty>();
            Add<NiTexturingProperty>(); Add<NiSourceTexture>(); Add<NiPixelData>(); Add<NiPalette>();
            Add<NiStringExtraData>(); Add<NiIntegerExtraData>(); Add<NiFloatExtraData>(); Add<NiBooleanExtraData>();
            Add<NiColorExtraData>(); Add<NiVectorExtraData>(); Add<NiFloatsExtraData>(); Add<NiIntegersExtraData>();
            Add<NiBinaryExtraData>(); Add<NiTextKeyExtraData>();
            Add<NiTransformController>(); Add<NiAlphaController>(); Add<NiVisController>(); Add<NiMaterialColorController>();
            Add<NiFlipController>(); Add<NiTextureTransformController>(); Add<NiGeomMorpherController>();
            Add<NiMultiTargetTransformController>(); Add<NiPSysEmitterCtlr>(); Add<NiPSysModifierActiveCtlr>(); Add<NiPSysUpdateCtlr>();
            Add<NiControllerManager>(); Add<NiControllerSequence>(); Add<NiStringPalette>(); Add<NiDefaultAVObjectPalette>();
            Add<NiFloatInterpolator>(); Add<NiBoolInterpolator>(); Add<NiPoint3Interpolator>(); Add<NiTransformInterpolator>();
            Add<NiFloatData>(); Add<NiBoolData>(); Add<NiColorData>(); Add<NiPosData>(); Add<NiTransformData>(); Add<NiMorphData>();
            Add<NiSkinInstance>(); Add<NiSkinData>(); Add<NiSkinPartition>();
            Add<NiBSplineCompTransformInterpolator>(); Add<NiBSplineTransformInterpolator>(); Add<NiBSplineData>(); Add<NiBSplineBasisData>();
            Add<NiAmbientLight>(); Add<NiDirectionalLight>(); Add<NiPointLight>(); Add<NiSpotLight>(); Add<NiCamera>();
            Add<NiSortAdjustNode>(); Add<NiSourceCubeMap>(); Add<NiTextureEffect>(); Add<NiLookAtInterpolator>();
            Add<NiPathInterpolator>(); Add<NiFloatExtraDataController>(); Add<NiPSysResetOnLoopCtlr>(); Add<NiCollisionData>();
        }

        public static NiObject Create(string type) => factories.TryGetValue(type, out var f) ? f() : null;
    }
}
