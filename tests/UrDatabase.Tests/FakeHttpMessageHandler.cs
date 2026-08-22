using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace UrDatabase.Tests
{
    /// <summary>
    /// Stands in for the network. Every test in this project uses one of these; nothing here ever
    /// reaches TMDB, Firebase or any other live service, and no API key is required.
    /// </summary>
    public sealed class FakeHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

        public List<string> Requests { get; } = new();
        public List<string?> RequestBodies { get; } = new();
        public List<string?> AuthorizationHeaders { get; } = new();
        public int CallCount => Requests.Count;

        public FakeHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
            => _responder = responder;

        public static FakeHttpMessageHandler Json(string json, HttpStatusCode status = HttpStatusCode.OK)
            => new(_ => new HttpResponseMessage(status)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
            });

        /// <summary>Responds based on what the requested URL contains, for multi-hop flows.</summary>
        public static FakeHttpMessageHandler Routed(params (string UrlFragment, HttpStatusCode Status, string Json)[] routes)
            => new(request =>
            {
                var url = request.RequestUri?.ToString() ?? "";
                foreach (var (fragment, status, json) in routes)
                {
                    if (url.Contains(fragment, StringComparison.OrdinalIgnoreCase))
                        return new HttpResponseMessage(status)
                        {
                            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
                        };
                }

                return new HttpResponseMessage(HttpStatusCode.NotFound)
                {
                    Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json")
                };
            });

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri?.ToString() ?? "");
            AuthorizationHeaders.Add(request.Headers.Authorization?.ToString());
            RequestBodies.Add(request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken));

            return _responder(request);
        }
    }
}
