using System;

namespace PlcLibrary.Controller.Engine
{
    // 采集进度心跳：用于识别“任务仍在运行但长时间没有任何进展”的卡死状态。
    internal interface ICollectorHeartbeat
    {
        DateTime LastActivityUtc { get; }

        TimeSpan StallTimeout { get; }
    }
}
