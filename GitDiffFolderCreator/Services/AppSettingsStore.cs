using System;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;

namespace GitDiffFolderCreator.Services
{
    /// <summary>Persisted user settings.</summary>
    [DataContract]
    public sealed class AppSettings
    {
        public AppSettings()
        {
            GitDirectory = string.Empty;
            OutputDirectory = string.Empty;
            LogLimit = 500;
            OpenOutputWhenFinished = true;
            CollapsedSections = string.Empty;
            DiffToolCommand = string.Empty;
            ChangedFilesStartMarker = ChangeDocumentParser.DefaultStartMarker;
            ChangedFilesEndMarker = ChangeDocumentParser.DefaultEndMarker;
        }

        /// <summary>
        /// Coerces the string members that can be absent from an older settings file.
        /// </summary>
        /// <remarks>
        /// <c>DataContractJsonSerializer</c> runs the parameterless constructor and then overwrites
        /// only the members the file actually contains, so a file written before a member existed
        /// should arrive with the constructor's defaults. It does not always: a member present but
        /// null - which a hand-edited or partly written file can carry - arrives as null, and that
        /// would otherwise reach a string comparison as a null reference. Normalised here, once, so
        /// every caller can treat these as non-null.
        /// </remarks>
        [OnDeserializing]
        internal void OnDeserializing(StreamingContext context)
        {
        }

        [OnDeserialized]
        internal void OnDeserialized(StreamingContext context)
        {
            GitDirectory = GitDirectory ?? string.Empty;
            OutputDirectory = OutputDirectory ?? string.Empty;
            CollapsedSections = CollapsedSections ?? string.Empty;
            DiffToolCommand = DiffToolCommand ?? string.Empty;

            // The two markers are the exception to the rule above: a reader who cleared the box did not
            // ask for a document that cannot be read, so an absent or blank marker means the default
            // rather than an empty string. The window does the same to what is typed before it is stored.
            ChangedFilesStartMarker = MarkerOrDefault(
                ChangedFilesStartMarker,
                ChangeDocumentParser.DefaultStartMarker);
            ChangedFilesEndMarker = MarkerOrDefault(
                ChangedFilesEndMarker,
                ChangeDocumentParser.DefaultEndMarker);
        }

        [DataMember(Name = "gitDirectory")]
        public string GitDirectory { get; set; }

        [DataMember(Name = "outputDirectory")]
        public string OutputDirectory { get; set; }

        /// <summary>Number of commits to load into the log list.</summary>
        [DataMember(Name = "logLimit")]
        public int LogLimit { get; set; }

        /// <summary>
        /// Reveal the run folder in File Explorer when an export finishes. The serializer runs the
        /// parameterless constructor, so a settings file written before this option existed keeps the
        /// default rather than reading back as <c>false</c>.
        /// </summary>
        [DataMember(Name = "openOutputWhenFinished")]
        public bool OpenOutputWhenFinished { get; set; }

        /// <summary>
        /// Comma-separated names of the panels that were folded away. Empty means everything is
        /// open, which is also what a settings file without this key means.
        /// </summary>
        [DataMember(Name = "collapsedSections")]
        public string CollapsedSections { get; set; }

        /// <summary>
        /// The diff tool chosen in the picker, as the full command line including the
        /// <c>$LOCAL</c>/<c>$REMOTE</c> placeholders. Empty means "use whatever git has configured",
        /// which is what a settings file without this option means.
        /// </summary>
        /// <remarks>
        /// The command line is stored rather than the identifier alone, because a tool chosen by hand
        /// has no identifier to look up later and because an installed tool may move between runs.
        /// </remarks>
        [DataMember(Name = "diffToolCommand")]
        public string DiffToolCommand { get; set; }

        /// <summary>
        /// The heading the changed-file list starts at, as the document spells it.
        /// </summary>
        /// <remarks>
        /// Stored with the brackets and any colon, because that is how the reader typed it and it is what
        /// they will recognise when they come back to it; the parser strips them before comparing.
        /// </remarks>
        [DataMember(Name = "changedFilesStartMarker")]
        public string ChangedFilesStartMarker { get; set; }

        /// <summary>
        /// The heading the changed-file list ends at. Everything after it belongs to another section.
        /// </summary>
        [DataMember(Name = "changedFilesEndMarker")]
        public string ChangedFilesEndMarker { get; set; }

        /// <summary>
        /// A usable marker: the one given, or the default when none was.
        /// </summary>
        /// <remarks>
        /// Shared with the view model, which has the same problem with a box the reader has just emptied
        /// and which must not save something it could not itself read back.
        /// </remarks>
        internal static string MarkerOrDefault(string? marker, string fallback)
        {
            return string.IsNullOrWhiteSpace(marker) ? fallback : marker!.Trim();
        }
    }

    /// <summary>
    /// Stores settings as JSON under %LocalAppData%.
    /// </summary>
    /// <remarks>
    /// The previous implementation wrote to the application's own <c>.exe.config</c>, which fails
    /// when the application is installed under Program Files, and dereferenced a possibly absent
    /// key. <see cref="DataContractJsonSerializer"/> is used because System.Text.Json does not
    /// support .NET Framework 4.6.1.
    /// </remarks>
    public sealed class AppSettingsStore
    {
        private readonly string settingsPath;

        public AppSettingsStore(string? settingsPath)
        {
            this.settingsPath = settingsPath ?? DefaultSettingsPath();
        }

        public static string DefaultSettingsPath()
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "GitDiffFolderCreator",
                "settings.json");
        }

        public AppSettings Load()
        {
            try
            {
                if (File.Exists(settingsPath))
                {
                    string json = File.ReadAllText(settingsPath, Encoding.UTF8);
                    return Deserialize(json) ?? new AppSettings();
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is SerializationException)
            {
                // A corrupt or unreadable settings file must never stop the application from starting.
            }

            return new AppSettings();
        }

        public void Save(AppSettings settings)
        {
            if (settings == null)
            {
                throw new ArgumentNullException("settings");
            }

            string directory = Path.GetDirectoryName(settingsPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(settingsPath, Serialize(settings), new UTF8Encoding(false));
        }

        private static string Serialize(AppSettings settings)
        {
            DataContractJsonSerializer serializer = new DataContractJsonSerializer(typeof(AppSettings));
            using (MemoryStream stream = new MemoryStream())
            {
                serializer.WriteObject(stream, settings);
                return new UTF8Encoding(false).GetString(stream.ToArray());
            }
        }

        private static AppSettings? Deserialize(string json)
        {
            DataContractJsonSerializer serializer = new DataContractJsonSerializer(typeof(AppSettings));
            using (MemoryStream stream = new MemoryStream(Encoding.UTF8.GetBytes(json)))
            {
                return serializer.ReadObject(stream) as AppSettings;
            }
        }
    }
}