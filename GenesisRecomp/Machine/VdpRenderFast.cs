namespace GenesisRecomp.Machine;

/// <summary>
/// Fast scanline renderer. Same output as <see cref="RenderLineReference"/> (checked pixel by pixel with --verify-vdp),
/// but it works a tile row (up to 8 pixels) at a time instead of looking everything up per pixel,
/// and converts colours through a 64-entry table built once per line.
/// Lines that use per-column vertical scroll still go through the reference renderer.
/// </summary>
public sealed partial class Vdp
{
    /// <summary>When true, every line is also drawn by the reference renderer and compared (see --verify-vdp).</summary>
    public static bool VerifyRenderer;
    public long RenderMismatches, FastLines, ReferenceLines;
    public string? FirstRenderMismatch;

    private readonly int[] colorLut = new int[64];
    private readonly byte[] planeA = new byte[320 + 8];
    private readonly byte[] planeB = new byte[320 + 8];
    private readonly int[] verifyRow = new int[320];

    private void RenderLine(int y)
    {
        if (!VerifyRenderer) { RenderLineFast(y); return; }

        int w = Width, row = y * 320;
        RenderLineFast(y);
        Array.Copy(Frame, row, verifyRow, 0, w);
        RenderLineReference(y);
        for (int x = 0; x < w; x++)
        {
            if (Frame[row + x] != verifyRow[x])
            {
                RenderMismatches++;
                FirstRenderMismatch ??= $"line {y}, x {x}: fast {verifyRow[x]:X8}, reference {Frame[row + x]:X8}";
                break;
            }
        }
    }

    private void RenderLineFast(int y)
    {
        if ((R[1] & 0x40) != 0 && (R[11] & 0x04) != 0)      // column scroll: rare, use the simple path
        {
            ReferenceLines++;
            RenderLineReference(y);
            return;
        }
        FastLines++;

        int w = Width;
        int row = y * 320;
        for (int i = 0; i < 64; i++) colorLut[i] = ColorOf(i);
        int backdrop = colorLut[((R[7] >> 4) & 3) * 16 + (R[7] & 15)];
        if ((R[1] & 0x40) == 0)
        {
            Array.Fill(Frame, backdrop, row, w);
            return;
        }

        int cellsW = PlaneCells(R[16] & 3), cellsH = PlaneCells((R[16] >> 4) & 3);
        int ntA = (R[2] & 0x38) << 10, ntB = (R[4] & 0x07) << 13;
        int ntW = (w == 320 ? (R[3] & 0x3C) : (R[3] & 0x3E)) << 10;
        int winCells = w == 320 ? 64 : 32;
        int maskX = cellsW * 8 - 1, maskY = cellsH * 8 - 1;

        int hsBase = (R[13] & 0x3F) << 10;
        int hsMode = R[11] & 3;
        int hsOff = hsMode == 0 ? 0 : hsMode == 2 ? (y & ~7) * 4 : hsMode == 3 ? y * 4 : 0;
        int hsA = Word(hsBase + hsOff) & 0x3FF, hsB = Word(hsBase + hsOff + 2) & 0x3FF;
        int vsA = Vsram[0] & 0x3FF, vsB = Vsram[1] & 0x3FF;

        int wh = (R[17] & 0x1F) * 2, wv = R[18] & 0x1F;
        bool wRight = (R[17] & 0x80) != 0, wDown = (R[18] & 0x80) != 0;
        bool inV = wDown ? (y >> 3) >= wv : (y >> 3) < wv;

        DrawSprites(y, w);

        FillScrollPlane(planeB, ntB, cellsW, cellsH, (-hsB) & maskX, (y + vsB) & maskY, w);

        int cells = w >> 3;
        if (inV)
        {
            FillWindowCells(planeA, ntW, winCells, y, 0, cells);
        }
        else
        {
            // window cells: those where the horizontal rule says "inside" (cell index < wh, or >= wh when anchored right)
            int winFrom, winTo;                                       // [winFrom, winTo) are window cells
            if (wRight) { winFrom = Math.Min(wh, cells); winTo = cells; }
            else { winFrom = 0; winTo = Math.Min(wh, cells); }
            if (winFrom == 0 && winTo == cells)
            {
                FillWindowCells(planeA, ntW, winCells, y, 0, cells);
            }
            else
            {
                FillScrollPlane(planeA, ntA, cellsW, cellsH, (-hsA) & maskX, (y + vsA) & maskY, w);
                if (winTo > winFrom) FillWindowCells(planeA, ntW, winCells, y, winFrom, winTo);
            }
        }

        for (int x = 0; x < w; x++)
        {
            int b = planeB[x], a = planeA[x], s = sprBuf[x];
            int pix = 0;
            if (b != 0 && (b & 0x40) == 0) pix = b;
            if (a != 0 && (a & 0x40) == 0) pix = a;
            if (s != 0 && (s & 0x40) == 0) pix = s;
            if (b != 0 && (b & 0x40) != 0) pix = b;
            if (a != 0 && (a & 0x40) != 0) pix = a;
            if (s != 0 && (s & 0x40) != 0) pix = s;
            Frame[row + x] = pix == 0 ? backdrop : colorLut[pix & 0x3F];
        }
    }

    /// <summary>One line of a scrolling plane: screen x = 0 shows plane column <paramref name="px"/>, plane row <paramref name="py"/>.</summary>
    private void FillScrollPlane(byte[] buf, int nt, int cellsW, int cellsH, int px, int py, int w)
    {
        int maskX = cellsW * 8 - 1;
        int cy = (py >> 3) & (cellsH - 1), ty = py & 7;
        int rowBase = nt + ((cy * cellsW) << 1);
        int x = 0;
        while (x < w)
        {
            int tx0 = px & 7;
            int n = 8 - tx0;
            if (n > w - x) n = w - x;
            int cx = (px >> 3) & (cellsW - 1);
            DecodeTileRow(buf, x, Word((rowBase + (cx << 1)) & 0xFFFE), ty, tx0, n);
            x += n;
            px = (px + n) & maskX;
        }
    }

    /// <summary>Window plane cells [c0, c1) for this line (not scrolled, 8-aligned).</summary>
    private void FillWindowCells(byte[] buf, int nt, int winCells, int y, int c0, int c1)
    {
        int cy = (y >> 3) & 31, ty = y & 7;
        int rowBase = nt + ((cy * winCells) << 1);
        for (int c = c0; c < c1; c++)
            DecodeTileRow(buf, c << 3, Word((rowBase + (((c) & (winCells - 1)) << 1)) & 0xFFFE), ty, 0, 8);
    }

    /// <summary>Decode <paramref name="n"/> pixels (starting at pixel <paramref name="tx0"/> of the row) of one tile row into buf.</summary>
    private void DecodeTileRow(byte[] buf, int x, int entry, int ty, int tx0, int n)
    {
        if ((entry & 0x1000) != 0) ty = 7 - ty;
        int addr = (entry & 0x7FF) * 32 + ty * 4;
        int attr = (((entry >> 13) & 3) << 4) | ((entry & 0x8000) != 0 ? 0x40 : 0);
        bool hflip = (entry & 0x0800) != 0;
        for (int k = 0; k < n; k++)
        {
            int t = tx0 + k;
            if (hflip) t = 7 - t;
            int b = Vram[(addr + (t >> 1)) & 0xFFFF];
            int idx = (t & 1) == 0 ? b >> 4 : b & 15;
            buf[x + k] = idx == 0 ? (byte)0 : (byte)(idx | attr);
        }
    }
}
