using Microsoft.Extensions.Logging.Abstractions;
using PlcLibrary.DriverDomain.Enums;
using PlcLibrary.General.Configuration;
using PlcLibrary.S7;

namespace PlcLibrary.Tests.S7;

public class S7DriverTests
{
    [Fact]
    public async Task ConnectAsync_ConnectionFailure_MarksFaulted()
    {
        // 127.0.0.1:1（tcpmux）无服务，TCP 连接立即被拒绝——验证 ConnectAsync 失败置 Faulted 且不泄漏
        var device = new DeviceConfiguration
        {
            Id = "s7-fail",
            Protocol = "S7",
            ConnectionString = "host:127.0.0.1;port:1;rack:0;slot:1;timeout:2000",
        };
        var driver = new S7Driver(NullLogger<S7Driver>.Instance, device);

        await Assert.ThrowsAnyAsync<Exception>(() => driver.ConnectAsync());

        Assert.Equal(DriverStatus.Faulted, driver.DriverStatus);
    }
}
