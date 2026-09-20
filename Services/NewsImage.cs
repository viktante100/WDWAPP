namespace WDWAPP.Services;

public static class NewsImage
{
    public const int MaxBytes = 5 * 1024 * 1024;

    public static async Task<byte[]> ReadAsync(IFormFile file)
    {
        await using var stream = file.OpenReadStream();
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int count;
        while ((count = await stream.ReadAsync(chunk)) > 0)
        {
            if (buffer.Length + count > MaxBytes) return [];
            await buffer.WriteAsync(chunk.AsMemory(0, count));
        }
        return buffer.ToArray();
    }

    // Never trust the client-supplied file extension or MIME type.
    public static string? ContentType(byte[] data)
    {
        var bytes = data.AsSpan();
        if (bytes.Length < 12) return null;
        if (bytes.StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })) return "image/png";
        if (bytes.StartsWith(new byte[] { 255, 216, 255 })) return "image/jpeg";
        if (bytes.StartsWith("GIF87a"u8) || bytes.StartsWith("GIF89a"u8)) return "image/gif";
        if (bytes.StartsWith("RIFF"u8) && bytes.Slice(8, 4).SequenceEqual("WEBP"u8)) return "image/webp";
        return null;
    }
}
