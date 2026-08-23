using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace UrDatabase.Services
{
    /// <summary>
    /// Where one reading of VLC's status document comes from.
    /// </summary>
    /// <remarks>
    /// An interface for one reason: the implementation below opens a socket, and everything that
    /// decides anything — what the document means, when a report is due, what goes to the server —
    /// sits on this side of it and is tested with no socket at all. The same split
    /// <c>IImdbRatingLookup</c> already uses for OMDb.
    /// </remarks>
    public interface IVlcStatusReader
    {
        /// <summary>
        /// The document, or null when the interface did not answer. Never throws for a connection
        /// problem: not answering is an ordinary outcome here, not an error.
        /// </summary>
        Task<string?> ReadAsync(CancellationToken ct = default);
    }

    /// <summary>
    /// Reads <c>/requests/status.xml</c> from a VLC that was launched with an HTTP control
    /// interface on loopback.
    /// </summary>
    /// <remarks>
    /// VLC authenticates the interface with HTTP Basic, an empty username and the password it was
    /// given on the command line. The password is held here and never written anywhere: not to a
    /// log, not into an exception message, and not into the status line.
    /// </remarks>
    public sealed class HttpVlcStatusReader : IVlcStatusReader, IDisposable
    {
        /// <summary>
        /// A deliberately short deadline. This is a request to a process on the same machine; if
        /// it has not answered in two seconds it is not going to, and waiting longer only delays
        /// noticing that the player has gone.
        /// </summary>
        public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(2);

        private readonly HttpClient _http;
        private readonly Uri _statusUri;
        private readonly string _authorization;

        public HttpVlcStatusReader(
            VlcControlEndpoint endpoint,
            HttpMessageHandler? handler = null,
            TimeSpan? timeout = null)
        {
            if (endpoint is null) throw new ArgumentNullException(nameof(endpoint));

            _statusUri = endpoint.StatusUri;
            _authorization = BuildBasicAuthorization(endpoint.Password);

            _http = handler is null ? new HttpClient() : new HttpClient(handler);
            _http.Timeout = timeout ?? DefaultTimeout;
        }

        /// <summary>
        /// The <c>Authorization</c> value VLC expects: Basic, with no username at all.
        /// </summary>
        internal static string BuildBasicAuthorization(string? password) =>
            "Basic " + Convert.ToBase64String(
                Encoding.ASCII.GetBytes($"{VlcControlEndpoint.Username}:{password ?? ""}"));

        public async Task<string?> ReadAsync(CancellationToken ct = default)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, _statusUri);
                request.Headers.TryAddWithoutValidation("Authorization", _authorization);
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/xml"));

                using var response = await _http.SendAsync(request, ct);

                // A 401 is the wrong password and a 404 is a VLC whose interface is not what this
                // expects. Both are "no reading", which is what the schedule already knows how to
                // handle, so neither is worth a distinct failure that could reach the viewer.
                if (!response.IsSuccessStatusCode) return null;

                return await response.Content.ReadAsStringAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                // Refused, timed out, or the player is gone. All the same answer.
                return null;
            }
        }

        public void Dispose() => _http.Dispose();
    }
}
