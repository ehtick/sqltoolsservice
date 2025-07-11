//
// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
//

#nullable disable

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.SqlServer.Management.Common;
using Microsoft.SqlServer.Management.SqlParser;
using Microsoft.SqlServer.Management.SqlParser.Binder;
using Microsoft.SqlServer.Management.SqlParser.Common;
using Microsoft.SqlServer.Management.SqlParser.Intellisense;
using Microsoft.SqlServer.Management.SqlParser.Parser;
using Microsoft.SqlServer.Management.SqlParser.SqlCodeDom;
using Microsoft.SqlTools.Extensibility;
using Microsoft.SqlTools.Hosting.Protocol;
using Microsoft.SqlTools.ServiceLayer.AutoParameterizaition;
using Microsoft.SqlTools.ServiceLayer.Connection;
using Microsoft.SqlTools.ServiceLayer.Connection.Contracts;
using Microsoft.SqlTools.ServiceLayer.Hosting;
using Microsoft.SqlTools.ServiceLayer.LanguageServices.Completion;
using Microsoft.SqlTools.ServiceLayer.LanguageServices.Completion.Extension;
using Microsoft.SqlTools.ServiceLayer.LanguageServices.Contracts;
using Microsoft.SqlTools.ServiceLayer.Scripting;
using Microsoft.SqlTools.ServiceLayer.SqlContext;
using Microsoft.SqlTools.ServiceLayer.Utility;
using Microsoft.SqlTools.ServiceLayer.Workspace;
using Microsoft.SqlTools.ServiceLayer.Workspace.Contracts;
using Microsoft.SqlTools.Utility;
using Location = Microsoft.SqlTools.ServiceLayer.Workspace.Contracts.Location;

namespace Microsoft.SqlTools.ServiceLayer.LanguageServices
{
    /// <summary>
    /// Main class for Language Service functionality including anything that requires knowledge of
    /// the language to perform, such as definitions, intellisense, etc.
    /// </summary>
    public class LanguageService : IDisposable
    {
        #region Singleton Instance Implementation

        private static readonly Lazy<LanguageService> instance = new Lazy<LanguageService>(() => new LanguageService());

        /// <summary>
        /// Gets the singleton instance object
        /// </summary>
        public static LanguageService Instance
        {
            get { return instance.Value; }
        }

        #endregion

        #region Instance fields and constructor

        public const string SQL_LANG = "SQL";

        public const string SQL_CMD_LANG = "SQLCMD";

        private const int OneSecond = 1000;

        private const int PrepopulateBindTimeout = 60000;

        internal const string DefaultBatchSeperator = "GO";

        internal const int DiagnosticParseDelay = 750;

        internal const int HoverTimeout = 500;

        internal const int BindingTimeout = 500;

        internal const int OnConnectionWaitTimeout = 300 * OneSecond;

        internal const int PeekDefinitionTimeout = 10 * OneSecond;

        internal const int ExtensionLoadingTimeout = 10 * OneSecond;

        internal const int CompletionExtTimeout = 200;

        // For testability only
        internal Task DelayedDiagnosticsTask = null;

        private ConnectionService connectionService = null;

        private WorkspaceService<SqlToolsSettings> workspaceServiceInstance;

        private ServiceHost serviceHostInstance;

        private object parseMapLock = new object();

        private ScriptParseInfo currentCompletionParseInfo;

        private ConnectedBindingQueue bindingQueue = new ConnectedBindingQueue();

        private ParseOptions defaultParseOptions = new ParseOptions(
            batchSeparator: LanguageService.DefaultBatchSeperator,
            isQuotedIdentifierSet: true,
            compatibilityLevel: DatabaseCompatibilityLevel.Current,
            transactSqlVersion: TransactSqlVersion.Current);

        private ConcurrentDictionary<string, bool> nonMssqlUriMap = new();

        private Lazy<ConcurrentDictionary<string, ScriptParseInfo>> scriptParseInfoMap
            = new Lazy<ConcurrentDictionary<string, ScriptParseInfo>>(() => new());

        private readonly ConcurrentDictionary<string, ICompletionExtension> completionExtensions = new();
        private readonly ConcurrentDictionary<string, DateTime> extAssemblyLastUpdateTime = new();

        /// <summary>
        /// Gets a mapping dictionary for SQL file URIs to ScriptParseInfo objects
        /// </summary>
        internal ConcurrentDictionary<string, ScriptParseInfo> ScriptParseInfoMap
        {
            get
            {
                return this.scriptParseInfoMap.Value;
            }
        }

        private ParseOptions DefaultParseOptions
        {
            get
            {
                return this.defaultParseOptions;
            }
        }

        /// <summary>
        /// Default, parameterless constructor.
        /// </summary>
        internal LanguageService()
        {
        }

        #endregion

        #region Properties

        /// <summary>
        /// Gets or sets the binding queue instance
        /// Internal for testing purposes only
        /// </summary>
        internal ConnectedBindingQueue BindingQueue
        {
            get
            {
                return this.bindingQueue;
            }
            set
            {
                this.bindingQueue = value;
            }
        }

        /// <summary>
        /// Internal for testing purposes only
        /// </summary>
        internal ConnectionService ConnectionServiceInstance
        {
            get
            {
                if (connectionService == null)
                {
                    connectionService = ConnectionService.Instance;
                    connectionService.RegisterConnectedQueue(Constants.LanguageServiceFeature, bindingQueue);
                }
                return connectionService;
            }

            set
            {
                connectionService = value;
            }
        }

        private CancellationTokenSource? existingRequestCancellation;

        /// <summary>
        /// Gets or sets the current workspace service instance
        /// Setter for internal testing purposes only
        /// </summary>
        internal WorkspaceService<SqlToolsSettings> WorkspaceServiceInstance
        {
            get
            {
                workspaceServiceInstance ??= WorkspaceService<SqlToolsSettings>.Instance;
                return workspaceServiceInstance;
            }
            set
            {
                workspaceServiceInstance = value;
            }
        }

        internal ServiceHost ServiceHostInstance
        {
            get
            {
                this.serviceHostInstance ??= ServiceHost.Instance;
                return this.serviceHostInstance;
            }
            set
            {
                this.serviceHostInstance = value;
            }
        }

        /// <summary>
        /// Gets the current settings
        /// </summary>
        internal SqlToolsSettings CurrentWorkspaceSettings
        {
            get { return WorkspaceServiceInstance.CurrentSettings; }
        }

        /// <summary>
        /// Gets the current workspace instance
        /// </summary>
        internal Workspace.Workspace CurrentWorkspace
        {
            get { return WorkspaceServiceInstance.Workspace; }
        }

        /// <summary>
        /// Gets or sets the current SQL Tools context
        /// </summary>
        /// <returns></returns>
        internal SqlToolsContext Context { get; set; }

        #endregion

        #region Public Methods

        /// <summary>
        /// Initializes the Language Service instance
        /// </summary>
        /// <param name="serviceHost"></param>
        /// <param name="context"></param>
        public void InitializeService(ServiceHost serviceHost, SqlToolsContext context)
        {
            // Register the requests that this service will handle

            // turn off until needed (10/28/2016)
            // serviceHost.SetRequestHandler(ReferencesRequest.Type, HandleReferencesRequest);
            // serviceHost.SetRequestHandler(DocumentHighlightRequest.Type, HandleDocumentHighlightRequest);

            // Not enabling parallel processing for LanguageService as message order might matter.
            serviceHost.SetRequestHandler(SignatureHelpRequest.Type, HandleSignatureHelpRequest);
            serviceHost.SetRequestHandler(CompletionResolveRequest.Type, HandleCompletionResolveRequest);
            serviceHost.SetRequestHandler(HoverRequest.Type, HandleHoverRequest);
            serviceHost.SetRequestHandler(CompletionRequest.Type, HandleCompletionRequest);
            serviceHost.SetRequestHandler(DefinitionRequest.Type, HandleDefinitionRequest);
            serviceHost.SetRequestHandler(SyntaxParseRequest.Type, HandleSyntaxParseRequest);
            serviceHost.SetRequestHandler(CompletionExtLoadRequest.Type, HandleCompletionExtLoadRequest);
            serviceHost.SetEventHandler(RebuildIntelliSenseNotification.Type, HandleRebuildIntelliSenseNotification);
            serviceHost.SetEventHandler(LanguageFlavorChangeNotification.Type, HandleDidChangeLanguageFlavorNotification);
            serviceHost.SetEventHandler(TokenRefreshedNotification.Type, HandleTokenRefreshedNotification);

            // Register a no-op shutdown task for validation of the shutdown logic
            serviceHost.RegisterShutdownTask((shutdownParams, shutdownRequestContext) =>
            {
                Logger.Verbose("Shutting down language service");
                DeletePeekDefinitionScripts();
                this.Dispose();
                return Task.FromResult(0);
            });

            ServiceHostInstance = serviceHost;

            // Register the configuration update handler
            WorkspaceServiceInstance.RegisterConfigChangeCallback(HandleDidChangeConfigurationNotification);

            // Register the file change update handler
            WorkspaceServiceInstance.RegisterTextDocChangeCallback(HandleDidChangeTextDocumentNotification);

            // Register the file open update handler
            WorkspaceServiceInstance.RegisterTextDocOpenCallback(HandleDidOpenTextDocumentNotification);

            // Register the file open update handler
            WorkspaceServiceInstance.RegisterTextDocCloseCallback(HandleDidCloseTextDocumentNotification);

            // Register a callback for when a connection is created
            ConnectionServiceInstance.RegisterOnConnectionTask(StartUpdateLanguageServiceOnConnection);

            // Register a callback for when a connection is closed
            ConnectionServiceInstance.RegisterOnDisconnectTask(RemoveAutoCompleteCacheUriReference);

            // Store the SqlToolsContext for future use
            Context = context;

        }

        #endregion

        #region Request Handlers

        /// <summary>
        /// Completion extension load request callback
        /// </summary>
        /// <param name="param"></param>
        /// <param name="requestContext"></param>
        /// <returns></returns>
        internal async Task HandleCompletionExtLoadRequest(CompletionExtensionParams param, RequestContext<bool> requestContext)
        {
            //register the new assembly
            var serviceProvider = (ExtensionServiceProvider)ServiceHostInstance.ServiceProvider;
            var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(param.AssemblyPath);
            var assemblies = new Assembly[] { assembly };
            serviceProvider.AddAssembliesToConfiguration<ICompletionExtension>(assemblies);
            foreach (var ext in serviceProvider.GetServices<ICompletionExtension>())
            {
                var cancellationTokenSource = new CancellationTokenSource(ExtensionLoadingTimeout);
                var cancellationToken = cancellationTokenSource.Token;
                string extName = ext.Name;
                string extTypeName = ext.GetType().FullName;
                if (extTypeName != param.TypeName)
                {
                    continue;
                }

                if (!CheckIfAssemblyShouldBeLoaded(param.AssemblyPath, extTypeName))
                {
                    await requestContext.SendError(string.Format("Skip loading {0} because it's already loaded", param.AssemblyPath));
                    return;
                }

                await ext.Initialize(param.Properties, cancellationToken).WithTimeout(ExtensionLoadingTimeout);
                cancellationTokenSource.Dispose();
                if (!string.IsNullOrEmpty(extName))
                {
                    completionExtensions[extName] = ext;
                    await requestContext.SendResult(true);
                    return;
                }
                else
                {
                    await requestContext.SendError(string.Format("Skip loading an unnamed completion extension from {0}", param.AssemblyPath));
                    return;
                }
            }

            await requestContext.SendError(string.Format("Couldn't discover completion extension with type {0} in {1}", param.TypeName, param.AssemblyPath));
        }

        /// <summary>
        /// Check whether a particular assembly should be reloaded based on
        /// whether it's been updated since it was last loaded.
        /// </summary>
        /// <param name="assemblyPath">The assembly path</param>
        /// <param name="extTypeName">The type loading from the assembly</param>
        /// <returns></returns>
        private bool CheckIfAssemblyShouldBeLoaded(string assemblyPath, string extTypeName)
        {
            var lastModified = File.GetLastWriteTime(assemblyPath);
            if (extAssemblyLastUpdateTime.ContainsKey(extTypeName))
            {
                if (lastModified > extAssemblyLastUpdateTime[extTypeName])
                {
                    extAssemblyLastUpdateTime[extTypeName] = lastModified;
                    return true;
                }
            }
            else
            {
                extAssemblyLastUpdateTime[extTypeName] = lastModified;
                return true;
            }

            return false;

        }

        /// <summary>
        /// T-SQL syntax parse request callback
        /// </summary>
        /// <param name="param"></param>
        /// <param name="requestContext"></param>
        /// <returns></returns>
        internal async Task HandleSyntaxParseRequest(SyntaxParseParams param, RequestContext<SyntaxParseResult> requestContext)
        {
            ParseResult result = Parser.Parse(param.Query);
            SyntaxParseResult syntaxResult = new SyntaxParseResult();
            if (result != null && !result.Errors.Any())
            {
                syntaxResult.Parseable = true;
            }
            else
            {
                syntaxResult.Parseable = false;
                string[] errorMessages = new string[result.Errors.Count()];
                for (int i = 0; i < result.Errors.Count(); i++)
                {
                    errorMessages[i] = result.Errors.ElementAt(i).Message;
                }
                syntaxResult.Errors = errorMessages;
            }
            await requestContext.SendResult(syntaxResult);
        }

        /// <summary>
        /// Auto-complete completion provider request callback
        /// </summary>
        /// <param name="textDocumentPosition"></param>
        /// <param name="requestContext"></param>
        /// <returns></returns>
        internal async Task HandleCompletionRequest(
            TextDocumentPosition textDocumentPosition,
            RequestContext<CompletionItem[]> requestContext)
        {
            var scriptFile = CurrentWorkspace.GetFile(textDocumentPosition.TextDocument.Uri);
            if (scriptFile == null)
            {
                await requestContext.SendResult(null);
                return;
            }
            // check if Intellisense suggestions are enabled
            if (ShouldSkipIntellisense(scriptFile.ClientUri))
            {
                await requestContext.SendResult(null);
            }
            else
            {
                ConnectionInfo connInfo = null;
                // Check if we need to refresh the auth token, and if we do then don't pass in the 
                // connection so that we only show the default options until the refreshed token is returned
                if (!await connectionService.TryRequestRefreshAuthToken(scriptFile.ClientUri))
                {
                    ConnectionServiceInstance.TryFindConnection(
                        scriptFile.ClientUri,
                        out connInfo);
                }
                var completionItems = await GetCompletionItems(
                    textDocumentPosition, scriptFile, connInfo);

                await requestContext.SendResult(completionItems);
            }
        }

        /// <summary>
        /// Handle the resolve completion request event to provide additional
        /// autocomplete metadata to the currently select completion item
        /// </summary>
        /// <param name="completionItem"></param>
        /// <param name="requestContext"></param>
        /// <returns></returns>
        internal async Task HandleCompletionResolveRequest(
            CompletionItem completionItem,
            RequestContext<CompletionItem> requestContext)
        {
            // check if Intellisense suggestions are enabled
            // Note: Do not know file, so no need to check for MSSQL flavor
            if (!CurrentWorkspaceSettings.IsSuggestionsEnabled)
            {
                await requestContext.SendResult(completionItem);
            }
            else
            {
                completionItem = ResolveCompletionItem(completionItem);
                await requestContext.SendResult(completionItem);
            }
        }

        internal async Task HandleDefinitionRequest(TextDocumentPosition textDocumentPosition, RequestContext<Location[]> requestContext)
        {
            DocumentStatusHelper.SendStatusChange(requestContext, textDocumentPosition, DocumentStatusHelper.DefinitionRequested);

            if (!ShouldSkipIntellisense(textDocumentPosition.TextDocument.Uri))
            {
                // Retrieve document and connection
                ConnectionInfo connInfo;
                var scriptFile = CurrentWorkspace.GetFile(textDocumentPosition.TextDocument.Uri);
                bool isConnected = false;
                bool succeeded = false;
                DefinitionResult definitionResult = null;
                if (scriptFile != null)
                {
                    isConnected = ConnectionServiceInstance.TryFindConnection(scriptFile.ClientUri, out connInfo);
                    definitionResult = GetDefinition(textDocumentPosition, scriptFile, connInfo);
                }

                if (definitionResult != null && !definitionResult.IsErrorResult)
                {
                    await requestContext.SendResult(definitionResult.Locations);
                    succeeded = true;
                }
                else
                {
                    await requestContext.SendResult(Array.Empty<Location>());
                }

                DocumentStatusHelper.SendTelemetryEvent(requestContext, CreatePeekTelemetryProps(succeeded, isConnected));
            }
            else
            {
                // Send an empty result so that processing does not hang when peek def service called from non-mssql clients
                await requestContext.SendResult(Array.Empty<Location>());
            }

            DocumentStatusHelper.SendStatusChange(requestContext, textDocumentPosition, DocumentStatusHelper.DefinitionRequestCompleted);
        }

        private static TelemetryProperties CreatePeekTelemetryProps(bool succeeded, bool connected)
        {
            return new TelemetryProperties
            {
                Properties = new Dictionary<string, string>
                {
                    { TelemetryPropertyNames.Succeeded, succeeded.ToOneOrZeroString() },
                    { TelemetryPropertyNames.Connected, connected.ToOneOrZeroString() }
                },
                EventName = TelemetryEventNames.PeekDefinitionRequested
            };
        }

        internal async Task HandleSignatureHelpRequest(
            TextDocumentPosition textDocumentPosition,
            RequestContext<SignatureHelp> requestContext)
        {
            // check if Intellisense suggestions are enabled
            if (ShouldSkipNonMssqlFile(textDocumentPosition))
            {
                await requestContext.SendResult(null);
            }
            else
            {
                ScriptFile scriptFile = CurrentWorkspace.GetFile(
                    textDocumentPosition.TextDocument.Uri);
                if (scriptFile != null)
                {
                    // Start task asynchronously without blocking main thread - this is by design.
                    // Explanation: STS message queues are single-threaded queues, which should be unblocked as soon as possible.
                    // All Long-running tasks should be performed in a non-blocking background task, and results should be sent when ready.
#pragma warning disable CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed
                    GetSignatureHelp(textDocumentPosition, scriptFile)
                        .ContinueWith(async task =>
                    {
                        if (task.IsFaulted)
                        {
                            Logger.Error($"Error getting signature help for script file {scriptFile}: {task.Exception}");
                            await requestContext.SendError(task.Exception);
                            return;
                        }
                        var result = await task;
                        if (result != null)
                        {
                            await requestContext.SendResult(result);
                        }
                        else
                        {
                            await requestContext.SendResult(new SignatureHelp());
                        }
                    });
#pragma warning restore CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed
                }
            }
        }

        private async Task HandleHoverRequest(
            TextDocumentPosition textDocumentPosition,
            RequestContext<Hover> requestContext)
        {
            Hover hover = null;

            // check if Quick Info hover tooltips are enabled
            if (CurrentWorkspaceSettings.IsQuickInfoEnabled
                && !ShouldSkipNonMssqlFile(textDocumentPosition))
            {
                var scriptFile = CurrentWorkspace.GetFile(
                    textDocumentPosition.TextDocument.Uri);

                if (scriptFile != null)
                {
                    hover = GetHoverItem(textDocumentPosition, scriptFile);
                }
            }
            await requestContext.SendResult(hover);
        }

        #endregion

        #region Handlers for Events from Other Services

        /// <summary>
        /// Handle the file open notification
        /// </summary>
        /// <param name="uri"></param>
        /// <param name="scriptFile"></param>
        /// <param name="eventContext"></param>
        /// <returns></returns>
        public async Task HandleDidOpenTextDocumentNotification(
            string uri,
            ScriptFile scriptFile,
            EventContext eventContext)
        {
            try
            {
                // if not in the preview window and diagnostics are enabled then run diagnostics
                if (!IsPreviewWindow(scriptFile)
                    && CurrentWorkspaceSettings.IsDiagnosticsEnabled)
                {
                    await RunScriptDiagnostics(
                        new ScriptFile[] { scriptFile },
                        eventContext);
                }
            }
            catch (Exception ex)
            {
                Logger.Error("Unknown error " + ex.ToString());
                // TODO: need mechanism return errors from event handlers
            }
        }

        /// <summary>
        /// Handles text document change events
        /// </summary>
        /// <param name="textChangeParams"></param>
        /// <param name="eventContext"></param>
        public async Task HandleDidChangeTextDocumentNotification(ScriptFile[] changedFiles, EventContext eventContext)
        {
            try
            {
                if (CurrentWorkspaceSettings.IsDiagnosticsEnabled)
                {
                    // Only process files that are MSSQL flavor
                    await this.RunScriptDiagnostics(
                        changedFiles.ToArray(),
                        eventContext);
                }
            }
            catch (Exception ex)
            {
                Logger.Error("Unknown error " + ex.ToString());
                // TODO: need mechanism return errors from event handlers
            }
        }

        /// <summary>
        /// Handle the file close notification
        /// </summary>
        /// <param name="uri"></param>
        /// <param name="scriptFile"></param>
        /// <param name="eventContext"></param>
        /// <returns></returns>
        public async Task HandleDidCloseTextDocumentNotification(
            string uri,
            ScriptFile scriptFile,
            EventContext eventContext)
        {
            try
            {
                // This clears the uri of the connection from the tokenUpdateUris map, which is used to track
                // open editors that have requested a refreshed Microsoft Entra token.
                connectionService.TokenUpdateUris.Remove(uri, out var result);
                // if not in the preview window and diagnostics are enabled then clear diagnostics
                if (!IsPreviewWindow(scriptFile)
                    && CurrentWorkspaceSettings.IsDiagnosticsEnabled)
                {
                    await DiagnosticsHelper.ClearScriptDiagnostics(uri, eventContext);
                }
            }
            catch (Exception ex)
            {
                Logger.Error("Unknown error " + ex.ToString());
                // TODO: need mechanism return errors from event handlers
            }
        }

        /// <summary>
        /// Handle the rebuild IntelliSense cache notification
        /// </summary>
        /// <param name="rebuildParams">Rebuild params</param>
        /// <param name="eventContext">Event context</param>
        /// <returns>Async task</returns>
        public Task HandleRebuildIntelliSenseNotification(
            RebuildIntelliSenseParams rebuildParams,
            EventContext eventContext)
        {
            // Start task asynchronously without blocking main thread - this is by design.
            // Explanation: STS message queues are single-threaded queues, which should be unblocked as soon as possible.
            // All Long-running tasks should be performed in a non-blocking background task, and results should be sent when ready.
            Task.Factory.StartNew(async () =>
            {
                await DoHandleRebuildIntellisenseNotification(rebuildParams, eventContext);
            }, CancellationToken.None,
            TaskCreationOptions.None,
            TaskScheduler.Default);

            return Task.CompletedTask;
        }

        /// <summary>
        /// Internal method to handle rebuild intellisense notification
        /// </summary>
        /// <param name="rebuildParams">Rebuild params</param>
        /// <param name="eventContext">Event context</param>
        /// <returns>Async task</returns>
        public async Task DoHandleRebuildIntellisenseNotification(RebuildIntelliSenseParams rebuildParams, EventContext eventContext)
        {
            try
            {
                Logger.Verbose("HandleRebuildIntelliSenseNotification");

                // This URI doesn't come in escaped - so if it's a file path with reserved characters (such as %)
                // then we'll fail to find it since GetFile expects the URI to be a fully-escaped URI as that's
                // what the document events are sent in as.
                var escapedOwnerUri = Uri.EscapeDataString(rebuildParams.OwnerUri);
                // Skip closing this file if the file doesn't exist
                var scriptFile = this.CurrentWorkspace.GetFile(escapedOwnerUri);
                if (scriptFile == null)
                {
                    return;
                }

                ConnectionInfo connInfo;
                ConnectionServiceInstance.TryFindConnection(
                    scriptFile.ClientUri,
                    out connInfo);

                // check that there is an active connection for the current editor
                if (connInfo != null)
                {
                    // Get the current ScriptInfo if one exists so we can lock it while we're rebuilding the cache
                    ScriptParseInfo scriptInfo = GetScriptParseInfo(connInfo.OwnerUri, createIfNotExists: false);
                    if (scriptInfo != null && scriptInfo.IsConnected &&
                        Monitor.TryEnter(scriptInfo.BuildingMetadataLock, LanguageService.OnConnectionWaitTimeout))
                    {
                        try
                        {
                            this.BindingQueue.AddConnectionContext(connInfo, featureName: Constants.LanguageServiceFeature, overwrite: true);
                            RemoveScriptParseInfo(rebuildParams.OwnerUri);
                            await UpdateLanguageServiceOnConnection(connInfo);
                        }
                        catch (Exception ex)
                        {
                            Logger.Error("Unknown error " + ex.ToString());
                        }
                        finally
                        {
                            // Set Metadata Build event to Signal state.
                            Monitor.Exit(scriptInfo.BuildingMetadataLock);
                        }

                        // if not in the preview window and diagnostics are enabled then run diagnostics
                        if (!IsPreviewWindow(scriptFile)
                            && CurrentWorkspaceSettings.IsDiagnosticsEnabled)
                        {
                            await RunScriptDiagnostics(
                                                new ScriptFile[] { scriptFile },
                                                eventContext);
                        }

                        // Send a notification to signal that autocomplete is ready
                        await ServiceHostInstance.SendEvent(IntelliSenseReadyNotification.Type, new IntelliSenseReadyParams() { OwnerUri = connInfo.OwnerUri });
                    }
                    else
                    {
                        // Send a notification to signal that autocomplete is ready
                        await ServiceHostInstance.SendEvent(IntelliSenseReadyNotification.Type, new IntelliSenseReadyParams() { OwnerUri = rebuildParams.OwnerUri });
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error("Unknown error " + ex.ToString());
                await ServiceHostInstance.SendEvent(IntelliSenseReadyNotification.Type, new IntelliSenseReadyParams() { OwnerUri = rebuildParams.OwnerUri });
            }
        }

        /// <summary>
        /// Handle the file configuration change notification
        /// </summary>
        /// <param name="newSettings"></param>
        /// <param name="oldSettings"></param>
        /// <param name="eventContext"></param>
        public async Task HandleDidChangeConfigurationNotification(
            SqlToolsSettings newSettings,
            SqlToolsSettings oldSettings,
            EventContext eventContext)
        {
            try
            {
                bool oldEnableIntelliSense = oldSettings.SqlTools.IntelliSense.EnableIntellisense;
                bool oldAlwaysEncryptedParameterizationEnabled = oldSettings.SqlTools.QueryExecutionSettings.IsAlwaysEncryptedParameterizationEnabled;
                bool? oldEnableDiagnostics = oldSettings.SqlTools.IntelliSense.EnableErrorChecking;

                // update the current settings to reflect any changes
                CurrentWorkspaceSettings.Update(newSettings);

                // if script analysis settings have changed we need to clear the current diagnostic markers
                if (oldEnableIntelliSense != newSettings.SqlTools.IntelliSense.EnableIntellisense
                    || oldEnableDiagnostics != newSettings.SqlTools.IntelliSense.EnableErrorChecking
                    || oldAlwaysEncryptedParameterizationEnabled != newSettings.SqlTools.QueryExecutionSettings.IsAlwaysEncryptedParameterizationEnabled)
                {
                    // if the user just turned off diagnostics then send an event to clear the error markers
                    if (!newSettings.IsDiagnosticsEnabled)
                    {
                        foreach (var scriptFile in CurrentWorkspace.GetOpenedFiles())
                        {
                            await DiagnosticsHelper.ClearScriptDiagnostics(scriptFile.ClientUri, eventContext);
                        }
                    }
                    // otherwise rerun diagnostic analysis on all opened SQL files
                    else
                    {
                        await RunScriptDiagnostics(CurrentWorkspace.GetOpenedFiles(), eventContext);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error("Unknown error " + ex.ToString());
                // TODO: need mechanism return errors from event handlers
            }
        }

        /// <summary>
        /// Handles language flavor changes by disabling intellisense on a file if it does not match the specific
        /// "MSSQL" language flavor returned by our service
        /// </summary>
        /// <param name="info"></param>
        public async Task HandleDidChangeLanguageFlavorNotification(
            LanguageFlavorChangeParams changeParams,
            EventContext eventContext)
        {
            try
            {
                Validate.IsNotNull(nameof(changeParams), changeParams);
                Validate.IsNotNull(nameof(changeParams), changeParams.Uri);
                bool shouldBlock = false;
                if (SQL_LANG.Equals(changeParams.Language, StringComparison.OrdinalIgnoreCase))
                {
                    shouldBlock = !ServiceHost.ProviderName.Equals(changeParams.Flavor, StringComparison.OrdinalIgnoreCase);
                }
                if (SQL_CMD_LANG.Equals(changeParams.Language, StringComparison.OrdinalIgnoreCase))
                {
                    shouldBlock = true; // the provider will continue to be mssql
                }
                if (shouldBlock)
                {
                    this.nonMssqlUriMap.AddOrUpdate(changeParams.Uri, true, (k, oldValue) => true);
                    if (CurrentWorkspace.ContainsFile(changeParams.Uri))
                    {
                        await DiagnosticsHelper.ClearScriptDiagnostics(changeParams.Uri, eventContext);
                    }
                }
                else
                {
                    bool value;
                    this.nonMssqlUriMap.TryRemove(changeParams.Uri, out value);
                    // should rebuild intellisense when re-considering as sql
                    RebuildIntelliSenseParams param = new RebuildIntelliSenseParams { OwnerUri = changeParams.Uri };
                    await HandleRebuildIntelliSenseNotification(param, eventContext);
                }
            }
            catch (Exception ex)
            {
                Logger.Error("Unknown error " + ex.ToString());
                // TODO: need mechanism return errors from event handlers
            }
        }

        internal Task HandleTokenRefreshedNotification(
            TokenRefreshedParams tokenRefreshedParams,
            EventContext eventContext
        )
        {
            connectionService.UpdateAuthToken(tokenRefreshedParams);
            return Task.CompletedTask;
        }

        #endregion


        #region "AutoComplete Provider methods"

        /// <summary>
        /// Remove a reference to an autocomplete cache from a URI. If
        /// it is the last URI connected to a particular connection,
        /// then remove the cache.
        /// </summary>
        public Task RemoveAutoCompleteCacheUriReference(IConnectionSummary summary, string ownerUri)
        {
            RemoveScriptParseInfo(ownerUri);

            // currently this method is disabled, but we need to reimplement now that the
            // implementation of the 'cache' has changed.
            return Task.CompletedTask;
        }

        /// <summary>
        /// Parses the SQL text and binds it to the SMO metadata provider if connected
        /// </summary>
        /// <param name="filePath"></param>
        /// <param name="sqlText"></param>
        /// <returns>The ParseResult instance returned from SQL Parser</returns>
        public Task<ParseResult> ParseAndBind(ScriptFile scriptFile, ConnectionInfo connInfo)
        {
            Logger.Verbose($"ParseAndBind - {scriptFile}");
            // get or create the current parse info object
            ScriptParseInfo parseInfo = GetScriptParseInfo(scriptFile.ClientUri, createIfNotExists: true);
            return Task.Run(() =>
            {
                if (Monitor.TryEnter(parseInfo.BuildingMetadataLock, LanguageService.BindingTimeout))
                {
                    try
                    {
                        if (connInfo == null || !parseInfo.IsConnected)
                        {
                            // parse on separate thread so stack size can be increased
                            var parseThread = new Thread(() =>
                            {
                                try
                                {
                                    // parse current SQL file contents to retrieve a list of errors
                                    ParseResult parseResult = Parser.IncrementalParse(
                                    scriptFile.Contents,
                                    parseInfo.ParseResult,
                                    this.DefaultParseOptions);

                                    parseInfo.ParseResult = parseResult;
                                }
                                catch (Exception e)
                                {
                                    // Log the exception but don't rethrow it to prevent parsing errors from crashing SQL Tools Service
                                    Logger.Error(string.Format("An unexpected error occured while parsing: {0}", e.ToString()));
                                }
                            }, ConnectedBindingQueue.QueueThreadStackSize);
                            parseThread.Start();
                            parseThread.Join();
                        }
                        else
                        {
                            QueueItem queueItem = this.BindingQueue.QueueBindingOperation(
                                key: parseInfo.ConnectionKey,
                                bindingTimeout: LanguageService.BindingTimeout,
                                bindOperation: (bindingContext, cancelToken) =>
                                {
                                    try
                                    {
                                        ParseResult parseResult = Parser.IncrementalParse(
                                            scriptFile.Contents,
                                            parseInfo.ParseResult,
                                            bindingContext.ParseOptions);

                                        parseInfo.ParseResult = parseResult;

                                        List<ParseResult> parseResults = new List<ParseResult>();
                                        parseResults.Add(parseResult);
                                        if (bindingContext.IsConnected && bindingContext.Binder != null)
                                        {
                                            bindingContext.Binder.Bind(
                                                parseResults,
                                                connInfo.ConnectionDetails.DatabaseName,
                                                BindMode.Batch);
                                        }
                                    }
                                    catch (ConnectionException)
                                    {
                                        Logger.Error("Hit connection exception while binding - disposing binder object...");
                                    }
                                    catch (SqlParserInternalBinderError)
                                    {
                                        Logger.Error("Hit connection exception while binding - disposing binder object...");
                                    }
                                    catch (Exception ex)
                                    {
                                        Logger.Error("Unknown exception during parsing " + ex.ToString());
                                    }

                                    return null;
                                });

                            queueItem.ItemProcessed.WaitOne();
                        }
                    }
                    catch (Exception ex)
                    {
                        // reset the parse result to do a full parse next time
                        parseInfo.ParseResult = null;
                        Logger.Error("Unknown exception during parsing " + ex.ToString());
                    }
                    finally
                    {
                        Monitor.Exit(parseInfo.BuildingMetadataLock);
                    }
                }
                else
                {
                    Logger.Warning("Binding metadata lock timeout in ParseAndBind");
                }

                return parseInfo.ParseResult;
            });
        }

        /// <summary>
        /// Runs UpdateLanguageServiceOnConnection as a background task
        /// </summary>
        /// <param name="info">Connection Info</param>
        /// <returns></returns>
        public Task StartUpdateLanguageServiceOnConnection(ConnectionInfo info)
        {
            // Start task asynchronously without blocking main thread - this is by design.
            // Explanation: STS message queues are single-threaded queues, which should be unblocked as soon as possible.
            // All Long-running tasks should be performed in a non-blocking background task, and results should be sent when ready.
            Task.Factory.StartNew(() => UpdateLanguageServiceOnConnection(info));
            return Task.CompletedTask;
        }

        /// <summary>
        /// Starts a Task to update the autocomplete metadata provider when the user connects to a database
        /// </summary>
        /// <param name="info"></param>
        public async Task UpdateLanguageServiceOnConnection(ConnectionInfo info)
        {
            if (ConnectionService.IsDedicatedAdminConnection(info.ConnectionDetails))
            {
                // Intellisense cannot be run on these connections as only 1 SqlConnection can be opened on them at a time
                return;
            }
            ScriptParseInfo scriptInfo = GetScriptParseInfo(info.OwnerUri, createIfNotExists: true);
            if (Monitor.TryEnter(scriptInfo.BuildingMetadataLock, LanguageService.OnConnectionWaitTimeout))
            {
                try
                {
                    scriptInfo.ConnectionKey = this.BindingQueue.AddConnectionContext(info, Constants.LanguageServiceFeature);
                    scriptInfo.IsConnected = this.BindingQueue.IsBindingContextConnected(scriptInfo.ConnectionKey);
                }
                catch (Exception ex)
                {
                    Logger.Error("Unknown error in OnConnection " + ex.ToString());
                    scriptInfo.IsConnected = false;
                }
                finally
                {
                    // Set Metadata Build event to Signal state.
                    // (Tell Language Service that I am ready with Metadata Provider Object)
                    Monitor.Exit(scriptInfo.BuildingMetadataLock);
                }
            }
            await PrepopulateCommonMetadata(info, scriptInfo, this.BindingQueue).ContinueWith(async _ =>
            {
                // Send a notification to signal that autocomplete is ready
                await ServiceHostInstance.SendEvent(IntelliSenseReadyNotification.Type, new IntelliSenseReadyParams() { OwnerUri = info.OwnerUri });
            });
        }


        /// <summary>
        /// Preinitialize the parser and binder with common metadata.
        /// This should front load the long binding wait to the time the
        /// connection is established.  Once this is completed other binding
        /// requests should be faster.
        /// </summary>
        /// <param name="info"></param>
        /// <param name="scriptInfo"></param>
        internal async Task PrepopulateCommonMetadata(
            ConnectionInfo info,
            ScriptParseInfo scriptInfo,
            ConnectedBindingQueue bindingQueue)
        {
            if (scriptInfo.IsConnected)
            {
                // This URI doesn't come in escaped - so if it's a file path with reserved characters (such as %)
                // then we'll fail to find it since GetFile expects the URI to be a fully-escaped URI as that's
                // what the document events are sent in as.
                var fileUri = Uri.EscapeUriString(info.OwnerUri);
                var scriptFile = CurrentWorkspace.GetFile(fileUri);
                if (scriptFile == null)
                {
                    return;
                }

                await ParseAndBind(scriptFile, info).ContinueWith(t =>
                {
                    if (Monitor.TryEnter(scriptInfo.BuildingMetadataLock, LanguageService.OnConnectionWaitTimeout))
                    {
                        try
                        {
                            QueueItem queueItem = bindingQueue.QueueBindingOperation(
                                key: scriptInfo.ConnectionKey,
                                bindingTimeout: PrepopulateBindTimeout,
                                waitForLockTimeout: PrepopulateBindTimeout,
                                bindOperation: (bindingContext, cancelToken) =>
                                {
                                    // parse a simple statement that returns common metadata
                                    ParseResult parseResult = Parser.Parse(
                                        "select ",
                                        bindingContext.ParseOptions);
                                    if (bindingContext.IsConnected && bindingContext.Binder != null)
                                    {
                                        List<ParseResult> parseResults = new List<ParseResult>();
                                        parseResults.Add(parseResult);
                                        bindingContext.Binder.Bind(
                                            parseResults,
                                            info.ConnectionDetails.DatabaseName,
                                            BindMode.Batch);

                                        // get the completion list from SQL Parser
                                        var suggestions = Resolver.FindCompletions(
                                            parseResult, 1, 8,
                                            bindingContext.MetadataDisplayInfoProvider);

                                        // this forces lazy evaluation of the suggestion metadata
                                        AutoCompleteHelper.ConvertDeclarationsToCompletionItems(suggestions, 1, 8, 8);

                                        parseResult = Parser.Parse(
                                            "exec ",
                                            bindingContext.ParseOptions);

                                        parseResults = new List<ParseResult>();
                                        parseResults.Add(parseResult);
                                        bindingContext.Binder.Bind(
                                            parseResults,
                                            info.ConnectionDetails.DatabaseName,
                                            BindMode.Batch);

                                        // get the completion list from SQL Parser
                                        suggestions = Resolver.FindCompletions(
                                            parseResult, 1, 6,
                                            bindingContext.MetadataDisplayInfoProvider);

                                        // this forces lazy evaluation of the suggestion metadata
                                        AutoCompleteHelper.ConvertDeclarationsToCompletionItems(suggestions, 1, 6, 6);
                                    }
                                    return null;
                                });

                            queueItem.ItemProcessed.WaitOne();
                        }
                        catch (Exception ex)
                        {
                            Logger.Error("Exception in PrepopulateCommonMetadata " + ex.ToString());
                        }
                        finally
                        {
                            Monitor.Exit(scriptInfo.BuildingMetadataLock);
                        }
                    }
                });
            }
        }

        private bool ShouldSkipNonMssqlFile(TextDocumentPosition textDocPosition)
        {
            return ShouldSkipNonMssqlFile(textDocPosition.TextDocument.Uri);
        }

        private bool ShouldSkipNonMssqlFile(ScriptFile scriptFile)
        {
            return ShouldSkipNonMssqlFile(scriptFile.ClientUri);
        }

        /// <summary>
        /// Checks if a given URI is not an MSSQL file. Only files explicitly excluded by a language flavor change
        /// notification will be treated as skippable
        /// </summary>
        public virtual bool ShouldSkipNonMssqlFile(string uri)
        {
            bool isNonMssql = false;
            nonMssqlUriMap.TryGetValue(uri, out isNonMssql);
            return isNonMssql;
        }

        /// <summary>
        /// Determines whether intellisense should be skipped for a document.
        /// If IntelliSense is disabled or it's a non-MSSQL doc this will be skipped
        /// </summary>
        private bool ShouldSkipIntellisense(string uri)
        {
            return !CurrentWorkspaceSettings.IsSuggestionsEnabled
                || ShouldSkipNonMssqlFile(uri);
        }

        /// <summary>
        /// Determines whether a reparse and bind is required to provide autocomplete
        /// </summary>
        /// <param name="info"></param>
        private bool RequiresReparse(ScriptParseInfo info, ScriptFile scriptFile)
        {
            if (info.ParseResult == null)
            {
                return true;
            }

            string prevSqlText = info.ParseResult.Script.Sql;
            string currentSqlText = scriptFile.Contents;

            return prevSqlText.Length != currentSqlText.Length
                || !string.Equals(prevSqlText, currentSqlText);
        }

        /// <summary>
        /// Resolves the details and documentation for a completion item
        /// </summary>
        /// <param name="completionItem"></param>
        internal CompletionItem ResolveCompletionItem(CompletionItem completionItem)
        {
            var scriptParseInfo = currentCompletionParseInfo;
            if (scriptParseInfo != null && scriptParseInfo.CurrentSuggestions != null)
            {
                if (Monitor.TryEnter(scriptParseInfo.BuildingMetadataLock))
                {
                    try
                    {
                        QueueItem queueItem = this.BindingQueue.QueueBindingOperation(
                            key: scriptParseInfo.ConnectionKey,
                            bindingTimeout: LanguageService.BindingTimeout,
                            bindOperation: (bindingContext, cancelToken) =>
                            {
                                foreach (var suggestion in scriptParseInfo.CurrentSuggestions)
                                {
                                    if (string.Equals(suggestion.Title, completionItem.Label))
                                    {
                                        completionItem.Detail = suggestion.DatabaseQualifiedName;
                                        completionItem.Documentation = suggestion.Description;
                                        break;
                                    }
                                }
                                return completionItem;
                            });

                        queueItem.ItemProcessed.WaitOne();
                    }
                    catch (Exception ex)
                    {
                        // if any exceptions are raised looking up extended completion metadata
                        // then just return the original completion item
                        Logger.Error("Exception in ResolveCompletionItem " + ex.ToString());
                    }
                    finally
                    {
                        Monitor.Exit(scriptParseInfo.BuildingMetadataLock);
                    }
                }
            }


            return completionItem;
        }


        /// <summary>
        /// Queue a task to the binding queue
        /// </summary>
        /// <param name="textDocumentPosition"></param>
        /// <param name="scriptParseInfo"></param>
        /// <param name="connectionInfo"></param>
        /// <param name="scriptFile"></param>
        /// <param name="tokenText"></param>
        /// <returns> Returns the result of the task as a DefinitionResult </returns>
        private DefinitionResult QueueTask(TextDocumentPosition textDocumentPosition, ScriptParseInfo scriptParseInfo,
                                            ConnectionInfo connInfo, ScriptFile scriptFile, string tokenText)
        {
            // Queue the task with the binding queue
            QueueItem queueItem = this.BindingQueue.QueueBindingOperation(
                key: scriptParseInfo.ConnectionKey,
                bindingTimeout: LanguageService.PeekDefinitionTimeout,
                bindOperation: (bindingContext, cancelToken) =>
                {
                    Sql4PartIdentifier identifier = this.GetFullIdentifier(scriptParseInfo, textDocumentPosition.Position);

                    // Script object using SMO
                    Scripter scripter = new Scripter(bindingContext.ServerConnection, connInfo);
                    return scripter.GetScript(
                        scriptParseInfo.ParseResult,
                        textDocumentPosition.Position,
                        bindingContext.MetadataDisplayInfoProvider,
                        identifier);
                },
                timeoutOperation: (bindingContext) =>
                {
                    // return error result
                    return new DefinitionResult
                    {
                        IsErrorResult = true,
                        Message = SR.PeekDefinitionTimedoutError,
                        Locations = null
                    };
                },
                errorHandler: ex =>
                {
                    // return error result
                    return new DefinitionResult
                    {
                        IsErrorResult = true,
                        Message = ex.Message,
                        Locations = null
                    };
                });

            // wait for the queue item
            queueItem.ItemProcessed.WaitOne();
            var result = queueItem.GetResultAsT<DefinitionResult>();
            return result;
        }

        private DefinitionResult GetDefinitionFromTokenList(TextDocumentPosition textDocumentPosition, List<Token> tokenList,
                ScriptParseInfo scriptParseInfo, ScriptFile scriptFile, ConnectionInfo connInfo)
        {

            DefinitionResult lastResult = null;
            foreach (var token in tokenList)
            {

                // Strip "[" and "]"(if present) from the token text to enable matching with the suggestions.
                // The suggestion title does not contain any sql punctuation
                string tokenText = TextUtilities.RemoveSquareBracketSyntax(token.Text);
                textDocumentPosition.Position.Line = token.StartLocation.LineNumber;
                textDocumentPosition.Position.Character = token.StartLocation.ColumnNumber;
                if (Monitor.TryEnter(scriptParseInfo.BuildingMetadataLock))
                {
                    try
                    {
                        var result = QueueTask(textDocumentPosition, scriptParseInfo, connInfo, scriptFile, tokenText);
                        lastResult = result;
                        if (!result.IsErrorResult)
                        {
                            return result;
                        }
                    }
                    catch (Exception ex)
                    {
                        // if any exceptions are raised return error result with message
                        Logger.Error("Exception in GetDefinition " + ex.ToString());
                        return new DefinitionResult
                        {
                            IsErrorResult = true,
                            Message = SR.PeekDefinitionError(ex.Message),
                            Locations = null
                        };
                    }
                    finally
                    {
                        Monitor.Exit(scriptParseInfo.BuildingMetadataLock);
                    }
                }
                else
                {
                    Logger.Error("Timeout waiting to query metadata from server");
                }
            }
            return (lastResult != null) ? lastResult : null;
        }

        /// <summary>
        /// Get definition for a selected sql object using SMO Scripting
        /// </summary>
        /// <param name="textDocumentPosition"></param>
        /// <param name="scriptFile"></param>
        /// <param name="connInfo"></param>
        /// <returns> Location with the URI of the script file</returns>
        internal DefinitionResult GetDefinition(TextDocumentPosition textDocumentPosition, ScriptFile scriptFile, ConnectionInfo connInfo)
        {
            // Parse sql
            ScriptParseInfo scriptParseInfo = GetScriptParseInfo(scriptFile.ClientUri);
            if (scriptParseInfo == null)
            {
                return null;
            }

            if (RequiresReparse(scriptParseInfo, scriptFile))
            {
                scriptParseInfo.ParseResult = ParseAndBind(scriptFile, connInfo).GetAwaiter().GetResult();
            }

            // Get token from selected text
            Tuple<Stack<Token>, Queue<Token>> selectedToken = ScriptDocumentInfo.GetPeekDefinitionTokens(scriptParseInfo,
                textDocumentPosition.Position.Line + 1, textDocumentPosition.Position.Character + 1);

            if (selectedToken == null)
            {
                return null;
            }

            if (scriptParseInfo.IsConnected)
            {
                //try children tokens first
                Stack<Token> childrenTokens = selectedToken.Item1;
                List<Token> tokenList = childrenTokens.ToList();
                DefinitionResult childrenResult = GetDefinitionFromTokenList(textDocumentPosition, tokenList, scriptParseInfo, scriptFile, connInfo);

                // if the children peak definition returned null then
                // try the parents
                if (childrenResult == null || childrenResult.IsErrorResult)
                {
                    Queue<Token> parentTokens = selectedToken.Item2;
                    tokenList = parentTokens.ToList();
                    DefinitionResult parentResult = GetDefinitionFromTokenList(textDocumentPosition, tokenList, scriptParseInfo, scriptFile, connInfo);
                    return (parentResult == null) ? null : parentResult;
                }
                else
                {
                    return childrenResult;
                }
            }
            else
            {
                // User is not connected.
                return new DefinitionResult
                {
                    IsErrorResult = true,
                    Message = SR.PeekDefinitionNotConnectedError,
                    Locations = null
                };
            }
        }

        /// <summary>
        /// Wrapper around find token method
        /// </summary>
        /// <param name="scriptParseInfo"></param>
        /// <param name="position"></param>
        /// <returns> token index</returns>
        private int FindTokenWithCorrectOffset(ScriptParseInfo scriptParseInfo, Position position)
        {
            var tokenIndex = scriptParseInfo.ParseResult.Script.TokenManager.FindToken(position.Line, position.Character);
            var end = scriptParseInfo.ParseResult.Script.TokenManager.GetToken(tokenIndex).EndLocation;
            if (end.LineNumber == position.Line && end.ColumnNumber == position.Character)
            {
                return tokenIndex + 1;
            }
            return tokenIndex;
        }

        /// <summary>
        /// Returns a 4 part identifier at the position in a script, if present
        /// </summary>
        /// <param name="scriptParseInfo"></param>
        /// <param name="position"></param>
        /// <returns></returns>
        private Sql4PartIdentifier GetFullIdentifier(ScriptParseInfo scriptParseInfo, Position position)
        {
            if (scriptParseInfo?.ParseResult?.Script?.Tokens == null) return null;
            var tokenManager = scriptParseInfo.ParseResult.Script.TokenManager;
            int tokenIndex = this.FindTokenWithCorrectOffset(scriptParseInfo, position);
            var identifiers = new string[4];
            //work backwards from the initial token to read identifier parts
            for (int i = 0; i < identifiers.Length; i++)
            {
                if (i > 0) //consume separator dot
                {
                    tokenIndex = tokenManager.GetPreviousSignificantTokenIndex(tokenIndex);
                    if (tokenIndex < 0) break;
                    var period = tokenManager.GetText(tokenIndex);
                    if (period is null or not ".") break;
                    tokenIndex = tokenManager.GetPreviousSignificantTokenIndex(tokenIndex);
                }

                if (tokenIndex < 0) break;
                string identifierText = tokenManager.GetText(tokenIndex);
                if (string.IsNullOrEmpty(identifierText)) break;
                identifiers[i] = TextUtilities.RemoveSquareBracketSyntax(identifierText);
            }
            return new Sql4PartIdentifier
            {
                ObjectName = identifiers[0],
                SchemaName = identifiers[1],
                DatabaseName = identifiers[2],
                ServerName = identifiers[3]
            };
        }

        /// <summary>
        /// Get quick info hover tooltips for the current position
        /// </summary>
        /// <param name="textDocumentPosition"></param>
        /// <param name="scriptFile"></param>
        internal Hover GetHoverItem(TextDocumentPosition textDocumentPosition, ScriptFile scriptFile)
        {
            int startLine = textDocumentPosition.Position.Line;
            int startColumn = TextUtilities.PositionOfPrevDelimeter(
                                scriptFile.Contents,
                                textDocumentPosition.Position.Line,
                                textDocumentPosition.Position.Character);
            int endColumn = textDocumentPosition.Position.Character;

            ScriptParseInfo scriptParseInfo = GetScriptParseInfo(scriptFile.ClientUri);
            if (scriptParseInfo != null && scriptParseInfo.ParseResult != null)
            {
                if (Monitor.TryEnter(scriptParseInfo.BuildingMetadataLock))
                {
                    try
                    {
                        QueueItem queueItem = this.BindingQueue.QueueBindingOperation(
                            key: scriptParseInfo.ConnectionKey,
                            bindingTimeout: LanguageService.HoverTimeout,
                            bindOperation: (bindingContext, cancelToken) =>
                            {
                                // get the current quick info text
                                Babel.CodeObjectQuickInfo quickInfo = Resolver.GetQuickInfo(
                                    scriptParseInfo.ParseResult,
                                    startLine + 1,
                                    endColumn + 1,
                                    bindingContext.MetadataDisplayInfoProvider);

                                // convert from the parser format to the VS Code wire format
                                return AutoCompleteHelper.ConvertQuickInfoToHover(
                                        quickInfo,
                                        startLine,
                                        startColumn,
                                        endColumn);
                            });

                        queueItem.ItemProcessed.WaitOne();
                        return queueItem.GetResultAsT<Hover>();
                    }
                    finally
                    {
                        Monitor.Exit(scriptParseInfo.BuildingMetadataLock);
                    }
                }
            }

            // return null if there isn't a tooltip for the current location
            return null;
        }

        /// <summary>
        /// Get function signature help for the current position
        /// </summary>
        internal async Task<SignatureHelp?> GetSignatureHelp(TextDocumentPosition textDocumentPosition, ScriptFile scriptFile)
        {
            Logger.Verbose($"GetSignatureHelp -  {scriptFile}");
            int startLine = textDocumentPosition.Position.Line;
            int endColumn = textDocumentPosition.Position.Character;

            ScriptParseInfo? scriptParseInfo = GetScriptParseInfo(scriptFile.ClientUri);

            if (scriptParseInfo == null)
            {
                Logger.Verbose($"GetSignatureHelp - Could not find ScriptParseInfo for {scriptFile}");
                // Cache not set up yet - skip and wait until later
                return null;
            }

            ConnectionInfo? connInfo;
            ConnectionServiceInstance.TryFindConnection(
                scriptFile.ClientUri,
                out connInfo);

            // reparse and bind the SQL statement if needed
            if (RequiresReparse(scriptParseInfo, scriptFile))
            {
                await ParseAndBind(scriptFile, connInfo);
            }
            else
            {
                Logger.Verbose($"GetSignatureHelp - No reparse needed for {scriptFile}");
            }

            if (scriptParseInfo.ParseResult != null)
            {
                if (Monitor.TryEnter(scriptParseInfo.BuildingMetadataLock))
                {
                    try
                    {
                        QueueItem queueItem = this.BindingQueue.QueueBindingOperation(
                            key: scriptParseInfo.ConnectionKey,
                            bindingTimeout: LanguageService.BindingTimeout,
                            bindOperation: (bindingContext, cancelToken) =>
                            {
                                // get the list of possible current methods for signature help
                                var methods = Resolver.FindMethods(
                                    scriptParseInfo.ParseResult,
                                    startLine + 1,
                                    endColumn + 1,
                                    bindingContext.MetadataDisplayInfoProvider);

                                // get positional information on the current method
                                var methodLocations = Resolver.GetMethodNameAndParams(scriptParseInfo.ParseResult,
                                   startLine + 1,
                                   endColumn + 1,
                                   bindingContext.MetadataDisplayInfoProvider);

                                if (methodLocations != null)
                                {
                                    // convert from the parser format to the VS Code wire format
                                    return AutoCompleteHelper.ConvertMethodHelpTextListToSignatureHelp(methods,
                                        methodLocations,
                                        startLine + 1,
                                        endColumn + 1);
                                }
                                else
                                {
                                    Logger.Verbose($"GetSignatureHelp - Didn't get any method locations from parse result");
                                    return null;
                                }
                            });
                        queueItem.ItemProcessed.WaitOne();
                        Logger.Verbose($"GetSignatureHelp - Got result {queueItem.Result}");
                        return queueItem.GetResultAsT<SignatureHelp>();
                    }
                    catch (Exception ex)
                    {
                        Logger.Error("Exception in GetSignatureHelp " + ex.ToString());
                    }
                    finally
                    {
                        Monitor.Exit(scriptParseInfo.BuildingMetadataLock);
                    }
                }
            }
            Logger.Verbose($"GetSignatureHelp - No ScriptParseInfo.ParseResult for {scriptFile}");

            // return null if there isn't a tooltip for the current location
            return null;
        }

        /// <summary>
        /// Return the completion item list for the current text position.
        /// This method does not await cache builds since it expects to return quickly
        /// </summary>
        /// <param name="textDocumentPosition"></param>
        public async Task<CompletionItem[]> GetCompletionItems(
            TextDocumentPosition textDocumentPosition,
            ScriptFile scriptFile,
            ConnectionInfo connInfo)
        {
            // initialize some state to parse and bind the current script file
            this.currentCompletionParseInfo = null;
            CompletionItem[] resultCompletionItems = null;
            CompletionService completionService = new CompletionService(BindingQueue);
            bool useLowerCaseSuggestions = this.CurrentWorkspaceSettings.SqlTools.IntelliSense.LowerCaseSuggestions.Value;

            // get the current script parse info object
            ScriptParseInfo scriptParseInfo = GetScriptParseInfo(scriptFile.ClientUri);

            if (scriptParseInfo == null)
            {
                var scriptDocInfo = ScriptDocumentInfo.CreateDefaultDocumentInfo(textDocumentPosition, scriptFile);
                resultCompletionItems = AutoCompleteHelper.GetDefaultCompletionItems(scriptDocInfo, useLowerCaseSuggestions);
                //call completion extensions only for default completion list
                resultCompletionItems = await ApplyCompletionExtensions(connInfo, resultCompletionItems, scriptDocInfo);
                return resultCompletionItems;
            }

            // reparse and bind the SQL statement if needed
            if (RequiresReparse(scriptParseInfo, scriptFile))
            {
                await ParseAndBind(scriptFile, connInfo);
            }

            ScriptDocumentInfo scriptDocumentInfo = new ScriptDocumentInfo(textDocumentPosition, scriptFile, scriptParseInfo);

            // if the parse failed then return the default list
            if (scriptParseInfo.ParseResult == null)
            {
                resultCompletionItems = AutoCompleteHelper.GetDefaultCompletionItems(scriptDocumentInfo, useLowerCaseSuggestions);
                //call completion extensions only for default completion list
                resultCompletionItems = await ApplyCompletionExtensions(connInfo, resultCompletionItems, scriptDocumentInfo);
                return resultCompletionItems;
            }
            AutoCompletionResult result = completionService.CreateCompletions(connInfo, scriptDocumentInfo, useLowerCaseSuggestions);
            // cache the current script parse info object to resolve completions later
            this.currentCompletionParseInfo = scriptParseInfo;
            resultCompletionItems = result.CompletionItems;
            
            /*
             Expanding star expressions in query only when the script is connected to a database
             as the parser requires a connection to determine column names
            */
            if (connInfo != null)
            {
                CompletionItem[] starExpansionSuggestion = AutoCompleteHelper.ExpandSqlStarExpression(scriptDocumentInfo);
                if (starExpansionSuggestion != null)
                {
                    return starExpansionSuggestion;
                }
            }

            // if there are no completions then provide the default list
            if (resultCompletionItems == null)
            {
                resultCompletionItems = AutoCompleteHelper.GetDefaultCompletionItems(scriptDocumentInfo, useLowerCaseSuggestions);
                //call completion extensions only for default completion list
                resultCompletionItems = await ApplyCompletionExtensions(connInfo, resultCompletionItems, scriptDocumentInfo);
            }

            return resultCompletionItems;
        }

        /// <summary>
        /// Run all completion extensions
        /// </summary>
        /// <param name="connInfo"></param>
        /// <param name="resultCompletionItems"></param>
        /// <param name="scriptDocumentInfo"></param>
        /// <returns></returns>
        private async Task<CompletionItem[]> ApplyCompletionExtensions(ConnectionInfo connInfo, CompletionItem[] resultCompletionItems, ScriptDocumentInfo scriptDocumentInfo)
        {
            //invoke the completion extensions
            foreach (var completionExt in completionExtensions.Values)
            {
                var cancellationTokenSource = new CancellationTokenSource();
                cancellationTokenSource.CancelAfter(CompletionExtTimeout);
                var cancellationToken = cancellationTokenSource.Token;
                try
                {
                    resultCompletionItems = await completionExt.HandleCompletionAsync(connInfo, scriptDocumentInfo, resultCompletionItems, cancellationToken).WithTimeout(CompletionExtTimeout);
                }
                catch (Exception e)
                {
                    Logger.Error(string.Format("Exception in calling completion extension {0}:\n{1}", completionExt.Name, e.ToString()));
                }

                cancellationTokenSource.Dispose();
            }

            return resultCompletionItems;
        }

        #endregion

        #region Diagnostic Provider methods

        /// <summary>
        /// Checks for non T-SQL syntax within the Parse Result, and 
        /// sends notification if non T-SQL syntax is detected
        /// Public for testing purposes
        /// </summary>
        /// <param name="uri"></param>
        /// <param name="parseResult"></param>
        public async Task<bool> CheckForNonTSqlLanguage(string uri, ParseResult parseResult)
        {
            if (parseResult.Errors.Count() >= TSqlDetectionConstants.SqlFileErrorLimit)
            {
                await ServiceHostInstance.SendEvent(
                                   NonTSqlNotification.Type,
                                   new NonTSqlParams
                                   {
                                       OwnerUri = uri,
                                       NonTSqlKeyword = null,
                                   });
                return true;
            }

            HashSet<string> identifiers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            IList<SqlIdentifier> scriptIdentifiers = parseResult.Script.RetrieveAllIdentifiers();
            foreach (SqlIdentifier identifier in scriptIdentifiers)
            {
                identifiers.Add(identifier.ToString());
            }

            int count = 0;
            string[] nonTSqlKeywords = new string[TSqlDetectionConstants.NonTSqlKeywordLimit];
            foreach (Token token in parseResult.Script.Tokens)
            {
                if (token.IsSignificant && TSqlDetectionConstants.Keywords.Contains(token.Text) && !identifiers.Contains(token.Text))
                {
                    nonTSqlKeywords[count] = token.Text;
                    count++;
                    if (count == TSqlDetectionConstants.NonTSqlKeywordLimit)
                    {
                        await ServiceHostInstance.SendEvent(
                        NonTSqlNotification.Type,
                        new NonTSqlParams
                        {
                            OwnerUri = uri,
                            NonTSqlKeyword = string.Join(", ", nonTSqlKeywords)
                        });
                        return true;
                    }
                }
            }
            return false;
        }

        /// <summary>
        /// Gets a list of semantic diagnostic marks for the provided script file
        /// </summary>
        /// <param name="scriptFile"></param>
        internal async Task<ScriptFileMarker[]> GetSemanticMarkers(ScriptFile scriptFile)
        {
            ConnectionInfo connInfo;
            ConnectionServiceInstance.TryFindConnection(
                scriptFile.ClientUri,
                out connInfo);
            var parseResult = await ParseAndBind(scriptFile, connInfo);

            _ = CheckForNonTSqlLanguage(scriptFile.ClientUri, parseResult);

            // build a list of SQL script file markers from the errors
            List<ScriptFileMarker> markers = new List<ScriptFileMarker>();
            if (parseResult != null && parseResult.Errors != null)
            {
                foreach (var error in parseResult.Errors)
                {
                    markers.Add(new ScriptFileMarker()
                    {
                        Message = error.Message,
                        Level = ScriptFileMarkerLevel.Error,
                        ScriptRegion = new ScriptRegion()
                        {
                            File = scriptFile.FilePath,
                            StartLineNumber = error.Start.LineNumber,
                            StartColumnNumber = error.Start.ColumnNumber,
                            StartOffset = 0,
                            EndLineNumber = error.End.LineNumber,
                            EndColumnNumber = error.End.ColumnNumber,
                            EndOffset = 0
                        }
                    });
                }
            }

            if (CurrentWorkspaceSettings.QueryExecutionSettings.IsAlwaysEncryptedParameterizationEnabled)
            {
                markers.AddRange(SqlParameterizer.CodeSense(scriptFile.Contents));
            }

            return markers.ToArray();
        }

        /// <summary>
        /// Runs script diagnostics on changed files
        /// </summary>
        /// <param name="filesToAnalyze"></param>
        /// <param name="eventContext"></param>
        private Task RunScriptDiagnostics(ScriptFile[] filesToAnalyze, EventContext eventContext)
        {
            if (!CurrentWorkspaceSettings.IsDiagnosticsEnabled)
            {
                // If the user has disabled script analysis, skip it entirely
                return Task.FromResult(true);
            }

            // If there's an existing task, attempt to cancel it
            try
            {
                if (existingRequestCancellation != null)
                {
                    // Try to cancel the request
                    existingRequestCancellation.Cancel();

                    // If cancellation didn't throw an exception,
                    // clean up the existing token
                    existingRequestCancellation.Dispose();
                    existingRequestCancellation = null;
                }
            }
            catch (Exception e)
            {
                Logger.Error(string.Format("Exception while cancelling analysis task:\n\n{0}", e.ToString()));

                TaskCompletionSource<bool> cancelTask = new TaskCompletionSource<bool>();
                cancelTask.SetCanceled();
                return cancelTask.Task;
            }

            // Create a fresh cancellation token and then start the task.
            // We create this on a different TaskScheduler so that we
            // don't block the main message loop thread.
            existingRequestCancellation = new CancellationTokenSource();
            Task.Factory.StartNew(
                () =>
                    this.DelayedDiagnosticsTask = DelayThenInvokeDiagnostics(
                        LanguageService.DiagnosticParseDelay,
                        filesToAnalyze,
                        eventContext,
                        existingRequestCancellation.Token),
                CancellationToken.None,
                TaskCreationOptions.None,
                TaskScheduler.Default);

            return Task.FromResult(true);
        }

        /// <summary>
        /// Actually run the script diagnostics after waiting for some small delay
        /// </summary>
        /// <param name="delayMilliseconds"></param>
        /// <param name="filesToAnalyze"></param>
        /// <param name="eventContext"></param>
        /// <param name="cancellationToken"></param>
        private async Task DelayThenInvokeDiagnostics(
            int delayMilliseconds,
            ScriptFile[] filesToAnalyze,
            EventContext eventContext,
            CancellationToken cancellationToken)
        {
            // First of all, wait for the desired delay period before
            // analyzing the provided list of files
            try
            {
                await Task.Delay(delayMilliseconds, cancellationToken);
            }
            catch (TaskCanceledException)
            {
                // If the task is cancelled, exit directly
                return;
            }

            // If we've made it past the delay period then we don't care
            // about the cancellation token anymore.  This could happen
            // when the user stops typing for long enough that the delay
            // period ends but then starts typing while analysis is going
            // on.  It makes sense to send back the results from the first
            // delay period while the second one is ticking away.

            // Get the requested files
            foreach (ScriptFile scriptFile in filesToAnalyze)
            {
                try
                {
                    if (IsPreviewWindow(scriptFile))
                    {
                        continue;
                    }
                    else if (ShouldSkipNonMssqlFile(scriptFile.ClientUri))
                    {
                        // Clear out any existing markers in case file type was changed
                        await DiagnosticsHelper.ClearScriptDiagnostics(scriptFile.ClientUri, eventContext);
                        continue;
                    }

                    Logger.Verbose("Analyzing script file: " + scriptFile.FilePath);

                    // Start task asynchronously without blocking main thread - this is by design.
                    // Explanation: STS message queues are single-threaded queues, which should be unblocked as soon as possible.
                    // All Long-running tasks should be performed in a non-blocking background task, and results should be sent when ready.
#pragma warning disable CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed
                    GetSemanticMarkers(scriptFile).ContinueWith(async t =>
                    {
                        if (t.IsFaulted)
                        {
                            Logger.Error($"Error analyzing script file {scriptFile.FilePath}: {t.Exception}");
                            return;
                        }
                        var semanticMarkers = t.GetAwaiter().GetResult();
                        Logger.Verbose($"Analysis complete for script file: {scriptFile.FilePath}");
                        await DiagnosticsHelper.PublishScriptDiagnostics(scriptFile, semanticMarkers, eventContext);
                    }, CancellationToken.None);
#pragma warning restore CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed
                }
                catch (Exception e)
                {
                    // If any errors occur while starting up the analyze task for a script file then just log it and move on so
                    // we at least try to analyze the other files
                    Logger.Error($"Error while starting to analyze script file {scriptFile.FilePath}: {e}");
                }

            }
        }

        #endregion

        /// <summary>
        /// Adds a new or updates an existing script parse info instance in local cache
        /// </summary>
        /// <param name="uri"></param>
        /// <param name="scriptInfo"></param>
        internal void AddOrUpdateScriptParseInfo(string uri, ScriptParseInfo scriptInfo)
        {
            lock (this.parseMapLock)
            {
                if (this.ScriptParseInfoMap.ContainsKey(uri))
                {
                    Logger.Verbose($"Updating ScriptParseInfo for uri {uri}");
                    this.ScriptParseInfoMap[uri] = scriptInfo;
                }
                else
                {
                    Logger.Verbose($"Adding ScriptParseInfo for uri {uri}");
                    this.ScriptParseInfoMap.TryAdd(uri, scriptInfo);
                }

            }
        }

        /// <summary>
        /// Gets a script parse info object for a file from the local cache
        /// Internal for testing purposes only
        /// </summary>
        /// <param name="uri"></param>
        /// <param name="createIfNotExists">Creates a new instance if one doesn't exist</param>
        internal ScriptParseInfo? GetScriptParseInfo(string uri, bool createIfNotExists = false)
        {
            lock (this.parseMapLock)
            {
                if (this.ScriptParseInfoMap.TryGetValue(uri, out ScriptParseInfo value))
                {
                    Logger.Verbose($"Found ScriptParseInfo for uri {uri}");
                    return value;
                }
                else if (createIfNotExists)
                {
                    Logger.Verbose($"ScriptParseInfo for uri {uri} did not exist, creating new one");
                    // create a new script parse info object and initialize with the current settings
                    ScriptParseInfo scriptInfo = new ScriptParseInfo();
                    this.ScriptParseInfoMap.TryAdd(uri, scriptInfo);
                    return scriptInfo;
                }
                else
                {
                    Logger.Verbose($"Could not find ScriptParseInfo for uri {uri}");
                    return null;
                }
            }
        }

        internal bool RemoveScriptParseInfo(string uri)
        {
            lock (this.parseMapLock)
            {
                Logger.Verbose($"Removing ScriptParseInfo for uri {uri}");
                return this.ScriptParseInfoMap.TryRemove(uri, out _);
            }
        }

        /// <summary>
        /// Returns a flag indicating if the ScriptFile refers to the output window.
        /// </summary>
        /// <param name="scriptFile"></param>
        internal bool IsPreviewWindow(ScriptFile scriptFile)
        {
            if (scriptFile != null && !string.IsNullOrWhiteSpace(scriptFile.ClientUri))
            {
                return scriptFile.ClientUri.StartsWith("tsqloutput:");
            }
            else
            {
                return false;
            }
        }

        internal void DeletePeekDefinitionScripts()
        {
            // Delete temp folder created to store peek definition scripts
            if (FileUtilities.SafeDirectoryExists(FileUtilities.PeekDefinitionTempFolder))
            {
                FileUtilities.SafeDirectoryDelete(FileUtilities.PeekDefinitionTempFolder, true);
            }
        }

        internal string ParseStatementAtPosition(string sql, int line, int column)
        {
            // adjust from 0-based to 1-based index
            int parserLine = line + 1;
            int parserColumn = column + 1;

            // parse current SQL file contents to retrieve a list of errors
            ParseResult parseResult = Parser.Parse(sql, this.DefaultParseOptions);
            if (parseResult != null && parseResult.Script != null && parseResult.Script.Batches != null)
            {
                foreach (var batch in parseResult.Script.Batches)
                {
                    if (batch.Statements == null)
                    {
                        continue;
                    }

                    // If there is a single statement on the line, track it so that we can return it regardless of where the user's cursor is
                    SqlStatement lineStatement = null;
                    bool? lineHasSingleStatement = null;

                    // check if the batch matches parameters
                    if (batch.StartLocation.LineNumber <= parserLine
                        && batch.EndLocation.LineNumber >= parserLine)
                    {
                        foreach (var statement in batch.Statements)
                        {
                            // check if the statement matches parameters
                            if (statement.StartLocation.LineNumber <= parserLine
                                && statement.EndLocation.LineNumber >= parserLine)
                            {
                                if (statement.EndLocation.LineNumber == parserLine && statement.EndLocation.ColumnNumber < parserColumn
                                    || statement.StartLocation.LineNumber == parserLine && statement.StartLocation.ColumnNumber > parserColumn)
                                {
                                    if (lineHasSingleStatement == null)
                                    {
                                        lineHasSingleStatement = true;
                                        lineStatement = statement;
                                    }
                                    else if (lineHasSingleStatement == true)
                                    {
                                        lineHasSingleStatement = false;
                                    }
                                    continue;
                                }
                                return statement.Sql;
                            }
                        }
                    }

                    if (lineHasSingleStatement == true)
                    {
                        return lineStatement.Sql;
                    }
                }
            }

            return string.Empty;
        }

        public void Dispose()
        {
            if (bindingQueue != null)
            {
                bindingQueue.Dispose();
            }
        }
    }
}