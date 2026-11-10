using Avalonia.Media.Imaging;
using QRCoder;

namespace YokoWave.App.ViewModels;

public sealed class PairingCodeViewModel : ObservableObject, IDisposable
{
    public string Url { get; private set; } = "";
    public string ControllerId { get; private set; } = "";
    public Bitmap? Image { get; private set; }
    public bool HasCode => Image is not null;

    public void Update(string url, string controllerId = "")
    {
        if (url == Url)
            return;
        Bitmap? image = null;
        if (url.Length > 0)
        {
            byte[] bytes = PngByteQRCodeHelper.GetQRCode(url, QRCodeGenerator.ECCLevel.Q, 4);
            using var stream = new MemoryStream(bytes);
            image = new(stream);
        }
        Image?.Dispose();
        Image = image;
        Url = url;
        ControllerId = controllerId;
        Raise(nameof(Url));
        Raise(nameof(ControllerId));
        Raise(nameof(Image));
        Raise(nameof(HasCode));
    }

    public void Dispose()
    {
        Image?.Dispose();
        Image = null;
    }
}
