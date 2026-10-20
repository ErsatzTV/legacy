namespace ErsatzTV.Infrastructure.Troubleshooting;

// age uses bech32, not bech32m, and has no 90-character limit
internal static class Bech32
{
    private const string Charset = "qpzry9x8gf2tvdw0s3jn54khce6mua7l";
    private const int ChecksumLength = 6;

    public static byte[] Decode(string value, string expectedHrp)
    {
        if (value.Any(char.IsUpper))
        {
            throw new FormatException("bech32 string must be lowercase");
        }

        int separator = value.LastIndexOf('1');
        if (separator < 1 || separator + ChecksumLength + 1 > value.Length)
        {
            throw new FormatException("invalid bech32 separator position");
        }

        string hrp = value[..separator];
        if (hrp != expectedHrp)
        {
            throw new FormatException($"expected bech32 prefix '{expectedHrp}', got '{hrp}'");
        }

        var data = new byte[value.Length - separator - 1];
        for (var i = 0; i < data.Length; i++)
        {
            int index = Charset.IndexOf(value[separator + 1 + i], StringComparison.Ordinal);
            if (index < 0)
            {
                throw new FormatException("invalid bech32 character");
            }

            data[i] = (byte)index;
        }

        if (Polymod([..ExpandHrp(hrp), ..data]) != 1)
        {
            throw new FormatException("invalid bech32 checksum");
        }

        return ConvertBits(data.AsSpan(0, data.Length - ChecksumLength), 5, 8, false);
    }

    public static string Encode(string hrp, ReadOnlySpan<byte> bytes)
    {
        byte[] data = ConvertBits(bytes, 8, 5, true);
        uint polymod = Polymod([..ExpandHrp(hrp), ..data, 0, 0, 0, 0, 0, 0]) ^ 1;

        var chars = new char[data.Length + ChecksumLength];
        for (var i = 0; i < data.Length; i++)
        {
            chars[i] = Charset[data[i]];
        }

        for (var i = 0; i < ChecksumLength; i++)
        {
            chars[data.Length + i] = Charset[(int)((polymod >> (5 * (5 - i))) & 31)];
        }

        return $"{hrp}1{new string(chars)}";
    }

    private static uint Polymod(ReadOnlySpan<byte> values)
    {
        uint[] generator = [0x3b6a57b2, 0x26508e6d, 0x1ea119fa, 0x3d4233dd, 0x2a1462b3];

        uint chk = 1;
        foreach (byte value in values)
        {
            uint top = chk >> 25;
            chk = ((chk & 0x1ffffff) << 5) ^ value;
            for (var i = 0; i < 5; i++)
            {
                if (((top >> i) & 1) != 0)
                {
                    chk ^= generator[i];
                }
            }
        }

        return chk;
    }

    private static byte[] ExpandHrp(string hrp)
    {
        var result = new byte[hrp.Length * 2 + 1];
        for (var i = 0; i < hrp.Length; i++)
        {
            result[i] = (byte)(hrp[i] >> 5);
            result[hrp.Length + 1 + i] = (byte)(hrp[i] & 31);
        }

        return result;
    }

    private static byte[] ConvertBits(ReadOnlySpan<byte> data, int fromBits, int toBits, bool pad)
    {
        var acc = 0;
        var bits = 0;
        int maxValue = (1 << toBits) - 1;
        var result = new List<byte>(data.Length * fromBits / toBits + 1);

        foreach (byte value in data)
        {
            acc = (acc << fromBits) | value;
            bits += fromBits;
            while (bits >= toBits)
            {
                bits -= toBits;
                result.Add((byte)((acc >> bits) & maxValue));
            }
        }

        if (pad)
        {
            if (bits > 0)
            {
                result.Add((byte)((acc << (toBits - bits)) & maxValue));
            }
        }
        else if (bits >= fromBits || ((acc << (toBits - bits)) & maxValue) != 0)
        {
            throw new FormatException("invalid bech32 padding");
        }

        return result.ToArray();
    }
}
