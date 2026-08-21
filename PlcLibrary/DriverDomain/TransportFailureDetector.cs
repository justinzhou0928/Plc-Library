using System;
using System.IO;
using System.Net.Sockets;
using System.Reflection;

namespace PlcLibrary.DriverDomain
{
    /// <summary>
    /// 判定异常是否为"连接/传输级"故障（连接已断、socket 失效、IO 超时等）。
    /// 驱动在批量读/写捕获到此类异常时，应额外将 <see cref="Enums.DriverStatus"/> 置为
    /// <c>Faulted</c>，连接池据此丢弃并重建驱动，使断线重连真正生效。
    /// 点位级/业务级错误（如设备返回错误码、地址不存在）不在此列，保持逐点 Bad 语义。
    /// </summary>
    public static class TransportFailureDetector
    {
        public static bool IsTransportFailure(Exception ex)
        {
            ex = Unwrap(ex);
            return ex is SocketException or IOException or TimeoutException or ObjectDisposedException;
        }

        /// <summary>逐层剥开常见的包装异常（AggregateException / TargetInvocationException）。</summary>
        private static Exception Unwrap(Exception ex)
        {
            while (ex is AggregateException { InnerExceptions.Count: 1 } agg)
                ex = agg.InnerExceptions[0]!;

            if (ex is TargetInvocationException { InnerException: { } inner })
                return inner;

            return ex;
        }
    }
}
