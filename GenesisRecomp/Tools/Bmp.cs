namespace GenesisRecomp.Tools;

public static class Bmp
{
    /// <summary>Saves a width x height region of an ARGB buffer (row stride in pixels) as a 24-bit BMP.</summary>
    public static void Save(string path, int[] pixels, int stride, int width, int height)
    {
        int rowBytes = (width * 3 + 3) & ~3;
        int size = 54 + rowBytes * height;
        using var w = new BinaryWriter(File.Create(path));
        w.Write((byte)'B'); w.Write((byte)'M');
        w.Write(size); w.Write(0); w.Write(54);
        w.Write(40); w.Write(width); w.Write(height);
        w.Write((short)1); w.Write((short)24); w.Write(0); w.Write(rowBytes * height);
        w.Write(2835); w.Write(2835); w.Write(0); w.Write(0);
        var row = new byte[rowBytes];
        for (int y = height - 1; y >= 0; y--)
        {
            for (int x = 0; x < width; x++)
            {
                int p = pixels[y * stride + x];
                row[x * 3] = (byte)p; row[x * 3 + 1] = (byte)(p >> 8); row[x * 3 + 2] = (byte)(p >> 16);
            }
            w.Write(row);
        }
    }
}
