using PlcLibrary.General.Configuration;

namespace PlcLibrary.DriverPool.Models
{
    internal static class ResiliencePipelineKeys
    {
        /// <summary>连接级管线键：跟随连接池（多设备共享同一连接串时共享管线，池回收时一并移除）。</summary>
        public static string Pool(DeviceConfiguration device) => $"Pool:{device.Protocol}|{device.ConnectionString}";
        /// <summary>推送订阅级管线键：每设备独立（随 PushCollector 销毁而移除）。</summary>
        public static string Push(string deviceId) => $"Push:{deviceId}";
    }
}
