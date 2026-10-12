using MRemoteMac.Models;
using MRemoteMac.ViewModels;
using Xunit;

namespace MRemoteMac.Tests;

public class TreeMoveTests
{
    private static (MainViewModel vm, ContainerInfo a, ContainerInfo b, ConnectionInfo a1, ConnectionInfo a2, ConnectionInfo b1) Build()
    {
        var vm = new MainViewModel(new MRemoteMac.Services.AppSettings());
        var a = new ContainerInfo { Name = "A" };
        var b = new ContainerInfo { Name = "B" };
        var a1 = new ConnectionInfo { Name = "A1" };
        var a2 = new ConnectionInfo { Name = "A2" };
        var b1 = new ConnectionInfo { Name = "B1" };
        vm.Root.AddChild(a);
        vm.Root.AddChild(b);
        a.AddChild(a1);
        a.AddChild(a2);
        b.AddChild(b1);
        return (vm, a, b, a1, a2, b1);
    }

    [Fact]
    public void MovesBetweenFolders()
    {
        var (vm, a, b, a1, _, b1) = Build();
        Assert.True(vm.MoveTo(a1, b, 0));
        Assert.Same(b, a1.Parent);
        Assert.Equal(new[] { "A1", "B1" }, b.Children.Select(c => c.Name));
        Assert.Equal(new[] { "A2" }, a.Children.Select(c => c.Name));
        Assert.True(vm.IsDirty);
        Assert.True(b.IsExpanded);
        Assert.Same(b, b1.Parent);
    }

    [Fact]
    public void ReordersWithinAFolderUsingPreRemovalIndexes()
    {
        var (vm, a, _, a1, a2, _) = Build();
        Assert.False(vm.MoveTo(a1, a, 0));   // already first: nothing to do
        Assert.False(vm.MoveTo(a1, a, 1));   // "before A2" is where it already is
        Assert.True(vm.MoveTo(a1, a, 2));    // after A2
        Assert.Equal(new[] { "A2", "A1" }, a.Children.Select(c => c.Name));
        Assert.True(vm.MoveTo(a1, a, 0));
        Assert.Equal(new[] { "A1", "A2" }, a.Children.Select(c => c.Name));
        Assert.Same(a, a2.Parent);
    }

    [Fact]
    public void RefusesItselfItsSubtreeAndTheRoot()
    {
        var (vm, a, b, a1, _, _) = Build();
        var sub = new ContainerInfo { Name = "Sub" };
        a.AddChild(sub);
        Assert.False(MainViewModel.CanMoveTo(a, a));
        Assert.False(MainViewModel.CanMoveTo(a, sub));
        Assert.False(MainViewModel.CanMoveTo(vm.Root, b));
        Assert.True(MainViewModel.CanMoveTo(sub, b));
        Assert.True(MainViewModel.CanMoveTo(a1, vm.Root));
        Assert.False(vm.MoveTo(a, sub, 0));
        Assert.Same(vm.Root, a.Parent);
    }

    [Fact]
    public void MovesAFolderToTheTopLevel()
    {
        var (vm, a, b, _, _, _) = Build();
        var sub = new ContainerInfo { Name = "Sub" };
        a.AddChild(sub);
        Assert.True(vm.MoveTo(sub, vm.Root, 1));
        Assert.Equal(new[] { "A", "Sub", "B" }, vm.Root.Children.Select(c => c.Name));
        Assert.Same(vm.Root, sub.Parent);
        Assert.Same(vm.Root, b.Parent);
    }
}
