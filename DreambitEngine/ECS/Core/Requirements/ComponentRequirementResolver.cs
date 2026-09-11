using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace Dreambit.ECS;

public static class ComponentRequirementResolver
{
    public static IReadOnlyList<Type> ResolveCreationOrder(
        IEnumerable<Type> roots,
        Func<Type, bool> hasAlready)
    {
        ArgumentNullException.ThrowIfNull(roots);
        ArgumentNullException.ThrowIfNull(hasAlready);

        var rootTypes =
            roots
                .Where(static type => type != null)
                .Distinct()
                .ToList();

        var order =
            new List<Type>(8);

        var marks =
            new Dictionary<Type, Mark>(16);

        var stack =
            new Stack<Type>();

        Type ResolveDeclaredProvider(
            Type requiredType)
        {
            // Prefer an explicitly declared exact type.
            for (var i = 0;
                 i < rootTypes.Count;
                 i++)
            {
                if (rootTypes[i] == requiredType)
                    return requiredType;
            }

            // Otherwise a declared derived component can satisfy
            // the base requirement.
            for (var i = 0;
                 i < rootTypes.Count;
                 i++)
            {
                var rootType =
                    rootTypes[i];

                if (requiredType.IsAssignableFrom(
                        rootType))
                {
                    return rootType;
                }
            }

            return requiredType;
        }

        void Visit(Type type)
        {
            if (hasAlready(type))
                return;

            if (marks.TryGetValue(
                    type,
                    out var mark))
            {
                if (mark == Mark.Visiting)
                {
                    var cycle =
                        string.Join(
                            " -> ",
                            stack
                                .Reverse()
                                .Append(type)
                                .Select(
                                    static current =>
                                        current.FullName));

                    throw new InvalidOperationException(
                        $"Cycle detected in [Require]: {cycle}");
                }

                return;
            }

            marks[type] =
                Mark.Visiting;

            stack.Push(type);

            foreach (var requiredType in
                     GetRequireTypes(type))
            {
                var providerType =
                    ResolveDeclaredProvider(
                        requiredType);

                Visit(providerType);
            }

            stack.Pop();

            marks[type] =
                Mark.Done;

            order.Add(type);
        }

        for (var i = 0;
             i < rootTypes.Count;
             i++)
        {
            Visit(rootTypes[i]);
        }

        return order;
    }

    public static IReadOnlyList<Type> ResolveOrder(
        Type root,
        Func<Type, bool> hasAlready)
    {
        var order =
            ResolveCreationOrder(
                    [root],
                    hasAlready)
                .ToList();

        order.Remove(root);

        return order;
    }

    internal static IEnumerable<Type> GetRequireTypes(
        Type type)
    {
        foreach (var requireAttribute in
                 type.GetCustomAttributes<RequireAttribute>(inherit: true))
        {
            foreach (var requiredType in
                     requireAttribute.RequiredTypes)
            {
                yield return requiredType;
            }
        }
    }

    private enum Mark : byte
    {
        Visiting,
        Done
    }
}
