//--------------------------------------------------------------------------------
// NotificationModels — транспортный контракт Hub.
//
// Hub не знает, какой индикатор и по какому правилу родил сообщение.
// Индикатор присылает готовые Title/Body/Tags и dedupeKey; Hub только
// доставляет это в настроенный канал и защищает от повторов.
//--------------------------------------------------------------------------------

using System;
using System.Collections.Generic;

namespace AvanpostHub
{
    public sealed class NotificationMessage
    {
        public string DedupeKey { get; set; } = "";
        public string Title { get; set; } = "";
        public string Body { get; set; } = "";
        public string[] Tags { get; set; } = new string[0];

        /// <summary>ntfy priority: 1 (min) … 5 (max).</summary>
        public int Priority { get; set; } = 3;
        public long OccurredAtMs { get; set; }
        public long ExpiresAtMs { get; set; }

        public NotificationMessage Clone()
        {
            return new NotificationMessage
            {
                DedupeKey = DedupeKey,
                Title = Title,
                Body = Body,
                Tags = Tags == null ? new string[0] : (string[])Tags.Clone(),
                Priority = Priority,
                OccurredAtMs = OccurredAtMs,
                ExpiresAtMs = ExpiresAtMs,
            };
        }
    }

    public sealed class NotificationSettings
    {
        public bool Enabled { get; set; }
        public string NtfyBaseUrl { get; set; } = "https://ntfy.sh";
        public string NtfyTopic { get; set; } = "";
        public string NtfyAccessToken { get; set; } = "";

        public bool Configured =>
            Enabled &&
            !string.IsNullOrWhiteSpace(NtfyBaseUrl) &&
            !string.IsNullOrWhiteSpace(NtfyTopic);

        public NotificationSettings Sanitized()
        {
            return new NotificationSettings
            {
                Enabled = Enabled,
                NtfyBaseUrl = NormalizeBaseUrl(NtfyBaseUrl),
                NtfyTopic = NormalizeTopic(NtfyTopic),
                NtfyAccessToken = (NtfyAccessToken ?? "").Trim(),
            };
        }

        public NotificationSettings Clone()
        {
            return new NotificationSettings
            {
                Enabled = Enabled,
                NtfyBaseUrl = NtfyBaseUrl,
                NtfyTopic = NtfyTopic,
                NtfyAccessToken = NtfyAccessToken,
            };
        }

        private static string NormalizeTopic(string value)
        {
            var topic = (value ?? "").Trim();
            return topic.Length <= 64 ? topic : topic.Substring(0, 64);
        }

        private static string NormalizeBaseUrl(string value)
        {
            var url = (value ?? "").Trim().TrimEnd('/');
            Uri uri;
            if (!Uri.TryCreate(url, UriKind.Absolute, out uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                return "https://ntfy.sh";
            return url;
        }
    }

    public sealed class NotificationPublishResult
    {
        public bool Accepted { get; set; }
        public bool Queued { get; set; }
        public bool Duplicate { get; set; }
        public string Reason { get; set; }
        public string DedupeKey { get; set; }
    }

    public sealed class NotificationSendResult
    {
        public bool Success { get; set; }
        public int HttpStatus { get; set; }
        public string Error { get; set; }
    }

    public sealed class NotificationStatus
    {
        public bool Enabled { get; set; }
        public bool Configured { get; set; }
        public long Matched { get; set; }
        public long Queued { get; set; }
        public long Sent { get; set; }
        public long Failed { get; set; }
        public long Duplicates { get; set; }
        public long LastSentAtMs { get; set; }
        public string LastError { get; set; } = "";
    }
}
