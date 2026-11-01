namespace ServiceLib.Services.CoreConfig;

/// <summary>
/// 生成 mihomo 的 dns 段。
///
/// 不写 dns 段时 mihomo 会退回 Go 的系统解析器：解析结果完全取决于系统 DNS。
/// 系统 DNS 一旦被改成已停服的地址或被污染的境外 DNS，国内域名就会被解析到海外节点，
/// 规则虽然命中 DIRECT，实际却在跨境访问，表现为"国内网站比代理还慢"。
///
/// 这里沿用 v2rayN 的 DNS 设置：
/// - DirectDNS 解析直连域名（默认用国内公共 DNS）；
/// - BootstrapDNS 只负责解析 DoH 服务器域名，必须是纯 IP；
/// - 走代理的域名不需要本地解析（mihomo 把域名交给节点），所以不配置 fallback，
///   否则 fallback 用境外 DoH 直连时会固定超时一轮，反而拖慢每个请求。
/// </summary>
public partial class CoreConfigClashProfileService
{
    /// <summary>
    /// 用户把 DNS 设成失效地址时的兜底，保证 DoH 服务器域名还能解析。
    /// </summary>
    private static readonly List<string> _fallbackDnsAddresses = ["223.5.5.5", "119.29.29.29"];

    /// <summary>
    /// fake-ip 过滤：这些域名的解析结果会被直接使用，不返回假 IP，
    /// 否则局域网设备发现、STUN 组网之类会失效。
    /// </summary>
    private static readonly List<string> _fakeIpFilter =
    [
        "*.lan",
        "*.local",
        "*.arpa",
        "*.home.arpa",
        "localhost",
        "localhost.ptlogin2.qq.com",
        "localhost.weixin.qq.com",
        "*.msftncsi.com",
        "*.msftconnecttest.com",
        "WORKGROUP",
        "+.stun.*.*",
        "+.stun.*.*.*",
        "+.stun.playstation.net",
    ];

    private Dictionary<string, object>? GenDns()
    {
        try
        {
            var dns = context.SimpleDnsItem;
            var useFakeIp = dns.FakeIP == true;

            var nameserver = GenDnsAddresses(dns.DirectDNS);
            foreach (var address in _fallbackDnsAddresses)
            {
                if (!nameserver.Contains(address, StringComparer.OrdinalIgnoreCase))
                {
                    nameserver.Add(address);
                }
            }

            var yaml = new Dictionary<string, object>
            {
                ["enable"] = true,
                ["ipv6"] = _config.ClashUIItem.EnableIPv6,
                ["enhanced-mode"] = useFakeIp ? "fake-ip" : "redir-host",
                // default-nameserver 只用于解析 DoH 服务器域名，mihomo 要求这里都是纯 IP
                ["default-nameserver"] = GenDnsAddresses(dns.BootstrapDNS, ipOnly: true),
                ["nameserver"] = nameserver,
            };

            if (useFakeIp)
            {
                yaml["fake-ip-range"] = dns.FakeIPRange.IsNotEmpty() ? dns.FakeIPRange : "198.18.0.1/16";
                yaml["fake-ip-filter"] = _fakeIpFilter;
            }

            return yaml;
        }
        catch (Exception ex)
        {
            Logging.SaveLog(_tag, ex);
            return null;
        }
    }

    /// <summary>
    /// v2rayN 里用 dhcp/localhost 表示"跟随系统 DNS"，对应 mihomo 的 system。
    /// ipOnly 用于 default-nameserver：非 IP 的地址会被丢弃，由调用方补兜底 IP。
    /// </summary>
    private static List<string> GenDnsAddresses(string? input, bool ipOnly = false)
    {
        var result = new List<string>();
        foreach (var item in Utils.String2List(input) ?? [])
        {
            var address = item.Trim();
            if (address.IsNullOrEmpty())
            {
                continue;
            }
            if (address.Equals("dhcp", StringComparison.OrdinalIgnoreCase)
                || address.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            {
                address = "system";
            }
            else if (ipOnly && !IPAddress.TryParse(address, out _))
            {
                continue;
            }
            if (!result.Contains(address, StringComparer.OrdinalIgnoreCase))
            {
                result.Add(address);
            }
        }

        if (result.Count == 0)
        {
            result.AddRange(_fallbackDnsAddresses.Where(a => !ipOnly || IPAddress.TryParse(a, out _)));
        }
        return result;
    }
}
