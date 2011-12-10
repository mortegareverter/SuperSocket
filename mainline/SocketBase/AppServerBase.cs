﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.ServiceModel;
using System.ServiceModel.Description;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SuperSocket.Common;
using SuperSocket.SocketBase.Command;
using SuperSocket.SocketBase.Config;
using SuperSocket.SocketBase.Protocol;
using SuperSocket.SocketBase.Security;

namespace SuperSocket.SocketBase
{
    public abstract class AppServerBase<TAppSession, TCommandInfo> : IAppServer<TAppSession, TCommandInfo>
        where TCommandInfo : ICommandInfo
        where TAppSession : IAppSession<TAppSession, TCommandInfo>, new()
    {
        protected readonly TAppSession NullAppSession = default(TAppSession);

        private IPEndPoint m_LocalEndPoint;

        public IServerConfig Config { get; private set; }

        protected virtual ConsoleHostInfo ConsoleHostInfo { get { return null; } }

        public virtual X509Certificate Certificate { get; protected set; }

        public virtual ICustomProtocol<TCommandInfo> Protocol { get; protected set; }

        public ServiceCredentials ServerCredentials { get; set; }

        private List<ICommandLoader> m_CommandLoaders;

        private Dictionary<string, ICommand<TAppSession, TCommandInfo>> m_CommandDict = new Dictionary<string, ICommand<TAppSession, TCommandInfo>>(StringComparer.OrdinalIgnoreCase);

        private ISocketServerFactory m_SocketServerFactory;

        public SslProtocols BasicSecurity { get; private set; }

        protected IRootConfig RootConfig { get; private set; }

        public ILogger Logger { get; private set; }

        private static bool m_ThreadPoolConfigured = false;

        private List<IConnectionFilter> m_ConnectionFilters;

        private Dictionary<string, List<CommandFilterAttribute>> m_CommandFilterDict;

        private long m_TotalHandledCommands = 0;

        protected long TotalHandledCommands
        {
            get { return m_TotalHandledCommands; }
        }

        public AppServerBase()
        {

        }

        public AppServerBase(ICustomProtocol<TCommandInfo> protocol)
        {
            this.Protocol = protocol;
        }

        protected virtual bool SetupCommands(Dictionary<string, ICommand<TAppSession, TCommandInfo>> commandDict)
        {
            foreach (var loader in m_CommandLoaders)
            {
                try
                {
                    var ret = loader.LoadCommands<TAppSession, TCommandInfo>(this, c =>
                    {
                        if (commandDict.ContainsKey(c.Name))
                        {
                            Logger.LogError("Duplicated name command has been found! Command name: " + c.Name);
                            return false;
                        }

                        commandDict.Add(c.Name, c);
                        return true;
                    }, u =>
                    {
                        var workingDict = m_CommandDict.Values.ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);
                        var updatedCommands = 0;

                        foreach (var c in u)
                        {
                            if (c == null)
                                continue;

                            if (c.UpdateAction == CommandUpdateAction.Remove)
                            {
                                workingDict.Remove(c.Command.Name);
                                Logger.LogInfo(string.Format("The command '{0}' has been removed from this server!", c.Command.Name));
                            }
                            else if (c.UpdateAction == CommandUpdateAction.Add)
                            {
                                workingDict.Add(c.Command.Name, c.Command);
                                Logger.LogInfo(string.Format("The command '{0}' has been added into this server!", c.Command.Name));
                            }
                            else
                            {
                                workingDict[c.Command.Name] = c.Command;
                                Logger.LogInfo(string.Format("The command '{0}' has been updated!", c.Command.Name));
                            }

                            updatedCommands++;
                        }

                        if (updatedCommands > 0)
                        {
                            Interlocked.Exchange<Dictionary<string, ICommand<TAppSession, TCommandInfo>>>(ref m_CommandDict, workingDict);
                        }
                    });

                    if (!ret)
                        return false;
                }
                catch (Exception e)
                {
                    LogUtil.LogError("Failed to load command by " + loader.GetType().ToString() + "!", e);
                    return false;
                }
            }

            SetupCommandFilters(commandDict.Values);

            return true;
        }

        private void SetupCommandFilters(IEnumerable<ICommand<TAppSession, TCommandInfo>> commands)
        {
            m_CommandFilterDict = CommandFilterFactory.GenerateCommandFilterLibrary(this.GetType(), commands.Cast<ICommand>());
        }

        public bool Setup(IRootConfig rootConfig, IServerConfig config, ISocketServerFactory socketServerFactory)
        {
            return Setup(rootConfig, config, socketServerFactory, null);
        }

        public virtual bool Setup(IRootConfig rootConfig, IServerConfig config, ISocketServerFactory socketServerFactory, ICustomProtocol<TCommandInfo> protocol)
        {
            if (rootConfig == null)
                throw new ArgumentNullException("rootConfig");

            RootConfig = rootConfig;

            if (!m_ThreadPoolConfigured)
            {
                if (!TheadPoolEx.ResetThreadPool(rootConfig.MaxWorkingThreads >= 0 ? rootConfig.MaxWorkingThreads : new Nullable<int>(),
                        rootConfig.MaxCompletionPortThreads >= 0 ? rootConfig.MaxCompletionPortThreads : new Nullable<int>(),
                        rootConfig.MinWorkingThreads >= 0 ? rootConfig.MinWorkingThreads : new Nullable<int>(),
                        rootConfig.MinCompletionPortThreads >= 0 ? rootConfig.MinCompletionPortThreads : new Nullable<int>()))
                {
                    return false;
                }

                m_ThreadPoolConfigured = true;
            }

            if (config == null)
                throw new ArgumentNullException("config");

            Config = config;

            m_SocketServerFactory = socketServerFactory;

            SetupLogger();

            if (!SetupLocalEndpoint(config))
            {
                Logger.LogError("Invalid config ip/port");
                return false;
            }

            if (!SetupProtocol(config, protocol))
                return false;

            m_CommandLoaders = new List<ICommandLoader>
            {
                new ReflectCommandLoader()
            };

            if (Config.EnableDynamicCommand)
            {
                ICommandLoader dynamicCommandLoader;
                Exception exception;

                if (!AssemblyUtil.TryCreateInstance<ICommandLoader>("SuperSocket.Dlr.DynamicCommandLoader, SuperSocket.Dlr", out dynamicCommandLoader, out exception))
                {
                    Logger.LogError("The file SuperSocket.Dlr is required for dynamic command support!", exception);
                    return false;
                }

                m_CommandLoaders.Add(dynamicCommandLoader);
            }

            if (!SetupCommands(m_CommandDict))
                return false;

            if (!SetupSecurity(config))
                return false;

            return SetupSocketServer();
        }

        private bool SetupProtocol(IServerConfig config, ICustomProtocol<TCommandInfo> protocol)
        {
            //The protocol passed by programming has higher priority, then by config
            if (protocol != null)
            {
                Protocol = protocol;
            }
            else
            {
                //There is a protocol configuration existing
                if (!string.IsNullOrEmpty(config.Protocol))
                {
                    ICustomProtocol<TCommandInfo> configuredProtocol;
                    Exception exception;

                    if (!AssemblyUtil.TryCreateInstance<ICustomProtocol<TCommandInfo>>(config.Protocol, out configuredProtocol, out exception))
                    {
                        Logger.LogError("Invalid configured protocol " + config.Protocol + ".", exception);
                        return false;
                    }
                    Protocol = configuredProtocol;
                }
            }

            //If there is no defined protocol, use CommandLineProtocol as default
            if (Protocol == null)
            {
                Logger.LogError("Protocol hasn't been set!");
                return false;
            }

            return true;
        }

        private void SetupLogger()
        {
            switch (RootConfig.LoggingMode)
            {
                case (LoggingMode.IndependantFile):
                    Logger = new DynamicLog4NetLogger(Config.Name);
                    break;

                case (LoggingMode.Console):
                    Logger = new ConsoleLogger(Config.Name);
                    break;

                default:
                    Logger = new Log4NetLogger(Config.Name);
                    break;
            }
        }

        private bool SetupSecurity(IServerConfig config)
        {
            if (!string.IsNullOrEmpty(config.Security))
            {
                SslProtocols configProtocol;
                if (!config.Security.TryParseEnum<SslProtocols>(true, out configProtocol))
                {
                    Logger.LogError(string.Format("Failed to parse '{0}' to SslProtocol!", config.Security));
                    return false;
                }

                if (configProtocol != SslProtocols.None)
                {
                    if (config.Certificate == null || !config.Certificate.IsEnabled)
                    {
                        Logger.LogError("There is no certificate defined and enabled!");
                        return false;
                    }

                    if (!SetupCertificate(config))
                        return false;
                }

                BasicSecurity = configProtocol;
            }
            else
            {
                BasicSecurity = SslProtocols.None;
            }

            return true;
        }

        private bool SetupSocketServer()
        {
            try
            {
                m_SocketServer = m_SocketServerFactory.CreateSocketServer<TAppSession, TCommandInfo>(this, m_LocalEndPoint, Config, Protocol);
                return m_SocketServer != null;
            }
            catch (Exception e)
            {
                Logger.LogError(e);
                return false;
            }
        }

        private bool SetupLocalEndpoint(IServerConfig config)
        {
            if (config.Port > 0)
            {
                try
                {
                    if (string.IsNullOrEmpty(config.Ip) || "Any".Equals(config.Ip, StringComparison.OrdinalIgnoreCase))
                        m_LocalEndPoint = new IPEndPoint(IPAddress.Any, config.Port);
                    else if ("IPv6Any".Equals(config.Ip, StringComparison.OrdinalIgnoreCase))
                        m_LocalEndPoint = new IPEndPoint(IPAddress.IPv6Any, config.Port);
                    else
                        m_LocalEndPoint = new IPEndPoint(IPAddress.Parse(config.Ip), config.Port);

                    return true;
                }
                catch (Exception e)
                {
                    Logger.LogError(e);
                    return false;
                }
            }

            return false;
        }

        private bool SetupCertificate(IServerConfig config)
        {
            try
            {
                if (config.Certificate.IsEnabled && string.IsNullOrEmpty(config.Certificate.FilePath) && string.IsNullOrEmpty(config.Certificate.Thumbprint))
                {
                    Logger.LogError("Failed to initialize certificate! The attributes 'filePath' or 'thumbprint' is required!");
                    return false;
                }

                Certificate = CertificateManager.Initialize(config.Certificate);
                return true;
            }
            catch (Exception e)
            {
                Logger.LogError("Failed to initialize certificate!", e);
                return false;
            }
        }


        public string Name
        {
            get { return Config.Name; }
        }

        private ISocketServer m_SocketServer;

        public virtual bool Start()
        {
            if (this.IsRunning)
            {
                Logger.LogError("This socket server is running already, you needn't start it.");
                return false;
            }

            if (!m_SocketServer.Start())
                return false;

            OnStartup();

            return true;
        }

        protected virtual void OnStartup()
        {

        }

        protected virtual void OnStopped()
        {

        }

        public virtual void Stop()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
            OnStopped();
        }

        public bool IsRunning
        {
            get
            {
                if (m_SocketServer == null)
                    return false;

                return m_SocketServer.IsRunning;
            }
        }

        public virtual bool IsReady
        {
            get { return true; }
        }

        public ICommand<TAppSession, TCommandInfo> GetCommandByName(string commandName)
        {
            ICommand<TAppSession, TCommandInfo> command;

            if (m_CommandDict.TryGetValue(commandName, out command))
                return command;
            else
                return null;
        }

        private CommandHandler<TAppSession, TCommandInfo> m_CommandHandler;

        protected event CommandHandler<TAppSession, TCommandInfo> CommandHandler
        {
            add { m_CommandHandler += value; }
            remove { m_CommandHandler -= value; }
        }

        private void ExecuteCommandFilters(List<CommandFilterAttribute> filters, TAppSession session, ICommand command, Action<CommandFilterAttribute, TAppSession, ICommand> filterAction)
        {
            if (filters == null || filters.Count <= 0)
                return;

            for (var i = 0; i < filters.Count; i++)
            {
                var filter = filters[i];
                filterAction(filter, session, command);
            }
        }

        private Action<CommandFilterAttribute, TAppSession, ICommand> m_CommandFilterExecutingAction = (f, s, c) => f.OnCommandExecuting(s, c);

        private Action<CommandFilterAttribute, TAppSession, ICommand> m_CommandFilterExecutedAction = (f, s, c) => f.OnCommandExecuted(s, c);

        public virtual void ExecuteCommand(TAppSession session, TCommandInfo commandInfo)
        {
            if (m_CommandHandler == null)
            {
                var command = GetCommandByName(commandInfo.Key);

                if (command != null)
                {
                    List<CommandFilterAttribute> commandFilters;
                    m_CommandFilterDict.TryGetValue(command.Name, out commandFilters);

                    session.CurrentCommand = commandInfo.Key;

                    ExecuteCommandFilters(commandFilters, session, command, m_CommandFilterExecutingAction);

                    //Command filter may close the session,
                    //so detect whether session is connected before execute command
                    if (session.Status != SessionStatus.Disconnected)
                    {
                        command.ExecuteCommand(session, commandInfo);

                        ExecuteCommandFilters(commandFilters, session, command, m_CommandFilterExecutedAction);
                    }

                    session.PrevCommand = commandInfo.Key;

                    if (Config.LogCommand)
                        Logger.LogInfo(session, string.Format("Command - {0}", commandInfo.Key));
                }
                else
                {
                    session.HandleUnknownCommand(commandInfo);
                }

                session.LastActiveTime = DateTime.Now;
            }
            else
            {
                session.CurrentCommand = commandInfo.Key;
                m_CommandHandler(session, commandInfo);
                session.PrevCommand = commandInfo.Key;
                session.LastActiveTime = DateTime.Now;

                if (Config.LogCommand)
                    Logger.LogInfo(session, string.Format("Command - {0}", commandInfo.Key));
            }

            Interlocked.Increment(ref m_TotalHandledCommands);
        }

        public IEnumerable<IConnectionFilter> ConnectionFilters
        {
            get { return m_ConnectionFilters; }
            set
            {
                if (m_ConnectionFilters == null)
                    m_ConnectionFilters = new List<IConnectionFilter>();

                m_ConnectionFilters.AddRange(value);
            }
        }

        private bool ExecuteConnectionFilters(IPEndPoint remoteAddress)
        {
            if (m_ConnectionFilters == null)
                return true;

            for (var i = 0; i < m_ConnectionFilters.Count; i++)
            {
                var currentFilter = m_ConnectionFilters[i];
                if (!currentFilter.AllowConnect(remoteAddress))
                {
                    Logger.LogInfo(string.Format("A connection from {0} has been refused by filter {1}!", remoteAddress, currentFilter.Name));
                    return false;
                }
            }

            return true;
        }

        public virtual TAppSession CreateAppSession(ISocketSession socketSession)
        {
            if (!ExecuteConnectionFilters(socketSession.RemoteEndPoint))
                return NullAppSession;

            TAppSession appSession = new TAppSession();
            appSession.Initialize(this, socketSession);
            socketSession.Closed += new EventHandler<SocketSessionClosedEventArgs>(OnSocketSessionClosed);

            return appSession;
        }

        internal protected virtual void OnSocketSessionClosed(object sender, SocketSessionClosedEventArgs e)
        {

        }

        public virtual TAppSession GetAppSessionByID(string sessionID)
        {
            throw new NotSupportedException();
        }

        /// <summary>
        /// Gets the matched sessions from sessions snapshot.
        /// </summary>
        /// <param name="critera">The prediction critera.</param>
        public virtual IEnumerable<TAppSession> GetSessions(Func<TAppSession, bool> critera)
        {
            throw new NotSupportedException();
        }

        /// <summary>
        /// Gets all sessions in sessions snapshot.
        /// </summary>
        public virtual IEnumerable<TAppSession> GetAllSessions()
        {
            throw new NotSupportedException();
        }

        public virtual int SessionCount
        {
            get
            {
                throw new NotSupportedException();
            }
        }

        #region IDisposable Members

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (IsRunning)
                {
                    m_SocketServer.Stop();
                }
            }
        }

        #endregion
    }
}
