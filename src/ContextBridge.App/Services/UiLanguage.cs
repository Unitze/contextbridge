using System.Globalization;
using System.Windows;
using System.Windows.Markup;

namespace ContextBridge.App.Services;

/// <summary>Chooses the UI language once at startup (XAML reads strings through x:Static).</summary>
static class UiLanguage
{
    public static readonly IReadOnlyList<string> Supported = new[] { "ja", "en" };

    /// <summary>Native names: shown untranslated so users can always find their language.</summary>
    public static string NativeName(string tag) => tag switch
    {
        "ja" => "日本語",
        "en" => "English",
        _ => tag,
    };

    public static void Apply(string? preference)
    {
        CultureInfo ui = CultureInfo.CurrentUICulture;
        if (!string.IsNullOrEmpty(preference) && Supported.Contains(preference))
        {
            ui = CultureInfo.GetCultureInfo(preference == "ja" ? "ja-JP" : "en-US");
            CultureInfo.CurrentUICulture = ui;
            CultureInfo.DefaultThreadCurrentUICulture = ui;  // background scans/validation run on the pool
        }
        Resources.Strings.Culture = null;  // follow CurrentUICulture
        // xml:lang drives font fallback (Japanese glyph forms) and default number/date formatting in bindings.
        FrameworkElement.LanguageProperty.OverrideMetadata(typeof(FrameworkElement),
            new FrameworkPropertyMetadata(XmlLanguage.GetLanguage(ui.IetfLanguageTag)));
    }
}
