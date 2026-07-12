#region "copyright"

/*
    Copyright © 2016 - 2024 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using Nito.AsyncEx;
using Nito.AsyncEx.Synchronous;
using OpenAstroAra.Core.Locale;
using OpenAstroAra.Core.Utility;
using OpenAstroAra.Core.Utility.Notification;
using System;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Equipment.Equipment.MyGuider.MetaGuide {

    public class MetaGuideListener {

        public event EventHandler<MetaGuideCameraMsg>? OnCamera;

        public event EventHandler<MetaGuideStatusMsg>? OnStatus;

        public event EventHandler<MetaGuideGuideMsg>? OnGuide;

        public event EventHandler<MetaGuideGuideParamsMsg>? OnGuideParams;

        public event EventHandler<MetaGuideCalibrationInfoMsg>? OnCalibrationInfo;

        public event EventHandler<MetaGuideMountMsg>? OnMount;

        public event EventHandler? OnDisconnected;

        private const int METAGUIDE_QUEUE_TIMEOUT_MS = 5000;

        public MetaGuideListener() {
        }

        [SuppressMessage("Design", "CA1031:Do not catch general exception types",
            Justification = "UDP message-processing boundary: each datagram is parsed and dispatched to arbitrary subscriber event handlers; any handler or malformed-message fault is logged so one bad packet cannot tear down the long-running listen loop. CA1031 sanctions general catches at such recover-and-continue boundaries.")]
        private void ProcessMessage(string[] splitMessage) {
            try {
                if (splitMessage[0] == "OPENSCI" && splitMessage[1] == "ASTRO" && splitMessage[3] == "MG") {
                    string type = splitMessage[2];
                    switch (type) {
                        case "CAMERA": {
                                var parsedMessage = MetaGuideCameraMsg.Create(splitMessage);
                                if (parsedMessage != null) {
                                    this.OnCamera?.Invoke(this, parsedMessage);
                                }
                            }
                            break;

                        case "STATUS": {
                                var parsedMessage = MetaGuideStatusMsg.Create(splitMessage);
                                if (parsedMessage != null) {
                                    this.OnStatus?.Invoke(this, parsedMessage);
                                }
                            }
                            break;

                        case "GUIDE": {
                                var parsedMessage = MetaGuideGuideMsg.Create(splitMessage);
                                if (parsedMessage != null) {
                                    this.OnGuide?.Invoke(this, parsedMessage);
                                }
                            }
                            break;

                        case "GUIDEPARMS": {
                                var parsedMessage = MetaGuideGuideParamsMsg.Create(splitMessage);
                                if (parsedMessage != null) {
                                    this.OnGuideParams?.Invoke(this, parsedMessage);
                                }
                            }
                            break;

                        case "CALINFO": {
                                var parsedMessage = MetaGuideCalibrationInfoMsg.Create(splitMessage);
                                if (parsedMessage != null) {
                                    this.OnCalibrationInfo?.Invoke(this, parsedMessage);
                                }
                            }
                            break;

                        case "MOUNTNAME": {
                                var parsedMessage = MetaGuideMountMsg.Create(splitMessage);
                                if (parsedMessage != null) {
                                    this.OnMount?.Invoke(this, parsedMessage);
                                }
                            }
                            break;

                        default:
                            break;
                    }
                }
            } catch (Exception ex) {
                Logger.Error(ex);
                Notifier.ShowError(String.Format(CultureInfo.CurrentCulture, Loc.Instance["LblMetaGuideListenerError"], ex.Message));
            }
        }

        private async Task RunConsumer(
            AsyncProducerConsumerQueue<string[]> messageQueue,
            CancellationToken cancellationToken) {
            await Task.Run(async () => {
                try {
                    while (!cancellationToken.IsCancellationRequested) {
                        string[] message = await messageQueue.DequeueAsync(cancellationToken);
                        ProcessMessage(message);
                    }
                } catch (OperationCanceledException) {
                }
            }, cancellationToken);
        }

        public async Task RunListener(
            bool useIpAddressAny,
            int port,
            CancellationToken ct) {
            await Task.Run(async () => {
                Task? consumerTask = null;
                Socket? socket = null;
                var consumerTokenSource = new CancellationTokenSource();

                try {
                    var messageQueue = new AsyncProducerConsumerQueue<string[]>(200);
                    // Run consumer on a separate, concurrent task to keep up with UDP messages from MetaGuide
                    consumerTask = RunConsumer(messageQueue, CancellationTokenSource.CreateLinkedTokenSource(ct, consumerTokenSource.Token).Token);

                    var listenAddr = new IPEndPoint(useIpAddressAny ? IPAddress.Any : IPAddress.Loopback, port);
                    EndPoint remoteEndpoint = listenAddr;
                    var receiveBytes = new byte[1024];

                    socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp) {
                        EnableBroadcast = true,
                        ReceiveTimeout = METAGUIDE_QUEUE_TIMEOUT_MS
                    };

                    socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                    socket.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.IPv6Only, false);
                    socket.Bind(listenAddr);

                    while (!ct.IsCancellationRequested) {
                        var bytesReceived = socket.ReceiveFrom(receiveBytes, ref remoteEndpoint);
                        var rawMessage = Encoding.UTF8.GetString(receiveBytes, 0, bytesReceived);
                        var splitMessage = rawMessage.Split(' ', StringSplitOptions.RemoveEmptyEntries);

                        using var timeoutTokenSource = new CancellationTokenSource(TimeSpan.FromMilliseconds(METAGUIDE_QUEUE_TIMEOUT_MS));
                        using var timeoutOrCancelledTokenSource = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutTokenSource.Token);

                        using (timeoutTokenSource.Token.Register(() => Logger.Error($"MetaGuide queue full"))) {
                            await messageQueue.EnqueueAsync(splitMessage, timeoutOrCancelledTokenSource.Token);
                        }
                    }
                } catch (Exception ex) {
                    Logger.Error(ex);
                    Notifier.ShowError(ex.Message);
                    throw;
                } finally {
                    socket?.Close();
                    try { if (consumerTokenSource != null) { await consumerTokenSource.CancelAsync(); } } catch (ObjectDisposedException) { }
                    consumerTask?.WaitWithoutException(new CancellationTokenSource(METAGUIDE_QUEUE_TIMEOUT_MS).Token);
                    this.OnDisconnected?.Invoke(this, System.EventArgs.Empty);
                }
            }, ct);
        }
    }
}