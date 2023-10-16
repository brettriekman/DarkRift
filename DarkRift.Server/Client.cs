/*
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Net.Sockets;
using DarkRift.Dispatching;

namespace DarkRift.Server
{
    /// <inheritDoc />
    internal sealed class Client : IClient, IDisposable
    {
        /// <inheritdoc/>
        public event EventHandler<MessageReceivedEventArgs> MessageReceived;

        /// <inheritdoc/>
        public ushort ID { get; }

        /// <inheritdoc/>
        public IPEndPoint RemoteTcpEndPoint => connection.GetRemoteEndPoint("tcp");

        /// <inheritdoc/>
        public IPEndPoint RemoteUdpEndPoint => connection.GetRemoteEndPoint("udp");

        /// <inheritdoc/>
        [Obsolete("Use Client.ConnectionState instead.")]
        public bool IsConnected => connection.ConnectionState == ConnectionState.Connected;

        /// <inheritdoc/>
        public ConnectionState ConnectionState => connection.ConnectionState;

        /// <inheritdoc/>
        public DateTime ConnectionTime { get; }

        /// <inheritdoc/>
        public uint MessagesSent => (uint)Thread.VolatileRead(ref messagesSent);

        private int messagesSent;

        /// <inheritdoc/>
        public uint MessagesPushed => (uint)Thread.VolatileRead(ref messagesPushed);

        private int messagesPushed;

        /// <inheritdoc/>
        public uint MessagesReceived => (uint)Thread.VolatileRead(ref messagesReceived);

        private int messagesReceived;

        /// <inheritdoc/>
        public IEnumerable<IPEndPoint> RemoteEndPoints => connection.RemoteEndPoints;

        /// <inheritdoc/>
        public RoundTripTimeHelper RoundTripTime { get; }

        /// <summary>
        ///     The connection to the client.
        /// </summary>
        private readonly NetworkServerConnection connection;

        /// <summary>
        ///     The client manager in charge of this client.
        /// </summary>
        private readonly ClientManager clientManager;

        /// <summary>
        ///     The thread helper this client will use.
        /// </summary>
        private readonly DarkRiftThreadHelper threadHelper;

        /// <summary>
        ///     The logger this client will use.
        /// </summary>
        private readonly Logger logger;

        /// <summary>
        ///     Creates a new client connection with a given global identifier and the client they are connected through.
        /// </summary>
        /// <param name="connection">The connection we handle.</param>
        /// <param name="id">The ID we've been assigned.</param>
        /// <param name="clientManager">The client manager in charge of this client.</param>
        /// <param name="threadHelper">The thread helper this client will use.</param>
        /// <param name="logger">The logger this client will use.</param>
        internal static Client Create(NetworkServerConnection connection, ushort id, ClientManager clientManager, DarkRiftThreadHelper threadHelper, Logger logger)
        {
            Client client = new Client(connection, id, clientManager, threadHelper, logger);

            return client;
        }

        /// <summary>
        ///     Creates a new client connection with a given global identifier and the client they are connected through.
        /// </summary>
        /// <param name="connection">The connection we handle.</param>
        /// <param name="id">The ID assigned to this client.</param>
        /// <param name="clientManager">The client manager in charge of this client.</param>
        /// <param name="threadHelper">The thread helper this client will use.</param>
        /// <param name="logger">The logger this client will use.</param>
        private Client(NetworkServerConnection connection, ushort id, ClientManager clientManager, DarkRiftThreadHelper threadHelper, Logger logger)
        {
            this.connection = connection;
            this.ID = id;
            this.clientManager = clientManager;
            this.threadHelper = threadHelper;
            this.logger = logger;

            // TODO make a UTC version of this as this is local date time
            this.ConnectionTime = DateTime.Now;

            connection.MessageReceived = HandleIncomingDataBuffer;
            connection.Disconnected = Disconnected;

            //TODO make configurable
            this.RoundTripTime = new RoundTripTimeHelper(10, 10);
        }

        /// <summary>
        ///     Sends the client their ID.
        /// </summary>
        private void SendID()
        {
            using (DarkRiftWriter writer = DarkRiftWriter.Create())
            {
                writer.Write(ID);

                using (Message command = Message.Create((ushort)CommandCode.Configure, writer))
                {
                    command.IsCommandMessage = true;
                    PushBuffer(command.ToBuffer(), SendMode.Reliable);
                }
            }
        }

        /// <summary>
        /// Starts this client's connecting listening for messages.
        /// </summary>
        internal void StartListening()
        {
            try
            {
                connection.StartListening();
                SendID();
            }
            catch (Exception e)
            {
                logger.Error("Failed to start listening to connection.", e);
                clientManager.HandleDisconnection(this, false, SocketError.SocketError, e);
            }
        }

        /// <inheritdoc/>
        public bool SendMessage(Message message, SendMode sendMode)
        {
            //Send frame
            if (!PushBuffer(message.ToBuffer(), sendMode))
                return false;

            if (message.IsPingMessage)
                RoundTripTime.RecordOutboundPing(message.PingCode);

            //Increment counter
            Interlocked.Increment(ref messagesSent);
            return true;
        }

        /// <inheritdoc/>
        public bool Disconnect()
        {
            if (!connection.Disconnect())
                return false;

            clientManager.HandleDisconnection(this, true, SocketError.Disconnecting, null);

            return true;
        }

        /// <summary>
        ///     Disconnects the connection without invoking events for plugins.
        /// </summary>
        internal bool DropConnection()
        {
            clientManager.DropClient(this);

            return connection.Disconnect();
        }

        /// <inheritdoc/>
        public IPEndPoint GetRemoteEndPoint(string name)
        {
            return connection.GetRemoteEndPoint(name);
        }

        /// <summary>
        ///     Handles a remote disconnection.
        /// </summary>
        /// <param name="error">The error that caused the disconnection.</param>
        /// <param name="exception">The exception that caused the disconnection.</param>
        private void Disconnected(SocketError error, Exception exception)
        {
            clientManager.HandleDisconnection(this, false, error, exception);
        }

        /// <summary>
        ///     Handles data that was sent from this client.
        /// </summary>
        /// <param name="buffer">The buffer that was received.</param>
        /// <param name="sendMode">The method data was sent using.</param>
        internal void HandleIncomingDataBuffer(MessageBuffer buffer, SendMode sendMode)
        {
            //Add to received message counter
            Interlocked.Increment(ref messagesReceived);
            Message message;
            try
            {
                message = Message.Create(buffer, true);
            }
            catch (IndexOutOfRangeException)
            {
                return;
            }

            try
            {
                HandleIncomingMessage(message, sendMode);
            }
            finally
            {
                message.Dispose();
            }
        }

        /// <summary>
        ///     Handles messages that were sent from this client.
        /// </summary>
        /// <param name="message">The message that was received.</param>
        /// <param name="sendMode">The method data was sent using.</param>
        internal void HandleIncomingMessage(Message message, SendMode sendMode)
        {
            // Get another reference to the message so 1. we can control the backing array's lifecycle and thus it won't get disposed of before we dispatch, and
            // 2. because the current message will be disposed of when this method returns.
            Message messageReference = message.Clone();

            void DoMessageReceived()
            {
                MessageReceivedEventArgs args = MessageReceivedEventArgs.Create(
                    messageReference,
                    sendMode,
                    this
                );

                try
                {
                    MessageReceived?.Invoke(this, args);
                }
                catch (Exception e)
                {
                    logger.Error("A plugin encountered an error whilst handling the MessageReceived event.", e);

                    return;
                }
                finally
                {
                    // Now we've executed everything, dispose the message reference and release the backing array!
                    args.Dispose();
                    messageReference.Dispose();
                }

            }

            //Inform plugins
            threadHelper.DispatchIfNeeded(DoMessageReceived);
        }

        /// <summary>
        ///     Pushes a buffer to the client.
        /// </summary>
        /// <param name="buffer">The buffer to push.</param>
        /// <param name="sendMode">The method to send the data using.</param>
        /// <returns>Whether the send was successful.</returns>
        private bool PushBuffer(MessageBuffer buffer, SendMode sendMode)
        {
            if (!connection.SendMessage(buffer, sendMode))
                return false;

            Interlocked.Increment(ref messagesPushed);

            return true;
        }


        /// <summary>
        ///     Disposes of this client.
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

#pragma warning disable CS0628
        protected void Dispose(bool disposing)
        {
            if (disposing)
                connection.Dispose();
        }
#pragma warning restore CS0628
    }
}
