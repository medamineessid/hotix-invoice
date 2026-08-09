using System.IO;
using System.Threading;
using System.Windows.Media.Imaging;
using Xunit;

namespace Hotix.InvoiceClient.Tests;

/// <summary>Proves the WPF rendering step can decode PNG bytes produced by
/// the server /preview endpoint, using the exact same BitmapImage
/// construction as LoadPreviewImageAsync (CacheOption.OnLoad + MemoryStream +
/// Freeze). BitmapImage requires an STA thread, so each check runs on a
/// dedicated STA thread — the same guarantee the UI thread provides at
/// runtime. The sample file (preview_sample.png, copied to the test output
/// by the csproj) is a real PNG derived from the synthetic invoice image and
/// represents the byte shape /preview returns for both raw images and
/// PDF-rendered pages.</summary>
public sealed class BitmapDecodeSmokeTest
{
    private static T RunOnSta<T>(Func<T> body)
    {
        T? result = default;
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { result = body(); }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error != null) throw error;
        return result!;
    }

    private static BitmapImage Decode(byte[] bytes)
    {
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.StreamSource = new MemoryStream(bytes);
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    private static byte[] LoadSample() =>
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "preview_sample.png"));

    [Fact]
    public void PreviewBytes_DecodeInWpfBitmapImage_OnStaThread()
    {
        byte[] bytes = LoadSample();

        var bitmap = RunOnSta(() => Decode(bytes));
        Assert.True(bitmap.PixelWidth > 0 && bitmap.PixelHeight > 0, "decoded image must have real dimensions");
        Assert.Equal(200, bitmap.PixelWidth);
        Assert.Equal(125, bitmap.PixelHeight);
        Assert.Equal(96, bitmap.DpiX);
        Assert.True(bitmap.IsFrozen, "preview bitmap must be frozen before binding");
    }

    [Fact]
    public void RawImageBytes_DecodeInWpfBitmapImage()
    {
        byte[] img = LoadSample();
        var bitmap = RunOnSta(() => Decode(img));
        Assert.NotNull(bitmap);
        Assert.False(bitmap.IsDownloading);
    }
}
