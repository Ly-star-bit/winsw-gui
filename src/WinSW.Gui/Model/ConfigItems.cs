using WinSW.Gui.Mvvm;

namespace WinSW.Gui.Model
{
    /// <summary>An <c>&lt;env name="" value="" /&gt;</c> entry.</summary>
    public sealed class EnvironmentVariable : ObservableObject
    {
        private string name = string.Empty;
        private string value = string.Empty;

        public string Name
        {
            get => this.name;
            set => this.Set(ref this.name, value);
        }

        public string Value
        {
            get => this.value;
            set => this.Set(ref this.value, value);
        }
    }

    /// <summary>A <c>&lt;download /&gt;</c> entry.</summary>
    public sealed class DownloadItem : ObservableObject
    {
        private string from = string.Empty;
        private string to = string.Empty;
        private string auth = "none";
        private string? user;
        private string? password;
        private bool unsecureAuth;
        private bool failOnError;
        private string? proxy;

        public string From
        {
            get => this.from;
            set => this.Set(ref this.from, value);
        }

        public string To
        {
            get => this.to;
            set => this.Set(ref this.to, value);
        }

        /// <summary>One of <c>none</c>, <c>sspi</c>, <c>basic</c>.</summary>
        public string Auth
        {
            get => this.auth;
            set => this.Set(ref this.auth, value);
        }

        /// <summary>
        /// Serialized as the <c>user</c> attribute. Note that this is <em>not</em>
        /// <c>username</c>: <see href="../../WinSW.Core/Download.cs">Download</see> reads <c>user</c>.
        /// </summary>
        public string? User
        {
            get => this.user;
            set => this.Set(ref this.user, value);
        }

        public string? Password
        {
            get => this.password;
            set => this.Set(ref this.password, value);
        }

        public bool UnsecureAuth
        {
            get => this.unsecureAuth;
            set => this.Set(ref this.unsecureAuth, value);
        }

        public bool FailOnError
        {
            get => this.failOnError;
            set => this.Set(ref this.failOnError, value);
        }

        public string? Proxy
        {
            get => this.proxy;
            set => this.Set(ref this.proxy, value);
        }
    }

    /// <summary>An <c>&lt;onfailure action="" delay="" /&gt;</c> entry.</summary>
    public sealed class FailureAction : ObservableObject
    {
        private string action = "restart";
        private string? delay = "10 sec";

        /// <summary>One of <c>restart</c>, <c>reboot</c>, <c>none</c>.</summary>
        public string Action
        {
            get => this.action;
            set => this.Set(ref this.action, value);
        }

        public string? Delay
        {
            get => this.delay;
            set => this.Set(ref this.delay, value);
        }
    }

    /// <summary>A <c>&lt;depend /&gt;</c> entry, wrapped so it can be edited in a grid.</summary>
    public sealed class DependencyItem : ObservableObject
    {
        private string serviceName = string.Empty;

        public string ServiceName
        {
            get => this.serviceName;
            set => this.Set(ref this.serviceName, value);
        }
    }

    /// <summary>
    /// One of the <c>prestart / poststart / prestop / poststop</c> hooks: a program the wrapper
    /// runs around the service's own lifecycle, with optional output capture.
    /// </summary>
    public sealed class ProcessCommandModel : ObservableObject
    {
        private string? executable;
        private string? arguments;
        private string? stdoutPath;
        private string? stderrPath;

        public string? Executable
        {
            get => this.executable;
            set => this.Set(ref this.executable, value);
        }

        public string? Arguments
        {
            get => this.arguments;
            set => this.Set(ref this.arguments, value);
        }

        public string? StdoutPath
        {
            get => this.stdoutPath;
            set => this.Set(ref this.stdoutPath, value);
        }

        public string? StderrPath
        {
            get => this.stderrPath;
            set => this.Set(ref this.stderrPath, value);
        }

        /// <summary>An empty hook is omitted from the file entirely.</summary>
        public bool IsEmpty => string.IsNullOrWhiteSpace(this.executable);

        public void Clear()
        {
            this.Executable = null;
            this.Arguments = null;
            this.StdoutPath = null;
            this.StderrPath = null;
        }
    }

    /// <summary>A <c>&lt;sharedDirectoryMapping&gt;&lt;map label="" uncpath="" /&gt;</c> entry.</summary>
    public sealed class DriveMapping : ObservableObject
    {
        private string label = "N:";
        private string uncPath = string.Empty;

        /// <summary>The drive letter with colon, e.g. <c>N:</c>.</summary>
        public string Label
        {
            get => this.label;
            set => this.Set(ref this.label, value);
        }

        public string UncPath
        {
            get => this.uncPath;
            set => this.Set(ref this.uncPath, value);
        }
    }
}
