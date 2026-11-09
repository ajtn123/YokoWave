using YokoWave.Core.Localization;

namespace YokoWave.App.ViewModels;

public sealed record StoredReason(string Code)
{
    public override string ToString() => L10n.Current.TranslateStored(Code, "Reason.");
}
