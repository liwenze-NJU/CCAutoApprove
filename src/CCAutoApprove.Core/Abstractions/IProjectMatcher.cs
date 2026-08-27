namespace CCAutoApprove.Core.Abstractions;

public interface IProjectMatcher
{
    bool IsMatch(string selectedProject, string requestDirectory);
}
