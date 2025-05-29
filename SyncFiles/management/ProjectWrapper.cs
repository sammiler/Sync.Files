// File: Settings/ProjectWrapper.cs
using System;
using System.Xml.Serialization;
namespace SyncFiles.Core.Settings
{
    [Serializable]
    [XmlRoot("project")]
    public class ProjectWrapper
    {
        [XmlAttribute("version")]
        public string Version { get; set; }
        [XmlElement("component")]
        public SyncFilesSettingsState SyncFilesComponent { get; set; }
        public ProjectWrapper()
        {
            Version = "4"; // Default, matches IntelliJ's typical project file version
            SyncFilesComponent = new SyncFilesSettingsState();
        }
    }
}