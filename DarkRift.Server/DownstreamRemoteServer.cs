/*
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace DarkRift.Server
{
    internal sealed class DownstreamRemoteServer : IRemoteServer, IDisposable
    {
        /// <inheritdoc />
        public event EventHandler<ServerMessageReceivedEventArgs> MessageReceived;

        /// <inheritdoc />
        public event EventHandler<ServerConnectedEventArgs> ServerConnected;

        /// <inheritdoc />
        public event EventHandler<ServerDisconnectedEventArgs> ServerDisconnected;

        /// <inheritdoc />
        public ConnectionState ConnectionState => connection?.ConnectionState ?? ConnectionState.Disconnected;

        /// <inheritdoc />
        public IEnumerable<IPEndPoint> RemoteEndPoints => connection?.RemoteEndPoints ?? new IPEndPoint[0];

        /// <inheritdoc />
        public IServerGroup ServerGroup => serverGroup;


        /// <inheritdoc />
        public ushort ID { get; }

        /// <inheritdoc />
        public string Host { get; }

        /// <inheritdoc />
        public ushort Port { get; }

        /// <inheritdoc />
        public ServerConnectionDirection ServerConnectionDirection => ServerConnectionDirection.Downstream;

        /// <summary>
        ///     The connection to the remote server.
        /// </summary>
        /// <remarks>
        ///     Will change reference on reconnections. Currently this is not marked volatile as that is a very exceptional circumstance and at that point
        ///     was can likely tolerate just waiting for something else to synchronise caches later.
        /// </remarks>
        private NetworkServerConnection connection;

        /// <summary>
        ///     The server group we are part of.
        /// </summary>
        private readonly DownstreamServerGroup serverGroup;

        /// <summary>
        ///     The thread helper to use.
        /// </summary>
        private readonly DarkRiftThreadHelper threadHelper;

        /// <summary>
        ///     The logger to use.
        /// </summary>
        private readonly Logger logger;

        /// <summary>
        ///     Creates a new remote server.
        /// </summary>
        /// <param name="id">The ID of the server.</param>
        /// <param name="host">The host connected to.</param>
        /// <param name="port">The port connected to.</param>
        /// <param name="group">The group the server belongs to.</param>
        /// <param name="threadHelper">The thread helper to use.</param>
        /// <param name="logger">The logger to use.</param>
        internal DownstreamRemoteServer(ushort id, string host, ushort port, DownstreamServerGroup group, DarkRiftThreadHelper threadHelper, Logger logger)
        {
            this.ID = id;
            this.Host = host;
            this.Port = port;
            this.serverGroup = group;
            this.threadHelper = threadHelper;
            this.logger = logger;
        }

        /// <summary>
        ///     Sets the connection being used by this remote server.
        /// </summary>
        /// <param name="pendingServer">The connection to switch to.</param>
        internal void SetConnection(PendingDownstreamRemoteServer pendingServer)
        {
            if (connection != null)
            {
                connection.MessageReceived -= MessageReceivedHandler;
                connection.Disconnected -= DisconnectedHandler;
            }

            connection = pendingServer.Connection;

            // Switch out message received handler from the pending server
            connection.MessageReceived = MessageReceivedHandler;
            connection.Disconnected = DisconnectedHandler;

            EventHandler<ServerConnectedEventArgs> handler = ServerConnected;
            if (handler != null)
            {
                void DoServerConnectedEvent()
                {
                    try
                    {
                        handler?.Invoke(this, new ServerConnectedEventArgs(this));
                    }
                    catch (Exception e)
                    {

                        logger.Error("A plugin encountered an error whilst handling the ServerConnected event. The server will still be connected. (See logs for exception)", e);
                    }

                }

                threadHelper.DispatchIfNeeded(DoServerConnectedEvent);
            }

            // Handle all messages that had queued
            foreach (PendingDownstreamRemoteServer.QueuedMessage queuedMessage in pendingServer.GetQueuedMessages())
            {
                HandleMessage(queuedMessage.Message, queuedMessage.SendMode);

                queuedMessage.Message.Dispose();
            }
        }

        /// <summary>
        ///     Sends a message to the server.
        /// </summary>
        /// <param name="message">The message to send.</param>
        /// <param name="sendMode">How the message should be sent.</param>
        /// <returns>Whether the send was successful.</returns>
        public bool SendMessage(Message message, SendMode sendMode)
        {
            bool success = connection?.SendMessage(message.ToBuffer(), sendMode) ?? false;
            return success;
        }

        /// <summary>
        ///     Gets the endpoint with the given name.
        /// </summary>
        /// <param name="name">The name of the endpoint.</param>
        /// <returns>The end point.</returns>
        public IPEndPoint GetRemoteEndPoint(string name)
        {
            return connection?.GetRemoteEndPoint(name);
        }

        /// <summary>
        ///     Callback for when data is received.
        /// </summary>
        /// <param name="buffer">The data recevied.</param>
        /// <param name="sendMode">The SendMode used to send the data.</param>
        private void MessageReceivedHandler(MessageBuffer buffer, SendMode sendMode)
        {
            using (Message message = Message.Create(buffer, true))
            {
                if (message.IsCommandMessage)
                    logger.Warning($"Server {ID} sent us a command message unexpectedly. This server may be configured to expect clients to connect.");

                HandleMessage(message, sendMode);
            }
        }

        /// <summary>
        ///     Handles a message received.
        /// </summary>
        /// <param name="message">The message that was received.</param>
        /// <param name="sendMode">The send mode the emssage was received with.</param>
        private void HandleMessage(Message message, SendMode sendMode)
        {
            // Get another reference to the message so 1. we can control the backing array's lifecycle and thus it won't get disposed of before we dispatch, and
            // 2. because the current message will be disposed of when this method returns.
            Message messageReference = message.Clone();

            void DoMessageReceived()
            {
                ServerMessageReceivedEventArgs args = ServerMessageReceivedEventArgs.Create(message, sendMode, this);

                try
                {
                    MessageReceived?.Invoke(this, args);
                }
                catch (Exception e)
                {
                    logger.Error("A plugin encountered an error whilst handling the MessageReceived event. (See logs for exception)", e);
                }
                finally
                {
                    // Now we've executed everything, dispose the message reference and release the backing array!
                    messageReference.Dispose();
                    args.Dispose();
                }

            }

            //Inform plugins
            threadHelper.DispatchIfNeeded(DoMessageReceived);
        }

        /// <summary>
        /// Called when the connection is lost.
        /// </summary>
        /// <param name="error">The socket error that ocurred</param>
        /// <param name="exception">The exception that ocurred.</param>
        private void DisconnectedHandler(SocketError error, Exception exception)
        {
            serverGroup.DisconnectedHandler(this, exception);

            EventHandler<ServerDisconnectedEventArgs> handler = ServerDisconnected;
            if (handler != null)
            {
                void DoServerDisconnectedEvent()
                {
                    try
                    {
                        handler?.Invoke(this, new ServerDisconnectedEventArgs(this, error, exception));
                    }
                    catch (Exception e)
                    {
                        logger.Error("A plugin encountered an error whilst handling the ServerDisconnected event. (See logs for exception)", e);
                    }

                }

                threadHelper.DispatchIfNeeded(DoServerDisconnectedEvent);
            }
        }

        /// <summary>
        ///     Disconnects the connection without calling back to the server manager.
        /// </summary>
        internal bool DropConnection()
        {
            return connection.Disconnect();
        }

        private bool disposed = false;

        /// <summary>
        ///     Disposes of the connection.
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        ///     Handles disposing of the connection.
        /// </summary>
        /// <param name="disposing"></param>
#pragma warning disable CS0628
        protected void Dispose(bool disposing)
        {
            if (disposing && !disposed)
            {
                disposed = true;

                if (connection != null)
                    connection.Dispose();
            }
        }
#pragma warning restore CS0628
    }
}
