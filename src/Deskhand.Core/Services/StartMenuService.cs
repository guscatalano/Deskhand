namespace Deskhand.Core.Services;

public record StartMenuAppDto(string Name, string Path, string Folder);

/// <summary>
/// Enumerates the Start Menu programs (the <c>.lnk</c>/<c>.url</c> shortcuts under the all-users and
/// per-user Start Menu\Programs trees). Launch one by passing its <see cref="StartMenuAppDto.Path"/> to
/// <c>LaunchProcess</c> (ShellExecute resolves the shortcut). Note: UWP/Store apps aren't shortcuts and
/// aren't included here — enumerating those needs the Shell AppsFolder (a possible follow-up).
/// </summary>
public static class StartMenuService
{
    public static IReadOnlyList<StartMenuAppDto> List()
    {
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
        };
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<StartMenuAppDto>();
        foreach (var root in roots)
        {
            if (string.IsNullOrEmpty(root)) continue;
            var programs = Path.Combine(root, "Programs");
            if (!Directory.Exists(programs)) continue;
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(programs, "*", SearchOption.AllDirectories); }
            catch { continue; }
            foreach (var file in files)
            {
                var ext = Path.GetExtension(file).ToLowerInvariant();
                if (ext != ".lnk" && ext != ".url") continue;
                var name = Path.GetFileNameWithoutExtension(file);
                var dir = Path.GetDirectoryName(file)!;
                var folder = dir.Length > programs.Length ? dir[(programs.Length + 1)..] : "";
                if (!seen.Add(name + "|" + folder)) continue;
                list.Add(new StartMenuAppDto(name, file, folder));
            }
        }
        return list.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }
}
