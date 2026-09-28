using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;
using WinSW.Configuration;
using WinSW.Gui.Mvvm;
using WinSW.Gui.Localization;
using WinSW.Gui.Services;

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

    /// <summary>What <see cref="ServiceConfigModel.CheckRollPattern"/> found wrong with a date pattern.</summary>
    internal enum RollPatternFault
    {
        None,

        /// <summary>Not a .NET date format at all: applying it throws.</summary>
        NotADateFormat,

        /// <summary>Applied, it gives a character no file name can have, such as '/' or ':'.</summary>
        NotAFileName,

        /// <summary>Changes less than daily, which roll-by-time refuses: <c>yyyyMM</c>.</summary>
        ChangesTooRarely,
    }

    public sealed class ServiceConfigModel : ObservableObject
    {
        /// <summary>Time suffixes accepted by <c>XmlServiceConfig.ParseTimeSpan</c>.</summary>
        public static readonly string[] TimeSuffixes =
        {
            "ms", "sec", "secs", "min", "mins", "hr", "hrs", "hour", "hours", "day", "days",
        };

        /// <summary>The start modes a service installed by the wrapper can have.</summary>
        /// <remarks>
        /// The parser also accepts <c>Boot</c> and <c>System</c>, but those are for drivers: the
        /// wrapper installs an ordinary service, and Windows refuses either for one with error 87,
        /// at install and at refresh alike. <c>Disabled</c> is the one that matters, the plainest
        /// way to park a service that keeps failing.
        /// </remarks>
        public static readonly string[] StartModes = { "Automatic", "Manual", "Disabled" };

        public static readonly string[] Priorities =
        {
            "Normal", "Idle", "High", "RealTime", "BelowNormal", "AboveNormal",
        };

        public static readonly string[] LogModes =
        {
            "append", "none", "reset", "roll", "roll-by-time", "roll-by-size", "roll-by-size-time", "rotate",
        };

        /// <summary>The two ways the wrapper can ask for the account's credentials at install.</summary>
        /// <remarks>
        /// The editor offers only <c>dialog</c>: see <see cref="OffersConsolePrompt"/>. The list is
        /// what the wrapper understands, for reading a file that says either.
        /// </remarks>
        public static readonly string[] Prompts = { "dialog", "console" };

        /// <summary>
        /// The characters Windows refuses in a file name, as <c>Path.GetInvalidFileNameChars</c>
        /// returns them there. Spelled out, because the rule that counts is that of the server
        /// where the wrapper names its log files.
        /// </summary>
        private static readonly char[] InvalidFileNameChars =
            "\"<>|:*?\\/".Concat(Enumerable.Range(0, 32).Select(c => (char)c)).ToArray();

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
        private bool endProcessesWithWrapper;
        private bool hideWindow;
        private string startMode = "Automatic";
        private string[] startModeChoices = StartModes;
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
        private bool offersConsolePrompt;
        private bool declaredFailureActions;
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
        private string? proxyAddress;
        private string? proxyNoProxy;
        private bool proxyJava;
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
            set
            {
                // Refused blank for the reason the log mode refuses it: a ComboBox whose
                // ItemsSource resolves after its SelectedItem binding writes null back into the
                // source. A blank would quietly drop <priority> and put the program back at Normal.
                if (!string.IsNullOrWhiteSpace(value))
                {
                    this.Set(ref this.priority, value);
                }
            }
        }

        public string? StopTimeout
        {
            get => this.stopTimeout;
            set => this.Set(ref this.stopTimeout, value);
        }

        /// <summary>
        /// <c>&lt;endProcessesWithWrapper&gt;</c>: the wrapper puts itself in a kill-on-close job
        /// before it starts anything, so Windows ends every process the service started when the
        /// wrapper's process ends, a crash or End task included.
        /// </summary>
        /// <remarks>
        /// Without it, a wrapper that dies leaves the program running with nothing supervising
        /// it, still holding its port, and the next start of the service fails against it. Only a
        /// wrapper that knows the element reads it: an older one skips it as it skips any unknown
        /// element, and behaves as if it said <c>false</c>. It is written only when on, the
        /// wrapper's default being off.
        /// </remarks>
        public bool EndProcessesWithWrapper
        {
            get => this.endProcessesWithWrapper;
            set => this.Set(ref this.endProcessesWithWrapper, value);
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
                // The same accident as the priority's. Here it does real harm: a blank drops
                // <startmode> and <delayedAutoStart> from the file, and the next Save & apply
                // quietly turns a Manual or Disabled service into an Automatic one.
                if (string.IsNullOrWhiteSpace(value))
                {
                    return;
                }

                if (this.Set(ref this.startMode, value))
                {
                    this.Raise(nameof(this.SupportsDelayedAutoStart));
                }
            }
        }

        /// <summary>
        /// What the start mode box offers: <see cref="StartModes"/>, plus the file's own mode when
        /// it is none of them, so a file that says <c>Boot</c> shows <c>Boot</c> rather than an
        /// empty box, and is written back as it was unless somebody picks another.
        /// </summary>
        /// <remarks>
        /// Fixed when the file is read. Replacing the list under a bound selection is how a
        /// ComboBox comes to write null into its source, so the file's own mode stays on offer
        /// after another is chosen.
        /// </remarks>
        public string[] StartModeChoices => this.startModeChoices;

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

        /// <summary>
        /// <c>&lt;interactive&gt;</c>, which the wrapper parses and never uses: it installs every
        /// service as its own process without the interactive flag. The editor has no box for it,
        /// but a file that says it keeps saying it.
        /// </summary>
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

        /// <summary>
        /// <c>&lt;allowservicelogon&gt;</c>, which the wrapper parses and never uses: install grants
        /// the 'Log on as a service' right to any account other than the built-in ones whatever
        /// this says. Kept for the round trip, like <see cref="Interactive"/>.
        /// </summary>
        public bool AllowServiceLogon
        {
            get => this.allowServiceLogon;
            set => this.Set(ref this.allowServiceLogon, value);
        }

        /// <summary>
        /// One of <see cref="Prompts"/>, spelled as the list spells it, or null for none. A value
        /// the wrapper does not know is kept as the file wrote it.
        /// </summary>
        public string? ServiceAccountPrompt
        {
            get => this.serviceAccountPrompt;
            set => this.Set(ref this.serviceAccountPrompt, value);
        }

        /// <summary>
        /// Whether the prompt box still offers <c>console</c>, which it does only when the file
        /// said <c>console</c>: it shows what the file says rather than an empty box, and saves
        /// as it was unless somebody picks another.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Install from this console runs the wrapper elevated in a hidden window
        /// (<c>WinSwCli</c>), where a console prompt waits for typing nobody can see. The install
        /// times out after a minute or three and leaves the elevated wrapper blocked behind it, so
        /// the editor does not offer the option, and <see cref="ValidateEnvironment"/> warns about
        /// a file that has it.
        /// </para>
        /// <para>
        /// Fixed when the file is read, like <see cref="StartModeChoices"/>: an item that vanished
        /// under a bound selection would leave the box empty, a blank away from dropping the
        /// element.
        /// </para>
        /// </remarks>
        public bool OffersConsolePrompt => this.offersConsolePrompt;

        // Failure actions ----------------------------------------------------

        public ObservableCollection<FailureAction> FailureActions { get; } = new();

        /// <summary>
        /// Whether the file had any <c>&lt;onfailure&gt;</c> when it was read. When it did and the
        /// rows are all removed, saving writes <c>&lt;onfailure action="none"/&gt;</c> instead of
        /// nothing; see <see cref="BuildDocument"/>.
        /// </summary>
        /// <remarks>
        /// Fixed when the file is read and never raised: the editor counts anything the model
        /// raises as an edit, and this is a fact about where the file started, not a change.
        /// </remarks>
        public bool DeclaredFailureActions => this.declaredFailureActions;

        public string? ResetFailureAfter
        {
            get => this.resetFailureAfter;
            set => this.Set(ref this.resetFailureAfter, value);
        }

        /// <summary>
        /// What the service control manager will do when the service fails, as the form stands:
        /// the rows it will get, the reset period, and whether no rows clears what it has. Null
        /// while a row or the reset period says something the wrapper cannot read, which
        /// <see cref="Validate"/> reports.
        /// </summary>
        public RecoveryPlan? DescribeRecovery()
        {
            var steps = new List<RecoveryStep>();
            foreach (var action in this.FailureActions)
            {
                // The wrapper matches the action exactly, and refuses anything else outright.
                RecoveryKind kind;
                switch (action.Action)
                {
                    case "restart":
                        kind = RecoveryKind.Restart;
                        break;
                    case "reboot":
                        kind = RecoveryKind.Reboot;
                        break;
                    case "none":
                        kind = RecoveryKind.None;
                        break;
                    default:
                        return null;
                }

                // No delay is no wait, as the wrapper has it.
                var delay = TimeSpan.Zero;
                if (!string.IsNullOrWhiteSpace(action.Delay) && !TryParseTime(action.Delay!, out delay))
                {
                    return null;
                }

                steps.Add(new RecoveryStep(kind, delay));
            }

            var resetAfter = RecoveryPlan.DefaultResetAfter;
            if (!string.IsNullOrWhiteSpace(this.resetFailureAfter) && !TryParseTime(this.resetFailureAfter!, out resetAfter))
            {
                return null;
            }

            return new RecoveryPlan(steps, resetAfter, writesNone: steps.Count == 0 && this.declaredFailureActions);
        }

        /// <summary>
        /// Adds a failure row, as the editor's Add button does: a restart after the default delay.
        /// </summary>
        /// <remarks>
        /// When every row there is says <c>none</c>, which is usually the
        /// <c>&lt;onfailure action="none"/&gt;</c> written when the rows were removed, read back as
        /// the row it is, the new row takes their place. Added after it, the <c>none</c> would
        /// still be what Windows does at the first failure, and the restart just added would wait
        /// for a second one. The rows are removed one by one rather than cleared, so that the
        /// editor unhooks each as it goes; and <see cref="DeclaredFailureActions"/> stays as it
        /// is, so that removing the new row again still writes <c>none</c>.
        /// </remarks>
        public void AddFailureAction()
        {
            if (this.FailureActions.Count > 0 && this.FailureActions.All(row => row.Action == "none"))
            {
                for (int i = this.FailureActions.Count - 1; i >= 0; i--)
                {
                    this.FailureActions.RemoveAt(i);
                }
            }

            this.FailureActions.Add(new FailureAction());
        }

        /// <summary>
        /// Carries <see cref="DeclaredFailureActions"/> over from the model this one replaces, when
        /// both are the same file edited another way: deleting the rows as XML text is removing
        /// them all the same.
        /// </summary>
        internal void KeepDeclaredFailureActions(ServiceConfigModel earlier) =>
            this.declaredFailureActions |= earlier.declaredFailureActions;

        /// <summary>
        /// Forgets that the file declared failure actions, for a model that is to become another
        /// service's file: a service still to be installed has no recovery in Windows for
        /// <c>&lt;onfailure action="none"/&gt;</c> to clear, so no rows there is no element.
        /// </summary>
        internal void ForgetDeclaredFailureActions() => this.declaredFailureActions = false;

        /// <summary>
        /// Gives back the secrets an AI prompt masked: wherever this model, read from an
        /// assistant's answer, holds <see cref="ConfigRedactor.Mask"/> and
        /// <paramref name="earlier"/>, the configuration the answer replaces, has the real
        /// value, the real value is kept.
        /// </summary>
        /// <remarks>
        /// The fields are the ones <see cref="ConfigRedactor"/> masks. A variable is matched
        /// to the earlier one by name and a download by where it is saved to: not by position,
        /// because an answer may add or reorder them and a password must not move to another
        /// server, and not by the source URL, whose credentials may be the very part that was
        /// masked. The credentials of a URL, which the redaction takes out of every value, come
        /// back to a URL anywhere in the same field that names the same host.
        /// </remarks>
        /// <returns>
        /// How many masked values were given back, and how many still read as the mask because
        /// the earlier configuration had nothing to give back for them.
        /// </returns>
        internal (int Kept, int Left) KeepMaskedValues(ServiceConfigModel earlier)
        {
            int kept = 0;
            int left = 0;

            this.serviceAccountPassword = Keep(this.serviceAccountPassword, earlier.serviceAccountPassword, ConfigRedactor.Unmask);
            this.proxyAddress = Keep(this.proxyAddress, earlier.proxyAddress, ConfigRedactor.UnmaskUrl);
            this.arguments = Keep(this.arguments, earlier.arguments, UnmaskCommandLine);
            this.startArguments = Keep(this.startArguments, earlier.startArguments, UnmaskCommandLine);
            this.stopArguments = Keep(this.stopArguments, earlier.stopArguments, UnmaskCommandLine);
            this.Prestart.Arguments = Keep(this.Prestart.Arguments, earlier.Prestart.Arguments, UnmaskCommandLine);
            this.Poststart.Arguments = Keep(this.Poststart.Arguments, earlier.Poststart.Arguments, UnmaskCommandLine);
            this.Prestop.Arguments = Keep(this.Prestop.Arguments, earlier.Prestop.Arguments, UnmaskCommandLine);
            this.Poststop.Arguments = Keep(this.Poststop.Arguments, earlier.Poststop.Arguments, UnmaskCommandLine);

            foreach (var (variable, twin) in Pair(this.EnvironmentVariables, earlier.EnvironmentVariables, v => v.Name.Trim()))
            {
                // Masked whole when the name says secret, or only the credentials of a URL in
                // it: DATABASE_URL says nothing of the password it carries.
                variable.Value = Keep(variable.Value, twin?.Value, UnmaskValue);
            }

            foreach (var (download, twin) in Pair(this.Downloads, earlier.Downloads, d => ConfigRedactor.MaskUrl(d.To.Trim())))
            {
                download.Password = Keep(download.Password, twin?.Password, ConfigRedactor.Unmask);
                download.From = Keep(download.From, twin?.From, ConfigRedactor.UnmaskUrl);
                download.To = Keep(download.To, twin?.To, ConfigRedactor.UnmaskUrl);
                download.Proxy = Keep(download.Proxy, twin?.Proxy, ConfigRedactor.UnmaskUrl);
            }

            // The extensions are written back as the text they are, with no field to match a
            // value to: a URL's credentials can still go back to the same host, and any other
            // mask in them can only be reported.
            this.extensionsXml = Keep(this.extensionsXml, earlier.extensionsXml, ConfigRedactor.UnmaskUrl);

            return (kept, left);

            // A value masked whole, then the credentials of a URL in it.
            static string? UnmaskValue(string? pasted, string? before) =>
                ConfigRedactor.UnmaskUrl(ConfigRedactor.Unmask(pasted, before), before);

            // An argument masked by name, then the credentials of a URL anywhere on the line.
            static string? UnmaskCommandLine(string? pasted, string? before) =>
                ConfigRedactor.UnmaskUrl(ConfigRedactor.UnmaskCommandLine(pasted, before), before);

            [return: NotNullIfNotNull(nameof(pasted))]
            string? Keep(string? pasted, string? before, Func<string?, string?, string?> unmask)
            {
                if (pasted is null)
                {
                    return null;
                }

                string value = unmask(pasted, before) ?? pasted;
                int masks = ConfigRedactor.CountMasks(value);
                kept += ConfigRedactor.CountMasks(pasted) - masks;
                left += masks;
                return value;
            }

            // Each item with the earlier item of the same key: the first with the first, the
            // second with the second, and nothing for an item the earlier list has no match for.
            static IEnumerable<(T Item, T? Earlier)> Pair<T>(IEnumerable<T> items, IEnumerable<T> earlierItems, Func<T, string> key)
                where T : class
            {
                var byKey = new Dictionary<string, Queue<T>>(StringComparer.OrdinalIgnoreCase);
                foreach (var item in earlierItems)
                {
                    if (!byKey.TryGetValue(key(item), out var queue))
                    {
                        byKey[key(item)] = queue = new Queue<T>();
                    }

                    queue.Enqueue(item);
                }

                foreach (var item in items)
                {
                    yield return (item, byKey.TryGetValue(key(item), out var queue) && queue.Count > 0 ? queue.Dequeue() : null);
                }
            }
        }

        /// <summary>
        /// Where this configuration still holds <see cref="ConfigRedactor.Mask"/>, named as the
        /// XML names them: the secrets <see cref="KeepMaskedValues"/> had nothing to give back for.
        /// </summary>
        /// <remarks>
        /// The fields <see cref="ConfigRedactor"/> masks. XML names rather than the form's labels,
        /// so that the list reads the same in every language and can be found in the preview.
        /// </remarks>
        internal IReadOnlyList<string> MaskedPlaces()
        {
            var places = new List<string>();
            Check(this.serviceAccountPassword, "<serviceaccount><password>");
            Check(this.proxyAddress, "<proxy>");
            Check(this.arguments, "<arguments>");
            Check(this.startArguments, "<startarguments>");
            Check(this.stopArguments, "<stoparguments>");
            Check(this.Prestart.Arguments, "<prestart><arguments>");
            Check(this.Poststart.Arguments, "<poststart><arguments>");
            Check(this.Prestop.Arguments, "<prestop><arguments>");
            Check(this.Poststop.Arguments, "<poststop><arguments>");

            foreach (var variable in this.EnvironmentVariables)
            {
                Check(variable.Value, $"<env name=\"{variable.Name.Trim()}\">");
            }

            foreach (var download in this.Downloads)
            {
                if (HasMask(download.Password) || HasMask(download.From) || HasMask(download.To) || HasMask(download.Proxy))
                {
                    places.Add($"<download to=\"{download.To.Trim()}\">");
                }
            }

            Check(this.extensionsXml, "<extensions>");
            return places;

            void Check(string? value, string place)
            {
                if (HasMask(value))
                {
                    places.Add(place);
                }
            }

            static bool HasMask(string? value) => value is not null && value.Contains(ConfigRedactor.Mask, StringComparison.Ordinal);
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

        /// <summary>The <c>&lt;proxy&gt;</c> address, scheme included.</summary>
        public string? ProxyAddress
        {
            get => this.proxyAddress;
            set => this.Set(ref this.proxyAddress, value);
        }

        /// <summary>The hosts the wrapped program should reach without the proxy.</summary>
        public string? ProxyNoProxy
        {
            get => this.proxyNoProxy;
            set => this.Set(ref this.proxyNoProxy, value);
        }

        /// <summary>Whether the proxy is also handed to the JVM, which ignores the variables.</summary>
        public bool ProxyJava
        {
            get => this.proxyJava;
            set => this.Set(ref this.proxyJava, value);
        }

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
            this.priority = MatchChoice(Priorities, Text(root, "priority") ?? "Normal");
            this.stopTimeout = Text(root, "stoptimeout");
            this.endProcessesWithWrapper = Bool(root, "endProcessesWithWrapper");
            this.hideWindow = Bool(root, "hidewindow");
            this.startMode = MatchChoice(StartModes, Text(root, "startmode") ?? "Automatic");
            if (Array.IndexOf(StartModes, this.startMode) < 0)
            {
                this.startModeChoices = StartModes.Append(this.startMode).ToArray();
            }

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
                // The wrapper lower-cases the prompt before it compares, and the box matches its
                // items by exact string: 'Console' is still the console prompt, and has to show.
                this.serviceAccountPrompt = Text(account, "prompt") is { } prompt ? MatchChoice(Prompts, prompt) : null;
                this.offersConsolePrompt = IsConsolePrompt(this.serviceAccountPrompt);
            }

            foreach (XmlElement element in root.SelectNodes("depend")!.OfType<XmlElement>())
            {
                this.Dependencies.Add(new DependencyItem { ServiceName = element.InnerText.Trim() });
            }

            if (root.SelectSingleNode("proxy") is XmlElement proxy)
            {
                this.proxyAddress = NullIfEmpty(proxy.InnerText.Trim());
                this.proxyNoProxy = NullIfEmpty(proxy.GetAttribute("noProxy"));
                this.proxyJava = ParseBool(proxy.GetAttribute("java"));
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
                    Auth = MatchChoice(DownloadItem.AuthTypes, NullIfEmpty(element.GetAttribute("auth")) ?? "none"),
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

            this.declaredFailureActions = this.FailureActions.Count > 0;

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
            SetChoice(root, "priority", string.Equals(this.priority, "Normal", StringComparison.OrdinalIgnoreCase) ? null : this.priority);
            SetText(root, "stoptimeout", this.stopTimeout);
            SetBool(root, "endProcessesWithWrapper", this.endProcessesWithWrapper, false);
            SetBool(root, "hidewindow", this.hideWindow, false);
            SetChoice(root, "startmode", string.Equals(this.startMode, "Automatic", StringComparison.OrdinalIgnoreCase) ? null : this.startMode);

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

            this.SaveProxy(document, root);

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
            var failureActions = this.FailureActions.Where(a => !string.IsNullOrWhiteSpace(a.Action)).ToList();

            // No <onfailure> at all tells the wrapper to leave the service's recovery as it finds
            // it, which is right for a file that never had any: recovery set by hand in
            // services.msc survives Save & apply. It is wrong for a file whose rows were just
            // removed, the obvious way out of a restart loop: the file would say nothing, and
            // Windows would go on restarting. <onfailure action="none"/> says "no recovery" to
            // every wrapper, older ones included, and reads back as the row it is.
            if (failureActions.Count == 0 && this.declaredFailureActions)
            {
                failureActions.Add(new FailureAction { Action = "none", Delay = null });
            }

            ReplaceAll(document, root, "onfailure", failureActions, static (element, item) =>
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

        /// <summary>
        /// The address is the element's text and everything else is an attribute of it, so an
        /// empty address removes the element: an empty <c>&lt;proxy&gt;</c> is a file the
        /// wrapper refuses to start from.
        /// </summary>
        private void SaveProxy(XmlDocument document, XmlElement root)
        {
            if (string.IsNullOrWhiteSpace(this.proxyAddress))
            {
                RemoveAll(root, "proxy");
                return;
            }

            if (root.SelectSingleNode("proxy") is not XmlElement element)
            {
                element = (XmlElement)root.AppendChild(document.CreateElement("proxy"))!;
            }

            element.InnerText = this.proxyAddress!.Trim();
            SetAttribute(element, "noProxy", this.proxyNoProxy?.Trim());
            SetAttribute(element, "java", this.proxyJava ? "true" : null);
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
            SetChoice(element, "prompt", this.serviceAccountPrompt);
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
            else if (this.UsesTimePattern)
            {
                switch (CheckRollPattern(this.rollPattern!, this.logMode == "roll-by-time", out string? name))
                {
                    case RollPatternFault.NotADateFormat:
                        Add(nameof(this.RollPattern), Localizer.Format("M.Val.PatternNotAFormat", this.rollPattern));
                        break;

                    case RollPatternFault.NotAFileName:
                        Add(nameof(this.RollPattern), Localizer.Format("M.Val.PatternNotAFileName", this.rollPattern, name));
                        break;

                    case RollPatternFault.ChangesTooRarely:
                        Add(nameof(this.RollPattern), Localizer.Format("M.Val.PatternTooCoarse", this.rollPattern));
                        break;
                }
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

            if (!string.IsNullOrWhiteSpace(this.proxyAddress))
            {
                try
                {
                    // The wrapper's own parser, so the console refuses exactly what it refuses,
                    // and says so before the file is written rather than at first start.
                    _ = new ProxyConfig(this.proxyAddress, this.proxyNoProxy, this.proxyJava);
                }
                catch (InvalidDataException e)
                {
                    Add(nameof(this.ProxyAddress), Localizer.Format("M.Val.Proxy", e.Message));
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
        public IReadOnlyList<string> ValidateEnvironment() =>
            this.CheckEnvironment().Findings.Select(finding => finding.Describe(Localizer.Get)).ToArray();

        /// <summary>
        /// <see cref="ValidateEnvironment"/> as findings, together with the full path to offer in
        /// place of a bare <c>&lt;executable&gt;</c>.
        /// </summary>
        /// <param name="wrapperPath">
        /// The wrapper that runs this configuration, when it is known. A service looks for a bare
        /// name in the wrapper's folder first; without it, that is taken to be the configuration's
        /// own folder, where the wrapper usually sits.
        /// </param>
        public EnvironmentCheck CheckEnvironment(string? wrapperPath = null) =>
            this.CheckEnvironment(ServiceMachine.Local, wrapperPath);

        /// <summary>
        /// Whether an account is one of those Windows has built in, which a user name lookup
        /// cannot resolve and which have no profile of any user's.
        /// </summary>
        internal static bool IsBuiltInAccount(string user) =>
            user.StartsWith("NT AUTHORITY\\", StringComparison.OrdinalIgnoreCase)
            || user.StartsWith("NT SERVICE\\", StringComparison.OrdinalIgnoreCase)
            || user.Equals("LocalSystem", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// The <c>home</c> of a <c>pyvenv.cfg</c>: the folder of the Python the environment was
        /// made from, which its <c>Scripts\python.exe</c> runs. Read as Python reads it, as
        /// <c>key = value</c> lines with the key in any case.
        /// </summary>
        internal static string? PyvenvHome(string text)
        {
            foreach (string line in text.Split('\n'))
            {
                int equals = line.IndexOf('=');
                if (equals > 0 && line.Substring(0, equals).Trim().Equals("home", StringComparison.OrdinalIgnoreCase))
                {
                    string home = line.Substring(equals + 1).Trim();
                    return home.Length == 0 ? null : home;
                }
            }

            return null;
        }

        /// <summary>
        /// The check itself, with the machine to ask passed in: the tests hand it one made up
        /// for the purpose.
        /// </summary>
        /// <remarks>
        /// A path gets one warning at most, the first that applies of: missing, on a network
        /// drive, in a user's profile. The first is the one to fix, and a path that is not there
        /// has nothing more to be said about it.
        /// </remarks>
        internal EnvironmentCheck CheckEnvironment(IServiceMachine machine, string? wrapperPath)
        {
            var findings = new List<EnvironmentFinding>();
            string? basePath = this.FilePath;
            string? configFolder = basePath is null ? null : WindowsPath.Parent(basePath);

            // A secret "Copy as AI prompt" masked, which the pasted answer kept and nothing gave
            // back. The password box shows dots whatever it holds, so this is the one place the
            // asterisks can be seen before a service is given them.
            if (this.MaskedPlaces() is { Count: > 0 } masked)
            {
                findings.Add(new EnvironmentFinding("M.Warn.MaskedValueLeft", string.Join(", ", masked)));
            }

            // What the wrapper puts into its own environment before it expands a path: %BASE%
            // and the service's id, then each <env> in order, expanded against those before it
            // and the machine's, the PATH among them. <executable>%JAVA_HOME%\bin\java</executable>
            // with JAVA_HOME set by an <env> is the usual way to name a JDK, and it runs. Of a
            // name nobody here sets, the wrapper keeps the %NAME% as it is; so does this, and
            // marks the value as one that cannot be told.
            var variables = new Dictionary<string, (string Value, bool Known)>(StringComparer.OrdinalIgnoreCase);
            string serviceId = this.id.Trim();
            Define("BASE", configFolder);
            Define("SERVICE_ID", serviceId);
            Define("WINSW_SERVICE_ID", serviceId);
            Define("WINSW_EXECUTABLE", wrapperPath);
            variables["PATH"] = (machine.MachinePath, true);
            foreach (var variable in this.EnvironmentVariables)
            {
                string name = variable.Name.Trim();
                if (name.Length > 0)
                {
                    variables[name] = WindowsEnvironment.Expand(variable.Value, Lookup);
                }
            }

            // The wrapper makes the working directory its current directory, and CreateProcess
            // looks there for a bare name and resolves a relative path against it. A relative
            // working directory is itself relative to system32 in a service, which the search
            // covers next in any case.
            string? wrapperFolder = string.IsNullOrWhiteSpace(wrapperPath) ? configFolder : WindowsPath.Parent(wrapperPath!);
            string? workDir = Expand(this.workingDirectory);
            string? currentFolder = string.IsNullOrWhiteSpace(this.workingDirectory)
                ? configFolder
                : workDir is not null && WindowsPath.IsRooted(workDir) ? workDir : null;

            // The PATH the wrapper starts programs with: the machine's, as the configuration's own
            // <env name="PATH"> leaves it. Where that names a variable nobody sets, a folder of it
            // is unknown, and a name found nowhere else may yet be there.
            var servicePath = variables["PATH"];
            var search = new ProgramSearch(machine, wrapperFolder, currentFolder, servicePath.Value);

            // A file not saved yet has no folder, and a name that is nowhere else may be meant to
            // sit beside the wrapper there: "a service would not find it" cannot be said yet.
            bool searchComplete = wrapperFolder is not null && (currentFolder is not null || workDir is not null) && servicePath.Known;

            // The built-in accounts have nobody's profile, and nobody's PATH or pip --user.
            string account = this.serviceAccountUser?.Trim() ?? string.Empty;
            bool builtIn = account.Length == 0 || IsBuiltInAccount(account);

            // Drive letters the wrapper maps for the service itself before it starts anything.
            var mappedForService = new HashSet<char>(this.SharedDirectories
                .Select(mapping => WindowsPath.DriveLetter(mapping.Label.Trim()))
                .OfType<char>());
            var venvs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            string? fullExecutablePath = null;
            if (Expand(this.executable) is { } exe)
            {
                fullExecutablePath = CheckProgram(exe, "M.Warn.ExecutableMissing");
            }

            if (Expand(this.stopExecutable) is { } stopExe)
            {
                CheckProgram(stopExe, "M.Warn.StopExecutableMissing");
            }

            if (workDir is not null)
            {
                if (!machine.DirectoryExists(workDir))
                {
                    findings.Add(new EnvironmentFinding("M.Warn.WorkingDirectoryMissing", workDir));
                }
                else
                {
                    CheckPlace(workDir, profileMatters: true);
                }
            }

            if (Expand(this.logPath) is { } logDir)
            {
                if (!machine.DirectoryExists(logDir))
                {
                    findings.Add(new EnvironmentFinding("M.Warn.LogDirectoryMissing", logDir));
                }
                else
                {
                    CheckPlace(logDir, profileMatters: false);
                }
            }

            // The service control manager starts the wrapper itself as the service's account, in
            // a session where a drive mapped at somebody's sign-in does not exist.
            if (wrapperFolder is not null && WindowsPath.DriveLetter(wrapperFolder) is { } wrapperDrive && machine.IsNetworkDrive(wrapperDrive))
            {
                findings.Add(new EnvironmentFinding("M.Warn.WrapperOnMappedDrive", wrapperFolder, wrapperDrive + ":"));
            }

            foreach (var hook in new[] { this.Prestart, this.Poststart, this.Prestop, this.Poststop })
            {
                if (Expand(hook.Executable) is { } hookExe)
                {
                    CheckProgram(hookExe, "M.Warn.ExecutableMissing");
                }
            }

            if (account.Length > 0 && !IsBuiltInAccount(account) && machine.AccountExists(account) == false)
            {
                findings.Add(new EnvironmentFinding("M.Warn.AccountUnknown", account));
            }

            // The parser takes a driver's start mode and Windows then refuses it, at install and
            // at every Save & apply. A warning rather than a problem: the editor no longer offers
            // either, but a file that says one must still open and save as it is.
            if (IsDriverStartMode(this.startMode))
            {
                findings.Add(new EnvironmentFinding("M.Warn.DriverStartMode", this.startMode));
            }

            // Likewise a console prompt: the box offers it only to a file that already has it.
            if (IsConsolePrompt(this.serviceAccountPrompt))
            {
                findings.Add(new EnvironmentFinding("M.Warn.ConsolePrompt"));
            }

            return new EnvironmentCheck(findings, this.executable, fullExecutablePath);

            // A value from the configuration as the wrapper expands it, or null when it is blank
            // or names a variable nobody sets here: a path that cannot be told is not checked,
            // since the only warning it could get is a made-up one. The service may well have
            // the variable; if it has not, it is told so by a start that fails.
            string? Expand(string? value)
            {
                if (string.IsNullOrWhiteSpace(value))
                {
                    return null;
                }

                var (expanded, known) = WindowsEnvironment.Expand(value!, Lookup);
                return known ? expanded : null;
            }

            // A variable as the wrapper would have it: one it sets or the configuration's <env>
            // sets, or else the machine's. The service is started with the machine's PATH, and
            // that is what a %PATH% in an <env> adds to.
            (string Value, bool Known)? Lookup(string name) =>
                variables.TryGetValue(name, out var value) ? value
                : machine.Variable(name) is { } machineValue ? (machineValue, true)
                : null;

            // One the wrapper sets itself, where this cannot tell its value: never the machine's.
            void Define(string name, string? value) =>
                variables[name] = string.IsNullOrWhiteSpace(value) ? (string.Empty, false) : (value!, true);

            // Checks a program the wrapper starts, and returns the full path to offer in place of
            // a bare name, if there is one to offer.
            string? CheckProgram(string value, string missingKey)
            {
                // Process.Start trims the name and takes one already in quotes as it is.
                value = value.Trim();
                if (value.Length > 2 && value[0] == '"' && value[value.Length - 1] == '"')
                {
                    value = value.Substring(1, value.Length - 2);
                }

                string? program = null;
                string? offer = null;
                if (WindowsPath.IsBare(value))
                {
                    // A name alone passes the try run, which has this user's PATH, and then fails
                    // or changes under the service, which has the machine's as it was at boot.
                    var (source, found) = search.Find(value);
                    switch (source)
                    {
                        case ProgramSource.Fixed:
                            program = found;
                            break;
                        case ProgramSource.MachinePath:
                            findings.Add(new EnvironmentFinding("M.Warn.OnMachinePath", value, found!));
                            program = offer = found;
                            break;
                        case ProgramSource.UserPath:
                            findings.Add(new EnvironmentFinding("M.Warn.OnUserPathOnly", value, found!));
                            offer = found;
                            break;
                        case ProgramSource.Script:
                            findings.Add(new EnvironmentFinding("M.Warn.ScriptByName", value, found!));
                            offer = found;
                            break;
                        default:
                            if (searchComplete)
                            {
                                findings.Add(new EnvironmentFinding("M.Warn.NotFoundForService", value));
                            }

                            break;
                    }
                }
                else
                {
                    // A path with a folder in it is not searched for: it is taken as it is, or
                    // against the current directory, with .exe added when it has no extension.
                    string? path = WindowsPath.IsRooted(value) ? value
                        : currentFolder is null ? null
                        : WindowsPath.Join(currentFolder, value);
                    if (path is null)
                    {
                        return null;
                    }

                    path = WindowsPath.HasExtension(path) ? path : path + ".exe";
                    if (machine.FileExists(path))
                    {
                        program = path;
                    }
                    else
                    {
                        findings.Add(new EnvironmentFinding(missingKey, path));
                    }
                }

                if (program is not null)
                {
                    CheckPlace(program, profileMatters: true);
                    CheckVirtualEnvironment(program);
                }

                return offer;
            }

            // A path the service reaches through the network, or finds in a user's profile.
            void CheckPlace(string path, bool profileMatters)
            {
                if (WindowsPath.DriveLetter(path) is { } drive)
                {
                    if (machine.IsNetworkDrive(drive) && !mappedForService.Contains(drive))
                    {
                        findings.Add(new EnvironmentFinding("M.Warn.MappedDrive", path, drive + ":"));
                        return;
                    }
                }
                else if (WindowsPath.IsUnc(path))
                {
                    findings.Add(new EnvironmentFinding("M.Warn.NetworkShare", path));
                    return;
                }

                // Per-user installs, pip --user and the user's own variables are all where
                // LocalSystem never looks; the program starts and then cannot find its parts.
                if (profileMatters
                    && builtIn
                    && machine.ProfilesDirectory is { } profiles
                    && WindowsPath.ProfileFolder(path, profiles) is { } profile)
                {
                    findings.Add(new EnvironmentFinding("M.Warn.UserProfile", path, profile, account.Length == 0 ? "LocalSystem" : account));
                }
            }

            // A venv's Scripts\python.exe, and every launcher pip puts beside it, runs the Python
            // named by 'home' in the pyvenv.cfg one folder up. Upgrade or remove that Python and
            // the environment still looks whole, but nothing in it starts.
            void CheckVirtualEnvironment(string program)
            {
                string? folder = WindowsPath.Parent(program);
                foreach (string? root in new[] { folder, folder is null ? null : WindowsPath.Parent(folder) })
                {
                    if (root is null || machine.ReadText(WindowsPath.Join(root, "pyvenv.cfg")) is not { } text)
                    {
                        continue;
                    }

                    if (venvs.Add(root)
                        && PyvenvHome(text) is { } home
                        && !machine.FileExists(WindowsPath.Join(home, "python.exe")))
                    {
                        findings.Add(new EnvironmentFinding("M.Warn.VenvHomeMissing", program, root, home));
                    }

                    return;
                }
            }
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

        /// <summary>
        /// <see cref="SetText"/> for an enumeration the wrapper reads without regard to case: an
        /// element that already names the same member in other letters is left as its author
        /// wrote it, since the model holds the list's spelling (<see cref="MatchChoice"/>).
        /// </summary>
        private static void SetChoice(XmlElement parent, string name, string? value)
        {
            if (value != null
                && parent.SelectSingleNode(name) is XmlElement element
                && string.Equals(element.InnerText.Trim(), value.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            SetText(parent, name, value);
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

        /// <summary>
        /// The member of <paramref name="known"/> that <paramref name="value"/> names, spelled as
        /// the list spells it; the value as written when it names none of them.
        /// </summary>
        /// <remarks>
        /// The wrapper reads these enumerations without regard to case, while a ComboBox matches
        /// its selection by exact string: a file that says <c>manual</c> would show an empty box,
        /// and an empty box is one binding accident away from a blank written back.
        /// </remarks>
        internal static string MatchChoice(string[] known, string value) =>
            Array.Find(known, k => string.Equals(k, value.Trim(), StringComparison.OrdinalIgnoreCase)) ?? value;

        /// <summary>
        /// Whether a start mode is one of the two only drivers can have, which Windows refuses for
        /// the ordinary service the wrapper installs.
        /// </summary>
        internal static bool IsDriverStartMode(string? startMode) =>
            string.Equals(startMode?.Trim(), "Boot", StringComparison.OrdinalIgnoreCase)
            || string.Equals(startMode?.Trim(), "System", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Whether a <c>&lt;prompt&gt;</c> asks at the console, which an install from this console
        /// can never answer: see <see cref="OffersConsolePrompt"/>.
        /// </summary>
        internal static bool IsConsolePrompt(string? prompt) =>
            string.Equals(prompt?.Trim(), "console", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// What stops the wrapper from naming its log files with a date <c>&lt;pattern&gt;</c>, or
        /// <see cref="RollPatternFault.None"/> when nothing does.
        /// </summary>
        /// <param name="pattern">The pattern as the file will say it.</param>
        /// <param name="rollsByTime">
        /// True for <c>roll-by-time</c>, which rolls when the formatted date changes and so needs a
        /// pattern that does. <c>roll-by-size-time</c> rolls on size and only names files with it,
        /// so <c>yyyyMM</c> is a fine pattern there.
        /// </param>
        /// <param name="name">The pattern applied to the present moment, when it could be applied.</param>
        /// <remarks>
        /// The wrapper reads the pattern without looking at it. Roll-by-time applies it only when
        /// the service starts, in the task that copies the program's output into the log: an
        /// exception there ends that task with one event log entry, nothing reads the output any
        /// more, and the program blocks on a full pipe while the service still shows Running.
        /// </remarks>
        internal static RollPatternFault CheckRollPattern(string pattern, bool rollsByTime, out string? name)
        {
            name = null;
            try
            {
                // In a custom format '/' and ':' stand for the culture's own separators, which are
                // '.' or '-' in some cultures. The wrapper formats in the service account's culture,
                // not this desktop's, so the separators are judged as they are written.
                name = DateTime.Now.ToString(pattern, CultureInfo.InvariantCulture);
            }
            catch (FormatException)
            {
                return RollPatternFault.NotADateFormat;
            }

            if (name.IndexOfAny(InvalidFileNameChars) >= 0)
            {
                return RollPatternFault.NotAFileName;
            }

            if (rollsByTime)
            {
                // The wrapper's own calendar decides how often the pattern changes, trying a step
                // of a millisecond, a second, a minute, an hour and a day in turn, and throws from
                // Init when none of them changes it: yyyyMM, or no date in the pattern at all.
                var calendar = new PeriodicRollingCalendar(pattern, 1);
                try
                {
                    calendar.Init();
                }
                catch (FormatException)
                {
                    return RollPatternFault.NotADateFormat;
                }
                catch (Exception) when (calendar.Periodicity == PeriodicRollingCalendar.PeriodicityType.ERRONEOUS)
                {
                    return RollPatternFault.ChangesTooRarely;
                }
            }

            return RollPatternFault.None;
        }

        private static bool ParseBool(string? value, bool defaultValue = false) =>
            bool.TryParse(value?.Trim(), out bool result) ? result : defaultValue;

        private static string? NullIfEmpty(string? value) =>
            string.IsNullOrWhiteSpace(value) ? null : value;
    }
}
