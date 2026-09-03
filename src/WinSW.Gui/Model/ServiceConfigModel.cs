using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;
using WinSW.Gui.Mvvm;
using WinSW.Gui.Localization;

namespace WinSW.Gui.Model
{
    /// <summary>
    /// A read/write view over a WinSW configuration file.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This intentionally does not reuse <c>WinSW.XmlServiceConfig</c>. That type is a
    /// read-only projection, and its constructor has process-wide side effects: it calls
    /// <see cref="Environment.SetEnvironmentVariable(string, string)"/> for <c>BASE</c>,
    /// <c>SERVICE_ID</c>, the wrapper executable path and every <c>&lt;env&gt;</c> entry in
    /// the file. A GUI loads many configurations in one process, so those writes would
    /// leak from one service into the next and into anything the GUI later launches.
    /// </para>
    /// <para>
    /// Element and attribute names below mirror <c>XmlServiceConfig</c> exactly. When that
    /// parser changes, this must change with it.
    /// </para>
    /// </remarks>
    /// <summary>
    /// Validation messages by field name, with a null-returning indexer so a binding such as
    /// <c>{Binding FieldErrors[StopTimeout]}</c> is simply empty for a clean field.
    /// </summary>
    public sealed class FieldErrorMap : ObservableObject
    {
        private readonly Dictionary<string, string> errors = new(StringComparer.Ordinal);

        public string? this[string field] => this.errors.TryGetValue(field, out string? message) ? message : null;

        public int Count => this.errors.Count;

        internal void Replace(Dictionary<string, string> fresh)
        {
            this.errors.Clear();
            foreach (var pair in fresh)
            {
                this.errors[pair.Key] = pair.Value;
            }

            this.Raise("Item[]");
            this.Raise(nameof(this.Count));
        }
    }

    public sealed class ServiceConfigModel : ObservableObject
    {
        /// <summary>Time suffixes accepted by <c>XmlServiceConfig.ParseTimeSpan</c>.</summary>
        public static readonly string[] TimeSuffixes =
        {
            "ms", "sec", "secs", "min", "mins", "hr", "hrs", "hour", "hours", "day", "days",
        };

        public static readonly string[] StartModes = { "Automatic", "Manual", "Boot", "System" };

        public static readonly string[] Priorities =
        {
            "Normal", "Idle", "High", "RealTime", "BelowNormal", "AboveNormal",
        };

        public static readonly string[] LogModes =
        {
            "append", "none", "reset", "roll", "roll-by-time", "roll-by-size", "roll-by-size-time", "rotate",
        };

        /// <summary>
        /// The document the model was loaded from, kept so that saving rewrites the user's
        /// own file in place: comments, formatting and unknown elements all survive.
        /// </summary>
        private XmlDocument? source;

        private string? filePath;
        private string id = string.Empty;
        private string? displayName;
        private string? description;
        private string executable = string.Empty;
        private string? arguments;
        private string? startArguments;
        private string? stopExecutable;
        private string? stopArguments;
        private string? workingDirectory;
        private string priority = "Normal";
        private string? stopTimeout;
        private bool hideWindow;
        private string startMode = "Automatic";
        private bool delayedAutoStart;
        private bool interactive;
        private bool beepOnShutdown;
        private bool preshutdown;
        private string? preshutdownTimeout;
        private bool autoRefresh = true;
        private string? securityDescriptor;
        private string? serviceAccountUser;
        private string? serviceAccountPassword;
        private bool allowServiceLogon;
        private string? serviceAccountPrompt;
        private string? resetFailureAfter;
        private string? logPath;
        private string logMode = "append";
        private string? logName;
        private bool outFileDisabled;
        private bool errFileDisabled;
        private string? outFilePattern;
        private string? errFilePattern;
        private string? rollPattern;
        private string? rollPeriod;
        private string? keepFiles;
        private string? sizeThreshold;
        private string? autoRollAtTime;
        private string? zipOlderThanNumDays;
        private string? zipDateFormat;
        private string? extensionsXml;

        public string? FilePath
        {
            get => this.filePath;
            set => this.Set(ref this.filePath, value);
        }

        /// <summary>Refreshed by <see cref="Validate"/>; the editor binds field borders to it.</summary>
        public FieldErrorMap FieldErrors { get; } = new();

        // Identity -----------------------------------------------------------

        public string Id
        {
            get => this.id;
            set => this.Set(ref this.id, value);
        }

        public string? DisplayName
        {
            get => this.displayName;
            set => this.Set(ref this.displayName, value);
        }

        public string? Description
        {
            get => this.description;
            set => this.Set(ref this.description, value);
        }

        // Executable ---------------------------------------------------------

        public string Executable
        {
            get => this.executable;
            set => this.Set(ref this.executable, value);
        }

        public string? Arguments
        {
            get => this.arguments;
            set => this.Set(ref this.arguments, value);
        }

        public string? StartArguments
        {
            get => this.startArguments;
            set => this.Set(ref this.startArguments, value);
        }

        public string? StopExecutable
        {
            get => this.stopExecutable;
            set => this.Set(ref this.stopExecutable, value);
        }

        public string? StopArguments
        {
            get => this.stopArguments;
            set => this.Set(ref this.stopArguments, value);
        }

        public string? WorkingDirectory
        {
            get => this.workingDirectory;
            set => this.Set(ref this.workingDirectory, value);
        }

        public string Priority
        {
            get => this.priority;
            set => this.Set(ref this.priority, value);
        }

        public string? StopTimeout
        {
            get => this.stopTimeout;
            set => this.Set(ref this.stopTimeout, value);
        }

        public bool HideWindow
        {
            get => this.hideWindow;
            set => this.Set(ref this.hideWindow, value);
        }

        // Service management -------------------------------------------------

        public string StartMode
        {
            get => this.startMode;
            set
            {
                if (this.Set(ref this.startMode, value))
                {
                    this.Raise(nameof(this.SupportsDelayedAutoStart));
                }
            }
        }

        /// <summary>
        /// The wrapper only applies <c>delayedAutoStart</c> when the start mode is
        /// <c>Automatic</c>; the editor greys the option out otherwise.
        /// </summary>
        public bool SupportsDelayedAutoStart =>
            string.Equals(this.startMode, "Automatic", StringComparison.OrdinalIgnoreCase);

        public bool DelayedAutoStart
        {
            get => this.delayedAutoStart;
            set => this.Set(ref this.delayedAutoStart, value);
        }

        public bool Interactive
        {
            get => this.interactive;
            set => this.Set(ref this.interactive, value);
        }

        public bool BeepOnShutdown
        {
            get => this.beepOnShutdown;
            set => this.Set(ref this.beepOnShutdown, value);
        }

        public bool Preshutdown
        {
            get => this.preshutdown;
            set => this.Set(ref this.preshutdown, value);
        }

        public string? PreshutdownTimeout
        {
            get => this.preshutdownTimeout;
            set => this.Set(ref this.preshutdownTimeout, value);
        }

        public bool AutoRefresh
        {
            get => this.autoRefresh;
            set => this.Set(ref this.autoRefresh, value);
        }

        public string? SecurityDescriptor
        {
            get => this.securityDescriptor;
            set => this.Set(ref this.securityDescriptor, value);
        }

        public ObservableCollection<DependencyItem> Dependencies { get; } = new();

        // Service account ----------------------------------------------------

        public string? ServiceAccountUser
        {
            get => this.serviceAccountUser;
            set => this.Set(ref this.serviceAccountUser, value);
        }

        public string? ServiceAccountPassword
        {
            get => this.serviceAccountPassword;
            set => this.Set(ref this.serviceAccountPassword, value);
        }

        public bool AllowServiceLogon
        {
            get => this.allowServiceLogon;
            set => this.Set(ref this.allowServiceLogon, value);
        }

        /// <summary>One of <c>dialog</c>, <c>console</c>, or null.</summary>
        public string? ServiceAccountPrompt
        {
            get => this.serviceAccountPrompt;
            set => this.Set(ref this.serviceAccountPrompt, value);
        }

        // Failure actions ----------------------------------------------------

        public ObservableCollection<FailureAction> FailureActions { get; } = new();

        public string? ResetFailureAfter
        {
            get => this.resetFailureAfter;
            set => this.Set(ref this.resetFailureAfter, value);
        }

        // Logging ------------------------------------------------------------

        public string? LogPath
        {
            get => this.logPath;
            set => this.Set(ref this.logPath, value);
        }

        public string LogMode
        {
            get => this.logMode;
            set
            {
                // A ComboBox whose ItemsSource resolves after its SelectedItem binding writes
                // null straight back into the source. The mode is an enumeration and has no
                // empty member, so a blank is that accident and never an intention: an empty
                // mode reaches the wrapper as "Undefined logging mode" and the service will
                // not start.
                if (string.IsNullOrWhiteSpace(value))
                {
                    return;
                }

                if (this.Set(ref this.logMode, value))
                {
                    this.Raise(nameof(this.UsesTimePattern));
                    this.Raise(nameof(this.UsesSizeThreshold));
                    this.Raise(nameof(this.UsesKeepFiles));
                    this.Raise(nameof(this.UsesZipOptions));
                }
            }
        }

        public bool UsesTimePattern =>
            this.logMode is "roll-by-time" or "roll-by-size-time";

        public bool UsesSizeThreshold =>
            this.logMode is "roll-by-size" or "roll-by-size-time";

        public bool UsesKeepFiles =>
            this.logMode is "roll-by-size" or "roll-by-time";

        public bool UsesZipOptions => this.logMode is "roll-by-size-time";

        public string? LogName
        {
            get => this.logName;
            set => this.Set(ref this.logName, value);
        }

        public bool OutFileDisabled
        {
            get => this.outFileDisabled;
            set => this.Set(ref this.outFileDisabled, value);
        }

        public bool ErrFileDisabled
        {
            get => this.errFileDisabled;
            set => this.Set(ref this.errFileDisabled, value);
        }

        public string? OutFilePattern
        {
            get => this.outFilePattern;
            set => this.Set(ref this.outFilePattern, value);
        }

        public string? ErrFilePattern
        {
            get => this.errFilePattern;
            set => this.Set(ref this.errFilePattern, value);
        }

        public string? RollPattern
        {
            get => this.rollPattern;
            set => this.Set(ref this.rollPattern, value);
        }

        public string? RollPeriod
        {
            get => this.rollPeriod;
            set => this.Set(ref this.rollPeriod, value);
        }

        public string? KeepFiles
        {
            get => this.keepFiles;
            set => this.Set(ref this.keepFiles, value);
        }

        /// <summary>Size threshold in KB, matching the wrapper's own unit.</summary>
        public string? SizeThreshold
        {
            get => this.sizeThreshold;
            set => this.Set(ref this.sizeThreshold, value);
        }

        public string? AutoRollAtTime
        {
            get => this.autoRollAtTime;
            set => this.Set(ref this.autoRollAtTime, value);
        }

        public string? ZipOlderThanNumDays
        {
            get => this.zipOlderThanNumDays;
            set => this.Set(ref this.zipOlderThanNumDays, value);
        }

        public string? ZipDateFormat
        {
            get => this.zipDateFormat;
            set => this.Set(ref this.zipDateFormat, value);
        }

        // Environment --------------------------------------------------------

        public ObservableCollection<EnvironmentVariable> EnvironmentVariables { get; } = new();

        public ObservableCollection<DownloadItem> Downloads { get; } = new();

        public ObservableCollection<DriveMapping> SharedDirectories { get; } = new();

        // Lifecycle hooks ----------------------------------------------------

        public ProcessCommandModel Prestart { get; } = new();

        public ProcessCommandModel Poststart { get; } = new();

        public ProcessCommandModel Prestop { get; } = new();

        public ProcessCommandModel Poststop { get; } = new();

        // Extensions ---------------------------------------------------------

        /// <summary>
        /// The <c>&lt;extensions&gt;</c> element verbatim, or null. The GUI has no form for
        /// extension configuration; it is edited as XML and written back unchanged.
        /// </summary>
        public string? ExtensionsXml
        {
            get => this.extensionsXml;
            set => this.Set(ref this.extensionsXml, value);
        }

        // Loading ------------------------------------------------------------

        public static ServiceConfigModel CreateNew() => new();

        /// <exception cref="InvalidDataException">The file is not a WinSW configuration.</exception>
        public static ServiceConfigModel Load(string path)
        {
            var document = new XmlDocument
            {
                // The wrapper never resolves external entities, and neither should the editor.
                XmlResolver = null,
            };

            try
            {
                document.Load(path);
            }
            catch (XmlException e)
            {
                throw new InvalidDataException(e.Message, e);
            }

            var root = document.SelectSingleNode("service") as XmlElement
                ?? throw new InvalidDataException("<service> is missing in configuration XML.");

            var model = new ServiceConfigModel
            {
                source = document,
                FilePath = Path.GetFullPath(path),
            };

            model.ReadFrom(root);
            return model;
        }

        /// <summary>Builds a model from XML text, e.g. from the editor's raw-XML mode.</summary>
        /// <exception cref="InvalidDataException">The text is not a WinSW configuration.</exception>
        public static ServiceConfigModel FromXml(string xml, string? filePath)
        {
            var document = new XmlDocument { XmlResolver = null };
            try
            {
                document.LoadXml(xml);
            }
            catch (XmlException e)
            {
                throw new InvalidDataException(e.Message, e);
            }

            var root = document.SelectSingleNode("service") as XmlElement
                ?? throw new InvalidDataException("<service> is missing in configuration XML.");

            var model = new ServiceConfigModel { source = document };
            model.ReadFrom(root);
            model.FilePath = filePath;
            return model;
        }

        private void ReadFrom(XmlElement root)
        {
            this.id = Text(root, "id") ?? string.Empty;
            this.displayName = Text(root, "name");
            this.description = Text(root, "description");
            this.executable = Text(root, "executable") ?? string.Empty;
            this.arguments = Text(root, "arguments");
            this.startArguments = Text(root, "startarguments");
            this.stopExecutable = Text(root, "stopexecutable");
            this.stopArguments = Text(root, "stoparguments");
            this.workingDirectory = Text(root, "workingdirectory");
            this.priority = Text(root, "priority") ?? "Normal";
            this.stopTimeout = Text(root, "stoptimeout");
            this.hideWindow = Bool(root, "hidewindow");
            this.startMode = Text(root, "startmode") ?? "Automatic";
            this.delayedAutoStart = Bool(root, "delayedAutoStart");
            this.interactive = Bool(root, "interactive");
            this.beepOnShutdown = Bool(root, "beeponshutdown");
            this.preshutdown = Bool(root, "preshutdown");
            this.preshutdownTimeout = Text(root, "preshutdownTimeout");
            this.autoRefresh = Bool(root, "autoRefresh", true);
            this.securityDescriptor = Text(root, "securityDescriptor");
            this.resetFailureAfter = Text(root, "resetfailure");

            this.logPath = Text(root, "logpath");
            this.logName = Text(root, "logname");
            this.outFileDisabled = Bool(root, "outfiledisabled");
            this.errFileDisabled = Bool(root, "errfiledisabled");
            this.outFilePattern = Text(root, "outfilepattern");
            this.errFilePattern = Text(root, "errfilepattern");

            // <logmode> is the legacy spelling and wins over <log mode="">, exactly as the parser does.
            var legacyLogMode = root.SelectSingleNode("logmode") as XmlElement;
            var logElement = root.SelectSingleNode("log") as XmlElement;
            this.logMode = legacyLogMode?.InnerText.Trim()
                ?? (logElement is null ? null : NullIfEmpty(logElement.GetAttribute("mode")))
                ?? "append";

            var logSettings = legacyLogMode ?? logElement;
            if (logSettings != null)
            {
                this.rollPattern = Text(logSettings, "pattern");
                this.rollPeriod = Text(logSettings, "period");
                this.keepFiles = Text(logSettings, "keepFiles");
                this.sizeThreshold = Text(logSettings, "sizeThreshold");
                this.autoRollAtTime = Text(logSettings, "autoRollAtTime");
                this.zipOlderThanNumDays = Text(logSettings, "zipOlderThanNumDays");
                this.zipDateFormat = Text(logSettings, "zipDateFormat");
            }

            var account = root.SelectSingleNode("serviceaccount") as XmlElement;
            if (account != null)
            {
                this.serviceAccountUser = Text(account, "username");
                this.serviceAccountPassword = Text(account, "password");
                this.allowServiceLogon = Bool(account, "allowservicelogon");
                this.serviceAccountPrompt = Text(account, "prompt");
            }

            foreach (XmlElement element in root.SelectNodes("depend")!.OfType<XmlElement>())
            {
                this.Dependencies.Add(new DependencyItem { ServiceName = element.InnerText.Trim() });
            }

            foreach (XmlElement element in root.SelectNodes("env")!.OfType<XmlElement>())
            {
                this.EnvironmentVariables.Add(new EnvironmentVariable
                {
                    Name = element.GetAttribute("name"),
                    Value = element.GetAttribute("value"),
                });
            }

            foreach (XmlElement element in root.SelectNodes("download")!.OfType<XmlElement>())
            {
                this.Downloads.Add(new DownloadItem
                {
                    From = element.GetAttribute("from"),
                    To = element.GetAttribute("to"),
                    Auth = NullIfEmpty(element.GetAttribute("auth")) ?? "none",
                    User = NullIfEmpty(element.GetAttribute("user")),
                    Password = NullIfEmpty(element.GetAttribute("password")),
                    UnsecureAuth = ParseBool(element.GetAttribute("unsecureAuth")),
                    FailOnError = ParseBool(element.GetAttribute("failOnError")),
                    Proxy = NullIfEmpty(element.GetAttribute("proxy")),
                });
            }

            foreach (XmlElement element in root.SelectNodes("onfailure")!.OfType<XmlElement>())
            {
                this.FailureActions.Add(new FailureAction
                {
                    Action = NullIfEmpty(element.GetAttribute("action")) ?? "restart",
                    Delay = NullIfEmpty(element.GetAttribute("delay")),
                });
            }

            foreach (XmlElement element in root.SelectNodes("sharedDirectoryMapping/map")!.OfType<XmlElement>())
            {
                this.SharedDirectories.Add(new DriveMapping
                {
                    Label = element.GetAttribute("label"),
                    UncPath = element.GetAttribute("uncpath"),
                });
            }

            ReadHook(root, "prestart", this.Prestart);
            ReadHook(root, "poststart", this.Poststart);
            ReadHook(root, "prestop", this.Prestop);
            ReadHook(root, "poststop", this.Poststop);

            this.extensionsXml = (root.SelectSingleNode("extensions") as XmlElement)?.OuterXml;

            static void ReadHook(XmlElement parent, string name, ProcessCommandModel hook)
            {
                if (parent.SelectSingleNode(name) is not XmlElement element)
                {
                    return;
                }

                // Same child names the wrapper's SettingNames uses for ProcessCommand.
                hook.Executable = Text(element, "executable");
                hook.Arguments = Text(element, "arguments");
                hook.StdoutPath = Text(element, "stdoutPath");
                hook.StderrPath = Text(element, "stderrPath");
            }

            static string? Text(XmlElement parent, string name) =>
                NullIfEmpty(parent.SelectSingleNode(name)?.InnerText.Trim());

            static bool Bool(XmlElement parent, string name, bool defaultValue = false) =>
                parent.SelectSingleNode(name) is { } node ? ParseBool(node.InnerText, defaultValue) : defaultValue;
        }

        // Saving -------------------------------------------------------------

        /// <summary>
        /// Writes the configuration to <paramref name="path"/>, updating the document the
        /// model was loaded from so comments and hand formatting are preserved.
        /// </summary>
        public void Save(string path)
        {
            var document = this.BuildDocument();

            var settings = new XmlWriterSettings
            {
                Indent = true,
                IndentChars = "  ",
                Encoding = new UTF8Encoding(false),
                NewLineChars = "\r\n",
            };

            // Write to a temporary file first: a half-written configuration next to an
            // installed service is worse than no write at all.
            string directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
            string temporary = Path.Combine(directory, Path.GetFileName(path) + ".tmp");

            using (var writer = XmlWriter.Create(temporary, settings))
            {
                document.Save(writer);
            }

            if (File.Exists(path))
            {
                File.Replace(temporary, path, null);
            }
            else
            {
                File.Move(temporary, path);
            }

            this.source = document;
            this.FilePath = Path.GetFullPath(path);
        }

        /// <summary>
        /// Applies the model onto the backing document, creating one if the model was not
        /// loaded from a file. Safe to call repeatedly; the preview relies on that.
        /// </summary>
        private XmlDocument BuildDocument()
        {
            XmlDocument document;
            XmlElement root;

            if (this.source != null && this.source.SelectSingleNode("service") is XmlElement existing)
            {
                document = this.source;
                root = existing;
            }
            else
            {
                document = new XmlDocument { XmlResolver = null };
                document.AppendChild(document.CreateXmlDeclaration("1.0", "UTF-8", null));
                root = (XmlElement)document.AppendChild(document.CreateElement("service"))!;
            }

            SetText(root, "id", this.id);
            SetText(root, "name", this.displayName);
            SetText(root, "description", this.description);
            SetText(root, "executable", this.executable);
            SetText(root, "arguments", this.arguments);
            SetText(root, "startarguments", this.startArguments);
            SetText(root, "stopexecutable", this.stopExecutable);
            SetText(root, "stoparguments", this.stopArguments);
            SetText(root, "workingdirectory", this.workingDirectory);
            SetText(root, "priority", string.Equals(this.priority, "Normal", StringComparison.OrdinalIgnoreCase) ? null : this.priority);
            SetText(root, "stoptimeout", this.stopTimeout);
            SetBool(root, "hidewindow", this.hideWindow, false);
            SetText(root, "startmode", string.Equals(this.startMode, "Automatic", StringComparison.OrdinalIgnoreCase) ? null : this.startMode);

            // The wrapper ignores delayedAutoStart unless the start mode is Automatic;
            // writing it in any other mode would only mislead whoever reads the file.
            SetBool(root, "delayedAutoStart", this.SupportsDelayedAutoStart && this.delayedAutoStart, false);
            SetBool(root, "interactive", this.interactive, false);
            SetBool(root, "beeponshutdown", this.beepOnShutdown, false);
            SetBool(root, "preshutdown", this.preshutdown, false);
            SetText(root, "preshutdownTimeout", this.preshutdownTimeout);
            SetBool(root, "autoRefresh", this.autoRefresh, true);
            SetText(root, "securityDescriptor", this.securityDescriptor);
            SetText(root, "resetfailure", this.resetFailureAfter);
            SetText(root, "logpath", this.logPath);
            SetText(root, "logname", this.logName);
            SetBool(root, "outfiledisabled", this.outFileDisabled, false);
            SetBool(root, "errfiledisabled", this.errFileDisabled, false);
            SetText(root, "outfilepattern", this.outFilePattern);
            SetText(root, "errfilepattern", this.errFilePattern);

            this.SaveLog(document, root);
            this.SaveServiceAccount(document, root);

            ReplaceAll(document, root, "depend", this.Dependencies, static (element, item) => element.InnerText = item.ServiceName);

            ReplaceAll(document, root, "env", this.EnvironmentVariables, static (element, item) =>
            {
                element.SetAttribute("name", item.Name);
                element.SetAttribute("value", item.Value);
            });

            ReplaceAll(document, root, "download", this.Downloads, static (element, item) =>
            {
                element.SetAttribute("from", item.From);
                element.SetAttribute("to", item.To);
                SetAttribute(element, "auth", string.Equals(item.Auth, "none", StringComparison.OrdinalIgnoreCase) ? null : item.Auth);
                SetAttribute(element, "user", item.User);
                SetAttribute(element, "password", item.Password);
                SetAttribute(element, "unsecureAuth", item.UnsecureAuth ? "true" : null);
                SetAttribute(element, "failOnError", item.FailOnError ? "true" : null);
                SetAttribute(element, "proxy", item.Proxy);
            });

            // An action attribute is mandatory and has no empty member; a row without one is
            // dropped rather than written as action="", which the wrapper cannot parse.
            ReplaceAll(document, root, "onfailure", this.FailureActions.Where(a => !string.IsNullOrWhiteSpace(a.Action)), static (element, item) =>
            {
                element.SetAttribute("action", item.Action);
                SetAttribute(element, "delay", item.Delay);
            });

            this.SaveSharedDirectories(document, root);
            SaveHook(document, root, "prestart", this.Prestart);
            SaveHook(document, root, "poststart", this.Poststart);
            SaveHook(document, root, "prestop", this.Prestop);
            SaveHook(document, root, "poststop", this.Poststop);
            this.SaveExtensions(document, root);

            this.source = document;
            return document;
        }

        private void SaveLog(XmlDocument document, XmlElement root)
        {
            // Collapse onto the modern <log mode=""> form and drop the legacy <logmode>,
            // so there is only one place the mode can come from.
            RemoveAll(root, "logmode");

            var element = root.SelectSingleNode("log") as XmlElement;

            if (string.IsNullOrWhiteSpace(this.logMode))
            {
                // No mode is not the same as an empty one: absent means append, while
                // <log mode=""> is a file the wrapper refuses to start from.
                RemoveAll(root, "log");
                return;
            }

            if (element is null)
            {
                element = document.CreateElement("log");
                root.AppendChild(element);
            }

            element.SetAttribute("mode", this.logMode);

            SetOrRemove(element, "pattern", this.UsesTimePattern ? this.rollPattern : null);
            SetOrRemove(element, "period", this.logMode == "roll-by-time" ? this.rollPeriod : null);
            SetOrRemove(element, "keepFiles", this.UsesKeepFiles ? this.keepFiles : null);
            SetOrRemove(element, "sizeThreshold", this.UsesSizeThreshold ? this.sizeThreshold : null);
            SetOrRemove(element, "autoRollAtTime", this.UsesZipOptions ? this.autoRollAtTime : null);
            SetOrRemove(element, "zipOlderThanNumDays", this.UsesZipOptions ? this.zipOlderThanNumDays : null);
            SetOrRemove(element, "zipDateFormat", this.UsesZipOptions ? this.zipDateFormat : null);

            void SetOrRemove(XmlElement parent, string name, string? value)
            {
                RemoveAll(parent, name);
                if (!string.IsNullOrWhiteSpace(value))
                {
                    var child = document.CreateElement(name);
                    child.InnerText = value!.Trim();
                    parent.AppendChild(child);
                }
            }
        }

        private void SaveSharedDirectories(XmlDocument document, XmlElement root)
        {
            if (this.SharedDirectories.Count == 0)
            {
                RemoveAll(root, "sharedDirectoryMapping");
                return;
            }

            var container = root.SelectSingleNode("sharedDirectoryMapping") as XmlElement;
            if (container is null)
            {
                container = document.CreateElement("sharedDirectoryMapping");
                root.AppendChild(container);
            }

            ReplaceAll(document, container, "map", this.SharedDirectories, static (element, item) =>
            {
                element.SetAttribute("label", item.Label);
                element.SetAttribute("uncpath", item.UncPath);
            });
        }

        private static void SaveHook(XmlDocument document, XmlElement root, string name, ProcessCommandModel hook)
        {
            if (hook.IsEmpty)
            {
                RemoveAll(root, name);
                return;
            }

            var element = root.SelectSingleNode(name) as XmlElement;
            if (element is null)
            {
                element = document.CreateElement(name);
                root.AppendChild(element);
            }

            SetText(element, "executable", hook.Executable);
            SetText(element, "arguments", hook.Arguments);
            SetText(element, "stdoutPath", hook.StdoutPath);
            SetText(element, "stderrPath", hook.StderrPath);
        }

        private void SaveExtensions(XmlDocument document, XmlElement root)
        {
            var existing = root.SelectSingleNode("extensions");

            if (string.IsNullOrWhiteSpace(this.extensionsXml))
            {
                if (existing != null)
                {
                    root.RemoveChild(existing);
                }

                return;
            }

            // Validate() has already confirmed this parses; a stale value cannot get here.
            var fragment = document.CreateDocumentFragment();
            fragment.InnerXml = this.extensionsXml!;
            var replacement = fragment.SelectSingleNode("extensions") ?? fragment.FirstChild!;

            if (existing != null)
            {
                root.ReplaceChild(replacement, existing);
            }
            else
            {
                root.AppendChild(replacement);
            }
        }

        private void SaveServiceAccount(XmlDocument document, XmlElement root)
        {
            bool any = !string.IsNullOrWhiteSpace(this.serviceAccountUser)
                || !string.IsNullOrWhiteSpace(this.serviceAccountPassword)
                || !string.IsNullOrWhiteSpace(this.serviceAccountPrompt)
                || this.allowServiceLogon;

            if (!any)
            {
                RemoveAll(root, "serviceaccount");
                return;
            }

            var element = root.SelectSingleNode("serviceaccount") as XmlElement;
            if (element is null)
            {
                element = document.CreateElement("serviceaccount");
                root.AppendChild(element);
            }

            SetText(element, "username", this.serviceAccountUser);
            SetText(element, "password", this.serviceAccountPassword);
            SetText(element, "prompt", this.serviceAccountPrompt);
            SetBool(element, "allowservicelogon", this.allowServiceLogon, false);
        }

        // Validation ---------------------------------------------------------

        /// <summary>
        /// Returns the problems that would make the wrapper reject this configuration.
        /// </summary>
        public IReadOnlyList<string> Validate()
        {
            var problems = new List<string>();
            var byField = new Dictionary<string, string>(StringComparer.Ordinal);

            void Add(string field, string message)
            {
                problems.Add(message);
                if (!byField.ContainsKey(field))
                {
                    byField[field] = message;
                }
            }

            if (string.IsNullOrWhiteSpace(this.id))
            {
                Add(nameof(this.Id), Localizer.Get("M.Val.IdRequired"));
            }
            else if (this.id.IndexOfAny(new[] { ' ', '/', '\\' }) >= 0)
            {
                Add(nameof(this.Id), Localizer.Get("M.Val.IdChars"));
            }

            if (string.IsNullOrWhiteSpace(this.executable))
            {
                Add(nameof(this.Executable), Localizer.Get("M.Val.ExeRequired"));
            }

            CheckTime(nameof(this.StopTimeout), this.stopTimeout, Localizer.Get("M.Val.StopTimeout"));
            CheckTime(nameof(this.PreshutdownTimeout), this.preshutdownTimeout, Localizer.Get("M.Val.PreshutdownTimeout"));
            CheckTime(nameof(this.ResetFailureAfter), this.resetFailureAfter, Localizer.Get("M.Val.ResetFailure"));

            foreach (var action in this.FailureActions)
            {
                if (string.IsNullOrWhiteSpace(action.Action))
                {
                    Add(nameof(this.FailureActions), Localizer.Get("M.Val.FailureActionRequired"));
                    continue;
                }

                CheckTime(nameof(this.FailureActions), action.Delay, Localizer.Format("M.Val.FailureDelay", action.Action));
            }

            if (this.UsesTimePattern && string.IsNullOrWhiteSpace(this.rollPattern))
            {
                Add(nameof(this.RollPattern), Localizer.Format("M.Val.PatternRequired", this.logMode));
            }

            CheckInt(nameof(this.RollPeriod), this.rollPeriod, Localizer.Get("M.Val.RollPeriod"));
            CheckInt(nameof(this.KeepFiles), this.keepFiles, Localizer.Get("M.Val.KeepFiles"));
            CheckInt(nameof(this.SizeThreshold), this.sizeThreshold, Localizer.Get("M.Val.SizeThreshold"));
            CheckInt(nameof(this.ZipOlderThanNumDays), this.zipOlderThanNumDays, Localizer.Get("M.Val.ZipDays"));

            if (this.UsesZipOptions
                && !string.IsNullOrWhiteSpace(this.autoRollAtTime)
                && !TimeSpan.TryParse(this.autoRollAtTime, out _))
            {
                Add(nameof(this.AutoRollAtTime), Localizer.Get("M.Val.BadAutoRoll"));
            }

            foreach (var variable in this.EnvironmentVariables)
            {
                if (string.IsNullOrWhiteSpace(variable.Name))
                {
                    Add(nameof(this.EnvironmentVariables), Localizer.Get("M.Val.EnvName"));
                }
            }

            foreach (var download in this.Downloads)
            {
                if (string.IsNullOrWhiteSpace(download.From) || string.IsNullOrWhiteSpace(download.To))
                {
                    Add(nameof(this.Downloads), Localizer.Get("M.Val.DownloadIncomplete"));
                    continue;
                }

                // Mirrors the wrapper's own refusal to send Basic credentials in the clear.
                if (string.Equals(download.Auth, "basic", StringComparison.OrdinalIgnoreCase)
                    && !download.From.StartsWith("https:", StringComparison.OrdinalIgnoreCase)
                    && !download.UnsecureAuth)
                {
                    Add(nameof(this.Downloads), Localizer.Format("M.Val.BasicInsecure", download.From));
                }
            }

            if (!string.IsNullOrWhiteSpace(this.extensionsXml))
            {
                try
                {
                    var probe = new XmlDocument { XmlResolver = null };
                    probe.LoadXml(this.extensionsXml!);
                    if (probe.DocumentElement?.Name != "extensions")
                    {
                        Add(nameof(this.ExtensionsXml), Localizer.Get("M.Val.ExtensionsRoot"));
                    }
                }
                catch (XmlException e)
                {
                    Add(nameof(this.ExtensionsXml), Localizer.Format("M.Val.ExtensionsXml", e.Message));
                }
            }

            foreach (var mapping in this.SharedDirectories)
            {
                if (mapping.Label.Length != 2 || mapping.Label[1] != ':' || !char.IsLetter(mapping.Label[0]))
                {
                    Add(nameof(this.SharedDirectories), Localizer.Format("M.Val.MappingLabel", mapping.Label));
                }

                if (!mapping.UncPath.StartsWith(@"\\", StringComparison.Ordinal))
                {
                    Add(nameof(this.SharedDirectories), Localizer.Format("M.Val.MappingPath", mapping.UncPath));
                }
            }

            this.FieldErrors.Replace(byField);
            return problems;

            void CheckTime(string field, string? value, string label)
            {
                if (string.IsNullOrWhiteSpace(value))
                {
                    return;
                }

                if (!TryParseTime(value!))
                {
                    Add(field, Localizer.Format("M.Val.BadTime", label, value, string.Join(", ", TimeSuffixes)));
                }
            }

            void CheckInt(string field, string? value, string label)
            {
                if (!string.IsNullOrWhiteSpace(value)
                    && !int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
                {
                    Add(field, Localizer.Format("M.Val.BadInt", label, value));
                }
            }
        }

        /// <summary>
        /// Checks the configuration against the machine: things the wrapper would accept
        /// syntactically but that will fail at start. Warnings, not errors; the file can be
        /// saved for another machine where the paths exist.
        /// </summary>
        public IReadOnlyList<string> ValidateEnvironment()
        {
            var warnings = new List<string>();
            string? basePath = this.FilePath;

            string? Expand(string? value)
            {
                if (string.IsNullOrWhiteSpace(value))
                {
                    return null;
                }

                // Without a file location %BASE% is unknowable; skip rather than guess.
                if (basePath is null && value!.Contains("%BASE%", StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }

                return basePath is null
                    ? Environment.ExpandEnvironmentVariables(value!)
                    : Services.ConfigPaths.Expand(value!, basePath);
            }

            if (Expand(this.executable) is { } exe && Path.IsPathRooted(exe) && !File.Exists(exe))
            {
                warnings.Add(Localizer.Format("M.Warn.ExecutableMissing", exe));
            }

            if (Expand(this.stopExecutable) is { } stopExe && Path.IsPathRooted(stopExe) && !File.Exists(stopExe))
            {
                warnings.Add(Localizer.Format("M.Warn.StopExecutableMissing", stopExe));
            }

            if (Expand(this.workingDirectory) is { } workDir && !Directory.Exists(workDir))
            {
                warnings.Add(Localizer.Format("M.Warn.WorkingDirectoryMissing", workDir));
            }

            if (Expand(this.logPath) is { } logDir && !Directory.Exists(logDir))
            {
                warnings.Add(Localizer.Format("M.Warn.LogDirectoryMissing", logDir));
            }

            foreach (var hook in new[] { this.Prestart, this.Poststart, this.Prestop, this.Poststop })
            {
                if (Expand(hook.Executable) is { } hookExe && Path.IsPathRooted(hookExe) && !File.Exists(hookExe))
                {
                    warnings.Add(Localizer.Format("M.Warn.ExecutableMissing", hookExe));
                }
            }

            if (!string.IsNullOrWhiteSpace(this.serviceAccountUser))
            {
                string user = this.serviceAccountUser!.Trim();
                bool builtIn = user.StartsWith("NT AUTHORITY\\", StringComparison.OrdinalIgnoreCase)
                    || user.StartsWith("NT SERVICE\\", StringComparison.OrdinalIgnoreCase)
                    || user.Equals("LocalSystem", StringComparison.OrdinalIgnoreCase);

                if (!builtIn)
                {
                    try
                    {
                        new System.Security.Principal.NTAccount(user).Translate(typeof(System.Security.Principal.SecurityIdentifier));
                    }
                    catch (System.Security.Principal.IdentityNotMappedException)
                    {
                        warnings.Add(Localizer.Format("M.Warn.AccountUnknown", user));
                    }
                    catch (SystemException)
                    {
                        // Domain unreachable: cannot tell either way.
                    }
                }
            }

            return warnings;
        }

        /// <summary>Mirrors <c>XmlServiceConfig.ParseTimeSpan</c>.</summary>
        public static bool TryParseTime(string value) => TryParseTime(value, out _);

        /// <summary>Mirrors <c>XmlServiceConfig.ParseTimeSpan</c>, including its suffix table.</summary>
        public static bool TryParseTime(string value, out TimeSpan result)
        {
            result = TimeSpan.Zero;
            value = value.Trim();

            foreach (var (suffix, milliseconds) in SuffixMilliseconds)
            {
                if (value.EndsWith(suffix, StringComparison.Ordinal))
                {
                    string number = value.Substring(0, value.Length - suffix.Length).Trim();
                    if (!int.TryParse(number, NumberStyles.Integer, CultureInfo.InvariantCulture, out int count))
                    {
                        return false;
                    }

                    result = TimeSpan.FromMilliseconds(count * milliseconds);
                    return true;
                }
            }

            if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int plain))
            {
                return false;
            }

            result = TimeSpan.FromMilliseconds(plain);
            return true;
        }

        private static readonly (string Suffix, long Milliseconds)[] SuffixMilliseconds =
        {
            ("ms", 1L), ("secs", 1000L), ("sec", 1000L), ("mins", 60_000L), ("min", 60_000L),
            ("hours", 3_600_000L), ("hour", 3_600_000L), ("hrs", 3_600_000L), ("hr", 3_600_000L),
            ("days", 86_400_000L), ("day", 86_400_000L),
        };

        /// <summary>Renders the configuration as it would be written, for the live preview.</summary>
        public string ToXmlString()
        {
            var document = this.BuildDocument();

            var settings = new XmlWriterSettings
            {
                Indent = true,
                IndentChars = "  ",
                // StringWriter is UTF-16; declaring anything else would be a lie in the preview.
                OmitXmlDeclaration = false,
                NewLineChars = "\r\n",
            };

            var text = new StringWriter(CultureInfo.InvariantCulture);
            using (var writer = XmlWriter.Create(text, settings))
            {
                document.Save(writer);
            }

            return text.ToString();
        }

        // XML helpers --------------------------------------------------------

        private static void SetText(XmlElement parent, string name, string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                RemoveAll(parent, name);
                return;
            }

            if (parent.SelectSingleNode(name) is XmlElement element)
            {
                element.InnerText = value!;
            }
            else
            {
                element = parent.OwnerDocument!.CreateElement(name);
                element.InnerText = value!;
                parent.AppendChild(element);
            }
        }

        private static void SetBool(XmlElement parent, string name, bool value, bool defaultValue)
        {
            SetText(parent, name, value == defaultValue ? null : value ? "true" : "false");
        }

        private static void SetAttribute(XmlElement element, string name, string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                element.RemoveAttribute(name);
            }
            else
            {
                element.SetAttribute(name, value);
            }
        }

        private static void RemoveAll(XmlElement parent, string name)
        {
            foreach (var node in parent.SelectNodes(name)!.OfType<XmlNode>().ToList())
            {
                parent.RemoveChild(node);
            }
        }

        /// <summary>
        /// Rewrites a repeated element in place: the first occurrence keeps its position in
        /// the document so surrounding comments stay attached to the right block.
        /// </summary>
        private static void ReplaceAll<T>(XmlDocument document, XmlElement root, string name, IEnumerable<T> items, Action<XmlElement, T> write)
        {
            var existing = root.SelectNodes(name)!.OfType<XmlNode>().ToList();
            XmlNode? anchor = existing.Count > 0 ? existing[0].PreviousSibling : null;

            foreach (var node in existing)
            {
                root.RemoveChild(node);
            }

            foreach (var item in items)
            {
                var element = document.CreateElement(name);
                write(element, item);

                if (anchor is null)
                {
                    root.AppendChild(element);
                }
                else
                {
                    root.InsertAfter(element, anchor);
                }

                anchor = element;
            }
        }

        private static bool ParseBool(string? value, bool defaultValue = false) =>
            bool.TryParse(value?.Trim(), out bool result) ? result : defaultValue;

        private static string? NullIfEmpty(string? value) =>
            string.IsNullOrWhiteSpace(value) ? null : value;
    }
}
