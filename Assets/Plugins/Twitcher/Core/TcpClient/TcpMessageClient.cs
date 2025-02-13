using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Text;
using UnityEngine;

namespace Twitcher
{
    /// <summary>
    /// Wrapper class around the .Net TcpClient class. This wrapper adds some basic
    /// message handling, and utility methods for message sending. It also supports
    /// application of a rate limiter on sent messages, as Twitch has restrictions.
    /// </summary>
    public class TcpMessageClient
    {
        private const int BUFFER_SIZE = 1024;

        private const string END_MESSAGE_TAG = "\r\n";
        public delegate void RawMessageDelegate(string message);

        private readonly IRateLimiter rateLimiter;
        private readonly string host;
        private readonly int port;

        private TcpClient tcpClient;
        private byte[] buffer;
        private bool closed;
        private Queue<string> messageQueue;
        private NetworkStream networkStream;
        private StreamWriter streamWriter;
        private string incomingMessages = "";

        /// <summary>
        /// Event called when a message is received containing the raw message contents.
        /// </summary>
        public event RawMessageDelegate onRawMessageReceived;


        /// <summary>
        /// Gets a value indicating whether this client is connected.
        /// </summary>
        /// <value>True if connected, false otherwise.</value>
        public bool Connected
        {
            get
            {
                return (tcpClient != null && tcpClient.Connected);
            }
        }

        /// <summary>
        /// Creates a new instance of a TcpMessageClient
        /// </summary>
        /// <param name="host">Host to connect to.</param>
        /// <param name="port">Port to connect to.</param>
        /// <param name="rateLimiter">Rate limiter if desired, null for unlimited rate.</param>
        public TcpMessageClient(string host, int port, IRateLimiter rateLimiter = null)
        {
            this.host = host;
            this.port = port;

            this.rateLimiter = rateLimiter ?? new UnlimitedRate();
            messageQueue = new Queue<string>();

            Reconnect();
        }

        /// <summary>
        /// Send the specified message.
        /// </summary>
        /// <param name="message">Message.</param>
        /// <param name="flushOnSend">True to flush the stream, false otherwise.</param>
        /// <returns>True if the message sent, false otherwise.</returns>
        public bool Send(string message, bool flushOnSend = true)
        {
            if (rateLimiter.HasTokens(1))
            {
                if (!message.EndsWith(END_MESSAGE_TAG, StringComparison.InvariantCultureIgnoreCase))
                {
                    message += END_MESSAGE_TAG;
                }
                rateLimiter.ConsumeTokens(1);
                streamWriter.WriteLine(message);
                if (flushOnSend)
                {
                    streamWriter.Flush();
                }
                return true;
            }
            else
            {
                messageQueue.Enqueue(message);
                return false;
            }
        }

        /// <summary>
        /// Send multiple messages through the connection.
        /// </summary>
        /// <param name="messages">Messages to send.</param>
        public void Send(string[] messages)
        {
            bool needsFlush = false;
            for (int i = 0; i < messages.Length; i++)
            {
                needsFlush |= Send(messages[i], false);
            }
            if (needsFlush)
            {
                streamWriter.Flush();
            }
        }

        /// <summary>
        /// Callback used when the TcpClient has a result to process.
        /// </summary>
        /// <param name="result">Result to process.</param>
        private void ReadAsyncCallback(IAsyncResult result)
        {
            if (closed || !Connected)
                return;

            lock (incomingMessages)
            {
                try
                {
                    // First lock the stream and extract the message from the AsyncResult.
                    int bytesRead = networkStream.EndRead(result);
                    incomingMessages += Encoding.UTF8.GetString(buffer, 0, bytesRead);
                    networkStream.BeginRead(buffer, 0, BUFFER_SIZE, new AsyncCallback(ReadAsyncCallback), null);
                }
                catch
                {
                    Close();
                }
            }
        }

        /// <summary>
        /// Closes the TcpClient connection.
        /// </summary>
        public virtual void Close()
        {
            if (closed)
                return;

            if (streamWriter != null)
            {
                streamWriter.Flush();
                streamWriter.Close();
            }
            if (networkStream != null)
            {
                networkStream.Close();
            }
            if (tcpClient != null)
            {
                tcpClient.Close();
            }
            closed = true;
        }

        /// <summary>
        /// Processes any and all messages pending in the message queue. This queue will
        /// be filled by message that have not yet sent due to rate limiting.
        /// It will also then attempt to parse any pending messages that have been received.
        /// </summary>
        public virtual void ProcessMessages()
        {
            // Process the message queue first.
            while (messageQueue.Count > 0 && rateLimiter.HasTokens(1))
                Send(messageQueue.Dequeue());

            // Then try to process any new messages.
            lock (incomingMessages)
            {
                if (string.IsNullOrEmpty(incomingMessages))
                    return;

                // Search the string for an end of message tag, if found, process the message up to that tag, remove it, and repeat.
                int tagIndex = incomingMessages.IndexOf(END_MESSAGE_TAG, StringComparison.InvariantCultureIgnoreCase);
                while (tagIndex != -1)
                {
                    onRawMessageReceived?.Invoke(incomingMessages.Substring(0, tagIndex));

                    incomingMessages = incomingMessages.Substring(tagIndex + END_MESSAGE_TAG.Length);
                    tagIndex = incomingMessages.IndexOf(END_MESSAGE_TAG, StringComparison.InvariantCultureIgnoreCase);
                }
            }
        }

        /// <summary>
        /// Attempts to connect the TcpClient if not currently connected.
        /// </summary>
        protected void Reconnect()
        {
            if (!Connected)
            {
                try
                {
                    tcpClient = new TcpClient(host, port);
                    networkStream = tcpClient.GetStream();
                    streamWriter = new StreamWriter(tcpClient.GetStream());

                    buffer = new byte[BUFFER_SIZE];
                    networkStream.BeginRead(buffer, 0, BUFFER_SIZE, new AsyncCallback(ReadAsyncCallback), null);
                    closed = false;

                    OnConnect();
                }
                catch (SocketException exception)
                {
                    if (TwitcherUtil.LogErrors)
                        Debug.LogException(exception);
                }
            }
        }

        /// <summary>
        /// Called when the TcpClient establishes a connection to the host.
        /// </summary>
        protected virtual void OnConnect() { }
    }

}