namespace ServiceLib.Services.CoreConfig;

/// <summary>
/// 把 v2rayN 的路由规则(RulesItem)翻译成 mihomo/Clash 的规则行。
///
/// v2rayN 的规则是字段式的：域名、IP、进程之间是"或"关系，端口、网络是"与"关系
/// (与 xray 的 routing 语义一致)。Clash 的每条规则只能描述一个匹配器，所以：
/// - 域名/IP/进程各自展开成一条独立的规则行；
/// - 端口/网络用 AND 逻辑规则与匹配器组合，保持"与"语义。
///
/// 生成结果直接用 Clash 的规则字符串表示，规则顺序与 v2rayN 中保持一致。
/// </summary>
public partial class CoreConfigClashProfileService
{
    private const string ClashProxyGroup = "PROXY";
    private const string ClashDirectTag = "DIRECT";
    private const string ClashRejectTag = "REJECT";

    /// <summary>
    /// v2ray 的 geoip:private 覆盖的私有/保留地址段。mihomo 没有等价的 GEOIP 关键字
    /// (不同版本是否支持 LAN/PRIVATE 不一致)，这里直接展开成 IP-CIDR，保证离线可用。
    /// </summary>
    private static readonly List<string> _privateIpCidrs =
    [
        "0.0.0.0/8",
        "10.0.0.0/8",
        "100.64.0.0/10",
        "127.0.0.0/8",
        "169.254.0.0/16",
        "172.16.0.0/12",
        "192.0.0.0/24",
        "192.0.2.0/24",
        "192.88.99.0/24",
        "192.168.0.0/16",
        "198.18.0.0/15",
        "198.51.100.0/24",
        "203.0.113.0/24",
        "224.0.0.0/4",
        "240.0.0.0/4",
        "::1/128",
        "fc00::/7",
        "fe80::/10",
        "ff00::/8",
    ];

    private const string GeoSiteFileName = "geosite.dat";
    private const string GeoIpFileName = "geoip.dat";

    private static readonly ConcurrentDictionary<string, (long Ticks, HashSet<string> Codes)> _geoCodeCache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 校验 GEOIP/GEOSITE 代码是否存在于 v2rayN 维护的 geo 数据中。
    /// 返回 null 表示无法校验(文件不存在或解析失败)，此时保留规则交给 mihomo 自己处理。
    /// </summary>
    private static bool? GeoCodeExists(string fileName, string? code)
    {
        if (code.IsNullOrEmpty())
        {
            return false;
        }
        try
        {
            var path = Utils.GetBinPath(fileName);
            if (!File.Exists(path))
            {
                return null;
            }
            var ticks = File.GetLastWriteTimeUtc(path).Ticks;
            if (!_geoCodeCache.TryGetValue(path, out var entry) || entry.Ticks != ticks)
            {
                entry = (ticks, ClashGeoCodeReader.ReadCodes(path));
                _geoCodeCache[path] = entry;
            }
            return entry.Codes.Count == 0 ? null : entry.Codes.Contains(code);
        }
        catch (Exception ex)
        {
            Logging.SaveLog(_tag, ex);
            return null;
        }
    }

    /// <summary>
    /// 生成 mihomo 的 rules。规则里引用到的节点会被补充到 proxies 中。
    /// </summary>
    private List<string> GenRoutingRules(List<object> proxies)
    {
        var rules = new List<string>();
        try
        {
            var routing = context.RoutingItem;
            if (routing?.RuleSet.IsNotEmpty() == true)
            {
                var ruleItems = JsonUtils.Deserialize<List<RulesItem>>(routing.RuleSet) ?? [];
                foreach (var ruleItem in ruleItems)
                {
                    if (!ruleItem.Enabled || ruleItem.RuleType == ERuleType.DNS)
                    {
                        continue;
                    }
                    if (GenRoutingUserRule(ruleItem, proxies, rules))
                    {
                        // 已经是兜底规则，后面的规则不会再被匹配到
                        return rules;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Logging.SaveLog(_tag, ex);
        }

        // 与 xray 的隐式 final rule 一致：没有兜底规则时默认走代理
        rules.Add($"MATCH,{ClashProxyGroup}");
        return rules;
    }

    /// <summary>
    /// 翻译单条 v2rayN 路由规则；返回 true 表示生成的是兜底规则(MATCH)。
    /// </summary>
    private bool GenRoutingUserRule(RulesItem ruleItem, List<object> proxies, List<string> rules)
    {
        var target = GenRoutingUserRuleOutbound(ruleItem.OutboundTag, proxies);

        var matchers = new List<string>();
        foreach (var domain in ruleItem.Domain ?? [])
        {
            var matcher = ConvertDomain(domain);
            if (matcher.IsNotEmpty())
            {
                matchers.Add(matcher);
            }
        }
        foreach (var ip in ruleItem.Ip ?? [])
        {
            matchers.AddRange(ConvertIp(ip));
        }
        foreach (var process in ruleItem.Process ?? [])
        {
            var matcher = ConvertProcess(process);
            if (matcher.IsNotEmpty())
            {
                matchers.Add(matcher);
            }
        }

        var portMatchers = ConvertPort(ruleItem.Port);
        var networkMatcher = ConvertNetwork(ruleItem.Network);
        var hasUnsupportedCondition = ruleItem.Protocol?.Count > 0 || ruleItem.InboundTag?.Count > 0;

        if (matchers.Count == 0 && portMatchers.Count == 0 && networkMatcher.IsNullOrEmpty())
        {
            if (hasUnsupportedCondition)
            {
                // protocol / inboundTag 在 Clash 规则里没有对应写法，不能退化成 MATCH 把全部流量改道
                Logging.SaveLog($"{_tag} skip routing rule [{ruleItem.Remarks}]: clash 不支持 protocol/inboundTag 条件");
                return false;
            }
            rules.Add($"MATCH,{target}");
            return true;
        }

        if (hasUnsupportedCondition)
        {
            Logging.SaveLog($"{_tag} routing rule [{ruleItem.Remarks}]: clash 不支持 protocol/inboundTag 条件，已忽略");
        }

        var matcherList = matchers.Count > 0 ? matchers : [null];
        var portList = portMatchers.Count > 0 ? portMatchers : new List<string?> { null };
        foreach (var matcher in matcherList)
        {
            foreach (var port in portList)
            {
                var conditions = new List<string>();
                if (matcher.IsNotEmpty())
                {
                    conditions.Add(matcher);
                }
                if (networkMatcher.IsNotEmpty())
                {
                    conditions.Add(networkMatcher);
                }
                if (port.IsNotEmpty())
                {
                    conditions.Add(port);
                }
                rules.Add(ComposeRule(conditions, target));
            }
        }
        return false;
    }

    /// <summary>
    /// 单个匹配器直接输出 rule；多个条件(与关系)用 AND 逻辑规则组合。
    /// </summary>
    private static string ComposeRule(List<string> conditions, string target)
    {
        if (conditions.Count == 1)
        {
            return $"{conditions[0]},{target}";
        }
        var payload = string.Join(",", conditions.Select(c => $"({c})"));
        return $"AND,({payload}),{target}";
    }

    /// <summary>
    /// 规则目标：proxy/direct/block 映射为 Clash 内置目标，其它值是节点备注，
    /// 需要把该节点作为独立 proxy 加进配置后直接引用。
    /// </summary>
    private string GenRoutingUserRuleOutbound(string? outboundTag, List<object> proxies)
    {
        if (outboundTag.IsNullOrEmpty() || outboundTag == Global.ProxyTag)
        {
            return ClashProxyGroup;
        }
        if (outboundTag == Global.DirectTag)
        {
            return ClashDirectTag;
        }
        if (outboundTag == Global.BlockTag)
        {
            return ClashRejectTag;
        }

        var node = context.AllProxiesMap.GetValueOrDefault($"remark:{outboundTag}");
        if (node is null
            || !Global.MihomoSupportConfigType.Contains(node.ConfigType)
            || node.ConfigType.IsGroupType())
        {
            Logging.SaveLog($"{_tag} routing outbound [{outboundTag}] not supported by mihomo, fallback to proxy");
            return ClashProxyGroup;
        }

        // 规则行以逗号分隔，备注里的逗号会破坏规则
        var name = (node.Remarks.IsNotEmpty() ? node.Remarks : $"{node.Address}:{node.Port}")
            .Replace(",", " ")
            .Trim();
        if (name.IsNullOrEmpty()
            || name == ClashProxyGroup
            || name == ClashDirectTag
            || name == ClashRejectTag)
        {
            return ClashProxyGroup;
        }

        if (!proxies.OfType<Dictionary<string, object>>()
                .Any(p => p.GetValueOrDefault("name")?.ToString() == name))
        {
            proxies.Add(BuildProxy(node, name));
        }
        return name;
    }

    private static string? ConvertDomain(string? domain)
    {
        if (domain.IsNullOrEmpty())
        {
            return null;
        }
        var value = domain.Trim();
        if (value.StartsWith('#')
            || value.StartsWith("ext:", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("ext-domain:", StringComparison.OrdinalIgnoreCase))
        {
            // 注释行，或引用外部 rule set，Clash 配置内无法解析
            return null;
        }
        if (value.StartsWith(Global.GeoSitePrefix, StringComparison.OrdinalIgnoreCase))
        {
            var code = value[Global.GeoSitePrefix.Length..];
            if (GeoCodeExists(GeoSiteFileName, code) == false)
            {
                Logging.SaveLog($"{_tag} skip geosite rule [{code}]: not found in {GeoSiteFileName}");
                return null;
            }
            return $"GEOSITE,{code}";
        }
        if (value.StartsWith("regexp:", StringComparison.OrdinalIgnoreCase))
        {
            return $"DOMAIN-REGEX,{value[7..].Replace(Global.RoutingRuleComma, ",")}";
        }
        if (value.StartsWith("domain:", StringComparison.OrdinalIgnoreCase))
        {
            return $"DOMAIN-SUFFIX,{value[7..]}";
        }
        if (value.StartsWith("full:", StringComparison.OrdinalIgnoreCase))
        {
            return $"DOMAIN,{value[5..]}";
        }
        if (value.StartsWith("keyword:", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("dotless:", StringComparison.OrdinalIgnoreCase))
        {
            return $"DOMAIN-KEYWORD,{value[8..]}";
        }
        // 与 xray 一致：不带前缀的字符串按子串匹配
        return $"DOMAIN-KEYWORD,{value}";
    }

    private static IEnumerable<string> ConvertIp(string? ip)
    {
        if (ip.IsNullOrEmpty())
        {
            yield break;
        }
        var value = ip.Trim();
        if (value.StartsWith('!'))
        {
            // 取反的 IP 条件无法与其它条件一起表达，忽略
            Logging.SaveLog($"{_tag} skip negative ip rule [{value}]");
            yield break;
        }
        if (value.StartsWith("ext:", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("ext-ip:", StringComparison.OrdinalIgnoreCase))
        {
            yield break;
        }
        if (value.Equals($"{Global.GeoIPPrefix}private", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var cidr in _privateIpCidrs)
            {
                yield return $"IP-CIDR,{cidr}";
            }
            yield break;
        }
        if (value.StartsWith(Global.GeoIPPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var code = value[Global.GeoIPPrefix.Length..];
            if (GeoCodeExists(GeoIpFileName, code) == false)
            {
                Logging.SaveLog($"{_tag} skip geoip rule [{code}]: not found in {GeoIpFileName}");
                yield break;
            }
            yield return $"GEOIP,{code}";
            yield break;
        }

        var rule = ConvertIpCidr(value);
        if (rule.IsNotEmpty())
        {
            yield return rule;
        }
    }

    private static string? ConvertIpCidr(string value)
    {
        if (value.Contains('/'))
        {
            return IPAddress.TryParse(value.Split('/')[0], out var network)
                ? $"{(network.AddressFamily == AddressFamily.InterNetworkV6 ? "IP-CIDR6" : "IP-CIDR")},{value}"
                : null;
        }
        if (!IPAddress.TryParse(value, out var address))
        {
            return null;
        }
        return address.AddressFamily == AddressFamily.InterNetworkV6
            ? $"IP-CIDR6,{value}/128"
            : $"IP-CIDR,{value}/32";
    }

    private static string? ConvertProcess(string? process)
    {
        if (process.IsNullOrEmpty())
        {
            return null;
        }
        var value = process.Trim();
        // v2rayN 用 self/ 表示 v2rayN 自身、xray/ 表示整个核心目录，Clash 规则没有对应写法
        if (value is "self/" or "xray/")
        {
            return null;
        }
        if (value.Contains('/') || value.Contains('\\'))
        {
            return Utils.IsWindows()
                ? $"PROCESS-PATH,{value.Replace('/', '\\')}"
                : $"PROCESS-PATH,{value}";
        }
        return $"PROCESS-NAME,{Utils.GetExeName(value)}";
    }

    /// <summary>
    /// 端口条件；0-65535 等价于不限端口，直接忽略。
    /// 多个端口是"或"关系，这里展开成多条规则(由调用方与其它条件组合)。
    /// </summary>
    private static List<string> ConvertPort(string? port)
    {
        var result = new List<string>();
        if (port.IsNullOrEmpty())
        {
            return result;
        }
        foreach (var item in port.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var value = item.Trim();
            if (value.IsNullOrEmpty() || value == "0-65535")
            {
                continue;
            }
            result.Add($"DST-PORT,{value}");
        }
        return result;
    }

    private static string? ConvertNetwork(string? network)
    {
        var networks = Utils.String2List(network)?
            .Select(n => n.Trim().ToLowerInvariant())
            .Where(n => n.IsNotEmpty())
            .Distinct()
            .ToList();
        if (networks is null || networks.Count != 1)
        {
            // tcp,udp 表示不限；其它未知写法同样忽略
            return null;
        }
        return networks[0] is "tcp" or "udp" ? $"NETWORK,{networks[0].ToUpperInvariant()}" : null;
    }
}
