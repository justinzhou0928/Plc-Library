using PlcLibrary.DriverDomain.Parser;
using PlcLibrary.Modbus;
using PlcLibrary.S7;

namespace PlcLibrary.Tests.DriverDomain;

/// <summary>
/// 连接串严格校验：未知键必须在构造驱动配置时暴露，不能静默用默认值。
/// 别名（如 S7 的 cpu → CpuType）通过 additionalKeys 显式允许。
/// </summary>
public class ConnectionStringBinderTests
{
    [Fact]
    public void Bind_UnknownKey_ThrowsWithKeyAndAllowedKeys()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => ModbusDriverConfig.Parse("host:127.0.0.1;prot:502"));

        Assert.Contains("prot", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Allowed keys", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Bind_S7CpuAlias_IsAllowed()
    {
        var config = S7DriverConfig.Parse("host:127.0.0.1;port:102;cpu:S71500");

        Assert.Equal(global::S7.Net.CpuType.S71500, config.CpuType);
    }
}
