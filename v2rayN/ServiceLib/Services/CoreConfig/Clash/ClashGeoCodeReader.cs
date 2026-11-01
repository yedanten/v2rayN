namespace ServiceLib.Services.CoreConfig;

/// <summary>
/// 读取 v2ray 格式的 geoip.dat / geosite.dat 中已包含的分类/国家代码。
///
/// 两个文件都是 protobuf：
///   GeoIPList  / GeoSiteList { repeated GeoIP  / GeoSite  entry = 1; }
///   GeoIP      / GeoSite     { string country_code = 1; ... }
/// 所以只需要解析外层每个 entry 的第一个字符串字段即可。
///
/// mihomo 遇到不存在的 GEOIP/GEOSITE 代码时会拒绝加载整份配置，
/// 生成规则前先校验可以避免一个拼错的分类把核心整个带崩。
/// </summary>
public static class ClashGeoCodeReader
{
    /// <summary>
    /// 读取 geo 数据文件中的全部代码；文件不存在或格式不符时返回空集合。
    /// </summary>
    public static HashSet<string> ReadCodes(string geoDataPath)
    {
        var codes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (geoDataPath.IsNullOrEmpty() || !File.Exists(geoDataPath))
        {
            return codes;
        }

        byte[] data;
        try
        {
            data = File.ReadAllBytes(geoDataPath);
        }
        catch (Exception ex)
        {
            Logging.SaveLog("ClashGeoCodeReader", ex);
            return codes;
        }

        var index = 0;
        while (index < data.Length)
        {
            // 外层：field 1, wire type 2
            if (!TryReadVarint(data, ref index, out var key) || (key >> 3) != 1 || (key & 7) != 2)
            {
                break;
            }
            if (!TryReadVarint(data, ref index, out var length) || length <= 0 || index + length > data.Length)
            {
                break;
            }

            var entryEnd = index + (int)length;
            var entryIndex = index;
            // 内层：第一个字段就是 country_code 字符串
            if (TryReadVarint(data, ref entryIndex, out var innerKey)
                && (innerKey >> 3) == 1
                && (innerKey & 7) == 2
                && TryReadVarint(data, ref entryIndex, out var codeLength)
                && codeLength > 0
                && entryIndex + codeLength <= entryEnd)
            {
                codes.Add(Encoding.UTF8.GetString(data, entryIndex, (int)codeLength));
            }

            index = entryEnd;
        }

        return codes;
    }

    private static bool TryReadVarint(byte[] data, ref int index, out long value)
    {
        value = 0;
        var shift = 0;
        while (index < data.Length && shift < 64)
        {
            var current = data[index++];
            value |= (long)(current & 0x7F) << shift;
            if ((current & 0x80) == 0)
            {
                return true;
            }
            shift += 7;
        }
        return false;
    }
}
