using System.Globalization;
using Microsoft.Extensions.Options;
using TradeOps.Application.Interfaces;
using TradeOps.Domain.Enums;

namespace TradeOps.Api.Security;

public sealed class SignalIngressAuthenticationMiddleware(
    RequestDelegate next,
    IOptions<SignalIngressAuthOptions> authOptions,
    IOptions<SignalIngressReplayProtectionOptions> replayOptions,
    IOptions<SignalIngressSigningOptions> signingOptions)
{
    private readonly SignalIngressAuthOptions _authOptions = authOptions.Value;
    private readonly SignalIngressReplayProtectionOptions _replayOptions = replayOptions.Value;
    private readonly SignalIngressSigningOptions _signingOptions = signingOptions.Value;

    public async Task InvokeAsync(
        HttpContext context,
        ISignalIngressReplayStore replayStore,
        ISignalIngressRequestAuditRepository auditRepository)
    {
        if (!IsSignalSubmission(context.Request))
        {
            await next(context);
            return;
        }

        if (_authOptions.Enabled
            && (!TryGetSingleHeader(context.Request, _authOptions.HeaderName, out var suppliedKey)
                || !ApiKeyVerifier.Matches(_authOptions.ApiKey, suppliedKey)))
        {
            context.Response.Headers["WWW-Authenticate"] = "ApiKey";
            await WriteProblemAsync(
                context,
                StatusCodes.Status401Unauthorized,
                "Signal ingress authentication failed.");
            return;
        }

        ReplayMetadata? replayMetadata = null;
        Guid? auditId = null;

        if (_replayOptions.Enabled)
        {
            replayMetadata = await ParseReplayMetadataAsync(context);
            if (replayMetadata is null)
            {
                return;
            }

            var path = GetRequestPath(context.Request);
            auditId = await auditRepository.StartAsync(
                replayMetadata.RequestId,
                replayMetadata.RequestTimestamp,
                replayMetadata.ReceivedAt,
                context.Request.Method.ToUpperInvariant(),
                path,
                context.RequestAborted);

            SignalIngressAuditContext.SetAuditId(
                context,
                auditId.Value);

            var allowedClockSkew = TimeSpan.FromSeconds(
                _replayOptions.AllowedClockSkewSeconds);

            if ((replayMetadata.RequestTimestamp - replayMetadata.ReceivedAt)
                .Duration() > allowedClockSkew)
            {
                await auditRepository.CompleteAsync(
                    auditId.Value,
                    SignalIngressRequestOutcome.TimestampRejected,
                    StatusCodes.Status401Unauthorized,
                    cancellationToken: context.RequestAborted);

                await WriteProblemAsync(
                    context,
                    StatusCodes.Status401Unauthorized,
                    "Signal ingress request timestamp is outside the allowed window.");
                return;
            }
        }

        if (_signingOptions.Enabled)
        {
            if (replayMetadata is null)
            {
                throw new InvalidOperationException(
                    "Signal ingress signing requires replay metadata.");
            }

            var signatureResult = await ValidateSignatureAsync(
                context,
                replayMetadata);

            if (signatureResult != SignatureValidationResult.Valid)
            {
                if (auditId.HasValue)
                {
                    var (outcome, statusCode) = signatureResult switch
                    {
                        SignatureValidationResult.PayloadTooLarge =>
                            (SignalIngressRequestOutcome.PayloadRejected,
                                StatusCodes.Status413PayloadTooLarge),
                        _ =>
                            (SignalIngressRequestOutcome.SignatureRejected,
                                StatusCodes.Status401Unauthorized)
                    };

                    await auditRepository.CompleteAsync(
                        auditId.Value,
                        outcome,
                        statusCode,
                        cancellationToken: context.RequestAborted);
                }

                return;
            }
        }

        if (replayMetadata is not null)
        {
            var expiresAt = replayMetadata.ReceivedAt.AddSeconds(
                _replayOptions.ReceiptRetentionSeconds);

            var registered = await replayStore.TryRegisterAsync(
                replayMetadata.RequestId,
                replayMetadata.RequestTimestamp,
                replayMetadata.ReceivedAt,
                expiresAt,
                context.RequestAborted);

            if (!registered)
            {
                if (auditId.HasValue)
                {
                    await auditRepository.CompleteAsync(
                        auditId.Value,
                        SignalIngressRequestOutcome.ReplayRejected,
                        StatusCodes.Status409Conflict,
                        cancellationToken: context.RequestAborted);
                }

                await WriteProblemAsync(
                    context,
                    StatusCodes.Status409Conflict,
                    "Signal ingress request ID has already been used.");
                return;
            }
        }

        try
        {
            await next(context);

            if (auditId.HasValue)
            {
                await auditRepository.CompleteIfPendingAsync(
                    auditId.Value,
                    MapFallbackOutcome(context.Response.StatusCode),
                    context.Response.StatusCode,
                    context.RequestAborted);
            }
        }
        catch
        {
            if (auditId.HasValue)
            {
                await auditRepository.CompleteIfPendingAsync(
                    auditId.Value,
                    SignalIngressRequestOutcome.Failed,
                    StatusCodes.Status500InternalServerError,
                    CancellationToken.None);
            }

            throw;
        }
    }

    private async Task<ReplayMetadata?> ParseReplayMetadataAsync(
        HttpContext context)
    {
        if (!TryGetSingleHeader(
                context.Request,
                _replayOptions.TimestampHeaderName,
                out var timestampValue)
            || !TryGetSingleHeader(
                context.Request,
                _replayOptions.RequestIdHeaderName,
                out var requestIdValue)
            || !long.TryParse(
                timestampValue,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var unixTimestamp)
            || !Guid.TryParse(requestIdValue, out var requestId))
        {
            await WriteProblemAsync(
                context,
                StatusCodes.Status400BadRequest,
                "Signal ingress replay headers are missing or invalid.");
            return null;
        }

        DateTimeOffset requestTimestamp;
        try
        {
            requestTimestamp = DateTimeOffset.FromUnixTimeSeconds(
                unixTimestamp);
        }
        catch (ArgumentOutOfRangeException)
        {
            await WriteProblemAsync(
                context,
                StatusCodes.Status400BadRequest,
                "Signal ingress replay headers are missing or invalid.");
            return null;
        }

        return new ReplayMetadata(
            unixTimestamp,
            requestId,
            requestTimestamp,
            DateTimeOffset.UtcNow);
    }

    private async Task<SignatureValidationResult> ValidateSignatureAsync(
        HttpContext context,
        ReplayMetadata replayMetadata)
    {
        if (!TryGetSingleHeader(
                context.Request,
                _signingOptions.SignatureHeaderName,
                out var suppliedSignature))
        {
            context.Response.Headers["WWW-Authenticate"] = "HMAC-SHA256";
            await WriteProblemAsync(
                context,
                StatusCodes.Status401Unauthorized,
                "Signal ingress signature validation failed.");
            return SignatureValidationResult.Rejected;
        }

        if (context.Request.ContentLength is > 0
            && context.Request.ContentLength > _signingOptions.MaxBodyBytes)
        {
            await WriteProblemAsync(
                context,
                StatusCodes.Status413PayloadTooLarge,
                "Signal ingress request body exceeds the configured signing limit.");
            return SignatureValidationResult.PayloadTooLarge;
        }

        byte[] body;
        try
        {
            context.Request.EnableBuffering(
                bufferThreshold: Math.Min(
                    _signingOptions.MaxBodyBytes,
                    30 * 1024),
                bufferLimit: _signingOptions.MaxBodyBytes);

            using var bodyBuffer = new MemoryStream();
            await context.Request.Body.CopyToAsync(
                bodyBuffer,
                context.RequestAborted);

            body = bodyBuffer.ToArray();
            context.Request.Body.Position = 0;
        }
        catch (IOException)
        {
            if (context.Request.Body.CanSeek)
            {
                context.Request.Body.Position = 0;
            }

            await WriteProblemAsync(
                context,
                StatusCodes.Status413PayloadTooLarge,
                "Signal ingress request body exceeds the configured signing limit.");
            return SignatureValidationResult.PayloadTooLarge;
        }

        var path = GetRequestPath(context.Request);

        if (!SignalIngressHmacVerifier.Verify(
                _signingOptions.Secret,
                replayMetadata.UnixTimestamp,
                replayMetadata.RequestId,
                context.Request.Method,
                path,
                body,
                suppliedSignature))
        {
            context.Response.Headers["WWW-Authenticate"] = "HMAC-SHA256";
            await WriteProblemAsync(
                context,
                StatusCodes.Status401Unauthorized,
                "Signal ingress signature validation failed.");
            return SignatureValidationResult.Rejected;
        }

        return SignatureValidationResult.Valid;
    }

    private static SignalIngressRequestOutcome MapFallbackOutcome(
        int statusCode) => statusCode switch
        {
            >= 200 and < 300 => SignalIngressRequestOutcome.Accepted,
            StatusCodes.Status409Conflict =>
                SignalIngressRequestOutcome.SignalConflict,
            StatusCodes.Status422UnprocessableEntity =>
                SignalIngressRequestOutcome.RiskRejected,
            >= 500 => SignalIngressRequestOutcome.Failed,
            _ => SignalIngressRequestOutcome.RequestRejected
        };

    private static string GetRequestPath(HttpRequest request) =>
        string.Concat(
            request.PathBase.Value,
            request.Path.Value);

    private static bool TryGetSingleHeader(
        HttpRequest request,
        string headerName,
        out string value)
    {
        value = string.Empty;

        if (!request.Headers.TryGetValue(headerName, out var values)
            || values.Count != 1
            || string.IsNullOrWhiteSpace(values[0]))
        {
            return false;
        }

        value = values[0]!;
        return true;
    }

    private static async Task WriteProblemAsync(
        HttpContext context,
        int statusCode,
        string title)
    {
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/problem+json";

        await context.Response.WriteAsJsonAsync(
            new
            {
                type = $"https://httpstatuses.com/{statusCode}",
                title,
                status = statusCode
            },
            cancellationToken: context.RequestAborted);
    }

    private static bool IsSignalSubmission(HttpRequest request) =>
        HttpMethods.IsPost(request.Method)
        && request.Path.Equals(new PathString("/api/signals"));

    private sealed record ReplayMetadata(
        long UnixTimestamp,
        Guid RequestId,
        DateTimeOffset RequestTimestamp,
        DateTimeOffset ReceivedAt);

    private enum SignatureValidationResult
    {
        Valid,
        Rejected,
        PayloadTooLarge
    }
}
