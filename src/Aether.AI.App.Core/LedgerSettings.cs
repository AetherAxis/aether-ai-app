using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aether.AI.App.Core;

/// <summary>
/// 链上钱包的**宿主配置**（<c>&lt;dataDir&gt;/ledger.json</c>）：链节点地址 + 交易所 custody 托管地址。
/// <para>为什么需要这个文件：钱包工具是纯 RPC 客户端，节点在哪由宿主告诉它。桌面可以用本机节点
/// （<c>127.0.0.1:17805</c>），手机必须指向服务端节点——而手机端设不了环境变量，所以配置必须落成文件，
/// 且两端同一份布局（换端同步就是搬这个文件）。</para>
/// <para>优先级：<b>本文件 &gt; 环境变量 &gt; 开发默认</b>。环境变量那档是给服务器/CI/既有部署留的兼容路，
/// 文件那档才是用户在应用里填的入口——用户填了就该压过环境。</para>
/// <para>⚠ 这里**没有**钱包种子：种子是 <c>&lt;dataDir&gt;/wallet.seed</c>（明文 hex，= 全部财富），
/// 只由签名工具现读、不进本配置、不进 LLM 上下文。</para>
/// </summary>
public sealed class LedgerSettings
{
    public const string FileName = "ledger.json";
    public const string SeedFileName = "wallet.seed";

    /// <summary>开发默认节点（本机节点）。与 ⑭ <c>LedgerWalletTool</c> 的默认值保持同一口径。</summary>
    public const string DefaultNodeAddress = "127.0.0.1:17805";

    public const string NodeAddressEnv = "AETHER_LEDGER_NODE_ADDR";
    public const string CustodyAddressEnv = "AETHER_LEDGER_CUSTODY_ADDRESS";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>配置文件中手写的值（未填 = null；空白按未填处理）。</summary>
    public string? NodeAddress { get; private set; }

    /// <summary>配置文件中手写的 custody 地址（40 位 hex；空 = 未配置，充值工具会回指引）。</summary>
    public string? CustodyAddress { get; private set; }

    public string FilePath { get; }

    private LedgerSettings(string filePath) => FilePath = filePath;

    /// <summary>
    /// 读配置。文件不存在 / 空 / 坏 JSON 一律退回「两项都未填」——
    /// 配置读不出来不该让 App 起不来（钱包是可选能力，不是启动前提）。
    /// </summary>
    public static LedgerSettings Load(string dataDirectory)
    {
        var settings = new LedgerSettings(Path.Combine(dataDirectory, FileName));
        try
        {
            if (!File.Exists(settings.FilePath)) return settings;
            var dto = JsonSerializer.Deserialize<LedgerSettingsDto>(File.ReadAllText(settings.FilePath), JsonOptions);
            if (dto is null) return settings;
            settings.NodeAddress = Clean(dto.NodeAddress);
            settings.CustodyAddress = Clean(dto.CustodyAddress);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // 坏配置 = 未配置（下面 Effective* 会回落到 env / 默认值）。
        }
        return settings;
    }

    /// <summary>落盘（用户在设置页点保存时调）。空串 = 清空该项，回落到环境变量/默认。</summary>
    public void Save(string? nodeAddress, string? custodyAddress)
    {
        NodeAddress = Clean(nodeAddress);
        CustodyAddress = Clean(custodyAddress);
        var dir = Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var dto = new LedgerSettingsDto { NodeAddress = NodeAddress, CustodyAddress = CustodyAddress };
        File.WriteAllText(FilePath, JsonSerializer.Serialize(dto, JsonOptions));
    }

    /// <summary>实际生效的节点地址：配置 &gt; 环境变量 &gt; 开发默认。</summary>
    public string EffectiveNodeAddress =>
        NodeAddress ?? Clean(Environment.GetEnvironmentVariable(NodeAddressEnv)) ?? DefaultNodeAddress;

    /// <summary>实际生效的 custody 地址（null = 未配置）。</summary>
    public string? EffectiveCustodyAddress =>
        CustodyAddress ?? Clean(Environment.GetEnvironmentVariable(CustodyAddressEnv));

    /// <summary>节点地址的来源（设置页照实显示——用户要能看出「我填的」还是「环境变量/默认」）。</summary>
    public string NodeAddressSource => NodeAddress is not null
        ? "ledger.json"
        : Clean(Environment.GetEnvironmentVariable(NodeAddressEnv)) is not null ? $"环境变量 {NodeAddressEnv}" : "开发默认";

    /// <summary>设置页的一行状态。</summary>
    public string Describe()
    {
        var custody = EffectiveCustodyAddress is { } c ? $"{c[..Math.Min(12, c.Length)]}…" : "未配置（充值时工具会提示）";
        return $"节点：{EffectiveNodeAddress}（来源：{NodeAddressSource}）　托管地址：{custody}";
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private sealed class LedgerSettingsDto
    {
        public string? NodeAddress { get; set; }
        public string? CustodyAddress { get; set; }
    }
}
