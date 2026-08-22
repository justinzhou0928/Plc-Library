using PlcLibrary.AllenBradley;
using PlcLibrary.Bacnet;
using PlcLibrary.Mitsubishi;
using PlcLibrary.Modbus;
using PlcLibrary.Omron;
using PlcLibrary.OpcUa;
using PlcLibrary.S7;
using System.Reflection;
using System.Text;

namespace PlcLibrary.Tests.Drivers;

/// <summary>
/// 配置-消费闭环测试：每个 *DriverConfig 的公共属性必须被对应驱动源码消费
/// （源码中出现 config.X / _config.X 引用），防止"文档声称可配、代码未生效"类问题
/// （曾出现：OPC UA Security、Omron localnode/isudp、BACnet deviceinstance 等）。
/// 已知因第三方库限制无法应用的字段列入 KnownUnused 例外清单（README 已标注）。
/// </summary>
public class DriverConfigConsumptionTests
{
    private static readonly (Type ConfigType, string ProjectDir, Dictionary<string, string> KnownUnused)[] Drivers =
    [
        (typeof(S7DriverConfig), "PlcLibrary.S7", []),
        (typeof(ModbusDriverConfig), "PlcLibrary.Modbus", []),
        (typeof(OpcUaDriverConfig), "PlcLibrary.OpcUa", []),
        (typeof(MitsubishiDriverConfig), "PlcLibrary.Mitsubishi", []),
        (typeof(OmronDriverConfig), "PlcLibrary.Omron", new()
        {
            ["LocalNode"] = "FinsClient 节点号属性只读，无法配置",
            ["DestinyNode"] = "FinsClient 节点号属性只读，无法配置",
            ["IsUdp"] = "FinsClient 仅支持 TCP 传输",
        }),
        (typeof(AllenBradleyDriverConfig), "PlcLibrary.AllenBradley", new()
        {
            ["Timeout"] = "TagClient 无超时 API，由驱动池 OperationTimeout 兜底",
        }),
        (typeof(BacnetDriverConfig), "PlcLibrary.Bacnet", []),
    ];

    [Fact]
    public void AllConfigProperties_AreConsumedByDrivers()
    {
        var repoRoot = FindRepoRoot();
        var failures = new List<string>();

        foreach (var (configType, projectDir, knownUnused) in Drivers)
        {
            var source = LoadProjectSources(repoRoot, projectDir);
            foreach (var prop in configType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (knownUnused.ContainsKey(prop.Name)) continue;

                if (!ReferenceExists(source, prop.Name))
                    failures.Add($"{configType.Name}.{prop.Name} 未被驱动消费：{projectDir} 中无 config.{prop.Name} / _config.{prop.Name} 引用");
            }
        }

        Assert.True(failures.Count == 0, "存在配置字段未被驱动消费（连接串里配了也不会生效）：\n" + string.Join("\n", failures));
    }

    private static bool ReferenceExists(string source, string propertyName)
        => source.Contains($"_config.{propertyName}", StringComparison.OrdinalIgnoreCase)
        || source.Contains($"config.{propertyName}", StringComparison.OrdinalIgnoreCase);

    private static string LoadProjectSources(string repoRoot, string projectDir)
    {
        var sb = new StringBuilder();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(repoRoot, projectDir), "*.cs"))
            sb.AppendLine(File.ReadAllText(file));
        return sb.ToString();
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "PlcLibrary.slnx")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("无法定位仓库根目录（未找到 PlcLibrary.slnx）");
    }
}
