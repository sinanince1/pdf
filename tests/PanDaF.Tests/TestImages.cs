/// Test yardımcıları: JPEG'e EXIF yön bilgisi ekler (telefon fotoğrafı gibi).
internal static class TestImages
{
    public static byte[] WithExifOrientation(byte[] jpeg, ushort orientation)
    {
        if (jpeg.Length < 2 || jpeg[0] != 0xFF || jpeg[1] != 0xD8) throw new ArgumentException("JPEG değil");
        // APP1: "Exif\0\0" + TIFF (küçük uçlu) + tek girdili IFD0 (0x0112 Orientation, SHORT, 1).
        var tiff = new List<byte> { (byte)'I', (byte)'I', 42, 0, 8, 0, 0, 0, 1, 0, 0x12, 0x01, 3, 0, 1, 0, 0, 0, (byte)(orientation & 0xFF), (byte)(orientation >> 8), 0, 0, 0, 0, 0, 0 };
        var payload = new List<byte> { (byte)'E', (byte)'x', (byte)'i', (byte)'f', 0, 0 };
        payload.AddRange(tiff);
        var length = payload.Count + 2;
        var segment = new List<byte> { 0xFF, 0xE1, (byte)(length >> 8), (byte)(length & 0xFF) };
        segment.AddRange(payload);
        var result = new byte[jpeg.Length + segment.Count];
        result[0] = 0xFF; result[1] = 0xD8;
        segment.CopyTo(result, 2);
        Array.Copy(jpeg, 2, result, 2 + segment.Count, jpeg.Length - 2);
        return result;
    }
}
