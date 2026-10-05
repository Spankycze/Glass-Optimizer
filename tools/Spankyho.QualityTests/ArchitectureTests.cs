using System.Reflection;
using ArchUnitNET.Domain;
using ArchUnitNET.Loader;
using ArchUnitNET.xUnit;
using static ArchUnitNET.Fluent.ArchRuleDefinition;

namespace Spankyho.QualityTests;

public sealed class ArchitectureTests
{
    private static readonly Architecture Architecture = new ArchLoader()
        .LoadAssemblies(System.Reflection.Assembly.Load("SpankyhoOptimizer.Core"))
        .Build();

    [Fact]
    public void Services_must_not_depend_on_WinUI()
    {
        var services = Types().That().ResideInNamespace("SpankyhoOptimizer.Services");
        var winUi = Types().That().ResideInNamespace("Microsoft.UI.Xaml");
        Types().That().Are(services).Should().NotDependOnAny(winUi)
            .Because("backend services must stay UI-agnostic")
            .Check(Architecture);
    }

    [Fact]
    public void Service_classes_should_live_in_services_namespace()
    {
        Classes().That().HaveNameEndingWith("Service")
            .Should().ResideInNamespace("SpankyhoOptimizer.Services")
            .Because("service layer ownership must stay explicit")
            .Check(Architecture);
    }
}
