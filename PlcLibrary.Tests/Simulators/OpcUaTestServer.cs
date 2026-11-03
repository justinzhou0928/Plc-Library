using System.Net;
using System.Net.Sockets;
using Opc.Ua;
using Opc.Ua.Configuration;
using Opc.Ua.Server;

namespace PlcLibrary.Tests.Simulators;

/// <summary>
/// 最小 OPC UA 服务端（标准节点空间），用于离线验证 OpcUaDriver 的真实会话/读/订阅数据面。
/// 仅监听回环地址、SecurityPolicy=None、匿名登录；只使用 SDK 标准节点（不实现自定义 NodeManager）。
/// </summary>
internal sealed class OpcUaTestServer : IAsyncDisposable
{
    private ApplicationInstance? _application;
    private StandardServer? _server;
    private string? _pkiRoot;

    public int Port { get; private set; }

    public string EndpointUrl => $"opc.tcp://127.0.0.1:{Port}";

    /// <summary>标准节点：Server_ServerStatus_CurrentTime（每秒变化，可用于订阅验证）。</summary>
    public static NodeId CurrentTimeNodeId => new(2258u);

    /// <summary>标准节点：Server_ServerStatus（只读结构体，可用于读取验证）。</summary>
    public static NodeId ServerStatusNodeId => new(2256u);

    public async Task StartAsync(CancellationToken ct = default)
    {
        Port = GetFreeTcpPort();
        _pkiRoot = Path.Combine(Path.GetTempPath(), "plc-ua-test", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_pkiRoot);

        var config = BuildConfiguration(Port, _pkiRoot);
        await config.ValidateAsync(ApplicationType.Server, ct).ConfigureAwait(false);

        var application = new ApplicationInstance(telemetry: null!)
        {
            ApplicationName = config.ApplicationName,
            ApplicationType = ApplicationType.Server,
            ApplicationConfiguration = config,
        };

        await application.CheckApplicationInstanceCertificatesAsync(true, null, ct).ConfigureAwait(false);

        _server = new StandardServer();
        await application.StartAsync(_server).ConfigureAwait(false);
        _application = application;
    }

    public async ValueTask DisposeAsync()
    {
        if (_application is not null)
        {
            try
            { await _application.StopAsync().ConfigureAwait(false); }
            catch { }
            _application = null;
        }

        if (_server is not null)
        {
            try
            { _server.Dispose(); }
            catch { }
            _server = null;
        }

        if (_pkiRoot is not null && Directory.Exists(_pkiRoot))
        {
            try
            { Directory.Delete(_pkiRoot, recursive: true); }
            catch { }
        }
    }

    private static ApplicationConfiguration BuildConfiguration(int port, string pkiRoot) => new()
    {
        ApplicationName = "PlcLibrary.Tests.OpcUaServer",
        ApplicationUri = $"urn:localhost:PlcLibrary:Tests:Server:{port}",
        ProductUri = "urn:plclibrary:tests",
        ApplicationType = ApplicationType.Server,
        SecurityConfiguration = new SecurityConfiguration
        {
            ApplicationCertificate = new CertificateIdentifier
            {
                StoreType = CertificateStoreType.Directory,
                StorePath = Path.Combine(pkiRoot, "own"),
                SubjectName = "CN=PlcLibrary.Tests.OpcUaServer",
            },
            TrustedPeerCertificates = new CertificateTrustList
            {
                StoreType = CertificateStoreType.Directory,
                StorePath = Path.Combine(pkiRoot, "trusted"),
            },
            TrustedIssuerCertificates = new CertificateTrustList
            {
                StoreType = CertificateStoreType.Directory,
                StorePath = Path.Combine(pkiRoot, "issuer"),
            },
            RejectedCertificateStore = new CertificateStoreIdentifier
            {
                StoreType = CertificateStoreType.Directory,
                StorePath = Path.Combine(pkiRoot, "rejected"),
            },
            AutoAcceptUntrustedCertificates = true,
            RejectSHA1SignedCertificates = false,
            AddAppCertToTrustedStore = true,
        },
        TransportQuotas = new TransportQuotas { OperationTimeout = 15000, MaxStringLength = 1_000_000 },
        ServerConfiguration = new ServerConfiguration
        {
            BaseAddresses = { $"opc.tcp://127.0.0.1:{port}" },
            SecurityPolicies =
            [
                new ServerSecurityPolicy
                {
                    SecurityMode = MessageSecurityMode.None,
                    SecurityPolicyUri = SecurityPolicies.None,
                },
            ],
            UserTokenPolicies = [new UserTokenPolicy(UserTokenType.Anonymous)],
            DiagnosticsEnabled = false,
            MaxSessionCount = 10,
            MinSessionTimeout = 10_000,
            MaxSessionTimeout = 3_600_000,
            MaxBrowseContinuationPoints = 10,
            MaxQueryContinuationPoints = 10,
            MaxHistoryContinuationPoints = 10,
        },
        TraceConfiguration = new TraceConfiguration(),
    };

    private static int GetFreeTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
