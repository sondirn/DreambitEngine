using Dreambit;
using Dreambit.ECS;
using Xunit;

namespace DreambitEngine.Networking.Tests;

public sealed class ComponentRequirementIsolationTests
{
    [Fact]
    public void BlueprintValidationDoesNotInstantiateUnrelatedComponentAttributes()
    {
        var blueprint = new EntityBlueprint
        {
            Name = "attributed",
            Guid = Guid.NewGuid(),
            Components =
            [
                new ComponentBlueprint
                {
                    Type = typeof(AttributedComponent).AssemblyQualifiedName!
                }
            ]
        };

        Assert.Empty(BlueprintValidator.Validate(blueprint));
    }

    [Fact]
    public void RuntimeRequirementCreationDoesNotInstantiateUnrelatedComponentAttributes()
    {
        using var scene = new TestScene();
        var entity = scene.CreateEntity("attributed");

        entity.AttachComponent<AttributedComponent>();

        Assert.NotNull(entity.GetComponent<RequiredComponent>());
    }

    [AttributeUsage(AttributeTargets.Class, Inherited = true)]
    private sealed class ExplodingAttribute : Attribute
    {
        public ExplodingAttribute()
        {
            throw new InvalidOperationException(
                "An unrelated Component attribute must not be instantiated by requirement resolution.");
        }
    }

    [Exploding]
    [Require(typeof(RequiredComponent))]
    private sealed class AttributedComponent : Component
    {
    }

    private sealed class RequiredComponent : Component
    {
    }

    private sealed class TestScene : Scene
    {
        internal override void InitializeInternals()
        {
        }
    }
}
