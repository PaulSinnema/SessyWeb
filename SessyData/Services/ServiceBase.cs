using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SessyData.Helpers;
using SessyData.Model;
using System.ComponentModel.DataAnnotations;
using System.Linq.Expressions;
using System.Reflection;

namespace SessyData.Services
{
    public class ServiceBase<T> : IDisposable
        where T : class, new()
    {
        private IServiceScope _scope { get; set; }
        protected DbHelper _dbHelper { get; set; }

        public ServiceBase(IServiceScopeFactory serviceScopeFactory)
        {
            _scope = serviceScopeFactory.CreateScope();
            _dbHelper = _scope.ServiceProvider.GetRequiredService<DbHelper>();
        }

        public virtual async Task AddRange(List<T> list)
        {
            await _dbHelper.ExecuteTransaction(async db =>
            {
                db.Set<T>().AddRange(list);

                await Task.FromResult<bool>(true);
            });
        }

        public virtual async Task Add(List<T> list, Func<T, DbSet<T>, bool>? contains = null)
        {
            await _dbHelper.ExecuteTransaction(async db =>
            {
                foreach (var item in list)
                {
                    if (contains == null || !contains(item, db.Set<T>()))
                    {
                        db.Set<T>().Add(item);
                    }

                    await Task.FromResult<bool>(true);
                }
            });
        }

        private static void EnsureUpdatable()
        {
            if (!typeof(IUpdatable<T>).IsAssignableFrom(typeof(T)))
                throw new InvalidCastException($"For StoreOrUpdate the type {typeof(T).Name} must implement IUpdatable<{typeof(T).Name}>");
        }

        public virtual async Task Add(List<T> list, Func<T, DbSet<T>, T?> contains, bool checkDuplicate = true)
        {
            await _dbHelper.ExecuteTransaction(async db =>
            {
                foreach (var item in list)
                {
                    var containedItem = await GetContainedItem(contains, db, item);

                    if (containedItem != null)
                    {
                        if (checkDuplicate)
                            throw new InvalidOperationException($"Item to add is duplicate {item}");
                    }
                    else
                    {
                        db.Attach(item);

                        db.Set<T>().Add(item);
                    }
                }

                await Task.FromResult<bool>(true);
            });
        }

        /// <summary>
        /// Adds or updates an item in the set 'T'.
        /// 'T' Must be of type IUpdatable<T> and you must provide the Update() code in class 'T'.
        /// </summary>
        /// <param name="list">List of IUpdatable<T> objects to Add or Update.</param>
        /// <param name="contains">
        /// If 'contains' is not null it will be called to fetch the unique item from the set. You must
        /// provide the code to fetch the item.
        /// if 'contains' is null the routine fetches the item for you using it's 'Id'.
        /// </param>
        public virtual async Task AddOrUpdate(List<T> list, Func<T, DbSet<T>, T?>? contains = null)
        {
            EnsureUpdatable();

            await _dbHelper.ExecuteTransaction(async db =>
            {
                foreach (T item in list)
                {
                    var containedItem = await GetContainedItem(contains, db, item, false);

                    if (containedItem != null)
                    {
                        ((IUpdatable<T>)containedItem!).Update(item);

                        var state = db.Entry(containedItem).State;

                        var entry = db.Set<T>().Update(containedItem);
                    }
                    else
                    {
                        db.Set<T>().Add(item);
                    }
                }

                await Task.FromResult<bool>(true);
            });
        }

        public virtual async Task Update(List<T> list, Func<T, DbSet<T>, T?>? contains)
        {
            EnsureUpdatable();

            await _dbHelper.ExecuteTransaction(async db =>
            {
                foreach (var item in list)
                {
                    var containedItem = await GetContainedItem(contains, db, item);

                    if (containedItem != null)
                    {
                        ((IUpdatable<T>)containedItem!).Update(item);

                        var state = db.Entry(containedItem).State;

                        db.Set<T>().Update(containedItem);
                    }
                    else
                    {
                        throw new InvalidOperationException($"Item to update was not found {item}");
                    }
                }

                await Task.FromResult<bool>(true);
            });
        }

        public virtual async Task Remove(List<T> list, Func<T, DbSet<T>, T?>? contains = null)
        {
            EnsureUpdatable();

            await _dbHelper.ExecuteTransaction(async db =>
            {
                foreach (var item in list)
                {
                    var containedItem = await GetContainedItem(contains, db, item);

                    if (containedItem != null)
                    {
                        var itemToRemove = await GetByKeyAsync(db, ((IUpdatable<T>)containedItem).Id);

                        db.Set<T>().Remove(containedItem);
                    }
                    else
                    {
                        throw new InvalidOperationException($"Item to remove was not found {item}");
                    }
                }
            });
        }

        private async Task<T?> GetContainedItem(Func<T, DbSet<T>, T?>? contains, ModelContext db, T item, bool shouldExist = true)
        {
            T? containedItem;

            if (contains != null)
            {
                containedItem = contains(item, db.Set<T>());
            }
            else
            {
                containedItem = await db.Set<T>().FindAsync(((IUpdatable<T>)item).Id);
            }

            if (containedItem == null && shouldExist)
                throw new InvalidOperationException($"Not found {((IUpdatable<T>)item).Id}");

            return containedItem;
        }

        private async Task<T?> GetByKeyAsync(ModelContext db, int key)
        {
            var entityType = typeof(T);
            var keyProperty = entityType.GetProperties()
                .FirstOrDefault(p => p.GetCustomAttribute<KeyAttribute>() != null)
                ?? entityType.GetProperties().FirstOrDefault(p => p.Name.Equals("Id", StringComparison.OrdinalIgnoreCase));

            if (keyProperty == null)
                throw new InvalidOperationException($"No [Key] attribute found on {entityType.Name}, and no default 'Id' property detected.");

            return await db.Set<T>().FindAsync(key);
        }

        public virtual async Task<bool> Exists(Func<DbSet<T>, Task<bool>> func)
        {
            return await _dbHelper.ExecuteQueryAsync(async (ModelContext db) =>
            {
                return await func(db.Set<T>());
            }).ConfigureAwait(false);
        }

        public virtual async Task<T?> Get(Func<IQueryable<T>, Task<T?>> func)
        {
            return await _dbHelper.ExecuteQueryAsync(async (ModelContext db) =>
            {
                return await func(db.Set<T>().AsNoTracking());
            }).ConfigureAwait(false);
        }

        public virtual async Task<List<T>> GetList(Func<IQueryable<T>, Task<List<T>>> func)
        {
            return await _dbHelper.ExecuteQueryAsync(async (ModelContext db) =>
            {
                return await func(db.Set<T>().AsNoTracking());
            }).ConfigureAwait(false);
        }

        /// <summary>
        /// Read with a projection, so filtering, grouping and paging stay in SQL. GetList can only
        /// hand back whole entities, which tempts callers into pulling a table into memory and
        /// finishing the job with LINQ-to-Objects — on PlannedActions that was 80.000 rows per call.
        /// </summary>
        public virtual async Task<TResult> Query<TResult>(Func<IQueryable<T>, Task<TResult>> func)
        {
            return await _dbHelper.ExecuteQueryAsync(async (ModelContext db) =>
            {
                return await func(db.Set<T>().AsNoTracking());
            }).ConfigureAwait(false);
        }

        /// <summary>
        /// Bulk delete in one statement. Remove() fetches every row and deletes it one by one,
        /// which is fine for a handful and ruinous for a purge. Runs without an explicit
        /// transaction: ExecuteDelete applies immediately and would be rolled back by
        /// ExecuteTransaction, which only commits when the change tracker has something to save.
        /// </summary>
        public virtual async Task RemoveWhere(Expression<Func<T, bool>> predicate)
        {
            await _dbHelper.ExecuteWriteAsync(async db =>
            {
                await db.Set<T>().Where(predicate).ExecuteDeleteAsync().ConfigureAwait(false);
            }).ConfigureAwait(false);
        }

        /// <summary>
        /// Match delegate for AddOrUpdate that looks the existing rows up once instead of running a
        /// query per item. <paramref name="window"/> limits what is loaded — an upsert of one plan
        /// must not pull in the whole table. The entities come from the same context AddOrUpdate
        /// writes through, so they stay tracked and Update() behaves exactly as before.
        /// </summary>
        public static Func<T, DbSet<T>, T?> MatchOn<TKey>(
            Func<T, TKey> keySelector,
            Expression<Func<T, bool>>? window = null)
            where TKey : notnull
        {
            Dictionary<TKey, T>? existing = null;

            return (item, set) =>
            {
                existing ??= (window == null ? set : set.Where(window))
                    .AsEnumerable()
                    .GroupBy(keySelector)
                    .ToDictionary(g => g.Key, g => g.First());

                return existing.TryGetValue(keySelector(item), out var found) ? found : null;
            };
        }
        private bool _isDisposed = false;

        public void Dispose()
        {
            if (!_isDisposed)
            {
                _scope.Dispose();
                _isDisposed = true;
            }
        }
    }
}