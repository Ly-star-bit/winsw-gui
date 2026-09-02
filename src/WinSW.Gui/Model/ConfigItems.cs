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
}
