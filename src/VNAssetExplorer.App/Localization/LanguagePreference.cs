using System.IO;

namespace VNAssetExplorer.App.Localization;

internal sealed class LanguagePreference(string path)
{
    public LanguagePreference() : this(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "VNAssetExplorer", "language.txt")) { }

    public string? Read()
    {
        try { return File.ReadAllText(path).Trim(); }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    public void Save(string code)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            try
            {
                File.WriteAllText(temporary, code);
                File.Move(temporary, path, overwrite: true);
            }
            finally { File.Delete(temporary); }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
