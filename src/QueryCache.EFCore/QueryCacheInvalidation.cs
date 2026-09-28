using System.Runtime.CompilerServices;
using System.Transactions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using QueryCache.EFCore.Keys;

namespace QueryCache.EFCore
{
    /// <summary>Invalidates cached EF queries when <c>SaveChanges</c> writes to the tables they read.</summary>
    /// <remarks>
    /// Entries are invalidated after the save and again when the surrounding transaction (EF or <see cref="TransactionScope"/>) commits,
    /// so readers on other connections cannot keep data they cached while the transaction was open.
    /// <c>ExecuteUpdate</c>, <c>ExecuteDelete</c>, raw SQL and Dapper writes are not seen; call <c>InvalidateCacheAsync()</c> for those.
    /// </remarks>
    public static class QueryCacheInvalidation
    {
        private static readonly ConditionalWeakTable<DbContext, State> States = [];

        private sealed class State
        {
            public string[] Saving = [];
            public readonly HashSet<string> Pending = new(StringComparer.Ordinal);
        }

        /// <summary>Adds the interceptors that invalidate cached queries on <c>SaveChanges</c>.</summary>
        /// <param name="builder">The options builder.</param>
        /// <returns>The same builder.</returns>
        public static DbContextOptionsBuilder UseQueryCacheInvalidation(this DbContextOptionsBuilder builder)
        {
            return builder.AddInterceptors(SaveChangesInvalidator.Instance, TransactionInvalidator.Instance);
        }

        /// <summary>Adds the interceptors that invalidate cached queries on <c>SaveChanges</c>.</summary>
        /// <typeparam name="TContext">The context type.</typeparam>
        /// <param name="builder">The options builder.</param>
        /// <returns>The same builder.</returns>
        public static DbContextOptionsBuilder<TContext> UseQueryCacheInvalidation<TContext>(this DbContextOptionsBuilder<TContext> builder) where TContext : DbContext
        {
            return builder.AddInterceptors(SaveChangesInvalidator.Instance, TransactionInvalidator.Instance);
        }

        private static void Saving(DbContext? context)
        {
            if (context is null)
            {
                return;
            }
            var scope = ConnectionScope.Of(context.Database.GetDbConnection());
            States.GetOrCreateValue(context).Saving = [.. context.ChangeTracker.Entries()
                .Where(static entry => entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
                .SelectMany(entry => TableTags.ForEntityType(entry.Metadata, scope))
                .Distinct(StringComparer.Ordinal)];
        }

        private static ValueTask Saved(DbContext? context)
        {
            if (context is null || !States.TryGetValue(context, out var state))
            {
                return ValueTask.CompletedTask;
            }
            var tags = state.Saving;
            state.Saving = [];
            if (context.Database.CurrentTransaction is not null)
            {
                state.Pending.UnionWith(tags);
            }
            else if (Transaction.Current is { } ambient)
            {
                ambient.TransactionCompleted += (_, _) => Wait(QueryCacheStore.InvalidateTagsAsync(tags, CancellationToken.None));
            }
            return QueryCacheStore.InvalidateTagsAsync(tags, CancellationToken.None);
        }

        private static ValueTask Ended(DbContext? context, bool committed)
        {
            if (context is null || !States.TryGetValue(context, out var state))
            {
                return ValueTask.CompletedTask;
            }
            string[] tags = committed ? [.. state.Pending] : [];
            state.Pending.Clear();
            return QueryCacheStore.InvalidateTagsAsync(tags, CancellationToken.None);
        }

        private static void Wait(ValueTask task)
        {
            if (!task.IsCompletedSuccessfully)
            {
                task.AsTask().GetAwaiter().GetResult();
            }
        }

        private sealed class SaveChangesInvalidator : SaveChangesInterceptor
        {
            public static readonly SaveChangesInvalidator Instance = new();

            public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
            {
                Saving(eventData.Context);
                return result;
            }

            public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
            {
                Saving(eventData.Context);
                return ValueTask.FromResult(result);
            }

            public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
            {
                Wait(Saved(eventData.Context));
                return result;
            }

            public override async ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
            {
                await Saved(eventData.Context).ConfigureAwait(false);
                return result;
            }
        }

        private sealed class TransactionInvalidator : DbTransactionInterceptor
        {
            public static readonly TransactionInvalidator Instance = new();

            public override void TransactionCommitted(System.Data.Common.DbTransaction transaction, TransactionEndEventData eventData)
            {
                Wait(Ended(eventData.Context, committed: true));
            }

            public override Task TransactionCommittedAsync(System.Data.Common.DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
            {
                return Ended(eventData.Context, committed: true).AsTask();
            }

            public override void TransactionRolledBack(System.Data.Common.DbTransaction transaction, TransactionEndEventData eventData)
            {
                Wait(Ended(eventData.Context, committed: false));
            }

            public override Task TransactionRolledBackAsync(System.Data.Common.DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
            {
                return Ended(eventData.Context, committed: false).AsTask();
            }
        }
    }
}
