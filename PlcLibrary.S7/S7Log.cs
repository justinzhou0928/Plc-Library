using Microsoft.Extensions.Logging;
using System;

namespace PlcLibrary.S7
{
    internal static partial class S7Log
    {
        [LoggerMessage(Level = LogLevel.Warning,
        Message = "S7 批量写入失败，降级逐个写入")]
        internal static partial void LogBatchWriteFallback(ILogger logger, Exception ex);
    }
}
