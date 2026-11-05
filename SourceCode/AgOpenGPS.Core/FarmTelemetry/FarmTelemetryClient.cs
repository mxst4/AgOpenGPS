using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AgOpenGPS.Core.FarmTelemetry
{
    public sealed class FarmTelemetryResult
    {
        public bool Successful { get; }
        public bool TooLarge { get; }
        public string Message { get; }
        public FarmTelemetryResult(bool successful, bool tooLarge, string message)
        {
            Successful = successful;
            TooLarge = tooLarge;
            Message = message;
        }
    }

    public sealed class FarmTelemetryClient : IDisposable
    {
        private readonly HttpClient _client;
        private readonly TimeSpan _retryDelay;

        public FarmTelemetryClient(string server, string apiKey, HttpMessageHandler handler = null, TimeSpan? retryDelay = null)
        {
            if (!IsCustomHttpsServer(server)) throw new ArgumentException("Farm sync requires a custom HTTPS AgShare server.", nameof(server));
            if (string.IsNullOrWhiteSpace(apiKey)) throw new ArgumentException("Configure the shared AgShare API key.", nameof(apiKey));
            _client = handler == null ? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) : new HttpClient(handler);
            _client.BaseAddress = new Uri(server);
            _client.Timeout = TimeSpan.FromSeconds(15);
            _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("ApiKey", apiKey);
            _client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            _retryDelay = retryDelay ?? TimeSpan.FromSeconds(1);
        }

        public static bool IsCustomHttpsServer(string server)
        {
            return Uri.TryCreate(server, UriKind.Absolute, out Uri uri) && uri.Scheme == Uri.UriSchemeHttps &&
                string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment) &&
                !uri.Host.TrimEnd('.').Equals("agshare.agopengps.com", StringComparison.OrdinalIgnoreCase) &&
                !uri.Host.TrimEnd('.').EndsWith(".agshare.agopengps.com", StringComparison.OrdinalIgnoreCase);
        }

        public async Task<FarmTelemetryResult> SendAsync(string body, Guid telemetryId, Guid snapshotId,
            Func<bool> isCurrent, CancellationToken cancellationToken)
        {
            for (int attempt = 0; attempt < 2; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!isCurrent()) return new FarmTelemetryResult(false, false, "Field changed; waiting for a fresh snapshot.");
                try
                {
                    using (var content = new StringContent(body, Encoding.UTF8, "application/json"))
                    using (var response = await _client.PostAsync("/api/telemetry/v1/snapshots", content, cancellationToken).ConfigureAwait(false))
                    {
                        int status = (int)response.StatusCode;
                        if (attempt == 0 && (status == 429 || status >= 500))
                        {
                            await Task.Delay(_retryDelay, cancellationToken).ConfigureAwait(false);
                            continue;
                        }
                        string responseBody = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                        if (status == 200)
                        {
                            try
                            {
                                var ack = JObject.Parse(responseBody);
                                string result = (string)ack["status"];
                                if ((int?)ack["schemaVersion"] == 1 && (Guid?)ack["telemetryId"] == telemetryId &&
                                    (Guid?)ack["snapshotId"] == snapshotId && (result == "ACCEPTED" || result == "IGNORED_STALE"))
                                    return new FarmTelemetryResult(true, false, result == "ACCEPTED" ? "Snapshot accepted by Farm." : "Older snapshot ignored; check the tablet clock.");
                            }
                            catch (JsonException) { }
                            catch (FormatException) { }
                            return new FarmTelemetryResult(false, false, "Farm returned an invalid snapshot acknowledgement.");
                        }
                        return new FarmTelemetryResult(false, status == 413, ErrorMessage(status, responseBody));
                    }
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    if (attempt == 1) return new FarmTelemetryResult(false, false, "Farm request timed out; next scheduled sync will capture fresh state.");
                }
                catch (HttpRequestException)
                {
                    if (attempt == 1) return new FarmTelemetryResult(false, false, "Farm is unavailable; next scheduled sync will capture fresh state.");
                }
                await Task.Delay(_retryDelay, cancellationToken).ConfigureAwait(false);
            }
            return new FarmTelemetryResult(false, false, "Farm sync failed.");
        }

        private static string ErrorMessage(int status, string responseBody)
        {
            if (status == 401 || status == 403) return "Farm rejected access. Check the shared key and source terminal assignment.";
            if (status == 413) return "Snapshot too large; next sync will send metadata without geometry.";
            if (status == 409)
            {
                try
                {
                    if ((string)JObject.Parse(responseBody)["code"] == "TELEMETRY_SOURCE_UNASSIGNED")
                        return "Register this telemetry ID and assign it to a tractor in Farm → AgOpenGPS.";
                }
                catch (JsonException) { }
                return "Farm rejected the snapshot identity (409). Check telemetry ID and tablet clock.";
            }
            if (status == 400 || status == 422)
            {
                try
                {
                    var error = JObject.Parse(responseBody);
                    string field = (string)error["field"];
                    string code = (string)error["code"];
                    if (field != null && field.Length <= 120 && code != null && code.Length <= 80 &&
                        System.Text.RegularExpressions.Regex.IsMatch(field + code, @"\A[a-zA-Z0-9_\.\[\]]+\z"))
                        return "Farm rejected " + field + " (" + code + "). Check the saved field data and tablet clock.";
                }
                catch (JsonException) { }
            }
            return "Farm sync HTTP " + status + ". Check source configuration and saved field data.";
        }

        public void Dispose() => _client.Dispose();
    }
}
