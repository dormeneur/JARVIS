using Windows.Win32.Globalization;

namespace Jarvis.Platform.Text;

/// <summary>
/// Whether a word is ordinary English, from Windows' own spell checker (en-US, then en-GB), so
/// "add to dictionary" only offers names, brands and jargon. Words it can't check count as new.
/// </summary>
public static unsafe class EnglishWords
{
    private static readonly Lazy<ISpellChecker[]> Checkers = new(Create);
    private static readonly Lock Gate = new();

    public static bool IsKnown(string word)
    {
        lock (Gate)
        {
            foreach (var checker in Checkers.Value)
            {
                try
                {
                    IEnumSpellingError errors;
                    fixed (char* text = word)
                    {
                        errors = checker.Check(new Windows.Win32.Foundation.PCWSTR(text));
                    }

                    if (errors.Next(out _).Value == 1)
                    {
                        return true; // S_FALSE: no spelling error, so a real word
                    }
                }
                catch (System.Runtime.InteropServices.COMException)
                {
                    // Unchecked counts as new.
                }
            }

            return false;
        }
    }

    private static ISpellChecker[] Create()
    {
        try
        {
            var factory = (ISpellCheckerFactory)Activator.CreateInstance(Type.GetTypeFromCLSID(typeof(SpellCheckerFactory).GUID)!)!;
            var checkers = new List<ISpellChecker>();
            foreach (var tag in new[] { "en-US", "en-GB" })
            {
                fixed (char* language = tag)
                {
                    var name = new Windows.Win32.Foundation.PCWSTR(language);
                    if (factory.IsSupported(name))
                    {
                        checkers.Add(factory.CreateSpellChecker(name));
                    }
                }
            }

            return [.. checkers];
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            return [];
        }
    }
}
