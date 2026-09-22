using System.IO;

namespace Pulse.App;

/// <summary>
/// UI language preference. Chinese is the product default; English is the
/// alternate. Stored beside the other PulseWin settings. Switching raises
/// <see cref="LanguageChanged"/> so open windows can re-apply strings.
/// </summary>
public static class UiLanguage
{
    public enum Language
    {
        Chinese,
        English,
    }

    public static event Action<Language>? LanguageChanged;

    private static Language _current = Load();

    public static Language Current => _current;

    public static bool IsChinese => _current == Language.Chinese;

    public static void Apply(Language language)
    {
        _current = language;
        Save(language);
        LanguageChanged?.Invoke(language);
    }

    public static string DisplayName(Language language) => language switch
    {
        Language.English => "English",
        _ => "中文",
    };

    private static string PrefsPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PulseWin", "language.txt");

    private static Language Load()
    {
        try
        {
            var file = PrefsPath();
            if (!File.Exists(file)) return Language.Chinese;
            return File.ReadAllText(file).Trim() switch
            {
                "en" or "en-US" or "English" => Language.English,
                _ => Language.Chinese,
            };
        }
        catch (Exception)
        {
            return Language.Chinese;
        }
    }

    private static void Save(Language language)
    {
        try
        {
            var file = PrefsPath();
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, language == Language.English ? "en" : "zh");
        }
        catch (Exception)
        {
        }
    }
}
