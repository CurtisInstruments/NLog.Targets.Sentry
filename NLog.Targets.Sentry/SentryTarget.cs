using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using NLog.Common;
using NLog.Config;
using NLog.Layouts;
using SharpRaven;
using SharpRaven.Data;

// ReSharper disable CheckNamespace

namespace NLog.Targets
// ReSharper restore CheckNamespace
{
    [Target("Sentry")]
    public class SentryTarget : TargetWithLayout
    {
        private TimeSpan clientTimeout;
        private LogLevel minLogLevelForEvent = LogLevel.Trace;
        private readonly Lazy<IRavenClient> client;
        private VersionNumberType versionNumberType = Targets.VersionNumberType.AssemblyVersion;
        private static readonly Assembly RootAssembly;

        /// <summary>
        /// Map of NLog log levels to Raven/Sentry log levels
        /// </summary>
        protected static readonly IDictionary<LogLevel, ErrorLevel> LoggingLevelMap = new Dictionary<LogLevel, ErrorLevel>
        {
            { LogLevel.Debug, ErrorLevel.Debug },
            { LogLevel.Error, ErrorLevel.Error },
            { LogLevel.Fatal, ErrorLevel.Fatal },
            { LogLevel.Info, ErrorLevel.Info },
            { LogLevel.Trace, ErrorLevel.Debug },
            { LogLevel.Warn, ErrorLevel.Warning },
        };

        protected static readonly IDictionary<LogLevel, BreadcrumbLevel> BreadcrumbLevelMap = new Dictionary<LogLevel, BreadcrumbLevel>
        {
            { LogLevel.Debug, BreadcrumbLevel.Debug },
            { LogLevel.Error, BreadcrumbLevel.Error },
            { LogLevel.Fatal, BreadcrumbLevel.Critical },
            { LogLevel.Info, BreadcrumbLevel.Info },
            { LogLevel.Trace, BreadcrumbLevel.Debug },
            { LogLevel.Warn, BreadcrumbLevel.Warning },
        };

        static SentryTarget()
        {
            var entryAssembly = Assembly.GetEntryAssembly();

            if (null != entryAssembly)
            {
                RootAssembly = entryAssembly;
                return;
            }

            try
            {
                // ReSharper disable PossibleNullReferenceException
                var systemWebAssembly = AppDomain.CurrentDomain.GetAssemblies()
                                                 .SingleOrDefault(
                                                                  e => e.FullName.StartsWith("System.Web,") &&
                                                                       e.FullName.Contains(
                                                                                           "PublicKeyToken=b03f5f7f11d50a3a"));
                var httpContext =
                    systemWebAssembly.ExportedTypes.SingleOrDefault(
                                                                    e => "HttpContext" == e.Name &&
                                                                         "System.Web" == e.Namespace);
                var currentContextProperty =
                    httpContext.GetProperty("Current", BindingFlags.Public | BindingFlags.Static);
                var currentContext = currentContextProperty.GetValue(null);
                var appInstanceProperty = currentContextProperty.PropertyType.GetProperty("ApplicationInstance");
                var appInstance = appInstanceProperty.GetValue(currentContext);
                var appInstanceType = appInstance.GetType();
                var appInstanceBaseType = appInstanceType.BaseType;
                RootAssembly = appInstanceBaseType.Assembly;

                // ReSharper restore PossibleNullReferenceException
            }
            catch (NullReferenceException)
            {
                // If we could not find the web assembly or any of the properties that we needed to access to get the root assembly version then just return
                // because we have done all we can do.
            }
            catch (TargetException)
            {
            }
        }

        /// <summary>
        /// The DSN for the Sentry host
        /// </summary>
        [RequiredParameter]
        public Layout Dsn { get; set; }

        /// <summary>
        /// Gets or sets the minimum log level required to trigger a Sentry event.
        /// </summary>
        /// <remarks>
        /// NLog <see cref="LogEventInfo"/>'s received below this level will be used for breadcrumbs.
        /// </remarks>
        public string MinLogLevelForEvent
        {
            get => minLogLevelForEvent?.ToString();
            set => minLogLevelForEvent = LogLevel.FromString(value);
        }

        /// <summary>
        /// Gets or sets the environment name to send with the event logs.
        /// </summary>
        public Layout Environment { get; set; }

        /// <summary>
        /// Gets or sets the type of version number to send with the logs.
        /// </summary>
        public string VersionNumberType
        {
            get => versionNumberType.ToString();
            set => versionNumberType = (VersionNumberType)Enum.Parse(typeof(VersionNumberType), value);
        }

        /// <summary>
        /// Gets or sets the timeout for the Raven client.
        /// </summary>
        public string Timeout
        {
            get => clientTimeout.ToString("c");
            set => clientTimeout = TimeSpan.ParseExact(value, "c", CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Determines whether events with no exceptions will be send to Sentry or not
        /// </summary>
        public bool IgnoreEventsWithNoException { get; set; }

        /// <summary>
        /// Determines whether event properties will be sent to sentry as Tags or not
        /// </summary>
        public bool SendLogEventInfoPropertiesAsTags { get; set; }

        /// <summary>
        /// Constructor
        /// </summary>
        public SentryTarget()
        {
            this.client = new Lazy<IRavenClient>(DefaultClientFactory);
        }

        /// <summary>
        /// Internal constructor, used for unit-testing
        /// </summary>
        /// <param name="ravenClient">A <see cref="IRavenClient"/></param>
        public SentryTarget(IRavenClient ravenClient)
        {
            this.client = new Lazy<IRavenClient>(() => ravenClient);
        }

        /// <summary>
        /// Writes logging event to the log target.
        /// </summary>
        /// <param name="logEvent">Logging event to be written out.</param>
        protected override void Write(LogEventInfo logEvent)
        {
            try
            {
                if (logEvent.Level >= this.minLogLevelForEvent)
                {
                    if (logEvent.Exception == null && IgnoreEventsWithNoException)
                    {
                        return;
                    }

                    Dictionary<string, string> eventProperties = null;
                    if (logEvent.HasProperties)
                    {
                        eventProperties = logEvent.Properties.ToDictionary(x => x.Key.ToString(), x => x.Value?.ToString());
                    }

                    var tags = SendLogEventInfoPropertiesAsTags ? eventProperties : null;
                    var extras = SendLogEventInfoPropertiesAsTags ? null : eventProperties;

                    this.client.Value.Logger = logEvent.LoggerName;

                    // If the log event did not contain an exception and we're not ignoring
                    // those kinds of events then we'll send a "Message" to Sentry
                    if (logEvent.Exception == null)
                    {
                        var sentryMessage = new SentryMessage(Layout.Render(logEvent));
                        var msg = new SentryEvent(sentryMessage)
                        {
                            Level = LoggingLevelMap[logEvent.Level],
                            Extra = extras,
                            Tags = tags
                        };
                        if (this.client.Value.Capture(msg) == null)
                        {
                            throw new InvalidOperationException("RavenClient Failed to capture LogEvent. See NLog InternalLog for more details.");
                        }
                    }
                    else
                    {
                        var sentryMessage = new SentryMessage(logEvent.FormattedMessage);
                        var sentryEvent = new SentryEvent(logEvent.Exception)
                        {
                            Message = sentryMessage,
                            Level = LoggingLevelMap[logEvent.Level],
                            Extra = extras,
                            Tags = tags
                        };
                        if (this.client.Value.Capture(sentryEvent) == null)
                        {
                            throw new InvalidOperationException("RavenClient Failed to capture LogEvent. See NLog InternalLog for more details.");
                        }
                    }
                }
                else
                {
                    var breadcrumb = new Breadcrumb(logEvent.LoggerName)
                    {
                        Level = BreadcrumbLevelMap[logEvent.Level],
                        Message = logEvent.FormattedMessage,
                    };

                    if (logEvent.HasProperties)
                    {
                        breadcrumb.Data =
                            logEvent.Properties.ToDictionary(x => x.Key.ToString(), x => x.Value?.ToString());
                    }

                    this.client.Value.AddTrail(breadcrumb);
                }
            }
            catch (Exception ex)
            {
                LogException(ex);
                throw;  // Notify NLog about failure, so fallback/retry can be performed
            }
        }

        /// <summary>
        /// Releases unmanaged and - optionally - managed resources.
        /// </summary>
        /// <param name="disposing">True to release both managed and unmanaged resources; <c>false</c> to release only unmanaged resources.</param>
        protected override void Dispose(bool disposing)
        {
            if (disposing && this.client.IsValueCreated)
            {
                var ravenClient = this.client.Value as RavenClient;

                if (ravenClient != null)
                {
                    ravenClient.ErrorOnCapture = null;
                }
            }

            base.Dispose(disposing);
        }

        /// <summary>
        /// Implements the default client factory behavior.
        /// </summary>
        /// <returns>New instance of a RavenClient.</returns>
        private IRavenClient DefaultClientFactory()
        {
            var renderedDsn = Dsn.Render(LogEventInfo.CreateNullEvent());
            var renderedEnvironment = Environment?.Render(LogEventInfo.CreateNullEvent());
            var ravenClient = new RavenClient(renderedDsn)
            {
                ErrorOnCapture = LogException,
                Timeout = this.clientTimeout,
                Environment = renderedEnvironment,
                Release = GetVersion(),
            };

            if (string.IsNullOrWhiteSpace(ravenClient.Environment))
            {
                ravenClient.Environment = "develop";
            }

            if (TimeSpan.Zero == ravenClient.Timeout)
            {
                ravenClient.Timeout = TimeSpan.FromSeconds(10);
            }

            return ravenClient;
        }

        private string GetVersion()
        {
            switch (this.versionNumberType)
            {
                case Targets.VersionNumberType.AssemblyVersion:
                    return RootAssembly?.GetName().Version.ToString();
                case Targets.VersionNumberType.AssemblyFileVersion:
                    if (RootAssembly != null)
                    {
                        return (Attribute.GetCustomAttributes(RootAssembly, typeof(AssemblyFileVersionAttribute)) as
                                    AssemblyFileVersionAttribute[])?.FirstOrDefault()
                                                                   ?.Version;
                    }
                    else
                    {
                        return null;
                    }
                case Targets.VersionNumberType.AssemblyInformationalVersion:
                    if (RootAssembly != null)
                    {
                        return (Attribute.GetCustomAttributes(
                                                              RootAssembly,
                                                              typeof(AssemblyInformationalVersionAttribute))
                                    as AssemblyInformationalVersionAttribute[])?.FirstOrDefault()
                                                                               ?.InformationalVersion;
                    }
                    else
                    {
                        return null;
                    }
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }

        /// <summary>
        /// Logs an exception using the internal logger class.
        /// </summary>
        /// <param name="ex">The ex to log to the internal logger.</param>
        private void LogException(Exception ex)
        {
            InternalLogger.Error(ex, "SentryTarget(Name={0}): Unable to send Sentry request: {1}", Name, ex.Message);
        }
    }
}
