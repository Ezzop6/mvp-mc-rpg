using System;
using System.IO;
using System.IO.Compression;

namespace Renesance.Content;

// Minimal PNG reader: all colour types and bit depths, non-interlaced; 16-bit samples keep the high byte.
public static class Png
{
	public static (float R, float G, float B) Average(string path)
	{
		var (w, h, px) = Decode(File.ReadAllBytes(path));
		double r = 0, g = 0, b = 0, n = 0;
		for (int i = 0; i < w * h; i++)
		{
			double a = px[i * 4 + 3] / 255.0;
			r += px[i * 4] * a; g += px[i * 4 + 1] * a; b += px[i * 4 + 2] * a; n += a;
		}
		return n == 0 ? (0, 0, 0) : ((float)(r / n / 255), (float)(g / n / 255), (float)(b / n / 255));
	}

	public static (int W, int H, byte[] Rgba) Decode(byte[] f)
	{
		int w = 0, h = 0, depth = 0, type = 0;
		byte[] plte = null, trns = null;
		var idat = new MemoryStream();
		for (int p = 8; p < f.Length;)
		{
			int len = (f[p] << 24) | (f[p + 1] << 16) | (f[p + 2] << 8) | f[p + 3];
			string id = System.Text.Encoding.ASCII.GetString(f, p + 4, 4);
			var data = new ReadOnlySpan<byte>(f, p + 8, len);
			if (id == "IHDR") { w = BE(data, 0); h = BE(data, 4); depth = data[8]; type = data[9]; if (data[12] != 0) throw new InvalidDataException("interlaced PNG"); }
			else if (id == "PLTE") plte = data.ToArray();
			else if (id == "tRNS") trns = data.ToArray();
			else if (id == "IDAT") idat.Write(data);
			p += 12 + len;
		}
		int ch = type switch { 0 => 1, 2 => 3, 3 => 1, 4 => 2, 6 => 4, _ => throw new InvalidDataException($"PNG colour type {type}") };
		int bpp = Math.Max(1, ch * depth / 8), stride = (w * ch * depth + 7) / 8;
		idat.Position = 0;
		var raw = new MemoryStream();
		using (var z = new ZLibStream(idat, CompressionMode.Decompress)) z.CopyTo(raw);
		var src = raw.ToArray();
		var cur = new byte[stride]; var prev = new byte[stride]; var s = new byte[w * ch];
		var rgba = new byte[w * h * 4];
		for (int y = 0; y < h; y++)
		{
			int o = y * (stride + 1), filter = src[o];
			for (int x = 0; x < stride; x++)
			{
				int a = x >= bpp ? cur[x - bpp] : 0, b = prev[x], c = x >= bpp ? prev[x - bpp] : 0, v = src[o + 1 + x];
				cur[x] = (byte)(filter switch { 1 => v + a, 2 => v + b, 3 => v + (a + b) / 2, 4 => v + Paeth(a, b, c), _ => v });
			}
			for (int i = 0; i < s.Length; i++)
				s[i] = depth switch
				{
					16 => cur[i * 2],
					8 => cur[i],
					_ => (byte)((cur[i * depth / 8] >> (8 - depth - i * depth % 8) & ((1 << depth) - 1)) * (type == 3 ? 1 : 255 / ((1 << depth) - 1))),
				};
			for (int x = 0; x < w; x++)
			{
				int d = (y * w + x) * 4;
				switch (type)
				{
					case 0: rgba[d] = rgba[d + 1] = rgba[d + 2] = s[x]; rgba[d + 3] = 255; break;
					case 2: rgba[d] = s[x * 3]; rgba[d + 1] = s[x * 3 + 1]; rgba[d + 2] = s[x * 3 + 2]; rgba[d + 3] = 255; break;
					case 3: int pi = s[x]; rgba[d] = plte[pi * 3]; rgba[d + 1] = plte[pi * 3 + 1]; rgba[d + 2] = plte[pi * 3 + 2]; rgba[d + 3] = trns != null && pi < trns.Length ? trns[pi] : (byte)255; break;
					case 4: rgba[d] = rgba[d + 1] = rgba[d + 2] = s[x * 2]; rgba[d + 3] = s[x * 2 + 1]; break;
					case 6: Array.Copy(s, x * 4, rgba, d, 4); break;
				}
			}
			(cur, prev) = (prev, cur);
		}
		return (w, h, rgba);
	}

	static int BE(ReadOnlySpan<byte> d, int i) => (d[i] << 24) | (d[i + 1] << 16) | (d[i + 2] << 8) | d[i + 3];

	static int Paeth(int a, int b, int c)
	{
		int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
		return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
	}
}
