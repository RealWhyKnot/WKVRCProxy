using System;

namespace WKVRCProxy.Core.Diagnostics;

public class RequestContext
{
    public string CorrelationId { get; }
    public string OriginalUrl { get; }
    public DateTime CreatedAt { get; }

    private RequestContext(string correlationId, string originalUrl, DateTime createdAt)
    {
        CorrelationId = correlationId;
        OriginalUrl = originalUrl;
        CreatedAt = createdAt;
    }

    public static RequestContext Create(string originalUrl)
    {
        return new RequestContext(
            Guid.NewGuid().ToString("N").Substring(0, 8),
            originalUrl,
            DateTime.Now);
    }

    public RequestContext CreateChild()
    {
        return new RequestContext(CorrelationId, OriginalUrl, CreatedAt);
    }
}
