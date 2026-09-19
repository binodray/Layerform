using Compositor.Editing;
using Xunit;

namespace Compositor.Tests;

public class ProjectWorkspaceTests
{
    [Fact]
    public void UntitledTabsStartAtOneAndIncrement()
    {
        var workspace = new ProjectWorkspace(NullEditorHost.Instance);

        Assert.Equal("Untitled 1", workspace.Current.Title);
        Assert.Equal("Untitled 2", workspace.AddTab(reuseEmpty: false).Title);
        Assert.Equal("Untitled 3", workspace.AddTab(reuseEmpty: false).Title);
    }

    [Fact]
    public void ClosingEveryTabResetsUntitledNumbering()
    {
        var workspace = new ProjectWorkspace(NullEditorHost.Instance);
        workspace.AddTab(reuseEmpty: false);

        foreach (var id in workspace.Tabs.Select(tab => tab.Id).ToList())
            workspace.RemoveTab(id);

        Assert.Single(workspace.Tabs);
        Assert.Equal("Untitled 1", workspace.Current.Title);
        Assert.Equal("Untitled 2", workspace.AddTab(reuseEmpty: false).Title);
    }
}
