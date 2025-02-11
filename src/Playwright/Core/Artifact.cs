/*
 * MIT License
 *
 * Copyright (c) Microsoft Corporation.
 *
 * Permission is hereby granted, free of charge, to any person obtaining a copy
 * of this software and associated documentation files (the "Software"), to deal
 * in the Software without restriction, including without limitation the rights
 * to use, copy, modify, merge, publish, distribute, sublicense, and / or sell
 * copies of the Software, and to permit persons to whom the Software is
 * furnished to do so, subject to the following conditions:
 *
 * The above copyright notice and this permission notice shall be included in all
 * copies or substantial portions of the Software.
 *
 * THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
 * IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
 * FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
 * AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
 * LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
 * OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
 * SOFTWARE.
 */

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Playwright.Transport;
using Microsoft.Playwright.Transport.Channels;
using Microsoft.Playwright.Transport.Protocol;

namespace Microsoft.Playwright.Core
{
    internal class Artifact : ChannelOwnerBase, IChannelOwner<Artifact>
    {
        private readonly Connection _connection;
        private readonly ArtifactChannel _channel;

        internal Artifact(IChannelOwner parent, string guid, ArtifactInitializer initializer) : base(parent, guid)
        {
            _connection = parent.Connection;
            _channel = new(guid, parent.Connection, this);
            AbsolutePath = initializer.AbsolutePath;
        }

        Connection IChannelOwner.Connection => _connection;

        ChannelBase IChannelOwner.Channel => _channel;

        IChannel<Artifact> IChannelOwner<Artifact>.Channel => _channel;

        internal string AbsolutePath { get; }

        public async Task<string> PathAfterFinishedAsync()
        {
            if (_connection.IsRemote)
            {
                throw new PlaywrightException("Path is not available when connecting remotely. Use SaveAsAsync() to save a local copy.");
            }
            return await _channel.PathAfterFinishedAsync().ConfigureAwait(false);
        }

        public async Task SaveAsAsync(string path)
        {
            if (!_connection.IsRemote)
            {
                await _channel.SaveAsAsync(path).ConfigureAwait(false);
                return;
            }
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path));
            await using var stream = await _channel.SaveAsStreamAsync().ConfigureAwait(false);

            // TODO: Write it via a stream to the file
            string base64 = await stream.ReadAsync().ConfigureAwait(false);
            var bytes = Convert.FromBase64String(base64);
            using (var fileStream = new FileStream(path, FileMode.Create, FileAccess.Write))
            {
                using var cancellationToken = new CancellationTokenSource();
#pragma warning disable CA1835 // We can't use ReadOnlyMemory on netstandard
                await fileStream.WriteAsync(bytes, 0, bytes.Length, cancellationToken.Token).ConfigureAwait(false);
#pragma warning restore CA1835
            }
        }

        public async Task<System.IO.Stream> CreateReadStreamAsync()
        {
            await using var stream = await _channel.StreamAsync().ConfigureAwait(false);

            // TODO: use an actual Stream implementation
            string base64 = await stream.ReadAsync().ConfigureAwait(false);
            return new MemoryStream(Convert.FromBase64String(base64));
        }

        internal Task CancelAsync() => _channel.CancelAsync();

        internal Task<string> FailureAsync() => _channel.FailureAsync();

        internal Task DeleteAsync() => _channel.DeleteAsync();
    }
}
