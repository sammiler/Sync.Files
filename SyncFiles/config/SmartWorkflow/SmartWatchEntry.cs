// File: Config/SmartWorkflow/SmartWatchEntry.cs
// Requires YamlDotNet NuGet package
using YamlDotNet.Serialization;

namespace SyncFiles.Core.Config.SmartWorkflow
{
    public class SmartWatchEntry
    {
        [YamlMember(Alias = "watchedPath")]
        public string WatchedPath { get; set; }

        [YamlMember(Alias = "onEventScript")]
        public string OnEventScript { get; set; }

        public SmartWatchEntry()
        {
            WatchedPath = string.Empty;
            OnEventScript = string.Empty;
        }
    }
}