// File: Models/Mapping.cs
using System;
using System.Xml.Serialization;

namespace SyncFiles.Core.Models
{
    [Serializable] // Good practice for classes that might be serialized in various ways
    public class Mapping
    {
        [XmlAttribute("sourceUrl")]
        public string SourceUrl { get; set; }

        [XmlAttribute("targetPath")]
        public string TargetPath { get; set; }

        public Mapping()
        {
            SourceUrl = string.Empty;
            TargetPath = string.Empty;
        }

        public Mapping(string sourceUrl, string targetPath)
        {
            SourceUrl = sourceUrl ?? string.Empty;
            TargetPath = targetPath ?? string.Empty;
        }

        public override bool Equals(object obj)
        {
            if (obj is Mapping other)
            {
                return SourceUrl == other.SourceUrl && TargetPath == other.TargetPath;
            }
            return false;
        }

        public override int GetHashCode()
        {
            // Simple hash code combination
            int hash = 17;
            hash = hash * 23 + (SourceUrl?.GetHashCode() ?? 0);
            hash = hash * 23 + (TargetPath?.GetHashCode() ?? 0);
            return hash;
        }

        public override string ToString()
        {
            return string.Format("Mapping{{SourceUrl='{0}', TargetPath='{1}'}}", SourceUrl, TargetPath);
        }
    }
}