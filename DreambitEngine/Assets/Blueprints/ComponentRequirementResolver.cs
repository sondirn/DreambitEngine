using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Dreambit.ECS;

namespace Dreambit;

public static class ComponentRequirementResolver
{
    public static IReadOnlyList<Type> ResolveCreationOrder(
        IEnumerable<Type> roots,
        Func<Type, bool> hasAlready)
    {
        ArgumentNullException.ThrowIfNull(roots);
        ArgumentNullException.ThrowIfNull(hasAlready);

        var order = new List<Type>(8);
        var marks = new Dictionary<Type, Mark>(16);
        var stack = new Stack<Type>();

        void Visit(Type type)
        {
            if (hasAlready(type))
                return;

            if (marks.TryGetValue(type, out var mark))
            {
                if (mark == Mark.Visiting)
                {
                    var cycle = string.Join(
                        " -> ",
                        stack.Reverse().Append(type).Select(x => x.FullName));

                    throw new InvalidOperationException(
                        $"Cycle detected in [Require]: {cycle}");
                }

                return;
            }

            marks[type] = Mark.Visiting;
            stack.Push(type);

            foreach (var requiredType in GetRequireTypes(type))
                Visit(requiredType);

            stack.Pop();
            marks[type] = Mark.Done;
            order.Add(type); // Dependencies were added first.
        }

        foreach (var root in roots)
            Visit(root);

        return order;
    }

    // Preserves the old method's intended meaning: return only dependencies.
    public static IReadOnlyList<Type> ResolveOrder(
        Type root,
        Func<Type, bool> hasAlready)
    {
        var order = ResolveCreationOrder([root], hasAlready).ToList();
        order.Remove(root);
        return order;
    }

    internal static IEnumerable<Type> GetRequireTypes(Type type)
    {
        foreach (var requireAttribute in type.GetCustomAttributes<RequireAttribute>(inherit: true))
        {
            foreach (var requiredType in requireAttribute.RequiredTypes)
                yield return requiredType;
        }
    }

    private enum Mark : byte
    {
        Visiting,
        Done
    }
}
