using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;
using Hotix.InvoiceClient.ViewModels;

namespace Hotix.InvoiceClient;

/// <summary>
/// The single point of translation between a caught .NET exception and the
/// user-facing message shown in the UI (banners, error rows, dialogs).
///
/// Rationale: several historical bugs displayed raw, .NET-localized exception
/// text directly to the user (e.g. "Aucune connexion n'a pu être établie car
/// l'ordinateur cible l'a expressément refusée. (127.0.0.1:8000)"). Every UI
/// error path must go through <see cref="ToUserMessage"/> so that:
///   • known exception types map to existing translation keys, and
///   • the fallback for any unknown type is a generic translated message that
///     contains the exception TYPE NAME only — never the raw .NET message.
///
/// The one deliberate exception to "never ex.Message" is the passthrough for
/// <see cref="CloudApiException"/> / <see cref="CloudQuotaExceededException"/>:
/// those wrapper types are ALWAYS constructed with a translated FRAME at the
/// throw site (e.g. "Gemini API error: 429 — …"). Note that the frame's {1}
/// argument may embed raw API response text — that is pre-existing behavior,
/// preserved here, not a new leak.
/// </summary>
public static class ErrorMessageTranslator
{
    /// <summary>Maps an exception to a user-facing, translated message. Never
    /// returns the raw .NET exception text for unrecognized types.</summary>
    /// <param name="ex">The caught exception.</param>
    /// <param name="ocrServerContext">True (default) when the failure came from
    /// a call to the local OCR server: a connection-refused maps to the
    /// OCR-server-specific key. Pass false from cloud-API paths (Gemini/Grok)
    /// so a refused connection there falls through to the generic fallback
    /// instead of wrongly blaming the local OCR server.</param>
    public static string ToUserMessage(Exception ex, bool ocrServerContext = true)
    {
        ArgumentNullException.ThrowIfNull(ex);

        // Already-translated, user-safe wrappers (see class doc) — pass through.
        if (ex is CloudApiException or CloudQuotaExceededException)
            return ex.Message;

        // Local OCR server extraction errors → friendly HTTP-status keys.
        if (ex is InvoiceExtractionException ive)
            return MapOcrServerError(ive);

        // Connection refused → the local OCR server is not reachable.
        // (Cloud-API paths pass ocrServerContext: false to skip this branch.)
        if (ocrServerContext && IsConnectionRefused(ex))
            return TranslationSource.Fmt("ErrorServerRefused", App.ServerLogPath);

        // DNS resolution failure (host not found / no data).
        if (HasSocketError(ex, SocketError.HostNotFound) || HasSocketError(ex, SocketError.NoData))
            return TranslationSource.Get("ErrorHostNotFound");

        // Timeouts.
        if (ex is TaskCanceledException or TimeoutException || HasSocketError(ex, SocketError.TimedOut))
            return TranslationSource.Get("ErrorRequestTimeout");

        // Malformed JSON from a remote service.
        if (ex is JsonException)
            return TranslationSource.Get("ErrorJsonParse");

        // EnsureServerReadyAsync raises InvalidOperationException whose message
        // is already translated ("Failed to start server: …" / log path) — keep
        // that translated text instead of replacing it with a generic fallback.
        if (ex is InvalidOperationException && IsTranslatedServerStartupMessage(ex.Message))
            return ex.Message;

        // Generic fallback — exception type name only, never the raw message.
        return TranslationSource.Fmt("ErrorUnexpected", ex.GetType().Name);
    }

    /// <summary>True when the exception (or its inner chain) wraps a
    /// SocketException with the given error code.</summary>
    private static bool HasSocketError(Exception ex, SocketError code)
    {
        for (Exception? current = ex; current != null; current = current.InnerException)
        {
            if (current is SocketException se && se.SocketErrorCode == code)
                return true;
        }
        return false;
    }

    private static bool IsConnectionRefused(Exception ex)
        => HasSocketError(ex, SocketError.ConnectionRefused);

    private static bool IsTranslatedServerStartupMessage(string message)
        => message.Contains(App.ServerLogPath, StringComparison.Ordinal)
           || message.StartsWith(TranslationSource.Get("ServerStartFailPrefix"), StringComparison.Ordinal);

    /// <summary>Maps an HTTP error returned by the local OCR server to a
    /// friendly, translated key (moved here from MainViewModel.MapErrorMessage
    /// so every error path shares a single mapping).</summary>
    private static string MapOcrServerError(InvoiceExtractionException ex)
    {
        if (ex.StatusCode == System.Net.HttpStatusCode.UnprocessableEntity)
            return TranslationSource.Get("ErrorOcrFormat");

        if (ex.StatusCode == System.Net.HttpStatusCode.InternalServerError)
        {
            if (ex.ResponseBody.Contains("poppler", StringComparison.OrdinalIgnoreCase)
                || ex.ResponseBody.Contains("pdfinfo", StringComparison.OrdinalIgnoreCase)
                || ex.ResponseBody.Contains("pdftoppm", StringComparison.OrdinalIgnoreCase))
                return TranslationSource.Get("ErrorOcrPoppler");

            if (ex.ResponseBody.Contains("OcrEngineError", StringComparison.OrdinalIgnoreCase)
                || ex.ResponseBody.Contains("PaddleOCR", StringComparison.OrdinalIgnoreCase))
                return TranslationSource.Get("ErrorOcrEngine");

            return TranslationSource.Get("ErrorOcrInternal");
        }

        return TranslationSource.Fmt("ErrorOcrHttp", (int)ex.StatusCode);
    }
}
