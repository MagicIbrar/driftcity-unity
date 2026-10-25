// Minimal DDS reader. DXT1/DXT5 are kept as raw blocks (GPU-uploadable);
// DXT3 and uncompressed layouts are decoded to RGBA32. Rows stay in DDS
// (top-down) order; callers flip V instead of the pixel data.
using System;
using System.IO;

namespace DriftCity.Core
{
    public enum PixelFormat { RGBA32, DXT1, DXT5 }

    public sealed class DdsImage
    {
        public int Width, Height, MipCount;
        public PixelFormat Format;
        public byte[] Data; // full mip chain, largest first
        public bool HasAlpha;

        const uint DDPF_ALPHAPIXELS = 0x1, DDPF_FOURCC = 0x4, DDPF_RGB = 0x40, DDPF_LUMINANCE = 0x20000;

        public static DdsImage Load(byte[] file)
        {
            if (file.Length < 128 || file[0] != 'D' || file[1] != 'D' || file[2] != 'S' || file[3] != ' ')
                throw new InvalidDataException("not a DDS file");
            int height = BitConverter.ToInt32(file, 12);
            int width = BitConverter.ToInt32(file, 16);
            int mips = Math.Max(1, BitConverter.ToInt32(file, 28));
            uint pfFlags = BitConverter.ToUInt32(file, 80);
            string fourCC = System.Text.Encoding.ASCII.GetString(file, 84, 4);
            int bpp = BitConverter.ToInt32(file, 88);
            uint rMask = BitConverter.ToUInt32(file, 92), gMask = BitConverter.ToUInt32(file, 96),
                 bMask = BitConverter.ToUInt32(file, 100), aMask = BitConverter.ToUInt32(file, 104);
            uint caps2 = BitConverter.ToUInt32(file, 112);
            int offset = 128;
            if ((pfFlags & DDPF_FOURCC) != 0 && fourCC == "DX10") offset += 20;

            // cube maps: keep only the first face
            var img = new DdsImage { Width = width, Height = height };
            if ((pfFlags & DDPF_FOURCC) != 0)
            {
                switch (fourCC)
                {
                    case "DXT1": return Blocks(img, file, offset, mips, PixelFormat.DXT1, 8);
                    case "DXT5":
                    case "DXT4": img.HasAlpha = true; return Blocks(img, file, offset, mips, PixelFormat.DXT5, 16);
                    case "DXT3":
                    case "DXT2": img.HasAlpha = true; return DecodeDxt3(img, file, offset, mips);
                    default: throw new NotSupportedException("DDS fourCC " + fourCC);
                }
            }
            bool alpha = (pfFlags & DDPF_ALPHAPIXELS) != 0 && aMask != 0;
            bool lum = (pfFlags & DDPF_LUMINANCE) != 0;
            if ((pfFlags & (DDPF_RGB | DDPF_LUMINANCE)) == 0 && bpp == 8 && aMask == 0xFF) { lum = false; alpha = true; }
            return DecodeMasked(img, file, offset, mips, bpp, rMask, gMask, bMask, alpha ? aMask : 0, lum);
        }

        static DdsImage Blocks(DdsImage img, byte[] file, int offset, int mips, PixelFormat fmt, int blockBytes)
        {
            int total = 0, w = img.Width, h = img.Height, count = 0;
            for (int i = 0; i < mips; i++)
            {
                int size = Math.Max(1, (w + 3) / 4) * Math.Max(1, (h + 3) / 4) * blockBytes;
                if (offset + total + size > file.Length) break;
                total += size; count++;
                w = Math.Max(1, w / 2); h = Math.Max(1, h / 2);
            }
            img.Format = fmt;
            img.MipCount = Math.Max(1, count);
            img.Data = new byte[total];
            Buffer.BlockCopy(file, offset, img.Data, 0, total);
            if (fmt == PixelFormat.DXT1) img.HasAlpha = Dxt1HasAlpha(img.Data, Math.Max(1, (img.Width + 3) / 4) * Math.Max(1, (img.Height + 3) / 4));
            return img;
        }

        static bool Dxt1HasAlpha(byte[] d, int blocks)
        {
            for (int b = 0; b < blocks && b * 8 + 8 <= d.Length; b++)
            {
                ushort c0 = BitConverter.ToUInt16(d, b * 8), c1 = BitConverter.ToUInt16(d, b * 8 + 2);
                if (c0 > c1) continue;
                uint bits = BitConverter.ToUInt32(d, b * 8 + 4);
                for (int i = 0; i < 16; i++) if (((bits >> (i * 2)) & 3) == 3) return true;
            }
            return false;
        }

        static DdsImage DecodeDxt3(DdsImage img, byte[] file, int offset, int mips)
        {
            using (var ms = new MemoryStream())
            {
                int w = img.Width, h = img.Height, count = 0;
                for (int m = 0; m < mips; m++)
                {
                    int bw = Math.Max(1, (w + 3) / 4), bh = Math.Max(1, (h + 3) / 4);
                    if (offset + bw * bh * 16 > file.Length) break;
                    var px = new byte[w * h * 4];
                    for (int by = 0; by < bh; by++)
                        for (int bx = 0; bx < bw; bx++)
                        {
                            int o = offset + (by * bw + bx) * 16;
                            var colors = ColorBlock(file, o + 8, false);
                            uint idx = BitConverter.ToUInt32(file, o + 12);
                            for (int y = 0; y < 4; y++)
                                for (int x = 0; x < 4; x++)
                                {
                                    int X = bx * 4 + x, Y = by * 4 + y;
                                    if (X >= w || Y >= h) continue;
                                    int i = y * 4 + x;
                                    int c = (int)((idx >> (i * 2)) & 3);
                                    int a = (file[o + i / 2] >> ((i & 1) * 4)) & 0xF;
                                    int p = (Y * w + X) * 4;
                                    px[p] = colors[c * 4]; px[p + 1] = colors[c * 4 + 1]; px[p + 2] = colors[c * 4 + 2];
                                    px[p + 3] = (byte)(a * 17);
                                }
                        }
                    ms.Write(px, 0, px.Length);
                    offset += bw * bh * 16; count++;
                    w = Math.Max(1, w / 2); h = Math.Max(1, h / 2);
                }
                img.Format = PixelFormat.RGBA32; img.MipCount = Math.Max(1, count); img.Data = ms.ToArray();
                return img;
            }
        }

        static byte[] ColorBlock(byte[] d, int o, bool dxt1)
        {
            ushort c0 = BitConverter.ToUInt16(d, o), c1 = BitConverter.ToUInt16(d, o + 2);
            var c = new byte[16];
            Rgb565(c0, c, 0); Rgb565(c1, c, 4);
            c[3] = c[7] = 255;
            if (!dxt1 || c0 > c1)
            {
                for (int k = 0; k < 3; k++) { c[8 + k] = (byte)((2 * c[k] + c[4 + k]) / 3); c[12 + k] = (byte)((c[k] + 2 * c[4 + k]) / 3); }
                c[11] = c[15] = 255;
            }
            else
            {
                for (int k = 0; k < 3; k++) c[8 + k] = (byte)((c[k] + c[4 + k]) / 2);
                c[11] = 255; c[15] = 0;
            }
            return c;
        }

        static void Rgb565(ushort v, byte[] c, int o)
        {
            c[o] = (byte)(((v >> 11) & 31) * 255 / 31);
            c[o + 1] = (byte)(((v >> 5) & 63) * 255 / 63);
            c[o + 2] = (byte)((v & 31) * 255 / 31);
        }

        static DdsImage DecodeMasked(DdsImage img, byte[] file, int offset, int mips, int bpp, uint r, uint g, uint b, uint a, bool lum)
        {
            int bytes = bpp / 8;
            if (bytes < 1 || bytes > 4) throw new NotSupportedException($"DDS {bpp} bpp");
            using (var ms = new MemoryStream())
            {
                int w = img.Width, h = img.Height, count = 0;
                for (int m = 0; m < mips; m++)
                {
                    if (offset + w * h * bytes > file.Length) break;
                    var px = new byte[w * h * 4];
                    for (int i = 0; i < w * h; i++)
                    {
                        uint v = 0;
                        for (int k = 0; k < bytes; k++) v |= (uint)file[offset + i * bytes + k] << (8 * k);
                        byte R, G, B, A;
                        if (lum) { R = G = B = Channel(v, r == 0 ? 0xFFu : r); A = a != 0 ? Channel(v, a) : (byte)255; }
                        else if (r == 0 && g == 0 && b == 0) { R = G = B = 255; A = Channel(v, a); }
                        else { R = Channel(v, r); G = Channel(v, g); B = Channel(v, b); A = a != 0 ? Channel(v, a) : (byte)255; }
                        px[i * 4] = R; px[i * 4 + 1] = G; px[i * 4 + 2] = B; px[i * 4 + 3] = A;
                    }
                    ms.Write(px, 0, px.Length);
                    offset += w * h * bytes; count++;
                    w = Math.Max(1, w / 2); h = Math.Max(1, h / 2);
                }
                img.Format = PixelFormat.RGBA32; img.MipCount = Math.Max(1, count); img.Data = ms.ToArray();
                img.HasAlpha = a != 0;
                return img;
            }
        }

        static byte Channel(uint v, uint mask)
        {
            if (mask == 0) return 0;
            int shift = 0; while (((mask >> shift) & 1) == 0) shift++;
            uint max = mask >> shift;
            return (byte)(((v & mask) >> shift) * 255 / max);
        }
    }
}
