using System;
using System.Collections.Generic;
using System.Text;

namespace MultiRLBX
{
    /// <summary>
    /// Zero-dependency ISO/IEC 18004 Byte-mode QR Code generator (Versions 1-6, Error Correction M)
    /// rendering clean inline SVG matrices for offline Quick Login QR display.
    /// </summary>
    public static class QrCode
    {
        static readonly int[] DataCodewordsM = new int[] { 0, 16, 28, 44, 64, 86, 108 };
        static readonly int[] EcPerBlockM = new int[] { 0, 10, 16, 26, 18, 24, 16 };
        static readonly int[] NumBlocksM = new int[] { 0, 1, 1, 1, 2, 2, 4 };
        static readonly int[] AlignCoord = new int[] { 0, 0, 18, 22, 26, 30, 34 };
        static readonly int[] GfExp = new int[512];
        static readonly int[] GfLog = new int[256];

        static QrCode()
        {
            int x = 1;
            for (int i = 0; i < 255; i++)
            {
                GfExp[i] = x;
                GfLog[x] = i;
                x <<= 1;
                if ((x & 0x100) != 0) x ^= 0x11d;
            }
            for (int i = 255; i < 512; i++) GfExp[i] = GfExp[i - 255];
        }

        static int GfMul(int a, int b)
        {
            if (a == 0 || b == 0) return 0;
            return GfExp[GfLog[a] + GfLog[b]];
        }

        static int[] RsGenerator(int ecLen)
        {
            int[] g = new int[] { 1 };
            for (int i = 0; i < ecLen; i++)
            {
                int[] next = new int[g.Length + 1];
                int factor = GfExp[i];
                for (int j = 0; j < g.Length; j++)
                {
                    next[j] ^= g[j];
                    next[j + 1] ^= GfMul(g[j], factor);
                }
                g = next;
            }
            return g;
        }

        static byte[] RsCompute(byte[] data, int ecLen)
        {
            int[] gen = RsGenerator(ecLen);
            int[] rem = new int[ecLen];
            for (int i = 0; i < data.Length; i++)
            {
                int factor = data[i] ^ rem[0];
                Array.Copy(rem, 1, rem, 0, ecLen - 1);
                rem[ecLen - 1] = 0;
                for (int j = 0; j < ecLen; j++)
                    rem[j] ^= GfMul(gen[j + 1], factor);
            }
            byte[] outBytes = new byte[ecLen];
            for (int i = 0; i < ecLen; i++) outBytes[i] = (byte)rem[i];
            return outBytes;
        }

        public static bool[,] Encode(string text)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(text ?? "");
            int ver = 0;
            for (int v = 1; v <= 6; v++)
            {
                int cap = ((DataCodewordsM[v] * 8) - 12) / 8;
                if (bytes.Length <= cap) { ver = v; break; }
            }
            if (ver == 0) throw new ArgumentException("QR payload exceeds Version 6-M capacity.");

            int dataCw = DataCodewordsM[ver];
            int ecCw = EcPerBlockM[ver];
            int blocks = NumBlocksM[ver];

            List<int> bits = new List<int>();
            AppendBits(bits, 4, 4);               // Byte mode (0100)
            AppendBits(bits, bytes.Length, 8);    // 8-bit length for V1..V9
            for (int i = 0; i < bytes.Length; i++) AppendBits(bits, bytes[i], 8);

            int maxBits = dataCw * 8;
            int term = Math.Min(4, maxBits - bits.Count);
            AppendBits(bits, 0, term);
            while (bits.Count % 8 != 0) bits.Add(0);
            int padIdx = 0;
            while (bits.Count < maxBits)
            {
                AppendBits(bits, (padIdx % 2 == 0) ? 0xEC : 0x11, 8);
                padIdx++;
            }

            byte[] codewords = new byte[dataCw];
            for (int i = 0; i < dataCw; i++)
            {
                int b = 0;
                for (int k = 0; k < 8; k++) b = (b << 1) | bits[i * 8 + k];
                codewords[i] = (byte)b;
            }

            int blockDataLen = dataCw / blocks;
            List<byte[]> dataBlocks = new List<byte[]>();
            List<byte[]> ecBlocks = new List<byte[]>();
            for (int blk = 0; blk < blocks; blk++)
            {
                byte[] d = new byte[blockDataLen];
                Array.Copy(codewords, blk * blockDataLen, d, 0, blockDataLen);
                dataBlocks.Add(d);
                ecBlocks.Add(RsCompute(d, ecCw));
            }

            List<byte> interleaved = new List<byte>();
            for (int i = 0; i < blockDataLen; i++)
                for (int blk = 0; blk < blocks; blk++) interleaved.Add(dataBlocks[blk][i]);
            for (int i = 0; i < ecCw; i++)
                for (int blk = 0; blk < blocks; blk++) interleaved.Add(ecBlocks[blk][i]);

            int size = ver * 4 + 17;
            bool[,] isFunc = new bool[size, size];
            bool[,] baseMod = new bool[size, size];

            PlaceFinder(baseMod, isFunc, 0, 0, size);
            PlaceFinder(baseMod, isFunc, size - 7, 0, size);
            PlaceFinder(baseMod, isFunc, 0, size - 7, size);

            if (ver >= 2) PlaceAlignment(baseMod, isFunc, AlignCoord[ver], AlignCoord[ver]);

            for (int i = 8; i < size - 8; i++)
            {
                bool dark = (i % 2 == 0);
                SetFunc(baseMod, isFunc, 6, i, dark);
                SetFunc(baseMod, isFunc, i, 6, dark);
            }
            SetFunc(baseMod, isFunc, 8, size - 8, true); // dark module

            // Reserve format areas
            for (int i = 0; i < 9; i++)
            {
                if (i != 6) { isFunc[8, i] = true; isFunc[i, 8] = true; }
            }
            for (int i = size - 8; i < size; i++)
            {
                isFunc[8, i] = true;
                isFunc[i, 8] = true;
            }

            // Place data bits zig-zag
            int bitIdx = 0;
            int totalDataBits = interleaved.Count * 8;
            for (int right = size - 1; right >= 1; right -= 2)
            {
                if (right == 6) right = 5;
                bool upward = (((right + 1) / 2) % 2 == 0);
                for (int step = 0; step < size; step++)
                {
                    int y = upward ? (size - 1 - step) : step;
                    for (int dx = 0; dx < 2; dx++)
                    {
                        int x = right - dx;
                        if (isFunc[x, y]) continue;
                        bool dark = false;
                        if (bitIdx < totalDataBits)
                        {
                            byte cw = interleaved[bitIdx >> 3];
                            dark = ((cw >> (7 - (bitIdx & 7))) & 1) != 0;
                            bitIdx++;
                        }
                        baseMod[x, y] = dark;
                    }
                }
            }

            int bestMask = 0;
            int bestScore = int.MaxValue;
            bool[,] bestGrid = null;
            for (int mask = 0; mask < 8; mask++)
            {
                bool[,] cand = new bool[size, size];
                for (int x = 0; x < size; x++)
                    for (int y = 0; y < size; y++)
                        cand[x, y] = isFunc[x, y] ? baseMod[x, y] : (baseMod[x, y] ^ MaskBit(mask, x, y));

                ApplyFormatBits(cand, mask, size);
                int score = PenaltyScore(cand, size);
                if (score < bestScore)
                {
                    bestScore = score;
                    bestMask = mask;
                    bestGrid = cand;
                }
            }
            return bestGrid;
        }

        public static string ToSvg(string text)
        {
            bool[,] mod = Encode(text);
            int size = mod.GetLength(0);
            int total = size + 4; // 2-module quiet zone
            StringBuilder sb = new StringBuilder();
            sb.Append("<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 ").Append(total).Append(" ").Append(total)
              .Append("\" shape-rendering=\"crispEdges\" role=\"img\" aria-label=\"Quick Login QR\">");
            sb.Append("<rect width=\"").Append(total).Append("\" height=\"").Append(total).Append("\" fill=\"#ffffff\"/>");
            sb.Append("<path fill=\"#0f172a\" d=\"");
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    if (mod[x, y]) sb.Append("M").Append(x + 2).Append(",").Append(y + 2).Append("h1v1h-1z");
                }
            }
            sb.Append("\"/></svg>");
            return sb.ToString();
        }

        static void AppendBits(List<int> list, int val, int count)
        {
            for (int i = count - 1; i >= 0; i--) list.Add((val >> i) & 1);
        }

        static void SetFunc(bool[,] m, bool[,] f, int x, int y, bool dark)
        {
            m[x, y] = dark;
            f[x, y] = true;
        }

        static void PlaceFinder(bool[,] m, bool[,] f, int ox, int oy, int size)
        {
            for (int dy = -1; dy <= 7; dy++)
            {
                for (int dx = -1; dx <= 7; dx++)
                {
                    int x = ox + dx;
                    int y = oy + dy;
                    if (x < 0 || y < 0 || x >= size || y >= size) continue;
                    int dist = Math.Max(Math.Abs(dx - 3), Math.Abs(dy - 3));
                    bool dark = (dist != 2 && dist != 4);
                    SetFunc(m, f, x, y, dark);
                }
            }
        }

        static void PlaceAlignment(bool[,] m, bool[,] f, int cx, int cy)
        {
            for (int dy = -2; dy <= 2; dy++)
                for (int dx = -2; dx <= 2; dx++)
                    SetFunc(m, f, cx + dx, cy + dy, Math.Max(Math.Abs(dx), Math.Abs(dy)) != 1);
        }

        static bool MaskBit(int mask, int x, int y)
        {
            switch (mask)
            {
                case 0: return (x + y) % 2 == 0;
                case 1: return y % 2 == 0;
                case 2: return x % 3 == 0;
                case 3: return (x + y) % 3 == 0;
                case 4: return ((y / 2) + (x / 3)) % 2 == 0;
                case 5: return ((x * y) % 2 + (x * y) % 3) == 0;
                case 6: return (((x * y) % 2 + (x * y) % 3) % 2) == 0;
                default: return (((x + y) % 2 + (x * y) % 3) % 2) == 0;
            }
        }

        static void ApplyFormatBits(bool[,] m, int mask, int size)
        {
            // EC level M = 00
            int data = mask;
            int rem = data;
            for (int i = 0; i < 10; i++) rem = (rem << 1) ^ (((rem >> 9) & 1) * 0x537);
            int bits = ((data << 10) | rem) ^ 0x5412;

            for (int i = 0; i < 15; i++)
            {
                bool dark = ((bits >> i) & 1) != 0;
                if (i < 6) m[8, i] = dark;
                else if (i < 8) m[8, i + 1] = dark;
                else m[8, size - 15 + i] = dark;

                if (i < 8) m[size - 1 - i, 8] = dark;
                else if (i < 9) m[15 - i, 8] = dark;
                else m[14 - i, 8] = dark;
            }
        }

        static int PenaltyScore(bool[,] m, int size)
        {
            int penalty = 0;
            for (int y = 0; y < size; y++)
            {
                int runX = 0; bool prevX = false;
                int runY = 0; bool prevY = false;
                for (int x = 0; x < size; x++)
                {
                    if (x == 0 || m[x, y] != prevX) { if (runX >= 5) penalty += 3 + (runX - 5); prevX = m[x, y]; runX = 1; }
                    else runX++;
                    if (x == 0 || m[y, x] != prevY) { if (runY >= 5) penalty += 3 + (runY - 5); prevY = m[y, x]; runY = 1; }
                    else runY++;
                }
                if (runX >= 5) penalty += 3 + (runX - 5);
                if (runY >= 5) penalty += 3 + (runY - 5);
            }
            for (int y = 0; y < size - 1; y++)
                for (int x = 0; x < size - 1; x++)
                    if (m[x, y] == m[x + 1, y] && m[x, y] == m[x, y + 1] && m[x, y] == m[x + 1, y + 1]) penalty += 3;
            return penalty;
        }
    }
}
