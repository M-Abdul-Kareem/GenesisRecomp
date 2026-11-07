namespace GenesisRecomp.Machine;

/// <summary>Scanline renderer: planes A and B, the window plane and sprites, with the Mega Drive priority rules.</summary>
public sealed partial class Vdp
{
    public readonly int[] Frame = new int[320 * 240];                  // ARGB, 320 pixels per row
    public int Width => (R[12] & 0x01) != 0 ? 320 : 256;
    public int Height => (R[1] & 0x08) != 0 ? 240 : 224;

    private static readonly int[] Level = { 0, 36, 73, 109, 146, 182, 219, 255 };
    private readonly byte[] sprBuf = new byte[320];                    // idx | pal<<4 | priority<<6, 0 = transparent
    private readonly int[] visibleSprites = new int[80];

    private int ColorOf(int cramIndex)
    {
        ushort c = Cram[cramIndex & 63];
        return (0xFF << 24) | (Level[(c >> 1) & 7] << 16) | (Level[(c >> 5) & 7] << 8) | Level[(c >> 9) & 7];
    }

    private static int PlaneCells(int bits) => bits == 1 ? 64 : bits == 3 ? 128 : 32;

    private int Word(int a) => (Vram[a & 0xFFFF] << 8) | Vram[(a + 1) & 0xFFFF];

    /// <summary>One pixel of a tile plane: color index | palette&lt;&lt;4 | priority&lt;&lt;6, or 0 if transparent.</summary>
    private int PlanePixel(int nt, int cellsW, int cellsH, int px, int py)
    {
        int cx = (px >> 3) & (cellsW - 1), cy = (py >> 3) & (cellsH - 1);
        int e = Word((nt + ((cy * cellsW + cx) << 1)) & 0xFFFE);
        int tx = px & 7, ty = py & 7;
        if ((e & 0x0800) != 0) tx = 7 - tx;
        if ((e & 0x1000) != 0) ty = 7 - ty;
        int b = Vram[((e & 0x7FF) * 32 + ty * 4 + (tx >> 1)) & 0xFFFF];
        int idx = (tx & 1) == 0 ? b >> 4 : b & 15;
        if (idx == 0) return 0;
        return idx | (((e >> 13) & 3) << 4) | ((e & 0x8000) != 0 ? 0x40 : 0);
    }

    /// <summary>The original per-pixel renderer. Kept as the reference the fast renderer is checked against (and for column scroll).</summary>
    private void RenderLineReference(int y)
    {
        int w = Width;
        int row = y * 320;
        int backdrop = ColorOf(((R[7] >> 4) & 3) * 16 + (R[7] & 15));
        if ((R[1] & 0x40) == 0)
        {
            for (int x = 0; x < w; x++) Frame[row + x] = backdrop;
            return;
        }

        int cellsW = PlaneCells(R[16] & 3), cellsH = PlaneCells((R[16] >> 4) & 3);
        int ntA = (R[2] & 0x38) << 10, ntB = (R[4] & 0x07) << 13;
        int ntW = (w == 320 ? (R[3] & 0x3C) : (R[3] & 0x3E)) << 10;
        int winCells = w == 320 ? 64 : 32;
        int maskX = cellsW * 8 - 1, maskY = cellsH * 8 - 1;

        // horizontal scroll: full screen, per 8-pixel row, or per line
        int hsBase = (R[13] & 0x3F) << 10;
        int hsMode = R[11] & 3;
        int hsOff = hsMode == 0 ? 0 : hsMode == 2 ? (y & ~7) * 4 : hsMode == 3 ? y * 4 : 0;
        int hsA = Word(hsBase + hsOff) & 0x3FF, hsB = Word(hsBase + hsOff + 2) & 0x3FF;
        bool colScroll = (R[11] & 0x04) != 0;

        // window region (union of the horizontal and vertical parts)
        int wh = (R[17] & 0x1F) * 2, wv = R[18] & 0x1F;
        bool wRight = (R[17] & 0x80) != 0, wDown = (R[18] & 0x80) != 0;
        bool inV = wDown ? (y >> 3) >= wv : (y >> 3) < wv;

        DrawSprites(y, w);

        for (int x = 0; x < w; x++)
        {
            int vsA, vsB;
            if (colScroll) { int c = (x >> 4) * 2; vsA = Vsram[c] & 0x3FF; vsB = Vsram[c + 1] & 0x3FF; }
            else { vsA = Vsram[0] & 0x3FF; vsB = Vsram[1] & 0x3FF; }

            int b = PlanePixel(ntB, cellsW, cellsH, (x - hsB) & maskX, (y + vsB) & maskY);
            bool inH = wRight ? (x >> 3) >= wh : (x >> 3) < wh;
            int a = (inV || inH)
                ? PlanePixel(ntW, winCells, 32, x, y)
                : PlanePixel(ntA, cellsW, cellsH, (x - hsA) & maskX, (y + vsA) & maskY);
            int s = sprBuf[x];

            int pix = 0;
            if (b != 0 && (b & 0x40) == 0) pix = b;
            if (a != 0 && (a & 0x40) == 0) pix = a;
            if (s != 0 && (s & 0x40) == 0) pix = s;
            if (b != 0 && (b & 0x40) != 0) pix = b;
            if (a != 0 && (a & 0x40) != 0) pix = a;
            if (s != 0 && (s & 0x40) != 0) pix = s;

            Frame[row + x] = pix == 0 ? backdrop : ColorOf(((pix >> 4) & 3) * 16 + (pix & 15));
        }
    }

    private void DrawSprites(int y, int w)
    {
        Array.Clear(sprBuf, 0, sprBuf.Length);
        bool h40 = w == 320;
        int maxPerLine = h40 ? 20 : 16, maxTotal = h40 ? 80 : 64;
        int sat = (R[5] & (h40 ? 0x7E : 0x7F)) << 9;

        // walk the linked list, collecting the sprites that cover this line
        int n = 0, idx = 0, guard = 0;
        do
        {
            int ea = (sat + idx * 8) & 0xFFFF;
            int sy = (Word(ea) & 0x3FF) - 128;
            int size = Vram[(ea + 2) & 0xFFFF];
            int link = Vram[(ea + 3) & 0xFFFF] & 0x7F;
            int vcells = (size & 3) + 1;
            if (y >= sy && y < sy + vcells * 8)
            {
                if (n >= maxPerLine) { spriteOverflow = true; break; }
                visibleSprites[n++] = idx;
            }
            idx = link;
            guard++;
        } while (idx != 0 && guard < maxTotal);

        bool anyDrawn = false;
        for (int i = 0; i < n; i++)
        {
            int ea = (sat + visibleSprites[i] * 8) & 0xFFFF;
            int sy = (Word(ea) & 0x3FF) - 128;
            int size = Vram[(ea + 2) & 0xFFFF];
            int attr = Word(ea + 4);
            int xRaw = Word(ea + 6) & 0x1FF;
            if (xRaw == 0 && anyDrawn) break;                           // an X of 0 masks the remaining sprites on the line
            int sx = xRaw - 128;
            int hcells = ((size >> 2) & 3) + 1, vcells = (size & 3) + 1;
            bool hflip = (attr & 0x0800) != 0, vflip = (attr & 0x1000) != 0;
            int ty = y - sy;
            if (vflip) ty = vcells * 8 - 1 - ty;
            int cellRow = ty >> 3, rowIn = ty & 7;
            int pal = (attr >> 13) & 3, pri = (attr & 0x8000) != 0 ? 0x40 : 0;

            for (int cx = 0; cx < hcells; cx++)
            {
                int col = hflip ? hcells - 1 - cx : cx;
                int tile = ((attr & 0x7FF) + col * vcells + cellRow) & 0x7FF;
                for (int px = 0; px < 8; px++)
                {
                    int dx = sx + cx * 8 + px;
                    if ((uint)dx >= (uint)w) continue;
                    int tx = hflip ? 7 - px : px;
                    int b = Vram[(tile * 32 + rowIn * 4 + (tx >> 1)) & 0xFFFF];
                    int c = (tx & 1) == 0 ? b >> 4 : b & 15;
                    if (c == 0) continue;
                    if (sprBuf[dx] != 0) { spriteCollision = true; continue; }
                    sprBuf[dx] = (byte)(c | (pal << 4) | pri);
                }
            }
            anyDrawn = true;
        }
    }
}
