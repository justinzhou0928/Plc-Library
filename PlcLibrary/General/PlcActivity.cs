using System.Diagnostics;

namespace PlcLibrary.General
{
    /// <summary>
    /// 分布式追踪 ActivitySource：与 <see cref="PlcMetrics"/> 对称的基础库埋点。
    /// 库只负责创建 span，宿主通过 OpenTelemetry/自定义监听器消费；无监听器时 StartActivity 返回 null，开销可忽略。
    /// </summary>
    internal static class PlcActivity
    {
        internal static readonly ActivitySource Source = new("PlcLibrary", "1.0.3");
    }
}
