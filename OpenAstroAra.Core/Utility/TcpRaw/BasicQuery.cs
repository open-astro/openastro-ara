#region "copyright"

/*
    Copyright © 2016 - 2024 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Core.Utility.TcpRaw {

    public class BasicQuery {
        public string Address { get; set; }

        public int Port { get; set; }

        public string Command { get; set; }

        public string? WaitFor { get; set; }

        public BasicQuery(string address, int port, string command, string? waitFor = null) {
            Address = address;
            Port = port;
            Command = command;
            WaitFor = waitFor;
        }
        public Task<string> SendQuery() {
            return SendQuery(CancellationToken.None);
        }

        public async Task<string> SendQuery(CancellationToken token) {
            using (var client = new TcpClient()) {
                try {
                    Logger.Trace($"TcpRaw: Connecting to {Address}:{Port}");
                    await client.ConnectAsync(Address, Port, token);
                } catch (OperationCanceledException) {
                    throw;
                } catch (Exception ex) {
                    Logger.Error($"TcpRaw: Error connecting to {Address}:{Port}: {ex}");
                    throw;
                }

                var stream = client.GetStream();
                // ReadAsync returns 0 at EOF (peer closed the socket); without treating that as a
                // terminal condition the WaitFor loop would busy-spin forever. Also impose a default
                // read deadline so a silent peer that never sends the expected reply can't hang the
                // call indefinitely when no cancellation token was supplied.
                using var readCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                readCts.CancelAfter(TimeSpan.FromSeconds(30));
                var readToken = readCts.Token;
                try {

                    var buffer = new byte[2048];
                    int length;
                    string response = string.Empty;

                    if (!string.IsNullOrEmpty(WaitFor) && stream.CanRead) {
                        bool waitDone = false;

                        while (!waitDone) {
                            length = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), readToken);
                            if (length == 0) {
                                throw new IOException($"TcpRaw: connection to {Address}:{Port} closed before '{WaitFor}' was received");
                            }
                            response = Encoding.ASCII.GetString(buffer, 0, length);
                            Logger.Trace($"TcpRaw: Received message: {ToLiteral(response)}");

                            if (response.Equals(WaitFor, StringComparison.Ordinal)) { waitDone = true; }
                        }
                    }

                    // Send command
                    Logger.Trace($"TcpRaw: Sending command: {ToLiteral(Command)}");
                    var data = Encoding.ASCII.GetBytes($"{Command}");
                    await stream.WriteAsync(data.AsMemory(0, data.Length), token);

                    // Read response
                    length = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), readToken);
                    if (length == 0) {
                        throw new IOException($"TcpRaw: connection to {Address}:{Port} closed before a response was received");
                    }
                    response = Encoding.ASCII.GetString(buffer, 0, length);


                    Logger.Trace($"TcpRaw: Received message: {ToLiteral(response)}");

                    return response;
                } finally {
                    stream.Close();
                    client.Close();
                }
            }
        }

        private static string ToLiteral(string str) {
            str = str.Replace("\r", @"\r", StringComparison.Ordinal).Replace("\n", @"\n", StringComparison.Ordinal).Replace("\t", @"\t", StringComparison.Ordinal);

            return str;
        }
    }
}