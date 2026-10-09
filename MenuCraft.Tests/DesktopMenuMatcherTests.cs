using MenuCraft.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MenuCraft.Tests;

[TestClass]
public sealed class DesktopMenuMatcherTests
{
    private static MenuEntry Entry(string scope = @"Directory\Background", string hive = "HKCU") => new()
    { Name = "Git GUI", Scope = scope, Path = $@"Software\Classes\{scope}\shell\git_gui", Hive = hive, CanToggle = true };

    [TestMethod]
    public void Match_CanonicalVerb_ConnectsActualMenuItem()
    {
        var item = new DesktopMenuItem { Label = "Open Git GUI here", Verb = "git_gui" };
        var result = DesktopMenuMatcher.Match([item], [Entry()]);
        Assert.AreEqual("Open Git GUI here", result[0].Label);
        Assert.IsTrue(result[0].CanHide);
    }
    [TestMethod]
    public void Match_SimilarLabelWithoutVerb_RemainsReadOnly()
    {
        var result = DesktopMenuMatcher.Match([new() { Label = "Git GUI" }], [Entry()]);
        Assert.IsNull(result[0].Registration);
        Assert.IsFalse(result[0].CanHide);
    }
    [TestMethod]
    public void Match_AmbiguousScopes_DoesNotGuess()
    {
        var result = DesktopMenuMatcher.Match([new() { Verb = "git_gui" }], [Entry(), Entry("DesktopBackground")]);
        Assert.IsNull(result[0].Registration);
    }
    [TestMethod]
    public void Match_HiddenUserOverride_DoesNotFallBackToMachineEntry()
    {
        var result = DesktopMenuMatcher.Match([new() { Verb = "git_gui" }], [Entry() with { IsHidden = true }, Entry(hive: "HKLM")]);
        Assert.IsNull(result[0].Registration);
    }
    [TestMethod]
    public void Match_SeparatorAndSubmenu_CannotBeRemoved()
    {
        var result = DesktopMenuMatcher.Match([new() { Verb = "git_gui", IsSeparator = true }, new() { Verb = "git_gui", HasSubmenu = true }], [Entry()]);
        Assert.IsTrue(result.All(i => !i.CanHide));
    }
    [TestMethod]
    public void Match_MenuOrder_PreservesWindowsOrder()
    {
        var rows = new[] { new DesktopMenuItem { Label = "View", HasSubmenu = true }, new DesktopMenuItem { IsSeparator = true }, new DesktopMenuItem { Label = "Open Git GUI here", Verb = "git_gui" } };
        var result = DesktopMenuMatcher.Match(rows, [Entry()]);
        CollectionAssert.AreEqual(rows.Select(i => i.Label).ToArray(), result.Select(i => i.Label).ToArray());
        Assert.IsTrue(result[1].IsSeparator);
    }
}
