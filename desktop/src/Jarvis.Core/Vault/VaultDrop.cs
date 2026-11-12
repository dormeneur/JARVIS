using System.Text.RegularExpressions;

namespace Jarvis.Core.Vault;

/// <summary>
/// Files and folders dropped on the notch's Vault target: where each goes in the vault. A resume
/// goes where forms read it (<see cref="FormFiller.ResumeFolder"/>); everything else to
/// <see cref="DocumentsFolder"/>, a folder keeping its own layout. Only what the brain can read is
/// taken, and nothing from a folder called Secrets or a hidden one.
/// </summary>
public static partial class VaultDrop
{
    public const string DocumentsFolder = "Profile/documents";

    /// <summary>What the brain indexes (brain/app/services/document_loader.py SUPPORTED_EXTENSIONS).</summary>
    public static readonly IReadOnlySet<string> Readable = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".md", ".txt", ".pdf", ".json", ".csv", ".docx" };

    public const long MaxFileBytes = 25L * 1024 * 1024;

    public const int MaxFiles = 200;

    /// <summary>The uploads to make (local file → vault path), and the names left out.</summary>
    public sealed record Plan(IReadOnlyList<(string Local, string VaultPath)> Uploads, IReadOnlyList<string> Skipped);

    public static Plan For(IEnumerable<string> dropped)
    {
        ArgumentNullException.ThrowIfNull(dropped);
        var uploads = new List<(string, string)>();
        var skipped = new List<string>();
        foreach (var path in dropped.Where(Path.IsPathFullyQualified))
        {
            if (Directory.Exists(path))
            {
                var root = new DirectoryInfo(path);
                if (Excluded(root.Name))
                {
                    skipped.Add(root.Name);
                    continue;
                }

                foreach (var file in root.EnumerateFiles("*", SearchOption.AllDirectories))
                {
                    var relative = Path.GetRelativePath(root.FullName, file.FullName).Replace('\\', '/');
                    if (relative.Split('/').Any(Excluded))
                    {
                        continue; // hidden or secret: left out without a word, like the brain does
                    }

                    Take(file, $"{DocumentsFolder}/{root.Name}/{relative}");
                }
            }
            else if (File.Exists(path))
            {
                var file = new FileInfo(path);
                Take(file, LooksLikeResume(file.Name) ? $"{FormFiller.ResumeFolder}/{file.Name}" : $"{DocumentsFolder}/{file.Name}");
            }
        }

        return new Plan(uploads, skipped);

        void Take(FileInfo file, string vaultPath)
        {
            if (!Readable.Contains(file.Extension) || file.Length > MaxFileBytes || uploads.Count >= MaxFiles)
            {
                skipped.Add(file.Name);
            }
            else
            {
                uploads.Add((file.FullName, vaultPath));
            }
        }
    }

    /// <summary>"Resume", "résumé" or "CV" as a word in the name (Sam_CV_2026.pdf, resume-final.docx).</summary>
    public static bool LooksLikeResume(string fileName) => ResumeWord().IsMatch(Path.GetFileNameWithoutExtension(fileName));

    private static bool Excluded(string segment)
        => segment.StartsWith('.') || segment.Equals("Secrets", StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"(^|[^a-z])(resume|résumé|cv)([^a-z]|$)", RegexOptions.IgnoreCase)]
    private static partial Regex ResumeWord();
}
