//
// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
//

#nullable disable

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using Microsoft.Data.SqlClient;
using Microsoft.Data.SqlClient.AlwaysEncrypted.AzureKeyVaultProvider;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.SqlTools.Hosting.Protocol;
using Microsoft.SqlServer.Management.Common;
using Microsoft.SqlTools.ServiceLayer.Connection.Contracts;
using Microsoft.SqlTools.ServiceLayer.Connection.ReliableConnection;
using Microsoft.SqlTools.ServiceLayer.LanguageServices;
using Microsoft.SqlTools.ServiceLayer.LanguageServices.Contracts;
using Microsoft.SqlTools.ServiceLayer.Utility;
using Microsoft.SqlTools.Utility;
using static Microsoft.SqlTools.Utility.SqlConstants;
using Microsoft.SqlTools.Authentication.Sql;
using Microsoft.SqlTools.Authentication;
using System.IO;
using Microsoft.SqlTools.Hosting.Utility;
using Constants = Microsoft.SqlTools.Hosting.Protocol.Constants;
using Microsoft.SqlTools.SqlCore.Connection;

namespace Microsoft.SqlTools.ServiceLayer.Connection
{
    /// <summary>
    /// Main class for the Connection Management services
    /// </summary>
    public class ConnectionService
    {
        public const string AdminConnectionPrefix = "ADMIN:";
        internal const string PasswordPlaceholder = "******";
        private const string SqlAzureEdition = "SQL Azure";

        public const int MaxTolerance = 2 * 60; // two minutes - standard tolerance across ADS for Microsoft Entra tokens

        // SQL Error Code Constants
        // Referenced from: https://learn.microsoft.com/en-us/sql/relational-databases/errors-events/database-engine-events-and-errors?view=sql-server-ver16
        private const int DoesNotMeetPWReqs = 18466; // Password does not meet complexity requirements.
        private const int PWCannotBeUsed = 18463; // Password cannot be used at this time.

        // Default SQL constants (required to ensure connections such as serverless are able to wake up, connect, and retry properly).
        private const int DefaultConnectTimeout = 30;
        private const int DefaultCommandTimeout = 30;

        /// <summary>
        /// Singleton service instance
        /// </summary>
        private static readonly Lazy<ConnectionService> instance
            = new Lazy<ConnectionService>(() => new ConnectionService());

        /// <summary>
        /// Gets the singleton service instance
        /// </summary>
        public static ConnectionService Instance => instance.Value;

        private static readonly SqlConnectionStringBuilder defaultBuilder = new SqlConnectionStringBuilder();

        /// <summary>
        /// IV and Key as received from Encryption Key Notification event.
        /// </summary>
        private (string key, string iv) encryptionKeys;

        /// <summary>
        /// The SQL connection factory object
        /// </summary>
        private ISqlConnectionFactory connectionFactory;

        private DatabaseLocksManager lockedDatabaseManager;

        /// <summary>
        /// A map containing all CancellationTokenSource objects that are associated with a given URI/ConnectionType pair.
        /// Entries in this map correspond to DbConnection instances that are in the process of connecting.
        /// </summary>
        private readonly ConcurrentDictionary<CancelTokenKey, CancellationTokenSource> cancelTupleToCancellationTokenSourceMap =
                    new ConcurrentDictionary<CancelTokenKey, CancellationTokenSource>();

        /// <summary>
        /// A map containing the uris of connections with expired tokens, these editors should have intellisense
        /// disabled until the new refresh token is returned, upon which they will be removed from the map
        /// </summary>
        public readonly ConcurrentDictionary<string, Boolean> TokenUpdateUris = new ConcurrentDictionary<string, Boolean>();
        private readonly object cancellationTokenSourceLock = new object();

        private ConcurrentDictionary<string, IConnectedBindingQueue> connectedQueues = new ConcurrentDictionary<string, IConnectedBindingQueue>();

        /// <summary>
        /// Map from script URIs to ConnectionInfo objects
        /// This is internal for testing access only
        /// </summary>
        internal ConcurrentDictionary<string, ConnectionInfo> OwnerToConnectionMap { get; } = new ConcurrentDictionary<string, ConnectionInfo>();

        /// <summary>
        /// Database Lock manager instance
        /// </summary>
        internal DatabaseLocksManager LockedDatabaseManager
        {
            get
            {
                lockedDatabaseManager ??= DatabaseLocksManager.Instance;
                return lockedDatabaseManager;
            }
            set
            {
                this.lockedDatabaseManager = value;
            }
        }

        /// <summary>
        /// Service host object for sending/receiving requests/events.
        /// Internal for testing purposes.
        /// </summary>
        internal IProtocolEndpoint ServiceHost { get; set; }

        /// <summary>
        /// Gets the connection queue
        /// </summary>
        internal IConnectedBindingQueue ConnectionQueue
        {
            get
            {
                return this.GetConnectedQueue("Default");
            }
        }

        static ConnectionService()
        {
            SqlColumnEncryptionAzureKeyVaultProvider sqlColumnEncryptionAzureKeyVaultProvider = new SqlColumnEncryptionAzureKeyVaultProvider(AzureActiveDirectoryAuthenticationCallback);
            SqlConnection.RegisterColumnEncryptionKeyStoreProviders(customProviders: new Dictionary<string, SqlColumnEncryptionKeyStoreProvider>(capacity: 1, comparer: StringComparer.OrdinalIgnoreCase)
            {
                { SqlColumnEncryptionAzureKeyVaultProvider.ProviderName, sqlColumnEncryptionAzureKeyVaultProvider }
            });
        }


        /// <summary>
        /// Default constructor should be private since it's a singleton class, but we need a constructor
        /// for use in unit test mocking.
        /// </summary>
        public ConnectionService()
        {
            var defaultQueue = new ConnectedBindingQueue(needsMetadata: false);
            connectedQueues.AddOrUpdate("Default", defaultQueue, (key, old) => defaultQueue);
            this.LockedDatabaseManager.ConnectionService = this;
        }

        public static async Task<string> AzureActiveDirectoryAuthenticationCallback(string authority, string resource, string scope)
        {
            RequestSecurityTokenParams message = new RequestSecurityTokenParams()
            {
                Provider = "Azure",
                Authority = authority,
                Resource = resource,
                Scopes = new string[] { scope }
            };

            RequestSecurityTokenResponse response = await Instance.ServiceHost.SendRequest(SecurityTokenRequest.Type, message, true).ConfigureAwait(false);

            return response.Token;
        }
        /// <summary>
        /// Default Application name as received in service startup
        /// </summary>
        public static string ApplicationName { get; set; }

        /// <summary>
        /// Enables configured 'Sql Authentication Provider' for 'Active Directory Interactive' authentication mode to be used
        /// when user chooses 'Azure MFA'.
        /// </summary>
        public bool EnableSqlAuthenticationProvider { get; set; }

        /// <summary>
        /// Enables connection pooling for all SQL connections, removing feature name identifier from application name to prevent unwanted connection pools.
        /// </summary>
        public static bool EnableConnectionPooling { get; set; }

        /// <summary>
        /// Returns a connection queue for given type
        /// </summary>
        /// <param name="type"></param>
        /// <returns></returns>
        public IConnectedBindingQueue GetConnectedQueue(string type)
        {
            IConnectedBindingQueue connectedBindingQueue;
            if (connectedQueues.TryGetValue(type, out connectedBindingQueue))
            {
                return connectedBindingQueue;
            }
            return null;
        }

        /// <summary>
        /// Returns all the connection queues
        /// </summary>
        public IEnumerable<IConnectedBindingQueue> ConnectedQueues
        {
            get
            {
                return this.connectedQueues.Values;
            }
        }

        /// <summary>
        /// Register a new connection queue if not already registered
        /// </summary>
        /// <param name="type"></param>
        /// <param name="connectedQueue"></param>
        public virtual void RegisterConnectedQueue(string type, IConnectedBindingQueue connectedQueue)
        {
            if (!connectedQueues.ContainsKey(type))
            {
                connectedQueues.AddOrUpdate(type, connectedQueue, (key, old) => connectedQueue);
            }
        }

        /// <summary>
        /// Callback for onconnection handler
        /// </summary>
        /// <param name="sqlConnection"></param>
        public delegate Task OnConnectionHandler(ConnectionInfo info);

        /// <summary>
        /// Callback for ondisconnect handler
        /// </summary>
        public delegate Task OnDisconnectHandler(IConnectionSummary summary, string ownerUri);

        /// <summary>
        /// List of onconnection handlers
        /// </summary>
        private readonly List<OnConnectionHandler> onConnectionActivities = new List<OnConnectionHandler>();

        /// <summary>
        /// List of ondisconnect handlers
        /// </summary>
        private readonly List<OnDisconnectHandler> onDisconnectActivities = new List<OnDisconnectHandler>();

        /// <summary>
        /// Gets the SQL connection factory instance
        /// </summary>
        public ISqlConnectionFactory ConnectionFactory
        {
            get
            {
                this.connectionFactory ??= new SqlConnectionFactory();
                return this.connectionFactory;
            }

            internal set { this.connectionFactory = value; }
        }

        /// <summary>
        /// Test constructor that injects dependency interfaces
        /// </summary>
        /// <param name="testFactory"></param>
        public ConnectionService(ISqlConnectionFactory testFactory) => this.connectionFactory = testFactory;

        // Attempts to link a URI to an actively used connection for this URI
        public virtual bool TryFindConnection(string ownerUri, out ConnectionInfo connectionInfo) => this.OwnerToConnectionMap.TryGetValue(ownerUri, out connectionInfo);

        /// <summary>
        /// Refreshes the auth token of a given connection, if needed
        /// </summary>
        /// <param name="ownerUri">The URI of the connection</param>
        /// <returns> True if a refreshed was needed and requested, false otherwise </returns>
        internal async Task<bool> TryRequestRefreshAuthToken(string ownerUri)
        {
            ConnectionInfo connInfo;
            if (this.TryFindConnection(ownerUri, out connInfo))
            {
                // If not an azure connection, no need to refresh token
                if (connInfo.ConnectionDetails.AuthenticationType != AzureMFA)
                {
                    return false;
                }
                else
                {
                    // Check if token is expired or about to expire
                    if (connInfo.ConnectionDetails.ExpiresOn - DateTimeOffset.Now.ToUnixTimeSeconds() < MaxTolerance)
                    {

                        var requestMessage = new RefreshTokenParams
                        {
                            AccountId = connInfo.ConnectionDetails.GetOptionValue("azureAccount", string.Empty),
                            TenantId = connInfo.ConnectionDetails.GetOptionValue("azureTenantId", string.Empty),
                            Provider = "Azure",
                            Resource = "SQL",
                            Uri = ownerUri
                        };
                        if (string.IsNullOrEmpty(requestMessage.TenantId))
                        {
                            Logger.Error("No tenant in connection details when refreshing token for connection {ownerUri}");
                            return false;
                        }
                        if (string.IsNullOrEmpty(requestMessage.AccountId))
                        {
                            Logger.Error("No accountId in connection details when refreshing token for connection {ownerUri}");
                            return false;
                        }
                        // Check if the token is updating already, in which case there is no need to request a new one,
                        // but still return true so that autocompletion is disabled until the token is refreshed
                        if (!this.TokenUpdateUris.TryAdd(ownerUri, true))
                        {
                            return true;
                        }
                        await this.ServiceHost.SendEvent(RefreshTokenNotification.Type, requestMessage);
                        return true;
                    }
                    else
                    {
                        return false;
                    }
                }
            }
            else
            {
                Logger.Error("Failed to find connection when refreshing token");
                return false;
            }
        }

        /// <summary>
        /// Requests an update of the Microsoft Entra auth token
        /// </summary>
        /// <param name="refreshToken">The token to update</param>
        /// <returns>true upon successful update, false if it failed to find
        /// the connection</returns>
        internal void UpdateAuthToken(TokenRefreshedParams tokenRefreshedParams)
        {
            if (!this.TryFindConnection(tokenRefreshedParams.Uri, out ConnectionInfo connection))
            {
                Logger.Error($"Failed to find connection when updating refreshed token for URI {tokenRefreshedParams.Uri}");
                return;
            }
            this.TokenUpdateUris.Remove(tokenRefreshedParams.Uri, out var result);
            connection.TryUpdateAccessToken(new SecurityToken() { Token = tokenRefreshedParams.Token, ExpiresOn = tokenRefreshedParams.ExpiresOn });
        }

        /// <summary>
        /// Validates the given ConnectParams object.
        /// </summary>
        /// <param name="connectionParams">The params to validate</param>
        /// <returns>A ConnectionCompleteParams object upon validation error,
        /// null upon validation success</returns>
        public ConnectionCompleteParams ValidateConnectParams(ConnectParams connectionParams)
        {
            if (connectionParams == null)
            {
                return new ConnectionCompleteParams
                {
                    ErrorMessage = SR.ConnectionServiceConnectErrorNullParams
                };
            }
            if (!connectionParams.IsValid(out string paramValidationErrorMessage))
            {
                return new ConnectionCompleteParams
                {
                    OwnerUri = connectionParams.OwnerUri,
                    ErrorMessage = paramValidationErrorMessage
                };
            }

            // return null upon success
            return null;
        }

        /// <summary>
        /// Open a connection with the specified ConnectParams
        /// </summary>
        public virtual async Task<ConnectionCompleteParams> Connect(ConnectParams connectionParams)
        {
            // Validate parameters
            ConnectionCompleteParams validationResults = ValidateConnectParams(connectionParams);
            if (validationResults != null)
            {
                return validationResults;
            }

            TrySetConnectionType(connectionParams);

            // Fill in any details that are necessary (timeouts and application name) to ensure connection doesn't immediately disconnect if not specified (such as for serverless). 
            connectionParams.Connection = FillInDefaultDetailsForConnections(connectionParams.Connection, connectionParams.Purpose);

            // If there is no ConnectionInfo in the map, create a new ConnectionInfo,
            // but wait until later when we are connected to add it to the map.
            ConnectionInfo connectionInfo;
            bool connectionChanged = false;
            if (!OwnerToConnectionMap.TryGetValue(connectionParams.OwnerUri, out connectionInfo))
            {
                connectionInfo = new ConnectionInfo(ConnectionFactory, connectionParams.OwnerUri, connectionParams.Connection);
            }
            else if (IsConnectionChanged(connectionParams, connectionInfo))
            {
                // We are actively changing the connection information for this connection. We must disconnect
                // all active connections, since it represents a full context change
                connectionChanged = true;
            }

            DisconnectExistingConnectionIfNeeded(connectionParams, connectionInfo, disconnectAll: connectionChanged);

            if (connectionChanged)
            {
                connectionInfo = new ConnectionInfo(ConnectionFactory, connectionParams.OwnerUri, connectionParams.Connection);
            }

            // Try to open a connection with the given ConnectParams
            ConnectionCompleteParams? response = await this.TryOpenConnection(connectionInfo, connectionParams);
            if (response != null)
            {
                return response;
            }

            // If this is the first connection for this URI, add the ConnectionInfo to the map
            bool addToMap = connectionChanged || !OwnerToConnectionMap.ContainsKey(connectionParams.OwnerUri);
            if (addToMap)
            {
                OwnerToConnectionMap[connectionParams.OwnerUri] = connectionInfo;
            }

            // Return information about the connected SQL Server instance
            ConnectionCompleteParams completeParams = GetConnectionCompleteParams(connectionParams.Type, connectionInfo);
            // Invoke callback notifications
            InvokeOnConnectionActivities(connectionInfo, connectionParams);

            TryCloseConnectionTemporaryConnection(connectionParams, connectionInfo);

            return completeParams;
        }

        private void TryCloseConnectionTemporaryConnection(ConnectParams connectionParams, ConnectionInfo connectionInfo)
        {
            try
            {
                if (connectionParams.Purpose == ConnectionType.ObjectExplorer || connectionParams.Purpose == ConnectionType.Dashboard || connectionParams.Purpose == ConnectionType.GeneralConnection)
                {
                    DbConnection connection;
                    string type = connectionParams.Type;
                    if (connectionInfo.TryGetConnection(type, out connection))
                    {
                        // OE doesn't need to keep the connection open
                        connection.Close();
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Information("Failed to close temporary connections. error: " + ex.Message);
            }
        }

        internal static string GetApplicationNameWithFeature(string applicationName, string featureName)
        {
            string appNameWithFeature = applicationName;
            // Connection Service will not set custom application name if connection pooling is enabled on service.
            if (!EnableConnectionPooling && !string.IsNullOrWhiteSpace(applicationName) && !string.IsNullOrWhiteSpace(featureName) && !applicationName.EndsWith(featureName))
            {
                int appNameStartIndex = applicationName.IndexOf(ApplicationName);
                string originalAppName = appNameStartIndex != -1
                    ? applicationName.Substring(0, appNameStartIndex + ApplicationName.Length)
                    : applicationName; // Reset to default if azdata not found.
                appNameWithFeature = $"{originalAppName}-{featureName}";
            }

            return appNameWithFeature;
        }

        private void TrySetConnectionType(ConnectParams connectionParams)
        {
            if (connectionParams != null && connectionParams.Type == ConnectionType.Default && !string.IsNullOrWhiteSpace(connectionParams.OwnerUri))
            {
                var uri = connectionParams.OwnerUri.ToLowerInvariant();
                if (uri.StartsWith("dashboard://"))
                {
                    connectionParams.Purpose = ConnectionType.Dashboard;
                }
                else if (uri.StartsWith("connection://"))
                {
                    connectionParams.Purpose = ConnectionType.GeneralConnection;
                }
                else if (uri.StartsWith("untitled:sqlquery") || (uri.StartsWith("file://") && uri.EndsWith(".sql")))
                {
                    connectionParams.Purpose = ConnectionType.Query;
                }
            }
            else if (connectionParams != null)
            {
                connectionParams.Purpose = connectionParams.Type;
            }
        }

        private bool IsConnectionChanged(ConnectParams connectionParams, ConnectionInfo connectionInfo)
        {
            if (connectionInfo.HasConnectionType(connectionParams.Type)
                && !connectionInfo.ConnectionDetails.IsComparableTo(connectionParams.Connection))
            {
                return true;
            }
            return false;
        }

        private bool IsDefaultConnectionType(string connectionType)
        {
            return string.IsNullOrEmpty(connectionType) || ConnectionType.Default.Equals(connectionType, StringComparison.CurrentCultureIgnoreCase);
        }

        private void DisconnectExistingConnectionIfNeeded(ConnectParams connectionParams, ConnectionInfo connectionInfo, bool disconnectAll)
        {
            // Resolve if it is an existing connection
            // Disconnect active connection if the URI is already connected for this connection type
            DbConnection existingConnection;
            if (connectionInfo.TryGetConnection(connectionParams.Type, out existingConnection))
            {
                var disconnectParams = new DisconnectParams()
                {
                    OwnerUri = connectionParams.OwnerUri,
                    Type = disconnectAll ? null : connectionParams.Type
                };
                Disconnect(disconnectParams);
            }
        }

        /// <summary>
        /// Creates a ConnectionCompleteParams as a response to a successful connection.
        /// Also sets the DatabaseName and IsAzure properties of ConnectionInfo.
        /// </summary>
        /// <returns>A ConnectionCompleteParams in response to the successful connection</returns>
        private ConnectionCompleteParams GetConnectionCompleteParams(string connectionType, ConnectionInfo connectionInfo)
        {
            ConnectionCompleteParams response = new ConnectionCompleteParams { OwnerUri = connectionInfo.OwnerUri, Type = connectionType };

            try
            {
                DbConnection connection;
                connectionInfo.TryGetConnection(connectionType, out connection);

                // Update with the actual database name in connectionInfo and result
                // Doing this here as we know the connection is open - expect to do this only on connecting
                // Do not update the DB name if it is a DB Pool database name (e.g. "db@pool")
                if (!ConnectionService.IsDbPool(connectionInfo.ConnectionDetails.DatabaseName))
                {
                    connectionInfo.ConnectionDetails.DatabaseName = connection.Database;
                }

                if (!string.IsNullOrEmpty(connectionInfo.ConnectionDetails.ConnectionString))
                {
                    // If the connection was set up with a connection string, use the connection string to get the details
                    var connectionString = new SqlConnectionStringBuilder(connection.ConnectionString);
                    response.ConnectionSummary = new ConnectionSummary
                    {
                        ServerName = connectionString.DataSource,
                        DatabaseName = connectionString.InitialCatalog,
                        UserName = connectionString.UserID
                    };
                }
                else
                {
                    response.ConnectionSummary = new ConnectionSummary
                    {
                        ServerName = connectionInfo.ConnectionDetails.ServerName,
                        DatabaseName = connectionInfo.ConnectionDetails.DatabaseName,
                        UserName = connectionInfo.ConnectionDetails.UserName
                    };
                }

                response.ConnectionId = connectionInfo.ConnectionId.ToString();

                var reliableConnection = connection as ReliableSqlConnection;
                DbConnection underlyingConnection = reliableConnection != null
                    ? reliableConnection.GetUnderlyingConnection()
                    : connection;
                
                var serverConnId = (underlyingConnection as SqlConnection).ServerProcessId;
                if (serverConnId != 0) {
                    // If 0, that would mean the connection is inactive, so there's no 
                    // need to return the connection id.
                    response.ServerConnectionId = serverConnId.ToString();
                }

                ReliableConnectionHelper.ServerInfo serverInfo = ReliableConnectionHelper.GetServerVersion(underlyingConnection);
                response.ServerInfo = new ServerInfo
                {
                    ServerMajorVersion = serverInfo.ServerMajorVersion,
                    ServerMinorVersion = serverInfo.ServerMinorVersion,
                    ServerReleaseVersion = serverInfo.ServerReleaseVersion,
                    EngineEditionId = serverInfo.EngineEditionId,
                    ServerVersion = serverInfo.ServerVersion,
                    ServerLevel = serverInfo.ServerLevel,
                    ServerEdition = MapServerEdition(serverInfo),
                    IsCloud = serverInfo.IsCloud,
                    AzureVersion = serverInfo.AzureVersion,
                    OsVersion = serverInfo.OsVersion,
                    MachineName = serverInfo.MachineName,
                    CpuCount = serverInfo.CpuCount,
                    PhysicalMemoryInMB = serverInfo.PhysicalMemoryInMB,
                    Options = serverInfo.Options
                };
                connectionInfo.IsCloud = serverInfo.IsCloud;
                connectionInfo.MajorVersion = serverInfo.ServerMajorVersion;
                connectionInfo.IsSqlDb = serverInfo.EngineEditionId == (int)DatabaseEngineEdition.SqlDatabase;
                connectionInfo.IsSqlDW = (serverInfo.EngineEditionId == (int)DatabaseEngineEdition.SqlDataWarehouse);
                // Determines that access token is used for creating connection.
                connectionInfo.IsAzureAuth = connectionInfo.ConnectionDetails.AuthenticationType == AzureMFA;
                connectionInfo.EngineEdition = (DatabaseEngineEdition)serverInfo.EngineEditionId;
                // Azure Data Studio supports SQL Server 2014 and later releases.
                response.IsSupportedVersion = serverInfo.IsCloud || serverInfo.ServerMajorVersion >= 12;
            }
            catch (Exception ex)
            {
                response.Messages = ex.ToString();
                response.ErrorMessage = ex.Message;
            }

            return response;
        }

        private string MapServerEdition(ReliableConnectionHelper.ServerInfo serverInfo)
        {
            string serverEdition = serverInfo.ServerEdition;
            if (string.IsNullOrWhiteSpace(serverEdition))
            {
                return string.Empty;
            }
            if (SqlAzureEdition.Equals(serverEdition, StringComparison.OrdinalIgnoreCase))
            {
                switch (serverInfo.EngineEditionId)
                {
                    case (int)DatabaseEngineEdition.SqlDataWarehouse:
                        serverEdition = SR.AzureSqlDwEdition;
                        break;
                    case (int)DatabaseEngineEdition.SqlStretchDatabase:
                        serverEdition = SR.AzureSqlStretchEdition;
                        break;
                    case (int)DatabaseEngineEdition.SqlOnDemand:
                        serverEdition = SR.AzureSqlAnalyticsOnDemandEdition;
                        break;
                    default:
                        serverEdition = SR.AzureSqlDbEdition;
                        break;
                }
            }
            return serverEdition;
        }

        internal static ConnectionDetails FillInDefaultDetailsForConnections(ConnectionDetails inputConnectionDetails, string featureName) { 
            ConnectionDetails newConnectionDetails = inputConnectionDetails;

            if(string.IsNullOrWhiteSpace(newConnectionDetails.ApplicationName)) 
            {
                newConnectionDetails.ApplicationName = ApplicationName;
            }
            else 
            {
                newConnectionDetails.ApplicationName = GetApplicationNameWithFeature(newConnectionDetails.ApplicationName, featureName);
            }

            newConnectionDetails.ConnectTimeout = Math.Max(DefaultConnectTimeout, newConnectionDetails.ConnectTimeout ?? 0);

            newConnectionDetails.CommandTimeout = Math.Max(DefaultCommandTimeout, newConnectionDetails.CommandTimeout ?? 0);

            return newConnectionDetails;
        }

        /// <summary>
        /// Tries to create and open a connection with the given ConnectParams.
        /// </summary>
        /// <returns>null upon success, a ConnectionCompleteParams detailing the error upon failure</returns>
        private async Task<ConnectionCompleteParams> TryOpenConnection(ConnectionInfo connectionInfo, ConnectParams connectionParams)
        {
            CancellationTokenSource source = null;
            DbConnection connection = null;
            CancelTokenKey cancelKey = new CancelTokenKey { OwnerUri = connectionParams.OwnerUri, Type = connectionParams.Type };
            ConnectionCompleteParams response = new ConnectionCompleteParams { OwnerUri = connectionInfo.OwnerUri, Type = connectionParams.Type };
            bool? currentPooling = connectionInfo.ConnectionDetails.Pooling;

            try
            {
                if (!EnableConnectionPooling)
                {
                    connectionInfo.ConnectionDetails.Pooling = false;
                }
                // build the connection string from the input parameters
                string connectionString = BuildConnectionString(connectionInfo.ConnectionDetails);

                // create a sql connection instance (with enabled serverless retry logic to handle sleeping serverless databases)
                connection = connectionInfo.Factory.CreateSqlConnection(connectionString, connectionInfo.ConnectionDetails.AzureAccountToken, SqlRetryProviders.ServerlessDBRetryProvider());
                connectionInfo.AddConnection(connectionParams.Type, connection);

                // Add a cancellation token source so that the connection OpenAsync() can be cancelled
                source = new CancellationTokenSource();
                // Locking here to perform two operations as one atomic operation
                lock (cancellationTokenSourceLock)
                {
                    // If the URI is currently connecting from a different request, cancel it before we try to connect
                    CancellationTokenSource currentSource;
                    if (cancelTupleToCancellationTokenSourceMap.TryGetValue(cancelKey, out currentSource))
                    {
                        currentSource.Cancel();
                    }
                    cancelTupleToCancellationTokenSourceMap[cancelKey] = source;
                }

                // Open the connection
                await connection.OpenAsync(source.Token);
            }
            catch (SqlException ex)
            {
                response.ErrorNumber = ex.Number;
                response.ErrorMessage = ex.Message;
                response.Messages = ex.ToString();
                return response;
            }
            catch (OperationCanceledException)
            {
                // OpenAsync was cancelled
                response.Messages = SR.ConnectionServiceConnectionCanceled;
                return response;
            }
            catch (Exception ex)
            {
                response.ErrorMessage = ex.Message;
                response.Messages = ex.ToString();
                return response;
            }
            finally
            {
                // Remove our cancellation token from the map since we're no longer connecting
                // Using a lock here to perform two operations as one atomic operation
                lock (cancellationTokenSourceLock)
                {
                    // Only remove the token from the map if it is the same one created by this request
                    CancellationTokenSource sourceValue;
                    if (cancelTupleToCancellationTokenSourceMap.TryGetValue(cancelKey, out sourceValue) && sourceValue == source)
                    {
                        cancelTupleToCancellationTokenSourceMap.TryRemove(cancelKey, out sourceValue);
                    }
                    source?.Dispose();
                }
                if (connectionInfo != null && connectionInfo.ConnectionDetails != null)
                {
                    connectionInfo.ConnectionDetails.Pooling = currentPooling;
                }
            }

            // Return null upon success
            return null;
        }

        /// <summary>
        /// Gets the existing connection with the given URI and connection type string. If none exists,
        /// creates a new connection. This cannot be used to create a default connection or to create a
        /// connection if a default connection does not exist.
        /// </summary>
        /// <param name="ownerUri">URI identifying the resource mapped to this connection</param>
        /// <param name="connectionType">
        /// What the purpose for this connection is. A single resource
        /// such as a SQL file may have multiple connections - one for Intellisense, another for query execution
        /// </param>
        /// <param name="alwaysPersistSecurity">
        /// Workaround for .Net Core clone connection issues: should persist security be used so that
        /// when SMO clones connections it can do so without breaking on SQL Password connections.
        /// This should be removed once the core issue is resolved and clone works as expected
        /// </param>
        /// <returns>A DB connection for the connection type requested</returns>
        public virtual async Task<DbConnection> GetOrOpenConnection(string ownerUri, string connectionType, bool alwaysPersistSecurity = false)
        {
            Validate.IsNotNullOrEmptyString(nameof(ownerUri), ownerUri);
            Validate.IsNotNullOrEmptyString(nameof(connectionType), connectionType);

            // Try to get the ConnectionInfo, if it exists
            ConnectionInfo connectionInfo;
            if (!OwnerToConnectionMap.TryGetValue(ownerUri, out connectionInfo))
            {
                throw new ArgumentOutOfRangeException(SR.ConnectionServiceListDbErrorNotConnected(ownerUri));
            }

            // Make sure a default connection exists
            DbConnection connection;
            DbConnection defaultConnection;
            if (!connectionInfo.TryGetConnection(ConnectionType.Default, out defaultConnection))
            {
                throw new InvalidOperationException(SR.ConnectionServiceDbErrorDefaultNotConnected(ownerUri));
            }

            if (IsDedicatedAdminConnection(connectionInfo.ConnectionDetails))
            {
                // Since this is a dedicated connection only 1 is allowed at any time. Return the default connection for use in the requested action
                connection = defaultConnection;
            }
            else
            {
                // Try to get the DbConnection and create if it doesn't already exist
                if (!connectionInfo.TryGetConnection(connectionType, out connection) && ConnectionType.Default != connectionType)
                {
                    connection = await TryOpenConnectionForConnectionType(ownerUri, connectionType, alwaysPersistSecurity, connectionInfo);
                }
            }

            VerifyConnectionOpen(connection);

            return connection;
        }

        private async Task<DbConnection> TryOpenConnectionForConnectionType(string ownerUri, string connectionType,
            bool alwaysPersistSecurity, ConnectionInfo connectionInfo)
        {
            // If the DbConnection does not exist and is not the default connection, create one.
            // We can't create the default (initial) connection here because we won't have a ConnectionDetails
            // if Connect() has not yet been called.
            bool? originalPersistSecurityInfo = connectionInfo.ConnectionDetails.PersistSecurityInfo;
            if (alwaysPersistSecurity)
            {
                connectionInfo.ConnectionDetails.PersistSecurityInfo = true;
            }
            ConnectParams connectParams = new ConnectParams
            {
                OwnerUri = ownerUri,
                Connection = connectionInfo.ConnectionDetails,
                Type = connectionType
            };
            try
            {
                await Connect(connectParams);
            }
            finally
            {
                connectionInfo.ConnectionDetails.PersistSecurityInfo = originalPersistSecurityInfo;
            }

            DbConnection connection;
            connectionInfo.TryGetConnection(connectionType, out connection);
            return connection;
        }

        private void VerifyConnectionOpen(DbConnection connection)
        {
            if (connection == null)
            {
                // Ignore this connection
                return;
            }

            if (connection.State != ConnectionState.Open)
            {
                // Note: this will fail and throw to the caller if something goes wrong.
                // This seems the right thing to do but if this causes serviceability issues where stack trace
                // is unexpected, might consider catching and allowing later code to fail. But given we want to get
                // an opened connection for any action using this, it seems OK to handle in this manner
                ClearPool(connection);
                connection.Open();
            }
        }

        /// <summary>
        /// Clears the connection pool if this is a SqlConnection of some kind.
        /// </summary>
        private void ClearPool(DbConnection connection)
        {
            SqlConnection sqlConn;
            if (TryGetAsSqlConnection(connection, out sqlConn))
            {
                SqlConnection.ClearPool(sqlConn);
            }
        }

        public bool TryGetAsSqlConnection(DbConnection dbConn, out SqlConnection sqlConn)
        {
            ReliableSqlConnection reliableConn = dbConn as ReliableSqlConnection;
            if (reliableConn != null)
            {
                sqlConn = reliableConn.GetUnderlyingConnection();
            }
            else
            {
                sqlConn = dbConn as SqlConnection;
            }

            return sqlConn != null;
        }

        /// <summary>
        /// Cancel a connection that is in the process of opening.
        /// </summary>
        public bool CancelConnect(CancelConnectParams cancelParams)
        {
            // Validate parameters
            if (cancelParams == null || string.IsNullOrEmpty(cancelParams.OwnerUri))
            {
                return false;
            }

            CancelTokenKey cancelKey = new CancelTokenKey
            {
                OwnerUri = cancelParams.OwnerUri,
                Type = cancelParams.Type
            };

            // Cancel any current connection attempts for this URI
            CancellationTokenSource source;
            if (cancelTupleToCancellationTokenSourceMap.TryGetValue(cancelKey, out source))
            {
                try
                {
                    source.Cancel();
                    return true;
                }
                catch
                {
                    return false;
                }
            }

            return false;
        }

        /// <summary>
        /// Reassign the uri associated with a connection info with a new uri.
        /// </summary>
        public bool ReplaceUri(string originalOwnerUri, string newOwnerUri)
        {
            // Lookup the ConnectionInfo owned by the URI
            if(OwnerToConnectionMap.TryRemove(originalOwnerUri, out ConnectionInfo info))
            {
                info.OwnerUri = newOwnerUri;
                return OwnerToConnectionMap.TryAdd(newOwnerUri, info);
            }
            return false;
        }

        /// <summary>
        /// Close a connection with the specified connection details.
        /// </summary>
        public virtual bool Disconnect(DisconnectParams disconnectParams)
        {
            // Validate parameters
            if (disconnectParams == null || string.IsNullOrEmpty(disconnectParams.OwnerUri))
            {
                return false;
            }

            // Cancel if we are in the middle of connecting
            if (CancelConnections(disconnectParams.OwnerUri, disconnectParams.Type))
            {
                return false;
            }

            // Lookup the ConnectionInfo owned by the URI
            ConnectionInfo info;
            if (!OwnerToConnectionMap.TryGetValue(disconnectParams.OwnerUri, out info))
            {
                return false;
            }

            // This clears the uri of the connection from the tokenUpdateUris map, which is used to track
            // open editors that have requested a refreshed Microsoft Entra token.
            this.TokenUpdateUris.Remove(disconnectParams.OwnerUri, out bool result);

            // Call Close() on the connections we want to disconnect
            // If no connections were located, return false
            if (!CloseConnections(info, disconnectParams.Type))
            {
                return false;
            }

            // Remove the disconnected connections from the ConnectionInfo map
            if (disconnectParams.Type == null)
            {
                info.RemoveAllConnections();
            }
            else
            {
                info.RemoveConnection(disconnectParams.Type);
            }

            // If the ConnectionInfo has no more connections, remove the ConnectionInfo
            if (info.CountConnections == 0)
            {
                OwnerToConnectionMap.TryRemove(disconnectParams.OwnerUri, out _);
            }

            // Handle Telemetry disconnect events if we are disconnecting the default connection
            if (disconnectParams.Type == null || disconnectParams.Type == ConnectionType.Default)
            {
                HandleDisconnectTelemetry(info);
                InvokeOnDisconnectionActivities(info);
            }

            // Return true upon success
            return true;
        }

        /// <summary>
        /// Cancel connections associated with the given ownerUri.
        /// If connectionType is not null, cancel the connection with the given connectionType
        /// If connectionType is null, cancel all pending connections associated with ownerUri.
        /// </summary>
        /// <returns>true if a single pending connection associated with the non-null connectionType was
        /// found and cancelled, false otherwise</returns>
        private bool CancelConnections(string ownerUri, string connectionType)
        {
            // Cancel the connection of the given type
            if (connectionType != null)
            {
                // If we are trying to disconnect a specific connection and it was just cancelled,
                // this will return true
                return CancelConnect(new CancelConnectParams() { OwnerUri = ownerUri, Type = connectionType });
            }

            // Cancel all pending connections
            foreach (var entry in cancelTupleToCancellationTokenSourceMap)
            {
                string entryConnectionUri = entry.Key.OwnerUri;
                string entryConnectionType = entry.Key.Type;
                if (ownerUri.Equals(entryConnectionUri))
                {
                    CancelConnect(new CancelConnectParams() { OwnerUri = ownerUri, Type = entryConnectionType });
                }
            }

            return false;
        }

        /// <summary>
        /// Closes DbConnections associated with the given ConnectionInfo.
        /// If connectionType is not null, closes the DbConnection with the type given by connectionType.
        /// If connectionType is null, closes all DbConnections.
        /// </summary>
        /// <returns>true if connections were found and attempted to be closed,
        /// false if no connections were found</returns>
        private bool CloseConnections(ConnectionInfo connectionInfo, string connectionType)
        {
            ICollection<DbConnection> connectionsToDisconnect = new List<DbConnection>();
            if (connectionType == null)
            {
                connectionsToDisconnect = connectionInfo.AllConnections;
            }
            else
            {
                // Make sure there is an existing connection of this type
                DbConnection connection;
                if (!connectionInfo.TryGetConnection(connectionType, out connection))
                {
                    return false;
                }
                connectionsToDisconnect.Add(connection);
            }

            if (connectionsToDisconnect.Count == 0)
            {
                return false;
            }

            foreach (DbConnection connection in connectionsToDisconnect)
            {
                try
                {
                    connection.Close();
                }
                catch (Exception)
                {
                    // Ignore
                }
            }

            return true;
        }

        /// <summary>
        /// List all databases on the server specified
        /// </summary>
        public ListDatabasesResponse ListDatabases(ListDatabasesParams listDatabasesParams)
        {
            // Verify parameters
            var owner = listDatabasesParams.OwnerUri;
            if (string.IsNullOrEmpty(owner))
            {
                throw new ArgumentException(SR.ConnectionServiceListDbErrorNullOwnerUri);
            }

            // Use the existing connection as a base for the search
            ConnectionInfo info;
            if (!TryFindConnection(owner, out info))
            {
                throw new Exception(SR.ConnectionServiceListDbErrorNotConnected(owner));
            }
            var handler = ListDatabaseRequestHandlerFactory.getHandler(listDatabasesParams.IncludeDetails.HasTrue(), info.IsSqlDb);
            return handler.HandleRequest(this.connectionFactory, info);
        }

        public void InitializeService(IProtocolEndpoint serviceHost, ServiceLayerCommandOptions commandOptions)
        {
            this.ServiceHost = serviceHost;

            if (commandOptions != null)
            {
                ApplicationName = commandOptions.ApplicationName switch
                {
                    "azuredatastudio" => "azdata",
                    "code" => "vscode-mssql",
                    _ => "sqltools" // fallback
                };

                if (commandOptions.EnableSqlAuthenticationProvider)
                {
                    // Register SqlAuthenticationProvider with SqlConnection for Microsoft Entra Interactive (MFA) authentication.
                    var provider = new AuthenticationProvider(GetAuthenticator(commandOptions));
                    SqlAuthenticationProvider.SetProvider(SqlAuthenticationMethod.ActiveDirectoryInteractive, provider);

                    this.EnableSqlAuthenticationProvider = true;
                    Logger.Information("Registering implementation of SQL Authentication provider for 'Active Directory Interactive' authentication mode.");
                }
                if (commandOptions.EnableConnectionPooling)
                {
                    ConnectionService.EnableConnectionPooling = true;
                    Logger.Information("Connection pooling will be enabled for all SQL connections.");
                }
            }

            // Register request and event handlers with the Service Host
            serviceHost.SetRequestHandler(ConnectionRequest.Type, HandleConnectRequest, true);
            serviceHost.SetRequestHandler(CancelConnectRequest.Type, HandleCancelConnectRequest, true);
            serviceHost.SetRequestHandler(ChangePasswordRequest.Type, HandleChangePasswordRequest, true);
            serviceHost.SetRequestHandler(DisconnectRequest.Type, HandleDisconnectRequest, true);
            serviceHost.SetRequestHandler(ListDatabasesRequest.Type, HandleListDatabasesRequest, true);
            serviceHost.SetRequestHandler(ChangeDatabaseRequest.Type, HandleChangeDatabaseRequest, true);
            serviceHost.SetRequestHandler(GetConnectionStringRequest.Type, HandleGetConnectionStringRequest, true);
            serviceHost.SetRequestHandler(BuildConnectionInfoRequest.Type, HandleBuildConnectionInfoRequest, true);
            serviceHost.SetRequestHandler(ClearPooledConnectionsRequest.Type, HandleClearPooledConnectionsRequest, true);
            serviceHost.SetRequestHandler(ParseConnectionStringRequest.Type, HandleParseConnectionStringRequest, true);
            serviceHost.SetEventHandler(EncryptionKeysChangedNotification.Type, HandleEncryptionKeysNotificationEvent, false);
        }

        /// <summary>
        /// Add a new method to be called when the onconnection request is submitted
        /// </summary>
        /// <param name="activity"></param>
        public void RegisterOnConnectionTask(OnConnectionHandler activity)
        {
            onConnectionActivities.Add(activity);
        }

        /// <summary>
        /// Add a new method to be called when the ondisconnect request is submitted
        /// </summary>
        public void RegisterOnDisconnectTask(OnDisconnectHandler activity)
        {
            onDisconnectActivities.Add(activity);
        }

        /// <summary>
        /// Handle new connection requests
        /// </summary>
        /// <param name="connectParams"></param>
        /// <param name="requestContext"></param>
        /// <returns></returns>
        protected async Task HandleConnectRequest(
            ConnectParams connectParams,
            RequestContext<bool> requestContext)
        {
            Logger.Verbose("ConnectionRequest");

            try
            {
                RunConnectRequestHandlerTask(connectParams);
                await requestContext.SendResult(true);
            }
            catch (Exception ex)
            {
                Logger.Error($"ConnectionRequest failed with exception: {ex}");
                await requestContext.SendResult(false);
            }
        }

        private IAuthenticator GetAuthenticator(CommandOptions commandOptions)
        {
            var applicationName = commandOptions.ApplicationName;
            if (string.IsNullOrEmpty(applicationName))
            {
                applicationName = nameof(SqlTools);
                Logger.Warning($"Application Name not received with command options, using default application name as: {applicationName}");
            }

            var applicationPath = commandOptions.ApplicationPath;
            if (string.IsNullOrEmpty(applicationPath))
            {
                applicationPath = CommonUtils.BuildAppDirectoryPath();
                Logger.Warning($"Application Path not received with command options, using default application path as: {applicationPath}");
            }

            var cachePath = Path.Combine(applicationPath, applicationName, AzureTokenFolder);
            return new Authenticator(new(ApplicationClientId, applicationName, cachePath, MsalCacheName, commandOptions.HttpProxyUrl, commandOptions.HttpProxyStrictSSL), () => this.encryptionKeys);
        }

        private Task HandleEncryptionKeysNotificationEvent(EncryptionKeysChangeParams @params, EventContext context)
        {
            Logger.Verbose("EncryptionKeysNotificationEvent");
            this.encryptionKeys = (@params.Key, @params.Iv);
            return Task.FromResult(true);
        }

        private void RunConnectRequestHandlerTask(ConnectParams connectParams)
        {
            // create a task to connect asynchronously so that other requests are not blocked in the meantime
            Task.Run(async () =>
            {
                try
                {
                    // result is null if the ConnectParams was successfully validated
                    ConnectionCompleteParams result = ValidateConnectParams(connectParams);
                    if (result != null)
                    {
                        await ServiceHost.SendEvent(ConnectionCompleteNotification.Type, result);
                        return;
                    }

                    // open connection based on request details
                    result = await Connect(connectParams);
                    await ServiceHost.SendEvent(ConnectionCompleteNotification.Type, result);
                }
                catch (Exception ex)
                {
                    ConnectionCompleteParams result = new ConnectionCompleteParams()
                    {
                        Messages = ex.ToString()
                    };
                    await ServiceHost.SendEvent(ConnectionCompleteNotification.Type, result);
                }
            }).ContinueWithOnFaulted(null);
        }

        /// <summary>
        /// Handle new change password requests
        /// </summary>
        /// <param name="connectParams"></param>
        /// <param name="requestContext"></param>
        /// <returns></returns>
        protected async Task HandleChangePasswordRequest(
            ChangePasswordParams changePasswordParams,
            RequestContext<PasswordChangeResponse> requestContext)
        {
            Logger.Verbose("ChangePasswordRequest");
            PasswordChangeResponse newResponse = new PasswordChangeResponse();
            try
            {
                ChangePassword(changePasswordParams);
                newResponse.Result = true;
            }
            catch (Exception ex)
            {
                newResponse.Result = false;
                newResponse.ErrorMessage = ex.Message;
                int errorCode = 0;

                if ((ex.InnerException as SqlException) != null && (ex.InnerException as SqlException)?.Errors.Count != 0)
                {
                    SqlError endError = (ex.InnerException as SqlException).Errors[0];
                    newResponse.ErrorMessage = endError.Message;
                    errorCode = endError.Number;
                }

                if (errorCode == 0 && newResponse.ErrorMessage.Equals(SR.PasswordChangeEmptyPassword))
                {
                    newResponse.ErrorMessage += Environment.NewLine + Environment.NewLine + SR.PasswordChangeEmptyPasswordRetry;
                }
                else if (errorCode == DoesNotMeetPWReqs)
                {
                    newResponse.ErrorMessage += Environment.NewLine + Environment.NewLine + SR.PasswordChangeDNMReqsRetry;
                }
                else if (errorCode == PWCannotBeUsed)
                {
                    newResponse.ErrorMessage += Environment.NewLine + Environment.NewLine + SR.PasswordChangePWCannotBeUsedRetry;
                }
            }
            await requestContext.SendResult(newResponse);
        }

        public void ChangePassword(ChangePasswordParams changePasswordParams)
        {
            // Empty passwords are not valid.
            if (string.IsNullOrEmpty(changePasswordParams.NewPassword))
            {
                throw new Exception(SR.PasswordChangeEmptyPassword);
            }

            // result is null if the ConnectParams was successfully validated
            ConnectionCompleteParams result = ValidateConnectParams(changePasswordParams);
            if (result != null)
            {
                throw new Exception(result.ErrorMessage, new Exception(result.Messages));
            }

            // Change the password of the connection
            ServerConnection serverConnection = new ServerConnection();
            serverConnection.ConnectionString = ConnectionService.BuildConnectionString(changePasswordParams.Connection);
            serverConnection.ChangePassword(changePasswordParams.NewPassword);
        }

        /// <summary>
        /// Handle cancel connect requests
        /// </summary>
        protected async Task HandleCancelConnectRequest(
            CancelConnectParams cancelParams,
            RequestContext<bool> requestContext)
        {
            Logger.Verbose("CancelConnectRequest");
            bool result = CancelConnect(cancelParams);
            await requestContext.SendResult(result);
        }

        /// <summary>
        /// Handle disconnect requests
        /// </summary>
        protected async Task HandleDisconnectRequest(
            DisconnectParams disconnectParams,
            RequestContext<bool> requestContext)
        {
            Logger.Verbose("DisconnectRequest");
            bool result = Instance.Disconnect(disconnectParams);
            await requestContext.SendResult(result);

        }

        /// <summary>
        /// Handle requests to list databases on the current server
        /// </summary>
        protected Task HandleListDatabasesRequest(
            ListDatabasesParams listDatabasesParams,
            RequestContext<ListDatabasesResponse> requestContext)
        {
            Task.Run(async () =>
            {
                Logger.Verbose("ListDatabasesRequest");
                try
                {
                    ListDatabasesResponse result = ListDatabases(listDatabasesParams);
                    await requestContext.SendResult(result);
                }
                catch (Exception ex)
                {
                    await requestContext.SendError(ex.ToString());
                }
            });
            return Task.CompletedTask;
        }

        /// <summary>
        /// Checks if a ConnectionDetails object represents a DAC connection
        /// </summary>
        /// <param name="connectionDetails"></param>
        public static bool IsDedicatedAdminConnection(ConnectionDetails connectionDetails)
        {
            Validate.IsNotNull(nameof(connectionDetails), connectionDetails);
            SqlConnectionStringBuilder builder = CreateConnectionStringBuilder(connectionDetails);
            string serverName = builder.DataSource;
            return serverName != null && serverName.StartsWith(AdminConnectionPrefix, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Build a connection string from a connection details instance
        /// </summary>
        /// <param name="connectionDetails">Connection details</param>
        /// <param name="forceDisablePooling">Whether to disable connection pooling, defaults to true.</param>
        public static string BuildConnectionString(ConnectionDetails connectionDetails, bool forceDisablePooling = true)
        {
            return CreateConnectionStringBuilder(connectionDetails, forceDisablePooling).ToString();
        }

        /// <summary>
        /// Build a connection string builder a connection details instance
        /// </summary>
        /// <param name="connectionDetails">Connection details</param>
        /// <param name="forceDisablePooling">Whether to disable connection pooling, defaults to true.</param>
        public static SqlConnectionStringBuilder CreateConnectionStringBuilder(ConnectionDetails connectionDetails, bool forceDisablePooling = true)
        {
            SqlConnectionStringBuilder connectionBuilder;

            // If connectionDetails has a connection string already, use it to initialize the connection builder, then override any provided options.
            // Otherwise use the server name, username, and password from the connection details.
            if (!string.IsNullOrEmpty(connectionDetails.ConnectionString))
            {
                connectionBuilder = new SqlConnectionStringBuilder(connectionDetails.ConnectionString);
            }
            else
            {
                // add alternate port to data source property if provided
                string dataSource = !connectionDetails.Port.HasValue
                    ? connectionDetails.ServerName
                    : string.Format("{0},{1}", connectionDetails.ServerName, connectionDetails.Port.Value);

                connectionBuilder = new SqlConnectionStringBuilder
                {
                    DataSource = dataSource
                };
            }

            // Check for any optional parameters
            if (!string.IsNullOrEmpty(connectionDetails.DatabaseName))
            {
                connectionBuilder.InitialCatalog = connectionDetails.DatabaseName;
            }
            if (!string.IsNullOrEmpty(connectionDetails.AuthenticationType))
            {
                switch (connectionDetails.AuthenticationType)
                {
                    case Integrated:
                        connectionBuilder.IntegratedSecurity = true;
                        break;
                    case SqlLogin:
                        // Don't erase username from connection string.
                        if (string.IsNullOrEmpty(connectionBuilder.UserID))
                        {
                            connectionBuilder.UserID = connectionDetails.UserName;
                        }
                        // Don't erase password from connection string.
                        if (string.IsNullOrEmpty(connectionBuilder.Password))
                        {
                            connectionBuilder.Password = string.IsNullOrEmpty(connectionDetails.Password)
                            ? string.Empty // Support empty password for accounts without password
                            : connectionDetails.Password;
                        }
                        connectionBuilder.Authentication = SqlAuthenticationMethod.SqlPassword;
                        break;
                    case AzureMFA:
                        if (Instance.EnableSqlAuthenticationProvider)
                        {
                            if (string.IsNullOrEmpty(connectionBuilder.UserID))
                            {
                                connectionBuilder.UserID = connectionDetails.UserName;
                            }
                            connectionDetails.AuthenticationType = ActiveDirectoryInteractive;
                            connectionBuilder.Authentication = SqlAuthenticationMethod.ActiveDirectoryInteractive;
                        }
                        break;
                    case ActiveDirectoryInteractive:
                        // Don't erase username from connection string.
                        if (string.IsNullOrEmpty(connectionBuilder.UserID))
                        {
                            connectionBuilder.UserID = connectionDetails.UserName;
                        }
                        connectionBuilder.Authentication = SqlAuthenticationMethod.ActiveDirectoryInteractive;
                        break;
                    case ActiveDirectoryPassword:
                        // Don't erase username from connection string.
                        if (string.IsNullOrEmpty(connectionBuilder.UserID))
                        {
                            connectionBuilder.UserID = connectionDetails.UserName;
                        }
                        // Don't erase password from connection string.
                        if (string.IsNullOrEmpty(connectionBuilder.Password))
                        {
                            connectionBuilder.Password = connectionDetails.Password;
                        }
                        connectionBuilder.Authentication = SqlAuthenticationMethod.ActiveDirectoryPassword;
                        break;
                    default:
                        throw new ArgumentException(SR.ConnectionServiceConnStringInvalidAuthType(connectionDetails.AuthenticationType));
                }
            }
            if (!string.IsNullOrEmpty(connectionDetails.ColumnEncryptionSetting))
            {
                if (Enum.TryParse<SqlConnectionColumnEncryptionSetting>(connectionDetails.ColumnEncryptionSetting, true, out var value))
                {
                    connectionBuilder.ColumnEncryptionSetting = value;
                }
                else
                {
                    throw new ArgumentException(SR.ConnectionServiceConnStringInvalidColumnEncryptionSetting(connectionDetails.ColumnEncryptionSetting));
                }
            }
            if (!string.IsNullOrEmpty(connectionDetails.SecureEnclaves))
            {
                // Secure Enclaves is not mapped to SqlConnection, it's only used for throwing validation errors
                // when Enclave Attestation Protocol is missing.
                switch (connectionDetails.SecureEnclaves.ToUpper(CultureInfo.InvariantCulture))
                {
                    case "ENABLED":
                        if (string.IsNullOrEmpty(connectionDetails.EnclaveAttestationProtocol))
                        {
                            throw new ArgumentException(SR.ConnectionServiceConnStringMissingAttestationProtocolWithSecureEnclaves);
                        }
                        break;
                    case "DISABLED":
                        break;
                    default:
                        throw new ArgumentException(SR.ConnectionServiceConnStringInvalidSecureEnclaves(connectionDetails.SecureEnclaves));
                }
            }
            if (!string.IsNullOrEmpty(connectionDetails.EnclaveAttestationProtocol))
            {
                if (connectionBuilder.ColumnEncryptionSetting != SqlConnectionColumnEncryptionSetting.Enabled
                    || string.IsNullOrEmpty(connectionDetails.SecureEnclaves) || connectionDetails.SecureEnclaves.ToUpper(CultureInfo.InvariantCulture) == "DISABLED")
                {
                    throw new ArgumentException(SR.ConnectionServiceConnStringInvalidAlwaysEncryptedOptionCombination);
                }

                if (Enum.TryParse<SqlConnectionAttestationProtocol>(connectionDetails.EnclaveAttestationProtocol, true, out var value))
                {
                    connectionBuilder.AttestationProtocol = value;
                }
                else
                {
                    throw new ArgumentException(SR.ConnectionServiceConnStringInvalidEnclaveAttestationProtocol(connectionDetails.EnclaveAttestationProtocol));
                }
            }
            if (!string.IsNullOrEmpty(connectionDetails.EnclaveAttestationUrl))
            {
                if (connectionBuilder.ColumnEncryptionSetting != SqlConnectionColumnEncryptionSetting.Enabled
                    || string.IsNullOrEmpty(connectionDetails.SecureEnclaves) || connectionDetails.SecureEnclaves.ToUpper(CultureInfo.InvariantCulture) == "DISABLED")
                {
                    throw new ArgumentException(SR.ConnectionServiceConnStringInvalidAlwaysEncryptedOptionCombination);
                }

                if (connectionBuilder.AttestationProtocol == SqlConnectionAttestationProtocol.None)
                {
                    throw new ArgumentException(SR.ConnectionServiceConnStringInvalidAttestationProtocolNoneWithUrl);
                }

                connectionBuilder.EnclaveAttestationUrl = connectionDetails.EnclaveAttestationUrl;
            }
            else if (connectionBuilder.AttestationProtocol == SqlConnectionAttestationProtocol.AAS
                || connectionBuilder.AttestationProtocol == SqlConnectionAttestationProtocol.HGS)
            {
                throw new ArgumentException(SR.ConnectionServiceConnStringMissingAttestationUrlWithAttestationProtocol);
            }

            if (!string.IsNullOrEmpty(connectionDetails.Encrypt))
            {
                connectionBuilder.Encrypt = connectionDetails.Encrypt.ToLowerInvariant() switch
                {
                    "optional" or "false" or "no" => SqlConnectionEncryptOption.Optional,
                    "mandatory" or "true" or "yes" => SqlConnectionEncryptOption.Mandatory,
                    "strict" => SqlConnectionEncryptOption.Strict,
                    _ => throw new ArgumentException(SR.ConnectionServiceConnStringInvalidEncryptOption(connectionDetails.Encrypt))
                };
            }

            if (connectionDetails.TrustServerCertificate.HasValue)
            {
                connectionBuilder.TrustServerCertificate = connectionDetails.TrustServerCertificate.Value;
            }
            if (!string.IsNullOrEmpty(connectionDetails.HostNameInCertificate))
            {
                connectionBuilder.HostNameInCertificate = connectionDetails.HostNameInCertificate;
            }
            if (connectionDetails.PersistSecurityInfo.HasValue)
            {
                connectionBuilder.PersistSecurityInfo = connectionDetails.PersistSecurityInfo.Value;
            }
            if (connectionDetails.ConnectTimeout.HasValue)
            {
                connectionBuilder.ConnectTimeout = connectionDetails.ConnectTimeout.Value;
            }
            if (connectionDetails.CommandTimeout.HasValue)
            {
                connectionBuilder.CommandTimeout = connectionDetails.CommandTimeout.Value;
            }
            if (connectionDetails.ConnectRetryCount.HasValue)
            {
                connectionBuilder.ConnectRetryCount = connectionDetails.ConnectRetryCount.Value;
            }
            if (connectionDetails.ConnectRetryInterval.HasValue)
            {
                connectionBuilder.ConnectRetryInterval = connectionDetails.ConnectRetryInterval.Value;
            }
            if (!string.IsNullOrEmpty(connectionDetails.ApplicationName))
            {
                connectionBuilder.ApplicationName = connectionDetails.ApplicationName;
            }
            if (!string.IsNullOrEmpty(connectionDetails.WorkstationId))
            {
                connectionBuilder.WorkstationID = connectionDetails.WorkstationId;
            }
            if (!string.IsNullOrEmpty(connectionDetails.ApplicationIntent))
            {
                if (Enum.TryParse<ApplicationIntent>(connectionDetails.ApplicationIntent, true, out ApplicationIntent value))
                {
                    connectionBuilder.ApplicationIntent = value;
                }
                else
                {
                    throw new ArgumentException(SR.ConnectionServiceConnStringInvalidIntent(connectionDetails.ApplicationIntent));
                }
            }
            if (!string.IsNullOrEmpty(connectionDetails.CurrentLanguage))
            {
                connectionBuilder.CurrentLanguage = connectionDetails.CurrentLanguage;
            }
            if (connectionDetails.Pooling.HasValue)
            {
                connectionBuilder.Pooling = connectionDetails.Pooling.Value;
            }
            if (connectionDetails.MaxPoolSize.HasValue)
            {
                connectionBuilder.MaxPoolSize = connectionDetails.MaxPoolSize.Value;
            }
            if (connectionDetails.MinPoolSize.HasValue)
            {
                connectionBuilder.MinPoolSize = connectionDetails.MinPoolSize.Value;
            }
            if (connectionDetails.LoadBalanceTimeout.HasValue)
            {
                connectionBuilder.LoadBalanceTimeout = connectionDetails.LoadBalanceTimeout.Value;
            }
            if (connectionDetails.Replication.HasValue)
            {
                connectionBuilder.Replication = connectionDetails.Replication.Value;
            }
            if (!string.IsNullOrEmpty(connectionDetails.AttachDbFilename))
            {
                connectionBuilder.AttachDBFilename = connectionDetails.AttachDbFilename;
            }
            if (!string.IsNullOrEmpty(connectionDetails.FailoverPartner))
            {
                connectionBuilder.FailoverPartner = connectionDetails.FailoverPartner;
            }
            if (connectionDetails.MultiSubnetFailover.HasValue)
            {
                connectionBuilder.MultiSubnetFailover = connectionDetails.MultiSubnetFailover.Value;
            }
            if (connectionDetails.MultipleActiveResultSets.HasValue)
            {
                connectionBuilder.MultipleActiveResultSets = connectionDetails.MultipleActiveResultSets.Value;
            }
            if (connectionDetails.PacketSize.HasValue)
            {
                connectionBuilder.PacketSize = connectionDetails.PacketSize.Value;
            }
            if (!string.IsNullOrEmpty(connectionDetails.TypeSystemVersion))
            {
                connectionBuilder.TypeSystemVersion = connectionDetails.TypeSystemVersion;
            }
            if (!EnableConnectionPooling && forceDisablePooling)
            {
                connectionBuilder.Pooling = false;
            }

            return connectionBuilder;
        }

        /// <summary>
        /// Handles a request to get a connection string for the provided connection
        /// </summary>
        public async Task HandleGetConnectionStringRequest(
            GetConnectionStringParams connStringParams,
            RequestContext<string> requestContext)
        {
            Logger.Verbose("GetConnectionStringRequest");
            string connectionString = string.Empty;
            ConnectionInfo info;
            SqlConnectionStringBuilder connStringBuilder;
            // set connection string using connection uri if connection details are undefined
            if (connStringParams.ConnectionDetails == null && TryFindConnection(connStringParams.OwnerUri, out info))
            {
                connStringBuilder = CreateConnectionStringBuilder(info.ConnectionDetails);
            }
            // set connection string using connection details
            else
            {
                connStringBuilder = CreateConnectionStringBuilder(connStringParams.ConnectionDetails as ConnectionDetails);
            }
            if (!connStringParams.IncludePassword)
            {
                connStringBuilder.Password = ConnectionService.PasswordPlaceholder;
            }
            // default connection string application name to always be included unless set to false
            if (connStringBuilder.ApplicationName == null && (!connStringParams.IncludeApplicationName.HasValue || connStringParams.IncludeApplicationName.Value == true))
            {
                connStringBuilder.ApplicationName = ApplicationName;
            }
            connectionString = connStringBuilder.ConnectionString;

            await requestContext.SendResult(connectionString);
        }

        /// <summary>
        /// Handles a request to serialize a connection string.  If parsing fails, returns null.
        /// </summary>
        public async Task HandleBuildConnectionInfoRequest(
            string connectionString,
            RequestContext<ConnectionDetails> requestContext)
        {
            Logger.Verbose("BuildConnectionInfoRequest");
            try
            {
                await requestContext.SendResult(ParseConnectionString(connectionString));
            }
            catch (Exception ex)
            {
                // If theres an error in the parse, it means we just can't parse, so we return undefined
                // rather than an error.
                Logger.Error($"BuildConnectionInfoRequest failed with exception: {ex}");
                await requestContext.SendResult(null);
            }
        }

        // DEVNOTE: HandleBuildConnectionInfoRequest is locked down in Azure Data Studio's core,
        // so instead adding ParseConnectionStringRequest that returns the error message instead of just null.

        /// <summary>
        /// Handles a request to parse a connection string into a ConnectionDetails object.
        /// If parsing fails, sends an error.
        /// </summary>
        /// <param name="connectionString"></param>
        /// <param name="requestContext"></param>
        /// <returns></returns>
        public async Task HandleParseConnectionStringRequest(
            string connectionString,
            RequestContext<ConnectionDetails> requestContext)
        {
            Logger.Verbose("ParseConnectionStringRequest");
            try
            {
                await requestContext.SendResult(ParseConnectionString(connectionString));
            }
            catch (Exception ex)
            {
                Logger.Error($"ParseConnectionStringRequest failed with exception: {ex}");
                await requestContext.SendError(ex.Message);
            }
        }

        /// <summary>
        /// Clears all pooled connections from SqlConnection pool, releasing open connection sockets not actively in use.
        /// </summary>
        /// <param name="_">Request param</param>
        /// <param name="requestContext">Request Context</param>
        /// <returns></returns>
        public async Task HandleClearPooledConnectionsRequest(object _, RequestContext<bool> requestContext)
        {
            Logger.Verbose("ClearPooledConnectionsRequest");
            // Run a detached task to clear pools in backend.
            await Task.Factory.StartNew(() => Task.Run(async () => {

                SqlConnection.ClearAllPools();

                Logger.Verbose("Cleared all pooled connections successfully.");
                await requestContext.SendResult(true);
            }));
        }

        public ConnectionDetails ParseConnectionString(string connectionString)
        {
            SqlConnectionStringBuilder builder = new SqlConnectionStringBuilder(connectionString);

            // Set defaults as per MSSQL connection property defaults, not SqlClient's Connection string buider defaults
            ConnectionDetails details = new ConnectionDetails()
            {
                ApplicationIntent = defaultBuilder.ApplicationIntent != builder.ApplicationIntent ? builder.ApplicationIntent.ToString() : null,
                ApplicationName = defaultBuilder.ApplicationName != builder.ApplicationName ? builder.ApplicationName : ApplicationName,
                AttachDbFilename = defaultBuilder.AttachDBFilename != builder.AttachDBFilename ? builder.AttachDBFilename.ToString() : null,
                AuthenticationType = builder.IntegratedSecurity ? "Integrated" :
                    ((builder.Authentication == SqlAuthenticationMethod.SqlPassword || builder.Authentication == SqlAuthenticationMethod.NotSpecified)
                    ? "SqlLogin" : "AzureMFA"),
                ConnectRetryCount = defaultBuilder.ConnectRetryCount != builder.ConnectRetryCount ? builder.ConnectRetryCount : 1,
                ConnectRetryInterval = defaultBuilder.ConnectRetryInterval != builder.ConnectRetryInterval ? builder.ConnectRetryInterval : 10,
                ConnectTimeout = defaultBuilder.ConnectTimeout != builder.ConnectTimeout ? builder.ConnectTimeout : 30,
                CommandTimeout = defaultBuilder.CommandTimeout != builder.CommandTimeout ? builder.CommandTimeout : 30,
                CurrentLanguage = defaultBuilder.CurrentLanguage != builder.CurrentLanguage ? builder.CurrentLanguage : null,
                DatabaseName = defaultBuilder.InitialCatalog != builder.InitialCatalog ? builder.InitialCatalog : null,
                ColumnEncryptionSetting = defaultBuilder.ColumnEncryptionSetting != builder.ColumnEncryptionSetting ? builder.ColumnEncryptionSetting.ToString() : null,
                EnclaveAttestationProtocol = defaultBuilder.AttestationProtocol != builder.AttestationProtocol ? builder.AttestationProtocol.ToString() : null,
                EnclaveAttestationUrl = defaultBuilder.EnclaveAttestationUrl != builder.EnclaveAttestationUrl ? builder.EnclaveAttestationUrl : null,
                Encrypt = defaultBuilder.Encrypt != builder.Encrypt ? builder.Encrypt.ToString() : Boolean.TrueString.ToLower(CultureInfo.InvariantCulture),
                FailoverPartner = defaultBuilder.FailoverPartner != builder.FailoverPartner ? builder.FailoverPartner : null,
                HostNameInCertificate = defaultBuilder.HostNameInCertificate != builder.HostNameInCertificate ? builder.HostNameInCertificate : null,
                LoadBalanceTimeout = defaultBuilder.LoadBalanceTimeout != builder.LoadBalanceTimeout ? builder.LoadBalanceTimeout : null,
                MaxPoolSize = defaultBuilder.MaxPoolSize != builder.MaxPoolSize ? builder.MaxPoolSize : null,
                MinPoolSize = defaultBuilder.MinPoolSize != builder.MinPoolSize ? builder.MinPoolSize : null,
                MultipleActiveResultSets = defaultBuilder.MultipleActiveResultSets != builder.MultipleActiveResultSets ? builder.MultipleActiveResultSets : null,
                MultiSubnetFailover = defaultBuilder.MultiSubnetFailover != builder.MultiSubnetFailover ? builder.MultiSubnetFailover : null,
                PacketSize = defaultBuilder.PacketSize != builder.PacketSize ? builder.PacketSize : null,
                Password = !builder.IntegratedSecurity ? builder.Password : null,
                PersistSecurityInfo = defaultBuilder.PersistSecurityInfo != builder.PersistSecurityInfo ? builder.PersistSecurityInfo : null,
                Pooling = defaultBuilder.Pooling != builder.Pooling ? builder.Pooling : null,
                Replication = defaultBuilder.Replication != builder.Replication ? builder.Replication : null,
                ServerName = defaultBuilder.DataSource != builder.DataSource ? builder.DataSource : null,
                TrustServerCertificate = defaultBuilder.TrustServerCertificate != builder.TrustServerCertificate ? builder.TrustServerCertificate : false,
                TypeSystemVersion = defaultBuilder.TypeSystemVersion != builder.TypeSystemVersion ? builder.TypeSystemVersion : null,
                // !!! ALERT - DO NOT CHANGE USER !!!
                // SSMS 19 treats "user" as mandatory, always set it to value from connection string builder, even if it's an empty string.
                UserName = builder.UserID,
                WorkstationId = defaultBuilder.WorkstationID != builder.WorkstationID ? builder.WorkstationID : null
            };

            return details;
        }

        /// <summary>
        /// Handles a request to change the database for a connection
        /// </summary>
        public async Task HandleChangeDatabaseRequest(
            ChangeDatabaseParams changeDatabaseParams,
            RequestContext<bool> requestContext)
        {
            Logger.Verbose("ChangeDatabaseRequest");
            await requestContext.SendResult(ChangeConnectionDatabaseContext(changeDatabaseParams.OwnerUri, changeDatabaseParams.NewDatabase, true));
        }

        /// <summary>
        /// Change the database context of a connection.
        /// </summary>
        /// <param name="ownerUri">URI of the owner of the connection</param>
        /// <param name="newDatabaseName">Name of the database to change the connection to</param>
        public bool ChangeConnectionDatabaseContext(string ownerUri, string newDatabaseName, bool force = false)
        {
            ConnectionInfo info;
            if (TryFindConnection(ownerUri, out info))
            {
                try
                {
                    info.ConnectionDetails.DatabaseName = newDatabaseName;

                    foreach (string key in info.AllConnectionTypes)
                    {
                        DbConnection conn;
                        info.TryGetConnection(key, out conn);
                        if (conn != null && conn.Database != newDatabaseName && conn.State == ConnectionState.Open)
                        {
                            if (info.IsCloud && force)
                            {
                                conn.Close();
                                conn.Dispose();
                                info.RemoveConnection(key);

                                string connectionString = BuildConnectionString(info.ConnectionDetails);

                                // create a sql connection instance
                                DbConnection connection = info.Factory.CreateSqlConnection(connectionString, info.ConnectionDetails.AzureAccountToken);
                                connection.Open();
                                info.AddConnection(key, connection);
                            }
                            else
                            {
                                conn.ChangeDatabase(newDatabaseName);
                            }
                        }

                    }

                    // Fire a connection changed event
                    ConnectionChangedParams parameters = new ConnectionChangedParams();
                    IConnectionSummary summary = info.ConnectionDetails;
                    parameters.Connection = summary.Clone();
                    parameters.OwnerUri = ownerUri;
                    ServiceHost.SendEvent(ConnectionChangedNotification.Type, parameters);
                    return true;
                }
                catch (Exception e)
                {
                    Logger.Error(
                        string.Format(
                            "Exception caught while trying to change database context to [{0}] for OwnerUri [{1}]. Exception:{2}",
                            newDatabaseName,
                            ownerUri,
                            e.ToString())
                    );
                }
            }
            return false;
        }

        /// <summary>
        /// Invokes the initial on-connect activities if the provided ConnectParams represents the default
        /// connection.
        /// </summary>
        private void InvokeOnConnectionActivities(ConnectionInfo connectionInfo, ConnectParams connectParams)
        {
            if (connectParams.Type != ConnectionType.Default && connectParams.Type != ConnectionType.GeneralConnection)
            {
                return;
            }

            foreach (var activity in this.onConnectionActivities)
            {
                // not awaiting here to allow handlers to run in the background
                activity(connectionInfo);
            }
        }

        /// <summary>
        /// Invokes the final on-disconnect activities if the provided DisconnectParams represents the default
        /// connection or is null - representing that all connections are being disconnected.
        /// </summary>
        private void InvokeOnDisconnectionActivities(ConnectionInfo connectionInfo)
        {
            foreach (var activity in this.onDisconnectActivities)
            {
                activity(connectionInfo.ConnectionDetails, connectionInfo.OwnerUri);
            }
        }

        /// <summary>
        /// Handles the Telemetry events that occur upon disconnect.
        /// </summary>
        /// <param name="info"></param>
        private void HandleDisconnectTelemetry(ConnectionInfo connectionInfo)
        {
            if (ServiceHost != null)
            {
                try
                {
                    // Send a telemetry notification for intellisense performance metrics
                    ServiceHost.SendEvent(TelemetryNotification.Type, new TelemetryParams()
                    {
                        Params = new TelemetryProperties
                        {
                            Properties = new Dictionary<string, string>
                            {
                                { TelemetryPropertyNames.IsAzure, connectionInfo.IsCloud.ToOneOrZeroString() }
                            },
                            EventName = TelemetryEventNames.IntellisenseQuantile,
                            Measures = connectionInfo.IntellisenseMetrics.Quantile
                        }
                    });
                }
                catch (Exception ex)
                {
                    Logger.Verbose("Could not send Connection telemetry event " + ex.ToString());
                }
            }
        }

        /// <summary>
        /// Create and open a new SqlConnection from a ConnectionInfo object
        /// Note: we need to audit all uses of this method to determine why we're
        /// bypassing normal ConnectionService connection management
        /// </summary>
        /// <param name="connInfo">The connection info to connect with</param>
        /// <param name="featureName">A plaintext string that will be included in the application name for the connection</param>
        /// <returns>A SqlConnection created with the given connection info</returns>
        /// <exception cref="Exception">When an error occurs.</exception>
        public static SqlConnection OpenSqlConnection(ConnectionInfo connInfo, string featureName = null)
        {
            try
            {
                // capture original values
                bool? originalPersistSecurityInfo = connInfo.ConnectionDetails.PersistSecurityInfo;
                bool? originalPooling = connInfo.ConnectionDetails.Pooling;

                // allow pooling connections for language service feature to improve intellisense connection retention and performance.
                bool shouldForceDisablePooling = !EnableConnectionPooling && featureName != Constants.LanguageServiceFeature;

                // enable PersistSecurityInfo to handle issues in SMO where the connection context is lost in reconnections
                connInfo.ConnectionDetails.PersistSecurityInfo = true;

                // turn off connection pool to avoid hold locks on server resources after calling SqlConnection Close method
                if (shouldForceDisablePooling)
                {
                    connInfo.ConnectionDetails.Pooling = false;
                }

                // increase the connection and command timeout to at least 30 seconds and set application name.
                connInfo.ConnectionDetails = FillInDefaultDetailsForConnections(connInfo.ConnectionDetails, featureName);

                // generate connection string
                string connectionString = ConnectionService.BuildConnectionString(connInfo.ConnectionDetails, shouldForceDisablePooling);

                // restore original values
                connInfo.ConnectionDetails.PersistSecurityInfo = originalPersistSecurityInfo;
                connInfo.ConnectionDetails.Pooling = originalPooling;

                // open a dedicated binding server connection
                SqlConnection sqlConn = new SqlConnection(connectionString);
                sqlConn.RetryLogicProvider = SqlRetryProviders.ServerlessDBRetryProvider();

                // Fill in Microsoft Entra authentication token if needed
                if (connInfo.ConnectionDetails.AzureAccountToken != null && connInfo.ConnectionDetails.AuthenticationType == AzureMFA)
                {
                    sqlConn.AccessToken = connInfo.ConnectionDetails.AzureAccountToken;
                }

                sqlConn.Open();
                return sqlConn;
            }
            catch (Exception ex)
            {
                string error = string.Format(CultureInfo.InvariantCulture,
                    "Failed opening a SqlConnection: error:{0} inner:{1} stacktrace:{2}",
                    ex.Message, ex.InnerException != null ? ex.InnerException.Message : string.Empty, ex.StackTrace);
                Logger.Error(error);
                throw;
            }
        }

        /// <summary>
        /// Create and open a new ServerConnection from a ConnectionInfo object.
        /// This calls ConnectionService.OpenSqlConnection and then creates a
        /// ServerConnection from it.
        /// </summary>
        /// <param name="connInfo">The connection info to connect with</param>
        /// <param name="featureName">A plaintext string that will be included in the application name for the connection</param>
        /// <returns>A ServerConnection (wrapping a SqlConnection) created with the given connection info</returns>
        internal static ServerConnection OpenServerConnection(ConnectionInfo connInfo, string featureName = null)
        {
            var sqlConnection = ConnectionService.OpenSqlConnection(connInfo, featureName);
            ServerConnection serverConnection;
            if (connInfo.ConnectionDetails.AzureAccountToken != null && connInfo.ConnectionDetails.AuthenticationType == AzureMFA)
            {
                serverConnection = new ServerConnection(sqlConnection, new AzureAccessToken(connInfo.ConnectionDetails.AzureAccountToken));
            }
            else
            {
                serverConnection = new ServerConnection(sqlConnection);
            }

            return serverConnection;
        }

        public static void EnsureConnectionIsOpen(DbConnection conn, bool forceReopen = false)
        {
            // verify that the connection is open
            if (conn.State != ConnectionState.Open || forceReopen)
            {
                try
                {
                    // close it in case it's in some non-Closed state
                    conn.Close();
                }
                catch
                {
                    // ignore any exceptions thrown from .Close
                    // if the connection is really broken the .Open call will throw
                }
                finally
                {
                    // try to reopen the connection
                    conn.Open();
                }
            }
        }

        public static bool IsDbPool(string databaseName)
        {
            return databaseName != null ? databaseName.IndexOf('@') != -1 : false;
        }
    }
}
