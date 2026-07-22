using System.Reflection;
using InfiniTranseon.App.Presentation.ViewModels;

namespace InfiniTranseon.App.Tests;

public sealed class ViewModelArchitectureTests
{
    private static readonly string[] ForbiddenNamespacePrefixes =
    [
        "Microsoft.UI",
        "Microsoft.Graphics",
        "Windows.UI.Xaml",
        "InfiniTranseon.Engine",
        "InfiniTranseon.Core",
    ];

    private static IReadOnlyList<Type> ViewModels { get; } =
        typeof(SetupWizardViewModel).Assembly
            .GetTypes()
            .Where(type => type is { IsClass: true, IsAbstract: false } && type.Name.EndsWith("ViewModel", StringComparison.Ordinal))
            .ToArray();

    [Fact]
    public void View_models_are_discovered()
    {
        Assert.NotEmpty(ViewModels);
        Assert.Contains(typeof(ProfileCenterViewModel), ViewModels);
        Assert.Contains(typeof(SetupWizardViewModel), ViewModels);
    }

    [Fact]
    public void No_view_model_references_ui_or_native_engine_types()
    {
        List<string> violations = [];

        foreach (Type viewModel in ViewModels)
        {
            foreach (Type referenced in SurfaceTypes(viewModel))
            {
                string? ns = referenced.Namespace;
                if (ns is null)
                {
                    continue;
                }

                if (ForbiddenNamespacePrefixes.Any(prefix =>
                    ns.StartsWith(prefix, StringComparison.Ordinal)))
                {
                    violations.Add($"{viewModel.Name} references {referenced.FullName}");
                }
            }
        }

        Assert.True(violations.Count == 0, string.Join(Environment.NewLine, violations));
    }

    // Public and non-public surface types a view model exposes or consumes.
    private static IEnumerable<Type> SurfaceTypes(Type viewModel)
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic |
            BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

        foreach (PropertyInfo property in viewModel.GetProperties(flags))
        {
            foreach (Type type in Expand(property.PropertyType))
            {
                yield return type;
            }
        }

        foreach (FieldInfo field in viewModel.GetFields(flags))
        {
            foreach (Type type in Expand(field.FieldType))
            {
                yield return type;
            }
        }

        foreach (ConstructorInfo constructor in viewModel.GetConstructors(flags))
        {
            foreach (ParameterInfo parameter in constructor.GetParameters())
            {
                foreach (Type type in Expand(parameter.ParameterType))
                {
                    yield return type;
                }
            }
        }

        foreach (MethodInfo method in viewModel.GetMethods(flags))
        {
            foreach (Type type in Expand(method.ReturnType))
            {
                yield return type;
            }

            foreach (ParameterInfo parameter in method.GetParameters())
            {
                foreach (Type type in Expand(parameter.ParameterType))
                {
                    yield return type;
                }
            }
        }
    }

    private static IEnumerable<Type> Expand(Type type)
    {
        if (type.IsByRef || type.IsArray || type.IsPointer)
        {
            Type? element = type.GetElementType();
            if (element is not null)
            {
                foreach (Type inner in Expand(element))
                {
                    yield return inner;
                }
            }

            yield break;
        }

        yield return type;

        if (type.IsGenericType)
        {
            foreach (Type argument in type.GetGenericArguments())
            {
                foreach (Type inner in Expand(argument))
                {
                    yield return inner;
                }
            }
        }
    }
}
