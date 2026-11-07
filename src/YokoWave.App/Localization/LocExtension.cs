using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Markup.Xaml;
using YokoWave.Core.Localization;

namespace YokoWave.App.Localization;

public sealed class LocExtension(string key) : MarkupExtension
{
    public override object ProvideValue(IServiceProvider serviceProvider) => new Binding(nameof(JsonLocalizer.Language))
    {
        Source = L10n.Current,
        Mode = BindingMode.OneWay,
        Converter = new FuncValueConverter<string?, string>(_ => L10n.Get(key))
    };
}
