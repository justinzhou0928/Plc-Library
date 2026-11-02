using Microsoft.Extensions.Logging;
using NModbus;
using PlcLibrary.DriverDomain.Attributes;
using PlcLibrary.General.Configuration;
using System;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace PlcLibrary.Modbus
{
    [ProtocolDriverName("Modbus_TCP")]
    public sealed class ModbusTcpDriver : ModbusDriverBase
    {
        public ModbusTcpDriver(ILogger<ModbusTcpDriver> logger, DeviceConfiguration device)
            : base(logger, ModbusDriverConfig.Parse(device.ConnectionString),
                ct => CreateMasterAsync(device.ConnectionString, ct)) { }

        /// <summary>
        /// 异步建立 TCP 连接：不再在构造函数/同步方法里阻塞线程池线程，
        /// 并让 <c>timeout</c> 真正作用于连接阶段（含取消令牌）。
        /// </summary>
        private static async Task<IModbusMaster> CreateMasterAsync(string connectionString, CancellationToken ct)
        {
            var config = ModbusDriverConfig.Parse(connectionString);
            var tcpClient = new TcpClient
            {
                NoDelay = true,
                SendTimeout = config.Timeout,
                ReceiveTimeout = config.Timeout,
            };

            try
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                if (config.Timeout > 0) timeoutCts.CancelAfter(config.Timeout);

                await tcpClient.ConnectAsync(config.Host, config.Port, timeoutCts.Token).ConfigureAwait(false);
                return new ModbusFactory().CreateMaster(tcpClient);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                tcpClient.Dispose();
                throw new TimeoutException(
                    $"Modbus TCP connect to {config.Host}:{config.Port} timed out after {config.Timeout}ms");
            }
            catch
            {
                tcpClient.Dispose();
                throw;
            }
        }
    }
}
