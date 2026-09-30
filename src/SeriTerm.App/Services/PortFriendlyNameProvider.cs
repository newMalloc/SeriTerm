using System.Management;
using System.Text.RegularExpressions;

namespace SeriTerm.App.Services;

/// <summary>
/// 通过 WMI 取串口的设备友好名，把下拉框里的 <c>COM5</c> 变成 <c>COM5 (USB-SERIAL CH340)</c>。
/// WMI 可能较慢或被策略禁用，因此：异步执行、结果缓存、任何失败都降级为纯端口名。
/// </summary>
public sealed partial class PortFriendlyNameProvider
{
    private IReadOnlyDictionary<string, string>? _cache;

    [GeneratedRegex(@"\((COM\d+)\)", RegexOptions.IgnoreCase)]
    private static partial Regex ComPortRegex();

    public async Task<IReadOnlyDictionary<string, string>> GetAsync(CancellationToken cancellationToken = default)
    {
        if (_cache is not null)
        {
            return _cache;
        }

        var result = await Task.Run(Query, cancellationToken).ConfigureAwait(false);
        _cache = result;
        return result;
    }

    /// <summary>强制下次重新查询（用户点刷新时调用）。</summary>
    public void Invalidate() => _cache = null;

    private static IReadOnlyDictionary<string, string> Query()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name FROM Win32_PnPEntity WHERE Name LIKE '%(COM%'");
            using var devices = searcher.Get();

            foreach (ManagementBaseObject device in devices)
            {
                using (device)
                {
                    if (device["Name"] is not string name)
                    {
                        continue;
                    }

                    var match = ComPortRegex().Match(name);
                    if (!match.Success)
                    {
                        continue;
                    }

                    var portName = match.Groups[1].Value.ToUpperInvariant();
                    var label = name[..match.Index].Trim().TrimEnd('(', ' ').Trim();
                    map[portName] = string.IsNullOrWhiteSpace(label) ? portName : $"{portName} ({label})";
                }
            }
        }
        catch (Exception)
        {
            // WMI 不可用时静默降级
        }

        return map;
    }
}
