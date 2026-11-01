namespace ServiceLib.Tests.CoreConfig.Clash;

/// <summary>
/// geoip.dat / geosite.dat 是 protobuf 格式，mihomo 对 GEOIP/GEOSITE 里不存在的代码会拒绝加载整份配置，
/// 所以生成规则前会用这个读取器做一次校验。这里用合成的数据文件验证解析逻辑。
/// </summary>
public class ClashGeoCodeReaderTests
{
    [Test]
    public async Task ReadCodes_ShouldParseEveryCountryCode()
    {
        var path = WriteGeoData(("cn", 200), ("GOOGLE", 3), ("geolocation-!cn", 0));

        var codes = ClashGeoCodeReader.ReadCodes(path);

        await codes.Count.Should().BeEqualTo(3);
        // mihomo 内部会把代码转成小写，比对应当大小写不敏感
        await codes.Contains("CN").Should().BeTrue();
        await codes.Contains("google").Should().BeTrue();
        await codes.Contains("GEOLOCATION-!CN").Should().BeTrue();
    }

    [Test]
    public async Task ReadCodes_MissingFile_ShouldReturnEmptySet()
    {
        var path = Path.Combine(Path.GetTempPath(), $"geo-missing-{Guid.NewGuid():N}.dat");

        var codes = ClashGeoCodeReader.ReadCodes(path);

        await codes.Count.Should().BeEqualTo(0);
    }

    [Test]
    public async Task ReadCodes_EntryWithoutCodeField_ShouldBeIgnored()
    {
        // 模拟第一个 entry 没有 country_code 字段（用 field 2 的数字代替），后续 entry 仍然要能解析
        using var stream = new MemoryStream();
        var badEntry = new byte[] { 0x10, 0x01 }; // field 2, varint 1
        WriteEntry(stream, badEntry);
        using var goodEntry = new MemoryStream();
        WriteString(goodEntry, 1, "gfw");
        WriteEntry(stream, goodEntry.ToArray());
        var path = Path.Combine(Path.GetTempPath(), $"geo-bad-{Guid.NewGuid():N}.dat");
        await File.WriteAllBytesAsync(path, stream.ToArray());

        var codes = ClashGeoCodeReader.ReadCodes(path);

        await codes.Count.Should().BeEqualTo(1);
        await codes.Contains("gfw").Should().BeTrue();
    }

    #region protobuf 编码

    private static string WriteGeoData(params (string Code, int Padding)[] entries)
    {
        using var stream = new MemoryStream();
        foreach (var (code, padding) in entries)
        {
            using var entry = new MemoryStream();
            WriteString(entry, 1, code);
            if (padding > 0)
            {
                // 用 field 2 的定长字节模拟域名列表，让 entry 长度超过 127，覆盖多字节 varint
                WriteTag(entry, 2, 2);
                WriteRawVarint(entry, (ulong)padding);
                entry.Write(new byte[padding]);
            }
            WriteEntry(stream, entry.ToArray());
        }

        var path = Path.Combine(Path.GetTempPath(), $"geo-{Guid.NewGuid():N}.dat");
        File.WriteAllBytes(path, stream.ToArray());
        return path;
    }

    private static void WriteEntry(Stream stream, byte[] entry)
    {
        WriteTag(stream, 1, 2);
        WriteRawVarint(stream, (ulong)entry.Length);
        stream.Write(entry);
    }

    private static void WriteString(Stream stream, int field, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        WriteTag(stream, field, 2);
        WriteRawVarint(stream, (ulong)bytes.Length);
        stream.Write(bytes);
    }

    private static void WriteTag(Stream stream, int field, int wireType)
    {
        WriteRawVarint(stream, (ulong)((field << 3) | wireType));
    }

    private static void WriteRawVarint(Stream stream, ulong value)
    {
        while (value >= 0x80)
        {
            stream.WriteByte((byte)(value | 0x80));
            value >>= 7;
        }
        stream.WriteByte((byte)value);
    }

    #endregion protobuf 编码
}
