using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;
using Hotix.InvoiceClient;
using Hotix.InvoiceClient.ViewModels;
using Xunit;

namespace Hotix.InvoiceClient.Tests;

/// <summary>
/// Behavioral tests for the single exception→message translation point.
/// The core invariant asserted everywhere: the user-visible text NEVER
/// contains the raw .NET exception message (the class of bug that showed
/// "Aucune connexion n'a pu être établie car l'ordinateur cible l'a
/// expressément refusée." straight to the user).
/// </summary>
public sealed class ErrorMessageTranslatorTests
{
    public ErrorMessageTranslatorTests()
    {
        // Deterministic assertions: use the English resource file.
        TranslationSource.Instance.CurrentCulture = "en";
    }

    private const string RawMarker = "MY_RAW_NET_MESSAGE_MARKER";

    private static HttpRequestException RefusedConnection()
        => new(RawMarker, new SocketException((int)SocketError.ConnectionRefused));

    private static HttpRequestException HostNotFoundConnection()
        => new(RawMarker, new SocketException((int)SocketError.HostNotFound));

    private static HttpRequestException TimedOutConnection()
        => new(RawMarker, new SocketException((int)SocketError.TimedOut));

    // ── Known types → dedicated keys, raw message never leaked ────────────

    [Fact]
    public void ConnectionRefused_MapsToErrorServerRefused_AndHidesRawMessage()
    {
        string msg = ErrorMessageTranslator.ToUserMessage(RefusedConnection());
        Assert.Contains("127.0.0.1:8000", msg);          // ErrorServerRefused key
        Assert.DoesNotContain(RawMarker, msg);           // never the raw .NET text
    }

    [Fact]
    public void DnsHostNotFound_MapsToErrorHostNotFound_AndHidesRawMessage()
    {
        string msg = ErrorMessageTranslator.ToUserMessage(HostNotFoundConnection());
        Assert.Contains("DNS", msg, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(RawMarker, msg);
    }

    [Fact]
    public void Timeout_MapsToErrorRequestTimeout_AndHidesRawMessage()
    {
        string msg = ErrorMessageTranslator.ToUserMessage(new TaskCanceledException(RawMarker));
        Assert.Contains("timed out", msg, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(RawMarker, msg);

        string msg2 = ErrorMessageTranslator.ToUserMessage(new TimeoutException(RawMarker));
        Assert.Contains("timed out", msg2, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(RawMarker, msg2);

        string msg3 = ErrorMessageTranslator.ToUserMessage(TimedOutConnection());
        Assert.Contains("timed out", msg3, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(RawMarker, msg3);
    }

    [Fact]
    public void JsonException_MapsToErrorJsonParse_AndHidesRawMessage()
    {
        string msg = ErrorMessageTranslator.ToUserMessage(new JsonException(RawMarker));
        Assert.Contains("invalid", msg, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(RawMarker, msg);
    }

    [Fact]
    public void UnknownException_MapsToGenericFallback_WithTypeNameOnly()
    {
        string msg = ErrorMessageTranslator.ToUserMessage(new InvalidOperationException(RawMarker));
        Assert.Contains("InvalidOperationException", msg);
        Assert.DoesNotContain(RawMarker, msg);
    }

    [Fact]
    public void ConnectionRefused_WithCloudContext_DoesNotBlameLocalOcrServer()
    {
        // Cloud-API paths pass ocrServerContext: false — a refused connection
        // there must NOT show the OCR-server-specific key, only a generic one.
        string msg = ErrorMessageTranslator.ToUserMessage(RefusedConnection(), ocrServerContext: false);
        Assert.DoesNotContain("127.0.0.1:8000", msg);
        Assert.DoesNotContain(RawMarker, msg);
    }

    // ── Already-translated wrappers pass through unchanged ────────────────

    [Fact]
    public void CloudApiException_PassesTranslatedMessageThrough()
    {
        const string translated = "Gemini API error: 429 — quota exceeded";
        string msg = ErrorMessageTranslator.ToUserMessage(new CloudApiException(translated, HttpStatusCode.TooManyRequests));
        Assert.Equal(translated, msg);
    }

    [Fact]
    public void CloudQuotaExceededException_PassesTranslatedMessageThrough()
    {
        const string translated = "Gemini API quota exceeded (429)";
        string msg = ErrorMessageTranslator.ToUserMessage(new CloudQuotaExceededException(translated, HttpStatusCode.TooManyRequests));
        Assert.Equal(translated, msg);
    }

    // ── OCR server HTTP errors → friendly keys ────────────────────────────

    [Fact]
    public void OcrServerUnprocessableEntity_MapsToErrorOcrFormat()
    {
        var ex = new InvoiceExtractionException(HttpStatusCode.UnprocessableEntity, "not a pdf");
        Assert.Equal("Unsupported format", ErrorMessageTranslator.ToUserMessage(ex));
    }

    [Fact]
    public void OcrServer500WithPoppler_MapsToErrorOcrPoppler()
    {
        var ex = new InvoiceExtractionException(HttpStatusCode.InternalServerError, "poppler/pdfinfo missing");
        Assert.Equal("Poppler missing — PDF not supported (see README)", ErrorMessageTranslator.ToUserMessage(ex));
    }

    [Fact]
    public void OcrServer500WithOcrEngineError_MapsToErrorOcrEngine()
    {
        var ex = new InvoiceExtractionException(HttpStatusCode.InternalServerError, "OcrEngineError: paddle failed");
        Assert.Equal("OCR error — Check installation", ErrorMessageTranslator.ToUserMessage(ex));
    }

    [Fact]
    public void OcrServer500Generic_MapsToErrorOcrInternal()
    {
        var ex = new InvoiceExtractionException(HttpStatusCode.InternalServerError, "something else");
        Assert.Equal("Internal OCR server error", ErrorMessageTranslator.ToUserMessage(ex));
    }

    [Fact]
    public void OcrServerOtherStatus_MapsToErrorOcrHttp()
    {
        var ex = new InvoiceExtractionException(HttpStatusCode.ServiceUnavailable, "down");
        Assert.Equal("OCR server error (HTTP 503)", ErrorMessageTranslator.ToUserMessage(ex));
    }

    // ── Translated server-startup messages keep their text ────────────────

    [Fact]
    public void ServerStartupPrefixMessage_PassesThrough()
    {
        var ex = new InvalidOperationException(TranslationSource.Get("ServerStartFailPrefix") + "HttpRequestException");
        string msg = ErrorMessageTranslator.ToUserMessage(ex);
        Assert.StartsWith(TranslationSource.Get("ServerStartFailPrefix"), msg);
    }

    [Fact]
    public void NullException_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => ErrorMessageTranslator.ToUserMessage(null!));
    }
}
