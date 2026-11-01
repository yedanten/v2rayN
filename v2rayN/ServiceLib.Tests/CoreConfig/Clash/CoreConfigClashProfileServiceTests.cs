namespace ServiceLib.Tests.CoreConfig.Clash;

/// <summary>
/// mihomo 以 Clash 规则字符串描述分流，v2rayN 的路由规则是字段式（domain/ip/port/network/process）。
/// 这里验证二者之间的转换，避免再次生成 "rules: [MATCH,PROXY]" 这种忽略路由设置的配置。
/// </summary>
public class CoreConfigClashProfileServiceTests
{
    [Test]
    public async Task GenerateClientConfigContent_WhitelistRouting_ShouldTranslateGeoSiteAndGeoIp()
    {
        var (result, rules, yaml) = await GenerateWithRouting(Global.CustomRoutingFileName + "white");

        await result.Success.Should().BeTrue().Because($"ret msg: {result.Msg}");

        // 阻断 udp443：字段条件需要 AND 组合
        await rules.Should().Contain("AND,((NETWORK,UDP),(DST-PORT,443)),REJECT");
        // geosite -> GEOSITE
        await rules.Should().Contain("GEOSITE,google,PROXY");
        await rules.Should().Contain("GEOSITE,private,DIRECT");
        await rules.Should().Contain("GEOSITE,cn,DIRECT");
        // geoip -> GEOIP
        await rules.Should().Contain("GEOIP,cn,DIRECT");
        // 单个 IP 需要补前缀长度
        await rules.Should().Contain("IP-CIDR,223.5.5.5/32,DIRECT");
        // domain: 前缀 -> DOMAIN-SUFFIX
        await rules.Should().Contain("DOMAIN-SUFFIX,alidns.com,DIRECT");
        // 模板没有兜底规则时，最终规则应为代理（与 xray 的隐式 final rule 一致）
        await rules[^1].Should().BeEqualTo("MATCH,PROXY");

        // geoip:private 在 mihomo 中没有对应写法，展开为私有网段
        await rules.Should().Contain("IP-CIDR,192.168.0.0/16,DIRECT");
        await rules.Should().Contain("IP-CIDR,10.0.0.0/8,DIRECT");

        // GEOIP/GEOSITE 需要 geo 数据；用 dat 模式才能复用 v2rayN 已下载的 geoip.dat/geosite.dat
        await bool.Parse(yaml["geodata-mode"].ToString()!).Should().BeTrue();
    }

    [Test]
    public async Task GenerateClientConfigContent_GlobalRouting_ShouldKeepGlobalProxyAndBlockRule()
    {
        var (result, rules, _) = await GenerateWithRouting(Global.CustomRoutingFileName + "global");

        await result.Success.Should().BeTrue().Because($"ret msg: {result.Msg}");
        await rules.Should().Contain("AND,((NETWORK,UDP),(DST-PORT,443)),REJECT");
        await rules[^1].Should().BeEqualTo("MATCH,PROXY");
        // 全局模板的最终规则（port 0-65535 -> proxy）应折叠成 MATCH，而不是 DST-PORT,0-65535
        await rules.Any(r => r.Contains("DST-PORT,0-65535", StringComparison.Ordinal)).Should().BeFalse();
    }

    [Test]
    public async Task GenerateClientConfigContent_BlacklistRouting_ShouldUseCatchAllDirectAndSkipUnsupportedRules()
    {
        var (result, rules, _) = await GenerateWithRouting(Global.CustomRoutingFileName + "black");

        await result.Success.Should().BeTrue().Because($"ret msg: {result.Msg}");
        await rules[^1].Should().BeEqualTo("MATCH,DIRECT");
        await rules.Should().Contain("GEOIP,facebook,PROXY");
        await rules.Should().Contain("DOMAIN-SUFFIX,cloudflare-dns.com,PROXY");
        // bittorrent 是 xray 的 protocol 条件，Clash 规则无法表达：整条规则应被跳过，不能变成 MATCH
        await rules.Any(r => r.Contains("bittorrent", StringComparison.OrdinalIgnoreCase)).Should().BeFalse();
        await rules.Any(r => r.StartsWith("MATCH,PROXY", StringComparison.Ordinal)).Should().BeFalse();
    }

    [Test]
    public async Task GenerateClientConfigContent_DomainPrefixes_ShouldMapToClashRuleTypes()
    {
        var (result, rules, _) = await GenerateWithRules(
        [
            new RulesItem
            {
                Enabled = true,
                OutboundTag = Global.ProxyTag,
                Domain =
                [
                    "geosite:google",
                    "domain:example.com",
                    "full:exact.example.com",
                    "keyword:kw",
                    @"regexp:^ad\..*$",
                    "plain.example",
                    "#a-comment",
                    "ext:custom-list",
                ],
            },
            new RulesItem
            {
                Enabled = true,
                OutboundTag = Global.DirectTag,
                Port = "0-65535",
            },
        ]);

        await result.Success.Should().BeTrue().Because($"ret msg: {result.Msg}");
        await rules.Should().Contain("GEOSITE,google,PROXY");
        await rules.Should().Contain("DOMAIN-SUFFIX,example.com,PROXY");
        await rules.Should().Contain("DOMAIN,exact.example.com,PROXY");
        await rules.Should().Contain("DOMAIN-KEYWORD,kw,PROXY");
        await rules.Should().Contain(@"DOMAIN-REGEX,^ad\..*$,PROXY");
        await rules.Should().Contain("DOMAIN-KEYWORD,plain.example,PROXY");
        await rules.Any(r => r.Contains("a-comment", StringComparison.Ordinal)).Should().BeFalse();
        await rules.Any(r => r.Contains("custom-list", StringComparison.Ordinal)).Should().BeFalse();
        await rules[^1].Should().BeEqualTo("MATCH,DIRECT");
    }

    [Test]
    public async Task GenerateClientConfigContent_IpRules_ShouldMapToClashIpRules()
    {
        var (result, rules, _) = await GenerateWithRules(
        [
            new RulesItem
            {
                Enabled = true,
                OutboundTag = Global.ProxyTag,
                Ip = ["geoip:cn", "1.1.1.1", "10.0.0.0/8", "2606:4700:4700::1111", "geoip:private"],
            },
            new RulesItem
            {
                Enabled = true,
                OutboundTag = Global.DirectTag,
                Port = "0-65535",
            },
        ]);

        await result.Success.Should().BeTrue().Because($"ret msg: {result.Msg}");
        await rules.Should().Contain("GEOIP,cn,PROXY");
        await rules.Should().Contain("IP-CIDR,1.1.1.1/32,PROXY");
        await rules.Should().Contain("IP-CIDR,10.0.0.0/8,PROXY");
        await rules.Should().Contain("IP-CIDR6,2606:4700:4700::1111/128,PROXY");
        await rules.Should().Contain("IP-CIDR,172.16.0.0/12,PROXY");
        await rules[^1].Should().BeEqualTo("MATCH,DIRECT");
    }

    [Test]
    public async Task GenerateClientConfigContent_PortRanges_ShouldMapToDstPort()
    {
        var (result, rules, _) = await GenerateWithRules(
        [
            new RulesItem
            {
                Enabled = true,
                OutboundTag = Global.DirectTag,
                Port = "1000-2000",
            },
            new RulesItem
            {
                Enabled = true,
                OutboundTag = Global.BlockTag,
                Port = "443",
                Network = "udp",
            },
            new RulesItem
            {
                Enabled = true,
                OutboundTag = Global.ProxyTag,
                Port = "0-65535",
            },
        ]);

        await result.Success.Should().BeTrue().Because($"ret msg: {result.Msg}");
        await rules.Should().Contain("DST-PORT,1000-2000,DIRECT");
        await rules.Should().Contain("AND,((NETWORK,UDP),(DST-PORT,443)),REJECT");
        await rules[^1].Should().BeEqualTo("MATCH,PROXY");
    }

    [Test]
    public async Task GenerateClientConfigContent_RuleToSpecificNode_ShouldAddProxyAndUseItAsTarget()
    {
        var (result, rules, yaml) = await GenerateWithRouting(
            Global.CustomRoutingFileName + "white",
            addRouteNode: true);

        await result.Success.Should().BeTrue().Because($"ret msg: {result.Msg}");

        var proxies = ((List<object>)yaml["proxies"]).OfType<Dictionary<object, object>>().ToList();
        await proxies.Should().Contain(p => Equals(p["name"], "route-node"));
        await rules.Should().Contain("DOMAIN,route.example.com,route-node");
        // 主节点仍然保留在 PROXY 组里，其余规则不受影响
        await rules.Should().Contain("MATCH,PROXY");
    }

    [Test]
    public async Task GenerateClientConfigContent_CatchAllRule_ShouldStopProcessingFollowingRules()
    {
        var (result, rules, _) = await GenerateWithRules(
        [
            new RulesItem
            {
                Enabled = true,
                OutboundTag = Global.DirectTag,
                Port = "0-65535",
                Network = "tcp,udp",
            },
            new RulesItem
            {
                Enabled = true,
                OutboundTag = Global.ProxyTag,
                Domain = ["geosite:cn"],
            },
        ]);

        await result.Success.Should().BeTrue().Because($"ret msg: {result.Msg}");
        await rules.Count.Should().BeEqualTo(1);
        await rules[0].Should().BeEqualTo("MATCH,DIRECT");
    }

    [Test]
    public async Task GenerateClientConfigContent_DisabledOrDnsRules_ShouldBeSkipped()
    {
        var (result, rules, _) = await GenerateWithRules(
        [
            new RulesItem
            {
                Enabled = false,
                OutboundTag = Global.DirectTag,
                Domain = ["geosite:cn"],
            },
            new RulesItem
            {
                Enabled = true,
                RuleType = ERuleType.DNS,
                OutboundTag = Global.DirectTag,
                Domain = ["geosite:cn"],
            },
            new RulesItem
            {
                Enabled = true,
                OutboundTag = Global.ProxyTag,
                Domain = ["geosite:google"],
            },
        ]);

        await result.Success.Should().BeTrue().Because($"ret msg: {result.Msg}");
        await rules.Any(r => r.Contains("GEOSITE,cn", StringComparison.Ordinal)).Should().BeFalse();
        await rules.Should().Contain("GEOSITE,google,PROXY");
        await rules[^1].Should().BeEqualTo("MATCH,PROXY");
    }

    [Test]
    public async Task GenerateClientConfigContent_ProcessRule_ShouldMapToProcessName()
    {
        var (result, rules, _) = await GenerateWithRules(
        [
            new RulesItem
            {
                Enabled = true,
                OutboundTag = Global.DirectTag,
                Process = ["chrome"],
            },
        ]);

        await result.Success.Should().BeTrue().Because($"ret msg: {result.Msg}");
        await rules.Should().Contain("PROCESS-NAME,chrome.exe,DIRECT");
        await rules[^1].Should().BeEqualTo("MATCH,PROXY");
    }

    [Test]
    public async Task GenerateClientConfigContent_NoRoutingItem_ShouldFallbackToProxy()
    {
        var config = CoreConfigTestFactory.CreateConfig(ECoreType.mihomo);
        CoreConfigTestFactory.BindAppManagerConfig(config);
        var node = CoreConfigTestFactory.CreateSsrNode(ECoreType.mihomo);
        var context = CoreConfigTestFactory.CreateContext(config, node, ECoreType.mihomo) with
        {
            RoutingItem = null,
        };

        var result = new CoreConfigClashProfileService(context).GenerateClientConfigContent();

        await result.Success.Should().BeTrue().Because($"ret msg: {result.Msg}");
        var rules = GetRules(result);
        await rules.Count.Should().BeEqualTo(1);
        await rules[0].Should().BeEqualTo("MATCH,PROXY");
    }

    [Test]
    public async Task GenerateClientConfigContent_SsrNode_ShouldStillGenerateSsrProxy()
    {
        var config = CoreConfigTestFactory.CreateConfig(ECoreType.mihomo);
        CoreConfigTestFactory.BindAppManagerConfig(config);
        var node = CoreConfigTestFactory.CreateSsrNode(ECoreType.mihomo);
        var context = CoreConfigTestFactory.CreateContext(config, node, ECoreType.mihomo);

        var result = new CoreConfigClashProfileService(context).GenerateClientConfigContent();

        await result.Success.Should().BeTrue().Because($"ret msg: {result.Msg}");
        var yaml = ParseYaml(result);
        var proxies = ((List<object>)yaml["proxies"]).OfType<Dictionary<object, object>>().ToList();
        await proxies.Count.Should().BeEqualTo(1);
        await proxies[0]["type"].Should().BeEqualTo("ssr");
        await proxies[0]["protocol"].Should().BeEqualTo("auth_aes128_md5");
    }

    [Test]
    public async Task GenerateClientConfigContent_ShouldEmitDnsThatDoesNotRelyOnSystemResolver()
    {
        var config = CoreConfigTestFactory.CreateConfig(ECoreType.mihomo);
        CoreConfigTestFactory.BindAppManagerConfig(config);
        var node = CoreConfigTestFactory.CreateSsrNode(ECoreType.mihomo);
        var context = CoreConfigTestFactory.CreateContext(config, node, ECoreType.mihomo);

        var result = new CoreConfigClashProfileService(context).GenerateClientConfigContent();

        await result.Success.Should().BeTrue().Because($"ret msg: {result.Msg}");
        var yaml = ParseYaml(result);

        // 没有 dns 段时 mihomo 用系统 DNS 解析直连域名，系统 DNS 一旦被污染，
        // 国内域名会被解析到海外节点，出现"国内直连比代理还慢"。
        await yaml.ContainsKey("dns").Should().BeTrue();
        var dns = (System.Collections.IDictionary)yaml["dns"]!;
        await bool.Parse(dns["enable"]!.ToString()!).Should().BeTrue();

        var nameserver = ((System.Collections.IEnumerable)dns["nameserver"]!)
            .Cast<object>()
            .Select(o => o.ToString())
            .ToList();
        // 默认开启国内 DNS，且即便用户填了失效地址也有国内公共 DNS 兜底
        await nameserver.Should().Contain("223.5.5.5");
        await nameserver.Should().Contain("119.29.29.29");

        // DoH 服务器域名要靠纯 IP 的 bootstrap DNS 解析
        var defaultNameserver = ((System.Collections.IEnumerable)dns["default-nameserver"]!)
            .Cast<object>()
            .Select(o => o.ToString())
            .ToList();
        await defaultNameserver.Any(a => System.Net.IPAddress.TryParse(a, out _)).Should().BeTrue();
    }

    #region helpers

    private static async Task<(RetResult Result, List<string> Rules, Dictionary<string, object> Yaml)>
        GenerateWithRouting(string routingResource, bool addRouteNode = false)
    {
        var config = CoreConfigTestFactory.CreateConfig(ECoreType.mihomo);
        CoreConfigTestFactory.BindAppManagerConfig(config);
        var node = CoreConfigTestFactory.CreateSsrNode(ECoreType.mihomo);

        var ruleSet = EmbedUtils.GetEmbedText(routingResource);
        if (addRouteNode)
        {
            var ruleItems = JsonUtils.Deserialize<List<RulesItem>>(ruleSet) ?? [];
            ruleItems.Insert(0, new RulesItem
            {
                Enabled = true,
                OutboundTag = "route-node",
                Domain = ["full:route.example.com"],
            });
            ruleSet = JsonUtils.Serialize(ruleItems);
        }

        var context = CoreConfigTestFactory.CreateContext(config, node, ECoreType.mihomo) with
        {
            RoutingItem = new RoutingItem
            {
                Id = "routing",
                Remarks = "demo",
                RuleSet = ruleSet,
                DomainStrategy = Global.AsIs,
                DomainStrategy4Singbox = string.Empty,
            },
        };
        if (addRouteNode)
        {
            var routeNode = CoreConfigTestFactory.CreateSocksNode(ECoreType.mihomo, "node-route", "route-node");
            context.AllProxiesMap[$"remark:route-node"] = routeNode;
        }

        var result = new CoreConfigClashProfileService(context).GenerateClientConfigContent();
        return (result, GetRules(result), ParseYaml(result));
    }

    private static async Task<(RetResult Result, List<string> Rules, Dictionary<string, object> Yaml)>
        GenerateWithRules(List<RulesItem> ruleItems)
    {
        var config = CoreConfigTestFactory.CreateConfig(ECoreType.mihomo);
        CoreConfigTestFactory.BindAppManagerConfig(config);
        var node = CoreConfigTestFactory.CreateSsrNode(ECoreType.mihomo);
        var context = CoreConfigTestFactory.CreateContext(config, node, ECoreType.mihomo) with
        {
            RoutingItem = new RoutingItem
            {
                Id = "routing",
                Remarks = "demo",
                RuleSet = JsonUtils.Serialize(ruleItems),
                DomainStrategy = Global.AsIs,
                DomainStrategy4Singbox = string.Empty,
            },
        };

        var result = new CoreConfigClashProfileService(context).GenerateClientConfigContent();
        return (result, GetRules(result), ParseYaml(result));
    }

    private static Dictionary<string, object> ParseYaml(RetResult result)
    {
        return YamlUtils.FromYaml<Dictionary<string, object>>(result.Data!.ToString())!;
    }

    private static List<string> GetRules(RetResult result)
    {
        var yaml = ParseYaml(result);
        return ((List<object>)yaml["rules"]).Select(r => r.ToString() ?? string.Empty).ToList();
    }

    #endregion helpers
}
