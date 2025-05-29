// File: Models/ScriptGroup.cs
using System;
using System.Collections.Generic;
using System.Xml.Serialization;
namespace SyncFiles.Core.Models
{
    [Serializable]
    public class ScriptGroup
    {
        public static readonly string DefaultGroupId = "syncfiles-default-group-id";
        public static readonly string DefaultGroupName = "Default";
        [XmlAttribute("id")]
        public string Id { get; set; }
        [XmlAttribute("name")]
        public string Name { get; set; }
        [XmlArray("scripts")]
        [XmlArrayItem("ScriptEntry")]
        public List<ScriptEntry> Scripts { get; set; }
        public ScriptGroup()
        {
            Id = Guid.NewGuid().ToString();
            Name = string.Empty;
            Scripts = new List<ScriptEntry>();
        }
        public ScriptGroup(string id, string name) : this()
        {
            Id = id;
            Name = name;
        }
        public override bool Equals(object obj)
        {
            if (obj is ScriptGroup other)
            {
                return Id == other.Id;
            }
            return false;
        }
        public override int GetHashCode()
        {
            return Id?.GetHashCode() ?? 0;
        }
        public override string ToString()
        {
            return string.Format("ScriptGroup{{Id='{0}', Name='{1}', ScriptCount={2}}}",
                                 Id, Name, Scripts?.Count ?? 0);
        }
    }
}