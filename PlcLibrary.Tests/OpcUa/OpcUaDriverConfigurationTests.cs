using Opc.Ua;
using PlcLibrary.OpcUa;

namespace PlcLibrary.Tests.OpcUa;

/// <summary>
/// 离线校验 OPC UA 客户端配置：不连接服务端也能发现证书存储漏配，
/// 避免 ConnectAsync 在 SelectEndpointAsync 之前抛 BadConfigurationError。
/// </summary>
public class OpcUaDriverConfigurationTests
{
    [Fact]
    public async Task ApplicationConfiguration_WithAllPkiPaths_PassesClientValidation()
    {
        // 使用不存在的临时路径，避免测试落盘；配置校验只要求 StorePath 已配置
        var root = Path.Combine(Path.GetTempPath(), "PlcLibrary.OpcUaTests", Guid.NewGuid().ToString("N"));
        try
        {
            var ownPath = Path.Combine(root, "own");
            var trustedPath = Path.Combine(root, "trusted");
            var rejectedPath = Path.Combine(root, "rejected");
            var issuerPath = Path.Combine(root, "issuer");

            var driverConfig = new OpcUaDriverConfig
            {
                PkiOwnPath = ownPath,
                PkiTrustedPath = trustedPath,
                PkiRejectedPath = rejectedPath,
                PkiIssuerPath = issuerPath,
            };

            var appConfig = OpcUaDriver.CreateApplicationConfig(driverConfig);

            await appConfig.ValidateAsync(ApplicationType.Client, CancellationToken.None);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ApplicationConfiguration_BindsEveryCertificateStorePath()
    {
        var driverConfig = new OpcUaDriverConfig
        {
            PkiOwnPath = "own",
            PkiTrustedPath = "trusted",
            PkiRejectedPath = "rejected",
            PkiIssuerPath = "issuer",
        };

        var appConfig = OpcUaDriver.CreateApplicationConfig(driverConfig);
        var security = appConfig.SecurityConfiguration;

        Assert.Equal("own", security.ApplicationCertificate.StorePath);
        Assert.Equal("trusted", security.TrustedPeerCertificates.StorePath);
        Assert.Equal("issuer", security.TrustedIssuerCertificates.StorePath);
        Assert.Equal("rejected", security.RejectedCertificateStore.StorePath);
        Assert.Equal(CertificateStoreType.Directory, security.TrustedIssuerCertificates.StoreType);
    }
}
