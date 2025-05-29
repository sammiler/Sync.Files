// File: Models/ScriptEntry.cs
using System;
using System.IO; // For Path
using System.Xml.Serialization;
namespace SyncFiles.Core.Models
{
    [Serializable]
    public class ScriptEntry
    {
        [XmlAttribute("id")]
        public string Id { get; set; }
        [XmlAttribute("path")]
        public string Path { get; set; } // Relative to pythonScriptPath
        [XmlAttribute("alias")]
        public string Alias { get; set; }
        [XmlAttribute("executionMode")]
        public string ExecutionMode { get; set; } // "terminal" or "directApi"
        [XmlElement("description")] // Changed from @Tag to standard XmlElement
        public string Description { get; set; }
        [XmlIgnore] // Equivalent to Java's transient for XmlSerializer
        public bool IsMissing { get; set; }
        public ScriptEntry()
        {
            Id = Guid.NewGuid().ToString();
            Path = string.Empty;
            Alias = string.Empty;
            ExecutionMode = "terminal";
            Description = string.Empty;
            IsMissing = false;
        }
        public ScriptEntry(string path) : this()
        {
            Path = path != null ? path.Replace('\\', '/') : string.Empty;
        }
        public string GetDisplayName()
        {
            if (!string.IsNullOrEmpty(Alias) && !string.IsNullOrWhiteSpace(Alias)) // Use IsNullOrWhiteSpace for better trim check
            {
                return Alias.Trim();
            }
            if (string.IsNullOrEmpty(Path))
                return "Unnamed Script";
            string fileName = System.IO.Path.GetFileNameWithoutExtension(Path); // More robust way to get filename without extension
            return string.IsNullOrEmpty(fileName) ? System.IO.Path.GetFileName(Path) : fileName;
        }
        public override bool Equals(object obj)
        {
            if (obj is ScriptEntry other)
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
            return string.Format("ScriptEntry{{Id='{0}', Path='{1}', Alias='{2}', Mode='{3}', Missing={4}}}",
                                 Id, Path, Alias, ExecutionMode, IsMissing);
        }
    }
}