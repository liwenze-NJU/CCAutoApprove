using CCAutoApprove.Core.Abstractions;

namespace CCAutoApprove.Infrastructure.Windows;

public sealed class WindowsProjectMatcher : IProjectMatcher
{
    public bool IsMatch(string selectedProject, string requestDirectory)
    {
        try
        {
            if (!Path.IsPathFullyQualified(selectedProject) || !Path.IsPathFullyQualified(requestDirectory))
                return false;

            string selected = Path.TrimEndingDirectorySeparator(Path.GetFullPath(selectedProject));
            string request = Path.TrimEndingDirectorySeparator(Path.GetFullPath(requestDirectory));
            string relative = Path.GetRelativePath(selected, request);

            return relative == "." ||
                (!relative.Equals("..", StringComparison.OrdinalIgnoreCase) &&
                 !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) &&
                 !Path.IsPathFullyQualified(relative));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }
}
