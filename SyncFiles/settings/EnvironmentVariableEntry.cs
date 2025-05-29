// File: Settings/EnvironmentVariableEntry.cs
using System;
using System.Xml.Serialization;
namespace SyncFiles.Core.Settings
{
    [Serializable]
    public class EnvironmentVariableEntry
    {
        [XmlAttribute("name")]
        public string Name { get; set; }
        [XmlAttribute("value")]
        public string Value { get; set; }
        public EnvironmentVariableEntry()
        {
            Name = string.Empty;
            Value = string.Empty;
        }
        public EnvironmentVariableEntry(string name, string value)
        {
            Name = name;
            Value = value;
        }
    }
}