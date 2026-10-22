using System.IO.Compression;
using System.Text;

namespace CodeMaster.Agent.Services;

public static class AiToolPayloadCodec
{
    private const string GzipPrefix = "gzip:";
    private static readonly byte[] BinaryHeader = [0x43, 0x4D, 0x41, 0x50, 0x01]; // CMAP v1

    public static string Encode(string json, int maximumStoredLength = 7800)
    {
        if (json.Length <= maximumStoredLength)
            return json;

        var source = Encoding.UTF8.GetBytes(json);
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            gzip.Write(source, 0, source.Length);
        }

        var encoded = GzipPrefix + Convert.ToBase64String(output.ToArray());
        if (encoded.Length > maximumStoredLength)
        {
            throw new InvalidOperationException(
                "The Agent tool payload is too large for one execution record even after compression. Split the request into smaller coherent change sets, for example one business aggregate per approval.");
        }

        return encoded;
    }

    public static string Decode(string value)
    {
        if (!value.StartsWith(GzipPrefix, StringComparison.Ordinal))
            return value;

        var compressed = Convert.FromBase64String(value[GzipPrefix.Length..]);
        using var input = new MemoryStream(compressed);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        gzip.CopyTo(output);
        return Encoding.UTF8.GetString(output.ToArray());
    }

    public static byte[] EncodeBinary(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        var source = Encoding.UTF8.GetBytes(json);
        using var output = new MemoryStream();
        output.Write(BinaryHeader);
        using (var gzip = new GZipStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            gzip.Write(source, 0, source.Length);
        }

        return output.ToArray();
    }

    public static string DecodeBinary(byte[] payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (payload.Length <= BinaryHeader.Length ||
            !payload.AsSpan(0, BinaryHeader.Length).SequenceEqual(BinaryHeader))
        {
            throw new InvalidDataException("Unsupported Agent tool payload format.");
        }

        using var input = new MemoryStream(payload, BinaryHeader.Length, payload.Length - BinaryHeader.Length);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        gzip.CopyTo(output);
        return Encoding.UTF8.GetString(output.ToArray());
    }
}
