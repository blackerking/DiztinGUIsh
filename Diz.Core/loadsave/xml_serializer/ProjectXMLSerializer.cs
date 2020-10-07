﻿using System.IO;
using System.Text;
using System.Xml;
using ExtendedXmlSerializer;

namespace DiztinGUIsh.loadsave.xml_serializer
{
    internal class ProjectXmlSerializer : ProjectSerializer
    {
        // NEVER CHANGE THIS ONE.
      private const int FIRST_SAVE_FORMAT_VERSION = 100;

      // increment this if you change the XML file format
      private const int CURRENT_SAVE_FORMAT_VERSION = FIRST_SAVE_FORMAT_VERSION;

      // update this if we are dropped support for really old save formats.
      private const int EARLIEST_SUPPORTED_SAVE_FORMAT_VERSION = FIRST_SAVE_FORMAT_VERSION;

        internal class Root
        {
            // XML serializer specific metadata, top-level deserializer.
            // This is unique to JUST the XML serializer, doesn't affect any other types of serializers.
            // i.e. there is no global 'save format version' number, it's serializer-specific.
            //
            // NOTE: Please try and keep 'Root' unchanged and as generic as possible.  It's way better
            // to change 'Project'
            public int SaveVersion { get; set; } = -1;
            public string Watermark { get; set; }
            public string Extra1 { get; set; } = ""; // reserved for future use
            public string Extra2 { get; set; } = ""; // reserved for future use

            // The actual project itself. Almost any change you want to make should go in here.
            public Project Project { get; set; }
        };

        public override byte[] Save(Project project)
        {
            // Wrap the project in a top-level root element with some info about the XML file
            // format version. Note that each serializer has its own implementation of storing this metadata
            var rootElement = new Root()
            {
                SaveVersion = CURRENT_SAVE_FORMAT_VERSION,
                Watermark = ProjectSerializer.Watermark,
                Project = project,
            };

            var xmlStr = XmlSerializerSupport.GetSerializer().Serialize(
                new XmlWriterSettings { Indent = true }, 
                rootElement);

            var finalBytes = Encoding.UTF8.GetBytes(xmlStr);

            // if you want some sanity checking, run this to verify everything saved correctly
            DebugVerifyProjectEquality(project, finalBytes);
            // end debug

            return finalBytes;
        }

        // just for debugging purposes, compare two projects together to make sure they serialize/deserialize
        // correctly.
        private void DebugVerifyProjectEquality(Project project1, byte[] finalBytes_project2)
        {
            var project2 = Load(finalBytes_project2);
            ProjectFileManager.PostSerialize(project2, null);
            DebugVerifyProjectEquality(project1, project2);
        }

        public override Project Load(byte[] data)
        {
            // TODO: it would be much more user-friendly/reliable if we could deserialize the
            // Root element ALONE first, check for valid version/watermark, and only then try
            // to deserialize the rest of the doc.
            //
            // Also, we can do data migrations based on versioning, and ExtendedXmlSerializer

            var text = Encoding.UTF8.GetString(data);
            var root = XmlSerializerSupport.GetSerializer().Deserialize<Root>(text);

            if (root.Watermark != Watermark)
                throw new InvalidDataException("This file doesn't appear to be a valid DiztinGUIsh XML file (missing/invalid watermark element in XML)");

            if (root.SaveVersion > CURRENT_SAVE_FORMAT_VERSION)
                throw new InvalidDataException($"Save file version is newer than this version of DiztinGUIsh, likely can't be opened safely. This save file version = '{root.SaveVersion}', our highest supported version is {CURRENT_SAVE_FORMAT_VERSION}");

            // Apply any migrations here for older save file formats. Right now,
            // there aren't any because we're on the first revision.
            // The XML serialization might be fairly forgiving of most kinds of changes,
            // so you may not have to write migrations unless properties are renamed or deleted.
            if (root.SaveVersion < CURRENT_SAVE_FORMAT_VERSION)
            {
                throw new InvalidDataException($"Save file version is newer than this version of DiztinGUIsh, likely can't be opened safely. This save file version = '{root.SaveVersion}', our highest supported version is {CURRENT_SAVE_FORMAT_VERSION}");
            }

            var project = root.Project;

            return project;
        }
    }
}
