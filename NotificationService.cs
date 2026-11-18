//--------------------------------------------------------------------------------
// NotificationService — транспорт Hub в ntfy.
//
// Сервис не знает про OI Flow, сигналы и confidence. Он получает готовое
// сообщение от индикатора, проверяет канал, дедуплицирует по dedupeKey и
// отправляет HTTP POST в фоне.
//--------------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;

namespace AvanpostHub
{
    public sealed class NotificationService : IDisposable
    {
        private const long DedupeRetentionMs = 24L * 60 * 60 * 1000;
        private readonly object _gate = new object();
        private readonly HttpClient _http;
        private readonly Dictionary<string, long> _recent = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        private NotificationSettings _settings = new NotificationSettings();
        private long _matched;
        private long _queued;
        private long _sent;
        private long _failed;
        private long _duplicates;
        private long _lastSentAtMs;
        private string _lastError = "";
        private bool _disposed;

        public NotificationService(HttpMessageHandler handler = null)
        {
            _http = new HttpClient(handler ?? new HttpClientHandler())
            {
                Timeout = TimeSpan.FromSeconds(10),
            };
        }

        public void Configure(NotificationSettings settings)
        {
            var clean = (settings ?? new NotificationSettings()).Sanitized();
            lock (_gate) _settings = clean;
        }

        public NotificationStatus GetStatus()
        {
            lock (_gate)
            {
                return new NotificationStatus
                {
                    Enabled = _settings.Enabled,
                    Configured = _settings.Configured,
                    Matched = _matched,
                    Queued = _queued,
                    Sent = _sent,
                    Failed = _failed,
                    Duplicates = _duplicates,
                    LastSentAtMs = _lastSentAtMs,
                    LastError = _lastError,
                };
            }
        }

        /// <summary>Принимает готовое сообщение и отправляет его в фоне.</summary>
        public NotificationPublishResult Publish(NotificationMessage source)
        {
            NotificationSettings settings;
            NotificationMessage message;
            var prepared = Prepare(source, out message, out settings);
            if (!prepared.Queued) return prepared;

            _ = DeliverAsync(message, settings);
            return prepared;
        }

        /// <summary>Отправка с ожиданием результата для тестов и диагностики.</summary>
        public async Task<NotificationSendResult> SendNowAsync(NotificationMessage source)
        {
            NotificationSettings settings;
            NotificationMessage message;
            var prepared = Prepare(source, out message, out settings);
            if (!prepared.Queued)
                return new NotificationSendResult { Success = false, Error = prepared.Reason };
            return await DeliverAndCountAsync(message, settings).ConfigureAwait(false);
        }

        public async Task<NotificationSendResult> SendTestAsync(NotificationSettings settings)
        {
            var clean = (settings ?? new NotificationSettings()).Sanitized();
            clean.Enabled = true;
            if (!clean.Configured)
                return new NotificationSendResult { Success = false, Error = "Укажите адрес ntfy и топик." };

            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var message = new NotificationMessage
            {
                DedupeKey = "hub-test-" + Guid.NewGuid().ToString("N"),
                Title = "Avanpost Hub · тест",
                Body = "Тестовое уведомление из Avanpost Hub.",
                Tags = new[] { "white_check_mark" },
                Priority = 3,
                OccurredAtMs = now,
                ExpiresAtMs = now + 60000,
            };
            return await DeliverAndCountAsync(message, clean).ConfigureAwait(false);
        }

        private NotificationPublishResult Prepare(
            NotificationMessage source,
            out NotificationMessage message,
            out NotificationSettings settings)
        {
            message = source == null ? new NotificationMessage() : source.Clone();
            message.Title = (message.Title ?? "").Trim();
            message.Body = (message.Body ?? "").Trim();
            message.DedupeKey = (message.DedupeKey ?? "").Trim();
            message.Tags = message.Tags == null ? new string[0] : message.Tags
                .Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()).ToArray();
            message.Priority = message.Priority < 1 ? 3 : (message.Priority > 5 ? 5 : message.Priority);
            if (message.OccurredAtMs <= 0) message.OccurredAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (message.DedupeKey.Length == 0)
                message.DedupeKey = "auto|" + message.Title + "|" + message.Body;

            lock (_gate) settings = _settings.Clone();

            if (!settings.Enabled)
                return Reject("уведомления выключены");
            if (!settings.Configured)
                return Reject("ntfy не настроен");
            if (message.Title.Length == 0 && message.Body.Length == 0)
                return Reject("пустое сообщение");

            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (message.ExpiresAtMs > 0 && message.ExpiresAtMs < now)
                return Reject("сообщение устарело");

            lock (_gate)
            {
                CleanupRecentLocked(now);
                long seenAt;
                if (_recent.TryGetValue(message.DedupeKey, out seenAt))
                {
                    if (now - seenAt <= DedupeRetentionMs)
                    {
                        _duplicates++;
                        return new NotificationPublishResult
                        {
                            Accepted = true,
                            Queued = false,
                            Duplicate = true,
                            Reason = "уже отправлялось",
                            DedupeKey = message.DedupeKey,
                        };
                    }
                    _recent.Remove(message.DedupeKey);
                }

                _recent[message.DedupeKey] = now;
                _matched++;
                _queued++;
            }

            return new NotificationPublishResult
            {
                Accepted = true,
                Queued = true,
                Reason = "в очереди на отправку",
                DedupeKey = message.DedupeKey,
            };
        }

        private static NotificationPublishResult Reject(string reason)
        {
            return new NotificationPublishResult { Accepted = false, Queued = false, Reason = reason };
        }

        private async Task DeliverAsync(NotificationMessage message, NotificationSettings settings)
        {
            var result = await DeliverCoreAsync(message, settings).ConfigureAwait(false);
            ApplyResult(result);
            if (result.Success)
                HubLog.Info("notification sent: " + message.DedupeKey);
            else
                HubLog.Warn("notification failed: " + message.DedupeKey + " — " + (result.Error ?? ""));
        }

        private async Task<NotificationSendResult> DeliverAndCountAsync(
            NotificationMessage message,
            NotificationSettings settings)
        {
            var result = await DeliverCoreAsync(message, settings).ConfigureAwait(false);
            ApplyResult(result);
            return result;
        }

        private void ApplyResult(NotificationSendResult result)
        {
            lock (_gate)
            {
                if (result.Success)
                {
                    _sent++;
                    _lastSentAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    _lastError = "";
                }
                else
                {
                    _failed++;
                    _lastError = result.Error ?? "неизвестная ошибка";
                }
            }
        }

        private async Task<NotificationSendResult> DeliverCoreAsync(
            NotificationMessage message,
            NotificationSettings settings)
        {
            try
            {
                var request = BuildRequest(message, settings);
                using (var response = await _http.SendAsync(request).ConfigureAwait(false))
                {
                    var status = (int)response.StatusCode;
                    if (response.IsSuccessStatusCode)
                        return new NotificationSendResult { Success = true, HttpStatus = status };

                    string body = "";
                    try { body = await response.Content.ReadAsStringAsync().ConfigureAwait(false); }
                    catch { }
                    return new NotificationSendResult
                    {
                        Success = false,
                        HttpStatus = status,
                        Error = "ntfy HTTP " + status + (string.IsNullOrWhiteSpace(body) ? "" : ": " + Trim(body, 240)),
                    };
                }
            }
            catch (Exception ex)
            {
                return new NotificationSendResult
                {
                    Success = false,
                    Error = ex.GetBaseException().Message,
                };
            }
        }

        private static HttpRequestMessage BuildRequest(NotificationMessage message, NotificationSettings settings)
        {
            var topic = Uri.EscapeDataString(settings.NtfyTopic ?? "");
            var url = settings.NtfyBaseUrl.TrimEnd('/') + "/" + topic;
            var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(message.Body ?? "", Encoding.UTF8, "text/plain"),
            };

            request.Headers.TryAddWithoutValidation("Title", AsciiHeader(message.Title));
            request.Headers.TryAddWithoutValidation("Tags", message.Tags.Length == 0 ? "bell" : string.Join(",", message.Tags));
            request.Headers.TryAddWithoutValidation("Priority", message.Priority.ToString(CultureInfo.InvariantCulture));
            if (!string.IsNullOrWhiteSpace(settings.NtfyAccessToken))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.NtfyAccessToken.Trim());
            return request;
        }

        private static string AsciiHeader(string value)
        {
            var s = value ?? "";
            var sb = new StringBuilder(s.Length);
            foreach (var c in s)
                sb.Append(c >= 32 && c <= 126 ? c : '?');
            return sb.ToString();
        }

        private static string Trim(string value, int max)
        {
            var s = (value ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
            return s.Length <= max ? s : s.Substring(0, max);
        }

        private void CleanupRecentLocked(long now)
        {
            if (_recent.Count < 4096) return;
            var old = _recent.Where(x => now - x.Value > DedupeRetentionMs).Select(x => x.Key).ToArray();
            foreach (var key in old) _recent.Remove(key);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { _http.Dispose(); } catch { }
        }
    }
}
