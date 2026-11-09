using System.Text;
using Xunit;
using YokoWave.Core.Localization;

namespace YokoWave.Tests;

public sealed class LocalizationTests
{
    [Theory]
    [InlineData("en")]
    [InlineData("zh-CN")]
    public void LocalizedExceptionsKeepTheirCauseAndTranslateWhenLanguageChanges(string language)
    {
        string previous = L10n.Current.Language;
        try
        {
            L10n.Current.SetLanguage(language);
            var cause = new ArgumentException("Original backend failure");
            Exception[] errors = [new LocalizedArgumentException("Error.AudioSettings", cause),
                new LocalizedOperationException("Error.AudioInputFailed", cause), new LocalizedIOException("Error.BluetoothWrite", cause),
                new LocalizedAggregateException("Error.StopUnconfirmed", [cause])];
            foreach (var error in errors)
            {
                Assert.Same(cause, error.InnerException);
                Assert.Equal(((ILocalizedError)error).LocalizedMessage.Value, error.Message);
                Assert.Contains(error.Message, error.ToString());
            }
            string[] messages = errors.Select(error => error.Message).ToArray();
            L10n.Current.SetLanguage(language == "en" ? "zh-CN" : "en");
            for (int index = 0; index < errors.Length; index++)
                Assert.NotEqual(messages[index], errors[index].Message);
        }
        finally
        {
            L10n.Current.SetLanguage(previous);
        }
    }

    [Fact]
    public void CatalogsHaveMatchingKeysAndCompatibleFormatArguments()
    {
        var localizer = new JsonLocalizer("en");
        Assert.NotEmpty(localizer.English);
        Assert.Equal(localizer.English.Keys.Order(), localizer.Chinese.Keys.Order());
        foreach (var (key, english) in localizer.English)
        {
            Assert.False(string.IsNullOrWhiteSpace(english), key);
            Assert.False(string.IsNullOrWhiteSpace(localizer.Chinese[key]), key);
            Assert.Equal(CompositeFormat.Parse(english).MinimumArgumentCount,
                CompositeFormat.Parse(localizer.Chinese[key]).MinimumArgumentCount);
        }
    }

    [Theory]
    [InlineData("zh-CN", "zh-CN")]
    [InlineData("zh-Hans", "zh-CN")]
    [InlineData("en-US", "en")]
    [InlineData("de-DE", "en")]
    [InlineData(null, "en")]
    public void LanguagesResolveToSupportedCatalogs(string? requested, string expected) =>
        Assert.Equal(expected, JsonLocalizer.NormalizeLanguage(requested));

    [Fact]
    public void MissingTranslationFallsBackToEnglishAndThenVisibleKey()
    {
        var localizer = new JsonLocalizer(new Dictionary<string, string> { ["Test.Message"] = "Value {0}" },
            new Dictionary<string, string>(), "zh-CN");
        Assert.Equal("Value 12", localizer.Format("Test.Message", 12));
        Assert.Equal("[Missing.Key]", localizer.Get("Missing.Key"));
    }

    [Fact]
    public void SwitchingLanguageNotifiesBindingsAndTranslatesLegacyReasons()
    {
        var localizer = new JsonLocalizer("en");
        var notifications = new List<string?>();
        localizer.PropertyChanged += (_, e) => notifications.Add(e.PropertyName);
        localizer.SetLanguage("zh-CN");
        Assert.Equal("连接", localizer["Device.Connect"]);
        Assert.Single(notifications, name => name == "Item[]");
        localizer.SetLanguage("zh-CN");
        Assert.Single(notifications, name => name == "Item[]");
        localizer.SetLanguage("en");
        Assert.Equal("Time limit reached", localizer.TranslateStored("定时结束", "Reason."));
        Assert.Equal("Custom reason", localizer.TranslateStored("Custom reason", "Reason."));
        Assert.Equal("Wave.Sine.Name", localizer.TranslateStored("Wave.Sine.Name", "Reason."));
    }
}
