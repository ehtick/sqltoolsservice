﻿//
// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
//

#nullable disable

using System;
using System.Collections.Generic;
using Microsoft.Data.SqlClient;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.SqlTools.Hosting.Protocol;
using Microsoft.SqlTools.ServiceLayer.Connection;
using Microsoft.SqlTools.ServiceLayer.Connection.Contracts;
using Microsoft.SqlTools.ServiceLayer.ObjectExplorer;
using Microsoft.SqlTools.ServiceLayer.ObjectExplorer.Contracts;
using Microsoft.SqlTools.SqlCore.ObjectExplorer.Nodes;
using Microsoft.SqlTools.ServiceLayer.UnitTests.Utility;
using Moq;
using Moq.Protected;
using NUnit.Framework;
using Microsoft.SqlTools.ServiceLayer.LanguageServices;
using Microsoft.SqlServer.Management.Common;
using Microsoft.SqlTools.ServiceLayer.Test.Common.RequestContextMocking;
using Microsoft.SqlTools.Utility;

namespace Microsoft.SqlTools.ServiceLayer.UnitTests.ObjectExplorer
{

    public class ObjectExplorerServiceTests : ObjectExplorerTestBase
    {
        private ObjectExplorerService service;
        private Mock<ConnectionService> connectionServiceMock;
        private Mock<IProtocolEndpoint> serviceHostMock;
        string fakeConnectionString = "Data Source=server;Initial Catalog=database;Integrated Security=False;User Id=user";
        private static ConnectionDetails details = new ConnectionDetails()
        {
            UserName = "user",
            Password = "password",
            DatabaseName = "msdb",
            ServerName = "serverName"
        };
        ConnectionInfo connectionInfo = new ConnectionInfo(null, null, details);

        ConnectedBindingQueue connectedBindingQueue;
        Mock<SqlConnectionOpener> mockConnectionOpener;

        [SetUp]
        public void InitObjectExplorerServiceTests()
        {
            connectionServiceMock = new Mock<ConnectionService>();
            serviceHostMock = new Mock<IProtocolEndpoint>();
            service = CreateOEService(connectionServiceMock.Object);
            connectionServiceMock.Setup(x => x.RegisterConnectedQueue(It.IsAny<string>(), It.IsAny<IConnectedBindingQueue>()));
            service.InitializeService(serviceHostMock.Object);
            ConnectedBindingContext connectedBindingContext = new ConnectedBindingContext();
            connectedBindingContext.ServerConnection = new ServerConnection(new SqlConnection(fakeConnectionString));
            connectedBindingQueue = new ConnectedBindingQueue(false);
            connectedBindingQueue.BindingContextMap.TryAdd($"{details.ServerName}_{details.DatabaseName}_{details.UserName}_NULL_persistSecurityInfo:true", connectedBindingContext);
            connectedBindingQueue.BindingContextTasks.TryAdd(connectedBindingContext, Task.Run(() => null));
            mockConnectionOpener = new Mock<SqlConnectionOpener>();
            connectedBindingQueue.SetConnectionOpener(mockConnectionOpener.Object);
            service.ConnectedBindingQueue = connectedBindingQueue;
        }

        [Test]
        public async Task CreateSessionRequestErrorsIfConnectionDetailsIsNull()
        {
            object errorResponse = null;
            var contextMock = RequestContextMocks.Create<CreateSessionResponse>(null)
                                                 .AddErrorHandling((errorMessage, errorCode, data) => errorResponse = errorMessage);

            await service.HandleCreateSessionRequest(null, contextMock.Object);
            VerifyErrorSent(contextMock);
            Assert.True(((string)errorResponse).Contains("ArgumentNullException"));
        }

        [Test]
        public async Task CreateSessionRequestReturnsFalseOnConnectionFailure()
        {
            // Given the connection service fails to connect
            ConnectionDetails details = TestObjects.GetTestConnectionDetails();

            string expectedExceptionText = "Error!!!";
            connectionServiceMock.Setup(c => c.Connect(It.IsAny<ConnectParams>()))
                .Throws(new Exception(expectedExceptionText));

            // when creating a new session
            // then expect the create session request to return false
            await RunAndVerify<CreateSessionResponse, SessionCreatedParameters>(
                test: (requestContext) => CallCreateSession(details, requestContext),
                verify: (actual =>
                {
                    Assert.NotNull(actual.SessionId);
                    Assert.NotNull(actual);
                    Assert.True(actual.ErrorMessage.Contains(expectedExceptionText));
                }));

            // And expect error notification to be sent
            serviceHostMock.Verify(x => x.SendEvent(CreateSessionCompleteNotification.Type, It.IsAny<SessionCreatedParameters>()), Times.Once());
        }


        [Test]
        public async Task CreateSessionRequestWithMasterConnectionReturnsServerSuccessAndNodeInfo()
        {
            // Given the connection service fails to connect
            ConnectionDetails details = new ConnectionDetails()
            {
                UserName = "user",
                Password = "password",
                DatabaseName = "master",
                ServerName = "serverName"
            };
            await CreateSessionRequestAndVerifyServerNodeHelper(details);
        }

        [Test]
        public async Task CreateSessionRequestWithEmptyConnectionReturnsServerSuccessAndNodeInfo()
        {
            // Given the connection service fails to connect
            ConnectionDetails details = new ConnectionDetails()
            {
                UserName = "user",
                Password = "password",
                DatabaseName = "",
                ServerName = "serverName"
            };
            await CreateSessionRequestAndVerifyServerNodeHelper(details);
        }

        [Test]
        public async Task CreateSessionRequestWithMsdbConnectionReturnsServerSuccessAndNodeInfo()
        {
            // Given the connection service fails to connect
            ConnectionDetails details = new ConnectionDetails()
            {
                UserName = "user",
                Password = "password",
                DatabaseName = "msdb",
                ServerName = "serverName"
            };
            await CreateSessionRequestAndVerifyServerNodeHelper(details);
        }

        [Test]
        public async Task CreateSessionRequestWithDefaultConnectionReturnsServerSuccessAndNodeInfo()
        {
            // Given the connection service fails to connect
            ConnectionDetails details = new ConnectionDetails()
            {
                UserName = "user",
                Password = "password",
                DatabaseName = "testdb",
                ServerName = "serverName",
                DatabaseDisplayName = ""
            };
            await CreateSessionRequestAndVerifyServerNodeHelper(details);
        }

        [Test]
        public async Task ExpandNodeGivenValidSessionShouldReturnTheNodeChildren()
        {
            await ExpandAndVerifyServerNodes();
        }

        [Test]
        public async Task RefreshNodeGivenValidSessionShouldReturnTheNodeChildren()
        {
            await RefreshAndVerifyServerNodes();
        }

        [Test]
        public async Task ExpandNodeGivenInvalidSessionShouldReturnEmptyList()
        {
            ExpandParams expandParams = new ExpandParams()
            {
                SessionId = "invalid session is",
                NodePath = "Any path"
            };


            // when expanding
            // then expect the nodes are server children 
            await RunAndVerify<bool, ExpandResponse>(
                test: (requestContext) => CallServiceExpand(expandParams, requestContext),
                verify: (actual =>
                {
                    Assert.AreEqual(actual.SessionId, expandParams.SessionId);
                    Assert.Null(actual.Nodes);
                }));
        }

        [Test]
        public async Task RefreshNodeGivenInvalidSessionShouldReturnEmptyList()
        {
            RefreshParams expandParams = new RefreshParams()
            {
                SessionId = "invalid session is",
                NodePath = "Any path"
            };

            // when expanding
            // then expect the nodes are server children 
            await RunAndVerify<bool, ExpandResponse>(
                test: (requestContext) => CallServiceRefresh(expandParams, requestContext),
                verify: (actual =>
                {
                    Assert.AreEqual(actual.SessionId, expandParams.SessionId);
                    Assert.Null(actual.Nodes);
                }));
        }

        [Test]
        public async Task RefreshNodeGivenNullSessionShouldReturnEmptyList()
        {
            RefreshParams expandParams = new RefreshParams()
            {
                SessionId = null,
                NodePath = "Any path"
            };

            // when expanding
            // then expect the nodes are server children 
            await RunAndVerify<bool, ExpandResponse>(
                test: (requestContext) => CallServiceRefresh(expandParams, requestContext),
                verify: (actual =>
                {
                    Assert.AreEqual(actual.SessionId, expandParams.SessionId);
                    Assert.Null(actual.Nodes);
                }));
        }

        [Test]
        public async Task CloseSessionGivenInvalidSessionShouldReturnEmptyList()
        {
            CloseSessionParams closeSessionParamsparams = new CloseSessionParams()
            {
                SessionId = "invalid session is",
            };

            // when expanding
            // then expect the nodes are server children 
            await RunAndVerify<CloseSessionResponse, CloseSessionResponse>(
                test: (requestContext) => CallCloseSession(closeSessionParamsparams, requestContext),
                verify: (actual =>
                {
                    Assert.AreEqual(actual.SessionId, closeSessionParamsparams.SessionId);
                    Assert.False(actual.Success);
                }));
        }

        [Test]
        public async Task CloseSessionGivenValidSessionShouldCloseTheSessionAndDisconnect()
        {
            var session = await CreateSession();
            CloseSessionParams closeSessionParamsparams = new CloseSessionParams()
            {
                SessionId = session.SessionId,
            };

            // when expanding
            // then expect the nodes are server children 
            await RunAndVerify<CloseSessionResponse, CloseSessionResponse>(
                test: (requestContext) => CallCloseSession(closeSessionParamsparams, requestContext),
                verify: (actual =>
                {
                    Assert.AreEqual(actual.SessionId, closeSessionParamsparams.SessionId);
                    Assert.True(actual.Success);
                    Assert.False(service.SessionIds.Contains(session.SessionId));
                }));

            connectionServiceMock.Verify(c => c.Disconnect(It.IsAny<DisconnectParams>()));
        }

        [Test]
        public async Task FindNodesReturnsMatchingNode()
        {
            var session = await CreateSession();

            var foundNodes = service.FindNodes(session.SessionId, "Server", null, null, null);
            Assert.AreEqual(1, foundNodes.Count);
            Assert.AreEqual("Server", foundNodes[0].NodeType);
            Assert.AreEqual(session.RootNode.NodePath, new NodeInfo(foundNodes[0]).NodePath);
        }

        [Test]
        public async Task FindNodesReturnsEmptyListForNoMatch()
        {
            var session = await CreateSession();

            var foundNodes = service.FindNodes(session.SessionId, "Table", "testSchema", "testTable", "testDatabase");
            Assert.AreEqual(0, foundNodes.Count);
        }

        [Test]
        public void FindNodeCanExpandParentNodes()
        {
            var mockTreeNode = new Mock<TreeNode>();
            object[] populateChildrenArguments = { ItExpr.Is<bool>(x => x == false), ItExpr.IsNull<string>(), new CancellationToken(), ItExpr.IsNull<string>(), ItExpr.IsNull<IEnumerable<INodeFilter>>() };
            mockTreeNode.Protected().Setup("PopulateChildren", populateChildrenArguments);
            mockTreeNode.Object.IsAlwaysLeaf = false;

            // If I try to find a child node of the mock tree node with the expand parameter set to true
            ObjectExplorerUtils.FindNode(mockTreeNode.Object, node => false, node => false, true);

            // Then PopulateChildren gets called to expand the tree node
            mockTreeNode.Protected().Verify("PopulateChildren", Times.Once(), populateChildrenArguments);
        }

        [Test]
        public async Task VerifyGeneratesSessionId()
        {
            GetSessionIdResponse result = null;
            string error = null;

            var requestContext = RequestContextMocks.Create<GetSessionIdResponse>(r => result = r);
            requestContext.AddErrorHandling((string e, int i, string s2) => error = e);

            ObjectExplorerService oeService = new();

            string testPassword = "test_password", testAzureToken = "test_azure_account_token";

            await oeService.HandleGetSessionIdRequest(new()
            {
                ServerName = "serverName",
                DatabaseName = "msdb",
                AuthenticationType = SqlConstants.ActiveDirectoryPassword,
                UserName = "TestUser",
                Password = testPassword,
                AzureAccountToken = testAzureToken,
                SecureEnclaves = "fakeEnclave"

            }, requestContext.Object);

            Assert.That(error, Is.Null, "No error should have been sent for an invalid input");
            Assert.That(result.SessionId, Does.Not.Contain(testPassword), "Password should not appear in SessionId");
            Assert.That(result.SessionId, Does.Not.Contain(testAzureToken), "AzureAccountToken should not appear in SessionId");
            Assert.That(result.SessionId, Is.EqualTo("serverName_msdb_TestUser_ActiveDirectoryPassword_secureEnclaves:fakeEnclave"), "SessionId not as expected");

            // reset
            result = null;
            error = null;

            await oeService.HandleGetSessionIdRequest(null, requestContext.Object);

            Assert.That(result, Is.Null, "No result should have been sent for an invalid input");
            Assert.That(error, Does.Contain("System.ArgumentNullException: Value cannot be null. (Parameter 'connectionDetails')"), "Error message about connectionDetails being null should have been sent for an invalid input");
        }

        #region Helper methods
        private async Task<SessionCreatedParameters> CreateSession()
        {
            SessionCreatedParameters sessionResult = null;
            serviceHostMock.AddEventHandling(CreateSessionCompleteNotification.Type, (et, p) => sessionResult = p);
            CreateSessionResponse result = default(CreateSessionResponse);
            var contextMock = RequestContextMocks.Create<CreateSessionResponse>(r => result = r).AddErrorHandling(null);

            connectionServiceMock.Setup(c => c.Connect(It.IsAny<ConnectParams>()))
                .Returns((ConnectParams connectParams) => Task.FromResult(GetCompleteParamsForConnection(connectParams.OwnerUri, details)));

            ConnectionInfo connectionInfo = new ConnectionInfo(null, null, details);
            connectionInfo.AddConnection("Default", new SqlConnection(fakeConnectionString));
            connectionServiceMock.Setup((c => c.TryFindConnection(It.IsAny<string>(), out connectionInfo))).
                OutCallback((string t, out ConnectionInfo v) => v = connectionInfo)
                .Returns(true);

            connectionServiceMock.Setup(c => c.Disconnect(It.IsAny<DisconnectParams>())).Returns(true);
            await service.HandleCreateSessionRequest(details, contextMock.Object);
            await service.CreateSessionTask;

            return sessionResult;
        }

        private async Task ExpandAndVerifyServerNodes()
        {
            var session = await CreateSession();
            ExpandParams expandParams = new ExpandParams()
            {
                SessionId = session.SessionId,
                NodePath = session.RootNode.NodePath
            };

            // when expanding
            // then expect the nodes are server children 
            await RunAndVerify<bool, ExpandResponse>(
                test: (requestContext) => CallServiceExpand(expandParams, requestContext),
                verify: (actual =>
                {
                    Assert.AreEqual(actual.SessionId, session.SessionId);
                    Assert.NotNull(actual.SessionId);
                    VerifyServerNodeChildren(actual.Nodes);
                }));
        }

        private async Task RefreshAndVerifyServerNodes()
        {
            var session = await CreateSession();
            RefreshParams expandParams = new RefreshParams()
            {
                SessionId = session.SessionId,
                NodePath = session.RootNode.NodePath
            };

            // when expanding
            // then expect the nodes are server children 
            await RunAndVerify<bool, ExpandResponse>(
                test: (requestContext) => CallServiceRefresh(expandParams, requestContext),
                verify: (actual =>
                {
                    Assert.AreEqual(actual.SessionId, session.SessionId);
                    Assert.NotNull(actual.SessionId);
                    VerifyServerNodeChildren(actual.Nodes);
                }));
        }

        private async Task<ExpandResponse> CallServiceRefresh(RefreshParams expandParams, RequestContext<bool> requestContext)
        {
            ExpandResponse result = null;
            serviceHostMock.AddEventHandling(ExpandCompleteNotification.Type, (et, p) => result = p);

            await service.HandleRefreshRequest(expandParams, requestContext);
            Task task = service.ExpandTask;
            if (task != null)
            {
                await task;
            }

            return result;

        }

        private async Task<ExpandResponse> CallServiceExpand(ExpandParams expandParams, RequestContext<bool> requestContext)
        {
            ExpandResponse result = null;
            serviceHostMock.AddEventHandling(ExpandCompleteNotification.Type, (et, p) => result = p);

            await service.HandleExpandRequest(expandParams, requestContext);
            Task task = service.ExpandTask;
            if (task != null)
            {
                await task;
            }
            return result;
        }

        private async Task<SessionCreatedParameters> CallCreateSession(ConnectionDetails connectionDetails, RequestContext<CreateSessionResponse> context)
        {
            SessionCreatedParameters result = null;
            serviceHostMock.AddEventHandling(CreateSessionCompleteNotification.Type, (et, p) => result = p);

            await service.HandleCreateSessionRequest(connectionDetails, context);
            Task task = service.CreateSessionTask;
            if (task != null)
            {
                await task;
            }
            return result;
        }

        private async Task<CloseSessionResponse> CallCloseSession(CloseSessionParams closeSessionParams, RequestContext<CloseSessionResponse> context)
        {
            SessionCreatedParameters result = null;
            serviceHostMock.AddEventHandling(CreateSessionCompleteNotification.Type, (et, p) => result = p);

            await service.HandleCloseSessionRequest(closeSessionParams, context);
            return null;
        }

        private async Task CreateSessionRequestAndVerifyServerNodeHelper(ConnectionDetails details)
        {
            serviceHostMock.AddEventHandling(ConnectionCompleteNotification.Type, null);

            // Stub out the connection to avoid a 30second timeout while attempting to connect.
            // The tests don't need any connection context anyhow so this doesn't impact the scenario
            mockConnectionOpener.Setup(b => b.OpenServerConnection(It.IsAny<ConnectionInfo>(), It.IsAny<string>()))
                .Throws<Exception>();
            connectionServiceMock.Setup(c => c.Connect(It.IsAny<ConnectParams>()))
                .Returns((ConnectParams connectParams) => Task.FromResult(GetCompleteParamsForConnection(connectParams.OwnerUri, details)));
            ConnectionInfo connectionInfo = new ConnectionInfo(null, null, details);
            connectionInfo.AddConnection("Default", new SqlConnection(fakeConnectionString));
            connectionServiceMock.Setup((c => c.TryFindConnection(It.IsAny<string>(), out connectionInfo))).
                OutCallback((string t, out ConnectionInfo v) => v = connectionInfo)
                .Returns(true);

            // when creating a new session
            // then expect the create session request to return false
            await RunAndVerify<CreateSessionResponse, SessionCreatedParameters>(
                test: (requestContext) =>
                {
                    return CallCreateSession(details, requestContext);
                },
                verify: (actual =>
                {
                    Assert.True(actual.Success);
                    Assert.NotNull(actual.SessionId);
                    VerifyServerNode(actual.RootNode, details);
                }));

            // And expect no error notification to be sent
            serviceHostMock.Verify(x => x.SendEvent(ConnectionCompleteNotification.Type,
                It.IsAny<ConnectionCompleteParams>()), Times.Never());
        }

        private void VerifyServerNode(NodeInfo serverNode, ConnectionDetails details)
        {
            Assert.NotNull(serverNode);
            Assert.AreEqual(NodeTypes.Server.ToString(), serverNode.NodeType);
            string[] pathParts = serverNode.NodePath.Split(TreeNode.PathPartSeperator);
            Assert.AreEqual(1, pathParts.Length);
            Assert.AreEqual(details.ServerName, pathParts[0]);
            Assert.True(serverNode.Label.Contains(details.ServerName));
            Assert.False(serverNode.IsLeaf);
        }

        private void VerifyServerNodeChildren(NodeInfo[] children)
        {
            Assert.NotNull(children);
            Assert.True(children.Length == 3);
            Assert.True(children.All((x => x.NodeType == "Folder")));
        }

        private static ConnectionCompleteParams GetCompleteParamsForConnection(string uri, ConnectionDetails details)
        {
            return new ConnectionCompleteParams()
            {
                ConnectionId = Guid.NewGuid().ToString(),
                OwnerUri = uri,
                ConnectionSummary = new ConnectionSummary()
                {
                    ServerName = details.ServerName,
                    DatabaseName = details.DatabaseName,
                    UserName = details.UserName
                },
                ServerInfo = TestObjects.GetTestServerInfo()
            };
        }
        #endregion
    }
}
