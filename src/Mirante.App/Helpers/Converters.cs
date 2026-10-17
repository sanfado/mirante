using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using Mirante.App.ViewModels;
using Mirante.Core.Cameras;

namespace Mirante.App.Helpers;

internal sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

internal sealed class ToneToBrushConverter : IValueConverter
{
    private static readonly Brush Good = Frozen(0x22, 0xC5, 0x5E);
    private static readonly Brush Busy = Frozen(0xF5, 0x9E, 0x0B);
    private static readonly Brush Bad = Frozen(0xEF, 0x44, 0x44);
    private static readonly Brush Neutral = Frozen(0x94, 0xA3, 0xB8);

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        StatusTone.Good => Good,
        StatusTone.Busy => Busy,
        StatusTone.Bad => Bad,
        _ => Neutral,
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    private static SolidColorBrush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}

internal static class ValidationText
{
    public static string For(CameraValidationError error) => error switch
    {
        CameraValidationError.NameRequired => "Dê um nome para a câmera.",
        CameraValidationError.NameTooLong => $"O nome pode ter no máximo {CameraValidator.MaxNameLength} caracteres.",
        CameraValidationError.InvalidHost => "Endereço inválido. Use o IP da câmera, por exemplo 192.168.1.100.",
        CameraValidationError.InvalidPort => "Porta inválida (de 1 a 65535).",
        CameraValidationError.InvalidStreamPath => "Caminho do vídeo inválido (por exemplo: stream1).",
        _ => "Dado inválido.",
    };
}
