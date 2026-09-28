using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Query;

namespace QueryCache.EFCore.Keys
{
    internal static class TableTags
    {
        public static string[] ForQuery(Expression expression, string scope)
        {
            var collector = new TypeCollector();
            collector.Visit(expression);
            if (collector.Model is null)
            {
                return [];
            }
            var entityTypes = collector.Types
                .Select(type => collector.Model.FindEntityType(ElementType(type)))
                .OfType<IEntityType>()
                .SelectMany(static entityType => entityType.GetDerivedTypesInclusive())
                .ToList();
            var joinTypes = entityTypes.SelectMany(static entityType => entityType.GetSkipNavigations()).Select(static skip => skip.JoinEntityType).OfType<IEntityType>();
            return [.. entityTypes.Concat(joinTypes).SelectMany(entityType => ForEntityType(entityType, scope)).Distinct(StringComparer.Ordinal)];
        }

        public static IEnumerable<string> ForEntityType(IEntityType entityType, string scope)
        {
            return entityType.GetTableMappings().Select(mapping => $"{scope}\n{mapping.Table.Schema}.{mapping.Table.Name}");
        }

        private static Type ElementType(Type type)
        {
            if (type == typeof(string))
            {
                return type;
            }
            var enumerable = type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IEnumerable<>)
                ? type
                : Array.Find(type.GetInterfaces(), static i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>));
            return enumerable?.GetGenericArguments()[0] ?? type;
        }

        private sealed class TypeCollector : ExpressionVisitor
        {
            public IModel? Model;
            public readonly HashSet<Type> Types = [];

            public override Expression? Visit(Expression? node)
            {
                if (node is not null)
                {
                    Types.Add(node.Type);
                }
                return base.Visit(node);
            }

            protected override Expression VisitExtension(Expression node)
            {
                if (node is EntityQueryRootExpression root)
                {
                    Model ??= root.EntityType.Model;
                    Types.Add(root.EntityType.ClrType);
                    return node;
                }
                return base.VisitExtension(node);
            }
        }
    }
}
