using Microsoft.EntityFrameworkCore;

namespace QueryCache.EFCore
{
    /// <summary>Entry points for building cached EF Core queries.</summary>
    public static class CacheQueryBuilderExtensions
    {
        /// <summary>Starts a cached query over <paramref name="set"/>: <c>db.Users.ToCacheQueryBuilder().Where(...).ToListAsync(expiration, ct)</c>.</summary>
        /// <typeparam name="T">The entity type.</typeparam>
        /// <param name="set">The set to query.</param>
        /// <returns>A <see cref="CacheQueryBuilder{T}"/> over <paramref name="set"/>.</returns>
        public static CacheQueryBuilder<T> ToCacheQueryBuilder<T>(this DbSet<T> set) where T : class
        {
            return new(set);
        }
    }
}
