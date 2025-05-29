// File: Models/WatchEntry.cs
using System;
using System.Xml.Serialization;

namespace SyncFiles.Core.Models
{
    [Serializable]
    public class WatchEntry
    {
        [XmlAttribute("watchedPath")]
        public string WatchedPath { get; set; }

        [XmlAttribute("onEventScript")]
        public string OnEventScript { get; set; }

        public WatchEntry()
        {
            WatchedPath = string.Empty;
            OnEventScript = string.Empty;
        }

        public WatchEntry(string watchedPath, string onEventScript)
        {
            WatchedPath = watchedPath != null ? watchedPath.Replace('\\', '/') : string.Empty;
            OnEventScript = onEventScript != null ? onEventScript.Replace('\\', '/') : string.Empty;
        }

        public override bool Equals(object obj)
        {
            if (obj is WatchEntry other)
            {
                return WatchedPath == other.WatchedPath && OnEventScript == other.OnEventScript;
            }
            return false;
        }

        public override int GetHashCode()
        {
            int hash = 17;
            hash = hash * 23 + (WatchedPath?.GetHashCode() ?? 0);
            hash = hash * 23 + (OnEventScript?.GetHashCode() ?? 0);
            return hash;
        }

        public override string ToString()
        {
            return string.Format("WatchedPath: {0}, onEventScript: {1} .", WatchedPath, OnEventScript);
        }
    }
}